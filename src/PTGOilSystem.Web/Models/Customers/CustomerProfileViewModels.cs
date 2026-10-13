using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Customers;

// Slim row model for Customers/Index — only the fields the card list renders.
// Mirrors the Suppliers/Index projection pattern; no full Customer entity loaded.
public sealed class CustomerIndexItemViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? NamePersian { get; init; }
    public string? Country { get; init; }
    public decimal BalanceUsd { get; init; }
    public string? Phone { get; init; }
    public bool IsActive { get; init; }
}

public sealed class CustomerProfileViewModel
{
    public int CustomerId { get; init; }
    public int Id => CustomerId;
    public string? Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? NamePersian { get; init; }
    public string? Country { get; init; }
    public string? ContactPerson { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    public int SaleContractsCount { get; init; }
    public int ActiveSaleContractsCount { get; init; }
    public decimal TotalContractQuantityMt { get; init; }
    public decimal EstimatedContractValueUsd { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal SoldValueUsd { get; init; }
    public decimal RemainingContractQuantityMt { get; init; }
    public decimal EstimatedRemainingContractValueUsd { get; init; }
    public decimal LedgerDebitUsd { get; init; }
    public decimal LedgerCreditUsd { get; init; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته). هم‌علامتِ
    // PartyStatementSummary.ClosingBalance که صفحهٔ جزئیات به آن fallback می‌کند.
    public decimal LedgerBalanceUsd => LedgerDebitUsd - LedgerCreditUsd;
    public decimal TotalReceivedUsd { get; init; }
    public decimal TotalPaidToCustomerUsd { get; init; }
    public DateTime? LastSaleDate { get; init; }
    public DateTime? LastPaymentDate { get; init; }

    public int? SelectedContractId { get; init; }
    public string ActiveTab { get; init; } = "overview";
    public CustomerContractSummaryViewModel? SelectedContract { get; init; }
    public IReadOnlyList<CustomerStatementContractFilterOptionViewModel> StatementContractOptions { get; init; } = [];
    public IReadOnlyList<CustomerContractSummaryViewModel> Contracts { get; init; } = [];
    public IReadOnlyList<CustomerSaleSummaryViewModel> Sales { get; init; } = [];
    public IReadOnlyList<CustomerPaymentSummaryViewModel> Payments { get; init; } = [];
    public IReadOnlyList<CustomerStatementRowViewModel> StatementRows { get; init; } = [];
}

public sealed class CustomerStatementContractFilterOptionViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
}

public sealed class CustomerContractSummaryViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public DateTime ContractDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? UnitPriceOriginal { get; init; }
    public decimal? FinalPriceUsd { get; init; }
    public decimal? EstimatedTotalOriginal { get; init; }
    public decimal? EstimatedTotalUsd { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal SoldValueUsd { get; init; }
    public decimal RemainingQuantityMt => Math.Max(QuantityMt - SoldQuantityMt, 0m);
    public decimal? EstimatedRemainingUsd => EstimatedTotalUsd.HasValue
        ? Math.Max(EstimatedTotalUsd.Value - SoldValueUsd, 0m)
        : null;
    public decimal LedgerDebitUsd { get; init; }
    public decimal LedgerCreditUsd { get; init; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته). هم‌علامتِ
    // PartyStatementSummary.ClosingBalance که صفحهٔ جزئیات به آن fallback می‌کند.
    public decimal LedgerBalanceUsd => LedgerDebitUsd - LedgerCreditUsd;
    public decimal ReceivedUsd { get; init; }
    public decimal PaidToCustomerUsd { get; init; }
    public decimal ReceivableUsd => SoldValueUsd - ReceivedUsd + PaidToCustomerUsd;
    public ContractStatus Status { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public string StatusBadgeClass { get; init; } = "status-badge-neutral";
}

public sealed class CustomerSaleSummaryViewModel
{
    public int SaleId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public string Product { get; init; } = string.Empty;
    public string? TransportReference { get; init; }
    public DateTime SaleDate { get; init; }
    public SaleStage SaleStage { get; init; }
    public string SaleStageName { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal UnitPriceInCurrency { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal TotalInCurrency { get; init; }
    public decimal TotalUsd { get; init; }
    public decimal ReceivedUsd { get; init; }
    public decimal PaidToCustomerUsd { get; init; }
    public decimal ReceivableUsd => TotalUsd - ReceivedUsd + PaidToCustomerUsd;
    public bool IsCancelled { get; init; }
    public bool IsSalePricePending { get; init; }
}

public sealed class CustomerPaymentSummaryViewModel
{
    public int PaymentId { get; init; }
    public DateTime PaymentDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string DirectionName { get; init; } = string.Empty;
    public PaymentKind PaymentKind { get; init; }
    public string PaymentKindName { get; init; } = string.Empty;
    public string CashAccount { get; init; } = string.Empty;
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public int? SaleId { get; init; }
    public string? InvoiceNumber { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public int? LedgerEntryId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public int? CreatedByUserId { get; init; }
    public string? CreatedByDisplay { get; init; }
}

public sealed class CustomerStatementRowViewModel
{
    public int LedgerEntryId { get; init; }
    public DateTime Date { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? Reference { get; init; }
    public string? SourceDetailsController { get; init; }
    public string? SourceDetailsAction { get; init; }
    public int? SourceDetailsRouteId { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Currency { get; init; } = "USD";
    public decimal? Debit { get; init; }
    public decimal? Credit { get; init; }
    public decimal? DebitUsd { get; init; }
    public decimal? CreditUsd { get; init; }
    public decimal RunningBalanceUsd { get; init; }

    // نرخ ارز به‌کاررفته در همین سند (از LedgerEntry.AppliedFxRateToUsd) — فقط نمایش.
    public decimal? FxRateUsed { get; init; }

    // نرخ معکوس کاربرپسند (ارز اصلی در برابر ۱ دالر) فقط برای ارز غیر USD.
    public decimal? FxRateDisplayPerUsd =>
        FxRateUsed.HasValue && FxRateUsed.Value > 0m && !string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? decimal.Round(1m / FxRateUsed.Value, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null;
}
