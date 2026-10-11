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
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// امانی مرحلهٔ ۲ — فروش به نمایندگی مالک، کمیشن، وصول، پرداخت مستقیم، مصارف و تسویهٔ مالک روی
/// PostgreSQL واقعی، با ژورنالِ واقعی از <see cref="AccountingPostingService"/>.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CustodyPostgresCollection.CollectionName)]
public sealed class CustodySalePostgresTests(ConcurrencyVersionPostgresFixture fixture)
{
    private static readonly DateTime SaleDay = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    private void RequirePostgres()
        => Assert.True(fixture.Available, $"تست امانی به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

    private sealed record World(int CompanyId, int ProductId, int TerminalId, int TankId, int OwnerId, int CustomerId, int CashAccountId, int LotId);

    private static readonly SemaphoreSlim AccountingSetup = new(1, 1);

    /// <summary>شرکتِ مالک، Chart (با حساب‌های امانی) و سال مالیِ ۲۰۲۶ با ۱۲ دورهٔ باز — یک بار برای کل مجموعه.</summary>
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

    private async Task<World> SeedAsync(decimal receivedMt = 100m, AccountingPartyType ownerType = AccountingPartyType.Supplier)
    {
        var companyId = await EnsureAccountingAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = fixture.CreateDbContext();
        var product = new Product { Code = $"SD-{tag}", Name = "Diesel" };
        var terminal = new Terminal { Code = $"ST-{tag}", Name = $"Herat {tag}" };
        var owner = new Supplier { Name = $"Ahmad {tag}" };
        var ownerCustomer = new Customer { Name = $"Ahmad-C {tag}" };
        var partner = new Partner { Code = $"P-{tag}", Name = $"Partner {tag}" };
        var customer = new Customer { Name = $"Buyer {tag}" };
        var cash = new CashAccount { Code = $"CA-{tag}", Name = $"Till {tag}", Currency = "USD", AccountType = CashAccountType.Cash, CompanyId = companyId };
        db.AddRange(product, terminal, owner, ownerCustomer, partner, customer, cash);
        await db.SaveChangesAsync();
        var tank = new StorageTank { TerminalId = terminal.Id, TankCode = $"T-{tag}", CapacityMt = 1_000m };
        db.Add(tank);
        await db.SaveChangesAsync();

        var ownerId = ownerType switch
        {
            AccountingPartyType.Customer => ownerCustomer.Id,
            AccountingPartyType.Partner => partner.Id,
            _ => owner.Id
        };
        var lotId = await Custody(db).ReceiveAsync(new CustodyReceiptInput
        {
            Owner = new CustodyOwner(ownerType, ownerId),
            ProductId = product.Id,
            Location = new CustodyLocation(terminal.Id, tank.Id),
            ReceiptDate = SaleDay.AddDays(-5),
            QuantityMt = receivedMt
        });
        return new World(companyId, product.Id, terminal.Id, tank.Id, ownerId, customer.Id, cash.Id, lotId);
    }

    private static CustodyService Custody(ApplicationDbContext db) => new(db, new AuditService(db), new FormTokenGuard(db));

    private static CustodySaleService Sales(ApplicationDbContext db, bool accountingEnabled = true, bool custodyPilot = true)
    {
        var options = Options.Create(new AccountingOptions { Enabled = accountingEnabled, Pilots = new AccountingPilotOptions { Custody = custodyPilot } });
        var company = new SystemCompanyProvider(db);
        var posting = new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, company);
        var adapter = new CustodyAccountingAdapter(db, posting, company, options, NullLogger<CustodyAccountingAdapter>.Instance);
        return new CustodySaleService(db, Custody(db), adapter, new LedgerPostingService(db), new AuditService(db), new FormTokenGuard(db), company);
    }

