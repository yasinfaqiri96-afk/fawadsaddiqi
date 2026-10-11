using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// نگهبان‌های موجودی که یک حرکت خروجی باید از آن‌ها عبور کند.
///
/// مسیرهای فعلی سیستم ترکیب‌های متفاوتی از این نگهبان‌ها را اجرا می‌کنند و این تفاوت‌ها
/// تصمیم عملیاتی‌اند، نه تصادف (مثلاً بارگیریِ تاریخ‌گذشته عمداً با موجودیِ «امروز» سنجیده
/// می‌شود). پس Writer آن‌ها را یکسان‌سازی نمی‌کند؛ فقط اجرایشان را یک‌جا می‌کند و انتخاب را
/// به caller می‌سپارد تا رفتار تجاری هیچ مسیری عوض نشود.
/// </summary>
[Flags]
public enum StockGuard
{
    None = 0,
    /// <summary>قفل ردیف مخزن / advisory lock روی کالا پیش از خواندن موجودی.</summary>
    Lock = 1,
    /// <summary>موجودی آزادِ همان scope در تاریخ حرکت باید کافی باشد.</summary>
    Available = 2,
    /// <summary>هیچ نقطه‌ای از خط زمانی نباید منفی شود (PTG-P0-02 — فعال).</summary>
    FutureTimeline = 4,
    Standard = Lock | Available,
    Full = Lock | Available | FutureTimeline
}

/// <summary>
/// یک درخواست ثبت حرکت موجودی. جهت حرکت را خودِ متدِ Writer تعیین می‌کند، نه این رکورد،
/// تا هیچ caller نتواند به‌اشتباه جهت را با نگهبان اشتباه ترکیب کند.
/// </summary>
public sealed record InventoryMovementRequest
{
    public required int ProductId { get; init; }
    public required int TerminalId { get; init; }
    public int? StorageTankId { get; init; }
    public int? ContractId { get; init; }
    public required DateTime MovementDate { get; init; }
    public required decimal QuantityMt { get; init; }
    /// <summary>
    /// قرارداد Reference سیستم است و گزارش‌ها، Reconciliation و P&amp;L به آن وابسته‌اند
    /// (`TRUCK-DISPATCH:{id}`، `TRANSPORT-RECEIPT:{id}` و …). Writer آن را نمی‌سازد و
    /// تغییر نمی‌دهد؛ فقط منتقل می‌کند.
    /// </summary>
    public required string ReferenceDocument { get; init; }
    public string? Notes { get; init; }
    public int? LoadingReceiptId { get; init; }
    public int? SalesTransactionId { get; init; }
    public int? InventoryBatchId { get; init; }
}

public interface IInventoryMovementWriter
{
    Task<InventoryMovement> PostOutboundAsync(
        InventoryMovementRequest request,
        StockGuard guards = StockGuard.Standard,
        CancellationToken ct = default);

    Task<InventoryMovement> PostInboundAsync(
        InventoryMovementRequest request,
        CancellationToken ct = default);

    Task<InventoryMovement> PostAdjustmentAsync(
        InventoryMovementRequest request,
        CancellationToken ct = default);

    /// <summary>مسیر سازگاریِ سندهای Manual Transfer قدیمی؛ مانند خروجی نگهبان موجودی دارد.</summary>
    Task<InventoryMovement> PostTransferAsync(
        InventoryMovementRequest request,
        StockGuard guards = StockGuard.Standard,
        CancellationToken ct = default);

