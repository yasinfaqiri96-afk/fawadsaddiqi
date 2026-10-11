using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public enum CustodyPostingStatus
{
    Skipped = 0,
    Posted = 1,
    Duplicate = 2
}

public sealed record CustodyAccountingResult(CustodyPostingStatus Status, JournalEntry? Journal, string? Reason);

public interface ICustodyAccountingAdapter
{
    Task<CustodyAccountingResult> TryPostSaleAsync(CustodySale sale, CancellationToken cancellationToken = default);

    Task<CustodyAccountingResult> TryPostFinanceAsync(
        CustodyFinanceEntry entry,
        CustodySale? sale,
        CancellationToken cancellationToken = default);

    /// <summary>برگشتِ سندِ قبلی؛ اگر سند اصلی ثبت نشده بود (Skip) کاری نمی‌کند.</summary>
    Task<CustodyAccountingResult> TryReverseAsync(
        string createdEventId,
        string journalNumber,
        DateTime reversalDate,
        string description,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// ژورنالِ امانی روی همان <see cref="IAccountingPostingService"/>. فروشِ امانی درآمدِ فروش، بهای
/// تمام‌شده یا موجودی ملکی ندارد: طلبِ مشتری در برابرِ «طلب امانی به نفع مالک» (کاهندهٔ دریافتنی،
/// پس بیلانس را بزرگ نمی‌کند) و پس از وصول در «وجوه امانی پرداختنی» به مالک می‌نشیند؛ فقط کمیشن و حق خدمات درآمدِ شرکت است. صندوق/بانک فقط وقتی حرکت
/// می‌کند که پول واقعاً از صندوق شرکت گذشته باشد. پرداخت‌های نقدیِ امانی از نوع Manual ساخته
/// می‌شوند که <see cref="PaymentAccountingAdapter"/> آن‌ها را عمداً Skip می‌کند؛ پس سطر صندوق
/// فقط همین‌جا یک بار ثبت می‌شود.
/// </summary>
public sealed class CustodyAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    ISystemCompanyProvider systemCompany,
    IOptions<AccountingOptions> options,
    ILogger<CustodyAccountingAdapter> logger) : ICustodyAccountingAdapter
{
    public const string SourceModule = "Custody";
    public const string AccountingDisabledReason = "ACCOUNTING_DISABLED";
    private readonly AccountingOptions _options = options.Value;

    public static string SaleEventId(int saleId) => $"Custody:Sale:{saleId}:Created";
    public static string FinanceEventId(int entryId) => $"Custody:Finance:{entryId}:Created";
    public static string ReversalEventId(string createdEventId) => createdEventId.Replace(":Created", ":Reversed", StringComparison.Ordinal);
    public static string SaleJournalNumber(int companyId, int saleId) => $"CUS-{companyId:D6}-S{saleId:D9}";
    public static string FinanceJournalNumber(int companyId, int entryId) => $"CUS-{companyId:D6}-F{entryId:D9}";

    public async Task<CustodyAccountingResult> TryPostSaleAsync(CustodySale sale, CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(sale.OwnerPartyType, cancellationToken);
        if (context.SkipReason is not null)
            return Skip("Sale", sale.Id, context.SkipReason);

        var s = context.Settings!;
        var owner = (sale.OwnerPartyType, sale.OwnerPartyId);
        var lines = new List<AccountingPostLine>
        {
            Line(s.AccountsReceivableAccountId, sale.AmountUsd, 0m, AccountingPartyType.Customer, sale.CustomerId, sale.ProductId, $"Custody sale {sale.SaleNumber} receivable"),
            Line(s.CustodyPendingCollectionAccountId!.Value, 0m, sale.AmountUsd, owner.OwnerPartyType, owner.OwnerPartyId, sale.ProductId, $"Custody sale {sale.SaleNumber} pending collection")
        };
        AddCommission(lines, s, owner, sale.CommissionRecognizedAtSaleUsd, sale.ProductId, sale.SaleNumber);

        return await PostAsync(
            context.CompanyId,
            SaleJournalNumber(context.CompanyId, sale.Id),
            sale.SaleDate,
            SaleEventId(sale.Id),
            "CustodySale",
            sale.Id,
            $"Custody sale {sale.SaleNumber} on behalf of owner",
            lines,
            cancellationToken);
    }

    public async Task<CustodyAccountingResult> TryPostFinanceAsync(
        CustodyFinanceEntry entry,
        CustodySale? sale,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(entry.OwnerPartyType, cancellationToken);
        if (context.SkipReason is not null)
            return Skip("Finance", entry.Id, context.SkipReason);

        var s = context.Settings!;
        var owner = (entry.OwnerPartyType, entry.OwnerPartyId);
        var amount = entry.AmountUsd;
        var productId = sale?.ProductId;
        var label = sale?.SaleNumber ?? $"#{entry.Id}";
        var lines = new List<AccountingPostLine>();

        switch (entry.Kind)
        {
            case CustodyFinanceKind.Collection:
                lines.Add(CashLine(s, entry.CashAccountId, amount, 0m, $"Custody collection {label}"));
                lines.Add(Line(s.AccountsReceivableAccountId, 0m, amount, AccountingPartyType.Customer, entry.CustomerId!.Value, productId, $"Custody collection {label}"));
                lines.Add(Line(s.CustodyPendingCollectionAccountId!.Value, amount, 0m, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Custody collection {label} released to owner"));
                lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, 0m, amount, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Custody funds held for owner {label}"));
                AddCommission(lines, s, owner, entry.CommissionUsd, productId, label);
                break;
            case CustodyFinanceKind.DirectToOwner:
                lines.Add(Line(s.CustodyPendingCollectionAccountId!.Value, amount, 0m, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Customer paid owner directly {label}"));
                lines.Add(Line(s.AccountsReceivableAccountId, 0m, amount, AccountingPartyType.Customer, entry.CustomerId!.Value, productId, $"Customer paid owner directly {label}"));
                AddCommission(lines, s, owner, entry.CommissionUsd, productId, label);
                break;
            case CustodyFinanceKind.OwnerPayout:
            case CustodyFinanceKind.ReimbursableExpense:
                lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, amount, 0m, owner.OwnerPartyType, owner.OwnerPartyId, productId,
                    entry.Kind == CustodyFinanceKind.OwnerPayout ? "Payment to custody owner" : "Expense paid on owner's behalf"));
                lines.Add(CashLine(s, entry.CashAccountId, 0m, amount,
                    entry.Kind == CustodyFinanceKind.OwnerPayout ? "Payment to custody owner" : "Expense paid on owner's behalf"));
                break;
            case CustodyFinanceKind.ServiceFee:
                lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, amount, 0m, owner.OwnerPartyType, owner.OwnerPartyId, productId, "Custody service fee charged to owner"));
                lines.Add(Line(s.CustodyServiceRevenueAccountId!.Value, 0m, amount, null, null, productId, "Custody service revenue"));
                break;
            default:
                return Skip("Finance", entry.Id, "UNSUPPORTED_KIND");
        }

        return await PostAsync(
            context.CompanyId,
            FinanceJournalNumber(context.CompanyId, entry.Id),
            entry.EntryDate,
            FinanceEventId(entry.Id),
            "CustodyFinanceEntry",
            entry.Id,
            $"Custody {entry.Kind} {label}",
            lines,
            cancellationToken);
    }

    public async Task<CustodyAccountingResult> TryReverseAsync(
        string createdEventId,
        string journalNumber,
        DateTime reversalDate,
        string description,
        CancellationToken cancellationToken = default)
    {
        var original = await db.JournalEntries.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SourceModule == SourceModule && x.SourceEventId == createdEventId, cancellationToken);
        if (original is null)
            return new CustodyAccountingResult(CustodyPostingStatus.Skipped, null, "ORIGINAL_JOURNAL_NOT_POSTED");

        var reversalEventId = ReversalEventId(createdEventId);
        var existing = await db.JournalEntries.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SourceModule == SourceModule && x.SourceEventId == reversalEventId, cancellationToken);
        if (existing is not null)
            return new CustodyAccountingResult(CustodyPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");

        var journal = await postingService.ReverseAsync(
            new AccountingReversalRequest(
                original.Id,
                journalNumber + "-R",
                reversalDate.Date,
                SourceModule,
                reversalEventId,
                description),
            cancellationToken);
        return new CustodyAccountingResult(CustodyPostingStatus.Posted, journal, null);
    }

