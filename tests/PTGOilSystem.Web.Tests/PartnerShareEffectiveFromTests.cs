using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG ۱۲-B — «سهم‌های جدید از چه تاریخی؟».
///
/// سناریوی مرجعِ خودِ فاز: ۵۰/۵۰ از جنوری، و ۸۰/۲۰ از اول جولای. گزارشِ جنوری تا جون باید
/// دقیقاً همان ۵۰/۵۰ بماند و فقط از جولای به بعد ۸۰/۲۰ شود. تاریخِ آینده هم نباید محاسبهٔ
/// امروز را تکان بدهد.
/// </summary>
public sealed class PartnerShareEffectiveFromTests
{
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>«امروزِ کاری» ثابت، تا آزمون به تاریخ اجرای واقعی وابسته نباشد.</summary>
    private sealed class FixedClock : IAfghanistanBusinessClock
    {
        public DateTime Today => PartnerShareEffectiveFromTests.Today;

        public DateTimeOffset Now => new(PartnerShareEffectiveFromTests.Today, TimeSpan.Zero);

        public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
            => (localDate.Date, localDate.Date.AddDays(1));
    }

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"share-effective-{Guid.NewGuid():N}")
            .Options;

    private static ContractsController BuildController(ApplicationDbContext db)
        => new(
            db,
            new AuditService(db),
            new CurrencyConversionService(new PricingService(db)),
            new FormTokenGuard(db),
            purchaseAccounting: null,
            businessClock: new FixedClock())
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static async Task SeedFiftyFiftyAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", ConversionFactorToBase = 1m, IsBaseUnit = true });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "S1", Name = "تأمین‌کننده", IsActive = true });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Partners.Add(new Partner { Id = 1, Code = "P1", Name = "شریک اول", IsActive = true });
        db.Partners.Add(new Partner { Id = 2, Code = "P2", Name = "شریک دوم", IsActive = true });

        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractName = "قرارداد شراکتی",
            ContractNumber = "PUR-100",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            QuantityMt = 1000m,
            ContractDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            OwnershipType = ContractOwnershipType.Partnership,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m,
            UnitId = 1,
            SupplierId = 1,
        });

        db.ContractPartners.Add(new ContractPartner
        {
            Id = 1,
            ContractId = 1,
            PartnerId = 1,
            SharePercent = 50m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.ContractPartners.Add(new ContractPartner
        {
            Id = 2,
            ContractId = 1,
            PartnerId = 2,
            SharePercent = 50m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        await db.SaveChangesAsync();
    }

    private static ContractFormViewModel EightyTwenty(DateTime? effectiveFrom)
        => new()
        {
            Id = 1,
            ContractName = "قرارداد شراکتی",
            ContractNumber = "PUR-100",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            QuantityMt = 1000m,
            ContractDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            OwnershipType = ContractOwnershipType.Partnership,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m,
            UnitPriceInCurrency = 500m,
            UnitId = 1,
            SupplierId = 1,
            Currency = "USD",
            PartnerSharesEffectiveFrom = effectiveFrom,
            PartnerShares =
            [
                new ContractPartnerShareInput { PartnerId = 1, SharePercent = 80m },
                new ContractPartnerShareInput { PartnerId = 2, SharePercent = 20m },
            ],
        };

    [Fact]
    public async Task A_Future_Date_Opens_A_Future_Period_And_Leaves_Today_Untouched()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        await SeedFiftyFiftyAsync(db);

        var july = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await BuildController(db).Edit(1, EightyTwenty(july));

        Assert.IsType<RedirectToActionResult>(result);

        var slices = await db.ContractPartners.AsNoTracking()
            .Where(cp => cp.ContractId == 1)
            .OrderBy(cp => cp.EffectiveFrom).ThenBy(cp => cp.PartnerId)
            .ToListAsync();

        Assert.Equal(4, slices.Count);

        // بازهٔ جنوری تا جولای، دقیقاً ۵۰/۵۰ و دست‌نخورده.
        var january = slices.Where(s => s.EffectiveFrom.Date == new DateTime(2026, 1, 1)).ToList();
        Assert.Equal(2, january.Count);
        Assert.All(january, s => Assert.Equal(50m, s.SharePercent));
        Assert.All(january, s => Assert.Equal(july.Date, s.EffectiveTo!.Value.Date));

        // بازهٔ جولای به بعد، ۸۰/۲۰ و هنوز باز.
        var fromJuly = slices.Where(s => s.EffectiveFrom.Date == july.Date).ToList();
        Assert.Equal(2, fromJuly.Count);
        Assert.Equal(80m, fromJuly.Single(s => s.PartnerId == 1).SharePercent);
        Assert.Equal(20m, fromJuly.Single(s => s.PartnerId == 2).SharePercent);
        Assert.All(fromJuly, s => Assert.Null(s.EffectiveTo));

        // «امروز» ۱۵ جون است، پس هنوز داخل بازهٔ ۵۰/۵۰.
        var effectiveToday = slices.Single(s =>
            s.PartnerId == 1
            && s.EffectiveFrom.Date <= Today.Date
            && (s.EffectiveTo == null || s.EffectiveTo.Value.Date > Today.Date));
        Assert.Equal(50m, effectiveToday.SharePercent);
    }

    [Fact]
    public async Task No_Date_Still_Means_Today()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        await SeedFiftyFiftyAsync(db);

        Assert.IsType<RedirectToActionResult>(await BuildController(db).Edit(1, EightyTwenty(effectiveFrom: null)));

        var newest = await db.ContractPartners.AsNoTracking()
            .Where(cp => cp.ContractId == 1)
            .MaxAsync(cp => cp.EffectiveFrom);

        Assert.Equal(Today.Date, newest.Date);
    }

    [Fact]
    public async Task A_Date_Before_The_Latest_Period_Is_Refused_Instead_Of_Rewriting_History()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        await SeedFiftyFiftyAsync(db);

        var controller = BuildController(db);
        var result = await controller.Edit(1, EightyTwenty(new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[nameof(ContractFormViewModel.PartnerSharesEffectiveFrom)]!.Errors,
            e => e.ErrorMessage.Contains("بازهٔ سهم"));

        // و هیچ چیزی تغییر نکرده است.
        var slices = await db.ContractPartners.AsNoTracking().Where(cp => cp.ContractId == 1).ToListAsync();
        Assert.Equal(2, slices.Count);
        Assert.All(slices, s => Assert.Equal(50m, s.SharePercent));
    }

    [Fact]
    public async Task A_Date_Inside_A_Closed_Operational_Period_Is_Refused()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        await SeedFiftyFiftyAsync(db);

        db.OperationalPeriodLocks.Add(new OperationalPeriodLock
        {
            LockedThroughDate = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
            Reason = "بستن ماه",
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Edit(1, EightyTwenty(new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)));

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[nameof(ContractFormViewModel.PartnerSharesEffectiveFrom)]!.Errors,
            e => e.ErrorMessage.Contains("بسته است"));
    }

    /// <summary>
    /// دو بار ویرایش در همان روز نباید دو بازهٔ هم‌روز بسازد — وگرنه «۱۰۰٪ در هر بازه» می‌شکند.
    /// </summary>
    [Fact]
    public async Task Two_Edits_On_The_Same_Effective_Date_Overwrite_That_Period_In_Place()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        await SeedFiftyFiftyAsync(db);

        var july = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        await BuildController(db).Edit(1, EightyTwenty(july));

        var second = EightyTwenty(july);
        second.PartnerShares[0].SharePercent = 70m;
        second.PartnerShares[1].SharePercent = 30m;
        await BuildController(db).Edit(1, second);

        var fromJuly = await db.ContractPartners.AsNoTracking()
            .Where(cp => cp.ContractId == 1 && cp.EffectiveFrom.Date == july.Date)
            .ToListAsync();

        Assert.Equal(2, fromJuly.Count);
        Assert.Equal(100m, fromJuly.Sum(s => s.SharePercent));
        Assert.Equal(70m, fromJuly.Single(s => s.PartnerId == 1).SharePercent);
    }

    /// <summary>
    /// شریکی که جای شریک دیگر می‌آید نباید در بازهٔ پیش از آمدنش سهم بگیرد، و شریکِ رفته در بازهٔ
    /// بعد. پیش از این هر شریک جدا به نخستین بازهٔ خودش برمی‌گشت و جمعِ سهم‌ها ۱۵۰٪ می‌شد.
    /// </summary>
    [Fact]
    public void A_Replaced_Partner_Never_Pushes_The_Shares_Of_A_Period_Above_100()
    {
        var october3 = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var october15 = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        var history = ContractPartnerShareHistory.FromSlices(
        [
            new ContractPartnerShareSlice(5, PartnerId: 1, 50m, october3, october15),
            new ContractPartnerShareSlice(5, PartnerId: 8, 50m, october3, october15),
            new ContractPartnerShareSlice(5, PartnerId: 1, 50m, october15, null),
            new ContractPartnerShareSlice(5, PartnerId: 2, 50m, october15, null),
        ]);

        var before = history.SharesOn(5, october3.AddDays(2));
        Assert.Equal([1, 8], before.Select(s => s.PartnerId).Order());
        Assert.Equal(100m, before.Sum(s => s.SharePercent));

        var after = history.SharesOn(5, october15.AddDays(5));
        Assert.Equal([1, 2], after.Select(s => s.PartnerId).Order());
        Assert.Equal(100m, after.Sum(s => s.SharePercent));

        // تاریخِ پیش از نخستین بازه همان بازهٔ اول را می‌گیرد، نه ترکیبی از همه.
        Assert.Equal(100m, history.SharesOn(5, october3.AddMonths(-1)).Sum(s => s.SharePercent));

        Assert.Equal(0m, history.ShareFor(5, partnerId: 2, october3.AddDays(2)));
        Assert.Equal(0m, history.ShareFor(5, partnerId: 8, october15.AddDays(5)));
        Assert.Equal(50m, history.ShareFor(5, partnerId: 8, october3.AddDays(2)));
    }

    [Fact]
    public async Task Correct_From_Start_Replaces_Every_Period_With_The_New_Partners()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        await SeedFiftyFiftyAsync(db);
        db.Partners.Add(new Partner { Id = 3, Code = "P3", Name = "شریک درست", IsActive = true });
        await db.SaveChangesAsync();

        // اول یک ویرایش عادی که بازهٔ تازه باز می‌کند، بعد اصلاحِ اشتباه از ابتدا.
        await BuildController(db).Edit(1, EightyTwenty(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
        var correction = EightyTwenty(effectiveFrom: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        correction.PartnerSharesCorrectFromStart = true;
        correction.PartnerShares[1].PartnerId = 3;

        Assert.IsType<RedirectToActionResult>(await BuildController(db).Edit(1, correction));

        var slices = await db.ContractPartners.AsNoTracking()
            .Where(cp => cp.ContractId == 1)
            .OrderBy(cp => cp.PartnerId)
            .ToListAsync();

        Assert.Equal([1, 3], slices.Select(s => s.PartnerId));
        Assert.All(slices, s => Assert.Equal(new DateTime(2026, 1, 1), s.EffectiveFrom.Date));
        Assert.All(slices, s => Assert.Null(s.EffectiveTo));
        Assert.Equal(80m, slices[0].SharePercent);
        Assert.Equal(20m, slices[1].SharePercent);
    }

    [Fact]
    public async Task Correct_From_Start_Is_Refused_When_The_Removed_Partner_Has_A_Settlement_On_The_Contract()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        await SeedFiftyFiftyAsync(db);
        db.Partners.Add(new Partner { Id = 3, Code = "P3", Name = "شریک درست", IsActive = true });
        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            FromPartnerId = 2,
            ToPartnerId = 1,
            ContractId = 1,
            Amount = 1000m,
            AmountUsd = 1000m,
        });
        await db.SaveChangesAsync();

        var correction = EightyTwenty(effectiveFrom: null);
        correction.PartnerSharesCorrectFromStart = true;
        correction.PartnerShares[1].PartnerId = 3;

        var controller = BuildController(db);
        Assert.IsType<ViewResult>(await controller.Edit(1, correction));
        Assert.Contains(
            controller.ModelState[nameof(ContractFormViewModel.PartnerSharesCorrectFromStart)]!.Errors,
            e => e.ErrorMessage.Contains("تسویه"));

        var slices = await db.ContractPartners.AsNoTracking().Where(cp => cp.ContractId == 1).ToListAsync();
        Assert.Equal([1, 2], slices.Select(s => s.PartnerId).Order());
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
