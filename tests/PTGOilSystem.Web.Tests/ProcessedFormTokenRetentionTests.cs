using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ۱۲-A — نگهداریِ جدولِ توکن‌های Idempotency.
///
/// خطرِ واقعیِ این پاک‌سازی، خودِ رشدِ جدول نیست؛ حذفِ زودهنگام است: توکنی که هنوز ممکن
/// است دوباره ارسال شود، اگر پاک شود، همان سندِ تکراریِ PTG-P0-01 دوباره ساخته می‌شود.
/// این تست‌ها پنجرهٔ ایمنی را pin می‌کنند.
/// </summary>
public sealed class ProcessedFormTokenRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ProcessedFormToken Token(string value, DateTime? consumedAtUtc) => new()
    {
        Token = value,
        // فورمِ فقط-آنلاین: پنجرهٔ عادی ۹۰ روزه. توکنِ فورم‌های آفلاین‌پذیر جداگانه (OfflineReplayRetentionDays).
        Purpose = "Sale.Cancel",
        ConsumedAtUtc = consumedAtUtc
    };

    private static ProcessedFormTokenRetentionService Service(ApplicationDbContext db)
        => new(db, new FixedTimeProvider(Now));

    [Fact]
    public async Task ATokenInsideTheRetentionWindow_IsNeverRemoved()
    {
        await using var db = NewDb();
        db.ProcessedFormTokens.Add(Token("fresh", Now.UtcDateTime.AddDays(-89)));
        await db.SaveChangesAsync();

        var removed = await Service(db).PurgeExpiredAsync();

        Assert.Equal(0, removed);
        Assert.Equal(1, await db.ProcessedFormTokens.CountAsync());
    }

    [Fact]
    public async Task ATokenOlderThanTheWindow_IsRemoved()
    {
        await using var db = NewDb();
        db.ProcessedFormTokens.Add(Token("stale", Now.UtcDateTime.AddDays(-91)));
        await db.SaveChangesAsync();

        var removed = await Service(db).PurgeExpiredAsync();

        Assert.Equal(1, removed);
        Assert.Equal(0, await db.ProcessedFormTokens.CountAsync());
    }

    /// <summary>سنِ نامعلوم یعنی «نمی‌دانیم امن است یا نه» — پس دست نمی‌خورد.</summary>
    [Fact]
    public async Task ATokenWithoutAConsumedTimestamp_IsNeverRemoved()
    {
        await using var db = NewDb();
        db.ProcessedFormTokens.Add(Token("unknown-age", consumedAtUtc: null));
        await db.SaveChangesAsync();

        Assert.Equal(0, await Service(db).PurgeExpiredAsync());
        Assert.Equal(1, await db.ProcessedFormTokens.CountAsync());
    }

    /// <summary>
    /// پیکربندیِ کوتاه‌تر از کفِ ایمنی نباید بتواند پنجره را کوچک کند؛ وگرنه یک عددِ
    /// اشتباه در تنظیمات، محافظ ضدتکرار را خاموش می‌کرد.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-500)]
    public async Task ARetentionShorterThanTheHardMinimum_IsClampedNotHonoured(int requestedDays)
    {
        await using var db = NewDb();
        db.ProcessedFormTokens.Add(Token("two-weeks-old", Now.UtcDateTime.AddDays(-14)));
        await db.SaveChangesAsync();

        var service = Service(db);

        Assert.Equal(
            Now.UtcDateTime.AddDays(-ProcessedFormTokenRetentionService.MinimumRetentionDays),
            service.CutoffUtc(requestedDays));
        Assert.Equal(0, await service.PurgeExpiredAsync(requestedDays));
        Assert.Equal(1, await db.ProcessedFormTokens.CountAsync());
    }

    [Fact]
    public async Task EachRunIsBounded_SoCleanupNeverLocksTheWholeTable()
    {
        await using var db = NewDb();
        var old = Now.UtcDateTime.AddDays(-200);
        for (var index = 0; index < ProcessedFormTokenRetentionService.BatchSize + 25; index++)
        {
            db.ProcessedFormTokens.Add(Token($"old-{index}", old.AddSeconds(index)));
        }
        await db.SaveChangesAsync();

        var service = Service(db);

        Assert.Equal(ProcessedFormTokenRetentionService.BatchSize, await service.PurgeExpiredAsync());
        Assert.Equal(25, await db.ProcessedFormTokens.CountAsync());

        // اجرای بعدی بقیه را برمی‌دارد؛ کار نیمه‌تمام گم نمی‌شود.
        Assert.Equal(25, await service.PurgeExpiredAsync());
        Assert.Equal(0, await db.ProcessedFormTokens.CountAsync());
    }

    /// <summary>پس از پاک‌سازی، توکنِ زندهٔ همان فرم باید همچنان تکراری را بگیرد.</summary>
    [Fact]
    public async Task CleanupDoesNotWeakenDuplicateDetectionForLiveTokens()
    {
        await using var db = NewDb();
        db.ProcessedFormTokens.AddRange(
            Token("expired", Now.UtcDateTime.AddDays(-120)),
            Token("live", Now.UtcDateTime.AddMinutes(-2)));
        await db.SaveChangesAsync();

        await Service(db).PurgeExpiredAsync();

        Assert.False(await db.ProcessedFormTokens.AnyAsync(t => t.Token == "expired"));
        Assert.True(await db.ProcessedFormTokens.AnyAsync(t => t.Token == "live"));
    }

    [Fact]
    public async Task DurableBulkRowResults_ArePreservedAfterTheOrdinaryTokenWindow()
    {
        await using var db = NewDb();
        var durable = Token("bulk-row", Now.UtcDateTime.AddDays(-200));
        durable.Purpose = "Transport.BulkRow:request:fingerprint";
        durable.ReferenceType = nameof(InventoryTransportLeg);
        durable.ReferenceId = 42;
        db.ProcessedFormTokens.AddRange(durable, Token("ordinary-expired", Now.UtcDateTime.AddDays(-200)));
        await db.SaveChangesAsync();
        Assert.Equal(1, await Service(db).PurgeExpiredAsync());
        var remaining = await db.ProcessedFormTokens.SingleAsync();
        Assert.Equal("bulk-row", remaining.Token);
        Assert.Equal(42, remaining.ReferenceId);
    }

    /// <summary>
    /// توکنِ فورم‌های آفلاین‌پذیر: دستگاهی که پاسخ ارسال آنلاین را نگرفت، شاید ماه‌ها بعد همان فورم را با همان توکن
    /// بفرستد؛ توکن تا بیش از یک سال می‌ماند تا سند دوم ساخته نشود. توکن‌های دیگر همان ۹۰ روز.
    /// </summary>
    [Theory]
    [InlineData("Expense.Create")]
    [InlineData("Payment.Create")]
    [InlineData("Sale.Create")]
    [InlineData("Offline.Form.Submit")]
    public async Task OfflineReplayableTokens_AreKeptForMoreThanAYear(string purpose)
    {
        await using var db = NewDb();
        var kept = Token("year-old", Now.UtcDateTime.AddDays(-365));
        kept.Purpose = purpose;
        var expired = Token("too-old", Now.UtcDateTime.AddDays(-(ProcessedFormTokenRetentionService.OfflineReplayRetentionDays + 1)));
        expired.Purpose = purpose;
        db.ProcessedFormTokens.AddRange(kept, expired, Token("ordinary", Now.UtcDateTime.AddDays(-91)));
        await db.SaveChangesAsync();

        Assert.Equal(2, await Service(db).PurgeExpiredAsync());
        Assert.Equal("year-old", (await db.ProcessedFormTokens.SingleAsync()).Token);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
