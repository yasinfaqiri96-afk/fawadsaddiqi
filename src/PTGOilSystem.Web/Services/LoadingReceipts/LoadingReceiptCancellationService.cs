using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.LoadingReceipts;

/// <summary>یک دلیل که چرا این رسید قابل لغو نیست.</summary>
public sealed record LoadingReceiptCancellationBlocker(int ReceiptId, string Reason);

/// <summary>نتیجهٔ لغو: یا همهٔ رسیدها لغو شده‌اند یا هیچ‌کدام (اتمیک).</summary>
public sealed record LoadingReceiptCancellationResult(
    IReadOnlyList<int> CancelledReceiptIds,
    IReadOnlyList<LoadingReceiptCancellationBlocker> Blockers)
{
    public bool Succeeded => Blockers.Count == 0;
}

public interface ILoadingReceiptCancellationService
{
    /// <summary>فقط بررسی وابستگی‌ها؛ هیچ تغییری ذخیره نمی‌شود.</summary>
    Task<IReadOnlyList<LoadingReceiptCancellationBlocker>> InspectAsync(
        IReadOnlyCollection<int> receiptIds,
        CancellationToken ct = default);

    /// <summary>لغو گروهی با تراکنش خودِ سرویس. اگر یک ردیف قابل لغو نباشد هیچ ردیفی لغو نمی‌شود.</summary>
    Task<LoadingReceiptCancellationResult> CancelAsync(
        IReadOnlyCollection<int> receiptIds,
        string reason,
        int? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// لغو داخل تراکنشِ فراخوان (مسیر «اصلاح مقدار» = لغو + ثبت دوباره).
    /// تراکنش باز یا Commit نمی‌کند؛ خطا باید باعث Rollback فراخوان شود.
    /// </summary>
    Task<LoadingReceiptCancellationResult> CancelWithinCurrentTransactionAsync(
        IReadOnlyCollection<int> receiptIds,
        string reason,
        int? actorUserId,
        CancellationToken ct = default);
}

/// <summary>
/// لغو امن رسید بارگیری.
///
/// قاعده‌ها از الگوهای موجود سیستم گرفته شده‌اند و مسیر مالی موازی ساخته نمی‌شود:
///   • رکورد حذف نمی‌شود؛ فقط <see cref="LoadingReceipt.IsCancelled"/> علامت می‌خورد
///     (مثل <see cref="InventoryTransportReceipt.IsCancelled"/> و <see cref="LossEvent.IsCancelled"/>).
///   • حرکت موجودیِ ورودی حذف نمی‌شود؛ یک حرکت خروجیِ معکوس ثبت می‌شود (مثل لغو ضایعات/فروش).
///   • اسناد حسابداری با Reversal رسمیِ همان Adapterهای موجود برگردانده می‌شوند.
///   • اگر بار این رسید در عملیات بعدی مصرف شده باشد، لغو انجام نمی‌شود و دلیل دقیق برگردانده می‌شود.
/// </summary>
public sealed class LoadingReceiptCancellationService : ILoadingReceiptCancellationService
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IStockService _stock;
    private readonly ILogger<LoadingReceiptCancellationService> _logger;
    private readonly IPurchaseAccountingAdapter? _purchaseAccounting;
    private readonly ISalesAccountingAdapter? _salesAccounting;
    private readonly IInventoryLossAccountingAdapter? _lossAccounting;
    private readonly IExpenseAccountingAdapter? _expenseAccounting;
    private readonly IInventoryMovementWriter _movements;

