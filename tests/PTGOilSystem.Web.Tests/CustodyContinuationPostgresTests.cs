using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Custody;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ادامهٔ امانی: فروش از چند بار، برگشت قسمی فروش، مصرف نسیهٔ خدمات‌دهنده با پرداخت قسمی،
/// حساب مالیِ شریکِ پیوندشده و تطبیق — همه روی PostgreSQL واقعی و ژورنال واقعی.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CustodyPostgresCollection.CollectionName)]
public sealed class CustodyContinuationPostgresTests(ConcurrencyVersionPostgresFixture fixture)
{
    private static readonly DateTime Day = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly SemaphoreSlim AccountingSetup = new(1, 1);

    private sealed record World(int CompanyId, int ProductId, int TerminalId, int TankA, int TankB,
        int OwnerId, int CustomerId, int CashAccountId, int LotA, int LotB, int PartnerId, int ProviderId);

    private void RequirePostgres()
        => Assert.True(fixture.Available, $"تست امانی به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

    private async Task<int> EnsureAccountingAsync()
    {
        await AccountingSetup.WaitAsync();
        try
        {
            await using var db = fixture.CreateDbContext();
            var company = await db.Companies.FirstOrDefaultAsync(c => c.IsSystemOwner);
            if (company is null)
            {
                company = new Company { Code = "OWN-" + Guid.NewGuid().ToString("N")[..6], Name = "Our company", Country = "AF", IsSystemOwner = true };
                db.Companies.Add(company);
                await db.SaveChangesAsync();
            }
            await new AccountingChartSeeder(db, Options.Create(new AccountingOptions { Enabled = true })).SeedAsync();
            if (!await db.FiscalYears.AnyAsync(y => y.CompanyId == company.Id))
            {
                var year = new FiscalYear
                {
                    CompanyId = company.Id, Name = "FY-2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31),
                    Status = FiscalYearStatus.Open, IsCurrent = true
                };
                db.FiscalYears.Add(year);
                await db.SaveChangesAsync();
                for (var month = 1; month <= 12; month++)
                {
                    var start = new DateTime(2026, month, 1);
                    db.FiscalPeriods.Add(new FiscalPeriod
                    {
                        CompanyId = company.Id, FiscalYearId = year.Id, PeriodNumber = month, Name = $"2026-{month:D2}",
                        StartDate = start, EndDate = start.AddMonths(1).AddDays(-1), Status = FiscalPeriodStatus.Open
                    });
                }
                await db.SaveChangesAsync();
            }
            return company.Id;
        }
        finally
        {
            AccountingSetup.Release();
        }
    }

    private async Task<World> SeedAsync(decimal lotA = 40m, decimal lotB = 50m, AccountingPartyType ownerType = AccountingPartyType.Supplier)
    {
        var companyId = await EnsureAccountingAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = fixture.CreateDbContext();
        var product = new Product { Code = $"CC-{tag}", Name = "Diesel" };
        var terminal = new Terminal { Code = $"CT-{tag}", Name = $"Kabul {tag}" };
        var owner = new Supplier { Name = $"Owner {tag}" };
        var partner = new Partner { Code = $"CP-{tag}", Name = $"Partner {tag}" };
        var customer = new Customer { Name = $"Buyer {tag}" };
        var provider = new ServiceProvider { Name = $"Carrier {tag}" };
        var cash = new CashAccount { Code = $"CC-{tag}", Name = $"Till {tag}", Currency = "USD", AccountType = CashAccountType.Cash, CompanyId = companyId };
        db.AddRange(product, terminal, owner, partner, customer, provider, cash);
        await db.SaveChangesAsync();
        var tankA = new StorageTank { TerminalId = terminal.Id, TankCode = $"A-{tag}", CapacityMt = 1_000m };
        var tankB = new StorageTank { TerminalId = terminal.Id, TankCode = $"B-{tag}", CapacityMt = 1_000m };
        db.AddRange(tankA, tankB);
        await db.SaveChangesAsync();
        var lotOwner = ownerType == AccountingPartyType.Partner
            ? new CustodyOwner(AccountingPartyType.Partner, partner.Id)
            : new CustodyOwner(AccountingPartyType.Supplier, owner.Id);
        var a = await Custody(db).ReceiveAsync(new CustodyReceiptInput
        {
            Owner = lotOwner, ProductId = product.Id, Location = new CustodyLocation(terminal.Id, tankA.Id),
            ReceiptDate = Day.AddDays(-5), QuantityMt = lotA
        });
        var b = await Custody(db).ReceiveAsync(new CustodyReceiptInput
        {
            Owner = lotOwner, ProductId = product.Id, Location = new CustodyLocation(terminal.Id, tankB.Id),
            ReceiptDate = Day.AddDays(-4), QuantityMt = lotB
        });
        return new World(companyId, product.Id, terminal.Id, tankA.Id, tankB.Id, owner.Id, customer.Id, cash.Id, a, b, partner.Id, provider.Id);
    }

    private static CustodyService Custody(ApplicationDbContext db) => new(db, new AuditService(db), new FormTokenGuard(db));

    private static CustodySaleService Sales(ApplicationDbContext db, bool accountingEnabled = true)
    {
        var options = Options.Create(new AccountingOptions { Enabled = accountingEnabled, Pilots = new AccountingPilotOptions { Custody = true } });
        var company = new SystemCompanyProvider(db);
        var posting = new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, company);
        var adapter = new CustodyAccountingAdapter(db, posting, company, options, NullLogger<CustodyAccountingAdapter>.Instance);
        return new CustodySaleService(db, Custody(db), adapter, new LedgerPostingService(db), new AuditService(db), new FormTokenGuard(db), company);
    }

    private static CustodyAccountingReconciliationService Reconciliation(ApplicationDbContext db)
    {
        var options = Options.Create(new AccountingOptions { Enabled = true, Pilots = new AccountingPilotOptions { Custody = true } });
        var company = new SystemCompanyProvider(db);
        var periods = new PeriodGuard(db, new FiscalCalendarService(db));
        var posting = new AccountingPostingService(db, periods, options, company);
        var adapter = new CustodyAccountingAdapter(db, posting, company, options, NullLogger<CustodyAccountingAdapter>.Instance);
        return new CustodyAccountingReconciliationService(db, adapter, company, periods, new AuditService(db));
    }

    private static CustodySaleInput SaleInput(World w, decimal fromA, decimal fromB, decimal price = 500m,
        CustodyCommissionRecognition recognition = CustodyCommissionRecognition.OnCollection,
        CustodyCommissionMethod method = CustodyCommissionMethod.PercentOfSale, decimal rate = 3m, string? token = null)
        => new()
        {
            LotId = w.LotA,
            Location = new CustodyLocation(w.TerminalId, w.TankA),
            QuantityMt = fromA,
            AdditionalSources = fromB > 0m ? [new CustodySaleSource(w.LotB, new CustodyLocation(w.TerminalId, w.TankB), fromB)] : [],
            CustomerId = w.CustomerId,
            SaleDate = Day,
            UnitPriceUsd = price,
            CommissionMethod = method,
            CommissionRate = rate,
            CommissionRecognition = recognition,
            FormToken = token
        };

