using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

public sealed class InventoryTransportPnlSaleSnapshot
{
    public int SaleId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime SaleDate { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal AmountUsd { get; init; }
    public string TraceKind { get; init; } = string.Empty;
}

public sealed class InventoryTransportPnlSnapshot
{
    public int TransportLegId { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal ShortageQuantityMt { get; init; }
    public decimal ChargeableLossMt { get; init; }
    public decimal LossQuantityMt { get; init; }
    public decimal? PurchaseUnitCostUsd { get; init; }
    public string PurchaseCostSource { get; init; } = "Missing purchase cost";
    public decimal PurchaseCostUsd { get; init; }
    public decimal LossCostUsd { get; init; }
    public decimal ExpenseTransactionsUsd { get; init; }
    public decimal SharedShipmentExpensesUsd { get; init; }
    public decimal CustomsUsd { get; init; }
    public decimal ReceiptFreightCostUsd { get; init; }
    public decimal ShortageChargeUsd { get; init; }
    public decimal ReceiptFreightExpenseUsd { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal SalesUsd { get; init; }
    public IReadOnlyList<InventoryTransportPnlSaleSnapshot> Sales { get; init; } = [];
    public string SalesTraceNote { get; init; } = string.Empty;
    public decimal UnsoldQuantityMt { get; init; }
    public decimal OperationalExpensesUsd { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
}

public sealed class InventoryTransportPnlService
{
    private const string ReceiptFreightExpenseCode = "TRANSPORT-RECEIPT-FREIGHT";
    private readonly ApplicationDbContext _db;

    public InventoryTransportPnlService(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// فقط «نرخ خرید مؤثر» هر لِج را با همان زنجیرهٔ سود و زیان برمی‌گرداند
    /// (override خود لِج، سپس تخصیص‌های منبع، سپس زنجیرهٔ قرارداد).
    /// برای فهرست‌ها که به کل محاسبهٔ P&L نیاز ندارند.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, decimal?>> ResolveUnitCostsForLegsAsync(
        IReadOnlyCollection<int> transportLegIds,
        CancellationToken ct = default)
    {
        var requestedIds = transportLegIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (requestedIds.Count == 0)
        {
            return new Dictionary<int, decimal?>();
        }

        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.Allocations)
            .Include(l => l.SourcePurchaseContract)
            .Where(l => requestedIds.Contains(l.Id))
            .ToListAsync(ct);

        if (legs.Count == 0)
        {
            return new Dictionary<int, decimal?>();
        }

        var sourceContractIds = legs
            .Select(l => l.SourcePurchaseContractId)
            .Concat(legs.SelectMany(l => l.Allocations.Select(a => a.SourcePurchaseContractId)))
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var sourceContracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => sourceContractIds.Contains(c.Id))
            .ToListAsync(ct);
        var finalPriceByContract = sourceContracts.ToDictionary(
            c => c.Id,
            ContractPricingAdapter.GetCanonicalFinalPrice);

        var purchaseSnapshots = await new PurchaseAggregationService(_db)
            .AggregateForContractsAsync(finalPriceByContract.Keys.ToList(), finalPriceByContract, ct);

        return legs.ToDictionary(
            l => l.Id,
            l => ResolvePurchaseUnitCost(l, purchaseSnapshots, finalPriceByContract).UnitCost);
    }

    public async Task<IReadOnlyDictionary<int, InventoryTransportPnlSnapshot>> BuildForLegsAsync(
        IReadOnlyCollection<int> transportLegIds,
        CancellationToken ct = default)
    {
        var requestedIds = transportLegIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (requestedIds.Count == 0)
        {
            return new Dictionary<int, InventoryTransportPnlSnapshot>();
        }

        var requestedLegs = await _db.InventoryTransportLegs
            .Include(l => l.Allocations)
            .Include(l => l.SourcePurchaseContract)
            .AsNoTracking()
            .Where(l => requestedIds.Contains(l.Id))
            .ToListAsync(ct);

        if (requestedLegs.Count == 0)
        {
            return new Dictionary<int, InventoryTransportPnlSnapshot>();
        }

        var requestedLegById = requestedLegs.ToDictionary(l => l.Id);
        var shipmentIds = requestedLegs
            .Where(l => l.ShipmentId.HasValue)
            .Select(l => l.ShipmentId!.Value)
            .Distinct()
            .ToList();

        var allocationLegs = shipmentIds.Count == 0
            ? requestedLegs
            : await _db.InventoryTransportLegs
                .AsNoTracking()
                .Include(l => l.Allocations)
                .Where(l => l.ShipmentId.HasValue && shipmentIds.Contains(l.ShipmentId.Value))
                .ToListAsync(ct);
        var allocationLegIds = allocationLegs.Select(l => l.Id).Distinct().ToList();

        var sourceContractIds = requestedLegs
            .Select(l => l.SourcePurchaseContractId)
            .Concat(allocationLegs.SelectMany(l => l.Allocations.Select(a => a.SourcePurchaseContractId)))
            .Distinct()
            .ToList();
        var sourceContracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => sourceContractIds.Contains(c.Id))
            .ToListAsync(ct);
        var finalPriceByContract = sourceContracts.ToDictionary(
            c => c.Id,
            ContractPricingAdapter.GetCanonicalFinalPrice);

        var purchaseSnapshots = await new PurchaseAggregationService(_db)
            .AggregateForContractsAsync(finalPriceByContract.Keys.ToList(), finalPriceByContract, ct);

        var builders = requestedLegs.ToDictionary(
            l => l.Id,
            l =>
            {
                var (unitCost, source) = ResolvePurchaseUnitCost(
                    l,
                    purchaseSnapshots,
                    finalPriceByContract);
                return new PnlBuilder(l, unitCost, source);
            });

        var receipts = allocationLegIds.Count == 0
            ? new List<InventoryTransportReceipt>()
            : await _db.InventoryTransportReceipts
                .Include(r => r.SalesTransaction)
                .AsNoTracking()
                .Where(r => allocationLegIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
                .ToListAsync(ct);

        foreach (var receipt in receipts.Where(r => builders.ContainsKey(r.InventoryTransportLegId)))
        {
            builders[receipt.InventoryTransportLegId].AddReceipt(receipt);
        }

        var receiptById = receipts.ToDictionary(r => r.Id);
        var exactSaleIds = new HashSet<int>();

        foreach (var receipt in receipts.Where(r => r.SalesTransaction is not null && !r.SalesTransaction.IsCancelled))
        {
            var sale = receipt.SalesTransaction!;
            exactSaleIds.Add(sale.Id);

            if (sale.SaleStage == SaleStage.PreSale || !builders.ContainsKey(receipt.InventoryTransportLegId))
            {
                continue;
            }

            var saleQuantity = receipt.ReceivedQuantityMt > 0m
                ? Math.Min(receipt.ReceivedQuantityMt, sale.QuantityMt)
                : sale.QuantityMt;
            builders[receipt.InventoryTransportLegId].AddSale(ToSaleSnapshot(
                sale,
                saleQuantity,
                AllocateSaleAmount(sale, saleQuantity),
                "Direct sale from transport receipt"));
        }

        // نسب‌نامهٔ جدید نتیجهٔ حمل، فروش موترِ چندمنبعی را مستقیماً به child leg واقعی وصل
        // می‌کند. این مسیر پیش از Projection قدیمی خوانده می‌شود تا درآمد روی parent تکرار نشود.
        var alreadyExactSaleIds = exactSaleIds.ToList();
        var allocatedSaleRows = await _db.SalesTransactionSourceAllocations
            .Include(a => a.SalesTransaction)
            .AsNoTracking()
            .Where(a => a.TransportLegId.HasValue
                && requestedIds.Contains(a.TransportLegId.Value)
                && !alreadyExactSaleIds.Contains(a.SalesTransactionId)
                && a.SalesTransaction != null
                && !a.SalesTransaction.IsCancelled
                && !a.SalesTransaction.IsSalePricePending)
            .ToListAsync(ct);
        foreach (var group in allocatedSaleRows.GroupBy(a => new { a.SalesTransactionId, LegId = a.TransportLegId!.Value }))
        {
            var sale = group.First().SalesTransaction!;
            exactSaleIds.Add(sale.Id);
            if (sale.SaleStage == SaleStage.PreSale || !builders.ContainsKey(group.Key.LegId))
            {
                continue;
            }

            builders[group.Key.LegId].AddSale(ToSaleSnapshot(
                sale,
                group.Sum(a => a.QuantityMt),
                group.Sum(a => a.AmountUsd),
                "Transport source allocation"));
        }

        var receiptIds = receipts.Select(r => r.Id).ToList();
        if (receiptIds.Count > 0)
        {
            var dispatchSales = await _db.TruckDispatches
                .Include(d => d.SalesTransaction)
                .AsNoTracking()
                .Where(d => d.InventoryTransportReceiptId.HasValue
                    && receiptIds.Contains(d.InventoryTransportReceiptId.Value)
                    && d.SalesTransactionId.HasValue
                    && d.Status != DispatchStatus.Cancelled)
                .ToListAsync(ct);

            foreach (var dispatch in dispatchSales)
            {
                if (dispatch.SalesTransaction is null || dispatch.SalesTransaction.IsCancelled)
                {
                    continue;
                }

                if (exactSaleIds.Contains(dispatch.SalesTransaction.Id))
                {
                    continue;
                }

                exactSaleIds.Add(dispatch.SalesTransaction.Id);
                if (dispatch.SalesTransaction.SaleStage == SaleStage.PreSale
                    || !dispatch.InventoryTransportReceiptId.HasValue
                    || !receiptById.TryGetValue(dispatch.InventoryTransportReceiptId.Value, out var receipt)
                    || !builders.ContainsKey(receipt.InventoryTransportLegId))
                {
                    continue;
                }

                builders[receipt.InventoryTransportLegId].AddSale(ToSaleSnapshot(
                    dispatch.SalesTransaction,
                    dispatch.SalesTransaction.QuantityMt,
                    dispatch.SalesTransaction.TotalUsd,
                    "Sale from direct dispatch after transport receipt"));
            }
        }

        if (shipmentIds.Count > 0)
        {
            await AllocateShipmentSalesAsync(shipmentIds, allocationLegs, builders, exactSaleIds, ct);
            await AllocateShipmentExpensesAsync(shipmentIds, allocationLegs, builders, ct);
        }

        var exactExpenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.TransportLegId.HasValue
                && requestedIds.Contains(e.TransportLegId.Value)
                && !e.IsCancelled
                // PTG-P1-04 — مصرفِ گمرک ستونِ خودش را دارد؛ اگر اینجا هم شمرده شود،
                // همان پول دو بار در هزینهٔ عملیاتی می‌نشیند.
                && !e.CustomsDeclarationId.HasValue
                && (e.ExpenseType == null || e.ExpenseType.Code != ReceiptFreightExpenseCode)
                // برای دارایی ملکی، این رکورد درآمد داخلی دارایی است؛ کرایهٔ حمل قبلاً
                // از رسید تسویه در ReceiptFreightExpenseUsd آمده و نباید دوباره مصرف شود.
                && !(e.OperationalAssetId.HasValue
                    && e.ExpenseType != null
                    && e.ExpenseType.Code == InventoryTransportReceiptService.TransportFreightExpenseCode
                    && e.Description != null
                    && e.Description.StartsWith(InventoryTransportReceiptService.OperationalAssetFreightIncomeDescriptionPrefix)))
            .GroupBy(e => e.TransportLegId!.Value)
            .Select(g => new { LegId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToListAsync(ct);
        foreach (var expense in exactExpenses)
        {
            builders[expense.LegId].ExpenseTransactionsUsd += expense.AmountUsd;
        }

        // مصرفِ ثبت‌شده از مسیر موتر (فقط TruckDispatchId — مثل کرایه موتر یا مصرف دستی روی دیسپچِ
        // ساخته‌شده از رسید همین leg) به legِ مبدأ وصل می‌شود (dispatch → رسید حمل → leg).
        // فیلترهای !TransportLegId و !ShipmentId ضامن عدم دوباره‌شماری با حلقهٔ بالا و
        // AllocateShipmentExpensesAsync هستند.
        var dispatchExpenseRows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled
                && !e.TransportLegId.HasValue
                && !e.ShipmentId.HasValue
                && e.TruckDispatchId.HasValue
                // PTG-P1-04 — گمرک ستونِ خودش را دارد.
                && !e.CustomsDeclarationId.HasValue
                && e.TruckDispatch!.InventoryTransportReceipt != null
                && requestedIds.Contains(e.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))
            .GroupBy(e => e.TruckDispatch!.InventoryTransportReceipt!.InventoryTransportLegId)
            .Select(g => new { LegId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToListAsync(ct);
        foreach (var expense in dispatchExpenseRows)
        {
            builders[expense.LegId].ExpenseTransactionsUsd += expense.AmountUsd;
        }

        // ستونِ «گمرک» یک منبع دارد: خودِ اظهارنامه. CustomsDeclaration.TotalUsd جمعِ همهٔ
        // اقلامِ اظهارنامه است، ولی CustomsDeclarationExpenseSync فقط برای گروهی مصرف
        // می‌سازد که هویتِ تسویه‌اش انتخاب شده باشد؛ پس خواندن از مصرف‌ها، گروهِ
        // «طبقه‌بندی‌نشده» را خاموش از هزینه می‌انداخت. حلقه‌های مصرف بالاتر با فیلترِ
        // !CustomsDeclarationId.HasValue همین مبالغ را رد می‌کنند، پس دوباره‌شماری نیست.
        var customsRows = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => c.TransportLegId.HasValue
                && requestedIds.Contains(c.TransportLegId.Value))
            .GroupBy(c => c.TransportLegId!.Value)
            .Select(g => new { LegId = g.Key, AmountUsd = g.Sum(c => c.TotalUsd) })
            .ToListAsync(ct);
        foreach (var customs in customsRows)
        {
            builders[customs.LegId].CustomsUsd += customs.AmountUsd;
        }

        // گمرکِ ثبت‌شده از مسیر موتر (فقط TruckDispatchId، بدون TransportLegId) به legِ مبدأ همان موتر
        // (dispatch → رسید حمل → leg) وصل می‌شود. فیلترِ !TransportLegId.HasValue تضمینِ عدم دوباره‌شماری با حلقهٔ بالا است.
        var dispatchCustomsRows = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => !c.TransportLegId.HasValue
                && c.TruckDispatchId.HasValue
                && c.TruckDispatch!.InventoryTransportReceipt != null
                && requestedIds.Contains(c.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))
            .GroupBy(c => c.TruckDispatch!.InventoryTransportReceipt!.InventoryTransportLegId)
            .Select(g => new { LegId = g.Key, AmountUsd = g.Sum(c => c.TotalUsd) })
            .ToListAsync(ct);
        foreach (var customs in dispatchCustomsRows)
        {
            builders[customs.LegId].CustomsUsd += customs.AmountUsd;
        }

        var lossRows = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.TransportLegId.HasValue
                && requestedIds.Contains(l.TransportLegId.Value)
                && !l.IsCancelled)
            .GroupBy(l => l.TransportLegId!.Value)
            .Select(g => new
            {
                LegId = g.Key,
                LossQuantityMt = g.Sum(l => l.DifferenceQuantityMt),
                ChargeableLossMt = g.Sum(l => l.ChargeableLossMt)
            })
            .ToListAsync(ct);
        foreach (var loss in lossRows)
        {
            builders[loss.LegId].LossQuantityMt = Math.Max(builders[loss.LegId].LossQuantityMt, loss.LossQuantityMt);
            builders[loss.LegId].ChargeableLossMt = Math.Max(builders[loss.LegId].ChargeableLossMt, loss.ChargeableLossMt);
        }

        return requestedLegById.Keys.ToDictionary(id => id, id => builders[id].Build());
    }