    /// <summary>
    /// ثبت گروهیِ حرکت‌های ورودیِ از قبل ساخته‌شده. برای workflowهایی است که رابطه‌های EF
    /// را پیش از گرفتن شناسه می‌سازند؛ همهٔ حرکت‌ها در یک SaveChanges ثبت می‌شوند.
    /// </summary>
    Task<IReadOnlyList<InventoryMovement>> PostInboundRangeAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        CancellationToken ct = default);

    /// <summary>
    /// ثبت گروهیِ خروجی‌های از قبل ساخته‌شده. caller می‌تواند همان نگهبان‌های تاریخی مسیر
    /// خود را نگه دارد؛ Writer فقط آن‌ها را یک‌جا اجرا و ثبت می‌کند.
    /// </summary>
    Task<IReadOnlyList<InventoryMovement>> PostOutboundRangeAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        StockGuard guards = StockGuard.Standard,
        CancellationToken ct = default);

    /// <summary>
    /// سند معکوسِ یک حرکت قطعی. اگر معکوسِ همان سند از قبل وجود داشته باشد <c>null</c>
    /// برمی‌گرداند و هیچ سند تازه‌ای نمی‌سازد — همان چیزی که دابل‌کلیک/ارسال مجدد را بی‌اثر می‌کند.
    /// </summary>
    Task<InventoryMovement?> PostReversalAsync(
        InventoryMovement original,
        DateTime reversalDate,
        string? notes = null,
        CancellationToken ct = default,
        StockGuard guards = StockGuard.None);

    /// <summary>آیا سندی با این Reference و جهت از قبل ثبت شده است.</summary>
    Task<bool> ReferenceExistsAsync(
        string referenceDocument,
        MovementDirection? direction = null,
        CancellationToken ct = default);
}

/// <summary>
/// تنها نقطهٔ اجرای قواعد مشترکِ ثبت حرکت موجودی: اعتبارسنجی مقدار، قفل هم‌زمانی،
/// نگهبان موجودی، ساخت سند و ثبت آن.
///
/// <para><b>مالکیت تراکنش:</b> این سرویس هیچ تراکنشی باز نمی‌کند. caller مالک تراکنش است و
/// باید همهٔ فراخوانی‌های یک عملیات را داخل یک تراکنش واحد بگذارد. الگو عمداً همان
/// <see cref="InventoryTransportLegLoadService"/> است که از قبل در تولید ثابت شده.</para>
///
/// <para><b>قرارداد Reference:</b> Writer قالب Reference را نمی‌سازد و عوض نمی‌کند. گزارش‌ها،
/// Reconciliation و P&amp;L قالب‌های فعلی را parse می‌کنند و تغییرشان دادهٔ تاریخی را می‌شکند.</para>
///
/// <para><b>Hookها:</b> قلاب‌های حسابداری و نسب‌نامه سطح <i>سند تجاری</i> هستند (رسید، فروش،
/// بارگیری)، نه سطح تک‌حرکت، و ورودی‌شان همان موجودیت تجاری است. پس در سرویس‌های همان سند
/// می‌مانند و Writer آن‌ها را جابه‌جا نمی‌کند؛ وگرنه یک عملیات چندحرکتی چند بار سند می‌زد.</para>
/// </summary>
public sealed class InventoryMovementWriter : IInventoryMovementWriter
{
    private const string CancelReferenceSuffix = "-CANCEL";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;

    public InventoryMovementWriter(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    public Task<InventoryMovement> PostOutboundAsync(
        InventoryMovementRequest request,
        StockGuard guards = StockGuard.Standard,
        CancellationToken ct = default)
        => PostAsync(request, MovementDirection.Out, guards, ct);

    public Task<InventoryMovement> PostInboundAsync(
        InventoryMovementRequest request,
        CancellationToken ct = default)
        // ورودی هرگز موجودی را کم نمی‌کند، پس نگهبان موجودی برایش بی‌معنا است.
        => PostAsync(request, MovementDirection.In, StockGuard.None, ct);

    public Task<InventoryMovement> PostAdjustmentAsync(
        InventoryMovementRequest request,
        CancellationToken ct = default)
        => PostAsync(request, MovementDirection.Adjustment, StockGuard.None, ct);

    public Task<InventoryMovement> PostTransferAsync(
        InventoryMovementRequest request,
        StockGuard guards = StockGuard.Standard,
        CancellationToken ct = default)
        => PostAsync(request, MovementDirection.Transfer, guards, ct);

    public Task<IReadOnlyList<InventoryMovement>> PostInboundRangeAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        CancellationToken ct = default)
        => PostRangeAsync(movements, MovementDirection.In, StockGuard.None, ct);

