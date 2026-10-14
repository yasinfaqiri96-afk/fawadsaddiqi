using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Time;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// How confidently an active pre-sale commitment can be attributed to a physical
/// stock source. The schema carries company and product on <see cref="PreSaleOrder"/>
/// but no purchase contract, terminal, tank or lot, so anything below
/// <see cref="Scoped"/> is reported as unallocated instead of being guessed.
/// </summary>
public enum ReservationAttribution
{
    /// <summary>Company and product are both known.</summary>
    Scoped = 1,

    /// <summary>Product known, company unknown — the reservation cannot be netted against a company stock pool.</summary>
    Unallocated = 2
}

public sealed record PreSaleReservationRow(
    int? CompanyId,
    string CompanyName,
    int ProductId,
    string ProductName,
    decimal CommittedMt,
    decimal DeliveredMt,
    decimal ReservedMt,
    int OrderCount,
    ReservationAttribution Attribution);

public sealed record SellableStockRow(
    int? CompanyId,
    string CompanyName,
    int ProductId,
    string ProductName,
    decimal PhysicalStockMt,
    decimal ReservedMt,
    decimal UnallocatedReservedMt,
    ReservationAttribution Attribution)
{
    /// <summary>Physical stock minus valid active reservations. Never inflated by in-transit goods.</summary>
    public decimal SellableMt => decimal.Round(PhysicalStockMt - ReservedMt, 4, MidpointRounding.AwayFromZero);

    public bool IsOverReserved => ReservedMt > PhysicalStockMt;
}

public enum PreSaleDiscrepancyKind
{
    OverDelivery = 1,
    DeliveryWithoutPreSale = 2,
    PreSaleWithoutStockSource = 3,
    ReservationExceedsStock = 4,
    OverdueUndelivered = 5,
    CancelledDeliveryWithEffect = 6,
    UnconsumedAdvance = 7,
    AllocationExceedsReceipt = 8
}

public sealed record PreSaleDiscrepancyRow(
    PreSaleDiscrepancyKind Kind,
    string Title,
    string Detail,
    decimal? QuantityMt,
    decimal? AmountUsd,
    DateTime? DocumentDate,
    string DocumentController,
    string DocumentAction,
    int DocumentId);

public sealed record PreSaleDiscrepancySummary(PreSaleDiscrepancyKind Kind, string Title, int Count);

public interface IPreSaleReservationService
{
    Task<IReadOnlyList<PreSaleReservationRow>> GetReservationsAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default);

    Task<IReadOnlyList<SellableStockRow>> GetSellableStockAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default);

    Task<IReadOnlyList<PreSaleDiscrepancySummary>> GetDiscrepancySummaryAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default);

    Task<IReadOnlyList<PreSaleDiscrepancyRow>> GetDiscrepanciesAsync(
        ManagementReportFilterViewModel filter,
        PreSaleDiscrepancyKind kind,
        int skip,
        int take,
        CancellationToken ct = default);
}

/// <summary>
/// Single reader for pre-sale reservation, sellable stock and pre-sale discrepancies.
///
/// Reservation rules enforced here:
///   * only <see cref="PreSaleOrderStatus.Confirmed"/> and
///     <see cref="PreSaleOrderStatus.PartiallyDelivered"/> reserve stock;
///   * cancelled deliveries never consume a commitment;
///   * reservation is clamped to the commitment, so over-delivery cannot create a
///     negative reservation and under-delivery cannot reserve more than promised;
///   * sellable = physical stock (InventoryMovement only) − valid active reservations;
///     goods in transit are not in <see cref="InventoryMovement"/> yet and are never added.
/// </summary>
public sealed class PreSaleReservationService : IPreSaleReservationService
{
    private const string UnallocatedCompanyName = "بدون جواز مشخص";

    private readonly ApplicationDbContext _db;
    private readonly IAfghanistanBusinessClock _clock;