    private async Task<int> SellAsync(World w, decimal qty = 30m, decimal price = 500m,
        CustodySaleSettlement settlement = CustodySaleSettlement.Credit,
        CustodyCommissionRecognition recognition = CustodyCommissionRecognition.OnCollection,
        CustodyCommissionMethod method = CustodyCommissionMethod.PercentOfSale, decimal rate = 3m,
        string? token = null, DateTime? date = null)
    {
        await using var db = fixture.CreateDbContext();
        return await Sales(db).CreateSaleAsync(new CustodySaleInput
        {
            LotId = w.LotId,
            Location = new CustodyLocation(w.TerminalId, w.TankId),
            CustomerId = w.CustomerId,
            SaleDate = date ?? SaleDay,
            QuantityMt = qty,
            UnitPriceUsd = price,
            CommissionMethod = method,
            CommissionRate = rate,
            CommissionRecognition = recognition,
            Settlement = settlement,
            CashAccountId = settlement == CustodySaleSettlement.CashToCompany ? w.CashAccountId : null,
            FormToken = token
        });
    }

    private async Task<int> FinanceAsync(World w, CustodyFinanceKind kind, decimal amount, int? saleId = null)
    {
        await using var db = fixture.CreateDbContext();
        return await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
        {
            Kind = kind,
            SaleId = saleId,
            Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
            EntryDate = SaleDay.AddDays(1),
            AmountUsd = amount,
            CashAccountId = kind is CustodyFinanceKind.Collection or CustodyFinanceKind.OwnerPayout or CustodyFinanceKind.ReimbursableExpense
                ? w.CashAccountId
                : null
        });
    }

    private async Task<decimal> OwnerBalanceAsync(World w, AccountingPartyType type = AccountingPartyType.Supplier)
    {
        await using var db = fixture.CreateDbContext();
        return await CustodySaleService.OwnerFundsBalanceAsync(db, new CustodyOwner(type, w.OwnerId), CancellationToken.None);
    }

    private async Task<decimal> LotBalanceAsync(World w)
    {
        await using var db = fixture.CreateDbContext();
        return await db.CustodyMovements.Where(m => m.CustodyLotId == w.LotId)
            .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt);
    }

    /// <summary>ماندهٔ ژورنالیِ یک حساب برای یک طرف (بدهکار − بستانکار).</summary>
    private async Task<decimal> JournalBalanceAsync(int companyId, string accountCode, AccountingPartyType? partyType = null, int? partyId = null, int? cashAccountId = null)
    {
        await using var db = fixture.CreateDbContext();
        var account = await db.Accounts.SingleAsync(a => a.CompanyId == companyId && a.Code == accountCode);
        var lines = db.JournalEntryLines.Where(l => l.AccountId == account.Id && l.JournalEntry!.SourceModule == CustodyAccountingAdapter.SourceModule);
        if (partyType.HasValue) lines = lines.Where(l => l.PartyType == partyType && l.PartyId == partyId);
        if (cashAccountId.HasValue) lines = lines.Where(l => l.CashAccountId == cashAccountId);
        return await lines.SumAsync(l => l.Debit - l.Credit);
    }

    private async Task<decimal> CashBalanceAsync(World w)
    {
        await using var db = fixture.CreateDbContext();
        var rows = db.PaymentTransactions.Where(p => p.CashAccountId == w.CashAccountId);
        return await rows.SumAsync(p => p.Direction == PaymentDirection.In ? p.AmountUsd : -p.AmountUsd);
    }

    [Fact]
    public async Task Required_Example_Agency_Sale_With_Three_Percent_Commission_Collected_In_Full()
    {
        RequirePostgres();
        var w = await SeedAsync();
        int salesBefore, movementsBefore;
        await using (var db = fixture.CreateDbContext())
        {
            salesBefore = await db.SalesTransactions.CountAsync();
            movementsBefore = await db.InventoryMovements.CountAsync();
        }

        var saleId = await SellAsync(w, settlement: CustodySaleSettlement.CashToCompany);

        Assert.Equal(70m, await LotBalanceAsync(w));
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal(15_000m, sale.AmountUsd);
            Assert.Equal(450m, sale.CommissionUsd);
            Assert.Equal(salesBefore, await db.SalesTransactions.CountAsync());
            Assert.Equal(movementsBefore, await db.InventoryMovements.CountAsync());
        }
        Assert.Equal(14_550m, await OwnerBalanceAsync(w));
        Assert.Equal(15_000m, await CashBalanceAsync(w));

        // ژورنال: صندوق +۱۵٬۰۰۰، وجوه امانی مالک −۱۴٬۵۵۰، درآمد کمیشن −۴۵۰، دریافتنی و در انتظار وصول صفر.
        Assert.Equal(15_000m, await JournalBalanceAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        Assert.Equal(-14_550m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "4100"));

        // دفتر طرف‌ها: مالک (فروشنده) ۱۵٬۰۰۰ رسید و ۴۵۰ برد ⇒ شرکت ۱۴٬۵۵۰ بدهکار. مشتری سطری ندارد.
        await using (var db = fixture.CreateDbContext())
        {
            var ownerRows = await db.LedgerEntries.Where(l => l.SupplierId == w.OwnerId).ToListAsync();
            Assert.Equal(14_550m, ownerRows.Sum(r => r.Side == LedgerSide.Credit ? r.AmountUsd : -r.AmountUsd));
            Assert.False(await db.LedgerEntries.AnyAsync(l => l.CustomerId == w.CustomerId));
        }
    }

    [Fact]
    public async Task Company_Period_Profit_Counts_Only_Commission_Not_The_Owners_Sale_Amount()
    {
        RequirePostgres();
        var w = await SeedAsync();
        // روزی که هیچ تست دیگری سند امانی ندارد؛ بعد از تاریخ ورودِ بار.
        var day = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
        await SellAsync(w, settlement: CustodySaleSettlement.CashToCompany, date: day);

        await using var db = fixture.CreateDbContext();
        var period = await new ProfitAndLossService(db).BuildCompanyPeriodAsync(
            new ManagementReportFilterViewModel { FromDate = day, ToDate = day }, day);
        Assert.Equal(0m, period.SalesRevenueUsd);
        Assert.Equal(450m, period.CustodyIncomeUsd);
        Assert.Equal(450m, period.NetProfitUsd);
    }

    [Fact]
    public async Task Credit_Sale_Creates_No_Cash_Or_Owner_Debt_Until_Collected_And_Commission_Follows_Collections()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var commissionRevenueBefore = await JournalBalanceAsync(w.CompanyId, "4500");
        var saleId = await SellAsync(w);

        Assert.Equal(0m, await CashBalanceAsync(w));
        Assert.Equal(0m, await OwnerBalanceAsync(w));
        Assert.Equal(15_000m, await JournalBalanceAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-15_000m, await JournalBalanceAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));

        await FinanceAsync(w, CustodyFinanceKind.Collection, 6_000m, saleId);
        Assert.Equal(5_820m, await OwnerBalanceAsync(w));
        Assert.Equal(6_000m, await CashBalanceAsync(w));

        var over = await Assert.ThrowsAsync<BusinessRuleException>(() => FinanceAsync(w, CustodyFinanceKind.Collection, 9_000.01m, saleId));
        Assert.Equal("CUSTODY_OVER_COLLECTION", over.Code);

        await FinanceAsync(w, CustodyFinanceKind.Collection, 9_000m, saleId);
        Assert.Equal(14_550m, await OwnerBalanceAsync(w));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(450m, await db.CustodyFinanceEntries.Where(e => e.CustodySaleId == saleId).SumAsync(e => e.CommissionUsd));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-450m, await JournalBalanceAsync(w.CompanyId, "4500") - commissionRevenueBefore);
    }

    [Fact]
    public async Task Customer_Paying_Owner_Directly_Does_Not_Touch_Company_Cash_And_Owner_Owes_Only_Commission()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await SellAsync(w, settlement: CustodySaleSettlement.DirectToOwner);

        Assert.Equal(0m, await CashBalanceAsync(w));
        Assert.Equal(-450m, await OwnerBalanceAsync(w));
        Assert.Equal(450m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));

        var payout = await Assert.ThrowsAsync<BusinessRuleException>(() => FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 1m));
        Assert.Equal("CUSTODY_OVER_PAYOUT", payout.Code);
    }

    [Fact]
    public async Task Partial_Payouts_Charges_And_Service_Fee_Settle_The_Owner_Account_Without_Double_Deduction()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await SellAsync(w, settlement: CustodySaleSettlement.CashToCompany);
        int expensesBefore;
        await using (var before = fixture.CreateDbContext())
        {
            expensesBefore = await before.ExpenseTransactions.CountAsync();
        }
        var serviceRevenueBefore = await JournalBalanceAsync(w.CompanyId, "4510");

        await FinanceAsync(w, CustodyFinanceKind.ReimbursableExpense, 300m);
        await FinanceAsync(w, CustodyFinanceKind.ServiceFee, 250m);
        Assert.Equal(14_000m, await OwnerBalanceAsync(w));

        await FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 10_000m);
        Assert.Equal(4_000m, await OwnerBalanceAsync(w));
        var over = await Assert.ThrowsAsync<BusinessRuleException>(() => FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 4_000.01m));
        Assert.Equal("CUSTODY_OVER_PAYOUT", over.Code);
        await FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 4_000m);
        Assert.Equal(0m, await OwnerBalanceAsync(w));

        // صندوق: +۱۵٬۰۰۰ − ۳۰۰ − ۱۰٬۰۰۰ − ۴٬۰۰۰ = ۷۰۰ = کمیشن ۴۵۰ + حق خدمات ۲۵۰.
        Assert.Equal(700m, await CashBalanceAsync(w));
        Assert.Equal(700m, await JournalBalanceAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        await using var db = fixture.CreateDbContext();
        // مصرف به حساب مالک مصرفِ شرکت نیست: هیچ ExpenseTransaction (مصرف P&L) ساخته نشد.
        Assert.Equal(expensesBefore, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(-250m, await JournalBalanceAsync(w.CompanyId, "4510") - serviceRevenueBefore);
    }

    [Fact]
    public async Task Cancelling_Collection_Then_Sale_Restores_Stock_Cash_Owner_Account_And_Journals()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, settlement: CustodySaleSettlement.CashToCompany);

        await using (var db = fixture.CreateDbContext())
        {
            var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelSaleAsync(saleId, "اشتباه"));
            Assert.Equal("CUSTODY_SALE_HAS_RECEIPTS", blocked.Code);
        }

        int collectionId;
        await using (var db = fixture.CreateDbContext())
        {
            collectionId = await db.CustodyFinanceEntries.Where(e => e.CustodySaleId == saleId).Select(e => e.Id).SingleAsync();
            await Sales(db).CancelFinanceAsync(collectionId, "اشتباه در مبلغ");
        }
        await using (var db = fixture.CreateDbContext())
        {
            await Sales(db).CancelSaleAsync(saleId, "فروش انجام نشد");
            var again = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelSaleAsync(saleId, "دوباره"));
            Assert.Equal("CUSTODY_SALE_CANCELLED", again.Code);
        }

        Assert.Equal(100m, await LotBalanceAsync(w));
        Assert.Equal(0m, await CashBalanceAsync(w));
        Assert.Equal(0m, await OwnerBalanceAsync(w));
        var events = new[]
        {
            CustodyAccountingAdapter.SaleEventId(saleId),
            CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.SaleEventId(saleId)),
            CustodyAccountingAdapter.FinanceEventId(collectionId),
            CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.FinanceEventId(collectionId))
        };
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(4, await db.JournalEntries.CountAsync(j => j.SourceModule == CustodyAccountingAdapter.SourceModule && events.Contains(j.SourceEventId!)));
            var perAccount = await db.JournalEntryLines
                .Where(l => l.JournalEntry!.SourceModule == CustodyAccountingAdapter.SourceModule && events.Contains(l.JournalEntry.SourceEventId!))
                .GroupBy(l => l.AccountId)
                .Select(g => g.Sum(l => l.Debit - l.Credit))
                .ToListAsync();
            Assert.All(perAccount, net => Assert.Equal(0m, net));
        }
        await using (var db = fixture.CreateDbContext())
        {
            var ownerRows = await db.LedgerEntries.Where(l => l.SupplierId == w.OwnerId).ToListAsync();
            Assert.Equal(0m, ownerRows.Sum(r => r.Side == LedgerSide.Credit ? r.AmountUsd : -r.AmountUsd));
        }
    }

    [Fact]
    public async Task Cancelling_A_Collection_Already_Paid_Out_To_The_Owner_Is_Blocked()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, settlement: CustodySaleSettlement.CashToCompany);
        await FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 14_550m);

        await using var db = fixture.CreateDbContext();
        var collectionId = await db.CustodyFinanceEntries
            .Where(e => e.CustodySaleId == saleId && e.Kind == CustodyFinanceKind.Collection).Select(e => e.Id).SingleAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Sales(db).CancelFinanceAsync(collectionId, "اشتباه"));
        Assert.Equal("CUSTODY_CANCEL_OVERPAID", error.Code);
    }

    [Fact]
    public async Task Sale_Beyond_Free_Or_Held_Stock_Is_Rejected_And_Leaves_Nothing_Behind()
    {
        RequirePostgres();
        var w = await SeedAsync(receivedMt: 40m);
        var tooMuch = await Assert.ThrowsAsync<BusinessRuleException>(() => SellAsync(w, qty: 40.5m));
        Assert.Equal("CUSTODY_INSUFFICIENT", tooMuch.Code);

        await using (var db = fixture.CreateDbContext())
        {
            await Custody(db).PlaceHoldAsync(w.LotId, 30m, "اختلاف کیفیت", null);
        }
        var held = await Assert.ThrowsAsync<BusinessRuleException>(() => SellAsync(w, qty: 15m));
        Assert.Equal("CUSTODY_HELD", held.Code);

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.CustodySales.AnyAsync(s => s.CustodyLotId == w.LotId));
        Assert.Equal(40m, await LotBalanceAsync(w));
    }

    [Fact]
    public async Task Same_Form_Token_Records_The_Sale_Once()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var token = Guid.NewGuid().ToString("N");
        await SellAsync(w, token: token);
        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => SellAsync(w, token: token));

        await using var db = fixture.CreateDbContext();
        Assert.True(new FormTokenGuard(db).IsDuplicate(duplicate));
        Assert.Equal(1, await db.CustodySales.CountAsync(s => s.CustodyLotId == w.LotId));
        Assert.Equal(70m, await LotBalanceAsync(w));
    }

    [Fact]
    public async Task Two_Concurrent_Payouts_Cannot_Overpay_The_Owner()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await SellAsync(w, settlement: CustodySaleSettlement.CashToCompany);

        var results = await Task.WhenAll(
            Task.Run(async () => { try { await FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 10_000m); return true; } catch (BusinessRuleException) { return false; } }),
            Task.Run(async () => { try { await FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 10_000m); return true; } catch (BusinessRuleException) { return false; } }));

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(4_550m, await OwnerBalanceAsync(w));
    }

    [Fact]
    public async Task Two_Concurrent_Sales_From_The_Same_Lot_Cannot_Oversell()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var results = await Task.WhenAll(
            Task.Run(async () => { try { await SellAsync(w, qty: 60m); return true; } catch (BusinessRuleException) { return false; } }),
            Task.Run(async () => { try { await SellAsync(w, qty: 60m); return true; } catch (BusinessRuleException) { return false; } }));

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(40m, await LotBalanceAsync(w));
    }

    [Fact]
    public async Task Sale_Stock_Movement_Cannot_Be_Reversed_Alone_From_The_Lot_Page()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w);

        await using var db = fixture.CreateDbContext();
        var key = await db.CustodySales.Where(s => s.Id == saleId).Select(s => s.StockOperationKey).SingleAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Custody(db).ReverseOperationAsync(key, "اشتباه"));
        Assert.Equal("CUSTODY_SALE_REVERSAL", error.Code);
        Assert.Equal(70m, await LotBalanceAsync(w));
    }

    [Fact]
    public async Task Commission_On_Sale_Is_Earned_Immediately_Even_On_Credit()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w, recognition: CustodyCommissionRecognition.OnSale);

        Assert.Equal(-450m, await OwnerBalanceAsync(w));
        await FinanceAsync(w, CustodyFinanceKind.Collection, 15_000m, saleId);
        Assert.Equal(14_550m, await OwnerBalanceAsync(w));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(0m, await db.CustodyFinanceEntries.Where(e => e.CustodySaleId == saleId).SumAsync(e => e.CommissionUsd));
    }

    [Fact]
    public async Task Partner_Owner_Has_Custody_Storage_But_No_Financial_Sale_Yet()
    {
        RequirePostgres();
        var w = await SeedAsync(ownerType: AccountingPartyType.Partner);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SellAsync(w));
        Assert.Equal("CUSTODY_OWNER_FINANCE_UNSUPPORTED", error.Code);
        Assert.Equal(100m, await LotBalanceAsync(w));
    }

    [Fact]
    public async Task Partial_Collection_Of_7000_Earns_210_Commission_And_Uncollected_8000_Is_Neither_Asset_Nor_Liability()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var commissionBefore = await JournalBalanceAsync(w.CompanyId, "4500");
        var saleId = await SellAsync(w);

        // نسیه: طلبِ مشتری ۱۵٬۰۰۰ و کاهندهٔ آن به نفع مالک ۱۵٬۰۰۰ ⇒ خالصِ دارایی صفر؛ بدهی به مالک صفر.
        Assert.Equal(15_000m, await JournalBalanceAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-15_000m, await JournalBalanceAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(0m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));

        var entryId = await FinanceAsync(w, CustodyFinanceKind.Collection, 7_000m, saleId);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(210m, (await db.CustodyFinanceEntries.SingleAsync(e => e.Id == entryId)).CommissionUsd);
            var account = await db.Accounts.SingleAsync(a => a.CompanyId == w.CompanyId && a.Code == "1210");
            Assert.Equal(AccountType.Asset, account.AccountType);
            Assert.Equal(NormalBalance.Credit, account.NormalBalance);
        }
        Assert.Equal(7_000m, await CashBalanceAsync(w));
        Assert.Equal(7_000m, await JournalBalanceAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        Assert.Equal(8_000m, await JournalBalanceAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-8_000m, await JournalBalanceAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        // فقط پولِ وصول‌شده منهای کمیشنِ همان وصول بدهیِ شرکت به مالک است.
        Assert.Equal(-6_790m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(6_790m, await OwnerBalanceAsync(w));
        Assert.Equal(-210m, await JournalBalanceAsync(w.CompanyId, "4500") - commissionBefore);
    }

    [Fact]
    public async Task Expense_Paid_For_Owner_Before_Any_Sale_Is_A_Receivable_From_The_Owner_And_Is_Deducted_Later()
    {
        RequirePostgres();
        var w = await SeedAsync();

        await FinanceAsync(w, CustodyFinanceKind.ReimbursableExpense, 500m);

        Assert.Equal(-500m, await OwnerBalanceAsync(w));
        Assert.Equal(-500m, await CashBalanceAsync(w));
        Assert.Equal(-500m, await JournalBalanceAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        // ماندهٔ بدهکارِ مالک در حسابِ مالک = طلبِ شرکت از او.
        Assert.Equal(500m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
        await using (var db = fixture.CreateDbContext())
        {
            var rows = await db.LedgerEntries.Where(l => l.SupplierId == w.OwnerId).ToListAsync();
            Assert.Equal(-500m, rows.Sum(r => r.Side == LedgerSide.Credit ? r.AmountUsd : -r.AmountUsd));
        }
        var payout = await Assert.ThrowsAsync<BusinessRuleException>(() => FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 1m));
        Assert.Equal("CUSTODY_OVER_PAYOUT", payout.Code);

        await SellAsync(w, settlement: CustodySaleSettlement.CashToCompany);
        Assert.Equal(14_050m, await OwnerBalanceAsync(w));
        Assert.Equal(-14_050m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
    }

    [Fact]
    public async Task Every_Custody_Journal_Balances_And_Traces_Back_To_Its_Document()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var creditSale = await SellAsync(w, qty: 20m, recognition: CustodyCommissionRecognition.OnSale);
        var directSale = await SellAsync(w, qty: 10m, settlement: CustodySaleSettlement.DirectToOwner);
        var collection = await FinanceAsync(w, CustodyFinanceKind.Collection, 4_000m, creditSale);
        await using (var db = fixture.CreateDbContext())
        {
            await Sales(db).CancelFinanceAsync(collection, "اشتباه در مبلغ");
        }
        await FinanceAsync(w, CustodyFinanceKind.ReimbursableExpense, 120m);
        await FinanceAsync(w, CustodyFinanceKind.ServiceFee, 80m);
        await FinanceAsync(w, CustodyFinanceKind.Collection, 3_500m, creditSale);
        await FinanceAsync(w, CustodyFinanceKind.OwnerPayout, 1_000m);

        await using var check = fixture.CreateDbContext();
        var saleIds = await check.CustodySales.Where(s => s.CustodyLotId == w.LotId).Select(s => s.Id).ToListAsync();
        var entryIds = await check.CustodyFinanceEntries
            .Where(e => e.OwnerPartyType == AccountingPartyType.Supplier && e.OwnerPartyId == w.OwnerId)
            .Select(e => e.Id).ToListAsync();
        Assert.Equal(2, saleIds.Count);
        Assert.Contains(directSale, saleIds);
        var eventIds = saleIds
            .SelectMany(id => new[] { CustodyAccountingAdapter.SaleEventId(id), CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.SaleEventId(id)) })
            .Concat(entryIds.SelectMany(id => new[] { CustodyAccountingAdapter.FinanceEventId(id), CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.FinanceEventId(id)) }))
            .ToList();
        var journals = await check.JournalEntries.Include(j => j.Lines)
            .Where(j => j.SourceModule == CustodyAccountingAdapter.SourceModule && eventIds.Contains(j.SourceEventId!))
            .ToListAsync();

        // هر سند امانی دقیقاً یک ژورنال؛ سندِ لغوشده یک ژورنالِ برگشت هم دارد.
        Assert.Equal(saleIds.Count + entryIds.Count + 1, journals.Count);
        foreach (var journal in journals)
        {
            Assert.NotEmpty(journal.Lines);
            Assert.Equal(journal.Lines.Sum(l => l.Debit), journal.Lines.Sum(l => l.Credit));
            Assert.True(journal.Lines.Sum(l => l.Debit) > 0m);
        }
        foreach (var id in entryIds)
            Assert.Single(journals, j => j.SourceEventId == CustodyAccountingAdapter.FinanceEventId(id));
        Assert.Single(journals, j => j.SourceEventId == CustodyAccountingAdapter.ReversalEventId(CustodyAccountingAdapter.FinanceEventId(collection)));

        // صندوق، دفتر کل و حساب مالک یکدیگر را تکمیل می‌کنند.
        Assert.Equal(await CashBalanceAsync(w), await JournalBalanceAsync(w.CompanyId, "1100", cashAccountId: w.CashAccountId));
        Assert.Equal(-await OwnerBalanceAsync(w), await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
    }

    [Fact]
    public async Task General_Ledger_Not_Ready_Rejects_The_Whole_Custody_Document()
    {
        RequirePostgres();
        var w = await SeedAsync();
        int paymentsBefore;
        await using (var db = fixture.CreateDbContext())
            paymentsBefore = await db.PaymentTransactions.CountAsync();

        async Task<int> SellWith(bool enabled, bool pilot)
        {
            await using var db = fixture.CreateDbContext();
            return await Sales(db, enabled, pilot).CreateSaleAsync(new CustodySaleInput
            {
                LotId = w.LotId, Location = new CustodyLocation(w.TerminalId, w.TankId), CustomerId = w.CustomerId,
                SaleDate = SaleDay, QuantityMt = 30m, UnitPriceUsd = 500m,
                CommissionMethod = CustodyCommissionMethod.PercentOfSale, CommissionRate = 3m,
                Settlement = CustodySaleSettlement.CashToCompany, CashAccountId = w.CashAccountId
            });
        }

        // دفتر کل روشن ولی ژورنالِ امانی خاموش: هیچ چیز نصفه ثبت نمی‌شود.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SellWith(enabled: true, pilot: false));
        Assert.Equal("CUSTODY_GL_NOT_READY", error.Code);
        Assert.Equal(100m, await LotBalanceAsync(w));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.CustodySales.AnyAsync(s => s.CustodyLotId == w.LotId));
            Assert.Equal(paymentsBefore, await db.PaymentTransactions.CountAsync());
            Assert.False(await db.LedgerEntries.AnyAsync(l => l.SupplierId == w.OwnerId));
        }

        // دفتر کلِ کل سیستم خاموش (رفتارِ همهٔ ماژول‌ها): فقط دفتر عملیاتی، بدون ژورنال.
        var saleId = await SellWith(enabled: false, pilot: true);
        Assert.Equal(70m, await LotBalanceAsync(w));
        await using (var db = fixture.CreateDbContext())
            Assert.False(await db.JournalEntries.AnyAsync(j => j.SourceEventId == CustodyAccountingAdapter.SaleEventId(saleId)));
    }

    [Fact]
    public async Task Collection_On_A_Sale_Recorded_While_Ledger_Was_Off_First_Posts_The_Missing_Sale_Journal()
    {
        RequirePostgres();
        var w = await SeedAsync();
        int saleId;
        await using (var db = fixture.CreateDbContext())
        {
            saleId = await Sales(db, accountingEnabled: false).CreateSaleAsync(new CustodySaleInput
            {
                LotId = w.LotId, Location = new CustodyLocation(w.TerminalId, w.TankId), CustomerId = w.CustomerId,
                SaleDate = SaleDay, QuantityMt = 30m, UnitPriceUsd = 500m,
                CommissionMethod = CustodyCommissionMethod.PercentOfSale, CommissionRate = 3m
            });
        }

        await FinanceAsync(w, CustodyFinanceKind.Collection, 7_000m, saleId);

        // بدونِ ژورنالِ فروش، وصول دریافتنیِ مشتری را منفی می‌کرد.
        Assert.Equal(8_000m, await JournalBalanceAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-8_000m, await JournalBalanceAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(-6_790m, await JournalBalanceAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId));
    }

    [Fact]
    public async Task Closed_Fiscal_Period_Rejects_Custody_Sale_Atomically()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var december = new DateTime(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = fixture.CreateDbContext())
        {
            await db.FiscalPeriods.Where(p => p.CompanyId == w.CompanyId && p.PeriodNumber == 12)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, FiscalPeriodStatus.Closed));
        }
        try
        {
            await Assert.ThrowsAsync<AccountingValidationException>(() =>
                SellAsync(w, settlement: CustodySaleSettlement.CashToCompany, date: december));

            Assert.Equal(100m, await LotBalanceAsync(w));
            Assert.Equal(0m, await CashBalanceAsync(w));
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.CustodySales.AnyAsync(s => s.CustodyLotId == w.LotId));
            Assert.False(await db.LedgerEntries.AnyAsync(l => l.SupplierId == w.OwnerId));
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            await db.FiscalPeriods.Where(p => p.CompanyId == w.CompanyId && p.PeriodNumber == 12)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, FiscalPeriodStatus.Open));
        }
    }

    [Fact]
    public async Task Same_Form_Token_Records_A_Collection_Once()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(w);
        var token = Guid.NewGuid().ToString("N");

        async Task Collect()
        {
            await using var db = fixture.CreateDbContext();
            await Sales(db).RecordFinanceAsync(new CustodyFinanceInput
            {
                Kind = CustodyFinanceKind.Collection, SaleId = saleId, EntryDate = SaleDay.AddDays(1),
                AmountUsd = 7_000m, CashAccountId = w.CashAccountId, FormToken = token
            });
        }

        await Collect();
        var duplicate = await Assert.ThrowsAnyAsync<Exception>(Collect);
        await using (var db = fixture.CreateDbContext())
            Assert.True(new FormTokenGuard(db).IsDuplicate(duplicate));
        Assert.Equal(7_000m, await CashBalanceAsync(w));
        Assert.Equal(6_790m, await OwnerBalanceAsync(w));
    }

    [Theory]
    [InlineData(CustodyCommissionMethod.PercentOfSale, 3, 30, 15000, 450)]
    [InlineData(CustodyCommissionMethod.PerMt, 12.5, 30, 15000, 375)]
    [InlineData(CustodyCommissionMethod.FixedPerSale, 200, 30, 15000, 200)]
    [InlineData(CustodyCommissionMethod.None, 9, 30, 15000, 0)]
    public void Commission_Methods_Calculate_Separately_From_Sale_Amount(
        CustodyCommissionMethod method, double rate, double qty, double amount, double expected)
        => Assert.Equal((decimal)expected, CustodySaleService.CalculateCommission(method, (decimal)rate, (decimal)qty, (decimal)amount));
}
