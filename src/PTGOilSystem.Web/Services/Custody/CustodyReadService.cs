using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Parties;

namespace PTGOilSystem.Web.Services.Custody;

public interface ICustodyReadService
{
    Task<IReadOnlyList<CustodyLotSummary>> GetLotSummariesAsync(CustodyLotFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<CustodyPosition>> GetPositionsAsync(int lotId, CancellationToken ct = default);
    Task<CustodyLotDetail?> GetLotDetailAsync(int lotId, CancellationToken ct = default);
    Task<IReadOnlyList<PhysicalTankPosition>> GetPhysicalPositionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CompanyStockAtThirdParty>> GetCompanyStockAtThirdPartiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CustodyOwnerOption>> GetOwnerOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CustodyOwnerOption>> GetCustodianOptionsAsync(CancellationToken ct = default);
    Task<string?> GetPartyNameAsync(AccountingPartyType partyType, int partyId, CancellationToken ct = default);
    Task<CustodyOwnerAccount> GetOwnerAccountAsync(CustodyOwner owner, CancellationToken ct = default);
    Task<CustodySaleRow?> GetSaleAsync(int saleId, CancellationToken ct = default);
}

public sealed record CustodyLotFilter(
    AccountingPartyType? OwnerPartyType = null,
    int? OwnerPartyId = null,
    int? ProductId = null,
    bool IncludeEmpty = false);

public sealed record CustodyLotSummary(
    int LotId,
    string LotNumber,
    AccountingPartyType OwnerPartyType,
    int OwnerPartyId,
    string OwnerName,
    int ProductId,
    string ProductName,
    CustodyLotStatus Status,
    decimal BalanceMt,
    decimal HeldMt,
    decimal FreeMt,
    decimal ReceivedMt,
    decimal? LoadedQuantityMt,
    decimal? TransitDifferenceMt,
    string Locations,
    DateTime? LastMovementDate);

public sealed record CustodyPosition(
    int TerminalId,
    string TerminalName,
    int? StorageTankId,
    string? TankCode,
    decimal QuantityMt);

public sealed record CustodyMovementRow(
    int Id,
    Guid OperationKey,
    CustodyMovementKind Kind,
    CustodyDirection Direction,
    DateTime MovementDate,
    decimal QuantityMt,
    string TerminalName,
    string? TankCode,
    string? DocumentNumber,
    string? CounterpartyName,
    string? VehicleReference,
    CustodyLossResponsibility? LossResponsibility,
    string? Notes,
    bool IsReversed,
    bool IsReversal,
    int? RelatedLotId,
    string? RelatedLotNumber);

public sealed record CustodyHoldRow(
    int Id,
    decimal QuantityMt,
    string Reason,
    DateTime PlacedAtUtc,
    DateTime? ReleasedAtUtc,
    string? ReleaseNote);

public sealed record CustodyLotDetail(
    CustodyLotSummary Summary,
    CustodyLot Lot,
    string? ParentLotNumber,
    IReadOnlyList<CustodyPosition> Positions,
    IReadOnlyList<CustodyMovementRow> Movements,
    IReadOnlyList<CustodyHoldRow> Holds);

public sealed record PhysicalOwnerShare(string OwnerName, bool IsCompany, int? LotId, string? LotNumber, decimal QuantityMt);

public sealed record PhysicalTankPosition(
    int TerminalId,
    string TerminalName,
    int? StorageTankId,
    string? TankCode,
    decimal? CapacityMt,
    string ProductName,
    decimal CompanyMt,
    decimal CustodyMt,
    IReadOnlyList<PhysicalOwnerShare> Shares)
{
    public decimal TotalMt => CompanyMt + CustodyMt;
}

public sealed record CompanyStockAtThirdParty(
    int TerminalId,
    string TerminalName,
    string CustodianName,
    string ProductName,
    decimal QuantityMt);

public sealed record CustodyOwnerOption(AccountingPartyType PartyType, int PartyId, string Name);

public sealed record CustodySaleRow(
    int Id,
    string SaleNumber,
    int LotId,
    string LotNumber,
    AccountingPartyType OwnerPartyType,
    int OwnerPartyId,
    DateTime SaleDate,
    int CustomerId,
    string CustomerName,
    string ProductName,
    decimal QuantityMt,
    decimal UnitPriceUsd,
    decimal AmountUsd,
    decimal CollectedByCompanyUsd,
    decimal CollectedByOwnerUsd,
    CustodyCommissionMethod CommissionMethod,
    decimal CommissionRate,
    decimal CommissionUsd,
    decimal CommissionRecognizedUsd,
    CustodyCommissionRecognition CommissionRecognition,
    CustodyDocumentStatus Status,
    string? CancellationReason)
{
    public decimal OutstandingUsd => Status == CustodyDocumentStatus.Posted ? AmountUsd - CollectedByCompanyUsd - CollectedByOwnerUsd : 0m;
}

public sealed record CustodyFinanceRow(
    int Id,
    CustodyFinanceKind Kind,
    DateTime EntryDate,
    decimal AmountUsd,
    decimal CommissionUsd,
    string? SaleNumber,
    int? SaleId,
    string? CashAccountName,
    string? ExpenseTypeName,
    string? PayeeName,
    string? DocumentNumber,
    string? Description,
    CustodyDocumentStatus Status,
    string? CancellationReason);

/// <summary>
/// حساب مالکِ امانی — از خودِ اسناد امانی ساخته می‌شود؛ همان تعریفی که سقفِ «پرداخت به مالک» را می‌سازد
/// (<see cref="CustodySaleService.OwnerFundsBalanceAsync"/>). مبلغ فروش، وصول و مانده جدا نشان داده می‌شوند.
/// </summary>
public sealed record CustodyOwnerAccount(
    CustodyOwner Owner,
    string OwnerName,
    bool SupportsFinance,
    IReadOnlyList<CustodySaleRow> Sales,
    IReadOnlyList<CustodyFinanceRow> Entries,
    decimal FundsBalanceUsd)
{
    private IEnumerable<CustodySaleRow> Posted => Sales.Where(s => s.Status == CustodyDocumentStatus.Posted);
    private decimal Sum(CustodyFinanceKind kind) => Entries.Where(e => e.Kind == kind && e.Status == CustodyDocumentStatus.Posted).Sum(e => e.AmountUsd);

    public decimal SalesTotalUsd => Posted.Sum(s => s.AmountUsd);
    public decimal CollectedByCompanyUsd => Posted.Sum(s => s.CollectedByCompanyUsd);
    public decimal CollectedByOwnerUsd => Posted.Sum(s => s.CollectedByOwnerUsd);
    public decimal OutstandingUsd => Posted.Sum(s => s.OutstandingUsd);
    public decimal CommissionRecognizedUsd => Posted.Sum(s => s.CommissionRecognizedUsd);
    public decimal CommissionPendingUsd => Posted.Sum(s => s.CommissionUsd - s.CommissionRecognizedUsd);
    public decimal ExpensesUsd => Sum(CustodyFinanceKind.ReimbursableExpense);
    public decimal ServiceFeesUsd => Sum(CustodyFinanceKind.ServiceFee);
    public decimal PayoutsUsd => Sum(CustodyFinanceKind.OwnerPayout);
}

/// <summary>
/// خوانندهٔ دفتر امانی. موجودی ملکی فقط از <see cref="IStockService"/> خوانده می‌شود؛ اینجا
/// هیچ فرمولِ موازی برای موجودی شرکت نوشته نشده است.
/// </summary>
public sealed class CustodyReadService(
    ApplicationDbContext db,
    IStockService stock,
    IPartyDirectory parties) : ICustodyReadService
{
    public async Task<IReadOnlyList<CustodyLotSummary>> GetLotSummariesAsync(CustodyLotFilter filter, CancellationToken ct = default)
    {
        var lots = db.CustodyLots.AsNoTracking().Where(l => l.Status != CustodyLotStatus.Cancelled);
        if (filter.OwnerPartyType.HasValue) lots = lots.Where(l => l.OwnerPartyType == filter.OwnerPartyType.Value);
        if (filter.OwnerPartyId.HasValue) lots = lots.Where(l => l.OwnerPartyId == filter.OwnerPartyId.Value);
        if (filter.ProductId.HasValue) lots = lots.Where(l => l.ProductId == filter.ProductId.Value);

        var rows = await lots
            .Select(l => new
            {
                l.Id, l.LotNumber, l.OwnerPartyType, l.OwnerPartyId, l.ProductId,
                ProductName = l.Product!.NamePersian ?? l.Product.Name,
                l.Status, l.LoadedQuantityMt, l.ParentCustodyLotId,
                Balance = l.Movements.Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt),
                Received = l.Movements.Where(m => m.Kind == CustodyMovementKind.Receipt)
                    .Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt),
                Held = l.Holds.Where(h => h.ReleasedAtUtc == null).Sum(h => h.QuantityMt),
                LastMovement = l.Movements.Max(m => (DateTime?)m.MovementDate)
            })
            .OrderByDescending(l => l.Id)
            .ToListAsync(ct);

