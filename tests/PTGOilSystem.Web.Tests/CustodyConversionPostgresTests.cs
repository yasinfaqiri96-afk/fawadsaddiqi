using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Custody;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.LoadingReceipts;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class CustodyConversionPostgresTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime Day = new(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);
    private static CustodyService Stock(ApplicationDbContext db) => new(db, new AuditService(db), new FormTokenGuard(db));
    private static AccountingOptions OptionsValue() => new() { Enabled = true, Pilots = new() { Purchase = true, InventoryReceipt = true } };

    private static SimplePurchaseWorkflowService Purchase(ApplicationDbContext db)
    {
        var options = Options.Create(OptionsValue());
        var accounting = new PurchaseAccountingAdapter(db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(), new PricingService(db), new InventoryValuationService(db), options,
            NullLogger<PurchaseAccountingAdapter>.Instance);
        var audit = new AuditService(db);
        var stock = new StockService(db);
        var movements = new InventoryMovementWriter(db, stock);
        return new(db, movements, accounting,
            new LoadingReceiptCancellationService(db, audit, NullLogger<LoadingReceiptCancellationService>.Instance,
                stock, accounting, movements: movements), audit, new LedgerPostingService(db));
    }

    private static CustodyConversionService Service(ApplicationDbContext db)
        => new(db, Stock(db), Purchase(db), new FormTokenGuard(db), new AuditService(db), Options.Create(OptionsValue()), new StockService(db));

    private async Task<(PaymentAccountingAdapterTests.PaymentScope Scope, int Lot)> SeedAsync(AccountingPartyType type = AccountingPartyType.Supplier)
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        scope.Tank.CapacityMt = 100m;
        await db.SaveChangesAsync();
        int owner;
        if (type == AccountingPartyType.Customer) owner = scope.Customer.Id;
        else if (type == AccountingPartyType.Partner)
        {
            var partner = new Partner { Code = "CP-" + Guid.NewGuid().ToString("N"), Name = "Actual person " + Guid.NewGuid() };
            db.Partners.Add(partner);
            await db.SaveChangesAsync();
            owner = partner.Id;
        }
        else owner = scope.Supplier.Id;
        var lot = await Stock(db).ReceiveAsync(new()
        {
            Owner = new(type, owner), ProductId = scope.Product.Id, Location = new(scope.Terminal.Id, scope.Tank.Id),
            ReceiptDate = Day, QuantityMt = 100m
        });
        return (scope, lot);
    }

    private static CustodyConversionInput Input(int lot, int tank, decimal qty = 40m, string? token = null)
        => new(lot, tank, Day, qty, 500m, "Signed purchase agreement CP-2026", token);
    private static Task<decimal> CustodyBalance(ApplicationDbContext db, int lot)
        => db.CustodyMovements.Where(x => x.CustodyLotId == lot)
            .SumAsync(x => x.Direction == CustodyDirection.In ? x.QuantityMt : -x.QuantityMt);

    [Theory]
    [InlineData(AccountingPartyType.Customer)]
    [InlineData(AccountingPartyType.Partner)]
    public async Task Concurrent_Identity_Confirmations_Are_Idempotent_And_Audited_Once(AccountingPartyType type)
    {
        var (scope, lot) = await SeedAsync(type);
        await using var db = fixture.CreateDbContext();
        var owner = new CustodyOwner(type, (await db.CustodyLots.SingleAsync(x => x.Id == lot)).OwnerPartyId);
        async Task Confirm()
        {
            await using var separate = fixture.CreateDbContext();
            await Service(separate).LinkSupplierIdentityAsync(owner, scope.Supplier.Id, "verified existing identity", true);
        }
        await Task.WhenAll(Confirm(), Confirm());
        var links = await db.CustodySupplierIdentityLinks.Where(x => type == AccountingPartyType.Customer
            ? x.CustomerId == owner.PartyId : x.PartnerId == owner.PartyId).ToListAsync();
        Assert.Single(links);
        Assert.Equal(scope.Supplier.Id, links[0].SupplierId);
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.EntityName == nameof(CustodySupplierIdentityLink) && x.EntityId == links[0].Id));
    }

    [Fact]
    public async Task Active_Supplier_Balance_Transfer_Blocks_Conversion_Cancellation_Without_Partial_Reversal()
    {
        var (scope, lot) = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var id = await Service(db).ConvertAsync(Input(lot, scope.Tank.Id));
        var conversion = await db.CustodyConversions.SingleAsync(x => x.Id == id);
        db.SupplierBalanceTransfers.Add(new SupplierBalanceTransfer
        {
            BatchId = Guid.NewGuid(), SupplierId = scope.Supplier.Id, CompanyId = scope.Company.Id,
            ContractId = conversion.ContractId, TransferDate = Day, TransferOriginalAmount = 100m,
            HistoricalAmountUsd = 100m, TransferValueUsd = 100m, TransferContractCurrencyAmount = 100m,
            Status = SupplierBalanceTransferStatus.Active
        });
        await db.SaveChangesAsync();
        var movements = await db.CustodyMovements.CountAsync(x => x.CustodyLotId == lot);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).CancelAsync(id, "unsafe transfer"));
        Assert.Equal("CUSTODY_CONVERSION_PAYMENT_DEPENDENCY", error.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(CustodyDocumentStatus.Posted, (await db.CustodyConversions.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal(60m, await CustodyBalance(db, lot));
        Assert.Equal(movements, await db.CustodyMovements.CountAsync(x => x.CustodyLotId == lot));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public async Task Partial_And_Full_Conversion_Keep_Physical_Stock_And_Post_Canonical_Cost_And_Debt(int qty)
    {
        var (s, lot) = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var id = await Service(db).ConvertAsync(Input(lot, s.Tank.Id, qty));
        var c = await db.CustodyConversions.Include(x => x.Contract).SingleAsync(x => x.Id == id);
        await using var auditDb = fixture.CreateDbContext();
        Assert.Equal(1, await auditDb.AuditLogs.CountAsync(x => x.EntityName == nameof(CustodyConversion) && x.EntityId == id));
        Assert.Equal(ContractStatus.Active, c.Contract!.Status);
        Assert.Equal(s.Supplier.Id, c.Contract.SupplierId);
        Assert.Equal(qty, c.Contract.QuantityMt);
        Assert.Equal(100m - qty, await CustodyBalance(db, lot));
        var owned = await new StockService(db).GetFreeQuantityMtAsync(s.Product.Id, terminalId: s.Terminal.Id, storageTankId: s.Tank.Id);
        Assert.Equal((decimal)qty, owned);
        Assert.Equal(100m, owned + await CustodyBalance(db, lot));
        var pool = await db.InventoryAverageCosts.SingleAsync(x => x.CompanyId == s.Company.Id && x.ProductId == s.Product.Id);
        Assert.Equal((decimal)qty, pool.QuantityMt);
        Assert.Equal(qty * 500m, pool.TotalValueUsd);
        var debt = await db.LedgerEntries.SingleAsync(x => x.ContractId == c.ContractId);
        Assert.Equal(LedgerSide.Credit, debt.Side);
        Assert.Equal(qty * 500m, debt.AmountUsd);
        Assert.Equal(2, await db.JournalEntries.CountAsync(x => x.CompanyId == s.Company.Id && x.SourceModule == "Purchase"));
        Assert.False(await db.CustodyFinanceEntries.AnyAsync(x => x.CustodyLotId == lot));
        Assert.False(await db.PaymentTransactions.AnyAsync(x => x.ContractId == c.ContractId));
    }

    [Fact]
    public async Task Cancellation_Restores_Owner_And_Reverses_Cost_Debt_And_GL_Without_Physical_Change()
    {
        var (s, lot) = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var service = Service(db);
        var id = await service.ConvertAsync(Input(lot, s.Tank.Id));
        var contractId = (await db.CustodyConversions.SingleAsync(x => x.Id == id)).ContractId;
        await service.CancelAsync(id, "Agreement cancelled");
        db.ChangeTracker.Clear();
        Assert.Equal(100m, await CustodyBalance(db, lot));
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(s.Product.Id, terminalId: s.Terminal.Id, storageTankId: s.Tank.Id));
        Assert.Equal(ContractStatus.Cancelled, (await db.Contracts.SingleAsync(x => x.Id == contractId)).Status);
        var pool = await db.InventoryAverageCosts.SingleAsync(x => x.CompanyId == s.Company.Id && x.ProductId == s.Product.Id);
        Assert.Equal(0m, pool.QuantityMt);
        Assert.Equal(0m, pool.TotalValueUsd);
        Assert.Equal(0m, await db.LedgerEntries.Where(x => x.ContractId == contractId).SumAsync(x => x.Side == LedgerSide.Credit ? x.AmountUsd : -x.AmountUsd));
        Assert.Equal(4, await db.JournalEntries.CountAsync(x => x.CompanyId == s.Company.Id && x.SourceModule == "Purchase"));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CancelAsync(id, "again"));
        Assert.Equal("CUSTODY_CONVERSION_ALREADY_CANCELLED", ex.Code);
    }

    [Fact]
    public async Task Standalone_Reversal_Of_Either_Ledger_Is_Blocked()
    {
        var (s, lot) = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var id = await Service(db).ConvertAsync(Input(lot, s.Tank.Id));
        var c = await db.CustodyConversions.SingleAsync(x => x.Id == id);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Stock(db).ReverseOperationAsync(c.StockOperationKey, "wrong"));
        Assert.Equal("CUSTODY_CONVERSION_REVERSAL", ex.Code);
        ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Purchase(db).ReverseAsync(c.ContractId, "wrong"));
        Assert.Equal("CUSTODY_CONVERSION_REVERSAL", ex.Code);
        Assert.Equal(60m, await CustodyBalance(db, lot));
    }

    [Fact]
    public async Task Consumed_Company_Stock_Prevents_Cancellation_And_Rolls_Back_Status()
    {
        var (s, lot) = await SeedAsync();
        int id;
        await using (var db = fixture.CreateDbContext())
        {
            id = await Service(db).ConvertAsync(Input(lot, s.Tank.Id));
            await using var tx = await db.Database.BeginTransactionAsync();
            await new InventoryMovementWriter(db, new StockService(db)).PostOutboundAsync(new()
            { ProductId = s.Product.Id, TerminalId = s.Terminal.Id, StorageTankId = s.Tank.Id,
                QuantityMt = 10m, MovementDate = Day.AddDays(1), ReferenceDocument = "Consumed" });
            await tx.CommitAsync();
        }
        await using (var db = fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).CancelAsync(id, "unsafe"));
            Assert.Equal("STOCK_INSUFFICIENT", ex.Code);
        }
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(CustodyDocumentStatus.Posted, (await verify.CustodyConversions.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal(60m, await CustodyBalance(verify, lot));
        Assert.False(await verify.CustodyMovements.AnyAsync(x => x.CustodyLotId == lot && x.ReversalOfCustodyMovementId != null));
    }

    [Theory]
    [InlineData(AccountingPartyType.Customer)]
    [InlineData(AccountingPartyType.Partner)]
    public async Task Explicit_Identity_Link_Uses_Existing_Supplier_Without_Creating_Person(AccountingPartyType type)
    {
        var (s, lot) = await SeedAsync(type);
        await using var db = fixture.CreateDbContext();
        var ownerId = (await db.CustodyLots.SingleAsync(x => x.Id == lot)).OwnerPartyId;
        var owner = new CustodyOwner(type, ownerId);
        var before = await db.Suppliers.CountAsync();
        var service = Service(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ConvertAsync(Input(lot, s.Tank.Id)));
        Assert.Equal("CUSTODY_SUPPLIER_IDENTITY_REQUIRED", ex.Code);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.LinkSupplierIdentityAsync(owner, s.Supplier.Id, "passport verified", false));
        await service.LinkSupplierIdentityAsync(owner, s.Supplier.Id, "passport verified", true);
        await service.LinkSupplierIdentityAsync(owner, s.Supplier.Id, "same evidence", true);
        Assert.Equal(1, await db.CustodySupplierIdentityLinks.CountAsync(x => x.SupplierId == s.Supplier.Id));
        var id = await service.ConvertAsync(Input(lot, s.Tank.Id));
        Assert.Equal(before, await db.Suppliers.CountAsync());
        Assert.Equal(s.Supplier.Id, (await db.CustodyConversions.Include(x => x.Contract).SingleAsync(x => x.Id == id)).Contract!.SupplierId);
    }

    [Fact]
    public async Task Held_Stock_And_Invalid_Price_Create_No_Purchase()
    {
        var (s, lot) = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        await Stock(db).PlaceHoldAsync(lot, 80m, "Blocked", null);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).ConvertAsync(Input(lot, s.Tank.Id)));
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).ConvertAsync(Input(lot, s.Tank.Id) with { UnitPriceUsd = 0m }));
        Assert.False(await db.Contracts.AnyAsync(x => x.ProductId == s.Product.Id && x.ContractNumber.StartsWith("CP-")));
        Assert.Equal(100m, await CustodyBalance(db, lot));
    }

    [Fact]
    public async Task Concurrent_Conversions_Cannot_Consume_The_Same_Quantity_Twice()
    {
        var (s, lot) = await SeedAsync();
        async Task<bool> Attempt()
        {
            await using var db = fixture.CreateDbContext();
            try { await Service(db).ConvertAsync(Input(lot, s.Tank.Id, 80m)); return true; }
            catch (BusinessRuleException ex) when (ex.Code == "CUSTODY_INSUFFICIENT") { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results.Where(x => x));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(20m, await CustodyBalance(verify, lot));
        Assert.Equal(1, await verify.CustodyConversions.CountAsync(x => x.CustodyLotId == lot));
    }

    [Fact]
    public async Task Repeated_FormToken_Rolls_Back_Second_Conversion()
    {
        var (s, lot) = await SeedAsync();
        var token = Guid.NewGuid().ToString();
        await using (var db = fixture.CreateDbContext())
            await Service(db).ConvertAsync(Input(lot, s.Tank.Id, 40m, token));
        await using (var db = fixture.CreateDbContext())
            await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).ConvertAsync(Input(lot, s.Tank.Id, 40m, token)));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(60m, await CustodyBalance(verify, lot));
        Assert.Equal(1, await verify.CustodyConversions.CountAsync(x => x.CustodyLotId == lot));
    }
    [Fact]
    public async Task Closed_Period_Rejects_Conversion_Without_Changing_Either_Ownership_Ledger()
    {
        var (s, lot) = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            await db.FiscalPeriods.Where(x => x.CompanyId == s.Company.Id && x.StartDate <= Day && x.EndDate >= Day)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, FiscalPeriodStatus.Closed));
            await Assert.ThrowsAsync<AccountingValidationException>(() => Service(db).ConvertAsync(Input(lot, s.Tank.Id)));
        }
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(100m, await CustodyBalance(verify, lot));
        Assert.False(await verify.Contracts.AnyAsync(x => x.ProductId == s.Product.Id && x.ContractNumber.StartsWith("CP-")));
        Assert.False(await verify.InventoryMovements.AnyAsync(x => x.ProductId == s.Product.Id));
    }

    [Fact]
    public async Task Unavailable_GL_Account_Rolls_Back_Custody_Contract_And_Company_Stock()
    {
        var (s, lot) = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var settings = await db.AccountingSettings.SingleAsync(x => x.CompanyId == s.Company.Id);
            var account = await db.Accounts.SingleAsync(x => x.Id == settings.InventoryAccountId);
            account.IsActive = false;
            await db.SaveChangesAsync();
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).ConvertAsync(Input(lot, s.Tank.Id)));
            Assert.Equal("CUSTODY_PURCHASE_GL_NOT_READY", ex.Code);
        }
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(100m, await CustodyBalance(verify, lot));
        Assert.False(await verify.Contracts.AnyAsync(x => x.ProductId == s.Product.Id && x.ContractNumber.StartsWith("CP-")));
        Assert.False(await verify.InventoryMovements.AnyAsync(x => x.ProductId == s.Product.Id));
        Assert.False(await verify.JournalEntries.AnyAsync(x => x.CompanyId == s.Company.Id));
    }

}