    public Task<IReadOnlyList<InventoryMovement>> PostOutboundRangeAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        StockGuard guards = StockGuard.Standard,
        CancellationToken ct = default)
        => PostRangeAsync(movements, MovementDirection.Out, guards, ct);

    public async Task<InventoryMovement?> PostReversalAsync(
        InventoryMovement original,
        DateTime reversalDate,
        string? notes = null,
        CancellationToken ct = default,
        StockGuard guards = StockGuard.None)
    {
        ArgumentNullException.ThrowIfNull(original);

        var sourceReference = string.IsNullOrWhiteSpace(original.ReferenceDocument)
            ? $"INVENTORY-MOVEMENT:{original.Id}"
            : original.ReferenceDocument;
        var reversalReference = sourceReference + CancelReferenceSuffix;
        var reversedDirection = InvertDirection(original.Direction);

        // کلید صریح، برگشتِ سهم‌های چندقراردادی با Reference مشترک را از هم جدا می‌کند.
        // unique index دیتابیس نیز دو درخواست هم‌زمان را از ساخت دو برگشت بازمی‌دارد.
        if (await _db.InventoryMovements
            .AsNoTracking()
            .AnyAsync(m => m.ReversalOfInventoryMovementId == original.Id, ct))
        {
            return null;
        }

        // سازگاری با برگشت‌های تاریخیِ پیش از ستون ReversalOf: فقط match کامل همان scope
        // پذیرفته می‌شود. اگر پیدا شد، پیوند حسابرسی روی همان سند موجود تکمیل می‌شود.
        var legacyReversal = await _db.InventoryMovements
            .FirstOrDefaultAsync(m => m.ReversalOfInventoryMovementId == null
                && m.ReferenceDocument == reversalReference
                && m.Direction == reversedDirection
                && m.ProductId == original.ProductId
                && m.ContractId == original.ContractId
                && m.TerminalId == original.TerminalId
                && m.StorageTankId == original.StorageTankId
                && m.InventoryBatchId == original.InventoryBatchId
                && m.QuantityMt == original.QuantityMt,
                ct);
        if (legacyReversal is not null)
        {
            legacyReversal.ReversalOfInventoryMovementId = original.Id;
            legacyReversal.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return null;
        }

        var reversal = new InventoryMovement
        {
            ProductId = original.ProductId,
            ContractId = original.ContractId,
            TerminalId = original.TerminalId,
            StorageTankId = original.StorageTankId,
            InventoryBatchId = original.InventoryBatchId,
            SalesTransactionId = original.SalesTransactionId,
            ReversalOfInventoryMovementId = original.Id,
            Direction = reversedDirection,
            MovementDate = reversalDate,
            QuantityMt = original.QuantityMt,
            ReferenceDocument = reversalReference,
            Notes = notes
        };

        if (reversedDirection is MovementDirection.Out or MovementDirection.Transfer)
        {
            if (guards.HasFlag(StockGuard.Lock))
            {
                await _stock.AcquireStockMutationLockAsync(reversal, ct);
            }
            if (guards.HasFlag(StockGuard.Available))
            {
                await _stock.EnsureSufficientStockForMovementAsync(reversal, ct);
            }
            if (guards.HasFlag(StockGuard.FutureTimeline))
            {
                await _stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(reversal, ct);
            }
        }
        else if (reversedDirection is MovementDirection.In or MovementDirection.Adjustment)
        {
            await EnsureTankCapacityAsync([reversal], ct);
        }

        _db.InventoryMovements.Add(reversal);
        await _db.SaveChangesAsync(ct);
        return reversal;
    }

    public Task<bool> ReferenceExistsAsync(
        string referenceDocument,
        MovementDirection? direction = null,
        CancellationToken ct = default)
        => _db.InventoryMovements
            .AsNoTracking()
            .AnyAsync(
                m => m.ReferenceDocument == referenceDocument
                    && (direction == null || m.Direction == direction),
                ct);

    private static MovementDirection InvertDirection(MovementDirection direction) => direction switch
    {
        MovementDirection.In => MovementDirection.Out,
        MovementDirection.Out => MovementDirection.In,
        MovementDirection.Transfer => MovementDirection.In,
        _ => MovementDirection.Adjustment
    };

    private async Task<InventoryMovement> PostAsync(
        InventoryMovementRequest request,
        MovementDirection direction,
        StockGuard guards,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.QuantityMt <= 0m)
        {
            throw new BusinessRuleException(
                "STOCK_QTY_NON_POSITIVE",
                "مقدار حرکت موجودی باید بزرگ‌تر از صفر باشد.");
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceDocument))
        {
            throw new BusinessRuleException(
                "STOCK_REFERENCE_MISSING",
                "هر حرکت موجودی باید سند مرجع داشته باشد.");
        }

        var movement = new InventoryMovement
        {
            ProductId = request.ProductId,
            ContractId = request.ContractId,
            TerminalId = request.TerminalId,
            StorageTankId = request.StorageTankId,
            InventoryBatchId = request.InventoryBatchId,
            LoadingReceiptId = request.LoadingReceiptId,
            SalesTransactionId = request.SalesTransactionId,
            Direction = direction,
            MovementDate = request.MovementDate,
            QuantityMt = request.QuantityMt,
            ReferenceDocument = request.ReferenceDocument,
            Notes = request.Notes
        };

        // ترتیب مهم است: قفل پیش از خواندن موجودی، وگرنه دو درخواست هم‌زمان هر دو از چک عبور
        // می‌کنند و موجودی منفی می‌شود.
        if (guards.HasFlag(StockGuard.Lock))
        {
            await _stock.AcquireStockMutationLockAsync(movement, ct);
        }

        if (guards.HasFlag(StockGuard.Available))
        {
            await _stock.EnsureSufficientStockForMovementAsync(movement, ct);
        }

        if (guards.HasFlag(StockGuard.FutureTimeline))
        {
            await _stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(movement, ct);
        }

        if (direction is MovementDirection.In or MovementDirection.Adjustment)
        {
            await EnsureTankCapacityAsync([movement], ct);
        }

        _db.InventoryMovements.Add(movement);
        await _db.SaveChangesAsync(ct);
        return movement;
    }

    private async Task<IReadOnlyList<InventoryMovement>> PostRangeAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        MovementDirection direction,
        StockGuard guards,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(movements);

        if (movements.Count == 0)
        {
            return [];
        }

        var prepared = movements.ToList();
        foreach (var movement in prepared)
        {
            ArgumentNullException.ThrowIfNull(movement);
            ValidateMovement(movement.QuantityMt, movement.ReferenceDocument);
            movement.Direction = direction;
        }

        if (guards.HasFlag(StockGuard.Lock))
        {
            // ترتیب پایدار مانع deadlock دو عملیات چندمخزنی با ترتیب ورودی متفاوت می‌شود.
            foreach (var movement in prepared
                .OrderBy(m => m.ProductId)
                .ThenBy(m => m.TerminalId)
                .ThenBy(m => m.StorageTankId)
                .ThenBy(m => m.ContractId)
                .ThenBy(m => m.InventoryBatchId))
            {
                await _stock.AcquireStockMutationLockAsync(movement, ct);
            }
        }

        if (guards.HasFlag(StockGuard.Available))
        {
            // چند خروجیِ هم‌scope را جمع می‌کنیم تا هر کدام جداگانه از یک موجودی واحد عبور نکند.
            foreach (var group in prepared.GroupBy(m => new
                     {
                         m.ProductId,
                         m.TerminalId,
                         m.StorageTankId,
                         m.ContractId,
                         m.InventoryBatchId,
                         m.MovementDate
                     }))
            {
                var sample = group.First();
                var probe = new InventoryMovement
                {
                    ProductId = sample.ProductId,
                    TerminalId = sample.TerminalId,
                    StorageTankId = sample.StorageTankId,
                    ContractId = sample.ContractId,
                    InventoryBatchId = sample.InventoryBatchId,
                    Direction = direction,
                    MovementDate = sample.MovementDate,
                    QuantityMt = group.Sum(m => m.QuantityMt)
                };
                await _stock.EnsureSufficientStockForMovementAsync(probe, ct);
            }
        }

        if (guards.HasFlag(StockGuard.FutureTimeline))
        {
            foreach (var movement in prepared)
            {
                await _stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(movement, ct);
            }
        }

        if (direction == MovementDirection.In)
        {
            await EnsureTankCapacityAsync(prepared, ct);
        }

        _db.InventoryMovements.AddRange(prepared);
        await _db.SaveChangesAsync(ct);
        return prepared;
    }

    /// <summary>
    /// موجودی فعلیِ مخزن به‌اضافهٔ ورودی‌های این ثبت نباید از ظرفیت مخزن بیشتر شود.
    /// ظرفیت صفر یا منفی یعنی «بدون محدودیت» (رفتار پیشین حفظ می‌شود). خط زمانی آینده سنجیده نمی‌شود.
    /// </summary>
    private async Task EnsureTankCapacityAsync(
        IReadOnlyCollection<InventoryMovement> movements,
        CancellationToken ct)
    {
        foreach (var group in movements
                     .Where(m => m.StorageTankId.HasValue)
                     .GroupBy(m => m.StorageTankId!.Value)
                     .OrderBy(g => g.Key))
        {
            var tankId = group.Key;
            // قفل پیش از خواندن ظرفیت و هر دو دفتر؛ ورود ملکی و امانی یک ظرفیت فزیکی دارند.
            var sample = group.First();
            await _stock.AcquireStockMutationLockAsync(
                new InventoryMovement { ProductId = sample.ProductId, StorageTankId = tankId }, ct);
            var tankProductId = await _db.StorageTanks.AsNoTracking()
                .Where(t => t.Id == tankId).Select(t => t.ProductId).FirstOrDefaultAsync(ct);
            foreach (var productId in group.Select(m => m.ProductId).Distinct())
            {
                var otherCompanyProduct = await _db.InventoryMovements.AsNoTracking()
                    .Where(m => m.StorageTankId == tankId && m.ProductId != productId)
                    .GroupBy(m => m.ProductId)
                    .AnyAsync(g => g.Sum(m => m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt : 0m) > 0m, ct);
                var otherCustodyProduct = await _db.CustodyMovements.AsNoTracking()
                    .Where(m => m.StorageTankId == tankId && m.CustodyLot!.ProductId != productId)
                    .GroupBy(m => m.CustodyLot!.ProductId)
                    .AnyAsync(g => g.Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt) > 0m, ct);
                if ((tankProductId.HasValue && tankProductId != productId) || otherCompanyProduct || otherCustodyProduct
                    || group.Any(m => m.ProductId != productId))
                    throw new BusinessRuleException("STOCK_TANK_MIXED_PRODUCT", "در این مخزن جنسِ دیگری موجود یا تعریف شده است؛ اختلاط جنس‌ها مجاز نیست.");
            }
            var capacityMt = await _db.StorageTanks
                .AsNoTracking()
                .Where(t => t.Id == tankId)
                .Select(t => (decimal?)t.CapacityMt)
                .FirstOrDefaultAsync(ct);
            if (capacityMt is not > 0m)
            {
                continue;
            }

            var currentMt = await _db.InventoryMovements
                .AsNoTracking()
                .Where(m => m.StorageTankId == tankId)
                .Select(m => (decimal?)(
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m))
                .SumAsync(ct) ?? 0m;
            currentMt += await _db.CustodyMovements.AsNoTracking()
                .Where(m => m.StorageTankId == tankId)
                .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt, ct);
            var incomingMt = group.Sum(m => m.QuantityMt);

            if (currentMt + incomingMt > capacityMt.Value)
            {
                var freeMt = Math.Max(capacityMt.Value - currentMt, 0m);
                throw new BusinessRuleException(
                    "STOCK_TANK_CAPACITY_EXCEEDED",
                    $"ظرفیت مخزن مقصد کافی نیست. ظرفیت مخزن: {capacityMt.Value:N4} MT، موجودی فعلی: {currentMt:N4} MT، فضای خالی: {freeMt:N4} MT، مقدار ورودی: {incomingMt:N4} MT.");
            }
        }
    }

    private static void ValidateMovement(decimal quantityMt, string? referenceDocument)
    {
        if (quantityMt <= 0m)
        {
            throw new BusinessRuleException(
                "STOCK_QTY_NON_POSITIVE",
                "مقدار حرکت موجودی باید بزرگ‌تر از صفر باشد.");
        }

        if (string.IsNullOrWhiteSpace(referenceDocument))
        {
            throw new BusinessRuleException(
                "STOCK_REFERENCE_MISSING",
                "هر حرکت موجودی باید سند مرجع داشته باشد.");
        }
    }
}
