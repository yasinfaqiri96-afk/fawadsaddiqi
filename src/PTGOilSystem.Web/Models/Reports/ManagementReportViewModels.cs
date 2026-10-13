using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Models.Reports;

// مرکز گزارشات هیچ محاسبه‌ای انجام نمی‌دهد؛ فقط دسته‌بندی و مسیر نگه می‌دارد.
// شمارنده‌های قبلی (فروش/مصارف/قرارداد/...) حذف شدند چون هیچ Query پشتشان نبود.
public sealed class ReportHubViewModel
{
    public IReadOnlyList<ReportHubGroupViewModel> Groups { get; init; } = [];
}

public sealed class ReportHubCardViewModel
{
    public string Controller { get; init; } = "";
    public string Action { get; init; } = "";
    public string TitleFa { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public string DescriptionFa { get; init; } = "";
    public string DescriptionEn { get; init; } = "";
    public string Icon { get; init; } = "";
    public string ToneClass { get; init; } = "";
}

public sealed class ReportMetricViewModel
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Detail { get; init; }
    public string Icon { get; init; } = "";
    public string ToneClass { get; init; } = "";
}

public sealed class CompanyFinancialOverviewViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public decimal RevenueUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public decimal ExpenseUsd { get; init; }

    /// <summary>
    /// فقط برای اطلاع: مصارفِ درون‌خطیِ بارگیری (حمل، گدام، سایر، خط‌آهن) در چرخهٔ کامل قراردادهای
    /// همین فیلتر. در <see cref="NetProfitUsd"/> جمع نمی‌شود؛ در مفاد دوره، کرایهٔ بی‌سند از راه بهای
    /// فروش و گدام/سایرِ بی‌سند در <see cref="ExpenseUsd"/> آمده است.
    /// </summary>
    public decimal LoadingOperationalCostUsd { get; init; }

    /// <summary>
    /// فقط برای اطلاع: ارزش دالریِ ضایعاتِ قابل شارژ قراردادهای همین فیلتر؛ در <see cref="NetProfitUsd"/> جمع نمی‌شود.
    /// </summary>
    public decimal LossCostUsd { get; init; }

    /// <summary>ضایعاتی که قیمت بارگیری ندارند و ارزششان محاسبه‌نشدنی است.</summary>
    public int UnvaluedLossCount { get; init; }
    public decimal ExchangeGainUsd { get; init; }
    public decimal ExchangeLossUsd { get; init; }

    /// <summary>کمیشن و حق خدمات امانی (همان CompanyPeriodPerformanceSnapshot.CustodyIncomeUsd).</summary>
    public decimal CustodyIncomeUsd { get; init; }
    public decimal NetCashMovementUsd { get; init; }
    public decimal CustomerReceivableUsd { get; init; }
    public decimal SupplierPayableUsd { get; init; }
    public decimal SarrafNetUsd { get; init; }
    public int WarningCount { get; init; }
    public int UncostedSaleCount { get; init; }
    public PnlConfidence PnlConfidence { get; init; } = PnlConfidence.NeedsReview;
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<ContractPnlRowViewModel> TopContracts { get; init; } = [];

    /// <summary>
    /// مانده واقعی صندوق و بانک در همین لحظه — مستقل از بازهٔ فیلتر صفحه.
    /// این با <see cref="NetCashMovementUsd"/> یکی نیست: آن یکی «چقدر پول در این
    /// بازه وارد و خارج شد» است، این یکی «چقدر پول همین حالا داریم».
    /// </summary>
    public decimal CashOnHandUsd { get; init; }

    /// <summary>همهٔ طلبات شرکت (مانده مثبت)، از بزرگ به کوچک، در بازهٔ فیلترشده.</summary>
    public IReadOnlyList<ReceivablePayableRowViewModel> TopReceivables { get; init; } = [];

    /// <summary>همهٔ بدهی‌های شرکت (مانده منفی)، از بزرگ به کوچک، در بازهٔ فیلترشده.</summary>
    public IReadOnlyList<ReceivablePayableRowViewModel> TopPayables { get; init; } = [];

    /// <summary>جمع بدهی شرکت به تأمین‌کننده، شرکت خدماتی و هر طرف‌حساب دیگر با مانده منفی.</summary>
    public decimal TotalPayableUsd { get; init; }

    /// <summary>جمع طلبات شرکت از همهٔ طرف‌حساب‌ها با مانده مثبت.</summary>
    public decimal TotalReceivableUsd { get; init; }

    /// <summary>
    /// شمار کارهایی که برای کامل‌شدن ارقام باید انجام شوند: موارد باز صفحهٔ
    /// «کارهای نیمه‌تمام» به‌علاوهٔ فروش‌های بدون بهای تمام‌شده.
    /// </summary>
    public int ActionNeededCount => WarningCount + UncostedSaleCount;

    /// <summary>تاریخ کاری کابل هنگام ساخت صفحه؛ مبنای «چند روز از آخرین حرکت حساب».</summary>
    public DateTime AsOfDate { get; init; }

    public decimal GrossProfitUsd => PnlMath.GrossProfit(RevenueUsd, PurchaseCostUsd);

    /// <summary>همان فرمول موتور عملکرد دوره (CompanyPeriodPerformanceSnapshot.NetProfitUsd).</summary>
    public decimal NetProfitUsd => PnlMath.NetProfit(
        RevenueUsd,
        PurchaseCostUsd,
        ExpenseUsd,
        ExchangeGainUsd,
        ExchangeLossUsd) + CustodyIncomeUsd;

    /// <summary>
    /// سود فقط وقتی منتشر می‌شود که همهٔ فروش‌های این بازه بها داشته باشند (Pool ثبت‌شده یا بهای
    /// قرارداد خرید). تا وقتی <see cref="UncostedSaleCount"/> بزرگ‌تر از صفر است، COGS آن فروش‌ها صفر
    /// خوانده می‌شود و سود بیش از واقع درمی‌آید؛ در آن حالت هیچ صفحه/خروجی نباید این
    /// عدد را به‌عنوان سود واقعی منتشر کند.
    /// </summary>
    public bool IsProfitPublishable => UncostedSaleCount == 0 && PnlConfidence != PnlConfidence.NeedsReview;

    /// <summary>عنوان و توضیحِ بخشِ مصارف بارگیری و ضایعات؛ صفحه و خروجی از همین متن می‌خوانند.</summary>
    public const string OperationalIndicatorsTitleFa = "شاخص‌های عملیاتی دوره";
    public const string OperationalIndicatorsTitleEn = "Period operational indicators";
    public const string OperationalIndicatorsNoteFa =
        "این ارقام برای کنترول عملیاتی نمایش داده می‌شوند و مستقیماً در مفاد خالص بالا دوباره کسر نشده‌اند.";
    public const string OperationalIndicatorsNoteEn =
        "These figures are shown for operational control and are not deducted again from the net profit above.";

    public string ProfitUnavailableNoteFa =>
        $"بهای تمام‌شدهٔ فروش تکمیل نشده ({UncostedSaleCount:N0} فروش)؛ سود نهایی قابل محاسبه نیست.";

    public string ProfitUnavailableNoteEn =>
        $"Cost of goods sold is incomplete ({UncostedSaleCount:N0} sale(s)); final profit cannot be calculated.";
}

