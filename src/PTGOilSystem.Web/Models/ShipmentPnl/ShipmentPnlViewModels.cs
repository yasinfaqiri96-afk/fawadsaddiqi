using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.ShipmentPnl;

public sealed class ShipmentPnlListItemViewModel
{
    public int Id { get; init; }
    public string ShipmentCode { get; init; } = string.Empty;
    public string? VesselName { get; init; }
    public DateTime? DepartureDate { get; init; }
    public DateTime? ArrivalDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractUnitText { get; init; } = "—";
    public string? ProductName { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? OriginName { get; init; }
    public string? DestinationName { get; init; }
    public decimal TotalSalesUsd { get; init; }
    public decimal TotalPurchaseCostUsd { get; init; }
    public decimal TotalOperationalExpensesUsd { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
    public int RelatedTransportLegCount { get; init; }
    public int RelatedSalesCount { get; init; }
    public int RelatedExpensesCount { get; init; }
    public int RelatedLedgerCount { get; init; }

    // ===== سود محقق‌شدهٔ فروش: تنها منبع مجاز ProfitAndLossService است =====
    public RealisedPnlViewModel RealisedPnl { get; init; } = RealisedPnlViewModel.Empty;
}

/// <summary>
/// یک ردیف لیست محموله‌ها. عمداً فقط چیزی را دارد که لیست نشان می‌دهد؛ ارقام بهای خرید و
/// مصرف در پروندهٔ محموله و خروجی (<see cref="ShipmentPnlListItemViewModel"/>) می‌مانند تا
/// باز کردن لیست به رول‌آپ کامل مالی گره نخورد.
/// </summary>
public sealed class ShipmentPnlIndexRowViewModel
{
    public int Id { get; init; }
    public string ShipmentCode { get; init; } = string.Empty;
    public string? VesselName { get; init; }
    public DateTime? DepartureDate { get; init; }
    public DateTime? ArrivalDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? ProductName { get; init; }
    public string? OriginName { get; init; }
    public string? DestinationName { get; init; }
    public int RelatedTransportLegCount { get; init; }
    public int RelatedSalesCount { get; init; }
    public int RelatedExpensesCount { get; init; }

    // ===== سود محقق‌شدهٔ فروش: تنها منبع مجاز ProfitAndLossService است =====
    public decimal TotalSalesUsd { get; init; }
    public RealisedPnlViewModel RealisedPnl { get; init; } = RealisedPnlViewModel.Empty;
}

public sealed class ShipmentPnlIndexViewModel
{
    public IReadOnlyList<ShipmentPnlIndexRowViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public ShipmentPnlIndexTotalsViewModel Totals { get; init; } = new();
}

/// <summary>
/// اعداد کارت‌های آمار بالای لیست محموله‌ها. روی همهٔ محموله‌ها هستند، پس فقط از query سبک
/// ساخته می‌شوند؛ سود محقق‌شده مثل بقیهٔ صفحات از ProfitAndLossService می‌آید.
/// </summary>
public sealed record ShipmentPnlIndexTotalsViewModel
{
    public int TotalCount { get; init; }
    public decimal SumQuantityMt { get; init; }
    public int SumRelatedSalesCount { get; init; }
    public decimal RealisedGrossProfitUsd { get; init; }
}

public sealed class ShipmentPnlSalesItemViewModel
{
    public int Id { get; init; }
    public DateTime SaleDate { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public string? ContractNumber { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal TotalUsd { get; init; }
    /// <summary>قیمت فروش هنوز تعیین نشده؛ مبلغ نامشخص است و در عاید محقق‌شده نیست.</summary>
    public bool IsSalePricePending { get; init; }
    // true = فروش مستقیم از محموله (ShipmentId روی خود فروش)، false = فروش پس از ورود به مخزن که
    // Lineage آن به همین محموله می‌رسد. برای تفکیک نمایشی؛ هر فروش فقط یک بار در جمع می‌آید.
    public bool IsDirectShipmentSale { get; init; }
    /// <summary>نمبر وسیلهٔ فروش: پلاک موتری که فروش از آن انجام شده.</summary>
    public string? VehicleNumber { get; init; }
    public IReadOnlyList<ShipmentContractBreakdownLine> ContractBreakdownLines { get; init; } = [];
}

public sealed class ShipmentPnlExpenseItemViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
    public string? ContractNumber { get; init; }
    public string? TruckDispatchLabel { get; init; }
    /// <summary>نمبر وسیلهٔ مصرف: پلاک موتر یا شمارهٔ واگن/بارنامهٔ سفر حمل.</summary>
    public string? VehicleNumber { get; init; }
    public decimal AllocationQuantityMt { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public bool IsCustoms { get; init; }
    /// <summary>ExpenseType.Category دیتابیس؛ ورودی ساخت‌یافتهٔ دسته‌بندی نمایشی.</summary>
    public string? ExpenseTypeCategory { get; init; }
}

public sealed class ShipmentPnlTransportLegItemViewModel
{
    public int Id { get; init; }
    public DateTime LoadedDate { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractUnitText { get; init; } = "-";
    public string? ProductName { get; init; }
    public string? SupplierName { get; init; }
    public string? TransportReference { get; init; }
    public string? DocumentReference { get; init; }
    public string? TransportGroupKey { get; init; }
    public string TransportTypeName { get; init; } = "-";
    public string TransportStatusName { get; init; } = "-";
    public bool IsOriginalVesselMovement { get; init; }
    // مبدأ این حمل یک مخزن است؛ یعنی سفر بعد از تخلیهٔ کشتی (نه مرحلهٔ اولِ کشتی→مخزن).
    public bool SourceIsStorageTank { get; init; }
    public bool IsDraft { get; init; }
    public string SourceName { get; init; } = "-";
    public string DestinationName { get; init; } = "-";
    public string? Notes { get; init; }
    public decimal QuantityMt { get; init; }
    public bool HasOutboundInventoryMovement { get; init; }
    public decimal? PurchaseUnitCostUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public string CostSource { get; init; } = string.Empty;
    public decimal ReceivedQuantityMt { get; init; }
    public decimal ShortageQuantityMt { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal SalesUsd { get; init; }
    public decimal OperationalExpensesUsd { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
    public decimal UnsoldQuantityMt { get; init; }
    public string SalesTraceNote { get; init; } = string.Empty;
}

public sealed class ShipmentPnlLedgerItemViewModel
{
    public int Id { get; init; }
    public DateTime EntryDate { get; init; }
    public string SideName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string Description { get; init; } = string.Empty;
}

// یک قرارداد خرید داخل محموله، با مقدار تخصیص/استفاده‌شده/باقی‌مانده (فقط نمایش).
public sealed class ShipmentContractLineViewModel
{
    public int ContractId { get; init; }
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string? SupplierName { get; init; }
    public string? ProductName { get; init; }
    public string ContractUnitText { get; init; } = "-";
    public decimal AllocatedQuantityMt { get; init; }
    // آیا برای این قرارداد نرخ مؤثر خرید پیدا شد؟ (میانگین وزنی بارگیری‌ها یا نرخ نهایی هدر —
    // ShipmentPurchaseCostResolver). اگر نه، بهای خرید این مقدار در «سود کل محموله» شمرده نمی‌شود.
    public bool HasFinalPrice { get; init; }
    // منبع نرخ (ثابت‌های ShipmentPurchaseCostResolver) — برای توضیح‌پذیریِ «این نرخ از کجا آمده».
    public string? UnitPriceSource { get; init; }
    // Only the original vessel stage consumes the shipment allocation.
    public decimal UsedQuantityMt { get; init; }
    public decimal TransportedFromInventoryQuantityMt { get; init; }
    public decimal TransportShortageQuantityMt { get; init; }
    public decimal DirectLossQuantityMt { get; init; }
    public decimal RemainingBeforeLossQuantityMt
        => decimal.Round(AllocatedQuantityMt - UsedQuantityMt, 4, MidpointRounding.AwayFromZero);
    public decimal RemainingQuantityMt
        => decimal.Round(Math.Max(RemainingBeforeLossQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    public decimal LoadedAvailableBeforeDirectLossQuantityMt
        => decimal.Round(Math.Max(UsedQuantityMt - TransportShortageQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    public decimal ShortageRegisterableQuantityMt
        => decimal.Round(Math.Max(LoadedAvailableBeforeDirectLossQuantityMt - DirectLossQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    // نرخ نهایی و ارزش تخمینی قرارداد (USD) — فقط نمایش؛ از ContractPricingAdapter موجود گرفته می‌شود.
    public decimal? UnitPriceUsd { get; init; }
    public decimal? TotalValueUsd { get; init; }
    public decimal? DirectLossValueUsd
        => UnitPriceUsd.HasValue && UnitPriceUsd.Value > 0m && DirectLossQuantityMt > 0m
            ? decimal.Round(DirectLossQuantityMt * UnitPriceUsd.Value, 2, MidpointRounding.AwayFromZero)
            : null;
}

public sealed class ShipmentPnlDispatchTraceItemViewModel
{
    public int DeliveryReceiptId { get; init; }
    public DateTime ReceiptDate { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public string? DocumentReference { get; init; }
    public int? TruckDispatchId { get; init; }
    public DateTime? DispatchDate { get; init; }
    public string? TruckPlateNumber { get; init; }
    public decimal? LoadedQuantityMt { get; init; }
}

public sealed class ShipmentPnlRegisteredVesselReceiptItemViewModel
{
    public int Id { get; init; }
    // رسیدِ حمل صفحهٔ جزئیات جدا ندارد؛ جزئیاتش در پروندهٔ همان leg دیده می‌شود.
    public int InventoryTransportLegId { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string ContractNumber { get; init; } = "-";
    public string DestinationTerminalName { get; init; } = "-";
    public string? DestinationTankName { get; init; }
    public decimal ReceivedQuantityMt { get; init; }

    // برای deep-link «حمل بعدی از این مخزن» (پیش‌پرکردن مبدأ در فرم انتقال از موجودی).
    public int? DestinationTerminalId { get; init; }
    public int? DestinationStorageTankId { get; init; }
    public int ProductId { get; init; }

    public bool CanChainOnward => DestinationTerminalId.HasValue
        && DestinationStorageTankId.HasValue
        && ProductId > 0;
}

// یک بار «ثبت رسید» گروهی، به‌ازای هر قرارداد منبع یک رسید جدا می‌سازد (تقسیم چندقراردادی).
// این مدل فقط برای نمایش است: همان رسیدها را زیر یک سطرِ جمع کنار هم می‌گذارد تا کاربر
// اول مقدار کل را ببیند و با باز کردن سطر، سهم هر قرارداد را. داده و محاسبه تغییر نمی‌کند.
public sealed class ShipmentPnlRegisteredVesselReceiptGroupViewModel
{
    public int Key { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string DestinationTerminalName { get; init; } = "-";
    public string? DestinationTankName { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public IReadOnlyList<ShipmentPnlRegisteredVesselReceiptItemViewModel> Items { get; init; } = [];

    public bool IsSplitAcrossContracts => Items.Count > 1;
}

// یک دریافت/پرداخت نقدیِ وصل‌شده به همین کشتی (از PaymentTransaction.ShipmentId).
// فقط نمایش؛ هیچ محاسبهٔ حاشیهٔ سود به آن وابسته نیست.
public sealed class ShipmentPnlCustomerReceiptItemViewModel
{
    public int Id { get; init; }
    public DateTime PaymentDate { get; init; }
    public string? CustomerName { get; init; }
    public string? Reference { get; init; }
    public string? CashAccountName { get; init; }
    public decimal AmountUsd { get; init; }
    public bool IsInflow { get; init; }
    public decimal SignedAmountUsd => IsInflow ? AmountUsd : -AmountUsd;
}

public sealed class ShipmentPnlDetailsViewModel
{
    public int Id { get; init; }
    public string ShipmentCode { get; init; } = string.Empty;
    public int? VesselId { get; init; }
    public string? VesselName { get; init; }
    public DateTime? DepartureDate { get; init; }
    public DateTime? ArrivalDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? Notes { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractUnitText { get; init; } = "—";
    public string? CompanyName { get; init; }
    public string? ProductName { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? OriginName { get; init; }
    public string? DestinationName { get; init; }
    public decimal TotalSalesUsd { get; init; }
    public decimal TotalPurchaseCostUsd { get; init; }
    public decimal TotalOperationalExpensesUsd { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
    // سود محقق‌شدهٔ فروش، مستقیماً از ProfitAndLossService.
    public RealisedPnlViewModel RealisedPnl { get; init; } = RealisedPnlViewModel.Empty;
    public int LedgerEntriesCount { get; init; }
    public decimal LedgerDebitTotalUsd { get; init; }
    public decimal LedgerCreditTotalUsd { get; init; }

    // دریافت‌های نقدیِ وصل‌شده به همین کشتی (PaymentTransaction با ShipmentId و طرف مشتری).
    // خالص = دریافت (In) منهای بازپرداخت به مشتری (Out). فقط نمایش.
    public IReadOnlyList<ShipmentPnlCustomerReceiptItemViewModel> CustomerReceipts { get; init; } = [];
    public decimal CustomerReceiptsUsd { get; init; }
    public decimal OutstandingReceivableUsd
        => decimal.Round(TotalSalesUsd - CustomerReceiptsUsd, 2, MidpointRounding.AwayFromZero);
    public decimal CollectionCoveragePercent
        => TotalSalesUsd > 0m
            ? decimal.Round(Math.Min(Math.Max(CustomerReceiptsUsd / TotalSalesUsd, 0m), 1m) * 100m, 1, MidpointRounding.AwayFromZero)
            : 0m;
    public IReadOnlyList<ShipmentPnlSalesItemViewModel> Sales { get; init; } = [];
    public IReadOnlyList<ShipmentPnlExpenseItemViewModel> Expenses { get; init; } = [];
    public IReadOnlyList<ShipmentPnlTransportLegItemViewModel> TransportLegs { get; init; } = [];
    public IReadOnlyList<ShipmentPnlLedgerItemViewModel> LedgerEntries { get; init; } = [];
    public IReadOnlyList<ShipmentPnlDispatchTraceItemViewModel> DispatchTraces { get; init; } = [];
    public IReadOnlyList<ShipmentPnlRegisteredVesselReceiptItemViewModel> RegisteredVesselReceipts { get; init; } = [];
    // همان RegisteredVesselReceipts، فقط برای نمایش گروه‌شده بر اساس یک بار ثبت رسید.
    public IReadOnlyList<ShipmentPnlRegisteredVesselReceiptGroupViewModel> RegisteredVesselReceiptGroups { get; init; } = [];
    public decimal RegisteredVesselReceiptQuantityMt
        => decimal.Round(RegisteredVesselReceipts.Sum(r => r.ReceivedQuantityMt), 4, MidpointRounding.AwayFromZero);
    // قراردادهای خرید داخل این محموله (از ShipmentContracts) با مقدار باقی‌مانده.
    public IReadOnlyList<ShipmentContractLineViewModel> ContractLines { get; init; } = [];
    // ریز منابع بهای خرید: هر سطر یک بارگیریِ تخصیص‌یافته (یا در دادهٔ قدیمی، یک قرارداد).
    // فقط نمایش؛ همان اعدادی که TotalPurchaseCostUsd از آن‌ها ساخته شده است.
    public IReadOnlyList<ShipmentPurchaseSourceLine> PurchaseSourceLines { get; init; } = [];
    public bool HasLoadingExactPurchaseSources => PurchaseSourceLines.Any(l => l.IsLoadingExact);
    public decimal PurchaseSourceQuantityMt
        => decimal.Round(PurchaseSourceLines.Sum(l => l.QuantityMt), 4, MidpointRounding.AwayFromZero);
    public decimal? PurchaseSourceWeightedAverageUsd
    {
        get
        {
            var priced = PurchaseSourceLines.Where(l => l.UnitCostUsd is > 0m).ToList();
            var quantityMt = priced.Sum(l => l.QuantityMt);
            return quantityMt > 0m
                ? decimal.Round(priced.Sum(l => l.ExtendedCostUsd) / quantityMt, 4, MidpointRounding.AwayFromZero)
                : null;
        }
    }
    public decimal TotalCargoValueUsd
    {
        get
        {
            var contractValueUsd = ContractLines.Sum(c => c.TotalValueUsd ?? 0m);
            if (contractValueUsd > 0m)
            {
                return contractValueUsd;
            }

            // اگر ارزش قراردادی هنوز نرخ نداشته باشد، از مجموع مصرف خرید حمل‌ها استفاده می‌کنیم
            // تا کارت بالایی با جدول سود و زیان به تفکیک حمل هم‌خوان بماند.
            return TransportLegs.Sum(l => l.PurchaseCostUsd);
        }
    }
    // ضایعات مستقیم محموله + اخطارهای نمایشی (از منطق نمای کشتی منتقل شده؛ بدون محاسبهٔ مالی جدید).
    public IReadOnlyList<ShipmentJourneyLossItem> Losses { get; init; } = [];
    public IReadOnlyList<ShipmentSaleDisplayRow> SaleDisplayRows
        => ShipmentPnlDisplayGrouping.GroupSales(Sales);
    // تفکیک فروش منتسب به محموله (هر فروش فقط یک بار؛ جمع دو گروه = کل فروش).
    public decimal DirectSaleQuantityMt
        => decimal.Round(Sales.Where(s => s.IsDirectShipmentSale).Sum(s => s.QuantityMt), 4, MidpointRounding.AwayFromZero);
    public decimal DirectSaleUsd
        => decimal.Round(Sales.Where(s => s.IsDirectShipmentSale).Sum(s => s.TotalUsd), 4, MidpointRounding.AwayFromZero);
    public decimal StorageSaleQuantityMt
        => decimal.Round(Sales.Where(s => !s.IsDirectShipmentSale).Sum(s => s.QuantityMt), 4, MidpointRounding.AwayFromZero);
    public decimal StorageSaleUsd
        => decimal.Round(Sales.Where(s => !s.IsDirectShipmentSale).Sum(s => s.TotalUsd), 4, MidpointRounding.AwayFromZero);
    public bool HasStorageOriginSales => Sales.Any(s => !s.IsDirectShipmentSale);
    public bool HasDirectShipmentSales => Sales.Any(s => s.IsDirectShipmentSale);
    public IReadOnlyList<ShipmentExpenseDisplayRow> ExpenseDisplayRows
        => ShipmentPnlDisplayGrouping.GroupExpenses(Expenses);
    /// <summary>
    /// گروه‌های دسته‌بندی مصارف — منبع واحد کارت‌های KPI، گروه‌های تب «مصارف و گمرک»
    /// و جدول «جزئیات هزینه‌ها بر اساس دسته». View نباید خودش دسته‌بندی کند.
    /// </summary>
    public IReadOnlyList<ShipmentExpenseCategoryGroup> ExpenseCategoryGroups
        => ShipmentExpenseCategorizer.Group(ExpenseDisplayRows);
    public IReadOnlyList<ShipmentLossDisplayRow> LossDisplayRows
        => ShipmentPnlDisplayGrouping.GroupLosses(Losses, ContractLines);
    public IReadOnlyList<ShipmentTransportDisplayRow> TransportDisplayRows
        => ShipmentPnlDisplayGrouping.GroupTransports(TransportLegs);
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasLineageData { get; init; }
    public bool IsLineageCalculationActive { get; init; }
    public string? LineageBadgeText { get; init; }
    public string? LineageBadgeTone { get; init; }
    public string? OverallLineageConfidence { get; init; }
    public bool HasVerifiedLineage { get; init; }
    public bool HasEstimatedLineage { get; init; }
    public bool HasLegacyLineage { get; init; }
    public int NeedsReviewCount { get; init; }
    public IReadOnlyList<string> LineageWarnings { get; init; } = [];
    public decimal? LineageCargoInVesselMt { get; init; }
    public decimal? LineageArrivedReceivedMt { get; init; }
    public decimal? LineageInTransitMt { get; init; }
    public decimal? LineageInStockMt { get; init; }
    public decimal? LineageSoldMt { get; init; }
    public decimal? LineageSoldUsd { get; init; }
    public decimal? LineageLossMt { get; init; }
    public decimal? LineageExpenseUsd { get; init; }
    public decimal? LineageCustomsUsd { get; init; }
    public decimal LossQuantityMt { get; init; }
    public decimal DirectLossQuantityMt { get; init; }
    // مجموع مقدار ضایعاتی که مسئولیتش «ضرر شرکت» ثبت شده — فقط در محاسبهٔ تحقق‌یافتهٔ
    // قدیمی استفاده می‌شود؛ نتیجهٔ کل محموله قیمت خرید بار را قبلاً کامل کسر کرده است.
    public decimal CompanyLossQuantityMt { get; init; }

    // ===== مرحله‌های مستقل جریان مقدار (MT) =====
    // Explicit stages prevent later inventory transfers from inflating vessel cargo.
    public decimal OriginalShipmentQuantityMt { get; init; }
    public decimal VesselUnloadedQuantityMt { get; init; }
    public decimal InventoryTransportedOutQuantityMt { get; init; }
    public decimal InTransitQuantityMt { get; init; }
    public decimal DeliveredAtDestinationQuantityMt { get; init; }
    public decimal RemainingInSourceTankQuantityMt { get; init; }
    public decimal InventoryTransportShortageQuantityMt { get; init; }
    public decimal CancelledTransportQuantityMt { get; init; }
    public decimal DraftTransportQuantityMt { get; init; }
    public bool HasExactSourceTankLineage { get; init; }

    public decimal ShipmentSalesQuantityMt { get; init; }
    public decimal SoldQuantityMt
        => decimal.Round(Math.Max(ShipmentSalesQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    public decimal DispatchedQuantityMt => DispatchTraces.Sum(d => d.ReceivedQuantityMt);

    // ===== کسری و ضایعه: سه سطلِ بدون هم‌پوشانی و یک جمع =====
    // هر مقدارِ ازدست‌رفته دقیقاً در یکی از سه سطل می‌افتد، پس TotalLossAndShortageMt
    // همیشه برابر جمع همان سه است و هیچ عددی دوبار شمرده نمی‌شود.
    //  ۱) کسری تخلیهٔ ریشه: کشتی/واگن → مخزن.
    public decimal VesselUnloadingShortageQuantityMt
        => ContractLines.Sum(c => c.TransportShortageQuantityMt);
    //  ۲) کسری حمل داخلی: مخزن → مقصد، از رسیدهای لِج‌های موتر/واگون (InventoryTransportShortageQuantityMt).
    //  ۳) سایر ضایعات ثبت‌شده: هر LossEvent دیگری که پشتش رسیدِ حمل نیست — چه دستی روی محموله ثبت
    //     شده باشد، چه با دکمهٔ «ثبت کسری» روی یک سفر/ارسال/فروش (DirectLossQuantityMt).
    //     تنها موردی که اینجا کنار گذاشته می‌شود، LossEventـی است که خودِ سیستم برای کسریِ همان
    //     رسیدِ حمل می‌سازد؛ آن مقدار از قبل در سطل ۱ یا ۲ آمده است.
    public decimal TotalLossAndShortageMt
        => VesselUnloadingShortageQuantityMt + InventoryTransportShortageQuantityMt + DirectLossQuantityMt;
    // مجموع همهٔ LossEventهای محموله — همان ردیف‌هایی که جدول تب کسری‌ها فهرست می‌کند.
    // با سه سطل بالا هم‌پوشانی دارد (LossEventِ وصل به لِج در سطل ۱ یا ۲ هم دیده می‌شود)،
    // پس هرگز نباید کنار آن‌ها جمع زده شود؛ فقط جمعِ خودِ جدول است.
    public decimal RecordedLossQuantityMt => LossQuantityMt;
    // کسری مشتق (اختلاف بارگیری/رسید) بدون ضایعهٔ مستقیم = سطل ۱ + سطل ۲.
    public decimal DerivedShortageMt
        => VesselUnloadingShortageQuantityMt + InventoryTransportShortageQuantityMt;
    public decimal RemainingUnsoldQuantityMt
        => decimal.Round(
            Math.Max(OriginalShipmentQuantityMt - SoldQuantityMt - TotalLossAndShortageMt, 0m),
            4,
            MidpointRounding.AwayFromZero);
    // «بار تخلیه‌نشده» = بار محموله منهای آنچه واقعاً از کشتی تخلیه شده و کسری همان تخلیه.
    // پیش‌تر اینجا RemainingInVesselTankQuantityMt بود که حمل داخلی و فروش را هم کسر می‌کرد؛
    // آن رقم با ثبت تخلیه تغییر نمی‌کرد و «باقی روی کشتی» را اشتباه نشان می‌داد.
    // رابطهٔ بسته: OriginalShipment = VesselUnloaded + VesselUnloadingShortage + NotYetDischarged.
    public decimal NotYetDischargedQuantityMt
        => decimal.Round(
            Math.Max(
                OriginalShipmentQuantityMt
                - VesselUnloadedQuantityMt
                - VesselUnloadingShortageQuantityMt,
                0m),
            4,
            MidpointRounding.AwayFromZero);
    // مجموع مقدار باقی‌ماندهٔ همهٔ قراردادها — برای فعال/غیرفعال‌کردن دکمهٔ «انتقال از موجودی».
    public decimal TotalRemainingQuantityMt => ContractLines.Sum(c => c.RemainingQuantityMt);
    public decimal TotalRemainingBeforeLossQuantityMt => ContractLines.Sum(c => c.RemainingBeforeLossQuantityMt);
    public decimal TotalAllocatedQuantityMt => ContractLines.Sum(c => c.AllocatedQuantityMt);
    public decimal TotalUsedQuantityMt => ContractLines.Sum(c => c.UsedQuantityMt);
    public decimal TotalShortageRegisterableQuantityMt => ContractLines.Sum(c => c.ShortageRegisterableQuantityMt);
    public decimal AvailableForSaleOrReceiptQuantityMt => RemainingInSourceTankQuantityMt;
    public decimal ShipShortageLoadedQuantityMt
    {
        get
        {
            if (TotalAllocatedQuantityMt > 0m)
            {
                return TotalAllocatedQuantityMt;
            }

            if (QuantityMt > 0m)
            {
                return QuantityMt;
            }

            return OriginalShipmentQuantityMt;
        }
    }
    public decimal ShipShortageQuantityMt
    {
        get
        {
            var loss = Math.Max(TotalLossAndShortageMt, 0m);
            return ShipShortageLoadedQuantityMt > 0m
                ? Math.Min(loss, ShipShortageLoadedQuantityMt)
                : loss;
        }
    }
    public decimal ShipShortageActualQuantityMt
        => decimal.Round(Math.Max(ShipShortageLoadedQuantityMt - ShipShortageQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    public decimal ShipShortagePercent
        => ShipShortageLoadedQuantityMt > 0m
            ? decimal.Round(ShipShortageQuantityMt / ShipShortageLoadedQuantityMt * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
    public decimal AverageCostPerMtUsd
    {
        get
        {
            var contractQuantity = ContractLines.Sum(c => c.AllocatedQuantityMt);
            var contractValue = ContractLines.Sum(c => c.TotalValueUsd ?? 0m);
            if (contractQuantity > 0m && contractValue > 0m)
            {
                return decimal.Round(contractValue / contractQuantity, 6, MidpointRounding.AwayFromZero);
            }

            if (OriginalShipmentQuantityMt > 0m && TotalPurchaseCostUsd > 0m)
            {
                return decimal.Round(TotalPurchaseCostUsd / OriginalShipmentQuantityMt, 6, MidpointRounding.AwayFromZero);
            }

            return 0m;
        }
    }
    public decimal RealizedPurchaseCostUsd
        => decimal.Round(SoldQuantityMt * AverageCostPerMtUsd, 4, MidpointRounding.AwayFromZero);
    public decimal RealizedOperationalExpensesUsd
    {
        get
        {
            if (TotalOperationalExpensesUsd <= 0m || SoldQuantityMt <= 0m)
            {
                return 0m;
            }

            if (OriginalShipmentQuantityMt <= 0m)
            {
                return TotalOperationalExpensesUsd;
            }

            var soldShare = Math.Min(SoldQuantityMt / OriginalShipmentQuantityMt, 1m);
            return decimal.Round(
                TotalOperationalExpensesUsd * soldShare,
                4,
                MidpointRounding.AwayFromZero);
        }
    }
    // بهای خرید مقدار ضایعاتِ «ضرر شرکت» (ارزش خودِ جنس از دست رفته).
    public decimal CompanyLossPurchaseCostUsd
        => decimal.Round(CompanyLossQuantityMt * AverageCostPerMtUsd, 4, MidpointRounding.AwayFromZero);
    // سهم مصارف عملیاتی نسبت به همان مقدار ضایعاتِ «ضرر شرکت».
    public decimal CompanyLossExpenseShareUsd
    {
        get
        {
            if (TotalOperationalExpensesUsd <= 0m || CompanyLossQuantityMt <= 0m || OriginalShipmentQuantityMt <= 0m)
            {
                return 0m;
            }

            var lossShare = Math.Min(CompanyLossQuantityMt / OriginalShipmentQuantityMt, 1m);
            return decimal.Round(TotalOperationalExpensesUsd * lossShare, 4, MidpointRounding.AwayFromZero);
        }
    }
    // مجموع کسرِ «ضرر شرکت» از سود: هم بهای جنس ضایع‌شده، هم سهم مصارف آن.
    public decimal CompanyLossDeductionUsd
        => decimal.Round(CompanyLossPurchaseCostUsd + CompanyLossExpenseShareUsd, 4, MidpointRounding.AwayFromZero);
    // ===== نتیجهٔ کل محموله =====
    // منبع رسمی صفحهٔ جزئیات و فهرست محموله‌ها:
    // کل فروش − قیمت کامل خرید همهٔ بار − تمام مصارف مسیر و عملیات.
    // مقدار فروش‌نشده برای منفی‌شدن نتیجه نیاز به ثبت ضایعات ندارد؛ قیمت خرید آن از ابتدا
    // داخل TotalPurchaseCostUsd است. LossEvent فقط برای ردیابی کسری/مسئولیت است و نباید
    // هزینهٔ همان بار را دوباره از نتیجه کم کند.
    public decimal ShipmentNetResultUsd
        => decimal.Round(
            TotalSalesUsd - TotalPurchaseCostUsd - TotalOperationalExpensesUsd,
            4,
            MidpointRounding.AwayFromZero);

    // محاسبهٔ تحقق‌یافتهٔ قدیمی برای سازگاری داخلی نگه داشته شده است، اما UI پروندهٔ
    // محموله دیگر آن را به‌عنوان نتیجهٔ اصلی نمایش نمی‌دهد.
    public decimal RealizedGrossMarginUsd
        => decimal.Round(
            TotalSalesUsd - RealizedPurchaseCostUsd - RealizedOperationalExpensesUsd - CompanyLossDeductionUsd,
            4,
            MidpointRounding.AwayFromZero);

    // ===== تطبیق سود عملیاتی محموله با سود محقق‌شدهٔ حسابداری =====
    // عدد سمت راست از ProfitAndLossService می‌آید (درآمد فروش معتبر منهای COGS فعال).
    // عدد سمت چپ برآورد عملیاتی سهم فروخته‌شده از بهای بار و مصارف مسیر است.
    // اختلاف پنهان نمی‌شود؛ با مقدار و دلیل نمایش داده می‌شود.
    public decimal OperationalVsRealisedVarianceUsd
        => decimal.Round(RealizedGrossMarginUsd - RealisedPnl.GrossProfitUsd, 4, MidpointRounding.AwayFromZero);

    public bool HasOperationalVsRealisedVariance
        => Math.Abs(OperationalVsRealisedVarianceUsd) > 0.01m;

    public string OperationalVsRealisedVarianceReasonFa
        => RealisedPnl.HasUncostedSales
            ? $"{RealisedPnl.UncostedSaleCount} فروش بدون بهای تمام‌شدهٔ ثبت‌شده است؛ COGS حسابداری کمتر از بهای عملیاتی می‌ماند."
            : PurchasePricingIncomplete
                ? "نرخ نهایی خرید بعضی قراردادهای این محموله ثبت نشده؛ بهای عملیاتی ناقص است."
                : "برآورد عملیاتی سهم مصارف مسیر و ضایعاتِ شرکت را هم کسر می‌کند، اما COGS حسابداری فقط بهای انبارِ مصرف‌شده است.";
    public bool NetResultIsProfit => ShipmentNetResultUsd >= 0m;
    public decimal NetResultAbsUsd => Math.Abs(ShipmentNetResultUsd);
    public bool IsFullySold => RemainingUnsoldQuantityMt <= 0.0001m;
    // ارزش موجودی باقی‌مانده به بهای خرید فقط برای پیگیری موجودی؛ قیمت خرید این مقدار
    // در ShipmentNetResultUsd از قبل به‌صورت کامل کسر شده است.
    public decimal RemainingInventoryValueUsd
        => AverageCostPerMtUsd > 0m && RemainingUnsoldQuantityMt > 0m
            ? decimal.Round(RemainingUnsoldQuantityMt * AverageCostPerMtUsd, 2, MidpointRounding.AwayFromZero)
            : 0m;
    // نرخ نهایی خرید بعضی قراردادها ثبت نشده → بهای خرید ناقص است و اعداد سود قابل‌اعتماد نیستند.
    public bool PurchasePricingIncomplete
        => ContractLines.Any(c => c.AllocatedQuantityMt > 0.0001m && !c.HasFinalPrice);
    public decimal EstimatedShortageValueUsd => EstimateLossValueUsd(ShipShortageQuantityMt);
    public decimal EstimatedDirectLossValueUsd => ContractLines.Sum(c => c.DirectLossValueUsd ?? 0m);
    public decimal EstimateLossValueUsd(decimal quantityMt)
        => AverageCostPerMtUsd > 0m && quantityMt > 0m
            ? decimal.Round(quantityMt * AverageCostPerMtUsd, 2, MidpointRounding.AwayFromZero)
            : 0m;
    // وضعیت سادهٔ محموله برای نمایش (بدون منطق مالی).
    public string SimpleStatusFa
    {
        get
        {
            if (ContractLines.Count == 0)
            {
                return "نیاز به قرارداد";
            }
            if (TotalUsedQuantityMt <= 0m)
            {
                return "آماده عملیات";
            }
            return TotalRemainingQuantityMt > 0.0001m ? "در جریان" : "تکمیل‌شده";
        }
    }
}

public static class ShipmentShortageResponsibilityTypes
{
    public const string CompanyLoss = "CompanyLoss";
    public const string SupplierDeduction = "SupplierDeduction";
    public const string ServiceProviderClaim = "ServiceProviderClaim";
    public const string PartnerShareDeduction = "PartnerShareDeduction";
    public const string Split = "Split";
}

public sealed class ShipmentDirectLossCreateViewModel
{
    public int ShipmentId { get; set; }
    public DateTime EventDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public decimal LossQuantityMt { get; set; }
    public decimal? LossAmountUsd { get; set; }
    public string ResponsibilityType { get; set; } = ShipmentShortageResponsibilityTypes.CompanyLoss;
    public string? ResponsiblePartyName { get; set; }
    public int? ContractId { get; set; }
    public string? ClaimStatus { get; set; }
    public string? Notes { get; set; }
    public string? ReturnUrl { get; set; }
    public List<ShipmentDirectLossSplitLineInput> SplitLines { get; set; } = [];
}

public sealed class ShipmentDirectLossSplitLineInput
{
    public string? ResponsibilityType { get; set; }
    public string? ResponsiblePartyName { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal? AmountUsd { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
}
