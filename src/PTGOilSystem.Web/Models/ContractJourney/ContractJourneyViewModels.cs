using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;

namespace PTGOilSystem.Web.Models.ContractJourney;

public static class ContractJourneyTabs
{
    public static class Index
    {
        public const string Picker = "picker";
        public const string Recent = "recent";

        public static string Normalize(string? tab)
            => string.Equals(tab, Recent, StringComparison.OrdinalIgnoreCase)
                ? Recent
                : Picker;
    }

    public static class Details
    {
        public const string Summary = "summary";
        public const string Dashboard = "dashboard";
        public const string Operations = "operations";
        public const string Loadings = "loadings";
        public const string Receipts = "receipts";
        public const string InventoryTransport = "inventorytransport";
        public const string Inventory = "inventory";
        public const string Dispatch = "dispatch";
        public const string Sales = "sales";
        public const string Costs = "costs";
        public const string Finance = "finance";
        public const string Ledger = "ledger";

        public static string Normalize(string? tab) => tab?.Trim().ToLowerInvariant() switch
        {
            Dashboard => Summary,
            Operations => Loadings,
            Loadings => Loadings,
            Receipts => Receipts,
            InventoryTransport => InventoryTransport,
            Inventory => Inventory,
            Dispatch => Dispatch,
            Sales => Sales,
            Costs => Costs,
            Finance => Finance,
            Ledger => Ledger,
            _ => Summary
        };
    }
}

public sealed class ContractJourneyIndexViewModel
{
    public int? SelectedContractId { get; init; }
    public string ActiveTab { get; init; } = ContractJourneyTabs.Index.Picker;
    public IReadOnlyList<ContractJourneyIndexItemViewModel> Items { get; init; } = [];
}