    private async Task AllocateShipmentSalesAsync(
        IReadOnlyCollection<int> shipmentIds,
        IReadOnlyList<InventoryTransportLeg> allocationLegs,
        IReadOnlyDictionary<int, PnlBuilder> requestedBuilders,
        IReadOnlySet<int> exactSaleIds,
        CancellationToken ct)
    {
        var shipmentSales = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && !s.IsSalePricePending
                && s.ShipmentId.HasValue
                && shipmentIds.Contains(s.ShipmentId.Value)
                && !exactSaleIds.Contains(s.Id)
                && s.SaleStage != SaleStage.PreSale)
            .OrderBy(s => s.SaleDate)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        foreach (var sale in shipmentSales)
        {
            // فروشِ سطح‌محموله/کشتی فقط به legهای مبدأِ خودِ محموله تخصیص می‌شود، نه به
            // legهایی که بارشان از موجودیِ داخلی برداشته شده (نگاه کنید به
            // IsInventoryOriginTransport) و ShipmentId والد را حفظ کرده‌اند). آن فروش از خودِ کشتی/منبع رخ داده، نه از این حملِ بعدی؛
            // در غیر این صورت فروشِ ساختگی روی جزییات حمل از موجودی ظاهر می‌شود.
            // گاردِ تاریخ (LoadedDate.Date <= SaleDate.Date) صرفاً گاردِ فرعی است و راه‌حلِ اصلی نیست.
            // اگر فروش قرارداد منبع دارد، درآمد فقط به legهای همان قرارداد نسبت داده می‌شود تا
            // محموله‌ای با چند قرارداد (جواز/شرکت متفاوت) سود هر قرارداد را جدا نگه دارد.
            var candidates = allocationLegs
                .Where(l => l.ShipmentId == sale.ShipmentId
                    && l.ProductId == sale.ProductId
                    && (!sale.SourcePurchaseContractId.HasValue
                        || l.SourcePurchaseContractId == sale.SourcePurchaseContractId.Value)
                    && l.QuantityMt > 0m
                    && !IsInventoryOriginTransport(l)
                    && l.LoadedDate.Date <= sale.SaleDate.Date)
                .OrderBy(l => l.LoadedDate)
                .ThenBy(l => l.Id)
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var quantityByLeg = AllocateByLegQuantity(sale.QuantityMt, candidates);
            var amountByLeg = AllocateByLegQuantity(sale.TotalUsd, candidates);

            foreach (var leg in candidates.Where(l => requestedBuilders.ContainsKey(l.Id)))
            {
                var quantity = quantityByLeg.GetValueOrDefault(leg.Id);
                var amount = amountByLeg.GetValueOrDefault(leg.Id);
                if (quantity <= 0m && amount <= 0m)
                {
                    continue;
                }

                requestedBuilders[leg.Id].AddSale(ToSaleSnapshot(
                    sale,
                    quantity,
                    amount,
                    "Allocated shipment sale"));
            }
        }
    }