    private static void AddCommission(
        List<AccountingPostLine> lines,
        AccountingSettings s,
        (AccountingPartyType Type, int Id) owner,
        decimal commissionUsd,
        int? productId,
        string label)
    {
        if (commissionUsd <= 0m) return;
        lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, commissionUsd, 0m, owner.Type, owner.Id, productId, $"Commission deducted {label}"));
        lines.Add(Line(s.CustodyCommissionRevenueAccountId!.Value, 0m, commissionUsd, null, null, productId, $"Custody commission {label}"));
    }

    private static AccountingPostLine Line(
        int accountId,
        decimal debit,
        decimal credit,
        AccountingPartyType? partyType,
        int? partyId,
        int? productId,
        string description)
        => new(accountId, debit, credit, "USD", debit + credit, 1m, partyType, partyId,
            ProductId: productId, Description: description);

    private static AccountingPostLine CashLine(AccountingSettings s, int? cashAccountId, decimal debit, decimal credit, string description)
        => new(s.CashBankControlAccountId, debit, credit, "USD", debit + credit, 1m,
            CashAccountId: cashAccountId, Description: description);

    private async Task<CustodyAccountingResult> PostAsync(
        int companyId,
        string journalNumber,
        DateTime date,
        string eventId,
        string entityType,
        int entityId,
        string description,
        IReadOnlyCollection<AccountingPostLine> lines,
        CancellationToken cancellationToken)
    {
        var existing = await db.JournalEntries.AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SourceModule == SourceModule && x.SourceEventId == eventId, cancellationToken);
        if (existing is not null)
            return new CustodyAccountingResult(CustodyPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");

        var journal = await postingService.PostAsync(
            new AccountingPostRequest(
                companyId,
                journalNumber,
                date.Date,
                date.Date,
                date.Date,
                SourceModule,
                lines,
                SourceEventId: eventId,
                SourceEntityType: entityType,
                SourceEntityId: entityId,
                Description: description),
            cancellationToken);
        logger.LogInformation("Custody journal posted: {EntityType} {EntityId} journal {JournalNumber}", entityType, entityId, journalNumber);
        return new CustodyAccountingResult(CustodyPostingStatus.Posted, journal, null);
    }

    private CustodyAccountingResult Skip(string entityType, int entityId, string reason)
    {
        logger.LogInformation("Custody journal skipped: {EntityType} {EntityId} reason {Reason}", entityType, entityId, reason);
        return new CustodyAccountingResult(CustodyPostingStatus.Skipped, null, reason);
    }

    private async Task<(int CompanyId, AccountingSettings? Settings, string? SkipReason)> ResolveContextAsync(
        AccountingPartyType ownerType,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return (0, null, AccountingDisabledReason);
        if (!_options.Pilots.Custody) return (0, null, "PILOT_DISABLED");

        var companyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);
        var settings = await db.AccountingSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId, cancellationToken);
        if (settings is null) return (companyId, null, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId, null, "UNSUPPORTED_FUNCTIONAL_CURRENCY");
        if (settings.CustodyFundsPayableAccountId is null
            || settings.CustodyPendingCollectionAccountId is null
            || settings.CustodyCommissionRevenueAccountId is null
            || settings.CustodyServiceRevenueAccountId is null)
            return (companyId, null, "CUSTODY_ACCOUNTS_NOT_CONFIGURED");
        if (ownerType is not (AccountingPartyType.Customer or AccountingPartyType.Supplier))
            return (companyId, null, "UNSUPPORTED_OWNER_TYPE");

        return (companyId, settings, null);
    }
}
