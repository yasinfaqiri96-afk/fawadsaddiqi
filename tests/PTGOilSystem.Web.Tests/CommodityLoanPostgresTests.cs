using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.CommodityLoans;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Models.Reports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرض جنسی روی PostgreSQL واقعی: قرض‌دادن از موجودی ملکی به بهای دفتری، قرض‌گرفتن به ارزش توافقی،
/// برگشت قسمی، تسویهٔ نقدی، تفاوت مقدار در حد تلورانس، لغو از آخر به اول، هم‌زمانی، دفتر کل و راپور.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CustodyPostgresCollection.CollectionName)]
public sealed class CommodityLoanPostgresTests(ConcurrencyVersionPostgresFixture fixture)
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly SemaphoreSlim Setup = new(1, 1);

    private sealed record World(int CompanyId, int ProductId, int TerminalId, int TankId, int SupplierId, int CustomerId,
        int ReceivableAccountId, int PayableAccountId, int DifferenceAccountId, int CashAccountId);

    private void RequirePostgres()
        => Assert.True(fixture.Available, $"تست قرض جنسی به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

    private async Task<int> EnsureAccountingAsync()
    {
        await Setup.WaitAsync();
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
            if (!await db.FiscalYears.AnyAsync(y => y.CompanyId == company.Id && y.StartDate <= Day && y.EndDate >= Day))
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
            Setup.Release();
        }
    }

    /// <summary>۱۰۰ تُن موجودیِ ملکی به بهای ۶۰۰ دالر در یک مخزن، با حساب‌های مخصوصِ همین تست.</summary>
    private async Task<World> SeedAsync(decimal stockMt = 100m, decimal unitCost = 600m, bool valued = true)
    {
        var companyId = await EnsureAccountingAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = fixture.CreateDbContext();
        var product = new Product { Code = $"CL-{tag}", Name = "Gasoil" };
        var terminal = new Terminal { Code = $"CLT-{tag}", Name = $"Torghundi {tag}" };
        var supplier = new Supplier { Name = $"Lender {tag}" };
        var customer = new Customer { Name = $"Borrower {tag}" };
        var cash = new CashAccount { Code = $"CLC-{tag}", Name = $"Till {tag}", Currency = "USD", AccountType = CashAccountType.Cash, CompanyId = companyId };
        var receivable = new Account { CompanyId = companyId, Code = $"1460-{tag}", Name = "Commodity loans receivable", AccountType = AccountType.Asset, NormalBalance = NormalBalance.Debit };
        var payable = new Account { CompanyId = companyId, Code = $"2460-{tag}", Name = "Commodity loans payable", AccountType = AccountType.Liability, NormalBalance = NormalBalance.Credit };
        var difference = new Account { CompanyId = companyId, Code = $"4460-{tag}", Name = "Commodity loan differences", AccountType = AccountType.Revenue, NormalBalance = NormalBalance.Credit };
        db.AddRange(product, terminal, supplier, customer, cash, receivable, payable, difference);
        await db.SaveChangesAsync();
        var tank = new StorageTank { TerminalId = terminal.Id, TankCode = $"T-{tag}", CapacityMt = 1_000m };
        db.Add(tank);
        await db.SaveChangesAsync();
        await new InventoryMovementWriter(db, new StockService(db)).PostInboundAsync(new InventoryMovementRequest
        {
            ProductId = product.Id, TerminalId = terminal.Id, StorageTankId = tank.Id, MovementDate = Day.AddDays(-10),
            QuantityMt = stockMt, ReferenceDocument = $"TEST-OPENING:{tag}"
        });
        if (valued)
            await new InventoryValuationService(db).ApplyReceiptAsync(companyId, product.Id, terminal.Id, stockMt, stockMt * unitCost);
        return new World(companyId, product.Id, terminal.Id, tank.Id, supplier.Id, customer.Id, receivable.Id, payable.Id, difference.Id, cash.Id);
    }

    private static CommodityLoanService Loans(ApplicationDbContext db, bool accounting = true, bool pilot = true)
    {
        var options = Options.Create(new AccountingOptions { Enabled = accounting, Pilots = new AccountingPilotOptions { CommodityLoan = pilot } });
        var company = new SystemCompanyProvider(db);
        var posting = new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, company);
        return new CommodityLoanService(db, new InventoryMovementWriter(db, new StockService(db)), new InventoryValuationService(db),
            posting, company, options, new AuditService(db), new FormTokenGuard(db));
    }

    private static CommodityLoanInput Lend(World w, decimal quantity = 30m, bool valued = true, decimal? tolerance = null, bool cash = false,
        bool difference = true)
        => new()
        {
            Direction = CommodityLoanDirection.Lend, CounterpartyType = AccountingPartyType.Customer, CounterpartyId = w.CustomerId,
            ProductId = w.ProductId, QualitySpec = "EN590 10ppm", QuantityMt = quantity, LoanDate = Day, DueDate = Day.AddDays(20),
            ReturnObligation = "همان جنس و کیفیت در همین ترمینال", TerminalId = w.TerminalId, StorageTankId = w.TankId,
            ValuationPolicy = valued ? CommodityLoanValuation.BookCost : null, LoanAccountId = valued ? w.ReceivableAccountId : null,
            DifferenceAccountId = difference ? w.DifferenceAccountId : null, QuantityTolerancePercent = tolerance,
            CashSettlementAllowed = cash, CashSettlementUnitPriceUsd = cash ? 640m : null, CashSettlementReference = cash ? "Cash clause 4" : null,
            AgreementReference = "Loan agreement L-7"
        };

    private static CommodityLoanInput Borrow(World w, decimal quantity = 50m, bool difference = true, decimal? tolerance = 2m, bool cash = true)
        => new()
        {
            Direction = CommodityLoanDirection.Borrow, CounterpartyType = AccountingPartyType.Supplier, CounterpartyId = w.SupplierId,
            ProductId = w.ProductId, QualitySpec = "EN590 10ppm", QuantityMt = quantity, LoanDate = Day, DueDate = Day.AddDays(5),
            ReturnObligation = "همان جنس و کیفیت در ترمینال طرف", BorrowBasis = CommodityLoanBorrowBasis.RightOfUse,
            TerminalId = w.TerminalId, StorageTankId = w.TankId,
            ValuationPolicy = CommodityLoanValuation.AgreedValue, AgreedUnitValueUsd = 620m, ValuationReference = "Deal price sheet 12",
            LoanAccountId = w.PayableAccountId, DifferenceAccountId = difference ? w.DifferenceAccountId : null,
            QuantityTolerancePercent = tolerance, CashSettlementAllowed = cash, CashSettlementUnitPriceUsd = cash ? 615m : null,
            CashSettlementReference = cash ? "Cash clause 9" : null, AgreementReference = "Borrow agreement B-3"
        };

    private async Task<int> CreateAsync(CommodityLoanInput input, bool accounting = true, bool pilot = true)
    {
        await using var db = fixture.CreateDbContext();
        return await Loans(db, accounting, pilot).CreateAsync(input);
    }

    private async Task<int> SettleAsync(int loanId, CommodityLoanSettlementKind kind, decimal quantity, World w, int dayOffset = 2,
        string? document = null, bool accounting = true)
    {
        await using var db = fixture.CreateDbContext();
        return await Loans(db, accounting).SettleAsync(new CommodityLoanSettlementInput
        {
            LoanId = loanId, Kind = kind, SettlementDate = Day.AddDays(dayOffset), QuantityMt = quantity,
            TerminalId = kind == CommodityLoanSettlementKind.InKindReturn ? w.TerminalId : null,
            StorageTankId = kind == CommodityLoanSettlementKind.InKindReturn ? w.TankId : null,
            CashAccountId = kind == CommodityLoanSettlementKind.CashSettlement ? w.CashAccountId : null,
            DocumentNumber = document
        });
    }

    private async Task<decimal> StockAsync(World w)
    {
        await using var db = fixture.CreateDbContext();
        return await new StockService(db).GetFreeQuantityMtAsync(w.ProductId, terminalId: w.TerminalId);
    }

    private async Task<(decimal Quantity, decimal Value)> PoolAsync(World w)
    {
        await using var db = fixture.CreateDbContext();
        var pool = await db.InventoryAverageCosts.SingleOrDefaultAsync(p => p.CompanyId == w.CompanyId && p.ProductId == w.ProductId && p.TerminalId == w.TerminalId);
        return pool is null ? (0m, 0m) : (pool.QuantityMt, pool.TotalValueUsd);
    }

    private async Task<decimal> GlAsync(int accountId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.JournalEntryLines.Where(l => l.AccountId == accountId && l.JournalEntry!.Status == JournalEntryStatus.Posted)
            .SumAsync(l => l.Debit - l.Credit);
    }

    private async Task<decimal> InventoryGlForProductAsync(World w)
    {
        await using var db = fixture.CreateDbContext();
        var settings = await db.AccountingSettings.SingleAsync(s => s.CompanyId == w.CompanyId);
        return await db.JournalEntryLines.Where(l => l.AccountId == settings.InventoryAccountId && l.ProductId == w.ProductId
                && l.JournalEntry!.Status == JournalEntryStatus.Posted && l.JournalEntry.SourceModule == CommodityLoanService.SourceModule)
            .SumAsync(l => l.Debit - l.Credit);
    }

    private async Task AssertJournalsBalancedAsync()
    {
        await using var db = fixture.CreateDbContext();
        var unbalanced = await db.JournalEntries.Where(j => j.SourceModule == CommodityLoanService.SourceModule)
            .Select(j => new { j.JournalNumber, Debit = j.Lines.Sum(l => l.Debit), Credit = j.Lines.Sum(l => l.Credit) })
            .Where(x => x.Debit != x.Credit).ToListAsync();
        Assert.Empty(unbalanced);
    }

    private static async Task<string> CodeAsync(Func<Task> action) => (await Assert.ThrowsAsync<BusinessRuleException>(action)).Code;

    // ---------------------------------------------------------------- قرض‌دادن

    [Fact]
    public async Task Lending_Moves_Owned_Stock_At_Book_Cost_And_Partial_Returns_Restore_Stock_And_Value()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var loanId = await CreateAsync(Lend(w));
        Assert.Equal(70m, await StockAsync(w));
        Assert.Equal((70m, 42_000m), await PoolAsync(w));
        Assert.Equal(18_000m, await GlAsync(w.ReceivableAccountId));
        Assert.Equal(-18_000m, await InventoryGlForProductAsync(w));
        await using (var db = fixture.CreateDbContext())
        {
            var loan = await db.CommodityLoans.SingleAsync(l => l.Id == loanId);
            Assert.Equal($"CL-{loanId:D5}", loan.LoanNumber);
            Assert.Equal(18_000m, loan.CarryingValueUsd);
            var movement = await db.InventoryMovements.SingleAsync(m => m.Id == loan.InventoryMovementId);
            Assert.Equal(MovementDirection.Out, movement.Direction);
            Assert.Null(movement.ContractId);
            Assert.False(await db.CustodyMovements.AnyAsync(m => m.CustodyLot!.ProductId == w.ProductId));
        }

        var first = await SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 10m, w);
        Assert.Equal(80m, await StockAsync(w));
        Assert.Equal((80m, 48_000m), await PoolAsync(w));
        Assert.Equal(12_000m, await GlAsync(w.ReceivableAccountId));
        Assert.Equal("LOAN_OVER_SETTLEMENT", await CodeAsync(() => SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 25m, w)));
        Assert.Equal("LOAN_CASH_NOT_ALLOWED", await CodeAsync(() => SettleAsync(loanId, CommodityLoanSettlementKind.CashSettlement, 5m, w)));
        Assert.Equal("LOAN_TOLERANCE_NOT_SET", await CodeAsync(() => SettleAsync(loanId, CommodityLoanSettlementKind.QuantityDifference, 20m, w, document: "x")));

        var second = await SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 20m, w, dayOffset: 3);
        Assert.Equal(100m, await StockAsync(w));
        Assert.Equal((100m, 60_000m), await PoolAsync(w));
        Assert.Equal(0m, await GlAsync(w.ReceivableAccountId));
        await using (var db = fixture.CreateDbContext())
        {
            var row = (await new CommodityLoanReadService(db, new PartyDirectory(db), new SystemCompanyProvider(db)).ListAsync()).Single(r => r.Id == loanId);
            Assert.True(row.IsSettled);
            Assert.Equal(0m, row.OutstandingValueUsd);
        }

        // لغو فقط از آخر به اول؛ قرضِ دارای تسویه لغو نمی‌شود.
        await using (var db = fixture.CreateDbContext())
            Assert.Equal("LOAN_SETTLEMENT_NOT_LATEST", await CodeAsync(() => Loans(db).CancelSettlementAsync(first, "اشتباه")));
        await using (var db = fixture.CreateDbContext())
            Assert.Equal("LOAN_HAS_SETTLEMENTS", await CodeAsync(() => Loans(db).CancelLoanAsync(loanId, "اشتباه")));
        await using (var db = fixture.CreateDbContext())
            await Loans(db).CancelSettlementAsync(second, "اشتباه ثبت");
        Assert.Equal(80m, await StockAsync(w));
        Assert.Equal((80m, 48_000m), await PoolAsync(w));
        await using (var db = fixture.CreateDbContext())
            await Loans(db).CancelSettlementAsync(first, "اشتباه ثبت");
        await using (var db = fixture.CreateDbContext())
            await Loans(db).CancelLoanAsync(loanId, "قرض انجام نشد");
        Assert.Equal(100m, await StockAsync(w));
        Assert.Equal((100m, 60_000m), await PoolAsync(w));
        Assert.Equal(0m, await GlAsync(w.ReceivableAccountId));
        Assert.Equal(0m, await InventoryGlForProductAsync(w));
        await using (var db = fixture.CreateDbContext())
            Assert.Equal("LOAN_CANCELLED", await CodeAsync(() => Loans(db).CancelLoanAsync(loanId, "دوباره")));
        await AssertJournalsBalancedAsync();
    }

    [Fact]
    public async Task Financial_Loans_Without_Approved_Terms_Or_Valued_Stock_Stay_Blocked_Without_Side_Effects()
    {
        RequirePostgres();
        var w = await SeedAsync();
        Assert.Equal("LOAN_VALUATION_REQUIRED", await CodeAsync(() => CreateAsync(Lend(w, valued: false))));
        var noAccount = Lend(w);
        Assert.Equal("LOAN_ACCOUNT_REQUIRED", await CodeAsync(() => CreateAsync(new CommodityLoanInput
        {
            Direction = noAccount.Direction, CounterpartyType = noAccount.CounterpartyType, CounterpartyId = noAccount.CounterpartyId,
            ProductId = noAccount.ProductId, QualitySpec = noAccount.QualitySpec, QuantityMt = 10m, LoanDate = Day, DueDate = Day.AddDays(3),
            ReturnObligation = "same", TerminalId = w.TerminalId, StorageTankId = w.TankId, ValuationPolicy = CommodityLoanValuation.BookCost,
            AgreementReference = "A"
        })));
        Assert.Equal("LOAN_VALUATION", await CodeAsync(() => CreateAsync(new CommodityLoanInput
        {
            Direction = CommodityLoanDirection.Lend, CounterpartyType = AccountingPartyType.Customer, CounterpartyId = w.CustomerId,
            ProductId = w.ProductId, QualitySpec = "q", QuantityMt = 10m, LoanDate = Day, DueDate = Day.AddDays(3), ReturnObligation = "same",
            TerminalId = w.TerminalId, ValuationPolicy = CommodityLoanValuation.AgreedValue, LoanAccountId = w.ReceivableAccountId, AgreementReference = "A"
        })));
        Assert.Equal("LOAN_GL_NOT_READY", await CodeAsync(() => CreateAsync(Lend(w), pilot: false)));
        Assert.Equal("LOAN_ACCOUNT", await CodeAsync(() => CreateAsync(new CommodityLoanInput
        {
            Direction = CommodityLoanDirection.Lend, CounterpartyType = AccountingPartyType.Customer, CounterpartyId = w.CustomerId,
            ProductId = w.ProductId, QualitySpec = "q", QuantityMt = 10m, LoanDate = Day, DueDate = Day.AddDays(3), ReturnObligation = "same",
            TerminalId = w.TerminalId, ValuationPolicy = CommodityLoanValuation.BookCost, LoanAccountId = w.PayableAccountId, AgreementReference = "A"
        })));
        Assert.Equal("LOAN_BORROW_BASIS", await CodeAsync(() => CreateAsync(new CommodityLoanInput
        {
            Direction = CommodityLoanDirection.Borrow, CounterpartyType = AccountingPartyType.Supplier, CounterpartyId = w.SupplierId,
            ProductId = w.ProductId, QualitySpec = "q", QuantityMt = 10m, LoanDate = Day, DueDate = Day.AddDays(3), ReturnObligation = "same",
            TerminalId = w.TerminalId, ValuationPolicy = CommodityLoanValuation.AgreedValue, AgreedUnitValueUsd = 600m, ValuationReference = "r",
            LoanAccountId = w.PayableAccountId, AgreementReference = "A"
        })));
        Assert.Equal("STOCK_INSUFFICIENT", await CodeAsync(() => CreateAsync(Lend(w, quantity: 150m))));
        Assert.Equal(100m, await StockAsync(w));

        // موجودیِ بی‌ارزش (Pool خالی): بهای دفتری حدس زده نمی‌شود؛ حرکت موجودی هم برمی‌گردد.
        var unvalued = await SeedAsync(valued: false);
        Assert.Equal("LOAN_NOT_VALUED", await CodeAsync(() => CreateAsync(Lend(unvalued))));
        Assert.Equal(100m, await StockAsync(unvalued));
        await using var check = fixture.CreateDbContext();
        Assert.False(await check.CommodityLoans.AnyAsync(l => l.ProductId == w.ProductId || l.ProductId == unvalued.ProductId));
        Assert.Equal(0m, await GlAsync(w.ReceivableAccountId));
    }

    // ---------------------------------------------------------------- قرض‌گرفتن

    [Fact]
    public async Task Borrowing_Recognises_Stock_At_Agreed_Value_And_Settles_In_Kind_Cash_And_Within_Tolerance()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var loanId = await CreateAsync(Borrow(w));
        Assert.Equal(150m, await StockAsync(w));
        Assert.Equal((150m, 91_000m), await PoolAsync(w));
        Assert.Equal(-31_000m, await GlAsync(w.PayableAccountId));

        // برگشت ۲۰ تُن به بهای میانگین (۹۱٬۰۰۰ ÷ ۱۵۰)؛ تفاوت با ارزش دفتری بدهی (۱۲٬۴۰۰) سود است.
        var inKind = await SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 20m, w);
        await using (var db = fixture.CreateDbContext())
        {
            var s = await db.CommodityLoanSettlements.SingleAsync(x => x.Id == inKind);
            Assert.Equal(12_400m, s.CarryingValueUsd);
            Assert.Equal(12_133.3333m, s.InventoryValueUsd);
            Assert.Equal(266.6667m, s.DifferenceUsd);
        }
        Assert.Equal(130m, await StockAsync(w));
        Assert.Equal(-266.6667m, await GlAsync(w.DifferenceAccountId));

        // تسویهٔ نقدی ۱۰ تُن به قیمت قرارداد (۶۱۵): صندوق ۶٬۱۵۰، بدهی ۶٬۲۰۰، سود ۵۰.
        var cash = await SettleAsync(loanId, CommodityLoanSettlementKind.CashSettlement, 10m, w, dayOffset: 3);
        await using (var db = fixture.CreateDbContext())
        {
            var s = await db.CommodityLoanSettlements.SingleAsync(x => x.Id == cash);
            Assert.Equal(6_150m, s.CashAmountUsd);
            Assert.Equal(50m, s.DifferenceUsd);
            var payment = await db.PaymentTransactions.SingleAsync(p => p.Id == s.PaymentTransactionId);
            Assert.Equal(PaymentDirection.Out, payment.Direction);
            Assert.Equal(6_150m, payment.AmountUsd);
        }
        Assert.Equal(130m, await StockAsync(w));

        // باقی ۲۰ تُن؛ تلورانس ۲٪ = ۱ تُن. اول بیش از تلورانس رد می‌شود.
        Assert.Equal("LOAN_DIFFERENCE_EXCEEDS", await CodeAsync(() => SettleAsync(loanId, CommodityLoanSettlementKind.QuantityDifference, 20m, w, document: "Q-1")));
        await SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 19.5m, w, dayOffset: 4);
        Assert.Equal("LOAN_DIFFERENCE_DOCUMENT", await CodeAsync(() => SettleAsync(loanId, CommodityLoanSettlementKind.QuantityDifference, 0.5m, w, dayOffset: 4)));
        var diff = await SettleAsync(loanId, CommodityLoanSettlementKind.QuantityDifference, 0.5m, w, dayOffset: 4, document: "Counterparty waiver 0.5 MT");
        await using (var db = fixture.CreateDbContext())
        {
            var s = await db.CommodityLoanSettlements.SingleAsync(x => x.Id == diff);
            Assert.Equal(310m, s.CarryingValueUsd);
            Assert.Equal(310m, s.DifferenceUsd);
            var report = await new CommodityLoanReadService(db, new PartyDirectory(db), new SystemCompanyProvider(db)).ReportAsync(Day.AddDays(30));
            var supplierName = await db.Suppliers.Where(x => x.Id == w.SupplierId).Select(x => x.Name).SingleAsync();
            Assert.DoesNotContain(report.Positions, p => p.CounterpartyName == supplierName);
            var check = report.GlChecks.Single(c => c.AccountId == w.PayableAccountId);
            Assert.Equal(0m, check.OpenCarryingUsd);
            Assert.Equal(0m, check.DifferenceUsd);
        }
        Assert.Equal(0m, await GlAsync(w.PayableAccountId));
        await AssertJournalsBalancedAsync();
    }

    [Fact]
    public async Task Settlement_Difference_Without_A_Contract_Difference_Account_Is_Blocked_And_Overdue_Is_Reported()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var loanId = await CreateAsync(Borrow(w, difference: false));
        Assert.Equal("LOAN_DIFFERENCE_ACCOUNT", await CodeAsync(() => SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 20m, w)));
        Assert.Equal(150m, await StockAsync(w));
        Assert.Equal((150m, 91_000m), await PoolAsync(w));

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.CommodityLoanSettlements.AnyAsync(s => s.CommodityLoanId == loanId));
        var report = await new CommodityLoanReadService(db, new PartyDirectory(db), new SystemCompanyProvider(db)).ReportAsync(Day.AddDays(10));
        var supplierName = await db.Suppliers.Where(s => s.Id == w.SupplierId).Select(s => s.Name).SingleAsync();
        var position = report.Positions.Single(p => p.CounterpartyName == supplierName);
        Assert.Equal(50m, position.BorrowedOutstandingMt);
        Assert.Equal(31_000m, position.BorrowedValueUsd);
        Assert.Equal(1, position.OverdueLoans);
        Assert.Equal(0m, report.GlChecks.Single(c => c.AccountId == w.PayableAccountId).DifferenceUsd);

        // بیلانس مدیریتی قرض باز را شفاف می‌گوید و آن را دوباره جمع نمی‌زند.
        var balance = await new CompanyBalanceReportService(db, new ProfitAndLossService(db), new StockService(db), new PricingService(db))
            .BuildAsync(new CompanyBalanceReportRequest(Day.AddDays(-30), Day.AddDays(10), "USD", Day.AddDays(10)), []);
        Assert.Contains(balance.NotesFa, note => note.Contains("باقی قرض تیل") && note.Contains("راپور باقی قرض تیل"));
    }

    [Fact]
    public async Task Cancelling_A_Borrow_Whose_Goods_Were_Consumed_Is_Refused()
    {
        RequirePostgres();
        var w = await SeedAsync(stockMt: 0.0001m, unitCost: 600m);
        var loanId = await CreateAsync(Borrow(w));
        // جنسِ قرض‌گرفته مصرف شد (مثلاً فروش) — اینجا با خروجِ مستقیمِ موجودی و Pool.
        await using (var db = fixture.CreateDbContext())
        {
            await new InventoryMovementWriter(db, new StockService(db)).PostOutboundAsync(new InventoryMovementRequest
            {
                ProductId = w.ProductId, TerminalId = w.TerminalId, StorageTankId = w.TankId, MovementDate = Day.AddDays(1),
                QuantityMt = 40m, ReferenceDocument = "TEST-CONSUME:" + loanId
            });
            Assert.True((await new InventoryValuationService(db).TryConsumeAsync(w.CompanyId, w.ProductId, w.TerminalId, 40m)).Succeeded);
        }
        await using (var db = fixture.CreateDbContext())
            Assert.Equal("LOAN_CANCEL_CONSUMED", await CodeAsync(() => Loans(db).CancelLoanAsync(loanId, "اشتباه")));
        await using (var check = fixture.CreateDbContext())
            Assert.Equal(CommodityLoanStatus.Active, (await check.CommodityLoans.SingleAsync(l => l.Id == loanId)).Status);
    }

    // ---------------------------------------------------------------- هم‌زمانی و دفتر کل خاموش

    [Fact]
    public async Task Concurrent_Returns_Cannot_Exceed_The_Outstanding_Quantity()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var loanId = await CreateAsync(Lend(w, quantity: 50m));
        var attempts = Enumerable.Range(0, 2).Select(async i =>
        {
            try { await SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 30m, w, dayOffset: 2 + i); return true; }
            catch (BusinessRuleException ex) when (ex.Code == "LOAN_OVER_SETTLEMENT") { return false; }
        }).ToList();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(x => x));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(30m, await db.CommodityLoanSettlements.Where(s => s.CommodityLoanId == loanId && s.Status == CommodityLoanStatus.Active).SumAsync(s => s.QuantityMt));
        Assert.Equal(80m, await StockAsync(w));
    }

    [Fact]
    public async Task With_Accounting_Off_A_Loan_Is_Operational_Only()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var loanId = await CreateAsync(Lend(w, valued: false, tolerance: 5m), accounting: false);
        Assert.Equal(70m, await StockAsync(w));
        Assert.Equal((100m, 60_000m), await PoolAsync(w));
        await SettleAsync(loanId, CommodityLoanSettlementKind.InKindReturn, 29m, w, accounting: false);
        await SettleAsync(loanId, CommodityLoanSettlementKind.QuantityDifference, 1m, w, document: "Waiver", accounting: false);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.JournalEntries.AnyAsync(j => j.SourceModule == CommodityLoanService.SourceModule
            && (j.SourceEventId == CommodityLoanService.LoanEventId(loanId))));
        var loan = await db.CommodityLoans.SingleAsync(l => l.Id == loanId);
        Assert.Null(loan.CarryingValueUsd);
        Assert.Equal(99m, await StockAsync(w));
        // پس از روشن‌شدن دفتر کل، تسویهٔ مالیِ قرضِ بی‌ارزش ممکن نیست (مبنای ارزش ندارد).
        var other = await CreateAsync(Lend(w, valued: false), accounting: false);
        Assert.Equal("LOAN_NOT_VALUED", await CodeAsync(() => SettleAsync(other, CommodityLoanSettlementKind.InKindReturn, 1m, w)));
    }
}
