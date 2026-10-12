using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// تنها قاعدهٔ «بهای تمام‌شدهٔ یک فروش» برای همهٔ سطوح (شرکت، محموله، قرارداد):
/// <list type="number">
/// <item>سهمِ فروش از قرارداد خرید (<see cref="ISaleContractAttributionReader.LoadPurchaseContractSalesAsync"/>)
/// × بهای واحدِ همان قرارداد «تا تاریخ همان فروش» (<see cref="BuildPurchaseCostBasesAsync"/>: میانگین وزنی
/// قیمت خرید + کرایهٔ مستقیمِ بی‌سند بر هر تن، با همان حذف آینهٔ راننده و همان مسئولیت کرایه). بارگیریِ
/// بعد از فروش بهای آن فروش را عوض نمی‌کند.</item>
/// <item>فروشی که چنین بهایی ندارد، بهای Pool فعال (<see cref="SalesCostConsumption"/>) را می‌گیرد.</item>
/// <item>بقیه بی‌بها می‌مانند. یک فروش هرگز هم بهای قرارداد و هم بهای Pool نمی‌گیرد.</item>
/// </list>
/// </summary>
public sealed partial class ProfitAndLossService
{
    public async Task<SalesPnlSnapshot> BuildForPurchaseContractSalesAsync(
        int purchaseContractId,
        IReadOnlyCollection<int> salesTransactionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(salesTransactionIds);
        var inputs = await LoadSaleCostInputsAsync(salesTransactionIds, ct);
        return ToSnapshot(CostSales(inputs.Sales.Keys, inputs, purchaseContractId));
    }

    private async Task<SaleCostInputs> LoadSaleCostInputsAsync(IReadOnlyCollection<int> salesTransactionIds, CancellationToken ct)
    {
        var ids = salesTransactionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return SaleCostInputs.Empty;
        }