public sealed class CashFlowReportRowViewModel
{
    public string GroupName { get; init; } = "";
    public decimal InflowUsd { get; init; }
    public decimal OutflowUsd { get; init; }
    public decimal NetUsd => InflowUsd - OutflowUsd;
    public int Count { get; init; }
}

public sealed class CashFlowAccountRowViewModel
{
    public string CashAccountName { get; init; } = "";

    /// <summary>
    /// ارزِ خودِ حساب (<c>CashAccount.Currency</c>)، نه ارزِ سند. همهٔ مبالغِ این ردیف USD اند؛
    /// این ستون فقط می‌گوید حساب به کدام ارز نگهداری می‌شود.
    /// </summary>
    public string Currency { get; init; } = "";

    /// <summary>مانده‌ی همین حساب پیش از شروعِ بازه (USD).</summary>
    public decimal OpeningUsd { get; init; }
    public decimal InflowUsd { get; init; }
    public decimal OutflowUsd { get; init; }
    public decimal NetUsd => InflowUsd - OutflowUsd;
    public decimal ClosingUsd => OpeningUsd + NetUsd;
}

/// <summary>یک ماهِ تقویمی از گردش پول — برای دیدنِ روند، نه فقط جمعِ کلِ دوره.</summary>
public sealed class CashFlowPeriodRowViewModel
{
    public int Year { get; init; }
    public int Month { get; init; }
    public string Label => $"{Year:0000}-{Month:00}";
    public decimal InflowUsd { get; init; }
    public decimal OutflowUsd { get; init; }
    public decimal NetUsd => InflowUsd - OutflowUsd;