    private async Task AllocateShipmentExpensesAsync(
        IReadOnlyCollection<int> shipmentIds,
        IReadOnlyList<InventoryTransportLeg> allocationLegs,
        IReadOnlyDictionary<int, PnlBuilder> requestedBuilders,
        CancellationToken ct)
    {
        var sharedExpenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled
                && e.ShipmentId.HasValue
                && shipmentIds.Contains(e.ShipmentId.Value)
                && !e.TransportLegId.HasValue
                // PTG-P1-04 — گمرک ستونِ خودش را دارد. امروز مصرفِ گمرک ShipmentId نمی‌گیرد،
                // ولی این فیلتر صریح است تا اگر روزی گرفت، خاموش دو بار شمرده نشود.
                && !e.CustomsDeclarationId.HasValue)
            .OrderBy(e => e.ExpenseDate)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        foreach (var expense in sharedExpenses)
        {
            var candidates = allocationLegs
                .Where(l => l.ShipmentId == expense.ShipmentId && l.QuantityMt > 0m)
                .Where(l => !expense.ContractId.HasValue || l.SourcePurchaseContractId == expense.ContractId.Value)
                .OrderBy(l => l.LoadedDate)
                .ThenBy(l => l.Id)
                .ToList();

            if (candidates.Count == 0 && !expense.ContractId.HasValue)
            {
                candidates = allocationLegs
                    .Where(l => l.ShipmentId == expense.ShipmentId && l.QuantityMt > 0m)
                    .OrderBy(l => l.LoadedDate)
                    .ThenBy(l => l.Id)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            var amountByLeg = AllocateByLegQuantity(expense.AmountUsd, candidates);
            foreach (var leg in candidates.Where(l => requestedBuilders.ContainsKey(l.Id)))
            {
                requestedBuilders[leg.Id].SharedShipmentExpensesUsd += amountByLeg.GetValueOrDefault(leg.Id);
            }
        }
    }