    public PreSaleReservationService(ApplicationDbContext db, IAfghanistanBusinessClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PreSaleReservationRow>> GetReservationsAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var rows = await BuildActiveOrderQuery(filter)
            .Select(o => new
            {
                o.CompanyId,
                CompanyName = o.Company != null ? o.Company.Name : null,
                o.ProductId,
                ProductName = o.Product != null ? o.Product.Name : "",
                o.QuantityMt,
                DeliveredMt = _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => (decimal?)s.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => new { r.CompanyId, r.ProductId })
            .Select(g => new PreSaleReservationRow(
                g.Key.CompanyId,
                g.First().CompanyName ?? UnallocatedCompanyName,
                g.Key.ProductId,
                g.First().ProductName,
                CommittedMt: Round(g.Sum(r => r.QuantityMt)),
                DeliveredMt: Round(g.Sum(r => r.DeliveredMt)),
                // هر سفارش جداگانه clamp می‌شود تا تحویلِ بیش از تعهدِ یک سفارش،
                // رزروِ سفارش دیگر را پنهان نکند.
                ReservedMt: Round(g.Sum(r => Math.Max(0m, r.QuantityMt - r.DeliveredMt))),
                OrderCount: g.Count(),
                Attribution: g.Key.CompanyId.HasValue
                    ? ReservationAttribution.Scoped
                    : ReservationAttribution.Unallocated))
            .OrderBy(r => r.ProductName)
            .ThenBy(r => r.CompanyName)
            .ToList();
    }

    public async Task<IReadOnlyList<SellableStockRow>> GetSellableStockAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var stockQuery = _db.InventoryMovements.AsNoTracking().AsQueryable();
        if (filter.ProductId.HasValue) stockQuery = stockQuery.Where(m => m.ProductId == filter.ProductId.Value);
        if (filter.TerminalId.HasValue) stockQuery = stockQuery.Where(m => m.TerminalId == filter.TerminalId.Value);
        if (filter.StorageTankId.HasValue) stockQuery = stockQuery.Where(m => m.StorageTankId == filter.StorageTankId.Value);
        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            stockQuery = stockQuery.Where(m => m.MovementDate < end);
        }

        var stockRows = await stockQuery
            .Quantities()
            .GroupBy(m => new { m.CompanyId, m.ProductId, m.ProductName })
            .Select(g => new
            {
                g.Key.CompanyId,
                g.Key.ProductId,
                g.Key.ProductName,
                QuantityMt = g.Sum(m => m.SignedQuantityMt)
            })
            .ToListAsync(ct);

