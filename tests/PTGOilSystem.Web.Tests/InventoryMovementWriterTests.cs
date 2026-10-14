using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// Writer تنها نقطهٔ اجرای قواعد مشترک ثبت حرکت موجودی است. این تست‌ها قرارداد آن را قفل می‌کنند:
// نگهبان مقدار، نگهبان موجودی برای خروجی، معکوسِ idempotent، و مهم‌تر از همه اینکه Writer
// هرگز تراکنش مستقل باز نمی‌کند (تراکنش مال caller است).
public class InventoryMovementWriterTests
{
    [Fact]
    public async Task PostInbound_Writes_The_Movement_With_The_Callers_Reference()
    {
        await using var db = BuildDb();
        await SeedAsync(db);

        var movement = await BuildWriter(db).PostInboundAsync(Request(quantityMt: 40m, reference: "SEED-IN:1"));

        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal(40m, movement.QuantityMt);
        Assert.Equal("SEED-IN:1", movement.ReferenceDocument);
        Assert.True(movement.Id > 0);
    }

    [Fact]
    public async Task PostOutbound_Blocks_When_The_Scope_Does_Not_Hold_Enough_Stock()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        await BuildWriter(db).PostInboundAsync(Request(quantityMt: 10m, reference: "SEED-IN:1"));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildWriter(db).PostOutboundAsync(Request(quantityMt: 25m, reference: "OUT:1")));

        Assert.Equal("STOCK_INSUFFICIENT", error.Code);
        Assert.Equal(0, await db.InventoryMovements.CountAsync(m => m.Direction == MovementDirection.Out));
    }

    // نگهبان‌ها انتخابی‌اند چون مسیرهای فعلی ترکیب‌های متفاوتی دارند و آن تفاوت‌ها تصمیم عملیاتی‌اند.
    [Fact]
    public async Task PostOutbound_Skips_The_Stock_Check_When_The_Caller_Already_Ran_It()
    {
        await using var db = BuildDb();
        await SeedAsync(db);

        var movement = await BuildWriter(db).PostOutboundAsync(
            Request(quantityMt: 25m, reference: "OUT:1"),
            StockGuard.None);

        Assert.Equal(MovementDirection.Out, movement.Direction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_Positive_Quantities_Are_Rejected(int quantityMt)
    {
        await using var db = BuildDb();
        await SeedAsync(db);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildWriter(db).PostInboundAsync(Request(quantityMt, "ANY:1")));

        Assert.Equal("STOCK_QTY_NON_POSITIVE", error.Code);
    }

    [Fact]
    public async Task A_Movement_Without_A_Reference_Is_Rejected()
    {
        await using var db = BuildDb();
        await SeedAsync(db);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildWriter(db).PostInboundAsync(Request(10m, "   ")));

        Assert.Equal("STOCK_REFERENCE_MISSING", error.Code);
    }

    [Fact]
    public async Task Reversal_Mirrors_The_Original_And_Keeps_The_Cancel_Reference()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        var original = await BuildWriter(db).PostInboundAsync(Request(30m, "TRUCK-DISPATCH:7"));

        var reversal = await BuildWriter(db).PostReversalAsync(original, new DateTime(2026, 5, 9), "cancel");

        Assert.NotNull(reversal);
        Assert.Equal(MovementDirection.Out, reversal!.Direction);
        Assert.Equal(30m, reversal.QuantityMt);
        Assert.Equal("TRUCK-DISPATCH:7-CANCEL", reversal.ReferenceDocument);
        Assert.Equal(original.ContractId, reversal.ContractId);
        Assert.Equal(original.StorageTankId, reversal.StorageTankId);
    }

    // دابل‌کلیک/ارسال مجدد فرم لغو نباید سند برگشتیِ دوم بسازد.
    [Fact]
    public async Task ReversalOfPositiveAdjustment_RemovesItsQuantity_WithoutDuplicatingStock()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        var writer = BuildWriter(db);
        var original = await writer.PostAdjustmentAsync(Request(30m, "ADJUSTMENT-1"));
        var reversal = await writer.PostReversalAsync(original, new DateTime(2026, 5, 9));
        Assert.Equal(MovementDirection.Out, reversal!.Direction);
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(original.ProductId));
        Assert.Null(await writer.PostReversalAsync(original, new DateTime(2026, 5, 9)));
    }

    // دابل‌کلیک/ارسال مجدد فرم لغو نباید سند برگشتیِ دوم بسازد.
    [Fact]
    public async Task Reversing_Twice_Is_A_No_Op()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        var original = await BuildWriter(db).PostInboundAsync(Request(30m, "TRUCK-DISPATCH:7"));

        Assert.NotNull(await BuildWriter(db).PostReversalAsync(original, new DateTime(2026, 5, 9)));
        Assert.Null(await BuildWriter(db).PostReversalAsync(original, new DateTime(2026, 5, 9)));

        Assert.Equal(1, await db.InventoryMovements.CountAsync(m => m.ReferenceDocument!.EndsWith("-CANCEL")));
    }

    // مالکیت تراکنش: Writer نباید تراکنش مستقل باز کند، وگرنه rollback مربوط به caller
    // نمی‌تواند سندی را که Writer ثبت کرده برگرداند.
    [Fact]
    public async Task Writer_Runs_Inside_The_Callers_Transaction_And_Rolls_Back_With_It()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        // فقط کلیدهای واقعاً لازم؛ SQLite برخلاف InMemory کلید خارجی را اجرا می‌کند.
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        await db.SaveChangesAsync();

        var request = new InventoryMovementRequest
        {
            ProductId = 1,
            TerminalId = 1,
            MovementDate = new DateTime(2026, 5, 8),
            QuantityMt = 15m,
            ReferenceDocument = "ROLLBACK-ME:1"
        };

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await BuildWriter(db).PostInboundAsync(request);
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.InventoryMovements.CountAsync(m => m.ReferenceDocument == "ROLLBACK-ME:1"));
        await connection.CloseAsync();
    }

    [Fact]
    public async Task ReferenceExists_Sees_A_Posted_Movement()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        await BuildWriter(db).PostInboundAsync(Request(5m, "TRANSPORT-RECEIPT:3"));

        Assert.True(await BuildWriter(db).ReferenceExistsAsync("TRANSPORT-RECEIPT:3"));
        Assert.True(await BuildWriter(db).ReferenceExistsAsync("TRANSPORT-RECEIPT:3", MovementDirection.In));
        Assert.False(await BuildWriter(db).ReferenceExistsAsync("TRANSPORT-RECEIPT:3", MovementDirection.Out));
    }

    private static InventoryMovementRequest Request(decimal quantityMt, string reference)
        => new()
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            MovementDate = new DateTime(2026, 5, 8),
            QuantityMt = quantityMt,
            ReferenceDocument = reference
        };

    private static InventoryMovementWriter BuildWriter(ApplicationDbContext db)
        => new(db, new StockService(db));

    private static ApplicationDbContext BuildDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.StorageTanks.Add(new StorageTank { Id = 1, TankCode = "A", TerminalId = 1, ProductId = 1, IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CTR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 500m
        });
        await db.SaveChangesAsync();
    }
}
