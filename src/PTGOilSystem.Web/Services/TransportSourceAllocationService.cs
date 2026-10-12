using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

public sealed record TransportSourceShare(
    int SourcePurchaseContractId,
    decimal QuantityMt,
    int? SourceLoadingReceiptId = null,
    int? SourceInventoryMovementId = null,
    int? SourceTransportLegId = null,
    int? SourceTransportReceiptId = null);

public sealed record TransportSourcePlan(
    IReadOnlyList<TransportSourceShare> Shares,
    int? SingleContractId,
    int? SingleCompanyId)
{
    public static TransportSourcePlan Empty { get; } = new([], null, null);
}

public sealed record SaleSourceAllocationWrite(
    SalesTransaction Sale,
    TransportSourcePlan Plan,
    int? TransportLegId = null);

public sealed record LossSourceAllocationWrite(
    LossEvent LossEvent,
    TransportSourcePlan Plan,
    int? TransportLegId = null);

public interface ITransportSourceAllocationService
{
    Task<TransportSourcePlan> BuildFromLegAsync(
        int transportLegId,
        decimal quantityMt,
        CancellationToken ct = default);

    Task<TransportSourcePlan> BuildFromInventoryMovementsAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        decimal quantityMt,
        CancellationToken ct = default);

    Task<TransportSourcePlan> BuildFromSaleAsync(
        int salesTransactionId,
        decimal quantityMt,
        CancellationToken ct = default);

    void ApplyLegacyHeader(SalesTransaction sale, TransportSourcePlan plan);
    void ApplyLegacyHeader(LossEvent lossEvent, TransportSourcePlan plan);

    Task PersistSaleAsync(
        SalesTransaction sale,
        TransportSourcePlan plan,
        int? transportLegId = null,
        CancellationToken ct = default);

    Task PersistSaleBatchAsync(
        IReadOnlyCollection<SaleSourceAllocationWrite> writes,
        CancellationToken ct = default);

    /// <summary>
    /// مبلغ سهم‌های منبعِ یک فروش را از TotalUsd فعلی فروش و با همان نسبت مقدار دوباره تقسیم
    /// می‌کند. فقط برای فروشی که با قیمت باز ثبت شده و اکنون قیمت گرفته است؛ مقدار سهم‌ها
    /// دست نمی‌خورد. تغییرات ردیابی می‌شوند و با SaveChanges فراخواننده ذخیره می‌شوند.
    /// </summary>
    Task RepriceSaleAsync(SalesTransaction sale, CancellationToken ct = default);

    Task PersistLossAsync(
        LossEvent lossEvent,
        TransportSourcePlan plan,
        int? transportLegId = null,
        CancellationToken ct = default);

    Task PersistLossBatchAsync(
        IReadOnlyCollection<LossSourceAllocationWrite> writes,
        CancellationToken ct = default);

    Task<int?> ResolveCurrentLegIdAsync(
        TruckDispatch dispatch,
        CancellationToken ct = default);
}

/// <summary>
/// تبدیل سهم‌های منبع حمل/موجودی به نسب‌نامهٔ نتیجه‌های مقدار (فروش و کسری).
/// این سرویس سند تجاری، دفترکل یا حرکت موجودی تازه نمی‌سازد؛ فقط یک سند موجود را به
/// چند قرارداد منبع وصل می‌کند و فیلد تک‌قراردادی قدیمی را وقتی دقیقاً یک منبع وجود دارد پر می‌کند.
/// </summary>
public sealed class TransportSourceAllocationService : ITransportSourceAllocationService
{
    private const decimal Epsilon = 0.0001m;
    private readonly ApplicationDbContext _db;

    public TransportSourceAllocationService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<TransportSourcePlan> BuildFromLegAsync(
        int transportLegId,
        decimal quantityMt,
        CancellationToken ct = default)
    {
        if (quantityMt <= 0m)
        {
            return TransportSourcePlan.Empty;
        }

        var sourceRows = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.InventoryTransportLegId == transportLegId && a.QuantityMt > 0m)
            .Select(a => new TransportSourceShare(
                a.SourcePurchaseContractId,
                a.QuantityMt,
                a.SourceLoadingReceiptId,
                a.SourceInventoryMovementId,
                a.SourceTransportLegId,
                a.SourceTransportReceiptId))
            .ToListAsync(ct);

        if (sourceRows.Count == 0)
        {
            var fallbackContractId = await _db.InventoryTransportLegs
                .AsNoTracking()
                .Where(l => l.Id == transportLegId)
                .Select(l => (int?)l.SourcePurchaseContractId)
                .FirstOrDefaultAsync(ct);

            if (!fallbackContractId.HasValue)
            {
                return TransportSourcePlan.Empty;
            }

            sourceRows.Add(new TransportSourceShare(fallbackContractId.Value, quantityMt));
        }