    private async Task<int> SellAsync(World w, decimal fromA, decimal fromB, decimal price = 500m,
        CustodyCommissionRecognition recognition = CustodyCommissionRecognition.OnCollection,
        CustodyCommissionMethod method = CustodyCommissionMethod.PercentOfSale, decimal rate = 3m)
    {
        await using var db = fixture.CreateDbContext();
        return await Sales(db).CreateSaleAsync(SaleInput(w, fromA, fromB, price, recognition, method, rate));
    }

    private async Task<int> ReturnAsync(World w, int saleId, decimal fromA, decimal fromB = 0m, DateTime? date = null, bool accountingEnabled = true)
    {
        var lines = new List<CustodySaleSource>();
        if (fromA > 0m) lines.Add(new CustodySaleSource(w.LotA, new CustodyLocation(w.TerminalId, w.TankA), fromA));
        if (fromB > 0m) lines.Add(new CustodySaleSource(w.LotB, new CustodyLocation(w.TerminalId, w.TankB), fromB));
        await using var db = fixture.CreateDbContext();
        return await Sales(db, accountingEnabled).RecordReturnAsync(new CustodySaleReturnInput
        {
            SaleId = saleId, ReturnDate = date ?? Day.AddDays(2), Lines = lines
        });
    }

    private async Task<int> CollectAsync(World w, int saleId, decimal amount)
    {
        await using var db = fixture.CreateDbContext();
        return await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.Collection, SaleId = saleId, EntryDate = Day.AddDays(3), AmountUsd = amount, CashAccountId = w.CashAccountId
        });
    }

    private async Task<decimal> LotBalanceAsync(int lotId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.CustodyMovements.Where(m => m.CustodyLotId == lotId)
            .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt);
    }

    private async Task<decimal> OwnerFundsAsync(int supplierId)
    {
        await using var db = fixture.CreateDbContext();
        return await CustodySaleService.OwnerFundsBalanceAsync(db, new CustodyOwner(AccountingPartyType.Supplier, supplierId), CancellationToken.None);
    }

    /// <summary>ماندهٔ ژورنالیِ یک حساب (بدهکار − بستانکار) برای یک طرف یا صندوق، از همهٔ ماژول‌ها.</summary>
    private async Task<decimal> GlAsync(int companyId, string code, AccountingPartyType? partyType = null, int? partyId = null, int? cashAccountId = null)
    {
        await using var db = fixture.CreateDbContext();
        var account = await db.Accounts.SingleAsync(a => a.CompanyId == companyId && a.Code == code);
        var lines = db.JournalEntryLines.Where(l => l.AccountId == account.Id && l.JournalEntry!.Status == JournalEntryStatus.Posted);
        if (partyType.HasValue) lines = lines.Where(l => l.PartyType == partyType && l.PartyId == partyId);
        if (cashAccountId.HasValue) lines = lines.Where(l => l.CashAccountId == cashAccountId);
        return await lines.SumAsync(l => l.Debit - l.Credit);
    }

    private async Task AssertCustodyJournalsBalancedAsync()
    {
        await using var db = fixture.CreateDbContext();
        var unbalanced = await db.JournalEntries
            .Where(j => j.SourceModule == CustodyAccountingAdapter.SourceModule)
            .Select(j => new { j.JournalNumber, Debit = j.Lines.Sum(l => l.Debit), Credit = j.Lines.Sum(l => l.Credit) })
            .Where(x => x.Debit != x.Credit)
            .ToListAsync();
        Assert.Empty(unbalanced);
    }

    // ---------------------------------------------------------------- فروش از چند بار

    [Fact]
    public async Task Sale_From_Two_Lots_Takes_Each_Share_With_One_Document_One_Journal_And_One_Commission()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 20m, fromB: 25m);

        Assert.Equal(20m, await LotBalanceAsync(w.LotA));
        Assert.Equal(25m, await LotBalanceAsync(w.LotB));
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal(45m, sale.QuantityMt);
            Assert.Equal(22_500m, sale.AmountUsd);
            Assert.Equal(675m, sale.CommissionUsd);
            var shares = await db.CustodyMovements.Where(m => m.OperationKey == sale.StockOperationKey).ToListAsync();
            Assert.Equal(2, shares.Count);
            Assert.All(shares, m => Assert.Equal(CustodyMovementKind.Sale, m.Kind));
            Assert.Equal(20m, shares.Single(m => m.CustodyLotId == w.LotA).QuantityMt);
            Assert.Equal(25m, shares.Single(m => m.CustodyLotId == w.LotB).QuantityMt);
            Assert.Equal(1, await db.CustodySales.CountAsync(s => s.StockOperationKey == sale.StockOperationKey));
            Assert.Single(await db.JournalEntries.Where(j => j.SourceEventId == CustodyAccountingAdapter.SaleEventId(saleId)).ToListAsync());
            Assert.False(await db.InventoryMovements.AnyAsync(m => m.ProductId == w.ProductId));
        }
        Assert.Equal(22_500m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-22_500m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));

        // وصول کامل: کمیشن فقط یک بار و روی کلِ فروش.
        await CollectAsync(w, saleId, 22_500m);
        Assert.Equal(21_825m, await OwnerFundsAsync(w.OwnerId));

        await using (var audit = fixture.CreateDbContext())
            Assert.Equal(2, await audit.AuditLogs.CountAsync(a => a.EntityName == "CustodyMovement"
                && a.Action == "Insert" && audit.CustodyMovements.Any(m => m.Id == a.EntityId && m.Kind == CustodyMovementKind.Sale
                    && (m.CustodyLotId == w.LotA || m.CustodyLotId == w.LotB))));
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Cancelling_A_Two_Lot_Sale_Restores_Both_Lots_And_Reverses_The_Journal_Once()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 10m, fromB: 30m);
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelSaleAsync(saleId, "wrong customer");

        Assert.Equal(40m, await LotBalanceAsync(w.LotA));
        Assert.Equal(50m, await LotBalanceAsync(w.LotB));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        await using var verify = fixture.CreateDbContext();
        Assert.Single(await verify.JournalEntries.Where(j => j.SourceEventId == CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.SaleEventId(saleId))).ToListAsync());
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Multi_Lot_Sale_Rejects_Other_Owner_Other_Product_And_Held_Stock_Atomically()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var other = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var input = SaleInput(w, 10m, 0m);
            var mixed = new CustodySaleInput
            {
                LotId = input.LotId, Location = input.Location, QuantityMt = 10m, CustomerId = w.CustomerId, SaleDate = Day, UnitPriceUsd = 500m,
                CommissionMethod = CustodyCommissionMethod.None,
                AdditionalSources = [new CustodySaleSource(other.LotA, new CustodyLocation(other.TerminalId, other.TankA), 5m)]
            };
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CreateSaleAsync(mixed));
            Assert.Equal("CUSTODY_SALE_MIXED_OWNERS", ex.Code);
        }
        await using (var db = fixture.CreateDbContext())
        {
            await Custody(db).PlaceHoldAsync(w.LotB, 45m, "Dispute", null);
        }
        await using (var db = fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CreateSaleAsync(SaleInput(w, 10m, 6m)));
            Assert.Equal("CUSTODY_HELD", ex.Code);
        }
        Assert.Equal(40m, await LotBalanceAsync(w.LotA));
        Assert.Equal(50m, await LotBalanceAsync(w.LotB));
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.CustodySales.AnyAsync(s => s.CustodyLotId == w.LotA));
    }

    [Fact]
    public async Task Two_Concurrent_Multi_Lot_Sales_Cannot_Consume_The_Same_Lot_Twice()
    {
        RequirePostgres();
        var w = await SeedAsync(lotA: 40m, lotB: 50m);
        async Task<bool> Attempt()
        {
            await using var db = fixture.CreateDbContext();
            try { await Sales(db).CreateSaleAsync(SaleInput(w, 5m, 30m)); return true; }
            catch (BusinessRuleException ex) when (ex.Code is "CUSTODY_INSUFFICIENT" or "CUSTODY_HELD") { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results.Where(x => x));
        Assert.Equal(35m, await LotBalanceAsync(w.LotA));
        Assert.Equal(20m, await LotBalanceAsync(w.LotB));
    }

    [Fact]
    public async Task Same_Form_Token_Records_A_Multi_Lot_Sale_Once()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var token = Guid.NewGuid().ToString();
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CreateSaleAsync(SaleInput(w, 5m, 5m, token: token));
        await using (var db = fixture.CreateDbContext())
            await Assert.ThrowsAsync<DbUpdateException>(() => Sales(db).CreateSaleAsync(SaleInput(w, 5m, 5m, token: token)));
        Assert.Equal(35m, await LotBalanceAsync(w.LotA));
        Assert.Equal(45m, await LotBalanceAsync(w.LotB));
    }

    // ---------------------------------------------------------------- برگشت قسمی

    [Fact]
    public async Task Partial_Return_Restores_The_Original_Lot_Reduces_Receivable_And_Commission_Follows_Collections()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 30m, fromB: 0m);
        var returnId = await ReturnAsync(w, saleId, fromA: 10m);

        Assert.Equal(20m, await LotBalanceAsync(w.LotA));
        await using (var db = fixture.CreateDbContext())
        {
            var r = await db.CustodySaleReturns.SingleAsync(x => x.Id == returnId);
            Assert.Equal(5_000m, r.AmountUsd);
            Assert.Equal(150m, r.CommissionUsd);
            Assert.Equal(0m, r.RecognizedCommissionReversedUsd);
            var movement = await db.CustodyMovements.SingleAsync(m => m.OperationKey == r.StockOperationKey);
            Assert.Equal(CustodyMovementKind.SaleReturn, movement.Kind);
            Assert.Equal(CustodyDirection.In, movement.Direction);
            Assert.Equal(w.TankA, movement.StorageTankId);
        }
        Assert.Equal(10_000m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-10_000m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));

        var over = await Assert.ThrowsAsync<BusinessRuleException>(() => CollectAsync(w, saleId, 10_000.01m));
        Assert.Equal("CUSTODY_OVER_COLLECTION", over.Code);
        await CollectAsync(w, saleId, 10_000m);
        // کمیشنِ ۳٪ روی ۱۰٬۰۰۰ = ۳۰۰؛ پولِ مالک ۹٬۷۰۰.
        Assert.Equal(9_700m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-9_700m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));

        await using (var db = fixture.CreateDbContext())
        {
            var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelSaleAsync(saleId, "x"));
            Assert.Equal("CUSTODY_SALE_HAS_RECEIPTS", blocked.Code);
        }
        await using (var auditDb = fixture.CreateDbContext())
            Assert.Equal(1, await auditDb.AuditLogs.CountAsync(a => a.EntityName == "CustodySaleReturn" && a.EntityId == returnId));
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Return_Of_On_Sale_Commission_Reverses_Earned_Commission_And_Company_Profit()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var day = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc);
        int saleId;
        await using (var db = fixture.CreateDbContext())
        {
            var input = SaleInput(w, 30m, 0m, recognition: CustodyCommissionRecognition.OnSale);
            saleId = await Sales(db).CreateSaleAsync(new CustodySaleInput
            {
                LotId = input.LotId, Location = input.Location, QuantityMt = 30m, CustomerId = w.CustomerId, SaleDate = day,
                UnitPriceUsd = 500m, CommissionMethod = CustodyCommissionMethod.PercentOfSale, CommissionRate = 3m,
                CommissionRecognition = CustodyCommissionRecognition.OnSale
            });
        }
        Assert.Equal(-450m, await OwnerFundsAsync(w.OwnerId));
        await ReturnAsync(w, saleId, fromA: 10m, date: day);
        Assert.Equal(-300m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(300m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));

        await using (var db = fixture.CreateDbContext())
        {
            var period = await new ProfitAndLossService(db).BuildCompanyPeriodAsync(
                new ManagementReportFilterViewModel { FromDate = day, ToDate = day }, day);
            Assert.Equal(300m, period.CustodyIncomeUsd);
            Assert.Equal(0m, period.SalesRevenueUsd);
            var ownerLedger = await db.LedgerEntries.Where(l => l.SupplierId == w.OwnerId).ToListAsync();
            Assert.Equal(-300m, ownerLedger.Sum(r => r.Side == LedgerSide.Credit ? r.AmountUsd : -r.AmountUsd));
        }
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Return_Beyond_Uncollected_Money_Wrong_Source_Or_Excess_Quantity_Is_Rejected_Without_Effect()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 30m, fromB: 0m);
        await CollectAsync(w, saleId, 12_000m);

        var collected = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnAsync(w, saleId, fromA: 10m));
        Assert.Equal("CUSTODY_RETURN_COLLECTED", collected.Code);
        var source = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnAsync(w, saleId, fromA: 0m, fromB: 1m));
        Assert.Equal("CUSTODY_RETURN_SOURCE", source.Code);
        var excess = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnAsync(w, saleId, fromA: 31m));
        Assert.Equal("CUSTODY_RETURN_EXCEEDS", excess.Code);

        Assert.Equal(10m, await LotBalanceAsync(w.LotA));
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.CustodySaleReturns.AnyAsync(r => r.CustodySaleId == saleId));
        // ۶ تُن (۳٬۰۰۰ دالر) هنوز از مبلغ وصول‌نشده است و مجاز است.
        await ReturnAsync(w, saleId, fromA: 6m);
        Assert.Equal(16m, await LotBalanceAsync(w.LotA));
    }

    [Fact]
    public async Task Multi_Lot_Return_Then_Full_Return_Clears_Commission_And_Cancellation_Is_Last_First()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 10m, fromB: 20m, price: 333.3333m);
        var first = await ReturnAsync(w, saleId, fromA: 4m, fromB: 5m);
        var second = await ReturnAsync(w, saleId, fromA: 6m, fromB: 15m);
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            var returns = await db.CustodySaleReturns.Where(r => r.CustodySaleId == saleId).ToListAsync();
            // آخرین برگشتِ کامل، باقیماندهٔ دقیق مبلغ و کمیشن را می‌گیرد.
            Assert.Equal(sale.AmountUsd, returns.Sum(r => r.AmountUsd));
            Assert.Equal(sale.CommissionUsd, returns.Sum(r => r.CommissionUsd));
        }
        Assert.Equal(40m, await LotBalanceAsync(w.LotA));
        Assert.Equal(50m, await LotBalanceAsync(w.LotB));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));

        await using (var db = fixture.CreateDbContext())
        {
            var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelSaleAsync(saleId, "x"));
            Assert.Equal("CUSTODY_SALE_HAS_RETURNS", blocked.Code);
            var order = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelReturnAsync(first, "x"));
            Assert.Equal("CUSTODY_RETURN_NOT_LATEST", order.Code);
        }
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelReturnAsync(second, "count error");
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelReturnAsync(first, "count error");
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelSaleAsync(saleId, "deal cancelled");

        Assert.Equal(40m, await LotBalanceAsync(w.LotA));
        Assert.Equal(50m, await LotBalanceAsync(w.LotB));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(2, await verify.JournalEntries.CountAsync(j => j.SourceModule == CustodyAccountingAdapter.SourceModule
            && (j.SourceEventId == CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.ReturnEventId(first))
                || j.SourceEventId == CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.ReturnEventId(second)))));
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Cancelling_A_Return_Whose_Stock_Was_Resold_Is_Blocked()
    {
        RequirePostgres();
        var w = await SeedAsync(lotA: 30m, lotB: 10m);
        var saleId = await SellAsync(w, fromA: 30m, fromB: 0m);
        var returnId = await ReturnAsync(w, saleId, fromA: 10m);
        await using (var db = fixture.CreateDbContext())
        {
            var input = SaleInput(w, 10m, 0m);
            await Sales(db).CreateSaleAsync(new CustodySaleInput
            {
                LotId = input.LotId, Location = input.Location, QuantityMt = 10m, CustomerId = w.CustomerId,
                SaleDate = Day.AddDays(3), UnitPriceUsd = 500m, CommissionMethod = CustodyCommissionMethod.None
            });
        }
        await using (var db = fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelReturnAsync(returnId, "x"));
            Assert.Equal("CUSTODY_REVERSAL_NEGATIVE", ex.Code);
        }
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(CustodyDocumentStatus.Posted, (await verify.CustodySaleReturns.SingleAsync(r => r.Id == returnId)).Status);
        Assert.Equal(0m, await LotBalanceAsync(w.LotA));
    }

    [Fact]
    public async Task Return_Movement_Cannot_Be_Reversed_Alone_From_The_Lot_Page()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 10m, fromB: 0m);
        var returnId = await ReturnAsync(w, saleId, fromA: 5m);
        await using var db = fixture.CreateDbContext();
        var key = (await db.CustodySaleReturns.SingleAsync(r => r.Id == returnId)).StockOperationKey;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Custody(db).ReverseOperationAsync(key, "x"));
        Assert.Equal("CUSTODY_SALE_REVERSAL", ex.Code);
    }

    // ---------------------------------------------------------------- مصرف نسیهٔ خدمات‌دهنده

    private async Task<int> ExpenseTypeAsync(ExpensePayableKind? kind)
    {
        await using var db = fixture.CreateDbContext();
        var type = new ExpenseType { Code = "CX-" + Guid.NewGuid().ToString("N")[..10], Name = "Trucking", PayableAccountKind = kind };
        db.ExpenseTypes.Add(type);
        await db.SaveChangesAsync();
        return type.Id;
    }

    [Fact]
    public async Task Unpaid_Provider_Charge_Debits_Owner_Credits_Provider_And_Is_Paid_In_Parts()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 30m, fromB: 0m);
        await CollectAsync(w, saleId, 15_000m);
        var typeId = await ExpenseTypeAsync(ExpensePayableKind.FreightPayable);
        var freightBefore = await GlAsync(w.CompanyId, "2300", AccountingPartyType.ServiceProvider, w.ProviderId);
        int chargeId;
        await using (var db = fixture.CreateDbContext())
        {
            chargeId = await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
            {
                Kind = CustodyFinanceKind.ProviderCharge, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
                EntryDate = Day.AddDays(4), AmountUsd = 400m, ServiceProviderId = w.ProviderId, ExpenseTypeId = typeId
            });
        }
        Assert.Equal(14_150m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(-400m, await GlAsync(w.CompanyId, "2300", AccountingPartyType.ServiceProvider, w.ProviderId) - freightBefore);
        Assert.Equal(-14_150m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(15_000m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));

        async Task<int> PayAsync(decimal amount)
        {
            await using var db = fixture.CreateDbContext();
            return await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
            {
                Kind = CustodyFinanceKind.ProviderPayment, ProviderChargeId = chargeId, EntryDate = Day.AddDays(5),
                AmountUsd = amount, CashAccountId = w.CashAccountId
            });
        }
        var payment = await PayAsync(150m);
        var over = await Assert.ThrowsAsync<BusinessRuleException>(() => PayAsync(250.01m));
        Assert.Equal("CUSTODY_PROVIDER_OVERPAID", over.Code);
        Assert.Equal(14_150m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(-250m, await GlAsync(w.CompanyId, "2300", AccountingPartyType.ServiceProvider, w.ProviderId) - freightBefore);
        Assert.Equal(14_850m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        await using (var db = fixture.CreateDbContext())
        {
            var providerLedger = await db.LedgerEntries.Where(l => l.ServiceProviderId == w.ProviderId).ToListAsync();
            Assert.Equal(250m, providerLedger.Sum(r => r.Side == LedgerSide.Credit ? r.AmountUsd : -r.AmountUsd));
            Assert.False(await db.ExpenseTransactions.AnyAsync(e => e.ServiceProviderId == w.ProviderId));
            var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelFinanceAsync(chargeId, "x"));
            Assert.Equal("CUSTODY_PROVIDER_CHARGE_PAID", blocked.Code);
        }
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelFinanceAsync(payment, "wrong amount");
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelFinanceAsync(chargeId, "duplicate bill");

        Assert.Equal(14_550m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "2300", AccountingPartyType.ServiceProvider, w.ProviderId) - freightBefore);
        Assert.Equal(15_000m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        await using (var db = fixture.CreateDbContext())
        {
            var providerLedger = await db.LedgerEntries.Where(l => l.ServiceProviderId == w.ProviderId).ToListAsync();
            Assert.Equal(0m, providerLedger.Sum(r => r.Side == LedgerSide.Credit ? r.AmountUsd : -r.AmountUsd));
            Assert.Equal(-await OwnerFundsAsync(w.OwnerId), await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        }
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Provider_Charge_Without_A_Configured_Payable_Account_Is_Rejected_And_Writes_Nothing()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var typeId = await ExpenseTypeAsync(null);
        await using var db = fixture.CreateDbContext();
        var entries = await db.CustodyFinanceEntries.CountAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).RecordFinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.ProviderCharge, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
            EntryDate = Day, AmountUsd = 100m, ServiceProviderId = w.ProviderId, ExpenseTypeId = typeId
        }));
        Assert.Equal("CUSTODY_PROVIDER_PAYABLE", ex.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(entries, await db.CustodyFinanceEntries.CountAsync());
        Assert.False(await db.LedgerEntries.AnyAsync(l => l.ServiceProviderId == w.ProviderId));
    }

    [Fact]
    public async Task Concurrent_Payments_Cannot_Overpay_One_Provider_Charge()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var typeId = await ExpenseTypeAsync(ExpensePayableKind.AccountsPayable);
        int chargeId;
        await using (var db = fixture.CreateDbContext())
        {
            chargeId = await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
            {
                Kind = CustodyFinanceKind.ProviderCharge, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
                EntryDate = Day, AmountUsd = 300m, ServiceProviderId = w.ProviderId, ExpenseTypeId = typeId
            });
        }
        async Task<bool> Attempt()
        {
            await using var db = fixture.CreateDbContext();
            try
            {
                await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
                {
                    Kind = CustodyFinanceKind.ProviderPayment, ProviderChargeId = chargeId, EntryDate = Day,
                    AmountUsd = 200m, CashAccountId = w.CashAccountId
                });
                return true;
            }
            catch (BusinessRuleException ex) when (ex.Code == "CUSTODY_PROVIDER_OVERPAID") { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results.Where(x => x));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(200m, await CustodySaleService.ProviderPaidAsync(verify, chargeId, CancellationToken.None));
    }

    // ---------------------------------------------------------------- هویت مالیِ شریکِ پیوندشده

    [Fact]
    public async Task Partner_Owned_Lot_Is_Sold_Through_Its_Linked_Supplier_Account_Only_After_Explicit_Link()
    {
        RequirePostgres();
        var w = await SeedAsync(ownerType: AccountingPartyType.Partner);
        await using (var db = fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CreateSaleAsync(SaleInput(w, 10m, 0m)));
            Assert.Equal("CUSTODY_OWNER_FINANCE_UNSUPPORTED", ex.Code);
            db.CustodySupplierIdentityLinks.Add(new CustodySupplierIdentityLink { PartnerId = w.PartnerId, SupplierId = w.OwnerId, Evidence = "Tazkira verified" });
            await db.SaveChangesAsync();
        }
        var suppliersBefore = 0;
        await using (var db = fixture.CreateDbContext())
            suppliersBefore = await db.Suppliers.CountAsync();
        var saleId = await SellAsync(w, fromA: 10m, fromB: 5m);
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal(AccountingPartyType.Supplier, sale.OwnerPartyType);
            Assert.Equal(w.OwnerId, sale.OwnerPartyId);
            Assert.Equal(suppliersBefore, await db.Suppliers.CountAsync());
            var lot = await db.CustodyLots.SingleAsync(l => l.Id == w.LotA);
            Assert.Equal(AccountingPartyType.Partner, lot.OwnerPartyType);
            Assert.False(await db.LedgerEntries.AnyAsync(l => l.PartnerId == w.PartnerId));
        }
        Assert.Equal(-7_500m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        await CollectAsync(w, saleId, 7_500m);
        Assert.Equal(7_275m, await OwnerFundsAsync(w.OwnerId));
    }

    // ---------------------------------------------------------------- تطبیق و راپور

    [Fact]
    public async Task Returns_And_Provider_Charges_Posted_While_GL_Was_Off_Are_Reconciled_Once_And_Report_Balances()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var typeId = await ExpenseTypeAsync(ExpensePayableKind.FreightPayable);
        int saleId;
        await using (var db = fixture.CreateDbContext())
            saleId = await Sales(db, accountingEnabled: false).CreateSaleAsync(SaleInput(w, 20m, 0m));
        var returnId = await ReturnAsync(w, saleId, fromA: 4m, accountingEnabled: false);
        await using (var db = fixture.CreateDbContext())
        {
            await Sales(db, accountingEnabled: false).RecordFinanceAsync(new CustodyFinanceInput
            {
                Kind = CustodyFinanceKind.ProviderCharge, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
                EntryDate = Day.AddDays(1), AmountUsd = 90m, ServiceProviderId = w.ProviderId, ExpenseTypeId = typeId
            });
        }
        var owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await CustodyAccountingReconciliationService.HasMissingJournalsAsync(db, owner, CancellationToken.None));
            var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnAsync(w, saleId, fromA: 1m));
            Assert.Equal("CUSTODY_RECONCILIATION_REQUIRED", blocked.Code);
        }
        await using (var db = fixture.CreateDbContext())
        {
            var preview = await Reconciliation(db).PreviewAsync(owner);
            Assert.Contains(preview.Rows, r => r.EventId == CustodyAccountingAdapter.ReturnEventId(returnId) && !r.AlreadyPosted);
            Assert.Equal(3, preview.MissingCount);
            Assert.Equal(3, await Reconciliation(db).ApplyAsync(owner, preview.Fingerprint, true, "GL enabled"));
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await CustodyAccountingReconciliationService.HasMissingJournalsAsync(db, owner, CancellationToken.None));
            var report = await new CustodyGlReadService(db, new SystemCompanyProvider(db), new PartyDirectory(db),
                Options.Create(new AccountingOptions { Enabled = true })).ReadAsync(Day.AddDays(30));
            var row = Assert.Single(report.Owners.Where(x => x.Owner == owner));
            Assert.Equal(0m, row.DifferenceUsd);
            Assert.Equal(90m, row.OwnerReceivableUsd);
            Assert.Equal(8_000m, row.PendingNetCreditUsd);
        }
        Assert.Equal(8_000m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        await using (var db = fixture.CreateDbContext())
        {
            var report = await new CustodyGlReadService(db, new SystemCompanyProvider(db), new PartyDirectory(db),
                Options.Create(new AccountingOptions { Enabled = true })).ReadAsync(Day.AddDays(30));
            var open = Assert.Single(report.ProviderCharges.Where(x => x.ServiceProviderId == w.ProviderId));
            Assert.Equal(90m, open.UnpaidUsd);

            // بیلانس مدیریتی: تیل امانی در ارزش موجودی شرکت نیست و تفکیک امانی شفاف گفته می‌شود.
            var balance = await new CompanyBalanceReportService(db, new ProfitAndLossService(db), new StockService(db), new PricingService(db))
                .BuildAsync(new CompanyBalanceReportRequest(Day.AddDays(-30), Day.AddDays(30), "USD", Day.AddDays(30)), []);
            Assert.DoesNotContain(balance.DetailsFor(CompanyBalanceSection.InventoryValue), r => r.QuantityMt is > 0m && r.Name.Contains("Diesel"));
            Assert.Contains(balance.NotesFa, note => note.Contains("وجوه امانی"));
        }
        await AssertCustodyJournalsBalancedAsync();
    }

    // ---------------------------------------------------------------- لیتر در سایر عملیات

    private static CustodyMeasurement Liters(decimal liters, decimal density = 800m, decimal temp = 15m, decimal densityTemp = 15m)
        => new(liters, density, temp, densityTemp, "Lab certificate LC-77");

    [Fact]
    public async Task Delivery_Transfer_Ownership_And_Loss_Accept_Certified_Liters_And_Keep_The_Source_On_Reversal()
    {
        RequirePostgres();
        var w = await SeedAsync(lotA: 40m, lotB: 50m);
        Guid deliveryKey;
        await using (var db = fixture.CreateDbContext())
        {
            deliveryKey = await Custody(db).ReleaseAsync(new CustodyReleaseInput
            {
                LotId = w.LotA, Kind = CustodyMovementKind.DeliveryToThirdParty, Location = new CustodyLocation(w.TerminalId, w.TankA),
                MovementDate = Day, Measurement = Liters(5_000m), CounterpartyName = "Truck owner"
            });
            await Custody(db).TransferLocationAsync(new CustodyLocationTransferInput
            {
                LotId = w.LotA, From = new CustodyLocation(w.TerminalId, w.TankA), To = new CustodyLocation(w.TerminalId, w.TankB),
                MovementDate = Day, Measurement = Liters(10_000m, 820m)
            });
            await Custody(db).TransferOwnershipAsync(new CustodyOwnershipTransferInput
            {
                LotId = w.LotB, NewOwner = new CustodyOwner(AccountingPartyType.Partner, w.PartnerId),
                Location = new CustodyLocation(w.TerminalId, w.TankB), MovementDate = Day, Measurement = Liters(2_500m)
            });
            await Custody(db).AdjustAsync(new CustodyAdjustmentInput
            {
                LotId = w.LotB, Kind = CustodyMovementKind.Loss, Location = new CustodyLocation(w.TerminalId, w.TankB),
                MovementDate = Day, Measurement = Liters(125m)
            });
        }
        // ۵٬۰۰۰ لیتر × ۸۰۰ = ۴ تُن؛ ۱۰٬۰۰۰ × ۸۲۰ = ۸.۲ تُن (انتقال محل، ماندهٔ بار را تغییر نمی‌دهد)؛
        // ۲٬۵۰۰ × ۸۰۰ = ۲ تُن؛ ۱۲۵ × ۸۰۰ = ۰.۱ تُن.
        Assert.Equal(40m - 4m, await LotBalanceAsync(w.LotA));
        await using (var db = fixture.CreateDbContext())
            Assert.Equal(8.2m, await db.CustodyMovements.Where(m => m.CustodyLotId == w.LotA && m.StorageTankId == w.TankB)
                .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt));
        Assert.Equal(50m - 2m - 0.1m, await LotBalanceAsync(w.LotB));
        await using (var db = fixture.CreateDbContext())
        {
            var measured = await db.CustodyMovements.Where(m => (m.CustodyLotId == w.LotA || m.CustodyLotId == w.LotB)
                && m.Kind != CustodyMovementKind.Receipt).ToListAsync();
            Assert.All(measured, m => Assert.NotNull(m.SourceLiters));
            Assert.Equal(2, measured.Count(m => m.Kind == CustodyMovementKind.LocationTransfer && m.SourceLiters == 10_000m && m.QuantityMt == 8.2m));
            await Custody(db).ReverseOperationAsync(deliveryKey, "wrong truck");
        }
        await using var verify = fixture.CreateDbContext();
        var reversal = await verify.CustodyMovements.SingleAsync(m => m.ReversalOfCustodyMovement!.OperationKey == deliveryKey);
        Assert.Equal(5_000m, reversal.SourceLiters);
        Assert.Equal(800m, reversal.DensityKgPerM3);
        Assert.Equal(4m, reversal.QuantityMt);
        Assert.Equal(40m, await LotBalanceAsync(w.LotA));
    }

    [Fact]
    public async Task Sale_And_In_Transit_Loading_In_Liters_Use_The_Same_Rule_And_Reject_Guessed_Values()
    {
        RequirePostgres();
        var w = await SeedAsync();
        int saleId;
        await using (var db = fixture.CreateDbContext())
        {
            var input = SaleInput(w, 0m, 0m);
            saleId = await Sales(db).CreateSaleAsync(new CustodySaleInput
            {
                LotId = input.LotId, Location = input.Location, Measurement = Liters(12_500m), CustomerId = w.CustomerId,
                SaleDate = Day, UnitPriceUsd = 500m, CommissionMethod = CustodyCommissionMethod.PercentOfSale, CommissionRate = 3m
            });
        }
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal(10m, sale.QuantityMt);
            Assert.Equal(5_000m, sale.AmountUsd);
            var movement = await db.CustodyMovements.SingleAsync(m => m.OperationKey == sale.StockOperationKey);
            Assert.Equal(12_500m, movement.SourceLiters);

            var temperature = await Assert.ThrowsAsync<BusinessRuleException>(() => Custody(db).ReleaseAsync(new CustodyReleaseInput
            {
                LotId = w.LotA, Kind = CustodyMovementKind.ReturnToOwner, Location = new CustodyLocation(w.TerminalId, w.TankA),
                MovementDate = Day, Measurement = Liters(1_000m, temp: 20m, densityTemp: 15m)
            }));
            Assert.Equal("CUSTODY_MEASUREMENT_TEMPERATURE", temperature.Code);
            var mismatch = await Assert.ThrowsAsync<BusinessRuleException>(() => Custody(db).ReleaseAsync(new CustodyReleaseInput
            {
                LotId = w.LotA, Kind = CustodyMovementKind.ReturnToOwner, Location = new CustodyLocation(w.TerminalId, w.TankA),
                MovementDate = Day, QuantityMt = 1m, Measurement = Liters(1_000m)
            }));
            Assert.Equal("CUSTODY_MEASUREMENT_MISMATCH", mismatch.Code);
        }
        Assert.Equal(30m, await LotBalanceAsync(w.LotA));

        int transitLot;
        await using (var db = fixture.CreateDbContext())
        {
            transitLot = await Custody(db).RegisterInTransitAsync(new CustodyInTransitInput
            {
                Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId), ProductId = w.ProductId,
                LoadedDate = Day, LoadedMeasurement = Liters(30_000m, 835m)
            });
        }
        await using (var db = fixture.CreateDbContext())
        {
            var lot = await db.CustodyLots.SingleAsync(l => l.Id == transitLot);
            Assert.Equal(25.05m, lot.LoadedQuantityMt);
            Assert.Equal(30_000m, lot.LoadedSourceLiters);
            Assert.Equal(835m, lot.LoadedDensityKgPerM3);
            Assert.Equal(CustodyLotStatus.InTransit, lot.Status);
            Assert.Equal(0m, await LotBalanceAsync(transitLot));
            var guessed = await Assert.ThrowsAsync<BusinessRuleException>(() => Custody(db).RegisterInTransitAsync(new CustodyInTransitInput
            {
                Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId), ProductId = w.ProductId,
                LoadedDate = Day, LoadedMeasurement = new CustodyMeasurement(30_000m, 835m, 15m, 15m, " ")
            }));
            Assert.Equal("CUSTODY_MEASUREMENT_REFERENCE", guessed.Code);
        }
    }

    [Fact]
    public async Task Reconciliation_Preview_Blocks_A_Provider_Charge_Whose_Expense_Type_Has_No_Payable_Account()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var typeId = await ExpenseTypeAsync(null);
        await using (var db = fixture.CreateDbContext())
        {
            await Sales(db, accountingEnabled: false).RecordFinanceAsync(new CustodyFinanceInput
            {
                Kind = CustodyFinanceKind.ProviderCharge, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
                EntryDate = Day, AmountUsd = 50m, ServiceProviderId = w.ProviderId, ExpenseTypeId = typeId
            });
        }
        await using var check = fixture.CreateDbContext();
        var preview = await Reconciliation(check).PreviewAsync(new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId));
        Assert.NotNull(preview.Blocker);
        Assert.False(preview.CanApply);
        var journals = await check.JournalEntries.CountAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Reconciliation(check).ApplyAsync(
            new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId), preview.Fingerprint, true, "try"));
        Assert.Equal(journals, await check.JournalEntries.CountAsync());
    }

    // ---------------------------------------------------------------- مرحلهٔ ۳: لیتر در مسدودی و برگشت، مالکِ شرکت

    [Fact]
    public async Task Hold_And_Single_Source_Return_Accept_Certified_Liters()
    {
        RequirePostgres();
        var w = await SeedAsync(lotA: 40m, lotB: 10m);
        int holdId;
        await using (var db = fixture.CreateDbContext())
            holdId = await Custody(db).PlaceHoldAsync(w.LotA, 0m, Liters(6_250m), "Quality dispute", null);
        await using (var db = fixture.CreateDbContext())
        {
            var hold = await db.CustodyHolds.SingleAsync(h => h.Id == holdId);
            Assert.Equal(5m, hold.QuantityMt);
            Assert.Equal(6_250m, hold.SourceLiters);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CreateSaleAsync(SaleInput(w, 35.0001m, 0m)));
            Assert.Equal("CUSTODY_HELD", ex.Code);
        }
        var saleId = await SellAsync(w, fromA: 20m, fromB: 0m);
        int returnId;
        await using (var db = fixture.CreateDbContext())
        {
            returnId = await Sales(db).RecordReturnAsync(new CustodySaleReturnInput
            {
                SaleId = saleId, ReturnDate = Day.AddDays(1),
                Lines = [new CustodySaleSource(w.LotA, new CustodyLocation(w.TerminalId, w.TankA), 0m, Liters(3_750m))]
            });
        }
        await using (var db = fixture.CreateDbContext())
        {
            var r = await db.CustodySaleReturns.SingleAsync(x => x.Id == returnId);
            Assert.Equal(3m, r.QuantityMt);
            Assert.Equal(1_500m, r.AmountUsd);
            var movement = await db.CustodyMovements.SingleAsync(m => m.OperationKey == r.StockOperationKey);
            Assert.Equal(3_750m, movement.SourceLiters);
        }
        Assert.Equal(23m, await LotBalanceAsync(w.LotA));
    }

    [Fact]
    public async Task Other_Company_Owner_Uses_Its_Linked_Supplier_Role_Without_A_New_Identity()
    {
        RequirePostgres();
        var w = await SeedAsync(lotA: 1m, lotB: 1m);
        int companyId, lotId;
        await using (var db = fixture.CreateDbContext())
        {
            var company = new Company { Code = "OC-" + Guid.NewGuid().ToString("N")[..6], Name = "Other trading co", Country = "AF" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            companyId = company.Id;
            lotId = await Custody(db).ReceiveAsync(new CustodyReceiptInput
            {
                Owner = new CustodyOwner(AccountingPartyType.Company, companyId), ProductId = w.ProductId,
                Location = new CustodyLocation(w.TerminalId, w.TankA), ReceiptDate = Day.AddDays(-3), QuantityMt = 30m
            });
        }
        var input = new CustodySaleInput
        {
            LotId = lotId, Location = new CustodyLocation(w.TerminalId, w.TankA), QuantityMt = 10m, CustomerId = w.CustomerId,
            SaleDate = Day, UnitPriceUsd = 400m, CommissionMethod = CustodyCommissionMethod.None
        };
        await using (var db = fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CreateSaleAsync(input));
            Assert.Equal("CUSTODY_OWNER_FINANCE_UNSUPPORTED", ex.Code);
        }
        var options = Options.Create(new AccountingOptions { Enabled = false });
        await using (var db = fixture.CreateDbContext())
        {
            var conversions = new CustodyConversionService(db, Custody(db), null!, new FormTokenGuard(db), new AuditService(db), options, new StockService(db));
            await conversions.LinkSupplierIdentityAsync(new CustodyOwner(AccountingPartyType.Company, companyId), w.OwnerId, "Registration certificate", true);
            await conversions.LinkSupplierIdentityAsync(new CustodyOwner(AccountingPartyType.Company, companyId), w.OwnerId, "again", true);
            var conflict = await Assert.ThrowsAsync<BusinessRuleException>(() => conversions.LinkSupplierIdentityAsync(
                new CustodyOwner(AccountingPartyType.Company, companyId), w.OwnerId + 1_000_000, "other", true));
            Assert.Equal("CUSTODY_IDENTITY_NOT_FOUND", conflict.Code);
        }
        int saleId;
        await using (var db = fixture.CreateDbContext())
            saleId = await Sales(db).CreateSaleAsync(input);
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal(AccountingPartyType.Supplier, sale.OwnerPartyType);
            Assert.Equal(w.OwnerId, sale.OwnerPartyId);
            Assert.Equal(1, await db.CustodySupplierIdentityLinks.CountAsync(x => x.CompanyId == companyId));
            var safety = await new PTGOilSystem.Web.Services.DeleteSafety.MasterDataDeleteSafetyService(db).EvaluateCompanyAsync(companyId);
            Assert.Contains("پیوند هویت امانی", safety.UsageAreas);
            var customerSafety = await new PTGOilSystem.Web.Services.DeleteSafety.MasterDataDeleteSafetyService(db).EvaluateCustomerAsync(w.CustomerId);
            Assert.Contains("امانی", customerSafety.UsageAreas);
        }
    }

    // ---------------------------------------------------------------- برگشتِ فروشِ وصول‌شده و استرداد

    private async Task<int> ReturnWithRefundAsync(World w, int saleId, decimal fromA, decimal refundCompany = 0m, decimal refundOwner = 0m,
        string? document = "PV-77", bool recoveryConfirmed = false, string? recovery = null)
    {
        await using var db = fixture.CreateDbContext();
        return await Sales(db).RecordReturnAsync(new CustodySaleReturnInput
        {
            SaleId = saleId, ReturnDate = Day.AddDays(4),
            Lines = [new CustodySaleSource(w.LotA, new CustodyLocation(w.TerminalId, w.TankA), fromA)],
            RefundFromCompanyUsd = refundCompany, RefundCashAccountId = refundCompany > 0m ? w.CashAccountId : null,
            RefundDocumentNumber = refundCompany > 0m ? document : null,
            RefundByOwnerUsd = refundOwner, OwnerRefundReference = refundOwner > 0m ? "Owner receipt OR-5" : null,
            OwnerRecoveryConfirmed = recoveryConfirmed, OwnerRecoveryReference = recovery
        });
    }

    [Fact]
    public async Task Return_Of_A_Fully_Collected_Sale_Refunds_From_Company_Cash_And_Reverses_Earned_Commission()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 30m, fromB: 0m);
        await CollectAsync(w, saleId, 15_000m);
        var revenueBefore = await GlAsync(w.CompanyId, "4500");
        Assert.Equal(14_550m, await OwnerFundsAsync(w.OwnerId));

        var noDoc = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnWithRefundAsync(w, saleId, 10m, refundCompany: 5_000m, document: null));
        Assert.Equal("CUSTODY_REFUND_DOCUMENT", noDoc.Code);
        var wrongSum = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnWithRefundAsync(w, saleId, 10m, refundCompany: 4_000m));
        Assert.Equal("CUSTODY_RETURN_COLLECTED", wrongSum.Code);
        var notOwner = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnWithRefundAsync(w, saleId, 10m, refundOwner: 5_000m));
        Assert.Equal("CUSTODY_REFUND_EXCEEDS", notOwner.Code);
        Assert.Equal(0m, await LotBalanceAsync(w.LotA) - 10m);

        var returnId = await ReturnWithRefundAsync(w, saleId, 10m, refundCompany: 5_000m);
        Assert.Equal(20m, await LotBalanceAsync(w.LotA));
        Assert.Equal(9_700m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(10_000m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(-9_700m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(150m, await GlAsync(w.CompanyId, "4500") - revenueBefore);
        await using (var db = fixture.CreateDbContext())
        {
            var r = await db.CustodySaleReturns.SingleAsync(x => x.Id == returnId);
            Assert.Equal(150m, r.RecognizedCommissionReversedUsd);
            var refund = await db.CustodyFinanceEntries.SingleAsync(e => e.CustodySaleReturnId == returnId);
            Assert.Equal(CustodyFinanceKind.CustomerRefund, refund.Kind);
            var payment = await db.PaymentTransactions.SingleAsync(p => p.Id == refund.PaymentTransactionId);
            Assert.Equal(PaymentDirection.Out, payment.Direction);
            Assert.Equal(5_000m, payment.AmountUsd);
            var ownerLedger = await db.LedgerEntries.Where(l => l.SupplierId == w.OwnerId).ToListAsync();
            Assert.Equal(9_700m, ownerLedger.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : -x.AmountUsd));

            var direct = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelFinanceAsync(refund.Id, "x"));
            Assert.Equal("CUSTODY_REFUND_VIA_RETURN", direct.Code);
            var collection = await db.CustodyFinanceEntries.SingleAsync(e => e.CustodySaleId == saleId && e.Kind == CustodyFinanceKind.Collection);
            var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelFinanceAsync(collection.Id, "x"));
            Assert.Equal("CUSTODY_COLLECTION_HAS_REFUNDS", blocked.Code);
        }

        // دومین برگشت: باقیِ فروش؛ همه وصول شده بود، پس کل مبلغ مسترد و کمیشن صفر می‌شود.
        await ReturnWithRefundAsync(w, saleId, 20m, refundCompany: 10_000m, document: "PV-78");
        Assert.Equal(0m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        // کل کمیشنِ درآمدشدهٔ ۴۵۰ (پیش از برگشت‌ها) معکوس شد؛ درآمد کمیشن این فروش صفر است.
        Assert.Equal(450m, await GlAsync(w.CompanyId, "4500") - revenueBefore);
        // برگشتِ دوباره از فروشِ کاملاً برگشت‌خورده رد می‌شود.
        var again = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnWithRefundAsync(w, saleId, 1m, refundCompany: 500m));
        Assert.Equal("CUSTODY_RETURN_EXCEEDS", again.Code);
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Refund_After_The_Owner_Was_Paid_Needs_A_Documented_Recovery_From_The_Owner()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, fromA: 30m, fromB: 0m);
        await CollectAsync(w, saleId, 15_000m);
        await using (var db = fixture.CreateDbContext())
        {
            await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
            {
                Kind = CustodyFinanceKind.OwnerPayout, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
                EntryDate = Day.AddDays(3), AmountUsd = 14_550m, CashAccountId = w.CashAccountId
            });
        }
        var guessed = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnWithRefundAsync(w, saleId, 10m, refundCompany: 5_000m));
        Assert.Equal("CUSTODY_REFUND_OWNER_PAID", guessed.Code);
        var noReference = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnWithRefundAsync(w, saleId, 10m, refundCompany: 5_000m, recoveryConfirmed: true));
        Assert.Equal("CUSTODY_REFUND_OWNER_PAID", noReference.Code);
        Assert.Equal(30m, 30m + await LotBalanceAsync(w.LotA) - 10m);

        var returnId = await ReturnWithRefundAsync(w, saleId, 10m, refundCompany: 5_000m, recoveryConfirmed: true, recovery: "Owner signed debit note DN-9");
        Assert.Equal(-4_850m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(4_850m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        await using (var db = fixture.CreateDbContext())
            Assert.Contains("DN-9", (await db.CustodyFinanceEntries.SingleAsync(e => e.CustodySaleReturnId == returnId)).Description);

        // لغوِ برگشت، استرداد را هم با پرداختِ معکوس برمی‌گرداند.
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelReturnAsync(returnId, "customer kept the stock");
        Assert.Equal(0m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(450m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        await using (var db = fixture.CreateDbContext())
        {
            var refund = await db.CustodyFinanceEntries.SingleAsync(e => e.CustodySaleReturnId == returnId);
            Assert.Equal(CustodyDocumentStatus.Cancelled, refund.Status);
            Assert.NotNull(refund.ReversalPaymentTransactionId);
        }
        Assert.Equal(10m, await LotBalanceAsync(w.LotA));
        await AssertCustodyJournalsBalancedAsync();
    }

    [Fact]
    public async Task Return_Of_A_Sale_Paid_Directly_To_The_Owner_Is_Refunded_By_The_Owner_Without_Company_Cash()
    {
        RequirePostgres();
        var w = await SeedAsync();
        int saleId;
        await using (var db = fixture.CreateDbContext())
        {
            var input = SaleInput(w, 30m, 0m);
            saleId = await Sales(db).CreateSaleAsync(new CustodySaleInput
            {
                LotId = input.LotId, Location = input.Location, QuantityMt = 30m, CustomerId = w.CustomerId, SaleDate = Day,
                UnitPriceUsd = 500m, CommissionMethod = CustodyCommissionMethod.PercentOfSale, CommissionRate = 3m,
                Settlement = CustodySaleSettlement.DirectToOwner
            });
        }
        Assert.Equal(-450m, await OwnerFundsAsync(w.OwnerId));
        var company = await Assert.ThrowsAsync<BusinessRuleException>(() => ReturnWithRefundAsync(w, saleId, 10m, refundCompany: 5_000m));
        Assert.Equal("CUSTODY_REFUND_EXCEEDS", company.Code);

        await ReturnWithRefundAsync(w, saleId, 10m, refundOwner: 5_000m);
        Assert.Equal(-300m, await OwnerFundsAsync(w.OwnerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(300m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        await using (var db = fixture.CreateDbContext())
            Assert.False(await db.PaymentTransactions.AnyAsync(p => p.CashAccountId == w.CashAccountId));
        await AssertCustodyJournalsBalancedAsync();
    }
}