    // آیا بارِ این leg از یک منبعِ داخلیِ ثبت‌شده برداشته شده است؟
    //
    // معیار، نسب‌نامهٔ واقعیِ بار است نه ظرفِ دیتابیسیِ آن: هر سهم منبع
    // (InventoryTransportLegAllocation) دقیقاً می‌گوید بار از کجا آمده —
    //   SourceInventoryMovementId → از موجودیِ مخزن/ترمینال
    //   SourceLoadingReceiptId    → از رسیدِ بارگیریِ مستقیم
    //   SourceTransportLegId      → از وسیلهٔ قبلی در زنجیره
    // پس وجودِ سهم منبع، خودش سندِ «این بار از داخل سیستم برداشته شد» است.
    //
    // legِ مبدأِ خودِ محموله هیچ سهم منبعی ندارد، چون بار تازه وارد سیستم شده و از جایی
    // برداشته نشده. فقط همان legها حق دارند فروشِ سطح‌محموله را جذب کنند.
    //
    // عمداً به InventoryTransportBatchId نگاه نمی‌شود: دو حملِ تجاریِ یکسان نباید فقط به‌خاطر
    // اینکه یکی زیر سند Batch نشسته سود و زیان متفاوت بگیرند. هر مسیری که BatchId می‌گذارد
    // سهم منبع هم می‌سازد (ValidateAndPrepareAsync حداقل یک سهم را الزامی می‌کند و
    // StartFromReceiptAsync هم همیشه سهم می‌سازد)، پس رفتار موجود دست‌نخورده می‌ماند.
    //
    // فراخوان باید Allocations را Include کرده باشد، وگرنه نتیجه به‌غلط false می‌شود.
    private static bool IsInventoryOriginTransport(InventoryTransportLeg leg)
        => leg.Allocations.Count > 0;

