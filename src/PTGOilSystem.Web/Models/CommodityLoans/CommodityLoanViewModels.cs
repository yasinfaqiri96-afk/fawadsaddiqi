using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.CommodityLoans;

namespace PTGOilSystem.Web.Models.CommodityLoans;

public sealed class CommodityLoanForm
{
    public CommodityLoanDirection Direction { get; set; } = CommodityLoanDirection.Lend;
    /// <summary>کلید طرف: «نوع:شناسه» (همان قالب کلید طرف‌ها در امانی).</summary>
    public string? CounterpartyKey { get; set; }
    public int? ProductId { get; set; }
    [MaxLength(300)] public string? QualitySpec { get; set; }
    public decimal? QuantityMt { get; set; }
    [DataType(DataType.Date)] public DateTime LoanDate { get; set; }
    [DataType(DataType.Date)] public DateTime DueDate { get; set; }
    [MaxLength(500)] public string? ReturnObligation { get; set; }
    public CommodityLoanBorrowBasis? BorrowBasis { get; set; }
    public string? LocationKey { get; set; }
    public CommodityLoanValuation? ValuationPolicy { get; set; }
    public decimal? AgreedUnitValueUsd { get; set; }
    [MaxLength(300)] public string? ValuationReference { get; set; }
    public int? LoanAccountId { get; set; }
    public int? DifferenceAccountId { get; set; }
    public decimal? QuantityTolerancePercent { get; set; }
    public bool CashSettlementAllowed { get; set; }
    public decimal? CashSettlementUnitPriceUsd { get; set; }
    [MaxLength(300)] public string? CashSettlementReference { get; set; }
    [MaxLength(300)] public string? AgreementReference { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public sealed record CommodityLoanOption(string Key, string Label);

public sealed class CommodityLoanFormViewModel
{
    public required CommodityLoanForm Form { get; init; }
    public bool GlEnabled { get; init; }
    public IReadOnlyList<CommodityLoanOption> Counterparties { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> Products { get; init; } = [];
    public IReadOnlyList<CommodityLoanOption> Locations { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> AssetAccounts { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> LiabilityAccounts { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> DifferenceAccounts { get; init; } = [];
}

public sealed class CommodityLoanSettlementForm
{
    public int LoanId { get; set; }
    public CommodityLoanSettlementKind Kind { get; set; } = CommodityLoanSettlementKind.InKindReturn;
    [DataType(DataType.Date)] public DateTime Date { get; set; }
    public decimal? QuantityMt { get; set; }
    public string? LocationKey { get; set; }
    public int? CashAccountId { get; set; }
    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public static string Title(CommodityLoanSettlementKind kind) => kind switch
    {
        CommodityLoanSettlementKind.InKindReturn => "برگشت جنس",
        CommodityLoanSettlementKind.CashSettlement => "تسویه نقدی",
        CommodityLoanSettlementKind.QuantityDifference => "بستن تفاوت مقدار",
        _ => "تسویه"
    };
}

public sealed class CommodityLoanSettlementViewModel
{
    public required CommodityLoanSettlementForm Form { get; init; }
    public required CommodityLoanDetail Detail { get; init; }
    public IReadOnlyList<CommodityLoanOption> Locations { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> CashAccounts { get; init; } = [];
}

public sealed class CommodityLoanIndexViewModel
{
    public IReadOnlyList<CommodityLoanRow> Loans { get; init; } = [];
    public CommodityLoanDirection? Direction { get; init; }
    public bool IncludeCancelled { get; init; }
    public DateTime Today { get; init; }
}

public static class CommodityLoanLabels
{
    public static string Direction(CommodityLoanDirection direction)
        => direction == CommodityLoanDirection.Lend ? "قرض‌داده (طلب جنسی)" : "قرض‌گرفته (بدهی جنسی)";

    public static string Kind(CommodityLoanSettlementKind kind) => CommodityLoanSettlementForm.Title(kind);
}
