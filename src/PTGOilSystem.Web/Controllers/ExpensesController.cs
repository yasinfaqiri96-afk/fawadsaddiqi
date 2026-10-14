using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.FormDrafts;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Operations;
using PTGOilSystem.Web.Services.Ledger;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ExpensesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ILogger<ExpensesController> _logger;
    // مرحله ۵ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe: اگر تزریق
    // نشود یا خاموش باشد، مسیر قدیمی هیچ تغییری نمی‌کند.
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;
    // PTG-P0-01 — نگهبان ثبت دوباره. روی اینترنت ضعیف کاربر Refresh می‌کند و مرورگر همان POST
    // را دوباره می‌فرستد؛ قفلِ دکمه در مرورگر این حالت را نمی‌گیرد. توکن داخل همان Transaction
    // مصرف می‌شود، پس ارسال دوم به Unique Index می‌خورد و رد می‌شود.
    private readonly IFormTokenGuard _formTokens;
    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private readonly ILedgerPostingService _ledger;
    private readonly IExpenseLedgerPoster _expenseLedger;
    // PTG-P1-04 — تنها مالکِ قاعدهٔ «هر مصرف دقیقاً یک هویت تسویه دارد».
    private readonly IExpenseSettlementValidator _settlementValidator;
    private readonly GroupExpensePostingService _groupExpensePosting;
    private const int DefaultListLimit = 100;
    private const int LookupLimit = 200;
    private const string DefaultWagonRentExpenseName = "Wagon Rent";

    private readonly IAfghanistanBusinessClock _businessClock;

    [ActivatorUtilitiesConstructor]
    public ExpensesController(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ILogger<ExpensesController> logger,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        IAfghanistanBusinessClock? businessClock = null,
        IFormTokenGuard? formTokens = null,
        ILedgerPostingService? ledgerPosting = null,
        IExpenseLedgerPoster? expenseLedger = null,
        IExpenseSettlementValidator? settlementValidator = null)
    {
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _db = db;
        _currencyConversion = currencyConversion;
        _audit = audit;
        _logger = logger;
        _expenseAccounting = expenseAccounting;
        _formTokens = formTokens ?? new FormTokenGuard(db);
        _ledger = ledgerPosting ?? new LedgerPostingService(db);
        _expenseLedger = expenseLedger ?? new ExpenseLedgerPoster(_ledger);
        _settlementValidator = settlementValidator ?? new ExpenseSettlementValidator();
        _groupExpensePosting = new GroupExpensePostingService(db, _settlementValidator, _expenseLedger, audit, expenseAccounting);
    }

    public ExpensesController(
        ApplicationDbContext db,
        IAuditService audit,
        ILogger<ExpensesController> logger)
        : this(
            db,
            new CurrencyConversionService(new PricingService(db)),
            audit,
            logger)
    {
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

    /// <summary>
    /// PTG-P1-04 — هویت تسویه از فرم. انتخابِ صریحِ کاربر مقدم است و نبودِ انتخاب همان
    /// رفتارِ قبلی را نگه می‌دارد: تشخیص از طرف‌حسابِ خودِ سند.
    ///
    /// حالتِ «پرداخت‌شده» تنها چیزی است که فرم پیش از فاز ۱ نمی‌توانست بگوید، و حساب نقدی
    /// فقط در همین حالت روی سند می‌نشیند تا سندِ بدهی هرگز حسابِ نقدیِ ولگرد نداشته باشد.
    /// </summary>
    private static void ApplyFormSettlement(ExpenseTransaction expense, ExpenseCreateViewModel model)
    {
        switch (model.SettlementMode)
        {
            case ExpenseSettlementMode.PaidImmediately:
                expense.SettlementMode = ExpenseSettlementMode.PaidImmediately;
                expense.CounterpartyType = null;
                expense.CounterpartyId = null;
                expense.CashAccountId = model.CashAccountId;
                return;

            case ExpenseSettlementMode.NonCash:
                expense.SettlementMode = ExpenseSettlementMode.NonCash;
                expense.CounterpartyType = null;
                expense.CounterpartyId = null;
                expense.CashAccountId = null;
                return;

            // Payable و «نامشخص» هر دو یعنی طرف‌حساب مرجع است: همان قاعدهٔ واحدِ
            // ServiceProvider/Driver. اگر کاربر Payable گفت ولی طرف‌حسابی نبود،
            // اعتبارسنج جلوی ذخیره را می‌گیرد — اینجا حدسی زده نمی‌شود.
            default:
                ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);
                expense.CashAccountId = null;
                if (model.SettlementMode == ExpenseSettlementMode.Payable)
                {
                    expense.SettlementMode = ExpenseSettlementMode.Payable;
                }

                return;
        }
    }

    private async Task PopulateLookupsAsync(
        ExpenseCreateViewModel? createModel = null,
        ExpenseIndexFilterViewModel? filter = null)
    {
        // فیلترِ چندانتخابی فقط وقتی «انتخابِ جاریِ» فهرست‌های کمکی است که دقیقاً یک مقدار دارد.
        var selectedContractId = createModel?.ContractId ?? filter?.ContractId.Only();
        var selectedShipmentId = createModel?.ShipmentId ?? filter?.ShipmentId.Only();
        var selectedTruckDispatchId = createModel?.TruckDispatchId ?? filter?.TruckDispatchId.Only();
        var selectedTransportLegId = createModel?.TransportLegId ?? filter?.TransportLegId;
        var selectedServiceProviderId = createModel?.ServiceProviderId ?? filter?.ServiceProviderId.Only();
        var selectedOperationalAssetId = createModel?.OperationalAssetId ?? filter?.OperationalAssetId.Only();

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

        var expenseTypeOptions = await _db.ExpenseTypes
                .AsNoTracking()
                .Where(e => e.IsActive)
                .OrderBy(e => e.Category)
                .ThenBy(e => e.Code)
                .Select(e => new
                {
                    e.Id,
                    Name = e.NamePersian ?? e.Name,
                    DisplayName = (e.NamePersian ?? e.Name) + " (" + e.Code + ")"
                })
                .ToListAsync();

        ViewBag.ExpenseTypeNames = expenseTypeOptions.Select(e => e.Name).ToList();
        ViewBag.ExpenseTypes = new SelectList(
            expenseTypeOptions,
            "Id",
            "DisplayName",
            createModel?.ExpenseTypeId ?? filter?.ExpenseTypeId.Only());

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

        var shipments = await _db.Shipments
            .AsNoTracking()
            .OrderBy(s => selectedShipmentId.HasValue && s.Id == selectedShipmentId.Value ? 0 : 1)
            .ThenByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Take(LookupLimit)
            .Select(s => new { s.Id, s.ShipmentCode })
            .ToListAsync();

        ViewBag.Shipments = shipments
            .Select(s => new SelectListItem
            {
                Value = s.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(s.ShipmentCode)
                    ? $"Shipment #{s.Id}"
                    : s.ShipmentCode,
                Selected = (createModel?.ShipmentId ?? filter?.ShipmentId.Only()) == s.Id
            })
            .ToList();

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .OrderBy(d => selectedTruckDispatchId.HasValue && d.Id == selectedTruckDispatchId.Value ? 0 : 1)
            .ThenByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Take(LookupLimit)
            .Select(d => new
            {
                d.Id,
                d.DispatchDate,
                PlateNumber = d.Truck != null ? d.Truck.PlateNumber : null
            })
            .ToListAsync();

        ViewBag.TruckDispatches = dispatches
            .Select(d => new SelectListItem
            {
                Value = d.Id.ToString(),
                Text = $"#{d.Id} - {(d.PlateNumber ?? "بدون پلاک")} - {DateDisplay.Date(d.DispatchDate)}",

                Selected = (createModel?.TruckDispatchId ?? filter?.TruckDispatchId.Only()) == d.Id
            })
            .ToList();

        var transportLegs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .OrderBy(l => selectedTransportLegId.HasValue && l.Id == selectedTransportLegId.Value ? 0 : 1)
            .ThenByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Take(LookupLimit)
            .Select(l => new
            {
                l.Id,
                l.LoadedDate,
                l.WagonNumber,
                l.RwbNo,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null,
                ProductName = l.Product != null ? l.Product.Name : null
            })
            .ToListAsync();

        ViewBag.TransportLegs = transportLegs
            .Select(l => new SelectListItem
            {
                Value = l.Id.ToString(),
                Text = $"#{l.Id} - {(l.WagonNumber ?? l.RwbNo ?? "Transport leg")} - {DateDisplay.Date(l.LoadedDate)} - {l.ContractNumber} - {l.ProductName}",
                Selected = selectedTransportLegId == l.Id
            })
            .ToList();

        // PTG-P1-04 — حساب‌های نقدی برای حالت «پرداخت‌شده». حسابِ انتخاب‌شدهٔ یک سندِ قدیمی
        // حتی اگر غیرفعال شده باشد در فهرست می‌ماند، وگرنه ویرایشِ آن سند حسابش را می‌باخت.
        var selectedCashAccountId = createModel?.CashAccountId;
        var cashAccounts = await _db.CashAccounts
            .AsNoTracking()
            .Where(a => a.IsActive || (selectedCashAccountId.HasValue && a.Id == selectedCashAccountId.Value))
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, Label = a.Name + " (" + a.Currency + ")" })
            .Take(LookupLimit)
            .ToListAsync();
        ViewBag.CashAccounts = new SelectList(cashAccounts, "Id", "Label", selectedCashAccountId);

        var serviceProviders = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive || (selectedServiceProviderId.HasValue && p.Id == selectedServiceProviderId.Value))
            .OrderBy(p => selectedServiceProviderId.HasValue && p.Id == selectedServiceProviderId.Value ? 0 : 1)
            .ThenBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Code })
            .Take(LookupLimit)
            .ToListAsync();

        ViewBag.ServiceProviders = serviceProviders
            .Select(p => new SelectListItem
            {
                Value = p.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : $"{p.Name} ({p.Code})",
                Selected = selectedServiceProviderId == p.Id
            })
            .ToList();

        var operationalAssets = await _db.OperationalAssets
            .AsNoTracking()
            .Where(a => a.IsActive || (selectedOperationalAssetId.HasValue && a.Id == selectedOperationalAssetId.Value))
            .OrderBy(a => selectedOperationalAssetId.HasValue && a.Id == selectedOperationalAssetId.Value ? 0 : 1)
            .ThenBy(a => a.AssetCode)
            .ThenBy(a => a.Name)
            .Select(a => new { a.Id, a.AssetCode, a.Name })
            .Take(LookupLimit)
            .ToListAsync();

        ViewBag.OperationalAssets = operationalAssets
            .Select(a => new SelectListItem
            {
                Value = a.Id.ToString(),
                Text = $"{a.AssetCode} - {a.Name}",
                Selected = selectedOperationalAssetId == a.Id
            })
            .ToList();

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",

            "Code",
            createModel?.Currency);
    }

    private async Task PopulateWagonRentLookupsAsync(WagonRentCreateViewModel model)
    {
        var expenseTypeOptions = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .Select(e => new
            {
                e.Id,
                Name = e.NamePersian ?? e.Name,
                DisplayName = (e.NamePersian ?? e.Name) + " (" + e.Code + ")"
            })
            .ToListAsync();

        ViewBag.ExpenseTypeNames = expenseTypeOptions.Select(e => e.Name).ToList();
        ViewBag.ExpenseTypes = new SelectList(
            expenseTypeOptions,
            "Id",
            "DisplayName",
            model.ExpenseTypeId);

        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => model.ContractId > 0 && c.Id == model.ContractId ? 0 : 1)
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
            model.ContractId);

        var currencies = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new { c.Code })
            .ToListAsync();
        if (currencies.Count == 0)
        {
            currencies = [new { Code = SystemCurrency.BaseCurrencyCode }, new { Code = "RUB" }];
        }

        ViewBag.Currencies = new SelectList(currencies, "Code", "Code", model.Currency);
    }

    private async Task<int?> FindWagonRentExpenseTypeIdAsync()
    {
        var types = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.Code, e.Name, e.NamePersian })
            .ToListAsync();

        return types
            .FirstOrDefault(e => ExpenseClassification.IsWagonRent(e.Code, e.Name, e.NamePersian, null))
            ?.Id;
    }

    private static void NormalizeWagonRentModel(WagonRentCreateViewModel model)
    {
        model.ExpenseDate = model.ExpenseDate.Date;
        model.AmountOriginal = decimal.Round(
            model.QuantityMt * model.UnitPriceOriginal,
            4,
            MidpointRounding.AwayFromZero);

        if (!string.IsNullOrWhiteSpace(model.Currency))
        {
            model.Currency = SystemCurrency.Normalize(model.Currency);
            if (SystemCurrency.IsBaseCurrency(model.Currency))
            {
                model.AppliedFxRateToUsd = 1m;
            }
            else if (model.DocumentCurrencyPerUsdRate.HasValue && model.DocumentCurrencyPerUsdRate.Value > 0m)
            {
                model.AppliedFxRateToUsd = decimal.Round(
                    1m / model.DocumentCurrencyPerUsdRate.Value,
                    6,
                    MidpointRounding.AwayFromZero);
            }
        }

        model.ManualExpenseTypeName = string.IsNullOrWhiteSpace(model.ManualExpenseTypeName)
            ? null
            : model.ManualExpenseTypeName.Trim();
        model.Reference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private static string BuildWagonRentDescription(WagonRentCreateViewModel model, CurrencyConversionResult conversion)
    {
        var parts = new List<string>
        {
            "Wagon Rent",
            $"M-Tone: {model.QuantityMt:N4}",
            $"Unit Price: {model.UnitPriceOriginal:N4} {conversion.SourceCurrencyCode}/MT",
            $"Rent Amount: {model.AmountOriginal:N4} {conversion.SourceCurrencyCode}",
            $"FX USD-per-unit: {conversion.AppliedRateToBase:N6}"
        };

        if (model.DocumentCurrencyPerUsdRate.HasValue)
        {
            parts.Add($"Document rate: {model.DocumentCurrencyPerUsdRate.Value:N6} {conversion.SourceCurrencyCode}/USD");
        }

        if (!string.IsNullOrWhiteSpace(model.Reference))
        {
            parts.Add($"Reference: {model.Reference}");
        }

        if (!string.IsNullOrWhiteSpace(model.Notes))
        {
            parts.Add($"Notes: {model.Notes}");
        }

        var description = string.Join(" | ", parts);
        return description.Length <= 1000 ? description : description[..1000];
    }

    public async Task<IActionResult> Index([FromQuery] ExpenseIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        filter ??= new ExpenseIndexFilterViewModel();

        var query = _db.ExpenseTransactions
            .AsNoTracking()
            .AsQueryable();

        query = query.Where(e => !e.IsCancelled);

        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (filter.ExpenseTypeId.Length > 0)
        {
            var expenseTypeIds = filter.ExpenseTypeId;
            query = query.Where(e => expenseTypeIds.Contains(e.ExpenseTypeId));
        }
        if (filter.ContractId.Length > 0)
        {
            var contractIds = filter.ContractId;
            query = query.Where(e => e.ContractId != null && contractIds.Contains(e.ContractId.Value));
        }
        if (filter.ShipmentId.Length > 0)
        {
            var shipmentIds = filter.ShipmentId;
            query = query.Where(e => e.ShipmentId != null && shipmentIds.Contains(e.ShipmentId.Value));
        }
        if (filter.TruckDispatchId.Length > 0)
        {
            var truckDispatchIds = filter.TruckDispatchId;
            query = query.Where(e => e.TruckDispatchId != null && truckDispatchIds.Contains(e.TruckDispatchId.Value));
        }
        if (filter.TransportLegId.HasValue)
            query = query.Where(e => e.TransportLegId == filter.TransportLegId.Value);
        if (filter.ServiceProviderId.Length > 0)
        {
            var serviceProviderIds = filter.ServiceProviderId;
            query = query.Where(e => e.ServiceProviderId != null && serviceProviderIds.Contains(e.ServiceProviderId.Value));
        }
        if (filter.OperationalAssetId.Length > 0)
        {
            var operationalAssetIds = filter.OperationalAssetId;
            query = query.Where(e => e.OperationalAssetId != null && operationalAssetIds.Contains(e.OperationalAssetId.Value));
        }
        // PTG-P1-04 — فیلترِ وضعیت تسویه. «طبقه‌بندی‌نشده» (Unknown) هم یک انتخابِ صریح است
        // تا ردیف‌های پیش از فاز ۱ قابل دیدن و رسیدگی باشند، نه پنهان.
        if (filter.SettlementMode.HasValue)
            query = query.Where(e => e.SettlementMode == filter.SettlementMode.Value);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            query = query.Where(e => e.Description != null && e.Description.Contains(q));
        }
        if (filter.FromDate.HasValue)
            query = query.Where(e => e.ExpenseDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)
            query = query.Where(e => e.ExpenseDate <= filter.ToDate.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        await PopulateLookupsAsync(filter: filter);

        var expenses = await query
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(e => new ExpenseListItemViewModel
            {
                Id = e.Id,
                CustomsDeclarationId = e.CustomsDeclarationId,
                ExpenseDate = e.ExpenseDate,
                ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.NamePersian ?? e.ExpenseType.Name : string.Empty,
                SettlementMode = e.SettlementMode,
                CashAccountName = e.CashAccount != null ? e.CashAccount.Name : null,
                ContractName = e.Contract != null ? e.Contract.ContractName : null,
                ContractNumber = e.Contract != null ? e.Contract.ContractNumber : null,
                ShipmentCode = e.Shipment != null ? e.Shipment.ShipmentCode : null,
                TruckDispatchLabel = e.TruckDispatch == null
                    ? null
                    : $"#{e.TruckDispatch.Id} - {(e.TruckDispatch.Truck != null ? e.TruckDispatch.Truck.PlateNumber : "بدون پلاک")}",
                TransportLegLabel = e.TransportLeg == null
                    ? null
                    : $"#{e.TransportLeg.Id} - {(e.TransportLeg.WagonNumber ?? e.TransportLeg.RwbNo ?? "Transport leg")}",
                // نمبر وسیله برای اینکه در خروجی معلوم باشد مصرف برای کدام موتر/واگن است.
                VehicleNumber = e.TruckDispatch != null && e.TruckDispatch.Truck != null
                    ? e.TruckDispatch.Truck.PlateNumber
                    : e.TransportLeg != null
                        ? (e.TransportLeg.Truck != null
                            ? e.TransportLeg.Truck.PlateNumber
                            : e.TransportLeg.WagonNumber ?? e.TransportLeg.RwbNo)
                        : null,
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.AssetCode + " - " + e.OperationalAsset.Name : null,
                Amount = e.Amount,
                Currency = e.Currency,
                AppliedFxRateToUsd = e.AppliedFxRateToUsd,
                AmountUsd = e.AmountUsd,
                Description = e.Description ?? string.Empty
            })
            .ToListAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        ViewBag.SumAmountUsd = await query.SumAsync(e => e.AmountUsd);
        ViewBag.ExpenseTypeCount = await query
            .Where(e => e.ExpenseTypeId != null)
            .Select(e => e.ExpenseTypeId)
            .Distinct()
            .CountAsync();
        ViewBag.RelatedContractCount = await query
            .Where(e => e.ContractId != null)
            .Select(e => e.ContractId)
            .Distinct()
            .CountAsync();

        return View(new ExpenseIndexViewModel
        {
            Filter = filter,
            Items = expenses,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? returnUrl = null)
    {
        var expense = await _db.ExpenseTransactions
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
        {
            return NotFound();
        }

        if (expense.IsCancelled)
        {
            TempData["ok"] = "این هزینه قبلاً لغو شده است.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        // مصرفِ اعلامیهٔ گمرکی مالکِ جدا دارد: لغو از خود اعلامیه انجام می‌شود تا
        // اعلامیه و مصارفش باهم برگردند (CustomsDeclarationExpenseSync).
        if (expense.CustomsDeclarationId.HasValue)
        {
            TempData["err"] = "این مصرف از اعلامیهٔ گمرکی ساخته شده است؛ لغو را از همان اعلامیه انجام دهید.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && l.SourceId == expense.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        if (originalLedger is null)
        {
            TempData["err"] = "سند مالی این هزینه پیدا نشد؛ لغو انجام نشد.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        // مرحله ۵ — Reversal قبل از علامت‌خوردن IsCancelled، تا Adapter شرکت را از همان
        // روابط قبلی حل کند. Idempotent است و اگر سند اصلی پست نشده باشد بی‌اثر می‌ماند.
        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseReversalAsync(expense);
        }

        expense.IsCancelled = true;

        // برگشت از تنها مالکش می‌گذرد: همهٔ فیلدهای طرف‌حساب کپی می‌شوند (نه فقط شرکت
        // خدماتی) و محافظِ «دو بار برگشت» هم همان‌جاست.
        await _ledger.ReverseAsync(
            originalLedger,
            _businessClock.Today,
            $"لغو هزینه #{expense.Id} | {originalLedger.Description}",
            $"EXP-{expense.Id}");
        await _db.SaveChangesAsync();

        TempData["ok"] = "هزینه لغو شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? contractId = null, int? transportLegId = null, int? truckDispatchId = null, int? serviceProviderId = null, int? operationalAssetId = null, string? returnUrl = null, Guid? draft = null)
    {
        // ادامهٔ پیش‌نویس: همان نمایش دوبارهٔ فرم پس از خطای اعتبارسنجی.
        var draftSnapshot = await this.LoadFormDraftAsync(draft, FormDraftForms.ExpensesCreate);
        if (draftSnapshot is not null)
        {
            var restored = new ExpenseCreateViewModel();
            await TryUpdateModelAsync(restored, string.Empty, draftSnapshot.ToValueProvider());
            ModelState.Clear();
            NormalizeCreateModel(restored);
            await PopulateLookupsAsync(createModel: restored);
            return View(restored);
        }

        var model = new ExpenseCreateViewModel
        {
            ExpenseDate = _businessClock.Today,
            Currency = SystemCurrency.BaseCurrencyCode
        };

        if (contractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value);
            if (contract is not null)
            {
                model.ContractId = contract.Id;
            }
        }

        if (transportLegId.HasValue)
        {
            var transportLeg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == transportLegId.Value);
            if (transportLeg is not null)
            {
                model.TransportLegId = transportLeg.Id;
                model.ContractId = transportLeg.SourcePurchaseContractId;
                model.ShipmentId = transportLeg.ShipmentId;
            }
        }

        if (truckDispatchId.HasValue)
        {
            var truckDispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == truckDispatchId.Value);
            if (truckDispatch is not null)
            {
                model.TruckDispatchId = truckDispatch.Id;
                model.ContractId = truckDispatch.ContractId;
            }
        }

        if (serviceProviderId.HasValue)
        {
            model.ServiceProviderId = serviceProviderId.Value;
        }

        if (operationalAssetId.HasValue)
        {
            model.OperationalAssetId = operationalAssetId.Value;
        }

        model.ReturnUrl = returnUrl;

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateWagonRent(int? contractId = null, string? returnUrl = null)
    {
        var model = new WagonRentCreateViewModel
        {
            ExpenseDate = _businessClock.Today,
            ContractId = contractId ?? 0,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            ManualExpenseTypeName = DefaultWagonRentExpenseName,
            ReturnUrl = returnUrl
        };

        var wagonRentTypeId = await FindWagonRentExpenseTypeIdAsync();
        if (wagonRentTypeId.HasValue)
        {
            model.ExpenseTypeId = wagonRentTypeId;
            model.ManualExpenseTypeName = null;
        }

        await PopulateWagonRentLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateWagonRent(
        WagonRentCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        NormalizeWagonRentModel(model);
        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;

        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue)
        {
            expenseType = await _db.ExpenseTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
        }

        var contractExists = await _db.Contracts
            .AsNoTracking()
            .AnyAsync(c => c.Id == model.ContractId);
        if (!contractExists)
        {
            ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        if (!ModelState.IsValid)
        {
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateWagonRentLookupsAsync(model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.ExpenseDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateWagonRentLookupsAsync(model);
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            if (expenseType is null)
            {
                expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
            }

            if (expenseType is null)
            {
                expenseType = new ExpenseType
                {
                    Code = await BuildManualExpenseTypeCodeAsync(manualExpenseTypeName),
                    Name = manualExpenseTypeName,
                    NamePersian = manualExpenseTypeName,
                    Category = "Transport",
                    IsActive = true
                };
                _db.ExpenseTypes.Add(expenseType);
                await _db.SaveChangesAsync();
            }

            var description = BuildWagonRentDescription(model, conversion);
            var expense = new ExpenseTransaction
            {
                ExpenseTypeId = expenseType.Id,
                ContractId = model.ContractId,
                ExpenseDate = model.ExpenseDate.Date,
                Amount = model.AmountOriginal,
                Currency = conversion.SourceCurrencyCode,
                AppliedFxRateToUsd = conversion.AppliedRateToBase,
                AmountUsd = conversion.ConvertToBase(model.AmountOriginal),
                Description = description,
                // Phase 1 — مسئول کرایه واگن (فقط ثبت/نمایش، به Ledger اثر ندارد).
                CostResponsibility = model.CostResponsibility
            };

            // PTG-P0-01 — توکن با همان SaveChanges و همان Transaction مصرف می‌شود.
            _formTokens.Stamp(formToken, "Expense.CreateWagonRent", nameof(ExpenseTransaction));

            _db.ExpenseTransactions.Add(expense);
            await _db.SaveChangesAsync();

            var ledgerEntry = _expenseLedger.Post(new ExpenseLedgerRequest
            {
                Expense = expense,
                ExpenseType = expenseType,
                FxRateDate = conversion.EffectiveDate.Date,
                FxRateSource = conversion.SourceDescription
            });
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(ExpenseTransaction),
                expense.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ExpenseTypeId", expense.ExpenseTypeId),
                    ("ContractId", expense.ContractId),
                    ("ExpenseDate", expense.ExpenseDate),
                    ("Amount", expense.Amount),
                    ("Currency", expense.Currency),
                    ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                    ("AmountUsd", expense.AmountUsd),
                    ("Description", expense.Description),
                    ("LedgerReference", ledgerEntry.Reference)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = "کرایه واگون با موفقیت به عنوان مصرف قرارداد ثبت شد.";
            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id = expense.Id });
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            TempData["err"] = "این کرایه قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
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

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ExpenseCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        NormalizeCreateModel(model);
        var normalizedDescription = model.Description?.Trim() ?? string.Empty;
        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;

        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue)
        {
            expenseType = await _db.ExpenseTypes
            .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
        }

        InventoryTransportLeg? transportLeg = null;
        if (model.TransportLegId.HasValue)
        {
            transportLeg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == model.TransportLegId.Value);
            if (transportLeg is null)
            {
                ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg selection is invalid.");
            }
            else
            {
                if (!model.ContractId.HasValue)
                {
                    model.ContractId = transportLeg.SourcePurchaseContractId;
                }
                else if (model.ContractId.Value != transportLeg.SourcePurchaseContractId)
                {
                    ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg must match the selected purchase contract.");
                }

                if (transportLeg.ShipmentId.HasValue && !model.ShipmentId.HasValue)
                {
                    model.ShipmentId = transportLeg.ShipmentId;
                }
                else if (transportLeg.ShipmentId.HasValue
                         && model.ShipmentId.HasValue
                         && model.ShipmentId.Value != transportLeg.ShipmentId.Value)
                {
                    ModelState.AddModelError(nameof(model.ShipmentId), "Shipment must match the selected transport leg.");
                }
            }
        }

        Contract? contract = null;
        if (model.ContractId.HasValue)
        {
            contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
        }

        Shipment? shipment = null;
        if (model.ShipmentId.HasValue)
        {
            shipment = await _db.Shipments
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null
                     && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, contract.Id))
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        TruckDispatch? truckDispatch = null;
        if (model.TruckDispatchId.HasValue)
        {
            truckDispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId.Value);
            if (truckDispatch is null)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null && truckDispatch.ContractId != contract.Id)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        if (shipment is not null
            && truckDispatch is not null
            && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, truckDispatch.ContractId))
        {
            ModelState.AddModelError(string.Empty, "Shipment و دیسپچ انتخاب‌شده به یک قرارداد واحد اشاره نمی‌کنند.");
        }

        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            ModelState.AddModelError(nameof(model.Description), "ثبت شرح یا مرجع برای trace هزینه الزامی است.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        ServiceProviderEntity? serviceProvider = null;
        if (model.ServiceProviderId.HasValue)
        {
            serviceProvider = await _db.ServiceProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == model.ServiceProviderId.Value);
            if (serviceProvider is null)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider selection is invalid.");
            }
            else if (!serviceProvider.IsActive)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider is inactive.");
            }
        }

        OperationalAsset? operationalAsset = null;
        if (model.OperationalAssetId.HasValue)
        {
            operationalAsset = await _db.OperationalAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == model.OperationalAssetId.Value);
            if (operationalAsset is null)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset selection is invalid.");
            }
            else if (!operationalAsset.IsActive)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset is inactive.");
            }
        }

        if (!ModelState.IsValid)
        {
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.ExpenseDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        try
        {
            IDbContextTransaction? transaction = null;
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync();
            }

            try
            {
                if (expenseType is null)
                {
                    expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
                }

                if (expenseType is null)
                {
                    expenseType = new ExpenseType
                    {
                        Code = await BuildManualExpenseTypeCodeAsync(manualExpenseTypeName),
                        Name = manualExpenseTypeName,
                        NamePersian = manualExpenseTypeName,
                        Category = "Other",
                        IsActive = true
                    };
                    _db.ExpenseTypes.Add(expenseType);
                    await _db.SaveChangesAsync();
                }

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ContractId = model.ContractId,
                    ShipmentId = model.ShipmentId,
                    TruckDispatchId = model.TruckDispatchId,
                    TransportLegId = model.TransportLegId,
                    ServiceProviderId = serviceProvider?.Id,
                    OperationalAssetId = operationalAsset?.Id,
                    ExpenseDate = model.ExpenseDate,
                    Amount = model.Amount,
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = conversion.ConvertToBase(model.Amount),
                    Description = normalizedDescription,
                    // Phase 1 — مسئول هزینه/کرایه (فقط ثبت/نمایش، به Ledger اثر ندارد).
                    CostResponsibility = model.CostResponsibility
                };

                // PTG-P1-04 — هویت تسویه: انتخابِ فرم، وگرنه تشخیص از طرف‌حساب.
                ApplyFormSettlement(expense, model);

                var settlementCheck = _settlementValidator.Check(expense);
                if (!settlementCheck.IsValid)
                {
                    ModelState.AddModelError(settlementCheck.MemberName ?? string.Empty, settlementCheck.Message!);
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    model.Description = normalizedDescription;
                    model.ManualExpenseTypeName = manualExpenseTypeName;
                    await PopulateLookupsAsync(createModel: model);
                    return View(model);
                }

                // PTG-P0-01 — توکن در همان SaveChanges (و همان Transaction) با خودِ مصرف ثبت
                // می‌شود، پس ارسال دوم با همان توکن به Unique Index می‌خورد و هیچ سند/لجری
                // نمی‌سازد. نبودِ توکن fail-open است و رفتار قبلی را عوض نمی‌کند.
                _formTokens.Stamp(formToken, "Expense.Create", nameof(ExpenseTransaction));

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var ledgerReference = ExpenseLedgerPoster.BuildReference(expenseType!, expense);
                var ledgerDescription = ExpenseLedgerPoster.BuildDescription(expenseType!, expense);

                var ledgerEntry = _expenseLedger.Post(new ExpenseLedgerRequest
                {
                    Expense = expense,
                    Description = ledgerDescription,
                    Reference = ledgerReference,
                    FxRateDate = conversion.EffectiveDate.Date,
                    FxRateSource = conversion.SourceDescription
                });
                await _db.SaveChangesAsync();

                // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
                if (_expenseAccounting is not null)
                {
                    var accounting = await _expenseAccounting.TryPostExpenseAsync(expense);
                    // فقط اطلاع‌رسانی (قاعدهٔ حسابداری تغییر نکرده): ثبت موفقِ بدون سند دفتر کل نباید کامل به نظر برسد.
                    if (accounting is { Status: Services.Accounting.PaymentPostingStatus.Skipped, Reason: "EXPENSE_COMPANY_UNKNOWN" })
                    {
                        TempData["warn"] = "مصرف ثبت شد، ولی سند دفتر کل (حسابداری دوطرفه) برایش ساخته نشد: شرکتِ آن از قرارداد، "
                            + "محموله یا حمل مشخص نیست. برای اثر در دفتر کل، قرارداد یا سند مرتبط را تعیین کنید.";
                    }
                }

                await _audit.LogAndSaveAsync(
                    nameof(ExpenseTransaction),
                    expense.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ExpenseTypeId", expense.ExpenseTypeId),
                        ("ContractId", expense.ContractId),
                        ("ShipmentId", expense.ShipmentId),
                        ("TruckDispatchId", expense.TruckDispatchId),
                        ("TransportLegId", expense.TransportLegId),
                        ("ServiceProviderId", expense.ServiceProviderId),
                        ("OperationalAssetId", expense.OperationalAssetId),
                        ("ExpenseDate", expense.ExpenseDate),
                        ("Amount", expense.Amount),
                        ("Currency", expense.Currency),
                        ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                        ("AmountUsd", expense.AmountUsd),
                        ("Description", expense.Description),
                        ("LedgerReference", ledgerEntry.Reference)));

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                TempData["ok"] = "هزینه با موفقیت ثبت شد.";
                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return Redirect(localReturnUrl);
                }

                return RedirectToAction(nameof(Details), new { id = expense.Id });
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
            // ارسال دوباره همان فرم (Refresh، تب دوم، تلاش پس از Timeout).
            TempData["err"] = "این مصرف قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create expense transaction.");
            ModelState.AddModelError(string.Empty, "ثبت هزینه انجام نشد. دوباره تلاش کنید.");
        }

        model.Description = normalizedDescription;
        model.ManualExpenseTypeName = manualExpenseTypeName;
        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    // ----------------------------------------------------------------------------
    // Bulk import of expenses from Excel — Phase 1: preview + validation, then save.
    // Reuses the same ExpenseTransaction + LedgerEntry construction as the single
    // create path (no parallel financial logic). Advanced links (service provider,
    // shipment, dispatch, transport leg) are intentionally left out of import and
    // can be added later by editing the saved expense.
    // ----------------------------------------------------------------------------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Import(string? returnUrl = null)
    {
        await PopulateImportLookupsAsync();
        return View(new ExpenseImportViewModel { ReturnUrl = returnUrl });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 20000, MultipartBodyLengthLimit = 52_428_800L)]
    public async Task<IActionResult> ImportPreview(ExpenseImportViewModel model)
    {
        if (model.ImportFile is null || model.ImportFile.Length == 0)
        {
            ModelState.AddModelError(nameof(model.ImportFile), "فایل اکسل مصارف را انتخاب کنید.");
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }

        var importExtension = Path.GetExtension(model.ImportFile.FileName);
        if (!string.Equals(importExtension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(importExtension, ".xls", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.ImportFile), "فقط فایل‌های Excel با پسوند .xlsx و .xls پشتیبانی می‌شوند.");
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }

        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(model.ImportFile);
            model.Rows = ExpenseWorkbookParser.Parse(workbook.Stream).ToList();
        }
        catch (InvalidDataException ex)
        {
            ModelState.AddModelError(nameof(model.ImportFile), ex.Message);
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read expense import workbook.");
            ModelState.AddModelError(nameof(model.ImportFile), "خواندن فایل اکسل انجام نشد. ساختار فایل را بررسی کنید.");
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }

        await ResolveAndValidateImportRowsAsync(model);
        ModelState.Clear();
        return View("ImportPreview", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 20000)]
    public async Task<IActionResult> ImportConfirm(ExpenseImportViewModel model)
    {
        model.Rows ??= new List<ExpenseImportRowViewModel>();
        await ResolveAndValidateImportRowsAsync(model);

        if (!model.CanConfirm)
        {
            ModelState.Clear();
            if (!model.HasRows)
            {
                ModelState.AddModelError(string.Empty, "ردیفی برای ثبت وجود ندارد. دوباره فایل را وارد کنید.");
            }
            else if (model.ImportValidRowsOnly)
            {
                // حالت جزئی انتخاب شده ولی هیچ سطر قابل ثبتی نمانده است.
                ModelState.AddModelError(
                    string.Empty,
                    "هیچ سطر سالمِ ثبت‌نشده‌ای در این فایل نیست. سطرهای دارای خطا را اصلاح کنید.");
            }
            else if (model.DuplicateCount > 0 && model.ErrorCount == 0)
            {
                ModelState.AddModelError(
                    string.Empty,
                    $"{model.DuplicateCount} سطر از این فایل قبلاً ثبت شده است. برای ثبت بقیه، گزینهٔ «فقط سطرهای سالم و ثبت‌نشده» را فعال کنید.");
            }
            else
            {
                ModelState.AddModelError(
                    string.Empty,
                    "هنوز ردیف‌های دارای خطا وجود دارد. فایل را اصلاح کنید، یا گزینهٔ «فقط سطرهای سالم و ثبت‌نشده» را فعال کنید.");
            }

            return View("ImportPreview", model);
        }

        var expenseTypeIds = model.Rows
            .Where(r => r.ResolvedExpenseTypeId.HasValue)
            .Select(r => r.ResolvedExpenseTypeId!.Value)
            .Distinct()
            .ToList();

        var expenseTypeMap = await _db.ExpenseTypes
            .Where(e => expenseTypeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var savedCount = 0;
            foreach (var row in model.Rows)
            {
                // PTG-P2-02 — هیچ سطری بی‌صدا رد نمی‌شود: سطرهای ردشده در پیش‌نمایش با
                // دلیلشان دیده می‌شوند و شمارشِ نهایی هم آن‌ها را گزارش می‌کند.
                if (!row.IsImportable)
                {
                    continue;
                }

                if (!row.ResolvedExpenseTypeId.HasValue
                    || !expenseTypeMap.TryGetValue(row.ResolvedExpenseTypeId.Value, out var expenseType)
                    || !row.ExpenseDate.HasValue
                    || !row.Amount.HasValue)
                {
                    continue;
                }

                var conversion = await _currencyConversion.ResolveToBaseAsync(
                    row.Currency ?? SystemCurrency.BaseCurrencyCode,
                    row.ExpenseDate.Value,
                    ResolveManualRateToBase(row));

                var description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description.Trim();

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ContractId = row.ResolvedContractId,
                    ExpenseDate = row.ExpenseDate.Value,
                    Amount = row.Amount.Value,
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = conversion.ConvertToBase(row.Amount.Value),
                    Description = description,
                    // PTG-P2-02 — هویتِ سطر ذخیره می‌شود، وگرنه ایمپورتِ دوبارهٔ همان فایل
                    // همین مصرف را بار دوم می‌ساخت. Unique Index دیتابیس هم پشتوانهٔ نهایی است.
                    ImportUniqueKey = row.ImportUniqueKey
                };

                // PTG-P1-04 — هویت تسویه. فایلِ ایمپورت ستونِ طرف‌حساب و حساب نقدی ندارد.
                ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);
                _settlementValidator.Validate(expense);

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var ledgerEntry = _expenseLedger.Post(new ExpenseLedgerRequest
                {
                    Expense = expense,
                    ExpenseType = expenseType,
                    FxRateDate = conversion.EffectiveDate.Date,
                    FxRateSource = conversion.SourceDescription
                });
                await _db.SaveChangesAsync();

                await _audit.LogAndSaveAsync(
                    nameof(ExpenseTransaction),
                    expense.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ExpenseTypeId", expense.ExpenseTypeId),
                        ("ContractId", expense.ContractId),
                        ("ExpenseDate", expense.ExpenseDate),
                        ("Amount", expense.Amount),
                        ("Currency", expense.Currency),
                        ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                        ("AmountUsd", expense.AmountUsd),
                        ("Description", expense.Description),
                        ("Source", "ExcelImport"),
                        ("LedgerReference", ledgerEntry.Reference)));

                savedCount++;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            var skipped = model.Rows.Count - savedCount;
            TempData["ok"] = skipped == 0
                ? $"{savedCount} ردیف مصرف از فایل اکسل با موفقیت ثبت شد."
                : $"{savedCount} ردیف ثبت شد؛ {skipped} ردیف ثبت نشد ({model.ErrorCount} خطا، {model.DuplicateCount} تکراری).";

            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to save imported expenses.");
            ModelState.Clear();
            ModelState.AddModelError(string.Empty, "ثبت مصارف انجام نشد. دوباره تلاش کنید.");
            return View("ImportPreview", model);
        }
    }

    private static decimal? ResolveManualRateToBase(ExpenseImportRowViewModel row)
    {
        if (SystemCurrency.IsBaseCurrency(row.Currency) || !row.RatePerUsd.HasValue || row.RatePerUsd.Value <= 0m)
        {
            return null;
        }

        // The import column is "هر دالر = چند واحد ارز" (units per USD); the ledger
        // convention stores USD per 1 unit, so invert it.
        return decimal.Round(1m / row.RatePerUsd.Value, 6, MidpointRounding.AwayFromZero);
    }

    private async Task ResolveAndValidateImportRowsAsync(ExpenseImportViewModel model)
    {
        model.Rows ??= new List<ExpenseImportRowViewModel>();

        var activeCurrencies = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => c.Code)
            .ToListAsync();
        if (activeCurrencies.Count == 0)
        {
            activeCurrencies = new List<string> { SystemCurrency.BaseCurrencyCode };
        }

        var currencySet = new HashSet<string>(
            activeCurrencies.Select(c => c.ToUpperInvariant()),
            StringComparer.Ordinal);
        currencySet.Add(SystemCurrency.BaseCurrencyCode);

        var expenseTypes = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.Name, e.NamePersian })
            .ToListAsync();

        var typeByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in expenseTypes)
        {
            if (!string.IsNullOrWhiteSpace(type.Name))
            {
                typeByName.TryAdd(type.Name.Trim(), type.Id);
            }

            if (!string.IsNullOrWhiteSpace(type.NamePersian))
            {
                typeByName.TryAdd(type.NamePersian.Trim(), type.Id);
            }
        }

        var typeNamesHint = string.Join("، ", expenseTypes
            .Select(t => t.NamePersian ?? t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(8));
        var currencyHint = string.Join("، ", currencySet.Take(8));

        var requestedContractNumbers = model.Rows
            .Select(r => r.ContractNumber?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contractByNumber = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (requestedContractNumbers.Count > 0)
        {
            var contracts = await _db.Contracts
                .AsNoTracking()
                .Where(c => requestedContractNumbers.Contains(c.ContractNumber))
                .Select(c => new { c.Id, c.ContractNumber })
                .ToListAsync();
            foreach (var contract in contracts)
            {
                contractByNumber.TryAdd(contract.ContractNumber, contract.Id);
            }
        }

        var validCount = 0;
        var errorCount = 0;
        var occurrences = new ExpenseImportKey.OccurrenceTracker();

        foreach (var row in model.Rows)
        {
            row.Errors = new List<string>();
            row.ExpenseDate = null;
            row.Amount = null;
            row.RatePerUsd = null;
            row.AmountUsd = null;
            row.ResolvedExpenseTypeId = null;
            row.ResolvedExpenseTypeName = null;
            row.ResolvedContractId = null;

            // Date
            if (string.IsNullOrWhiteSpace(row.ExpenseDateText))
            {
                row.Errors.Add("ستون «تاریخ» خالی است.");
            }
            else if (PTGOilSystem.Web.Services.Calendars.AfghanSolarCalendar.TryParseActiveCalendarText(row.ExpenseDateText, out var solarDate))
            {
                // تقویم هجری شمسی: متنِ شمسی هرگز به‌عنوان سالِ میلادی خوانده نمی‌شود.
                if (solarDate.HasValue)
                    row.ExpenseDate = solarDate.Value;
                else
                    row.Errors.Add($"تاریخ «{row.ExpenseDateText}» معتبر نیست. قالب درست مانند 1405/07/12 است.");
            }
            else if (DateTime.TryParse(row.ExpenseDateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                row.ExpenseDate = date.Date;
            }
            else
            {
                row.Errors.Add($"تاریخ «{row.ExpenseDateText}» خوانده نشد. قالب درست مانند 2026-06-24 است.");
            }

            // Expense type
            if (string.IsNullOrWhiteSpace(row.ExpenseTypeName))
            {
                row.Errors.Add("ستون «نوع مصرف» خالی است.");
            }
            else if (typeByName.TryGetValue(row.ExpenseTypeName.Trim(), out var typeId))
            {
                row.ResolvedExpenseTypeId = typeId;
                row.ResolvedExpenseTypeName = row.ExpenseTypeName.Trim();
            }
            else
            {
                row.Errors.Add($"نوع مصرف «{row.ExpenseTypeName.Trim()}» در سیستم نیست. از این نام‌ها استفاده کنید: {typeNamesHint}");
            }

            // Amount
            if (string.IsNullOrWhiteSpace(row.AmountText))
            {
                row.Errors.Add("ستون «مبلغ» خالی است.");
            }
            else if (decimal.TryParse(row.AmountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) && amount > 0m)
            {
                row.Amount = decimal.Round(amount, 4, MidpointRounding.AwayFromZero);
            }
            else
            {
                row.Errors.Add($"مبلغ «{row.AmountText}» معتبر نیست. یک عدد بزرگ‌تر از صفر بنویسید.");
            }

            // Currency
            if (string.IsNullOrWhiteSpace(row.Currency))
            {
                row.Errors.Add("ستون «ارز» خالی است.");
            }
            else
            {
                row.Currency = row.Currency.Trim().ToUpperInvariant();
                if (!currencySet.Contains(row.Currency))
                {
                    row.Errors.Add($"ارز «{row.Currency}» تعریف نشده است. ارزهای مجاز: {currencyHint}");
                }
            }

            // Rate (optional; required only when daily rate is missing for non-USD)
            if (!string.IsNullOrWhiteSpace(row.RatePerUsdText))
            {
                if (decimal.TryParse(row.RatePerUsdText, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) && rate > 0m)
                {
                    row.RatePerUsd = rate;
                }
                else
                {
                    row.Errors.Add($"نرخ «{row.RatePerUsdText}» معتبر نیست. نرخ باید عدد بزرگ‌تر از صفر باشد (هر دالر = چند واحد ارز).");
                }
            }

            // Contract (optional)
            if (!string.IsNullOrWhiteSpace(row.ContractNumber))
            {
                if (contractByNumber.TryGetValue(row.ContractNumber.Trim(), out var contractId))
                {
                    row.ResolvedContractId = contractId;
                }
                else
                {
                    row.Errors.Add($"قرارداد «{row.ContractNumber.Trim()}» پیدا نشد. شمارهٔ قرارداد را بررسی کنید یا خالی بگذارید.");
                }
            }

            // Compute USD equivalent (and surface any FX problem) only if the basics are valid.
            if (row.Errors.Count == 0 && row.ExpenseDate.HasValue && row.Amount.HasValue)
            {
                try
                {
                    var conversion = await _currencyConversion.ResolveToBaseAsync(
                        row.Currency ?? SystemCurrency.BaseCurrencyCode,
                        row.ExpenseDate.Value,
                        ResolveManualRateToBase(row));
                    row.AmountUsd = conversion.ConvertToBase(row.Amount.Value);
                }
                catch (BusinessRuleException)
                {
                    row.Errors.Add($"نرخ دالر برای ارز «{row.Currency}» در این تاریخ موجود نیست. ستون «نرخ» را پر کنید (هر دالر = چند واحد ارز).");
                }
            }

            // PTG-P2-02 — هویتِ canonical سطر. با آن، ایمپورتِ دوبارهٔ همان فایل سطرهای
            // قبلاً ثبت‌شده را دوباره نمی‌سازد؛ بدون آن «ورودِ فقط سطرهای سالم» ناامن بود.
            row.ImportUniqueKey = ExpenseImportKey.Build(
                row.ResolvedExpenseTypeId,
                row.ExpenseDate,
                row.Amount,
                row.Currency,
                row.ResolvedContractId,
                row.Description,
                occurrences);

            if (row.Errors.Count == 0)
            {
                validCount++;
            }
            else
            {
                errorCount++;
            }
        }

        // یک پرس‌وجو برای همهٔ کلیدها؛ نه یک پرس‌وجو به‌ازای هر سطر.
        var candidateKeys = model.Rows
            .Where(r => r.Errors.Count == 0 && r.ImportUniqueKey is not null)
            .Select(r => r.ImportUniqueKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var alreadyImported = candidateKeys.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _db.ExpenseTransactions
                    .AsNoTracking()
                    .Where(e => e.ImportUniqueKey != null && candidateKeys.Contains(e.ImportUniqueKey))
                    .Select(e => e.ImportUniqueKey!)
                    .ToListAsync())
                .ToHashSet(StringComparer.Ordinal);

        foreach (var row in model.Rows)
        {
            row.IsDuplicate = row.Errors.Count == 0
                && row.ImportUniqueKey is not null
                && alreadyImported.Contains(row.ImportUniqueKey);
        }

        model.ValidCount = validCount;
        model.ErrorCount = errorCount;
        model.DuplicateCount = model.Rows.Count(r => r.IsDuplicate);
    }

    private async Task PopulateImportLookupsAsync()
    {
        ViewBag.ImportExpenseTypeNames = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .Select(e => e.NamePersian ?? e.Name)
            .ToListAsync();

        var currencies = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => c.Code)
            .ToListAsync();
        if (currencies.Count == 0)
        {
            currencies = new List<string> { SystemCurrency.BaseCurrencyCode };
        }

        ViewBag.ImportCurrencies = currencies;
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var expense = await _db.ExpenseTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
        {
            return NotFound();
        }

        if (expense.IsCancelled)
        {
            TempData["err"] = "هزینه لغوشده قابل ویرایش نیست.";
            if (TryGetLocalReturnUrl(returnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        var model = new ExpenseCreateViewModel
        {
            Id = expense.Id,
            // PTG-P1-05 — نسخه‌ای که کاربر می‌بیند، با فرم برمی‌گردد.
            Version = expense.Version,
            ExpenseTypeId = expense.ExpenseTypeId,
            ContractId = expense.ContractId,
            ShipmentId = expense.ShipmentId,
            TruckDispatchId = expense.TruckDispatchId,
            TransportLegId = expense.TransportLegId,
            ServiceProviderId = expense.ServiceProviderId,
            OperationalAssetId = expense.OperationalAssetId,
            ExpenseDate = expense.ExpenseDate,
            Amount = expense.Amount,
            Currency = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            Description = expense.Description ?? string.Empty,
            CostResponsibility = expense.CostResponsibility,
            // PTG-P1-04 — هویتِ تسویهٔ همین سند با فرم برمی‌گردد. ردیفِ پیش از فاز ۱ روی
            // Unknown است و به‌عنوان «تشخیص از طرف‌حساب» به فرم می‌رود، نه به‌عنوان انتخابِ
            // کاربر: ذخیرهٔ دوباره آن را به همان چیزی می‌برد که دفتر کل امروز می‌گوید.
            SettlementMode = expense.SettlementMode == ExpenseSettlementMode.Unknown
                ? null
                : expense.SettlementMode,
            CashAccountId = expense.CashAccountId,
            ReturnUrl = returnUrl
        };

        await PopulateLookupsAsync(createModel: model);
        ViewData["ExpenseFormMode"] = "Edit";
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ExpenseCreateViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var expense = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
        {
            return NotFound();
        }

        // PTG-P1-05 — معیارِ برخورد، نسخه‌ای است که کاربر هنگامِ بازکردنِ فرم دید،
        // نه نسخه‌ای که همین حالا خوانده شد.
        _db.UseExpectedVersion(expense, model.Version);

        if (expense.IsCancelled)
        {
            ModelState.AddModelError(string.Empty, "هزینه لغوشده قابل ویرایش نیست.");
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        NormalizeCreateModel(model);
        var normalizedDescription = model.Description?.Trim() ?? string.Empty;
        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;

        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue)
        {
            expenseType = await _db.ExpenseTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
        }

        InventoryTransportLeg? transportLeg = null;
        if (model.TransportLegId.HasValue)
        {
            transportLeg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == model.TransportLegId.Value);
            if (transportLeg is null)
            {
                ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg selection is invalid.");
            }
            else
            {
                if (!model.ContractId.HasValue)
                {
                    model.ContractId = transportLeg.SourcePurchaseContractId;
                }
                else if (model.ContractId.Value != transportLeg.SourcePurchaseContractId)
                {
                    ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg must match the selected purchase contract.");
                }

                if (transportLeg.ShipmentId.HasValue && !model.ShipmentId.HasValue)
                {
                    model.ShipmentId = transportLeg.ShipmentId;
                }
                else if (transportLeg.ShipmentId.HasValue
                         && model.ShipmentId.HasValue
                         && model.ShipmentId.Value != transportLeg.ShipmentId.Value)
                {
                    ModelState.AddModelError(nameof(model.ShipmentId), "Shipment must match the selected transport leg.");
                }
            }
        }

        Contract? contract = null;
        if (model.ContractId.HasValue)
        {
            contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
        }

        Shipment? shipment = null;
        if (model.ShipmentId.HasValue)
        {
            shipment = await _db.Shipments
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null
                     && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, contract.Id))
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        TruckDispatch? truckDispatch = null;
        if (model.TruckDispatchId.HasValue)
        {
            truckDispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId.Value);
            if (truckDispatch is null)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null && truckDispatch.ContractId != contract.Id)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        if (shipment is not null
            && truckDispatch is not null
            && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, truckDispatch.ContractId))
        {
            ModelState.AddModelError(string.Empty, "Shipment و دیسپچ انتخاب‌شده به یک قرارداد واحد اشاره نمی‌کنند.");
        }

        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            ModelState.AddModelError(nameof(model.Description), "ثبت شرح یا مرجع برای trace هزینه الزامی است.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        ServiceProviderEntity? serviceProvider = null;
        if (model.ServiceProviderId.HasValue)
        {
            serviceProvider = await _db.ServiceProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == model.ServiceProviderId.Value);
            if (serviceProvider is null)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider selection is invalid.");
            }
            else if (!serviceProvider.IsActive)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider is inactive.");
            }
        }

        OperationalAsset? operationalAsset = null;
        if (model.OperationalAssetId.HasValue)
        {
            operationalAsset = await _db.OperationalAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == model.OperationalAssetId.Value);
            if (operationalAsset is null)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset selection is invalid.");
            }
            else if (!operationalAsset.IsActive)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset is inactive.");
            }
        }

        if (!ModelState.IsValid)
        {
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.ExpenseDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        var ledgerEntry = await _db.LedgerEntries
            .Where(l => l.SourceType == "Expense" && l.SourceId == expense.Id)
            .OrderBy(l => l.Id)
            .FirstOrDefaultAsync();

        var previousExpense = new
        {
            expense.ExpenseTypeId,
            expense.ContractId,
            expense.ShipmentId,
            expense.TruckDispatchId,
            expense.TransportLegId,
            expense.ServiceProviderId,
            expense.OperationalAssetId,
            expense.ExpenseDate,
            expense.Amount,
            expense.Currency,
            expense.AppliedFxRateToUsd,
            expense.AmountUsd,
            expense.Description
        };

        var ledgerIsNew = ledgerEntry is null;
        var previousLedger = ledgerEntry is null
            ? null
            : new
            {
                ledgerEntry.EntryDate,
                ledgerEntry.Side,
                ledgerEntry.AmountUsd,
                ledgerEntry.SourceAmount,
                ledgerEntry.SourceCurrencyCode,
                ledgerEntry.AppliedFxRateToUsd,
                ledgerEntry.AppliedFxRateDate,
                ledgerEntry.AppliedFxRateSource,
                ledgerEntry.Description,
                ledgerEntry.SourceType,
                ledgerEntry.SourceId,
                ledgerEntry.Reference,
                ledgerEntry.ContractId,
                ledgerEntry.ServiceProviderId,
                ledgerEntry.ShipmentId
            };

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            if (expenseType is null)
            {
                expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
            }

            if (expenseType is null)
            {
                expenseType = new ExpenseType
                {
                    Code = await BuildManualExpenseTypeCodeAsync(manualExpenseTypeName),
                    Name = manualExpenseTypeName,
                    NamePersian = manualExpenseTypeName,
                    Category = "Other",
                    IsActive = true
                };
                _db.ExpenseTypes.Add(expenseType);
                await _db.SaveChangesAsync();
            }

            expense.ExpenseTypeId = expenseType.Id;
            expense.ContractId = model.ContractId;
            expense.ShipmentId = model.ShipmentId;
            expense.TruckDispatchId = model.TruckDispatchId;
            expense.TransportLegId = model.TransportLegId;
            expense.ServiceProviderId = serviceProvider?.Id;
            expense.OperationalAssetId = operationalAsset?.Id;
            expense.ExpenseDate = model.ExpenseDate.Date;
            expense.Amount = model.Amount;
            expense.Currency = conversion.SourceCurrencyCode;
            expense.AppliedFxRateToUsd = conversion.AppliedRateToBase;
            expense.AmountUsd = conversion.ConvertToBase(model.Amount);
            expense.Description = normalizedDescription;
            expense.CostResponsibility = model.CostResponsibility;

            // PTG-P1-04 — هویت تسویه: انتخابِ فرم، وگرنه تشخیص از طرف‌حسابِ خودِ سند — که
            // شاملِ DriverId‌ای هم می‌شود که این فرم نمایش نمی‌دهد و بالاتر دست‌کاری نشد؛
            // وگرنه ویرایشِ توضیحِ یک مصرفِ بدهیِ‌راننده همان بدهی را پاک می‌کرد.
            ApplyFormSettlement(expense, model);

            var settlementCheck = _settlementValidator.Check(expense);
            if (!settlementCheck.IsValid)
            {
                ModelState.AddModelError(settlementCheck.MemberName ?? string.Empty, settlementCheck.Message!);
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }

                model.Description = normalizedDescription;
                model.ManualExpenseTypeName = manualExpenseTypeName;
                await PopulateLookupsAsync(createModel: model);
                ViewData["ExpenseFormMode"] = "Edit";
                return View("Create", model);
            }

            // Same rule as a sale: once this expense's general-ledger journal is posted, a
            // financial edit would change the operational ledger alone. Only the description
            // stays editable; financial corrections go through cancellation and a replacement.
            if (await HasPostedExpenseJournalAsync(expense.Id) && HasFinancialChange(expense))
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }

                ModelState.AddModelError(string.Empty,
                    "برای این مصرف سند حسابداری ثبت شده است؛ فقط توضیحات قابل ویرایش است. برای اصلاح مبلغ یا مشخصات مالی، مصرف را لغو و مصرف جایگزین ثبت کنید.");
                model.Description = normalizedDescription;
                model.ManualExpenseTypeName = manualExpenseTypeName;
                await PopulateLookupsAsync(createModel: model);
                ViewData["ExpenseFormMode"] = "Edit";
                return View("Create", model);
            }

            var ledgerRequest = new ExpenseLedgerRequest
            {
                Expense = expense,
                ExpenseType = expenseType,
                FxRateDate = conversion.EffectiveDate.Date,
                FxRateSource = conversion.SourceDescription,

                // فیلدهایی که مسیر ویرایشِ مصرف هرگز دست نمی‌زد، عیناً از سطر موجود می‌آیند.
                CarryFrom = ledgerEntry
            };

            ledgerEntry = ledgerEntry is null
                ? _expenseLedger.Post(ledgerRequest)
                : _expenseLedger.Apply(ledgerEntry, ledgerRequest);

            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                nameof(ExpenseTransaction),
                expense.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("ExpenseTypeId", previousExpense.ExpenseTypeId, expense.ExpenseTypeId),
                    ("ContractId", previousExpense.ContractId, expense.ContractId),
                    ("ShipmentId", previousExpense.ShipmentId, expense.ShipmentId),
                    ("TruckDispatchId", previousExpense.TruckDispatchId, expense.TruckDispatchId),
                    ("TransportLegId", previousExpense.TransportLegId, expense.TransportLegId),
                    ("ServiceProviderId", previousExpense.ServiceProviderId, expense.ServiceProviderId),
                    ("OperationalAssetId", previousExpense.OperationalAssetId, expense.OperationalAssetId),
                    ("ExpenseDate", previousExpense.ExpenseDate, expense.ExpenseDate),
                    ("Amount", previousExpense.Amount, expense.Amount),
                    ("Currency", previousExpense.Currency, expense.Currency),
                    ("AppliedFxRateToUsd", previousExpense.AppliedFxRateToUsd, expense.AppliedFxRateToUsd),
                    ("AmountUsd", previousExpense.AmountUsd, expense.AmountUsd),
                    ("Description", previousExpense.Description, expense.Description)));

            await _audit.LogAsync(
                nameof(LedgerEntry),
                ledgerEntry.Id,
                ledgerIsNew ? AuditAction.Insert : AuditAction.Update,
                diff: ledgerIsNew || previousLedger is null
                    ? AuditDiffFormatter.ForCreate(
                        ("EntryDate", ledgerEntry.EntryDate),
                        ("Side", ledgerEntry.Side),
                        ("AmountUsd", ledgerEntry.AmountUsd),
                        ("SourceAmount", ledgerEntry.SourceAmount),
                        ("SourceCurrencyCode", ledgerEntry.SourceCurrencyCode),
                        ("AppliedFxRateToUsd", ledgerEntry.AppliedFxRateToUsd),
                        ("SourceType", ledgerEntry.SourceType),
                        ("SourceId", ledgerEntry.SourceId),
                        ("Reference", ledgerEntry.Reference))
                    : AuditDiffFormatter.ForUpdate(
                        ("EntryDate", previousLedger.EntryDate, ledgerEntry.EntryDate),
                        ("Side", previousLedger.Side, ledgerEntry.Side),
                        ("AmountUsd", previousLedger.AmountUsd, ledgerEntry.AmountUsd),
                        ("SourceAmount", previousLedger.SourceAmount, ledgerEntry.SourceAmount),
                        ("SourceCurrencyCode", previousLedger.SourceCurrencyCode, ledgerEntry.SourceCurrencyCode),
                        ("AppliedFxRateToUsd", previousLedger.AppliedFxRateToUsd, ledgerEntry.AppliedFxRateToUsd),
                        ("AppliedFxRateDate", previousLedger.AppliedFxRateDate, ledgerEntry.AppliedFxRateDate),
                        ("AppliedFxRateSource", previousLedger.AppliedFxRateSource, ledgerEntry.AppliedFxRateSource),
                        ("Description", previousLedger.Description, ledgerEntry.Description),
                        ("SourceType", previousLedger.SourceType, ledgerEntry.SourceType),
                        ("SourceId", previousLedger.SourceId, ledgerEntry.SourceId),
                        ("Reference", previousLedger.Reference, ledgerEntry.Reference),
                        ("ContractId", previousLedger.ContractId, ledgerEntry.ContractId),
                        ("ServiceProviderId", previousLedger.ServiceProviderId, ledgerEntry.ServiceProviderId),
                        ("ShipmentId", previousLedger.ShipmentId, ledgerEntry.ShipmentId)));

            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to update expense transaction {ExpenseTransactionId}.", id);
            ModelState.AddModelError(string.Empty, "ویرایش هزینه انجام نشد. دوباره تلاش کنید.");
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        TempData["ok"] = "هزینه با موفقیت ویرایش شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var redirectUrl))
        {
            return Redirect(redirectUrl);
        }

        return RedirectToAction(nameof(Details), new { id = expense.Id });
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var expense = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.TransportLeg)
            .Include(e => e.ServiceProvider)
            .Include(e => e.OperationalAsset)
            .Include(e => e.ExpenseBatch)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
            return NotFound();

        var ledgerEntry = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && l.SourceId == id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        return View(new ExpenseDetailsViewModel
        {
            Id = expense.Id,
            IsCancelled = expense.IsCancelled,
            CustomsDeclarationId = expense.CustomsDeclarationId,
            ExpenseDate = expense.ExpenseDate,
            ExpenseTypeName = expense.ExpenseType?.NamePersian ?? expense.ExpenseType?.Name ?? string.Empty,
            ContractId = expense.ContractId,
            ContractNumber = expense.Contract?.ContractNumber,
            ShipmentId = expense.ShipmentId,
            ShipmentCode = expense.Shipment?.ShipmentCode,
            TruckDispatchLabel = expense.TruckDispatch is null
                ? null
                : $"#{expense.TruckDispatch.Id} - {expense.TruckDispatch.Truck?.PlateNumber ?? "بدون پلاک"}",
            TransportLegLabel = expense.TransportLeg is null
                ? null
                : $"#{expense.TransportLeg.Id} - {expense.TransportLeg.WagonNumber ?? expense.TransportLeg.RwbNo ?? "Transport leg"}",
            TransportLegId = expense.TransportLegId,
            ServiceProviderId = expense.ServiceProviderId,
            ServiceProviderName = expense.ServiceProvider?.Name,
            OperationalAssetId = expense.OperationalAssetId,
            OperationalAssetName = expense.OperationalAsset is null
                ? null
                : $"{expense.OperationalAsset.AssetCode} - {expense.OperationalAsset.Name}",
            Amount = expense.Amount,
            Currency = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            AmountUsd = expense.AmountUsd,
            Description = expense.Description,
            LedgerEntryId = ledgerEntry?.Id,
            LedgerReference = ledgerEntry?.Reference,
            LedgerDescription = ledgerEntry?.Description,
            LedgerAmountUsd = ledgerEntry?.AmountUsd,
            LedgerSideName = ledgerEntry is null ? null : UiText.LedgerSideName(HttpContext, ledgerEntry.Side),
            ExpenseBatchId = expense.ExpenseBatchId,
            ExpenseBatchNumber = expense.ExpenseBatch?.BatchNumber
        });
    }

    private async Task<bool> ShipmentAllowsContractAsync(int shipmentId, int? primaryContractId, int contractId)
    {
        if (primaryContractId == contractId)
        {
            return true;
        }

        var hasShipmentContracts = await _db.ShipmentContracts
            .AsNoTracking()
            .AnyAsync(sc => sc.ShipmentId == shipmentId);

        if (!hasShipmentContracts && !primaryContractId.HasValue)
        {
            return true;
        }

        return await _db.ShipmentContracts
            .AsNoTracking()
            .AnyAsync(sc => sc.ShipmentId == shipmentId && sc.ContractId == contractId);
    }

    private async Task<ExpenseType?> FindExpenseTypeByManualNameAsync(string manualExpenseTypeName)
    {
        if (string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            return null;
        }

        var normalizedName = manualExpenseTypeName.Trim();
        return await _db.ExpenseTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.IsActive
                && (e.Name == normalizedName || e.NamePersian == normalizedName));
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

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CustomsBatch(int? contractId = null, int? dispatchId = null, string? returnUrl = null)
    {
        var expenseTypes = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .ToListAsync();

        var model = new CustomsBatchViewModel
        {
            ExpenseDate = _businessClock.Today,
            Currency = "AFN",
            ContractId = contractId ?? 0,
            TruckDispatchId = dispatchId,
            ReturnUrl = returnUrl,
            Rows = expenseTypes.Select(e => new CustomsBatchRowInput
            {
                ExpenseTypeId = e.Id,
                ExpenseTypeName = e.NamePersian ?? e.Name
            }).ToList()
        };

        await PopulateCustomsBatchLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CustomsBatch(CustomsBatchViewModel model)
    {
        if (model.ContractId <= 0)
            ModelState.AddModelError(nameof(model.ContractId), "انتخاب قرارداد الزامی است.");

        var activeRows = (model.Rows ?? []).Where(r => r.Amount.HasValue && r.Amount.Value > 0).ToList();
        if (activeRows.Count == 0)
            ModelState.AddModelError(string.Empty, "حداقل یک ردیف با مبلغ بزرگ‌تر از صفر وارد کنید.");

        if (!ModelState.IsValid)
        {
            await PopulateCustomsBatchLookupsAsync(model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.ExpenseDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            await PopulateCustomsBatchLookupsAsync(model);
            return View(model);
        }

        var expenseTypeIds = activeRows.Select(r => r.ExpenseTypeId).Distinct().ToList();
        var expenseTypeMap = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => expenseTypeIds.Contains(e.Id) && e.IsActive)
            .ToDictionaryAsync(e => e.Id);

        var descriptionBase = string.IsNullOrWhiteSpace(model.Description)
            ? $"مصارف گمرکی {DateDisplay.Date(model.ExpenseDate)}"
            : model.Description.Trim();

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
            transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            int savedCount = 0;
            foreach (var row in activeRows)
            {
                if (!expenseTypeMap.TryGetValue(row.ExpenseTypeId, out var expenseType)) continue;

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = row.ExpenseTypeId,
                    ContractId = model.ContractId > 0 ? model.ContractId : null,
                    TruckDispatchId = model.TruckDispatchId,
                    ExpenseDate = model.ExpenseDate,
                    Amount = row.Amount!.Value,
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = conversion.ConvertToBase(row.Amount.Value),
                    Description = descriptionBase,
                    // Phase 1 — مسئول مصارف گمرکی (فقط ثبت/نمایش، به Ledger اثر ندارد).
                    CostResponsibility = model.CostResponsibility
                };

                // PTG-P1-04 — هویت تسویه. این فرم نه طرف‌حساب می‌گیرد و نه حساب نقدی، پس
                // مصارف گمرکی همان چیزی می‌مانند که دفتر کل امروز از آن‌ها می‌سازد: بدونِ
                // سطرِ طرف، با حسابِ مقابل از نوعِ مصرف.
                ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);
                _settlementValidator.Validate(expense);

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var ledgerEntry = _expenseLedger.Post(new ExpenseLedgerRequest
                {
                    Expense = expense,
                    Description = $"مصارف گمرکی {expenseType.NamePersian ?? expenseType.Name} - {descriptionBase}",
                    Reference = $"{expenseType.Code}-{expense.Id}",
                    FxRateDate = conversion.EffectiveDate.Date,
                    FxRateSource = conversion.SourceDescription
                });
                await _db.SaveChangesAsync();
                savedCount++;
            }

            if (transaction is not null)
                await transaction.CommitAsync();

            TempData["ok"] = $"{savedCount} ردیف مصارف گمرکی با موفقیت ثبت شد.";

            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                return Redirect(localReturnUrl);

            return RedirectToAction(nameof(Index));
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync();
            _logger.LogError("Failed to save customs batch expenses.");
            ModelState.AddModelError(string.Empty, "خطا در ثبت هزینه‌ها. دوباره تلاش کنید.");
        }

        await PopulateCustomsBatchLookupsAsync(model);
        return View(model);
    }

    // ------------------------------------------------------------------------------
    // ثبت مصرف گروهی — یک رکورد اصلی (ExpenseBatch) + سهم هر عملیات به‌صورت
    // ExpenseTransaction عادی با Ledger خودش؛ هیچ منطق مالی موازی ساخته نمی‌شود.
    // ------------------------------------------------------------------------------

    private static string LegVehicleKind(LoadingTransportType type) => type switch
    {
        LoadingTransportType.Wagon => "واگن",
        LoadingTransportType.Truck => "موتر",
        LoadingTransportType.Vessel => "کشتی",
        _ => "نامشخص"
    };

    private static string BuildRoute(string? source, string? destination, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(destination))
        {
            return $"{source ?? "؟"} ← {destination ?? "؟"}";
        }

        return string.IsNullOrWhiteSpace(fallback) ? "-" : fallback.Trim();
    }

    // دیسپچ موترِ ساخته‌شده روی رسیدِ «ادامهٔ حمل» فقط رکورد سازگاری است و همان عملیات
    // به‌صورت «حمل از موجودی» (حمل فرزند) در لیست هست؛ نمایش هر دو یعنی عملیات تکراری و
    // چسبیدن مصرف به حمل والد.
    private IQueryable<int> ContinuedTransferReceiptIds()
        => TransportChainProjection.ContinuedTransferReceiptIds(_db);

    private async Task<List<GroupExpenseOperationItem>> LoadInProgressOperationsAsync()
    {
        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.Status == InventoryTransportLegStatus.Loaded
                        || l.Status == InventoryTransportLegStatus.InTransit)
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                l.WagonNumber,
                l.RwbNo,
                TruckPlate = l.Truck != null ? l.Truck.PlateNumber : null,
                SourceName = l.SourceTerminal != null ? l.SourceTerminal.Name : null,
                DestinationName = l.DestinationTerminal != null
                    ? l.DestinationTerminal.Name
                    : (l.DestinationLocation != null ? l.DestinationLocation.Name : null),
                l.RouteDescription,
                ProductName = l.Product != null ? l.Product.Name : null,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null,
                l.QuantityMt,
                l.Status,
                l.LoadedDate
            })
            .ToListAsync();

        var continuedReceiptIds = ContinuedTransferReceiptIds();

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                        && !(d.InventoryTransportReceiptId != null
                             && continuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)))
            .OrderByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id,
                TruckPlate = d.Truck != null ? d.Truck.PlateNumber : null,
                DestinationName = d.DestinationLocation != null ? d.DestinationLocation.Name : null,
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : null,
                ProductName = d.Product != null ? d.Product.Name : null,
                d.LoadedQuantityMt,
                d.Status,
                d.DispatchDate
            })
            .ToListAsync();

        var items = new List<GroupExpenseOperationItem>();
        var loadingSources = await new CargoSourceQueryService(_db).LoadLoadingSourcesAsync();
        items.AddRange(loadingSources.Where(l => CargoOperationEligibility.Evaluate(l, CargoAction.Expense).Allowed)
            .Select(l => new GroupExpenseOperationItem
            {
                Kind = "Loading", Id = l.Id, OperationLabel = "بارگیری",
                VehicleKind = l.VehicleLabel, Number = l.Number, Route = l.Route,
                ProductName = l.ProductName, ContractNumber = l.ContractNumber,
                QuantityMt = l.OriginalQuantityMt, StatusLabel = l.RemainingQuantityMt > 0 ? "مانده دارد" : "دریافت یا تخصیص شده",
                MoveDate = l.Date
            }));

        items.AddRange(legs.Select(l => new GroupExpenseOperationItem
        {
            Kind = "Leg",
            Id = l.Id,
            OperationLabel = "حمل از موجودی",
            VehicleKind = LegVehicleKind(l.TransportType),
            // نمبر واقعی وسیله (واگن یا پلیت موتر) مبنا است؛ سیمیر/CMR فقط وقتی نمبر وسیله نباشد.
            Number = l.WagonNumber ?? l.TruckPlate ?? l.RwbNo ?? $"#{l.Id}",
            AltNumber = l.RwbNo,
            Route = BuildRoute(l.SourceName, l.DestinationName, l.RouteDescription),
            ProductName = l.ProductName ?? "",
            ContractNumber = l.ContractNumber ?? "",
            QuantityMt = l.QuantityMt,
            StatusLabel = l.Status == InventoryTransportLegStatus.InTransit ? "در راه" : "بارگیری‌شده",
            MoveDate = l.LoadedDate
        }));

        items.AddRange(dispatches.Select(d => new GroupExpenseOperationItem
        {
            Kind = "Dispatch",
            Id = d.Id,
            OperationLabel = "ارسال موتر",
            VehicleKind = "موتر",
            Number = d.TruckPlate ?? $"#{d.Id}",
            Route = BuildRoute(d.ContractNumber, d.DestinationName, null),
            ProductName = d.ProductName ?? "",
            ContractNumber = d.ContractNumber ?? "",
            QuantityMt = d.LoadedQuantityMt,
            StatusLabel = d.Status == DispatchStatus.InTransit ? "در راه" : "بارگیری‌شده",
            MoveDate = d.DispatchDate
        }));

        return items
            .OrderByDescending(i => i.MoveDate)
            .ThenByDescending(i => i.Id)
            .ToList();
    }

    private async Task PopulateGroupExpenseLookupsAsync(GroupExpenseCreateViewModel model)
    {
        var expenseTypeOptions = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .Select(e => new { e.Id, DisplayName = (e.NamePersian ?? e.Name) + " (" + e.Code + ")" })
            .ToListAsync();
        ViewBag.ExpenseTypes = new SelectList(expenseTypeOptions, "Id", "DisplayName",
            model.ExpenseTypeId > 0 ? model.ExpenseTypeId : null);

        ViewBag.ExpenseTypeNames = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.NamePersian ?? e.Name)
            .Select(e => e.NamePersian ?? e.Name)
            .ToListAsync();

        var providers = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();
        ViewBag.ServiceProviders = new SelectList(providers, "Id", "Name", model.ServiceProviderId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code)
                .Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
        ViewBag.CashAccounts = new SelectList(await _db.CashAccounts.AsNoTracking()
            .Where(c => c.IsActive).OrderBy(c => c.Name)
            .Select(c => new { c.Id, Label = c.Name + " (" + c.Currency + ")" }).ToListAsync(),
            "Id", "Label", model.CashAccountId);
    }

    // محاسبهٔ سهم هر عملیات سمت سرور (به ورودی کلاینت اعتماد نمی‌شود).
    // خطای rounding روی سطر آخر تصحیح می‌شود تا Σ سهم‌ها == مبلغ کل بماند.
    private static bool TryComputeShares(
        GroupExpenseLineInput line,
        IReadOnlyList<(GroupExpenseSelectedInput Input, decimal QuantityMt, decimal? ManualAmount, decimal? RatePerTon)> items,
        out List<decimal> shares,
        out decimal total,
        out string? error)
    {
        shares = [];
        total = 0m;
        error = null;
        var count = items.Count;

        switch (line.AllocationMethod)
        {
            case ExpenseAllocationMethod.FixedPerOperation:
                if (!line.AmountPerOperation.HasValue || line.AmountPerOperation.Value <= 0)
                {
                    error = "مبلغ برای هر عملیات باید بزرگ‌تر از صفر باشد.";
                    return false;
                }
                shares = Enumerable.Repeat(Math.Round(line.AmountPerOperation.Value, 2), count).ToList();
                total = shares.Sum();
                return true;

            case ExpenseAllocationMethod.EqualSplit:
                if (!line.TotalAmount.HasValue || line.TotalAmount.Value <= 0)
                {
                    error = "مبلغ کل برای تقسیم باید بزرگ‌تر از صفر باشد.";
                    return false;
                }
                total = Math.Round(line.TotalAmount.Value, 2);

                var equal = Math.Round(total / count, 2);
                shares = Enumerable.Repeat(equal, count).ToList();

                // تصحیح rounding: تفاوت روی آخرین سهم.
                var diff = total - shares.Sum();
                shares[^1] = Math.Round(shares[^1] + diff, 2);
                if (shares.Any(s => s <= 0))
                {
                    error = "با این مبلغ و روش تقسیم، سهم بعضی عملیات‌ها صفر یا منفی می‌شود.";
                    return false;
                }
                return true;

            case ExpenseAllocationMethod.ByQuantity:
                // سهم هر عملیات = نرخ فی تن × مقدار (تن) همان حمل. مثال: نرخ ۲ و حمل ۴۰ تن → سهم ۸۰.
                // نرخ هر عملیات می‌تواند جداگانه (اکسل/دستی) داده شود؛ در نبودِ آن، نرخ مشترک خط.
                var sharedRate = line.RatePerTon is > 0 ? line.RatePerTon.Value : (decimal?)null;
                if (!sharedRate.HasValue && items.Any(i => i.RatePerTon is not > 0))
                {
                    error = "نرخ فی تن باید بزرگ‌تر از صفر باشد.";
                    return false;
                }
                if (items.Any(i => i.QuantityMt <= 0))
                {
                    error = "مقدار (تن) بعضی از عملیات‌های انتخاب‌شده صفر است؛ محاسبه بر اساس مقدار ممکن نیست.";
                    return false;
                }
                shares = items
                    .Select(i => Math.Round((i.RatePerTon is > 0 ? i.RatePerTon!.Value : sharedRate!.Value) * i.QuantityMt, 2))
                    .ToList();
                total = shares.Sum();
                return true;

            case ExpenseAllocationMethod.Manual:
                if (items.Any(i => !i.ManualAmount.HasValue || i.ManualAmount.Value <= 0))
                {
                    error = "در روش دستی، مبلغ هر عملیات باید بزرگ‌تر از صفر وارد شود.";
                    return false;
                }
                shares = items.Select(i => Math.Round(i.ManualAmount!.Value, 2)).ToList();
                total = shares.Sum();
                return true;

            default:
                error = "روش تقسیم نامعتبر است.";
                return false;
        }
    }

    // مصارف گروهی فعال (لغونشده) برای نمایش و لغو در فرم ثبت مصرف گروهی.
    private async Task<List<GroupExpenseBatchListItem>> LoadActiveGroupBatchesAsync()
    {
        return await _db.ExpenseBatches
            .AsNoTracking()
            .Where(b => !b.IsCancelled)
            .OrderByDescending(b => b.ExpenseDate)
            .ThenByDescending(b => b.Id)
            .Select(b => new GroupExpenseBatchListItem
            {
                Id = b.Id,
                BatchNumber = b.BatchNumber,
                ExpenseTypeName = b.ExpenseType != null ? (b.ExpenseType.NamePersian ?? b.ExpenseType.Name) : "-",
                ServiceProviderName = b.ServiceProvider != null ? b.ServiceProvider.Name : null,
                ExpenseDate = b.ExpenseDate,
                Currency = b.Currency,
                TotalAmount = b.TotalAmount,
                TotalAmountUsd = b.TotalAmountUsd,
                OperationCount = b.OperationCount
            })
            .ToListAsync();
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroup(string? returnUrl = null)
    {
        var model = new GroupExpenseCreateViewModel
        {
            ExpenseDate = _businessClock.Today,
            Currency = SystemCurrency.BaseCurrencyCode,
            ReturnUrl = returnUrl
        };

        await PopulateGroupExpenseLookupsAsync(model);
        ViewBag.Operations = await LoadInProgressOperationsAsync();
        ViewBag.RegisteredGroupBatches = await LoadActiveGroupBatchesAsync();
        return View(model);
    }

    // Reads a manual-split sheet (نمبر وسیله | مقدار مصرف) for the group-expense wizard
    // and returns the rows as JSON. Nothing is stored; the wizard matches each number to
    // a selected operation on the client.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportGroupAmountsExcel(IFormFile? file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return Json(new { ok = false, message = "فایلی انتخاب نشده است." });
        }
        if (file.Length > 5 * 1024 * 1024)
        {
            return Json(new { ok = false, message = "حجم فایل زیاد است (حداکثر ۵ مگابایت)." });
        }

        IReadOnlyList<GroupExpenseAmountImportRow> parsed;
        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(file, ct);
            parsed = GroupExpenseAmountWorkbookParser.Parse(workbook.Stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Group expense manual amount import failed to parse.");
            return Json(new { ok = false, message = "خواندن فایل اکسل ناموفق بود: " + ex.Message });
        }

        if (parsed.Count == 0)
        {
            return Json(new { ok = false, message = "در فایل هیچ ردیف معتبری (نمبر وسیله و مبلغ) یافت نشد." });
        }

        var rows = parsed
            .Select(row => new { vehicleNumber = row.VehicleNumber, amount = row.Amount })
            .ToList();

        return Json(new { ok = true, rows });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(
        GroupExpenseCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        var description = model.Description?.Trim() ?? string.Empty;

        async Task<IActionResult> BackToFormAsync()
        {
            model.Description = description;
            await PopulateGroupExpenseLookupsAsync(model);
            ViewBag.Operations = await LoadInProgressOperationsAsync();
            ViewBag.RegisteredGroupBatches = await LoadActiveGroupBatchesAsync();
            return View(model);
        }

        // چند مصرف در یک ثبت. فرم قدیمی (بدون Lines) همان فیلدهای اصلی را یک خط می‌کند.
        var lines = (model.Lines ?? []).Where(l => l is not null).ToList();
        if (lines.Count == 0)
        {
            lines.Add(new GroupExpenseLineInput
            {
                ExpenseTypeId = model.ExpenseTypeId,
                ManualExpenseTypeName = model.ManualExpenseTypeName,
                ServiceProviderId = model.ServiceProviderId,
                AllocationMethod = model.AllocationMethod,
                AmountPerOperation = model.AmountPerOperation,
                TotalAmount = model.TotalAmount,
                RatePerTon = model.RatePerTon,
                Description = model.Description,
                ManualAmounts = (model.Items ?? []).Select(i => i.ManualAmount).ToList()
            });
        }

        // نوع مصرف و شرکت خدماتیِ هر خط جدا اعتبارسنجی می‌شود.
        var lineContexts = new List<(GroupExpenseLineInput Line, ExpenseType? Type, string ManualName, ServiceProviderEntity? Provider, string Description)>();
        for (var li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            var label = lines.Count > 1 ? $"مصرف {li + 1}: " : string.Empty;
            var manualExpenseTypeName = line.ManualExpenseTypeName?.Trim() ?? string.Empty;

            ExpenseType? expenseType = null;
            if (line.ExpenseTypeId.HasValue && line.ExpenseTypeId.Value > 0)
            {
                expenseType = await _db.ExpenseTypes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == line.ExpenseTypeId.Value && e.IsActive);
                if (expenseType is null)
                {
                    ModelState.AddModelError(string.Empty, label + "نوع مصرف انتخاب‌شده معتبر نیست.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
            {
                expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
            }
            else
            {
                ModelState.AddModelError(string.Empty, label + "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
            }

            ServiceProviderEntity? serviceProvider = null;
            if (line.ServiceProviderId.HasValue)
            {
                serviceProvider = await _db.ServiceProviders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == line.ServiceProviderId.Value && p.IsActive);
                if (serviceProvider is null)
                {
                    ModelState.AddModelError(string.Empty, label + "شرکت خدماتی انتخاب‌شده معتبر نیست.");
                }
            }

            var lineDescription = line.Description?.Trim() ?? string.Empty;
            if (lineDescription.Length == 0)
            {
                lineDescription = description;
            }

            lineContexts.Add((line, expenseType, manualExpenseTypeName, serviceProvider, lineDescription));
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        if (model.SettlementMode == ExpenseSettlementMode.Unknown
            || (model.SettlementMode.HasValue && !Enum.IsDefined(model.SettlementMode.Value)))
            ModelState.AddModelError(nameof(model.SettlementMode), "نوع تسویه معتبر نیست.");
        if (model.SettlementMode == ExpenseSettlementMode.Payable
            && lineContexts.Any(c => c.Provider is null))
            ModelState.AddModelError(nameof(model.ServiceProviderId), "برای مصرف پرداخت‌نشده، شرکت خدماتی را مشخص کنید.");
        if (model.SettlementMode == ExpenseSettlementMode.PaidImmediately)
        {
            var cash = await _db.CashAccounts.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.CashAccountId && c.IsActive);
            if (cash is null)
                ModelState.AddModelError(nameof(model.CashAccountId), "صندوق یا بانک فعال را انتخاب کنید.");
            else if (cash.AccountType != CashAccountType.Mixed && SystemCurrency.Normalize(cash.Currency) != model.Currency)
                ModelState.AddModelError(nameof(model.CashAccountId), "ارز مصرف باید با ارز صندوق یا بانک یکسان باشد.");
        }
        else if (model.CashAccountId.HasValue)
            ModelState.AddModelError(nameof(model.CashAccountId), "حساب نقدی فقط برای مصرف پرداخت‌شده انتخاب می‌شود.");

        // انتخاب‌ها: dedupe + بارگذاری عملیات‌های معتبرِ در جریان.
        var selections = (model.Items ?? [])
            .Where(i => i.Id > 0 && (i.Kind == "Leg" || i.Kind == "Dispatch" || i.Kind == "Loading"))
            .GroupBy(i => (i.Kind, i.Id))
            .Select(g => g.First())
            .ToList();

        if (selections.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک عملیات در جریان را انتخاب کنید.");
        }

        var loadingIds = selections.Where(i => i.Kind == "Loading").Select(i => i.Id).ToList();
        var loadingSources = await new CargoSourceQueryService(_db).LoadLoadingSourcesAsync(loadingIds);
        var loadings = loadingSources.Where(l => CargoOperationEligibility.Evaluate(l, CargoAction.Expense).Allowed)
            .ToDictionary(l => l.Id);
        var legIds = selections.Where(i => i.Kind == "Leg").Select(i => i.Id).ToList();
        var dispatchIds = selections.Where(i => i.Kind == "Dispatch").Select(i => i.Id).ToList();

        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => legIds.Contains(l.Id)
                        && (l.Status == InventoryTransportLegStatus.Loaded
                            || l.Status == InventoryTransportLegStatus.InTransit))
            .ToDictionaryAsync(l => l.Id);

        var continuedReceiptIds = ContinuedTransferReceiptIds();

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => dispatchIds.Contains(d.Id)
                        && (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                        && !(d.InventoryTransportReceiptId != null
                             && continuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)))
            .ToDictionaryAsync(d => d.Id);

        if (legs.Count != legIds.Count || dispatches.Count != dispatchIds.Count || loadings.Count != loadingIds.Count)
        {
            ModelState.AddModelError(string.Empty, "بعضی از عملیات‌های انتخاب‌شده دیگر در جریان نیستند. لیست را تازه کنید.");
        }

        // مبلغ دستیِ هر خط هم‌ترتیب با Items پست می‌شود؛ در نبودِ آن، مبلغ خودِ Item مبنا است.
        var itemOrder = (model.Items ?? []).Select(i => (i.Kind, i.Id)).ToList();
        decimal? ManualFor(GroupExpenseLineInput line, GroupExpenseSelectedInput input)
        {
            if (line.ManualAmounts is { Count: > 0 })
            {
                var index = itemOrder.FindIndex(o => o.Kind == input.Kind && o.Id == input.Id);
                if (index >= 0 && index < line.ManualAmounts.Count)
                {
                    return line.ManualAmounts[index];
                }
            }

            return input.ManualAmount;
        }

        // نرخ فی تنِ اختصاصی هر عملیات (روش «بر اساس مقدار»)؛ هم‌ترتیب با Items پست می‌شود.
        decimal? RateFor(GroupExpenseLineInput line, GroupExpenseSelectedInput input)
        {
            if (line.RatePerTonOverrides is { Count: > 0 })
            {
                var index = itemOrder.FindIndex(o => o.Kind == input.Kind && o.Id == input.Id);
                if (index >= 0 && index < line.RatePerTonOverrides.Count)
                {
                    return line.RatePerTonOverrides[index];
                }
            }

            return null;
        }

        var lineShares = new List<(List<decimal> Shares, decimal Total)>();
        if (ModelState.IsValid)
        {
            var resolved = selections
                .Select(i => (Input: i, QuantityMt: i.Kind == "Loading" ? loadings[i.Id].OriginalQuantityMt
                    : i.Kind == "Leg" ? legs[i.Id].QuantityMt : dispatches[i.Id].LoadedQuantityMt))
                .ToList();

            for (var li = 0; li < lineContexts.Count; li++)
            {
                var line = lineContexts[li].Line;
                var label = lineContexts.Count > 1 ? $"مصرف {li + 1}: " : string.Empty;
                var items = resolved
                    .Select(r => (r.Input, r.QuantityMt, ManualAmount: ManualFor(line, r.Input), RatePerTon: RateFor(line, r.Input)))
                    .ToList();

                if (!TryComputeShares(line, items, out var shares, out var totalAmount, out var shareError))
                {
                    ModelState.AddModelError(string.Empty, label + shareError!);
                    continue;
                }

                lineShares.Add((shares, totalAmount));
            }
        }

        if (!ModelState.IsValid)
        {
            return await BackToFormAsync();
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.ExpenseDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            return await BackToFormAsync();
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            // Keep eligibility and cancellation ordered on the same loading row as receipts/transport.
            if (_db.Database.IsRelational() && loadingIds.Count > 0)
            {
                // Lock-only statements: tracking every locked row would make each later save
                // in this transaction re-scan them.
                var lockedLoadingIds = loadingIds.Order().ToArray();
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"LoadingRegisters\" WHERE \"Id\" = ANY ({lockedLoadingIds}) ORDER BY \"Id\" FOR UPDATE");
                var current = await new CargoSourceQueryService(_db).LoadLoadingSourcesAsync(loadingIds);
                if (current.Count != loadingIds.Count || current.Any(l => !CargoOperationEligibility.Evaluate(l, CargoAction.Expense).Allowed))
                    throw new BusinessRuleException("GROUP_EXPENSE_SOURCE_CHANGED", "بارگیری انتخاب‌شده لغو یا بایگانی شده است. فهرست را تازه کنید.");
            }

            if (_db.Database.IsRelational() && legIds.Count > 0)
            {
                var lockedLegIds = legIds.Order().ToArray();
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"InventoryTransportLegs\" WHERE \"Id\" = ANY ({lockedLegIds}) ORDER BY \"Id\" FOR UPDATE");
                var validCount = await _db.InventoryTransportLegs.AsNoTracking().CountAsync(l => legIds.Contains(l.Id)
                    && (l.Status == InventoryTransportLegStatus.Loaded || l.Status == InventoryTransportLegStatus.InTransit));
                if (validCount != legIds.Count)
                    throw new BusinessRuleException("GROUP_EXPENSE_SOURCE_CHANGED", "حمل انتخاب‌شده دیگر در جریان نیست. فهرست را تازه کنید.");
            }
            if (_db.Database.IsRelational() && dispatchIds.Count > 0)
            {
                var lockedDispatchIds = dispatchIds.Order().ToArray();
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"TruckDispatches\" WHERE \"Id\" = ANY ({lockedDispatchIds}) ORDER BY \"Id\" FOR UPDATE");
                var validCount = await _db.TruckDispatches.AsNoTracking().CountAsync(d => dispatchIds.Contains(d.Id)
                    && (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                    && !(d.InventoryTransportReceiptId != null && continuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)));
                if (validCount != dispatchIds.Count)
                    throw new BusinessRuleException("GROUP_EXPENSE_SOURCE_CHANGED", "ارسال موتر انتخاب‌شده دیگر در جریان نیست. فهرست را تازه کنید.");
            }

            // PTG-P0-01 — توکن یک‌بار برای کل ثبت؛ همهٔ خط‌ها در همین Transaction ثبت می‌شوند.
            _formTokens.Stamp(formToken, "Expense.CreateGroup", nameof(ExpenseBatch));

            var createdBatches = new List<ExpenseBatch>();

            for (var li = 0; li < lineContexts.Count; li++)
            {
                var (line, resolvedType, manualExpenseTypeName, serviceProvider, lineDescription) = lineContexts[li];
                var shares = lineShares[li].Shares;
                var totalAmount = lineShares[li].Total;

                var expenseType = resolvedType ?? await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);

                if (expenseType is null)
                {
                    expenseType = new ExpenseType
                    {
                        Code = await BuildManualExpenseTypeCodeAsync(manualExpenseTypeName),
                        Name = manualExpenseTypeName,
                        NamePersian = manualExpenseTypeName,
                        Category = "Transport",
                        IsActive = true
                    };
                    _db.ExpenseTypes.Add(expenseType);
                    await _db.SaveChangesAsync();
                }

                var descriptionBase = string.IsNullOrWhiteSpace(lineDescription)
                    ? $"مصرف گروهی {expenseType.NamePersian ?? expenseType.Name} {DateDisplay.Date(model.ExpenseDate)}"
                    : lineDescription;

                var batch = new ExpenseBatch
                {
                    ExpenseTypeId = expenseType.Id,
                    ServiceProviderId = serviceProvider?.Id,
                    ExpenseDate = model.ExpenseDate.Date,
                    AllocationMethod = line.AllocationMethod,
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    TotalAmount = totalAmount,
                    TotalAmountUsd = conversion.ConvertToBase(totalAmount),
                    OperationCount = selections.Count,
                    Description = descriptionBase
                };

                _db.ExpenseBatches.Add(batch);
                await _db.SaveChangesAsync();

                batch.BatchNumber = $"GEXP-{batch.Id}";
                await _db.SaveChangesAsync();

                for (var i = 0; i < selections.Count; i++)
                {
                    var selection = selections[i];
                    var isLeg = selection.Kind == "Leg";
                    var isLoading = selection.Kind == "Loading";
                    var loading = isLoading ? loadings[selection.Id] : null;
                    var leg = isLeg ? legs[selection.Id] : null;
                    var dispatch = isLeg || isLoading ? null : dispatches[selection.Id];

                    var opLabel = isLoading ? $"بارگیری #{selection.Id}" : isLeg
                        ? $"حمل از موجودی #{selection.Id}"
                        : $"ارسال موتر #{selection.Id}";

                    var expense = new ExpenseTransaction
                    {
                        ExpenseTypeId = expenseType.Id,
                        ExpenseBatchId = batch.Id,
                        ContractId = isLoading ? loading!.ContractId : isLeg ? leg!.SourcePurchaseContractId : dispatch!.ContractId,
                        ShipmentId = isLeg ? leg!.ShipmentId : null,
                        TransportLegId = isLeg ? selection.Id : null,
                        TruckDispatchId = isLeg || isLoading ? null : selection.Id,
                        LoadingRegisterId = isLoading ? selection.Id : null,
                        ServiceProviderId = serviceProvider?.Id,
                        ExpenseDate = model.ExpenseDate.Date,
                        Amount = shares[i],
                        Currency = conversion.SourceCurrencyCode,
                        AppliedFxRateToUsd = conversion.AppliedRateToBase,
                        AmountUsd = conversion.ConvertToBase(shares[i]),
                        Description = $"{descriptionBase} | {batch.BatchNumber} — {opLabel}",
                        CostResponsibility = model.CostResponsibility
                    };

                    using var row = new SavedRowTrackingScope(_db);
                    await _groupExpensePosting.PostOperationAsync(expense, expenseType, conversion,
                        model.SettlementMode, model.CashAccountId);
                    row.ReleaseSaved();
                }

                // Rounded row conversions are the actual posted USD total; do not advertise
                // a different header value after multi-source or multi-operation splitting.
                // Every row is saved (4-decimal amounts) inside this transaction.
                batch.TotalAmountUsd = await _db.ExpenseTransactions
                    .Where(e => e.ExpenseBatchId == batch.Id).SumAsync(e => e.AmountUsd);

                await _audit.LogAndSaveAsync(
                    nameof(ExpenseBatch),
                    batch.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("BatchNumber", batch.BatchNumber),
                        ("ExpenseTypeId", batch.ExpenseTypeId),
                        ("ServiceProviderId", batch.ServiceProviderId),
                        ("ExpenseDate", batch.ExpenseDate),
                        ("AllocationMethod", batch.AllocationMethod),
                        ("Currency", batch.Currency),
                        ("TotalAmount", batch.TotalAmount),
                        ("TotalAmountUsd", batch.TotalAmountUsd),
                        ("OperationCount", batch.OperationCount)));

                createdBatches.Add(batch);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            if (createdBatches.Count == 1)
            {
                TempData["ok"] = $"مصرف گروهی {createdBatches[0].BatchNumber} برای {selections.Count} عملیات ثبت شد.";
                return RedirectToAction(nameof(GroupDetails), new { id = createdBatches[0].Id });
            }

            TempData["ok"] = $"{createdBatches.Count} مصرف گروهی برای {selections.Count} عملیات ثبت شد: "
                + string.Join("، ", createdBatches.Select(b => b.BatchNumber));
            return RedirectToAction(nameof(CreateGroup), new { returnUrl = model.ReturnUrl });
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            TempData["err"] = "این مصرف گروهی قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to create group expense batch.");
            ModelState.AddModelError(string.Empty, "ثبت مصرف گروهی انجام نشد. دوباره تلاش کنید.");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }

        return await BackToFormAsync();
    }

    private static string AllocationMethodName(ExpenseAllocationMethod method) => method switch
    {
        ExpenseAllocationMethod.FixedPerOperation => "مبلغ برای هر عملیات",
        ExpenseAllocationMethod.EqualSplit => "تقسیم مساوی",
        ExpenseAllocationMethod.ByQuantity => "بر اساس مقدار",
        ExpenseAllocationMethod.Manual => "دستی",
        _ => method.ToString()
    };

    public async Task<IActionResult> GroupDetails(int id)
    {
        var batch = await _db.ExpenseBatches
            .AsNoTracking()
            .Include(b => b.ExpenseType)
            .Include(b => b.ServiceProvider)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (batch is null)
        {
            return NotFound();
        }

        var shares = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ExpenseBatchId == batch.Id)
            .OrderBy(e => e.Id)
            .Select(e => new GroupExpenseShareViewModel
            {
                ExpenseId = e.Id,
                OperationLabel = e.LoadingRegisterId.HasValue ? "بارگیری" : e.TransportLegId.HasValue ? "حمل از موجودی" : "ارسال موتر",
                VehicleKind = e.LoadingRegister != null
                    ? (e.LoadingRegister.TransportType == LoadingTransportType.Wagon ? "واگن"
                        : e.LoadingRegister.TransportType == LoadingTransportType.Truck ? "موتر" : "کشتی")
                    : e.TransportLeg != null
                    ? (e.TransportLeg.TransportType == LoadingTransportType.Wagon ? "واگن"
                        : e.TransportLeg.TransportType == LoadingTransportType.Truck ? "موتر" : "نامشخص")
                    : "موتر",
                Number = e.LoadingRegister != null
                    ? (e.LoadingRegister.WagonNumber ?? (e.LoadingRegister.Truck != null ? e.LoadingRegister.Truck.PlateNumber : null)
                        ?? e.LoadingRegister.RwbNo ?? ("#" + e.LoadingRegisterId))
                    : e.TransportLeg != null
                    ? (e.TransportLeg.WagonNumber ?? e.TransportLeg.RwbNo ?? ("#" + e.TransportLegId))
                    : (e.TruckDispatch != null && e.TruckDispatch.Truck != null
                        ? e.TruckDispatch.Truck.PlateNumber
                        : ("#" + e.TruckDispatchId)),
                Route = e.LoadingRegister != null
                    ? (e.LoadingRegister.RouteDescription ?? e.LoadingRegister.DestinationName ?? "-")
                    : e.TransportLeg != null
                    ? ((e.TransportLeg.SourceTerminal != null ? e.TransportLeg.SourceTerminal.Name : "؟")
                        + " ← "
                        + (e.TransportLeg.DestinationTerminal != null ? e.TransportLeg.DestinationTerminal.Name
                            : e.TransportLeg.DestinationLocation != null ? e.TransportLeg.DestinationLocation.Name : "؟"))
                    : (e.TruckDispatch != null && e.TruckDispatch.DestinationLocation != null
                        ? e.TruckDispatch.DestinationLocation.Name
                        : "-"),
                QuantityMt = e.LoadingRegister != null ? e.LoadingRegister.LoadedQuantityMt : e.TransportLeg != null
                    ? e.TransportLeg.QuantityMt
                    : (e.TruckDispatch != null ? e.TruckDispatch.LoadedQuantityMt : 0m),
                Amount = e.Amount,
                AmountUsd = e.AmountUsd,
                IsCancelled = e.IsCancelled,
                TruckDispatchId = e.TruckDispatchId,
                TransportLegId = e.TransportLegId,
                LoadingRegisterId = e.LoadingRegisterId
            })
            .ToListAsync();

        return View(new GroupExpenseDetailsViewModel
        {
            Id = batch.Id,
            BatchNumber = batch.BatchNumber,
            ExpenseTypeName = batch.ExpenseType != null ? batch.ExpenseType.NamePersian ?? batch.ExpenseType.Name : "-",
            ServiceProviderName = batch.ServiceProvider?.Name,
            ExpenseDate = batch.ExpenseDate,
            AllocationMethodName = AllocationMethodName(batch.AllocationMethod),
            Currency = batch.Currency,
            AppliedFxRateToUsd = batch.AppliedFxRateToUsd,
            TotalAmount = batch.TotalAmount,
            TotalAmountUsd = batch.TotalAmountUsd,
            Description = batch.Description,
            IsCancelled = batch.IsCancelled,
            Shares = shares
        });
    }

    private Task<bool> HasPostedExpenseJournalAsync(int expenseId)
    {
        var sourceEventId = Services.Accounting.ExpenseAccountingAdapter.BuildCreatedSourceEventId(expenseId);
        return _db.JournalEntries.AsNoTracking().AnyAsync(j =>
            j.SourceModule == Services.Accounting.ExpenseAccountingAdapter.SourceModule
            && j.SourceEventId == sourceEventId);
    }

    // Columns an edit may change without touching the posted journal: the description and
    // technical values (concurrency version, audit stamps) that the save itself maintains.
    private static readonly HashSet<string> NonFinancialExpenseProperties =
    [
        nameof(ExpenseTransaction.Description),
        nameof(ExpenseTransaction.Version),
        nameof(BaseEntity.CreatedAtUtc),
        nameof(BaseEntity.UpdatedAtUtc),
        nameof(BaseEntity.CreatedByUserId),
        nameof(BaseEntity.UpdatedByUserId)
    ];

    private bool HasFinancialChange(ExpenseTransaction expense)
    {
        var entry = _db.Entry(expense);
        entry.DetectChanges();
        return entry.Properties.Any(p => p.IsModified && !NonFinancialExpenseProperties.Contains(p.Metadata.Name));
    }

    // هستهٔ مشترک لغو یک بچ: هر سهم IsCancelled + سند معکوس Ledger (همان مسیر امن لغو تکی).
    // SaveChanges/Transaction با فراخواننده است.
    private async Task<int> ApplyGroupCancellationAsync(ExpenseBatch batch)
    {
        var expenseIds = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => e.ExpenseBatchId == batch.Id && !e.IsCancelled)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();
        var originalLedgers = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && expenseIds.Contains(l.SourceId))
            .ToListAsync();
        var ledgerBySource = originalLedgers
            .GroupBy(l => l.SourceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Id).First());

        var cancelledCount = 0;
        foreach (var expenseId in expenseIds)
        {
            if (!ledgerBySource.TryGetValue(expenseId, out var originalLedger))
            {
                continue; // بدون سند مالی؛ الگوی لغو تکی هم در این حالت لغو نمی‌کند.
            }

            // Each share is loaded and released once saved, so a large batch stays linear.
            using var row = new SavedRowTrackingScope(_db, includeLoaded: true);
            var expense = await _db.ExpenseTransactions.SingleAsync(e => e.Id == expenseId);

            // مرحله ۵ — Reversal قبل از علامت‌خوردن IsCancelled (مانند مسیر لغو تکی).
            if (_expenseAccounting is not null)
            {
                await _expenseAccounting.TryPostExpenseReversalAsync(expense);
            }

            expense.IsCancelled = true;
            await _ledger.ReverseAsync(
                originalLedger,
                _businessClock.Today,
                $"لغو مصرف گروهی {batch.BatchNumber} - هزینه #{expense.Id} | {originalLedger.Description}",
                $"EXP-{expense.Id}");
            cancelledCount++;
            row.ReleaseSaved();
        }

        batch.IsCancelled = true;
        return cancelledCount;
    }

    // لغو گروهی: همان مسیر امنِ لغو تکی برای هر سهم (IsCancelled + سند معکوس Ledger).
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelGroup(int id, string? returnUrl = null)
    {
        IActionResult BackToCaller()
            => !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true
                ? Redirect(returnUrl)
                : RedirectToAction(nameof(GroupDetails), new { id });

        var batch = await _db.ExpenseBatches
            .FirstOrDefaultAsync(b => b.Id == id);

        if (batch is null)
        {
            return NotFound();
        }

        if (batch.IsCancelled)
        {
            TempData["ok"] = "این مصرف گروهی قبلاً لغو شده است.";
            return BackToCaller();
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var cancelledCount = await ApplyGroupCancellationAsync(batch);
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(ExpenseBatch),
                batch.Id,
                AuditAction.Update,
                diff: $"CancelGroup: {cancelledCount} share(s) cancelled");

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"مصرف گروهی {batch.BatchNumber} با {cancelledCount} سهم لغو شد.";
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to cancel group expense batch {BatchId}.", id);
            TempData["err"] = "لغو مصرف گروهی انجام نشد. دوباره تلاش کنید.";
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }

        return BackToCaller();
    }

    // لغو همهٔ مصارف گروهی فعال — هر بچ با همان هستهٔ امن لغو می‌شود؛ همه در یک تراکنش.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelAllGroups(string? returnUrl = null)
    {
        IActionResult BackToCaller()
            => !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true
                ? Redirect(returnUrl)
                : RedirectToAction(nameof(CreateGroup));

        var batches = await _db.ExpenseBatches
            .Where(b => !b.IsCancelled)
            .OrderBy(b => b.Id)
            .ToListAsync();

        if (batches.Count == 0)
        {
            TempData["ok"] = "مصرف گروهی فعالی برای لغو وجود ندارد.";
            return BackToCaller();
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var totalShares = 0;
            foreach (var batch in batches)
            {
                totalShares += await ApplyGroupCancellationAsync(batch);
            }

            await _db.SaveChangesAsync();

            foreach (var batch in batches)
            {
                await _audit.LogAndSaveAsync(
                    nameof(ExpenseBatch),
                    batch.Id,
                    AuditAction.Update,
                    diff: "CancelAllGroups: batch cancelled");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"{batches.Count:N0} مصرف گروهی با مجموع {totalShares:N0} سهم لغو شد.";
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to cancel all group expense batches.");
            TempData["err"] = "لغو همهٔ مصارف گروهی انجام نشد. دوباره تلاش کنید.";
        }

        return BackToCaller();
    }

    private async Task PopulateCustomsBatchLookupsAsync(CustomsBatchViewModel model)
    {
        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => model.ContractId > 0 && c.Id == model.ContractId ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .Take(LookupLimit)
            .Select(c => new { c.Id, c.ContractName, c.ContractNumber })
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            contracts.Select(c => new ContractLookupOption(
                c.Id,
                ContractUiText.FormatDisplayLabel(c.ContractName, c.ContractNumber))),
            "Id",
            "Display",
            model.ContractId > 0 ? model.ContractId : null);

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .OrderBy(d => model.TruckDispatchId.HasValue && d.Id == model.TruckDispatchId ? 0 : 1)
            .ThenByDescending(d => d.DispatchDate)
            .Take(LookupLimit)
            .Select(d => new { d.Id, Label = $"#{d.Id} - {(d.Truck != null ? d.Truck.PlateNumber : "بدون پلاک")} - {DateDisplay.Date(d.DispatchDate)}" })
            .ToListAsync();

        ViewBag.TruckDispatches = new SelectList(dispatches, "Id", "Label", model.TruckDispatchId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
    }

    private static void NormalizeCreateModel(ExpenseCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.Description = (model.Description ?? string.Empty).Trim();
        model.ManualExpenseTypeName = model.ManualExpenseTypeName?.Trim();
    }
}
