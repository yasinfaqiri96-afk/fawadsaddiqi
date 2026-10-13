using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class AccountingCorePostgreSqlTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Corrective_Migration_Splits_Payables_Without_Renaming_Used_History()
    {
        await using var db = fixture.CreateDbContext();

        var unusedCompany = await db.Companies.SingleAsync(
            x => x.Code == AccountingPostgreSqlFixture.UnusedLegacyAccountCompanyCode);
        var unusedEmployee = await db.Accounts.SingleAsync(
            x => x.CompanyId == unusedCompany.Id && x.Code == "2500");
        var unusedAccrued = await db.Accounts.SingleAsync(
            x => x.CompanyId == unusedCompany.Id && x.Code == "2510");
        var unusedSettings = await db.AccountingSettings.SingleAsync(x => x.CompanyId == unusedCompany.Id);
        Assert.Equal("Employee Payable", unusedEmployee.Name);
        Assert.Equal(unusedEmployee.Id, unusedSettings.EmployeePayableAccountId);
        Assert.Equal(unusedAccrued.Id, unusedSettings.AccruedExpenseAccountId);

        var usedCompany = await db.Companies.SingleAsync(
            x => x.Code == AccountingPostgreSqlFixture.UsedLegacyAccountCompanyCode);
        var usedEmployee = await db.Accounts.SingleAsync(
            x => x.CompanyId == usedCompany.Id && x.Code == "2500");
        var usedAccrued = await db.Accounts.SingleAsync(
            x => x.CompanyId == usedCompany.Id && x.Code == "2510");
        var usedSettings = await db.AccountingSettings.SingleAsync(x => x.CompanyId == usedCompany.Id);
        Assert.Equal("Employee/Accrued Payable", usedEmployee.Name);
        Assert.Equal(usedEmployee.Id, usedSettings.EmployeePayableAccountId);
        Assert.Equal(usedAccrued.Id, usedSettings.AccruedExpenseAccountId);
    }

    [Fact]
    public async Task Same_Account_Code_Is_Allowed_For_Different_Companies()
    {
        await using var db = fixture.CreateDbContext();
        var first = await AddCompanyAsync(db);
        var second = await AddCompanyAsync(db);

        db.Accounts.AddRange(NewAccount(first.Id, "1100"), NewAccount(second.Id, "1100"));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Accounts.CountAsync(
            x => x.Code == "1100" && (x.CompanyId == first.Id || x.CompanyId == second.Id)));
    }

    [Fact]
    public async Task Duplicate_Account_Code_In_One_Company_Is_Rejected()
    {
        await using var db = fixture.CreateDbContext();
        var company = await AddCompanyAsync(db);
        db.Accounts.AddRange(NewAccount(company.Id, "1100"), NewAccount(company.Id, "1100"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Overlapping_Fiscal_Years_Are_Rejected_Per_Company()
    {
        await using var db = fixture.CreateDbContext();
        var company = await AddCompanyAsync(db);
        db.FiscalYears.Add(NewYear(company.Id, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "FY-1"));
        await db.SaveChangesAsync();

        db.FiscalYears.Add(NewYear(company.Id, new DateTime(2026, 6, 1), new DateTime(2027, 5, 31), "FY-2"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Same_Fiscal_Range_Is_Allowed_For_Different_Companies()
    {
        await using var db = fixture.CreateDbContext();
        var first = await AddCompanyAsync(db);
        var second = await AddCompanyAsync(db);
        db.FiscalYears.AddRange(
            NewYear(first.Id, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "FY-2026"),
            NewYear(second.Id, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "FY-2026"));

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Period_Outside_Fiscal_Year_Is_Rejected_By_PostgreSql()
    {
        await using var db = fixture.CreateDbContext();
        var company = await AddCompanyAsync(db);
        var year = NewYear(company.Id, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "FY-2026");
        db.FiscalYears.Add(year);
        await db.SaveChangesAsync();

        db.FiscalPeriods.Add(new FiscalPeriod
        {
            CompanyId = company.Id,
            FiscalYearId = year.Id,
            PeriodNumber = 1,
            Name = "Invalid",
            StartDate = new DateTime(2025, 12, 1),
            EndDate = new DateTime(2026, 1, 31),
            Status = FiscalPeriodStatus.Open
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Balanced_Journal_Is_Posted_Successfully()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var journal = await CreatePostingService(db).PostAsync(BalancedRequest(scope));

        Assert.Equal(JournalEntryStatus.Posted, journal.Status);
        Assert.NotNull(journal.PostedAt);
        Assert.Equal(journal.Lines.Sum(x => x.Debit), journal.Lines.Sum(x => x.Credit));
    }

    [Fact]
    public async Task Operational_Asset_Dimension_Is_Persisted_And_Preserved_On_Reversal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var asset = new OperationalAsset
        {
            AssetCode = Unique("ASSET"),
            Name = "Accounting dimension asset",
            AssetType = OperationalAssetType.Truck,
            IsActive = true
        };
        db.OperationalAssets.Add(asset);
        await db.SaveChangesAsync();
        var request = BalancedRequest(scope) with
        {
            Lines =
            [
                new AccountingPostLine(scope.Accounts["5600"].Id, 100m, 0m, "USD", 100m, 1m,
                    OperationalAssetId: asset.Id),
                new AccountingPostLine(scope.Accounts["4400"].Id, 0m, 100m, "USD", 100m, 1m,
                    OperationalAssetId: asset.Id)
            ]
        };
        var service = CreatePostingService(db);

        var original = await service.PostAsync(request);
        var reversal = await service.ReverseAsync(new AccountingReversalRequest(
            original.Id,
            Unique("REV-ASSET"),
            scope.AccountingDate,
            "Tests",
            Unique("asset-reversal")));

        Assert.All(original.Lines, line => Assert.Equal(asset.Id, line.OperationalAssetId));
        Assert.All(reversal.Lines, line => Assert.Equal(asset.Id, line.OperationalAssetId));
        Assert.Equal(0m, original.Lines.Sum(x => x.Debit - x.Credit));
        Assert.Equal(0m, reversal.Lines.Sum(x => x.Debit - x.Credit));
    }

    [Fact]
    public async Task Unbalanced_Journal_Is_Rejected_By_Service()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var request = BalancedRequest(scope) with
        {
            Lines =
            [
                Line(scope, "1100", debit: 100m),
                Line(scope, "4100", credit: 90m)
            ]
        };

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => CreatePostingService(db).PostAsync(request));
        Assert.Equal("UNBALANCED_JOURNAL", error.Code);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(-10, 0)]
    public async Task Invalid_Debit_Credit_Is_Rejected_By_Service(decimal debit, decimal credit)
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var request = BalancedRequest(scope) with
        {
            Lines =
            [
                Line(scope, "1100", debit: debit, credit: credit),
                Line(scope, "4100", credit: 10m)
            ]
        };

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => CreatePostingService(db).PostAsync(request));
        Assert.Equal("INVALID_DEBIT_CREDIT", error.Code);
    }

    [Fact]
    public async Task Account_From_Another_Company_Is_Rejected()
    {
        await using var db = fixture.CreateDbContext();
        // «other» اول ساخته می‌شود تا «scope» آخرین (و تنها) مالک بماند؛ آن‌گاه سندِ متعلق به مالک
        // با یک حسابِ متعلق به شرکت دیگر رد می‌شود — دقیقاً همان چیزی که این تست می‌سنجد.
        var other = await CreatePostingScopeAsync(db);
        var scope = await CreatePostingScopeAsync(db);
        var request = BalancedRequest(scope) with
        {
            Lines =
            [
                Line(scope, "1100", debit: 100m),
                Line(other, "4100", credit: 100m)
            ]
        };

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => CreatePostingService(db).PostAsync(request));
        Assert.Equal("INVALID_ACCOUNT_OWNERSHIP", error.Code);
    }

    [Fact]
    public async Task Posting_In_Closed_Period_Is_Rejected()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        scope.Period.Status = FiscalPeriodStatus.Closed;
        scope.Period.LockedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => CreatePostingService(db).PostAsync(BalancedRequest(scope)));
        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);
    }

    [Fact]
    public async Task Duplicate_Source_Event_Is_Rejected()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var request = BalancedRequest(scope);
        var service = CreatePostingService(db);
        await service.PostAsync(request);

        var duplicate = request with { JournalNumber = Unique("J") };
        var error = await Assert.ThrowsAsync<AccountingValidationException>(() => service.PostAsync(duplicate));
        Assert.Equal("DUPLICATE_SOURCE_EVENT", error.Code);
    }

    [Fact]
    public async Task Posted_Journal_And_Lines_Are_Immutable_In_PostgreSql()
    {
        int journalId;
        await using (var db = fixture.CreateDbContext())
        {
            var scope = await CreatePostingScopeAsync(db);
            journalId = (await CreatePostingService(db).PostAsync(BalancedRequest(scope))).Id;
        }

        await using (var db = fixture.CreateDbContext())
        {
            var journal = await db.JournalEntries.SingleAsync(x => x.Id == journalId);
            journal.Description = "changed";
            await AssertPostgresErrorAsync(() => db.SaveChangesAsync(), "23514");
        }

        await using (var db = fixture.CreateDbContext())
        {
            var journal = await db.JournalEntries.SingleAsync(x => x.Id == journalId);
            db.JournalEntries.Remove(journal);
            await AssertPostgresErrorAsync(() => db.SaveChangesAsync(), "23514");
        }

        await using (var db = fixture.CreateDbContext())
        {
            var line = await db.JournalEntryLines.FirstAsync(x => x.JournalEntryId == journalId);
            line.Description = "changed";
            await AssertPostgresErrorAsync(() => db.SaveChangesAsync(), "23514");
        }
    }

    [Fact]
    public async Task Reversal_Is_Balanced_Exact_And_Only_One_Is_Allowed()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var service = CreatePostingService(db);
        var original = await service.PostAsync(BalancedRequest(scope));
        var reversal = await service.ReverseAsync(new AccountingReversalRequest(
            original.Id,
            Unique("REV"),
            scope.AccountingDate,
            "Tests",
            Unique("reversal-event")));

        Assert.True(reversal.IsReversal);
        Assert.Equal(original.Id, reversal.ReversalOfJournalEntryId);
        Assert.Equal(original.Lines.Sum(x => x.Debit), reversal.Lines.Sum(x => x.Credit));
        Assert.Equal(original.Lines.Sum(x => x.Credit), reversal.Lines.Sum(x => x.Debit));

        var originalLines = original.Lines.OrderBy(x => x.LineNumber).ToArray();
        var reversalLines = reversal.Lines.OrderBy(x => x.LineNumber).ToArray();
        Assert.Equal(originalLines.Length, reversalLines.Length);
        for (var i = 0; i < originalLines.Length; i++)
        {
            Assert.Equal(originalLines[i].Debit, reversalLines[i].Credit);
            Assert.Equal(originalLines[i].Credit, reversalLines[i].Debit);
            Assert.Equal(originalLines[i].AccountId, reversalLines[i].AccountId);
        }

        var secondError = await Assert.ThrowsAsync<AccountingValidationException>(() => service.ReverseAsync(
            new AccountingReversalRequest(
                original.Id,
                Unique("REV"),
                scope.AccountingDate,
                "Tests",
                Unique("reversal-event"))));
        Assert.Equal("JOURNAL_ALREADY_REVERSED", secondError.Code);
    }

    [Fact]
    public async Task Seeder_Is_Idempotent()
    {
        await using var db = fixture.CreateDbContext();
        var company = await AddCompanyAsync(db);
        var seeder = CreateSeeder(db);

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        // ۳۳ = ۲۶ حساب استاندارد + حساب جاری شرکا (۳۳۰۰) + هزینهٔ معاش (۵۷۰۰)
        //      + چهار حساب امانی (۲۶۰۰، ۱۲۱۰، ۴۵۰۰، ۴۵۱۰) + کالای تحویل‌شده در انتظار قیمت (۱۳۲۰).
        Assert.Equal(33, await db.Accounts.CountAsync(x => x.CompanyId == company.Id));
        Assert.Equal(1, await db.AccountingSettings.CountAsync(x => x.CompanyId == company.Id));

        var settings = await db.AccountingSettings.SingleAsync(x => x.CompanyId == company.Id);
        foreach (var (code, accountId) in new[]
                 {
                     ("2600", settings.CustodyFundsPayableAccountId),
                     ("1210", settings.CustodyPendingCollectionAccountId),
                     ("4500", settings.CustodyCommissionRevenueAccountId),
                     ("4510", settings.CustodyServiceRevenueAccountId),
                     ("1320", settings.DeliveredPendingPricingAccountId)
                 })
        {
            Assert.Equal(
                (await db.Accounts.SingleAsync(x => x.CompanyId == company.Id && x.Code == code)).Id,
                accountId);
        }

        // اجرای دوم نه حساب دوباره می‌سازد و نه ارجاعِ پرشده را عوض می‌کند.
        var partnerCurrent = await db.Accounts.SingleAsync(
            x => x.CompanyId == company.Id && x.Code == "3300");
        Assert.Equal(partnerCurrent.Id, settings.PartnerCurrentAccountId);
        var employeePayable = await db.Accounts.SingleAsync(x => x.Id == settings.EmployeePayableAccountId);
        var accruedPayable = await db.Accounts.SingleAsync(x => x.Id == settings.AccruedExpenseAccountId);
        Assert.Equal("2500", employeePayable.Code);
        Assert.Equal("Employee Payable", employeePayable.Name);
        Assert.Equal("2510", accruedPayable.Code);
        Assert.Equal("Accrued Expenses Payable", accruedPayable.Name);
        Assert.NotEqual(employeePayable.Id, accruedPayable.Id);
    }

    [Fact]
    public async Task PostgreSql_Rejects_Unbalanced_Draft_To_Posted_Transition()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var journal = NewDraftJournal(scope,
        [
            NewLine(scope, "1100", debit: 100m),
            NewLine(scope, "4100", credit: 90m)
        ]);
        db.JournalEntries.Add(journal);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"JournalEntries\" SET \"Status\" = 1 WHERE \"Id\" = {0}",
            journal.Id));
    }

    [Fact]
    public async Task PostgreSql_Check_Rejects_Both_Sides_Positive()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreatePostingScopeAsync(db);
        var journal = NewDraftJournal(scope,
        [
            NewLine(scope, "1100", debit: 100m, credit: 1m),
            NewLine(scope, "4100", credit: 100m)
        ]);
        db.JournalEntries.Add(journal);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Disabled_Feature_Flag_Blocks_Posting()
    {
        await using var db = fixture.CreateDbContext();
        var service = new AccountingPostingService(
            db,
            new PeriodGuard(db, new FiscalCalendarService(db)),
            Options.Create(new AccountingOptions { Enabled = false }),
            new SystemCompanyProvider(db));

        var error = await Assert.ThrowsAsync<AccountingValidationException>(() => service.PostAsync(
            new AccountingPostRequest(0, "X", DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, "Tests", [])));
        Assert.Equal("ACCOUNTING_DISABLED", error.Code);
    }

    private static async Task<PostingScope> CreatePostingScopeAsync(ApplicationDbContext db)
    {
        var company = await AddCompanyAsync(db, isOwner: true);
        await CreateSeeder(db).SeedAsync();

        var accountingDate = new DateTime(2026, 7, 15);
        var year = NewYear(company.Id, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), Unique("FY"));
        year.Status = FiscalYearStatus.Open;
        year.IsCurrent = true;
        db.FiscalYears.Add(year);
        await db.SaveChangesAsync();

        var period = new FiscalPeriod
        {
            CompanyId = company.Id,
            FiscalYearId = year.Id,
            PeriodNumber = 7,
            Name = "July 2026",
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 31),
            Status = FiscalPeriodStatus.Open
        };
        db.FiscalPeriods.Add(period);
        await db.SaveChangesAsync();

        var accounts = await db.Accounts
            .Where(x => x.CompanyId == company.Id)
            .ToDictionaryAsync(x => x.Code);
        return new PostingScope(company, year, period, accounts, accountingDate);
    }

    private static AccountingPostRequest BalancedRequest(PostingScope scope)
        => new(
            scope.Company.Id,
            Unique("J"),
            scope.AccountingDate,
            scope.AccountingDate,
            scope.AccountingDate,
            "Tests",
            [
                Line(scope, "1100", debit: 100m),
                Line(scope, "4100", credit: 100m)
            ],
            SourceEventId: Unique("event"));

    private static AccountingPostLine Line(
        PostingScope scope,
        string accountCode,
        decimal debit = 0m,
        decimal credit = 0m)
        => new(
            scope.Accounts[accountCode].Id,
            debit,
            credit,
            "USD",
            debit + credit,
            1m);

    private static JournalEntry NewDraftJournal(
        PostingScope scope,
        IReadOnlyCollection<JournalEntryLine> lines)
    {
        var numberedLines = lines.Select((line, index) =>
        {
            line.LineNumber = index + 1;
            return line;
        }).ToList();

        return new JournalEntry
        {
            CompanyId = scope.Company.Id,
            FiscalYearId = scope.Year.Id,
            FiscalPeriodId = scope.Period.Id,
            JournalNumber = Unique("DIRECT"),
            Status = JournalEntryStatus.Draft,
            AccountingDate = scope.AccountingDate,
            DocumentDate = scope.AccountingDate,
            OperationDate = scope.AccountingDate,
            SourceModule = "Tests",
            Lines = numberedLines
        };
    }

    private static async Task AssertPostgresErrorAsync(Func<Task> action, string sqlState)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        var current = error;
        while (current is not null)
        {
            if (current is PostgresException postgres)
            {
                Assert.Equal(sqlState, postgres.SqlState);
                return;
            }

            current = current.InnerException;
        }

        throw new Xunit.Sdk.XunitException($"Expected PostgreSQL SQLSTATE {sqlState}, got {error.GetType().Name}.");
    }

    private static JournalEntryLine NewLine(
        PostingScope scope,
        string accountCode,
        decimal debit = 0m,
        decimal credit = 0m)
        => new()
        {
            AccountId = scope.Accounts[accountCode].Id,
            Debit = debit,
            Credit = credit,
            TransactionCurrencyCode = "USD",
            TransactionAmount = debit + credit,
            ExchangeRate = 1m
        };

    private static IAccountingPostingService CreatePostingService(ApplicationDbContext db)
        => new AccountingPostingService(
            db,
            new PeriodGuard(db, new FiscalCalendarService(db)),
            Options.Create(new AccountingOptions { Enabled = true }),
            new SystemCompanyProvider(db));

    private static IAccountingChartSeeder CreateSeeder(ApplicationDbContext db)
        => new AccountingChartSeeder(
            db,
            Options.Create(new AccountingOptions
            {
                Enabled = false,
                DefaultFunctionalCurrencyCode = "USD"
            }));

    // isOwner فقط برای سناریوهای تک‌شرکتیِ Posting؛ تست‌های چندشرکتی مالک نمی‌سازند تا ایندکسِ
    // یکتای جزئیِ IsSystemOwner (حداکثر یک مالک) نقض نشود.
    private static async Task<Company> AddCompanyAsync(ApplicationDbContext db, bool isOwner = false)
    {
        var company = new Company
        {
            Code = Unique("C"),
            Name = Unique("Company"),
            Country = "AF",
            IsActive = true,
            IsSystemOwner = isOwner
        };
        if (isOwner)
        {
            // دیتابیسِ مشترکِ مجموعه: مالکِ قبلی را demote کن تا قیدِ «حداکثر یک مالک» نقض نشود.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Companies\" SET \"IsSystemOwner\" = FALSE WHERE \"IsSystemOwner\" = TRUE");
        }
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static Account NewAccount(int companyId, string code)
        => new()
        {
            CompanyId = companyId,
            Code = code,
            Name = code,
            AccountType = AccountType.Asset,
            NormalBalance = NormalBalance.Debit,
            IsActive = true
        };

    private static FiscalYear NewYear(int companyId, DateTime start, DateTime end, string name)
        => new()
        {
            CompanyId = companyId,
            Name = name,
            StartDate = start,
            EndDate = end,
            Status = FiscalYearStatus.Draft
        };

    private static string Unique(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, prefix.Length + 33)];

    private sealed record PostingScope(
        Company Company,
        FiscalYear Year,
        FiscalPeriod Period,
        IReadOnlyDictionary<string, Account> Accounts,
        DateTime AccountingDate);
}
