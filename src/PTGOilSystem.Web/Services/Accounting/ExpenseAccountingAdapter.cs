using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record ExpenseAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IExpenseAccountingAdapter
{
    Task<ExpenseAccountingResult> TryPostExpenseAsync(
        ExpenseTransaction expense,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses the journal an expense raised, for when the legacy expense is cancelled.
    /// Idempotent: a second call returns the existing reversal instead of writing another.
    /// </summary>
    Task<ExpenseAccountingResult> TryPostExpenseReversalAsync(
        ExpenseTransaction expense,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The liability an expense accrues to, so a later payment can debit exactly that account.
    /// Returns null when the expense type has no configured payable kind.
    /// </summary>
    Task<int?> ResolvePayableAccountIdAsync(
        ExpenseTransaction expense,
        int companyId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 5 dual-write pilot for expenses, freight and commission.
///
///   Debit  General Expense (5200)
///   Credit the liability configured on the expense type — Accounts Payable, Freight Payable,
///          Commission Payable, or Accrued Expense
///
/// The credit account comes from <see cref="ExpenseType.PayableAccountKind"/>, an explicit field
/// the finance manager sets. It is deliberately not inferred from <see cref="ExpenseType.Category"/>,
/// which is free text a user can edit at will — an accounting account must never move because
/// someone renamed a category. An expense type without a configured kind is skipped, never guessed.
///
/// Party: the service provider when present, otherwise the driver. Expenses with no external
/// counterparty (internal asset rent, commission) post with no party line, which is why their
/// natural configured kind is Accrued Expense or Commission Payable.
///
/// Legacy comparison: the old ledger writes a single one-sided row whose side is
/// `ServiceProviderId.HasValue ? Credit : Debit` and never records a counter-account, so the
/// legacy row can only be reconciled on amount, which the logged comparison does.
/// </summary>
public sealed class ExpenseAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IOptions<AccountingOptions> options,
    ILogger<ExpenseAccountingAdapter> logger)
    : IExpenseAccountingAdapter
{
    public const string SourceModule = "Expense";
    public const string SourceEntityType = nameof(ExpenseTransaction);

    private readonly AccountingOptions _options = options.Value;

    public async Task<ExpenseAccountingResult> TryPostExpenseAsync(
        ExpenseTransaction expense,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        var (companyId, skipReason) = await ResolveCompanyAndSkipReasonAsync(expense, cancellationToken);
        if (skipReason is not null)
        {
            LogOutcome(expense, companyId, 0m, PaymentPostingStatus.Skipped, skipReason);
            return new ExpenseAccountingResult(PaymentPostingStatus.Skipped, null, skipReason);
        }

        var sourceEventId = BuildCreatedSourceEventId(expense.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(expense, companyId, existing.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new ExpenseAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);
        var standaloneCash = await IsStandaloneCashExpenseAsync(expense, cancellationToken);
        var payableAccountId = standaloneCash
            ? settings.CashBankControlAccountId
            : await ResolvePayableAccountIdAsync(expense, companyId, cancellationToken);
        var (partyType, partyId) = standaloneCash || expense.SettlementMode == ExpenseSettlementMode.NonCash
            ? ((AccountingPartyType?)null, (int?)null)
            : ResolveParty(expense);
        var rate = expense.AppliedFxRateToUsd!.Value;

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForExpense(companyId, expense.Id),
            expense.ExpenseDate.Date,
            expense.ExpenseDate.Date,
            expense.ExpenseDate.Date,
            SourceModule,
            [
                new AccountingPostLine(
                    settings.GeneralExpenseAccountId,
                    Debit: expense.AmountUsd,
                    Credit: 0m,
                    expense.Currency,
                    expense.Amount,
                    rate,
                    ContractId: expense.ContractId,
                    ShipmentId: expense.ShipmentId,
                    Description: "Expense incurred"),
                new AccountingPostLine(
                    payableAccountId!.Value,
                    Debit: 0m,
                    Credit: expense.AmountUsd,
                    expense.Currency,
                    expense.Amount,
                    rate,
                    partyType,
                    partyId,
                    ContractId: expense.ContractId,
                    ShipmentId: expense.ShipmentId,
                    CashAccountId: standaloneCash ? expense.CashAccountId : null,
                    Description: standaloneCash ? "Expense paid from cash account" : "Expense liability accrued")
            ],
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: expense.Id,
            Description: $"Expense #{expense.Id} on {expense.ExpenseDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(expense, companyId, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null);
            return new ExpenseAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(expense, exception);
            throw;
        }
    }

    public async Task<ExpenseAccountingResult> TryPostExpenseReversalAsync(
        ExpenseTransaction expense,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        if (!_options.Enabled)
            return Skipped(expense, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Expense)
            return Skipped(expense, 0, "PILOT_DISABLED");

        // Deliberately not the full posting guard: the expense is cancelled by now, and the
        // company must still resolve the same way it did when the original was posted.
        var companyId = await ExpenseCompanyResolver.ResolveAsync(db, expense, cancellationToken);
        if (companyId is null)
            return Skipped(expense, 0, "EXPENSE_COMPANY_UNKNOWN");

        var reversedEventId = BuildReversedSourceEventId(expense.Id);
        var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (existingReversal is not null)
        {
            LogOutcome(expense, companyId.Value, existingReversal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new ExpenseAccountingResult(
                PaymentPostingStatus.Duplicate, existingReversal, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value,
            BuildCreatedSourceEventId(expense.Id),
            cancellationToken);
        if (original is null)
        {
            // The expense was created legacy-only (pilot off or skipped at the time), so the
            // cancellation stays legacy-only too and both books remain internally consistent.
            LogOutcome(expense, companyId.Value, 0m,
                PaymentPostingStatus.Skipped, "ORIGINAL_JOURNAL_NOT_POSTED");
            return new ExpenseAccountingResult(
                PaymentPostingStatus.Skipped, null, "ORIGINAL_JOURNAL_NOT_POSTED");
        }

        // Legacy cancels an expense with a reversing ledger row dated today, so the journal
        // reversal uses the same date rather than reopening the original period.
        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForExpenseReversal(companyId.Value, expense.Id),
            AfghanistanBusinessClock.SystemToday,
            SourceModule,
            reversedEventId,
            $"Reversal of expense #{expense.Id}");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);
            LogOutcome(expense, companyId.Value, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null);
            return new ExpenseAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(expense, exception);
            throw;
        }
    }

    private ExpenseAccountingResult Skipped(ExpenseTransaction expense, int companyId, string reason)
    {
        LogOutcome(expense, companyId, 0m, PaymentPostingStatus.Skipped, reason);
        return new ExpenseAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    public async Task<int?> ResolvePayableAccountIdAsync(
        ExpenseTransaction expense,
        int companyId,
        CancellationToken cancellationToken = default)
    {
        var payableKind = expense.ExpenseType?.PayableAccountKind
            ?? await db.ExpenseTypes
                .AsNoTracking()
                .Where(x => x.Id == expense.ExpenseTypeId)
                .Select(x => x.PayableAccountKind)
                .SingleOrDefaultAsync(cancellationToken);
        if (payableKind is null)
            return null;

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId, cancellationToken);
        if (settings is null)
            return null;

        return PayableAccountFor(payableKind.Value, settings);
    }

    /// <summary>
    /// The one mapping from an expense type's explicit payable kind to the configured liability
    /// account; shared with custody provider charges so both credit the same account.
    /// </summary>
    public static int? PayableAccountFor(ExpensePayableKind payableKind, AccountingSettings settings)
        => payableKind switch
        {
            ExpensePayableKind.AccountsPayable => settings.AccountsPayableAccountId,
            ExpensePayableKind.FreightPayable => settings.FreightPayableAccountId,
            ExpensePayableKind.CommissionPayable => settings.CommissionPayableAccountId,
            ExpensePayableKind.AccruedExpense => settings.AccruedExpenseAccountId,
            _ => null
        };

    public static string BuildCreatedSourceEventId(int expenseId)
        => $"Expense:{expenseId}:Created";

    public static string BuildReversedSourceEventId(int expenseId)
        => $"Expense:{expenseId}:Reversed";

    /// <summary>
    /// The counterparty of an expense, when the legacy record proves one. Internal costs
    /// (operational assets) and commission carry no external party.
    /// </summary>
    public static (AccountingPartyType? PartyType, int? PartyId) ResolveParty(ExpenseTransaction expense)
    {
        if (expense.ServiceProviderId.HasValue)
            return (AccountingPartyType.ServiceProvider, expense.ServiceProviderId);
        if (expense.DriverId.HasValue)
            return (AccountingPartyType.Driver, expense.DriverId);
        return (null, null);
    }

    private async Task<(int CompanyId, string? SkipReason)> ResolveCompanyAndSkipReasonAsync(
        ExpenseTransaction expense,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return (0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Expense)
            return (0, "PILOT_DISABLED");
        if (expense.IsCancelled)
            return (0, "EXPENSE_CANCELLED");
        if (expense.Amount <= 0m || expense.AmountUsd <= 0m)
            return (0, "INVALID_EXPENSE_AMOUNT");

        var rate = expense.AppliedFxRateToUsd;
        if (!rate.HasValue || rate.Value <= 0m)
            return (0, "INVALID_EXPENSE_FX");
        if (SystemCurrency.IsBaseCurrency(expense.Currency) && rate.Value != 1m)
            return (0, "INVALID_EXPENSE_FX");

        var expectedUsd = decimal.Round(expense.Amount * rate.Value, 4, MidpointRounding.AwayFromZero);
        if (expense.AmountUsd != expectedUsd)
            return (0, "INVALID_EXPENSE_CONVERSION");

        var companyId = await ExpenseCompanyResolver.ResolveAsync(db, expense, cancellationToken);
        if (companyId is null)
            return (0, "EXPENSE_COMPANY_UNKNOWN");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, "UNSUPPORTED_FUNCTIONAL_CURRENCY");

        var standaloneCash = await IsStandaloneCashExpenseAsync(expense, cancellationToken);
        if (standaloneCash)
        {
            var cash = await db.CashAccounts.AsNoTracking()
                .Where(x => x.Id == expense.CashAccountId)
                .Select(x => new { x.CompanyId, x.IsActive, x.Currency, x.AccountType })
                .SingleOrDefaultAsync(cancellationToken);
            if (cash is null || !cash.IsActive)
                return (companyId.Value, "CASH_ACCOUNT_MISSING");
            if (cash.CompanyId.HasValue && cash.CompanyId.Value != companyId.Value)
                return (companyId.Value, "CASH_ACCOUNT_COMPANY_MISMATCH");
            if (cash.AccountType != CashAccountType.Mixed
                && !string.Equals(SystemCurrency.Normalize(cash.Currency), SystemCurrency.Normalize(expense.Currency), StringComparison.Ordinal))
                return (companyId.Value, "CASH_ACCOUNT_CURRENCY_MISMATCH");
        }

        var payableAccountId = standaloneCash
            ? settings.CashBankControlAccountId
            : await ResolvePayableAccountIdAsync(expense, companyId.Value, cancellationToken);
        if (payableAccountId is null)
            return (companyId.Value, "EXPENSE_PAYABLE_KIND_NOT_SET");

        var accountIds = new[] { settings.GeneralExpenseAccountId, payableAccountId.Value }.Distinct().ToArray();
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId.Value && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Length)
            return (companyId.Value, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");

        return (companyId.Value, null);
    }

    // CashPositionReader uses the same explicit payment link: an expense with its own payment
    // must accrue its configured counter-account; the payment alone credits cash. Never infer
    // the link from an equal amount/date, and never create a second payment here.
    private Task<bool> IsStandaloneCashExpenseAsync(ExpenseTransaction expense, CancellationToken ct)
        => expense.SettlementMode != ExpenseSettlementMode.PaidImmediately
            ? Task.FromResult(false)
            : IsUnlinkedCashExpenseAsync(expense, ct);

    private async Task<bool> IsUnlinkedCashExpenseAsync(ExpenseTransaction expense, CancellationToken ct)
        => !await db.PaymentTransactions.AsNoTracking().AnyAsync(p => p.ExpenseTransactionId == expense.Id, ct);

    private async Task<JournalEntry?> FindJournalAsync(
        int companyId,
        string sourceEventId,
        CancellationToken cancellationToken)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyId == companyId
                    && x.SourceModule == SourceModule
                    && x.SourceEventId == sourceEventId,
                cancellationToken);

    private void LogOutcome(
        ExpenseTransaction expense,
        int companyId,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // Legacy writes one one-sided row of AmountUsd with no counter-account, so amount is
        // the only field the two books can be reconciled on.
        logger.LogInformation(
            "Expense accounting pilot comparison: ExpenseId {ExpenseId}, ExpenseTypeId {ExpenseTypeId}, CompanyId {CompanyId}, ContractId {ContractId}, ServiceProviderId {ServiceProviderId}, DriverId {DriverId}, LegacyAmountUsd {LegacyAmountUsd}, JournalDebitTotal {JournalDebitTotal}, Difference {Difference}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            expense.Id,
            expense.ExpenseTypeId,
            companyId,
            expense.ContractId,
            expense.ServiceProviderId,
            expense.DriverId,
            expense.AmountUsd,
            journalDebitTotal,
            journalDebitTotal - expense.AmountUsd,
            status,
            reason);
    }

    private void LogFailure(ExpenseTransaction expense, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Expense accounting pilot posting failed for ExpenseId {ExpenseId} with FailureReason {FailureReason}",
            expense.Id,
            failureReason);
    }
}

