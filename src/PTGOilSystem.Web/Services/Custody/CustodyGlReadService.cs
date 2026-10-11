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

public sealed record CustodyGlReport(DateTime AsOf, IReadOnlyList<CustodyGlOwnerPosition> Owners,
    decimal UnassignedFundsNetCreditUsd, decimal UnassignedPendingNetCreditUsd,
    decimal AgencyCustomerReceivableUsd, int MissingActiveDocuments, IReadOnlyList<string> Blockers)
{
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
        var legacy = await db.LedgerEntries.AsNoTracking().Where(x => x.SourceType.StartsWith("Custody") && x.EntryDate < cutoff)
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
        var agencyAr = await lines.Where(x => x.AccountId == settings.AccountsReceivableAccountId && x.JournalEntry!.SourceModule == "Custody")
            .SumAsync(x => x.Debit - x.Credit, ct);
        return new(date, rows, unassigned.Sum(x => x.Funds), unassigned.Sum(x => x.Pending), agencyAr, missing, blockers.Distinct().ToList());
    }
}
