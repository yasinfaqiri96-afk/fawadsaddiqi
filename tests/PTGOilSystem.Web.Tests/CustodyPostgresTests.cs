using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Custody;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Parties;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[CollectionDefinition(CustodyPostgresCollection.CollectionName, DisableParallelization = true)]
public sealed class CustodyPostgresCollection : ICollectionFixture<ConcurrencyVersionPostgresFixture>
{
    public const string CollectionName = "PTG Custody PostgreSQL";
}

/// <summary>
/// امانی — قواعد مقدار، مالکیت، محل، قفل و ضدتکرار روی PostgreSQL واقعی. هیچ عملیات امانی
/// نباید InventoryMovement، Ledger، Journal، فروش یا قرارداد بسازد.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CustodyPostgresCollection.CollectionName)]
public sealed class CustodyPostgresTests(ConcurrencyVersionPostgresFixture fixture)
{
    private static readonly DateTime Day1 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = Day1.AddDays(1);
    private static readonly DateTime Day3 = Day1.AddDays(2);

    private void RequirePostgres()
        => Assert.True(fixture.Available, $"تست امانی به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

    private sealed record World(
        int ProductId, int OtherProductId, int TerminalId, int TankId, int Tank2Id, int SmallTankId,
        int ThirdPartyTerminalId, int CustomerId, int Customer2Id, int SupplierId, int SystemCompanyId);

    private async Task<World> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = fixture.CreateDbContext();
        var product = new Product { Code = $"D-{tag}", Name = "Diesel" };
        var other = new Product { Code = $"P-{tag}", Name = "Petrol" };
        var terminal = new Terminal { Code = $"T-{tag}", Name = $"Herat {tag}" };
        var supplier = new Supplier { Name = $"Farid {tag}" };
        db.AddRange(product, other, terminal, supplier);
        await db.SaveChangesAsync();
        var thirdParty = new Terminal
        {
            Code = $"X-{tag}", Name = $"Farid depot {tag}",
            CustodianPartyType = AccountingPartyType.Supplier, CustodianPartyId = supplier.Id
        };
        var tank = new StorageTank { TerminalId = terminal.Id, TankCode = $"A-{tag}", CapacityMt = 1_000m };
        var tank2 = new StorageTank { TerminalId = terminal.Id, TankCode = $"B-{tag}", CapacityMt = 1_000m };
        var small = new StorageTank { TerminalId = terminal.Id, TankCode = $"S-{tag}", CapacityMt = 120m, ProductId = product.Id };
        var customer = new Customer { Name = $"Ahmad {tag}" };
        var customer2 = new Customer { Name = $"Karim {tag}" };
        db.AddRange(thirdParty, tank, tank2, small, customer, customer2);
        var company = await db.Companies.FirstOrDefaultAsync(c => c.IsSystemOwner);
        if (company is null)
        {
            company = new Company { Code = $"C-{tag}", Name = "Our company", Country = "AF", IsSystemOwner = true };
            db.Companies.Add(company);
        }
        await db.SaveChangesAsync();
        return new World(product.Id, other.Id, terminal.Id, tank.Id, tank2.Id, small.Id, thirdParty.Id,
            customer.Id, customer2.Id, supplier.Id, company.Id);
    }

    private static CustodyService Writer(ApplicationDbContext db)
        => new(db, new AuditService(db), new FormTokenGuard(db));

    private static CustodyReadService Reader(ApplicationDbContext db)
        => new(db, new StockService(db), new PartyDirectory(db));

    private async Task<int> ReceiveAsync(World w, decimal qty, int? tankId = null, int? customerId = null, string? token = null, int? productId = null)
    {
        await using var db = fixture.CreateDbContext();
        return await Writer(db).ReceiveAsync(new CustodyReceiptInput
        {
            Owner = new CustodyOwner(AccountingPartyType.Customer, customerId ?? w.CustomerId),
            ProductId = productId ?? w.ProductId,
            Location = new CustodyLocation(w.TerminalId, tankId ?? w.TankId),
            ReceiptDate = Day1,
            QuantityMt = qty,
            FormToken = token
        });
    }