/// <summary>
/// Provable company for an expense. ExpenseTransaction has no CompanyId of its own, so the
/// contract is the primary source and a single-company shipment the fallback — the same two
/// paths the Stage 3 backfill trusts for expense-linked payments. Anything else stays unresolved.
/// </summary>
public static class ExpenseCompanyResolver
{
    public static async Task<int?> ResolveAsync(
        ApplicationDbContext db,
        ExpenseTransaction expense,
        CancellationToken cancellationToken = default)
    {
        if (expense.ContractId.HasValue)
        {
            var fromContract = await db.Contracts
                .AsNoTracking()
                .Where(x => x.Id == expense.ContractId.Value)
                .Select(x => (int?)x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (fromContract.HasValue)
                return fromContract;
        }

        if (!expense.ShipmentId.HasValue)
            return null;

        var junctionCompanies = await db.ShipmentContracts
            .AsNoTracking()
            .Where(x => x.ShipmentId == expense.ShipmentId.Value)
            .Join(db.Contracts.AsNoTracking(), sc => sc.ContractId, c => c.Id, (_, c) => c.CompanyId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var primaryCompanies = await db.Shipments
            .AsNoTracking()
            .Where(x => x.Id == expense.ShipmentId.Value && x.ContractId != null)
            .Join(db.Contracts.AsNoTracking(), s => s.ContractId, c => c.Id, (_, c) => c.CompanyId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var companies = junctionCompanies.Concat(primaryCompanies).Distinct().ToArray();
        return companies.Length == 1 ? companies[0] : null;
    }
}
