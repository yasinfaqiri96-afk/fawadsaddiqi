using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// صورت‌حساب شراکت — همان دو قرارداد واقعی P-016 و P-017 با همان اعداد ثبت‌شده.
///
/// اینجا فقط رفتار سرویس اثبات می‌شود: چه کسی چقدر پرداخت کرده، عاید فروش نزد کیست،
/// مفاد ۵۰/۵۰ چطور تقسیم می‌شود، مانده نهایی چه جهتی دارد و اینکه تسویهٔ بین شرکا
/// هیچ اثری روی مفاد دفتری قرارداد ندارد و هیچ درآمد/مصرفی دوباره شمرده نمی‌شود.
/// </summary>
public sealed class PartnershipStatementTests
{
    private const decimal C16FawadFunding = 321_575m;
    private const decimal C16YusufFunding = 29_155m;
    private const decimal C16SalesUsd = 447_910.9999m;
    private const decimal C17FawadFunding = 123_739m;
    private const decimal C17YusufFunding = 661_323.896m;
    private const decimal C17SalesUsd = 1_031_871m;

    // ————————————————— A و B: پرداخت واقعی هر شریک —————————————————

    [Fact]
    public async Task Contract16_Funding_MatchesEachPartnerActualPayments()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);
        var c16 = statement.Contracts.Single(c => c.ContractId == s.Contract16Id);

        Assert.Equal(321_575.00m, PartnerOf(c16, s.FawadId).FundingUsd);
        Assert.Equal(29_155.00m, PartnerOf(c16, s.YusufId).FundingUsd);
        Assert.Equal(350_730.00m, c16.TotalPartnerFundingUsd);
    }

    [Fact]
    public async Task Contract17_Funding_MatchesEachPartnerActualPayments()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);
        var c17 = statement.Contracts.Single(c => c.ContractId == s.Contract17Id);

        Assert.Equal(123_739.00m, PartnerOf(c17, s.FawadId).FundingUsd);
        Assert.Equal(661_323.90m, PartnerOf(c17, s.YusufId).FundingUsd);
    }

    // ————————————————— C و D: عاید فروش نزد کدام شریک —————————————————

    [Fact]
    public async Task Contract16_SaleProceeds_AreHeldByYusuf()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);
        var c16 = statement.Contracts.Single(c => c.ContractId == s.Contract16Id);

        Assert.Equal(s.YusufId, c16.ProceedsHolderPartnerId);
        Assert.Equal(447_911.00m, PartnerOf(c16, s.YusufId).ProceedsHeldUsd);
        Assert.Equal(0m, PartnerOf(c16, s.FawadId).ProceedsHeldUsd);
    }

    [Fact]
    public async Task Contract17_SaleProceeds_AreHeldByFawad()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);
        var c17 = statement.Contracts.Single(c => c.ContractId == s.Contract17Id);

        Assert.Equal(s.FawadId, c17.ProceedsHolderPartnerId);
        Assert.Equal(1_031_871.00m, PartnerOf(c17, s.FawadId).ProceedsHeldUsd);
        Assert.Equal(0m, PartnerOf(c17, s.YusufId).ProceedsHeldUsd);
    }

    // ————————————————— E: تقسیم ۵۰/۵۰ مفاد —————————————————

    [Fact]
    public async Task ProfitShare_IsSplitFiftyFifty_OnEachContract()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);
        foreach (var contract in statement.Contracts)
        {
            var fawad = PartnerOf(contract, s.FawadId);
            var yusuf = PartnerOf(contract, s.YusufId);

            Assert.Equal(50m, fawad.SharePercent);
            Assert.Equal(50m, yusuf.SharePercent);

            // مفادی که به سِنت بخش‌پذیر نیست، دو سهمِ دقیقاً برابر نمی‌دهد. قبلاً هر دو سهم
            // جداگانه بالا گِرد می‌شدند و جمعشان یک سِنت از خودِ مفاد بیشتر می‌شد؛ حالا باقیمانده
            // به‌صورت قطعی به یک شریک می‌رسد. پس شرطِ درست «برابری» نیست، «حداکثر یک سِنت فاصله
            // و جمعِ دقیق» است — همان چیزی که ژورنالِ تخصیص سود هم ثبت می‌کند.
            Assert.True(Math.Abs(fawad.ProfitShareUsd - yusuf.ProfitShareUsd) <= 0.01m);
            Assert.Equal(contract.BookProfitUsd, fawad.ProfitShareUsd + yusuf.ProfitShareUsd);

            var halfShare = decimal.Round(contract.BookProfitUsd * 0.5m, 2, MidpointRounding.AwayFromZero);
            Assert.True(Math.Abs(halfShare - fawad.ProfitShareUsd) <= 0.01m);
            Assert.True(Math.Abs(halfShare - yusuf.ProfitShareUsd) <= 0.01m);
            // مفاد = سودِ محققِ قرارداد (فقط بخشِ فروخته‌شده)، نه «فروش منهای کلِ خرید».
            Assert.Equal(
                decimal.Round(contract.SalesUsd - contract.RealizedCostOfGoodsSoldUsd
                    - contract.RealizedOperationalCostUsd + contract.RealizedFxNetUsd, 2,
                    MidpointRounding.AwayFromZero),
                contract.BookProfitUsd);
            // هزینهٔ فروخته‌نشده جدا و به همان نسبت بین شرکا تقسیم می‌شود؛ جمعِ «مفاد محقق − هزینهٔ
            // فروخته‌نشده» همان مفادِ کاملِ قبلی است، پس برابرسازیِ هزینه بین شرکا عوض نمی‌شود.
            Assert.Equal(
                decimal.Round(contract.SalesUsd - contract.PurchaseCostUsd - contract.OperationalExpenseUsd, 2,
                    MidpointRounding.AwayFromZero),
                contract.BookProfitUsd - contract.UnrealizedCostCarriedUsd);
            Assert.Equal(
                contract.UnrealizedCostCarriedUsd,
                fawad.UnsoldCostShareUsd + yusuf.UnsoldCostShareUsd);
        }
    }

    // ————————————————— F: مانده نهایی ترکیبی —————————————————

    [Fact]
    public async Task CombinedStatement_ProducesOneDirectionAndOneAmount()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);

        Assert.Equal(s.FawadId, statement.DebtorPartnerId);
        Assert.Equal(s.YusufId, statement.CreditorPartnerId);
        Assert.Equal(414_561.46m, statement.AmountDueUsd);
        Assert.Equal(414_563.45m, statement.CreditorClaimUsd);

        var fawad = statement.Totals.Single(t => t.PartnerId == s.FawadId);
        var yusuf = statement.Totals.Single(t => t.PartnerId == s.YusufId);
        Assert.Equal(-414_561.46m, fawad.NetPositionUsd);
        Assert.Equal(414_563.45m, yusuf.NetPositionUsd);

        // جمع مانده دو شریک صفر نیست و نباید به‌زور صفر شود: اختلافِ «پرداخت شرکا» با
        // «خرید + مصارف دفتری» به‌صورت باقیماندهٔ تطبیق‌نشده صریح گزارش می‌شود.
        // ۱٫۹۹ و نه ۲٫۰۰: باقیمانده فقط اختلافِ «پرداخت شرکا» با «خرید + مصارف دفتری» است.
        // یک سِنتِ اضافیِ قبلی از گِردکردنِ جداگانهٔ سهم‌ها می‌آمد و ربطی به تطبیق نداشت.
        Assert.Equal(1.99m, statement.UnreconciledResidualUsd);
        Assert.Equal(
            statement.UnreconciledResidualUsd,
            decimal.Round(fawad.NetPositionUsd + yusuf.NetPositionUsd, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Settlement_BothPartnersNegative_ByOneCent_HasNoDirection()
    {
        // P-003/P-004: هر دو فقط سهمِ خود از هزینهٔ پرداخت‌نشده را دارند؛ یک سنت گرد کردن جهت نمی‌سازد.
        var result = PartnershipStatementService.ResolveSettlement([(5, -10_215_000.68m), (2, -10_215_000.67m)]);

        Assert.Null(result.DebtorPartnerId);
        Assert.Null(result.CreditorPartnerId);
        Assert.Equal(0m, result.AmountDueUsd);
    }

    [Fact]
    public void Settlement_OnePositiveOneNegative_DueIsTheSmallerOfDebtAndClaim()
    {
        // یوسف/فواد: بدهی ۱۱۹٬۰۷۷٫۴۸ و طلب ۱۱۹٬۰۷۷٫۴۷ → مبلغ قابل تسویه ۱۱۹٬۰۷۷٫۴۷.
        var result = PartnershipStatementService.ResolveSettlement([(1, -119_077.48m), (2, 119_077.47m)]);

        Assert.Equal(1, result.DebtorPartnerId);
        Assert.Equal(2, result.CreditorPartnerId);
        Assert.Equal(119_077.47m, result.AmountDueUsd);
    }

    [Fact]
    public void Settlement_BothPartnersPositive_HasNoDirection()
    {
        var result = PartnershipStatementService.ResolveSettlement([(1, 0.50m), (2, 1.49m)]);

        Assert.Null(result.DebtorPartnerId);
        Assert.Null(result.CreditorPartnerId);
        Assert.Equal(0m, result.AmountDueUsd);
    }

    [Fact]
    public async Task CombinedStatement_BothPartnersPositive_ShowsNoDebtorAndNoAmount()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);
        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 23),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            Amount = 414_561.96m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 414_561.96m
        });
        await db.SaveChangesAsync();

        var statement = await BuildAsync(db, s);

        Assert.Equal(0.50m, statement.Totals.Single(t => t.PartnerId == s.FawadId).NetPositionUsd);
        Assert.Equal(1.49m, statement.Totals.Single(t => t.PartnerId == s.YusufId).NetPositionUsd);
        Assert.Null(statement.DebtorPartnerId);
        Assert.Null(statement.CreditorPartnerId);
        Assert.Equal(0m, statement.AmountDueUsd);
        Assert.Equal(1.99m, statement.UnreconciledResidualUsd);
    }

    // ————————————————— G: اثر تسویه روی مانده —————————————————

    [Fact]
    public async Task RecordingSettlement_MovesFinalBalanceByExactlyThatAmount()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = await BuildAsync(db, s);
        Assert.Equal(414_561.46m, before.AmountDueUsd);

        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 23),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            Amount = 100_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100_000m
        });
        await db.SaveChangesAsync();

        var after = await BuildAsync(db, s);
        Assert.Equal(314_561.46m, after.AmountDueUsd);
        Assert.Equal(s.FawadId, after.DebtorPartnerId);
        Assert.Equal(100_000m, after.Totals.Single(t => t.PartnerId == s.FawadId).SettlementsPaidUsd);
        Assert.Equal(100_000m, after.Totals.Single(t => t.PartnerId == s.YusufId).SettlementsReceivedUsd);
        Assert.Single(after.Settlements);
    }

    [Fact]
    public async Task ReversedSettlement_IsKeptInHistoryButDroppedFromBalance()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 23),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            Amount = 100_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100_000m,
            IsReversed = true,
            ReversedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var statement = await BuildAsync(db, s);
        Assert.Single(statement.Settlements);
        Assert.True(statement.Settlements[0].IsReversed);
        Assert.Equal(414_561.46m, statement.AmountDueUsd);
    }

    // ————————————————— H: P&L قرارداد دست‌نخورده —————————————————

    [Fact]
    public async Task PartnerSettlement_DoesNotChangeContractProfitOrExpenses()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = await BuildAsync(db, s);
        var beforeByContract = before.Contracts.ToDictionary(
            c => c.ContractId,
            c => (c.SalesUsd, c.PurchaseCostUsd, c.OperationalExpenseUsd, c.BookProfitUsd));

        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 23),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            ContractId = s.Contract16Id,
            Amount = 250_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 250_000m
        });
        await db.SaveChangesAsync();

        var after = await BuildAsync(db, s);
        foreach (var contract in after.Contracts)
        {
            var expected = beforeByContract[contract.ContractId];
            Assert.Equal(expected.SalesUsd, contract.SalesUsd);
            Assert.Equal(expected.PurchaseCostUsd, contract.PurchaseCostUsd);
            Assert.Equal(expected.OperationalExpenseUsd, contract.OperationalExpenseUsd);
            Assert.Equal(expected.BookProfitUsd, contract.BookProfitUsd);
        }

        // تسویه هیچ سند مصرف یا فروشی نمی‌سازد.
        Assert.Equal(2, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(2, await db.SalesTransactions.CountAsync());
    }

    // ————————————————— I: درآمد دوباره شمرده نشود —————————————————

    [Fact]
    public async Task SaleLinkedByBothLedgerAndSourceContract_IsCountedOnce()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        // همان فروشِ P-017 هم ستون SourcePurchaseContractId دارد و هم ردیف لجر قرارداد.
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 8, 22),
            Side = LedgerSide.Credit,
            AmountUsd = C17SalesUsd,
            Currency = "USD",
            ContractId = s.Contract17Id,
            SourceType = "Sale",
            SourceId = s.Sale17Id,
            Description = "Sale"
        });
        await db.SaveChangesAsync();

        var statement = await BuildAsync(db, s);
        var c17 = statement.Contracts.Single(c => c.ContractId == s.Contract17Id);

        Assert.Equal(1_031_871.00m, c17.SalesUsd);
        Assert.Equal(414_561.46m, statement.AmountDueUsd);
    }

    // ————————————————— J: پرداخت شریک مصرف نشود —————————————————

    [Fact]
    public async Task PartnerFunding_IsNeverCountedAsOperatingExpense()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);
        var c16 = statement.Contracts.Single(c => c.ContractId == s.Contract16Id);
        var c17 = statement.Contracts.Single(c => c.ContractId == s.Contract17Id);

        var expense16 = await db.ExpenseTransactions
            .Where(e => e.ContractId == s.Contract16Id)
            .SumAsync(e => e.AmountUsd);
        var expense17 = await db.ExpenseTransactions
            .Where(e => e.ContractId == s.Contract17Id)
            .SumAsync(e => e.AmountUsd);

        Assert.Equal(decimal.Round(expense16, 2), c16.OperationalExpenseUsd);
        Assert.Equal(decimal.Round(expense17, 2), c17.OperationalExpenseUsd);
        Assert.True(c16.OperationalExpenseUsd < c16.TotalPartnerFundingUsd);
    }

    // ————————————————— مفاد فقط از دفتر می‌آید، نه از پرداخت شرکا —————————————————

    [Fact]
    public async Task ChangingPartnerFunding_WithoutTouchingPurchaseOrExpenses_LeavesBookProfitAndProfitShareUnchanged()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = await BuildAsync(db, s);
        var beforeByContract = before.Contracts.ToDictionary(
            c => c.ContractId,
            c => (c.BookProfitUsd, c.PurchaseCostUsd, c.OperationalExpenseUsd, c.SalesUsd,
                  Shares: c.Partners.ToDictionary(p => p.PartnerId, p => p.ProfitShareUsd)));

        // یک پرداختِ شریکِ کاملاً جدید — بدون هیچ تغییری در بارگیری یا مصارف.
        AddFunding(db, s.Contract16Id, s.FawadId, 50_000m, PaymentKind.ServiceProviderPayment, "پرداخت اضافی شریک");
        await db.SaveChangesAsync();

        var after = await BuildAsync(db, s);
        foreach (var contract in after.Contracts)
        {
            var expected = beforeByContract[contract.ContractId];
            Assert.Equal(expected.SalesUsd, contract.SalesUsd);
            Assert.Equal(expected.PurchaseCostUsd, contract.PurchaseCostUsd);
            Assert.Equal(expected.OperationalExpenseUsd, contract.OperationalExpenseUsd);

            // مفاد و سهم مفاد نباید ذره‌ای تکان بخورند.
            Assert.Equal(expected.BookProfitUsd, contract.BookProfitUsd);
            foreach (var partner in contract.Partners)
            {
                Assert.Equal(expected.Shares[partner.PartnerId], partner.ProfitShareUsd);
            }
        }

        var c16 = after.Contracts.Single(c => c.ContractId == s.Contract16Id);
        // اثرِ پرداختِ جدید فقط در «پرداخت شریک» و در ردیف تطبیق دیده می‌شود.
        Assert.Equal(371_575.00m, PartnerOf(c16, s.FawadId).FundingUsd);
        Assert.Equal(400_730.00m, c16.TotalPartnerFundingUsd);
        Assert.Equal(50_000.01m, c16.PaymentToBookDifferenceUsd);
        Assert.Equal(50_001.99m, after.UnreconciledResidualUsd);
    }

    [Fact]
    public async Task PaymentToBookDifference_IsReportedSeparately_AndNeverFoldedIntoProfit()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s);
        foreach (var contract in statement.Contracts)
        {
            // ردیف تطبیق = جمع پرداخت شرکا منهای هزینهٔ دفتری. مستقل از مفاد.
            Assert.Equal(
                decimal.Round(contract.TotalPartnerFundingUsd - contract.PurchaseCostUsd - contract.OperationalExpenseUsd,
                    2, MidpointRounding.AwayFromZero),
                contract.PaymentToBookDifferenceUsd);
            Assert.NotEqual(0m, contract.PaymentToBookDifferenceUsd);
        }

        Assert.Equal(0.01m, statement.Contracts.Single(c => c.ContractId == s.Contract16Id).PaymentToBookDifferenceUsd);
        Assert.Equal(1.98m, statement.Contracts.Single(c => c.ContractId == s.Contract17Id).PaymentToBookDifferenceUsd);
    }

    // ————————————————— K: اجرای دوباره، نتیجهٔ یکسان و بدون رکورد جدید —————————————————

    [Fact]
    public async Task BuildingStatementTwice_IsIdempotent()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var first = await BuildAsync(db, s);
        var paymentsBefore = await db.PaymentTransactions.CountAsync();
        var salesBefore = await db.SalesTransactions.CountAsync();
        var ledgerBefore = await db.LedgerEntries.CountAsync();

        var second = await BuildAsync(db, s);

        Assert.Equal(first.AmountDueUsd, second.AmountDueUsd);
        Assert.Equal(first.DebtorPartnerId, second.DebtorPartnerId);
        Assert.Equal(paymentsBefore, await db.PaymentTransactions.CountAsync());
        Assert.Equal(salesBefore, await db.SalesTransactions.CountAsync());
        Assert.Equal(ledgerBefore, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task ContractFilter_LimitsStatementToTheSelectedContract()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = await BuildAsync(db, s, [s.Contract16Id]);

        Assert.Single(statement.Contracts);
        Assert.Equal(s.YusufId, statement.DebtorPartnerId);
        Assert.Equal(370_165.49m, statement.AmountDueUsd);
        Assert.Equal(370_165.50m, statement.CreditorClaimUsd);
    }

    // ————————————————— helpers —————————————————

    private static PartnershipPartnerTotals PartnerOf(PartnershipContractStatement contract, int partnerId)
        => contract.Partners.Single(p => p.PartnerId == partnerId);

    private static async Task<PartnershipStatement> BuildAsync(
        ApplicationDbContext db,
        Scenario s,
        IReadOnlyCollection<int>? contractIds = null)
    {
        var statement = await new PartnershipStatementService(db)
            .BuildAsync(s.FawadId, s.YusufId, contractIds);
        Assert.NotNull(statement);
        return statement!;
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Scenario(
        int FawadId,
        int YusufId,
        int Contract16Id,
        int Contract17Id,
        int Sale16Id,
        int Sale17Id);

    private static async Task<Scenario> SeedAsync(ApplicationDbContext db)
    {
        var company = new Company { Code = "PTG", Name = "PTG" };
        var product = new Product { Code = "MO", Name = "Base Oil" };
        var supplier = new Supplier { Name = "Refinery", IsActive = true };
        var customer = new Customer { Name = "Buyer", IsActive = true };
        var fawad = new Partner { Code = "PAR-F", Name = "گروپ کمپنی های فواد صدیقی", IsActive = true };
        var yusuf = new Partner { Code = "PAR-Y", Name = "شرکت یوسف اسماعیل", IsActive = true };
        var cash = new CashAccount { Code = "CASH", Name = "Main Cash", Currency = "USD", IsActive = true };
        var expenseType = new ExpenseType { Code = "OPS", Name = "مصارف قرارداد" };
        db.AddRange(company, product, supplier, customer, fawad, yusuf, cash, expenseType);
        await db.SaveChangesAsync();

        var c16 = NewPartnershipContract(company.Id, product.Id, supplier.Id, "P-016", "500 تن مبلایل شراکتی", 500m);
        var c17 = NewPartnershipContract(company.Id, product.Id, supplier.Id, "P-017", "1318 تن مبلایل شراکتی", 1318.8517m);
        db.Contracts.AddRange(c16, c17);
        await db.SaveChangesAsync();

        // عاید فروش: P-016 نزد یوسف، P-017 نزد فواد.
        c16.SaleProceedsHolderPartnerId = yusuf.Id;
        c17.SaleProceedsHolderPartnerId = fawad.Id;

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = c16.Id, PartnerId = fawad.Id, SharePercent = 50m },
            new ContractPartner { ContractId = c16.Id, PartnerId = yusuf.Id, SharePercent = 50m },
            new ContractPartner { ContractId = c17.Id, PartnerId = fawad.Id, SharePercent = 50m },
            new ContractPartner { ContractId = c17.Id, PartnerId = yusuf.Id, SharePercent = 50m });

        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                ContractId = c16.Id,
                ProductId = product.Id,
                LoadingDate = new DateTime(2026, 3, 20),
                LoadedQuantityMt = 500m,
                LoadingPriceUsd = 556m
            },
            // پنج بچ خریدِ همان قرارداد. در دیتابیس واقعی این ۵۲ بارگیریِ هر موتر است و
            // جمعش چند دهم دالر فرق می‌کند؛ رفتار سرویس یکی است.
            NewLoading(c17.Id, product.Id, 612.316m, 455m),
            NewLoading(c17.Id, product.Id, 302.816m, 450m),
            NewLoading(c17.Id, product.Id, 101.496m, 460m),
            NewLoading(c17.Id, product.Id, 199.898m, 457m),
            NewLoading(c17.Id, product.Id, 102.325m, 480m));

        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                ExpenseTypeId = expenseType.Id,
                ContractId = c16.Id,
                ExpenseDate = new DateTime(2026, 4, 20),
                Amount = 72_729.99m,
                Currency = "USD",
                AmountUsd = 72_729.99m,
                Description = "مصارف قرارداد"
            },
            new ExpenseTransaction
            {
                ExpenseTypeId = expenseType.Id,
                ContractId = c17.Id,
                ExpenseDate = new DateTime(2026, 6, 20),
                Amount = 183_032.39m,
                Currency = "USD",
                AmountUsd = 183_032.39m,
                Description = "مصارف قرارداد"
            });

        // فروش P-016 فقط از راه ردیف لجر به قرارداد وصل است (مثل دادهٔ واقعی گروهی).
        var sale16 = NewSale(company.Id, product.Id, customer.Id, "GSALE-10-1", 500m, C16SalesUsd, null);
        // فروش P-017 ستون SourcePurchaseContractId دارد.
        var sale17 = NewSale(company.Id, product.Id, customer.Id, "GSALE-11-1", 1307.2m, C17SalesUsd, c17.Id);
        db.SalesTransactions.AddRange(sale16, sale17);
        await db.SaveChangesAsync();

        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 8, 22),
            Side = LedgerSide.Credit,
            AmountUsd = C16SalesUsd,
            Currency = "USD",
            ContractId = c16.Id,
            SourceType = "Sale",
            SourceId = sale16.Id,
            Description = "Sale"
        });

        // پرداخت‌های واقعی شرکا — همان تفکیک بیلانس.
        AddFunding(db, c16.Id, fawad.Id, 278_000m, PaymentKind.SupplierPayment, "خرید");
        AddFunding(db, c16.Id, fawad.Id, 18_575m, PaymentKind.ServiceProviderPayment, "مصارف ترکمنستان");
        AddFunding(db, c16.Id, fawad.Id, 25_000m, PaymentKind.ServiceProviderPayment, "کرایه");
        AddFunding(db, c16.Id, yusuf.Id, 27_155m, PaymentKind.ServiceProviderPayment, "گمرک");
        AddFunding(db, c16.Id, yusuf.Id, 2_000m, PaymentKind.ServiceProviderPayment, "شب‌خواب موترها");

        AddFunding(db, c17.Id, yusuf.Id, 278_603.78m, PaymentKind.SupplierPayment, "خرید بچ ۱");
        AddFunding(db, c17.Id, yusuf.Id, 136_267.20m, PaymentKind.SupplierPayment, "خرید بچ ۲");
        AddFunding(db, c17.Id, yusuf.Id, 46_688.16m, PaymentKind.SupplierPayment, "خرید بچ ۳");
        AddFunding(db, c17.Id, yusuf.Id, 91_353.386m, PaymentKind.SupplierPayment, "خرید بچ ۴");
        AddFunding(db, c17.Id, yusuf.Id, 49_116m, PaymentKind.SupplierPayment, "خرید بچ ۵");
        AddFunding(db, c17.Id, yusuf.Id, 35_523.39m, PaymentKind.ServiceProviderPayment, "کرایه تا سرخس");
        AddFunding(db, c17.Id, yusuf.Id, 23_771.98m, PaymentKind.ServiceProviderPayment, "گمرک سرخس");
        AddFunding(db, c17.Id, fawad.Id, 100_200m, PaymentKind.ServiceProviderPayment, "کرایه سرخس تا بخارا");
        AddFunding(db, c17.Id, fawad.Id, 23_539m, PaymentKind.ServiceProviderPayment, "مصارف ازبکستان");

        await db.SaveChangesAsync();

        // دادهٔ ساختگی نگذاریم: مجموع پرداخت‌ها باید همان چیزی باشد که بیلانس واقعی می‌گوید.
        Assert.Equal(C16FawadFunding + C16YusufFunding + C17FawadFunding + C17YusufFunding,
            await db.PaymentTransactions.SumAsync(p => p.AmountUsd));

        return new Scenario(fawad.Id, yusuf.Id, c16.Id, c17.Id, sale16.Id, sale17.Id);
    }

    private static LoadingRegister NewLoading(int contractId, int productId, decimal quantityMt, decimal priceUsd)
        => new()
        {
            ContractId = contractId,
            ProductId = productId,
            LoadingDate = new DateTime(2026, 5, 10),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd
        };

    private static Contract NewPartnershipContract(
        int companyId,
        int productId,
        int supplierId,
        string number,
        string name,
        decimal quantityMt)
        => new()
        {
            ContractNumber = number,
            ContractName = name,
            ContractType = ContractType.Purchase,
            CompanyId = companyId,
            ProductId = productId,
            SupplierId = supplierId,
            OwnershipType = ContractOwnershipType.Partnership,
            Currency = "USD",
            QuantityMt = quantityMt,
            ContractDate = new DateTime(2026, 2, 11)
        };

    private static SalesTransaction NewSale(
        int companyId,
        int productId,
        int customerId,
        string invoice,
        decimal quantityMt,
        decimal totalUsd,
        int? sourcePurchaseContractId)
        => new()
        {
            CompanyId = companyId,
            ProductId = productId,
            CustomerId = customerId,
            InvoiceNumber = invoice,
            SaleDate = new DateTime(2026, 8, 22),
            QuantityMt = quantityMt,
            UnitPriceUsd = quantityMt == 0m ? 0m : totalUsd / quantityMt,
            TotalUsd = totalUsd,
            Currency = "USD",
            TotalInCurrency = totalUsd,
            AppliedFxRateToUsd = 1m,
            SourcePurchaseContractId = sourcePurchaseContractId
        };

    private static void AddFunding(
        ApplicationDbContext db,
        int contractId,
        int partnerId,
        decimal amountUsd,
        PaymentKind kind,
        string description)
        => db.PaymentTransactions.Add(new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 8, 23),
            Direction = PaymentDirection.Out,
            PaymentKind = kind,
            ContractId = contractId,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = partnerId,
            Description = description
        });
}
