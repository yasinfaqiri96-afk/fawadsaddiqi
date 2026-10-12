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
using PTGOilSystem.Web.Services.Parties;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// چندارزیِ امانی (USD/AFN) روی PostgreSQL واقعی و ژورنال واقعی: ارز فروش و ارز تعهد به مالک جدا،
/// نرخ از جدول نرخ روزانه یا نرخ توافقی با مرجع، مسئولیت تفاوت نرخ (مالک/شرکت/تقسیم) بدون پیش‌فرض،
/// وصول و پرداخت قسمی، صندوق ارزی، نرخ تاریخی، تفاوت نرخ و برگشت/لغو.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CustodyPostgresCollection.CollectionName)]
public sealed class CustodyMultiCurrencyPostgresTests(ConcurrencyVersionPostgresFixture fixture)
{
    // ماهِ جدا از سایر تست‌ها تا نرخ‌های روزانهٔ همین تست خوانده شود.
    private static readonly DateTime Day = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly SemaphoreSlim Setup = new(1, 1);

    private sealed record World(int CompanyId, int ProductId, int TerminalId, int Tank, int OwnerId, int CustomerId,
        int UsdTill, int AfnTill, int LotId, int ProviderId, int ExpenseTypeId);

    private void RequirePostgres()
        => Assert.True(fixture.Available, $"تست امانی به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

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
            // اگر جدول ارزها پر است، AFN باید فعال باشد (قاعدهٔ ژورنال).
            if (await db.Currencies.AnyAsync() && !await db.Currencies.AnyAsync(c => c.Code == "AFN"))
            {
                db.Currencies.Add(new Currency { Code = "AFN", Name = "Afghani", IsActive = true });
                await db.SaveChangesAsync();
            }
            return company.Id;
        }
        finally
        {
            Setup.Release();
        }
    }

    private async Task<World> SeedAsync()
    {
        var companyId = await EnsureAccountingAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = fixture.CreateDbContext();
        var product = new Product { Code = $"FX-{tag}", Name = "Diesel" };
        var terminal = new Terminal { Code = $"FT-{tag}", Name = $"Hairatan {tag}" };
        var owner = new Supplier { Name = $"Owner {tag}" };
        var customer = new Customer { Name = $"Buyer {tag}" };
        var provider = new ServiceProvider { Name = $"Carrier {tag}" };
        var expenseType = new ExpenseType { Code = $"FXE-{tag}", Name = $"Transport {tag}", PayableAccountKind = ExpensePayableKind.FreightPayable };
        var usdTill = new CashAccount { Code = $"FU-{tag}", Name = $"USD till {tag}", Currency = "USD", AccountType = CashAccountType.Cash, CompanyId = companyId };
        var afnTill = new CashAccount { Code = $"FA-{tag}", Name = $"AFN till {tag}", Currency = "AFN", AccountType = CashAccountType.Cash, CompanyId = companyId };
        db.AddRange(product, terminal, owner, customer, provider, expenseType, usdTill, afnTill);
        await db.SaveChangesAsync();
        var tank = new StorageTank { TerminalId = terminal.Id, TankCode = $"T-{tag}", CapacityMt = 1_000m };
        db.Add(tank);
        await db.SaveChangesAsync();
        var lot = await Custody(db).ReceiveAsync(new CustodyReceiptInput
        {
            Owner = new CustodyOwner(AccountingPartyType.Supplier, owner.Id), ProductId = product.Id,
            Location = new CustodyLocation(terminal.Id, tank.Id), ReceiptDate = Day.AddDays(-5), QuantityMt = 100m
        });
        return new World(companyId, product.Id, terminal.Id, tank.Id, owner.Id, customer.Id, usdTill.Id, afnTill.Id, lot, provider.Id, expenseType.Id);
    }

    /// <summary>نرخِ رسمیِ همان روز (۱ دالر = چند افغانی). هر روز تست نرخ خودش را دارد.</summary>
    private async Task RateAsync(DateTime date, decimal afnPerUsd)
    {
        await using var db = fixture.CreateDbContext();
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var existing = await db.DailyFxRates.Where(r => r.BaseCurrency == "USD" && r.QuoteCurrency == "AFN" && r.RateDate == day).ToListAsync();
        db.DailyFxRates.RemoveRange(existing);
        db.DailyFxRates.Add(new DailyFxRate { BaseCurrency = "USD", QuoteCurrency = "AFN", RateDate = day, Rate = afnPerUsd, Source = "test" });
        await db.SaveChangesAsync();
    }

    private static CustodyService Custody(ApplicationDbContext db) => new(db, new AuditService(db), new FormTokenGuard(db));

    private static CustodySaleService Sales(ApplicationDbContext db)
    {
        var options = Options.Create(new AccountingOptions { Enabled = true, Pilots = new AccountingPilotOptions { Custody = true } });
        var company = new SystemCompanyProvider(db);
        var posting = new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, company);
        var adapter = new CustodyAccountingAdapter(db, posting, company, options, NullLogger<CustodyAccountingAdapter>.Instance);
        return new CustodySaleService(db, Custody(db), adapter, new LedgerPostingService(db), new AuditService(db), new FormTokenGuard(db), company);
    }

    private static CustodySaleInput Sale(World w, string currency, decimal price, string? ownerCurrency = null,
        decimal? ownerAgreed = null, string? ownerReference = null, CustodyFxResponsibility? responsibility = null,
        decimal? split = null, decimal quantity = 10m, DateTime? date = null,
        CustodyCommissionRecognition recognition = CustodyCommissionRecognition.OnCollection)
        => new()
        {
            LotId = w.LotId, Location = new CustodyLocation(w.TerminalId, w.Tank), QuantityMt = quantity,
            CustomerId = w.CustomerId, SaleDate = date ?? Day, UnitPriceUsd = price, Currency = currency,
            OwnerCurrency = ownerCurrency, OwnerAgreedPerUsd = ownerAgreed, OwnerRateReference = ownerReference,
            FxResponsibility = responsibility, FxOwnerSharePercent = split,
            FxAgreementReference = responsibility is null ? null : "Owner agreement FX-1",
            CommissionMethod = CustodyCommissionMethod.PercentOfSale, CommissionRate = 3m, CommissionRecognition = recognition
        };

    private async Task<int> SellAsync(CustodySaleInput input)
    {
        await using var db = fixture.CreateDbContext();
        return await Sales(db).CreateSaleAsync(input);
    }

    private async Task<int> FinanceAsync(CustodyFinanceInput input)
    {
        await using var db = fixture.CreateDbContext();
        return await Sales(db).RecordFinanceAsync(input);
    }

    private Task<int> CollectAsync(World w, int saleId, decimal amount, DateTime date, int? till = null)
        => FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.Collection, SaleId = saleId, EntryDate = date, AmountUsd = amount, CashAccountId = till ?? w.AfnTill
        });

    private async Task<decimal> FundsAsync(World w, string currency)
    {
        await using var db = fixture.CreateDbContext();
        return await CustodySaleService.OwnerFundsBalanceAsync(db, new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId), currency, CancellationToken.None);
    }

    private async Task<decimal> GlAsync(int companyId, string code, AccountingPartyType? partyType = null, int? partyId = null, int? cashAccountId = null,
        string? currency = null, bool transactionAmount = false, string? journalPrefix = null)
    {
        await using var db = fixture.CreateDbContext();
        var account = await db.Accounts.SingleAsync(a => a.CompanyId == companyId && a.Code == code);
        var lines = db.JournalEntryLines.Where(l => l.AccountId == account.Id && l.JournalEntry!.Status == JournalEntryStatus.Posted);
        if (partyType.HasValue) lines = lines.Where(l => l.PartyType == partyType && l.PartyId == partyId);
        if (cashAccountId.HasValue) lines = lines.Where(l => l.CashAccountId == cashAccountId);
        if (currency is not null) lines = lines.Where(l => l.TransactionCurrencyCode == currency);
        if (journalPrefix is not null) lines = lines.Where(l => l.JournalEntry!.SourceEventId!.StartsWith(journalPrefix));
        return transactionAmount
            ? await lines.SumAsync(l => l.Debit > 0m ? l.TransactionAmount : -l.TransactionAmount)
            : await lines.SumAsync(l => l.Debit - l.Credit);
    }

    private async Task AssertJournalsBalancedAsync()
    {
        await using var db = fixture.CreateDbContext();
        var unbalanced = await db.JournalEntries
            .Where(j => j.SourceModule == CustodyAccountingAdapter.SourceModule)
            .Select(j => new { j.JournalNumber, Debit = j.Lines.Sum(l => l.Debit), Credit = j.Lines.Sum(l => l.Credit) })
            .Where(x => x.Debit != x.Credit)
            .ToListAsync();
        Assert.Empty(unbalanced);
    }

    /// <summary>دفتر کل (۲۶۰۰ همین مالک) و صورت‌حساب عملیاتیِ مالک به دالر یکی‌اند.</summary>
    private async Task AssertOwnerGlMatchesStatementAsync(World w)
    {
        await using var db = fixture.CreateDbContext();
        var report = await new CustodyGlReadService(db, new SystemCompanyProvider(db), new PartyDirectory(db),
            Options.Create(new AccountingOptions { Enabled = true })).ReadAsync(Day.AddDays(30));
        var mine = report.Owners.SingleOrDefault(o => o.Owner == new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId));
        if (mine is not null)
            Assert.True(Math.Abs(mine.DifferenceUsd) < 0.0001m, $"GL {mine.FundsNetCreditUsd} vs statement {mine.OperationalFundsUsd}");
    }

    private static async Task<BusinessRuleException> Rule(Func<Task> action) => await Assert.ThrowsAsync<BusinessRuleException>(action);

    // ---------------------------------------------------------------- هم‌ارز: فروش و مالک هر دو افغانی

    [Fact]
    public async Task Afn_Sale_With_Afn_Owner_Uses_Historical_Rates_Partial_Collections_And_Afn_Till_Without_Fx_Difference()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await RateAsync(Day, 70m);
        await RateAsync(Day.AddDays(3), 72m);
        await RateAsync(Day.AddDays(4), 71m);
        await RateAsync(Day.AddDays(5), 70m);

        var saleId = await SellAsync(Sale(w, "AFN", 35_000m));
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal("AFN", sale.Currency);
            Assert.Equal("AFN", sale.OwnerCurrency);
            Assert.Equal(350_000m, sale.SaleAmount);
            Assert.Equal(10_500m, sale.SaleCommission);
            Assert.Equal(70m, sale.CurrencyPerUsd);
            Assert.Equal(5_000m, sale.AmountUsd);
            Assert.Null(sale.FxResponsibility);
        }
        Assert.Equal(5_000m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(350_000m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId, currency: "AFN", transactionAmount: true));
        Assert.Equal(-5_000m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));

        // وصولِ قسمی به صندوقِ افغانی با نرخِ همان روز (۷۲)؛ صندوق دالری پذیرفته نمی‌شود.
        Assert.Equal("CUSTODY_CASH_CURRENCY", (await Rule(() => CollectAsync(w, saleId, 140_000m, Day.AddDays(3), w.UsdTill))).Code);
        var first = await CollectAsync(w, saleId, 140_000m, Day.AddDays(3));
        await using (var db = fixture.CreateDbContext())
        {
            var entry = await db.CustodyFinanceEntries.SingleAsync(e => e.Id == first);
            Assert.Equal(72m, entry.CurrencyPerUsd);
            Assert.Contains("2026-09-10", entry.RateSource);
            Assert.Equal(1_944.4444m, entry.AmountUsd);
            Assert.Equal(140_000m, entry.OwnerAmount);
            Assert.Equal(4_200m, entry.OwnerCommission);
            Assert.Null(entry.FxCompanyUsd);
            var payment = await db.PaymentTransactions.SingleAsync(p => p.Id == entry.PaymentTransactionId);
            Assert.Equal("AFN", payment.Currency);
            Assert.Equal(140_000m, payment.Amount);
            Assert.Equal(1_944.4444m, payment.AmountUsd);
        }
        Assert.Equal(135_800m, await FundsAsync(w, "AFN"));
        Assert.Equal(0m, await FundsAsync(w, "USD"));
        Assert.Equal(140_000m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.AfnTill, currency: "AFN", transactionAmount: true));
        // طلب مشتری و «در انتظار وصول» با نرخ فروش (۷۰) کم شدند: ۲۰۰۰ دالر.
        Assert.Equal(3_000m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(-3_000m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "4200", journalPrefix: $"Custody:Finance:{first}:")
            + await GlAsync(w.CompanyId, "5300", journalPrefix: $"Custody:Finance:{first}:"));

        // پرداخت قسمی به مالک از صندوق افغانی؛ سقف به همان ارز.
        var payout = await FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.OwnerPayout, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
            EntryDate = Day.AddDays(4), AmountUsd = 100_000m, Currency = "AFN", CashAccountId = w.AfnTill
        });
        Assert.Equal(35_800m, await FundsAsync(w, "AFN"));
        Assert.Equal("CUSTODY_OVER_PAYOUT", (await Rule(() => FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.OwnerPayout, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
            EntryDate = Day.AddDays(4), AmountUsd = 40_000m, Currency = "AFN", CashAccountId = w.AfnTill
        }))).Code);
        // پولِ افغانیِ مالک با دالر پرداخت نمی‌شود (ماندهٔ دالری ندارد).
        Assert.Equal("CUSTODY_OVER_PAYOUT", (await Rule(() => FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.OwnerPayout, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
            EntryDate = Day.AddDays(4), AmountUsd = 100m, Currency = "USD", CashAccountId = w.UsdTill
        }))).Code);

        // وصولِ باقی‌مانده؛ کمیشنِ باقی دقیقاً تکمیل می‌شود.
        await CollectAsync(w, saleId, 210_000m, Day.AddDays(5));
        Assert.Equal(239_500m, await FundsAsync(w, "AFN"));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1200", AccountingPartyType.Customer, w.CustomerId));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "1210", AccountingPartyType.Supplier, w.OwnerId));
        // ماندهٔ ۲۶۰۰ به افغانی دقیقاً ماندهٔ حساب مالک است.
        Assert.Equal(-239_500m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId, currency: "AFN", transactionAmount: true));

        // لغوِ پرداخت: مانده و صندوق به حالت قبل.
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelFinanceAsync(payout, "اشتباه ثبت");
        Assert.Equal(339_500m, await FundsAsync(w, "AFN"));
        Assert.Equal(350_000m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.AfnTill, currency: "AFN", transactionAmount: true));
        await AssertOwnerGlMatchesStatementAsync(w);
        await AssertJournalsBalancedAsync();
    }

    [Fact]
    public async Task Missing_Rate_Or_Unreferenced_Agreed_Rate_Blocks_The_Sale_Without_Side_Effects()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var oldDay = new DateTime(1990, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("CUSTODY_FX_RATE_MISSING", (await Rule(() => SellAsync(Sale(w, "AFN", 35_000m, date: oldDay)))).Code);
        Assert.Equal("CUSTODY_FX_AGREED_REFERENCE", (await Rule(() => SellAsync(new CustodySaleInput
        {
            LotId = w.LotId, Location = new CustodyLocation(w.TerminalId, w.Tank), QuantityMt = 10m, CustomerId = w.CustomerId,
            SaleDate = Day, UnitPriceUsd = 35_000m, Currency = "AFN", AgreedCurrencyPerUsd = 69m
        }))).Code);
        Assert.Equal("CUSTODY_CURRENCY", (await Rule(() => SellAsync(Sale(w, "EUR", 500m)))).Code);
        // ارزِ مالکِ متفاوت با نرخ توافقیِ بدون مرجع ثبت نمی‌شود.
        Assert.Equal("CUSTODY_FX_AGREED_REFERENCE", (await Rule(() => SellAsync(Sale(w, "USD", 500m, ownerCurrency: "AFN", ownerAgreed: 70m)))).Code);
        // مسئولیت بدون مرجع یا تقسیمِ نامعتبر.
        Assert.Equal("CUSTODY_FX_SPLIT", (await Rule(() => SellAsync(Sale(w, "USD", 500m, ownerCurrency: "AFN", ownerAgreed: 70m,
            ownerReference: "Deal 7", responsibility: CustodyFxResponsibility.Split, split: 100m)))).Code);
        await using var check = fixture.CreateDbContext();
        Assert.Equal(0, await check.CustodySales.CountAsync(s => s.CustodyLotId == w.LotId));
        var balance = await check.CustodyMovements.Where(m => m.CustodyLotId == w.LotId)
            .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt);
        Assert.Equal(100m, balance);
    }

    // ---------------------------------------------------------------- دوارزی: فروش افغانی، تعهد دالری به مالک

    [Fact]
    public async Task Cross_Currency_Collection_With_Fx_Difference_Waits_For_Explicit_Responsibility_Then_Company_Bears_It()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await RateAsync(Day, 70m);
        await RateAsync(Day.AddDays(1), 70m);
        await RateAsync(Day.AddDays(3), 72m);

        var saleId = await SellAsync(Sale(w, "AFN", 35_000m, ownerCurrency: "USD", ownerAgreed: 70m, ownerReference: "Owner deal 2026/91"));
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal("USD", sale.OwnerCurrency);
            Assert.Equal(70m, sale.OwnerAgreedPerUsd);
            Assert.Contains("Owner deal 2026/91", sale.OwnerRateSource);
        }

        // روزی که نرخ با نرخ قفل‌شده برابر است، تفاوتی نیست و وصول بدون توافق ثبت می‌شود.
        var noDiff = await CollectAsync(w, saleId, 70_000m, Day.AddDays(1));
        Assert.Equal(1_000m - 30m, await FundsAsync(w, "USD"));

        // نرخ ۷۲: تفاوت دارد و مسئول تعیین نشده → سند ثبت نمی‌شود و هیچ اثری نمی‌ماند.
        int Payments() { using var db = fixture.CreateDbContext(); return db.PaymentTransactions.Count(p => p.CashAccountId == w.AfnTill); }
        var before = Payments();
        var blocked = await Rule(() => CollectAsync(w, saleId, 140_000m, Day.AddDays(3)));
        Assert.Equal("CUSTODY_FX_RESPONSIBILITY", blocked.Code);
        Assert.Equal(before, Payments());

        await using (var db = fixture.CreateDbContext())
            await Sales(db).SetFxAgreementAsync(saleId, CustodyFxResponsibility.Company, null, "Owner agreement FX-9");
        await using (var db = fixture.CreateDbContext())
            Assert.Equal("CUSTODY_FX_AGREEMENT_EXISTS", (await Rule(() => Sales(db).SetFxAgreementAsync(saleId, CustodyFxResponsibility.Owner, null, "x"))).Code);

        var collected = await CollectAsync(w, saleId, 140_000m, Day.AddDays(3));
        await using (var db = fixture.CreateDbContext())
        {
            var entry = await db.CustodyFinanceEntries.SingleAsync(e => e.Id == collected);
            Assert.Equal(1_944.4444m, entry.AmountUsd);
            Assert.Equal(2_000m, entry.OwnerAmount);          // مالک به نرخ قفل‌شده
            Assert.Equal(60m, entry.OwnerCommission);
            Assert.Equal(-55.56m, entry.FxDifference);
            Assert.Equal(0m, entry.FxOwnerShare);
            Assert.Equal(-55.5556m, entry.FxCompanyUsd);       // زیانِ تسعیرِ شرکت
        }
        Assert.Equal(970m + 1_940m, await FundsAsync(w, "USD"));
        Assert.Equal(55.5556m, await GlAsync(w.CompanyId, "5300", journalPrefix: CustodyAccountingAdapter.FinanceEventId(collected)));

        // لغوِ وصول، زیانِ تسعیر را هم برمی‌گرداند.
        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelFinanceAsync(collected, "تصحیح");
        Assert.Equal(970m, await FundsAsync(w, "USD"));
        Assert.Equal(0m, await GlAsync(w.CompanyId, "5300", journalPrefix: $"Custody:Finance:{collected}:"));
        Assert.NotEqual(0, noDiff);
        await AssertOwnerGlMatchesStatementAsync(w);
        await AssertJournalsBalancedAsync();
    }

    [Fact]
    public async Task Split_And_Owner_Responsibility_Allocate_The_Fx_Difference_Per_Agreement()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await RateAsync(Day, 70m);
        await RateAsync(Day.AddDays(3), 72m);

        var split = await SellAsync(Sale(w, "AFN", 35_000m, ownerCurrency: "USD", ownerAgreed: 70m, ownerReference: "Deal S",
            responsibility: CustodyFxResponsibility.Split, split: 40m));
        var ownerBears = await SellAsync(Sale(w, "AFN", 35_000m, ownerCurrency: "USD", ownerAgreed: 70m, ownerReference: "Deal O",
            responsibility: CustodyFxResponsibility.Owner));

        var a = await CollectAsync(w, split, 140_000m, Day.AddDays(3));
        var b = await CollectAsync(w, ownerBears, 140_000m, Day.AddDays(3));
        await using (var db = fixture.CreateDbContext())
        {
            var splitEntry = await db.CustodyFinanceEntries.SingleAsync(e => e.Id == a);
            Assert.Equal(-22.224m, splitEntry.FxOwnerShare);
            Assert.Equal(1_977.776m, splitEntry.OwnerAmount);
            Assert.Equal(-33.3316m, splitEntry.FxCompanyUsd);
            var ownerEntry = await db.CustodyFinanceEntries.SingleAsync(e => e.Id == b);
            Assert.Equal(-55.56m, ownerEntry.FxOwnerShare);
            Assert.Equal(1_944.44m, ownerEntry.OwnerAmount);
            // فقط باقیماندهٔ گردکردن (کمتر از یک سنت) به تسعیر می‌رود.
            Assert.Equal(0.0044m, ownerEntry.FxCompanyUsd);
        }
        Assert.Equal((1_977.776m - 60m) + (1_944.44m - 60m), await FundsAsync(w, "USD"));
        await AssertOwnerGlMatchesStatementAsync(w);
        await AssertJournalsBalancedAsync();
    }

    // ---------------------------------------------------------------- دوارزی: فروش دالری، تعهد افغانی به مالک

    [Fact]
    public async Task Usd_Sale_With_Afn_Owner_Credits_Owner_In_Afn_And_Pays_Partially_From_Afn_Till()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await RateAsync(Day, 70m);
        await RateAsync(Day.AddDays(3), 68m);
        await RateAsync(Day.AddDays(4), 69m);

        var saleId = await SellAsync(Sale(w, "USD", 500m, ownerCurrency: "AFN", responsibility: CustodyFxResponsibility.Owner));
        await using (var db = fixture.CreateDbContext())
        {
            var sale = await db.CustodySales.SingleAsync(s => s.Id == saleId);
            Assert.Equal(70m, sale.OwnerAgreedPerUsd);           // نرخ رسمیِ تاریخ فروش قفل شد
            Assert.Contains("نرخ روزانهٔ سیستم", sale.OwnerRateSource);
        }
        var collected = await CollectAsync(w, saleId, 5_000m, Day.AddDays(3), w.UsdTill);
        await using (var db = fixture.CreateDbContext())
        {
            var entry = await db.CustodyFinanceEntries.SingleAsync(e => e.Id == collected);
            Assert.Equal("AFN", entry.OwnerCurrency);
            Assert.Equal(-10_000m, entry.FxDifference);
            Assert.Equal(340_000m, entry.OwnerAmount);
            Assert.Equal(10_500m, entry.OwnerCommission);
            Assert.Equal(0m, entry.FxCompanyUsd);
        }
        Assert.Equal(329_500m, await FundsAsync(w, "AFN"));
        Assert.Equal(0m, await FundsAsync(w, "USD"));

        await FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.OwnerPayout, Owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId),
            EntryDate = Day.AddDays(4), AmountUsd = 200_000m, Currency = "AFN", CashAccountId = w.AfnTill
        });
        Assert.Equal(129_500m, await FundsAsync(w, "AFN"));
        Assert.Equal(-129_500m, await GlAsync(w.CompanyId, "2600", AccountingPartyType.Supplier, w.OwnerId, currency: "AFN", transactionAmount: true));
        await AssertOwnerGlMatchesStatementAsync(w);
        await AssertJournalsBalancedAsync();
    }

    // ---------------------------------------------------------------- برگشت با استرداد در فروش دوارزی

    [Fact]
    public async Task Refund_Of_A_Cross_Currency_Sale_Follows_The_Agreement_And_Cancels_Cleanly()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await RateAsync(Day, 70m);
        await RateAsync(Day.AddDays(3), 72m);
        await RateAsync(Day.AddDays(6), 75m);

        var saleId = await SellAsync(Sale(w, "AFN", 35_000m, ownerCurrency: "USD", ownerAgreed: 70m, ownerReference: "Deal R",
            responsibility: CustodyFxResponsibility.Company));
        await CollectAsync(w, saleId, 350_000m, Day.AddDays(3));
        Assert.Equal(4_850m, await FundsAsync(w, "USD"));

        int returnId;
        await using (var db = fixture.CreateDbContext())
        {
            returnId = await Sales(db).RecordReturnAsync(new CustodySaleReturnInput
            {
                SaleId = saleId, ReturnDate = Day.AddDays(6),
                Lines = [new CustodySaleSource(w.LotId, new CustodyLocation(w.TerminalId, w.Tank), 4m)],
                RefundFromCompanyUsd = 140_000m, RefundCashAccountId = w.AfnTill, RefundDocumentNumber = "RF-AFN-1"
            });
        }
        await using (var db = fixture.CreateDbContext())
        {
            var saleReturn = await db.CustodySaleReturns.SingleAsync(r => r.Id == returnId);
            Assert.Equal(140_000m, saleReturn.Amount);
            Assert.Equal(2_000m, saleReturn.AmountUsd);              // به نرخ فروش
            Assert.Equal(4_200m, saleReturn.RecognizedCommissionReversed);
            Assert.Equal(60m, saleReturn.OwnerCommissionReversed);
            var refund = await db.CustodyFinanceEntries.SingleAsync(e => e.CustodySaleReturnId == returnId);
            Assert.Equal(CustodyFinanceKind.CustomerRefund, refund.Kind);
            Assert.Equal(75m, refund.CurrencyPerUsd);
            Assert.Equal(1_866.6667m, refund.AmountUsd);
            Assert.Equal(2_000m, refund.OwnerAmount);                 // مالک به نرخ قفل‌شده بدهکار
            Assert.Equal(133.3333m, refund.FxCompanyUsd);             // سودِ تسعیرِ شرکت
            Assert.Equal(-133.3333m, await GlAsync(w.CompanyId, "4200", journalPrefix: CustodyAccountingAdapter.FinanceEventId(refund.Id)));
        }
        Assert.Equal(4_850m - 2_000m + 60m, await FundsAsync(w, "USD"));
        Assert.Equal(210_000m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.AfnTill, currency: "AFN", transactionAmount: true));

        // استردادِ اضافه یا برگشتِ بیش از مقدار ممکن نیست.
        await using (var db = fixture.CreateDbContext())
            Assert.Equal("CUSTODY_RETURN_COLLECTED", (await Rule(() => Sales(db).RecordReturnAsync(new CustodySaleReturnInput
            {
                SaleId = saleId, ReturnDate = Day.AddDays(6),
                Lines = [new CustodySaleSource(w.LotId, new CustodyLocation(w.TerminalId, w.Tank), 1m)],
                RefundFromCompanyUsd = 40_000m, RefundCashAccountId = w.AfnTill, RefundDocumentNumber = "RF-AFN-2"
            }))).Code);

        await using (var db = fixture.CreateDbContext())
            await Sales(db).CancelReturnAsync(returnId, "برگشت اشتباه");
        Assert.Equal(4_850m, await FundsAsync(w, "USD"));
        Assert.Equal(350_000m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.AfnTill, currency: "AFN", transactionAmount: true));
        await AssertOwnerGlMatchesStatementAsync(w);
        await AssertJournalsBalancedAsync();
    }

    // ---------------------------------------------------------------- مصرف نسیهٔ افغانی و پرداخت قسمی

    [Fact]
    public async Task Afn_Provider_Charge_Is_Paid_Partially_Only_In_Its_Own_Currency()
    {
        RequirePostgres();
        var w = await SeedAsync();
        await RateAsync(Day, 70m);
        await RateAsync(Day.AddDays(2), 71m);
        var owner = new CustodyOwner(AccountingPartyType.Supplier, w.OwnerId);
        var charge = await FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.ProviderCharge, Owner = owner, LotId = w.LotId, EntryDate = Day, AmountUsd = 21_000m, Currency = "AFN",
            ExpenseTypeId = w.ExpenseTypeId, ServiceProviderId = w.ProviderId
        });
        Assert.Equal(-21_000m, await FundsAsync(w, "AFN"));
        Assert.Equal("CUSTODY_CURRENCY_MISMATCH", (await Rule(() => FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.ProviderPayment, ProviderChargeId = charge, EntryDate = Day.AddDays(2), AmountUsd = 100m,
            Currency = "USD", CashAccountId = w.UsdTill
        }))).Code);
        await FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.ProviderPayment, ProviderChargeId = charge, EntryDate = Day.AddDays(2), AmountUsd = 7_100m, CashAccountId = w.AfnTill
        });
        Assert.Equal("CUSTODY_PROVIDER_OVERPAID", (await Rule(() => FinanceAsync(new CustodyFinanceInput
        {
            Kind = CustodyFinanceKind.ProviderPayment, ProviderChargeId = charge, EntryDate = Day.AddDays(2), AmountUsd = 14_000m, CashAccountId = w.AfnTill
        }))).Code);
        await using (var db = fixture.CreateDbContext())
            Assert.Equal(7_100m, await CustodySaleService.ProviderPaidAsync(db, charge, CancellationToken.None));
        Assert.Equal(-21_000m, await FundsAsync(w, "AFN"));
        Assert.Equal(-7_100m, await GlAsync(w.CompanyId, "1100", cashAccountId: w.AfnTill, currency: "AFN", transactionAmount: true));
        await AssertOwnerGlMatchesStatementAsync(w);
        await AssertJournalsBalancedAsync();
    }

    // ---------------------------------------------------------------- اسناد دالریِ قبلی

    [Fact]
    public async Task Legacy_Usd_Documents_Without_Currency_Columns_Keep_Their_Balances()
    {
        RequirePostgres();
        var w = await SeedAsync();
        var saleId = await SellAsync(Sale(w, "USD", 500m));
        await CollectAsync(w, saleId, 2_000m, Day.AddDays(1), w.UsdTill);
        Assert.Equal(1_940m, await FundsAsync(w, "USD"));
        // شبیه‌سازیِ ردیف‌های پیش از این Migration: ستون‌های ارزی خالی.
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""CustodySales"" SET ""UnitPrice"" = NULL, ""SaleAmount"" = NULL, ""SaleCommission"" = NULL,
                ""RateToUsd"" = NULL, ""CurrencyPerUsd"" = NULL, ""OwnerCurrency"" = NULL, ""OwnerRateToUsd"" = NULL, ""CommissionRecognizedAtSaleOwner"" = NULL
                WHERE ""Id"" = {saleId}");
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""CustodyFinanceEntries"" SET ""Amount"" = NULL, ""Commission"" = NULL, ""RateToUsd"" = NULL,
                ""CurrencyPerUsd"" = NULL, ""OwnerCurrency"" = NULL, ""OwnerAmount"" = NULL, ""OwnerCommission"" = NULL, ""OwnerRateToUsd"" = NULL
                WHERE ""CustodySaleId"" = {saleId}");
        }
        Assert.Equal(1_940m, await FundsAsync(w, "USD"));
        await CollectAsync(w, saleId, 3_000m, Day.AddDays(2), w.UsdTill);
        Assert.Equal(1_940m + 2_910m, await FundsAsync(w, "USD"));
        await using (var db = fixture.CreateDbContext())
        {
            var totals = await CustodySaleService.ReturnTotalsAsync(db, saleId, CancellationToken.None);
            Assert.Equal(0m, totals.Amount);
        }
        await AssertJournalsBalancedAsync();
    }
}