        return await BuildPlanAsync(sourceRows, quantityMt, ct);
    }

    public Task<TransportSourcePlan> BuildFromInventoryMovementsAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        decimal quantityMt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(movements);

        var rows = movements
            .Where(m => m.ContractId.HasValue && m.QuantityMt > 0m)
            .Select(m => new TransportSourceShare(
                m.ContractId!.Value,
                m.QuantityMt,
                m.LoadingReceiptId,
                m.Id > 0 ? m.Id : null))
            .ToList();

        return BuildPlanAsync(rows, quantityMt, ct);
    }

    public async Task<TransportSourcePlan> BuildFromSaleAsync(
        int salesTransactionId,
        decimal quantityMt,
        CancellationToken ct = default)
    {
        if (quantityMt <= 0m)
        {
            return TransportSourcePlan.Empty;
        }

        var rows = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => a.SalesTransactionId == salesTransactionId && a.QuantityMt > 0m)
            .Select(a => new TransportSourceShare(
                a.SourcePurchaseContractId,
                a.QuantityMt,
                a.SourceLoadingReceiptId,
                a.SourceInventoryMovementId,
                a.SourceTransportLegId,
                a.SourceTransportReceiptId))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            var legacyContractId = await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => s.Id == salesTransactionId)
                .Select(s => s.SourcePurchaseContractId)
                .FirstOrDefaultAsync(ct);
            if (legacyContractId.HasValue)
            {
                rows.Add(new TransportSourceShare(legacyContractId.Value, quantityMt));
            }
        }

        return await BuildPlanAsync(rows, quantityMt, ct);
    }

    public void ApplyLegacyHeader(SalesTransaction sale, TransportSourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(plan);
        sale.SourcePurchaseContractId = plan.SingleContractId;
        sale.CompanyId = plan.SingleCompanyId;
    }

    public void ApplyLegacyHeader(LossEvent lossEvent, TransportSourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(lossEvent);
        ArgumentNullException.ThrowIfNull(plan);
        lossEvent.ContractId = plan.SingleContractId;
    }

    public async Task PersistSaleAsync(
        SalesTransaction sale,
        TransportSourcePlan plan,
        int? transportLegId = null,
        CancellationToken ct = default)
    {
        await PersistSaleBatchAsync([new SaleSourceAllocationWrite(sale, plan, transportLegId)], ct);
    }

    public async Task RepriceSaleAsync(SalesTransaction sale, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        var allocations = await _db.SalesTransactionSourceAllocations
            .Where(a => a.SalesTransactionId == sale.Id)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);
        if (allocations.Count == 0)
        {
            return;
        }

        var amounts = SplitAmount(sale.TotalUsd, allocations.Select(a => a.QuantityMt).ToList());
        for (var index = 0; index < allocations.Count; index++)
        {
            allocations[index].AmountUsd = amounts[index];
        }
    }

    public async Task PersistSaleBatchAsync(
        IReadOnlyCollection<SaleSourceAllocationWrite> writes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var candidates = writes
            .Where(w => w.Sale.Id > 0 && w.Plan.Shares.Count > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var saleIds = candidates.Select(w => w.Sale.Id).Distinct().ToList();
        var existingIds = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => saleIds.Contains(a.SalesTransactionId))
            .Select(a => a.SalesTransactionId)
            .Distinct()
            .ToListAsync(ct);
        var existing = existingIds.ToHashSet();

        var allocations = new List<SalesTransactionSourceAllocation>();
        foreach (var write in candidates.Where(w => !existing.Contains(w.Sale.Id)))
        {
            var amounts = SplitAmount(
                write.Sale.TotalUsd,
                write.Plan.Shares.Select(s => s.QuantityMt).ToList());
            allocations.AddRange(write.Plan.Shares.Select((share, index) => new SalesTransactionSourceAllocation
            {
                SalesTransactionId = write.Sale.Id,
                TransportLegId = write.TransportLegId,
                SourcePurchaseContractId = share.SourcePurchaseContractId,
                SourceLoadingReceiptId = share.SourceLoadingReceiptId,
                SourceInventoryMovementId = share.SourceInventoryMovementId,
                SourceTransportLegId = share.SourceTransportLegId,
                SourceTransportReceiptId = share.SourceTransportReceiptId,
                QuantityMt = share.QuantityMt,
                AmountUsd = amounts[index]
            }));
        }

        if (allocations.Count > 0)
        {
            _db.SalesTransactionSourceAllocations.AddRange(allocations);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task PersistLossAsync(
        LossEvent lossEvent,
        TransportSourcePlan plan,
        int? transportLegId = null,
        CancellationToken ct = default)
    {
        await PersistLossBatchAsync([new LossSourceAllocationWrite(lossEvent, plan, transportLegId)], ct);
    }

    public async Task PersistLossBatchAsync(
        IReadOnlyCollection<LossSourceAllocationWrite> writes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var candidates = writes
            .Where(w => w.LossEvent.Id > 0 && w.Plan.Shares.Count > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var lossIds = candidates.Select(w => w.LossEvent.Id).Distinct().ToList();
        var existingIds = await _db.LossEventSourceAllocations
            .AsNoTracking()
            .Where(a => lossIds.Contains(a.LossEventId))
            .Select(a => a.LossEventId)
            .Distinct()
            .ToListAsync(ct);
        var existing = existingIds.ToHashSet();

        var allocations = candidates
            .Where(w => !existing.Contains(w.LossEvent.Id))
            .SelectMany(write => write.Plan.Shares.Select(share => new LossEventSourceAllocation
            {
                LossEventId = write.LossEvent.Id,
                TransportLegId = write.TransportLegId,
                SourcePurchaseContractId = share.SourcePurchaseContractId,
                SourceLoadingReceiptId = share.SourceLoadingReceiptId,
                SourceInventoryMovementId = share.SourceInventoryMovementId,
                SourceTransportLegId = share.SourceTransportLegId,
                SourceTransportReceiptId = share.SourceTransportReceiptId,
                QuantityMt = share.QuantityMt
            }))
            .ToList();

        if (allocations.Count > 0)
        {
            _db.LossEventSourceAllocations.AddRange(allocations);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<int?> ResolveCurrentLegIdAsync(
        TruckDispatch dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        if (!dispatch.InventoryTransportReceiptId.HasValue)
        {
            return null;
        }

        // در Projection وسیله→وسیله، رسید روی والد است ولی فروش/کسری از فرزند رخ می‌دهد.
        var childLegId = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceTransportReceiptId == dispatch.InventoryTransportReceiptId.Value)
            .Select(a => (int?)a.InventoryTransportLegId)
            .FirstOrDefaultAsync(ct);
        if (childLegId.HasValue)
        {
            return childLegId;
        }

        // دادهٔ قدیمیِ بدون child allocation: همان مرحلهٔ رسید، بهترین lineage موجود است.
        return await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => r.Id == dispatch.InventoryTransportReceiptId.Value)
            .Select(r => (int?)r.InventoryTransportLegId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<TransportSourcePlan> BuildPlanAsync(
        IReadOnlyCollection<TransportSourceShare> sourceRows,
        decimal requestedQuantityMt,
        CancellationToken ct)
    {
        if (requestedQuantityMt <= 0m || sourceRows.Count == 0)
        {
            return TransportSourcePlan.Empty;
        }

        var grouped = sourceRows
            .Where(s => s.QuantityMt > 0m)
            .GroupBy(s => new
            {
                s.SourcePurchaseContractId,
                s.SourceLoadingReceiptId,
                s.SourceInventoryMovementId,
                s.SourceTransportLegId,
                s.SourceTransportReceiptId
            })
            .Select(g => new TransportSourceShare(
                g.Key.SourcePurchaseContractId,
                g.Sum(s => s.QuantityMt),
                g.Key.SourceLoadingReceiptId,
                g.Key.SourceInventoryMovementId,
                g.Key.SourceTransportLegId,
                g.Key.SourceTransportReceiptId))
            .OrderBy(s => s.SourcePurchaseContractId)
            .ThenBy(s => s.SourceLoadingReceiptId)
            .ThenBy(s => s.SourceInventoryMovementId)
            .ThenBy(s => s.SourceTransportLegId)
            .ThenBy(s => s.SourceTransportReceiptId)
            .ToList();

        if (grouped.Count == 0)
        {
            return TransportSourcePlan.Empty;
        }

        var totalSourceMt = grouped.Sum(s => s.QuantityMt);
        var scaled = new List<TransportSourceShare>(grouped.Count);
        decimal assignedMt = 0m;
        for (var i = 0; i < grouped.Count; i++)
        {
            var source = grouped[i];
            var quantityMt = i == grouped.Count - 1
                ? requestedQuantityMt - assignedMt
                : Math.Round(requestedQuantityMt * source.QuantityMt / totalSourceMt, 4, MidpointRounding.AwayFromZero);
            assignedMt += quantityMt;
            if (quantityMt <= 0m)
            {
                continue;
            }

            scaled.Add(source with { QuantityMt = quantityMt });
        }

        var contractIds = scaled
            .Select(s => s.SourcePurchaseContractId)
            .Distinct()
            .ToList();
        int? singleContractId = contractIds.Count == 1 ? contractIds[0] : null;

        var companyIds = await _db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id))
            .Select(c => c.CompanyId)
            .Distinct()
            .ToListAsync(ct);
        int? singleCompanyId = companyIds.Count == 1 ? companyIds[0] : null;

        return new TransportSourcePlan(scaled, singleContractId, singleCompanyId);
    }

    private static IReadOnlyList<decimal> SplitAmount(
        decimal totalAmount,
        IReadOnlyList<decimal> quantities)
    {
        if (quantities.Count == 0)
        {
            return [];
        }

        var totalQuantity = quantities.Sum();
        if (totalQuantity <= 0m)
        {
            return Enumerable.Repeat(0m, quantities.Count).ToList();
        }

        var amounts = new decimal[quantities.Count];
        decimal assigned = 0m;
        for (var i = 0; i < quantities.Count; i++)
        {
            amounts[i] = i == quantities.Count - 1
                ? totalAmount - assigned
                : Math.Round(totalAmount * quantities[i] / totalQuantity, 2, MidpointRounding.AwayFromZero);
            assigned += amounts[i];
        }

        return amounts;
    }
}
