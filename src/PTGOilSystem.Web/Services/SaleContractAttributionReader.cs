using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// سهم اثبات‌شدهٔ یک قرارداد خرید از یک فروش. مقدار و مبلغ هر دو از
/// <c>SalesTransactionSourceAllocations</c> می‌آیند و جمعشان روی یک فروش دقیقاً
/// برابر <c>SalesTransaction.TotalUsd</c> است.
/// </summary>
public sealed record SaleContractShare(
    int SalesTransactionId,
    int SourcePurchaseContractId,
    decimal QuantityMt,
    decimal AmountUsd);

/// <summary>
/// پاسخ فقط‌خواندنیِ «کدام قرارداد خرید چقدر از این فروش را دارد».
/// فروشی که ردیف allocation ندارد «اثبات‌نشده» است و این نقشه دربارهٔ آن هیچ حدسی نمی‌زند؛
/// فراخوان باید خودش تصمیم بگیرد (معمولاً: رفتار قدیمیِ همان صفحه را نگه دارد).
/// </summary>
public sealed class SaleContractAttributionMap
{
    public static SaleContractAttributionMap Empty { get; } =
        new(new Dictionary<int, IReadOnlyList<SaleContractShare>>());

    private readonly IReadOnlyDictionary<int, IReadOnlyList<SaleContractShare>> _bySale;

    internal SaleContractAttributionMap(IReadOnlyDictionary<int, IReadOnlyList<SaleContractShare>> bySale)
    {
        _bySale = bySale;
    }

    /// <summary>آیا برای این فروش انتساب اثبات‌شده وجود دارد؟</summary>
    public bool HasProvenAllocation(int salesTransactionId)
        => _bySale.ContainsKey(salesTransactionId);

    /// <summary>سهم‌های اثبات‌شدهٔ یک فروش (خالی یعنی اثبات‌نشده).</summary>
    public IReadOnlyList<SaleContractShare> SharesFor(int salesTransactionId)
        => _bySale.TryGetValue(salesTransactionId, out var shares) ? shares : [];

    /// <summary>
    /// سهم یک قرارداد مشخص از یک فروش. <c>null</c> یعنی انتساب اثبات‌شده‌ای در کار نیست؛
    /// <c>0</c> یعنی اثبات شده که این قرارداد سهمی ندارد.
    /// </summary>
    public (decimal QuantityMt, decimal AmountUsd)? ShareFor(int salesTransactionId, int purchaseContractId)
    {
        if (!_bySale.TryGetValue(salesTransactionId, out var shares))
        {
            return null;
        }

        var quantityMt = 0m;
        var amountUsd = 0m;
        foreach (var share in shares)
        {
            if (share.SourcePurchaseContractId != purchaseContractId)
            {
                continue;
            }

            quantityMt += share.QuantityMt;
            amountUsd += share.AmountUsd;
        }

        return (quantityMt, amountUsd);
    }
}

/// <summary>
/// تنها مرجع خواندنِ «این فروش به کدام قرارداد خرید تعلق دارد».
/// </summary>
public interface ISaleContractAttributionReader
{
    Task<SaleContractAttributionMap> LoadForSalesAsync(
        IReadOnlyCollection<int> salesTransactionIds,
        CancellationToken ct = default);

    Task<SaleContractAttributionMap> LoadForPurchaseContractAsync(
        int purchaseContractId,
        CancellationToken ct = default);

    /// <summary>
    /// فروش‌های هر قرارداد خرید با سهمِ مقدار و مبلغ — تنها قاعدهٔ «فروش‌های این قرارداد».
    /// گزارش مفاد قراردادها، پروندهٔ قرارداد و صورت‌حساب شراکت همه از همین می‌خوانند.
    /// </summary>
    Task<PurchaseContractSales> LoadPurchaseContractSalesAsync(
        IReadOnlyCollection<int> purchaseContractIds,
        CancellationToken ct = default);
}

/// <summary>یک فروش (یا سهمِ اثبات‌شدهٔ آن) که به یک قرارداد خرید نشسته است.</summary>
public sealed record AttributedContractSale(
    int SalesTransactionId,
    DateTime SaleDate,
    string? InvoiceNumber,
    decimal QuantityMt,
    decimal AmountUsd,
    bool IsProvenShare);

