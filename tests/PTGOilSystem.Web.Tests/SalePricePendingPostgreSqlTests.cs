using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// فروش با قیمت باز روی PostgreSQL واقعی: بهای تحویل در روزِ تحویل قفل می‌شود و میانگینِ بعدی
/// آن را عوض نمی‌کند، تعیین قیمت دقیقاً یک‌بار و اتمی ثبت می‌شود، و لغو هر دو دفتر را برمی‌گرداند.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class SalePricePendingPostgreSqlTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime SaleDate = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Delivery_Cost_Is_Locked_At_Delivery_And_Recognized_Unchanged_When_Priced_Later()
    {
        await using var db = fixture.CreateDbContext();
        var setup = await SeedAsync(db);
        var accounting = BuildAccounting(db, enabled: true);

        var created = await BuildController(db, accounting).Create(PendingModel(setup, 30m));
        Assert.IsType<RedirectToActionResult>(created);
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.InvoiceNumber == setup.Invoice);
        Assert.True(sale.IsSalePricePending);
        Assert.Empty(await SaleLedgerAsync(db, sale.Id));

        // تحویل: بهای ۳۰ تن به میانگینِ همان روز (۵۰۰) از موجودی به «تحویل‌شده در انتظار قیمت».
        var delivered = await JournalAsync(db, setup, SalesAccountingAdapter.BuildDeliveredCostSourceEventId(sale.Id));
        Assert.NotNull(delivered);
        Assert.Equal(15_000m, Debit(delivered!, setup.Settings.DeliveredPendingPricingAccountId!.Value));
        Assert.Equal(15_000m, Credit(delivered!, setup.Settings.InventoryAccountId));
        Assert.Null(await JournalAsync(db, setup, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id)));
        Assert.Null(await JournalAsync(db, setup, SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id)));
        Assert.Equal((70m, 35_000m), await PoolAsync(db, setup));

        // ورود بعدی با بهای دیگر میانگین را عوض می‌کند (۵۰۰ → ۶۰۰ برای ۱۷۰ تن).
        await new InventoryValuationService(db).ApplyReceiptAsync(
            setup.Scope.Company.Id, setup.Scope.Product.Id, setup.Scope.Terminal.Id, 100m, 67_000m);
        await db.SaveChangesAsync();
        Assert.Equal((170m, 102_000m), await PoolAsync(db, setup));

        var priced = await BuildController(db, accounting).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 650m
        });
        Assert.IsType<RedirectToActionResult>(priced);

        var ledger = Assert.Single(await SaleLedgerAsync(db, sale.Id));
        Assert.Equal(19_500m, ledger.AmountUsd);
        var revenue = await JournalAsync(db, setup, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        Assert.Equal(19_500m, Credit(revenue!, setup.Settings.SalesRevenueAccountId));
        // بهای تمام‌شده همان ۱۵٬۰۰۰ روزِ تحویل است، نه ۳۰ × ۶۰۰ = ۱۸٬۰۰۰ روزِ قیمت‌گذاری.
        var cogs = await JournalAsync(db, setup, SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id));
        Assert.Equal(15_000m, Debit(cogs!, setup.Settings.CostOfGoodsSoldAccountId));
        Assert.Equal(15_000m, Credit(cogs!, setup.Settings.DeliveredPendingPricingAccountId!.Value));
        Assert.Equal((170m, 102_000m), await PoolAsync(db, setup));
        Assert.Equal(0m, await AccountNetAsync(db, setup, sale.Id, setup.Settings.DeliveredPendingPricingAccountId!.Value));

        // لغو پس از قیمت‌گذاری: عاید، بها و سند تحویل برمی‌گردند؛ Pool همان ۳۰ تن × ۵۰۰ را پس می‌گیرد.
        var fresh = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        var cancelled = await BuildController(db, accounting).Cancel(fresh.Id, "مشتری بار را پس داد", version: fresh.Version);
        Assert.IsType<RedirectToActionResult>(cancelled);
        Assert.True((await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == sale.Id)).IsCancelled);
        Assert.Equal((200m, 117_000m), await PoolAsync(db, setup));
        Assert.Equal(0m, await AccountNetAsync(db, setup, sale.Id, setup.Settings.DeliveredPendingPricingAccountId!.Value));
        Assert.Equal(0m, await AccountNetAsync(db, setup, sale.Id, setup.Settings.CostOfGoodsSoldAccountId));
        Assert.Equal(0m, await AccountNetAsync(db, setup, sale.Id, setup.Settings.SalesRevenueAccountId));
        Assert.Equal(0m, (await SaleLedgerAsync(db, sale.Id)).Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd));
    }

    [Fact]
    public async Task Cancelling_Before_Pricing_Returns_The_Delivery_Cost_And_Posts_No_Revenue()
    {
        await using var db = fixture.CreateDbContext();
        var setup = await SeedAsync(db);
        var accounting = BuildAccounting(db, enabled: true);
        await BuildController(db, accounting).Create(PendingModel(setup, 40m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.InvoiceNumber == setup.Invoice);

        await BuildController(db, accounting).Cancel(sale.Id, "لغو قبل از قیمت", version: sale.Version);

        Assert.Equal((100m, 50_000m), await PoolAsync(db, setup));
        Assert.NotNull(await JournalAsync(db, setup, SalesAccountingAdapter.BuildDeliveredCostReversedSourceEventId(sale.Id)));
        Assert.Null(await JournalAsync(db, setup, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id)));
        Assert.Null(await JournalAsync(db, setup, SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id)));
        Assert.Empty(await SaleLedgerAsync(db, sale.Id));
        Assert.Equal(0m, await AccountNetAsync(db, setup, sale.Id, setup.Settings.DeliveredPendingPricingAccountId!.Value));
        Assert.Equal(100m, await new StockService(db).GetFreeQuantityMtAsync(
            setup.Scope.Product.Id, terminalId: setup.Scope.Terminal.Id, storageTankId: setup.Scope.Tank.Id));
    }

    [Fact]
    public async Task With_Accounting_Off_Only_The_Legacy_Ledger_Row_Is_Written_At_Pricing()
    {
        await using var db = fixture.CreateDbContext();
        var setup = await SeedAsync(db);
        var accounting = BuildAccounting(db, enabled: false);
        await BuildController(db, accounting).Create(PendingModel(setup, 10m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.InvoiceNumber == setup.Invoice);

        await BuildController(db, accounting).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });

        Assert.Equal(5_000m, Assert.Single(await SaleLedgerAsync(db, sale.Id)).AmountUsd);
        Assert.False(await db.JournalEntries.AnyAsync(j => j.SourceModule == SalesAccountingAdapter.SourceModule
            && j.SourceEntityType == SalesAccountingAdapter.SourceEntityType && j.SourceEntityId == sale.Id));
        Assert.Equal((100m, 50_000m), await PoolAsync(db, setup));
    }

    [Fact]
    public async Task Two_Users_Pricing_The_Same_Sale_At_Once_Post_Exactly_One_Set_Of_Documents()
    {
        Setup setup;
        int saleId;
        long version;
        await using (var db = fixture.CreateDbContext())
        {
            setup = await SeedAsync(db);
            await BuildController(db, BuildAccounting(db, enabled: true)).Create(PendingModel(setup, 20m));
            var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.InvoiceNumber == setup.Invoice);
            (saleId, version) = (sale.Id, sale.Version);
        }

        async Task<IActionResult> PriceAsync(decimal unitPrice)
        {
            await using var db = fixture.CreateDbContext();
            return await BuildController(db, BuildAccounting(db, enabled: true)).SetPrice(saleId, new SalePriceFixViewModel
            {
                Version = version,
                Currency = "USD",
                UnitPriceInCurrency = unitPrice
            });
        }

        var results = await Task.WhenAll(PriceAsync(500m), PriceAsync(900m));
        Assert.All(results, r => Assert.IsAssignableFrom<IActionResult>(r));

        await using var check = fixture.CreateDbContext();
        var priced = await check.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == saleId);
        Assert.False(priced.IsSalePricePending);
        Assert.Contains(priced.TotalUsd, new[] { 10_000m, 18_000m });
        var ledger = Assert.Single(await SaleLedgerAsync(check, saleId));
        Assert.Equal(priced.TotalUsd, ledger.AmountUsd);
        Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.SourceModule == SalesAccountingAdapter.SourceModule
            && j.SourceEventId == SalesAccountingAdapter.BuildCreatedSourceEventId(saleId)));
        Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.SourceModule == SalesAccountingAdapter.SourceModule
            && j.SourceEventId == SalesAccountingAdapter.BuildCogsSourceEventId(saleId)));
    }

    [Fact]
    public async Task Pricing_And_Cancelling_At_Once_Leave_The_Sale_Consistent()
    {
        Setup setup;
        int saleId;
        long version;
        await using (var db = fixture.CreateDbContext())
        {
            setup = await SeedAsync(db);
            await BuildController(db, BuildAccounting(db, enabled: true)).Create(PendingModel(setup, 20m));
            var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.InvoiceNumber == setup.Invoice);
            (saleId, version) = (sale.Id, sale.Version);
        }

        async Task PriceAsync()
        {
            await using var db = fixture.CreateDbContext();
            await BuildController(db, BuildAccounting(db, enabled: true)).SetPrice(saleId, new SalePriceFixViewModel
            {
                Version = version,
                Currency = "USD",
                UnitPriceInCurrency = 500m
            });
        }

        async Task CancelAsync()
        {
            await using var db = fixture.CreateDbContext();
            // بدترین حالت: فرم لغو نسخه نفرستاده است؛ فقط قفل سطری و خواندن دوباره محافظ است.
            await BuildController(db, BuildAccounting(db, enabled: true)).Cancel(saleId, "لغو هم‌زمان", version: 0);
        }

        await Task.WhenAll(PriceAsync(), CancelAsync());

        await using var check = fixture.CreateDbContext();
        var final = await check.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == saleId);
        var ledger = await SaleLedgerAsync(check, saleId);
        var netLedger = ledger.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
        if (final.IsCancelled)
        {
            // لغو برنده شد (پیش یا پس از قیمت‌گذاری): هیچ طلب/عایدی باقی نمی‌ماند و بار برگشته است.
            Assert.Equal(0m, netLedger);
            Assert.Equal(0m, await AccountNetAsync(check, setup, saleId, setup.Settings.SalesRevenueAccountId));
            Assert.Equal(0m, await AccountNetAsync(check, setup, saleId, setup.Settings.DeliveredPendingPricingAccountId!.Value));
            Assert.Equal(100m, await new StockService(check).GetFreeQuantityMtAsync(
                setup.Scope.Product.Id, terminalId: setup.Scope.Terminal.Id, storageTankId: setup.Scope.Tank.Id));
        }
        else
        {
            Assert.False(final.IsSalePricePending);
            Assert.Equal(10_000m, netLedger);
            Assert.Single(ledger);
        }
    }

    [Fact]
    public async Task A_Failure_After_The_Ledger_Row_Rolls_The_Whole_Pricing_Back()
    {
        await using var db = fixture.CreateDbContext();
        var setup = await SeedAsync(db);
        var real = BuildAccounting(db, enabled: true);
        await BuildController(db, real).Create(PendingModel(setup, 20m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.InvoiceNumber == setup.Invoice);

        await using var failingDb = fixture.CreateDbContext();
        var result = await BuildController(failingDb, new FailingRevenueAdapter(BuildAccounting(failingDb, enabled: true)))
            .SetPrice(sale.Id, new SalePriceFixViewModel { Version = sale.Version, Currency = "USD", UnitPriceInCurrency = 500m });

        Assert.IsType<ViewResult>(result);
        await using var check = fixture.CreateDbContext();
        var after = await check.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.True(after.IsSalePricePending);
        Assert.Equal(0m, after.TotalUsd);
        Assert.Null(after.SalePriceFixedAtUtc);
        Assert.Empty(await SaleLedgerAsync(check, sale.Id));
        Assert.Equal(0m, (await check.SalesTransactionSourceAllocations.AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id).ToListAsync()).Sum(a => a.AmountUsd));

        // تلاش دوباره پس از رفع خطا، همان اسناد را یک‌بار می‌سازد.
        await using var retryDb = fixture.CreateDbContext();
        await BuildController(retryDb, BuildAccounting(retryDb, enabled: true))
            .SetPrice(sale.Id, new SalePriceFixViewModel { Version = sale.Version, Currency = "USD", UnitPriceInCurrency = 500m });
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(10_000m, Assert.Single(await SaleLedgerAsync(verify, sale.Id)).AmountUsd);
    }

    [Fact]
    public async Task A_Repeated_Pricing_Request_Changes_Nothing()
    {
        await using var db = fixture.CreateDbContext();
        var setup = await SeedAsync(db);
        var accounting = BuildAccounting(db, enabled: true);
        await BuildController(db, accounting).Create(PendingModel(setup, 10m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.InvoiceNumber == setup.Invoice);
        var token = Guid.NewGuid().ToString("N");

        var model = new SalePriceFixViewModel { Version = sale.Version, Currency = "USD", UnitPriceInCurrency = 500m };
        await BuildController(db, accounting).SetPrice(sale.Id, model, token);
        await using var retry = fixture.CreateDbContext();
        var second = await BuildController(retry, BuildAccounting(retry, enabled: true)).SetPrice(sale.Id, model, token);

        Assert.IsType<RedirectToActionResult>(second);
        await using var check = fixture.CreateDbContext();
        Assert.Single(await SaleLedgerAsync(check, sale.Id));
    }

    [Fact]
    public async Task Rolling_Back_The_Migration_Is_Refused_While_A_Sale_Awaits_Its_Price()
    {
        var databaseName = $"{DatabaseSafetyGuard.AccountingTestDatabasePrefix}{Guid.NewGuid():N}";
        DatabaseSafetyGuard.EnsureIntegrationTestCreateAllowed(databaseName);
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" }.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString)
                .Options;
            await using var db = new ApplicationDbContext(options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync();

            var previous = db.Database.GetMigrations().TakeWhile(m => !m.EndsWith("_AddSalePricePending", StringComparison.Ordinal)).Last();
            await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO "Companies" ("Code","Name","Country","IsActive","IsSystemOwner","CreatedAtUtc") VALUES ('MG','MG','AF',TRUE,FALSE,NOW());
                    INSERT INTO "Customers" ("Name","IsActive","CreatedAtUtc") VALUES ('MG',TRUE,NOW());
                    INSERT INTO "Products" ("Code","Name","UnitOfMeasure","IsActive","CreatedAtUtc") VALUES ('MG','MG','MT',TRUE,NOW());
                    INSERT INTO "SalesTransactions" ("CompanyId","CustomerId","ProductId","SaleStage","InvoiceNumber","SaleDate","QuantityMt","Currency",
                        "UnitPriceInCurrency","UnitPriceUsd","TotalInCurrency","TotalUsd","IsCancelled","IsSalePricePending","CreatedAtUtc")
                    SELECT c."Id", cu."Id", p."Id", 2, 'MG-1', NOW(), 5, 'USD', 0, 0, 0, 0, FALSE, TRUE, NOW()
                    FROM "Companies" c, "Customers" cu, "Products" p;
                    """, connection);
                await seed.ExecuteNonQueryAsync();
            }

            var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(previous));
            Assert.Contains("awaiting a sale price", refused.ToString());
            Assert.Contains("20261012160942_AddSalePricePending", await db.Database.GetAppliedMigrationsAsync());

            await db.Database.ExecuteSqlRawAsync("UPDATE \"SalesTransactions\" SET \"IsSalePricePending\" = FALSE");
            await migrator.MigrateAsync(previous);
            Assert.DoesNotContain("20261012160942_AddSalePricePending", await db.Database.GetAppliedMigrationsAsync());
            await migrator.MigrateAsync();
        }
        finally
        {
            DatabaseSafetyGuard.EnsureIntegrationTestDropAllowed(databaseName);
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // ------------------------------------------------------------------

    private sealed record Setup(PaymentAccountingAdapterTests.PaymentScope Scope, AccountingSettings Settings, string Invoice);

    private static async Task<Setup> SeedAsync(ApplicationDbContext db)
    {
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = scope.Product.Id,
            ContractId = scope.Contract.Id,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            Direction = MovementDirection.In,
            MovementDate = SaleDate.AddDays(-5),
            QuantityMt = 100m,
            ReferenceDocument = PaymentAccountingAdapterTests.Unique("GRN")
        });
        await db.SaveChangesAsync();
        await new InventoryValuationService(db).ApplyReceiptAsync(
            scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 100m, 50_000m);
        await db.SaveChangesAsync();
        var settings = await db.AccountingSettings.AsNoTracking().SingleAsync(x => x.CompanyId == scope.Company.Id);
        Assert.NotNull(settings.DeliveredPendingPricingAccountId);
        return new Setup(scope, settings, PaymentAccountingAdapterTests.Unique("INV"));
    }

    private static SalesCreateViewModel PendingModel(Setup setup, decimal quantity) => new()
    {
        SaleStage = SaleStage.TerminalStock,
        CompanyId = setup.Scope.Company.Id,
        CustomerId = setup.Scope.Customer.Id,
        ProductId = setup.Scope.Product.Id,
        SourceTerminalId = setup.Scope.Terminal.Id,
        SourceStorageTankId = setup.Scope.Tank.Id,
        SaleDate = SaleDate,
        QuantityMt = quantity,
        Currency = "USD",
        IsSalePricePending = true,
        InvoiceNumber = setup.Invoice
    };

    private static ISalesAccountingAdapter BuildAccounting(ApplicationDbContext db, bool enabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = enabled,
            Pilots = new AccountingPilotOptions { Sale = enabled, Cogs = enabled }
        });
        var posting = new AccountingPostingService(
            db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db));
        return new SalesAccountingAdapter(
            db, posting, new AccountingJournalNumberGenerator(), new InventoryValuationService(db), options,
            NullLogger<SalesAccountingAdapter>.Instance);
    }

    private static SalesController BuildController(ApplicationDbContext db, ISalesAccountingAdapter accounting)
        => new(
            db,
            new StockService(db),
            new CurrencyConversionService(new PricingService(db)),
            new AuditService(db),
            NullLogger<SalesController>.Instance,
            salesAccounting: accounting,
            formTokens: new FormTokenGuard(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            Url = new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()))
        };

    private static Task<List<LedgerEntry>> SaleLedgerAsync(ApplicationDbContext db, int saleId)
        => db.LedgerEntries.AsNoTracking().Where(l => l.SourceType == "Sale" && l.SourceId == saleId).ToListAsync();

    private static Task<JournalEntry?> JournalAsync(ApplicationDbContext db, Setup setup, string sourceEventId)
        => db.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .SingleOrDefaultAsync(j => j.CompanyId == setup.Scope.Company.Id
                && j.SourceModule == SalesAccountingAdapter.SourceModule
                && j.SourceEventId == sourceEventId);

    private static decimal Debit(JournalEntry journal, int accountId)
        => journal.Lines.Where(l => l.AccountId == accountId).Sum(l => l.Debit);

    private static decimal Credit(JournalEntry journal, int accountId)
        => journal.Lines.Where(l => l.AccountId == accountId).Sum(l => l.Credit);

    /// <summary>ماندهٔ یک حساب از همهٔ ژورنال‌های این فروش (اصل و برگشت‌ها).</summary>
    private static async Task<decimal> AccountNetAsync(ApplicationDbContext db, Setup setup, int saleId, int accountId)
    {
        var saleJournalIds = await db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == setup.Scope.Company.Id && j.SourceModule == SalesAccountingAdapter.SourceModule
                && j.SourceEventId != null && j.SourceEventId.StartsWith($"Sale:{saleId}:"))
            .Select(j => j.Id)
            .ToListAsync();
        var reversalIds = await db.JournalEntries.AsNoTracking()
            .Where(j => j.ReversalOfJournalEntryId != null && saleJournalIds.Contains(j.ReversalOfJournalEntryId.Value))
            .Select(j => j.Id)
            .ToListAsync();
        var ids = saleJournalIds.Concat(reversalIds).Distinct().ToList();
        return await db.JournalEntryLines.AsNoTracking()
            .Where(l => ids.Contains(l.JournalEntryId) && l.AccountId == accountId)
            .SumAsync(l => l.Debit - l.Credit);
    }

    private async Task<(decimal QuantityMt, decimal ValueUsd)> PoolAsync(ApplicationDbContext db, Setup setup)
    {
        await using var read = fixture.CreateDbContext();
        var pool = await read.InventoryAverageCosts.AsNoTracking().SingleAsync(x => x.CompanyId == setup.Scope.Company.Id
            && x.ProductId == setup.Scope.Product.Id && x.TerminalId == setup.Scope.Terminal.Id);
        return (decimal.Round(pool.QuantityMt, 4), decimal.Round(pool.TotalValueUsd, 2));
    }

    // عاید را می‌سازد و بعد خطا می‌دهد: شبیه شکستِ میانهٔ ثبت اسناد.
    private sealed class FailingRevenueAdapter(ISalesAccountingAdapter inner) : ISalesAccountingAdapter
    {
        public async Task<SalesAccountingResult> TryPostSaleAsync(SalesTransaction sale, CancellationToken cancellationToken = default)
        {
            await inner.TryPostSaleAsync(sale, cancellationToken);
            throw new InvalidOperationException("Simulated failure after revenue posting.");
        }

        public Task<SalesAccountingResult> TryPostCogsAsync(SalesTransaction sale, CancellationToken cancellationToken = default)
            => inner.TryPostCogsAsync(sale, cancellationToken);

        public Task<SalesAccountingResult> TryReverseSaleAsync(SalesTransaction sale, DateTime reversalDate, CancellationToken cancellationToken = default)
            => inner.TryReverseSaleAsync(sale, reversalDate, cancellationToken);

        public Task<SalesAccountingResult> TryReverseCogsAsync(SalesTransaction sale, DateTime reversalDate, CancellationToken cancellationToken = default)
            => inner.TryReverseCogsAsync(sale, reversalDate, cancellationToken);

        public Task<int> TrySettleDeliveredReceivableAsync(int customerPaymentAllocationId, CancellationToken cancellationToken = default)
            => inner.TrySettleDeliveredReceivableAsync(customerPaymentAllocationId, cancellationToken);

        public Task<int> TryReleaseAdvanceApplicationsAsync(SalesTransaction sale, DateTime reversalDate, CancellationToken cancellationToken = default)
            => inner.TryReleaseAdvanceApplicationsAsync(sale, reversalDate, cancellationToken);
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
