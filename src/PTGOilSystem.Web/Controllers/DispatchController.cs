using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Dispatch;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class DispatchController : Controller
{
    private const string DispatchFreightExpenseCode = DispatchFreightExpenseSync.DispatchFreightExpenseCode;
    private readonly ApplicationDbContext _db;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);
    private IExpenseLedgerPoster? _expenseLedgerPoster;
    private IExpenseLedgerPoster ExpenseLedger => _expenseLedgerPoster ??= new ExpenseLedgerPoster(Ledger);
    // PTG-P1-04 — قاعدهٔ «هر مصرف دقیقاً یک هویت تسویه دارد». بی‌حالت است.
    private readonly IExpenseSettlementValidator _settlementValidator = new ExpenseSettlementValidator();
    private readonly IStockService _stock;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ILossEventWorkflowService _lossWorkflow;
    private readonly ILogger<DispatchController> _logger;
    private const int DefaultListLimit = 100;
    private const int LookupLimit = 200;
    private sealed record TransportResolution(int TruckId, int? DriverId, Truck? CreatedTruck, Driver? CreatedDriver);

    private readonly IAfghanistanBusinessClock _businessClock;

    [ActivatorUtilitiesConstructor]
    private readonly IFormTokenGuard _formTokens;

    public DispatchController(
        ApplicationDbContext db,
        IStockService stock,
        IAuditService audit,
        ILogger<DispatchController> logger,
        ILossEventWorkflowService? lossWorkflow = null,
        ICurrencyConversionService? currencyConversion = null,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        Services.Accounting.ISalesAccountingAdapter? salesAccounting = null,
        IAfghanistanBusinessClock? businessClock = null,
        IInventoryMovementWriter? movements = null,
        ITransportChainService? transportChain = null,
        ITransportSourceAllocationService? sourceAllocations = null,
        IFormTokenGuard? formTokens = null)
    {
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        // PTG-P0-01 — نگهبان ثبت دوباره (Refresh/تب دوم/تلاش پس از Timeout).
        _formTokens = formTokens ?? new FormTokenGuard(db);
        _salesAccounting = salesAccounting;
        _db = db;
        _stock = stock;
        _currencyConversion = currencyConversion ?? new CurrencyConversionService(new PricingService(db));
        _audit = audit;
        _lossWorkflow = lossWorkflow ?? new LossEventWorkflowService(db, stock, audit);
        _logger = logger;
        _expenseAccounting = expenseAccounting;
        _movements = movements ?? new InventoryMovementWriter(db, stock);
        _sourceAllocations = sourceAllocations ?? new TransportSourceAllocationService(db);
        _transportChain = transportChain ?? new TransportChainService(
            db,
            new InventoryTransportReceiptService(
                db,
                _currencyConversion,
                movements: _movements,
                quantities: new TransportQuantityService(db),
                sourceAllocations: _sourceAllocations),
            new TransportQuantityService(db));
    }

    // تنها مسیر ثبت حرکت موجودی این کنترلر. تراکنش را همین اکشن‌ها مالک‌اند.
    private readonly IInventoryMovementWriter _movements;
    private readonly ITransportChainService _transportChain;
    private readonly ITransportSourceAllocationService _sourceAllocations;

    // مراحل ۵ و ۷ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;
    private readonly Services.Accounting.ISalesAccountingAdapter? _salesAccounting;

    private bool HasFieldError(string key)
        => ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0;

    private static string? NormalizeTruckPlateNumber(string? value)
    {
        var normalized = NormalizeNullable(value)?.ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeDriverNameInput(string? value)
        => NormalizeNullable(value);

    private async Task<TransportResolution?> ResolveTransportAsync(
        int selectedTruckId,
        int? selectedDriverId,
        string? typedTruckPlateNumber,
        string? typedDriverName,
        string selectedTruckField,
        string typedTruckField,
        string selectedDriverField,
        string typedDriverField)
    {
        var normalizedPlate = NormalizeTruckPlateNumber(typedTruckPlateNumber);
        var normalizedDriverName = NormalizeDriverNameInput(typedDriverName);

        var resolvedTruckId = selectedTruckId;
        Truck? createdTruck = null;
        if (!string.IsNullOrWhiteSpace(normalizedPlate))
        {
            var existingTruck = await _db.Trucks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.PlateNumber == normalizedPlate);
            if (existingTruck is null)
            {
                createdTruck = new Truck
                {
                    PlateNumber = normalizedPlate,
                    IsActive = true
                };
                resolvedTruckId = 0;
            }
            else if (!existingTruck.IsActive)
            {
                ModelState.AddModelError(typedTruckField, "این نمبر پلیت قبلا غیرفعال ثبت شده است. اول آن را از داده‌های پایه فعال کنید.");
            }
            else
            {
                resolvedTruckId = existingTruck.Id;
            }
        }
        else if (selectedTruckId <= 0)
        {
            ModelState.AddModelError(selectedTruckField, "نمبر پلیت موتر را انتخاب یا تایپ کنید.");
        }
        else
        {
            var truckExists = await _db.Trucks
                .AsNoTracking()
                .AnyAsync(t => t.Id == selectedTruckId && t.IsActive);
            if (!truckExists)
            {
                ModelState.AddModelError(selectedTruckField, "موتر انتخاب‌شده معتبر نیست.");
            }
        }

        int? resolvedDriverId = selectedDriverId;
        Driver? createdDriver = null;
        if (!string.IsNullOrWhiteSpace(normalizedDriverName))
        {
            var existingDriver = await _db.Drivers
                .AsNoTracking()
                .Where(d => d.FullName == normalizedDriverName && d.IsActive)
                .OrderBy(d => d.Id)
                .FirstOrDefaultAsync();
            if (existingDriver is null)
            {
                createdDriver = new Driver
                {
                    FullName = normalizedDriverName,
                    IsActive = true
                };
                resolvedDriverId = null;
            }
            else
            {
                resolvedDriverId = existingDriver.Id;
            }
        }
        else if (selectedDriverId.HasValue)
        {
            var driverExists = await _db.Drivers
                .AsNoTracking()
                .AnyAsync(d => d.Id == selectedDriverId.Value && d.IsActive);
            if (!driverExists)
            {
                ModelState.AddModelError(selectedDriverField, "راننده انتخاب‌شده معتبر نیست.");
            }
        }

        if (HasFieldError(selectedTruckField) || HasFieldError(typedTruckField) || HasFieldError(selectedDriverField) || HasFieldError(typedDriverField))
        {
            return null;
        }

        return new TransportResolution(resolvedTruckId, resolvedDriverId, createdTruck, createdDriver);
    }

    private async Task LogCreatedTransportAsync(TransportResolution? transport)
    {
        if (transport?.CreatedTruck is not null)
        {
            await _audit.LogAsync(
                nameof(Truck),
                transport.CreatedTruck.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("PlateNumber", transport.CreatedTruck.PlateNumber),
                    ("IsActive", transport.CreatedTruck.IsActive)));
        }

        if (transport?.CreatedDriver is not null)
        {
            await _audit.LogAsync(
                nameof(Driver),
                transport.CreatedDriver.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("FullName", transport.CreatedDriver.FullName),
                    ("IsActive", transport.CreatedDriver.IsActive)));
        }
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

    private async Task PopulateLookupsAsync(DispatchCreateViewModel? createModel = null, DispatchIndexFilterViewModel? filter = null)
    {
        var selectedContractId = createModel?.ContractId ?? filter?.ContractId.Only();

        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => selectedContractId.HasValue && c.Id == selectedContractId.Value ? 0 : 1)
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
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            contracts
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
            selectedContractId);

        ViewBag.Products = new SelectList(
            await _db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).Select(p => new { p.Id, p.Name }).ToListAsync(),
            "Id",
            "Name",
            createModel?.ProductId ?? filter?.ProductId.Only());

        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).Select(t => new { t.Id, t.PlateNumber }).ToListAsync(),
            "Id",
            "PlateNumber",
            createModel?.TruckId ?? filter?.TruckId.Only());

        ViewBag.Drivers = new SelectList(
            await _db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).Select(d => new { d.Id, d.FullName }).ToListAsync(),
            "Id",
            "FullName",
            createModel?.DriverId);

        ViewBag.SourceTerminals = new SelectList(
            await _db.Terminals.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Code).Select(t => new { t.Id, t.Name }).ToListAsync(),
            "Id",
            "Name",
            createModel?.SourceTerminalId);

        ViewBag.SourceStorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks.AsNoTracking().OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display",
            createModel?.SourceStorageTankId);

        ViewBag.Destinations = new SelectList(
            await _db.Locations.AsNoTracking().OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync(),
            "Id",
            "Name",
            createModel?.DestinationLocationId);

        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders
                .AsNoTracking()
                .Where(p => p.IsActive || (createModel != null && createModel.ServiceProviderId.HasValue && p.Id == createModel.ServiceProviderId.Value))
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            createModel?.ServiceProviderId);

        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets
                .AsNoTracking()
                .Where(a => a.IsActive || (createModel != null && createModel.OperationalAssetId.HasValue && a.Id == createModel.OperationalAssetId.Value))
                .OrderBy(a => a.AssetCode)
                .ThenBy(a => a.Name)
                .Select(a => new
                {
                    a.Id,
                    Text = a.AssetCode + " - " + a.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            createModel?.OperationalAssetId);
    }

    private async Task PopulateDirectFromReceiptLookupsAsync(DispatchDirectFromReceiptCreateViewModel model)
    {
        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).Select(t => new { t.Id, t.PlateNumber }).ToListAsync(),
            "Id",
            "PlateNumber",
            model.TruckId);

        ViewBag.Drivers = new SelectList(
            await _db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).Select(d => new { d.Id, d.FullName }).ToListAsync(),
            "Id",
            "FullName",
            model.DriverId);

        ViewBag.Destinations = new SelectList(
            await _db.Locations.AsNoTracking().OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync(),
            "Id",
            "Name",
            model.DestinationLocationId);

        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders
                .AsNoTracking()
                .Where(p => p.IsActive || (model.ServiceProviderId.HasValue && p.Id == model.ServiceProviderId.Value))
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            model.ServiceProviderId);

        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets
                .AsNoTracking()
                .Where(a => a.IsActive || (model.OperationalAssetId.HasValue && a.Id == model.OperationalAssetId.Value))
                .OrderBy(a => a.AssetCode)
                .ThenBy(a => a.Name)
                .Select(a => new
                {
                    a.Id,
                    Text = a.AssetCode + " - " + a.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            model.OperationalAssetId);
    }

    private async Task PopulateDirectFromReceiptSaleLookupsAsync(DispatchDirectFromReceiptSaleCreateViewModel model)
    {
        var saleCustomers = await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name }).ToListAsync();
        var saleSuppliers = await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name }).ToListAsync();
        ViewBag.Buyers = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.BuildOptions(
            saleCustomers.Select(c => (c.Id, c.Name)), saleSuppliers.Select(s => (s.Id, s.Name)), model.BuyerKey);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            model.Currency);
    }

    // lookupهای پنل فروش در فرم یکپارچه تخلیه/فروش موتر (Customers + Currencies).
    private async Task PopulateDispatchSaleFieldLookupsAsync()
    {
        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name");

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            SystemCurrency.BaseCurrencyCode);
    }

    private async Task PopulateUnloadLookupsAsync(DispatchUnloadViewModel model)
    {
        ViewBag.DestinationTerminals = new SelectList(
            await _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(),
            "Id",
            "Name",
            model.DestinationTerminalId);

        ViewBag.DestinationStorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                .AsNoTracking()
                .OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display",
            model.DestinationStorageTankId);

        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders
                .AsNoTracking()
                .Where(p => p.IsActive || (model.ServiceProviderId.HasValue && p.Id == model.ServiceProviderId.Value))
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            model.ServiceProviderId);

        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets
                .AsNoTracking()
                .Where(a => a.IsActive || (model.OperationalAssetId.HasValue && a.Id == model.OperationalAssetId.Value))
                .OrderBy(a => a.AssetCode)
                .ThenBy(a => a.Name)
                .Select(a => new
                {
                    a.Id,
                    Text = a.AssetCode + " - " + a.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            model.OperationalAssetId);
    }

    private IQueryable<TruckDispatch> BuildUnloadDispatchQuery(bool asTracking)
    {
        var query = _db.TruckDispatches
            .Include(d => d.Contract)
            .Include(d => d.Product)
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.DestinationLocation)
            .Include(d => d.ServiceProvider)
            .Include(d => d.OperationalAsset)
            .Include(d => d.SalesTransaction)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.Terminal)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.StorageTank)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.DestinationTerminal)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.DestinationStorageTank)
            .AsQueryable();

        return asTracking ? query : query.AsNoTracking();
    }

    private async Task<InventoryMovement?> FindDispatchStockOutMovementAsync(int dispatchId, bool asTracking = false)
    {
        var query = _db.InventoryMovements
            .Include(m => m.Terminal)
            .Include(m => m.StorageTank)
            .Where(m => m.ReferenceDocument == $"TRUCK-DISPATCH:{dispatchId}" && m.Direction == MovementDirection.Out)
            .OrderByDescending(m => m.Id)
            .AsQueryable();

        return await (asTracking ? query : query.AsNoTracking()).FirstOrDefaultAsync();
    }

    private async Task<InventoryMovement?> FindTruckUnloadInventoryMovementAsync(int dispatchId, bool asTracking = false)
    {
        var query = _db.InventoryMovements
            .Include(m => m.Terminal)
            .Include(m => m.StorageTank)
            .Where(m => m.ReferenceDocument == $"TRUCK-UNLOAD:{dispatchId}" && m.Direction == MovementDirection.In)
            .OrderByDescending(m => m.Id)
            .AsQueryable();

        return await (asTracking ? query : query.AsNoTracking()).FirstOrDefaultAsync();
    }

    // اثرهای پایین‌دستِ یک دیسپچ که با لغو یا ویرایشِ خاموشِ سند بالادست ناسازگارند.
    // تا وقتی یکی از این‌ها فعال است، برگرداندن خروجی موجودی یا تغییر مقدار/کالا/قرارداد
    // موجودی مصنوعی می‌سازد (خروجی برمی‌گردد ولی ورودی مقصد سر جایش می‌ماند).
    private async Task<List<string>> FindDispatchDownstreamBlockersAsync(TruckDispatch dispatch)
    {
        var blockers = new List<string>();

        if (dispatch.SalesTransactionId.HasValue)
        {
            blockers.Add("فروش ثبت‌شده");
        }

        var unloadReference = $"TRUCK-UNLOAD:{dispatch.Id}";
        var arrivalReferencePrefix = $"TRUCK-ARRIVAL:{dispatch.Id}:";
        var hasInboundMovement = await _db.InventoryMovements
            .AsNoTracking()
            .AnyAsync(m => m.ReferenceDocument == unloadReference
                || (m.ReferenceDocument != null && m.ReferenceDocument.StartsWith(arrivalReferencePrefix)));
        if (hasInboundMovement)
        {
            blockers.Add("تخلیه در مخزن");
        }

        var hasDeliveryReceipt = await _db.DeliveryReceipts
            .AsNoTracking()
            .AnyAsync(r => r.TruckDispatchId == dispatch.Id);
        if (hasDeliveryReceipt)
        {
            blockers.Add("رسید تحویل");
        }

        return blockers;
    }

    private async Task ApplyUnloadContextAsync(DispatchUnloadViewModel model, TruckDispatch dispatch)
    {
        var sourceMovement = await FindDispatchStockOutMovementAsync(dispatch.Id);
        var unloadMovement = await FindTruckUnloadInventoryMovementAsync(dispatch.Id);
        var deliveryReceipt = await _db.DeliveryReceipts
            .AsNoTracking()
            .Where(r => r.TruckDispatchId == dispatch.Id)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        model.TruckDispatchId = dispatch.Id;
        model.ContractNumber = dispatch.Contract?.DisplayLabel ?? "";
        model.ProductName = dispatch.Product?.Name ?? "";
        model.TruckPlateNumber = dispatch.Truck?.PlateNumber ?? "";
        model.DriverName = dispatch.Driver?.FullName;
        model.DestinationName = dispatch.DestinationLocation?.Name
            ?? dispatch.LoadingReceiptAllocation?.DestinationName
            ?? dispatch.LoadingReceiptAllocation?.DestinationReference;
        model.SourceTerminalName = dispatch.DispatchMode == TruckDispatchMode.DirectFromReceipt
            ? dispatch.LoadingReceiptAllocation?.Terminal?.Name
            : sourceMovement?.Terminal?.Name;
        model.SourceStorageTankCode = dispatch.DispatchMode == TruckDispatchMode.DirectFromReceipt
            ? StorageTankDisplay.BuildOptional(dispatch.LoadingReceiptAllocation?.StorageTank)
            : StorageTankDisplay.BuildOptional(sourceMovement?.StorageTank);
        model.ExistingDeliveryReference = deliveryReceipt?.DocumentReference;
        model.DispatchDate = dispatch.DispatchDate;
        model.LoadedQuantityMt = dispatch.LoadedQuantityMt;
        model.DispatchMode = dispatch.DispatchMode;

        if (model.DestinationTerminalId <= 0)
        {
            model.DestinationTerminalId = unloadMovement?.TerminalId
                ?? dispatch.LoadingReceiptAllocation?.DestinationTerminalId
                ?? 0;
        }

        model.DestinationStorageTankId ??= unloadMovement?.StorageTankId
            ?? dispatch.LoadingReceiptAllocation?.DestinationStorageTankId;
    }

    private IQueryable<LoadingReceiptAllocation> BuildDirectFromReceiptAllocationQuery(bool asTracking)
    {
        var query = _db.LoadingReceiptAllocations
            .Include(a => a.LoadingReceipt)
                .ThenInclude(r => r!.LoadingRegister)
                    .ThenInclude(l => l!.Product)
            .Include(a => a.LoadingReceipt)
                .ThenInclude(r => r!.LoadingRegister)
                    .ThenInclude(l => l!.Contract)
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.Terminal)
            .Include(a => a.DestinationLocation)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.SalesTransaction)
            .AsSplitQuery()
            .AsQueryable();

        return asTracking ? query : query.AsNoTracking();
    }

    private static decimal GetActiveDirectFromReceiptDispatchQuantity(LoadingReceiptAllocation allocation)
        => allocation.DirectTruckDispatches
            .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.Status != DispatchStatus.Cancelled)
            .Sum(d => d.LoadedQuantityMt);

    private static string? FormatDirectFromReceiptDestination(LoadingReceiptAllocation allocation)
    {
        var parts = new[]
        {
            allocation.DestinationLocation?.Name,
            allocation.DestinationName,
            allocation.DestinationReference
        }.Where(part => !string.IsNullOrWhiteSpace(part));

        return parts.Any() ? string.Join(" / ", parts) : null;
    }

    private static void ApplyDirectFromReceiptContext(
        DispatchDirectFromReceiptCreateViewModel model,
        LoadingReceiptAllocation allocation,
        decimal totalDirectDispatchedQuantityMt,
        string? returnUrl = null)
    {
        model.LoadingReceiptAllocationId = allocation.Id;
        model.LoadingReceiptId = allocation.LoadingReceiptId;
        model.LoadingRegisterId = allocation.LoadingReceipt?.LoadingRegisterId ?? 0;
        model.ContractNumber = allocation.SourcePurchaseContract?.DisplayLabel
            ?? allocation.LoadingReceipt?.LoadingRegister?.Contract?.DisplayLabel
            ?? "";
        model.ProductName = allocation.LoadingReceipt?.LoadingRegister?.Product?.Name ?? "";
        model.SourceTerminalName = allocation.Terminal?.Name ?? "";
        model.DestinationSummary = FormatDirectFromReceiptDestination(allocation);
        model.AllocationStatusName = allocation.Status.ToString();
        model.AllocationQuantityMt = allocation.QuantityMt;
        model.TotalDirectDispatchedQuantityMt = totalDirectDispatchedQuantityMt;
        model.RemainingQuantityMt = Math.Max(allocation.QuantityMt - totalDirectDispatchedQuantityMt, 0m);

        if (returnUrl is not null)
        {
            model.ReturnUrl = returnUrl;
        }
    }

    /// <summary>
    /// وزن قابل فروشِ یک موتر = وزن واقعیِ تخلیه‌شده (اگر ثبت شده)، وگرنه وزن بارگیری.
    /// همان قراردادی که فروش گروهی (<c>SalesController.Group</c>) از قبل استفاده می‌کند، تا
    /// اضافه‌بارِ تخلیه‌شده در فروش مستقیم از موتر هم گم نشود. این فقط مقدارِ فروش را تعیین
    /// می‌کند؛ هیچ قیمت خرید یا حرکت موجودی تازه‌ای نمی‌سازد.
    /// </summary>
    private static decimal ResolveSellableQuantityMt(TruckDispatch dispatch)
        => decimal.Round(
            dispatch.DischargedQuantityMt ?? dispatch.LoadedQuantityMt,
            4,
            MidpointRounding.AwayFromZero);

    private static void ApplySellableQuantityContext(
        DispatchDirectFromReceiptSaleCreateViewModel model,
        TruckDispatch dispatch,
        decimal alreadySoldQuantityMt = 0m)
    {
        model.DispatchLoadedQuantityMt = dispatch.LoadedQuantityMt;
        model.DispatchDischargedQuantityMt = dispatch.DischargedQuantityMt;
        model.DispatchSellableQuantityMt = ResolveSellableQuantityMt(dispatch);
        model.DispatchSurplusMt = TransportVarianceMath.Surplus(
            TransportVarianceMath.Difference(dispatch.LoadedQuantityMt, model.DispatchSellableQuantityMt));
        model.DispatchAlreadySoldQuantityMt = alreadySoldQuantityMt;
        model.DispatchRemainingQuantityMt = ResolveRemainingSellableQuantityMt(
            model.DispatchSellableQuantityMt,
            alreadySoldQuantityMt);
    }

    /// <summary>
    /// مقدارِ فروخته‌شدهٔ یک موتر = مجموع مقدارِ فروش‌های لغونشدهٔ وصل‌شده به همان موتر.
    /// دو مسیرِ لینک با هم dedupe می‌شوند: <c>SalesTransaction.TruckDispatchId</c> (مسیر فعلی که
    /// فروش قسمتی را هم پوشش می‌دهد) و <c>TruckDispatch.SalesTransactionId</c> (رکوردهای قدیمی که
    /// پیش از افزودن آن فیلد ثبت شده‌اند). هیچ عددی ذخیره نمی‌شود؛ همیشه از فروش‌های فعال محاسبه
    /// می‌گردد، پس لغو فروش به‌طور خودکار باقی‌مانده را برمی‌گرداند و دوباره‌شماری ممکن نیست.
    /// </summary>
    private async Task<decimal> GetSoldQuantityMtAsync(TruckDispatch dispatch, int? excludeSaleId = null)
    {
        var legacySaleId = dispatch.SalesTransactionId;
        var soldMt = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && (s.TruckDispatchId == dispatch.Id
                    || (legacySaleId != null && s.Id == legacySaleId.Value))
                && (excludeSaleId == null || s.Id != excludeSaleId.Value))
            .SumAsync(s => (decimal?)s.QuantityMt) ?? 0m;

        return decimal.Round(soldMt, 4, MidpointRounding.AwayFromZero);
    }

    private static decimal ResolveRemainingSellableQuantityMt(decimal sellableQuantityMt, decimal soldQuantityMt)
        => Math.Max(
            decimal.Round(sellableQuantityMt - soldQuantityMt, 4, MidpointRounding.AwayFromZero),
            0m);

    /// <summary>
    /// محمولهٔ یک فروشِ مستقیم از موتر فقط از Lineage واقعیِ همان موتر گرفته می‌شود:
    /// موتر → رسید انتقال داخلی → حملِ همان محموله. مسیر «تخصیصِ رسید بارگیری» هیچ گره‌ای به
    /// محموله ندارد، پس آنجا عمداً null می‌ماند و محموله حدس زده نمی‌شود.
    /// </summary>
    private async Task<int?> ResolveDispatchShipmentIdAsync(TruckDispatch dispatch)
    {
        if (!dispatch.InventoryTransportReceiptId.HasValue)
        {
            return null;
        }

        return await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => r.Id == dispatch.InventoryTransportReceiptId.Value)
            .Join(
                _db.InventoryTransportLegs.AsNoTracking(),
                r => r.InventoryTransportLegId,
                l => l.Id,
                (_, l) => l.ShipmentId)
            .FirstOrDefaultAsync();
    }

    private static void ApplyDirectFromReceiptSaleContext(
        DispatchDirectFromReceiptSaleCreateViewModel model,
        TruckDispatch dispatch,
        LoadingReceiptAllocation allocation,
        decimal alreadySoldQuantityMt,
        string? returnUrl = null)
    {
        model.TruckDispatchId = dispatch.Id;
        model.LoadingReceiptAllocationId = allocation.Id;
        model.LoadingReceiptId = allocation.LoadingReceiptId;
        model.ContractNumber = allocation.SourcePurchaseContract?.DisplayLabel
            ?? allocation.LoadingReceipt?.LoadingRegister?.Contract?.DisplayLabel
            ?? dispatch.Contract?.DisplayLabel
            ?? "";
        model.ProductName = dispatch.Product?.Name
            ?? allocation.LoadingReceipt?.LoadingRegister?.Product?.Name
            ?? "";
        model.TruckPlateNumber = dispatch.Truck?.PlateNumber ?? "";
        model.DriverName = dispatch.Driver?.FullName;
        model.DestinationName = dispatch.DestinationLocation?.Name
            ?? allocation.DestinationName
            ?? allocation.DestinationLocation?.Name
            ?? allocation.DestinationReference;
        ApplySellableQuantityContext(model, dispatch, alreadySoldQuantityMt);

        if (returnUrl is not null)
        {
            model.ReturnUrl = returnUrl;
        }
    }

    private static void ApplyFromInventorySaleContext(
        DispatchDirectFromReceiptSaleCreateViewModel model,
        TruckDispatch dispatch,
        decimal alreadySoldQuantityMt,
        string? returnUrl = null)
    {
        model.TruckDispatchId = dispatch.Id;
        model.LoadingReceiptAllocationId = null;
        model.LoadingReceiptId = null;
        model.ContractNumber = dispatch.Contract?.DisplayLabel ?? "";
        model.ProductName = dispatch.Product?.Name ?? "";
        model.TruckPlateNumber = dispatch.Truck?.PlateNumber ?? "";
        model.DriverName = dispatch.Driver?.FullName;
        model.DestinationName = dispatch.DestinationLocation?.Name;
        ApplySellableQuantityContext(model, dispatch, alreadySoldQuantityMt);

        if (returnUrl is not null)
        {
            model.ReturnUrl = returnUrl;
        }
    }

    private IQueryable<TruckDispatch> BuildDirectFromReceiptSaleDispatchQuery(bool asTracking)
    {
        var query = _db.TruckDispatches
            .Include(d => d.Contract)
            .Include(d => d.Product)
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.DestinationLocation)
            .Include(d => d.SalesTransaction)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.SourcePurchaseContract)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.LoadingReceipt)
                    .ThenInclude(r => r!.LoadingRegister)
                        .ThenInclude(l => l!.Contract)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.LoadingReceipt)
                    .ThenInclude(r => r!.LoadingRegister)
                        .ThenInclude(l => l!.Product)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.DestinationLocation)
            .AsQueryable();

        return asTracking ? query : query.AsNoTracking();
    }

    private async Task<Contract?> LockSourceContractAsync(int contractId)
    {
        if (_db.Database.IsRelational()
            && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return await _db.Contracts
                .FromSqlInterpolated($@"SELECT * FROM ""Contracts"" WHERE ""Id"" = {contractId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync();
        }

        return await _db.Contracts
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == contractId);
    }

    private async Task<StorageTank?> LockSourceStorageTankAsync(int storageTankId)
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

    public async Task<IActionResult> Index([FromQuery] DispatchIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        filter ??= new DispatchIndexFilterViewModel();

        var query = _db.TruckDispatches
            .AsNoTracking()
            .AsQueryable();

        query = query.Where(d => d.Status != DispatchStatus.Cancelled);

        // رکوردهای سازگاریِ ادامهٔ حمل ارسال مستقل نیستند؛ همان عملیات در فهرست حمل‌های در
        // جریان دیده می‌شود. صفحهٔ جزئیاتشان از خودِ حملِ والد در دسترس می‌ماند.
        var continuedReceiptIds = TransportChainProjection.ContinuedTransferReceiptIds(_db);
        query = query.Where(d => !(d.InventoryTransportReceiptId != null
            && continuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)));

        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (filter.TruckId.Length > 0)
        {
            var truckIds = filter.TruckId;
            query = query.Where(d => truckIds.Contains(d.TruckId));
        }
        if (filter.ProductId.Length > 0)
        {
            var productIds = filter.ProductId;
            query = query.Where(d => productIds.Contains(d.ProductId));
        }
        if (filter.ContractId.Length > 0)
        {
            var contractIds = filter.ContractId;
            query = query.Where(d => contractIds.Contains(d.ContractId));
        }
        if (filter.FromDate.HasValue) query = query.Where(d => d.DispatchDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) query = query.Where(d => d.DispatchDate <= filter.ToDate.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        page = Math.Clamp(page, 1, pageCount);

        await PopulateLookupsAsync(filter: filter);

        var items = await query
            .OrderByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(d => new DispatchListItemViewModel
            {
                Id = d.Id,
                DispatchDate = d.DispatchDate,
                TruckPlateNumber = d.Truck != null ? d.Truck.PlateNumber : "",
                ProductName = d.Product != null ? d.Product.Name : "",
                ContractName = d.Contract != null ? d.Contract.ContractName : "",
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : "",
                DriverName = d.Driver != null ? d.Driver.FullName : null,
                DestinationName = d.DestinationLocation != null ? d.DestinationLocation.Name : null,
                LoadedQuantityMt = d.LoadedQuantityMt,
                ShortageMt = d.ShortageMt,
                FreightCostUsd = d.FreightCostUsd,
                ServiceProviderId = d.ServiceProviderId,
                ServiceProviderName = d.ServiceProvider != null ? d.ServiceProvider.Name : null,
                OperationalAssetId = d.OperationalAssetId,
                OperationalAssetName = d.OperationalAsset != null ? d.OperationalAsset.Name : null,
                StatusName = d.Status.ToString()
            })
            .ToListAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        ViewBag.SumQuantity = await query.SumAsync(d => d.LoadedQuantityMt);
        ViewBag.DeliveredCount = await query.CountAsync(d => d.Status == DispatchStatus.Delivered);
        // تفاوتِ ذخیره‌شده علامت‌دار است؛ این جمع فقط بخش کسری را برمی‌دارد تا اضافه‌بار آن را خنثی نکند.
        // تفکیک کامل کسری/اضافه‌بار در «راپور کسری و اضافه‌بار حمل» (Reports/TransportVariance) است.
        ViewBag.SumShortage = await query.SumAsync(d => d.ShortageMt.HasValue && d.ShortageMt.Value > 0m ? d.ShortageMt.Value : 0m);

        return View(new DispatchIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var dispatch = await _db.TruckDispatches
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id);

        if (dispatch is null)
        {
            return NotFound();
        }

        var stockOutMovement = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ReferenceDocument == $"TRUCK-DISPATCH:{dispatch.Id}" && m.Direction == MovementDirection.Out)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync();

        var model = new DispatchCreateViewModel
        {
            // PTG-P1-05 — نسخه‌ای که کاربر می‌بیند، با فرم برمی‌گردد.
            Version = dispatch.Version,
            ContractId = dispatch.ContractId,
            ProductId = dispatch.ProductId,
            TruckId = dispatch.TruckId,
            DriverId = dispatch.DriverId,
            DestinationLocationId = dispatch.DestinationLocationId,
            ServiceProviderId = dispatch.ServiceProviderId,
            OperationalAssetId = dispatch.OperationalAssetId,
            DispatchDate = dispatch.DispatchDate,
            SourceTerminalId = stockOutMovement?.TerminalId ?? 0,
            SourceStorageTankId = stockOutMovement?.StorageTankId,
            LoadedQuantityMt = dispatch.LoadedQuantityMt,
            DischargedQuantityMt = dispatch.DischargedQuantityMt,
            AllowanceMt = dispatch.AllowanceMt,
            ShortageMt = dispatch.ShortageMt,
            FreightCostUsd = dispatch.FreightCostUsd,
            ShortageRateUsd = dispatch.ShortageRateUsd,
            FreightPayableUsd = dispatch.FreightPayableUsd,
            // Gap #4 + #5 — must be restored on edit
            TicketSerialNumber = dispatch.TicketSerialNumber,
            ToleranceMt = dispatch.ToleranceMt,
            ChargeableShortageMt = dispatch.ChargeableShortageMt,
            ReferenceDocument = null,
            Notes = dispatch.Notes,
            ReturnUrl = returnUrl
        };

        ViewData["IsEdit"] = true;
        ViewData["EditId"] = id;
        ViewData["Title"] = "ویرایش دیسپچ";

        await PopulateLookupsAsync(createModel: model);
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, DispatchCreateViewModel model)
    {
        var dispatch = await _db.TruckDispatches
            .FirstOrDefaultAsync(d => d.Id == id);

        if (dispatch is null)
        {
            return NotFound();
        }

        // PTG-P1-05 — معیارِ برخورد، نسخه‌ای است که کاربر هنگامِ بازکردنِ فرم دید،
        // نه نسخه‌ای که همین حالا خوانده شد.
        _db.UseExpectedVersion(dispatch, model.Version);

        if (dispatch.Status == DispatchStatus.Cancelled)
        {
            ModelState.AddModelError(string.Empty, "این دیسپچ لغو شده است و قابل ویرایش نیست.");
        }

        // ویرایش هرگز سند خروجی موجودی یا اثرهای پایین‌دست را دوباره نمی‌نویسد. پس هر فیلدی که
        // آن اسناد را ناسازگار کند، بعد از ثبت آن‌ها قفل می‌شود. برای اصلاح واقعی باید دیسپچ لغو
        // (Reverse) و دوباره ثبت (Repost) شود؛ mutation خاموشِ سند قطعی مجاز نیست.
        var postedStockOutMovement = await FindDispatchStockOutMovementAsync(id);
        var downstreamBlockers = await FindDispatchDownstreamBlockersAsync(dispatch);
        var hasSettledEffects = postedStockOutMovement is not null || downstreamBlockers.Count > 0;

        if (hasSettledEffects)
        {
            var lockReason = downstreamBlockers.Count > 0
                ? "عملیات بعدی روی این دیسپچ ثبت شده است (" + string.Join("، ", downstreamBlockers) + ")"
                : "خروجی موجودی برای این دیسپچ ثبت شده است";

            if (model.ContractId != dispatch.ContractId)
            {
                ModelState.AddModelError(
                    nameof(model.ContractId),
                    $"{lockReason}؛ تغییر قرارداد مجاز نیست. برای اصلاح، دیسپچ را لغو و دوباره ثبت کنید.");
            }

            if (model.ProductId != dispatch.ProductId)
            {
                ModelState.AddModelError(
                    nameof(model.ProductId),
                    $"{lockReason}؛ تغییر کالا مجاز نیست. برای اصلاح، دیسپچ را لغو و دوباره ثبت کنید.");
            }

            if (!QuantitiesMatch(model.LoadedQuantityMt, dispatch.LoadedQuantityMt))
            {
                ModelState.AddModelError(
                    nameof(model.LoadedQuantityMt),
                    $"{lockReason}؛ تغییر مقدار بارگیری مجاز نیست. برای اصلاح، دیسپچ را لغو و دوباره ثبت کنید.");
            }

            // مبدأ فقط روی همان سند خروجی معنا دارد و ویرایش آن را نمی‌نویسد؛ پس به‌جای نادیده‌گرفتنِ
            // خاموشِ ورودی کاربر، تغییرش رد می‌شود.
            if (postedStockOutMovement is not null
                && (model.SourceTerminalId != postedStockOutMovement.TerminalId
                    || model.SourceStorageTankId != postedStockOutMovement.StorageTankId))
            {
                ModelState.AddModelError(
                    nameof(model.SourceTerminalId),
                    $"{lockReason}؛ تغییر ترمینال/مخزن مبدأ مجاز نیست. برای اصلاح، دیسپچ را لغو و دوباره ثبت کنید.");
            }
        }

        if (model.ContractId > 0)
        {
            var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
            else if (contract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError(
                    nameof(model.ContractId),
                    "برای دیسپچ از موجودی، فقط قرارداد خرید (Purchase) قابل انتخاب است.");
            }
        }

        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);
        await ValidateOperationalPartyAsync(
            model.ServiceProviderId,
            model.OperationalAssetId,
            nameof(model.ServiceProviderId),
            nameof(model.OperationalAssetId),
            model.TruckId);

        if (!ModelState.IsValid)
        {
            ViewData["IsEdit"] = true;
            ViewData["EditId"] = id;
            ViewData["Title"] = "ویرایش دیسپچ";
            await PopulateLookupsAsync(createModel: model);
            return View("Create", model);
        }

        dispatch.ContractId = model.ContractId;
        dispatch.ProductId = model.ProductId;
        dispatch.TruckId = model.TruckId;
        dispatch.DriverId = model.DriverId;
        dispatch.DestinationLocationId = model.DestinationLocationId;
        dispatch.ServiceProviderId = model.ServiceProviderId;
        dispatch.OperationalAssetId = model.OperationalAssetId;
        dispatch.DispatchDate = model.DispatchDate;
        dispatch.LoadedQuantityMt = model.LoadedQuantityMt;
        dispatch.DischargedQuantityMt = model.DischargedQuantityMt;
        dispatch.AllowanceMt = model.AllowanceMt;
        dispatch.ShortageMt = model.ShortageMt;
        dispatch.FreightCostUsd = model.FreightCostUsd;
        dispatch.ShortageRateUsd = model.ShortageRateUsd;
        // Gap #5 — pass tolerance + chargeable override when editing
        dispatch.FreightPayableUsd = ComputeFreightPayable(model.FreightCostUsd, model.ShortageMt, model.AllowanceMt, model.ShortageRateUsd, model.ToleranceMt, model.ChargeableShortageMt);
        // Gap #4 + #5 — persist new fields on edit
        dispatch.TicketSerialNumber = string.IsNullOrWhiteSpace(model.TicketSerialNumber) ? null : model.TicketSerialNumber.Trim();
        dispatch.ToleranceMt = model.ToleranceMt;
        dispatch.ChargeableShortageMt = model.ChargeableShortageMt;
        dispatch.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        await _db.SaveChangesAsync();
        await SyncDispatchFreightExpenseAsync(dispatch);

        TempData["ok"] = "دیسپچ با موفقیت ویرایش شد.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? returnUrl = null)
    {
        var dispatch = await _db.TruckDispatches
            .FirstOrDefaultAsync(d => d.Id == id);

        if (dispatch is null)
        {
            return NotFound();
        }

        // لغو دوباره هیچ سند جدیدی نمی‌سازد؛ بدون این نگهبان هر کلیک/ارسال مجدد یک ورودیِ
        // برگشتیِ تازه می‌ساخت و موجودی مبدأ را بی‌دلیل بالا می‌برد.
        if (dispatch.Status == DispatchStatus.Cancelled)
        {
            TempData["ok"] = "این دیسپچ قبلاً لغو شده است.";

            if (TryGetLocalReturnUrl(returnUrl, out var cancelledReturnUrl))
            {
                return Redirect(cancelledReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        // لغو باید downstream-aware باشد: اگر بار قبلاً تخلیه یا فروخته شده، برگرداندنِ فقط
        // خروجی مبدأ موجودی مصنوعی می‌سازد. اول باید عملیات بعدی لغو شود.
        var downstreamBlockers = await FindDispatchDownstreamBlockersAsync(dispatch);
        if (downstreamBlockers.Count > 0)
        {
            TempData["err"] = "لغو این دیسپچ ممکن نیست چون عملیات بعدی روی آن ثبت شده است ("
                + string.Join("، ", downstreamBlockers)
                + "). ابتدا عملیات بعدی را لغو کنید.";

            if (TryGetLocalReturnUrl(returnUrl, out var blockedReturnUrl))
            {
                return Redirect(blockedReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            // رسیدهای زنجیره‌ای که با همین لغو برمی‌گردند؛ سطر مصرفِ دارایی‌شان بعد از
            // SaveChanges همگام می‌شود (همان جایی که مصرفِ خود دیسپچ همگام می‌شود).
            var cancelledChainReceipts = new List<InventoryTransportReceipt>();

            // Dispatchهای ساخته‌شده از انتقال وسیله→وسیله فقط Projection سازگاری‌اند. لغوشان
            // باید دقیقاً همان نگهبان زنجیره را اجرا کند و همهٔ رسیدهای همراهِ همان child را برگرداند.
            if (dispatch.InventoryTransportReceiptId.HasValue)
            {
                var primaryReceiptId = dispatch.InventoryTransportReceiptId.Value;
                var companionMarker = $"{TransportChainService.CompanionReceiptNotePrefix}{primaryReceiptId}]";
                var chainReceipts = await _db.InventoryTransportReceipts
                    .Include(r => r.InventoryTransportLeg)
                    .Where(r => r.Id == primaryReceiptId
                        || (r.Notes != null && r.Notes.StartsWith(companionMarker)))
                    .ToListAsync();
                var activeReceiptIds = chainReceipts
                    .Where(r => !r.IsCancelled)
                    .Select(r => r.Id)
                    .ToList();

                await _transportChain.CancelVehicleTransferAsync(activeReceiptIds);

                foreach (var chainReceipt in chainReceipts.Where(r => !r.IsCancelled))
                {
                    chainReceipt.IsCancelled = true;
                    if (chainReceipt.InventoryTransportLeg?.Status == InventoryTransportLegStatus.Received)
                    {
                        chainReceipt.InventoryTransportLeg.Status = InventoryTransportLegStatus.InTransit;
                    }

                    cancelledChainReceipts.Add(chainReceipt);
                }
            }

            dispatch.Status = DispatchStatus.Cancelled;
            await CancelDispatchFreightExpenseAsync(dispatch.Id);

            var stockOutMovement = await _db.InventoryMovements
                .AsNoTracking()
                .Where(m => m.ReferenceDocument == $"TRUCK-DISPATCH:{dispatch.Id}" && m.Direction == MovementDirection.Out)
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync();

            if (stockOutMovement is not null)
            {
                // Writer خودش idempotent است: اگر معکوسِ همین سند از قبل باشد، سند دوم نمی‌سازد.
                await _movements.PostReversalAsync(
                    stockOutMovement,
                    _businessClock.Today,
                    $"Reversal for cancelled DispatchId={dispatch.Id}");
            }

            if (dispatch.DispatchMode == TruckDispatchMode.DirectFromReceipt
                && dispatch.LoadingReceiptAllocationId.HasValue)
            {
                var allocation = await _db.LoadingReceiptAllocations
                    .Include(a => a.DirectTruckDispatches)
                    .FirstOrDefaultAsync(a => a.Id == dispatch.LoadingReceiptAllocationId.Value);

                if (allocation is not null && allocation.Status != LoadingReceiptAllocationStatus.Cancelled)
                {
                    var activeDirectDispatchQuantityMt = GetActiveDirectFromReceiptDispatchQuantity(allocation);
                    allocation.Status = activeDirectDispatchQuantityMt <= 0m
                        ? LoadingReceiptAllocationStatus.TraceOnly
                        : activeDirectDispatchQuantityMt >= allocation.QuantityMt
                            ? LoadingReceiptAllocationStatus.Completed
                            : LoadingReceiptAllocationStatus.InTransit;
                }
            }

            await _db.SaveChangesAsync();
            var usageWriter = new AssetUsageChargeService(_db);
            await usageWriter.SyncOperationAsync(dispatch);
            foreach (var chainReceipt in cancelledChainReceipts.Where(r => r.InventoryTransportLeg is not null))
            {
                await usageWriter.SyncOperationAsync(chainReceipt, chainReceipt.InventoryTransportLeg!);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            TempData["err"] = ex.Message;
            if (TryGetLocalReturnUrl(returnUrl, out var chainBlockedReturnUrl))
            {
                return Redirect(chainBlockedReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["ok"] = "دیسپچ لغو شد.";

        if (TryGetLocalReturnUrl(returnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public Task<IActionResult> Create(int? contractId = null, string? returnUrl = null)
        => Task.FromResult<IActionResult>(RedirectToAction(
            "Create",
            "Transports",
            new { sourceKind = (int)TransportStartSourceKind.Inventory }));

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateDirectFromReceipt(int allocationId, string? returnUrl = null)
    {
        var allocation = await BuildDirectFromReceiptAllocationQuery(asTracking: false)
            .FirstOrDefaultAsync(a => a.Id == allocationId);

        if (allocation is null)
        {
            return NotFound();
        }

        if (allocation.Destination != LoadingReceiptAllocationDestination.DirectDispatchToTruck)
        {
            return BadRequest();
        }

        return RedirectToAction("FromReceipt", "Transports", new { loadingReceiptId = allocation.LoadingReceiptId });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDirectFromReceipt(
        DispatchDirectFromReceiptCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var allocation = await BuildDirectFromReceiptAllocationQuery(asTracking: true)
            .FirstOrDefaultAsync(a => a.Id == model.LoadingReceiptAllocationId);

        if (allocation is null)
        {
            return NotFound();
        }

        var loading = allocation.LoadingReceipt?.LoadingRegister;
        var totalDirectDispatchedQuantityMt = GetActiveDirectFromReceiptDispatchQuantity(allocation);
        var remainingQuantityMt = Math.Max(allocation.QuantityMt - totalDirectDispatchedQuantityMt, 0m);

        if (allocation.Destination != LoadingReceiptAllocationDestination.DirectDispatchToTruck)
        {
            ModelState.AddModelError(string.Empty, "Selected allocation is not a DirectDispatchToTruck allocation.");
        }

        if (allocation.Status is not (LoadingReceiptAllocationStatus.TraceOnly or LoadingReceiptAllocationStatus.InTransit))
        {
            ModelState.AddModelError(string.Empty, "Selected allocation is not open for direct truck dispatch.");
        }

        if (allocation.QuantityMt <= 0m)
        {
            ModelState.AddModelError(string.Empty, "Selected allocation quantity must be greater than zero.");
        }

        if (!allocation.SourcePurchaseContractId.HasValue || allocation.SourcePurchaseContract is null)
        {
            ModelState.AddModelError(string.Empty, "Selected allocation does not have a valid source purchase contract.");
        }

        if (loading is null)
        {
            ModelState.AddModelError(string.Empty, "Selected allocation does not have a valid loading receipt source.");
        }
        else if (allocation.SourcePurchaseContract is not null && allocation.SourcePurchaseContract.ProductId != loading.ProductId)
        {
            ModelState.AddModelError(string.Empty, "Selected allocation source contract product does not match the loading product.");
        }

        model.TruckPlateNumberInput = NormalizeTruckPlateNumber(model.TruckPlateNumberInput);
        model.DriverNameInput = NormalizeDriverNameInput(model.DriverNameInput);
        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);
        var transport = await ResolveTransportAsync(
            model.TruckId,
            model.DriverId,
            model.TruckPlateNumberInput,
            model.DriverNameInput,
            nameof(model.TruckId),
            nameof(model.TruckPlateNumberInput),
            nameof(model.DriverId),
            nameof(model.DriverNameInput));

        if (model.DestinationLocationId.HasValue)
        {
            var destinationExists = await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value);
            if (!destinationExists)
            {
                ModelState.AddModelError(nameof(model.DestinationLocationId), "Selected destination is not valid.");
            }
        }

        if (model.LoadedQuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.LoadedQuantityMt), "Loaded quantity must be greater than zero.");
        }
        else if (model.LoadedQuantityMt > remainingQuantityMt)
        {
            ModelState.AddModelError(
                nameof(model.LoadedQuantityMt),
                $"Loaded quantity exceeds remaining allocation quantity ({remainingQuantityMt:N4} MT).");
        }

        await ValidateOperationalPartyAsync(
            model.ServiceProviderId,
            model.OperationalAssetId,
            nameof(model.ServiceProviderId),
            nameof(model.OperationalAssetId),
            model.TruckId);

        // Discharged weight comes from the destination scale and may exceed the loaded weight
        // (scale overage). Shortage then becomes zero; freight still follows the loaded weight.

        if (!ModelState.IsValid)
        {
            ApplyDirectFromReceiptContext(model, allocation, totalDirectDispatchedQuantityMt);
            await PopulateDirectFromReceiptLookupsAsync(model);
            return View(model);
        }

        var dispatch = new TruckDispatch
        {
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = allocation.Id,
            ContractId = allocation.SourcePurchaseContractId!.Value,
            ProductId = loading!.ProductId,
            TruckId = transport!.CreatedTruck is null ? transport.TruckId : 0,
            Truck = transport.CreatedTruck,
            DriverId = transport.CreatedDriver is null ? transport.DriverId : null,
            Driver = transport.CreatedDriver,
            DestinationLocationId = model.DestinationLocationId,
            ServiceProviderId = model.ServiceProviderId,
            OperationalAssetId = model.OperationalAssetId,
            DispatchDate = model.DispatchDate,
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = model.LoadedQuantityMt,
            DischargedQuantityMt = model.DischargedQuantityMt,
            AllowanceMt = model.AllowanceMt,
            ShortageMt = model.ShortageMt,
            FreightCostUsd = model.FreightCostUsd,
            ShortageRateUsd = model.ShortageRateUsd,
            FreightPayableUsd = ComputeFreightPayable(
                model.FreightCostUsd,
                model.ShortageMt,
                model.AllowanceMt,
                model.ShortageRateUsd,
                model.ToleranceMt,
                model.ChargeableShortageMt),
            TicketSerialNumber = string.IsNullOrWhiteSpace(model.TicketSerialNumber) ? null : model.TicketSerialNumber.Trim(),
            ToleranceMt = model.ToleranceMt,
            ChargeableShortageMt = model.ChargeableShortageMt,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };
        var carrierParty = await new AssetUsageChargeService(_db).ResolveCarrierPartyAsync(
            dispatch.ServiceProviderId,
            dispatch.DriverId,
            dispatch.OperationalAssetId,
            dispatch.DispatchDate);
        dispatch.CarrierPartyType = carrierParty?.PartyType;
        dispatch.CarrierPartyId = carrierParty?.PartyId;

        var totalAfterDispatchMt = totalDirectDispatchedQuantityMt + model.LoadedQuantityMt;
        allocation.Status = totalAfterDispatchMt >= allocation.QuantityMt
            ? LoadingReceiptAllocationStatus.Completed
            : LoadingReceiptAllocationStatus.InTransit;

        // PTG-P0-01 — توکن با همان SaveChanges دیسپچ مصرف می‌شود؛ ارسال دوم چیزی نمی‌سازد.
        _formTokens.Stamp(formToken, "Dispatch.CreateDirectFromReceipt", nameof(TruckDispatch));

        _db.TruckDispatches.Add(dispatch);
        try
        {
            await _db.SaveChangesAsync();
            await new AssetUsageChargeService(_db).SyncOperationAsync(dispatch);
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            TempData["err"] = "این دیسپچ قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction("FromReceipt", "Transports", new { loadingReceiptId = allocation.LoadingReceiptId });
        }

        await SyncDispatchFreightExpenseAsync(dispatch);

        await LogCreatedTransportAsync(transport);
        await _audit.LogAndSaveAsync(
            nameof(TruckDispatch),
            dispatch.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("DispatchMode", dispatch.DispatchMode),
                ("LoadingReceiptAllocationId", dispatch.LoadingReceiptAllocationId),
                ("ContractId", dispatch.ContractId),
                ("ProductId", dispatch.ProductId),
                ("TruckId", dispatch.TruckId),
                ("DriverId", dispatch.DriverId),
                ("DestinationLocationId", dispatch.DestinationLocationId),
                ("DispatchDate", dispatch.DispatchDate),
                ("LoadedQuantityMt", dispatch.LoadedQuantityMt),
                ("NoInventoryMovement", true)));

        TempData["ok"] = "Direct dispatch from receipt allocation was registered without stock movement.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = dispatch.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Unload(int id, string? returnUrl = null)
    {
        var dispatch = await BuildUnloadDispatchQuery(asTracking: false)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (dispatch is null)
        {
            return NotFound();
        }

        // رکورد سازگاریِ ادامهٔ حمل بارِ مستقل ندارد؛ تخلیه‌اش سند موجودی موازیِ رسیدِ مرحلهٔ
        // فرزند می‌سازد. تخلیه فقط از مسیر همان مرحلهٔ فرزند انجام می‌شود.
        if (await TransportChainProjection.IsContinuationProjectionAsync(_db, dispatch))
        {
            TempData["err"] = "این ارسال رکورد سازگاریِ ادامهٔ حمل است؛ تخلیه را از خودِ حملِ در جریان ثبت کنید.";
            return RedirectToAction(nameof(Details), new { id, returnUrl });
        }

        var deliveryReceipt = await _db.DeliveryReceipts
            .AsNoTracking()
            .Where(r => r.TruckDispatchId == id)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        var unloadMovement = await FindTruckUnloadInventoryMovementAsync(id);

        // تفاوت علامت‌دار: مثبت = کسری، منفی = اضافه‌بار.
        var shortageMt = dispatch.ShortageMt
            ?? (dispatch.DischargedQuantityMt.HasValue
                ? TransportVarianceMath.Difference(dispatch.LoadedQuantityMt, dispatch.DischargedQuantityMt.Value)
                : (decimal?)null);
        var allowanceMt = dispatch.ToleranceMt ?? dispatch.AllowanceMt;
        var chargeableShortageMt = dispatch.ChargeableShortageMt
            ?? TransportVarianceMath.ChargeableShortage(shortageMt ?? 0m, allowanceMt ?? 0m);
        var driverShortageChargeUsd = dispatch.PayableUsd
            ?? ComputeShortageChargeUsd(chargeableShortageMt, dispatch.ShortageRateUsd);

        var model = new DispatchUnloadViewModel
        {
            TruckDispatchId = dispatch.Id,
            ReceiptDate = deliveryReceipt?.ReceiptDate ?? _businessClock.Today,
            DestinationTerminalId = unloadMovement?.TerminalId
                ?? dispatch.LoadingReceiptAllocation?.DestinationTerminalId
                ?? 0,
            DestinationStorageTankId = unloadMovement?.StorageTankId
                ?? dispatch.LoadingReceiptAllocation?.DestinationStorageTankId,
            DischargedQuantityMt = dispatch.DischargedQuantityMt
                ?? deliveryReceipt?.ReceivedQuantityMt
                ?? dispatch.LoadedQuantityMt,
            ShortageMt = shortageMt,
            AllowanceMt = allowanceMt,
            FreightCostUsd = dispatch.FreightCostUsd,
            ShortageRateUsd = dispatch.ShortageRateUsd,
            ChargeableShortageMt = dispatch.ChargeableShortageMt,
            DriverShortageChargeUsd = driverShortageChargeUsd,
            FreightPayableUsd = dispatch.FreightPayableUsd,
            ServiceProviderId = dispatch.ServiceProviderId,
            OperationalAssetId = dispatch.OperationalAssetId,
            ReceivedBy = deliveryReceipt?.ReceivedBy,
            DocumentReference = deliveryReceipt?.DocumentReference ?? dispatch.TicketSerialNumber,
            Notes = dispatch.Notes,
            ReturnUrl = returnUrl
        };

        await ApplyUnloadContextAsync(model, dispatch);
        await PopulateUnloadLookupsAsync(model);
        await PopulateDispatchSaleFieldLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Unload(int id, DispatchUnloadViewModel model)
    {
        model.TruckDispatchId = id;
        model.ReceiptDate = model.ReceiptDate.Date;
        model.ReceivedBy = NormalizeNullable(model.ReceivedBy);
        model.DocumentReference = NormalizeNullable(model.DocumentReference);
        model.Notes = NormalizeNullable(model.Notes);
        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);

        var dispatch = await BuildUnloadDispatchQuery(asTracking: true)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (dispatch is null)
        {
            return NotFound();
        }

        if (dispatch.Status == DispatchStatus.Cancelled)
        {
            ModelState.AddModelError(string.Empty, "این دیسپچ لغو شده است و قابل تخلیه نیست.");
        }

        if (dispatch.SalesTransactionId.HasValue)
        {
            ModelState.AddModelError(string.Empty, "این دیسپچ به فروش مستقیم وصل است؛ تخلیه به مخزن برای آن مجاز نیست.");
        }

        if (await TransportChainProjection.IsContinuationProjectionAsync(_db, dispatch))
        {
            ModelState.AddModelError(string.Empty, "این ارسال رکورد سازگاریِ ادامهٔ حمل است؛ تخلیه را از خودِ حملِ در جریان ثبت کنید.");
        }

        // اگر تخلیه جزئی از فرم جدید «رسید/تسویه/تخلیه وسایط» ثبت شده باشد، مسیر قدیمی
        // (که یک Movement واحد را بازنویسی می‌کند) مجاز نیست تا موجودی دوباره شمرده نشود.
        var arrivalRefPrefix = $"TRUCK-ARRIVAL:{dispatch.Id}:";
        var hasArrivalMovements = await _db.InventoryMovements
            .AsNoTracking()
            .AnyAsync(m => m.ReferenceDocument != null && m.ReferenceDocument.StartsWith(arrivalRefPrefix));
        if (hasArrivalMovements)
        {
            ModelState.AddModelError(string.Empty, "برای این دیسپچ از فرم «رسید، تسویه و تخلیه وسایط» ثبت انجام شده است؛ ادامه تخلیه را از همان فرم انجام دهید.");
        }

        await ValidateOperationalPartyAsync(
            model.ServiceProviderId,
            model.OperationalAssetId,
            nameof(model.ServiceProviderId),
            nameof(model.OperationalAssetId),
            dispatch.TruckId);

        // وزن تخلیه از ترازوی مقصد می‌آید و می‌تواند از وزن بارگیری کمتر (کسری) یا بیشتر (اضافه‌بار) باشد.
        // تفاوت با علامتِ واقعی ذخیره می‌شود تا هیچ اختلافی گم نشود؛ اضافه‌بار هرگز جریمه نمی‌گیرد و
        // کرایه همچنان روی وزن بارگیری‌شده محاسبه می‌گردد.
        var computedShortageMt = TransportVarianceMath.Difference(dispatch.LoadedQuantityMt, model.DischargedQuantityMt);
        if (model.ShortageMt.HasValue && !QuantitiesMatch(model.ShortageMt.Value, computedShortageMt))
        {
            ModelState.AddModelError(nameof(model.ShortageMt), $"تفاوت باید برابر وزن بارگیری منهای وزن تخلیه باشد ({computedShortageMt:N4} MT). عدد مثبت = کسری، عدد منفی = اضافه‌بار.");
        }

        var terminal = await _db.Terminals
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == model.DestinationTerminalId && t.IsActive);
        if (terminal is null)
        {
            ModelState.AddModelError(nameof(model.DestinationTerminalId), "ترمینال مقصد معتبر نیست.");
        }

        if (model.DestinationStorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.DestinationStorageTankId.Value);
            if (tank is null)
            {
                ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد معتبر نیست.");
            }
            else
            {
                if (tank.TerminalId != model.DestinationTerminalId)
                {
                    ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد به ترمینال انتخاب‌شده تعلق ندارد.");
                }

                if (tank.ProductId.HasValue && tank.ProductId != dispatch.ProductId)
                {
                    ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد برای کالای این دیسپچ تعریف نشده است.");
                }
            }
        }

        var normalizedShortageMt = computedShortageMt;
        var normalizedAllowanceMt = model.AllowanceMt ?? 0m;
        // اضافه‌بار هرگز قابل جریمه نیست: مقدار قابل جریمه فقط از بخش کسریِ تفاوت ساخته می‌شود.
        var chargeableShortageMt = TransportVarianceMath.IsSurplus(normalizedShortageMt)
            ? 0m
            : model.ChargeableShortageMt
                ?? TransportVarianceMath.ChargeableShortage(normalizedShortageMt, normalizedAllowanceMt);
        var driverShortageChargeUsd = ComputeShortageChargeUsd(chargeableShortageMt, model.ShortageRateUsd);
        var freightPayableUsd = ComputeFreightPayable(
            model.FreightCostUsd,
            normalizedShortageMt,
            normalizedAllowanceMt,
            model.ShortageRateUsd,
            normalizedAllowanceMt,
            chargeableShortageMt);

        model.ShortageMt = normalizedShortageMt;
        model.AllowanceMt = normalizedAllowanceMt;
        model.ChargeableShortageMt = chargeableShortageMt;
        model.DriverShortageChargeUsd = driverShortageChargeUsd;
        model.FreightPayableUsd = freightPayableUsd;

        var postedUnloadMovement = await FindTruckUnloadInventoryMovementAsync(dispatch.Id);
        if (postedUnloadMovement is not null)
        {
            // سند قطعی موجودی قابل ویرایش درجا نیست. مسیر قدیمی فقط Create است؛ اصلاح باید
            // با workflow لغو/برگشت و ثبت دوباره انجام شود تا تاریخچهٔ موجودی حفظ بماند.
            ModelState.AddModelError(
                string.Empty,
                "تخلیهٔ این موتر قبلاً قطعی ثبت شده است و قابل ویرایش نیست؛ برای اصلاح ابتدا عملیات بعدی را برگشت دهید و تخلیه را دوباره ثبت کنید.");
        }

        if (!ModelState.IsValid)
        {
            await ApplyUnloadContextAsync(model, dispatch);
            await PopulateUnloadLookupsAsync(model);
            await PopulateDispatchSaleFieldLookupsAsync();
            return View(model);
        }

        var deliveryReceipt = await _db.DeliveryReceipts
            .Where(r => r.TruckDispatchId == dispatch.Id)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        var deliveryReceiptIsNew = deliveryReceipt is null;
        deliveryReceipt ??= new DeliveryReceipt
        {
            TruckDispatchId = dispatch.Id
        };

        var lossEvent = await _db.LossEvents
            .Where(l => l.TruckDispatchId == dispatch.Id && l.Stage == LossEventStage.DispatchShortage)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();
        var lossEventIsNew = lossEvent is null;
        var unloadTransportLegId = await _sourceAllocations.ResolveCurrentLegIdAsync(dispatch);
        var unloadLossSourcePlan = TransportVarianceMath.HasVariance(normalizedShortageMt)
            && unloadTransportLegId.HasValue
                ? await _sourceAllocations.BuildFromLegAsync(
                    unloadTransportLegId.Value,
                    Math.Abs(normalizedShortageMt))
                : TransportSourcePlan.Empty;

        var beforeDischarged = dispatch.DischargedQuantityMt;
        var beforeShortage = dispatch.ShortageMt;
        var beforeStatus = dispatch.Status;
        var beforePayable = dispatch.PayableUsd;

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            dispatch.Status = DispatchStatus.Delivered;
            dispatch.DischargedQuantityMt = model.DischargedQuantityMt;
            dispatch.ShortageMt = normalizedShortageMt;
            dispatch.AllowanceMt = normalizedAllowanceMt;
            dispatch.ToleranceMt = normalizedAllowanceMt;
            dispatch.ChargeableShortageMt = chargeableShortageMt;
            dispatch.FreightCostUsd = model.FreightCostUsd;
            dispatch.ShortageRateUsd = model.ShortageRateUsd;
            dispatch.FreightPayableUsd = freightPayableUsd;
            dispatch.ServiceProviderId = model.ServiceProviderId;
            dispatch.OperationalAssetId = model.OperationalAssetId;
            dispatch.PayableUsd = driverShortageChargeUsd > 0m ? driverShortageChargeUsd : null;
            dispatch.Notes = model.Notes;

            deliveryReceipt.ReceiptDate = model.ReceiptDate;
            deliveryReceipt.ReceivedQuantityMt = model.DischargedQuantityMt;
            deliveryReceipt.ReceivedBy = model.ReceivedBy;
            deliveryReceipt.DocumentReference = model.DocumentReference;
            if (deliveryReceiptIsNew)
            {
                _db.DeliveryReceipts.Add(deliveryReceipt);
            }

            var unloadMovement = await _movements.PostInboundAsync(new InventoryMovementRequest
            {
                ProductId = dispatch.ProductId,
                ContractId = dispatch.ContractId,
                TerminalId = model.DestinationTerminalId,
                StorageTankId = model.DestinationStorageTankId,
                MovementDate = model.ReceiptDate,
                QuantityMt = model.DischargedQuantityMt,
                ReferenceDocument = $"TRUCK-UNLOAD:{dispatch.Id}",
                Notes = BuildTruckUnloadInventoryNotes(dispatch.Id, model.DocumentReference, model.Notes)
            });

            // هر تفاوت غیر صفر — کسری یا اضافه‌بار — یک رکورد قابل ردیابی می‌سازد و رکورد موجودِ
            // همین دیسپچ به‌روزرسانی می‌شود (نه رکورد تکراری). تفاوت صفر رکورد قبلی را لغو می‌کند.
            if (TransportVarianceMath.HasVariance(normalizedShortageMt))
            {
                var metrics = _lossWorkflow.ComputeMetrics(
                    dispatch.LoadedQuantityMt,
                    model.DischargedQuantityMt,
                    normalizedAllowanceMt);
                var isSurplus = TransportVarianceMath.IsSurplus(normalizedShortageMt);

                lossEvent ??= new LossEvent
                {
                    Stage = LossEventStage.DispatchShortage,
                    TruckDispatchId = dispatch.Id
                };

                lossEvent.Stage = LossEventStage.DispatchShortage;
                lossEvent.ProductId = dispatch.ProductId;
                lossEvent.ContractId = dispatch.ContractId;
                lossEvent.TruckDispatchId = dispatch.Id;
                lossEvent.TransportLegId = unloadTransportLegId;
                lossEvent.TerminalId = model.DestinationTerminalId;
                lossEvent.StorageTankId = model.DestinationStorageTankId;
                lossEvent.EventDate = model.ReceiptDate;
                lossEvent.ExpectedQuantityMt = dispatch.LoadedQuantityMt;
                lossEvent.ActualQuantityMt = model.DischargedQuantityMt;
                lossEvent.DifferenceQuantityMt = metrics.DifferenceQuantityMt;
                lossEvent.ToleranceQuantityMt = normalizedAllowanceMt;
                lossEvent.AllowableLossMt = metrics.AllowableLossMt;
                lossEvent.ChargeableLossMt = metrics.ChargeableLossMt;
                lossEvent.ResponsiblePartyType = "Driver";
                lossEvent.ResponsiblePartyName = dispatch.Driver?.FullName;
                lossEvent.FinancialTreatment = isSurplus
                    ? "Truck unload positive variance (surplus). Never charged to the driver."
                    : BuildDriverFinancialTreatment(driverShortageChargeUsd, freightPayableUsd);
                lossEvent.AffectsInventory = false;
                lossEvent.InventoryMovementId = null;
                lossEvent.Reference = model.DocumentReference;
                lossEvent.Notes = model.Notes;
                lossEvent.IsCancelled = false;
                if (unloadLossSourcePlan.Shares.Count > 0)
                {
                    _sourceAllocations.ApplyLegacyHeader(lossEvent, unloadLossSourcePlan);
                }

                if (lossEventIsNew)
                {
                    _db.LossEvents.Add(lossEvent);
                }
            }
            else if (lossEvent is not null && !lossEvent.IsCancelled)
            {
                lossEvent.IsCancelled = true;
            }

            await _db.SaveChangesAsync();
            if (lossEvent is not null && !lossEvent.IsCancelled)
            {
                await _sourceAllocations.PersistLossAsync(
                    lossEvent,
                    unloadLossSourcePlan,
                    unloadTransportLegId);
            }
            await SyncDispatchFreightExpenseAsync(dispatch);

            await _audit.LogAsync(
                nameof(TruckDispatch),
                dispatch.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", beforeStatus, dispatch.Status),
                    ("DischargedQuantityMt", beforeDischarged, dispatch.DischargedQuantityMt),
                    ("ShortageMt", beforeShortage, dispatch.ShortageMt),
                    ("PayableUsd", beforePayable, dispatch.PayableUsd),
                    ("FreightPayableUsd", null, dispatch.FreightPayableUsd)));

            await _audit.LogAsync(
                nameof(DeliveryReceipt),
                deliveryReceipt.Id,
                deliveryReceiptIsNew ? AuditAction.Insert : AuditAction.Update,
                diff: deliveryReceiptIsNew
                    ? AuditDiffFormatter.ForCreate(
                        ("TruckDispatchId", deliveryReceipt.TruckDispatchId),
                        ("ReceiptDate", deliveryReceipt.ReceiptDate),
                        ("ReceivedQuantityMt", deliveryReceipt.ReceivedQuantityMt),
                        ("DocumentReference", deliveryReceipt.DocumentReference))
                    : AuditDiffFormatter.ForUpdate(
                        ("ReceiptDate", null, deliveryReceipt.ReceiptDate),
                        ("ReceivedQuantityMt", null, deliveryReceipt.ReceivedQuantityMt),
                        ("DocumentReference", null, deliveryReceipt.DocumentReference)));

            await _audit.LogAsync(
                nameof(InventoryMovement),
                unloadMovement.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ProductId", unloadMovement.ProductId),
                    ("ContractId", unloadMovement.ContractId),
                    ("TerminalId", unloadMovement.TerminalId),
                    ("StorageTankId", unloadMovement.StorageTankId),
                    ("Direction", unloadMovement.Direction),
                    ("QuantityMt", unloadMovement.QuantityMt),
                    ("ReferenceDocument", unloadMovement.ReferenceDocument)));

            if (lossEvent is not null)
            {
                await _audit.LogAsync(
                    nameof(LossEvent),
                    lossEvent.Id,
                    lossEventIsNew ? AuditAction.Insert : AuditAction.Update,
                    diff: lossEventIsNew
                        ? AuditDiffFormatter.ForCreate(
                            ("TruckDispatchId", lossEvent.TruckDispatchId),
                            ("ExpectedQuantityMt", lossEvent.ExpectedQuantityMt),
                            ("ActualQuantityMt", lossEvent.ActualQuantityMt),
                            ("ChargeableLossMt", lossEvent.ChargeableLossMt),
                            ("FinancialTreatment", lossEvent.FinancialTreatment))
                        : AuditDiffFormatter.ForUpdate(
                            ("IsCancelled", null, lossEvent.IsCancelled),
                            ("ActualQuantityMt", null, lossEvent.ActualQuantityMt),
                            ("ChargeableLossMt", null, lossEvent.ChargeableLossMt),
                            ("FinancialTreatment", null, lossEvent.FinancialTreatment)));
            }

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

        TempData["ok"] = "تخلیه موتر، رسید تحویل، موجودی مقصد و محاسبه کسری راننده ثبت شد.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = dispatch.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateSaleFromDirectDispatch(int dispatchId, string? returnUrl = null)
    {
        var dispatch = await BuildDirectFromReceiptSaleDispatchQuery(asTracking: false)
            .FirstOrDefaultAsync(d => d.Id == dispatchId);

        if (dispatch is null)
        {
            return NotFound();
        }

        var hasDirectDispatchAllocation =
            dispatch.LoadingReceiptAllocation?.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck;

        // منبع فروش: یا allocation نوع DirectDispatchToTruck، یا هر موتری با قرارداد خرید معتبر
        // (شامل موتر واگن→موتر که از رسید حمل ساخته می‌شود و allocation ندارد).
        if (!hasDirectDispatchAllocation
            && (dispatch.Contract is null || dispatch.Contract.ContractType != ContractType.Purchase))
        {
            TempData["error"] = "قرارداد خرید معتبر برای فروش این موتر موجود نیست.";
            return RedirectToAction(nameof(Details), new { id = dispatch.Id });
        }

        if (await _db.DeliveryReceipts.AsNoTracking().AnyAsync(r => r.TruckDispatchId == dispatch.Id))
        {
            TempData["error"] = "این دیسپچ قبلا به مخزن تخلیه شده است و دیگر نمی‌تواند فروش مستقیم از واگن ثبت کند.";
            return RedirectToAction(nameof(Details), new { id = dispatch.Id });
        }

        // باقی‌مانده = مقدار تخلیه‌شده منهای فروش‌های فعال قبلی. اگر چیزی باقی نمانده، فروش تازه
        // معنی ندارد و کاربر به جزئیات همان موتر برمی‌گردد (رفتار قبلیِ «قبلاً فروخته شده»).
        var alreadySoldQuantityMt = await GetSoldQuantityMtAsync(dispatch);
        var remainingQuantityMt = ResolveRemainingSellableQuantityMt(
            ResolveSellableQuantityMt(dispatch),
            alreadySoldQuantityMt);
        if (remainingQuantityMt <= 0m)
        {
            return RedirectToAction(nameof(Details), new { id = dispatch.Id });
        }

        var model = new DispatchDirectFromReceiptSaleCreateViewModel
        {
            TruckDispatchId = dispatch.Id,
            SaleDate = _businessClock.Today,
            // پیش‌فرض = باقی‌ماندهٔ مقدار واقعیِ تخلیه‌شده (شامل اضافه‌بار).
            QuantityMt = remainingQuantityMt,
            Currency = SystemCurrency.BaseCurrencyCode
        };

        if (dispatch.LoadingReceiptAllocation is not null)
        {
            ApplyDirectFromReceiptSaleContext(model, dispatch, dispatch.LoadingReceiptAllocation, alreadySoldQuantityMt, returnUrl);
        }
        else
        {
            ApplyFromInventorySaleContext(model, dispatch, alreadySoldQuantityMt, returnUrl);
        }

        await PopulateDirectFromReceiptSaleLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSaleFromDirectDispatch(DispatchDirectFromReceiptSaleCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        var normalizedInvoice = model.InvoiceNumber?.Trim() ?? string.Empty;
        var normalizedNotes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        var dispatch = await BuildDirectFromReceiptSaleDispatchQuery(asTracking: true)
            .FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId);

        if (dispatch is null)
        {
            return NotFound();
        }

        var allocation = dispatch.LoadingReceiptAllocation;
        var sourcePurchaseContract = allocation?.SourcePurchaseContract
            ?? dispatch.Contract;
        var loading = allocation?.LoadingReceipt?.LoadingRegister;

        var usesAllocation = allocation is not null
            && allocation.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck;

        // اعتبار منبع فروش با بررسی قرارداد خرید پایین‌تر انجام می‌شود؛ اینجا محدودیت mode لازم نیست.

        if (dispatch.Status == DispatchStatus.Cancelled)
        {
            ModelState.AddModelError(string.Empty, "Cancelled direct dispatch cannot be sold.");
        }

        if (await _db.DeliveryReceipts.AsNoTracking().AnyAsync(r => r.TruckDispatchId == dispatch.Id))
        {
            ModelState.AddModelError(string.Empty, "This dispatch already has a delivery receipt and cannot be sold as direct-from-wagon.");
        }

        if (sourcePurchaseContract is null || sourcePurchaseContract.ContractType != ContractType.Purchase)
        {
            ModelState.AddModelError(string.Empty, "A valid source purchase contract is required.");
        }

        if (usesAllocation && loading is null)
        {
            ModelState.AddModelError(string.Empty, "Dispatch source loading context is missing.");
        }

        if (model.CustomerId.HasValue == model.SupplierId.HasValue)
        {
            ModelState.AddModelError(nameof(model.BuyerKey), "Exactly one buyer is required.");
        }
        else if (model.CustomerId.HasValue
            && !await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.CustomerId.Value && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.BuyerKey), "Customer is invalid.");
        }
        else if (model.SupplierId.HasValue
            && !await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == model.SupplierId.Value && s.IsActive))
        {
            ModelState.AddModelError(nameof(model.BuyerKey), "Supplier is invalid.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        // مبنای فروش وزن واقعیِ تخلیه‌شده است، نه وزن بارگیری؛ پس اضافه‌بار هم فروخته می‌شود و
        // طلب مشتری روی مقدار واقعی می‌نشیند. (قبلاً به وزن بارگیری قفل بود و اضافه‌بار گم می‌شد.)
        // فروش قسمتی مجاز است: سقفِ هر فروش «باقی‌ماندهٔ» همان موتر است، نه کلِ تخلیه؛ فروش‌های
        // لغوشده در باقی‌مانده حساب نمی‌شوند، پس لغو یک فروش مقدارش را دوباره قابل فروش می‌کند.
        var sellableQuantityMt = ResolveSellableQuantityMt(dispatch);
        var alreadySoldQuantityMt = await GetSoldQuantityMtAsync(dispatch);
        var remainingQuantityMt = ResolveRemainingSellableQuantityMt(sellableQuantityMt, alreadySoldQuantityMt);
        if (model.QuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "Sale quantity must be greater than zero.");
        }
        else if (remainingQuantityMt <= 0m)
        {
            ModelState.AddModelError(
                string.Empty,
                $"تمام مقدار تخلیه‌شدهٔ این موتر قبلاً فروخته شده است ({sellableQuantityMt:N4} MT).");
        }
        else if (model.QuantityMt > remainingQuantityMt)
        {
            ModelState.AddModelError(
                nameof(model.QuantityMt),
                $"مقدار فروش از باقی‌ماندهٔ قابل فروش این موتر بیشتر است ({remainingQuantityMt:N4} MT).");
        }

        if (model.IsSalePricePending)
        {
            // قیمت باز: قیمت و نرخ ارز بعداً از صفحهٔ همان فروش ثبت می‌شود.
            ModelState.Remove(nameof(model.UnitPriceInCurrency));
            ModelState.Remove(nameof(model.AppliedFxRateToUsd));
            model.UnitPriceInCurrency = 0m;
            model.AppliedFxRateToUsd = null;
        }
        else if (model.UnitPriceInCurrency <= 0m)
        {
            ModelState.AddModelError(nameof(model.UnitPriceInCurrency), "Unit price must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(normalizedInvoice))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "Invoice number is required.");
        }
        else if (await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.InvoiceNumber == normalizedInvoice))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "Invoice number already exists.");
        }

        CurrencyConversionResult? conversion = null;
        if (ModelState.IsValid && model.IsSalePricePending)
        {
            conversion = SalePendingPricing.NeutralConversion(model.Currency, model.SaleDate);
        }
        else if (ModelState.IsValid)
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
            }
        }

        if (!ModelState.IsValid || conversion is null)
        {
            model.InvoiceNumber = normalizedInvoice;
            model.Notes = normalizedNotes;
            if (allocation is not null)
            {
                ApplyDirectFromReceiptSaleContext(model, dispatch, allocation, alreadySoldQuantityMt);
            }
            else
            {
                ApplyFromInventorySaleContext(model, dispatch, alreadySoldQuantityMt);
            }

            await PopulateDirectFromReceiptSaleLookupsAsync(model);
            return View(model);
        }

        var totalInCurrency = decimal.Round(model.QuantityMt * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var currentTransportLegId = await _sourceAllocations.ResolveCurrentLegIdAsync(dispatch);
        var saleSourcePlan = currentTransportLegId.HasValue
            ? await _sourceAllocations.BuildFromLegAsync(currentTransportLegId.Value, model.QuantityMt)
            : TransportSourcePlan.Empty;
        // ContractId قراردادِ *فروش* است و این فروش قرارداد فروش ندارد ⇒ null می‌ماند.
        // قرارداد خرید و محموله فقط از Lineage واقعیِ همین موتر می‌آیند (نه حدس) تا عاید در
        // سود و زیان قرارداد و محموله دیده شود.
        var sale = new SalesTransaction
        {
            ContractId = null,
            CompanyId = sourcePurchaseContract!.CompanyId,
            CustomerId = model.CustomerId,
            SupplierId = model.SupplierId,
            ProductId = dispatch.ProductId,
            DestinationLocationId = dispatch.DestinationLocationId ?? allocation?.DestinationLocationId,
            ShipmentId = await ResolveDispatchShipmentIdAsync(dispatch),
            SourcePurchaseContractId = sourcePurchaseContract.Id,
            TruckDispatchId = dispatch.Id,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = normalizedInvoice,
            SaleDate = model.SaleDate.Date,
            QuantityMt = model.QuantityMt,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = normalizedNotes,
            TicketSerialNumber = dispatch.TicketSerialNumber
        };
        if (saleSourcePlan.Shares.Count > 0)
        {
            _sourceAllocations.ApplyLegacyHeader(sale, saleSourcePlan);
        }

        if (model.IsSalePricePending)
        {
            SalePendingPricing.MarkPending(sale, model.Currency);
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            _db.SalesTransactions.Add(sale);
            await _db.SaveChangesAsync();
            await _sourceAllocations.PersistSaleAsync(
                sale,
                saleSourcePlan,
                currentTransportLegId);

            // TruckDispatch.SalesTransactionId یکتاست و فقط «اولین فروشِ فعالِ موتر» را نگه می‌دارد
            // (سازگاری عقب‌رو با گزارش‌ها و ویوهای موجود). فروش‌های قسمتیِ بعدی از راه
            // SalesTransaction.TruckDispatchId ردیابی می‌شوند.
            dispatch.SalesTransactionId ??= sale.Id;

            var ledgerEntry = SalePendingPricing.ShouldPostLedger(sale)
                ? Ledger.Post(SaleLedgerFactory.BuildSaleLedgerEntry(
                    sale,
                    conversion,
                    contractId: saleSourcePlan.Shares.Count > 0
                        ? saleSourcePlan.SingleContractId
                        : sourcePurchaseContract.Id))
                : null;
            await _db.SaveChangesAsync();

            // مرحله ۷ — Dual-write داخل همان Transaction قدیمی.
            if (_salesAccounting is not null)
            {
                await _salesAccounting.TryPostSaleAsync(sale);
                await _salesAccounting.TryPostCogsAsync(sale);
            }

            await _audit.LogAsync(
                nameof(SalesTransaction),
                sale.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("Source", usesAllocation ? "DirectDispatchToTruck allocation" : "TruckDispatch purchase contract"),
                    ("TruckDispatchId", dispatch.Id),
                    ("ContractId", sale.ContractId),
                    ("SourcePurchaseContractId", sale.SourcePurchaseContractId),
                    ("ShipmentId", sale.ShipmentId),
                    ("CompanyId", sale.CompanyId),
                    ("CustomerId", sale.CustomerId),
                    ("SupplierId", sale.SupplierId),
                    ("ProductId", sale.ProductId),
                    ("SaleStage", sale.SaleStage),
                    ("InvoiceNumber", sale.InvoiceNumber),
                    ("SaleDate", sale.SaleDate),
                    ("QuantityMt", sale.QuantityMt),
                    ("Currency", sale.Currency),
                    ("UnitPriceUsd", sale.UnitPriceUsd),
                    ("TotalUsd", sale.TotalUsd),
                    ("NoInventoryMovement", true)));

            await _audit.LogAsync(
                nameof(TruckDispatch),
                dispatch.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("SalesTransactionId", null, dispatch.SalesTransactionId)));

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

        TempData["ok"] = "فروش موتر بدون حرکت موجودی ثبت شد (موجودی هنگام بارگیری قبلاً کم شده).";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = dispatch.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        DispatchCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!HasFieldError(nameof(model.ContractId)) && model.ContractId > 0)
        {
            var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
            else if (contract.ContractType != ContractType.Purchase)
            {
                // Stock-out movements (InventoryMovement.ContractId) must always trace back to a
                // Purchase contract. A Sales contract here would corrupt per-contract stock and P&L.
                ModelState.AddModelError(
                    nameof(model.ContractId),
                    "برای دیسپچ از موجودی، فقط قرارداد خرید (Purchase) قابل انتخاب است.");
            }
            else if (contract.ProductId != model.ProductId)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با کالای انتخابی سازگار نیست.");
            }
        }

        if (!HasFieldError(nameof(model.ProductId)) && model.ProductId > 0)
        {
            var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ProductId && p.IsActive);
            if (product is null)
            {
                ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
            }
        }

        model.TruckPlateNumberInput = NormalizeTruckPlateNumber(model.TruckPlateNumberInput);
        model.DriverNameInput = NormalizeDriverNameInput(model.DriverNameInput);
        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);
        var transport = await ResolveTransportAsync(
            model.TruckId,
            model.DriverId,
            model.TruckPlateNumberInput,
            model.DriverNameInput,
            nameof(model.TruckId),
            nameof(model.TruckPlateNumberInput),
            nameof(model.DriverId),
            nameof(model.DriverNameInput));

        if (!HasFieldError(nameof(model.SourceTerminalId)) && model.SourceTerminalId > 0)
        {
            var terminal = await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.SourceTerminalId && t.IsActive);
            if (terminal is null)
            {
                ModelState.AddModelError(nameof(model.SourceTerminalId), "ترمینال مبدا معتبر نیست.");
            }
        }

        if (model.SourceStorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.SourceStorageTankId.Value);
            if (tank is null)
            {
                ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن مبدا معتبر نیست.");
            }
            else
            {
                if (tank.TerminalId != model.SourceTerminalId)
                {
                    ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن انتخاب‌شده با ترمینال مبدا سازگار نیست.");
                }

                if (tank.ProductId.HasValue && tank.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن انتخاب‌شده با کالای انتخابی سازگار نیست.");
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

        // وزن تخلیه از ترازوی مقصد می‌آید و می‌تواند از وزن بارگیری بیشتر باشد (اضافه‌وزن ترازو).

        await ValidateOperationalPartyAsync(
            model.ServiceProviderId,
            model.OperationalAssetId,
            nameof(model.ServiceProviderId),
            nameof(model.OperationalAssetId),
            model.TruckId);

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        var provisionalMovement = new InventoryMovement
        {
            ProductId = model.ProductId,
            ContractId = model.ContractId,
            TerminalId = model.SourceTerminalId,
            StorageTankId = model.SourceStorageTankId,
            Direction = MovementDirection.Out,
            MovementDate = model.DispatchDate,
            QuantityMt = model.LoadedQuantityMt,
            Notes = model.Notes
        };

        try
        {
            await _stock.EnsureSufficientStockForMovementAsync(provisionalMovement);

            IDbContextTransaction? transaction = null;
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync();
            }

            try
            {
                var lockedContract = await LockSourceContractAsync(model.ContractId);
                if (lockedContract is null)
                {
                    throw new BusinessRuleException(
                        "DISPATCH_CONTRACT_NOT_FOUND",
                        "قرارداد منبع موجودی انتخابشده دیگر معتبر نیست.");
                }

                if (lockedContract.ProductId != model.ProductId)
                {
                    throw new BusinessRuleException(
                        "DISPATCH_CONTRACT_PRODUCT_MISMATCH",
                        "قرارداد منبع موجودی دیگر با کالای انتخابشده همخوان نیست.");
                }

                if (model.SourceStorageTankId.HasValue)
                {
                    var lockedTank = await LockSourceStorageTankAsync(model.SourceStorageTankId.Value);
                    if (lockedTank is null)
                    {
                        throw new BusinessRuleException(
                            "DISPATCH_STORAGE_TANK_NOT_FOUND",
                            "مخزن مبدا انتخابشده دیگر معتبر نیست.");
                    }

                    if (lockedTank.TerminalId != model.SourceTerminalId)
                    {
                        throw new BusinessRuleException(
                            "DISPATCH_STORAGE_TANK_TERMINAL_MISMATCH",
                            "مخزن انتخابشده دیگر با ترمینال مبدا سازگار نیست.");
                    }

                    if (lockedTank.ProductId.HasValue && lockedTank.ProductId != model.ProductId)
                    {
                        throw new BusinessRuleException(
                            "DISPATCH_STORAGE_TANK_PRODUCT_MISMATCH",
                            "مخزن انتخابشده دیگر با کالای انتخابشده سازگار نیست.");
                    }
                }

                await _stock.EnsureSufficientStockForMovementAsync(provisionalMovement);
                await _stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(provisionalMovement);

                var dispatch = new TruckDispatch
                {
                    DispatchMode = TruckDispatchMode.FromInventory,
                    ContractId = model.ContractId,
                    ProductId = model.ProductId,
                    TruckId = transport!.CreatedTruck is null ? transport.TruckId : 0,
                    Truck = transport.CreatedTruck,
                    DriverId = transport.CreatedDriver is null ? transport.DriverId : null,
                    Driver = transport.CreatedDriver,
                    DestinationLocationId = model.DestinationLocationId,
                    ServiceProviderId = model.ServiceProviderId,
                    OperationalAssetId = model.OperationalAssetId,
                    DispatchDate = model.DispatchDate,
                    Status = DispatchStatus.Loaded,
                    LoadedQuantityMt = model.LoadedQuantityMt,
                    DischargedQuantityMt = model.DischargedQuantityMt,
                    AllowanceMt = model.AllowanceMt,
                    ShortageMt = model.ShortageMt,
                    FreightCostUsd = model.FreightCostUsd,
                    ShortageRateUsd = model.ShortageRateUsd,
                    FreightPayableUsd = ComputeFreightPayable(model.FreightCostUsd, model.ShortageMt, model.AllowanceMt, model.ShortageRateUsd, model.ToleranceMt, model.ChargeableShortageMt),
                    TicketSerialNumber = string.IsNullOrWhiteSpace(model.TicketSerialNumber) ? null : model.TicketSerialNumber.Trim(),
                    ToleranceMt = model.ToleranceMt,
                    ChargeableShortageMt = model.ChargeableShortageMt,
                    Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
                };
                var carrierParty = await new AssetUsageChargeService(_db).ResolveCarrierPartyAsync(
                    dispatch.ServiceProviderId,
                    dispatch.DriverId,
                    dispatch.OperationalAssetId,
                    dispatch.DispatchDate);
                dispatch.CarrierPartyType = carrierParty?.PartyType;
                dispatch.CarrierPartyId = carrierParty?.PartyId;

                // PTG-P0-01 — توکن با همان SaveChanges و همان Transaction مصرف می‌شود.
                _formTokens.Stamp(formToken, "Dispatch.Create", nameof(TruckDispatch));

                _db.TruckDispatches.Add(dispatch);
                await _db.SaveChangesAsync();
                await new AssetUsageChargeService(_db).SyncOperationAsync(dispatch);

                // موجودی از قبل روی provisionalMovement چک و قرارداد مبدأ قفل شده است؛ پس
                // Writer اینجا فقط سند را می‌سازد و چک را دوباره اجرا نمی‌کند.
                var stockOutMovement = await _movements.PostOutboundAsync(
                    new InventoryMovementRequest
                    {
                        ProductId = model.ProductId,
                        ContractId = model.ContractId,
                        TerminalId = model.SourceTerminalId,
                        StorageTankId = model.SourceStorageTankId,
                        MovementDate = model.DispatchDate,
                        QuantityMt = model.LoadedQuantityMt,
                        ReferenceDocument = $"TRUCK-DISPATCH:{dispatch.Id}",
                        Notes = string.IsNullOrWhiteSpace(model.ReferenceDocument)
                            ? dispatch.Notes
                            : $"Ref={model.ReferenceDocument.Trim()} | {dispatch.Notes}".TrimEnd(' ', '|')
                    },
                    StockGuard.None);

                await SyncDispatchFreightExpenseAsync(dispatch);

                var dispatchLossSubmission = BuildDispatchLossSubmission(dispatch, model);
                var hasDispatchLoss = dispatchLossSubmission is not null;
                if (dispatchLossSubmission is not null)
                {
                    await _lossWorkflow.CreateAsync(dispatchLossSubmission);
                }

                await LogCreatedTransportAsync(transport);
                await _audit.LogAndSaveAsync(
                    nameof(TruckDispatch),
                    dispatch.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ContractId", dispatch.ContractId),
                        ("ProductId", dispatch.ProductId),
                        ("TruckId", dispatch.TruckId),
                        ("DriverId", dispatch.DriverId),
                        ("DestinationLocationId", dispatch.DestinationLocationId),
                        ("DispatchDate", dispatch.DispatchDate),
                        ("LoadedQuantityMt", dispatch.LoadedQuantityMt),
                        ("SourceTerminalId", model.SourceTerminalId),
                        ("SourceStorageTankId", model.SourceStorageTankId),
                        ("InventoryReference", stockOutMovement.ReferenceDocument),
                        ("ReferenceDocument", model.ReferenceDocument),
                        ("FreightCostUsd", dispatch.FreightCostUsd),
                        ("ShortageMt", dispatch.ShortageMt)));

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                TempData["ok"] = hasDispatchLoss
                    ? "دیسپچ موتر و ضایعات این مرحله با موفقیت ثبت شد."
                    : "دیسپچ موتر با موفقیت ثبت شد.";

                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return Redirect(localReturnUrl);
                }

                return RedirectToAction(nameof(Details), new { id = dispatch.Id });
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
            TempData["err"] = "این دیسپچ قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create truck dispatch.");
            ModelState.AddModelError(string.Empty, "ثبت دیسپچ انجام نشد. خطای غیرمنتظره رخ داد؛ لطفاً اطلاعات واردشده را بررسی کرده و دوباره تلاش کنید.");
        }

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NormalizePositiveInt(int? value)
        => value.HasValue && value.Value > 0 ? value.Value : null;

    private async Task ValidateOperationalPartyAsync(
        int? serviceProviderId,
        int? operationalAssetId,
        string serviceProviderField,
        string operationalAssetField,
        int? truckId = null)
    {
        if (serviceProviderId.HasValue && operationalAssetId.HasValue)
        {
            ModelState.AddModelError(operationalAssetField, "Select either a service provider or an operational asset, not both.");
        }

        if (serviceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == serviceProviderId.Value && p.IsActive))
        {
            ModelState.AddModelError(serviceProviderField, "Service provider selection is invalid.");
        }

        if (operationalAssetId.HasValue
            && !await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == operationalAssetId.Value && a.IsActive))
        {
            ModelState.AddModelError(operationalAssetField, "Operational asset selection is invalid.");
        }

        // دارایی عملیاتی که به یک موتر مشخص وصل است نباید روی سندی بنشیند که موتر دیگری دارد؛
        // همان کنترلی که مسیر گروهی حمل از قبل انجام می‌دهد. مقدار خودکار اصلاح نمی‌شود.
        if (operationalAssetId.HasValue && truckId is > 0)
        {
            var asset = await _db.OperationalAssets
                .AsNoTracking()
                .Where(a => a.Id == operationalAssetId.Value)
                .Select(a => new
                {
                    a.Name,
                    a.LinkedTruckId,
                    LinkedPlateNumber = a.LinkedTruck != null ? a.LinkedTruck.PlateNumber : null
                })
                .FirstOrDefaultAsync();

            if (asset?.LinkedTruckId is int linkedTruckId && linkedTruckId != truckId.Value)
            {
                var documentPlateNumber = await _db.Trucks
                    .AsNoTracking()
                    .Where(t => t.Id == truckId.Value)
                    .Select(t => t.PlateNumber)
                    .FirstOrDefaultAsync();

                ModelState.AddModelError(
                    operationalAssetField,
                    $"دارایی «{asset.Name}» به موتر {asset.LinkedPlateNumber ?? "-"} وصل است، اما موتر این سند {documentPlateNumber ?? "-"} است. یا موتر سند را تغییر دهید یا دارایی دیگری انتخاب کنید.");
            }
        }
    }

    // اگر این ارسال موتر از «رسید حملِ موجودی» آمده باشد، حمل (leg) و محمولهٔ متصل را از زنجیرهٔ
    // dispatch → رسید حمل → leg پیدا می‌کنیم تا مصرفِ ثبت‌شده مثل گمرک به همان حمل/پرونده محموله وصل شود.
    private async Task<(int? TransportLegId, int? ShipmentId)> ResolveDispatchShipmentLinkAsync(TruckDispatch dispatch)
    {
        if (!dispatch.InventoryTransportReceiptId.HasValue)
        {
            return (null, null);
        }

        var link = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => r.Id == dispatch.InventoryTransportReceiptId.Value)
            .Select(r => new
            {
                r.InventoryTransportLegId,
                ShipmentId = r.InventoryTransportLeg != null ? r.InventoryTransportLeg.ShipmentId : null
            })
            .FirstOrDefaultAsync();

        return link is null ? (null, null) : (link.InventoryTransportLegId, link.ShipmentId);
    }

    // بدنهٔ اصلی به Services/DispatchFreightExpenseSync منتقل شد تا فرم جدید
    // «رسید/تسویه/تخلیه وسایط» همان رکوردهای مالی را بسازد (بدون منطق موازی).
    private Task SyncDispatchFreightExpenseAsync(TruckDispatch dispatch)
        => DispatchFreightExpenseSync.SyncAsync(_db, dispatch, _expenseAccounting);

    private Task CancelDispatchFreightExpenseAsync(ExpenseTransaction expense)
        => DispatchFreightExpenseSync.CancelExpenseAsync(_db, expense, _expenseAccounting);

    private Task CancelDispatchFreightExpenseAsync(int dispatchId)
        => DispatchFreightExpenseSync.CancelByDispatchIdAsync(_db, dispatchId, _expenseAccounting);

    private static decimal GetFreightExpenseAmountUsd(decimal? payableUsd, decimal? grossUsd)
        => DispatchFreightExpenseSync.GetFreightExpenseAmountUsd(payableUsd, grossUsd);

    private static bool QuantitiesMatch(decimal left, decimal right)
        => decimal.Round(left, 4, MidpointRounding.AwayFromZero)
            == decimal.Round(right, 4, MidpointRounding.AwayFromZero);

    // ── مودال «ثبت مصرف / کرایه» ارسال موتر (هم‌شکلِ مودال مصارف «حمل از موجودی») ─────────
    // GET: فرم چندردیفی را به‌صورت partial برای بارگذاری داخل مودال برمی‌گرداند (AJAX remote).
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateExpenseModal(int id, string? returnUrl = null)
    {
        var dispatch = await _db.TruckDispatches.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (dispatch is null)
        {
            return NotFound();
        }

        var model = new DispatchExpenseModalViewModel
        {
            TruckDispatchId = dispatch.Id,
            TransportReference = $"DSP-{dispatch.Id:0000}",
            LoadedQuantityMt = dispatch.LoadedQuantityMt,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true ? returnUrl : null,
            Lines = [new InventoryTransportGroupExpenseModalRow()]
        };

        await PopulateExpenseModalLookupsAsync();
        model.ExistingExpenses = await LoadDispatchExpenseItemsAsync(dispatch.Id);
        ViewData["IsExpenseModal"] = true;
        ViewData["CancelUrl"] = model.ReturnUrl;
        return PartialView("_DispatchExpenseEditor", model);
    }

    // POST: هر ردیف با همان الگوی موجودِ کرایهٔ ارسال، به ExpenseTransaction + LedgerEntry تبدیل می‌شود.
    // هیچ منطق Ledger جدیدی ساخته نمی‌شود؛ فقط همان الگو در یک حلقه بازاستفاده می‌شود.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveExpenses(DispatchExpenseModalViewModel model)
    {
        var request = HttpContext?.Request;
        var isAjax = request is not null
            && request.Headers.TryGetValue("X-Requested-With", out var xrw)
            && string.Equals(xrw.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        var dispatch = await _db.TruckDispatches.AsNoTracking().FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId);
        if (dispatch is null)
        {
            ModelState.AddModelError(nameof(model.TruckDispatchId), "ارسال موتر انتخاب‌شده یافت نشد.");
        }

        model.Lines = (model.Lines ?? []).Where(DispatchRowHasValue).ToList();
        if (model.Lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک ردیف مصرف با مبلغ لازم است.");
        }

        var prepared = new List<(InventoryTransportGroupExpenseModalRow Row, ExpenseType Type, PTGOilSystem.Web.Models.Entities.ServiceProvider? Provider, OperationalAsset? Asset)>();
        for (var i = 0; i < model.Lines.Count; i++)
        {
            var row = model.Lines[i];
            var prefix = $"Lines[{i}].";
            var manualName = ValidateManualExpenseTypeName(row.ManualExpenseTypeName, prefix + nameof(row.ManualExpenseTypeName));
            row.ManualExpenseTypeName = manualName;

            if (row.AmountUsd <= 0m)
            {
                ModelState.AddModelError(prefix + nameof(row.AmountUsd), "مبلغ مصرف باید بزرگ‌تر از صفر باشد.");
            }

            ExpenseType? type = null;
            if (row.ExpenseTypeId.HasValue)
            {
                type = await _db.ExpenseTypes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == row.ExpenseTypeId.Value && e.IsActive);
                if (type is null)
                {
                    ModelState.AddModelError(prefix + nameof(row.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(manualName))
            {
                type = await FindExpenseTypeByManualNameAsync(manualName)
                    ?? new ExpenseType
                    {
                        Name = manualName,
                        NamePersian = manualName,
                        Category = "Transport",
                        IsActive = true
                    };
            }
            else
            {
                ModelState.AddModelError(prefix + nameof(row.ExpenseTypeId), "نوع مصرف را انتخاب یا وارد کنید.");
            }

            PTGOilSystem.Web.Models.Entities.ServiceProvider? provider = null;
            OperationalAsset? asset = null;
            if (row.PartyType == LoadingExpensePartyType.ServiceProvider)
            {
                if (!row.ServiceProviderId.HasValue)
                {
                    ModelState.AddModelError(prefix + nameof(row.ServiceProviderId), "شرکت خدماتی را انتخاب کنید.");
                }
                else
                {
                    provider = await _db.ServiceProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == row.ServiceProviderId.Value && p.IsActive);
                    if (provider is null)
                    {
                        ModelState.AddModelError(prefix + nameof(row.ServiceProviderId), "شرکت خدماتی معتبر یا فعال نیست.");
                    }
                }
            }
            else if (row.PartyType == LoadingExpensePartyType.OperationalAsset)
            {
                if (!row.OperationalAssetId.HasValue)
                {
                    ModelState.AddModelError(prefix + nameof(row.OperationalAssetId), "دارایی عملیاتی را انتخاب کنید.");
                }
                else
                {
                    asset = await _db.OperationalAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == row.OperationalAssetId.Value && a.IsActive);
                    if (asset is null)
                    {
                        ModelState.AddModelError(prefix + nameof(row.OperationalAssetId), "دارایی عملیاتی معتبر یا فعال نیست.");
                    }
                }
            }

            if (type is not null)
            {
                prepared.Add((row, type, provider, asset));
            }
        }

        if (!ModelState.IsValid || dispatch is null)
        {
            await PopulateExpenseModalLookupsAsync();
            model.ExistingExpenses = dispatch is null ? [] : await LoadDispatchExpenseItemsAsync(dispatch.Id);
            model.TransportReference = $"DSP-{model.TruckDispatchId:0000}";
            ViewData["IsExpenseModal"] = true;
            ViewData["CancelUrl"] = model.ReturnUrl;
            if (isAjax) { Response.StatusCode = 400; }
            return PartialView("_DispatchExpenseEditor", model);
        }

        // حمل/محمولهٔ متصل به این ارسال را یک‌بار پیدا می‌کنیم تا هر مصرف مثل گمرک به پرونده محموله وصل شود.
        var (transportLegId, shipmentId) = await ResolveDispatchShipmentLinkAsync(dispatch);

        var createdCount = 0;
        IDbContextTransaction? transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            foreach (var (row, type, provider, asset) in prepared)
            {
                var expenseType = type;
                if (expenseType.Id == 0)
                {
                    expenseType = new ExpenseType
                    {
                        Code = await BuildManualExpenseTypeCodeAsync(row.ManualExpenseTypeName!.Trim()),
                        Name = row.ManualExpenseTypeName!.Trim(),
                        NamePersian = row.ManualExpenseTypeName!.Trim(),
                        Category = "Transport",
                        IsActive = true
                    };
                    _db.ExpenseTypes.Add(expenseType);
                    await _db.SaveChangesAsync();
                }

                var amountUsd = decimal.Round(row.AmountUsd, 4, MidpointRounding.AwayFromZero);
                var description = string.IsNullOrWhiteSpace(row.Notes)
                    ? $"مصرف ارسال موتر | Dispatch: #{dispatch.Id}"
                    : $"{row.Notes!.Trim()} | Dispatch: #{dispatch.Id}";
                if (description.Length > 1000) { description = description[..1000]; }

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ContractId = dispatch.ContractId,
                    TruckDispatchId = dispatch.Id,
                    TransportLegId = transportLegId,
                    ShipmentId = shipmentId,
                    ServiceProviderId = provider?.Id,
                    OperationalAssetId = asset?.Id,
                    ExpenseDate = dispatch.DispatchDate.Date,
                    Amount = amountUsd,
                    Currency = SystemCurrency.BaseCurrencyCode,
                    AppliedFxRateToUsd = 1m,
                    AmountUsd = amountUsd,
                    Description = description
                };

                // PTG-P1-04 — هویت تسویه از همان انتخابِ سطر می‌آید: شرکت خدماتی ⇒ بدهی به او؛
                // موترِ ملکی (دارایی عملیاتی) یا بدونِ طرف ⇒ نه بدهیِ بیرونی، نه حرکتِ پول.
                ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);
                _settlementValidator.Validate(expense);

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
                if (_expenseAccounting is not null)
                {
                    await _expenseAccounting.TryPostExpenseAsync(expense);
                }

                var ledgerEntry = ExpenseLedger.Post(new ExpenseLedgerRequest
                {
                    Expense = expense,
                    Description = $"ثبت هزینه {(expenseType.NamePersian ?? expenseType.Name)}",
                    Reference = $"TRUCK-DISPATCH:{expense.TruckDispatchId}-{expense.Id}",
                    FxRateSource = "Base currency"
                });
                await _db.SaveChangesAsync();

                await _audit.LogAndSaveAsync(
                    nameof(ExpenseTransaction),
                    expense.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ExpenseTypeId", expense.ExpenseTypeId),
                        ("ContractId", expense.ContractId),
                        ("TruckDispatchId", expense.TruckDispatchId),
                        ("ServiceProviderId", expense.ServiceProviderId),
                        ("OperationalAssetId", expense.OperationalAssetId),
                        ("AmountUsd", expense.AmountUsd),
                        ("LedgerReference", ledgerEntry.Reference)));

                createdCount++;
            }

            if (transaction is not null) { await transaction.CommitAsync(); }
        }
        catch (Exception ex)
        {
            if (transaction is not null) { await transaction.RollbackAsync(); }
            _logger.LogError(ex, "Failed to save truck dispatch expenses for dispatch #{DispatchId}.", model.TruckDispatchId);
            ModelState.AddModelError(string.Empty, "ثبت مصارف ذخیره نشد. مقدارها را بررسی کنید و دوباره تلاش کنید.");
            await PopulateExpenseModalLookupsAsync();
            model.ExistingExpenses = await LoadDispatchExpenseItemsAsync(model.TruckDispatchId);
            model.TransportReference = $"DSP-{model.TruckDispatchId:0000}";
            ViewData["IsExpenseModal"] = true;
            ViewData["CancelUrl"] = model.ReturnUrl;
            if (isAjax) { Response.StatusCode = 400; }
            return PartialView("_DispatchExpenseEditor", model);
        }
        finally
        {
            if (transaction is not null) { await transaction.DisposeAsync(); }
        }

        var redirectUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var local)
            ? local
            : Url?.Action(nameof(Details), new { id = model.TruckDispatchId }) ?? $"/Dispatch/Details/{model.TruckDispatchId}";

        TempData["ok"] = $"{createdCount:N0} رکورد مصرف برای این ارسال ثبت شد.";

        if (isAjax) { return Json(new { success = true, redirectUrl }); }
        return Redirect(redirectUrl);
    }

    private static bool DispatchRowHasValue(InventoryTransportGroupExpenseModalRow row)
        => row.ExpenseTypeId.HasValue
            || !string.IsNullOrWhiteSpace(row.ManualExpenseTypeName)
            || row.AmountUsd != 0m
            || row.QuantityMt.HasValue
            || row.UnitRateUsd.HasValue
            || !string.IsNullOrWhiteSpace(row.Notes);

    private string? ValidateManualExpenseTypeName(string? value, string modelKey)
    {
        var normalized = NormalizeNullable(value);
        if (normalized is null) { return null; }
        if (normalized.Length > 200)
        {
            ModelState.AddModelError(modelKey, "نوع مصرف دستی نمی‌تواند بیشتر از 200 کرکتر باشد.");
        }
        if (normalized.Any(char.IsControl))
        {
            ModelState.AddModelError(modelKey, "نوع مصرف دستی دارای کاراکتر نامعتبر است.");
        }
        return normalized;
    }

    private async Task<ExpenseType?> FindExpenseTypeByManualNameAsync(string manualExpenseTypeName)
    {
        if (string.IsNullOrWhiteSpace(manualExpenseTypeName)) { return null; }
        var normalizedName = manualExpenseTypeName.Trim();
        return await _db.ExpenseTypes.AsNoTracking()
            .FirstOrDefaultAsync(e => e.IsActive && (e.Name == normalizedName || e.NamePersian == normalizedName));
    }

    private async Task<string> BuildManualExpenseTypeCodeAsync(string manualExpenseTypeName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manualExpenseTypeName))).Substring(0, 8);
        var prefix = $"MAN-{hash}";
        var candidate = prefix;
        var suffix = 2;
        while (await _db.ExpenseTypes.AsNoTracking().AnyAsync(e => e.Code == candidate))
        {
            candidate = $"{prefix}-{suffix++}";
        }
        return candidate;
    }

    private async Task PopulateExpenseModalLookupsAsync()
    {
        ViewBag.ExpenseTypes = new SelectList(
            await _db.ExpenseTypes.AsNoTracking()
                .Where(e => e.IsActive)
                .OrderBy(e => e.Category).ThenBy(e => e.Code)
                .Select(e => new { e.Id, Text = e.Code + " - " + (e.NamePersian ?? e.Name) })
                .ToListAsync(),
            "Id", "Text");
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name })
                .ToListAsync(),
            "Id", "Text");
        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets.AsNoTracking()
                .Where(a => a.IsActive)
                .OrderBy(a => a.AssetCode).ThenBy(a => a.Name)
                .Select(a => new { a.Id, Text = a.AssetCode + " - " + a.Name })
                .ToListAsync(),
            "Id", "Text");
    }

    private async Task<IReadOnlyList<InventoryTransportFlowExpenseItemViewModel>> LoadDispatchExpenseItemsAsync(int dispatchId)
    {
        return await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => e.TruckDispatchId == dispatchId && !e.IsCancelled)
            .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Id)
            .Select(e => new InventoryTransportFlowExpenseItemViewModel
            {
                Id = e.Id,
                ContractId = e.ContractId,
                ContractNumber = e.Contract != null ? e.Contract.ContractNumber : "",
                ExpenseDate = e.ExpenseDate,
                ExpenseTypeName = e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : "",
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.Name : null,
                Amount = e.Amount,
                Currency = e.Currency,
                AmountUsd = e.AmountUsd,
                Description = e.Description
            })
            .ToListAsync();
    }

    private static decimal ComputeChargeableShortage(decimal shortageMt, decimal allowanceMt)
        => FreightShortageMath.ChargeableShortage(shortageMt, allowanceMt);

    private static decimal ComputeShortageChargeUsd(decimal chargeableShortageMt, decimal? shortageRateUsd)
        => FreightShortageMath.ShortageChargeUsd(chargeableShortageMt, shortageRateUsd);

    private static string BuildDriverFinancialTreatment(decimal driverShortageChargeUsd, decimal? freightPayableUsd)
    {
        if (driverShortageChargeUsd <= 0m)
        {
            return "No driver shortage charge.";
        }

        var freightText = freightPayableUsd.HasValue
            ? $" Net freight payable: {freightPayableUsd.Value:N2} USD."
            : string.Empty;
        return $"Driver shortage charge/deduction: {driverShortageChargeUsd:N2} USD.{freightText}";
    }

    private static string BuildTruckUnloadInventoryNotes(int dispatchId, string? documentReference, string? notes)
    {
        var parts = new List<string> { $"Truck unload from DispatchId={dispatchId}" };
        if (!string.IsNullOrWhiteSpace(documentReference))
        {
            parts.Add($"Doc={documentReference.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            parts.Add(notes.Trim());
        }

        var result = string.Join(" | ", parts);
        return result.Length <= 1000 ? result : result[..1000];
    }

    private static decimal? ComputeFreightPayable(
        decimal? freightCostUsd,
        decimal? shortageMt,
        decimal? allowanceMt,
        decimal? shortageRateUsd,
        decimal? toleranceMt = null,
        decimal? chargeableShortageMtOverride = null)
        => FreightShortageMath.FreightPayableUsd(
            freightCostUsd,
            shortageMt,
            allowanceMt,
            shortageRateUsd,
            toleranceMt,
            chargeableShortageMtOverride);

    private static LossEventSubmission? BuildDispatchLossSubmission(
        TruckDispatch dispatch,
        DispatchCreateViewModel model)
    {
        // تفاوت علامت‌دار: مثبت = کسری، منفی = اضافه‌بار. هر دو رکورد قابل ردیابی می‌سازند.
        var lossQuantityMt = model.ShortageMt
            ?? (model.DischargedQuantityMt.HasValue
                ? TransportVarianceMath.Difference(model.LoadedQuantityMt, model.DischargedQuantityMt.Value)
                : 0m);

        if (!TransportVarianceMath.HasVariance(lossQuantityMt))
        {
            return null;
        }

        var actualQuantityMt = model.DischargedQuantityMt
            ?? Math.Max(model.LoadedQuantityMt - lossQuantityMt, 0m);

        return new LossEventSubmission
        {
            Stage = LossEventStage.DispatchShortage,
            ProductId = dispatch.ProductId,
            ContractId = dispatch.ContractId,
            TruckDispatchId = dispatch.Id,
            TerminalId = model.SourceTerminalId,
            StorageTankId = model.SourceStorageTankId,
            EventDate = dispatch.DispatchDate,
            ExpectedQuantityMt = dispatch.LoadedQuantityMt,
            ActualQuantityMt = actualQuantityMt,
            ToleranceQuantityMt = model.AllowanceMt ?? 0m,
            AffectsInventory = false,
            Reference = model.ReferenceDocument,
            Notes = dispatch.Notes
        };
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var dispatch = await _db.TruckDispatches
            .Include(d => d.Contract)
            .Include(d => d.Product)
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.DestinationLocation)
            .Include(d => d.SalesTransaction)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id);

        if (dispatch is null) return NotFound();

        var linkedMovement = await _db.InventoryMovements
            .Include(m => m.Terminal)
            .Include(m => m.StorageTank)
            .AsNoTracking()
            .Where(m => m.ReferenceDocument == $"TRUCK-DISPATCH:{id}")
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync();

        var deliveryMovement = await FindTruckUnloadInventoryMovementAsync(id);

        var deliveryReceipts = await _db.DeliveryReceipts
            .AsNoTracking()
            .Where(r => r.TruckDispatchId == id)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new DispatchDeliveryReceiptItemViewModel
            {
                Id = r.Id,
                ReceiptDate = r.ReceiptDate,
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                ReceivedBy = r.ReceivedBy,
                DocumentReference = r.DocumentReference
            })
            .ToListAsync();

        var customs = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => c.TruckDispatchId == id)
            .OrderByDescending(c => c.DeclarationDate)
            .ThenByDescending(c => c.Id)
            .Select(c => new DispatchCustomsItemViewModel
            {
                Id = c.Id,
                DeclarationDate = c.DeclarationDate,
                DeclarationReference = c.DeclarationReference,
                WagonOrTruckNumber = c.WagonOrTruckNumber,
                TotalAfn = c.TotalAfn,
                TotalUsd = c.TotalUsd
            })
            .ToListAsync();

        // مصرفی که از اظهارنامهٔ گمرکی ساخته شده در بخش «گمرکات» بالا آمده است؛ اگر اینجا
        // هم بیاید همان مبلغ دو بار دیده می‌شود (یک بار گمرک، یک بار مصارف عملیاتی).
        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.TruckDispatchId == id && !e.IsCancelled && !e.CustomsDeclarationId.HasValue)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Select(e => new DispatchExpenseItemViewModel
            {
                Id = e.Id,
                ExpenseDate = e.ExpenseDate,
                ExpenseTypeName = e.ExpenseType != null
                    ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name)
                    : "",
                Amount = e.Amount,
                Currency = e.Currency,
                AmountUsd = e.AmountUsd,
                Description = e.Description
            })
            .ToListAsync();

        LoadingReceiptAllocation? directAllocation = null;
        decimal? allocationTotalDirectDispatchedQuantityMt = null;
        decimal? allocationRemainingQuantityMt = null;
        if (dispatch.LoadingReceiptAllocationId.HasValue)
        {
            directAllocation = await BuildDirectFromReceiptAllocationQuery(asTracking: false)
                .FirstOrDefaultAsync(a => a.Id == dispatch.LoadingReceiptAllocationId.Value);

            if (directAllocation is not null)
            {
                allocationTotalDirectDispatchedQuantityMt = GetActiveDirectFromReceiptDispatchQuantity(directAllocation);
                allocationRemainingQuantityMt = Math.Max(directAllocation.QuantityMt - allocationTotalDirectDispatchedQuantityMt.Value, 0m);
            }
        }

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        var soldQuantityMt = await GetSoldQuantityMtAsync(dispatch);
        var remainingSellableQuantityMt = ResolveRemainingSellableQuantityMt(
            ResolveSellableQuantityMt(dispatch),
            soldQuantityMt);

        return View(new DispatchDetailsViewModel
        {
            Id = dispatch.Id,
            DispatchMode = dispatch.DispatchMode,
            LoadingReceiptAllocationId = dispatch.LoadingReceiptAllocationId,
            LoadingReceiptId = directAllocation?.LoadingReceiptId,
            LoadingRegisterId = directAllocation?.LoadingReceipt?.LoadingRegisterId,
            DispatchDate = dispatch.DispatchDate,
            ContractNumber = dispatch.Contract?.DisplayLabel ?? "",
            ProductName = dispatch.Product?.Name ?? "",
            TruckPlateNumber = dispatch.Truck?.PlateNumber ?? "",
            DriverName = dispatch.Driver?.FullName,
            DestinationName = dispatch.DestinationLocation?.Name,
            ServiceProviderId = dispatch.ServiceProviderId,
            ServiceProviderName = dispatch.ServiceProvider?.Name,
            OperationalAssetId = dispatch.OperationalAssetId,
            OperationalAssetName = dispatch.OperationalAsset?.Name,
            StatusName = dispatch.Status.ToString(),
            LoadedQuantityMt = dispatch.LoadedQuantityMt,
            DischargedQuantityMt = dispatch.DischargedQuantityMt,
            AllowanceMt = dispatch.AllowanceMt,
            ShortageMt = dispatch.ShortageMt,
            FreightCostUsd = dispatch.FreightCostUsd,
            ShortageRateUsd = dispatch.ShortageRateUsd,
            FreightPayableUsd = dispatch.FreightPayableUsd,
            PayableUsd = dispatch.PayableUsd,
            TicketSerialNumber = dispatch.TicketSerialNumber,
            ToleranceMt = dispatch.ToleranceMt,
            ChargeableShortageMt = dispatch.ChargeableShortageMt,
            Notes = dispatch.Notes,
            SourceTerminalName = dispatch.DispatchMode == TruckDispatchMode.DirectFromReceipt
                ? directAllocation?.Terminal?.Name
                : linkedMovement?.Terminal?.Name,
            SourceStorageTankCode = StorageTankDisplay.BuildOptional(linkedMovement?.StorageTank),
            InventoryReference = linkedMovement?.ReferenceDocument,
            SalesTransactionId = dispatch.SalesTransactionId,
            SaleInvoiceNumber = dispatch.SalesTransaction?.InvoiceNumber,
            SaleQuantityMt = dispatch.SalesTransaction?.QuantityMt,
            SaleTotalUsd = dispatch.SalesTransaction?.TotalUsd,
            SoldQuantityMt = soldQuantityMt,
            RemainingSellableQuantityMt = remainingSellableQuantityMt,
            AllocationQuantityMt = directAllocation?.QuantityMt,
            AllocationTotalDirectDispatchedQuantityMt = allocationTotalDirectDispatchedQuantityMt,
            AllocationRemainingQuantityMt = allocationRemainingQuantityMt,
            DriverShortageChargeUsd = dispatch.PayableUsd,
            DeliveryInventoryReference = deliveryMovement?.ReferenceDocument,
            DeliveryReceipts = deliveryReceipts,
            Customs = customs,
            Expenses = expenses,
            DeliveryReceivedTotalMt = deliveryReceipts.Sum(receipt => receipt.ReceivedQuantityMt),
            CustomsTotalUsd = customs.Sum(item => item.TotalUsd),
            ExpenseTotalUsd = expenses.Sum(item => item.AmountUsd)
        });
    }
}
