using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Parties;

namespace PTGOilSystem.Web.Services.Custody;

public sealed record CustodyGlOwnerPosition(CustodyOwner Owner, string Name, decimal FundsNetCreditUsd,
    decimal PendingNetCreditUsd, decimal OperationalFundsUsd)
{
    public decimal OwnerPayableUsd => Math.Max(FundsNetCreditUsd, 0m);
    public decimal OwnerReceivableUsd => Math.Max(-FundsNetCreditUsd, 0m);
    public decimal DifferenceUsd => FundsNetCreditUsd - OperationalFundsUsd;
}

/// <summary>مصرف نسیهٔ امانیِ هنوز پرداخت‌نشده به یک خدمات‌دهنده (بدهی واقعی شرکت)؛ مبلغ به ارز همان مصرف.</summary>
public sealed record CustodyOpenProviderCharge(int ServiceProviderId, string Name, int Charges, decimal UnpaidUsd, string Currency = "USD");

/// <summary>تبدیل‌های فعالِ امانی به خرید قطعی تا تاریخ راپور (مقدار و بهای قرارداد خرید canonical).</summary>
public sealed record CustodyConversionTotals(int Count, decimal QuantityMt, decimal PurchaseValueUsd,
    IReadOnlyList<CustodyConversionLine>? Lines = null);

public sealed record CustodyConversionLine(int ConversionId, string LotNumber, int ContractId, string? ContractNumber, DateTime Date,
    decimal QuantityMt, decimal UnitPriceUsd);

/// <summary>
/// ماندهٔ یک مالک به یک ارز: عملیاتی (از اسناد امانی) و دفتر کل (مبلغ اصلیِ همان ارز در ۲۶۰۰). اختلاف
/// به ارز اصلی سنجیده می‌شود تا تسعیرِ پایان سال آن را پنهان نکند.
/// </summary>
public sealed record CustodyCurrencyPosition(CustodyOwner Owner, string Name, string Currency, decimal OperationalBalance, decimal GlBalance)
{
    public decimal Difference => OperationalBalance - GlBalance;
}

/// <summary>مصارف و حق خدمات به حساب مالکان، به تفکیک نوع و ارز.</summary>
public sealed record CustodyExpenseLine(string Kind, string ExpenseType, string Currency, int Documents, decimal Amount, decimal AmountUsd);

public sealed record CustodyGlReport(DateTime AsOf, IReadOnlyList<CustodyGlOwnerPosition> Owners,
    decimal UnassignedFundsNetCreditUsd, decimal UnassignedPendingNetCreditUsd,
    decimal AgencyCustomerReceivableUsd, int MissingActiveDocuments, IReadOnlyList<string> Blockers,
    IReadOnlyList<CustodyOpenProviderCharge>? OpenProviderCharges = null,
    CustodyConversionTotals? Conversions = null,
    IReadOnlyList<CustodyCurrencyPosition>? CurrencyPositions = null,
    IReadOnlyList<CustodyExpenseLine>? Expenses = null,
    decimal FxGainUsd = 0m,
    decimal FxLossUsd = 0m)
{
    public IReadOnlyList<CustodyCurrencyPosition> Currencies => CurrencyPositions ?? [];
    public IReadOnlyList<CustodyExpenseLine> ExpenseLines => Expenses ?? [];
    public IReadOnlyList<CustodyOpenProviderCharge> ProviderCharges => OpenProviderCharges ?? [];
    // Split per person BEFORE totaling: one owner's debt cannot offset another owner's money.
    public decimal OwnerPayablesUsd => Owners.Sum(x => x.OwnerPayableUsd);
    public decimal OwnerReceivablesUsd => Owners.Sum(x => x.OwnerReceivableUsd);
    public decimal PendingCollectionContraUsd => Owners.Sum(x => x.PendingNetCreditUsd) + UnassignedPendingNetCreditUsd;
    public decimal AgencyNetCompanyReceivableUsd => AgencyCustomerReceivableUsd - PendingCollectionContraUsd;
    public bool IsReconciled => Blockers.Count == 0 && MissingActiveDocuments == 0
        && UnassignedFundsNetCreditUsd == 0m && UnassignedPendingNetCreditUsd == 0m
        && Owners.All(x => Math.Abs(x.DifferenceUsd) < 0.0001m)
        && Math.Abs(AgencyNetCompanyReceivableUsd) < 0.0001m;
}

