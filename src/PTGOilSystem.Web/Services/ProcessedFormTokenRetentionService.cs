using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// نگهداریِ جدولِ <c>ProcessedFormTokens</c> (یافتهٔ باقی‌ماندهٔ فاز P0-01).
///
/// هر ثبتِ موفق یک سطر می‌سازد و هیچ‌چیز آن را برنمی‌داشت؛ جدول برای همیشه رشد می‌کرد.
///
/// <b>قاعدهٔ ایمنی:</b> توکن تا وقتی «پنجرهٔ تلاش دوباره» تمام نشده باشد حذف نمی‌شود.
/// اگر توکنی زودتر از موعد پاک شود، همان ارسالِ دوباره‌ای که قرار بود مسدود شود سند
/// تکراری می‌سازد — یعنی بازگشتِ خودِ PTG-P0-01. به همین دلیل پنجره عمداً بلند است
/// (پیش‌فرض ۹۰ روز) و کمتر از حداقلِ سختِ ۳۰ روز پذیرفته نمی‌شود.
///
/// حذف دسته‌ای و کران‌دار است: هر بار حداکثر <see cref="BatchSize"/> سطر، بر پایهٔ
/// <c>ConsumedAtUtc</c>، تا یک پاک‌سازی هرگز جدول را قفل نکند.
/// </summary>
public sealed class ProcessedFormTokenRetentionService(ApplicationDbContext db, TimeProvider? timeProvider = null)
{
    /// <summary>کف مطلق. هیچ پیکربندی‌ای نمی‌تواند پنجره را کوتاه‌تر کند.</summary>
    public const int MinimumRetentionDays = 30;

    public const int DefaultRetentionDays = 90;

    /// <summary>
    /// توکنِ سندهای فورم‌هایی که آفلاین هم ثبت می‌شوند (<c>OfflineFormRegistry</c>) و توکن‌های <c>Offline.*</c>.
    /// اگر فورم آنلاین فرستاده شد و پاسخ نیامد، دستگاه همان فورم را با همان توکن صفحه نگه می‌دارد و شاید پس از ماه‌ها
    /// قطع اینترنت بفرستد؛ توکن باید تا آن وقت باشد تا سند دوم ساخته نشود. بیش از یک سال؛ پس از آن سرور چنین
    /// عملیاتی را خودکار ثبت نمی‌کند و برای بررسی کاربر نگه می‌دارد (<c>OfflineSyncService</c>).
    /// </summary>
    public const int OfflineReplayRetentionDays = 400;

    /// <summary>سقفِ هر اجرا. پاک‌سازی نباید یک تراکنش بزرگِ قفل‌کننده بسازد.</summary>
    public const int BatchSize = 5_000;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// مرزِ حذف: هر توکنی که پیش از این لحظه مصرف شده، دیگر داخل پنجرهٔ ایمنی نیست.
    /// </summary>
    public DateTime CutoffUtc(int retentionDays)
        => _timeProvider.GetUtcNow().UtcDateTime.AddDays(-Math.Max(retentionDays, MinimumRetentionDays));

    /// <summary>
    /// یک دستهٔ کران‌دار از توکن‌های منقضی را حذف می‌کند و تعداد حذف‌شده را برمی‌گرداند.
    /// توکنی که <c>ConsumedAtUtc</c> ندارد هرگز حذف نمی‌شود: سنِ آن معلوم نیست.
    /// ارتباط پایدار ردیف‌های تبدیل گروهی نیز بخشی از تاریخچه است و منقضی نمی‌شود.
    /// </summary>
    public async Task<int> PurgeExpiredAsync(
        int retentionDays = DefaultRetentionDays,
        CancellationToken cancellationToken = default)
    {
        var cutoff = CutoffUtc(retentionDays);
        var offlineReplayCutoff = CutoffUtc(Math.Max(retentionDays, OfflineReplayRetentionDays));

        var expired = await db.ProcessedFormTokens
            .Where(token => token.ConsumedAtUtc != null && token.ConsumedAtUtc < cutoff
                // Durable bulk row outcomes are part of the operation's request history.
                // Removing them could consume a still-valid partial source again on retry.
                && !token.Purpose.StartsWith("Transport.BulkRow:")
                // سندِ فورم‌های آفلاین‌پذیر: پنجرهٔ بلندتر (OfflineReplayRetentionDays).
                && (token.ConsumedAtUtc < offlineReplayCutoff
                    || !(token.Purpose.StartsWith("Offline.") || token.Purpose.StartsWith("Contract.")
                        || token.Purpose.StartsWith("Loading") || token.Purpose.StartsWith("Transport.")
                        || token.Purpose.StartsWith("InventoryTransport.") || token.Purpose.StartsWith("TruckSettlement.")
                        || token.Purpose.StartsWith("Dispatch.") || token.Purpose.StartsWith("LossEvent.")
                        || token.Purpose.StartsWith("Sale.Create") || token.Purpose.StartsWith("Sale.SetPrice")
                        || token.Purpose.StartsWith("Payment.") || token.Purpose.StartsWith("Expense.")
                        || token.Purpose.StartsWith("PartySettlement.") || token.Purpose.StartsWith("PartnerSettlement.")
                        || token.Purpose.StartsWith("SupplierBalanceTransfer.") || token.Purpose.StartsWith("Custody.")
                        || token.Purpose.StartsWith("CommodityLoan."))))
            .OrderBy(token => token.ConsumedAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        db.ProcessedFormTokens.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }
}

/// <summary>
/// اجرا‌کنندهٔ پس‌زمینهٔ پاک‌سازی. از همان الگوی <c>BackupSchedulerHostedService</c>
/// استفاده می‌کند تا هیچ زیرساخت تازه‌ای (صف بیرونی، زمان‌بند خارجی) لازم نشود.
/// </summary>
public sealed class ProcessedFormTokenRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProcessedFormTokenRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // کمی صبر تا راه‌اندازی برنامه و Migration تمام شود.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var service = new ProcessedFormTokenRetentionService(db);

                var removed = await service.PurgeExpiredAsync(cancellationToken: stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Removed {Count} expired idempotency tokens.", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // پاک‌سازی هرگز نباید برنامه را زمین بزند؛ دفعهٔ بعد دوباره تلاش می‌شود.
                logger.LogWarning(ex, "Idempotency token cleanup failed; will retry.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