    private async Task<Guid> ReleaseAsync(World w, int lotId, decimal qty, CustodyMovementKind kind = CustodyMovementKind.DeliveryToThirdParty, DateTime? date = null, int? tankId = null)
    {
        await using var db = fixture.CreateDbContext();
        return await Writer(db).ReleaseAsync(new CustodyReleaseInput
        {
            LotId = lotId,
            Kind = kind,
            Location = new CustodyLocation(w.TerminalId, tankId ?? w.TankId),
            MovementDate = date ?? Day2,
            QuantityMt = qty,
            CounterpartyName = "Customer of owner"
        });
    }

    private async Task<CustodyLotSummary> LotAsync(int lotId)
    {
        await using var db = fixture.CreateDbContext();
        return (await Reader(db).GetLotDetailAsync(lotId))!.Summary;
    }

    private async Task<(int Movements, int Ledger, int Journal, int Sales, int Contracts)> FinancialFootprintAsync()
    {
        await using var db = fixture.CreateDbContext();
        return (await db.InventoryMovements.CountAsync(), await db.LedgerEntries.CountAsync(),
            await db.JournalEntries.CountAsync(), await db.SalesTransactions.CountAsync(), await db.Contracts.CountAsync());
    }

    [Fact]
    public async Task Receipt_Of_100_Tons_Needs_No_Purchase_Price_And_Creates_No_Financial_Or_Company_Stock_Row()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var before = await FinancialFootprintAsync();

        var lotId = await ReceiveAsync(w, 100m);

        var lot = await LotAsync(lotId);
        Assert.Equal(100m, lot.BalanceMt);
        Assert.Equal(100m, lot.FreeMt);
        Assert.StartsWith("AM-", lot.LotNumber);
        Assert.Equal(before, await FinancialFootprintAsync());

