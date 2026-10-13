namespace PTGOilSystem.Web.Models.Reports;

// ─────────────────────────────────────────────────────────────────────────
// گزارش مستقل مصارف
// ─────────────────────────────────────────────────────────────────────────

/// <summary>بخشی که مصرف از آن ثبت شده است. هر مصرف دقیقاً یک منبع دارد.</summary>
public enum ExpenseReportSource
{
    General = 1,
    Contract = 2,
    Loading = 3,
    LoadingInline = 4,
    Customs = 5,
    TransportLeg = 6,
    Dispatch = 7,
    Shipment = 8,
    OperationalAsset = 9
}

public enum ExpenseReportPaymentStatus
{
    Paid = 1,
    PartlyPaid = 2,
    Unpaid = 3,
    NonCash = 4,
    Unknown = 5
}

public enum ExpenseReportGroupBy
{
    Type = 1,
    Source = 2,
    Contract = 3,
    Month = 4
}

public sealed class ExpenseReportFilterViewModel
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int? ExpenseTypeId { get; set; }
    public int? ContractId { get; set; }
    public int? ServiceProviderId { get; set; }
    public ExpenseReportSource? Source { get; set; }
    public ExpenseReportPaymentStatus? PaymentStatus { get; set; }
    public ExpenseReportGroupBy GroupBy { get; set; } = ExpenseReportGroupBy.Type;
    public string? Search { get; set; }
}

public sealed class ExpenseReportRowViewModel
{
    /// <summary>هویت یکتای مصرف در این گزارش: «E-شناسهٔ سند مصرف» یا «L-شناسهٔ بارگیری».</summary>
    public string Key { get; init; } = "";
    public DateTime Date { get; init; }
    public string TypeName { get; init; } = "";
    public ExpenseReportSource Source { get; init; }
    public int? ContractId { get; init; }
    public string? ContractLabel { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public string? PartyName { get; init; }
    public decimal AmountUsd { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? FxRateToUsd { get; init; }
    public ExpenseReportPaymentStatus PaymentStatus { get; init; }
    public decimal PaidUsd { get; init; }
    public decimal UnpaidUsd { get; init; }
    public string DetailsController { get; init; } = "Expenses";
    public int DetailsId { get; init; }
    public bool HasOriginalCurrency => !string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase);
}

public sealed record ExpenseReportGroupRowViewModel(string Label, int Count, decimal AmountUsd, decimal PaidUsd, decimal UnpaidUsd);

public sealed class ExpenseReportViewModel
{
    public ExpenseReportFilterViewModel Filter { get; init; } = new();
    /// <summary>سطرهای صفحهٔ جاری؛ جمع‌ها همیشه از <see cref="TotalAmountUsd"/> و هم‌خانواده‌اش می‌آیند.</summary>
    public IReadOnlyList<ExpenseReportRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<ExpenseReportGroupRowViewModel> Groups { get; init; } = [];
    public int TotalCount { get; init; }
    public decimal TotalAmountUsd { get; init; }
    public decimal TotalPaidUsd { get; init; }
    public decimal TotalUnpaidUsd { get; init; }
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
}

// ─────────────────────────────────────────────────────────────────────────
// گزارش مستقل کسری‌ها و ضایعات
// ─────────────────────────────────────────────────────────────────────────

public enum LossReportKind
{
    All = 0,
    ChargeableOnly = 1,
    NonChargeableOnly = 2
}

public sealed class LossReportFilterViewModel
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int? ProductId { get; set; }
    public int? ContractId { get; set; }
    public Entities.LossEventStage? Stage { get; set; }
    public LossReportKind Kind { get; set; } = LossReportKind.All;
    /// <summary>جست‌وجوی مسئول / طرف‌حساب و مرجع.</summary>
    public string? Search { get; set; }
}

public sealed class LossReportRowViewModel
{
    public int LossEventId { get; init; }
    public DateTime Date { get; init; }
    public Entities.LossEventStage Stage { get; init; }
    public int? ContractId { get; init; }
    public string? ContractLabel { get; init; }
    public string? LoadReference { get; init; }
    public string ProductName { get; init; } = "";
    public decimal LossMt { get; init; }
    public decimal ChargeableMt { get; init; }
    /// <summary>قیمت هر تن از منبع معتبر؛ null یعنی «قیمت نامشخص» — هیچ عدد ساختگی ساخته نمی‌شود.</summary>
    public decimal? UnitPriceUsd { get; init; }
    public decimal? ValueUsd => UnitPriceUsd.HasValue ? decimal.Round(LossMt * UnitPriceUsd.Value, 2, MidpointRounding.AwayFromZero) : null;
    public string? ResponsibleName { get; init; }
    public string? ResponsibleType { get; init; }
    public string? FinancialTreatment { get; init; }
    public string? Reference { get; init; }
}

public sealed class LossReportViewModel
{
    public LossReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<LossReportRowViewModel> Rows { get; init; } = [];
    public int TotalCount { get; init; }
    public decimal TotalLossMt { get; init; }
    public decimal TotalChargeableMt { get; init; }
    public decimal TotalValuedUsd { get; init; }
    public int UnpricedCount { get; init; }
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
}