    /// <summary>مانده‌ی پایانِ همین ماه = مانده‌ی اول دوره + گردشِ تجمعی تا این ماه.</summary>
    public decimal ClosingUsd { get; init; }
    public int Count { get; init; }
}

public sealed class CashFlowReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<CashFlowReportRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<CashFlowAccountRowViewModel> AccountRows { get; init; } = [];
    public IReadOnlyList<CashFlowPeriodRowViewModel> PeriodRows { get; init; } = [];

    /// <summary>
    /// مانده‌ی صندوق و بانک پیش از شروعِ بازه. از همان دو منبعِ گردش خوانده می‌شود، فقط با
    /// تاریخِ کوچک‌تر از «از تاریخ». بدونِ «از تاریخ» صفر است، چون گزارش از آغاز شروع می‌شود.
    /// </summary>
    public decimal OpeningBalanceUsd { get; init; }
    public decimal TotalInflowUsd => Rows.Sum(r => r.InflowUsd);
    public decimal TotalOutflowUsd => Rows.Sum(r => r.OutflowUsd);
    public decimal NetCashFlowUsd => TotalInflowUsd - TotalOutflowUsd;
    public decimal ClosingBalanceUsd => OpeningBalanceUsd + NetCashFlowUsd;

    /// <summary>
    /// پرداختی که شریک از جیب خودش داده. از صندوق/بانکِ شرکت خارج نشده، پس در جمع‌های بالا
    /// نیست؛ جدا نشان داده می‌شود تا خاموش گم نشود.
    /// </summary>
    public decimal PartnerFundedOutflowUsd { get; init; }
    public int PartnerFundedCount { get; init; }

    /// <summary>اگر فیلترِ فعال بخشی از گردش را بی‌صدا کنار می‌گذارد، متنِ هشدار.</summary>
    public string? ScopeWarning { get; init; }
}

public sealed class ReceivablePayableRowViewModel
{
    public string PartyType { get; init; } = "";
    public int? PartyId { get; init; }
    public string PartyName { get; init; } = "";
    public decimal OpeningBalanceUsd { get; init; }
    public decimal DebitUsd { get; init; }
    public decimal CreditUsd { get; init; }
    public decimal PeriodMovementUsd => CreditUsd - DebitUsd;
    /// <summary>
    /// اصلاح فقط‌خواندنیِ تفاوت نرخِ ثبت‌نشده برای حساب تأمین‌کننده. این مقدار قرارداد،
    /// پرداخت یا دفتر را تغییر نمی‌دهد و اگر سند تفاوت نرخ قبلاً ثبت شده باشد صفر می‌شود.
    /// </summary>
    public decimal FxAdjustmentUsd { get; init; }
    public decimal BalanceUsd => OpeningBalanceUsd + PeriodMovementUsd + FxAdjustmentUsd;

    /// <summary>
    /// همان مانده از دید شرکت: مثبت = طلب شرکت، منفی = بدهی شرکت. علامتِ <see cref="BalanceUsd"/> قاعدهٔ
    /// صورت‌حساب همان طرف‌حساب است و فقط برای شریک برعکس است (مثبت = شریک طلبکار).
    /// </summary>
    public decimal CompanyClaimUsd => PartyType == nameof(PartyStatementPartyType.Partner) ? -BalanceUsd : BalanceUsd;
    public string BalanceKind { get; init; } = "";
    public DateTime? LastEntryDate { get; init; }
    public string? DetailsController { get; init; }
}