    private static Dictionary<int, decimal> AllocateByLegQuantity(decimal total, IReadOnlyList<InventoryTransportLeg> legs)
    {
        var result = new Dictionary<int, decimal>();
        if (legs.Count == 0 || total == 0m)
        {
            return result;
        }

        var totalQuantity = legs.Sum(l => Math.Max(l.QuantityMt, 0m));
        if (totalQuantity <= 0m)
        {
            return result;
        }

        var allocated = 0m;
        for (var index = 0; index < legs.Count; index++)
        {
            var leg = legs[index];
            var amount = index == legs.Count - 1
                ? total - allocated
                : RoundMoney(total * leg.QuantityMt / totalQuantity);
            result[leg.Id] = amount;
            allocated += amount;
        }

        return result;
    }

    private static InventoryTransportPnlSaleSnapshot ToSaleSnapshot(
        SalesTransaction sale,
        decimal quantityMt,
        decimal amountUsd,
        string traceKind)
        => new()
        {
            SaleId = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            SaleDate = sale.SaleDate,
            QuantityMt = RoundQuantity(quantityMt),
            AmountUsd = RoundMoney(amountUsd),
            TraceKind = traceKind
        };

    private static decimal AllocateSaleAmount(SalesTransaction sale, decimal quantityMt)
        => sale.QuantityMt > 0m
            ? RoundMoney(sale.TotalUsd * quantityMt / sale.QuantityMt)
            : sale.TotalUsd;