        var sales = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && ids.Contains(s.Id))
            .Select(s => new SaleCostSale(s.Id, s.SaleDate, s.TotalUsd, s.IsSalePricePending))
            .ToDictionaryAsync(s => s.Id, ct);
        if (sales.Count == 0)
        {
            return SaleCostInputs.Empty;
        }

        var purchaseIds = await _db.Contracts.AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var attribution = purchaseIds.Count == 0
            ? PurchaseContractSales.Empty
            : await _saleAttribution.LoadPurchaseContractSalesAsync(purchaseIds, ct);
        var rawShares = attribution.SalesByContract
            .SelectMany(pair => pair.Value
                .Where(s => sales.ContainsKey(s.SalesTransactionId))
                .Select(s => (ContractId: pair.Key, Share: s)))
            .ToList();

        var bases = rawShares.Count == 0
            ? new Dictionary<DateTime, PurchaseCostBasis>()
            : await BuildPurchaseCostBasesAsync(
                purchaseIds,
                rawShares.Select(s => sales[s.Share.SalesTransactionId].SaleDate.Date).Distinct().ToList(),
                ct);
        var sharesBySale = rawShares
            .GroupBy(s => s.Share.SalesTransactionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SaleCostShare>)g
                    .Select(s => new SaleCostShare(
                        s.ContractId,
                        s.Share.QuantityMt,
                        s.Share.AmountUsd,
                        bases.TryGetValue(sales[s.Share.SalesTransactionId].SaleDate.Date, out var basis)
                            && basis.Contracts.TryGetValue(s.ContractId, out var contractBasis)
                                ? contractBasis.UnitCostUsd
                                : null))
                    .ToList());

        var saleIds = sales.Keys.ToArray();
        var poolCostBySale = await LoadVerifiedSaleCostsAsync(saleIds, ct);

        return new SaleCostInputs(sales, sharesBySale, poolCostBySale);
    }

    /// <summary>
    /// بها و عایدِ یک مجموعه فروش. بدون <paramref name="scopePurchaseContractId"/> کلِ هر فروش حساب می‌شود
    /// (شرکت، محموله). با آن فقط سهمِ همان قرارداد: عاید و بهای سهمِ آن قرارداد، و برای فروشی که هیچ سهمی
    /// از آن قرارداد ندارد کل فروش. Pool کل فروش است، پس فقط برای فروشی جایگزین می‌شود که یا انتساب ندارد یا
    /// کاملاً مال همان قرارداد است؛ تقسیم Pool حدس زده نمی‌شود. گردکردن روی هر (قرارداد، بهای واحد) یک بار.
    /// </summary>
    private static SaleCostTotals CostSales(
        IEnumerable<int> saleIds,
        SaleCostInputs inputs,
        int? scopePurchaseContractId = null)
    {
        var revenue = 0m;
        var saleCount = 0;
        var contractCosted = 0;
        var poolCosted = 0;
        var poolCost = 0m;
        var buckets = new Dictionary<(int ContractId, decimal UnitCostUsd), decimal>();

        foreach (var saleId in saleIds.Distinct())
        {
            if (!inputs.Sales.TryGetValue(saleId, out var sale))
            {
                continue;
            }

            saleCount++;
            // قیمت باز: نه عاید و نه بها؛ فروش «بی‌بها» شمرده می‌شود تا گزارش NeedsReview شود.
            if (sale.IsSalePricePending)
            {
                continue;
            }

            var shares = inputs.SharesBySale.GetValueOrDefault(saleId) ?? [];
            var scoped = scopePurchaseContractId is { } contractId
                ? shares.Where(s => s.ContractId == contractId).ToList()
                : shares.ToList();
            revenue += scopePurchaseContractId.HasValue && scoped.Count > 0
                ? scoped.Sum(s => s.AmountUsd)
                : sale.TotalUsd;

            var costedShares = scoped.Where(s => s.UnitCostUsd.HasValue).ToList();
            if (costedShares.Count > 0)
            {
                foreach (var share in costedShares)
                {
                    var key = (share.ContractId, share.UnitCostUsd!.Value);
                    buckets[key] = buckets.GetValueOrDefault(key) + share.QuantityMt;
                }

                // فروشی که سهمِ بی‌بها هم دارد بهای کامل ندارد: عایدِ آن سهم بدون بها در سود می‌نشیند،
                // پس فروش «بی‌بها» شمرده می‌شود تا گزارش NeedsReview شود و سودش قطعی خوانده نشود.
                if (costedShares.Count == scoped.Count)
                {
                    contractCosted++;
                }

                continue;
            }

            var wholeSaleInScope = shares.Count == 0
                || scopePurchaseContractId is null
                || shares.All(s => s.ContractId == scopePurchaseContractId.Value);
            if (wholeSaleInScope && inputs.PoolCostBySale.TryGetValue(saleId, out var cost))
            {
                poolCosted++;
                poolCost += cost;
            }
        }

        var contractCost = buckets.Sum(b => decimal.Round(b.Value * b.Key.UnitCostUsd, 2, MidpointRounding.AwayFromZero));
        return new SaleCostTotals(revenue, contractCost + poolCost, saleCount, contractCosted, poolCosted);
    }

    private static SalesPnlSnapshot ToSnapshot(SaleCostTotals totals)
    {
        var uncosted = totals.SaleCount - totals.ContractCostedCount - totals.PoolCostedCount;
        return new SalesPnlSnapshot(
            RevenueUsd: totals.RevenueUsd,
            CostOfGoodsSoldUsd: totals.CostUsd,
            SaleCount: totals.SaleCount,
            CostedSaleCount: totals.ContractCostedCount + totals.PoolCostedCount,
            UncostedSaleCount: uncosted,
            Confidence: uncosted > 0
                ? PnlConfidence.NeedsReview
                : totals.ContractCostedCount > 0 ? PnlConfidence.Estimated : PnlConfidence.Verified);
    }

    private sealed record SaleCostSale(int Id, DateTime SaleDate, decimal TotalUsd, bool IsSalePricePending);

    private sealed record SaleCostShare(int ContractId, decimal QuantityMt, decimal AmountUsd, decimal? UnitCostUsd);

    private sealed record SaleCostInputs(
        IReadOnlyDictionary<int, SaleCostSale> Sales,
        IReadOnlyDictionary<int, IReadOnlyList<SaleCostShare>> SharesBySale,
        IReadOnlyDictionary<int, decimal> PoolCostBySale)
    {
        public static SaleCostInputs Empty { get; } = new(
            new Dictionary<int, SaleCostSale>(),
            new Dictionary<int, IReadOnlyList<SaleCostShare>>(),
            new Dictionary<int, decimal>());
    }

    private sealed record SaleCostTotals(
        decimal RevenueUsd,
        decimal CostUsd,
        int SaleCount,
        int ContractCostedCount,
        int PoolCostedCount);
}