public sealed class ReceivablesPayablesReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<ReceivablePayableRowViewModel> Rows { get; init; } = [];
    public decimal CustomerReceivableUsd => Rows
        .Where(r => r.PartyType == nameof(PartyStatementPartyType.Customer) && r.BalanceUsd > 0m)
        .Sum(r => r.BalanceUsd);
    public decimal SupplierPayableUsd => -Rows.Where(r => r.PartyType == "Supplier" && r.BalanceUsd < 0m).Sum(r => r.BalanceUsd);
    public decimal ServiceProviderPayableUsd => -Rows.Where(r => r.PartyType == "ServiceProvider" && r.BalanceUsd < 0m).Sum(r => r.BalanceUsd);
    public decimal SarrafBalanceUsd => Rows.Where(r => r.PartyType == "Sarraf").Sum(r => r.BalanceUsd);

    /// <summary>حسابی که بیش از این تعداد روز حرکتی نداشته «راکد» شمرده می‌شود.</summary>
    public const int StaleAfterDays = 60;

    /// <summary>تاریخ مبنای گزارش؛ سنِ سکوتِ هر حساب نسبت به همین تاریخ سنجیده می‌شود.</summary>
    public DateTime AsOfDate { get; init; } = DateTime.Today;

    /// <summary>جمع طلب‌های شرکت از همهٔ طرف‌حساب‌ها (<see cref="ReceivablePayableRowViewModel.CompanyClaimUsd"/> مثبت).</summary>
    public decimal TotalReceivableUsd => Rows.Where(r => r.CompanyClaimUsd > 0m).Sum(r => r.CompanyClaimUsd);

    /// <summary>جمع بدهی‌های شرکت به همهٔ طرف‌حساب‌ها (مثبت نمایش می‌شود).</summary>
    public decimal TotalPayableUsd => -Rows.Where(r => r.CompanyClaimUsd < 0m).Sum(r => r.CompanyClaimUsd);

    /// <summary>خالص وضعیت: طلب منهای بدهی. مثبت یعنی شرکت در مجموع طلبکار است.</summary>
    public decimal NetBalanceUsd => TotalReceivableUsd - TotalPayableUsd;

    /// <summary>
    /// طلبی که حسابش بیش از <see cref="StaleAfterDays"/> روز حرکتی نداشته (یا هیچ حرکتی ندارد).
    /// این عدد فقط از همین ردیف‌ها خوانده می‌شود و هیچ مانده‌ای را دوباره محاسبه نمی‌کند.
    /// </summary>
    public decimal StaleReceivableUsd => Rows
        .Where(r => r.CompanyClaimUsd > 0m
            && (r.LastEntryDate is null
                || (AsOfDate.Date - r.LastEntryDate.Value.Date).TotalDays > StaleAfterDays))
        .Sum(r => r.CompanyClaimUsd);
}

public sealed class InventoryOperationsRowViewModel
{
    public string GroupName { get; init; } = "";
    public string? SecondaryName { get; init; }
    public decimal QuantityMt { get; init; }
    public int MovementCount { get; init; }
    public DateTime? LastMovementDate { get; init; }
}

public sealed class InventoryOperationsWarningViewModel
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public int Count { get; init; }
    public string? Controller { get; init; }
    public string? Action { get; init; }
    public object? RouteValues { get; init; }
}

public sealed class InventoryOperationsReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<InventoryOperationsRowViewModel> ProductRows { get; init; } = [];
    public IReadOnlyList<InventoryOperationsRowViewModel> TerminalRows { get; init; } = [];
    public IReadOnlyList<InventoryOperationsWarningViewModel> Warnings { get; init; } = [];
}

public sealed class ReportsWarningItemViewModel
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public int Count { get; init; }
    public string Severity { get; init; } = "";
    public string? Controller { get; init; }
    public string? Action { get; init; }
}