    private static (decimal? UnitCost, string Source) ResolvePurchaseUnitCost(
        InventoryTransportLeg leg,
        IReadOnlyDictionary<int, PurchaseAggregationSnapshot> purchaseSnapshots,
        IReadOnlyDictionary<int, decimal?> finalPriceByContract)
    {
        if (leg.PurchaseUnitCostUsd.HasValue && leg.PurchaseUnitCostUsd.Value > 0m)
        {
            return (leg.PurchaseUnitCostUsd.Value, ShipmentPurchaseCostResolver.SourceLegActualCost);
        }


        var allocationShares = leg.Allocations
            .Where(a => a.QuantityMt > 0m)
            .GroupBy(a => a.SourcePurchaseContractId)
            .Select(g => new { ContractId = g.Key, QuantityMt = g.Sum(a => a.QuantityMt) })
            .ToList();
        if (allocationShares.Count > 0)
        {
            decimal weightedCost = 0m;
            var allCostsKnown = true;
            foreach (var share in allocationShares)
            {
                decimal? sourceUnitCost = null;
                if (purchaseSnapshots.TryGetValue(share.ContractId, out var allocationSnapshot)
                    && allocationSnapshot.WeightedAveragePurchasePriceUsd is > 0m)
                {
                    sourceUnitCost = allocationSnapshot.WeightedAveragePurchasePriceUsd.Value;
                }
                else if (finalPriceByContract.TryGetValue(share.ContractId, out var finalPrice)
                    && finalPrice is > 0m)
                {
                    sourceUnitCost = finalPrice.Value;
                }

                if (!sourceUnitCost.HasValue)
                {
                    allCostsKnown = false;
                    break;
                }

                weightedCost += sourceUnitCost.Value * share.QuantityMt;
            }

            var totalAllocatedMt = allocationShares.Sum(a => a.QuantityMt);
            if (allCostsKnown && totalAllocatedMt > 0m)
            {
                return (
                    RoundMoney(weightedCost / totalAllocatedMt),
                    allocationShares.Count == 1
                        ? "Source allocation contract cost"
                        : "Source allocation weighted average");
            }
        }

        // زنجیرهٔ پایانی (میانگین وزنی بارگیری‌ها → نرخ نهایی قرارداد) از منبع واحد می‌آید.
        purchaseSnapshots.TryGetValue(leg.SourcePurchaseContractId, out var snapshot);
        var contractFinalPrice = finalPriceByContract.GetValueOrDefault(leg.SourcePurchaseContractId)
            ?? (leg.SourcePurchaseContract is null
                ? null
                : ContractPricingAdapter.GetCanonicalFinalPrice(leg.SourcePurchaseContract));

        return ShipmentPurchaseCostResolver.ResolveContractUnitCost(snapshot, contractFinalPrice);
    }