        await using var db = fixture.CreateDbContext();
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(w.ProductId, storageTankId: w.TankId));
    }

    [Fact]
    public async Task Partial_Delivery_And_Return_Leave_Remaining_Owner_Balance()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 100m);

        await ReleaseAsync(w, lotId, 30m);
        Assert.Equal(70m, (await LotAsync(lotId)).BalanceMt);

        await ReleaseAsync(w, lotId, 20m, CustodyMovementKind.ReturnToOwner);
        Assert.Equal(50m, (await LotAsync(lotId)).BalanceMt);
    }

    [Fact]
    public async Task Release_Beyond_Balance_Is_Rejected()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 40m);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => ReleaseAsync(w, lotId, 40.0001m));
        Assert.Equal("CUSTODY_INSUFFICIENT", error.Code);
        Assert.Equal(40m, (await LotAsync(lotId)).BalanceMt);
    }

    [Fact]
    public async Task Backdated_Release_That_Would_Make_A_Later_Day_Negative_Is_Rejected()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var lotId = await Writer(db).ReceiveAsync(new CustodyReceiptInput
            {
                Owner = new CustodyOwner(AccountingPartyType.Customer, w.CustomerId),
                ProductId = w.ProductId,
                Location = new CustodyLocation(w.TerminalId, w.TankId),
                ReceiptDate = Day2,
                QuantityMt = 50m
            });
            var error = await Assert.ThrowsAsync<BusinessRuleException>(() => ReleaseAsync(w, lotId, 10m, date: Day1));
            Assert.Equal("CUSTODY_BACKDATED_NEGATIVE", error.Code);
        }
    }

    [Fact]
    public async Task Two_Concurrent_Releases_From_The_Same_Lot_Cannot_Oversell()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 100m);

        var results = await Task.WhenAll(
            Task.Run(async () => { try { await ReleaseAsync(w, lotId, 60m); return true; } catch (BusinessRuleException) { return false; } }),
            Task.Run(async () => { try { await ReleaseAsync(w, lotId, 60m); return true; } catch (BusinessRuleException) { return false; } }));

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(40m, (await LotAsync(lotId)).BalanceMt);
    }

    [Fact]
    public async Task Repeating_The_Same_Form_Token_Does_Not_Post_Twice()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var token = Guid.NewGuid().ToString("N");
        await ReceiveAsync(w, 25m, token: token);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => ReceiveAsync(w, 25m, token: token));
        await using var db = fixture.CreateDbContext();
        Assert.True(new FormTokenGuard(db).IsDuplicate(error));
        var owned = await db.CustodyLots.CountAsync(l => l.OwnerPartyId == w.CustomerId && l.OwnerPartyType == AccountingPartyType.Customer);
        Assert.Equal(1, owned);
    }

    [Fact]
    public async Task Location_Transfer_Keeps_Owner_And_Total_And_Moves_Tank_Quantity()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 100m);

        await using (var db = fixture.CreateDbContext())
        {
            await Writer(db).TransferLocationAsync(new CustodyLocationTransferInput
            {
                LotId = lotId,
                From = new CustodyLocation(w.TerminalId, w.TankId),
                To = new CustodyLocation(w.TerminalId, w.Tank2Id),
                MovementDate = Day2,
                QuantityMt = 35m
            });
        }

        await using var verify = fixture.CreateDbContext();
        var positions = await Reader(verify).GetPositionsAsync(lotId);
        Assert.Equal(65m, positions.Single(p => p.StorageTankId == w.TankId).QuantityMt);
        Assert.Equal(35m, positions.Single(p => p.StorageTankId == w.Tank2Id).QuantityMt);
        var lot = await LotAsync(lotId);
        Assert.Equal(100m, lot.BalanceMt);
        Assert.Equal(w.CustomerId, lot.OwnerPartyId);
    }

    [Fact]
    public async Task Ownership_Transfer_Moves_Quantity_Between_Owners_Without_Changing_Physical_Tank_Total()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 100m);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(100m, await TankPhysicalAsync(db, w.TankId));
        }

        int newLotId;
        await using (var db = fixture.CreateDbContext())
        {
            newLotId = await Writer(db).TransferOwnershipAsync(new CustodyOwnershipTransferInput
            {
                LotId = lotId,
                NewOwner = new CustodyOwner(AccountingPartyType.Customer, w.Customer2Id),
                Location = new CustodyLocation(w.TerminalId, w.TankId),
                MovementDate = Day2,
                QuantityMt = 15m
            });
        }

        Assert.Equal(85m, (await LotAsync(lotId)).BalanceMt);
        var target = await LotAsync(newLotId);
        Assert.Equal(15m, target.BalanceMt);
        Assert.Equal(w.Customer2Id, target.OwnerPartyId);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(100m, await TankPhysicalAsync(verify, w.TankId));
    }

    [Fact]
    public async Task Shared_Tank_Shows_Company_And_Each_Owner_Separately_And_Enforces_Capacity_And_Product()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.InventoryMovements.Add(new InventoryMovement
            {
                TerminalId = w.TerminalId, StorageTankId = w.SmallTankId, ProductId = w.ProductId,
                Direction = MovementDirection.In, MovementDate = Day1, QuantityMt = 50m
            });
            await db.SaveChangesAsync();
        }

        await ReceiveAsync(w, 40m, tankId: w.SmallTankId);
        await ReceiveAsync(w, 20m, tankId: w.SmallTankId, customerId: w.Customer2Id);

        var capacity = await Assert.ThrowsAsync<BusinessRuleException>(() => ReceiveAsync(w, 10.5m, tankId: w.SmallTankId));
        Assert.Equal("CUSTODY_TANK_CAPACITY", capacity.Code);
        var mixed = await Assert.ThrowsAsync<BusinessRuleException>(() => ReceiveAsync(w, 5m, tankId: w.SmallTankId, productId: w.OtherProductId));
        Assert.Equal("CUSTODY_TANK_PRODUCT", mixed.Code);

        await using var verify = fixture.CreateDbContext();
        var position = (await Reader(verify).GetPhysicalPositionsAsync()).Single(p => p.StorageTankId == w.SmallTankId);
        Assert.Equal(50m, position.CompanyMt);
        Assert.Equal(60m, position.CustodyMt);
        Assert.Equal(110m, position.TotalMt);
        Assert.Equal(3, position.Shares.Count);
        // موجودی ملکی همان ۵۰ تُن می‌ماند — تیل مردم وارد آن نمی‌شود.
        Assert.Equal(50m, await new StockService(verify).GetFreeQuantityMtAsync(w.ProductId, storageTankId: w.SmallTankId));
    }

    [Fact]
    public async Task Different_Product_Already_In_Tank_Blocks_Mixing()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await ReceiveAsync(w, 10m, tankId: w.Tank2Id, productId: w.OtherProductId);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => ReceiveAsync(w, 10m, tankId: w.Tank2Id));
        Assert.Equal("CUSTODY_TANK_MIXED", error.Code);
    }

    [Fact]
    public async Task Our_Stock_At_A_Third_Party_Facility_Is_Reported_Separately_And_Custody_Cannot_Be_Received_There()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.InventoryMovements.Add(new InventoryMovement
            {
                TerminalId = w.ThirdPartyTerminalId, ProductId = w.ProductId,
                Direction = MovementDirection.In, MovementDate = Day1, QuantityMt = 40m
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var reader = Reader(db);
            var atOthers = (await reader.GetCompanyStockAtThirdPartiesAsync()).Single(r => r.TerminalId == w.ThirdPartyTerminalId);
            Assert.Equal(40m, atOthers.QuantityMt);
            Assert.DoesNotContain(await reader.GetPhysicalPositionsAsync(), p => p.TerminalId == w.ThirdPartyTerminalId);
            // ملکِ شرکت است — در موجودی ملکی هست.
            Assert.Equal(40m, await new StockService(db).GetFreeQuantityMtAsync(w.ProductId, terminalId: w.ThirdPartyTerminalId));
        }

        await using var write = fixture.CreateDbContext();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Writer(write).ReceiveAsync(new CustodyReceiptInput
        {
            Owner = new CustodyOwner(AccountingPartyType.Customer, w.CustomerId),
            ProductId = w.ProductId,
            Location = new CustodyLocation(w.ThirdPartyTerminalId, null),
            ReceiptDate = Day1,
            QuantityMt = 5m
        }));
        Assert.Equal("CUSTODY_THIRD_PARTY_FACILITY", error.Code);
    }

    [Fact]
    public async Task Hold_Blocks_Release_Until_It_Is_Released()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 100m);
        int holdId;
        await using (var db = fixture.CreateDbContext())
        {
            holdId = await Writer(db).PlaceHoldAsync(lotId, 80m, "اختلاف کیفیت", null);
        }

        Assert.Equal(20m, (await LotAsync(lotId)).FreeMt);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => ReleaseAsync(w, lotId, 30m));
        Assert.Equal("CUSTODY_HELD", error.Code);

        await using (var db = fixture.CreateDbContext())
        {
            await Writer(db).ReleaseHoldAsync(holdId, "حل شد");
        }
        await ReleaseAsync(w, lotId, 30m);
        Assert.Equal(70m, (await LotAsync(lotId)).BalanceMt);
    }

    [Fact]
    public async Task Loss_Is_Recorded_With_Responsibility_And_No_Financial_Effect()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 100m);
        var before = await FinancialFootprintAsync();

        await using (var db = fixture.CreateDbContext())
        {
            await Writer(db).AdjustAsync(new CustodyAdjustmentInput
            {
                LotId = lotId,
                Kind = CustodyMovementKind.Loss,
                Location = new CustodyLocation(w.TerminalId, w.TankId),
                MovementDate = Day2,
                QuantityMt = 0.75m,
                Responsibility = CustodyLossResponsibility.Carrier
            });
        }

        Assert.Equal(99.25m, (await LotAsync(lotId)).BalanceMt);
        Assert.Equal(before, await FinancialFootprintAsync());
        await using var verify = fixture.CreateDbContext();
        var loss = await verify.CustodyMovements.SingleAsync(m => m.CustodyLotId == lotId && m.Kind == CustodyMovementKind.Loss);
        Assert.Equal(CustodyLossResponsibility.Carrier, loss.LossResponsibility);
    }

    [Fact]
    public async Task Reversal_Restores_Balance_Once_And_Cannot_Undo_A_Receipt_Already_Delivered()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var lotId = await ReceiveAsync(w, 100m);
        var releaseKey = await ReleaseAsync(w, lotId, 30m);

        await using (var db = fixture.CreateDbContext())
        {
            await Writer(db).ReverseOperationAsync(releaseKey, "مقدار اشتباه");
        }
        Assert.Equal(100m, (await LotAsync(lotId)).BalanceMt);

        await using (var db = fixture.CreateDbContext())
        {
            var again = await Assert.ThrowsAsync<BusinessRuleException>(() => Writer(db).ReverseOperationAsync(releaseKey, "دوباره"));
            Assert.Equal("CUSTODY_ALREADY_REVERSED", again.Code);
        }

        await ReleaseAsync(w, lotId, 60m);
        Guid receiptKey;
        await using (var db = fixture.CreateDbContext())
        {
            receiptKey = await db.CustodyMovements
                .Where(m => m.CustodyLotId == lotId && m.Kind == CustodyMovementKind.Receipt)
                .Select(m => m.OperationKey).SingleAsync();
            var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => Writer(db).ReverseOperationAsync(receiptKey, "اشتباه"));
            Assert.Equal("CUSTODY_REVERSAL_NEGATIVE", blocked.Code);
        }
        Assert.Equal(40m, (await LotAsync(lotId)).BalanceMt);
    }

    [Fact]
    public async Task In_Transit_Lot_Is_Received_Partially_And_Shows_Transit_Difference()
    {
        RequirePostgres();
        var w = await SeedAsync();
        int lotId;
        await using (var db = fixture.CreateDbContext())
        {
            lotId = await Writer(db).RegisterInTransitAsync(new CustodyInTransitInput
            {
                Owner = new CustodyOwner(AccountingPartyType.Customer, w.CustomerId),
                ProductId = w.ProductId,
                LoadedDate = Day1,
                LoadedQuantityMt = 60m,
                OriginDescription = "Turkmenistan"
            });
        }

        var transit = await LotAsync(lotId);
        Assert.Equal(CustodyLotStatus.InTransit, transit.Status);
        Assert.Equal(0m, transit.BalanceMt);
        var notReceived = await Assert.ThrowsAsync<BusinessRuleException>(() => ReleaseAsync(w, lotId, 1m));
        Assert.Equal("CUSTODY_LOT_NOT_ACTIVE", notReceived.Code);

        foreach (var qty in new[] { 30m, 29.6m })
        {
            await using var db = fixture.CreateDbContext();
            await Writer(db).ReceiveAsync(new CustodyReceiptInput
            {
                LotId = lotId,
                Location = new CustodyLocation(w.TerminalId, w.TankId),
                ReceiptDate = Day2,
                QuantityMt = qty
            });
        }

        var received = await LotAsync(lotId);
        Assert.Equal(CustodyLotStatus.Active, received.Status);
        Assert.Equal(59.6m, received.BalanceMt);
        Assert.Equal(0.4m, received.TransitDifferenceMt);
    }

    [Fact]
    public async Task Our_Own_Company_Cannot_Be_A_Custody_Owner()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Writer(db).ReceiveAsync(new CustodyReceiptInput
        {
            Owner = new CustodyOwner(AccountingPartyType.Company, w.SystemCompanyId),
            ProductId = w.ProductId,
            Location = new CustodyLocation(w.TerminalId, w.TankId),
            ReceiptDate = Day1,
            QuantityMt = 5m
        }));
        Assert.Equal("CUSTODY_OWNER_IS_COMPANY", error.Code);
    }

    private static async Task<decimal> TankPhysicalAsync(ApplicationDbContext db, int tankId)
        => (await Reader(db).GetPhysicalPositionsAsync())
            .Where(p => p.StorageTankId == tankId)
            .Sum(p => p.TotalMt);
}