        var companyIds = stockRows.Where(r => r.CompanyId.HasValue).Select(r => r.CompanyId!.Value).Distinct().ToArray();
        var companyNames = companyIds.Length == 0
            ? new Dictionary<int, string>()
            : await _db.Companies.AsNoTracking()
                .Where(c => companyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var reservations = await GetReservationsAsync(filter, ct);
        var scopedReservation = reservations
            .Where(r => r.Attribution == ReservationAttribution.Scoped)
            .ToDictionary(r => (r.CompanyId, r.ProductId), r => r.ReservedMt);
        // رزروی که جوازش مشخص نیست به هیچ حوضچهٔ شرکتی کم نمی‌شود؛ فقط در همان محصول
        // به‌صورت جداگانه نمایش داده می‌شود تا رقم ساختگی ساخته نشود.
        var unallocatedReservation = reservations
            .Where(r => r.Attribution == ReservationAttribution.Unallocated)
            .GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.ReservedMt));

        var result = stockRows
            .Select(r =>
            {
                var reserved = scopedReservation.GetValueOrDefault((r.CompanyId, r.ProductId));
                return new SellableStockRow(
                    r.CompanyId,
                    r.CompanyId.HasValue
                        ? companyNames.GetValueOrDefault(r.CompanyId.Value, UnallocatedCompanyName)
                        : UnallocatedCompanyName,
                    r.ProductId,
                    r.ProductName,
                    PhysicalStockMt: Round(r.QuantityMt),
                    ReservedMt: Round(reserved),
                    UnallocatedReservedMt: Round(unallocatedReservation.GetValueOrDefault(r.ProductId)),
                    Attribution: r.CompanyId.HasValue
                        ? ReservationAttribution.Scoped
                        : ReservationAttribution.Unallocated);
            })
            .ToList();

        // تعهدهایی که هیچ موجودی متناظر ندارند نباید از گزارش حذف شوند؛ موجودی صفر و
        // رزرو مثبت یعنی «تعهد بدون منبع موجودی».
        var missingScopes = scopedReservation.Keys
            .Where(key => !result.Any(r => r.CompanyId == key.CompanyId && r.ProductId == key.ProductId))
            .ToList();
        foreach (var key in missingScopes)
        {
            var reservation = reservations.First(r => r.CompanyId == key.CompanyId && r.ProductId == key.ProductId);
            result.Add(new SellableStockRow(
                reservation.CompanyId,
                reservation.CompanyName,
                reservation.ProductId,
                reservation.ProductName,
                PhysicalStockMt: 0m,
                ReservedMt: reservation.ReservedMt,
                UnallocatedReservedMt: Round(unallocatedReservation.GetValueOrDefault(reservation.ProductId)),
                Attribution: ReservationAttribution.Scoped));
        }

        return result
            .OrderBy(r => r.ProductName)
            .ThenBy(r => r.CompanyName)
            .ToList();
    }

    public async Task<IReadOnlyList<PreSaleDiscrepancySummary>> GetDiscrepancySummaryAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var summary = new List<PreSaleDiscrepancySummary>();
        foreach (var kind in Enum.GetValues<PreSaleDiscrepancyKind>())
        {
            var count = await CountAsync(filter, kind, ct);
            summary.Add(new PreSaleDiscrepancySummary(kind, TitleOf(kind), count));
        }

        return summary;
    }

    public async Task<IReadOnlyList<PreSaleDiscrepancyRow>> GetDiscrepanciesAsync(
        ManagementReportFilterViewModel filter,
        PreSaleDiscrepancyKind kind,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);

        return kind switch
        {
            PreSaleDiscrepancyKind.OverDelivery => await OverDeliveryAsync(filter, skip, take, ct),
            PreSaleDiscrepancyKind.DeliveryWithoutPreSale => await DeliveryWithoutPreSaleAsync(filter, skip, take, ct),
            PreSaleDiscrepancyKind.PreSaleWithoutStockSource => await PreSaleWithoutStockSourceAsync(filter, skip, take, ct),
            PreSaleDiscrepancyKind.ReservationExceedsStock => await ReservationExceedsStockAsync(filter, skip, take, ct),
            PreSaleDiscrepancyKind.OverdueUndelivered => await OverdueUndeliveredAsync(filter, skip, take, ct),
            PreSaleDiscrepancyKind.CancelledDeliveryWithEffect => await CancelledDeliveryWithEffectAsync(filter, skip, take, ct),
            PreSaleDiscrepancyKind.UnconsumedAdvance => await UnconsumedAdvanceAsync(filter, skip, take, ct),
            PreSaleDiscrepancyKind.AllocationExceedsReceipt => await AllocationExceedsReceiptAsync(filter, skip, take, ct),
            _ => []
        };
    }

    private async Task<int> CountAsync(
        ManagementReportFilterViewModel filter,
        PreSaleDiscrepancyKind kind,
        CancellationToken ct)
        => kind switch
        {
            PreSaleDiscrepancyKind.OverDelivery => await OverDeliveryQuery(filter).CountAsync(ct),
            PreSaleDiscrepancyKind.DeliveryWithoutPreSale => await DeliveryWithoutPreSaleQuery(filter).CountAsync(ct),
            PreSaleDiscrepancyKind.PreSaleWithoutStockSource => (await PreSaleWithoutStockSourceAsync(filter, 0, 200, ct)).Count,
            PreSaleDiscrepancyKind.ReservationExceedsStock => (await ReservationExceedsStockAsync(filter, 0, 200, ct)).Count,
            PreSaleDiscrepancyKind.OverdueUndelivered => await OverdueUndeliveredQuery(filter).CountAsync(ct),
            PreSaleDiscrepancyKind.CancelledDeliveryWithEffect => await CancelledDeliveryWithEffectQuery(filter).CountAsync(ct),
            PreSaleDiscrepancyKind.UnconsumedAdvance => await UnconsumedAdvanceQuery(filter).CountAsync(ct),
            PreSaleDiscrepancyKind.AllocationExceedsReceipt => await AllocationExceedsReceiptQuery(filter).CountAsync(ct),
            _ => 0
        };

    // ---------------------------------------------------------------- 1. Over-delivery

    private IQueryable<PreSaleOrder> OverDeliveryQuery(ManagementReportFilterViewModel filter)
        => BuildOrderQuery(filter)
            .Where(o => o.Status != PreSaleOrderStatus.Cancelled
                && _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => (decimal?)s.QuantityMt).GetValueOrDefault() > o.QuantityMt);

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> OverDeliveryAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var rows = await OverDeliveryQuery(filter)
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.Id)
            .Skip(skip).Take(take)
            .Select(o => new
            {
                o.Id,
                o.OrderNumber,
                o.OrderDate,
                o.QuantityMt,
                CustomerName = o.Customer != null ? o.Customer.Name : "",
                ProductName = o.Product != null ? o.Product.Name : "",
                DeliveredMt = _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => (decimal?)s.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);

        return rows.Select(o => new PreSaleDiscrepancyRow(
            PreSaleDiscrepancyKind.OverDelivery,
            $"پیش‌فروش {o.OrderNumber}",
            $"{o.CustomerName} — {o.ProductName} — تعهد {o.QuantityMt:N4} MT، تحویل {o.DeliveredMt:N4} MT",
            QuantityMt: Round(o.DeliveredMt - o.QuantityMt),
            AmountUsd: null,
            DocumentDate: o.OrderDate,
            DocumentController: "Sales",
            DocumentAction: "PreSaleDetails",
            DocumentId: o.Id)).ToList();
    }

    // ------------------------------------------- 2. Delivery that is very likely a pre-sale delivery but is not linked

    private IQueryable<SalesTransaction> DeliveryWithoutPreSaleQuery(ManagementReportFilterViewModel filter)
    {
        var query = _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.PreSaleOrderId == null);
        query = ApplySaleDateFilter(query, filter);
        if (filter.ProductId.HasValue) query = query.Where(s => s.ProductId == filter.ProductId.Value);
        if (filter.CustomerId.HasValue) query = query.Where(s => s.CustomerId == filter.CustomerId.Value);

        // فقط وقتی همان مشتری و همان جنس یک تعهدِ باز و پوشا دارد این فروش «تحویلِ
        // احتمالاً بدون پیش‌فروش» است. بدون این شرط، هر فروش نقدی هم مغایرت می‌شد.
        return query.Where(s => _db.PreSaleOrders.Any(o =>
            o.CustomerId == s.CustomerId
            && o.ProductId == s.ProductId
            && (o.Status == PreSaleOrderStatus.Confirmed || o.Status == PreSaleOrderStatus.PartiallyDelivered)
            && o.OrderDate <= s.SaleDate));
    }

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> DeliveryWithoutPreSaleAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var rows = await DeliveryWithoutPreSaleQuery(filter)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Skip(skip).Take(take)
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.SaleDate,
                s.QuantityMt,
                s.TotalUsd,
                CustomerName = s.Customer != null ? s.Customer.Name : "",
                ProductName = s.Product != null ? s.Product.Name : ""
            })
            .ToListAsync(ct);

        return rows.Select(s => new PreSaleDiscrepancyRow(
            PreSaleDiscrepancyKind.DeliveryWithoutPreSale,
            $"فروش {s.InvoiceNumber}",
            $"{s.CustomerName} — {s.ProductName} — تعهد باز برای همین مشتری و جنس وجود دارد اما این فروش به آن وصل نیست",
            QuantityMt: Round(s.QuantityMt),
            AmountUsd: Round(s.TotalUsd),
            DocumentDate: s.SaleDate,
            DocumentController: "Sales",
            DocumentAction: "Details",
            DocumentId: s.Id)).ToList();
    }

    // ---------------------------------------------------- 3. Pre-sale with no attributable stock source

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> PreSaleWithoutStockSourceAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var reservations = await GetReservationsAsync(filter, ct);
        return reservations
            .Where(r => r.Attribution == ReservationAttribution.Unallocated && r.ReservedMt > 0m)
            .OrderByDescending(r => r.ReservedMt)
            .Skip(skip).Take(take)
            .Select(r => new PreSaleDiscrepancyRow(
                PreSaleDiscrepancyKind.PreSaleWithoutStockSource,
                r.ProductName,
                $"{r.OrderCount:N0} تعهد بدون جواز مشخص — منبع موجودی قابل تعیین نیست",
                QuantityMt: r.ReservedMt,
                AmountUsd: null,
                DocumentDate: null,
                DocumentController: "Sales",
                DocumentAction: "PreSales",
                DocumentId: 0))
            .ToList();
    }

    // ------------------------------------------------------------- 4. Reservation above physical stock

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> ReservationExceedsStockAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var rows = await GetSellableStockAsync(filter, ct);
        return rows
            .Where(r => r.IsOverReserved)
            .OrderByDescending(r => r.ReservedMt - r.PhysicalStockMt)
            .Skip(skip).Take(take)
            .Select(r => new PreSaleDiscrepancyRow(
                PreSaleDiscrepancyKind.ReservationExceedsStock,
                $"{r.ProductName} — {r.CompanyName}",
                $"موجودی {r.PhysicalStockMt:N4} MT، رزرو فعال {r.ReservedMt:N4} MT",
                QuantityMt: Round(r.ReservedMt - r.PhysicalStockMt),
                AmountUsd: null,
                DocumentDate: null,
                DocumentController: "Sales",
                DocumentAction: "PreSales",
                DocumentId: 0))
            .ToList();
    }

    // --------------------------------------------------------------- 5. Overdue undelivered commitment

    private IQueryable<PreSaleOrder> OverdueUndeliveredQuery(ManagementReportFilterViewModel filter)
    {
        var today = _clock.Today;
        return BuildActiveOrderQuery(filter)
            .Where(o => o.ExpectedDeliveryTo != null
                && o.ExpectedDeliveryTo < today
                && o.QuantityMt > _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => (decimal?)s.QuantityMt).GetValueOrDefault());
    }

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> OverdueUndeliveredAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var rows = await OverdueUndeliveredQuery(filter)
            .OrderBy(o => o.ExpectedDeliveryTo)
            .ThenBy(o => o.Id)
            .Skip(skip).Take(take)
            .Select(o => new
            {
                o.Id,
                o.OrderNumber,
                o.ExpectedDeliveryTo,
                o.QuantityMt,
                CustomerName = o.Customer != null ? o.Customer.Name : "",
                ProductName = o.Product != null ? o.Product.Name : "",
                DeliveredMt = _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => (decimal?)s.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);

        return rows.Select(o => new PreSaleDiscrepancyRow(
            PreSaleDiscrepancyKind.OverdueUndelivered,
            $"پیش‌فروش {o.OrderNumber}",
            $"{o.CustomerName} — {o.ProductName} — سررسید {o.ExpectedDeliveryTo.ToCalendarString("yyyy-MM-dd")}",
            QuantityMt: Round(o.QuantityMt - o.DeliveredMt),
            AmountUsd: null,
            DocumentDate: o.ExpectedDeliveryTo,
            DocumentController: "Sales",
            DocumentAction: "PreSaleDetails",
            DocumentId: o.Id)).ToList();
    }

    // ---------------------------------------- 6. Cancelled delivery that still carries ledger/inventory effect

    private IQueryable<SalesTransaction> CancelledDeliveryWithEffectQuery(ManagementReportFilterViewModel filter)
    {
        var query = _db.SalesTransactions.AsNoTracking()
            .Where(s => s.IsCancelled && s.PreSaleOrderId != null);
        query = ApplySaleDateFilter(query, filter);
        if (filter.ProductId.HasValue) query = query.Where(s => s.ProductId == filter.ProductId.Value);
        if (filter.CustomerId.HasValue) query = query.Where(s => s.CustomerId == filter.CustomerId.Value);

        return query.Where(s => _db.InventoryMovements.Any(m => m.SalesTransactionId == s.Id));
    }

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> CancelledDeliveryWithEffectAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var rows = await CancelledDeliveryWithEffectQuery(filter)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Skip(skip).Take(take)
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.SaleDate,
                s.TotalUsd,
                CustomerName = s.Customer != null ? s.Customer.Name : "",
                MovementQuantityMt = _db.InventoryMovements
                    .Where(m => m.SalesTransactionId == s.Id)
                    .Sum(m => (decimal?)m.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);

        return rows.Select(s => new PreSaleDiscrepancyRow(
            PreSaleDiscrepancyKind.CancelledDeliveryWithEffect,
            $"فروش لغوشده {s.InvoiceNumber}",
            $"{s.CustomerName} — حرکت موجودی مرتبط هنوز وجود دارد",
            QuantityMt: Round(s.MovementQuantityMt),
            AmountUsd: Round(s.TotalUsd),
            DocumentDate: s.SaleDate,
            DocumentController: "Sales",
            DocumentAction: "Details",
            DocumentId: s.Id)).ToList();
    }

    // ---------------------------------------------------------------------- 7. Unconsumed advance

    private IQueryable<CustomerPaymentAllocation> UnconsumedAdvanceQuery(ManagementReportFilterViewModel filter)
    {
        var query = _db.CustomerPaymentAllocations.AsNoTracking()
            .Where(a => a.Status == CustomerPaymentAllocationStatus.Active);
        if (filter.FromDate.HasValue) query = query.Where(a => a.AllocationDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(a => a.AllocationDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.CustomerId.HasValue)
        {
            query = query.Where(a => a.PreSaleOrder != null && a.PreSaleOrder.CustomerId == filter.CustomerId.Value);
        }

        return query.Where(a => a.AllocatedAmountUsd > _db.CustomerPaymentAllocationApplications
            .Where(x => x.CustomerPaymentAllocationId == a.Id
                && x.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .Sum(x => (decimal?)x.AppliedAmountUsd).GetValueOrDefault());
    }

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> UnconsumedAdvanceAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var rows = await UnconsumedAdvanceQuery(filter)
            .OrderByDescending(a => a.AllocationDate)
            .ThenByDescending(a => a.Id)
            .Skip(skip).Take(take)
            .Select(a => new
            {
                a.Id,
                a.PreSaleOrderId,
                a.AllocationDate,
                a.AllocatedAmountUsd,
                OrderNumber = a.PreSaleOrder != null ? a.PreSaleOrder.OrderNumber : "",
                CustomerName = a.PreSaleOrder != null && a.PreSaleOrder.Customer != null
                    ? a.PreSaleOrder.Customer.Name
                    : "",
                AppliedUsd = _db.CustomerPaymentAllocationApplications
                    .Where(x => x.CustomerPaymentAllocationId == a.Id
                        && x.Status == CustomerPaymentAllocationApplicationStatus.Active)
                    .Sum(x => (decimal?)x.AppliedAmountUsd) ?? 0m
            })
            .ToListAsync(ct);

        return rows.Select(a => new PreSaleDiscrepancyRow(
            PreSaleDiscrepancyKind.UnconsumedAdvance,
            $"پیش‌فروش {a.OrderNumber}",
            $"{a.CustomerName} — تخصیص {a.AllocatedAmountUsd:N2}$، مصرف‌شده {a.AppliedUsd:N2}$",
            QuantityMt: null,
            AmountUsd: Round(a.AllocatedAmountUsd - a.AppliedUsd),
            DocumentDate: a.AllocationDate,
            DocumentController: "Sales",
            DocumentAction: "PreSaleDetails",
            DocumentId: a.PreSaleOrderId)).ToList();
    }

    // -------------------------------------------------------- 8. Allocated more than the customer actually paid

    private IQueryable<PaymentTransaction> AllocationExceedsReceiptQuery(ManagementReportFilterViewModel filter)
    {
        var query = _db.PaymentTransactions.AsNoTracking().Where(p => p.CustomerId != null);
        if (filter.FromDate.HasValue) query = query.Where(p => p.PaymentDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(p => p.PaymentDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.CustomerId.HasValue) query = query.Where(p => p.CustomerId == filter.CustomerId.Value);

        return query.Where(p => _db.CustomerPaymentAllocations
            .Where(a => a.PaymentTransactionId == p.Id && a.Status == CustomerPaymentAllocationStatus.Active)
            .Sum(a => (decimal?)a.AllocatedAmountUsd).GetValueOrDefault() > p.AmountUsd);
    }

    private async Task<IReadOnlyList<PreSaleDiscrepancyRow>> AllocationExceedsReceiptAsync(
        ManagementReportFilterViewModel filter, int skip, int take, CancellationToken ct)
    {
        var rows = await AllocationExceedsReceiptQuery(filter)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Skip(skip).Take(take)
            .Select(p => new
            {
                p.Id,
                p.PaymentDate,
                p.AmountUsd,
                CustomerName = p.Customer != null ? p.Customer.Name : "",
                AllocatedUsd = _db.CustomerPaymentAllocations
                    .Where(a => a.PaymentTransactionId == p.Id && a.Status == CustomerPaymentAllocationStatus.Active)
                    .Sum(a => (decimal?)a.AllocatedAmountUsd) ?? 0m
            })
            .ToListAsync(ct);

        return rows.Select(p => new PreSaleDiscrepancyRow(
            PreSaleDiscrepancyKind.AllocationExceedsReceipt,
            $"دریافت #{p.Id}",
            $"{p.CustomerName} — دریافت {p.AmountUsd:N2}$، تخصیص {p.AllocatedUsd:N2}$",
            QuantityMt: null,
            AmountUsd: Round(p.AllocatedUsd - p.AmountUsd),
            DocumentDate: p.PaymentDate,
            DocumentController: "Payments",
            DocumentAction: "Details",
            DocumentId: p.Id)).ToList();
    }

    // ---------------------------------------------------------------------------- helpers

    private IQueryable<PreSaleOrder> BuildOrderQuery(ManagementReportFilterViewModel filter)
    {
        var query = _db.PreSaleOrders.AsNoTracking().AsQueryable();
        if (filter.FromDate.HasValue) query = query.Where(o => o.OrderDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(o => o.OrderDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ProductId.HasValue) query = query.Where(o => o.ProductId == filter.ProductId.Value);
        if (filter.CustomerId.HasValue) query = query.Where(o => o.CustomerId == filter.CustomerId.Value);
        return query;
    }

    private IQueryable<PreSaleOrder> BuildActiveOrderQuery(ManagementReportFilterViewModel filter)
        => BuildOrderQuery(filter)
            .Where(o => o.Status == PreSaleOrderStatus.Confirmed
                || o.Status == PreSaleOrderStatus.PartiallyDelivered);

    private static IQueryable<SalesTransaction> ApplySaleDateFilter(
        IQueryable<SalesTransaction> query,
        ManagementReportFilterViewModel filter)
    {
        if (filter.FromDate.HasValue) query = query.Where(s => s.SaleDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(s => s.SaleDate < filter.ToDate.Value.Date.AddDays(1));
        return query;
    }

    private static decimal Round(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    public static string TitleOf(PreSaleDiscrepancyKind kind) => kind switch
    {
        PreSaleDiscrepancyKind.OverDelivery => "تحویل بیشتر از تعهد",
        PreSaleDiscrepancyKind.DeliveryWithoutPreSale => "تحویل بدون اتصال به پیش‌فروش",
        PreSaleDiscrepancyKind.PreSaleWithoutStockSource => "پیش‌فروش بدون منبع موجودی",
        PreSaleDiscrepancyKind.ReservationExceedsStock => "رزرو بیشتر از موجودی",
        PreSaleDiscrepancyKind.OverdueUndelivered => "تعهد سررسیدشده و تحویل‌نشده",
        PreSaleDiscrepancyKind.CancelledDeliveryWithEffect => "تحویل لغوشده با اثر موجودی",
        PreSaleDiscrepancyKind.UnconsumedAdvance => "پیش‌پرداخت مصرف‌نشده",
        PreSaleDiscrepancyKind.AllocationExceedsReceipt => "تخصیص بیشتر از دریافت مشتری",
        _ => kind.ToString()
    };
}