        if (!filter.IncludeEmpty)
            rows = rows.Where(r => r.Balance != 0m || r.Status == CustodyLotStatus.InTransit).ToList();

        var lotIds = rows.Select(r => r.Id).ToArray();
        var locations = await db.CustodyMovements.AsNoTracking()
            .Where(m => lotIds.Contains(m.CustodyLotId))
            .GroupBy(m => new { m.CustodyLotId, TerminalName = m.Terminal!.Name, TankCode = m.StorageTank != null ? m.StorageTank.TankCode : null })
            .Select(g => new
            {
                g.Key.CustodyLotId, g.Key.TerminalName, g.Key.TankCode,
                Qty = g.Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt)
            })
            .ToListAsync(ct);
        var locationText = locations.Where(x => x.Qty != 0m)
            .GroupBy(x => x.CustodyLotId)
            .ToDictionary(g => g.Key, g => string.Join("، ", g.Select(x => x.TankCode is null ? x.TerminalName : $"{x.TerminalName} / {x.TankCode}")));

        var names = await OwnerNamesAsync(rows.Select(r => (r.OwnerPartyType, r.OwnerPartyId)), ct);
        return rows.Select(r => new CustodyLotSummary(
                r.Id, r.LotNumber, r.OwnerPartyType, r.OwnerPartyId,
                names.GetValueOrDefault((r.OwnerPartyType, r.OwnerPartyId), "-"),
                r.ProductId, r.ProductName, r.Status,
                r.Balance, r.Held, Math.Max(r.Balance - r.Held, 0m), r.Received,
                r.LoadedQuantityMt,
                r.LoadedQuantityMt.HasValue && r.ParentCustodyLotId == null && r.Status != CustodyLotStatus.InTransit
                    ? r.LoadedQuantityMt.Value - r.Received
                    : null,
                locationText.GetValueOrDefault(r.Id, ""),
                r.LastMovement))
            .ToList();
    }

    public async Task<IReadOnlyList<CustodyPosition>> GetPositionsAsync(int lotId, CancellationToken ct = default)
    {
        var rows = await db.CustodyMovements.AsNoTracking()
            .Where(m => m.CustodyLotId == lotId)
            .GroupBy(m => new { m.TerminalId, TerminalName = m.Terminal!.Name, m.StorageTankId, TankCode = m.StorageTank != null ? m.StorageTank.TankCode : null })
            .Select(g => new
            {
                g.Key.TerminalId, g.Key.TerminalName, g.Key.StorageTankId, g.Key.TankCode,
                Qty = g.Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt)
            })
            .ToListAsync(ct);
        return rows.Where(r => r.Qty != 0m)
            .OrderBy(r => r.TerminalName).ThenBy(r => r.TankCode)
            .Select(r => new CustodyPosition(r.TerminalId, r.TerminalName, r.StorageTankId, r.TankCode, r.Qty))
            .ToList();
    }

    public async Task<CustodyLotDetail?> GetLotDetailAsync(int lotId, CancellationToken ct = default)
    {
        var lot = await db.CustodyLots.AsNoTracking()
            .Include(l => l.Product)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null) return null;

        var summary = (await GetLotSummariesAsync(
                new CustodyLotFilter(lot.OwnerPartyType, lot.OwnerPartyId, lot.ProductId, IncludeEmpty: true), ct))
            .FirstOrDefault(s => s.LotId == lotId);
        if (summary is null)
        {
            var name = (await OwnerNamesAsync([(lot.OwnerPartyType, lot.OwnerPartyId)], ct))
                .GetValueOrDefault((lot.OwnerPartyType, lot.OwnerPartyId), "-");
            summary = new CustodyLotSummary(lot.Id, lot.LotNumber, lot.OwnerPartyType, lot.OwnerPartyId, name,
                lot.ProductId, lot.Product?.NamePersian ?? lot.Product?.Name ?? "-", lot.Status,
                0m, 0m, 0m, 0m, lot.LoadedQuantityMt, null, "", null);
        }

        var movements = await db.CustodyMovements.AsNoTracking()
            .Where(m => m.CustodyLotId == lotId)
            .OrderBy(m => m.MovementDate).ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id, m.OperationKey, m.Kind, m.Direction, m.MovementDate, m.QuantityMt,
                TerminalName = m.Terminal!.Name,
                TankCode = m.StorageTank != null ? m.StorageTank.TankCode : null,
                m.DocumentNumber, m.CounterpartyName, m.VehicleReference, m.LossResponsibility, m.Notes,
                m.IsReversed, IsReversal = m.ReversalOfCustodyMovementId != null
            })
            .ToListAsync(ct);

        // طرفِ دیگرِ انتقال مالکیت (بارِ مبدأ یا مقصد) برای پیوند در تاریخچه.
        var operationKeys = movements.Where(m => m.Kind == CustodyMovementKind.OwnershipTransfer && !m.IsReversal)
            .Select(m => m.OperationKey).Distinct().ToArray();
        var related = await db.CustodyMovements.AsNoTracking()
            .Where(m => operationKeys.Contains(m.OperationKey) && m.CustodyLotId != lotId)
            .Select(m => new { m.OperationKey, m.CustodyLotId, m.CustodyLot!.LotNumber })
            .ToListAsync(ct);
        var relatedByKey = related.GroupBy(r => r.OperationKey).ToDictionary(g => g.Key, g => g.First());

        var holds = await db.CustodyHolds.AsNoTracking()
            .Where(h => h.CustodyLotId == lotId)
            .OrderByDescending(h => h.Id)
            .Select(h => new CustodyHoldRow(h.Id, h.QuantityMt, h.Reason, h.CreatedAtUtc, h.ReleasedAtUtc, h.ReleaseNote))
            .ToListAsync(ct);

        string? parentNumber = null;
        if (lot.ParentCustodyLotId.HasValue)
        {
            parentNumber = await db.CustodyLots.AsNoTracking()
                .Where(l => l.Id == lot.ParentCustodyLotId.Value)
                .Select(l => l.LotNumber).FirstOrDefaultAsync(ct);
        }

        return new CustodyLotDetail(
            summary,
            lot,
            parentNumber,
            await GetPositionsAsync(lotId, ct),
            movements.Select(m =>
            {
                relatedByKey.TryGetValue(m.OperationKey, out var other);
                return new CustodyMovementRow(m.Id, m.OperationKey, m.Kind, m.Direction, m.MovementDate, m.QuantityMt,
                    m.TerminalName, m.TankCode, m.DocumentNumber, m.CounterpartyName, m.VehicleReference,
                    m.LossResponsibility, m.Notes, m.IsReversed, m.IsReversal,
                    other?.CustodyLotId, other?.LotNumber);
            }).ToList(),
            holds);
    }

    public async Task<IReadOnlyList<PhysicalTankPosition>> GetPhysicalPositionsAsync(CancellationToken ct = default)
    {
        // فقط تأسیساتِ خودِ ما. تیلِ ملکی در مخزنِ دیگران در بخشِ «تیل ما نزد دیگران» است.
        var terminals = await db.Terminals.AsNoTracking()
            .Where(t => t.CustodianPartyType == null)
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var tanks = await db.StorageTanks.AsNoTracking()
            .Where(t => terminals.Keys.Contains(t.TerminalId))
            .Select(t => new { t.Id, t.TankCode, t.CapacityMt })
            .ToDictionaryAsync(t => t.Id, ct);
        var products = await db.Products.AsNoTracking()
            .Select(p => new { p.Id, Name = p.NamePersian ?? p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        // کلیدهای محل/جنس که موجودی ملکی دارند — مقدار از خودِ StockService خوانده می‌شود.
        var companyKeys = await db.InventoryMovements.AsNoTracking()
            .Where(m => terminals.Keys.Contains(m.TerminalId))
            .Select(m => new { m.TerminalId, m.StorageTankId, m.ProductId })
            .Distinct()
            .ToListAsync(ct);

        var custody = await db.CustodyMovements.AsNoTracking()
            .Where(m => terminals.Keys.Contains(m.TerminalId))
            .GroupBy(m => new { m.TerminalId, m.StorageTankId, m.CustodyLot!.ProductId, m.CustodyLotId, m.CustodyLot.LotNumber, m.CustodyLot.OwnerPartyType, m.CustodyLot.OwnerPartyId })
            .Select(g => new
            {
                g.Key.TerminalId, g.Key.StorageTankId, g.Key.ProductId, g.Key.CustodyLotId, g.Key.LotNumber,
                g.Key.OwnerPartyType, g.Key.OwnerPartyId,
                Qty = g.Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt)
            })
            .ToListAsync(ct);
        custody = custody.Where(c => c.Qty != 0m).ToList();
        var names = await OwnerNamesAsync(custody.Select(c => (c.OwnerPartyType, c.OwnerPartyId)), ct);

        var company = new Dictionary<(int TerminalId, int? TankId, int ProductId), decimal>();
        foreach (var group in companyKeys.GroupBy(k => new { k.TerminalId, k.ProductId }))
        {
            var tankIds = group.Where(k => k.StorageTankId.HasValue).Select(k => k.StorageTankId!.Value).Distinct().ToList();
            var tankTotal = 0m;
            foreach (var tankId in tankIds)
            {
                var qty = await stock.GetFreeQuantityMtAsync(group.Key.ProductId, terminalId: group.Key.TerminalId, storageTankId: tankId, ct: ct);
                tankTotal += qty;
                if (qty != 0m) company[(group.Key.TerminalId, tankId, group.Key.ProductId)] = qty;
            }
            var terminalTotal = await stock.GetFreeQuantityMtAsync(group.Key.ProductId, terminalId: group.Key.TerminalId, ct: ct);
            var withoutTank = terminalTotal - tankTotal;
            if (withoutTank != 0m) company[(group.Key.TerminalId, null, group.Key.ProductId)] = withoutTank;
        }

        var keys = company.Keys
            .Concat(custody.Select(c => (c.TerminalId, c.StorageTankId, c.ProductId)))
            .Distinct()
            .ToList();

        return keys
            .Select(key =>
            {
                var companyQty = company.GetValueOrDefault(key);
                var shares = custody.Where(c => c.TerminalId == key.Item1 && c.StorageTankId == key.Item2 && c.ProductId == key.Item3)
                    .Select(c => new PhysicalOwnerShare(
                        names.GetValueOrDefault((c.OwnerPartyType, c.OwnerPartyId), "-"), false, c.CustodyLotId, c.LotNumber, c.Qty))
                    .ToList();
                if (companyQty != 0m)
                    shares.Insert(0, new PhysicalOwnerShare("ملکِ شرکت", true, null, null, companyQty));
                tanks.TryGetValue(key.Item2 ?? 0, out var tank);
                return new PhysicalTankPosition(
                    key.Item1,
                    terminals.GetValueOrDefault(key.Item1, "-"),
                    key.Item2,
                    tank?.TankCode,
                    tank is not null && tank.CapacityMt > 0m ? tank.CapacityMt : (decimal?)null,
                    products.GetValueOrDefault(key.Item3, "-"),
                    companyQty,
                    shares.Where(s => !s.IsCompany).Sum(s => s.QuantityMt),
                    shares);
            })
            .OrderBy(p => p.TerminalName).ThenBy(p => p.TankCode).ThenBy(p => p.ProductName)
            .ToList();
    }

    public async Task<IReadOnlyList<CompanyStockAtThirdParty>> GetCompanyStockAtThirdPartiesAsync(CancellationToken ct = default)
    {
        var terminals = await db.Terminals.AsNoTracking()
            .Where(t => t.CustodianPartyType != null && t.CustodianPartyId != null)
            .Select(t => new { t.Id, t.Name, t.CustodianPartyType, t.CustodianPartyId })
            .ToListAsync(ct);
        if (terminals.Count == 0) return [];

        var names = await OwnerNamesAsync(terminals.Select(t => (t.CustodianPartyType!.Value, t.CustodianPartyId!.Value)), ct);
        var result = new List<CompanyStockAtThirdParty>();
        foreach (var terminal in terminals)
        {
            var summary = await stock.GetStockSummaryAsync(terminalId: terminal.Id, ct: ct);
            foreach (var product in summary.GroupBy(s => s.ProductName))
            {
                var qty = product.Sum(s => s.FreeQuantityMt);
                if (qty == 0m) continue;
                result.Add(new CompanyStockAtThirdParty(
                    terminal.Id, terminal.Name,
                    names.GetValueOrDefault((terminal.CustodianPartyType!.Value, terminal.CustodianPartyId!.Value), "-"),
                    product.Key, qty));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<CustodyOwnerOption>> GetOwnerOptionsAsync(CancellationToken ct = default)
    {
        var customers = await db.Customers.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new CustodyOwnerOption(AccountingPartyType.Customer, x.Id, x.NamePersian ?? x.Name)).ToListAsync(ct);
        var suppliers = await db.Suppliers.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new CustodyOwnerOption(AccountingPartyType.Supplier, x.Id, x.NamePersian ?? x.Name)).ToListAsync(ct);
        var partners = await db.Partners.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new CustodyOwnerOption(AccountingPartyType.Partner, x.Id, x.NamePersian ?? x.Name)).ToListAsync(ct);
        var companies = await db.Companies.AsNoTracking().Where(x => x.IsActive && !x.IsSystemOwner)
            .Select(x => new CustodyOwnerOption(AccountingPartyType.Company, x.Id, x.NamePersian ?? x.Name)).ToListAsync(ct);
        return companies.Concat(customers).Concat(suppliers).Concat(partners).ToList();
    }

    public async Task<IReadOnlyList<CustodyOwnerOption>> GetCustodianOptionsAsync(CancellationToken ct = default)
    {
        var providers = await db.ServiceProviders.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new CustodyOwnerOption(AccountingPartyType.ServiceProvider, x.Id, x.Name)).ToListAsync(ct);
        return (await GetOwnerOptionsAsync(ct)).Concat(providers).ToList();
    }

    public async Task<CustodyOwnerAccount> GetOwnerAccountAsync(CustodyOwner owner, CancellationToken ct = default)
    {
        var name = await GetPartyNameAsync(owner.PartyType, owner.PartyId, ct) ?? "-";
        var sales = await SaleRowsAsync(db.CustodySales.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId), ct);
        var entries = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId)
            .OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id)
            .Select(e => new CustodyFinanceRow(
                e.Id, e.Kind, e.EntryDate, e.AmountUsd, e.CommissionUsd,
                e.CustodySale != null ? e.CustodySale.SaleNumber : null, e.CustodySaleId,
                e.CashAccount != null ? e.CashAccount.Name : null,
                e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : null,
                e.PayeeName, e.DocumentNumber, e.Description, e.Status, e.CancellationReason))
            .ToListAsync(ct);
        return new CustodyOwnerAccount(
            owner,
            name,
            CustodySaleService.FinancialOwnerTypes.Contains(owner.PartyType),
            sales,
            entries,
            await CustodySaleService.OwnerFundsBalanceAsync(db, owner, ct));
    }

    public async Task<CustodySaleRow?> GetSaleAsync(int saleId, CancellationToken ct = default)
        => (await SaleRowsAsync(db.CustodySales.AsNoTracking().Where(s => s.Id == saleId), ct)).FirstOrDefault();

    private async Task<List<CustodySaleRow>> SaleRowsAsync(IQueryable<CustodySale> query, CancellationToken ct)
    {
        var rows = await query
            .OrderByDescending(s => s.SaleDate).ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id, s.SaleNumber, s.CustodyLotId, LotNumber = s.CustodyLot!.LotNumber, s.OwnerPartyType, s.OwnerPartyId,
                s.SaleDate, s.CustomerId, CustomerName = s.Customer!.NamePersian ?? s.Customer.Name,
                ProductName = db.Products.Where(p => p.Id == s.ProductId).Select(p => p.NamePersian ?? p.Name).FirstOrDefault(),
                s.QuantityMt, s.UnitPriceUsd, s.AmountUsd, s.CommissionMethod, s.CommissionRate, s.CommissionUsd,
                s.CommissionRecognizedAtSaleUsd, s.CommissionRecognition, s.Status, s.CancellationReason
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToArray();
        var collected = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.CustodySaleId != null && ids.Contains(e.CustodySaleId.Value) && e.Status == CustodyDocumentStatus.Posted)
            .GroupBy(e => new { SaleId = e.CustodySaleId!.Value, e.Kind })
            .Select(g => new { g.Key.SaleId, g.Key.Kind, Amount = g.Sum(e => e.AmountUsd), Commission = g.Sum(e => e.CommissionUsd) })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var mine = collected.Where(c => c.SaleId == r.Id).ToList();
            return new CustodySaleRow(
                r.Id, r.SaleNumber, r.CustodyLotId, r.LotNumber, r.OwnerPartyType, r.OwnerPartyId, r.SaleDate,
                r.CustomerId, r.CustomerName, r.ProductName ?? "-", r.QuantityMt, r.UnitPriceUsd, r.AmountUsd,
                mine.Where(c => c.Kind == CustodyFinanceKind.Collection).Sum(c => c.Amount),
                mine.Where(c => c.Kind == CustodyFinanceKind.DirectToOwner).Sum(c => c.Amount),
                r.CommissionMethod, r.CommissionRate, r.CommissionUsd,
                r.CommissionRecognizedAtSaleUsd + mine.Sum(c => c.Commission),
                r.CommissionRecognition, r.Status, r.CancellationReason);
        }).ToList();
    }

    public async Task<string?> GetPartyNameAsync(AccountingPartyType partyType, int partyId, CancellationToken ct = default)
        => (await OwnerNamesAsync([(partyType, partyId)], ct)).GetValueOrDefault((partyType, partyId));

    private async Task<Dictionary<(AccountingPartyType, int), string>> OwnerNamesAsync(
        IEnumerable<(AccountingPartyType Type, int Id)> owners,
        CancellationToken ct)
    {
        var distinct = owners.Distinct().ToList();
        var keys = distinct.Select(o => new PartyKey(PartyTypeMap.ToStatement(o.Type), o.Id)).ToList();
        var names = await parties.GetNamesAsync(keys, ct);
        return distinct.ToDictionary(
            o => o,
            o => names.GetValueOrDefault(new PartyKey(PartyTypeMap.ToStatement(o.Type), o.Id), "-"));
    }
}