public sealed class ContractJourneyIndexItemViewModel
{
    public int ContractId { get; init; }
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string ContractTypeName { get; init; } = string.Empty;
    public string ContractTypeBadgeClass { get; init; } = "status-badge status-badge-neutral";
    public string ProductName { get; init; } = string.Empty;
    public string ContractUnitText { get; init; } = "—";
    public string PartnerName { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public DateTime ContractDate { get; init; }
    public string StatusName { get; init; } = string.Empty;
}

/// <summary>یک زیرقرارداد در گزارش تجمیعیِ قرارداد اصلی.</summary>
public sealed class ContractJourneySubContractItemViewModel
{
    public int ContractId { get; init; }
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string StatusName { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal RemainingQuantityMt { get; init; }
    public decimal LoadingsValueUsd { get; init; }
}

/// <summary>سهم یک شریک در قرارداد شراکتی (فقط نمایشی).</summary>
public sealed class ContractJourneyPartnerShareItemViewModel
{
    public int PartnerId { get; init; }
    public string PartnerCode { get; init; } = string.Empty;
    public string PartnerName { get; init; } = string.Empty;
    public decimal SharePercent { get; init; }
}

public sealed class ContractJourneyDetailsViewModel
{
    public int ContractId { get; init; }
    public string ActiveTab { get; init; } = ContractJourneyTabs.Details.Summary;
    public bool LockContract { get; init; }
    public bool IsInitialSummaryPayload { get; init; }
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string ContractTypeName { get; init; } = string.Empty;
    public string ContractTypeBadgeClass { get; init; } = "status-badge status-badge-neutral";
    public string CompanyName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string ContractUnitText { get; init; } = "—";
    public string? SupplierName { get; init; }
    public string? CustomerName { get; init; }
    public decimal ContractQuantityMt { get; init; }
    // قیمت هر بارگیری مستقل است. اگر بارگیری‌های این قرارداد بیش از یک قیمت داشته باشند،
    // «ارزش قرارداد» باید جمع ارزش بارگیری‌ها باشد، نه مقدار قرارداد × نرخ قرارداد.
    public bool HasMixedLoadingPrices { get; init; }
    public decimal LoadingsValueUsd { get; init; }
    // قرارداد اصلی / زیرقرارداد (فقط گزارش تجمیعی؛ پرداخت و دفتر کل جابه‌جا نمی‌شود).
    public int? ParentContractId { get; init; }
    public string? ParentContractNumber { get; init; }
    public IReadOnlyList<ContractJourneySubContractItemViewModel> SubContractItems { get; init; } = [];
    // مالکیت شراکتی: فقط برای نمایش سهم هر شریک در تب خلاصه. هیچ اثر مالی یا دفترکلی ندارد.
    public bool IsPartnershipContract { get; init; }
    public IReadOnlyList<ContractJourneyPartnerShareItemViewModel> PartnerShareItems { get; init; } = [];
    public string Currency { get; init; } = "USD";
    public string PriceDisplay { get; init; } = string.Empty;
    public string PricingMethodName { get; init; } = string.Empty;
    public string PricingStatusName { get; init; } = string.Empty;
    public ContractJourneyRubSettlementSummaryViewModel RubSettlementSummary { get; init; } = new();
    public string EditPricingUrl { get; init; } = string.Empty;
    public string PricingFormulaText { get; init; } = string.Empty;
    public decimal? PricingFinalUnitPriceUsd { get; init; }
    public bool PricingNeedsReview { get; init; }
    public string? PricingReason { get; init; }
    public bool PricingFallbackApplied { get; init; }
    public string? PricingFormulaNote { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public string StatusBadgeClass { get; init; } = "status-badge status-badge-neutral";
    public DateTime ContractDate { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string? Notes { get; init; }
    public bool IsPurchaseContract { get; init; }
    public string? UnsupportedMessage { get; init; }
    public int ShipmentCount { get; init; }
    public decimal ShipmentQuantityMt { get; init; }
    public IReadOnlyList<string> LoadingDocumentReferences { get; init; } = [];
    public int UnreceiptedLoadingCount { get; init; }
    public decimal ReceiptDifferenceQuantityMt { get; init; }
    public decimal LoadingDifferenceLossMt { get; init; }
    public decimal ReceiptShortageLossMt { get; init; }
    public decimal DispatchShortageLossMt { get; init; }
    public decimal InventoryTransportLossMt { get; init; }
    public decimal TankLossMt { get; init; }
    public decimal SalesLossMt { get; init; }
    // وضعیت موقت: مقدار رسیدهای «ضایعات بعداً از تسویه مخزن» که هنوز در مخزن است
    // و ضایعهٔ نهایی‌شان مشخص نشده. مشتق از موجودی؛ ContractJourney فقط نمایش است.
    public decimal PendingTankSettlementQuantityMt { get; init; }
    /// <summary>بارِ بارگیری‌شده‌ای که هنوز در مبدأ/بارگیری مانده (GoodsInTransitQuantityReader).</summary>
    public decimal OriginRemainingQuantityMt { get; init; }
    /// <summary>کرایهٔ ناخالصِ رسیدهای حملِ موجودیِ این قرارداد.</summary>
    public decimal TransportFreightGrossUsd { get; init; }
    /// <summary>مبلغِ کسری که از کرایهٔ راننده کم شده (جبرانِ خسارت؛ فقط یک بار، داخلِ کرایهٔ خالص).</summary>
    public decimal TransportShortageDeductionUsd { get; init; }
    /// <summary>کرایهٔ خالصِ قابل پرداخت به راننده/حمل‌کننده.</summary>
    public decimal TransportFreightPayableUsd { get; init; }
    /// <summary>مقدارِ کسری که از کرایه کم شده (مبنای نرخِ کسرات).</summary>
    public decimal TransportDeductedShortageMt { get; init; }
    public bool HasPendingTankSettlement => PendingTankSettlementQuantityMt > 0m;
    public decimal InventoryInQuantityMt { get; init; }
    public decimal InventoryOutQuantityMt { get; init; }
    public bool HasNegativeStockWarning { get; init; }
    public int SalesFromInventoryCount { get; init; }
    public int SalesWithoutTraceCount { get; init; }
    public decimal DispatchFreightCostUsd { get; init; }
    public int DispatchWithoutInventoryTraceCount { get; init; }
    public decimal LoadingOperationalExpenseUsd { get; init; }
    public decimal LoadingRailwayExpenseUsd { get; init; }
    public decimal LoadingWarehouseExpenseUsd { get; init; }
    public decimal ContractTransportExpenseUsd { get; init; }
    public decimal ContractStorageRentExpenseUsd { get; init; }
    public int PendingLoadingPriceCount { get; init; }
    public decimal PendingLoadingPriceQuantityMt { get; init; }
    public int CustomsDeclarationCount { get; init; }
    public decimal CustomsDeclarationTotalUsd { get; init; }
    public decimal PaymentInTotalUsd { get; init; }
    public decimal PaymentOutTotalUsd { get; init; }
    public ContractJourneyKpiSummaryViewModel Kpis { get; init; } = new();
    public IReadOnlyList<ContractJourneyKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<ContractJourneyTimelineStepViewModel> TimelineSteps { get; init; } = [];
    public IReadOnlyList<ContractJourneyQuantityFlowItemViewModel> QuantityFlowItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyExpenseBreakdownViewModel> ExpenseBreakdowns { get; init; } = [];
    public IReadOnlyList<ContractJourneyShipmentScenarioViewModel> ShipmentScenarios { get; init; } = [];
    public IReadOnlyList<ContractJourneyTransportLegExpenseAllocationViewModel> InventoryTransportExpenseAllocations { get; init; } = [];
    public IReadOnlyList<ContractJourneyActivityItemViewModel> ActivityItems { get; init; } = [];
    public ContractJourneySectionStateViewModel DispatchSectionState { get; init; } = new();
    public ContractJourneySectionStateViewModel PreSaleSectionState { get; init; } = new();
    public IReadOnlyList<ContractJourneyLoadingItemViewModel> LoadingItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyReceiptItemViewModel> ReceiptItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyBulkReceiptCandidateViewModel> BulkReceiptCandidates { get; init; } = [];
    public IReadOnlyList<ContractJourneyReceiptAllocationItemViewModel> ReceiptAllocationItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyInventoryMovementItemViewModel> InventoryMovementItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyTransportLegItemViewModel> InventoryTransportLegItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyDispatchItemViewModel> DispatchItems { get; init; } = [];
    public IReadOnlyList<ContractJourneySaleItemViewModel> SalesItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyPreSaleItemViewModel> PreSaleItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyExpenseItemViewModel> ExpenseItems { get; init; } = [];
    /// <summary>کرایهٔ داخلی ترانسپورت شخصی شرکت — فقط گزارش مدیریتی؛ در دفتر کل و جمع مصارف بالا نیست.</summary>
    public IReadOnlyList<ContractJourneyInternalTransportCostItemViewModel> InternalTransportCostItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyLossItemViewModel> LossItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyPaymentItemViewModel> PaymentItems { get; init; } = [];
    public IReadOnlyList<ContractJourneySarrafSettlementItemViewModel> SarrafSettlementItems { get; init; } = [];
    public ContractJourneyLedgerSummaryViewModel LedgerSummary { get; init; } = new();
    public IReadOnlyList<ContractJourneyLedgerItemViewModel> LedgerItems { get; init; } = [];
    public ContractJourneyMiniPnlViewModel MiniPnl { get; init; } = new();
    public IReadOnlyList<string> NotesForReview { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ContractJourneySummaryMetricsViewModel SummaryMetrics { get; init; } = new();

    // ── پرداختِ تأمین‌کننده: یک تعریف برای صفحه، خلاصه و PDF ──
    /// <summary>
    /// پرداخت‌شدهٔ خالص به تأمین‌کننده = پرداخت‌ها − برگشتی‌ها + تسویهٔ صرافِ ثبت‌شده. در payload خلاصه
    /// فهرست‌ها خالی‌اند و رقمِ آمادهٔ کنترلر (همین قاعده روی همان ردیف‌ها) خوانده می‌شود.
    /// </summary>
    public decimal SupplierPaidNetUsd => IsInitialSummaryPayload && SummaryMetrics.HasValues
        ? SummaryMetrics.SupplierPaidNetUsd
        : PaymentItems
              .Where(p => p.PaymentKind == PaymentKind.SupplierPayment && p.Direction == PaymentDirection.Out)
              .Sum(p => p.AmountUsd)
          - PaymentItems
              .Where(p => p.PaymentKind == PaymentKind.SupplierReceipt && p.Direction == PaymentDirection.In)
              .Sum(p => p.AmountUsd)
          + SarrafSettlementItems
              .Where(s => s.Status == SarrafSettlementStatus.Posted)
              .Sum(s => s.SupplierReductionAmountUsd);

    /// <summary>قابل پرداخت به تأمین‌کننده = ارزشِ خریدِ قیمت‌دارِ قرارداد (فقط قرارداد خرید).</summary>
    public decimal SupplierPayableTotalUsd => IsPurchaseContract ? MiniPnl.TraceablePurchaseCostUsd : 0m;

    public decimal SupplierRemainingUsd => SupplierPayableTotalUsd - SupplierPaidNetUsd;
    public string NextRecommendedActionTitle { get; init; } = string.Empty;
    public string NextRecommendedActionDescription { get; init; } = string.Empty;
    public string NextRecommendedActionUrl { get; init; } = string.Empty;
    public string NextRecommendedActionCssClass { get; init; } = "btn btn-primary";
}

public sealed class ContractJourneySummaryMetricsViewModel
{
    public bool HasValues { get; init; }
    public int LoadingCount { get; init; }
    public int ReceiptCount { get; init; }
    public int InventoryMovementCount { get; init; }
    public int InventoryTransportLegCount { get; init; }
    public int DispatchCount { get; init; }
    public int SaleCount { get; init; }
    public int PreSaleCount { get; init; }
    public int ExpenseCount { get; init; }
    public int LossCount { get; init; }
    public int PaymentCount { get; init; }
    public int LedgerCount { get; init; }
    public decimal LoadingQuantityMt { get; init; }
    public decimal LoadingValueUsd { get; init; }
    public decimal ReceiptQuantityMt { get; init; }
    public int ReceiptTankCount { get; init; }
    public decimal InventoryTransportQuantityMt { get; init; }
    public decimal InventoryTransportReceivedMt { get; init; }
    public decimal InventoryTransportShortageMt { get; init; }
    public decimal InventoryTransportInTransitMt { get; init; }
    public decimal DispatchQuantityMt { get; init; }
    public decimal SaleQuantityMt { get; init; }
    public decimal PreSaleQuantityMt { get; init; }
    public decimal SaleTotalUsd { get; init; }
    public decimal ExpenseTotalUsd { get; init; }
    public decimal InventoryTransportExpenseTotalUsd { get; init; }
    public decimal PaymentTotalUsd { get; init; }
    // پرداخت خالص به تأمین‌کننده در payload خلاصه (PaymentItems در این حالت بارگذاری نمی‌شود).
    public decimal SupplierPaidNetUsd { get; init; }
    public decimal LossQuantityMt { get; init; }
    /// <summary>بخشِ قابل‌جبرانِ ضایعاتِ ثبت‌شده (بر عهدهٔ راننده/حمل‌کننده).</summary>
    public decimal ChargeableLossQuantityMt { get; init; }
    /// <summary>بخشِ بخشیده‌شده (در حدِ تلورانس) از ضایعاتِ ثبت‌شده.</summary>
    public decimal AllowedLossQuantityMt { get; init; }
    public IReadOnlyList<ContractJourneyStorageOverviewItemViewModel> StorageOverviewItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyTransportOverviewItemViewModel> TransportOverviewItems { get; init; } = [];
    public IReadOnlyList<ContractJourneySalesOverviewItemViewModel> SalesOverviewItems { get; init; } = [];
}

public sealed class ContractJourneyRubSettlementSummaryViewModel
{
    public string SettlementCurrencyCode { get; init; } = "USD";
    public RubSettlementRatePolicy RubRatePolicy { get; init; } = RubSettlementRatePolicy.NotApplicable;
    public bool IsRubSettlement => string.Equals(SettlementCurrencyCode, "RUB", StringComparison.OrdinalIgnoreCase);
    public decimal? ContractRubPerUsdRate { get; init; }
    public DateTime? ContractRubRateDate { get; init; }
    public string? ContractRubRateSource { get; init; }
    public decimal LockedAmountUsd { get; init; }
    public decimal LockedAmountRub { get; init; }
    public decimal PendingAmountUsd { get; init; }
    public decimal PendingQuantityMt { get; init; }
    public int LockedLoadingCount { get; init; }
    public int PendingRateLoadingCount { get; init; }
    public decimal? WeightedRubPerUsdRate => LockedAmountUsd > 0m
        ? Math.Round(LockedAmountRub / LockedAmountUsd, 6, MidpointRounding.AwayFromZero)
        : null;

    // ارزش روبلی بارگیری‌های قرارداد از منبع مشترک (LoadingRubValuation) — همان عددی که
    // صفحهٔ تأمین‌کننده نشان می‌دهد. null یعنی هیچ منبع روبلی‌ای موجود نیست (نه صفر).
    // برای قراردادهایی که ارز تسویه‌شان RUB نیست هم پر می‌شود، چون رقم روبلی فایل بارگیری
    // مستقل از SettlementCurrencyCode ثبت می‌شود.
    public decimal? LoadingValueRub { get; init; }
    // true یعنی رقم روبلیِ واقعی نبود و با ContractRubPerUsdRate تبدیل شده است.
    public bool LoadingValueRubIsEstimated { get; init; }
}

public sealed class ContractJourneyStorageOverviewItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
}

public sealed class ContractJourneyTransportOverviewItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed class ContractJourneySalesOverviewItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public decimal AmountUsd { get; init; }
}

public sealed class ContractJourneyKpiSummaryViewModel
{
    public decimal ContractQuantityMt { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal DispatchedQuantityMt { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal? PreSaleQuantityMt { get; init; }
    public decimal CurrentStockQuantityMt { get; init; }
    public decimal LossQuantityMt { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    public decimal TotalPaymentsUsd { get; init; }
    public decimal RelatedBalanceUsd { get; init; }
    public bool PreSaleNeedsReview { get; init; }
    public string? PreSaleNote { get; init; }
}

public sealed class ContractJourneyKpiCardViewModel
{
    public string Icon { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string ToneClass { get; init; } = "journey-kpi-primary";
}

public sealed class ContractJourneyTimelineStepViewModel
{
    public string Icon { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string BadgeText { get; init; } = string.Empty;
    public string BadgeClass { get; init; } = "status-badge status-badge-neutral";
}

public sealed class ContractJourneyQuantityFlowItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public decimal? QuantityMt { get; init; }
    public string DisplayValue { get; init; } = string.Empty;
    public string? HelpText { get; init; }
    public string ToneClass { get; init; } = "journey-progress-neutral";
    public int Percentage { get; init; }
}

public sealed class ContractJourneyExpenseBreakdownViewModel
{
    public string ExpenseTypeName { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal AmountUsd { get; init; }
}

public sealed class ContractJourneyShipmentScenarioViewModel
{
    public int ShipmentId { get; init; }
    public string ShipmentCode { get; init; } = string.Empty;
    public string? VesselName { get; init; }
    public DateTime? DepartureDate { get; init; }
    public DateTime? ArrivalDate { get; init; }
    public string? OriginName { get; init; }
    public string? DestinationName { get; init; }
    public decimal AllocatedQuantityMt { get; init; }
    public decimal PurchaseUnitCostUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public decimal TransportLoadedQuantityMt { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal SalesUsd { get; init; }
    public decimal ExpenseTransactionsUsd { get; init; }
    public decimal SharedExpenseTransactionsUsd { get; init; }
    public decimal CustomsUsd { get; init; }
    public decimal TotalOperationalCostUsd => ExpenseTransactionsUsd + SharedExpenseTransactionsUsd + CustomsUsd;
    public decimal TotalCostUsd => PurchaseCostUsd + TotalOperationalCostUsd;
    public decimal GrossMarginUsd => SalesUsd - TotalCostUsd;
    public decimal RemainingQuantityMt => Math.Max(AllocatedQuantityMt - SoldQuantityMt, 0m);
    public decimal? AverageSalePriceUsd => SoldQuantityMt > 0m ? SalesUsd / SoldQuantityMt : null;
    public decimal? UnitMarginUsd => SoldQuantityMt > 0m ? GrossMarginUsd / SoldQuantityMt : null;
    public int TransportLegCount { get; init; }
    public int SalesCount { get; init; }
    public int ExpenseCount { get; init; }
    public int CustomsCount { get; init; }
}

public sealed class ContractJourneyActivityItemViewModel
{
    public DateTime Date { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string ToneClass { get; init; } = "journey-activity-neutral";
}

public sealed class ContractJourneySectionStateViewModel
{
    public string BadgeText { get; init; } = "قطعی";
    public string BadgeClass { get; init; } = "status-badge status-badge-success";
    public string? Message { get; init; }
    public bool IsNeedsReview { get; init; }
}

public sealed class ContractJourneyLoadingItemViewModel
{
    public int Id { get; init; }
    public DateTime LoadingDate { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string? BillOfLadingNumber { get; init; }
    public string? RwbNo { get; init; }
    public string? TransportTypeName { get; init; }
    public string TransportTypeLabel { get; init; } = string.Empty;
    public string? DocumentSummary { get; init; }
    public string? WagonNumber { get; init; }
    public string? RouteDescription { get; init; }
    public string? LogisticsCompanyName { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal TotalReceivedQuantityMt { get; init; }
    public decimal? PlattsUsd { get; init; }
    public decimal? PremiumDiscountUsd { get; init; }
    public decimal? LoadingPriceUsd { get; init; }
    public string SettlementCurrencyCode { get; init; } = "USD";
    public RubSettlementRateStatus RubRateStatus { get; init; } = RubSettlementRateStatus.NotRequired;
    public decimal? RubPerUsdRate { get; init; }
    public decimal? AmountUsdAtRubLock { get; init; }
    public decimal? AmountRubAtRubLock { get; init; }
    public decimal? SettlementUnitPriceRub { get; init; }
    public decimal? SettlementValueRub { get; init; }
    public string? RubRateSource { get; init; }
    public DateTime? RubRateDate { get; init; }
    public decimal? TransportExpenseUsd { get; init; }
    public decimal? WarehouseExpenseUsd { get; init; }
    public decimal? OtherExpenseUsd { get; init; }
    public decimal? RailwayExpenseUsd { get; init; }
    public decimal LoadingExpenseTotalUsd => (TransportExpenseUsd ?? 0m)
        + (WarehouseExpenseUsd ?? 0m)
        + (OtherExpenseUsd ?? 0m)
        + (RailwayExpenseUsd ?? 0m);
    public decimal? LoadingValueUsd => LoadingPriceUsd.HasValue && LoadingPriceUsd.Value > 0m
        ? Math.Round(LoadedQuantityMt * LoadingPriceUsd.Value, 4, MidpointRounding.AwayFromZero)
        : null;
    // نرخ روبل قفل می‌ماند، اما مبلغ روبلی با ارزش زندهٔ دالری (که قیمت قرارداد را دنبال می‌کند) بازمحاسبه می‌شود.
    public decimal? AmountRubLive =>
        RubRateStatus == RubSettlementRateStatus.Locked && LoadingValueUsd.HasValue && RubPerUsdRate.HasValue
            ? Math.Round(LoadingValueUsd.Value * RubPerUsdRate.Value, 2, MidpointRounding.AwayFromZero)
            : null;
    public bool HasFileRub => SettlementValueRub.HasValue || SettlementUnitPriceRub.HasValue;
    public decimal? FileRubAmount => SettlementValueRub
        ?? (SettlementUnitPriceRub.HasValue
            ? Math.Round(LoadedQuantityMt * SettlementUnitPriceRub.Value, 2, MidpointRounding.AwayFromZero)
            : null);
    public bool IsPricePending => !LoadingPriceUsd.HasValue || LoadingPriceUsd.Value <= 0m;
    public string? ConsigneeName { get; init; }
    public string? DestinationName { get; init; }
    public string? Notes { get; init; }
    public string? VehicleSummary { get; init; }
    /// <summary>نمبر وسیله: پلاک موتر یا شمارهٔ واگن/بارنامهٔ همین بارگیری.</summary>
    public string? VehicleNumber { get; init; }
}

public sealed class ContractJourneyReceiptItemViewModel
{
    public int Id { get; init; }
    public int LoadingRegisterId { get; init; }
    // شمارهٔ واقعی همان بارگیری (پلاک/واگن/RWB/بارنامه) — همان چیزی که در تب بارگیری
    // به‌عنوان «نمبر وسیله» نمایش داده می‌شود؛ فقط برای نمایش.
    public string? LoadingVehicleNumber { get; init; }
    public DateTime ReceiptDate { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public string TerminalName { get; init; } = string.Empty;
    public string? StorageTankCode { get; init; }
    public int? InventoryMovementId { get; init; }
    public decimal RemainingLoadingMt { get; init; }
    public decimal? ActualArrivedQuantityMt { get; init; }
    public decimal? DifferenceQuantityMt { get; init; }
    public string? ReferenceDocument { get; init; }
}

public sealed class ContractJourneyBulkReceiptCandidateViewModel
{
    public int LoadingRegisterId { get; init; }
    public DateTime LoadingDate { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string TransportTypeLabel { get; init; } = string.Empty;
    public string? VehicleNumber { get; init; }
    public string? BillOfLadingNumber { get; init; }
    public string? RwbNo { get; init; }
    public string? WagonNumber { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal? LoadingPriceUsd { get; init; }
    public decimal? LoadingValueUsd { get; init; }
    public decimal AlreadyReceivedQuantityMt { get; init; }
    public decimal RemainingQuantityMt { get; init; }
    public string? ConsigneeName { get; init; }
    public string? DestinationName { get; init; }
}

public sealed class ContractJourneyReceiptAllocationItemViewModel
{
    public int Id { get; init; }
    public int LoadingReceiptId { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string DestinationName { get; init; } = string.Empty;
    public string StatusName { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public string? SourcePurchaseContractNumber { get; init; }
    public string? TerminalName { get; init; }
    public string? StorageTankCode { get; init; }
    public string? DestinationTerminalName { get; init; }
    public string? DestinationStorageTankCode { get; init; }
    public string? DestinationLocationName { get; init; }
    public int? TruckDispatchId { get; init; }
    public int? SalesTransactionId { get; init; }
    public int? InventoryMovementId { get; init; }
    public bool HasQuantityMismatch { get; init; }
}

public sealed class ContractJourneyInventoryMovementItemViewModel
{
    public int Id { get; init; }
    public DateTime MovementDate { get; init; }
    public string DirectionName { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public decimal SignedQuantityMt { get; init; }
    public decimal RunningBalanceMt { get; init; }
    public string TerminalName { get; init; } = string.Empty;
    public string? StorageTankCode { get; init; }
    public string? ReferenceDocument { get; init; }
    public string? SourceLabel { get; init; }
    public int? SalesTransactionId { get; init; }
}

public sealed class ContractJourneyTransportLegItemViewModel
{
    public int Id { get; init; }
    public DateTime LoadedDate { get; init; }
    public DateTime? ReceivedDate { get; init; }
    public string TransportTypeName { get; init; } = string.Empty;
    public string? WagonNumber { get; init; }
    /// <summary>نمبر وسیله: پلاک موتر یا شمارهٔ واگن همین سفر حمل.</summary>
    public string? VehicleNumber { get; init; }
    public string? RwbNo { get; init; }
    public string SourceTerminalName { get; init; } = string.Empty;
    public string? SourceTankCode { get; init; }
    public string? DestinationTerminalName { get; init; }
    public string? DestinationTankCode { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal ShortageQuantityMt { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public int? OutboundInventoryMovementId { get; init; }
    public int? DestinationReceiptId { get; init; }
    public decimal? PurchaseUnitCostUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public decimal OperationalExpensesUsd { get; init; }
    public decimal SalesUsd { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
    public decimal UnsoldQuantityMt { get; init; }
    public string PnlTraceNote { get; init; } = string.Empty;
    public ContractJourneyTransportReceiptDetailViewModel? DestinationReceipt { get; init; }
    public IReadOnlyList<ContractJourneyTransportExpenseDetailViewModel> Expenses { get; init; } = [];
    public IReadOnlyList<ContractJourneyTransportCustomsDetailViewModel> CustomsDeclarations { get; init; } = [];
    public IReadOnlyList<ContractJourneyTransportLossDetailViewModel> Losses { get; init; } = [];
}

public sealed class ContractJourneyTransportReceiptDetailViewModel
{
    public int Id { get; init; }
    public DateTime ReceiptDate { get; init; }
    public InventoryTransportReceiptDestination ReceiptDestination { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal ShortageQuantityMt { get; init; }
    public decimal? AllowanceMt { get; init; }
    public decimal? ChargeableShortageMt { get; init; }
    public decimal? FreightRateUsdPerMt { get; init; }
    public decimal? FreightCostUsd { get; init; }
    public decimal? ShortageRateUsd { get; init; }
    public decimal? ShortageChargeUsd { get; init; }
    public decimal? FreightPayableUsd { get; init; }
    public string? DestinationTerminalName { get; init; }
    public string? DestinationTankCode { get; init; }
    public int? InventoryMovementId { get; init; }
    public int? SalesTransactionId { get; init; }
    public string? SaleInvoiceNumber { get; init; }
    public int DirectTruckDispatchCount { get; init; }
    public decimal DirectTruckDispatchedQuantityMt { get; init; }
    public int? FirstDirectTruckDispatchId { get; init; }
}

public sealed class ContractJourneyTransportExpenseDetailViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
}

public sealed class ContractJourneyTransportCustomsDetailViewModel
{
    public int Id { get; init; }
    public DateTime DeclarationDate { get; init; }
    public string? WagonOrTruckNumber { get; init; }
    public string? DeclarationReference { get; init; }
    public decimal? ConsignmentWeightMt { get; init; }
    public decimal TotalAfn { get; init; }
    public decimal TotalUsd { get; init; }
}

public sealed class ContractJourneyTransportLossDetailViewModel
{
    public int Id { get; init; }
    public DateTime EventDate { get; init; }
    public string StageName { get; init; } = string.Empty;
    public decimal ExpectedQuantityMt { get; init; }
    public decimal ActualQuantityMt { get; init; }
    public decimal DifferenceQuantityMt { get; init; }
    public decimal ChargeableLossMt { get; init; }
    public string? Reference { get; init; }
}

public sealed class ContractJourneyDispatchItemViewModel
{
    public int Id { get; init; }
    public TruckDispatchMode DispatchMode { get; init; } = TruckDispatchMode.FromInventory;
    public int? LoadingReceiptAllocationId { get; init; }
    public int? LoadingReceiptId { get; init; }
    public DateTime DispatchDate { get; init; }
    public string TruckPlateNumber { get; init; } = string.Empty;
    public string? DriverName { get; init; }
    public string? DestinationName { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public decimal LoadedQuantityMt { get; init; }
    public decimal? DischargedQuantityMt { get; init; }
    public decimal? AllocationQuantityMt { get; init; }
    public decimal? AllocationTotalDirectDispatchedQuantityMt { get; init; }
    public decimal? AllocationRemainingQuantityMt { get; init; }
    public string? SourceTerminalName { get; init; }
    public string? SourceStorageTankCode { get; init; }
    public decimal? FreightCostUsd { get; init; }
    public string? ReferenceDocument { get; init; }
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class ContractJourneySaleItemViewModel
{
    public int SalesTransactionId { get; init; }
    public int? ShipmentId { get; init; }
    public int? TruckDispatchId { get; init; }
    public int? InventoryTransportLegId { get; init; }
    public int? InventoryTransportReceiptId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTime SaleDate { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal AmountUsd { get; init; }
    /// <summary>قیمت فروش هنوز تعیین نشده؛ مبلغ‌ها «نامشخص»اند، نه صفر.</summary>
    public bool IsSalePricePending { get; init; }
    public string SalesContractDisplay { get; init; } = string.Empty;
    public string? StockSourceTypeName { get; init; }
    public string? SaleStageName { get; init; }
    public bool HasInventoryMovementTrace { get; init; }
    public string? SourcePurchaseContractNumber { get; init; }
    public string? InventoryTransportReference { get; init; }
    public int? LoadingReceiptAllocationId { get; init; }
    public decimal? AllocationQuantityMt { get; init; }
    public decimal? PurchaseUnitCostUsd { get; init; }
    public decimal? PurchaseCostUsd { get; init; }
    public decimal? TransportLegExpenseCostUsd { get; init; }
    public decimal? TransportLegCustomsCostUsd { get; init; }
    public string? CostAllocationNote { get; init; }
    public decimal? TransportCostUsd =>
        TransportLegExpenseCostUsd.HasValue || TransportLegCustomsCostUsd.HasValue
            ? (TransportLegExpenseCostUsd ?? 0m) + (TransportLegCustomsCostUsd ?? 0m)
            : null;
    public decimal? TotalCostUsd =>
        PurchaseCostUsd.HasValue || TransportCostUsd.HasValue
            ? (PurchaseCostUsd ?? 0m) + (TransportCostUsd ?? 0m)
            : null;
    public decimal? GrossProfitUsd => TotalCostUsd.HasValue ? AmountUsd - TotalCostUsd.Value : null;
    public bool HasQuantityMismatch { get; init; }
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class ContractJourneyPreSaleItemViewModel
{
    public int SalesTransactionId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTime SaleDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string SalesContractDisplay { get; init; } = string.Empty;
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class ContractJourneyExpenseItemViewModel
{
    public int ExpenseTransactionId { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public DateTime ExpenseDate { get; init; }
    public decimal AmountUsd { get; init; }
    public string? ShipmentCode { get; init; }
    public string? DispatchLabel { get; init; }
    public int? TransportLegId { get; init; }
    public string? TransportLegLabel { get; init; }
    public decimal? TransportLegQuantityMt { get; init; }
    public decimal? TransportLegExpensePerMtUsd { get; init; }
    public string? Description { get; init; }
    /// <summary>نمبر وسیلهٔ مرتبط با این مصرف: پلاک موتر ارسال یا شمارهٔ واگن حمل.</summary>
    public string? VehicleNumber { get; init; }
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class ContractJourneyInternalTransportCostItemViewModel
{
    public int AssetRentTransactionId { get; init; }
    public int? LoadingRegisterId { get; init; }
    public DateTime RentDate { get; init; }
    public string AssetName { get; init; } = string.Empty;
    public decimal? QuantityMt { get; init; }
    public decimal? RateUsdPerMt { get; init; }
    public decimal AmountUsd { get; init; }
}

public sealed class ContractJourneyTransportLegExpenseAllocationViewModel
{
    public int TransportLegId { get; init; }
    public DateTime LoadedDate { get; init; }
    public string TransportTypeName { get; init; } = string.Empty;
    public string TransportReference { get; init; } = string.Empty;
    public string? RwbNo { get; init; }
    public string SourceLocation { get; init; } = string.Empty;
    public string DestinationLocation { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public int ExpenseCount { get; init; }
    public decimal ExpenseTotalUsd { get; init; }
    public decimal? ExpensePerMtUsd { get; init; }
}

public sealed class ContractJourneyLossItemViewModel
{
    public int LossEventId { get; init; }
    public string StageName { get; init; } = string.Empty;
    public DateTime EventDate { get; init; }
    public decimal ExpectedQuantityMt { get; init; }
    public decimal ActualQuantityMt { get; init; }
    public decimal DifferenceQuantityMt { get; init; }
    public decimal ToleranceQuantityMt { get; init; }
    public decimal AllowableLossMt { get; init; }
    public decimal ChargeableLossMt { get; init; }
    public int? RelatedMovementId { get; init; }
    /// <summary>نمبر وسیلهٔ مرحله‌ای که کسری روی آن ثبت شده: بارگیری، حمل داخلی یا ارسال با موتر.</summary>
    public string? VehicleNumber { get; init; }
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class ContractJourneyPaymentItemViewModel
{
    public int PaymentTransactionId { get; init; }
    public DateTime PaymentDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string DirectionName { get; init; } = string.Empty;
    public PaymentKind PaymentKind { get; init; }
    public string PaymentKindName { get; init; } = string.Empty;
    public string CashAccountName { get; init; } = string.Empty;
    /// <summary>منبع تأمین پول: صندوق شرکت یا جیب یکی از شرکای همین قرارداد.</summary>
    public PaymentFundingSource FundingSource { get; init; } = PaymentFundingSource.Company;
    /// <summary>نام شریکی که پرداخت را انجام داده؛ فقط وقتی FundingSource = Partner است.</summary>
    public string? PaidByPartnerName { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
    public int? LedgerEntryId { get; init; }
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class ContractJourneySarrafSettlementItemViewModel
{
    public int SarrafSettlementId { get; init; }
    public DateTime SettlementDate { get; init; }
    public string SarrafName { get; init; } = string.Empty;
    public string? SupplierName { get; init; }
    public string? ReferenceNumber { get; init; }
    public decimal RequestedAmountUsd { get; init; }
    public decimal SarrafChargedAmountUsd { get; init; }
    public decimal SupplierAcceptedAmountUsd { get; init; }
    public decimal SupplierReductionAmountUsd { get; init; }
    public decimal? SupplierReductionAmountRub { get; init; }
    public decimal DifferenceAmountUsd { get; init; }
    public SarrafSettlementDifferenceType DifferenceType { get; init; }
    public SarrafSettlementDifferenceTreatment DifferenceTreatment { get; init; }
    public SarrafSettlementStatus Status { get; init; }
    public int? LedgerEntryId { get; init; }
    public int? ExchangeDifferenceLedgerEntryId { get; init; }
}

public sealed class ContractJourneyLedgerSummaryViewModel
{
    public decimal DebitTotalUsd { get; init; }
    public decimal CreditTotalUsd { get; init; }
    public decimal BalanceUsd { get; init; }
    public IReadOnlyList<ContractJourneySourceCountViewModel> SourceTypeCounts { get; init; } = [];
    public IReadOnlyList<string> ReferenceList { get; init; } = [];
}

public sealed class ContractJourneySourceCountViewModel
{
    public string SourceType { get; init; } = string.Empty;
    public int Count { get; init; }
}

public sealed class ContractJourneyLedgerItemViewModel
{
    public int LedgerEntryId { get; init; }
    public DateTime EntryDate { get; init; }
    public string SideName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string Description { get; init; } = string.Empty;
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class ContractJourneyMiniPnlViewModel
{
    [Display(Name = "فروش قابل‌ردیابی")]
    public decimal TraceableSalesRevenueUsd { get; init; }

    // مقدار فروخته‌شده؛ مبنای «هزینه تمام‌شده کالای فروخته‌شده» و تسهیم مصارف.
    public decimal SoldQuantityMt { get; init; }

    // کل هزینه خرید بارگیری‌شده (بدهی به تأمین‌کننده). برای نمایش است و در
    // محاسبه سود استفاده نمی‌شود؛ سود بر پایه هزینه کالای فروخته‌شده حساب می‌شود.
    public decimal TraceablePurchaseCostUsd { get; init; }

    public decimal PricedPurchaseQuantityMt { get; init; }

    public decimal PendingPurchaseQuantityMt { get; init; }

    public decimal? WeightedAveragePurchasePriceUsd { get; init; }

    public bool NeedsReview => PendingPurchaseQuantityMt > 0m;

    [Display(Name = "مصرف قابل‌ردیابی")]
    public decimal TraceableExpensesUsd { get; init; }

    /// <summary>مصارفِ پولیِ قرارداد (بارگیری، گمرک، مصارف ثبت‌شده)؛ هر سند یک بار، بی‌ارزشِ ضایعات.</summary>
    public decimal RecordedExpensesUsd { get; init; }

    /// <summary>ارزشِ ضایعاتِ قابل‌شارژی که در مفاد قرارداد کسر می‌شود (همان رقمِ موتور مفاد).</summary>
    public decimal LossCostUsd { get; init; }

    /// <summary>بهای تمام‌شدهٔ کالای هنوز فروخته‌نشده؛ مفاد نمی‌سازد (همان رقمِ حساب شرکا).</summary>
    public decimal UnsoldCostUsd { get; init; }

    /// <summary>اطمینانِ مفادِ قرارداد از موتور مفاد؛ غیرِ «قطعی» یعنی رقم برآوردی است.</summary>
    public PTGOilSystem.Web.Services.Reporting.PnlConfidence Confidence { get; init; } = PTGOilSystem.Web.Services.Reporting.PnlConfidence.Verified;

    // سودِ محققِ قرارداد — همهٔ اعداد از ProfitAndLossService.BuildContractEconomicsAsync (همان مرجعِ
    // گزارش مفاد قراردادها و صورت‌حساب شراکت). این ViewModel هیچ فرمولی ندارد.
    /// <summary>بهای کالای فروخته‌شده = مقدار فروش × میانگین وزنی قیمت خرید.</summary>
    public decimal CostOfGoodsSoldUsd { get; init; }
    /// <summary>سهمِ فروخته‌شده از بارِ قیمت‌دار؛ مبنای تسهیمِ هزینه‌ها.</summary>
    public decimal SoldShareRatio { get; init; }
    /// <summary>سهمِ فروخته‌شدهٔ هزینه‌های عملیاتی.</summary>
    public decimal ExpensesForSoldUsd { get; init; }
    [Display(Name = "حاشیه ناخالص")]
    public decimal GrossMarginUsd { get; init; }
    /// <summary>اثرِ ارزیِ محققِ قرارداد (سود − زیان − کسریِ صراف).</summary>
    public decimal RealizedFxNetUsd { get; init; }
    [Display(Name = "سود محقق قرارداد")]
    public decimal RealizedNetProfitUsd { get; init; }

    /// <summary>Full-cycle contract margin from the canonical engine; includes all recorded purchase and operational costs.</summary>
    public decimal LifecycleMarginUsd { get; init; }

    // ===== سود محقق‌شدهٔ حسابداری: تنها منبع مجاز ProfitAndLossService =====
    // اعداد بالا برآورد عملیاتی قرارداد هستند (میانگین وزنی خرید + تسهیم مصارف).
    // اعداد زیر همان چیزی است که در Company/Contract P&L و گزارش‌ها گزارش می‌شود.
    public RealisedPnlViewModel Realised { get; init; } = RealisedPnlViewModel.Empty;

    public decimal OperationalVsRealisedVarianceUsd
        => decimal.Round(GrossMarginUsd - Realised.GrossProfitUsd, 2, MidpointRounding.AwayFromZero);

    public bool HasOperationalVsRealisedVariance
        => Math.Abs(OperationalVsRealisedVarianceUsd) > 0.01m;

    public string OperationalVsRealisedVarianceReasonFa
        => Realised.HasUncostedSales
            ? $"{Realised.UncostedSaleCount} فروش این قرارداد بهای تمام‌شدهٔ فعال ندارد؛ سود حسابداری برای آن‌ها صفر COGS نمی‌گیرد و «قطعی» شمرده نمی‌شود."
            : NeedsReview
                ? "بخشی از خرید هنوز قیمت‌گذاری نشده؛ برآورد عملیاتی ناقص است."
                : "برآورد عملیاتی سهم مصارف مسیر را هم کسر می‌کند، اما COGS حسابداری فقط بهای انبارِ مصرف‌شده است.";

    public string Note { get; init; } = string.Empty;
}