/// <summary>Reads the one existing GL and operational ledger; never posts, revalues or nets different owners.</summary>
public sealed class CustodyGlReadService(ApplicationDbContext db, ISystemCompanyProvider company,
    IPartyDirectory directory, IOptions<AccountingOptions> options)
{
    public async Task<CustodyGlReport> ReadAsync(DateTime asOf, CancellationToken ct = default)
    {
        var date = asOf.Date;
        var cutoff = DateTime.SpecifyKind(date.AddDays(1), DateTimeKind.Utc);
        var blockers = new List<string>();
        if (!options.Value.Enabled) blockers.Add("دفتر کل خاموش است؛ این راپور تأیید کامل حسابداری نیست.");
        var companyId = await company.FindOwnerCompanyIdAsync(ct);
        var settings = companyId.HasValue ? await db.AccountingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct) : null;
        if (settings?.CustodyFundsPayableAccountId is not { } fundsId || settings.CustodyPendingCollectionAccountId is not { } pendingId)
            return new(date, [], 0m, 0m, 0m, 0, [..blockers, "شرکت مالک و نقشه حساب‌های امانی باید کامل باشند."]);
        if (!string.Equals(settings.FunctionalCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase))
            return new(date, [], 0m, 0m, 0m, 0, [..blockers, "ارز عملیاتی دفتر کل USD نیست؛ ارقام به دالر حدس زده نمی‌شوند."]);
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.Id == fundsId || x.Id == pendingId).ToListAsync(ct);
        if (!accounts.Any(x => x.Id == fundsId && x.CompanyId == companyId && x.IsActive && x.AccountType == AccountType.Liability && x.NormalBalance == NormalBalance.Credit)
            || !accounts.Any(x => x.Id == pendingId && x.CompanyId == companyId && x.IsActive && x.AccountType == AccountType.Asset && x.NormalBalance == NormalBalance.Credit))
            blockers.Add("نوع، سمت طبیعی یا وضعیت حساب کنترولی امانی معتبر نیست؛ نقشه حساب را بررسی کنید.");
        var lines = db.JournalEntryLines.AsNoTracking().Where(x => x.JournalEntry!.CompanyId == companyId
            && x.JournalEntry.Status == JournalEntryStatus.Posted && x.JournalEntry.AccountingDate < cutoff);
        var positions = await lines.Where(x => x.AccountId == fundsId || x.AccountId == pendingId)
            .GroupBy(x => new { x.PartyType, x.PartyId })
            .Select(g => new { g.Key.PartyType, g.Key.PartyId,
                Funds = g.Sum(x => x.AccountId == fundsId ? x.Credit - x.Debit : 0m),
                Pending = g.Sum(x => x.AccountId == pendingId ? x.Credit - x.Debit : 0m) }).ToListAsync(ct);
        // Provider rows of unpaid custody charges are the company's payable to that provider, not owner funds.
        var legacy = await db.LedgerEntries.AsNoTracking().Where(x => x.SourceType.StartsWith("Custody") && x.EntryDate < cutoff
                && x.ServiceProviderId == null)
            .GroupBy(x => new { x.CustomerId, x.SupplierId })
            .Select(g => new { g.Key.CustomerId, g.Key.SupplierId,
                Balance = g.Sum(x => x.CustomerId.HasValue
                    ? (x.Side == LedgerSide.Debit ? x.AmountUsd : -x.AmountUsd)
                    : (x.Side == LedgerSide.Credit ? x.AmountUsd : -x.AmountUsd)) }).ToListAsync(ct);
        var balances = new Dictionary<CustodyOwner, decimal>();
        foreach (var row in legacy)
        {
            if (row.CustomerId.HasValue && row.SupplierId.HasValue)
            { blockers.Add("سطر دفتر امانی دارای دو هویت است؛ تطبیق دستی لازم است."); continue; }
            if (row.CustomerId is { } customer) balances[new(AccountingPartyType.Customer, customer)] = row.Balance;
            else if (row.SupplierId is { } supplier) balances[new(AccountingPartyType.Supplier, supplier)] = row.Balance;
            else if (row.Balance != 0m) blockers.Add("سطر دفتر عملیاتی امانی بدون هویت مالی معتبر وجود دارد.");
        }
        var typed = positions.Where(x => x.PartyId > 0 && x.PartyType is AccountingPartyType.Customer or AccountingPartyType.Supplier).ToList();
        var owners = typed.Select(x => new CustodyOwner(x.PartyType!.Value, x.PartyId!.Value)).Concat(balances.Keys).Distinct().ToList();
        var keys = owners.Select(x => new PartyKey(x.PartyType == AccountingPartyType.Customer ? PartyStatementPartyType.Customer : PartyStatementPartyType.Supplier, x.PartyId)).ToList();
        var names = await directory.GetNamesAsync(keys, ct);
        var rows = owners.Select(owner =>
        {
            var position = typed.SingleOrDefault(x => x.PartyType == owner.PartyType && x.PartyId == owner.PartyId);
            var key = new PartyKey(owner.PartyType == AccountingPartyType.Customer ? PartyStatementPartyType.Customer : PartyStatementPartyType.Supplier, owner.PartyId);
            return new CustodyGlOwnerPosition(owner, names.GetValueOrDefault(key, "هویت پیدا نشد"),
                position?.Funds ?? 0m, position?.Pending ?? 0m, balances.GetValueOrDefault(owner));
        }).Where(x => x.FundsNetCreditUsd != 0m || x.PendingNetCreditUsd != 0m || x.OperationalFundsUsd != 0m)
            .OrderBy(x => x.Name).ToList();
        var unassigned = positions.Except(typed).ToList();
        var missing = await db.CustodySales.AsNoTracking().CountAsync(s => s.Status == CustodyDocumentStatus.Posted && s.SaleDate < cutoff
            && !db.JournalEntries.Any(j => j.CompanyId == companyId && j.SourceModule == "Custody" && j.SourceEntityType == nameof(CustodySale)
                && j.SourceEntityId == s.Id && j.Status == JournalEntryStatus.Posted), ct);
        missing += await db.CustodyFinanceEntries.AsNoTracking().CountAsync(e => e.Status == CustodyDocumentStatus.Posted && e.EntryDate < cutoff
            && !db.JournalEntries.Any(j => j.CompanyId == companyId && j.SourceModule == "Custody" && j.SourceEntityType == nameof(CustodyFinanceEntry)
                && j.SourceEntityId == e.Id && j.Status == JournalEntryStatus.Posted), ct);
        missing += await db.CustodySaleReturns.AsNoTracking().CountAsync(r => r.Status == CustodyDocumentStatus.Posted && r.ReturnDate < cutoff
            && !db.JournalEntries.Any(j => j.CompanyId == companyId && j.SourceModule == "Custody" && j.SourceEntityType == nameof(CustodySaleReturn)
                && j.SourceEntityId == r.Id && j.Status == JournalEntryStatus.Posted), ct);
        var agencyAr = await lines.Where(x => x.AccountId == settings.AccountsReceivableAccountId && x.JournalEntry!.SourceModule == "Custody")
            .SumAsync(x => x.Debit - x.Credit, ct);
        var fx = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.Status == CustodyDocumentStatus.Posted && e.EntryDate < cutoff && e.FxCompanyUsd != null && e.FxCompanyUsd != 0m)
            .Select(e => e.FxCompanyUsd!.Value).ToListAsync(ct);
        return new(date, rows, unassigned.Sum(x => x.Funds), unassigned.Sum(x => x.Pending), agencyAr, missing, blockers.Distinct().ToList(),
            await OpenProviderChargesAsync(cutoff, ct), await ConversionTotalsAsync(cutoff, ct),
            await CurrencyPositionsAsync(lines, fundsId, cutoff, ct), await ExpensesAsync(cutoff, ct),
            fx.Where(x => x > 0m).Sum(), -fx.Where(x => x < 0m).Sum());
    }

    /// <summary>
    /// ماندهٔ هر مالک به هر ارز: از اسناد امانی (همان تعریفِ سقف پرداخت) و از مبلغ اصلیِ همان ارز در ۲۶۰۰.
    /// </summary>
    private async Task<IReadOnlyList<CustodyCurrencyPosition>> CurrencyPositionsAsync(IQueryable<JournalEntryLine> lines, int fundsId,
        DateTime cutoff, CancellationToken ct)
    {
        var gl = await lines.Where(x => x.AccountId == fundsId && x.PartyId != null
                && (x.PartyType == AccountingPartyType.Customer || x.PartyType == AccountingPartyType.Supplier))
            .GroupBy(x => new { x.PartyType, x.PartyId, x.TransactionCurrencyCode })
            .Select(g => new { g.Key.PartyType, g.Key.PartyId, Currency = g.Key.TransactionCurrencyCode,
                Balance = g.Sum(x => x.Credit > 0m ? x.TransactionAmount : -x.TransactionAmount) })
            .ToListAsync(ct);
        var owners = await db.CustodyFinanceEntries.AsNoTracking().Where(e => e.EntryDate < cutoff)
            .Select(e => new { e.OwnerPartyType, e.OwnerPartyId })
            .Union(db.CustodySales.AsNoTracking().Where(s => s.SaleDate < cutoff).Select(s => new { s.OwnerPartyType, s.OwnerPartyId }))
            .Distinct().ToListAsync(ct);
        var all = owners.Select(o => new CustodyOwner(o.OwnerPartyType, o.OwnerPartyId))
            .Concat(gl.Select(g => new CustodyOwner(g.PartyType!.Value, g.PartyId!.Value))).Distinct().ToList();
        var keys = all.Select(x => new PartyKey(x.PartyType == AccountingPartyType.Customer ? PartyStatementPartyType.Customer : PartyStatementPartyType.Supplier, x.PartyId)).ToList();
        var names = await directory.GetNamesAsync(keys, ct);
        var result = new List<CustodyCurrencyPosition>();
        foreach (var owner in all)
        {
            var operational = await CustodySaleService.OwnerFundsBalancesAsync(db, owner, ct, cutoff);
            var currencies = operational.Keys.Concat(gl.Where(g => g.PartyType == owner.PartyType && g.PartyId == owner.PartyId).Select(g => g.Currency))
                .Select(c => c.ToUpperInvariant()).Distinct();
            var key = new PartyKey(owner.PartyType == AccountingPartyType.Customer ? PartyStatementPartyType.Customer : PartyStatementPartyType.Supplier, owner.PartyId);
            foreach (var currency in currencies)
            {
                var row = new CustodyCurrencyPosition(owner, names.GetValueOrDefault(key, "هویت پیدا نشد"), currency,
                    operational.GetValueOrDefault(currency),
                    gl.Where(g => g.PartyType == owner.PartyType && g.PartyId == owner.PartyId && string.Equals(g.Currency, currency, StringComparison.OrdinalIgnoreCase)).Sum(g => g.Balance));
                if (row.OperationalBalance != 0m || row.GlBalance != 0m)
                    result.Add(row);
            }
        }
        return result.OrderBy(x => x.Name).ThenBy(x => x.Currency == "USD" ? 0 : 1).ToList();
    }

    private async Task<IReadOnlyList<CustodyExpenseLine>> ExpensesAsync(DateTime cutoff, CancellationToken ct)
    {
        var rows = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.Status == CustodyDocumentStatus.Posted && e.EntryDate < cutoff
                && (e.Kind == CustodyFinanceKind.ReimbursableExpense || e.Kind == CustodyFinanceKind.ProviderCharge || e.Kind == CustodyFinanceKind.ServiceFee))
            .GroupBy(e => new { e.Kind, Type = e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : null, e.Currency })
            .Select(g => new { g.Key.Kind, g.Key.Type, g.Key.Currency, Count = g.Count(), Amount = g.Sum(e => e.Amount ?? e.AmountUsd), Usd = g.Sum(e => e.AmountUsd) })
            .ToListAsync(ct);
        return rows.Select(r => new CustodyExpenseLine(Models.Custody.CustodyFinanceForm.Title(r.Kind), r.Type ?? "—", r.Currency, r.Count, r.Amount, r.Usd))
            .OrderBy(x => x.Kind).ThenBy(x => x.ExpenseType).ToList();
    }

    private async Task<IReadOnlyList<CustodyOpenProviderCharge>> OpenProviderChargesAsync(DateTime cutoff, CancellationToken ct)
    {
        var charges = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.Kind == CustodyFinanceKind.ProviderCharge && e.Status == CustodyDocumentStatus.Posted
                && e.EntryDate < cutoff && e.ServiceProviderId != null)
            .Select(e => new
            {
                e.Id, ProviderId = e.ServiceProviderId!.Value, ProviderName = e.ServiceProvider!.Name, e.Currency,
                Amount = e.Amount ?? e.AmountUsd,
                Paid = db.CustodyFinanceEntries.Where(p => p.SettlesFinanceEntryId == e.Id && p.Kind == CustodyFinanceKind.ProviderPayment
                    && p.Status == CustodyDocumentStatus.Posted && p.EntryDate < cutoff).Sum(p => (decimal?)(p.Amount ?? p.AmountUsd)) ?? 0m
            })
            .ToListAsync(ct);
        // به ارزِ خودِ مصرف: پرداختِ افغانی به نرخ روزِ دیگر، ماندهٔ افغانی را درست کم می‌کند.
        return charges.Where(c => c.Amount - c.Paid != 0m)
            .GroupBy(c => new { c.ProviderId, c.ProviderName, c.Currency })
            .Select(g => new CustodyOpenProviderCharge(g.Key.ProviderId, g.Key.ProviderName, g.Count(), g.Sum(c => c.Amount - c.Paid), g.Key.Currency))
            .OrderBy(x => x.Name).ThenBy(x => x.Currency).ToList();
    }

    private async Task<CustodyConversionTotals> ConversionTotalsAsync(DateTime cutoff, CancellationToken ct)
    {
        var rows = await db.CustodyConversions.AsNoTracking()
            .Where(c => c.Status == CustodyDocumentStatus.Posted && c.ConversionDate < cutoff)
            .OrderBy(c => c.ConversionDate).ThenBy(c => c.Id)
            .Select(c => new { c.Id, Lot = c.CustodyLot!.LotNumber, c.ContractId, c.Contract!.ContractNumber, c.ConversionDate, c.QuantityMt,
                Price = c.Contract.UnitPriceUsd ?? 0m })
            .ToListAsync(ct);
        return new CustodyConversionTotals(rows.Count, rows.Sum(r => r.QuantityMt), rows.Sum(r => decimal.Round(r.QuantityMt * r.Price, 2, MidpointRounding.AwayFromZero)),
            rows.Select(r => new CustodyConversionLine(r.Id, r.Lot, r.ContractId, r.ContractNumber, r.ConversionDate, r.QuantityMt, r.Price)).ToList());
    }
}
