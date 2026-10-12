using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Parties;

namespace PTGOilSystem.Web.Services.CommodityLoans;

public sealed record CommodityLoanRow(
    int Id,
    string LoanNumber,
    CommodityLoanDirection Direction,
    AccountingPartyType CounterpartyType,
    int CounterpartyId,
    string CounterpartyName,
    int ProductId,
    string ProductName,
    string QualitySpec,
    decimal QuantityMt,
    decimal SettledMt,
    DateTime LoanDate,
    DateTime DueDate,
    CommodityLoanStatus Status,
    decimal? CarryingValueUsd,
    decimal? SettledCarryingUsd,
    string Location,
    string AgreementReference)
{
    public decimal OutstandingMt => Status == CommodityLoanStatus.Active ? QuantityMt - SettledMt : 0m;
    public decimal? OutstandingValueUsd => Status == CommodityLoanStatus.Active && CarryingValueUsd.HasValue
        ? CarryingValueUsd - (SettledCarryingUsd ?? 0m) : null;
    public bool IsSettled => Status == CommodityLoanStatus.Active && OutstandingMt == 0m;
    public bool IsOverdue(DateTime today) => OutstandingMt > 0m && DueDate.Date < today.Date;
}

public sealed record CommodityLoanSettlementRow(
    int Id,
    CommodityLoanSettlementKind Kind,
    DateTime SettlementDate,
    decimal QuantityMt,
    string? Location,
    decimal? CarryingValueUsd,
    decimal? InventoryValueUsd,
    decimal? CashAmountUsd,
    string? CashAccountName,
    decimal? DifferenceUsd,
    string? DocumentNumber,
    string? Notes,
    CommodityLoanStatus Status,
    string? CancellationReason);

public sealed record CommodityLoanDetail(
    CommodityLoan Loan,
    CommodityLoanRow Row,
    IReadOnlyList<CommodityLoanSettlementRow> Settlements,
    string? LoanAccountName,
    string? DifferenceAccountName);

/// <summary>طلب و بدهیِ جنسی با یک طرف و یک جنس (مقدار و ارزش دفتریِ باز).</summary>
public sealed record CommodityLoanPosition(
    string CounterpartyName,
    string ProductName,
    decimal LentOutstandingMt,
    decimal BorrowedOutstandingMt,
    decimal LentValueUsd,
    decimal BorrowedValueUsd,
    int OverdueLoans,
    int UnvaluedLoans);

/// <summary>تطبیقِ ارزش دفتریِ باز با ماندهٔ همان حساب در ژورنال‌های قرض جنسی.</summary>
public sealed record CommodityLoanGlCheck(int AccountId, string AccountName, decimal OpenCarryingUsd, decimal GlBalanceUsd)
{
    public decimal DifferenceUsd => OpenCarryingUsd - GlBalanceUsd;
}

public sealed record CommodityLoanReport(
    DateTime AsOf,
    IReadOnlyList<CommodityLoanPosition> Positions,
    IReadOnlyList<CommodityLoanGlCheck> GlChecks,
    decimal SettlementGainUsd,
    decimal SettlementLossUsd)
{
    public decimal LentOutstandingMt => Positions.Sum(x => x.LentOutstandingMt);
    public decimal BorrowedOutstandingMt => Positions.Sum(x => x.BorrowedOutstandingMt);
    public int OverdueLoans => Positions.Sum(x => x.OverdueLoans);
}