public sealed class ReportsWarningsViewModel
{
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<ReportsWarningItemViewModel> Items { get; init; } = [];
    public int TotalIssueCount => Items.Sum(i => i.Count);
}

public sealed class ManagementReportFilterViewModel
{
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    public int? ProductId { get; set; }
    public int? ContractId { get; set; }
    public int? CustomerId { get; set; }
    public int? SupplierId { get; set; }
    public int? TerminalId { get; set; }
    public int? StorageTankId { get; set; }

    /// <summary>صندوق/بانکِ مشخص. فقط گزارش‌های نقدی به آن نگاه می‌کنند.</summary>
    public int? CashAccountId { get; set; }

    /// <summary>
    /// شرکتِ صاحبِ صندوق/بانک. تفکیک از روی <c>CashAccount.CompanyId</c> انجام می‌شود، نه از
    /// <c>PaymentTransaction.CompanyId</c> که برای رکوردهای مبهم عمداً null مانده است.
    /// </summary>
    public int? CompanyId { get; set; }

    /// <summary>بابتِ پرداخت. فقط گزارش‌های نقدی به آن نگاه می‌کنند.</summary>
    public PaymentKind? PaymentKind { get; set; }
}

public sealed class ContractPnlRowViewModel
{
    public int ContractId { get; init; }
    public string ContractName { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public ContractType ContractType { get; init; }
    public ContractStatus Status { get; init; }
    public string ProductName { get; init; } = "";
    public string? CounterpartyName { get; init; }
    public decimal ContractQuantityMt { get; init; }
    public decimal? ContractUnitPriceUsd { get; init; }
    public decimal TotalLoadedMt { get; init; }
    public decimal PricedLoadedMt { get; init; }
    public decimal PendingLoadedMt { get; init; }
    public int PendingLoadingCount { get; init; }
    public decimal PurchaseValueUsd { get; init; }
    public decimal? AveragePurchasePriceUsd => PricedLoadedMt > 0m
        ? Math.Round(PurchaseValueUsd / PricedLoadedMt, 4, MidpointRounding.AwayFromZero)
        : null;
    public bool NeedsReview => PendingLoadingCount > 0 || DirectSaleQuantityMismatchCount > 0;
    public decimal TransportCostUsd { get; init; }
    public decimal WarehouseCostUsd { get; init; }
    public decimal OtherCostUsd { get; init; }
    public decimal RailwayCostUsd { get; init; }
    public decimal CustomsCostUsd { get; init; }
    /// <summary>
    /// Sum of <c>ExpenseTransaction.AmountUsd</c> rows directly linked to this purchase contract
    /// (<c>ContractId == this.ContractId</c>, not cancelled). Inline LoadingRegister expenses
    /// (Transport/Warehouse/Other/Railway) and CustomsDeclaration totals live on separate
    /// records and are tracked in their own columns, so this column adds without double-counting.
    /// </summary>
    public decimal GeneralExpenseCostUsd { get; init; }
    /// <summary>
    /// Read-only USD valuation of chargeable losses on this purchase contract:
    /// <c>Σ(LossEvent.ChargeableLossMt × LoadingRegister.LoadingPriceUsd)</c> over non-cancelled
    /// events whose <c>LoadingRegisterId</c> resolves to a priced loading. Loss events without a
    /// known LoadingPriceUsd are excluded from the cost (and surfaced in
    /// <see cref="UnvaluedLossCount"/> so the operator can fix the missing snapshot).
    /// No data is stored — this is a derived figure for reporting only.
    /// </summary>
    public decimal LossCostUsd { get; init; }
    /// <summary>
    /// Number of non-cancelled chargeable LossEvents on this purchase contract whose linked
    /// LoadingRegister has no <c>LoadingPriceUsd</c>, so the USD value cannot be computed.
    /// </summary>
    public int UnvaluedLossCount { get; init; }
    /// <summary>
    /// مقدار موجودی که هنوز در مخزن است و رسیدش با حالت «ضایعات بعداً از تسویه مخزن»
    /// ثبت شده — یعنی ضایعهٔ قطعی این قرارداد هنوز مشخص نیست. مشتق از موجودی است.
    /// </summary>
    public decimal PendingSettlementQuantityMt { get; init; }
    /// <summary>تا وقتی مقدار بالا مثبت است، سود/زیان این قرارداد «موقت» است.</summary>
    public bool HasPendingTankSettlement => PendingSettlementQuantityMt > 0m;
    public decimal SarrafSupplierShortfallUsd { get; init; }
    public decimal ExchangeGainUsd { get; init; }
    public decimal ExchangeLossUsd { get; init; }
    public decimal NetExchangeDifferenceUsd => ExchangeLossUsd - ExchangeGainUsd;
    /// <summary>جمعِ هزینهٔ چرخهٔ کاملِ قرارداد؛ از <c>ContractEconomicsSnapshot.LifecycleTotalCostUsd</c>.</summary>
    public decimal TotalCostUsd { get; init; }
    public decimal TotalSoldMt { get; init; }
    public decimal TotalRevenueUsd { get; init; }
    public int DirectSaleQuantityMismatchCount { get; init; }
    public int UncostedSaleCount { get; init; }
    public PnlConfidence PnlConfidence { get; init; } = PnlConfidence.Legacy;
    /// <summary>سودِ چرخهٔ کاملِ قرارداد (کلِ خرید و کلِ هزینه)؛ از <c>ContractEconomicsSnapshot.LifecycleMarginUsd</c>.</summary>
    public decimal GrossMarginUsd { get; init; }
    /// <summary>سودِ محققِ قرارداد (فقط بخشِ فروخته‌شده، با اثرِ ارزی)؛ از <c>ContractEconomicsSnapshot.RealizedNetProfitUsd</c>.</summary>
    public decimal RealizedNetProfitUsd { get; init; }
    public decimal? MarginPercent => TotalRevenueUsd > 0 ? Math.Round((GrossMarginUsd / TotalRevenueUsd) * 100m, 1) : null;

    /// <summary>
    /// مبلغ ثبت‌شدهٔ قرارداد = مقدار قرارداد × قیمت قطعی همان قرارداد. قیمت نامعلوم یعنی null، نه صفر.
    /// این عدد تعهد قرارداد است، نه خرید واقعیِ بارگیری‌شده (<see cref="PurchaseValueUsd"/>).
    /// </summary>
    public decimal? ContractValueUsd => ContractUnitPriceUsd.HasValue
        ? decimal.Round(ContractQuantityMt * ContractUnitPriceUsd.Value, 2, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>
    /// مصارف قرارداد بدون قیمت خرید نفت: مصارف پولی ثبت‌شده و اثر مالی ارزیِ همان قرارداد.
    /// جمع <see cref="PurchaseValueUsd"/> و همین عدد دقیقاً همان <see cref="TotalCostUsd"/> موتور مالی است.
    /// </summary>
    public decimal ExpensesUsd => TotalCostUsd - PurchaseValueUsd;

    /// <summary>بارِ بدون قیمت خرید قطعی دارد؛ مفاد این قرارداد هنوز قطعی نیست.</summary>
    public bool HasPendingPurchasePrice => PendingLoadingCount > 0 || PendingLoadedMt > 0m;

    /// <summary>
    /// مفاد قابل نمایش: فقط وقتی فروش ثبت شده و قیمت خرید همهٔ بارها معلوم است. در غیر این صورت null —
    /// برآوردِ ناقص به‌عنوان مفاد قطعی نشان داده نمی‌شود.
    /// </summary>
    public decimal? ContractProfitUsd => TotalRevenueUsd > 0m && !HasPendingPurchasePrice ? GrossMarginUsd : null;
}

public sealed class ContractPnlReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ContractPnlRowViewModel> PurchaseRows { get; init; } = [];
    public IReadOnlyList<ContractPnlRowViewModel> SaleRows { get; init; } = [];
    public int PendingPurchaseLoadingCount => PurchaseRows.Sum(r => r.PendingLoadingCount);
    public decimal PendingPurchaseLoadedMt => PurchaseRows.Sum(r => r.PendingLoadedMt);
    public bool HasPendingPurchasePricing => PendingPurchaseLoadingCount > 0;
    public int PendingTankSettlementCount => PurchaseRows.Count(r => r.HasPendingTankSettlement);
    public decimal PendingTankSettlementQuantityMt => PurchaseRows.Sum(r => r.PendingSettlementQuantityMt);
    public bool HasPendingTankSettlement => PendingTankSettlementCount > 0;
    public decimal TotalPurchaseCostUsd => PurchaseRows.Sum(r => r.TotalCostUsd);
    public decimal TotalSarrafSupplierShortfallUsd => PurchaseRows.Sum(r => r.SarrafSupplierShortfallUsd);
    public decimal TotalExchangeGainUsd => PurchaseRows.Sum(r => r.ExchangeGainUsd);
    public decimal TotalExchangeLossUsd => PurchaseRows.Sum(r => r.ExchangeLossUsd);
    public decimal TotalNetExchangeDifferenceUsd => TotalExchangeLossUsd - TotalExchangeGainUsd;
    public decimal TotalDirectSaleRevenueUsd => PurchaseRows.Sum(r => r.TotalRevenueUsd);
    /// <summary>
    /// مفاد ناخالصِ همان چیزی که جدول قراردادهای خرید نشان می‌دهد: درآمد فروش مستقیمِ این
    /// قراردادها منهای جمع مصارفشان. هیچ عدد تازه‌ای ساخته نمی‌شود — همان جمعِ ستون‌های جدول.
    /// </summary>
    public decimal TotalDirectSaleGrossMarginUsd => PnlMath.GrossProfit(TotalDirectSaleRevenueUsd, TotalPurchaseCostUsd);
    /// <summary>فیصدی مفاد نسبت به درآمد. بدون درآمد، فیصدی معنا ندارد و null می‌ماند.</summary>
    public decimal? DirectSaleMarginPercent => TotalDirectSaleRevenueUsd > 0m
        ? Math.Round((TotalDirectSaleGrossMarginUsd / TotalDirectSaleRevenueUsd) * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
    /// <summary>کارت اول: مبلغ ثبت‌شدهٔ قراردادهای خرید فهرست‌شده (قراردادِ بی‌قیمت شمرده نمی‌شود).</summary>
    public decimal TotalContractValueUsd => PurchaseRows.Sum(r => r.ContractValueUsd ?? 0m);
    public int UnpricedContractCount => PurchaseRows.Count(r => !r.ContractValueUsd.HasValue);
    /// <summary>ستون «قیمت خرید»: ارزش خرید واقعیِ بارهای قیمت‌دار.</summary>
    public decimal TotalPurchaseValueUsd => PurchaseRows.Sum(r => r.PurchaseValueUsd);
    /// <summary>کارت دوم: مصارف قراردادها بدون قیمت خرید.</summary>
    public decimal TotalContractExpensesUsd => PurchaseRows.Sum(r => r.ExpensesUsd);
    /// <summary>
    /// کارت چهارم: جمع همان مفادی که جدول نشان می‌دهد. قراردادِ بی‌فروش یا با قیمت خرید نامعلوم
    /// در این جمع نیست تا برآورد ناقص به‌عنوان مفاد قطعی جمع نشود.
    /// </summary>
    public decimal TotalContractProfitUsd => PurchaseRows.Sum(r => r.ContractProfitUsd ?? 0m);
    /// <summary>قراردادهای دارای فروش که به‌خاطر قیمت خرید نامعلوم در جمع مفاد نیامده‌اند.</summary>
    public int PendingPriceProfitCount => PurchaseRows.Count(r => r.TotalRevenueUsd > 0m && r.HasPendingPurchasePrice);
    public decimal TotalSalesRevenueUsd => SaleRows.Sum(r => r.TotalRevenueUsd);
    public decimal TotalRealisedSalesCostUsd => SaleRows.Sum(r => r.TotalCostUsd);
    public decimal TotalGrossMarginUsd => PnlMath.GrossProfit(TotalSalesRevenueUsd, TotalRealisedSalesCostUsd);
    public int TotalUncostedSaleCount => SaleRows.Sum(r => r.UncostedSaleCount);
}