public sealed class PurchaseContractSales
{
    public static PurchaseContractSales Empty { get; } = new(
        new Dictionary<int, IReadOnlyList<AttributedContractSale>>(),
        new Dictionary<int, int>());

    public PurchaseContractSales(
        IReadOnlyDictionary<int, IReadOnlyList<AttributedContractSale>> salesByContract,
        IReadOnlyDictionary<int, int> directSaleQuantityMismatchByContract)
    {
        SalesByContract = salesByContract;
        DirectSaleQuantityMismatchByContract = directSaleQuantityMismatchByContract;
    }

    public IReadOnlyDictionary<int, IReadOnlyList<AttributedContractSale>> SalesByContract { get; }

    /// <summary>تخصیصِ فروشِ مستقیم که مقدارش با خودِ فروش نمی‌خواند (نیازمند بررسی).</summary>
    public IReadOnlyDictionary<int, int> DirectSaleQuantityMismatchByContract { get; }

    public IReadOnlyList<AttributedContractSale> For(int contractId)
        => SalesByContract.TryGetValue(contractId, out var sales) ? sales : [];
}

/// <summary>
/// منبع واحد حقیقتِ انتسابِ «فروش → قرارداد خرید».
/// <para>
/// فقط <c>SalesTransactionSourceAllocations</c> خوانده می‌شود؛ همان ردیف‌هایی که از
/// <c>InventoryMovement.ContractId</c> واقعی (FIFO) ساخته شده‌اند. نه
/// <c>SalesTransaction.ContractId</c> (که قرارداد <em>فروش</em> است، نه خرید)، نه
/// <c>SalesTransaction.SourcePurchaseContractId</c> (که فقط برای فروش تک‌قراردادی پر
/// می‌شود) و نه <c>LedgerEntry.ContractId</c> مبنای انتساب قرار نمی‌گیرند.
/// </para>
/// <para>
/// این کلاس چیزی نمی‌نویسد و هیچ قراردادی را حدس نمی‌زند. فروشِ بدون allocation
/// «اثبات‌نشده» برمی‌گردد تا صفحه بتواند بدون ساختن عدد ساختگی تصمیم بگیرد.
/// </para>
/// </summary>
public sealed class SaleContractAttributionReader : ISaleContractAttributionReader
{
    private readonly ApplicationDbContext _db;
    private readonly DateTime? _reportBefore;

    public SaleContractAttributionReader(ApplicationDbContext db, DateTime? reportAsOfDate = null)
    {
        _db = db;
        _reportBefore = reportAsOfDate.HasValue ? DateTime.SpecifyKind(reportAsOfDate.Value.Date.AddDays(1), DateTimeKind.Utc) : null;
    }

    public async Task<SaleContractAttributionMap> LoadForSalesAsync(
        IReadOnlyCollection<int> salesTransactionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(salesTransactionIds);
        if (salesTransactionIds.Count == 0)
        {
            return SaleContractAttributionMap.Empty;
        }

        var ids = salesTransactionIds.Distinct().ToArray();
        var rows = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => ids.Contains(a.SalesTransactionId))
            .Select(a => new SaleContractShare(
                a.SalesTransactionId,
                a.SourcePurchaseContractId,
                a.QuantityMt,
                a.AmountUsd))
            .ToListAsync(ct);

