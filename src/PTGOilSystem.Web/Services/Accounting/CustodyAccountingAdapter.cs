using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Custody;

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
    bool IsEnabled { get; }
    bool IsPilotEnabled { get; }
    Task<CustodyAccountingResult> TryPostSaleAsync(CustodySale sale, CancellationToken cancellationToken = default);

    Task<CustodyAccountingResult> TryPostFinanceAsync(
        CustodyFinanceEntry entry,
        CustodySale? sale,
        CancellationToken cancellationToken = default);

    Task<CustodyAccountingResult> TryPostReturnAsync(
        CustodySaleReturn saleReturn,
        CustodySale sale,
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
    public bool IsEnabled => _options.Enabled;
    public bool IsPilotEnabled => _options.Pilots.Custody;

    public static string SaleEventId(int saleId) => $"Custody:Sale:{saleId}:Created";
    public static string FinanceEventId(int entryId) => $"Custody:Finance:{entryId}:Created";
    public static string ReturnEventId(int returnId) => $"Custody:Return:{returnId}:Created";
    public static string ReversalEventId(string createdEventId) => createdEventId.Replace(":Created", ":Reversed", StringComparison.Ordinal);
    public static string SaleJournalNumber(int companyId, int saleId) => $"CUS-{companyId:D6}-S{saleId:D9}";
    public static string FinanceJournalNumber(int companyId, int entryId) => $"CUS-{companyId:D6}-F{entryId:D9}";
    public static string ReturnJournalNumber(int companyId, int returnId) => $"CUS-{companyId:D6}-R{returnId:D9}";
    public const string ProviderPayableNotConfiguredReason = "CUSTODY_PROVIDER_PAYABLE_NOT_CONFIGURED";

    public async Task<CustodyAccountingResult> TryPostSaleAsync(CustodySale sale, CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(sale.OwnerPartyType, cancellationToken);
        if (context.SkipReason is not null)
            return Skip("Sale", sale.Id, context.SkipReason);

        var s = context.Settings!;
        var owner = (sale.OwnerPartyType, sale.OwnerPartyId);
        var receivable = Money.Of(sale.Currency, sale.EffectiveAmount, sale.AmountUsd, sale.RateToUsd);
        var lines = new List<AccountingPostLine>
        {
            Line(s.AccountsReceivableAccountId, receivable, true, AccountingPartyType.Customer, sale.CustomerId, sale.ProductId, $"Custody sale {sale.SaleNumber} receivable"),
            Line(s.CustodyPendingCollectionAccountId!.Value, receivable, false, owner.OwnerPartyType, owner.OwnerPartyId, sale.ProductId, $"Custody sale {sale.SaleNumber} pending collection")
        };
        AddCommission(lines, s, owner, Money.Of(sale.EffectiveOwnerCurrency, sale.EffectiveCommissionAtSaleOwner, sale.CommissionRecognizedAtSaleUsd, sale.OwnerRateToUsd),
            sale.ProductId, sale.SaleNumber);

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
        if (sale is not null && !await db.JournalEntries.AsNoTracking().AnyAsync(j =>
                j.CompanyId == context.CompanyId && j.SourceModule == SourceModule
                && j.SourceEventId == SaleEventId(sale.Id) && j.Status == JournalEntryStatus.Posted, cancellationToken))
            return Skip("Finance", entry.Id, "CUSTODY_RECONCILIATION_REQUIRED");

        var s = context.Settings!;
        var owner = (entry.OwnerPartyType, entry.OwnerPartyId);
        var productId = sale?.ProductId;
        var label = sale?.SaleNumber ?? $"#{entry.Id}";
        var lines = new List<AccountingPostLine>();
        // پولِ خودِ سند (صندوق) به ارز و نرخ همان روز؛ طلب مشتری و «در انتظار وصول» به نرخ فروش؛ حساب
        // مالک (۲۶۰۰) به ارز مالک. اختلافِ دالریِ باقی‌مانده فقط تفاوت نرخ است و به حساب سود/زیان تسعیر می‌رود.
        var cash = Money.Of(entry.Currency, entry.EffectiveAmount, entry.AmountUsd, entry.RateToUsd);
        var receivable = sale is null ? cash : Money.Of(sale.Currency, entry.EffectiveAmount, CustodyFx.AtSaleRate(sale, entry.EffectiveAmount), sale.RateToUsd);
        var ownerFunds = Money.Of(entry.EffectiveOwnerCurrency, entry.EffectiveOwnerAmount,
            Usd(entry.EffectiveOwnerAmount, entry.EffectiveOwnerCurrency, entry.EffectiveOwnerRateToUsd), entry.OwnerRateToUsd);
        var commission = Money.Of(entry.EffectiveOwnerCurrency, entry.EffectiveOwnerCommission, entry.CommissionUsd, entry.OwnerRateToUsd);

        switch (entry.Kind)
        {
            case CustodyFinanceKind.Collection:
                lines.Add(CashLine(s, entry.CashAccountId, cash, true, $"Custody collection {label}"));
                lines.Add(Line(s.AccountsReceivableAccountId, receivable, false, AccountingPartyType.Customer, entry.CustomerId!.Value, productId, $"Custody collection {label}"));
                lines.Add(Line(s.CustodyPendingCollectionAccountId!.Value, receivable, true, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Custody collection {label} released to owner"));
                lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, ownerFunds, false, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Custody funds held for owner {label}"));
                AddCommission(lines, s, owner, commission, productId, label);
                break;
            case CustodyFinanceKind.DirectToOwner:
                lines.Add(Line(s.CustodyPendingCollectionAccountId!.Value, receivable, true, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Customer paid owner directly {label}"));
                lines.Add(Line(s.AccountsReceivableAccountId, receivable, false, AccountingPartyType.Customer, entry.CustomerId!.Value, productId, $"Customer paid owner directly {label}"));
                AddCommission(lines, s, owner, commission, productId, label);
                break;
            case CustodyFinanceKind.OwnerPayout:
            case CustodyFinanceKind.ReimbursableExpense:
                lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, cash, true, owner.OwnerPartyType, owner.OwnerPartyId, productId,
                    entry.Kind == CustodyFinanceKind.OwnerPayout ? "Payment to custody owner" : "Expense paid on owner's behalf"));
                lines.Add(CashLine(s, entry.CashAccountId, cash, false,
                    entry.Kind == CustodyFinanceKind.OwnerPayout ? "Payment to custody owner" : "Expense paid on owner's behalf"));
                break;
            case CustodyFinanceKind.ServiceFee:
                lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, cash, true, owner.OwnerPartyType, owner.OwnerPartyId, productId, "Custody service fee charged to owner"));
                lines.Add(Line(s.CustodyServiceRevenueAccountId!.Value, Money.Dollars(entry.AmountUsd), false, null, null, productId, "Custody service revenue"));
                break;
            case CustodyFinanceKind.CustomerRefund:
                // آینهٔ وصول: پول از صندوق به مشتری برمی‌گردد و از وجوه مالک کم می‌شود.
                lines.Add(Line(s.AccountsReceivableAccountId, receivable, true, AccountingPartyType.Customer, entry.CustomerId!.Value, productId, $"Custody refund {label}"));
                lines.Add(CashLine(s, entry.CashAccountId, cash, false, $"Custody refund {label}"));
                lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, ownerFunds, true, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Custody refund {label} from owner funds"));
                lines.Add(Line(s.CustodyPendingCollectionAccountId!.Value, receivable, false, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Custody refund {label}"));
                break;
            case CustodyFinanceKind.OwnerDirectRefund:
                // آینهٔ پرداخت مستقیم مشتری به مالک؛ صندوق شرکت و وجوه مالک دست نمی‌خورند.
                lines.Add(Line(s.AccountsReceivableAccountId, receivable, true, AccountingPartyType.Customer, entry.CustomerId!.Value, productId, $"Owner refunded customer directly {label}"));
                lines.Add(Line(s.CustodyPendingCollectionAccountId!.Value, receivable, false, owner.OwnerPartyType, owner.OwnerPartyId, productId, $"Owner refunded customer directly {label}"));
                break;
            case CustodyFinanceKind.ProviderCharge:
            case CustodyFinanceKind.ProviderPayment:
            {
                // The payable account comes from the expense type's explicit payable kind — the same
                // mapping ordinary expenses use — and is never guessed.
                var payableAccountId = await ResolveProviderPayableAccountAsync(entry, s, cancellationToken);
                if (payableAccountId is null || entry.ServiceProviderId is null)
                    return Skip("Finance", entry.Id, ProviderPayableNotConfiguredReason);
                if (entry.Kind == CustodyFinanceKind.ProviderCharge)
                {
                    lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, cash, true, owner.OwnerPartyType, owner.OwnerPartyId, productId, "Unpaid provider charge on owner's behalf"));
                    lines.Add(Line(payableAccountId.Value, cash, false, AccountingPartyType.ServiceProvider, entry.ServiceProviderId, productId, "Provider payable for custody owner"));
                }
                else
                {
                    lines.Add(Line(payableAccountId.Value, cash, true, AccountingPartyType.ServiceProvider, entry.ServiceProviderId, productId, "Provider payable settled (custody)"));
                    lines.Add(CashLine(s, entry.CashAccountId, cash, false, "Provider payable settled (custody)"));
                }
                break;
            }
            default:
                return Skip("Finance", entry.Id, "UNSUPPORTED_KIND");
        }

        // فقط سندِ دوارزیِ نقدی (وصول/استرداد با ارز مالکِ متفاوت) تفاوت نرخ دارد؛ هر جای دیگر ناترازی خطاست.
        var crossCurrency = sale is not null && sale.IsCrossCurrency
            && entry.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.CustomerRefund;
        AddExchangeDifference(lines, s, crossCurrency, productId, label);

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

    public async Task<CustodyAccountingResult> TryPostReturnAsync(
        CustodySaleReturn saleReturn,
        CustodySale sale,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(saleReturn.OwnerPartyType, cancellationToken);
        if (context.SkipReason is not null)
            return Skip("Return", saleReturn.Id, context.SkipReason);
        if (!await db.JournalEntries.AsNoTracking().AnyAsync(j =>
                j.CompanyId == context.CompanyId && j.SourceModule == SourceModule
                && j.SourceEventId == SaleEventId(sale.Id) && j.Status == JournalEntryStatus.Posted, cancellationToken))
            return Skip("Return", saleReturn.Id, "CUSTODY_RECONCILIATION_REQUIRED");

        var s = context.Settings!;
        var owner = (saleReturn.OwnerPartyType, saleReturn.OwnerPartyId);
        var label = saleReturn.ReturnNumber;
        // Mirror image of the sale journal for the returned part only.
        var returned = Money.Of(sale.Currency, saleReturn.EffectiveAmount, saleReturn.AmountUsd, sale.RateToUsd);
        var lines = new List<AccountingPostLine>
        {
            Line(s.CustodyPendingCollectionAccountId!.Value, returned, true, owner.OwnerPartyType, owner.OwnerPartyId, sale.ProductId, $"Custody sale return {label} pending collection released"),
            Line(s.AccountsReceivableAccountId, returned, false, AccountingPartyType.Customer, saleReturn.CustomerId, sale.ProductId, $"Custody sale return {label} receivable reduced")
        };
        if (saleReturn.RecognizedCommissionReversedUsd > 0m)
        {
            lines.Add(Line(s.CustodyCommissionRevenueAccountId!.Value, Money.Dollars(saleReturn.RecognizedCommissionReversedUsd), true, null, null, sale.ProductId, $"Custody commission reversed {label}"));
            lines.Add(Line(s.CustodyFundsPayableAccountId!.Value,
                Money.Of(sale.EffectiveOwnerCurrency, saleReturn.EffectiveOwnerCommissionReversed, saleReturn.RecognizedCommissionReversedUsd, saleReturn.OwnerRateToUsd),
                false, owner.OwnerPartyType, owner.OwnerPartyId, sale.ProductId, $"Commission returned to owner {label}"));
        }

        return await PostAsync(
            context.CompanyId,
            ReturnJournalNumber(context.CompanyId, saleReturn.Id),
            saleReturn.ReturnDate,
            ReturnEventId(saleReturn.Id),
            "CustodySaleReturn",
            saleReturn.Id,
            $"Custody sale return {label} of {sale.SaleNumber}",
            lines,
            cancellationToken);
    }

    private async Task<int?> ResolveProviderPayableAccountAsync(CustodyFinanceEntry entry, AccountingSettings settings, CancellationToken ct)
    {
        var expenseTypeId = entry.ExpenseTypeId;
        if (entry.Kind == CustodyFinanceKind.ProviderPayment && entry.SettlesFinanceEntryId is { } chargeId)
            expenseTypeId = await db.CustodyFinanceEntries.AsNoTracking().Where(x => x.Id == chargeId)
                .Select(x => x.ExpenseTypeId).SingleOrDefaultAsync(ct);
        if (expenseTypeId is null) return null;
        var kind = await db.ExpenseTypes.AsNoTracking().Where(x => x.Id == expenseTypeId)
            .Select(x => x.PayableAccountKind).SingleOrDefaultAsync(ct);
        return kind is null ? null : ExpenseAccountingAdapter.PayableAccountFor(kind.Value, settings);
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

    /// <summary>مبلغِ یک سطر: ارز و مبلغِ اصلی و ارزشِ دالری.</summary>
    private readonly record struct Money(string Currency, decimal Amount, decimal Usd, decimal? Rate)
    {
        public static Money Of(string currency, decimal amount, decimal usd, decimal? rate = null)
            => SystemCurrency.IsBaseCurrency(currency) ? new(SystemCurrency.BaseCurrencyCode, usd, usd, 1m) : new(currency.Trim().ToUpperInvariant(), amount, usd, rate);
        public static Money Dollars(decimal usd) => new(SystemCurrency.BaseCurrencyCode, usd, usd, 1m);
    }

    private static decimal Usd(decimal amount, string currency, decimal toUsd)
        => SystemCurrency.IsBaseCurrency(currency) ? amount : decimal.Round(amount * toUsd, 4, MidpointRounding.AwayFromZero);

    private static void AddCommission(
        List<AccountingPostLine> lines,
        AccountingSettings s,
        (AccountingPartyType Type, int Id) owner,
        Money commission,
        int? productId,
        string label)
    {
        if (commission.Usd <= 0m) return;
        lines.Add(Line(s.CustodyFundsPayableAccountId!.Value, commission, true, owner.Type, owner.Id, productId, $"Commission deducted {label}"));
        lines.Add(Line(s.CustodyCommissionRevenueAccountId!.Value, Money.Dollars(commission.Usd), false, null, null, productId, $"Custody commission {label}"));
    }

    /// <summary>
    /// تفاوتِ دالریِ پولِ واقعی و سهمِ ثبت‌شدهٔ مالک (طبق توافقِ مسئولیت تفاوت نرخ) به حساب‌های
    /// تسعیرِ تنظیمات حسابداری می‌رود. سندِ هم‌ارز باید دقیقاً تراز باشد.
    /// </summary>
    private static void AddExchangeDifference(List<AccountingPostLine> lines, AccountingSettings s, bool crossCurrency, int? productId, string label)
    {
        var difference = lines.Sum(x => x.Debit) - lines.Sum(x => x.Credit);
        if (difference == 0m) return;
        if (!crossCurrency)
            throw new InvalidOperationException($"Custody journal for {label} is unbalanced by {difference} without a currency difference.");
        if (difference > 0m)
            lines.Add(Line(s.ExchangeGainAccountId, Money.Dollars(difference), false, null, null, productId, $"Custody exchange difference {label}"));
        else
            lines.Add(Line(s.ExchangeLossAccountId, Money.Dollars(-difference), true, null, null, productId, $"Custody exchange difference {label}"));
    }

    /// <summary>
    /// سطرِ ارزی: مبلغ اصلی × نرخ باید دقیقاً همان ارزش دالری باشد (قاعدهٔ <see cref="AccountingPostingService"/>).
    /// نرخ از خودِ ارزش‌ها مشتق می‌شود تا ارزشِ دالریِ ثبت‌شده در سند امانی و ژورنال یکی بماند.
    /// </summary>
    private static AccountingPostLine Line(
        int accountId,
        Money money,
        bool debit,
        AccountingPartyType? partyType,
        int? partyId,
        int? productId,
        string description,
        int? cashAccountId = null)
    {
        var rate = 1m;
        if (!SystemCurrency.IsBaseCurrency(money.Currency))
        {
            if (money.Amount <= 0m)
                throw new InvalidOperationException($"Custody {money.Currency} line without an amount: {description}");
            // اول نرخِ خودِ سند؛ اگر ارزش دالری از جمعِ چند جزء آمده، نرخِ مشتق از همان ارزش.
            rate = money.Rate is > 0m && decimal.Round(money.Amount * money.Rate.Value, 4, MidpointRounding.AwayFromZero) == money.Usd
                ? money.Rate.Value
                : FxRateMath.RoundRate(money.Usd / money.Amount);
            if (decimal.Round(money.Amount * rate, 4, MidpointRounding.AwayFromZero) != money.Usd)
                throw new InvalidOperationException($"Custody {money.Currency} line cannot be expressed with a 12-digit rate: {description}");
        }
        return new AccountingPostLine(accountId, debit ? money.Usd : 0m, debit ? 0m : money.Usd, money.Currency, money.Amount, rate,
            partyType, partyId, ProductId: productId, CashAccountId: cashAccountId, Description: description);
    }

    private static AccountingPostLine CashLine(AccountingSettings s, int? cashAccountId, Money money, bool debit, string description)
        => Line(s.CashBankControlAccountId, money, debit, null, null, null, description, cashAccountId);

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