    public LoadingReceiptCancellationService(
        ApplicationDbContext db,
        IAuditService audit,
        ILogger<LoadingReceiptCancellationService> logger,
        IStockService? stock = null,
        IPurchaseAccountingAdapter? purchaseAccounting = null,
        ISalesAccountingAdapter? salesAccounting = null,
        IInventoryLossAccountingAdapter? lossAccounting = null,
        IExpenseAccountingAdapter? expenseAccounting = null,
        IInventoryMovementWriter? movements = null)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
        _stock = stock ?? new StockService(db);
        _purchaseAccounting = purchaseAccounting;
        _salesAccounting = salesAccounting;
        _lossAccounting = lossAccounting;
        _expenseAccounting = expenseAccounting;
        _movements = movements ?? new InventoryMovementWriter(db, _stock);
    }

    public async Task<IReadOnlyList<LoadingReceiptCancellationBlocker>> InspectAsync(
        IReadOnlyCollection<int> receiptIds,
        CancellationToken ct = default)
    {
        var receipts = await LoadReceiptsAsync(receiptIds, tracking: false, ct);
        var blockers = new List<LoadingReceiptCancellationBlocker>();

        foreach (var receiptId in NormalizeIds(receiptIds))
        {
            if (!receipts.TryGetValue(receiptId, out var receipt))
            {
                blockers.Add(new LoadingReceiptCancellationBlocker(receiptId, "رسید پیدا نشد."));
                continue;
            }

            blockers.AddRange(await CollectBlockersAsync(receipt, ct));
        }

        return blockers;
    }

    public async Task<LoadingReceiptCancellationResult> CancelAsync(
        IReadOnlyCollection<int> receiptIds,
        string reason,
        int? actorUserId,
        CancellationToken ct = default)
    {
        var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            var result = await CancelCoreAsync(receiptIds, reason, actorUserId, ct);

            if (!result.Succeeded)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return result;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public Task<LoadingReceiptCancellationResult> CancelWithinCurrentTransactionAsync(
        IReadOnlyCollection<int> receiptIds,
        string reason,
        int? actorUserId,
        CancellationToken ct = default)
        => CancelCoreAsync(receiptIds, reason, actorUserId, ct);

    private async Task<LoadingReceiptCancellationResult> CancelCoreAsync(
        IReadOnlyCollection<int> receiptIds,
        string reason,
        int? actorUserId,
        CancellationToken ct)
    {
        var normalizedReason = (reason ?? string.Empty).Trim();
        if (normalizedReason.Length == 0)
        {
            return new LoadingReceiptCancellationResult(
                Array.Empty<int>(),
                [new LoadingReceiptCancellationBlocker(0, "ثبت دلیل لغو الزامی است.")]);
        }

        if (normalizedReason.Length > 500)
        {
            normalizedReason = normalizedReason[..500];
        }

        var ids = NormalizeIds(receiptIds);
        if (ids.Count == 0)
        {
            return new LoadingReceiptCancellationResult(
                Array.Empty<int>(),
                [new LoadingReceiptCancellationBlocker(0, "هیچ رسیدی انتخاب نشده است.")]);
        }

        // Receipt correction may already hold the loading lock. Do not acquire loading
        // locks here: all cancellation entry points take batch → sale → receipt locks.
        // Batch serialization also covers cancellations initiated from the receipt page.
        var linkedSales = await _db.LoadingReceiptAllocations.AsNoTracking()
            .Where(a => ids.Contains(a.LoadingReceiptId) && a.SalesTransactionId.HasValue)
            .Select(a => new { SaleId = a.SalesTransactionId!.Value, BatchId = a.SalesTransaction!.SalesBatchId })
            .Distinct().ToListAsync(ct);
        var coordinator = new SalesBatchCancellationCoordinator(_db);
        var batchIds = linkedSales.Where(s => s.BatchId.HasValue).Select(s => s.BatchId!.Value)
            .Distinct().OrderBy(id => id).ToArray();
        foreach (var batchId in batchIds) await coordinator.LockAsync(batchId, [], ct);
        await coordinator.LockAsync(null, linkedSales.Select(s => s.SaleId).ToArray(), ct);
        if (_db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            var orderedIds = ids.OrderBy(id => id).ToArray();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"SELECT 1 FROM ""LoadingReceipts"" WHERE ""Id"" = ANY({orderedIds}) ORDER BY ""Id"" FOR UPDATE", ct);
        }
        // EF may already track these rows from a controller's preflight. Refresh them
        // only after obtaining locks so a waiting request observes the committed cancel.
        foreach (var entry in _db.ChangeTracker.Entries<SalesTransaction>().Where(e => linkedSales.Any(s => s.SaleId == e.Entity.Id)).ToList())
            await entry.ReloadAsync(ct);
        foreach (var entry in _db.ChangeTracker.Entries<LoadingReceipt>().Where(e => ids.Contains(e.Entity.Id)).ToList())
            await entry.ReloadAsync(ct);
        var receipts = await LoadReceiptsAsync(ids, tracking: true, ct);

        // مرحله ۱ — اعتبارسنجی کاملِ همهٔ ردیف‌ها پیش از هر تغییر: یا همه یا هیچ.
        var blockers = new List<LoadingReceiptCancellationBlocker>();
        foreach (var receiptId in ids)
        {
            if (!receipts.TryGetValue(receiptId, out var receipt))
            {
                blockers.Add(new LoadingReceiptCancellationBlocker(receiptId, "رسید پیدا نشد."));
                continue;
            }

            blockers.AddRange(await CollectBlockersAsync(receipt, ct));
        }

        if (blockers.Count > 0)
        {
            return new LoadingReceiptCancellationResult(Array.Empty<int>(), blockers);
        }

        // مرحله ۲ — اجرای لغو.
        var cancelled = new List<int>(ids.Count);
        var cancelledDispatches = new List<TruckDispatch>();
        foreach (var receiptId in ids)
        {
            var receipt = receipts[receiptId];
            cancelledDispatches.AddRange(await CancelSingleAsync(receipt, normalizedReason, actorUserId, ct));
            cancelled.Add(receipt.Id);
        }

        await _db.SaveChangesAsync(ct);
        foreach (var batchId in batchIds) await coordinator.FinalizeAsync(batchId, ct);

        // دیسپچ‌های لغوشده سطر مصرفِ دارایی دارند؛ بعد از ذخیره sync می‌شوند تا کارکردِ
        // باطل‌شده در پروندهٔ دارایی نماند (همان قاعدهٔ لغو دیسپچ در DispatchController).
        var usageWriter = new AssetUsageChargeService(_db);
        foreach (var dispatch in cancelledDispatches)
        {
            await usageWriter.SyncOperationAsync(dispatch, ct);
        }

        return new LoadingReceiptCancellationResult(cancelled, Array.Empty<LoadingReceiptCancellationBlocker>());
    }

    private async Task<IReadOnlyList<TruckDispatch>> CancelSingleAsync(
        LoadingReceipt receipt,
        string reason,
        int? actorUserId,
        CancellationToken ct)
    {
        var reversalDate = AfghanistanBusinessClock.SystemToday;

        // ۱) اسناد خرید/موجودیِ همین رسید: Reversal رسمی پیش از علامت‌خوردن لغو، تا Adapter
        //    بتواند شرکت و سند اصلی را از همان روابط قبلی پیدا کند (الگوی CancelExpenseAsync).
        if (_purchaseAccounting is not null)
        {
            await _purchaseAccounting.TryPostInventoryReceiptReversalAsync(receipt, ct);
        }

        // ۲) فروش مستقیمِ ساخته‌شده از این رسید.
        var directSales = await LoadDirectSalesAsync(receipt.Id, ct);
        foreach (var sale in directSales)
        {
            await CancelDirectSaleAsync(sale, receipt, reversalDate, ct);
        }

        // ۳) دیسپچ مستقیمِ ساخته‌شده از این رسید و کرایهٔ آن.
        var dispatches = await LoadActiveDispatchesAsync(receipt.Id, ct);
        foreach (var dispatch in dispatches)
        {
            dispatch.Status = DispatchStatus.Cancelled;
            dispatch.UpdatedAtUtc = DateTime.UtcNow;
            await DispatchFreightExpenseSync.CancelByDispatchIdAsync(_db, dispatch.Id, _expenseAccounting);
            await _audit.LogAsync(
                nameof(TruckDispatch),
                dispatch.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("Status", DispatchStatus.Loaded, DispatchStatus.Cancelled)),
                ct: ct);
        }

        // ۴) کسری/مازادِ همین رسید: لغو + برگشت حرکت موجودی و سند آن (الگوی LossEventsController.Cancel).
        var losses = await _db.LossEvents
            .Where(e => e.LoadingReceiptId == receipt.Id && !e.IsCancelled)
            .ToListAsync(ct);
        foreach (var loss in losses)
        {
            await CancelReceiptLossAsync(loss, receipt, reversalDate, ct);
        }

        // برگشت کسری پیش از سنجش موجودی ذخیره می‌شود تا گارد موجودی همان تصویر واقعی را ببیند.
        if (losses.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        // ۵) حرکت‌های ورودی موجودیِ این رسید: حذف نمی‌شوند؛ حرکت خروجیِ معکوس ثبت می‌شود.
        var inboundMovements = await LoadInboundMovementsAsync(receipt.Id, ct);
        foreach (var movement in inboundMovements)
        {
            await _movements.PostReversalAsync(
                movement,
                reversalDate,
                $"Reversal for cancelled LoadingReceiptId={receipt.Id}",
                ct,
                StockGuard.Standard);
        }

        // ۶) allocationها: وضعیت لغو (الگوی LoadingReceiptAllocationStatus.Cancelled).
        var allocations = await _db.LoadingReceiptAllocations
            .Where(a => a.LoadingReceiptId == receipt.Id && a.Status != LoadingReceiptAllocationStatus.Cancelled)
            .ToListAsync(ct);
        foreach (var allocation in allocations)
        {
            var previousStatus = allocation.Status;
            allocation.Status = LoadingReceiptAllocationStatus.Cancelled;
            allocation.UpdatedAtUtc = DateTime.UtcNow;
            await _audit.LogAsync(
                nameof(LoadingReceiptAllocation),
                allocation.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("Status", previousStatus, allocation.Status)),
                ct: ct);
        }

        // ۷) خودِ رسید. مقدار دریافت‌شده و باقی‌ماندهٔ بارگیری همیشه از رسیدهای لغونشده محاسبه
        //    می‌شود، پس هیچ عددی روی LoadingRegister دستی برنمی‌گردد.
        receipt.IsCancelled = true;
        receipt.CancelledAtUtc = DateTime.UtcNow;
        receipt.CancelledByUserId = actorUserId;
        receipt.CancellationReason = reason;
        receipt.UpdatedAtUtc = DateTime.UtcNow;

        await _audit.LogAsync(
            nameof(LoadingReceipt),
            receipt.Id,
            AuditAction.Reverse,
            diff: AuditDiffFormatter.ForUpdate(
                ("IsCancelled", false, true),
                ("CancellationReason", null, reason),
                ("CancelledByUserId", null, actorUserId),
                ("ReversedInventoryMovements", null, inboundMovements.Count),
                ("CancelledDirectSales", null, directSales.Count),
                ("CancelledDispatches", null, dispatches.Count),
                ("CancelledLossEvents", null, losses.Count)),
            ct: ct);

        return dispatches;
    }

    private async Task CancelDirectSaleAsync(
        SalesTransaction sale,
        LoadingReceipt receipt,
        DateTime reversalDate,
        CancellationToken ct)
    {
        // سند لجر قدیمی حذف نمی‌شود؛ سند معکوس ثبت می‌شود (عیناً الگوی SalesController.Cancel).
        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync(ct);

        sale.IsCancelled = true;
        sale.UpdatedAtUtc = DateTime.UtcNow;

        if (originalLedger is not null)
        {
            await LedgerReversalWriter.ReverseAsync(
                _db,
                originalLedger,
                reversalDate,
                $"لغو رسید #{receipt.Id} | لغو فروش #{sale.Id} | {originalLedger.Description}",
                sale.InvoiceNumber,
                ct);
        }

        if (_salesAccounting is not null)
        {
            await _salesAccounting.TryReverseSaleAsync(sale, reversalDate, ct);
            await _salesAccounting.TryReverseCogsAsync(sale, reversalDate, ct);
            await _salesAccounting.TryReleaseAdvanceApplicationsAsync(sale, reversalDate, ct);
        }

        await _audit.LogAsync(
            nameof(SalesTransaction),
            sale.Id,
            AuditAction.Reverse,
            diff: AuditDiffFormatter.ForUpdate(
                ("IsCancelled", false, true),
                ("CancelledByLoadingReceiptId", null, receipt.Id)),
            ct: ct);
    }

    private async Task CancelReceiptLossAsync(
        LossEvent loss,
        LoadingReceipt receipt,
        DateTime reversalDate,
        CancellationToken ct)
    {
        loss.IsCancelled = true;
        loss.UpdatedAtUtc = DateTime.UtcNow;

        if (loss.InventoryMovementId.HasValue)
        {
            var linkedMovement = await _db.InventoryMovements
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == loss.InventoryMovementId.Value, ct);

            if (linkedMovement is not null)
            {
                await _movements.PostReversalAsync(
                    linkedMovement,
                    reversalDate,
                    $"Reversal for cancelled LossEventId={loss.Id} (LoadingReceiptId={receipt.Id})",
                    ct);
            }
        }

        if (_lossAccounting is not null)
        {
            await _lossAccounting.TryPostLossReversalAsync(loss, ct);
        }

        await _audit.LogAsync(
            nameof(LossEvent),
            loss.Id,
            AuditAction.Reverse,
            diff: AuditDiffFormatter.ForUpdate(
                ("IsCancelled", false, true),
                ("CancelledByLoadingReceiptId", null, receipt.Id)),
            ct: ct);
    }

    /// <summary>
    /// وابستگی‌های پایین‌دستی که لغو کورکورانه را ممنوع می‌کنند. پیام‌ها می‌گویند کدام عملیات
    /// باید اول لغو شود.
    /// </summary>
    private async Task<List<LoadingReceiptCancellationBlocker>> CollectBlockersAsync(
        LoadingReceipt receipt,
        CancellationToken ct)
    {
        var blockers = new List<LoadingReceiptCancellationBlocker>();

        void Add(string reason) => blockers.Add(new LoadingReceiptCancellationBlocker(receipt.Id, reason));

        if (receipt.IsCancelled)
        {
            Add($"رسید #{receipt.Id} قبلاً لغو شده است.");
            return blockers;
        }

        if (await _db.CustodyConversions.AnyAsync(x => x.Status == CustodyDocumentStatus.Posted
                && _db.LoadingRegisters.Any(l => l.Id == receipt.LoadingRegisterId && l.ContractId == x.ContractId), ct))
            Add("این رسید مربوط به تبدیل امانی است؛ لغو باید از سند تبدیل و همراه برگشت مالکیت انجام شود.");

        var allocationIds = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Where(a => a.LoadingReceiptId == receipt.Id)
            .Select(a => a.Id)
            .ToListAsync(ct);

        var inboundMovements = await LoadInboundMovementsAsync(receipt.Id, ct);
        var movementIds = inboundMovements.Select(m => m.Id).ToList();

        // حمل داخلی (واگن/موتر) که بارِ همین رسید را برداشته است.
        var transportLegCount = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .CountAsync(
                a => a.SourceLoadingReceiptId == receipt.Id
                    || (a.SourceInventoryMovementId != null
                        && movementIds.Contains(a.SourceInventoryMovementId.Value)),
                ct);
        if (transportLegCount > 0)
        {
            Add($"رسید #{receipt.Id}: برای بار این رسید حمل داخلی ثبت شده است؛ ابتدا حمل مربوطه را لغو کنید.");
        }

        // نسب‌نامه (Lineage): اگر از Lot ساخته‌شده از این رسید برداشت شده باشد.
        if (movementIds.Count > 0)
        {
            var consumedLotCount = await _db.InventoryLots
                .AsNoTracking()
                .CountAsync(
                    lot => lot.CreatedFromMovementId != null
                        && movementIds.Contains(lot.CreatedFromMovementId.Value)
                        && lot.Status != InventoryLotStatus.Cancelled
                        && lot.RemainingQuantityMt < lot.QuantityMt,
                    ct);
            if (consumedLotCount > 0)
            {
                Add($"رسید #{receipt.Id}: بخشی از بار این رسید در زنجیرهٔ رهگیری مصرف شده است؛ ابتدا عملیات مصرف‌کننده را لغو کنید.");
            }
        }

        // دیسپچ مستقیمِ همین رسید.
        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.LoadingReceiptAllocationId != null
                && allocationIds.Contains(d.LoadingReceiptAllocationId.Value)
                && d.Status != DispatchStatus.Cancelled)
            .Select(d => new { d.Id, d.Status, d.SalesTransactionId })
            .ToListAsync(ct);

        foreach (var dispatch in dispatches)
        {
            if (dispatch.Status == DispatchStatus.Delivered)
            {
                Add($"رسید #{receipt.Id}: موتر دیسپچ #{dispatch.Id} تحویل/تخلیه شده است؛ ابتدا تخلیه و دیسپچ را لغو کنید.");
                continue;
            }

            if (dispatch.SalesTransactionId.HasValue)
            {
                Add($"رسید #{receipt.Id}: برای دیسپچ #{dispatch.Id} فروش ثبت شده است؛ ابتدا فروش را لغو کنید.");
            }
        }

        var dispatchIds = dispatches.Select(d => d.Id).ToList();
        if (dispatchIds.Count > 0)
        {
            var soldFromDispatch = await _db.SalesTransactions
                .AsNoTracking()
                .CountAsync(s => s.TruckDispatchId != null && dispatchIds.Contains(s.TruckDispatchId.Value) && !s.IsCancelled, ct);
            if (soldFromDispatch > 0)
            {
                Add($"رسید #{receipt.Id}: از موتر این رسید فروش ثبت شده است؛ ابتدا فروش را لغو کنید.");
            }

            var settlementCount = await _db.AssetRentTransactions
                .AsNoTracking()
                .CountAsync(a => a.TruckDispatchId != null && dispatchIds.Contains(a.TruckDispatchId.Value) && !a.IsCancelled, ct);
            if (settlementCount > 0)
            {
                Add($"رسید #{receipt.Id}: برای موتر این رسید کرایهٔ دارایی ثبت شده است؛ ابتدا آن را لغو کنید.");
            }

            var dispatchLossCount = await _db.LossEvents
                .AsNoTracking()
                .CountAsync(e => e.TruckDispatchId != null && dispatchIds.Contains(e.TruckDispatchId.Value) && !e.IsCancelled, ct);
            if (dispatchLossCount > 0)
            {
                Add($"رسید #{receipt.Id}: برای موتر این رسید کسری ثبت شده است؛ ابتدا رویداد کسری را لغو کنید.");
            }

            var dispatchCustomsCount = await _db.CustomsDeclarations
                .AsNoTracking()
                .CountAsync(c => c.TruckDispatchId != null && dispatchIds.Contains(c.TruckDispatchId.Value), ct);
            if (dispatchCustomsCount > 0)
            {
                Add($"رسید #{receipt.Id}: برای موتر این رسید سند گمرکی ثبت شده است؛ ابتدا سند گمرکی را لغو کنید.");
            }
        }

        // فروش مستقیم و مصرفِ دریافت مشتری روی آن.
        var directSaleIds = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Where(a => a.LoadingReceiptId == receipt.Id && a.SalesTransactionId != null)
            .Select(a => a.SalesTransactionId!.Value)
            .ToListAsync(ct);
        if (directSaleIds.Count > 0)
        {
            var appliedPaymentCount = await _db.CustomerPaymentAllocationApplications
                .AsNoTracking()
                .CountAsync(
                    x => directSaleIds.Contains(x.SalesTransactionId)
                        && x.Status == CustomerPaymentAllocationApplicationStatus.Active,
                    ct);
            if (appliedPaymentCount > 0)
            {
                Add($"رسید #{receipt.Id}: برای فروش مستقیم این رسید دریافت مشتری تخصیص یافته است؛ ابتدا تخصیص دریافت را برگردانید.");
            }

            var dispatchedSaleCount = await _db.TruckDispatches
                .AsNoTracking()
                .CountAsync(
                    d => d.SalesTransactionId != null
                        && directSaleIds.Contains(d.SalesTransactionId.Value)
                        && d.LoadingReceiptAllocationId == null
                        && d.Status != DispatchStatus.Cancelled,
                    ct);
            if (dispatchedSaleCount > 0)
            {
                Add($"رسید #{receipt.Id}: برای فروش این رسید دیسپچ جداگانه ثبت شده است؛ ابتدا دیسپچ را لغو کنید.");
            }
        }

        // کسری/ضایعهٔ همین رسید هنگام لغو برمی‌گردد، پس مقدار آن هنگام سنجش موجودی به
        // موجودی آزاد اضافه می‌شود؛ وگرنه رسیدِ دارای کسری هرگز قابل لغو نبود.
        var lossReturnMovements = await _db.LossEvents
            .AsNoTracking()
            .Where(e => e.LoadingReceiptId == receipt.Id && !e.IsCancelled && e.InventoryMovementId != null)
            .Join(
                _db.InventoryMovements.AsNoTracking(),
                e => e.InventoryMovementId!.Value,
                m => m.Id,
                (_, m) => m)
            .Where(m => m.Direction == MovementDirection.Out)
            .ToListAsync(ct);

        // موجودیِ واردشده باید هنوز موجود باشد تا برگشت آن موجودی را منفی نکند.
        foreach (var movement in inboundMovements)
        {
            var available = await _stock.GetFreeQuantityMtAsync(
                movement.ProductId,
                terminalId: movement.TerminalId,
                contractId: movement.ContractId,
                inventoryBatchId: movement.InventoryBatchId,
                storageTankId: movement.StorageTankId,
                ct: ct);

            available += lossReturnMovements
                .Where(m => m.ProductId == movement.ProductId
                    && m.TerminalId == movement.TerminalId
                    && m.StorageTankId == movement.StorageTankId)
                .Sum(m => m.QuantityMt);

            if (available < movement.QuantityMt)
            {
                Add($"رسید #{receipt.Id}: بار این رسید مصرف شده است (موجودی آزاد {available:N4} MT در برابر {movement.QuantityMt:N4} MT)؛ ابتدا عملیات مصرف‌کننده را لغو کنید.");
            }
        }

        return blockers;
    }

    private async Task<Dictionary<int, LoadingReceipt>> LoadReceiptsAsync(
        IReadOnlyCollection<int> receiptIds,
        bool tracking,
        CancellationToken ct)
    {
        var ids = NormalizeIds(receiptIds);
        if (ids.Count == 0)
        {
            return new Dictionary<int, LoadingReceipt>();
        }

        var query = _db.LoadingReceipts.Where(r => ids.Contains(r.Id));
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.ToDictionaryAsync(r => r.Id, ct);
    }

    private Task<List<InventoryMovement>> LoadInboundMovementsAsync(int receiptId, CancellationToken ct)
        => _db.InventoryMovements
            .Where(m => m.Direction == MovementDirection.In
                && (m.LoadingReceiptId == receiptId
                    || _db.LoadingReceiptAllocations.Any(
                        a => a.LoadingReceiptId == receiptId && a.InventoryMovementId == m.Id)))
            .ToListAsync(ct);

    private Task<List<SalesTransaction>> LoadDirectSalesAsync(int receiptId, CancellationToken ct)
        => _db.SalesTransactions
            .Where(s => !s.IsCancelled
                && _db.LoadingReceiptAllocations.Any(
                    a => a.LoadingReceiptId == receiptId && a.SalesTransactionId == s.Id))
            .ToListAsync(ct);

    private Task<List<TruckDispatch>> LoadActiveDispatchesAsync(int receiptId, CancellationToken ct)
        => _db.TruckDispatches
            .Where(d => d.Status != DispatchStatus.Cancelled
                && d.LoadingReceiptAllocationId != null
                && _db.LoadingReceiptAllocations.Any(
                    a => a.LoadingReceiptId == receiptId && a.Id == d.LoadingReceiptAllocationId!.Value))
            .ToListAsync(ct);

    private static string BuildCancelReference(string? reference, LoadingReceipt receipt)
    {
        var source = string.IsNullOrWhiteSpace(reference) ? $"RCPT-{receipt.Id}" : reference!;
        var value = source + "-CANCEL";
        return value.Length <= 500 ? value : value[..500];
    }

    private static List<int> NormalizeIds(IReadOnlyCollection<int> receiptIds)
        => (receiptIds ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
}
