using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IProfitAndLossService _profitAndLoss;
    private readonly ISaleContractAttributionReader _saleAttribution;
    private readonly IPartyBalanceReadService _partyBalances;
    private readonly ISupplierFxSettlementService _supplierFxSettlements;
    private readonly IStockService _stock;
    private readonly IPreSaleReservationService _preSaleReservations;
    private readonly INegativeStockAnalysisService _negativeStock;
    private readonly Services.Accounting.ISystemCompanyProvider _systemCompany;
    private readonly IMemoryCache? _cache;

    private readonly IAfghanistanBusinessClock _businessClock;
    private readonly IGoodsInTransitReader _goodsInTransit;

    public ReportsController(
        ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null,
        IProfitAndLossService? profitAndLoss = null,
        IPartyBalanceReadService? partyBalances = null,
        IStockService? stock = null,
        IPreSaleReservationService? preSaleReservations = null,
        INegativeStockAnalysisService? negativeStock = null,
        IAfghanistanBusinessClock? clock = null,
        IMemoryCache? cache = null,
        Services.Accounting.ISystemCompanyProvider? systemCompany = null,
        ISaleContractAttributionReader? saleAttribution = null,
        ISupplierFxSettlementService? supplierFxSettlements = null,
        IGoodsInTransitReader? goodsInTransit = null)
    {
        _db = db;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        // انتساب فروش به قرارداد خرید فقط از همین مرجع خوانده می‌شود.
        _saleAttribution = saleAttribution ?? new SaleContractAttributionReader(db);
        // سودِ قرارداد (محقق و چرخهٔ کامل) فقط از ProfitAndLossService، با همان مراجعِ خرید و انتساب.
        _profitAndLoss = profitAndLoss ?? new ProfitAndLossService(db, _purchaseAggregation, _saleAttribution);
        _partyBalances = partyBalances ?? new PartyBalanceReadService(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            new PartyDirectory(db));
        _supplierFxSettlements = supplierFxSettlements ?? new SupplierFxSettlementService(db);
        _stock = stock ?? new StockService(db);
        // یک مرجع واحد ساعت کابل برای همهٔ گزارش‌ها و خروجی‌های همین کنترلر.
        _businessClock = clock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _preSaleReservations = preSaleReservations ?? new PreSaleReservationService(db, _businessClock);
        _negativeStock = negativeStock ?? new NegativeStockAnalysisService(db);
        // مرجع مشترک «بارهای در مسیر» با داشبورد موبایل؛ همان ساعت کابلِ همین کنترلر.
        _goodsInTransit = goodsInTransit ?? new GoodsInTransitReader(db, _businessClock);
        // مرجع واحدِ «شرکت مالک سیستم» — گزارش کشتی‌ها سال‌های مالی را فقط از همین شرکت می‌خواند.
        _systemCompany = systemCompany ?? new Services.Accounting.SystemCompanyProvider(db);
        _cache = cache;
    }

    private sealed record LookupOption(int Id, string Name);
    private sealed record TankLookupOption(int Id, string Display);

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

    public IActionResult Index()
    {
        return View(new ReportHubViewModel
        {
            Groups = BuildReportHubGroups()
        });
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> CompanyOverview([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true, includeInventory: true);
        return View(await BuildCompanyFinancialOverviewAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> CashFlow([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true, includeCashAccounts: true);
        return View(await BuildCashFlowReportAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> ReceivablesPayables([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true);
        return View(await BuildReceivablesPayablesReportAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> InventoryOperations([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeInventory: true);
        return View(await BuildInventoryOperationsReportAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> Warnings()
    {
        return View(await BuildReportsWarningsAsync());
    }

    /// <summary>
    /// مرکز گزارشات: هشت دستهٔ اصلی، هر دسته فقط چند گزارش کلیدی. هیچ گزارشی دو بار
    /// در دو دسته تکرار نمی‌شود و هیچ محاسبه‌ای اینجا انجام نمی‌گیرد — فقط مسیر.
    /// Routeهای قدیمی (InventoryOperations، Warnings و غیره) دست‌نخورده باقی می‌مانند؛
    /// این فهرست فقط تعیین می‌کند چه چیزی در مرکز گزارشات دیده شود.
    /// </summary>
    private static IReadOnlyList<ReportHubGroupViewModel> BuildReportHubGroups()
        =>
        [
            new()
            {
                TitleFa = "وضعیت شرکت",
                TitleEn = "Company status",
                Icon = "bi-speedometer2",
                Cards =
                [
                    new()
                    {
                        Action = nameof(CompanyBalance),
                        TitleFa = "بیلانس کلی شرکت", TitleEn = "Company Balance Sheet",
                        DescriptionFa = "دارایی‌ها، تعهدات و خالص وضعیت شرکت تا یک تاریخ، با مفاد یا ضرر دوره و نسخهٔ PDF.",
                        DescriptionEn = "Assets, liabilities and net position up to a date, with period profit or loss and PDF.",
                        Icon = "bi-journal-check", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Action = nameof(CompanyOverview),
                        TitleFa = "وضعیت مالی شرکت", TitleEn = "Company Financial Status",
                        DescriptionFa = "پول نقد، طلبات، بدهی‌ها و مفاد شرکت در یک صفحه.",
                        DescriptionEn = "Cash, receivables, payables and profit on one page.",
                        Icon = "bi-clipboard-data", ToneClass = "tone-mint"
                    },
                    new()
                    {
                        Action = nameof(ContractPnl),
                        TitleFa = "مفاد قراردادها", TitleEn = "Contract Profit",
                        DescriptionFa = "مصارف، فروش و مفاد هر قرارداد خرید.",
                        DescriptionEn = "Cost, revenue and profit per purchase contract.",
                        Icon = "bi-graph-up-arrow", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        Controller = "ShipmentPnl", Action = "Index",
                        TitleFa = "مفاد محموله‌ها", TitleEn = "Shipment Profit",
                        DescriptionFa = "فروش و مصارف هر محموله به‌تفکیک.",
                        DescriptionEn = "Revenue and cost per shipment.",
                        Icon = "bi-truck", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Controller = "ContractJourney", Action = "Index",
                        TitleFa = "مسیر قرارداد", TitleEn = "Contract Journey",
                        DescriptionFa = "از عقد قرارداد تا بارگیری، حمل، فروش و تصفیه.",
                        DescriptionEn = "From contract to loading, transport, sale and settlement.",
                        Icon = "bi-signpost-split", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        // پیش‌تر این صفحه ساخته شده بود ولی هیچ لینکی به آن نمی‌رفت.
                        Action = nameof(Warnings),
                        TitleFa = "کارهای نیمه‌تمام", TitleEn = "Unfinished Work",
                        DescriptionFa = "ثبت‌هایی که ناقص مانده‌اند و تا تکمیل نشوند ارقام را ناقص نگه می‌دارند.",
                        DescriptionEn = "Incomplete entries that keep the figures incomplete until they are finished.",
                        Icon = "bi-exclamation-triangle", ToneClass = "tone-rose"
                    }
                ]
            },
            new()
            {
                TitleFa = "پول، طلبات و بدهی‌ها",
                TitleEn = "Money, Receivables & Payables",
                Icon = "bi-wallet2",
                Cards =
                [
                    new()
                    {
                        Action = nameof(ReceivablesPayables),
                        TitleFa = "طلبات و بدهی‌ها", TitleEn = "Receivables & Payables",
                        DescriptionFa = "مانده مشتری، تأمین‌کننده، شرکت خدماتی و صراف در یک جدول.",
                        DescriptionEn = "Customer, supplier, service provider and sarraf balances in one table.",
                        Icon = "bi-people", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(PartyAging),
                        TitleFa = "سن طلبات و بدهی‌ها بر اساس آخرین حرکت", TitleEn = "Account Age Since Last Movement",
                        DescriptionFa = "هر حساب چند روز است بی‌حرکت مانده: تا ۳۰، ۳۱ تا ۶۰، ۶۱ تا ۹۰ و بیشتر از ۹۰ روز.",
                        DescriptionEn = "How long each account has been idle: up to 30, 31-60, 61-90 and over 90 days.",
                        Icon = "bi-hourglass-split", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Action = nameof(CashFlow),
                        TitleFa = "گردش پول", TitleEn = "Cash Movement",
                        DescriptionFa = "پول واقعی که از صندوق و بانک وارد یا خارج شده است.",
                        DescriptionEn = "Actual cash in and out of the cash and bank accounts.",
                        Icon = "bi-cash-stack", ToneClass = "tone-sky"
                    },
                    new()
                    {
                        Controller = "Balance", Action = "Customers",
                        TitleFa = "گردش حساب مشتریان", TitleEn = "Customer Accounts",
                        DescriptionFa = "فروش، مصارف و مانده هر مشتری، با لینک به گردش حساب او.",
                        DescriptionEn = "Sales, expenses and balance per customer, linked to the statement.",
                        Icon = "bi-person-lines-fill", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Controller = "Balance", Action = "Suppliers",
                        TitleFa = "گردش حساب تأمین‌کننده‌ها", TitleEn = "Supplier Accounts",
                        DescriptionFa = "خرید، پرداخت و مانده هر تأمین‌کننده، با لینک به گردش حساب او.",
                        DescriptionEn = "Purchases, payments and balance per supplier, linked to the statement.",
                        Icon = "bi-building-check", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Controller = "Balance", Action = "Contracts",
                        TitleFa = "مانده قراردادها", TitleEn = "Contract Balances",
                        DescriptionFa = "مانده هر قرارداد با مشتری و تأمین‌کنندهٔ آن.",
                        DescriptionEn = "Balance per contract with its customer and supplier.",
                        Icon = "bi-scales", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Action = nameof(FxDifference),
                        TitleFa = "حساب صراف و تفاوت نرخ", TitleEn = "Sarraf & FX Difference",
                        DescriptionFa = "مفاد و ضرر تفاوت نرخ در حواله‌های صراف، با کمیشن.",
                        DescriptionEn = "Gain and loss on the FX rate of sarraf transfers, with commission.",
                        Icon = "bi-currency-exchange", ToneClass = "tone-sky"
                    },
                    new()
                    {
                        Controller = "Expenses", Action = "Index",
                        TitleFa = "مصارف", TitleEn = "Expenses",
                        DescriptionFa = "مصارف ثبت‌شده به‌تفکیک نوع، قرارداد و تاریخ.",
                        DescriptionEn = "Recorded expenses by type, contract and date.",
                        Icon = "bi-receipt", ToneClass = "tone-amber"
                    }
                ]
            },
            new()
            {
                TitleFa = "مال، فروش و حمل",
                TitleEn = "Stock, Sales & Transport",
                Icon = "bi-box-seam",
                Cards =
                [
                    new()
                    {
                        Action = nameof(InventorySales),
                        TitleFa = "موجودی و فروش", TitleEn = "Inventory & Sales",
                        DescriptionFa = "موجودی، رزرو پیش‌فروش و مقدار قابل فروش؛ با جزئیات گردش‌ها.",
                        DescriptionEn = "Physical stock, reservations and sellable quantities, with movement details.",
                        Icon = "bi-box-seam", ToneClass = "tone-teal"
                    },
                    new()
                    {
                        Action = nameof(NegativeStock),
                        TitleFa = "موجودی منفی", TitleEn = "Negative Stock",
                        DescriptionFa = "جاهایی که مانده زیر صفر رفته، با علت و سند ایجادکننده.",
                        DescriptionEn = "Scopes that went below zero, with cause and source document.",
                        Icon = "bi-exclamation-octagon", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Controller = "Sales", Action = "PreSales",
                        TitleFa = "پیش‌فروش‌ها", TitleEn = "Pre-sales",
                        DescriptionFa = "تعهدهای فروش آینده و مقدار باقی‌ماندهٔ تحویل.",
                        DescriptionEn = "Future sale commitments and remaining delivery.",
                        Icon = "bi-bookmark-check", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(PreSaleDiscrepancies),
                        TitleFa = "ناهماهنگی‌های پیش‌فروش", TitleEn = "Pre-sale Discrepancies",
                        DescriptionFa = "تحویل بیشتر از تعهد، تعهد سررسیدشده و پیش‌پرداخت مصرف‌نشده.",
                        DescriptionEn = "Over-delivery, overdue commitments and unconsumed advances.",
                        Icon = "bi-exclamation-triangle", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Action = nameof(GoodsInTransit),
                        TitleFa = "بارهای در مسیر", TitleEn = "Goods In Transit",
                        DescriptionFa = "هر باری که حرکت کرده و هنوز نرسیده، با مسیر، مقدار و وضعیت آن.",
                        DescriptionEn = "Every load that has departed and not yet arrived, with route, quantity and status.",
                        Icon = "bi-truck", ToneClass = "tone-sky"
                    },
                    new()
                    {
                        Action = nameof(TransportVariance),
                        TitleFa = "کسری و اضافه‌بار حمل", TitleEn = "Transport Shortage & Surplus",
                        DescriptionFa = "تفاوت وزن بارگیری و تخلیهٔ هر حمل، با مجموع جداگانهٔ کسری و اضافه‌بار.",
                        DescriptionEn = "Loaded vs unloaded weight per transport, with separate shortage and surplus totals.",
                        Icon = "bi-truck", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Controller = "LossEvents", Action = "Index",
                        TitleFa = "کسری و ضایعات", TitleEn = "Shortage & Loss",
                        DescriptionFa = "کسری بار، ضایعات و مبلغ قابل مطالبهٔ هر مورد.",
                        DescriptionEn = "Shortage, loss and the chargeable amount of each case.",
                        Icon = "bi-droplet-half", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(VesselVoyages),
                        TitleFa = "گزارش کشتی‌ها", TitleEn = "Vessel Voyages",
                        DescriptionFa = "هر سفر کشتی با محصول، مقدار، Shipperها، مقصد و کرایهٔ آن.",
                        DescriptionEn = "Each vessel voyage with product, quantity, shippers, destination and freight.",
                        Icon = "bi-water", ToneClass = "tone-sky"
                    }
                ]
            },
            new()
            {
                TitleFa = "کنترول و اسناد",
                TitleEn = "Control & Documents",
                Icon = "bi-shield-check",
                Cards =
                [
                    new()
                    {
                        // Summary فقط endpoint‌ـی JSON برای شمارنده‌های AJAX است و صفحه ندارد؛
                        // ورودی کاربر باید Index باشد.
                        Controller = "Reconciliation", Action = "Index",
                        TitleFa = "بررسی ناهماهنگی‌ها", TitleEn = "Reconciliation",
                        DescriptionFa = "مواردی که بین عملیات، موجودی و حساب‌ها جور نیستند.",
                        DescriptionEn = "Items where operations, stock and accounts disagree.",
                        Icon = "bi-clipboard-check", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Controller = "CustomsPermitTurnover", Action = "Index",
                        TitleFa = "گردش جواز گمرکی", TitleEn = "Permit Turnover",
                        DescriptionFa = "مقدار مصرف‌شده و باقی‌ماندهٔ هر جواز گمرکی.",
                        DescriptionEn = "Consumed and remaining quantity per customs permit.",
                        Icon = "bi-card-checklist", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Controller = "QualityInspections", Action = "Index",
                        TitleFa = "کیفیت و لابراتوار", TitleEn = "Quality & Laboratory",
                        DescriptionFa = "نتیجهٔ آزمایش هر بار: در انتظار، قبول یا رد.",
                        DescriptionEn = "Inspection result per load: pending, accepted or rejected.",
                        Icon = "bi-clipboard-check", ToneClass = "tone-mint"
                    },
                    new()
                    {
                        Controller = "Ledger", Action = "Index",
                        TitleFa = "دفتر کل", TitleEn = "Ledger",
                        DescriptionFa = "تمام اسناد مالی ثبت‌شده با منبع و مبلغ. مخصوص حسابدار.",
                        DescriptionEn = "All posted financial entries with source and amount. For the accountant.",
                        Icon = "bi-journals", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        // این صفحه صورت‌حساب طرف‌حساب نیست؛ اسناد Opening و Adjustment ارزی است.
                        // نام کارت با محتوای واقعی صفحه یکی شد تا با «گردش حساب» اشتباه نشود.
                        Controller = "AccountStatements", Action = "Index",
                        TitleFa = "اسناد اول دوره و اصلاح ارزی", TitleEn = "Opening & FX Adjustment Documents",
                        DescriptionFa = "ثبت مانده اول دوره و اصلاح ارزی. مخصوص حسابدار.",
                        DescriptionEn = "Opening balance and FX adjustment entries. For the accountant.",
                        Icon = "bi-journal-text", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        Controller = "AuditLogs", Action = "Index",
                        TitleFa = "تاریخچهٔ تغییرات", TitleEn = "Change History",
                        DescriptionFa = "چه کسی، چه وقت و چه چیزی را تغییر داده یا لغو کرده است.",
                        DescriptionEn = "Who changed or cancelled what, and when.",
                        Icon = "bi-clock-history", ToneClass = "tone-sky"
                    }
                ]
            }
        ];

    private async Task<CompanyFinancialOverviewViewModel> BuildCompanyFinancialOverviewAsync(ManagementReportFilterViewModel filter)
    {
        // عملکرد دوره فقط از موتور مشترک گزارش‌های مدیریتی؛ «بیلانس کلی شرکت» برای همین دوره همین
        // فروش، بهای فروش، مصارف، نتیجهٔ ارزی و مفاد را نشان می‌دهد.
        var period = await _profitAndLoss.BuildCompanyPeriodAsync(
            filter, _businessClock.Today, ct: HttpContext?.RequestAborted ?? CancellationToken.None);
        var revenueUsd = period.SalesRevenueUsd;
        var cogsUsd = period.CostOfSalesUsd;
        var expenseUsd = period.PeriodExpensesUsd;

        // «حرکت نقدی» همان خالصِ گزارش «گردش پول» برای همین فیلتر است: فقط حرکتِ واقعیِ
        // صندوق/بانک (مرجع: CashPositionReader)، نه همهٔ اسنادِ روزنامچه.
        var (cashInUsd, cashOutUsd) = await ReadCashMovementTotalsAsync(filter);

        var pnl = await BuildContractPnlAsync(filter);
        // ردیفِ شریک در این صفحه هیچ مصرفی ندارد: چند خط پایین‌تر با operationalPartyBalances
        // کنار گذاشته می‌شود و هیچ کارتی هم آن را نمی‌خواند (دلیلش همان‌جا نوشته شده).
        // ساختنِ صورت‌حساب شراکت گران‌ترین بخشِ این گزارش بود و تمامش دور ریخته می‌شد؛
        // حالا اصلاً ساخته نمی‌شود. عددِ هیچ کارتی تغییر نمی‌کند، چون ماندهٔ مشتری،
        // تأمین‌کننده و صراف از ردیف شریک ساخته نمی‌شود.
        var balances = await BuildReceivablesPayablesReportAsync(filter, CompanyOverviewPartyTypes);
        var warnings = await BuildReportsWarningsAsync();

        // مانده واقعی صندوق و بانک از همان مرجعی خوانده می‌شود که صفحهٔ دفتر کل و
        // حساب‌های نقدی می‌خوانند؛ محاسبهٔ موازی ساخته نمی‌شود.
        var cashCards = await FinanceMetricCardsQuery.BuildAsync(_db, _cache, businessClock: _businessClock);

        var topContracts = pnl.PurchaseRows
            .OrderByDescending(r => Math.Abs(r.GrossMarginUsd))
            .Take(5)
            .ToList();

        // All rows now represent actual company claims, including external partners.
        // The shared reader excludes internal allocations and book-owner equity.
        var operationalPartyBalances = balances.Rows.ToList();
        var topReceivables = operationalPartyBalances
            .Where(r => r.CompanyClaimUsd > 0m)
            .OrderByDescending(r => r.CompanyClaimUsd)
            .ToList();
        var topPayables = operationalPartyBalances
            .Where(r => r.CompanyClaimUsd < 0m)
            .OrderBy(r => r.CompanyClaimUsd)
            .ToList();

        // تا وقتی فروشِ بدون بهای تمام‌شده وجود دارد، COGS آن فروش‌ها صفر خوانده می‌شود و
        // «سود» بیش از واقع درمی‌آید. در آن حالت عدد سود منتشر نمی‌شود و به‌جایش دلیلش
        // نوشته می‌شود؛ هیچ COGS حدسی جایگزین نمی‌گردد.
        var isProfitPublishable = period.UncostedSaleCount == 0;

        // ── اطلاعات چرخهٔ کامل قراردادها (داخل مفاد دوره جمع نمی‌شود) ──
        // مصارفِ درون‌خطیِ بارگیری و ارزشِ ضایعاتِ قابل شارژِ قراردادهای همین فیلتر، از همان
        // ردیف‌های «مفاد قراردادها» (pnl.PurchaseRows). در مفاد دوره، کرایهٔ بی‌سندِ بارگیری از راه
        // بهای فروش و گدام/سایرِ بی‌سند در مصارف دوره آمده است؛ این دو عدد فقط برای اطلاع‌اند.
        var loadingOperationalCostUsd = pnl.PurchaseRows.Sum(r =>
            r.TransportCostUsd + r.WarehouseCostUsd + r.OtherCostUsd + r.RailwayCostUsd);
        var lossCostUsd = pnl.PurchaseRows.Sum(r => r.LossCostUsd);
        var unvaluedLossCount = pnl.PurchaseRows.Sum(r => r.UnvaluedLossCount);

        return new CompanyFinancialOverviewViewModel
        {
            Filter = filter,
            RevenueUsd = revenueUsd,
            PurchaseCostUsd = cogsUsd,
            ExpenseUsd = expenseUsd,
            LoadingOperationalCostUsd = loadingOperationalCostUsd,
            LossCostUsd = lossCostUsd,
            UnvaluedLossCount = unvaluedLossCount,
            ExchangeGainUsd = period.ExchangeGainUsd,
            ExchangeLossUsd = period.ExchangeLossUsd,
            CustodyIncomeUsd = period.CustodyIncomeUsd,
            NetCashMovementUsd = cashInUsd - cashOutUsd,
            CustomerReceivableUsd = balances.CustomerReceivableUsd,
            SupplierPayableUsd = balances.SupplierPayableUsd,
            SarrafNetUsd = balances.SarrafBalanceUsd,
            WarningCount = warnings.TotalIssueCount,
            UncostedSaleCount = period.UncostedSaleCount,
            PnlConfidence = period.Confidence,
            TopContracts = topContracts,
            CashOnHandUsd = cashCards.CashAccountsBalanceUsd,
            TopReceivables = topReceivables,
            TopPayables = topPayables,
            TotalReceivableUsd = balances.TotalReceivableUsd,
            TotalPayableUsd = balances.TotalPayableUsd,
            // تاریخ مرجعِ «روز بدون حرکت» باید همان تاریخی باشد که ردیف‌های طلب و بدهی
            // با آن ساخته شده‌اند (filter.ToDate یا امروز)، وگرنه یک طرف‌حساب در این صفحه
            // و در «سررسید طلبات و بدهی‌ها» دو عدد روز متفاوت نشان می‌دهد.
            AsOfDate = balances.AsOfDate,
            Metrics =
            [
                new() { Label = "فروش کل", Value = Money(revenueUsd), Detail = "Sales revenue", Icon = "bi-cart-check", ToneClass = "finance-positive" },
                new() { Label = "بهای تمام‌شده فروش", Value = Money(cogsUsd), Detail = "Realised COGS", Icon = "bi-box-arrow-in-down", ToneClass = "" },
                new() { Label = "مصارف", Value = Money(expenseUsd), Detail = "Official expenses", Icon = "bi-receipt", ToneClass = "finance-negative" },
                new() { Label = "مصارف بارگیری", Value = Money(loadingOperationalCostUsd), Detail = "Loading transport / warehouse / railway / other", Icon = "bi-truck", ToneClass = "finance-negative" },
                new() { Label = "ارزش ضایعات", Value = Money(lossCostUsd), Detail = unvaluedLossCount > 0 ? $"{unvaluedLossCount:N0} loss event(s) cannot be valued" : "Chargeable loss valued at purchase price", Icon = "bi-droplet-half", ToneClass = "finance-negative" },
                new() { Label = "سود خالص", Value = isProfitPublishable ? Money(period.NetProfitUsd) : "—", Detail = isProfitPublishable ? "Net profit" : $"COGS incomplete — {period.UncostedSaleCount:N0} sale(s) need COGS", Icon = "bi-graph-up-arrow", ToneClass = !isProfitPublishable ? "" : period.NetProfitUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = "حرکت نقدی", Value = Money(cashInUsd - cashOutUsd), Detail = "Payment inflow - outflow", Icon = "bi-cash-stack", ToneClass = cashInUsd - cashOutUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = "مغایرت‌ها", Value = warnings.TotalIssueCount.ToString("N0"), Detail = "Open warnings", Icon = "bi-exclamation-triangle", ToneClass = warnings.TotalIssueCount == 0 ? "finance-positive" : "finance-negative" }
            ]
        };
    }

    /// <summary>
    /// گردشِ صندوق و بانک. دقیقاً همان دو منبعی خوانده می‌شود که صفحهٔ
    /// «حساب‌های نقدی» می‌خواند:
    /// <list type="number">
    ///   <item>روزنامچه (<see cref="PaymentTransaction"/>)، به‌جز پرداختی که شریک از جیب خودش
    ///   داده (<see cref="PaymentFundingSource.Partner"/>). آن پول اصلاً وارد یا خارجِ صندوقِ شرکت
    ///   نشده و <c>CashAccountId</c> هم ندارد؛ جدا و فقط برای اطلاع نشان داده می‌شود.</item>
    ///   <item>مصرفی که «نقد پرداخت شد» ثبت شده و سندِ روزنامچه ندارد. اگر همان مصرف
    ///   یک <see cref="PaymentTransaction"/> مرتبط داشته باشد (کمیسیونِ نقدی)، حرکتِ پول از
    ///   منبعِ ۱ شمرده می‌شود و اینجا کنار می‌رود تا دو بار شمرده نشود.</item>
    /// </list>
    /// هیچ سندی ساخته یا تغییر داده نمی‌شود و هیچ منطقِ دفتری عوض نمی‌شود؛ فقط خواندن است.
    /// </summary>
    private async Task<CashFlowReportViewModel> BuildCashFlowReportAsync(ManagementReportFilterViewModel filter)
    {
        // فیلترهای غیرتاریخی برای ماندهٔ اول دوره هم لازم‌اند، پس تاریخ جدا اعمال می‌شود.
        var undated = WithoutDates(filter);
        var (companyPayments, cashExpenses, includeCashExpenses) = CashMovementSources(undated);

        var fromDate = filter.FromDate?.Date;
        var toDate = filter.ToDate?.Date;

        var periodPayments = companyPayments;
        if (fromDate.HasValue) periodPayments = periodPayments.Where(p => p.PaymentDate >= fromDate.Value);
        if (toDate.HasValue) periodPayments = periodPayments.Where(p => p.PaymentDate <= toDate.Value);

        var periodExpenses = cashExpenses;
        if (fromDate.HasValue) periodExpenses = periodExpenses.Where(e => e.ExpenseDate >= fromDate.Value);
        if (toDate.HasValue) periodExpenses = periodExpenses.Where(e => e.ExpenseDate <= toDate.Value);

        // ---- «بابت»: تجمیع در دیتابیس، نام‌گذاری در حافظه ----
        var paymentGroups = await periodPayments
            .GroupBy(p => new { p.PaymentKind, p.Direction })
            .Select(g => new
            {
                g.Key.PaymentKind,
                g.Key.Direction,
                AmountUsd = g.Sum(p => p.AmountUsd),
                Count = g.Count()
            })
            .ToListAsync();

        var expenseGroups = await periodExpenses
            .GroupBy(e => e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : "")
            .Select(g => new { TypeName = g.Key, AmountUsd = g.Sum(e => e.AmountUsd), Count = g.Count() })
            .ToListAsync();

        var rows = paymentGroups
            .GroupBy(g => CashFlowGroupName(g.PaymentKind, g.Direction))
            .Select(g => new CashFlowReportRowViewModel
            {
                GroupName = g.Key,
                InflowUsd = g.Where(x => x.Direction == PaymentDirection.In).Sum(x => x.AmountUsd),
                OutflowUsd = g.Where(x => x.Direction == PaymentDirection.Out).Sum(x => x.AmountUsd),
                Count = g.Sum(x => x.Count)
            })
            .Concat(expenseGroups.Select(g => new CashFlowReportRowViewModel
            {
                GroupName = string.IsNullOrWhiteSpace(g.TypeName)
                    ? CashExpenseGroupLabel
                    : $"{CashExpenseGroupLabel} \u2014 {g.TypeName}",
                OutflowUsd = g.AmountUsd,
                Count = g.Count
            }))
            .OrderByDescending(r => Math.Abs(r.NetUsd))
            .ToList();

        // ---- تفکیک حساب. کلیدِ گروه شناسهٔ حساب است نه نام، پس دو
        // حسابِ هم‌نام روی هم نمی‌افتند؛ ارز هم از خودِ حساب می‌آید نه از ارزِ سند.
        var paymentByAccount = await periodPayments
            .GroupBy(p => p.CashAccountId)
            .Select(g => new
            {
                CashAccountId = g.Key,
                InflowUsd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : 0m),
                OutflowUsd = g.Sum(p => p.Direction == PaymentDirection.Out ? p.AmountUsd : 0m)
            })
            .ToListAsync();

        var expenseByAccount = await periodExpenses
            .GroupBy(e => e.CashAccountId)
            .Select(g => new { CashAccountId = g.Key, OutflowUsd = g.Sum(e => e.AmountUsd) })
            .ToListAsync();

        // ---- ماندهٔ اول دوره: همان دو منبع، فقط پیش از شروعِ بازه ----
        var openingByAccount = new Dictionary<int, decimal>();
        var openingNoAccountUsd = 0m;
        if (fromDate.HasValue)
        {
            var openingPayments = await companyPayments
                .Where(p => p.PaymentDate < fromDate.Value)
                .GroupBy(p => p.CashAccountId)
                .Select(g => new
                {
                    CashAccountId = g.Key,
                    NetUsd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : -p.AmountUsd)
                })
                .ToListAsync();

            var openingExpenses = await cashExpenses
                .Where(e => e.ExpenseDate < fromDate.Value)
                .GroupBy(e => e.CashAccountId)
                .Select(g => new { CashAccountId = g.Key, NetUsd = -g.Sum(e => e.AmountUsd) })
                .ToListAsync();

            foreach (var opening in openingPayments.Concat(openingExpenses))
            {
                if (opening.CashAccountId is int openingAccountId)
                {
                    openingByAccount[openingAccountId] =
                        openingByAccount.GetValueOrDefault(openingAccountId) + opening.NetUsd;
                }
                else
                {
                    openingNoAccountUsd += opening.NetUsd;
                }
            }
        }

        var accountIds = paymentByAccount.Select(a => a.CashAccountId)
            .Concat(expenseByAccount.Select(a => a.CashAccountId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Concat(openingByAccount.Keys)
            .Distinct()
            .ToList();

        var accountMeta = (await _db.CashAccounts
                .AsNoTracking()
                .Where(a => accountIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Name, a.Currency })
                .ToListAsync())
            .ToDictionary(a => a.Id);

        var accountKeys = accountIds.Select(id => (int?)id).ToList();
        if (paymentByAccount.Any(a => a.CashAccountId is null)
            || expenseByAccount.Any(a => a.CashAccountId is null)
            || openingNoAccountUsd != 0m)
        {
            accountKeys.Add(null);
        }

        var accountRows = accountKeys
            .Select(key => new CashFlowAccountRowViewModel
            {
                CashAccountName = key is int nameId && accountMeta.TryGetValue(nameId, out var named)
                    ? named.Name
                    : UnlinkedCashAccountLabel,
                Currency = key is int currencyId && accountMeta.TryGetValue(currencyId, out var priced)
                    ? priced.Currency
                    : "-",
                OpeningUsd = key is int openingId ? openingByAccount.GetValueOrDefault(openingId) : openingNoAccountUsd,
                InflowUsd = paymentByAccount.Where(a => a.CashAccountId == key).Sum(a => a.InflowUsd),
                OutflowUsd = paymentByAccount.Where(a => a.CashAccountId == key).Sum(a => a.OutflowUsd)
                    + expenseByAccount.Where(a => a.CashAccountId == key).Sum(a => a.OutflowUsd)
            })
            .OrderByDescending(r => Math.Abs(r.NetUsd))
            .ToList();

        // ---- روندِ ماهانه ----
        var paymentMonths = await periodPayments
            .GroupBy(p => new { p.PaymentDate.Year, p.PaymentDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                InflowUsd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : 0m),
                OutflowUsd = g.Sum(p => p.Direction == PaymentDirection.Out ? p.AmountUsd : 0m),
                Count = g.Count()
            })
            .ToListAsync();

        var expenseMonths = await periodExpenses
            .GroupBy(e => new { e.ExpenseDate.Year, e.ExpenseDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                InflowUsd = 0m,
                OutflowUsd = g.Sum(e => e.AmountUsd),
                Count = g.Count()
            })
            .ToListAsync();

        var openingBalanceUsd = openingByAccount.Values.Sum() + openingNoAccountUsd;

        var monthGroups = paymentMonths.Concat(expenseMonths)
            .GroupBy(m => new { m.Year, m.Month })
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .ToList();

        var periodRows = new List<CashFlowPeriodRowViewModel>(monthGroups.Count);
        var runningUsd = openingBalanceUsd;
        foreach (var monthGroup in monthGroups)
        {
            var monthInflowUsd = monthGroup.Sum(m => m.InflowUsd);
            var monthOutflowUsd = monthGroup.Sum(m => m.OutflowUsd);
            runningUsd += monthInflowUsd - monthOutflowUsd;
            periodRows.Add(new CashFlowPeriodRowViewModel
            {
                Year = monthGroup.Key.Year,
                Month = monthGroup.Key.Month,
                InflowUsd = monthInflowUsd,
                OutflowUsd = monthOutflowUsd,
                Count = monthGroup.Sum(m => m.Count),
                ClosingUsd = runningUsd
            });
        }

        // ---- پرداختِ شریک. خارج از صندوق است و برای یک حسابِ نقدیِ مشخص نمایش داده
        // نمی‌شود. در نمای شرکت، مالکیت از CompanyId اثبات‌شده یا قرارداد همان پرداخت
        // خوانده می‌شود تا پرداخت شریکِ شرکت‌های دیگر وارد این scope نشود. ----
        var partnerFundedFilter = new ManagementReportFilterViewModel
        {
            FromDate = filter.FromDate,
            ToDate = filter.ToDate,
            ProductId = filter.ProductId,
            ContractId = filter.ContractId,
            CustomerId = filter.CustomerId,
            SupplierId = filter.SupplierId,
            PaymentKind = filter.PaymentKind
        };

        var partnerFundedQuery = ApplyPaymentFilters(
            _db.PaymentTransactions.AsNoTracking().Where(p => p.FundingSource == PaymentFundingSource.Partner),
            partnerFundedFilter);

        if (filter.CompanyId.HasValue)
        {
            var companyId = filter.CompanyId.Value;
            partnerFundedQuery = partnerFundedQuery.Where(p => p.CompanyId == companyId
                || (p.CompanyId == null && p.Contract != null && p.Contract.CompanyId == companyId));
        }

        var partnerFunded = filter.CashAccountId.HasValue
            ? null
            : await partnerFundedQuery
                .GroupBy(_ => 1)
                .Select(g => new { AmountUsd = g.Sum(p => p.AmountUsd), Count = g.Count() })
                .FirstOrDefaultAsync();

        var totalInflowUsd = rows.Sum(r => r.InflowUsd);
        var totalOutflowUsd = rows.Sum(r => r.OutflowUsd);
        var netCashFlowUsd = totalInflowUsd - totalOutflowUsd;
        var closingBalanceUsd = openingBalanceUsd + netCashFlowUsd;

        var scopeNotes = new List<string>();
        if (filter.ContractId.HasValue || filter.ProductId.HasValue)
        {
            scopeNotes.Add("فیلترِ قرارداد/جنس فقط گردشِ متصل به قرارداد را نگه می‌دارد؛ معاش، دریافت دستی و مصرفِ عمومی در این نما نیستند.");
        }

        if (!includeCashExpenses)
        {
            scopeNotes.Add("با فیلترِ مشتری، تأمین‌کننده یا بابتِ پرداخت، مصرفِ نقدی قابل انتساب نیست و در این نما شمرده نشده است.");
        }

        // سندِ روزنامچهٔ شرکت که صندوق/بانک ندارد حرکتِ هیچ صندوقی نیست، پس در مانده و گردش
        // شمرده نمی‌شود؛ ولی مغایرت است و پنهان نمی‌ماند.
        var unlinked = await ApplyPaymentFilters(
                _db.PaymentTransactions.AsNoTracking()
                    .Where(p => p.CashAccountId == null && p.FundingSource != PaymentFundingSource.Partner),
                filter)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                InUsd = g.Sum(p => p.Direction == PaymentDirection.In ? (decimal?)p.AmountUsd : null) ?? 0m,
                OutUsd = g.Sum(p => p.Direction == PaymentDirection.Out ? (decimal?)p.AmountUsd : null) ?? 0m
            })
            .FirstOrDefaultAsync();
        if (unlinked is { Count: > 0 })
        {
            scopeNotes.Add($"{unlinked.Count:N0} سند روزنامچه {UnlinkedCashAccountLabel} است (رسیدکی {unlinked.InUsd:N2}، بردکی {unlinked.OutUsd:N2} USD)؛ حرکتِ هیچ صندوقی نیست و در این گزارش و ماندهٔ نقدی شمرده نشد.");
        }

        var partnerFundedUsd = partnerFunded?.AmountUsd ?? 0m;
        var partnerFundedCount = partnerFunded?.Count ?? 0;

        // چهار کارت، به ترتیبِ همان جمله‌ای که کاربر می‌خواند:
        // «اول دوره این‌قدر داشتیم، این‌قدر آمد، این‌قدر رفت، آخر دوره این‌قدر ماند.»
        // خالص، تعداد حساب‌ها و پرداختِ شریک از کارت‌ها برداشته شد؛ خالص در جمعِ
        // جدول‌ها، تعداد حساب‌ها در جدولِ تفکیک حساب و پرداختِ شریک در هشدارِ بالای
        // صفحه دیده می‌شود، پس ردیف کارت‌ها یک سطر تمیز می‌ماند.
        var metrics = new List<ReportMetricViewModel>
        {
            new() { Label = "مانده اول دوره", Value = Money(openingBalanceUsd), Detail = fromDate.HasValue ? "Opening balance" : "From the beginning", Icon = "bi-hourglass-top", ToneClass = openingBalanceUsd >= 0m ? "finance-positive" : "finance-negative" },
            new() { Label = "رسیدکی", Value = Money(totalInflowUsd), Detail = "Money in", Icon = "bi-arrow-down-circle", ToneClass = "finance-positive" },
            new() { Label = "بردکی", Value = Money(totalOutflowUsd), Detail = "Money out", Icon = "bi-arrow-up-circle", ToneClass = "finance-negative" },
            new() { Label = "مانده آخر دوره", Value = Money(closingBalanceUsd), Detail = "Opening + In - Out", Icon = "bi-hourglass-bottom", ToneClass = closingBalanceUsd >= 0m ? "finance-positive" : "finance-negative" }
        };

        return new CashFlowReportViewModel
        {
            Filter = filter,
            Rows = rows,
            AccountRows = accountRows,
            PeriodRows = periodRows,
            OpeningBalanceUsd = openingBalanceUsd,
            PartnerFundedOutflowUsd = partnerFundedUsd,
            PartnerFundedCount = partnerFundedCount,
            ScopeWarning = scopeNotes.Count == 0 ? null : string.Join(" ", scopeNotes),
            Metrics = metrics
        };
    }

    /// <param name="partyTypes">
    /// اگر داده شود، فقط ماندهٔ همین نوع‌های طرف‌حساب ساخته می‌شود. خودِ صفحهٔ «طلبات و
    /// بدهی‌ها» چیزی پاس نمی‌دهد و همهٔ نوع‌ها را می‌گیرد؛ فقط صداکننده‌ای که ثابت است
    /// بخشی از ردیف‌ها را دور می‌ریزد آن بخش را از اول نمی‌خواند.
    /// </param>
    private async Task<ReceivablesPayablesReportViewModel> BuildReceivablesPayablesReportAsync(
        ManagementReportFilterViewModel filter,
        IReadOnlyCollection<PartyStatementPartyType>? partyTypes = null)
    {
        // «طلبات و بدهی‌ها» فقط طرف‌حساب بیرونی را می‌شمارد؛ حساب جاریِ خودِ جوازها
        // طرف معامله نیست. رجوع: PartyBalanceSnapshotFilters.ExternalPartiesOnly.
        var balanceRows = (await _partyBalances.GetBalancesAsync(
                filter,
                HttpContext?.RequestAborted ?? CancellationToken.None,
                partyTypes))
            .ExternalPartiesOnly();
        // ماندهٔ کارمند همان ماندهٔ معاش است؛ بدون «دیدن معاش» در گزارش مانده/کهنگی هم نمی‌آید.
        if (User is not null && !RoleAccessRules.CanViewEmployeeSalary(User))
        {
            balanceRows = balanceRows.Where(row => row.PartyType != PartyStatementPartyType.Employee).ToList();
        }
        var supplierIds = balanceRows
            .Where(row => row.PartyType == PartyStatementPartyType.Supplier)
            .Select(row => row.PartyId)
            .Distinct()
            .ToArray();
        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        var fxSettlements = await _supplierFxSettlements.GetManyAsync(
            supplierIds,
            filter.ContractId,
            filter.ToDate,
            cancellationToken);

        // در نمای همهٔ قراردادها، تعدیل ثبت‌شدهٔ SupplierFxDifference از قبل داخل ماندهٔ
        // رسمی است؛ فقط بخشِ ثبت‌نشده را به‌صورت read-only اصلاح می‌کنیم تا دوباره‌شماری نشود.
        // در فیلتر یک قرارداد، سطر شناسایی عمداً ContractId ندارد و در ماندهٔ خام هم نیست؛
        // بنابراین کل تفاوت همان قرارداد باید در ستون تعدیل گزارش دیده شود.
        var recognizedFxEffects = filter.ContractId.HasValue
            ? new Dictionary<int, decimal>()
            : await LoadRecognizedSupplierFxEffectsAsync(supplierIds, filter.ToDate, cancellationToken);
        var policies = new PartyStatementPolicyResolver();
        var rows = balanceRows.Select(balance => new ReceivablePayableRowViewModel
        {
            PartyType = balance.PartyType.ToString(),
            PartyId = balance.PartyId,
            PartyName = balance.PartyName,
            OpeningBalanceUsd = balance.OpeningBalanceUsd,
            // The report keeps its historical Debit/Credit column names for route/UI
            // compatibility. Their values now come from the official statement
            // convention: Debit = received, Credit = given.
            DebitUsd = balance.TotalReceiptUsd,
            CreditUsd = balance.TotalOutflowUsd,
            FxAdjustmentUsd = balance.PartyType == PartyStatementPartyType.Supplier
                && fxSettlements.TryGetValue(balance.PartyId, out var fx)
                ? decimal.Round(
                    -(fx.RealizedFxDifferenceUsd + recognizedFxEffects.GetValueOrDefault(balance.PartyId)),
                    4,
                    MidpointRounding.AwayFromZero)
                : 0m,
            LastEntryDate = balance.LastEntryDate,
            BalanceKind = balance.PartyType == PartyStatementPartyType.Supplier
                && fxSettlements.TryGetValue(balance.PartyId, out var supplierFx)
                    ? policies.Resolve(balance.PartyType).BalanceMeaning(
                        balance.ClosingBalanceUsd + decimal.Round(
                            -(supplierFx.RealizedFxDifferenceUsd
                                + recognizedFxEffects.GetValueOrDefault(balance.PartyId)),
                            4,
                            MidpointRounding.AwayFromZero),
                        isEnglish: false)
                    : balance.BalanceMeaning,
            DetailsController = balance.DetailsController
        }).ToList();

        // تاریخ مبنای «راکد بودن» حساب: انتهای بازهٔ فیلتر، وگرنه امروز.
        var asOfDate = filter.ToDate?.Date ?? _businessClock.Today;
        var model = new ReceivablesPayablesReportViewModel
        {
            Filter = filter,
            Rows = rows,
            AsOfDate = asOfDate
        };

        return new ReceivablesPayablesReportViewModel
        {
            Filter = filter,
            Rows = rows,
            AsOfDate = asOfDate,
            Metrics =
            [
                new() { Label = "طلب از همهٔ طرف‌حساب‌ها (با شریک)", Value = Money(model.TotalReceivableUsd), Detail = "Receivables, all parties incl. partners", Icon = "bi-arrow-down-circle", ToneClass = "finance-positive" },
                new() { Label = "بدهی به همهٔ طرف‌حساب‌ها (با شریک)", Value = Money(model.TotalPayableUsd), Detail = "Payables, all parties incl. partners", Icon = "bi-arrow-up-circle", ToneClass = "finance-negative" },
                new() { Label = "خالص وضعیت", Value = Money(model.NetBalanceUsd), Detail = "Net position", Icon = "bi-graph-up-arrow", ToneClass = model.NetBalanceUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = $"طلبات راکد بیش از {ReceivablesPayablesReportViewModel.StaleAfterDays} روز", Value = Money(model.StaleReceivableUsd), Detail = "Idle receivables", Icon = "bi-exclamation-triangle", ToneClass = "finance-negative" }
            ]
        };
    }

    /// <summary>
    /// هر نوع طرف‌حسابی جز شریک. «وضعیت مالی شرکت» عمداً حساب شریک را نشان نمی‌دهد.
    /// </summary>
    private static readonly PartyStatementPartyType[] CompanyOverviewPartyTypes =
        Enum.GetValues<PartyStatementPartyType>()
            .Where(type => type != PartyStatementPartyType.Partner)
            .ToArray();

    private async Task<Dictionary<int, decimal>> LoadRecognizedSupplierFxEffectsAsync(
        IReadOnlyCollection<int> supplierIds,
        DateTime? toDate,
        CancellationToken ct)
    {
        if (supplierIds.Count == 0)
        {
            return [];
        }

        var query = _db.LedgerEntries.AsNoTracking()
            .Where(l => l.SupplierId.HasValue
                && supplierIds.Contains(l.SupplierId.Value)
                && l.SourceType == SupplierFxRecognitionService.PartyLedgerSourceType);

        if (toDate.HasValue)
        {
            var end = toDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < end);
        }

        var rows = await query
            .Select(l => new { SupplierId = l.SupplierId!.Value, l.Side, l.AmountUsd })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.SupplierId)
            // ماندهٔ گزارش برای طرف‌حساب: Debit/برد مثبت و Credit/رسید منفی است.
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.Side == LedgerSide.Debit ? row.AmountUsd : -row.AmountUsd));
    }

    private async Task<InventoryOperationsReportViewModel> BuildInventoryOperationsReportAsync(ManagementReportFilterViewModel filter)
    {
        var movementRows = await _stock.GetMovementSummaryAsync(
            productId: filter.ProductId,
            contractId: filter.ContractId,
            terminalId: filter.TerminalId,
            storageTankId: filter.StorageTankId,
            fromUtc: filter.FromDate?.Date,
            toUtc: filter.ToDate?.Date);
        var stockRows = movementRows
            .Where(r => !filter.FromDate.HasValue || r.MovementCount > 0)
            .Select(r => new
            {
                r.ProductName,
                r.TerminalName,
                r.StorageTankCode,
                QuantityMt = r.ClosingQuantityMt,
                r.MovementCount,
                r.LastMovementDate
            })
            .ToList();

        var productRows = stockRows
            .GroupBy(r => r.ProductName)
            .Select(g => new InventoryOperationsRowViewModel
            {
                GroupName = g.Key,
                QuantityMt = g.Sum(r => r.QuantityMt),
                MovementCount = g.Sum(r => r.MovementCount),
                LastMovementDate = g.Max(r => r.LastMovementDate)
            })
            .OrderByDescending(r => r.QuantityMt)
            .ToList();

        var terminalRows = stockRows
            .GroupBy(r => new { r.TerminalName, r.StorageTankCode })
            .Select(g => new InventoryOperationsRowViewModel
            {
                GroupName = g.Key.TerminalName,
                SecondaryName = g.Key.StorageTankCode,
                QuantityMt = g.Sum(r => r.QuantityMt),
                MovementCount = g.Sum(r => r.MovementCount),
                LastMovementDate = g.Max(r => r.LastMovementDate)
            })
            .OrderByDescending(r => r.QuantityMt)
            .Take(12)
            .ToList();

        var loadingQuery = _db.LoadingRegisters.AsNoTracking().AsQueryable();
        if (filter.FromDate.HasValue) loadingQuery = loadingQuery.Where(l => l.LoadingDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) loadingQuery = loadingQuery.Where(l => l.LoadingDate <= filter.ToDate.Value.Date);
        if (filter.ContractId.HasValue) loadingQuery = loadingQuery.Where(l => l.ContractId == filter.ContractId.Value);

        var unreceiptedLoadingCount = await loadingQuery
            .CountAsync(l => !_db.LoadingReceipts.Any(r => r.LoadingRegisterId == l.Id && !r.IsCancelled));
        var activeChargeableLossCount = await _db.LossEvents
            .AsNoTracking()
            .CountAsync(l => !l.IsCancelled && l.ChargeableLossMt > 0m);
        var negativeStockCount = stockRows.Count(r => r.QuantityMt < 0m);
        var totalQuantityMt = productRows.Sum(r => r.QuantityMt);
        var canAttributeReservation = !filter.ContractId.HasValue
            && !filter.TerminalId.HasValue
            && !filter.StorageTankId.HasValue;
        decimal? reservedPreSaleMt = null;
        if (canAttributeReservation)
        {
            var activePreSales = _db.PreSaleOrders.AsNoTracking()
                .Where(o => o.Status == PreSaleOrderStatus.Confirmed
                    || o.Status == PreSaleOrderStatus.PartiallyDelivered);
            if (filter.ProductId.HasValue)
            {
                activePreSales = activePreSales.Where(o => o.ProductId == filter.ProductId.Value);
            }

            reservedPreSaleMt = await activePreSales
                .Select(o => o.QuantityMt
                    - _db.SalesTransactions
                        .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                        .Sum(s => (decimal?)s.QuantityMt).GetValueOrDefault())
                .SumAsync();
            reservedPreSaleMt = Math.Max(0m, reservedPreSaleMt.Value);
        }

        var warnings = new List<InventoryOperationsWarningViewModel>();
        if (unreceiptedLoadingCount > 0)
        {
            warnings.Add(new()
            {
                Title = "بارگیری بدون رسید",
                Description = "بارگیری‌هایی که هنوز رسید نهایی ندارند.",
                Count = unreceiptedLoadingCount,
                Controller = "Loading",
                Action = "Index",
                RouteValues = new
                {
                    contractId = filter.ContractId,
                    productId = filter.ProductId,
                    fromDate = filter.FromDate?.ToString("yyyy-MM-dd"),
                    toDate = filter.ToDate?.ToString("yyyy-MM-dd"),
                    withoutReceipt = true
                }
            });
        }

        if (activeChargeableLossCount > 0)
        {
            warnings.Add(new()
            {
                Title = "ضایعات قابل شارژ",
                Description = "رویدادهای فعال که مقدار قابل شارژ دارند.",
                Count = activeChargeableLossCount,
                Controller = "LossEvents",
                Action = "Index",
                RouteValues = new Dictionary<string, string?>
                {
                    ["Filter.ProductId"] = filter.ProductId?.ToString(),
                    ["Filter.ContractId"] = filter.ContractId?.ToString(),
                    ["Filter.FromDate"] = filter.FromDate?.ToString("yyyy-MM-dd"),
                    ["Filter.ToDate"] = filter.ToDate?.ToString("yyyy-MM-dd"),
                    ["Filter.ChargeableOnly"] = "true"
                }
            });
        }

        if (negativeStockCount > 0)
        {
            warnings.Add(new()
            {
                Title = "موجودی منفی",
                Description = "ترکیب محصول/ترمینال/مخزن با موجودی منفی.",
                Count = negativeStockCount,
                Controller = "Inventory",
                Action = "StockCard",
                RouteValues = new Dictionary<string, string?>
                {
                    ["Filter.ProductId"] = filter.ProductId?.ToString(),
                    ["Filter.ContractId"] = filter.ContractId?.ToString(),
                    ["Filter.TerminalId"] = filter.TerminalId?.ToString(),
                    ["Filter.StorageTankId"] = filter.StorageTankId?.ToString(),
                    ["Filter.FromDate"] = filter.FromDate?.ToString("yyyy-MM-dd"),
                    ["Filter.ToDate"] = filter.ToDate?.ToString("yyyy-MM-dd")
                }
            });
        }

        return new InventoryOperationsReportViewModel
        {
            Filter = filter,
            ProductRows = productRows,
            TerminalRows = terminalRows,
            Warnings = warnings,
            Metrics =
            [
                new() { Label = "موجودی کل", Value = $"{totalQuantityMt:N4} MT", Detail = UiText.T(HttpContext, "مانده فیزیکی موجودی", "Stock balance"), Icon = "bi-box-seam", ToneClass = "" },
                new() { Label = "تعهد پیش‌فروش", Value = reservedPreSaleMt.HasValue ? $"{reservedPreSaleMt:N4} MT" : "—", Detail = reservedPreSaleMt.HasValue ? UiText.T(HttpContext, "تعهد فعال تحویل‌نشده", "Active undelivered commitment") : UiText.T(HttpContext, "تعهد به قرارداد/ترمینال/مخزن قابل انتساب نیست", "Reservation cannot be attributed to contract/terminal/tank"), Icon = "bi-bookmark-check", ToneClass = "" },
                new() { Label = "موجودی قابل فروش", Value = reservedPreSaleMt.HasValue ? $"{totalQuantityMt - reservedPreSaleMt.Value:N4} MT" : "—", Detail = UiText.T(HttpContext, "موجودی فیزیکی منهای تعهد فعال پیش‌فروش", "Physical stock - active pre-sale commitment"), Icon = "bi-box-arrow-up-right", ToneClass = reservedPreSaleMt.HasValue && totalQuantityMt - reservedPreSaleMt.Value < 0m ? "finance-negative" : "" },
                new() { Label = "محصولات", Value = productRows.Count.ToString("N0"), Detail = UiText.T(HttpContext, "محصولات دارای موجودی", "Products with stock"), Icon = "bi-droplet", ToneClass = "" },
                new() { Label = "مخزن/ترمینال", Value = terminalRows.Count.ToString("N0"), Detail = UiText.T(HttpContext, "گروه‌های ذخیره‌سازی", "Storage groups"), Icon = "bi-database", ToneClass = "" },
                new() { Label = "هشدار عملیاتی", Value = warnings.Sum(w => w.Count).ToString("N0"), Detail = UiText.T(HttpContext, "نیازمند بررسی", "Needs review"), Icon = "bi-exclamation-triangle", ToneClass = warnings.Any() ? "finance-negative" : "finance-positive" }
            ]
        };
    }

    private async Task<ReportsWarningsViewModel> BuildReportsWarningsAsync()
    {
        var paymentSourceTypes = Enum.GetNames<PaymentKind>();
        var paymentIdsWithLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => paymentSourceTypes.Contains(l.SourceType))
            .Select(l => l.SourceId)
            .Distinct()
            .ToListAsync();

        var salesWithoutLedger = await _db.SalesTransactions
            .AsNoTracking()
            .CountAsync(s => !s.IsCancelled && !_db.LedgerEntries.Any(l => l.SourceType == "Sale" && l.SourceId == s.Id));
        var expensesWithoutLedger = await _db.ExpenseTransactions
            .AsNoTracking()
            .CountAsync(e => !e.IsCancelled && !_db.LedgerEntries.Any(l => l.SourceType == "Expense" && l.SourceId == e.Id));
        var paymentsWithoutLedger = await _db.PaymentTransactions
            .AsNoTracking()
            .CountAsync(p => !p.LedgerEntryId.HasValue && !paymentIdsWithLedger.Contains(p.Id));
        var sarrafWithoutLedger = await _db.SarrafSettlements
            .AsNoTracking()
            .CountAsync(s => s.Status == SarrafSettlementStatus.Posted && !s.LedgerEntryId.HasValue);
        var unvaluedLosses = await _db.LossEvents
            .AsNoTracking()
            .CountAsync(l => !l.IsCancelled
                && l.ChargeableLossMt > 0m
                && l.LoadingRegisterId.HasValue
                && l.LoadingRegister != null
                && !l.LoadingRegister.LoadingPriceUsd.HasValue);

        // PTG-P1-04 — ردیف‌های «طبقه‌بندی‌نشده». هویتِ تسویه‌شان هیچ‌وقت حدس زده نمی‌شود،
        // پس باید دیده شوند تا کسی برایشان تصمیم بگیرد — نه اینکه خاموش در گزارش‌ها بمانند.
        var unclassifiedExpenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .CountAsync(e => !e.IsCancelled && e.SettlementMode == ExpenseSettlementMode.Unknown);

        // اظهارنامهٔ گمرکی‌ای که هنوز به دفتر کل نرسیده: مبلغش در گزارش‌ها هست، ولی سطرِ
        // مالی ندارد و در حساب هیچ طرف‌حسابی دیده نمی‌شود.
        var customsWithoutSettlement = await _db.CustomsDeclarations
            .AsNoTracking()
            .CountAsync(c => c.TotalUsd > 0m
                && !c.ExpenseTransactions.Any(e => !e.IsCancelled));

        var items = new List<ReportsWarningItemViewModel>
        {
            new() { Title = "فروش بدون سند دفتر", Description = "فروش‌های قطعی که سند دفتر کل ندارند.", Count = salesWithoutLedger, Severity = "danger", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "مصارف بدون سند دفتر", Description = "مصارف ثبت‌شده که در دفتر کل نیامده‌اند.", Count = expensesWithoutLedger, Severity = "danger", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "رسیدکی و بردکی بدون سند دفتر", Description = "دریافت و پرداخت‌هایی که سند دفتر کل ندارند.", Count = paymentsWithoutLedger, Severity = "warning", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "تصفیهٔ صراف بدون سند دفتر", Description = "تصفیه‌های ثبت‌شدهٔ صراف که سند تأمین‌کننده ندارند.", Count = sarrafWithoutLedger, Severity = "warning", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "ضایعات بدون قیمت", Description = "ضایعات قابل شارژ که قیمت بارگیری برای ارزش‌گذاری ندارند.", Count = unvaluedLosses, Severity = "warning", Controller = "Reports", Action = "ContractPnl" },
            new() { Title = "مصارف با تسویهٔ طبقه‌بندی‌نشده", Description = "مصرف‌هایی که معلوم نیست بدهی مانده‌اند یا پرداخت شده‌اند. حدس زده نمی‌شوند و باید یک‌یک مشخص شوند.", Count = unclassifiedExpenses, Severity = "warning", Controller = "Expenses", Action = "Index" },
            new() { Title = "گمرک بدون تسویه", Description = "اظهارنامه‌های گمرکی که وضعیت تسویه‌شان انتخاب نشده، پس هزینه‌شان به دفتر کل نرسیده است.", Count = customsWithoutSettlement, Severity = "warning", Controller = "CustomsDeclarations", Action = "Index" }
        };

        items = items.Where(i => i.Count > 0).ToList();

        return new ReportsWarningsViewModel
        {
            Items = items,
            Metrics =
            [
                new() { Label = "کل کارهای نیمه‌تمام", Value = items.Sum(i => i.Count).ToString("N0"), Detail = "Open items", Icon = "bi-exclamation-triangle", ToneClass = items.Any() ? "finance-negative" : "finance-positive" },
                new() { Label = "بدون سند دفتر", Value = (salesWithoutLedger + expensesWithoutLedger + paymentsWithoutLedger + sarrafWithoutLedger).ToString("N0"), Detail = "Ledger issues", Icon = "bi-journal-x", ToneClass = salesWithoutLedger + expensesWithoutLedger + paymentsWithoutLedger + sarrafWithoutLedger > 0 ? "finance-negative" : "finance-positive" },
                new() { Label = "ضایعات بدون قیمت", Value = unvaluedLosses.ToString("N0"), Detail = "Unvalued losses", Icon = "bi-graph-up", ToneClass = unvaluedLosses > 0 ? "finance-negative" : "finance-positive" }
            ]
        };
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> ContractPnl([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true);
        return View(await BuildContractPnlAsync(filter));
    }

    private async Task<ContractPnlReportViewModel> BuildContractPnlAsync(ManagementReportFilterViewModel filter)
    {
        // ── Purchase contracts ────────────────────────────────────────────
        var purchaseQuery = _db.Contracts.AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase);

        if (filter.ProductId.HasValue)  purchaseQuery = purchaseQuery.Where(c => c.ProductId == filter.ProductId.Value);
        if (filter.SupplierId.HasValue) purchaseQuery = purchaseQuery.Where(c => c.SupplierId == filter.SupplierId.Value);
        if (filter.ContractId.HasValue) purchaseQuery = purchaseQuery.Where(c => c.Id == filter.ContractId.Value);
        if (filter.FromDate.HasValue)   purchaseQuery = purchaseQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)     purchaseQuery = purchaseQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var purchaseContractRows = await purchaseQuery
            .OrderByDescending(c => c.ContractDate)
            .Select(c => new
            {
                c.Id, c.ContractName, c.ContractNumber, c.Status, c.QuantityMt, c.UnitPriceUsd, c.ManualFinalPriceUsd, c.PricingMethod,
                ProductName = c.Product != null ? c.Product.Name : "",
                CounterpartyName = c.Supplier != null ? c.Supplier.Name : null
            })
            .ToListAsync();
        var purchaseContracts = purchaseContractRows
            .Select(c => new
            {
                c.Id, c.ContractName, c.ContractNumber, c.Status, c.QuantityMt, c.ProductName, c.CounterpartyName,
                CanonicalFinalPriceUsd = ContractPricingAdapter.GetCanonicalFinalPrice(new Contract
                {
                    ManualFinalPriceUsd = c.ManualFinalPriceUsd,
                    UnitPriceUsd = c.UnitPriceUsd,
                    PricingMethod = c.PricingMethod
                })
            })
            .ToList();

        var purchaseIds = purchaseContracts.Select(c => c.Id).ToList();

        // اقتصادِ هر قرارداد (فروش‌های منتسب، بهای خرید، هزینه‌ها، ضایعات، ارز، سودِ محقق و سودِ
        // چرخهٔ کامل) فقط از ProfitAndLossService خوانده می‌شود؛ این کنترلر فرمولِ سود ندارد.
        var purchaseEconomics = await _profitAndLoss.BuildContractEconomicsAsync(purchaseIds);

        var purchaseRows = purchaseContracts.Select(c =>
        {
            var e = purchaseEconomics.GetValueOrDefault(c.Id)
                ?? new ContractEconomicsSnapshot { ContractId = c.Id, ContractType = ContractType.Purchase };
            return new ContractPnlRowViewModel
            {
                ContractId = c.Id,
                ContractName = c.ContractName,
                ContractNumber = c.ContractNumber,
                ContractType = ContractType.Purchase,
                Status = c.Status,
                ProductName = c.ProductName,
                CounterpartyName = c.CounterpartyName,
                ContractQuantityMt = c.QuantityMt,
                ContractUnitPriceUsd = c.CanonicalFinalPriceUsd,
                TotalLoadedMt = e.TotalLoadedMt,
                PricedLoadedMt = e.PricedLoadedMt,
                PendingLoadedMt = e.PendingLoadedMt,
                PendingLoadingCount = e.PendingLoadingCount,
                PurchaseValueUsd = e.PurchaseValueUsd,
                TransportCostUsd = e.TransportCostUsd,
                WarehouseCostUsd = e.WarehouseCostUsd,
                OtherCostUsd = e.OtherCostUsd,
                RailwayCostUsd = e.RailwayCostUsd,
                CustomsCostUsd = e.CustomsCostUsd,
                // سهمِ مصرفِ بدون تگِ محموله در همان ستون‌ها تا جمعِ ستون‌های پولی = بهای کل.
                GeneralExpenseCostUsd = e.GeneralExpenseCostUsd + e.SharedShipmentExpenseUsd,
                // ارزشِ ضایعه اطلاعی است: بهای همان نفت در ارزشِ خرید هست و در بهای کل دوباره نمی‌آید.
                LossCostUsd = e.LossCostUsd + e.ShipmentLossCostUsd,
                UnvaluedLossCount = e.UnvaluedLossCount,
                PendingSettlementQuantityMt = e.PendingSettlementQuantityMt,
                SarrafSupplierShortfallUsd = e.Fx.SupplierShortfallUsd,
                ExchangeGainUsd = e.Fx.GainUsd,
                ExchangeLossUsd = e.Fx.LossUsd,
                TotalSoldMt = e.SoldQuantityMt,
                TotalRevenueUsd = e.RevenueUsd,
                DirectSaleQuantityMismatchCount = e.DirectSaleQuantityMismatchCount,
                PnlConfidence = e.Confidence,
                TotalCostUsd = e.LifecycleTotalCostUsd,
                GrossMarginUsd = e.LifecycleMarginUsd,
                RealizedNetProfitUsd = e.RealizedNetProfitUsd
            };
        }).ToList();

        // ── Sale contracts ────────────────────────────────────────────────
        var saleQuery = _db.Contracts.AsNoTracking()
            .Where(c => c.ContractType == ContractType.Sale);

        if (filter.ProductId.HasValue)  saleQuery = saleQuery.Where(c => c.ProductId == filter.ProductId.Value);
        if (filter.CustomerId.HasValue) saleQuery = saleQuery.Where(c => c.CustomerId == filter.CustomerId.Value);
        if (filter.ContractId.HasValue) saleQuery = saleQuery.Where(c => c.Id == filter.ContractId.Value);
        if (filter.FromDate.HasValue)   saleQuery = saleQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)     saleQuery = saleQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var saleContracts = await saleQuery
            .OrderByDescending(c => c.ContractDate)
            .Select(c => new
            {
                c.Id, c.ContractName, c.ContractNumber, c.Status, c.QuantityMt, c.UnitPriceUsd,
                ProductName = c.Product != null ? c.Product.Name : "",
                CounterpartyName = c.Customer != null ? c.Customer.Name : null
            })
            .ToListAsync();

        var saleIds = saleContracts.Select(c => c.Id).ToList();
        var saleEconomics = await _profitAndLoss.BuildContractEconomicsAsync(saleIds);

        var saleRows = saleContracts.Select(c =>
        {
            var e = saleEconomics.GetValueOrDefault(c.Id)
                ?? new ContractEconomicsSnapshot { ContractId = c.Id, ContractType = ContractType.Sale };
            return new ContractPnlRowViewModel
            {
                ContractId = c.Id,
                ContractName = c.ContractName,
                ContractNumber = c.ContractNumber,
                ContractType = ContractType.Sale,
                Status = c.Status,
                ProductName = c.ProductName,
                CounterpartyName = c.CounterpartyName,
                ContractQuantityMt = c.QuantityMt,
                ContractUnitPriceUsd = c.UnitPriceUsd,
                TotalSoldMt = e.SoldQuantityMt,
                TotalRevenueUsd = e.RevenueUsd,
                PurchaseValueUsd = e.RealizedCostOfGoodsSoldUsd,
                GeneralExpenseCostUsd = e.GeneralExpenseCostUsd,
                SarrafSupplierShortfallUsd = e.Fx.SupplierShortfallUsd,
                ExchangeGainUsd = e.Fx.GainUsd,
                ExchangeLossUsd = e.Fx.LossUsd,
                UncostedSaleCount = e.UncostedSaleCount,
                PnlConfidence = e.Confidence,
                TotalCostUsd = e.LifecycleTotalCostUsd,
                GrossMarginUsd = e.LifecycleMarginUsd,
                RealizedNetProfitUsd = e.RealizedNetProfitUsd
            };
        }).ToList();

        return new ContractPnlReportViewModel
        {
            Filter = filter,
            PurchaseRows = purchaseRows,
            SaleRows = saleRows
        };
    }

    private async Task PopulateLookupsAsync(
        ManagementReportFilterViewModel filter,
        bool includeCustomers = false,
        bool includeSuppliers = false,
        bool includeInventory = false,
        bool includeCashAccounts = false)
    {
        var productLookups = await GetCachedLookupAsync(
            "reports:lookups:products:v1",
            () => _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new LookupOption(p.Id, p.Name))
                .ToListAsync());
        ViewBag.Products = new SelectList(
            productLookups,
            "Id",
            "Name",
            filter.ProductId);

        var contractLookupRows = await _db.Contracts
            .AsNoTracking()
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
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
            contractLookupRows
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
            filter.ContractId);

        if (includeCustomers)
        {
            var customerLookups = await GetCachedLookupAsync(
                "reports:lookups:customers:v1",
                () => _db.Customers.AsNoTracking()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.Name)
                    .Select(c => new LookupOption(c.Id, c.Name))
                    .ToListAsync());
            ViewBag.Customers = new SelectList(
                customerLookups,
                "Id",
                "Name",
                filter.CustomerId);
        }

        if (includeSuppliers)
        {
            var supplierLookups = await GetCachedLookupAsync(
                "reports:lookups:suppliers:v1",
                () => _db.Suppliers.AsNoTracking()
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.Name)
                    .Select(s => new LookupOption(s.Id, s.Name))
                    .ToListAsync());
            ViewBag.Suppliers = new SelectList(
                supplierLookups,
                "Id",
                "Name",
                filter.SupplierId);
        }

        if (includeInventory)
        {
            var terminalLookups = await GetCachedLookupAsync(
                "reports:lookups:terminals:v1",
                () => _db.Terminals.AsNoTracking()
                    .Where(t => t.IsActive)
                    .OrderBy(t => t.Code)
                    .Select(t => new LookupOption(t.Id, t.Name))
                    .ToListAsync());
            ViewBag.Terminals = new SelectList(
                terminalLookups,
                "Id",
                "Name",
                filter.TerminalId);
            var tankLookups = await GetCachedLookupAsync(
                "reports:lookups:storage-tanks:v2",
                async () => (await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks.AsNoTracking()
                        .OrderBy(t => t.DisplayName ?? t.TankCode)))
                    .Select(t => new TankLookupOption(t.Id, t.Display))
                    .ToList());
            ViewBag.StorageTanks = new SelectList(
                tankLookups,
                "Id",
                "Display",
                filter.StorageTankId);
        }

        if (includeCashAccounts)
        {
            var cashAccountLookups = await GetCachedLookupAsync(
                "reports:lookups:cash-accounts:v1",
                () => _db.CashAccounts.AsNoTracking()
                    .Where(a => a.IsActive)
                    .OrderBy(a => a.Name)
                    .Select(a => new LookupOption(a.Id, a.Name))
                    .ToListAsync());
            ViewBag.CashAccounts = new SelectList(
                cashAccountLookups,
                "Id",
                "Name",
                filter.CashAccountId);

            var companyLookups = await GetCachedLookupAsync(
                "reports:lookups:companies:v1",
                () => _db.Companies.AsNoTracking()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.Name)
                    .Select(c => new LookupOption(c.Id, c.NamePersian ?? c.Name))
                    .ToListAsync());
            ViewBag.Companies = new SelectList(
                companyLookups,
                "Id",
                "Name",
                filter.CompanyId);

            ViewBag.PaymentKinds = new SelectList(
                Enum.GetValues<PaymentKind>()
                    .Select(kind => new LookupOption((int)kind, PaymentKindLabels.ToPersian(kind)))
                    .OrderBy(kind => kind.Name)
                    .ToList(),
                "Id",
                "Name",
                filter.PaymentKind.HasValue ? (int)filter.PaymentKind.Value : null);
        }
    }

    private static string Money(decimal value) => $"{value:N2} USD";

    private static IQueryable<PaymentTransaction> ApplyPaymentFilters(
        IQueryable<PaymentTransaction> query,
        ManagementReportFilterViewModel filter)
    {
        if (filter.FromDate.HasValue) query = query.Where(p => p.PaymentDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(p => p.PaymentDate <= filter.ToDate.Value.Date);
        if (filter.ContractId.HasValue) query = query.Where(p => p.ContractId == filter.ContractId.Value);
        if (filter.CustomerId.HasValue) query = query.Where(p => p.CustomerId == filter.CustomerId.Value);
        if (filter.SupplierId.HasValue) query = query.Where(p => p.SupplierId == filter.SupplierId.Value);
        if (filter.ProductId.HasValue) query = query.Where(p => p.Contract != null && p.Contract.ProductId == filter.ProductId.Value);
        if (filter.CashAccountId.HasValue) query = query.Where(p => p.CashAccountId == filter.CashAccountId.Value);
        if (filter.PaymentKind.HasValue) query = query.Where(p => p.PaymentKind == filter.PaymentKind.Value);

        // شرکت از روی صاحبِ صندوق/بانک تفکیک می‌شود، نه از PaymentTransaction.CompanyId که
        // برای رکوردهای مبهم عمداً null مانده و فیلترِ مستقیم روی آن پول را خاموش گم می‌کند.
        if (filter.CompanyId.HasValue)
        {
            query = query.Where(p => p.CashAccount != null && p.CashAccount.CompanyId == filter.CompanyId.Value);
        }

        return query;
    }

    /// <summary>عنوانِ گروهِ مصرفِ نقدی در ستون «بابت».</summary>
    private const string CashExpenseGroupLabel = "مصرف نقدی";

    /// <summary>سطرِ پرداختی که صندوق/بانکِ معتبر ندارد. مغایرت است، پس پنهان نمی‌شود.</summary>
    private const string UnlinkedCashAccountLabel = "بدون حساب نقدی";

    /// <summary>
    /// مصرفی که از صندوق/بانک پرداخت شده و حرکتِ پولش در روزنامچه سند ندارد.
    /// مصرفِ لغوشده و مصرفی که <see cref="PaymentTransaction"/> مرتبط دارد (کمیسیونِ نقدی)
    /// بیرون می‌ماند تا همان خروجِ پول دو بار شمرده نشود.
    /// </summary>
    private IQueryable<ExpenseTransaction> ApplyCashExpenseFilters(
        IQueryable<ExpenseTransaction> query,
        ManagementReportFilterViewModel filter)
    {
        query = CashPositionReader.StandaloneCashExpenses(_db, query);

        if (filter.ContractId.HasValue) query = query.Where(e => e.ContractId == filter.ContractId.Value);
        if (filter.ProductId.HasValue) query = query.Where(e => e.Contract != null && e.Contract.ProductId == filter.ProductId.Value);
        if (filter.CashAccountId.HasValue) query = query.Where(e => e.CashAccountId == filter.CashAccountId.Value);
        if (filter.CompanyId.HasValue) query = query.Where(e => e.CashAccount != null && e.CashAccount.CompanyId == filter.CompanyId.Value);

        return query;
    }

    private static ManagementReportFilterViewModel WithoutDates(ManagementReportFilterViewModel filter) => new()
    {
        ProductId = filter.ProductId,
        ContractId = filter.ContractId,
        CustomerId = filter.CustomerId,
        SupplierId = filter.SupplierId,
        CashAccountId = filter.CashAccountId,
        CompanyId = filter.CompanyId,
        PaymentKind = filter.PaymentKind
    };

    /// <summary>
    /// اسنادِ «حرکتِ واقعیِ صندوق/بانک» برای یک فیلترِ بی‌تاریخ: همان دو منبعِ مرجعِ ماندهٔ نقدی
    /// (<see cref="CashPositionReader"/>). مصرفِ نقدی نه مشتری/تأمین‌کننده دارد و نه «بابتِ پرداخت»؛
    /// با این فیلترها قابل انتساب نیست و کنار می‌رود.
    /// </summary>
    private (IQueryable<PaymentTransaction> Payments, IQueryable<ExpenseTransaction> Expenses, bool IncludesCashExpenses)
        CashMovementSources(ManagementReportFilterViewModel undated)
    {
        var includeCashExpenses = !undated.CustomerId.HasValue
            && !undated.SupplierId.HasValue
            && !undated.PaymentKind.HasValue;
        var payments = ApplyPaymentFilters(CashPositionReader.CashPayments(_db.PaymentTransactions.AsNoTracking()), undated);
        var expenses = includeCashExpenses
            ? ApplyCashExpenseFilters(_db.ExpenseTransactions.AsNoTracking(), undated)
            : _db.ExpenseTransactions.AsNoTracking().Where(e => false);
        return (payments, expenses, includeCashExpenses);
    }

    /// <summary>جمعِ ورود و خروجِ واقعیِ صندوق/بانک در بازهٔ فیلتر؛ همان عددِ «گردش پول».</summary>
    private async Task<(decimal InflowUsd, decimal OutflowUsd)> ReadCashMovementTotalsAsync(ManagementReportFilterViewModel filter)
    {
        var (payments, expenses, _) = CashMovementSources(WithoutDates(filter));
        var fromDate = filter.FromDate?.Date;
        var toDate = filter.ToDate?.Date;
        if (fromDate.HasValue)
        {
            payments = payments.Where(p => p.PaymentDate >= fromDate.Value);
            expenses = expenses.Where(e => e.ExpenseDate >= fromDate.Value);
        }
        if (toDate.HasValue)
        {
            payments = payments.Where(p => p.PaymentDate <= toDate.Value);
            expenses = expenses.Where(e => e.ExpenseDate <= toDate.Value);
        }

        var paymentTotals = await payments
            .GroupBy(_ => 1)
            .Select(g => new
            {
                InUsd = g.Sum(p => p.Direction == PaymentDirection.In ? (decimal?)p.AmountUsd : null),
                OutUsd = g.Sum(p => p.Direction == PaymentDirection.Out ? (decimal?)p.AmountUsd : null)
            })
            .FirstOrDefaultAsync();
        var expenseOutUsd = await expenses.SumAsync(e => (decimal?)e.AmountUsd) ?? 0m;

        return (paymentTotals?.InUsd ?? 0m, (paymentTotals?.OutUsd ?? 0m) + expenseOutUsd);
    }

    private static string CashFlowGroupName(PaymentKind paymentKind, PaymentDirection direction)
        => paymentKind switch
        {
            PaymentKind.ManualReceipt when direction == PaymentDirection.In => "دریافت دستی",
            PaymentKind.ManualPayment when direction == PaymentDirection.Out => "پرداخت دستی",
            _ => PaymentKindLabels.ToPersian(paymentKind)
        };


}
