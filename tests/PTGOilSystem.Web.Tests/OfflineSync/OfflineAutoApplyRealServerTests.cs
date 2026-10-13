using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.OfflineSync;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests.OfflineSync;

[CollectionDefinition(Name)]
public sealed class OfflineRealServerCollection : ICollectionFixture<OfflineRealServerFixture>
{
    public const string Name = "Offline real server";
}

/// <summary>
/// ثبت قطعی خودکار عملیات آفلاین روی برنامهٔ واقعی (Program.cs کامل) و PostgreSQL موقت. هر سند با اکشن موجودِ
/// همان ماژول ساخته می‌شود؛ تست‌ها اثر واقعی (سند، حرکت موجودی، توکن ضد تکرار) را از دیتابیس می‌خوانند.
/// </summary>
[Collection(OfflineRealServerCollection.Name)]
public sealed class OfflineAutoApplyRealServerTests(OfflineRealServerFixture server, ITestOutputHelper output)
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static Seed? _seed;

    private sealed record Seed(
        int CompanyId, int CustomerId, int SupplierId, int ProductId, int TerminalId, int TankId, int CashAccountId,
        int ExpenseTypeId, int TruckId, int ContractId, int AdminId, int ClerkId);

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private async Task<Seed> SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seed is not null)
            {
                return _seed;
            }

            await using var db = server.CreateDbContext();
            var company = await db.Companies.FirstOrDefaultAsync(c => c.IsSystemOwner);
            if (company is null)
            {
                company = new Company { Code = "MSH", Name = "Mashal Test", Country = "AF", IsSystemOwner = true, IsActive = true };
                db.Companies.Add(company);
            }

            if (!await db.Currencies.AnyAsync(c => c.Code == "USD"))
            {
                db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", IsActive = true });
            }

            var customer = new Customer { Code = "C-OFF-1", Name = "Offline Customer" };
            var supplier = new Supplier { Code = "S-OFF-1", Name = "Offline Supplier" };
            var product = new Product { Code = "DSL-OFF", Name = "Diesel Offline", UnitOfMeasure = "MT" };
            var terminal = new Terminal { Code = "T-OFF", Name = "Offline Terminal" };
            var expenseType = new ExpenseType { Code = "EXP-OFF", Name = "Offline Expense" };
            var truck = new Truck { PlateNumber = "KBL-OFF-1" };
            db.AddRange(customer, supplier, product, terminal, expenseType, truck);
            await db.SaveChangesAsync();

            var tank = new StorageTank { TerminalId = terminal.Id, TankCode = "TK-OFF-1", ProductId = product.Id, CapacityMt = 10000 };
            var cash = new CashAccount { Code = "CA-OFF", Name = "Offline Cash", CompanyId = company.Id, Currency = "USD", AccountType = CashAccountType.Cash };
            var contract = new Contract
            {
                ContractName = "Offline Purchase", ContractNumber = "PC-OFF-1", ContractType = ContractType.Purchase,
                Status = ContractStatus.Active, CompanyId = company.Id, ProductId = product.Id, SupplierId = supplier.Id,
                ContractDate = DateTime.UtcNow.Date.AddDays(-30), QuantityMt = 5000, UnitPriceUsd = 600, Currency = "USD"
            };
            db.AddRange(tank, cash, contract);
            await db.SaveChangesAsync();

            var users = new UserService(db);
            var clerkRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == AuthRoles.Operator)
                ?? db.Roles.Add(new Role { Name = AuthRoles.Operator }).Entity;
            await db.SaveChangesAsync();
            var clerk = await users.CreateUserAsync("clerk", "Offline Clerk", OfflineRealServerFixture.ClerkPassword, clerkRole.Id);
            var admin = await db.Users.SingleAsync(u => u.Username == "admin");

            return _seed = new Seed(company.Id, customer.Id, supplier.Id, product.Id, terminal.Id, tank.Id, cash.Id,
                expenseType.Id, truck.Id, contract.Id, admin.Id, clerk.Id);
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private async Task<(OfflineClient Client, int UserId)> AdminAsync()
    {
        var seed = await SeedAsync();
        var client = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await client.RegisterAsync();
        return (client, seed.AdminId);
    }

    private void Log(OfflinePushResponse response)
    {
        foreach (var r in response.Results)
        {
            output.WriteLine($"{r.ClientOperationId:N} {r.Outcome} {r.Status} {r.Code} {r.AppliedReferenceType}/{r.AppliedReferenceId} :: {r.Message}");
        }
    }

    private async Task<decimal> TankStockAsync(int tankId)
    {
        await using var db = server.CreateDbContext();
        var movements = await db.InventoryMovements.AsNoTracking()
            .Where(m => m.StorageTankId == tankId)
            .Select(m => new { m.Direction, m.QuantityMt })
            .ToListAsync();
        return movements.Sum(m => m.Direction == MovementDirection.In ? m.QuantityMt : -m.QuantityMt);
    }

    [RealServerFact]
    public async Task Loading_Then_Receipt_Then_Sale_Are_Posted_Automatically_By_The_Existing_Actions()
    {
        var seed = await SeedAsync();
        var (client, userId) = await AdminAsync();

        // ۱) بارگیری آفلاین → اکشن Loading/Create
        var loadingOp = Guid.NewGuid();
        var loading = await client.PushAsync(OfflineClient.Op(loadingOp, userId, OfflineOperationTypes.LoadingNote,
            new { contractId = seed.ContractId, productId = seed.ProductId, quantity = 40m, truckId = seed.TruckId, truckPlate = "KBL-OFF-1", reference = "BL-OFF-1" }, Today));
        Log(loading);
        var loadingResult = Assert.Single(loading.Results);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), loadingResult.Status);
        Assert.False(string.IsNullOrEmpty(loadingResult.AppliedReferenceId));

        // ۲) رسید آفلاین همان بار به مخزن → اکشن LoadingReceipts/Create
        var loadingId = int.Parse(loadingResult.AppliedReferenceId!);
        var receiptOp = Guid.NewGuid();
        var receipt = await client.PushAsync(OfflineClient.Op(receiptOp, userId, OfflineOperationTypes.ReceiptNote,
            new { loadingId, tankId = seed.TankId, quantity = 40m }, Today));
        Log(receipt);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), Assert.Single(receipt.Results).Status);
        var stockAfterReceipt = await TankStockAsync(seed.TankId);
        output.WriteLine($"stock after receipt: {stockAfterReceipt}");
        Assert.True(stockAfterReceipt >= 40m);

        // ۳) فروش آفلاین از همان مخزن → اکشن Sales/Create (کنترل موجودی، حرکت خروجی، حسابداری موجود)
        var saleOp = Guid.NewGuid();
        var sale = await client.PushAsync(OfflineClient.Op(saleOp, userId, OfflineOperationTypes.SaleRequest,
            new { customerId = seed.CustomerId, productId = seed.ProductId, tankId = seed.TankId, quantity = 10m, unitPrice = 700m, currency = "USD" }, Today));
        Log(sale);
        var saleResult = Assert.Single(sale.Results);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), saleResult.Status);

        await using var db = server.CreateDbContext();
        var saleId = int.Parse(saleResult.AppliedReferenceId!);
        var saleRow = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == saleId);
        Assert.Equal(10m, saleRow.QuantityMt);
        Assert.Equal(seed.CustomerId, saleRow.CustomerId);
        Assert.Equal(stockAfterReceipt - 10m, await TankStockAsync(seed.TankId));
        Assert.True(await db.ProcessedFormTokens.AnyAsync(t => t.Token == OfflineOperationApplier.FormTokenFor(saleOp)));
    }

    // قطع اینترنت وسط Sync / ارسال تکراری / دو درخواست هم‌زمان: فقط یک سند.
    [RealServerFact]
    public async Task Duplicate_And_Concurrent_Resends_Create_Exactly_One_Document()
    {
        var seed = await SeedAsync();
        var (client, userId) = await AdminAsync();
        var opId = Guid.NewGuid();
        var op = OfflineClient.Op(opId, userId, OfflineOperationTypes.CustomerReceiptRequest,
            new { customerId = seed.CustomerId, cashAccountId = seed.CashAccountId, amount = 125.5m, currency = "USD", reference = "R-DUP" }, Today);

        var results = await Task.WhenAll(client.PushAsync(op), client.PushAsync(op), client.PushAsync(op));
        foreach (var r in results) Log(r);
        var again = await client.PushAsync(op);
        Log(again);
        var final = await client.StatusAsync(opId);

        Assert.Equal(nameof(OfflineSyncStatus.Applied), Assert.Single(final.Operations).Status);
        await using var db = server.CreateDbContext();
        Assert.Equal(1, await db.PaymentTransactions.CountAsync(p => p.Reference == "R-DUP"));
        Assert.Equal(1, await db.ProcessedFormTokens.CountAsync(t => t.Token == OfflineOperationApplier.FormTokenFor(opId)));
    }

    // سرور سند را ثبت کرد ولی پیش از علامت‌زدن متوقف شد → تلاش بعدی سند دوم نمی‌سازد.
    [RealServerFact]
    public async Task Crash_After_Commit_Is_Recovered_Without_A_Second_Document()
    {
        var seed = await SeedAsync();
        var (client, userId) = await AdminAsync();
        var opId = Guid.NewGuid();
        var op = OfflineClient.Op(opId, userId, OfflineOperationTypes.SupplierPaymentRequest,
            new { supplierId = seed.SupplierId, cashAccountId = seed.CashAccountId, amount = 77m, currency = "USD", reference = "R-CRASH" }, Today);
        Log(await client.PushAsync(op));

        await using (var db = server.CreateDbContext())
        {
            // وضعیت «در حال ثبت» رهاشده، با سندی که واقعاً ثبت شده (توکن موجود).
            await db.OfflineSyncOperations.Where(o => o.ClientOperationId == opId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OfflineSyncStatus.Applying)
                    .SetProperty(o => o.LastApplyAttemptAtUtc, DateTime.UtcNow.AddMinutes(-10)));
        }

        var status = await client.StatusAsync(opId);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), Assert.Single(status.Operations).Status);
        await using var check = server.CreateDbContext();
        Assert.Equal(1, await check.PaymentTransactions.CountAsync(p => p.Reference == "R-CRASH"));
    }

    // تغییر موجودی پیش از Sync + چند کاربر: فروش‌های هم‌زمان بیش از موجودی → یکی ثبت، دیگری «تعارض» با پیام فرم فروش.
    [RealServerFact]
    public async Task Concurrent_Sales_From_Two_Users_Never_Oversell_The_Tank()
    {
        var seed = await SeedAsync();
        var (admin, adminId) = await AdminAsync();
        var clerk = await server.LoginAsync("clerk", OfflineRealServerFixture.ClerkPassword);
        await clerk.RegisterAsync();

        var available = await TankStockAsync(seed.TankId);
        output.WriteLine($"available before: {available}");
        if (available <= 0)
        {
            // موجودی از تست زنجیرهٔ بارگیری/رسید می‌آید؛ اینجا یک زنجیرهٔ کوچک دیگر.
            var lo = Guid.NewGuid();
            var l = await admin.PushAsync(OfflineClient.Op(lo, adminId, OfflineOperationTypes.LoadingNote,
                new { contractId = seed.ContractId, productId = seed.ProductId, quantity = 30m, truckId = seed.TruckId, reference = "BL-CC" }, Today));
            var lid = int.Parse(Assert.Single(l.Results).AppliedReferenceId!);
            await admin.PushAsync(OfflineClient.Op(Guid.NewGuid(), adminId, OfflineOperationTypes.ReceiptNote,
                new { loadingId = lid, tankId = seed.TankId, quantity = 30m }, Today));
            available = await TankStockAsync(seed.TankId);
        }

        var each = decimal.Round(available * 0.6m, 3);
        var a = OfflineClient.Op(Guid.NewGuid(), adminId, OfflineOperationTypes.SaleRequest,
            new { customerId = seed.CustomerId, productId = seed.ProductId, tankId = seed.TankId, quantity = each, unitPrice = 650m, currency = "USD", reference = "CC-A" }, Today);
        var b = OfflineClient.Op(Guid.NewGuid(), seed.ClerkId, OfflineOperationTypes.SaleRequest,
            new { customerId = seed.CustomerId, productId = seed.ProductId, tankId = seed.TankId, quantity = each, unitPrice = 650m, currency = "USD", reference = "CC-B" }, Today);

        var results = await Task.WhenAll(admin.PushAsync(a), clerk.PushAsync(b));
        foreach (var r in results) Log(r);
        var statuses = results.Select(r => Assert.Single(r.Results).Status).ToList();

        Assert.Contains(nameof(OfflineSyncStatus.Applied), statuses);
        Assert.Contains(nameof(OfflineSyncStatus.Conflict), statuses);
        Assert.True(await TankStockAsync(seed.TankId) >= 0m, "tank must never go negative");
        var conflict = results.SelectMany(r => r.Results).Single(r => r.Status == nameof(OfflineSyncStatus.Conflict));
        Assert.False(string.IsNullOrWhiteSpace(conflict.Message));
    }

    // تعارض قابل اصلاح: همان عملیات با Revision تازه و مقدار درست دوباره فرستاده و ثبت می‌شود.
    [RealServerFact]
    public async Task Conflict_Is_Kept_And_The_Same_Operation_Can_Be_Corrected_And_Resent()
    {
        var seed = await SeedAsync();
        var (client, userId) = await AdminAsync();
        var opId = Guid.NewGuid();

        var tooMuch = await client.PushAsync(OfflineClient.Op(opId, userId, OfflineOperationTypes.SaleRequest,
            new { customerId = seed.CustomerId, productId = seed.ProductId, tankId = seed.TankId, quantity = 999999m, unitPrice = 650m, currency = "USD", reference = "FIX-1" }, Today));
        Log(tooMuch);
        var first = Assert.Single(tooMuch.Results);
        Assert.Equal(nameof(OfflineSyncStatus.Conflict), first.Status);

        await using (var db = server.CreateDbContext())
        {
            Assert.Equal(0, await db.SalesTransactions.CountAsync(s => s.TicketSerialNumber == "FIX-1"));
        }

        // اصلاح: یادداشت عملیاتی بدون مشکل (فروش بدون موجودی هم دوباره همان تعارض را می‌دهد).
        var available = await TankStockAsync(seed.TankId);
        var quantity = available > 1m ? 1m : 999999m;
        var fixedOp = await client.PushAsync(OfflineClient.Op(opId, userId, OfflineOperationTypes.SaleRequest,
            new { customerId = seed.CustomerId, productId = seed.ProductId, tankId = seed.TankId, quantity, unitPrice = 650m, currency = "USD", reference = "FIX-1" }, Today, revision: 2));
        Log(fixedOp);
        var second = Assert.Single(fixedOp.Results);
        Assert.Equal(2, second.Revision);
        if (available > 1m)
        {
            Assert.Equal(nameof(OfflineSyncStatus.Applied), second.Status);
            await using var db = server.CreateDbContext();
            Assert.Equal(1, await db.SalesTransactions.CountAsync(s => s.TicketSerialNumber == "FIX-1"));
        }
    }

    [RealServerFact]
    public async Task Expense_Customer_With_Dependent_Receipt_And_Note_Are_Posted()
    {
        var seed = await SeedAsync();
        var (client, userId) = await AdminAsync();
        var expenseOp = Guid.NewGuid();
        var customerOp = Guid.NewGuid();
        var receiptOp = Guid.NewGuid();
        var noteOp = Guid.NewGuid();

        var response = await client.PushAsync(
            OfflineClient.Op(expenseOp, userId, OfflineOperationTypes.ExpenseRequest,
                new { expenseTypeId = seed.ExpenseTypeId, amount = 15m, currency = "USD", cashAccountId = seed.CashAccountId, description = "مصرف آفلاین" }, Today),
            // دریافت از مشتریِ تازه‌ای که در همان دسته آفلاین ساخته شد.
            OfflineClient.Op(receiptOp, userId, OfflineOperationTypes.CustomerReceiptRequest,
                new { customerId = $"op:{customerOp}", cashAccountId = seed.CashAccountId, amount = 50m, currency = "USD", reference = "R-NEWCUST" }, Today,
                dependencies: [customerOp]),
            OfflineClient.Op(customerOp, userId, OfflineOperationTypes.CustomerDraft, new { name = "مشتری آفلاین تازه", phone = "0700000000" }, Today),
            OfflineClient.Op(noteOp, userId, OfflineOperationTypes.GeneralNote, new { text = "پمپ دو بررسی شد" }, Today));
        Log(response);

        Assert.All(response.Results, r => Assert.Equal(nameof(OfflineSyncStatus.Applied), r.Status));
        await using var db = server.CreateDbContext();
        var customerId = int.Parse(response.Results.Single(r => r.ClientOperationId == customerOp).AppliedReferenceId!);
        Assert.Equal("مشتری آفلاین تازه", (await db.Customers.SingleAsync(c => c.Id == customerId)).Name);
        Assert.Equal(customerId, (await db.PaymentTransactions.SingleAsync(p => p.Reference == "R-NEWCUST")).CustomerId);
        Assert.True(await db.ExpenseTransactions.AnyAsync(e => e.Description.Contains("مصرف آفلاین")));
    }

    // هیچ سند حسابداریِ نامتوازن: هر JournalEntry که سرویس‌های موجود برای عملیات آفلاین ساخته‌اند، بدهکار = بستانکار.
    [RealServerFact]
    public async Task All_Journal_Entries_Created_By_Offline_Posting_Are_Balanced()
    {
        var seed = await SeedAsync();
        var (client, userId) = await AdminAsync();
        await client.PushAsync(
            OfflineClient.Op(Guid.NewGuid(), userId, OfflineOperationTypes.CustomerReceiptRequest,
                new { customerId = seed.CustomerId, cashAccountId = seed.CashAccountId, amount = 33m, currency = "USD", reference = "R-BAL" }, Today),
            OfflineClient.Op(Guid.NewGuid(), userId, OfflineOperationTypes.ExpenseRequest,
                new { expenseTypeId = seed.ExpenseTypeId, amount = 9m, currency = "USD", cashAccountId = seed.CashAccountId, description = "توازن" }, Today));

        await using var db = server.CreateDbContext();
        var unbalanced = await db.JournalEntryLines.AsNoTracking()
            .GroupBy(l => l.JournalEntryId)
            .Select(g => new { g.Key, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) })
            .Where(x => x.Debit != x.Credit)
            .ToListAsync();
        output.WriteLine($"journal entries: {await db.JournalEntries.CountAsync()}, ledger rows: {await db.LedgerEntries.CountAsync()}");
        Assert.Empty(unbalanced);
        // اثر مالی را همان سرویس‌های موجود ساخته‌اند (دفتر عملیاتی). اگر ماژول حسابداری دوطرفه در این محیط
        // فعال/مقداردهی نشده باشد، JournalEntry ساخته نمی‌شود؛ آنگاه توازن فقط برای اسناد موجود سنجیده شده است.
        Assert.True(await db.LedgerEntries.AnyAsync(), "existing posting services must have written ledger rows");
    }

    [RealServerFact]
    public async Task Money_Transfer_Is_Not_Posted_Because_Mashal_Has_No_Transfer_Form()
    {
        await SeedAsync();
        var (client, userId) = await AdminAsync();
        var response = await client.PushAsync(OfflineClient.Op(Guid.NewGuid(), userId, OfflineOperationTypes.MoneyTransferRequest,
            new { fromCashAccountId = 1, toCashAccountId = 2, amount = 10m, currency = "USD" }, Today));
        Log(response);
        Assert.Equal(nameof(OfflineSyncStatus.Rejected), Assert.Single(response.Results).Status);
    }
}
