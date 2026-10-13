using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.FormDrafts;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;
using PTGOilSystem.Web.Services.LoadingReceipts;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class SalesController : Controller
{
    private readonly ApplicationDbContext _db;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);
    private readonly IStockService _stock;
    private readonly IPricingService _pricing;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ILossEventWorkflowService _lossWorkflow;
    private readonly ILogger<SalesController> _logger;
    private readonly IMemoryCache? _cache;
    private readonly IInventoryLineageWriter _lineage;
    private readonly IFormTokenGuard _formTokens;
    private readonly IInventoryMovementWriter _movements;
    private readonly ITransportSourceAllocationService _sourceAllocations;
    // مرحله ۷ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.ISalesAccountingAdapter? _salesAccounting;
    private const int DefaultListLimit = 100;
    private const int LookupLimit = 200;

    // مرحله ۷ — Dual-write داخل همان Transaction قدیمی. باید بعد از ذخیرهٔ حرکات خروجِ موجودی
    // صدا زده شود: COGS مقدار و ترمینال را از همان سطرهای InventoryMovement می‌خواند.
    // درآمد و COGS دو Flag جدا دارند، پس هر کدام مستقل Skip می‌شود.
    private async Task PostSaleAccountingAsync(SalesTransaction sale)
    {
        if (_salesAccounting is null)
        {
            return;
        }

        await _salesAccounting.TryPostSaleAsync(sale);
        await _salesAccounting.TryPostCogsAsync(sale);
    }

    // فروش با قیمت باز: فقط بهای کالای تحویل‌شده به میانگینِ همان روز از موجودی به حساب واسط
    // «تحویل‌شده در انتظار قیمت» می‌رود؛ عاید و طلب تا تعیین قیمت ثبت نمی‌شود.
    private async Task PostPendingSaleDeliveredCostAsync(SalesTransaction sale)
    {
        if (_salesAccounting is null)
        {
            return;
        }

        await _salesAccounting.TryPostCogsAsync(sale);
    }

    // لغو فروش باید هر دو دفتر را با هم برگرداند. ردیف‌های Legacy همان‌جا معکوس می‌شوند و
    // ژورنال‌های دفتر کل جدید اینجا، از طریق سرویس مرکزی، برگشت رسمی و متوازن می‌گیرند.
    //
    // برای «تحویل پیش‌فروش» شکست برگشتِ ژورنال عمداً خطا می‌دهد تا کل لغو برگردد و هرگز
    // تحویلی لغو نشود که درآمد و بهای تمام‌شده‌اش هنوز روی حساب‌هاست. برای فروش‌های دیگر رفتار
    // تاریخی حفظ می‌شود: لغو Legacy انجام می‌شود و شکست ژورنال فقط لاگ می‌شود، چون این مسیر
    // سال‌ها بدون ثبت جدید کار کرده و شکست‌دادن ناگهانیِ لغوهای قدیمی (مثلاً دورهٔ بسته) ریسک
    // عملیاتی بزرگ‌تری از خودِ گپ است.
    private async Task ReverseSaleAccountingAsync(SalesTransaction sale)
    {
        if (_salesAccounting is null)
        {
            return;
        }

        var reversalDate = _businessClock.Today;

        try
        {
            await _salesAccounting.TryReverseSaleAsync(sale, reversalDate);
            await _salesAccounting.TryReverseCogsAsync(sale, reversalDate);
            // مصرف‌های پیش‌دریافتِ همین تحویل آزاد می‌شوند؛ ژورنالِ مستقلِ مصرف‌های «بعد از تحویل» هم
            // معکوس می‌شود. Idempotent: لغو دوباره چیزی برای آزادکردن پیدا نمی‌کند.
            await _salesAccounting.TryReleaseAdvanceApplicationsAsync(sale, reversalDate);
        }
        catch (Exception exception) when (!sale.PreSaleOrderId.HasValue)
        {
            _logger.LogError(
                exception,
                "Journal reversal failed for cancelled sale {SaleId}; legacy cancellation kept.",
                sale.Id);
        }
    }

    private sealed record LookupOption(int Id, string Name);
    private sealed record TankLookupOption(int Id, string Display);
    private sealed record TankTerminalOption(int Id, int TerminalId);
    private sealed record CurrencyLookupOption(string Code);
    private sealed record TerminalStockAllocation(int ContractId, decimal QuantityMt);

    private sealed record TerminalStockSourceBalance(
        int ContractId,
        int CompanyId,
        string ContractNumber,
        DateTime ContractDate,
        decimal AvailableMt);

    private sealed record TerminalStockAvailabilityDiagnostic(
        decimal SellableMt,
        decimal PhysicalAsOfSaleDateMt,
        decimal CurrentPhysicalMt,
        decimal OtherCompaniesMt,
        decimal UnallocatedToPurchaseContractMt);

    private readonly IAfghanistanBusinessClock _businessClock;

    [ActivatorUtilitiesConstructor]
    public SalesController(
        ApplicationDbContext db,
        IStockService stock,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ILogger<SalesController> logger,
        IPricingService? pricing = null,
        ILossEventWorkflowService? lossWorkflow = null,
        IMemoryCache? cache = null,
        IInventoryLineageWriter? lineage = null,
        IFormTokenGuard? formTokens = null,
        Services.Accounting.ISalesAccountingAdapter? salesAccounting = null,
        IAfghanistanBusinessClock? businessClock = null,
        InventoryTransportReceiptService? receiptService = null,
        ITransportQuantityService? quantities = null,
        IInventoryMovementWriter? movements = null,
        ITransportSourceAllocationService? sourceAllocations = null,
        ILoadingReceiptCancellationService? loadingReceiptCancellation = null)
    {
        _quantities = quantities ?? new TransportQuantityService(db);
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _salesAccounting = salesAccounting;
        _db = db;
        _stock = stock;
        _pricing = pricing ?? new PricingService(db);
        _currencyConversion = currencyConversion;
        _audit = audit;
        _lossWorkflow = lossWorkflow ?? new LossEventWorkflowService(db, stock, audit);
        _logger = logger;
        _cache = cache;
        _lineage = lineage ?? InventoryLineageWriterFactory.Disabled(db);
        _formTokens = formTokens ?? new FormTokenGuard(db);
        _movements = movements ?? new InventoryMovementWriter(db, stock);
        _sourceAllocations = sourceAllocations ?? new TransportSourceAllocationService(db);
        _loadingReceiptCancellation = loadingReceiptCancellation ?? new LoadingReceiptCancellationService(
            db, audit, Microsoft.Extensions.Logging.Abstractions.NullLogger<LoadingReceiptCancellationService>.Instance,
            stock: _stock, salesAccounting: _salesAccounting, movements: _movements);
        // از DI می‌آید تا آداپترهای حسابداری وصل باشند؛ ساخت دستی آن‌ها را null می‌گذارد.
        _receiptService = receiptService
            ?? new InventoryTransportReceiptService(
                db,
                currencyConversion,
                _lineage,
                movements: _movements,
                quantities: _quantities,
                sourceAllocations: _sourceAllocations);
    }

    private readonly ILoadingReceiptCancellationService _loadingReceiptCancellation;
    private readonly InventoryTransportReceiptService _receiptService;
    // تک‌منبع «باقیماندهٔ حمل»؛ هیچ اکشنی نباید فرمول خودش را داشته باشد.
    private readonly ITransportQuantityService _quantities;

    public SalesController(
        ApplicationDbContext db,
        IStockService stock,
        IAuditService audit,
        ILogger<SalesController> logger,
        IPricingService? pricing = null,
        ILossEventWorkflowService? lossWorkflow = null)
        : this(
            db,
            stock,
            new CurrencyConversionService(pricing ?? new PricingService(db)),
            audit,
            logger,
            pricing,
            lossWorkflow)
    {
    }

    private Task<T> GetCachedLookupAsync<T>(string key, Func<Task<T>> factory)
        where T : class
    {
        if (_cache is null)
        {
            return factory();
        }

        return _cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            entry.SlidingExpiration = TimeSpan.FromSeconds(30);
            return factory();
        })!;
    }

    private async Task PopulateLookupsAsync(
        SalesCreateViewModel? createModel = null,
        SalesIndexFilterViewModel? filter = null)
    {
        var selectedSaleContractId = createModel?.ContractId ?? filter?.ContractId.Only();
        var selectedShipmentId = createModel?.ShipmentId;

        var saleContracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Sale)
            .OrderBy(c => selectedSaleContractId.HasValue && c.Id == selectedSaleContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractName,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null,
                c.DestinationLocationId
            })
            .ToListAsync();

        ViewBag.SaleContractDestinationMap = saleContracts
            .Select(c => new { contractId = c.Id, destinationLocationId = c.DestinationLocationId })
            .ToList();

        ViewBag.Contracts = new SelectList(
            saleContracts
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractName,
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedSaleContractId);

        var companyLookups = await GetCachedLookupAsync(
            "sales:lookups:companies:v1",
            () => _db.Companies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new LookupOption(c.Id, c.Name))
                .ToListAsync());
        ViewBag.Companies = new SelectList(
            companyLookups,
            "Id",
            "Name",
            createModel?.CompanyId ?? filter?.CompanyId.Only());

        var customerLookups = await GetCachedLookupAsync(
            "sales:lookups:customers:v1",
            () => _db.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new LookupOption(c.Id, c.Name))
                .ToListAsync());
        ViewBag.Customers = new SelectList(
            customerLookups,
            "Id",
            "Name",
            createModel?.CustomerId ?? filter?.CustomerId.Only());

        if (createModel is not null)
        {
            await PopulateBuyerLookupAsync(createModel.BuyerKey);
        }

        var productLookups = await GetCachedLookupAsync(
            "sales:lookups:products:v1",
            () => _db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new LookupOption(p.Id, p.Name))
                .ToListAsync());
        ViewBag.Products = new SelectList(
            productLookups,
            "Id",
            "Name",
            createModel?.ProductId ?? filter?.ProductId.Only());

        var destinationLookups = await GetCachedLookupAsync(
            "sales:lookups:destinations:v1",
            () => _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new LookupOption(l.Id, l.Name))
                .ToListAsync());
        ViewBag.Destinations = new SelectList(
            destinationLookups,
            "Id",
            "Name",
            createModel?.DestinationLocationId);

        var shipments = await _db.Shipments
            .AsNoTracking()
            .OrderBy(s => selectedShipmentId.HasValue && s.Id == selectedShipmentId.Value ? 0 : 1)
            .ThenByDescending(s => s.DepartureDate)
            .ThenBy(s => s.ShipmentCode)
            .ThenByDescending(s => s.Id)
            .Take(LookupLimit)
            .Select(s => new { s.Id, s.ShipmentCode, s.ContractId, s.DestinationLocationId })
            .ToListAsync();
        ViewBag.ShipmentContractMap = shipments
            .Select(s => new { shipmentId = s.Id, contractId = s.ContractId, destinationLocationId = s.DestinationLocationId })
            .ToList();
        ViewBag.Shipments = shipments
            .Select(s => new SelectListItem
            {
                Value = s.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(s.ShipmentCode)
                    ? $"Shipment #{s.Id}"
                    : s.ShipmentCode,
                Selected = createModel?.ShipmentId == s.Id
            })
            .ToList();

        // «فروش در مسیر»: به‌جای انتخاب خشکِ محموله، خودِ بارهای در راه فهرست می‌شوند.
        // فقط نمایش است؛ مقدارِ ثبت‌شده همان ShipmentId قبلی می‌ماند.
        var inTransitRows = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId != null
                && (l.Status == InventoryTransportLegStatus.Loaded
                    || l.Status == InventoryTransportLegStatus.InTransit))
            .GroupBy(l => l.ShipmentId!.Value)
            .Select(g => new
            {
                ShipmentId = g.Key,
                QuantityMt = g.Sum(x => x.QuantityMt),
                LegCount = g.Count()
            })
            .ToListAsync();

        var shipmentCodeById = shipments
            .ToDictionary(s => s.Id, s => s.ShipmentCode);
        var inTransitItems = inTransitRows
            .OrderByDescending(row => row.QuantityMt)
            .Select(row =>
            {
                shipmentCodeById.TryGetValue(row.ShipmentId, out var code);
                var label = string.IsNullOrWhiteSpace(code) ? $"Shipment #{row.ShipmentId}" : code;
                return new SelectListItem
                {
                    Value = row.ShipmentId.ToString(),
                    Text = $"{label} — {row.QuantityMt:N3} MT در راه — {row.LegCount} حمل",
                    Selected = createModel?.ShipmentId == row.ShipmentId
                };
            })
            .ToList();

        // سندی که از قبل محموله دارد ولی آن بار دیگر در راه نیست، انتخابش را از دست ندهد.
        if (createModel?.ShipmentId is int currentShipmentId
            && !inTransitItems.Any(item => item.Value == currentShipmentId.ToString()))
        {
            shipmentCodeById.TryGetValue(currentShipmentId, out var currentCode);
            inTransitItems.Insert(0, new SelectListItem
            {
                Value = currentShipmentId.ToString(),
                Text = string.IsNullOrWhiteSpace(currentCode) ? $"Shipment #{currentShipmentId}" : currentCode,
                Selected = true
            });
        }

        ViewBag.InTransitStocks = inTransitItems;

        var terminalLookups = await GetCachedLookupAsync(
            "sales:lookups:terminals:v1",
            () => _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .Select(t => new LookupOption(t.Id, t.Name))
                .ToListAsync());
        ViewBag.SourceTerminals = new SelectList(
            terminalLookups,
            "Id",
            "Name",
            createModel?.SourceTerminalId);

        var tankLookups = await GetCachedLookupAsync(
            "sales:lookups:storage-tanks:v2",
            async () => (await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                    .AsNoTracking()
                    .OrderBy(t => t.DisplayName ?? t.TankCode)))
                .Select(t => new TankLookupOption(t.Id, t.Display))
                .ToList());
        ViewBag.SourceStorageTanks = new SelectList(
            tankLookups,
            "Id",
            "Display",
            createModel?.SourceStorageTankId);

        // نگاشت «مخزن ← ترمینال»: فرم فروش فقط مخزن را می‌پرسد و ترمینال مبدا از همین نگاشت پر می‌شود.
        var tankTerminalLookups = await GetCachedLookupAsync(
            "sales:lookups:storage-tank-terminals:v1",
            () => _db.StorageTanks
                .AsNoTracking()
                .Select(t => new TankTerminalOption(t.Id, t.TerminalId))
                .ToListAsync());
        ViewBag.SourceTankTerminalMap = tankTerminalLookups
            .Select(t => new { tankId = t.Id, terminalId = t.TerminalId })
            .ToList();

        int? selectedProductId = createModel is not null && createModel.ProductId > 0
            ? createModel.ProductId
            : null;
        int? selectedCompanyId = createModel is not null && createModel.CompanyId > 0
            ? createModel.CompanyId
            : filter?.CompanyId.Only();
        var selectedSourcePurchaseContractId = createModel?.SourcePurchaseContractId;
        var purchaseContractsQuery = _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase)
            .Where(c => !selectedProductId.HasValue
                || c.ProductId == selectedProductId.Value
                || c.Id == selectedSourcePurchaseContractId)
            .Where(c => !selectedCompanyId.HasValue
                || c.CompanyId == selectedCompanyId.Value
                || c.Id == selectedSourcePurchaseContractId)
            .OrderBy(c => selectedSourcePurchaseContractId.HasValue && c.Id == selectedSourcePurchaseContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractName,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null,
                CompanyName = c.Company != null ? c.Company.Name : null
            });
        var purchaseContracts = await purchaseContractsQuery.ToListAsync();

        ViewBag.SourcePurchaseContracts = new SelectList(
            purchaseContracts
                .Select(c =>
                {
                    var display = ContractUiText.FormatLookup(
                        c.ContractName,
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName));

                    if (!string.IsNullOrWhiteSpace(c.CompanyName))
                    {
                        display = $"{display} | شرکت: {c.CompanyName}";
                    }

                    return new ContractLookupOption(c.Id, display);
                })
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedSourcePurchaseContractId);

        var currencyLookups = await GetCachedLookupAsync(
            "sales:lookups:currencies:v1",
            () => _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new CurrencyLookupOption(c.Code))
                .ToListAsync());
        ViewBag.Currencies = new SelectList(
            currencyLookups,
            "Code",
            "Code",
            createModel?.Currency);

        ViewBag.SaleStages = GetSaleStageItems(createModel?.SaleStage ?? SaleStage.TerminalStock);
        ViewBag.SaleLossStages = GetSaleLossStageItems(
            createModel?.Loss.Stage ?? ResolveDefaultLossStage(createModel?.SaleStage ?? SaleStage.TerminalStock));
    }

    private async Task PopulateBuyerLookupAsync(string? selectedKey)
    {
        var customers = await _db.Customers.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync();
        var suppliers = await _db.Suppliers.AsNoTracking().Where(s => s.IsActive)
            .OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToListAsync();
        ViewBag.Buyers = SaleBuyerKey.BuildOptions(
            customers.Select(c => (c.Id, c.Name)),
            suppliers.Select(s => (s.Id, s.Name)),
            selectedKey);
    }

    private async Task ValidateBuyerAsync(int? customerId, int? supplierId, string modelKey = "BuyerKey")
    {
        if (customerId.HasValue == supplierId.HasValue)
        {
            ModelState.AddModelError(modelKey, "دقیقاً یک خریدار (مشتری یا تأمین‌کننده) باید انتخاب شود.");
            return;
        }

        var valid = customerId.HasValue
            ? await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == customerId.Value && c.IsActive)
            : await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == supplierId!.Value && s.IsActive);
        if (!valid)
        {
            ModelState.AddModelError(modelKey, "خریدار انتخاب‌شده معتبر یا فعال نیست.");
        }
    }

    private async Task<StorageTank?> LockStorageTankAsync(int storageTankId)
    {
        if (_db.Database.IsRelational()
            && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return await _db.StorageTanks
                .FromSqlInterpolated($@"SELECT * FROM ""StorageTanks"" WHERE ""Id"" = {storageTankId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync();
        }

        return await _db.StorageTanks
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == storageTankId);
    }

    private async Task<IReadOnlyList<TerminalStockAllocation>> EnsureSufficientTerminalStockAsync(
        int productId,
        decimal quantityMt,
        DateTime saleDate,
        int terminalId,
        int storageTankId,
        int companyId,
        int? sourcePurchaseContractId)
    {
        if (sourcePurchaseContractId.HasValue)
        {
            var preferredAvailable = await _stock.GetFreeQuantityMtAsync(
                productId,
                terminalId: terminalId,
                contractId: sourcePurchaseContractId.Value,
                storageTankId: storageTankId,
                asOfUtc: saleDate);

            if (preferredAvailable >= quantityMt)
            {
                return [new TerminalStockAllocation(sourcePurchaseContractId.Value, quantityMt)];
            }
        }

        var balances = await GetTerminalStockSourceBalancesAsync(
            productId,
            terminalId,
            storageTankId,
            saleDate);

        var eligibleBalances = balances
            .Where(b => b.CompanyId == companyId && b.AvailableMt > 0m)
            .OrderBy(b => sourcePurchaseContractId.HasValue && b.ContractId == sourcePurchaseContractId.Value ? 0 : 1)
            .ThenBy(b => b.ContractDate)
            .ThenBy(b => b.ContractNumber)
            .ThenBy(b => b.ContractId)
            .ToList();

        var available = eligibleBalances.Sum(b => b.AvailableMt);
        if (available < quantityMt)
        {
            var diagnostic = await BuildTerminalStockAvailabilityDiagnosticAsync(
                productId,
                terminalId,
                storageTankId,
                saleDate,
                companyId,
                balances);
            throw new BusinessRuleException(
                "SALE_TERMINAL_STOCK_INSUFFICIENT",
                BuildTerminalStockInsufficientMessage(diagnostic, quantityMt));
        }

        var remaining = quantityMt;
        var allocations = new List<TerminalStockAllocation>();
        foreach (var balance in eligibleBalances)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var allocated = Math.Min(balance.AvailableMt, remaining);
            if (allocated <= 0m)
            {
                continue;
            }

            allocations.Add(new TerminalStockAllocation(balance.ContractId, allocated));
            remaining -= allocated;
        }

        return allocations;
    }

    private async Task<TerminalStockAvailabilityDiagnostic> BuildTerminalStockAvailabilityDiagnosticAsync(
        int productId,
        int terminalId,
        int storageTankId,
        DateTime saleDate,
        int companyId,
        IReadOnlyList<TerminalStockSourceBalance> balances)
    {
        var movementTotals = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ProductId == productId
                && m.TerminalId == terminalId
                && m.StorageTankId == storageTankId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PhysicalAsOfSaleDateMt = g.Sum(m => m.MovementDate <= saleDate
                    ? m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m
                    : 0m),
                CurrentPhysicalMt = g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m)
            })
            .SingleOrDefaultAsync();
        var physicalAsOfSaleDateMt = movementTotals?.PhysicalAsOfSaleDateMt ?? 0m;
        var currentPhysicalMt = movementTotals?.CurrentPhysicalMt ?? 0m;
        var sellableMt = balances
            .Where(b => b.CompanyId == companyId && b.AvailableMt > 0m)
            .Sum(b => b.AvailableMt);
        var purchaseContractMt = balances
            .Where(b => b.AvailableMt > 0m)
            .Sum(b => b.AvailableMt);
        var otherCompaniesMt = balances
            .Where(b => b.CompanyId != companyId && b.AvailableMt > 0m)
            .Sum(b => b.AvailableMt);

        return new TerminalStockAvailabilityDiagnostic(
            sellableMt,
            physicalAsOfSaleDateMt,
            currentPhysicalMt,
            otherCompaniesMt,
            Math.Max(0m, physicalAsOfSaleDateMt - purchaseContractMt));
    }

    private static string BuildTerminalStockInsufficientMessage(
        TerminalStockAvailabilityDiagnostic diagnostic,
        decimal requestedMt)
    {
        var message =
            $"موجودی قابل‌فروش شرکت انتخابی در این مخزن {diagnostic.SellableMt:N4} MT است و برای درخواست {requestedMt:N4} MT کافی نیست. " +
            $"کل موجودی فیزیکی مخزن تا تاریخ فروش: {diagnostic.PhysicalAsOfSaleDateMt:N4} MT.";

        if (diagnostic.CurrentPhysicalMt != diagnostic.PhysicalAsOfSaleDateMt)
        {
            message += $" موجودی فعلی مخزن بدون محدودیت تاریخ: {diagnostic.CurrentPhysicalMt:N4} MT.";
        }

        if (diagnostic.OtherCompaniesMt > 0m)
        {
            message += $" از این مقدار، {diagnostic.OtherCompaniesMt:N4} MT متعلق به قراردادهای خرید شرکت‌های دیگر است.";
        }

        if (diagnostic.UnallocatedToPurchaseContractMt > 0m)
        {
            message += $" همچنین {diagnostic.UnallocatedToPurchaseContractMt:N4} MT به قرارداد خرید معتبر قابل تخصیص نیست.";
        }

        return message + " شرکت، قرارداد خرید منبع و تاریخ فروش را بررسی کنید.";
    }

    private async Task<IReadOnlyList<TerminalStockSourceBalance>> GetTerminalStockSourceBalancesAsync(
        int productId,
        int terminalId,
        int storageTankId,
        DateTime saleDate)
    {
        var movementBalances = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ProductId == productId
                && m.TerminalId == terminalId
                && m.StorageTankId == storageTankId
                && m.MovementDate <= saleDate)
            .Select(m => new
            {
                ContractId = m.ContractId
                    ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                        : null),
                m.Direction,
                m.QuantityMt
            })
            .Where(m => m.ContractId.HasValue)
            .GroupBy(m => m.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                AvailableMt = g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m)
            })
            .Where(b => b.AvailableMt > 0m)
            .ToListAsync();

        var contractIds = movementBalances.Select(b => b.ContractId).Distinct().ToArray();
        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .Select(c => new
            {
                c.Id,
                c.CompanyId,
                c.ContractNumber,
                c.ContractDate
            })
            .ToDictionaryAsync(c => c.Id);

        return movementBalances
            .Where(b => contracts.ContainsKey(b.ContractId))
            .Select(b =>
            {
                var contract = contracts[b.ContractId];
                return new TerminalStockSourceBalance(
                    b.ContractId,
                    contract.CompanyId,
                    contract.ContractNumber,
                    contract.ContractDate,
                    b.AvailableMt);
            })
            .ToList();
    }

    private bool TryGetLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        {
            localReturnUrl = returnUrl;
            return true;
        }

        localReturnUrl = string.Empty;
        return false;
    }

    public async Task<IActionResult> Index([FromQuery] SalesIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        filter ??= new SalesIndexFilterViewModel();

        var query = _db.SalesTransactions
            .AsNoTracking()
            .AsQueryable();

        query = query.Where(s => !s.IsCancelled);

        // چندانتخابی: OR بین مقادیرِ یک فیلتر (همان زنجیرهٔ منابعِ قبلی، فقط برای
        // چند قرارداد)، AND بین فیلترهای مختلف.
        if (filter.ContractId.Length > 0)
        {
            var contractIds = filter.ContractId;
            query = query.Where(s =>
                (s.ContractId != null && contractIds.Contains(s.ContractId.Value))
                || (s.SourcePurchaseContractId != null && contractIds.Contains(s.SourcePurchaseContractId.Value))
                || _db.TruckDispatches.Any(d =>
                    d.Id == s.TruckDispatchId
                    && contractIds.Contains(d.ContractId))
                || _db.LoadingReceiptAllocations.Any(a =>
                    a.SourcePurchaseContractId != null
                    && contractIds.Contains(a.SourcePurchaseContractId.Value)
                    && a.SalesTransactionId == s.Id)
                || _db.InventoryMovements.Any(m =>
                    m.Direction == MovementDirection.Out
                    && m.ContractId != null
                    && contractIds.Contains(m.ContractId.Value)
                    && m.SalesTransactionId == s.Id)
                || _db.TruckDispatches.Any(d =>
                    contractIds.Contains(d.ContractId)
                    && d.SalesTransactionId == s.Id)
                || _db.InventoryTransportReceipts.Any(r =>
                    r.SalesTransactionId == s.Id
                    && !r.IsCancelled
                    && _db.InventoryTransportLegs.Any(l =>
                        l.Id == r.InventoryTransportLegId
                        && contractIds.Contains(l.SourcePurchaseContractId))));
        }
        if (filter.CompanyId.Length > 0)
        {
            var companyIds = filter.CompanyId;
            query = query.Where(s => s.CompanyId != null && companyIds.Contains(s.CompanyId.Value));
        }
        if (filter.CustomerId.Length > 0)
        {
            var customerIds = filter.CustomerId;
            query = query.Where(s => s.CustomerId.HasValue && customerIds.Contains(s.CustomerId.Value));
        }
        if (filter.ProductId.Length > 0)
        {
            var productIds = filter.ProductId;
            query = query.Where(s => productIds.Contains(s.ProductId));
        }
        if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber))
        {
            var invoice = filter.InvoiceNumber.Trim();
            query = query.Where(s => s.InvoiceNumber.Contains(invoice));
        }
        if (filter.FromDate.HasValue) query = query.Where(s => s.SaleDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) query = query.Where(s => s.SaleDate <= filter.ToDate.Value);
        var priceStatus = filter.PriceStatus?.Trim().ToLowerInvariant();
        if (priceStatus == "pending")
        {
            query = query.Where(s => s.IsSalePricePending);
        }
        else if (priceStatus == "final")
        {
            query = query.Where(s => !s.IsSalePricePending);
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        page = Math.Clamp(page, 1, pageCount);

        await PopulateLookupsAsync(filter: filter);

        var items = await query
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(s => new SalesListItemViewModel
            {
                Id = s.Id,
                SaleDate = s.SaleDate,
                SaleStage = s.SaleStage,
                InvoiceNumber = s.InvoiceNumber,
                ContractName = s.Contract != null ? s.Contract.ContractName : string.Empty,
                ContractNumber = s.Contract != null ? s.Contract.ContractNumber : SalesContractText.WithoutSalesContract,
                // CompanyId=null یعنی فروش چندجوازی؛ «چند جواز» نمایش داده می‌شود نه خالی/«بدون جواز».
                CompanyName = s.CompanyId == null
                    ? SalesContractText.MultiLicense
                    : (s.Company != null ? s.Company.Name : ""),
                CustomerName = s.Customer != null ? s.Customer.Name : (s.Supplier != null ? s.Supplier.Name : ""),
                ProductName = s.Product != null ? s.Product.Name : "",
                DestinationName = s.DestinationLocation != null ? s.DestinationLocation.Name : null,
                // نمبر وسیله برای فروش‌هایی که مستقیماً از موتر انجام شده‌اند.
                VehicleNumber = s.TruckDispatch != null && s.TruckDispatch.Truck != null
                    ? s.TruckDispatch.Truck.PlateNumber
                    : null,
                QuantityMt = s.QuantityMt,
                Currency = s.Currency,
                UnitPriceInCurrency = s.UnitPriceInCurrency,
                TotalInCurrency = s.TotalInCurrency,
                AppliedFxRateToUsd = s.AppliedFxRateToUsd,
                UnitPriceUsd = s.UnitPriceUsd,
                TotalUsd = s.TotalUsd,
                IsSalePricePending = s.IsSalePricePending
            })
            .ToListAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        ViewBag.SumQuantity = await query.SumAsync(s => s.QuantityMt);
        // جمع مبلغ فقط فروش‌های قیمت‌قطعی است؛ فروش با قیمت باز جدا شمرده می‌شود و صفر حساب نمی‌شود.
        ViewBag.SumTotalUsd = await query.Where(s => !s.IsSalePricePending).SumAsync(s => s.TotalUsd);
        ViewBag.PendingPriceCount = await query.CountAsync(s => s.IsSalePricePending);
        ViewBag.CustomerCount = await query
            .Where(s => s.CustomerId != null)
            .Select(s => s.CustomerId)
            .Distinct()
            .CountAsync();

        return View(new SalesIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    /// <summary>
    /// PTG-P2-03 — صفحهٔ «ابطال / اصلاح فروش».
    ///
    /// ابطال دیگر یک کلیکِ بی‌سؤال نیست: دلیل اجباری است و کاربر تصمیم می‌گیرد که پس از
    /// برگشتِ اسناد، فروشِ جایگزین هم ثبت کند یا نه. سندِ اصلی در هر دو حالت دست‌نخورده
    /// و قابل مشاهده می‌ماند.
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> Correct(int id, string? returnUrl = null)
    {
        var sale = await _db.SalesTransactions
            .AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsCancelled)
        {
            TempData["err"] = "این فروش قبلاً ابطال شده است و دوباره ابطال نمی‌شود.";
            return RedirectToAction(nameof(Details), new { id, returnUrl });
        }

        return View(new SaleCorrectionViewModel
        {
            SaleId = sale.Id,
            Version = sale.Version,
            InvoiceNumber = sale.InvoiceNumber,
            SaleDate = sale.SaleDate,
            CustomerName = sale.Customer?.Name ?? sale.Supplier?.Name ?? "",
            QuantityMt = sale.QuantityMt,
            Currency = sale.Currency,
            TotalInCurrency = sale.TotalInCurrency,
            TotalUsd = sale.TotalUsd,
            IsSalePricePending = sale.IsSalePricePending,
            CreateReplacement = true,
            ReturnUrl = returnUrl
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        int id,
        string? cancelReason = null,
        bool createReplacement = false,
        long version = 0,
        string? returnUrl = null,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var sale = await _db.SalesTransactions
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        // PTG-P1-05 — ابطال هم روی نسخه‌ای که کاربر دید انجام می‌شود، نه روی نسخهٔ لحظه.
        _db.UseExpectedVersion(sale, version);

        // PTG-P2-03 — دلیل اجباری. بدون آن، تاریخچهٔ ابطال چیزی برای گفتن ندارد.
        var reason = string.IsNullOrWhiteSpace(cancelReason) ? null : cancelReason.Trim();
        if (reason is null)
        {
            TempData["err"] = "برای ابطال فروش، نوشتن دلیل الزامی است.";
            return RedirectToAction(nameof(Correct), new { id, returnUrl });
        }

        if (reason.Length > 500)
        {
            reason = reason[..500];
        }

        if (sale.IsCancelled)
        {
            TempData["ok"] = "این فروش قبلاً لغو شده است.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        var directReceiptId = await FindSingleDirectLoadingReceiptAsync(sale.Id);
        if (directReceiptId.HasValue)
        {
            await using var directTransaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
            try
            {
                var coordinator = new SalesBatchCancellationCoordinator(_db);
                await coordinator.LockAsync(sale.SalesBatchId, [sale.Id]);
                await _db.Entry(sale).ReloadAsync();
                if (sale.IsCancelled)
                {
                    TempData["ok"] = "این فروش قبلاً لغو شده است.";
                    return RedirectToAction(nameof(Details), new { id });
                }
                _db.UseExpectedVersion(sale, version);
                var cancellation = await _loadingReceiptCancellation.CancelWithinCurrentTransactionAsync(
                    [directReceiptId.Value], reason, CurrentUserIdOrNull());
                if (cancellation.Succeeded)
                {
                    sale.CancelReason = reason;
                    sale.CancelledAtUtc = DateTime.UtcNow;
                    sale.CancelledByUserId = CurrentUserIdOrNull();
                    _formTokens.Stamp(formToken, "Sale.Cancel", nameof(SalesTransaction));
                    await _db.SaveChangesAsync();
                    await coordinator.FinalizeAsync(sale.SalesBatchId);
                    if (directTransaction is not null) await directTransaction.CommitAsync();
                }
                else if (directTransaction is not null) await directTransaction.RollbackAsync();
                TempData[cancellation.Succeeded ? "ok" : "err"] = cancellation.Succeeded
                    ? "فروش مستقیم بارگیری لغو شد و ماندهٔ بار آزاد گردید."
                    : string.Join(" ", cancellation.Blockers.Select(b => b.Reason));
                if (cancellation.Succeeded && createReplacement)
                    return RedirectToAction(nameof(Create), new { correctedFromSaleId = sale.Id, returnUrl });
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                    return Redirect(returnUrl);
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception exception)
            {
                if (directTransaction is not null) await directTransaction.RollbackAsync();
                _logger.LogError(exception, "Failed to cancel direct loading sale {SaleId}.", sale.Id);
                TempData["err"] = "لغو فروش انجام نشد؛ هیچ تغییری ثبت نشد. وضعیت و اسناد مالی را بررسی کنید.";
                return RedirectToAction(nameof(Details), new { id });
            }
        }
        if (await _db.LoadingReceiptAllocations.AsNoTracking().AnyAsync(a => a.SalesTransactionId == sale.Id))
        {
            TempData["err"] = "این فروش بخشی از یک رسید چندبخشی است؛ لغو یا اصلاح را از صفحهٔ همان رسید انجام دهید.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        // فروش با قیمت باز هرگز سطر دفتر نگرفته است؛ لغو آن فقط موجودی را برمی‌گرداند.
        if (originalLedger is null && !sale.IsSalePricePending)
        {
            TempData["err"] = "سند مالی این فروش پیدا نشد؛ لغو انجام نشد.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        var stockOutMovements = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .OrderBy(m => m.Id)
            .ToListAsync();

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        var cancellationCoordinator = new SalesBatchCancellationCoordinator(_db);
        try
        {
            await cancellationCoordinator.LockAsync(sale.SalesBatchId, [sale.Id]);
            await _db.Entry(sale).ReloadAsync();
            if (sale.IsCancelled)
            {
                TempData["ok"] = "این فروش قبلاً لغو شده است.";
                return RedirectToAction(nameof(Details), new { id });
            }

            // سند دفتر پس از قفل دوباره خوانده می‌شود: اگر هم‌زمان قیمتِ فروشِ قیمت‌باز ثبت شده باشد،
            // سندِ تازه هم باید برگردد، نه این‌که لغو بی‌برگشتِ دفتر انجام شود.
            originalLedger = await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
                .OrderByDescending(l => l.Id)
                .FirstOrDefaultAsync();
            if (originalLedger is null && !sale.IsSalePricePending)
            {
                throw new BusinessRuleException("SALE_CANCEL_LEDGER_MISSING", "سند مالی این فروش پیدا نشد؛ لغو انجام نشد.");
            }

            _db.UseExpectedVersion(sale, version);
            sale.IsCancelled = true;
            sale.CancelReason = reason;
            sale.CancelledAtUtc = DateTime.UtcNow;
            sale.CancelledByUserId = CurrentUserIdOrNull();

            // PTG-P0-01 — ابطال هم یک‌بارمصرف است: توکن با همان SaveChanges سند مصرف می‌شود.
            _formTokens.Stamp(formToken, "Sale.Cancel", nameof(SalesTransaction));

            // فروش مستقیم از موتر حرکت موجودی ندارد و تنها ردِ آن لینکِ موتر است. اگر
            // TruckDispatch.SalesTransactionId (که یکتاست) روی همین فروشِ لغوشده مانده باشد، موتر برای
            // فروش دوبارهٔ باقی‌مانده قفل می‌ماند. لینک را به یک فروشِ فعالِ دیگرِ همان موتر منتقل
            // می‌کنیم و اگر نبود آزادش می‌کنیم. مقدار باقی‌مانده جای دیگری ذخیره نشده و همیشه از
            // فروش‌های لغونشده محاسبه می‌شود، پس هیچ عددی نیاز به برگشت دستی ندارد.
            var linkedDispatch = await _db.TruckDispatches
                .FirstOrDefaultAsync(d => d.SalesTransactionId == sale.Id);
            if (linkedDispatch is not null)
            {
                var dispatchId = linkedDispatch.Id;
                linkedDispatch.SalesTransactionId = await _db.SalesTransactions
                    .Where(s => s.Id != sale.Id && !s.IsCancelled && s.TruckDispatchId == dispatchId)
                    .OrderBy(s => s.Id)
                    .Select(s => (int?)s.Id)
                    .FirstOrDefaultAsync();
            }

            if (originalLedger is not null)
            {
                await LedgerReversalWriter.ReverseAsync(
                    _db,
                    originalLedger,
                    _businessClock.Today,
                    $"لغو فروش #{sale.Id} | {originalLedger.Description}",
                    sale.InvoiceNumber);
            }

            foreach (var stockOutMovement in stockOutMovements)
            {
                await _movements.PostReversalAsync(
                    stockOutMovement,
                    _businessClock.Today,
                    $"Reversal for cancelled SaleId={sale.Id}");
            }

            await _db.SaveChangesAsync();
            await ReverseSaleAccountingAsync(sale);
            await cancellationCoordinator.FinalizeAsync(sale.SalesBatchId);

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(exception, "Failed to cancel sale {SaleId}.", sale.Id);
            TempData["err"] = "لغو فروش انجام نشد؛ برگشت اسناد حسابداری ناموفق بود و هیچ تغییری ثبت نشد.";

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);

            return RedirectToAction(nameof(Details), new { id });
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        if (createReplacement)
        {
            TempData["ok"] = "فروش ابطال شد. حالا فروش جایگزین را ثبت کنید.";
            return RedirectToAction(nameof(Create), new { correctedFromSaleId = sale.Id, returnUrl });
        }

        TempData["ok"] = "فروش لغو شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    private int? CurrentUserIdOrNull()
    {
        var raw = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(raw, out var userId) ? userId : null;
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(
        int? contractId = null,
        int? sourcePurchaseContractId = null,
        int? correctedFromSaleId = null,
        string? returnUrl = null,
        Guid? draft = null)
    {
        // ادامهٔ پیش‌نویس: همان نمایش دوبارهٔ فرم پس از خطای اعتبارسنجی.
        var draftSnapshot = await this.LoadFormDraftAsync(draft, FormDraftForms.SalesCreate);
        if (draftSnapshot is not null)
        {
            var restored = new SalesCreateViewModel();
            await TryUpdateModelAsync(restored, string.Empty, draftSnapshot.ToValueProvider());
            ModelState.Clear();
            NormalizeCreateModel(restored);
            await PopulateLookupsAsync(createModel: restored);
            return View(restored);
        }

        var model = new SalesCreateViewModel
        {
            SaleDate = _businessClock.Today,
            SaleStage = SaleStage.TerminalStock,
            Currency = SystemCurrency.BaseCurrencyCode
        };

        // PTG-P2-03 — فروشِ جایگزین: فرم با مقادیرِ سندِ ابطال‌شده پر می‌شود تا کاربر فقط
        // همان چیزی را که اشتباه بود عوض کند. هیچ عددی خودکار ثبت نمی‌شود؛ این فقط
        // پیش‌نویس است و ثبت مثل هر فروش دیگری از همان مسیر عادی می‌گذرد.
        if (correctedFromSaleId is > 0)
        {
            var original = await _db.SalesTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == correctedFromSaleId.Value);

            if (original is null || !original.IsCancelled || original.ReplacementSaleId.HasValue)
            {
                TempData["err"] = original is null
                    ? "سند اصلی پیدا نشد."
                    : original.IsCancelled
                        ? "برای این سند قبلاً فروش جایگزین ثبت شده است."
                        : "فروشِ جایگزین فقط برای سندِ ابطال‌شده ساخته می‌شود.";
                return RedirectToAction(nameof(Details), new { id = correctedFromSaleId.Value });
            }

            model.CorrectedFromSaleId = original.Id;
            model.SaleStage = original.SaleStage;
            model.ContractId = original.ContractId;
            model.CompanyId = original.CompanyId ?? 0;
            model.CustomerId = original.CustomerId;
            model.SupplierId = original.SupplierId;
            model.ProductId = original.ProductId;
            model.DestinationLocationId = original.DestinationLocationId;
            model.ShipmentId = original.ShipmentId;
            model.SourcePurchaseContractId = original.SourcePurchaseContractId;
            model.QuantityMt = original.QuantityMt;
            model.Currency = original.Currency;
            model.UnitPriceInCurrency = original.UnitPriceInCurrency;
            model.AppliedFxRateToUsd = original.AppliedFxRateToUsd;
            model.IsSalePricePending = original.IsSalePricePending;
            model.SaleDate = original.SaleDate;
            model.TicketSerialNumber = original.TicketSerialNumber;
            model.StockSourceType = original.StockSourceType;
            model.Notes = original.Notes;
            model.ReturnUrl = returnUrl;

            ViewData["SaleCorrectionOriginalInvoice"] = original.InvoiceNumber;

            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        if (contractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value && c.ContractType == ContractType.Sale);
            if (contract is not null)
            {
                model.ContractId = contract.Id;
                model.CompanyId = contract.CompanyId;
                if (contract.CustomerId.HasValue)
                {
                    model.CustomerId = contract.CustomerId.Value;
                }

                model.ProductId = contract.ProductId;
                model.DestinationLocationId = contract.DestinationLocationId;
            }
        }

        if (sourcePurchaseContractId.HasValue)
        {
            var sourceContract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == sourcePurchaseContractId.Value && c.ContractType == ContractType.Purchase);
            if (sourceContract is not null)
            {
                model.SourcePurchaseContractId = sourceContract.Id;
                if (!model.ContractId.HasValue || model.ProductId <= 0)
                {
                    model.ProductId = sourceContract.ProductId;
                }

                if (!model.ContractId.HasValue || model.CompanyId <= 0)
                {
                    model.CompanyId = sourceContract.CompanyId;
                }
            }
        }

        model.ReturnUrl = returnUrl;

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> CreateFromShipment(int shipmentId, string? returnUrl = null)
    {
        var context = await BuildShipmentFlowSaleContextAsync(shipmentId);
        if (context is null)
        {
            return NotFound();
        }

        var model = context.Model;

        // محمولهٔ تک‌قراردادی: قرارداد منبع بدیهی است و پیش‌فرض ست می‌شود تا ردیابی جواز حفظ شود.
        if (context.ContractFacts.Count == 1)
        {
            model.SourcePurchaseContractId = context.ContractFacts.Keys.First();
        }

        model.QuantityMt = ResolveShipmentFlowAvailableMt(context, model.SourcePurchaseContractId);
        model.SaleDate = _businessClock.Today;
        model.Currency = SystemCurrency.BaseCurrencyCode;
        model.InvoiceNumber = $"SHIP-{shipmentId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        model.ReturnUrl = NormalizeLocalReturnUrl(returnUrl);
        await PopulateShipmentFlowSaleLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFromShipment(ShipmentFlowSaleCreateViewModel model)
    {
        await ValidateBuyerAsync(model.CustomerId, model.SupplierId);
        var context = await BuildShipmentFlowSaleContextAsync(model.ShipmentId);
        if (context is null)
        {
            return NotFound();
        }

        ApplyShipmentFlowSaleDisplay(model, context.Model);
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.InvoiceNumber = model.InvoiceNumber?.Trim() ?? string.Empty;
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        model.ReturnUrl = NormalizeLocalReturnUrl(model.ReturnUrl);

        // قرارداد منبع اختیاری است و فقط برای فروشِ واقعاً تک‌قراردادی معنا دارد. فروش کلی از محموله‌ای
        // با چند قرارداد (حتی با جواز/تأمین‌کنندهٔ متفاوت) مجاز است و با همان تقسیم وزنیِ موجود بین
        // قراردادها سرشکن می‌شود؛ جواز فقط برچسب قرارداد است، نه شرط فروش.
        if (context.ContractFacts.Count == 1)
        {
            model.SourcePurchaseContractId ??= context.ContractFacts.Keys.First();
        }

        ShipmentFlowSaleContractFacts? sourceContract = null;
        if (model.SourcePurchaseContractId.HasValue
            && !context.ContractFacts.TryGetValue(model.SourcePurchaseContractId.Value, out sourceContract))
        {
            ModelState.AddModelError(
                nameof(model.SourcePurchaseContractId),
                "قرارداد منبع انتخاب‌شده جزو قراردادهای بارگیری‌شدهٔ این محموله نیست.");
        }

        // جواز فروش فقط وقتی معنا دارد که قرارداد منبع مشخص باشد یا همهٔ قراردادهای محموله یک جواز
        // داشته باشند. در فروش چندجوازی هیچ جواز ساختگی ثبت نمی‌شود و مقدار null می‌ماند.
        int? saleCompanyId = sourceContract?.CompanyId ?? (context.CompanyId > 0 ? context.CompanyId : null);
        var saleProductId = sourceContract?.ProductId ?? context.ProductId;
        var availableQuantityMt = sourceContract?.AvailableQuantityMt ?? context.Model.AvailableQuantityMt;

        if (sourceContract is null && saleProductId <= 0)
        {
            ModelState.AddModelError(
                nameof(model.SourcePurchaseContractId),
                "قراردادهای این محموله محصول یکسان ندارند؛ برای فروش، قرارداد منبع بار را انتخاب کنید.");
        }

        if (model.QuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "مقدار فروش باید بزرگ‌تر از صفر باشد.");
        }

        if (model.QuantityMt > availableQuantityMt + 0.0001m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt),
                $"مقدار فروش نمی‌تواند از مقدار قابل فروش ({availableQuantityMt:N4} تن) بیشتر باشد.");
        }

        if (model.IsSalePricePending)
        {
            // قیمت باز: قیمت و نرخ ارز بعداً از صفحهٔ همان فروش ثبت می‌شود.
            ModelState.Remove(nameof(model.UnitPriceInCurrency));
            ModelState.Remove(nameof(model.AppliedFxRateToUsd));
            model.UnitPriceInCurrency = 0m;
            model.AppliedFxRateToUsd = null;
            model.PrintAfterSave = false;
        }
        else if (model.UnitPriceInCurrency <= 0m)
        {
            ModelState.AddModelError(nameof(model.UnitPriceInCurrency), "قیمت هر تن باید بزرگ‌تر از صفر باشد.");
        }

        if (model.SaleDate == default)
        {
            ModelState.AddModelError(nameof(model.SaleDate), "تاریخ فروش الزامی است.");
        }

        if (!await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.CustomerId && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
        }

        if (model.DestinationLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value))
        {
            ModelState.AddModelError(nameof(model.DestinationLocationId), "مقصد انتخاب‌شده معتبر نیست.");
        }

        if (string.IsNullOrWhiteSpace(model.InvoiceNumber))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "شماره فاکتور الزامی است.");
        }
        else if (await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.InvoiceNumber == model.InvoiceNumber))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "این شماره فاکتور قبلاً ثبت شده است.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateShipmentFlowSaleLookupsAsync(model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = model.IsSalePricePending
                ? SalePendingPricing.NeutralConversion(model.Currency, model.SaleDate)
                : await _currencyConversion.ResolveToBaseAsync(
                    model.Currency,
                    model.SaleDate.Date,
                    model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            await PopulateShipmentFlowSaleLookupsAsync(model);
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            }

            // بازخوانی سقف با همان قرارداد منبع؛ مقدار قابل فروشِ همان قرارداد ملاک است، نه کل محموله.
            var latestContext = await BuildShipmentFlowSaleContextAsync(model.ShipmentId);
            var latestAvailableMt = latestContext is null
                ? 0m
                : ResolveShipmentFlowAvailableMt(latestContext, model.SourcePurchaseContractId);
            if (latestContext is null || model.QuantityMt > latestAvailableMt + 0.0001m)
            {
                throw new BusinessRuleException(
                    "SHIPMENT_SALE_QUANTITY_EXCEEDED",
                    $"مقدار قابل فروش محموله تغییر کرده است. مقدار فعلی: {latestAvailableMt:N4} تن.");
            }

            // جواز/شرکت و محصولِ فروش از قرارداد منبع می‌آید؛ اگر قرارداد منبع نداریم (محمولهٔ یکنواخت
            // یا رفتار قدیمی) همان مقدار سطح‌محموله استفاده می‌شود.
            var latestSourceContract = model.SourcePurchaseContractId.HasValue
                && latestContext.ContractFacts.TryGetValue(model.SourcePurchaseContractId.Value, out var latestFacts)
                    ? latestFacts
                    : null;

            var totalInCurrency = decimal.Round(
                model.QuantityMt * model.UnitPriceInCurrency,
                4,
                MidpointRounding.AwayFromZero);
            var sale = new SalesTransaction
            {
                ContractId = null,
                SourcePurchaseContractId = latestSourceContract?.ContractId,
                // فروش چندجوازی: جواز واحدی وجود ندارد و null ثبت می‌شود (هیچ جواز ساختگی).
                CompanyId = latestSourceContract?.CompanyId
                    ?? (latestContext.CompanyId > 0 ? latestContext.CompanyId : null),
                CustomerId = model.CustomerId,
                SupplierId = model.SupplierId,
                ProductId = latestSourceContract?.ProductId ?? latestContext.ProductId,
                DestinationLocationId = model.DestinationLocationId ?? latestContext.DestinationLocationId,
                ShipmentId = model.ShipmentId,
                SaleStage = SaleStage.InTransit,
                InvoiceNumber = model.InvoiceNumber,
                SaleDate = model.SaleDate.Date,
                QuantityMt = model.QuantityMt,
                Currency = conversion.SourceCurrencyCode,
                UnitPriceInCurrency = model.UnitPriceInCurrency,
                AppliedFxRateToUsd = conversion.AppliedRateToBase,
                UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
                TotalInCurrency = totalInCurrency,
                TotalUsd = conversion.ConvertToBase(totalInCurrency),
                Notes = model.Notes
            };
            if (model.IsSalePricePending)
            {
                SalePendingPricing.MarkPending(sale, model.Currency);
            }

            _db.SalesTransactions.Add(sale);
            await _db.SaveChangesAsync();

            var ledgerEntry = SalePendingPricing.ShouldPostLedger(sale)
                ? Ledger.Post(SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: null))
                : null;
            await _db.SaveChangesAsync();

            await PostSaleAccountingAsync(sale);

            await _audit.LogAsync(
                nameof(SalesTransaction),
                sale.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("CompanyId", sale.CompanyId),
                    ("CustomerId", sale.CustomerId),
                    ("SupplierId", sale.SupplierId),
                    ("ProductId", sale.ProductId),
                    ("ShipmentId", sale.ShipmentId),
                    ("SourcePurchaseContractId", sale.SourcePurchaseContractId),
                    ("SaleStage", sale.SaleStage),
                    ("InvoiceNumber", sale.InvoiceNumber),
                    ("QuantityMt", sale.QuantityMt),
                    ("TotalUsd", sale.TotalUsd),
                    ("IsSalePricePending", sale.IsSalePricePending),
                    ("LedgerReference", ledgerEntry?.Reference)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = sale.IsSalePricePending
                ? "فروش از محموله با قیمت باز ثبت شد؛ سند مالی پس از تعیین قیمت ثبت می‌شود."
                : "فروش از جریان حمل محموله با موفقیت ثبت شد.";
            if (model.PrintAfterSave)
            {
                return RedirectToAction("Sale", "Invoices", new { id = sale.Id });
            }

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction("Details", "ShipmentPnl", new { id = model.ShipmentId });
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            var latest = await BuildShipmentFlowSaleContextAsync(model.ShipmentId);
            if (latest is not null)
            {
                ApplyShipmentFlowSaleDisplay(model, latest.Model);
            }
            ModelState.AddModelError(nameof(model.QuantityMt), ex.Message);
            await PopulateShipmentFlowSaleLookupsAsync(model);
            return View(model);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var sale = await _db.SalesTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsCancelled)
        {
            TempData["err"] = "این فروش لغو شده است و قابل ویرایش نیست.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        var model = new SalesCreateViewModel
        {
            // PTG-P1-05 — نسخه‌ای که کاربر می‌بیند، با فرم برمی‌گردد.
            Version = sale.Version,
            SaleStage = sale.SaleStage,
            ContractId = sale.ContractId,
            // فروش‌های چندجوازیِ محموله جواز واحد ندارند؛ فرم ویرایش انتخاب جواز را اجباری می‌کند.
            CompanyId = sale.CompanyId ?? 0,
            CustomerId = sale.CustomerId,
            SupplierId = sale.SupplierId,
            ProductId = sale.ProductId,
            DestinationLocationId = sale.DestinationLocationId,
            ShipmentId = sale.ShipmentId,
            QuantityMt = sale.QuantityMt,
            Currency = sale.Currency,
            UnitPriceInCurrency = sale.UnitPriceInCurrency,
            AppliedFxRateToUsd = sale.AppliedFxRateToUsd,
            IsSalePricePending = sale.IsSalePricePending,
            InvoiceNumber = sale.InvoiceNumber,
            SaleDate = sale.SaleDate,
            Notes = sale.Notes,
            // Gap #4 — show existing values as context (edit POST only allows Quantity and Notes changes)
            TicketSerialNumber = sale.TicketSerialNumber,
            StockSourceType = sale.StockSourceType,
            ReturnUrl = returnUrl
        };

        ViewData["IsEdit"] = true;
        ViewData["EditId"] = id;

        await PopulateLookupsAsync(createModel: model);
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, SalesCreateViewModel model)
    {
        var sale = await _db.SalesTransactions
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        // PTG-P1-05 — معیارِ برخورد، نسخه‌ای است که کاربر هنگامِ بازکردنِ فرم دید،
        // نه نسخه‌ای که همین حالا خوانده شد.
        _db.UseExpectedVersion(sale, model.Version);

        if (sale.IsCancelled)
        {
            ModelState.AddModelError(string.Empty, "این فروش لغو شده است و قابل ویرایش نیست.");
        }

        // قیمت فروش باز از این فرم تعیین نمی‌شود (مسیر جدای SetPrice)؛ فیلد قیمت در فرم غیرفعال است.
        if (sale.IsSalePricePending)
        {
            ModelState.Remove(nameof(model.UnitPriceInCurrency));
            ModelState.Remove(nameof(model.UnitPriceUsd));
            ModelState.Remove(nameof(model.AppliedFxRateToUsd));
            model.UnitPriceInCurrency = sale.UnitPriceInCurrency;
            model.AppliedFxRateToUsd = null;
        }

        if (model.CompanyId != sale.CompanyId
            || model.CustomerId != sale.CustomerId
            || model.SupplierId != sale.SupplierId
            || model.ProductId != sale.ProductId
            || model.ContractId != sale.ContractId
            || model.DestinationLocationId != sale.DestinationLocationId
            || model.ShipmentId != sale.ShipmentId
            || model.SaleStage != sale.SaleStage
            || model.SaleDate.Date != sale.SaleDate.Date
            || model.InvoiceNumber != sale.InvoiceNumber
            || model.Currency != sale.Currency
            || model.UnitPriceInCurrency != sale.UnitPriceInCurrency
            // فرم برای ارز پایه فیلد نرخ را خالی می‌فرستد (finance-forms.js)؛ خالی یعنی «تغییر نکرده».
            || (model.AppliedFxRateToUsd.HasValue && model.AppliedFxRateToUsd != sale.AppliedFxRateToUsd))
        {
            ModelState.AddModelError(string.Empty, "در نسخه فعلی فقط مقدار و یادداشت فروش قابل ویرایش است.");
        }

        var quantityChanged = model.QuantityMt != sale.QuantityMt;
        if (quantityChanged && ModelState.IsValid)
        {
            var blocker = model.QuantityMt <= 0m
                ? "مقدار فروش باید بزرگ‌تر از صفر باشد."
                : await GetQuantityEditBlockerAsync(sale, model.QuantityMt);
            if (blocker is not null)
            {
                ModelState.AddModelError(nameof(model.QuantityMt), blocker);
            }
        }

        if (!ModelState.IsValid)
        {
            ViewData["IsEdit"] = true;
            ViewData["EditId"] = id;
            await PopulateLookupsAsync(createModel: model);
            return View("Create", model);
        }

        var notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        if (quantityChanged)
        {
            try
            {
                await ApplyQuantityEditAsync(sale, model.QuantityMt, notes);
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(nameof(model.QuantityMt), ex.Message);
                ViewData["IsEdit"] = true;
                ViewData["EditId"] = id;
                await PopulateLookupsAsync(createModel: model);
                return View("Create", model);
            }

            TempData["ok"] = "مقدار فروش ویرایش شد؛ موجودی و سند مالی با مقدار جدید اصلاح شد.";
        }
        else
        {
            sale.Notes = notes;
            await _db.SaveChangesAsync();
            TempData["ok"] = "یادداشت فروش به‌روزرسانی شد.";
        }

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // ویرایش مقدار فقط برای فروش عادیِ ثبت‌شده از یک مخزن ممکن است. هر چیزی که با مقدار قدیمی
    // قفل شده و این مسیر نمی‌تواند دقیق برگرداند (ژورنال دفتر کل جدید، مصرف پیش‌دریافت، Lot، ضایعه،
    // فروش گروهی/پیش‌فروش/موتر) ویرایش را می‌بندد و کاربر از مسیر «لغو و فروش جایگزین» می‌رود.
    private async Task<string?> GetQuantityEditBlockerAsync(SalesTransaction sale, decimal newQuantityMt)
    {
        const string useCorrection = " این فروش را از دکمهٔ «لغو» و ثبت فروش جایگزین اصلاح کنید.";

        if (sale.SaleStage != SaleStage.TerminalStock
            || sale.SalesBatchId.HasValue
            || sale.PreSaleOrderId.HasValue
            || sale.TruckDispatchId.HasValue
            || !sale.CompanyId.HasValue)
        {
            return "ویرایش مقدار فقط برای فروش عادی از مخزن ممکن است." + useCorrection;
        }

        var outs = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .Select(m => new { m.Id, m.TerminalId, m.StorageTankId })
            .ToListAsync();
        if (outs.Count == 0
            || outs.Any(m => !m.StorageTankId.HasValue)
            || outs.Select(m => (m.TerminalId, m.StorageTankId)).Distinct().Count() != 1)
        {
            return "خروج موجودی این فروش از یک مخزن مشخص نیست." + useCorrection;
        }

        var outIds = outs.Select(m => (int?)m.Id).ToList();
        if (await _db.InventoryMovements.AnyAsync(m => outIds.Contains(m.ReversalOfInventoryMovementId)))
        {
            return "خروج موجودی این فروش قبلاً برگشت خورده است." + useCorrection;
        }

        var createdEventId = Services.Accounting.SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id);
        var cogsEventId = Services.Accounting.SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id);
        if (await _db.JournalEntries.AnyAsync(j =>
                j.SourceModule == Services.Accounting.SalesAccountingAdapter.SourceModule
                && (j.SourceEventId == createdEventId || j.SourceEventId == cogsEventId))
            || await _db.SalesCostConsumptions.AnyAsync(c => c.SalesTransactionId == sale.Id))
        {
            return "برای این فروش سند حسابداری (درآمد/بهای تمام‌شده) ثبت شده است." + useCorrection;
        }

        if (await _db.CustomerPaymentAllocationApplications.AnyAsync(a =>
                a.SalesTransactionId == sale.Id && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            || await _db.SaleLotAllocations.AnyAsync(a => a.SalesTransactionId == sale.Id)
            || await _db.LossEvents.AnyAsync(l => l.SalesTransactionId == sale.Id))
        {
            return "به این فروش پیش‌دریافت، Lot یا ضایعه وصل است." + useCorrection;
        }

        // قیمت باز: مبلغی وجود ندارد که دریافت‌ها با آن مقایسه شوند؛ کنترل هنگام تعیین قیمت است.
        if (sale.IsSalePricePending)
        {
            return null;
        }

        var paidUsd = await _db.PaymentTransactions
            .Where(p => p.SalesTransactionId == sale.Id)
            .SumAsync(p => (decimal?)p.AmountUsd) ?? 0m;
        var newTotalUsd = decimal.Round(
            decimal.Round(newQuantityMt * sale.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero)
                * (sale.AppliedFxRateToUsd ?? 1m),
            4,
            MidpointRounding.AwayFromZero);
        if (paidUsd > newTotalUsd)
        {
            return $"دریافت ثبت‌شده روی این فروش ({paidUsd:N2} USD) از مبلغ جدید ({newTotalUsd:N2} USD) بیشتر می‌شود.";
        }

        return null;
    }

    // همان فروش و همان شمارهٔ فاکتور می‌ماند؛ اسنادِ مقدارِ قبلی برگشت رسمی می‌خورند و با مقدارِ جدید
    // از همان مسیرهای ثبت فروش دوباره ثبت می‌شوند — همه در یک تراکنش.
    //   • موجودی: خروج قبلی با PostReversalAsync برمی‌گردد و از فروش جدا می‌شود (SalesTransactionId=null)
    //     تا خواننده‌های «خروجِ این فروش» (COGS، لغو، گزارش‌ها) فقط خروجِ فعلی را ببینند.
    //   • دفتر: سطر فروشِ قبلی برگشت می‌خورد و سطر تازه با مرجع «فاکتور/E{n}» ثبت می‌شود تا برگشتِ
    //     بعدی (ویرایش یا لغو دوباره) با محافظ «دو بار برگشت» برخورد نکند.
    private async Task ApplyQuantityEditAsync(SalesTransaction sale, decimal newQuantityMt, string? notes)
    {
        var oldOuts = await _db.InventoryMovements
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .OrderBy(m => m.Id)
            .ToListAsync();
        var terminalId = oldOuts[0].TerminalId;
        var storageTankId = oldOuts[0].StorageTankId!.Value;

        // فروش با قیمت باز سطر دفتر ندارد: فقط موجودی و تخصیص منبع با مقدار جدید بازسازی می‌شود.
        var pricePending = sale.IsSalePricePending;
        var oldLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();
        if (oldLedger is null && !pricePending)
        {
            throw new BusinessRuleException("SALE_EDIT_LEDGER_MISSING", "سند مالی این فروش پیدا نشد؛ ویرایش انجام نشد.");
        }

        if (pricePending && oldLedger is not null)
        {
            throw new BusinessRuleException("SALE_EDIT_PENDING_HAS_LEDGER", "فروش با قیمت باز نباید سند مالی داشته باشد؛ ویرایش انجام نشد.");
        }

        if (oldLedger is not null && Services.CompanyFlow.CompanyFlowSourceTypes.IsReversalReference(oldLedger.Reference))
        {
            throw new BusinessRuleException("SALE_EDIT_LEDGER_REVERSED", "سند مالی این فروش قبلاً برگشت خورده است؛ ویرایش انجام نشد.");
        }

        var revision = oldLedger is null
            ? 0
            : await _db.LedgerEntries
                .CountAsync(l => l.SourceType == "Sale" && l.SourceId == sale.Id && l.Id <= oldLedger.Id
                    && (l.Reference == null || !l.Reference.EndsWith(Services.CompanyFlow.CompanyFlowSourceTypes.ReversalReferenceSuffix)));

        var conversion = pricePending
            ? null
            : await _currencyConversion.ResolveToBaseAsync(
                sale.Currency,
                sale.SaleDate.Date,
                sale.AppliedFxRateToUsd);

        var oldQuantityMt = sale.QuantityMt;
        var oldTotalUsd = sale.TotalUsd;

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            if (await LockStorageTankAsync(storageTankId) is null)
            {
                throw new BusinessRuleException("SALE_TERMINAL_STOCK_TANK_NOT_FOUND", "مخزن این فروش دیگر معتبر نیست.");
            }

            // ۱) موجودیِ مقدار قبلی به همان تاریخ به مخزن برمی‌گردد و از فروش جدا می‌شود.
            var oldMovementIds = new List<int>();
            foreach (var oldOut in oldOuts)
            {
                var reversal = await _movements.PostReversalAsync(
                    oldOut,
                    oldOut.MovementDate,
                    $"Reversal for edited SaleId={sale.Id}: {oldQuantityMt:0.####} → {newQuantityMt:0.####} MT");
                oldOut.SalesTransactionId = null;
                oldMovementIds.Add(oldOut.Id);
                if (reversal is not null)
                {
                    reversal.SalesTransactionId = null;
                }
            }

            // ۲) سطر دفترِ مقدار قبلی برگشت می‌خورد (همان تاریخ فروش).
            if (oldLedger is not null)
            {
                await LedgerReversalWriter.ReverseAsync(
                    _db,
                    oldLedger,
                    oldLedger.EntryDate,
                    $"ویرایش مقدار فروش #{sale.Id} ({oldQuantityMt:0.####} → {newQuantityMt:0.####} MT) | {oldLedger.Description}",
                    sale.InvoiceNumber);
            }

            // ۳) خود فروش با مقدار و مبالغ جدید؛ تخصیص منبعِ قبلی پاک و از خروج جدید دوباره ساخته می‌شود.
            sale.QuantityMt = newQuantityMt;
            if (conversion is not null)
            {
                var totalInCurrency = decimal.Round(newQuantityMt * sale.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
                sale.TotalInCurrency = totalInCurrency;
                sale.TotalUsd = conversion.ConvertToBase(totalInCurrency);
            }
            sale.Notes = notes;
            _db.SalesTransactionSourceAllocations.RemoveRange(await _db.SalesTransactionSourceAllocations
                .Where(a => a.SalesTransactionId == sale.Id)
                .ToListAsync());
            await _db.SaveChangesAsync();

            // ۴) خروج جدید از همان مخزن؛ اول از همان قرارداد منبع، بعد FIFO (همان قاعدهٔ ثبت فروش).
            var stockAllocations = await EnsureSufficientTerminalStockAsync(
                sale.ProductId,
                newQuantityMt,
                sale.SaleDate,
                terminalId,
                storageTankId,
                sale.CompanyId!.Value,
                sale.SourcePurchaseContractId);
            var stockOutMovements = stockAllocations.Select(allocation => new InventoryMovement
            {
                ProductId = sale.ProductId,
                ContractId = allocation.ContractId,
                TerminalId = terminalId,
                StorageTankId = storageTankId,
                SalesTransactionId = sale.Id,
                Direction = MovementDirection.Out,
                MovementDate = sale.SaleDate,
                QuantityMt = allocation.QuantityMt,
                ReferenceDocument = sale.InvoiceNumber,
                Notes = BuildSaleInventoryNotes(
                    sale.SaleStage,
                    sale.InvoiceNumber,
                    $"SaleId={sale.Id} | edited {oldQuantityMt:0.####} → {newQuantityMt:0.####} MT")
            }).ToList();
            await _movements.PostOutboundRangeAsync(stockOutMovements, StockGuard.Full);

            var saleSourcePlan = await _sourceAllocations.BuildFromInventoryMovementsAsync(stockOutMovements, sale.QuantityMt);
            _sourceAllocations.ApplyLegacyHeader(sale, saleSourcePlan);
            await _sourceAllocations.PersistSaleAsync(sale, saleSourcePlan);
            await _lineage.AllocateSaleAsync(sale, sale.SourcePurchaseContractId, terminalId, storageTankId);
            if (pricePending)
            {
                await ClearPendingSaleLotAmountsAsync(sale.Id);
            }

            // ۵) سطر دفتر تازه با مقدار جدید.
            LedgerEntry? ledgerEntry = null;
            if (conversion is not null)
            {
                ledgerEntry = Ledger.Post(SaleLedgerFactory.BuildSaleLedgerEntry(
                        sale,
                        conversion,
                        contractId: sale.ContractId ?? sale.SourcePurchaseContractId)
                    with { Reference = $"{sale.InvoiceNumber}/E{revision}" });
                await _db.SaveChangesAsync();

                await PostSaleAccountingAsync(sale);
            }

            await _audit.LogAsync(
                nameof(SalesTransaction),
                sale.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("QuantityMt", oldQuantityMt, sale.QuantityMt),
                    ("TotalUsd", oldTotalUsd, sale.TotalUsd),
                    ("InventoryMovementIds", string.Join(",", oldMovementIds), string.Join(",", stockOutMovements.Select(m => m.Id))),
                    ("LedgerReference", oldLedger?.Reference, ledgerEntry?.Reference)));
            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    [HttpGet]
    public async Task<IActionResult> SuggestedPrice(int? sourcePurchaseContractId)
    {
        if (!sourcePurchaseContractId.HasValue || sourcePurchaseContractId <= 0)
        {
            return Json(new
            {
                ok = false,
                finalUnitPrice = (decimal?)null,
                formulaText = string.Empty,
                needsReview = true,
                reason = "قرارداد انتخاب نشده",
                fallbackApplied = false
            });
        }

        var contract = await _db.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == sourcePurchaseContractId.Value);

        if (contract is null)
        {
            return Json(new
            {
                ok = false,
                finalUnitPrice = (decimal?)null,
                formulaText = string.Empty,
                needsReview = true,
                reason = "قرارداد یافت نشد",
                fallbackApplied = false
            });
        }

        var result = await _pricing.CalculateContractPriceAsync(contract.Id);

        return Json(new
        {
            ok = result.FinalUnitPrice.HasValue,
            finalUnitPrice = result.FinalUnitPrice,
            formulaText = result.FormulaText,
            needsReview = result.NeedsReview,
            reason = result.Reason,
            fallbackApplied = result.FallbackApplied
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> SourceStockBalance(
        int? productId,
        int? sourcePurchaseContractId,
        int? companyId,
        int? sourceTerminalId,
        int? sourceStorageTankId,
        DateTime? saleDate,
        CancellationToken ct)
    {
        if (!productId.HasValue || productId.Value <= 0
            || !sourceTerminalId.HasValue || sourceTerminalId.Value <= 0
            || !sourceStorageTankId.HasValue || sourceStorageTankId.Value <= 0
            || (!sourcePurchaseContractId.HasValue && (!companyId.HasValue || companyId.Value <= 0)))
        {
            return Json(new
            {
                ok = false,
                availableMt = (decimal?)null,
                message = "برای نمایش موجودی، شرکت، محصول، ترمینال و مخزن را انتخاب کنید."
            });
        }

        decimal available;
        if (sourcePurchaseContractId is > 0)
        {
            available = await _stock.GetFreeQuantityMtAsync(
                productId.Value,
                terminalId: sourceTerminalId.Value,
                contractId: sourcePurchaseContractId.Value,
                storageTankId: sourceStorageTankId.Value,
                asOfUtc: (saleDate ?? DateTime.UtcNow).Date,
                ct: ct);
        }
        else
        {
            var balances = await GetTerminalStockSourceBalancesAsync(
                productId.Value,
                sourceTerminalId.Value,
                sourceStorageTankId.Value,
                (saleDate ?? DateTime.UtcNow).Date);
            available = balances
                .Where(balance => balance.CompanyId == companyId!.Value)
                .Sum(balance => balance.AvailableMt);
        }

        return Json(new
        {
            ok = true,
            availableMt = available,
            message = string.Empty
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        SalesCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        NormalizeCreateModel(model);
        var normalizedInvoice = model.InvoiceNumber?.Trim() ?? string.Empty;
        var normalizedNotes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        var requiresTerminalStock = RequiresTerminalStock(model.SaleStage);
        var salePricePending = model.IsSalePricePending;
        if (salePricePending)
        {
            // قیمت باز: هر عددی که در فیلد قیمت مانده باشد نادیده گرفته می‌شود؛ قیمت فقط از فرم
            // «تعیین قیمت فروش» وارد می‌شود. نرخ تبدیل هم همان‌جا و با همان قیمت تعیین می‌شود.
            ModelState.Remove(nameof(model.UnitPriceInCurrency));
            ModelState.Remove(nameof(model.UnitPriceUsd));
            ModelState.Remove(nameof(model.AppliedFxRateToUsd));
            model.UnitPriceInCurrency = 0m;
            model.AppliedFxRateToUsd = null;
        }

        Contract? sourcePurchaseContract = null;
        if (requiresTerminalStock && model.SourcePurchaseContractId is > 0)
        {
            sourcePurchaseContract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.SourcePurchaseContractId.Value);

            if (sourcePurchaseContract is { ContractType: ContractType.Purchase }
                && !model.ContractId.HasValue)
            {
                if (model.CompanyId != sourcePurchaseContract.CompanyId)
                {
                    model.CompanyId = sourcePurchaseContract.CompanyId;
                    ModelState.Remove(nameof(model.CompanyId));
                }

                if (model.ProductId != sourcePurchaseContract.ProductId)
                {
                    model.ProductId = sourcePurchaseContract.ProductId;
                    ModelState.Remove(nameof(model.ProductId));
                }
            }
        }

        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.CompanyId && c.IsActive);
        if (company is null)
        {
            ModelState.AddModelError(nameof(model.CompanyId), "شرکت انتخاب‌شده معتبر نیست.");
        }

        await ValidateBuyerAsync(model.CustomerId, model.SupplierId);

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ProductId && p.IsActive);
        if (product is null)
        {
            ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        if (requiresTerminalStock)
        {
            if (!model.SourceTerminalId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SourceTerminalId), "برای فروش از مخزن، انتخاب ترمینال مبدا الزامی است.");
            }

            if (!model.SourceStorageTankId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SourceStorageTankId), "برای فروش از مخزن، انتخاب مخزن مبدا الزامی است.");
            }

            var sourceTerminal = model.SourceTerminalId.HasValue
                ? await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.SourceTerminalId.Value && t.IsActive)
                : null;
            if (model.SourceTerminalId.HasValue && sourceTerminal is null)
            {
                ModelState.AddModelError(nameof(model.SourceTerminalId), "ترمینال مبدا‌ی انتخاب‌شده معتبر نیست.");
            }

            if (model.SourceStorageTankId.HasValue)
            {
                var sourceTank = await _db.StorageTanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.SourceStorageTankId.Value);
                if (sourceTank is null)
                {
                    ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن مبدا‌ی انتخاب‌شده معتبر نیست.");
                }
                else
                {
                    if (model.SourceTerminalId.HasValue && sourceTank.TerminalId != model.SourceTerminalId.Value)
                    {
                        ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن مبدا به ترمینال انتخابی تعلق ندارد.");
                    }

                    if (sourceTank.ProductId.HasValue && sourceTank.ProductId != model.ProductId)
                    {
                        ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن مبدا برای کالای انتخابی تعریف نشده است.");
                    }
                }
            }
        }

        Contract? contract = null;
        if (model.ContractId is > 0)
        {
            contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null || contract.ContractType != ContractType.Sale)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد فروش انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (!contract.CustomerId.HasValue)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد فروش انتخاب‌شده مشتری معتبر ندارد.");
                }
                else if (contract.CustomerId.Value != model.CustomerId)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با مشتری انتخابی هم‌خوان نیست.");
                }

                if (contract.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (contract.CompanyId != model.CompanyId)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با شرکت انتخابی هم‌خوان نیست.");
                }

                if (contract.DestinationLocationId.HasValue && contract.DestinationLocationId != model.DestinationLocationId)
                {
                    ModelState.AddModelError(nameof(model.DestinationLocationId), "مقصد انتخابی باید با مقصد قرارداد فروش یکسان باشد.");
                }
            }
        }
        else if (model.ContractId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ContractId), "قرارداد فروش انتخاب‌شده معتبر نیست.");
        }

        if (requiresTerminalStock && model.SourcePurchaseContractId.HasValue)
        {
            sourcePurchaseContract ??= await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.SourcePurchaseContractId.Value);
            if (sourcePurchaseContract is null || sourcePurchaseContract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "قرارداد خرید منبع موجودی انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (sourcePurchaseContract.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "قرارداد خرید منبع موجودی با کالای انتخابی هم‌خوان نیست.");
                }

                if (sourcePurchaseContract.CompanyId != model.CompanyId)
                {
                    ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "قرارداد خرید منبع موجودی با شرکت انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (model.SaleStage == SaleStage.PreSale && !model.ContractId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ContractId), "برای پیش‌فروش، انتخاب قرارداد الزامی است.");
        }

        if (model.ShipmentId.HasValue)
        {
            // قرارداد فروش برای فروشِ مبتنی بر Shipment اجباری نیست؛ فروش مستقیم از محموله بدون قرارداد
            // فروش مجاز است (هم‌راستا با CreateFromShipment). فقط اگر قرارداد فروش انتخاب شده باشد و محموله
            // هم قرارداد صریح داشته باشد، این دو باید هم‌خوان باشند.
            var shipment = await _db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (model.ContractId.HasValue
                    && shipment.ContractId.HasValue
                    && shipment.ContractId.Value != model.ContractId.Value)
                {
                    ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده با قرارداد فروش هم‌خوان نیست.");
                }

                // فروشِ مبتنی بر Shipment باید محصولش با محصول واقعی محموله و قرارداد خرید منبع هم‌خوان باشد.
                // پیش‌فروش تعهدی است و از این قید مستثناست. اگر محموله قرارداد بار ثبت‌شده نداشته باشد
                // (داده‌ی قدیمی)، اعتبارسنجی موجودِ قرارداد منبع کافی است و اینجا چیزی اضافه نمی‌شود.
                if (model.SaleStage != SaleStage.PreSale)
                {
                    var shipmentContracts = await _db.ShipmentContracts
                        .AsNoTracking()
                        .Where(sc => sc.ShipmentId == model.ShipmentId.Value && sc.Contract != null)
                        .Select(sc => new { sc.ContractId, sc.Contract!.ProductId })
                        .ToListAsync();

                    if (shipmentContracts.Count > 0)
                    {
                        var shipmentProductIds = shipmentContracts.Select(x => x.ProductId).Distinct().ToList();

                        if (model.SourcePurchaseContractId is > 0)
                        {
                            var matchedSource = shipmentContracts
                                .FirstOrDefault(x => x.ContractId == model.SourcePurchaseContractId.Value);
                            if (matchedSource is null)
                            {
                                ModelState.AddModelError(
                                    nameof(model.SourcePurchaseContractId),
                                    "قرارداد خرید منبع باید یکی از قراردادهای بارگیری‌شدهٔ همین محموله باشد.");
                            }
                            else if (matchedSource.ProductId != model.ProductId)
                            {
                                ModelState.AddModelError(
                                    nameof(model.ProductId),
                                    "کالای انتخاب‌شده با محصول قرارداد خرید منبعِ این محموله هم‌خوان نیست.");
                            }
                        }
                        else if (shipmentProductIds.Count > 1)
                        {
                            ModelState.AddModelError(
                                nameof(model.SourcePurchaseContractId),
                                "این محموله چند محصول دارد؛ برای فروش، انتخاب قرارداد خرید منبع الزامی است.");
                        }
                        else if (!shipmentProductIds.Contains(model.ProductId))
                        {
                            ModelState.AddModelError(
                                nameof(model.ProductId),
                                "کالای انتخاب‌شده با محصول این محموله هم‌خوان نیست.");
                        }
                    }
                }
            }
        }

        if (model.DestinationLocationId.HasValue)
        {
            var destinationExists = await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value);
            if (!destinationExists)
            {
                ModelState.AddModelError(nameof(model.DestinationLocationId), "مقصد انتخاب‌شده معتبر نیست.");
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedInvoice))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "شماره فاکتور الزامی است.");
        }
        else if (await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.InvoiceNumber == normalizedInvoice))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "این شماره فاکتور قبلاً ثبت شده است.");
        }

        // PTG-P2-03 — فروشِ جایگزین فقط برای سندی ساخته می‌شود که واقعاً ابطال شده و هنوز
        // جایگزینی ندارد. این بررسی جلوی «دو بار اصلاح کردن یک فروش» را می‌گیرد — که در
        // عمل یعنی دو برابر شدنِ عاید.
        SalesTransaction? correctionOriginal = null;
        if (model.CorrectedFromSaleId is > 0)
        {
            correctionOriginal = await _db.SalesTransactions
                .FirstOrDefaultAsync(s => s.Id == model.CorrectedFromSaleId.Value);

            if (correctionOriginal is null)
            {
                ModelState.AddModelError(string.Empty, "سند اصلیِ اصلاح پیدا نشد.");
            }
            else if (!correctionOriginal.IsCancelled)
            {
                ModelState.AddModelError(string.Empty, "فروشِ جایگزین فقط برای سندِ ابطال‌شده ثبت می‌شود.");
            }
            else if (correctionOriginal.ReplacementSaleId.HasValue)
            {
                ModelState.AddModelError(string.Empty, "برای این سند قبلاً فروش جایگزین ثبت شده است.");
            }
        }

        var allowedLossStages = GetAllowedSaleLossStages();
        StageLossCaptureMapper.Validate(
            model.Loss,
            (field, error) => ModelState.AddModelError(BuildLossFieldKey(field), error),
            allowedLossStages);

        if (model.Loss.Enabled)
        {
            await _lossWorkflow.ValidateAsync(
                BuildSaleLossSubmission(model, normalizedInvoice),
                (field, error) => ModelState.AddModelError(BuildLossFieldKey(field), error));
        }

        if (!ModelState.IsValid)
        {
            model.InvoiceNumber = normalizedInvoice;
            model.Notes = normalizedNotes;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        CurrencyConversionResult? conversion = null;
        if (!salePricePending)
        {
            try
            {
                conversion = await _currencyConversion.ResolveToBaseAsync(
                    model.Currency,
                    model.SaleDate.Date,
                    model.AppliedFxRateToUsd);
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
                model.InvoiceNumber = normalizedInvoice;
                model.Notes = normalizedNotes;
                await PopulateLookupsAsync(createModel: model);
                return View(model);
            }
        }

        // قیمت باز: ستون‌های مبلغ صفر ذخیره می‌شوند ولی با IsSalePricePending «نامعتبر» علامت دارند
        // و هیچ مسیری آن‌ها را به‌عنوان مبلغ نمی‌خواند (سطر دفتر و ژورنال اصلاً ساخته نمی‌شود).
        var totalInCurrency = salePricePending
            ? 0m
            : decimal.Round(model.QuantityMt * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var unitPriceUsd = conversion?.ConvertToBase(model.UnitPriceInCurrency) ?? 0m;
        var totalUsd = conversion?.ConvertToBase(totalInCurrency) ?? 0m;

        var sale = new SalesTransaction
        {
            ContractId = model.ContractId,
            CompanyId = model.CompanyId,
            CustomerId = model.CustomerId,
            SupplierId = model.SupplierId,
            ProductId = model.ProductId,
            DestinationLocationId = model.DestinationLocationId,
            ShipmentId = model.ShipmentId,
            SaleStage = model.SaleStage,
            InvoiceNumber = normalizedInvoice,
            SaleDate = model.SaleDate,
            QuantityMt = model.QuantityMt,
            Currency = conversion?.SourceCurrencyCode ?? model.Currency,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion?.AppliedRateToBase,
            UnitPriceUsd = unitPriceUsd,
            TotalInCurrency = totalInCurrency,
            TotalUsd = totalUsd,
            IsSalePricePending = salePricePending,
            Notes = normalizedNotes,
            TicketSerialNumber = string.IsNullOrWhiteSpace(model.TicketSerialNumber) ? null : model.TicketSerialNumber.Trim(),
            StockSourceType = model.StockSourceType
        };

        // Duplicate-submit guard: token persists atomically with the sale (inside
        // the transaction below), so a second submit is rejected, not duplicated.
        _formTokens.Stamp(formToken, "Sale.Create", nameof(SalesTransaction));

        IReadOnlyList<TerminalStockAllocation> stockAllocations = [];

        try
        {
            if (requiresTerminalStock)
            {
                stockAllocations = await EnsureSufficientTerminalStockAsync(
                    sale.ProductId,
                    sale.QuantityMt,
                    sale.SaleDate,
                    model.SourceTerminalId!.Value,
                    model.SourceStorageTankId!.Value,
                    model.CompanyId,
                    model.SourcePurchaseContractId);

            }

            IDbContextTransaction? transaction = null;
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync();
            }

            try
            {
                var stockOutMovements = new List<InventoryMovement>();
                if (requiresTerminalStock)
                {
                    if (!model.SourceStorageTankId.HasValue)
                    {
                        throw new BusinessRuleException(
                            "SALE_TERMINAL_STOCK_TANK_REQUIRED",
                            "برای فروش از مخزن، انتخاب مخزن مبدا الزامی است.");
                    }

                    var lockedTank = await LockStorageTankAsync(model.SourceStorageTankId.Value);
                    if (lockedTank is null)
                    {
                        throw new BusinessRuleException(
                            "SALE_TERMINAL_STOCK_TANK_NOT_FOUND",
                            "مخزن مبدا انتخاب‌شده دیگر معتبر نیست.");
                    }

                    stockAllocations = await EnsureSufficientTerminalStockAsync(
                        sale.ProductId,
                        sale.QuantityMt,
                        sale.SaleDate,
                        model.SourceTerminalId!.Value,
                        model.SourceStorageTankId.Value,
                        model.CompanyId,
                        model.SourcePurchaseContractId);

                    // قرارداد منبع و محموله در فرم اختیاری‌اند (کاربرِ فروش نمی‌داند بار از کدام
                    // قرارداد/محموله است). هر دو از همان تخصیصِ موجودی که همین‌جا محاسبه شد و از
                    // منشأ فیزیکی مخزن استنباط می‌شوند، تا پروندهٔ محموله — که فروش‌ها را با
                    // ShipmentId می‌شمارد — این فروش‌ها را از دست ندهد. فقط وقتی نتیجه یکتاست
                    // مقدار داده می‌شود؛ حالت مبهم دست‌نخورده (null) می‌ماند.
                    if (sale.SourcePurchaseContractId is null)
                    {
                        var allocatedContractIds = stockAllocations
                            .Select(a => a.ContractId)
                            .Distinct()
                            .ToList();
                        if (allocatedContractIds.Count == 1)
                        {
                            sale.SourcePurchaseContractId = allocatedContractIds[0];
                        }
                    }

                    if (sale.ShipmentId is null)
                    {
                        sale.ShipmentId =
                            await ResolveShipmentIdForTankAsync(model.SourceStorageTankId.Value);

                        if (sale.ShipmentId is null && sale.SourcePurchaseContractId is int sourceContractId)
                        {
                            sale.ShipmentId = await ResolveShipmentIdForContractAsync(sourceContractId);
                        }
                    }
                }

                _db.SalesTransactions.Add(sale);
                await _db.SaveChangesAsync();

                // PTG-P2-03 — پیوندِ دوطرفه در همان تراکنشِ خودِ فروش. اگر این ذخیره برگردد،
                // نه فروشِ جایگزینی می‌ماند نه پیوندِ نیم‌بند.
                if (correctionOriginal is not null)
                {
                    correctionOriginal.ReplacementSaleId = sale.Id;
                    sale.CorrectedFromSaleId = correctionOriginal.Id;
                    await _db.SaveChangesAsync();
                }

                if (requiresTerminalStock)
                {
                    foreach (var allocation in stockAllocations)
                    {
                        stockOutMovements.Add(new InventoryMovement
                        {
                            ProductId = sale.ProductId,
                            ContractId = allocation.ContractId,
                            TerminalId = model.SourceTerminalId!.Value,
                            StorageTankId = model.SourceStorageTankId,
                            SalesTransactionId = sale.Id,
                            Direction = MovementDirection.Out,
                            MovementDate = sale.SaleDate,
                            QuantityMt = allocation.QuantityMt,
                            ReferenceDocument = sale.InvoiceNumber,
                            Notes = BuildSaleInventoryNotes(
                                sale.SaleStage,
                                sale.InvoiceNumber,
                                $"SaleId={sale.Id}" + (string.IsNullOrWhiteSpace(sale.Notes) ? string.Empty : $" | {sale.Notes}"))
                        });
                    }

                    await _movements.PostOutboundRangeAsync(stockOutMovements, StockGuard.Full);

                    var saleSourcePlan = await _sourceAllocations.BuildFromInventoryMovementsAsync(
                        stockOutMovements,
                        sale.QuantityMt);
                    _sourceAllocations.ApplyLegacyHeader(sale, saleSourcePlan);
                    await _sourceAllocations.PersistSaleAsync(sale, saleSourcePlan);

                    // لایهٔ Lineage: تخصیص فروش به Lotها با FIFO (پشت flag Lineage:WriteLots؛ خاموش=no-op).
                    // موجودی فیزیکی و Ledger دست‌نخورده می‌ماند؛ فقط SaleLotAllocation نوشته می‌شود.
                    await _lineage.AllocateSaleAsync(
                        sale,
                        model.SourcePurchaseContractId,
                        model.SourceTerminalId!.Value,
                        model.SourceStorageTankId);
                    if (salePricePending)
                    {
                        await ClearPendingSaleLotAmountsAsync(sale.Id);
                    }
                }

                var hasEmbeddedLoss = model.Loss.Enabled && model.Loss.QuantityMt.GetValueOrDefault() > 0m;
                if (hasEmbeddedLoss)
                {
                    var lossSubmission = BuildSaleLossSubmission(model, normalizedInvoice);
                    lossSubmission.SalesTransactionId = sale.Id;
                    await _lossWorkflow.CreateAsync(lossSubmission);
                }

                // AUD-06: انتساب فروش به قرارداد خرید فقط از منبع رسمی خوانده می‌شود — همان
                // planی که از InventoryMovementهای واقعی (FIFO) ساخته شد و در
                // SalesTransactionSourceAllocations نگه داشته می‌شود. ApplyLegacyHeader نتیجهٔ
                // همان plan را روی SourcePurchaseContractId نشانده است، پس Ledger و header
                // همیشه یک عدد را می‌گویند. انتخاب کاربر در فرم دیگر تصمیم‌گیرندهٔ Ledger نیست:
                // اگر FIFO قرارداد دیگری را مصرف کرده باشد FIFO برنده است، و اگر فروش
                // چند-قراردادی باشد هر دو سر عمداً null می‌مانند و سهم هر قرارداد فقط در
                // allocations زندگی می‌کند. هیچ قراردادی حدس زده نمی‌شود.
                // قیمت باز: نه طلب مشتری، نه عاید، نه بهای تمام‌شده. این‌ها فقط یک‌بار و هنگام
                // تعیین قیمت (SetPrice) از همین مسیرها ساخته می‌شوند.
                LedgerEntry? ledgerEntry = null;
                if (!salePricePending)
                {
                    ledgerEntry = Ledger.Post(SaleLedgerFactory.BuildSaleLedgerEntry(
                        sale,
                        conversion!,
                        contractId: sale.ContractId ?? sale.SourcePurchaseContractId));
                    await _db.SaveChangesAsync();

                    await PostSaleAccountingAsync(sale);
                }
                else
                {
                    await PostPendingSaleDeliveredCostAsync(sale);
                }

                await _audit.LogAsync(
                    nameof(SalesTransaction),
                    sale.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ContractId", sale.ContractId),
                        ("CompanyId", sale.CompanyId),
                        ("CustomerId", sale.CustomerId),
                        ("SupplierId", sale.SupplierId),
                        ("ProductId", sale.ProductId),
                        ("DestinationLocationId", sale.DestinationLocationId),
                        ("ShipmentId", sale.ShipmentId),
                        ("SaleStage", sale.SaleStage),
                        ("InvoiceNumber", sale.InvoiceNumber),
                        ("SaleDate", sale.SaleDate),
                        ("QuantityMt", sale.QuantityMt),
                        ("Currency", sale.Currency),
                        ("UnitPriceInCurrency", sale.UnitPriceInCurrency),
                        ("AppliedFxRateToUsd", sale.AppliedFxRateToUsd),
                        ("UnitPriceUsd", sale.UnitPriceUsd),
                        ("TotalInCurrency", sale.TotalInCurrency),
                        ("TotalUsd", sale.TotalUsd),
                        ("IsSalePricePending", sale.IsSalePricePending),
                        ("SourcePurchaseContractId", stockOutMovements.Count == 1 ? stockOutMovements[0].ContractId : null),
                        ("SourcePurchaseContractIds", string.Join(",", stockOutMovements.Select(m => m.ContractId))),
                        ("SourceTerminalId", stockOutMovements.FirstOrDefault()?.TerminalId),
                        ("SourceStorageTankId", stockOutMovements.FirstOrDefault()?.StorageTankId),
                        ("InventoryMovementId", stockOutMovements.Count == 1 ? stockOutMovements[0].Id : null),
                        ("InventoryMovementIds", string.Join(",", stockOutMovements.Select(m => m.Id))),
                        ("LedgerReference", ledgerEntry?.Reference)));

                foreach (var stockOutMovement in stockOutMovements)
                {
                    await _audit.LogAsync(
                        nameof(InventoryMovement),
                        stockOutMovement.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ProductId", stockOutMovement.ProductId),
                            ("ContractId", stockOutMovement.ContractId),
                            ("TerminalId", stockOutMovement.TerminalId),
                            ("StorageTankId", stockOutMovement.StorageTankId),
                            ("Direction", stockOutMovement.Direction),
                            ("QuantityMt", stockOutMovement.QuantityMt),
                            ("MovementDate", stockOutMovement.MovementDate),
                            ("ReferenceDocument", stockOutMovement.ReferenceDocument),
                            ("SaleId", sale.Id)));
                }

                await _db.SaveChangesAsync();

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                TempData["ok"] = salePricePending
                    ? "فروش با قیمت باز ثبت شد. سند مالی پس از تعیین قیمت فروش ثبت می‌شود."
                    : hasEmbeddedLoss
                        ? "فروش و ضایعات این مرحله با موفقیت ثبت شد."
                        : requiresTerminalStock
                            ? "فروش و خروج موجودی با موفقیت ثبت شد."
                            : "فروش با موفقیت ثبت شد.";

                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return Redirect(localReturnUrl);
                }

                return RedirectToAction(nameof(Details), new { id = sale.Id });
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }

                throw;
            }
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sale transaction.");
            ModelState.AddModelError(string.Empty, "ثبت فروش انجام نشد. لطفاً اطلاعات فروش را بررسی و دوباره تلاش کنید.");
        }

        model.InvoiceNumber = normalizedInvoice;
        model.Notes = normalizedNotes;
        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var sale = await _db.SalesTransactions
            .Include(s => s.Contract)
            .Include(s => s.Company)
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Include(s => s.Product)
            .Include(s => s.DestinationLocation)
            .Include(s => s.Shipment)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsCancelled)
        {
            TempData["err"] = "این فروش لغو شده است.";
        }

        var ledgerEntry = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        // پرداختی که ردیف تطبیقِ فعال روی همین فروش دارد از این‌جا کنار گذاشته می‌شود تا مبلغش
        // یک‌بار از مسیر تطبیق شمرده شود، نه دوبار.
        var directPayments = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.SalesTransactionId == sale.Id
                && !_db.CustomerPaymentAllocationApplications.Any(a =>
                    a.PaymentTransactionId == p.Id
                    && a.SalesTransactionId == sale.Id
                    && a.Status == CustomerPaymentAllocationApplicationStatus.Active))
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new SalesPaymentDetailsViewModel
            {
                PaymentTransactionId = p.Id,
                PaymentDate = p.PaymentDate,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                IsIncoming = p.Direction == PaymentDirection.In,
                IsAdvanceApplication = false
            })
            .ToListAsync();

        // ردیف‌های تطبیقِ نقد: هم مصرفِ پیش‌دریافت (والد دارد) و هم تطبیقِ مستقیمِ یک دریافت
        // روی این فروش (والد ندارد) — هر دو یک معنی دارند: این مبلغ روی همین فروش نشسته است.
        var appliedAdvances = await _db.CustomerPaymentAllocationApplications
            .AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .OrderByDescending(a => a.AppliedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new SalesPaymentDetailsViewModel
            {
                ApplicationId = a.Id,
                PaymentTransactionId = a.PaymentTransactionId,
                PaymentDate = a.PaymentTransaction!.PaymentDate,
                Amount = a.AppliedPaymentAmount,
                Currency = a.PaymentCurrencyCode,
                AmountUsd = a.AppliedAmountUsd,
                Reference = a.PaymentTransaction.Reference,
                IsIncoming = true,
                IsAdvanceApplication = a.CustomerPaymentAllocationId != null
            })
            .ToListAsync();

        var payments = directPayments
            .Concat(appliedAdvances)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.PaymentTransactionId)
            .ToList();
        // وصولی و طلبِ باز فقط از مرجعِ واحدِ وصولیِ فروش؛ فهرستِ بالا فقط نمایشِ همان اسناد است.
        var settlement = (await ReceiptApplications.GetSaleSettlementsAsync([sale.Id]))[sale.Id];
        var receivedUsd = settlement.NetReceivedUsd;
        var openReceivableUsd = settlement.OpenReceivableUsd(sale.TotalUsd);

        // دریافت‌های آزادِ همین مشتری، تا مانده این فروش بدون ساختن ساختار موازی تطبیق شود.
        var applicableReceipts = sale.IsCancelled || sale.SupplierId.HasValue || openReceivableUsd <= 0m || sale.CustomerId <= 0
            ? []
            : await LoadApplicableReceiptsAsync(sale.CustomerId!.Value, sale.Id);

        var stockOutMovements = await _db.InventoryMovements
            .Include(m => m.Contract)
            .Include(m => m.Terminal)
            .Include(m => m.StorageTank)
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .OrderBy(m => m.Id)
            .ToListAsync();
        var stockOutMovement = stockOutMovements.FirstOrDefault();
        var stockOutContractNumbers = stockOutMovements
            .Select(m => m.Contract?.ContractNumber ?? (m.ContractId.HasValue ? $"#{m.ContractId}" : null))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToList();
        var stockOutContractIds = stockOutMovements
            .Where(m => m.ContractId.HasValue)
            .Select(m => m.ContractId!.Value)
            .Distinct()
            .ToList();

        var receiptAllocation = await _db.LoadingReceiptAllocations
            .Include(a => a.LoadingReceipt)
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.Terminal)
            .Include(a => a.StorageTank)
            .AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        var inventoryTransportReceipt = await _db.InventoryTransportReceipts
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourcePurchaseContract)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourceTerminal)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourceStorageTank)
            .AsNoTracking()
            .Where(r => r.SalesTransactionId == sale.Id && !r.IsCancelled)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        var createdByName = sale.CreatedByUserId.HasValue
            ? await _db.Users.AsNoTracking()
                .Where(u => u.Id == sale.CreatedByUserId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync()
            : null;
        var priceFixedByName = sale.SalePriceFixedByUserId.HasValue
            ? await _db.Users.AsNoTracking()
                .Where(u => u.Id == sale.SalePriceFixedByUserId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync()
            : null;

        // قیمت خرید روی فروش ذخیره نمی‌شود؛ وضعیتش از قراردادهای خرید منبعِ همین فروش خوانده می‌شود.
        var sourceContractIds = stockOutContractIds
            .Concat(await _db.SalesTransactionSourceAllocations.AsNoTracking()
                .Where(a => a.SalesTransactionId == sale.Id)
                .Select(a => a.SourcePurchaseContractId)
                .ToListAsync())
            .Concat(new[]
            {
                sale.SourcePurchaseContractId,
                receiptAllocation?.SourcePurchaseContractId,
                inventoryTransportReceipt?.InventoryTransportLeg?.SourcePurchaseContractId
            }.Where(id => id.HasValue).Select(id => id!.Value))
            .Distinct()
            .ToList();
        var pendingPurchaseContracts = await LoadPendingPurchasePriceContractsAsync(sourceContractIds);

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        return View(new SalesDetailsViewModel
        {
            Id = sale.Id,
            CustomerId = sale.CustomerId,
            IsSupplierBuyer = sale.SupplierId.HasValue,
            ContractId = sale.ContractId,
            ShipmentId = sale.ShipmentId,
            PreSaleOrderId = sale.PreSaleOrderId,
            InvoiceNumber = sale.InvoiceNumber,
            TicketSerialNumber = sale.TicketSerialNumber,
            StockSourceType = sale.StockSourceType,
            IsCancelled = sale.IsCancelled,
            CreatedAtUtc = sale.CreatedAtUtc,
            CreatedByName = createdByName,
            SaleDate = sale.SaleDate,
            SaleStage = sale.SaleStage,
            ContractNumber = sale.Contract?.DisplayLabel ?? SalesContractText.WithoutSalesContract,
            CompanyName = sale.CompanyId is null ? SalesContractText.MultiLicense : (sale.Company?.Name ?? ""),
            CustomerName = sale.Customer?.Name ?? sale.Supplier?.Name ?? "",
            ProductName = sale.Product?.Name ?? "",
            DestinationName = sale.DestinationLocation?.Name,
            ShipmentCode = sale.Shipment?.ShipmentCode,
            QuantityMt = sale.QuantityMt,
            Currency = sale.Currency,
            UnitPriceInCurrency = sale.UnitPriceInCurrency,
            TotalInCurrency = sale.TotalInCurrency,
            AppliedFxRateToUsd = sale.AppliedFxRateToUsd,
            UnitPriceUsd = sale.UnitPriceUsd,
            TotalUsd = sale.TotalUsd,
            ReceivedUsd = receivedUsd,
            // قیمت باز: طلبی هنوز وجود ندارد؛ مانده و اضافه‌پرداخت «نامشخص»اند، نه صفر.
            ReceivableBalanceUsd = sale.IsCancelled || sale.IsSalePricePending ? 0m : Math.Max(openReceivableUsd, 0m),
            ApplicableReceipts = applicableReceipts,
            OverpaymentUsd = sale.IsCancelled || sale.IsSalePricePending ? 0m : Math.Max(-openReceivableUsd, 0m),
            Payments = payments,
            Notes = sale.Notes,
            LedgerEntryId = ledgerEntry?.Id,
            LedgerReference = ledgerEntry?.Reference,
            LedgerDescription = ledgerEntry?.Description,
            LedgerAmountUsd = ledgerEntry?.AmountUsd,
            LedgerSideName = ledgerEntry is null ? null : UiText.LedgerSideName(HttpContext, ledgerEntry.Side),
            InventoryMovementId = stockOutMovement?.Id,
            InventoryMovementCount = stockOutMovements.Count,
            InventoryMovementIdsText = stockOutMovements.Count == 0
                ? null
                : string.Join(", ", stockOutMovements.Select(m => $"#{m.Id}")),
            LoadingReceiptId = receiptAllocation?.LoadingReceiptId,
            LoadingReceiptAllocationId = receiptAllocation?.Id,
            InventoryTransportReceiptId = inventoryTransportReceipt?.Id,
            InventoryTransportLegId = inventoryTransportReceipt?.InventoryTransportLegId,
            InventoryTransportReference = inventoryTransportReceipt?.InventoryTransportLeg is null
                ? null
                : BuildInventoryTransportReference(inventoryTransportReceipt.InventoryTransportLeg),
            SourcePurchaseContractNumber = stockOutContractNumbers.Count > 0
                ? string.Join(", ", stockOutContractNumbers)
                : receiptAllocation?.SourcePurchaseContract?.DisplayLabel
                    ?? inventoryTransportReceipt?.InventoryTransportLeg?.SourcePurchaseContract?.DisplayLabel,
            // پیوند فقط وقتی معنا دارد که منبع یک قرارداد مشخص باشد؛ چند قراردادی بدون لینک می‌ماند.
            SourcePurchaseContractId = stockOutContractIds.Count > 0
                ? (stockOutContractIds.Count == 1 ? stockOutContractIds[0] : null)
                : receiptAllocation?.SourcePurchaseContractId
                    ?? inventoryTransportReceipt?.InventoryTransportLeg?.SourcePurchaseContractId,
            SourceTerminalName = stockOutMovement?.Terminal?.Name
                ?? receiptAllocation?.Terminal?.Name
                ?? inventoryTransportReceipt?.InventoryTransportLeg?.SourceTerminal?.Name,
            SourceStorageTankCode = StorageTankDisplay.BuildOptional(stockOutMovement?.StorageTank)
                ?? StorageTankDisplay.BuildOptional(receiptAllocation?.StorageTank)
                ?? StorageTankDisplay.BuildOptional(inventoryTransportReceipt?.InventoryTransportLeg?.SourceStorageTank),
            IsSalePricePending = sale.IsSalePricePending,
            IsPurchasePricePending = pendingPurchaseContracts.Count > 0,
            PendingPurchaseContracts = pendingPurchaseContracts,
            SalePriceFixedAtUtc = sale.SalePriceFixedAtUtc,
            SalePriceAccountingDate = sale.SalePriceAccountingDate,
            SalePriceFixedByName = priceFixedByName,
            Version = sale.Version
        });
    }

    public async Task<IActionResult> Invoice(int id, string template)
    {
        if (!SalesInvoiceTemplateOptions.TryGet(template, out var templateMetadata))
        {
            return BadRequest("Invalid invoice template.");
        }

        var sale = await _db.SalesTransactions
            .Include(s => s.Contract)
                .ThenInclude(c => c!.DestinationLocation)
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Include(s => s.Product)
            .Include(s => s.DestinationLocation)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsSalePricePending)
        {
            TempData["err"] = "قیمت فروش هنوز تعیین نشده است؛ فاکتور پس از تعیین قیمت قابل چاپ است.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var borderOrLocation = await ResolveInvoiceBorderOrLocationAsync(sale);
        var model = SalesInvoiceMapper.Build(sale, templateMetadata, borderOrLocation);
        return View(model);
    }

    private async Task<string?> ResolveInvoiceBorderOrLocationAsync(SalesTransaction sale)
    {
        if (!string.IsNullOrWhiteSpace(sale.DestinationLocation?.Name))
        {
            return sale.DestinationLocation.Name;
        }

        if (!string.IsNullOrWhiteSpace(sale.Contract?.DestinationLocation?.Name))
        {
            return sale.Contract.DestinationLocation.Name;
        }

        var allocation = await _db.LoadingReceiptAllocations
            .Include(a => a.DestinationLocation)
            .Include(a => a.DestinationTerminal)
            .Include(a => a.Terminal)
            .AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        if (allocation is not null)
        {
            return FirstNonEmpty(
                allocation.DestinationLocation?.Name,
                allocation.DestinationName,
                allocation.DestinationTerminal?.Name,
                allocation.Terminal?.Name);
        }

        var transportReceipt = await _db.InventoryTransportReceipts
            .Include(r => r.DestinationTerminal)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.DestinationLocation)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.DestinationTerminal)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourceTerminal)
            .AsNoTracking()
            .Where(r => r.SalesTransactionId == sale.Id && !r.IsCancelled)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        if (transportReceipt is not null)
        {
            return FirstNonEmpty(
                transportReceipt.InventoryTransportLeg?.DestinationLocation?.Name,
                transportReceipt.DestinationTerminal?.Name,
                transportReceipt.InventoryTransportLeg?.DestinationTerminal?.Name,
                transportReceipt.InventoryTransportLeg?.SourceTerminal?.Name);
        }

        var stockOut = await _db.InventoryMovements
            .Include(m => m.Terminal)
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync();

        return stockOut?.Terminal?.Name;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private async Task<ShipmentFlowSaleContext?> BuildShipmentFlowSaleContextAsync(int shipmentId)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Vessel)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shipmentId);
        if (shipment is null)
        {
            return null;
        }

        var legs = await _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Company)
            .Include(l => l.Product)
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId && l.Status != InventoryTransportLegStatus.Cancelled)
            .OrderBy(l => l.Id)
            .ToListAsync();

        // Shipment-flow sales normally represent the original vessel cargo. Later
        // tank-to-truck/wagon legs are the same inventory moving again and must
        // never increase sale capacity here. The current tank-backed shipment flow
        // records its root legs as Unspecified; a persisted outbound movement proves
        // that stock was actually loaded and is therefore eligible for this fallback.
        var originalVesselLegs = legs
            .Where(l => l.TransportType == LoadingTransportType.Vessel
                && l.Status != InventoryTransportLegStatus.Draft)
            .ToList();

        var tankLoadedRootLegs = originalVesselLegs.Count > 0
            ? []
            : legs
                .Where(l => l.TransportType == LoadingTransportType.Unspecified
                    && l.Status != InventoryTransportLegStatus.Draft
                    && l.OutboundInventoryMovementId.HasValue
                    && l.SourceStorageTankId.HasValue)
                .ToList();
        var saleCapacityLegs = originalVesselLegs.Count > 0
            ? originalVesselLegs
            : tankLoadedRootLegs;

        var rawLoadedByContract = saleCapacityLegs
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(g => g.Key, g => RoundQuantity(g.Sum(l => l.QuantityMt)));
        var loadedByContract = new Dictionary<int, decimal>(rawLoadedByContract);
        if (originalVesselLegs.Count == 0 && loadedByContract.Count > 0)
        {
            var allocationCaps = await _db.ShipmentContracts
                .AsNoTracking()
                .Where(sc => sc.ShipmentId == shipmentId && sc.QuantityMt.HasValue && sc.QuantityMt > 0m)
                .GroupBy(sc => sc.ContractId)
                .Select(group => new { ContractId = group.Key, QuantityMt = group.Sum(sc => sc.QuantityMt!.Value) })
                .ToDictionaryAsync(row => row.ContractId, row => row.QuantityMt);

            if (allocationCaps.Count == 0
                && shipment.ContractId.HasValue
                && shipment.QuantityMt > 0m)
            {
                allocationCaps[shipment.ContractId.Value] = shipment.QuantityMt;
            }

            foreach (var contractId in loadedByContract.Keys.ToList())
            {
                if (allocationCaps.TryGetValue(contractId, out var allocationCap))
                {
                    loadedByContract[contractId] = RoundQuantity(
                        Math.Min(loadedByContract[contractId], allocationCap));
                }
            }

            var cappedTotal = loadedByContract.Values.Sum();
            if (shipment.QuantityMt > 0m && cappedTotal > shipment.QuantityMt)
            {
                var scale = shipment.QuantityMt / cappedTotal;
                foreach (var contractId in loadedByContract.Keys.ToList())
                {
                    loadedByContract[contractId] = RoundQuantity(loadedByContract[contractId] * scale);
                }
            }
        }

        var saleCapacityLegIds = saleCapacityLegs.Select(l => l.Id).ToList();

        var receiptShortageByContract = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipmentId
                && saleCapacityLegIds.Contains(r.InventoryTransportLegId)
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Draft
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(r => r.InventoryTransportLeg!.SourcePurchaseContractId)
            .Select(g => new { ContractId = g.Key, QuantityMt = g.Sum(r => r.ShortageQuantityMt) })
            .ToDictionaryAsync(x => x.ContractId, x => x.QuantityMt);

        var directLosses = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId
                && !l.IsCancelled
                && l.TransportLegId == null
                && l.LoadingRegisterId == null
                && l.LoadingReceiptId == null
                && l.TruckDispatchId == null
                && l.SalesTransactionId == null
                && l.DifferenceQuantityMt > 0m)
            .Select(l => new ShipmentFlowSaleSlice(l.ContractId, l.DifferenceQuantityMt))
            .ToListAsync();

        var directLossCapacity = loadedByContract.ToDictionary(
            row => row.Key,
            row => Math.Max(row.Value - receiptShortageByContract.GetValueOrDefault(row.Key), 0m));
        var directLossByContract = AllocateShipmentFlowSlices(directLosses, directLossCapacity);
        var shortageByContract = loadedByContract.Keys.ToDictionary(
            contractId => contractId,
            contractId => RoundQuantity(
                receiptShortageByContract.GetValueOrDefault(contractId)
                + directLossByContract.GetValueOrDefault(contractId)));

        var previousSales = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.ShipmentId == shipmentId && !s.IsCancelled && s.SaleStage != SaleStage.PreSale)
            .Select(s => new ShipmentFlowSaleSlice(s.SourcePurchaseContractId, s.QuantityMt))
            .ToListAsync();
        var previousSalesQuantityMt = previousSales.Sum(s => s.QuantityMt);

        var capacityAfterShortage = loadedByContract.ToDictionary(
            row => row.Key,
            row => RoundQuantity(Math.Max(row.Value - shortageByContract.GetValueOrDefault(row.Key), 0m)));

        // فروشی که قرارداد منبع دارد دقیقاً به همان قرارداد نسبت داده می‌شود؛ فروش‌های قدیمیِ بدون
        // قرارداد منبع مثل قبل به‌نسبت ظرفیت بین قراردادهای محموله پخش می‌شوند.
        var previousSalesByContract = AllocateShipmentFlowSlices(previousSales, capacityAfterShortage);
        var pnlByLeg = await new InventoryTransportPnlService(_db)
            .BuildForLegsAsync(saleCapacityLegIds);

        var contractRows = saleCapacityLegs
            .GroupBy(l => l.SourcePurchaseContractId)
            .Select(group =>
            {
                var loaded = loadedByContract.GetValueOrDefault(group.Key);
                var shortage = shortageByContract.GetValueOrDefault(group.Key);
                var previousSale = previousSalesByContract.GetValueOrDefault(group.Key);
                var rawLoaded = rawLoadedByContract.GetValueOrDefault(group.Key);
                var capacityScale = rawLoaded > 0m ? loaded / rawLoaded : 0m;
                var costParts = group
                    .Select(l => new
                    {
                        l.QuantityMt,
                        UnitCost = l.PurchaseUnitCostUsd
                            ?? (l.SourcePurchaseContract is null
                                ? null
                                : ContractPricingAdapter.GetCanonicalFinalPrice(l.SourcePurchaseContract))
                    })
                    .Where(x => x.UnitCost.HasValue && x.UnitCost.Value > 0m)
                    .ToList();
                var costQuantity = costParts.Sum(x => x.QuantityMt);
                var unitCost = costQuantity > 0m
                    ? costParts.Sum(x => x.QuantityMt * x.UnitCost!.Value) / costQuantity
                    : (decimal?)null;

                return new ShipmentFlowSaleContractRowViewModel
                {
                    ContractId = group.Key,
                    ContractNumber = group.First().SourcePurchaseContract?.DisplayLabel ?? $"#{group.Key}",
                    CompanyName = group.First().SourcePurchaseContract?.Company?.Name,
                    ProductName = group.First().Product?.Name,
                    LoadedQuantityMt = loaded,
                    ShortageQuantityMt = shortage,
                    PreviousSalesQuantityMt = previousSale,
                    AvailableQuantityMt = RoundQuantity(Math.Max(loaded - shortage - previousSale, 0m)),
                    PurchaseUnitCostUsd = unitCost.HasValue ? decimal.Round(unitCost.Value, 4, MidpointRounding.AwayFromZero) : null,
                    TotalCostUsd = decimal.Round(
                        group.Sum(l => pnlByLeg.TryGetValue(l.Id, out var pnl) ? pnl.TotalCostUsd : 0m) * capacityScale,
                        4,
                        MidpointRounding.AwayFromZero)
                };
            })
            .OrderBy(r => r.ContractNumber)
            .ToList();

        var capacityContractIds = loadedByContract
            .Where(row => row.Value > 0m)
            .Select(row => row.Key)
            .ToHashSet();
        var effectiveCapacityLegs = saleCapacityLegs
            .Where(l => capacityContractIds.Contains(l.SourcePurchaseContractId))
            .ToList();
        var companyIds = effectiveCapacityLegs
            .Where(l => l.SourcePurchaseContract is not null)
            .Select(l => l.SourcePurchaseContract!.CompanyId)
            .Distinct()
            .ToList();
        var productIds = effectiveCapacityLegs.Select(l => l.ProductId).Distinct().ToList();
        var productNames = effectiveCapacityLegs
            .Select(l => l.Product?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var model = new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = shipment.Id,
            ShipmentCode = shipment.ShipmentCode,
            VesselName = shipment.Vessel?.Name,
            ProductName = productNames.Count == 1 ? productNames[0]! : "چند محصول",
            CurrentStageName = "در جریان حمل",
            LoadedQuantityMt = RoundQuantity(loadedByContract.Values.Sum()),
            RegisteredShortageQuantityMt = RoundQuantity(shortageByContract.Values.Sum()),
            PreviousSalesQuantityMt = RoundQuantity(previousSalesQuantityMt),
            AvailableQuantityMt = RoundQuantity(Math.Max(
                loadedByContract.Values.Sum() - shortageByContract.Values.Sum() - previousSalesQuantityMt,
                0m)),
            DestinationLocationId = shipment.DestinationLocationId,
            Contracts = contractRows
        };

        // مشخصات هر قرارداد محموله: جواز/شرکت و محصولِ خودش می‌ماند و از روی همان leg خوانده می‌شود.
        var availableByContract = contractRows.ToDictionary(r => r.ContractId, r => r.AvailableQuantityMt);
        var contractFacts = effectiveCapacityLegs
            .Where(l => l.SourcePurchaseContract is not null)
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(
                group => group.Key,
                group => new ShipmentFlowSaleContractFacts(
                    group.Key,
                    group.First().SourcePurchaseContract!.CompanyId,
                    group.First().ProductId,
                    availableByContract.GetValueOrDefault(group.Key)));

        var context = new ShipmentFlowSaleContext(
            model,
            companyIds.Count == 1 ? companyIds[0] : 0,
            productIds.Count == 1 ? productIds[0] : 0,
            shipment.DestinationLocationId,
            contractFacts);

        model.SourceContractRequired = context.RequiresSourceContract;
        return context;
    }

    private async Task PopulateShipmentFlowSaleLookupsAsync(ShipmentFlowSaleCreateViewModel model)
    {
        await PopulateBuyerLookupAsync(model.BuyerKey);
        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync(),
            "Code", "Code", model.Currency);
        ViewBag.Destinations = new SelectList(
            await _db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name).ToListAsync(),
            "Id", "Name", model.DestinationLocationId);
    }

    private static void ApplyShipmentFlowSaleDisplay(
        ShipmentFlowSaleCreateViewModel target,
        ShipmentFlowSaleCreateViewModel source)
    {
        target.ShipmentCode = source.ShipmentCode;
        target.VesselName = source.VesselName;
        target.ProductName = source.ProductName;
        target.CurrentStageName = source.CurrentStageName;
        target.LoadedQuantityMt = source.LoadedQuantityMt;
        target.RegisteredShortageQuantityMt = source.RegisteredShortageQuantityMt;
        target.PreviousSalesQuantityMt = source.PreviousSalesQuantityMt;
        target.AvailableQuantityMt = source.AvailableQuantityMt;
        target.SourceContractRequired = source.SourceContractRequired;
        target.Contracts = source.Contracts;
    }

    // سقف مقدار فروش: اگر قرارداد منبع مشخص باشد، مقدار قابل فروشِ همان قرارداد؛ وگرنه کل محموله.
    private static decimal ResolveShipmentFlowAvailableMt(
        ShipmentFlowSaleContext context,
        int? sourcePurchaseContractId)
        => sourcePurchaseContractId.HasValue
            && context.ContractFacts.TryGetValue(sourcePurchaseContractId.Value, out var facts)
                ? facts.AvailableQuantityMt
                : context.Model.AvailableQuantityMt;

    // مقدارهایی که قرارداد منبعشان مشخص است دقیقاً به همان قرارداد (تا سقف ظرفیتش) نسبت داده می‌شوند؛
    // باقی‌مانده به‌نسبت ظرفیت بین قراردادهای محموله پخش می‌شود. برای کسری و فروش مشترک است.
    private static Dictionary<int, decimal> AllocateShipmentFlowSlices(
        IReadOnlyList<ShipmentFlowSaleSlice> slices,
        IReadOnlyDictionary<int, decimal> capacityByContract)
    {
        var result = capacityByContract.Keys.ToDictionary(id => id, _ => 0m);
        var unassigned = 0m;
        foreach (var slice in slices)
        {
            if (slice.ContractId.HasValue && result.ContainsKey(slice.ContractId.Value))
            {
                var available = Math.Max(capacityByContract[slice.ContractId.Value] - result[slice.ContractId.Value], 0m);
                var applied = Math.Min(slice.QuantityMt, available);
                result[slice.ContractId.Value] += applied;
                unassigned += Math.Max(slice.QuantityMt - applied, 0m);
            }
            else
            {
                unassigned += slice.QuantityMt;
            }
        }

        if (unassigned > 0m)
        {
            var remaining = capacityByContract.ToDictionary(
                row => row.Key,
                row => Math.Max(row.Value - result[row.Key], 0m));
            var allocated = AllocateQuantityByWeight(unassigned, remaining);
            foreach (var row in allocated)
            {
                result[row.Key] += row.Value;
            }
        }

        return result;
    }

    private static Dictionary<int, decimal> AllocateQuantityByWeight(
        decimal total,
        IReadOnlyDictionary<int, decimal> weights)
    {
        var result = weights.Keys.ToDictionary(id => id, _ => 0m);
        var positive = weights.Where(row => row.Value > 0m).OrderBy(row => row.Key).ToList();
        var totalWeight = positive.Sum(row => row.Value);
        var quantity = Math.Min(Math.Max(total, 0m), totalWeight);
        if (quantity <= 0m || totalWeight <= 0m)
        {
            return result;
        }

        var allocated = 0m;
        for (var i = 0; i < positive.Count; i++)
        {
            var share = i == positive.Count - 1
                ? quantity - allocated
                : RoundQuantity(quantity * positive[i].Value / totalWeight);
            share = Math.Min(share, positive[i].Value);
            result[positive[i].Key] = share;
            allocated += share;
        }

        return result;
    }

    private string? NormalizeLocalReturnUrl(string? returnUrl)
        => TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static bool RequiresTerminalStock(SaleStage stage) => stage == SaleStage.TerminalStock;

    private static string BuildInventoryTransportReference(InventoryTransportLeg leg)
        => leg.WagonNumber
            ?? leg.BillOfLadingNumber
            ?? leg.RwbNo
            ?? $"#{leg.Id}";

    private static string BuildSaleInventoryNotes(SaleStage saleStage, string invoiceNumber, string? notes)
    {
        var baseNote = $"Sale trace | Stage={saleStage} | Invoice={invoiceNumber}";
        if (string.IsNullOrWhiteSpace(notes))
        {
            return baseNote;
        }

        var combined = $"{baseNote} | {notes.Trim()}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }

    private static string BuildLossFieldKey(string fieldName)
        => $"Loss.{fieldName}";

    private static IReadOnlyCollection<LossEventStage> GetAllowedSaleLossStages()
        => [LossEventStage.TransitLoss, LossEventStage.CustomsLoss, LossEventStage.SalesDifference];

    private static LossEventStage ResolveDefaultLossStage(SaleStage saleStage) => saleStage switch
    {
        SaleStage.InTransit => LossEventStage.TransitLoss,
        SaleStage.Border => LossEventStage.CustomsLoss,
        SaleStage.AfterCustoms => LossEventStage.CustomsLoss,
        _ => LossEventStage.SalesDifference
    };

    private static LossEventSubmission BuildSaleLossSubmission(
        SalesCreateViewModel model,
        string normalizedInvoice)
        => StageLossCaptureMapper.ToSubmission(
            model.Loss,
            new StageLossCaptureContext
            {
                Stage = ResolveDefaultLossStage(model.SaleStage),
                AllowCustomStage = true,
                ActualQuantityMt = model.QuantityMt,
                EventDate = model.SaleDate,
                ProductId = model.ProductId,
                ContractId = model.ContractId,
                ShipmentId = model.ShipmentId,
                TerminalId = model.SourceTerminalId,
                StorageTankId = model.SourceStorageTankId,
                DefaultReference = normalizedInvoice
            });

    private static List<SelectListItem> GetSaleLossStageItems(LossEventStage selectedStage)
        => GetAllowedSaleLossStages()
            .Select(stage => new SelectListItem
            {
                Value = ((int)stage).ToString(),
                Text = LossEventStageLabels.ToPersian(stage),
                Selected = stage == selectedStage
            })
            .ToList();

    private static List<SelectListItem> GetSaleStageItems(SaleStage selectedStage)
    {
        // فرم فقط دو انتخاب دارد: «فروش از مخزن» و «فروش در مسیر».
        // مرزی/بعد از گمرک از فهرست برداشته شدند اما مقدارشان در enum و در رکوردهای قدیمی دست‌نخورده است؛
        // اگر سندی با همان مرحله باز شود، همان گزینه برای حفظ داده به فهرست برمی‌گردد.
        var stages = new List<SaleStage>
        {
            SaleStage.TerminalStock,
            SaleStage.InTransit
        };
        if (!stages.Contains(selectedStage))
        {
            stages.Insert(0, selectedStage);
        }

        return stages.Select(stage => new SelectListItem
        {
            Value = ((int)stage).ToString(),
            Text = SaleStageLabels.ToPersian(stage),
            Selected = stage == selectedStage
        })
        .ToList();
    }

    private static void NormalizeCreateModel(SalesCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.InvoiceNumber = (model.InvoiceNumber ?? string.Empty).Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private sealed record ShipmentFlowSaleContext(
        ShipmentFlowSaleCreateViewModel Model,
        int CompanyId,
        int ProductId,
        int? DestinationLocationId,
        IReadOnlyDictionary<int, ShipmentFlowSaleContractFacts> ContractFacts)
    {
        // جواز (Company) فقط برچسبِ قرارداد است و نباید مانع فروش کلی شود؛ اختلاف جواز قراردادها
        // آزاد است. تنها چیزی که فروشِ سهمیِ کل محموله را ناممکن می‌کند، اختلاف محصول است
        // (یک قیمت واحد برای دو محصول معنا ندارد)؛ در آن حالت باید قرارداد منبع انتخاب شود.
        public bool RequiresSourceContract => ContractFacts.Count > 1 && ProductId <= 0;
    }

    private sealed record ShipmentFlowSaleContractFacts(
        int ContractId,
        int CompanyId,
        int ProductId,
        decimal AvailableQuantityMt);

    private sealed record ShipmentFlowSaleSlice(int? ContractId, decimal QuantityMt);
}