    private static decimal RoundMoney(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed class PnlBuilder
    {
        private readonly InventoryTransportLeg _leg;
        private readonly decimal? _purchaseUnitCostUsd;
        private readonly string _purchaseCostSource;
        private readonly List<InventoryTransportPnlSaleSnapshot> _sales = [];

        public PnlBuilder(InventoryTransportLeg leg, decimal? purchaseUnitCostUsd, string purchaseCostSource)
        {
            _leg = leg;
            _purchaseUnitCostUsd = purchaseUnitCostUsd;
            _purchaseCostSource = purchaseCostSource;
        }

        public decimal ReceivedQuantityMt { get; private set; }
        public decimal ShortageQuantityMt { get; private set; }
        public decimal ChargeableLossMt { get; set; }
        public decimal LossQuantityMt { get; set; }
        public decimal ReceiptFreightCostUsd { get; private set; }
        public decimal ShortageChargeUsd { get; private set; }
        public decimal ReceiptFreightExpenseUsd { get; private set; }
        public decimal ExpenseTransactionsUsd { get; set; }
        public decimal SharedShipmentExpensesUsd { get; set; }
        public decimal CustomsUsd { get; set; }

        public void AddReceipt(InventoryTransportReceipt receipt)
        {
            ReceivedQuantityMt += receipt.ReceivedQuantityMt;
            ShortageQuantityMt += receipt.ShortageQuantityMt;
            ChargeableLossMt += receipt.ChargeableShortageMt ?? 0m;
            LossQuantityMt += receipt.ShortageQuantityMt;
            ReceiptFreightCostUsd += receipt.FreightCostUsd ?? 0m;
            ShortageChargeUsd += receipt.ShortageChargeUsd ?? 0m;
            // هزینهٔ خالصِ کرایه = کرایهٔ ناخالص − خسارتِ کسریِ قابل‌وصول. این فرمول در هر دو حالتِ
            // «کسر از کرایه» و «بدهیِ جدا» یکسان است (پس P&L بین دو حالت فرقی نمی‌کند) و برای
            // رکوردهای قدیمی هم دقیقاً برابر مقدار قبلی (FreightPayable) است.
            ReceiptFreightExpenseUsd += (receipt.FreightCostUsd ?? 0m) - (receipt.ShortageChargeUsd ?? 0m);
        }

        public void AddSale(InventoryTransportPnlSaleSnapshot sale)
        {
            _sales.Add(sale);
        }

        public InventoryTransportPnlSnapshot Build()
        {
            var purchaseCostUsd = _purchaseUnitCostUsd.HasValue
                ? RoundMoney(_leg.QuantityMt * _purchaseUnitCostUsd.Value)
                : 0m;
            var lossQuantityMt = Math.Max(LossQuantityMt, ShortageQuantityMt);
            var lossCostUsd = _purchaseUnitCostUsd.HasValue
                ? RoundMoney(lossQuantityMt * _purchaseUnitCostUsd.Value)
                : 0m;
            var soldQuantityMt = _sales.Sum(s => s.QuantityMt);
            var salesUsd = _sales.Sum(s => s.AmountUsd);
            var operationalExpensesUsd = RoundMoney(
                ExpenseTransactionsUsd
                + SharedShipmentExpensesUsd
                + CustomsUsd
                + ReceiptFreightExpenseUsd);
            var totalCostUsd = RoundMoney(purchaseCostUsd + operationalExpensesUsd);
            var grossMarginUsd = RoundMoney(salesUsd - totalCostUsd);
            var unsoldQuantityMt = Math.Max(_leg.QuantityMt - soldQuantityMt - lossQuantityMt, 0m);
            var traceKinds = _sales
                .Select(s => s.TraceKind)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new InventoryTransportPnlSnapshot
            {
                TransportLegId = _leg.Id,
                QuantityMt = _leg.QuantityMt,
                ReceivedQuantityMt = RoundQuantity(ReceivedQuantityMt),
                ShortageQuantityMt = RoundQuantity(ShortageQuantityMt),
                ChargeableLossMt = RoundQuantity(ChargeableLossMt),
                LossQuantityMt = RoundQuantity(lossQuantityMt),
                PurchaseUnitCostUsd = _purchaseUnitCostUsd,
                PurchaseCostSource = _purchaseCostSource,
                PurchaseCostUsd = purchaseCostUsd,
                LossCostUsd = lossCostUsd,
                ExpenseTransactionsUsd = RoundMoney(ExpenseTransactionsUsd),
                SharedShipmentExpensesUsd = RoundMoney(SharedShipmentExpensesUsd),
                CustomsUsd = RoundMoney(CustomsUsd),
                ReceiptFreightCostUsd = RoundMoney(ReceiptFreightCostUsd),
                ShortageChargeUsd = RoundMoney(ShortageChargeUsd),
                ReceiptFreightExpenseUsd = RoundMoney(ReceiptFreightExpenseUsd),
                SoldQuantityMt = RoundQuantity(soldQuantityMt),
                SalesUsd = RoundMoney(salesUsd),
                Sales = _sales
                    .OrderBy(s => s.SaleDate)
                    .ThenBy(s => s.SaleId)
                    .ToList(),
                SalesTraceNote = traceKinds.Count == 0
                    ? "No traceable sale"
                    : string.Join(" + ", traceKinds),
                UnsoldQuantityMt = RoundQuantity(unsoldQuantityMt),
                OperationalExpensesUsd = operationalExpensesUsd,
                TotalCostUsd = totalCostUsd,
                GrossMarginUsd = grossMarginUsd
            };
        }
    }
}
