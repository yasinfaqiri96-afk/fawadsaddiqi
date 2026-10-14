using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Inventory;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Models.Reports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CustodyPostgresCollection.CollectionName)]
public sealed class ManualInventoryPostgresTests(ConcurrencyVersionPostgresFixture fixture)
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private sealed record World(int Product, int Terminal, int DestinationTerminal, int Tank, int DestinationTank);

    private async Task<World> SeedAsync(decimal capacity = 1000m)
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await using var db = fixture.CreateDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var product = new Product { Code = "MI-" + tag, Name = "تیل " + tag };
        var source = new Terminal { Code = "MS-" + tag, Name = "مبدأ" };
        var destination = new Terminal { Code = "MD-" + tag, Name = "مقصد" };
        db.AddRange(product, source, destination);
        await db.SaveChangesAsync();
        var tank = new StorageTank { TankCode = "MS-" + tag, TerminalId = source.Id, ProductId = product.Id, CapacityMt = 1000m };
        var destTank = new StorageTank { TankCode = "MD-" + tag, TerminalId = destination.Id, ProductId = product.Id, CapacityMt = capacity };
        db.AddRange(tank, destTank);
        await db.SaveChangesAsync();
        return new(product.Id, source.Id, destination.Id, tank.Id, destTank.Id);
    }

    private static ManualInventoryService Service(ApplicationDbContext db)
    {
        var stock = new StockService(db);
        return new(db, stock, new InventoryMovementWriter(db, stock), new AuditService(db), new FormTokenGuard(db));
    }

    private static InventoryMovementCreateViewModel Input(World w, MovementDirection direction, decimal quantity = 10m)
        => new() { ProductId = w.Product, TerminalId = w.Terminal, StorageTankId = w.Tank,
            DestinationTerminalId = w.DestinationTerminal, DestinationStorageTankId = w.DestinationTank,
            Direction = direction, QuantityMt = quantity, MovementDate = Day.AddDays(1),
            Reason = "سند شمارش مخزن", ReferenceDocument = "شماره سند مشترک" };

    private async Task InAsync(World w, decimal quantity = 100m, DateTime? day = null)
    {
        await using var db = fixture.CreateDbContext();
        var input = Input(w, MovementDirection.In, quantity);
        input.MovementDate = day ?? Day;
        await Service(db).PostAsync(input, Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task RepeatedToken_IsOneDocument_WhileSameHumanReferenceWithNewTokenIsValid()
    {
        var w = await SeedAsync();
        var token = Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateDbContext()) await Service(db).PostAsync(Input(w, MovementDirection.In), token);
        await using (var db = fixture.CreateDbContext()) await Service(db).PostAsync(Input(w, MovementDirection.In), token);
        await using (var db = fixture.CreateDbContext()) await Service(db).PostAsync(Input(w, MovementDirection.In), Guid.NewGuid().ToString("N"));
        await using var check = fixture.CreateDbContext();
        Assert.Equal(2, await check.InventoryMovements.CountAsync(m => m.ProductId == w.Product));
        Assert.Equal(20m, await new StockService(check).GetFreeQuantityMtAsync(w.Product));
    }

    [Fact]
    public async Task ConcurrentRepeatedToken_CommitsOnce()
    {
        var w = await SeedAsync();
        var token = Guid.NewGuid().ToString("N");
        async Task Save()
        {
            await using var db = fixture.CreateDbContext();
            try { await Service(db).PostAsync(Input(w, MovementDirection.In), token); }
            catch (Exception ex) when (new FormTokenGuard(db).IsDuplicate(ex)) { }
        }
        await Task.WhenAll(Save(), Save());
        await using var check = fixture.CreateDbContext();
        Assert.Single(await check.InventoryMovements.Where(m => m.ProductId == w.Product).ToListAsync());
        Assert.Equal(10m, await new StockService(check).GetFreeQuantityMtAsync(w.Product));
    }

    [Fact]
    public async Task Transfer_PreservesTotal_AndCreatesExactlyOneOutAndOneIn()
    {
        var w = await SeedAsync();
        await InAsync(w);
        var token = Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateDbContext()) await Service(db).PostAsync(Input(w, MovementDirection.Transfer, 25m), token);
        await using (var db = fixture.CreateDbContext()) await Service(db).PostAsync(Input(w, MovementDirection.Transfer, 25m), token);
        await using var check = fixture.CreateDbContext();
        var stock = new StockService(check);
        Assert.Equal(75m, await stock.GetFreeQuantityMtAsync(w.Product, storageTankId: w.Tank));
        Assert.Equal(25m, await stock.GetFreeQuantityMtAsync(w.Product, storageTankId: w.DestinationTank));
        Assert.Equal(100m, await stock.GetFreeQuantityMtAsync(w.Product));
        var rows = await check.InventoryMovements.Where(m => m.ProductId == w.Product && m.MovementDate == Day.AddDays(1)).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Where(m => m.Direction == MovementDirection.Out));
        Assert.Single(rows.Where(m => m.Direction == MovementDirection.In));
        Assert.All(rows, row => Assert.Contains("سند شمارش مخزن", row.Notes));
    }

    [Fact]
    public async Task DestinationCapacityFailure_RollsBackSourceAndToken_AndAllowsCorrectedRetry()
    {
        var w = await SeedAsync(20m);
        await InAsync(w);
        var token = Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateDbContext())
            await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).PostAsync(Input(w, MovementDirection.Transfer, 25m), token));
        await using (var check = fixture.CreateDbContext())
        {
            Assert.Equal(100m, await new StockService(check).GetFreeQuantityMtAsync(w.Product, storageTankId: w.Tank));
            Assert.False(await check.ProcessedFormTokens.AnyAsync(t => t.Token == token));
        }
        await using (var db = fixture.CreateDbContext()) await Service(db).PostAsync(Input(w, MovementDirection.Transfer, 20m), token);
        await using var final = fixture.CreateDbContext();
        Assert.Equal(100m, await new StockService(final).GetFreeQuantityMtAsync(w.Product));
    }

    [Theory]
    [InlineData(MovementDirection.Out)]
    [InlineData(MovementDirection.Transfer)]
    [InlineData(MovementDirection.Adjustment)]
    public async Task BackdatedWithdrawal_CannotBreakALaterBalanceEvenIfAnotherReceiptHealsIt(MovementDirection direction)
    {
        var w = await SeedAsync();
        await InAsync(w);
        await using (var db = fixture.CreateDbContext())
        {
            var output = Input(w, MovementDirection.Out, 80m);
            output.MovementDate = Day.AddDays(2);
            await Service(db).PostAsync(output, Guid.NewGuid().ToString("N"));
        }
        await InAsync(w, 100m, Day.AddDays(3));
        var token = Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateDbContext())
        {
            var input = Input(w, direction, 30m);
            input.NegativeAdjustment = true;
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).PostAsync(input, token));
            Assert.Equal("STOCK_FUTURE_NEGATIVE", ex.Code);
        }
        await using var check = fixture.CreateDbContext();
        Assert.Equal(120m, await new StockService(check).GetFreeQuantityMtAsync(w.Product));
        Assert.False(await check.ProcessedFormTokens.AnyAsync(t => t.Token == token));
    }

    [Theory]
    [InlineData(false, 110)]
    [InlineData(true, 90)]
    public async Task Adjustment_HasCorrectSignedEffectAndRequiredReason(bool negative, int expected)
    {
        var w = await SeedAsync();
        await InAsync(w);
        var input = Input(w, MovementDirection.Adjustment);
        input.NegativeAdjustment = negative;
        input.Reason = null;
        await using (var db = fixture.CreateDbContext())
            await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).PostAsync(input, Guid.NewGuid().ToString("N")));
        input.Reason = "تفاوت شمارش";
        await using (var db = fixture.CreateDbContext()) await Service(db).PostAsync(input, Guid.NewGuid().ToString("N"));
        await using var check = fixture.CreateDbContext();
        var stock = new StockService(check);
        Assert.Equal(expected, await stock.GetFreeQuantityMtAsync(w.Product));
        Assert.Equal(expected, (await stock.GetStockSummaryAsync(productId: w.Product)).Sum(s => s.FreeQuantityMt));
        Assert.Equal(expected, (await stock.GetStockCardAsync(productId: w.Product)).Sum(s => s.SignedQuantityMt));
        var sale = await new PreSaleReservationService(check, new PTGOilSystem.Web.Services.Time.AfghanistanBusinessClock(TimeProvider.System))
            .GetSellableStockAsync(new ManagementReportFilterViewModel { ProductId = w.Product });
        Assert.Equal(expected, sale.Sum(s => s.PhysicalStockMt));
        Assert.Equal(expected, sale.Sum(s => s.SellableMt));
    }

    [Fact]
    public async Task SameDayReceiptAndWithdrawal_AreAllowedInTheirRecordedOrder()
    {
        var w = await SeedAsync();
        await InAsync(w);
        await using var db = fixture.CreateDbContext();
        var input = Input(w, MovementDirection.Out, 20m);
        input.MovementDate = Day;
        await Service(db).PostAsync(input, Guid.NewGuid().ToString("N"));
        Assert.Equal(80m, await new StockService(db).GetFreeQuantityMtAsync(w.Product));
    }

    [Fact]
    public async Task MissingTokenAndSameLocation_AreRejectedBeforeAnyWrite()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).PostAsync(Input(w, MovementDirection.In), null));
        var input = Input(w, MovementDirection.Transfer);
        input.DestinationTerminalId = input.TerminalId;
        input.DestinationStorageTankId = input.StorageTankId;
        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).PostAsync(input, Guid.NewGuid().ToString("N")));
        Assert.Empty(await db.InventoryMovements.Where(m => m.ProductId == w.Product).ToListAsync());
    }

    [Fact]
    public async Task AccountingFailure_RollsBackStockAndToken()
    {
        var w = await SeedAsync();
        var token = Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateDbContext())
        {
            var stock = new StockService(db);
            var service = new ManualInventoryService(db, stock, new InventoryMovementWriter(db, stock),
                new AuditService(db), new FormTokenGuard(db), Options.Create(new AccountingOptions { Enabled = true }));
            await Assert.ThrowsAsync<BusinessRuleException>(() => service.PostAsync(Input(w, MovementDirection.In), token));
        }
        await using var check = fixture.CreateDbContext();
        Assert.Empty(await check.InventoryMovements.Where(m => m.ProductId == w.Product).ToListAsync());
        Assert.False(await check.ProcessedFormTokens.AnyAsync(t => t.Token == token));
    }

    [Fact]
    public async Task Accounting_TransferMovesOriginalCost_AdjustmentPostsBalancedJournal_AndRetryDoesNotDuplicate()
    {
        var w = await SeedAsync();
        var options = Options.Create(new AccountingOptions { Enabled = true });
        int companyId;
        int offsetId;
        await using (var db = fixture.CreateDbContext())
        {
            var company = await db.Companies.FirstOrDefaultAsync(c => c.IsSystemOwner);
            if (company is null)
            {
                company = new Company { Code = "MI-OWNER", Name = "مشعل", Country = "AF", IsSystemOwner = true };
                db.Companies.Add(company);
                await db.SaveChangesAsync();
            }
            companyId = company.Id;
            await new AccountingChartSeeder(db, options).SeedAsync();
            offsetId = await db.AccountingSettings.Where(s => s.CompanyId == companyId).Select(s => s.InventoryLossAccountId).SingleAsync();
            if (!await db.FiscalYears.AnyAsync(y => y.CompanyId == companyId && y.StartDate <= Day && y.EndDate >= Day))
            {
                var year = new FiscalYear { CompanyId = companyId, Name = "MI-2026", StartDate = new DateTime(2026, 1, 1),
                    EndDate = new DateTime(2026, 12, 31), Status = FiscalYearStatus.Open, IsCurrent = true };
                db.FiscalYears.Add(year);
                await db.SaveChangesAsync();
                for (var month = 1; month <= 12; month++)
                {
                    var start = new DateTime(2026, month, 1);
                    db.FiscalPeriods.Add(new FiscalPeriod { CompanyId = companyId, FiscalYearId = year.Id, PeriodNumber = month,
                        Name = $"MI-2026-{month:D2}", StartDate = start, EndDate = start.AddMonths(1).AddDays(-1), Status = FiscalPeriodStatus.Open });
                }
                await db.SaveChangesAsync();
            }
        }
        async Task Post(InventoryMovementCreateViewModel input, string token)
        {
            await using var db = fixture.CreateDbContext();
            var stock = new StockService(db);
            var company = new SystemCompanyProvider(db);
            var service = new ManualInventoryService(db, stock, new InventoryMovementWriter(db, stock), new AuditService(db),
                new FormTokenGuard(db), options, new InventoryValuationService(db),
                new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, company), company);
            await service.PostAsync(input, token);
        }
        var receipt = Input(w, MovementDirection.In, 100m);
        receipt.MovementDate = Day;
        receipt.ValueUsd = 60000m;
        receipt.OffsetAccountId = offsetId;
        await Post(receipt, Guid.NewGuid().ToString("N"));
        var transferToken = Guid.NewGuid().ToString("N");
        await Post(Input(w, MovementDirection.Transfer, 25m), transferToken);
        await Post(Input(w, MovementDirection.Transfer, 25m), transferToken);
        var adjustment = Input(w, MovementDirection.Adjustment, 5m);
        adjustment.MovementDate = Day.AddDays(2);
        adjustment.NegativeAdjustment = true;
        adjustment.OffsetAccountId = offsetId;
        await Post(adjustment, Guid.NewGuid().ToString("N"));
        await using var check = fixture.CreateDbContext();
        var pools = await check.InventoryAverageCosts.Where(p => p.CompanyId == companyId && p.ProductId == w.Product).ToListAsync();
        Assert.Equal(95m, pools.Sum(p => p.QuantityMt));
        Assert.Equal(57000m, pools.Sum(p => p.TotalValueUsd));
        Assert.Equal(15000m, pools.Single(p => p.TerminalId == w.DestinationTerminal).TotalValueUsd);
        var journals = await check.JournalEntries.Include(j => j.Lines).Where(j => j.SourceModule == "ManualInventory"
            && j.Lines.Any(l => l.ProductId == w.Product)).ToListAsync();
        Assert.Equal(3, journals.Count);
        Assert.All(journals, journal => Assert.Equal(journal.Lines.Sum(l => l.Debit), journal.Lines.Sum(l => l.Credit)));
        Assert.Empty(await check.CustodyMovements.Where(m => m.CustodyLot!.ProductId == w.Product).ToListAsync());
    }
}