public sealed class CommodityLoanReadService(ApplicationDbContext db, IPartyDirectory parties, ISystemCompanyProvider company)
{
    public async Task<IReadOnlyList<CommodityLoanRow>> ListAsync(CommodityLoanDirection? direction = null, bool includeCancelled = false,
        DateTime? asOf = null, CancellationToken ct = default)
    {
        var query = db.CommodityLoans.AsNoTracking().AsQueryable();
        if (direction.HasValue) query = query.Where(l => l.Direction == direction);
        if (!includeCancelled) query = query.Where(l => l.Status == CommodityLoanStatus.Active);
        var cutoff = asOf.HasValue ? DateTime.SpecifyKind(asOf.Value.Date.AddDays(1), DateTimeKind.Utc) : (DateTime?)null;
        if (cutoff.HasValue) query = query.Where(l => l.LoanDate < cutoff);
        var rows = await query
            .OrderByDescending(l => l.LoanDate).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id, l.LoanNumber, l.Direction, l.CounterpartyType, l.CounterpartyId, l.ProductId,
                ProductName = l.Product!.NamePersian ?? l.Product.Name, l.QualitySpec, l.QuantityMt, l.LoanDate, l.DueDate, l.Status,
                l.CarryingValueUsd, l.AgreementReference,
                Terminal = db.Terminals.Where(t => t.Id == l.TerminalId).Select(t => t.Name).FirstOrDefault(),
                Tank = db.StorageTanks.Where(t => t.Id == l.StorageTankId).Select(t => t.DisplayName ?? t.TankCode).FirstOrDefault(),
                Settled = l.Settlements.Where(s => s.Status == CommodityLoanStatus.Active && (cutoff == null || s.SettlementDate < cutoff))
                    .Sum(s => (decimal?)s.QuantityMt) ?? 0m,
                SettledValue = l.Settlements.Where(s => s.Status == CommodityLoanStatus.Active && (cutoff == null || s.SettlementDate < cutoff))
                    .Sum(s => s.CarryingValueUsd)
            })
            .ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => (r.CounterpartyType, r.CounterpartyId)), ct);
        return rows.Select(r => new CommodityLoanRow(r.Id, r.LoanNumber, r.Direction, r.CounterpartyType, r.CounterpartyId,
                names.GetValueOrDefault((r.CounterpartyType, r.CounterpartyId), "-"), r.ProductId, r.ProductName, r.QualitySpec,
                r.QuantityMt, r.Settled, r.LoanDate, r.DueDate, r.Status, r.CarryingValueUsd, r.SettledValue,
                r.Tank is null ? r.Terminal ?? "-" : $"{r.Terminal} / {r.Tank}", r.AgreementReference))
            .ToList();
    }

    public async Task<CommodityLoanDetail?> GetAsync(int id, CancellationToken ct = default)
    {
        var loan = await db.CommodityLoans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loan is null) return null;
        var row = (await ListAsync(includeCancelled: true, ct: ct)).Single(r => r.Id == id);
        var settlements = await db.CommodityLoanSettlements.AsNoTracking()
            .Where(s => s.CommodityLoanId == id)
            .OrderByDescending(s => s.Id)
            .Select(s => new CommodityLoanSettlementRow(s.Id, s.Kind, s.SettlementDate, s.QuantityMt,
                s.TerminalId == null ? null
                    : db.Terminals.Where(t => t.Id == s.TerminalId).Select(t => t.Name).FirstOrDefault()
                      + (s.StorageTankId == null ? "" : " / " + db.StorageTanks.Where(t => t.Id == s.StorageTankId).Select(t => t.DisplayName ?? t.TankCode).FirstOrDefault()),
                s.CarryingValueUsd, s.InventoryValueUsd, s.CashAmountUsd,
                db.CashAccounts.Where(a => a.Id == s.CashAccountId).Select(a => a.Name).FirstOrDefault(),
                s.DifferenceUsd, s.DocumentNumber, s.Notes, s.Status, s.CancellationReason))
            .ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == loan.LoanAccountId || a.Id == loan.DifferenceAccountId)
            .ToDictionaryAsync(a => a.Id, a => a.Code + " · " + a.Name, ct);
        return new CommodityLoanDetail(loan, row, settlements,
            loan.LoanAccountId is { } la ? accounts.GetValueOrDefault(la) : null,
            loan.DifferenceAccountId is { } da ? accounts.GetValueOrDefault(da) : null);
    }

    /// <summary>
    /// راپور طلب و بدهی جنسی تا یک تاریخ: مقدار و ارزش دفتریِ باز به تفکیک طرف و جنس، قرض‌های
    /// سررسیدگذشته و تطبیقِ ارزش باز با ماندهٔ حساب‌های قرارداد در ژورنال‌های قرض جنسی.
    /// </summary>
    public async Task<CommodityLoanReport> ReportAsync(DateTime asOf, CancellationToken ct = default)
    {
        var rows = await ListAsync(asOf: asOf, ct: ct);
        var positions = rows
            .Where(r => r.OutstandingMt != 0m)
            .GroupBy(r => new { r.CounterpartyName, r.ProductName })
            .Select(g => new CommodityLoanPosition(g.Key.CounterpartyName, g.Key.ProductName,
                g.Where(r => r.Direction == CommodityLoanDirection.Lend).Sum(r => r.OutstandingMt),
                g.Where(r => r.Direction == CommodityLoanDirection.Borrow).Sum(r => r.OutstandingMt),
                g.Where(r => r.Direction == CommodityLoanDirection.Lend).Sum(r => r.OutstandingValueUsd ?? 0m),
                g.Where(r => r.Direction == CommodityLoanDirection.Borrow).Sum(r => r.OutstandingValueUsd ?? 0m),
                g.Count(r => r.IsOverdue(asOf)),
                g.Count(r => r.CarryingValueUsd is null)))
            .OrderBy(x => x.CounterpartyName).ThenBy(x => x.ProductName)
            .ToList();

        var cutoff = DateTime.SpecifyKind(asOf.Date.AddDays(1), DateTimeKind.Utc);
        var companyId = await company.FindOwnerCompanyIdAsync(ct);
        var openByAccount = await db.CommodityLoans.AsNoTracking()
            .Where(l => l.Status == CommodityLoanStatus.Active && l.LoanAccountId != null && l.CarryingValueUsd != null && l.LoanDate < cutoff)
            .Select(l => new
            {
                AccountId = l.LoanAccountId!.Value, l.Direction, l.CarryingValueUsd,
                Settled = l.Settlements.Where(s => s.Status == CommodityLoanStatus.Active && s.SettlementDate < cutoff).Sum(s => s.CarryingValueUsd) ?? 0m
            })
            .ToListAsync(ct);
        var accountIds = openByAccount.Select(x => x.AccountId).Distinct().ToList();
        var gl = await db.JournalEntryLines.AsNoTracking()
            .Where(l => accountIds.Contains(l.AccountId) && l.JournalEntry!.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted && l.JournalEntry.SourceModule == CommodityLoanService.SourceModule
                && l.JournalEntry.AccountingDate < cutoff)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Balance = g.Sum(l => l.Debit - l.Credit) })
            .ToListAsync(ct);
        var accountNames = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Code + " · " + a.Name, ct);
        var checks = openByAccount.GroupBy(x => x.AccountId)
            .Select(g =>
            {
                // طلب جنسی مانده بدهکار دارد و بدهی جنسی مانده بستانکار؛ هر دو مثبت نمایش داده می‌شوند.
                var lend = g.First().Direction == CommodityLoanDirection.Lend;
                var balance = gl.SingleOrDefault(x => x.AccountId == g.Key)?.Balance ?? 0m;
                return new CommodityLoanGlCheck(g.Key, accountNames.GetValueOrDefault(g.Key, "-"),
                    g.Sum(x => (x.CarryingValueUsd ?? 0m) - x.Settled), lend ? balance : -balance);
            })
            .ToList();

        var differences = await db.CommodityLoanSettlements.AsNoTracking()
            .Where(s => s.Status == CommodityLoanStatus.Active && s.SettlementDate < cutoff && s.DifferenceUsd != null && s.DifferenceUsd != 0m)
            .Select(s => s.DifferenceUsd!.Value)
            .ToListAsync(ct);
        return new CommodityLoanReport(asOf.Date, positions, checks, differences.Where(x => x > 0m).Sum(), -differences.Where(x => x < 0m).Sum());
    }

    private async Task<Dictionary<(AccountingPartyType, int), string>> NamesAsync(IEnumerable<(AccountingPartyType Type, int Id)> owners, CancellationToken ct)
    {
        var distinct = owners.Distinct().ToList();
        var keys = distinct.Select(o => new PartyKey(PartyTypeMap.ToStatement(o.Type), o.Id)).ToList();
        var names = await parties.GetNamesAsync(keys, ct);
        return distinct.ToDictionary(o => o, o => names.GetValueOrDefault(new PartyKey(PartyTypeMap.ToStatement(o.Type), o.Id), "-"));
    }
}