        return Build(rows);
    }

    public async Task<SaleContractAttributionMap> LoadForPurchaseContractAsync(
        int purchaseContractId,
        CancellationToken ct = default)
    {
        // ابتدا فروش‌هایی که این قرارداد در آن‌ها سهم دارد، بعد همهٔ سهم‌های همان فروش‌ها؛
        // بدون گام دوم نمی‌شد فهمید فروش چند-قراردادی است یا تک‌قراردادی.
        var saleIds = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => a.SourcePurchaseContractId == purchaseContractId)
            .Select(a => a.SalesTransactionId)
            .Distinct()
            .ToListAsync(ct);

        return await LoadForSalesAsync(saleIds, ct);
    }

    /// <summary>
    /// قاعدهٔ واحد (پیش از این در کنترلر گزارش بود و صورت‌حساب شراکت قاعدهٔ دیگری داشت):
    /// <list type="number">
    /// <item>فروشی که سهمِ اثبات‌شده دارد (<c>SalesTransactionSourceAllocations</c>) به همان سهم‌ها
    /// شکسته می‌شود.</item>
    /// <item>فروشِ اثبات‌نشده فقط از lineage واقعی پیدا می‌شود، به این ترتیب: تخصیصِ فروشِ مستقیمِ
    /// رسید، حرکتِ خروجِ موجودی، lineage موتر/حملِ در جریان، و در آخر
    /// <c>SalesTransaction.SourcePurchaseContractId</c> که خودِ فرم فروش فقط برای فروشِ
    /// تک‌قراردادی از حرکتِ واقعیِ خروج پر می‌کند. هر فروش یک بار و کامل روی نخستین قراردادِ
    /// پیوندخورده می‌نشیند؛ «نخستین» از همهٔ پیوندهای خودِ فروش انتخاب می‌شود تا نتیجه به فهرستِ
    /// قراردادهای درخواست‌شده بستگی نداشته باشد.</item>
    /// <item>فروشِ سطحِ محموله بدونِ هیچ پیوندِ بالا: سهمِ حمل‌های هر قرارداد به نسبتِ مقدار
    /// (InventoryTransportPnlService، همان قاعدهٔ پروندهٔ محموله).</item>
    /// </list>
    /// <c>SalesTransaction.ContractId</c> و <c>LedgerEntry(Sale).ContractId</c> فقط وقتی مبنا هستند که به
    /// قرارداد <b>خرید</b> اشاره کنند (پیوندِ ثبت‌شدهٔ سیستم، پایین‌ترین اولویت)؛ قرارداد فروش هرگز
    /// قرارداد منبع نیست. هیچ قراردادی حدس زده نمی‌شود.
    /// </summary>
    public async Task<PurchaseContractSales> LoadPurchaseContractSalesAsync(
        IReadOnlyCollection<int> purchaseContractIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(purchaseContractIds);
        var ids = purchaseContractIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return PurchaseContractSales.Empty;
        }

        var mismatchByContract = await _db.LoadingReceiptAllocations.AsNoTracking()
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectSale
                && a.SourcePurchaseContractId.HasValue
                && ids.Contains(a.SourcePurchaseContractId.Value)
                && a.SalesTransactionId.HasValue
                && a.SalesTransaction != null
                && !a.SalesTransaction.IsCancelled
                && a.QuantityMt != a.SalesTransaction.QuantityMt)
            .GroupBy(a => a.SourcePurchaseContractId!.Value)
            .Select(g => new { ContractId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ContractId, x => x.Count, ct);

        // گام ۱: فروش‌هایی که به قراردادهای خواسته‌شده ربط دارند (هر مسیر).
        var requestedLinks = await LoadSaleLinksAsync(contractIds: ids, saleIds: null, ct);
        var provenSaleIds = await _db.SalesTransactionSourceAllocations.AsNoTracking()
            .Where(a => ids.Contains(a.SourcePurchaseContractId))
            .Select(a => a.SalesTransactionId)
            .Distinct()
            .ToListAsync(ct);
        var shipmentShares = await LoadShipmentSaleSharesAsync(ids, ct);

        var saleIds = requestedLinks.SelectMany(path => path).Select(l => l.SaleId)
            .Concat(provenSaleIds)
            .Concat(shipmentShares.Select(x => x.SaleId))
            .Distinct()
            .ToList();
        if (saleIds.Count == 0)
        {
            return new PurchaseContractSales(new Dictionary<int, IReadOnlyList<AttributedContractSale>>(), mismatchByContract);
        }

        // فروش با قیمت باز هنوز «فروش تحقق‌یافته» نیست: مقدارش در سهم فروش قرارداد نمی‌نشیند و
        // بهایش در بهای فروش‌نرفته می‌ماند تا قیمت تعیین شود (هیچ عاید صفری کنار بهای کامل نمی‌نشیند).
        var saleRows = await _db.SalesTransactions.AsNoTracking()
            .Where(s => (!_reportBefore.HasValue || s.SaleDate < _reportBefore.Value) && !s.IsCancelled && !s.IsSalePricePending && saleIds.Contains(s.Id))
            .Select(s => new { s.Id, s.SaleDate, s.InvoiceNumber, s.QuantityMt, s.TotalUsd })
            .ToDictionaryAsync(s => s.Id, ct);
        var attribution = await LoadForSalesAsync(saleIds, ct);

        // گام ۲: مالکِ فروشِ اثبات‌نشده از همهٔ پیوندهای خودِ فروش انتخاب می‌شود، نه فقط پیوند به
        // قراردادهای خواسته‌شده؛ وگرنه پروندهٔ یک قرارداد و گزارشِ همهٔ قراردادها یک فروش را به دو
        // قراردادِ مختلف می‌دادند. قاعده: نخستین مسیری که فروش را دیده، و در آن کوچک‌ترین شناسهٔ قرارداد.
        var unprovenSaleIds = saleIds.Where(id => !attribution.HasProvenAllocation(id)).ToList();
        var ownerBySale = new Dictionary<int, int>();
        if (unprovenSaleIds.Count > 0)
        {
            foreach (var path in await LoadSaleLinksAsync(contractIds: null, saleIds: unprovenSaleIds, ct))
            {
                foreach (var group in path.GroupBy(l => l.SaleId).Where(g => !ownerBySale.ContainsKey(g.Key)))
                {
                    ownerBySale[group.Key] = group.Min(l => l.ContractId);
                }
            }
        }

        var shipmentSharesBySale = shipmentShares.ToLookup(x => x.SaleId);
        var requested = ids.ToHashSet();
        var result = new Dictionary<int, List<AttributedContractSale>>();
        void Add(int contractId, AttributedContractSale sale)
        {
            if (!result.TryGetValue(contractId, out var list))
            {
                list = [];
                result[contractId] = list;
            }
            list.Add(sale);
        }

        foreach (var saleId in saleIds.Order())
        {
            if (!saleRows.TryGetValue(saleId, out var sale))
            {
                continue;
            }

            if (attribution.HasProvenAllocation(saleId))
            {
                foreach (var share in attribution.SharesFor(saleId).Where(sh => requested.Contains(sh.SourcePurchaseContractId)))
                {
                    Add(share.SourcePurchaseContractId, new AttributedContractSale(
                        saleId, sale.SaleDate, sale.InvoiceNumber, share.QuantityMt, share.AmountUsd, IsProvenShare: true));
                }
                continue;
            }

            if (ownerBySale.TryGetValue(saleId, out var ownerContractId))
            {
                if (requested.Contains(ownerContractId))
                {
                    Add(ownerContractId, new AttributedContractSale(
                        saleId, sale.SaleDate, sale.InvoiceNumber, sale.QuantityMt, sale.TotalUsd, IsProvenShare: false));
                }
                continue;
            }

            // فروشِ سطحِ محموله که هیچ پیوندِ دیگری ندارد: سهمِ هر حمل به نسبتِ مقدار
            // (همان قاعدهٔ پروندهٔ محموله، InventoryTransportPnlService).
            foreach (var share in shipmentSharesBySale[saleId].Where(x => requested.Contains(x.ContractId)))
            {
                Add(share.ContractId, new AttributedContractSale(
                    saleId, sale.SaleDate, sale.InvoiceNumber, share.QuantityMt, share.AmountUsd, IsProvenShare: false));
            }
        }

        return new PurchaseContractSales(
            result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AttributedContractSale>)kv.Value
                .OrderBy(x => x.SaleDate).ThenBy(x => x.SalesTransactionId).ToList()),
            mismatchByContract);
    }

    /// <summary>
    /// پیوندهای (قرارداد خرید، فروش) به ترتیبِ اولویتِ مسیر: فروشِ مستقیمِ رسید، خروجِ موجودی،
    /// lineage موتر/حملِ در جریان، و SourcePurchaseContractId. یا بر پایهٔ قرارداد یا بر پایهٔ فروش.
    /// </summary>
    private async Task<List<SaleLink>[]> LoadSaleLinksAsync(
        List<int>? contractIds,
        List<int>? saleIds,
        CancellationToken ct)
    {
        var byContract = contractIds is not null;
        var cIds = contractIds ?? [];
        var sIds = saleIds ?? [];

        var directQuery = _db.LoadingReceiptAllocations.AsNoTracking()
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectSale
                && a.SourcePurchaseContractId.HasValue
                && a.SalesTransactionId.HasValue
                && a.SalesTransaction != null
                && !a.SalesTransaction.IsCancelled);
        directQuery = byContract
            ? directQuery.Where(a => cIds.Contains(a.SourcePurchaseContractId!.Value))
            : directQuery.Where(a => sIds.Contains(a.SalesTransactionId!.Value));
        var directLinks = await directQuery
            .Select(a => new SaleLink(a.SourcePurchaseContractId!.Value, a.SalesTransactionId!.Value))
            .Distinct()
            .ToListAsync(ct);

        var stockQuery = _db.InventoryMovements.AsNoTracking()
            .Where(m => m.Direction == MovementDirection.Out
                && m.SalesTransactionId.HasValue
                && m.ContractId.HasValue);
        stockQuery = byContract
            ? stockQuery.Where(m => cIds.Contains(m.ContractId!.Value))
            : stockQuery.Where(m => sIds.Contains(m.SalesTransactionId!.Value));
        var stockLinks = await stockQuery
            .Select(m => new SaleLink(m.ContractId!.Value, m.SalesTransactionId!.Value))
            .Distinct()
            .ToListAsync(ct);

        var inTransitLinks = new List<SaleLink>();
        var saleDispatchQuery = _db.SalesTransactions.AsNoTracking()
            .Where(s => (!_reportBefore.HasValue || s.SaleDate < _reportBefore.Value) && !s.IsCancelled
                && s.TruckDispatchId.HasValue
                && s.TruckDispatch != null
                && s.TruckDispatch.Status != DispatchStatus.Cancelled);
        saleDispatchQuery = byContract
            ? saleDispatchQuery.Where(s => cIds.Contains(s.TruckDispatch!.ContractId))
            : saleDispatchQuery.Where(s => sIds.Contains(s.Id));
        inTransitLinks.AddRange(await saleDispatchQuery
            .Select(s => new SaleLink(s.TruckDispatch!.ContractId, s.Id))
            .ToListAsync(ct));
        var legacyDispatchQuery = _db.TruckDispatches.AsNoTracking()
            .Where(d => d.Status != DispatchStatus.Cancelled
                && d.SalesTransactionId.HasValue
                && d.SalesTransaction != null
                && !d.SalesTransaction.IsCancelled);
        legacyDispatchQuery = byContract
            ? legacyDispatchQuery.Where(d => cIds.Contains(d.ContractId))
            : legacyDispatchQuery.Where(d => sIds.Contains(d.SalesTransactionId!.Value));
        inTransitLinks.AddRange(await legacyDispatchQuery
            .Select(d => new SaleLink(d.ContractId, d.SalesTransactionId!.Value))
            .ToListAsync(ct));
        var transportReceiptQuery = _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.SalesTransactionId.HasValue
                && r.SalesTransaction != null
                && !r.SalesTransaction.IsCancelled
                && r.InventoryTransportLeg != null);
        transportReceiptQuery = byContract
            ? transportReceiptQuery.Where(r => cIds.Contains(r.InventoryTransportLeg!.SourcePurchaseContractId))
            : transportReceiptQuery.Where(r => sIds.Contains(r.SalesTransactionId!.Value));
        inTransitLinks.AddRange(await transportReceiptQuery
            .Select(r => new SaleLink(r.InventoryTransportLeg!.SourcePurchaseContractId, r.SalesTransactionId!.Value))
            .ToListAsync(ct));

        // پیوندِ ثبت‌شده روی خودِ سند فروش: SourcePurchaseContractId، و فروشِ قدیمی که ContractId اش
        // خودِ قرارداد خرید است (نه قرارداد فروش).
        var explicitQuery = _db.SalesTransactions.AsNoTracking()
            .Where(s => (!_reportBefore.HasValue || s.SaleDate < _reportBefore.Value) && !s.IsCancelled
                && (s.SourcePurchaseContractId.HasValue
                    || (s.Contract != null && s.Contract.ContractType == ContractType.Purchase)));
        explicitQuery = byContract
            ? explicitQuery.Where(s => cIds.Contains(s.SourcePurchaseContractId ?? s.ContractId!.Value))
            : explicitQuery.Where(s => sIds.Contains(s.Id));
        var explicitSourceLinks = await explicitQuery
            .Select(s => new SaleLink(s.SourcePurchaseContractId ?? s.ContractId!.Value, s.Id))
            .ToListAsync(ct);

        // ردیفِ لجرِ فروش که سیستم هنگامِ ثبت با قراردادِ منبع (دیسپچ، فروش گروهی، رسید حمل) ساخته؛
        // برای فروش‌های گروهیِ قدیمی تنها پیوندِ ثبت‌شده همین است. فقط وقتی قرارداد خرید باشد.
        var ledgerQuery =
            from l in _db.LedgerEntries.AsNoTracking()
            join s in _db.SalesTransactions.AsNoTracking() on l.SourceId equals s.Id
            where l.SourceType == SaleLedgerSourceType
                && !s.IsCancelled
                && l.ContractId.HasValue
                && l.Contract != null
                && l.Contract.ContractType == ContractType.Purchase
            select new { ContractId = l.ContractId!.Value, SaleId = s.Id };
        ledgerQuery = byContract
            ? ledgerQuery.Where(x => cIds.Contains(x.ContractId))
            : ledgerQuery.Where(x => sIds.Contains(x.SaleId));
        var ledgerLinks = (await ledgerQuery.Distinct().ToListAsync(ct))
            .Select(x => new SaleLink(x.ContractId, x.SaleId))
            .ToList();

        return [directLinks, stockLinks, inTransitLinks.Distinct().ToList(), explicitSourceLinks, ledgerLinks];
    }

    private const string SaleLedgerSourceType = "Sale";

    /// <summary>
    /// سهمِ حمل‌های موجودیِ هر قرارداد از فروش‌ها، از InventoryTransportPnlService (مرجعِ پروندهٔ
    /// محموله). همیشه همهٔ حمل‌های هر محموله با هم خوانده می‌شوند تا سهمِ هر حمل به این بستگی نداشته
    /// باشد که کدام قراردادها درخواست شده‌اند.
    /// </summary>
    private async Task<List<(int ContractId, int SaleId, decimal QuantityMt, decimal AmountUsd)>> LoadShipmentSaleSharesAsync(
        List<int> contractIds,
        CancellationToken ct)
    {
        var legRows = await _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => contractIds.Contains(l.SourcePurchaseContractId))
            .Select(l => new { l.Id, l.ShipmentId, ContractId = l.SourcePurchaseContractId })
            .ToListAsync(ct);
        if (legRows.Count == 0)
        {
            return [];
        }

        var shipmentIds = legRows.Where(l => l.ShipmentId.HasValue).Select(l => l.ShipmentId!.Value).Distinct().ToList();
        var shipmentLegIds = shipmentIds.Count == 0
            ? []
            : await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => l.ShipmentId.HasValue && shipmentIds.Contains(l.ShipmentId.Value))
                .Select(l => l.Id)
                .ToListAsync(ct);
        var looseLegIds = legRows.Where(l => !l.ShipmentId.HasValue).Select(l => l.Id).ToList();

        var transportPnl = new InventoryTransportPnlService(_db);
        var pnlByLeg = new Dictionary<int, InventoryTransportPnlSnapshot>();
        foreach (var batch in new[] { shipmentLegIds, looseLegIds }.Where(b => b.Count > 0))
        {
            foreach (var (legId, snapshot) in await transportPnl.BuildForLegsAsync(batch, ct))
            {
                pnlByLeg[legId] = snapshot;
            }
        }

        return legRows
            .Where(l => pnlByLeg.ContainsKey(l.Id))
            .SelectMany(l => pnlByLeg[l.Id].Sales.Select(sale => (l.ContractId, sale.SaleId, sale.QuantityMt, sale.AmountUsd)))
            .GroupBy(x => (x.ContractId, x.SaleId))
            .Select(g => (g.Key.ContractId, g.Key.SaleId, g.Sum(x => x.QuantityMt), g.Sum(x => x.AmountUsd)))
            .ToList();
    }

    private sealed record SaleLink(int ContractId, int SaleId);

    private static SaleContractAttributionMap Build(IReadOnlyCollection<SaleContractShare> rows)
    {
        if (rows.Count == 0)
        {
            return SaleContractAttributionMap.Empty;
        }

        var bySale = rows
            .GroupBy(r => r.SalesTransactionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SaleContractShare>)g
                    .GroupBy(r => r.SourcePurchaseContractId)
                    .Select(c => new SaleContractShare(
                        g.Key,
                        c.Key,
                        c.Sum(r => r.QuantityMt),
                        c.Sum(r => r.AmountUsd)))
                    .OrderBy(c => c.SourcePurchaseContractId)
                    .ToList());

        return new SaleContractAttributionMap(bySale);
    }
}
