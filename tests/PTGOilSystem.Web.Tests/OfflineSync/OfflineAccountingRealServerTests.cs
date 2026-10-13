using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.OfflineSync;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests.OfflineSync;

/// <summary>
/// حسابداری دوطرفهٔ فعال (همان appsettings: Accounting.Enabled + Pilotها، سرفصل از <see cref="AccountingChartSeeder"/>
/// و سال مالی باز) روی برنامهٔ واقعی و PostgreSQL موقت. دو مسیر موازی با معاملات یکسان:
/// <list type="bullet">
///   <item>آنلاین: POSTِ HTTP واقعی به همان فرم (کوکی، Antiforgery و FormToken صفحهٔ GET، فیلدهای مخفی همان فرم)</item>
///   <item>آفلاین: /api/sync/v1/operations → ثبت خودکار</item>
/// </list>
/// برای هر عملیات، سطرهای JournalEntry تازه (کد حساب، بدهکار، بستانکار، اسعار، نوع طرف) دو مسیر باید یکسان باشد؛
/// موجودی مخزن، مانده‌های طرف‌حساب در دفتر عملیاتی و توازن همهٔ اسناد هم سنجیده می‌شود.
/// </summary>
[Collection(OfflineRealServerCollection.Name)]
public sealed class OfflineAccountingRealServerTests(OfflineRealServerFixture server, ITestOutputHelper output)
{
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record Track(
        string Name, int CustomerId, int SupplierId, int ContractId, int TerminalId, int TankId);

    private sealed record Shared(int CompanyId, int ProductId, int ExpenseTypeId, int CashAccountId, int TruckId, int AdminId);

    private sealed record JournalLineKey(string AccountCode, decimal Debit, decimal Credit, string Currency, string? PartyType, bool HasCash, bool HasTank);

    [RealServerFact]
    public async Task Seven_Offline_Operations_Post_The_Same_Balanced_Journals_As_Online_Forms()
    {
        var shared = await SeedSharedAsync();
        var online = await SeedTrackAsync("ON");
        var offline = await SeedTrackAsync("OFF");

        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var userId = shared.AdminId;

        var steps = new List<(string Name, List<JournalLineKey> Online, List<JournalLineKey> Offline)>();

        // ۱) بارگیری
        var (onLoadingJournals, onLoadingId) = await OnlineAsync(admin, "LoadingNote",
            new { contractId = online.ContractId, productId = shared.ProductId, quantity = 40m, truckId = shared.TruckId, truckPlate = "KBL-ACC-1", reference = "BL-ACC-ON" },
            db => db.LoadingRegisters.Where(l => l.ContractId == online.ContractId).MaxAsync(l => (int?)l.Id));
        var (offLoadingJournals, offLoadingRef) = await OfflineAsync(admin, userId, OfflineOperationTypes.LoadingNote,
            new { contractId = offline.ContractId, productId = shared.ProductId, quantity = 40m, truckId = shared.TruckId, truckPlate = "KBL-ACC-1", reference = "BL-ACC-OFF" });
        steps.Add(("Loading", onLoadingJournals, offLoadingJournals));

        // ۲) رسید و تخلیه به مخزن
        var (onReceiptJournals, _) = await OnlineAsync(admin, "ReceiptNote",
            new { loadingId = onLoadingId, tankId = online.TankId, quantity = 40m, reference = "RC-ACC-ON" },
            db => db.LoadingReceipts.Where(r => r.LoadingRegisterId == onLoadingId).MaxAsync(r => (int?)r.Id));
        var (offReceiptJournals, _) = await OfflineAsync(admin, userId, OfflineOperationTypes.ReceiptNote,
            new { loadingId = int.Parse(offLoadingRef, CultureInfo.InvariantCulture), tankId = offline.TankId, quantity = 40m, reference = "RC-ACC-OFF" });
        steps.Add(("Receipt", onReceiptJournals, offReceiptJournals));

        // ۳) فروش از مخزن
        var (onSaleJournals, _) = await OnlineAsync(admin, "SaleRequest",
            new { customerId = online.CustomerId, productId = shared.ProductId, tankId = online.TankId, quantity = 10m, unitPrice = 700m, currency = "USD", reference = "S-ACC-ON" },
            db => db.SalesTransactions.Where(s => s.TicketSerialNumber == "S-ACC-ON").MaxAsync(s => (int?)s.Id));
        var (offSaleJournals, _) = await OfflineAsync(admin, userId, OfflineOperationTypes.SaleRequest,
            new { customerId = offline.CustomerId, productId = shared.ProductId, tankId = offline.TankId, quantity = 10m, unitPrice = 700m, currency = "USD", reference = "S-ACC-OFF" });
        steps.Add(("Sale", onSaleJournals, offSaleJournals));

        // ۴) دریافت از مشتری
        var (onReceiveJournals, _) = await OnlineAsync(admin, "CustomerReceiptRequest",
            new { customerId = online.CustomerId, cashAccountId = shared.CashAccountId, amount = 125.5m, currency = "USD", reference = "CR-ACC-ON" },
            db => db.PaymentTransactions.Where(p => p.Reference == "CR-ACC-ON").MaxAsync(p => (int?)p.Id));
        var (offReceiveJournals, _) = await OfflineAsync(admin, userId, OfflineOperationTypes.CustomerReceiptRequest,
            new { customerId = offline.CustomerId, cashAccountId = shared.CashAccountId, amount = 125.5m, currency = "USD", reference = "CR-ACC-OFF" });
        steps.Add(("CustomerReceipt", onReceiveJournals, offReceiveJournals));

        // ۵) پرداخت به فروشنده
        var (onPayJournals, _) = await OnlineAsync(admin, "SupplierPaymentRequest",
            new { supplierId = online.SupplierId, cashAccountId = shared.CashAccountId, amount = 77m, currency = "USD", reference = "SP-ACC-ON" },
            db => db.PaymentTransactions.Where(p => p.Reference == "SP-ACC-ON").MaxAsync(p => (int?)p.Id));
        var (offPayJournals, _) = await OfflineAsync(admin, userId, OfflineOperationTypes.SupplierPaymentRequest,
            new { supplierId = offline.SupplierId, cashAccountId = shared.CashAccountId, amount = 77m, currency = "USD", reference = "SP-ACC-OFF" });
        steps.Add(("SupplierPayment", onPayJournals, offPayJournals));

        // ۶) مصرف
        var (onExpenseJournals, _) = await OnlineAsync(admin, "ExpenseRequest",
            new { expenseTypeId = shared.ExpenseTypeId, amount = 15m, currency = "USD", cashAccountId = shared.CashAccountId, description = "مصرف حسابداری ON" },
            db => db.ExpenseTransactions.Where(e => e.Description.Contains("مصرف حسابداری ON")).MaxAsync(e => (int?)e.Id));
        var (offExpenseJournals, _) = await OfflineAsync(admin, userId, OfflineOperationTypes.ExpenseRequest,
            new { expenseTypeId = shared.ExpenseTypeId, amount = 15m, currency = "USD", cashAccountId = shared.CashAccountId, description = "مصرف حسابداری OFF" });
        steps.Add(("Expense", onExpenseJournals, offExpenseJournals));

        // ۷) مشتری جدید
        var (onCustomerJournals, _) = await OnlineAsync(admin, "CustomerDraft",
            new { name = "مشتری حسابداری ON", phone = "0700000001" },
            db => db.Customers.Where(c => c.Name == "مشتری حسابداری ON").MaxAsync(c => (int?)c.Id));
        var (offCustomerJournals, _) = await OfflineAsync(admin, userId, OfflineOperationTypes.CustomerDraft,
            new { name = "مشتری حسابداری OFF", phone = "0700000001" });
        steps.Add(("CustomerDraft", onCustomerJournals, offCustomerJournals));

        foreach (var (name, on, off) in steps)
        {
            output.WriteLine($"== {name}: online {on.Count} lines, offline {off.Count} lines");
            foreach (var line in off)
            {
                output.WriteLine($"   {line.AccountCode} Dr {line.Debit} Cr {line.Credit} {line.Currency} {line.PartyType}");
            }
        }

        // اسناد واقعاً ساخته شده‌اند (حسابداری فعال است) و برای هر عملیات دو مسیر یکسان‌اند.
        // بارگیری (1310/2100)، رسید (1300/1310) و فروش (درآمد + بهای تمام‌شده) سند دفتر کل دارند.
        // دریافت/پرداخت/مصرفِ بدون قرارداد در خودِ مشعل (آنلاین هم) سند نمی‌گیرد: PaymentCompanyResolver شرکت را
        // فقط از قرارداد/فروش/محموله اثبات می‌کند (PAYMENT_COMPANY_UNKNOWN / EXPENSE_COMPANY_UNKNOWN)؛ اینجا فقط
        // یکسان‌بودن دو مسیر سنجیده می‌شود.
        Assert.Equal(2, steps.Single(s => s.Name == "Loading").Offline.Count);
        Assert.Equal(2, steps.Single(s => s.Name == "Receipt").Offline.Count);
        Assert.Equal(4, steps.Single(s => s.Name == "Sale").Offline.Count);
        foreach (var (name, on, off) in steps)
        {
            Assert.True(on.SequenceEqual(off), $"{name}: online and offline journals differ.\nonline: {string.Join("; ", on)}\noffline: {string.Join("; ", off)}");
        }

        await using var db = server.CreateDbContext();

        // موجودی مخزن دقیق: ۴۰ رسید − ۱۰ فروش.
        Assert.Equal(30m, await TankStockAsync(db, online.TankId));
        Assert.Equal(30m, await TankStockAsync(db, offline.TankId));

        // مانده‌های طرف‌حساب (دفتر عملیاتی) دو مسیر یکسان و غیرصفر.
        var onCustomer = await PartyNetAsync(db, customerId: online.CustomerId);
        var offCustomer = await PartyNetAsync(db, customerId: offline.CustomerId);
        var onSupplier = await PartyNetAsync(db, supplierId: online.SupplierId);
        var offSupplier = await PartyNetAsync(db, supplierId: offline.SupplierId);
        output.WriteLine($"customer net (Debit−Credit USD): online {onCustomer}, offline {offCustomer}");
        output.WriteLine($"supplier net (Debit−Credit USD): online {onSupplier}, offline {offSupplier}");
        Assert.Equal(onCustomer, offCustomer);
        Assert.Equal(onSupplier, offSupplier);
        Assert.NotEqual(0m, offCustomer);
        Assert.NotEqual(0m, offSupplier);

        // ماندهٔ دفتر کل جدید روی سطرهای طرف همین مشتری/فروشنده هم یکسان است.
        Assert.Equal(await JournalPartyNetAsync(db, AccountingPartyType.Customer, online.CustomerId),
            await JournalPartyNetAsync(db, AccountingPartyType.Customer, offline.CustomerId));
        Assert.Equal(await JournalPartyNetAsync(db, AccountingPartyType.Supplier, online.SupplierId),
            await JournalPartyNetAsync(db, AccountingPartyType.Supplier, offline.SupplierId));

        // ارسال دوبارهٔ همهٔ عملیات آفلاین: هیچ سند/سطر دفتر تازه.
        var journalsBefore = await db.JournalEntries.CountAsync();
        var ledgerBefore = await db.LedgerEntries.CountAsync();
        foreach (var op in _sentOffline)
        {
            var again = await admin.PushAsync(op);
            Assert.Equal(nameof(OfflineSyncStatus.Applied), Assert.Single(again.Results).Status);
        }

        Assert.Equal(journalsBefore, await db.JournalEntries.CountAsync());
        Assert.Equal(ledgerBefore, await db.LedgerEntries.CountAsync());

        // هیچ سند نامتوازن و هیچ SourceEventId تکراری در کل دیتابیس.
        var unbalanced = await db.JournalEntryLines.AsNoTracking()
            .GroupBy(l => l.JournalEntryId)
            .Select(g => new { g.Key, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) })
            .Where(x => x.Debit != x.Credit)
            .ToListAsync();
        Assert.Empty(unbalanced);
        var duplicateSources = await db.JournalEntries.AsNoTracking()
            .Where(j => j.SourceEventId != null)
            .GroupBy(j => new { j.CompanyId, j.SourceModule, j.SourceEventId })
            .Where(g => g.Count() > 1)
            .CountAsync();
        Assert.Equal(0, duplicateSources);
    }

    // ثبت چندروزه: عملیات چند روز آفلاین ثبت و یکجا همگام می‌شوند؛ تاریخ سند و تاریخ حسابداری همان تاریخ کاری دستگاه است.
    [RealServerFact]
    public async Task Multi_Day_Offline_Operations_Keep_Their_Business_Dates_In_Documents_And_Journals()
    {
        var shared = await SeedSharedAsync();
        var track = await SeedTrackAsync("DAYS");
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var day1 = DateTime.UtcNow.Date.AddDays(-3);
        var day2 = DateTime.UtcNow.Date.AddDays(-2);
        var day3 = DateTime.UtcNow.Date.AddDays(-1);
        string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var loadingOp = Guid.NewGuid();
        var loading = await admin.PushAsync(OfflineClient.Op(loadingOp, shared.AdminId, OfflineOperationTypes.LoadingNote,
            new { contractId = track.ContractId, productId = shared.ProductId, quantity = 20m, truckId = shared.TruckId, reference = "BL-DAYS" }, D(day1)));
        var loadingId = int.Parse(Assert.Single(loading.Results).AppliedReferenceId!, CultureInfo.InvariantCulture);
        // رسید و فروشِ روزهای بعد در یک دستهٔ دیگر (مثل اتصال پس از چند روز).
        var batch = await admin.PushAsync(
            OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.ReceiptNote,
                new { loadingId, tankId = track.TankId, quantity = 20m, reference = "RC-DAYS" }, D(day2)),
            OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.SaleRequest,
                new { customerId = track.CustomerId, productId = shared.ProductId, tankId = track.TankId, quantity = 5m, unitPrice = 710m, currency = "USD", reference = "S-DAYS" }, D(day3)));
        foreach (var r in batch.Results)
        {
            output.WriteLine($"{r.Status} {r.AppliedReferenceType}/{r.AppliedReferenceId} {r.Message}");
        }

        Assert.All(batch.Results, r => Assert.Equal(nameof(OfflineSyncStatus.Applied), r.Status));
        await using var db = server.CreateDbContext();
        var loadingRow = await db.LoadingRegisters.AsNoTracking().SingleAsync(l => l.Id == loadingId);
        var receiptRow = await db.LoadingReceipts.AsNoTracking().SingleAsync(r => r.LoadingRegisterId == loadingId);
        var saleRow = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.TicketSerialNumber == "S-DAYS");
        Assert.Equal(day1, loadingRow.LoadingDate.Date);
        Assert.Equal(day2, receiptRow.ReceiptDate.Date);
        Assert.Equal(day3, saleRow.SaleDate.Date);
        Assert.Equal(15m, await TankStockAsync(db, track.TankId));

        var saleJournalDates = await db.JournalEntries.AsNoTracking()
            .Where(j => j.SourceEntityId == saleRow.Id && (j.SourceModule == "Sales" || j.SourceModule == "Sale" || j.SourceEntityType == nameof(SalesTransaction)))
            .Select(j => j.AccountingDate)
            .ToListAsync();
        output.WriteLine($"sale journal dates: {string.Join(", ", saleJournalDates.Select(d => d.ToString("yyyy-MM-dd")))}");
        Assert.NotEmpty(saleJournalDates);
        Assert.All(saleJournalDates, d => Assert.Equal(day3, d.Date));
    }

    // نشست دستگاه معتمد: کوکی وب → device-session → Bearer (JWT) بدون کوکی ارسال می‌کند؛ Bearer نشست تازه نمی‌سازد؛
    // کاربر غیرفعال با Bearer معتبر هم 401 می‌گیرد.
    [RealServerFact]
    public async Task Trusted_Device_Session_Pushes_With_Bearer_And_Stops_When_User_Is_Deactivated()
    {
        await SeedSharedAsync();
        await using (var db = server.CreateDbContext())
        {
            if (!await db.Users.AnyAsync(u => u.Username == "devclerk"))
            {
                var role = await db.Roles.FirstAsync(r => r.Name == PTGOilSystem.Web.Security.AuthRoles.Admin);
                await new PTGOilSystem.Web.Services.UserService(db).CreateUserAsync("devclerk", "Device Clerk", OfflineRealServerFixture.ClerkPassword, role.Id);
            }
        }

        var cookieClient = await server.LoginAsync("devclerk", OfflineRealServerFixture.ClerkPassword);
        var session = await cookieClient.SessionAsync();
        var issue = await cookieClient.SendAsync(HttpMethod.Post, "/device-session", new { deviceId = cookieClient.DeviceId, deviceName = "Real-server PWA" });
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        using var tokens = System.Text.Json.JsonDocument.Parse(await issue.Content.ReadAsStringAsync());
        var access = tokens.RootElement.GetProperty("accessToken").GetString()!;
        Assert.False(string.IsNullOrEmpty(tokens.RootElement.GetProperty("refreshToken").GetString()));

        async Task<HttpResponseMessage> BearerAsync(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, "/api/sync/v1" + path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            request.Headers.Add("X-PTG-Offline", "1");
            if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body, options: OfflineClient.Json);
            return await cookieClient.Http.SendAsync(request);
        }

        // بدون کوکی، فقط با Bearer (مثل پس از Restart مرورگر).
        var push = await BearerAsync(HttpMethod.Post, "/operations", new
        {
            deviceId = cookieClient.DeviceId,
            operations = new[] { OfflineClient.Op(Guid.NewGuid(), session.UserId, OfflineOperationTypes.GeneralNote, new { text = "bearer note" }, Today) }
        });
        var pushText = await push.Content.ReadAsStringAsync();
        Assert.True(push.StatusCode == HttpStatusCode.OK, pushText);
        Assert.Contains(nameof(OfflineSyncStatus.Applied), pushText);

        // عملیاتِ کاربر دیگر با همین توکن رد می‌شود.
        var foreign = await BearerAsync(HttpMethod.Post, "/operations", new
        {
            deviceId = cookieClient.DeviceId,
            operations = new[] { OfflineClient.Op(Guid.NewGuid(), session.UserId + 999, OfflineOperationTypes.GeneralNote, new { text = "x" }, Today) }
        });
        Assert.DoesNotContain(nameof(OfflineSyncStatus.Applied), await foreign.Content.ReadAsStringAsync());

        // Bearer نمی‌تواند نشست دستگاه تازه بسازد.
        var chained = await BearerAsync(HttpMethod.Post, "/device-session", new { deviceId = Guid.NewGuid(), deviceName = "chain" });
        Assert.Equal(HttpStatusCode.Forbidden, chained.StatusCode);

        // غیرفعال‌شدن کاربر: همان توکن معتبر دیگر چیزی نمی‌فرستد.
        await using (var db = server.CreateDbContext())
        {
            await db.Users.Where(u => u.Username == "devclerk").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var afterDeactivate = await BearerAsync(HttpMethod.Post, "/operations", new
        {
            deviceId = cookieClient.DeviceId,
            operations = new[] { OfflineClient.Op(Guid.NewGuid(), session.UserId, OfflineOperationTypes.GeneralNote, new { text = "after" }, Today) }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, afterDeactivate.StatusCode);
    }

    // فورم اصلی MVC (Form.Submit): همان فیلدهایی که مرورگر در فورم اصلی می‌فرستد، آفلاین ذخیره و بعداً با همان اکشن
    // ثبت می‌شود. سند، دفتر کل، موجودی و مانده باید با POSTِ آنلاینِ همان فورم یکسان باشد.
    [RealServerFact]
    public async Task Main_Form_Fields_Saved_Offline_Post_The_Same_Journals_As_The_Online_Form()
    {
        var shared = await SeedSharedAsync();
        var online = await SeedTrackAsync("MF-ON");
        var offline = await SeedTrackAsync("MF-OFF");
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var userId = shared.AdminId;
        var steps = new List<(string Name, List<JournalLineKey> Online, List<JournalLineKey> Offline)>();

        var (onLoading, onLoadingId) = await OnlineAsync(admin, "LoadingNote",
            new { contractId = online.ContractId, productId = shared.ProductId, quantity = 40m, truckId = shared.TruckId, truckPlate = "KBL-ACC-1", reference = "BL-MF-ON" },
            db => db.LoadingRegisters.Where(l => l.ContractId == online.ContractId).MaxAsync(l => (int?)l.Id));
        var (offLoading, _) = await MainFormOfflineAsync(admin, userId, "LoadingNote",
            new { contractId = offline.ContractId, productId = shared.ProductId, quantity = 40m, truckId = shared.TruckId, truckPlate = "KBL-ACC-1", reference = "BL-MF-OFF" });
        steps.Add(("Loading", onLoading, offLoading));
        int offLoadingId;
        await using (var db = server.CreateDbContext())
        {
            offLoadingId = await db.LoadingRegisters.Where(l => l.ContractId == offline.ContractId).MaxAsync(l => l.Id);
        }

        var (onReceipt, _) = await OnlineAsync(admin, "ReceiptNote",
            new { loadingId = onLoadingId, tankId = online.TankId, quantity = 40m, reference = "RC-MF-ON" },
            db => db.LoadingReceipts.Where(r => r.LoadingRegisterId == onLoadingId).MaxAsync(r => (int?)r.Id));
        var (offReceipt, _) = await MainFormOfflineAsync(admin, userId, "ReceiptNote",
            new { loadingId = offLoadingId, tankId = offline.TankId, quantity = 40m, reference = "RC-MF-OFF" });
        steps.Add(("Receipt", onReceipt, offReceipt));

        var (onSale, _) = await OnlineAsync(admin, "SaleRequest",
            new { customerId = online.CustomerId, productId = shared.ProductId, tankId = online.TankId, quantity = 10m, unitPrice = 700m, currency = "USD", reference = "S-MF-ON" },
            db => db.SalesTransactions.Where(s => s.TicketSerialNumber == "S-MF-ON").MaxAsync(s => (int?)s.Id));
        var (offSale, _) = await MainFormOfflineAsync(admin, userId, "SaleRequest",
            new { customerId = offline.CustomerId, productId = shared.ProductId, tankId = offline.TankId, quantity = 10m, unitPrice = 700m, currency = "USD", reference = "S-MF-OFF" });
        steps.Add(("Sale", onSale, offSale));

        var (onReceive, _) = await OnlineAsync(admin, "CustomerReceiptRequest",
            new { customerId = online.CustomerId, cashAccountId = shared.CashAccountId, amount = 125.5m, currency = "USD", reference = "CR-MF-ON" },
            db => db.PaymentTransactions.Where(p => p.Reference == "CR-MF-ON").MaxAsync(p => (int?)p.Id));
        var (offReceive, _) = await MainFormOfflineAsync(admin, userId, "CustomerReceiptRequest",
            new { customerId = offline.CustomerId, cashAccountId = shared.CashAccountId, amount = 125.5m, currency = "USD", reference = "CR-MF-OFF" });
        steps.Add(("CustomerReceipt", onReceive, offReceive));

        var (onExpense, _) = await OnlineAsync(admin, "ExpenseRequest",
            new { expenseTypeId = shared.ExpenseTypeId, amount = 15m, currency = "USD", cashAccountId = shared.CashAccountId, description = "مصرف فورم ON" },
            db => db.ExpenseTransactions.Where(e => e.Description.Contains("مصرف فورم ON")).MaxAsync(e => (int?)e.Id));
        var (offExpense, _) = await MainFormOfflineAsync(admin, userId, "ExpenseRequest",
            new { expenseTypeId = shared.ExpenseTypeId, amount = 15m, currency = "USD", cashAccountId = shared.CashAccountId, description = "مصرف فورم OFF" });
        steps.Add(("Expense", onExpense, offExpense));

        foreach (var (name, on, off) in steps)
        {
            output.WriteLine($"== {name}: online {on.Count} lines, offline {off.Count} lines");
            Assert.True(on.SequenceEqual(off), $"{name}: online and offline journals differ.\nonline: {string.Join("; ", on)}\noffline: {string.Join("; ", off)}");
        }

        Assert.Equal(2, steps.Single(s => s.Name == "Loading").Offline.Count);
        Assert.Equal(4, steps.Single(s => s.Name == "Sale").Offline.Count);
        await using var check = server.CreateDbContext();
        Assert.Equal(await TankStockAsync(check, online.TankId), await TankStockAsync(check, offline.TankId));
        Assert.Equal(30m, await TankStockAsync(check, offline.TankId));
        Assert.Equal(await PartyNetAsync(check, customerId: online.CustomerId), await PartyNetAsync(check, customerId: offline.CustomerId));
        Assert.Empty(await check.JournalEntryLines.AsNoTracking()
            .GroupBy(l => l.JournalEntryId)
            .Select(g => new { g.Key, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) })
            .Where(x => x.Debit != x.Credit)
            .ToListAsync());
    }

    // ارسال آنلاین فرستاده شد ولی پاسخ نیامد: فورم با همان توکن صفحه در صف می‌ماند؛ سرور سند دوم نمی‌سازد.
    [RealServerFact]
    public async Task Unknown_Online_Outcome_Replays_With_The_Page_Token_Without_A_Second_Document()
    {
        var shared = await SeedSharedAsync();
        var track = await SeedTrackAsync("UNK");
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var payload = new { customerId = track.CustomerId, cashAccountId = shared.CashAccountId, amount = 42m, currency = "USD", reference = "CR-UNK" };

        var browser = await BrowserFormAsync(admin, "CustomerReceiptRequest", payload);
        var pageToken = browser.Fields["__FormToken"];
        Assert.False(string.IsNullOrEmpty(pageToken));
        await PostOnlineAsync(admin, "CustomerReceiptRequest", browser,
            db => db.PaymentTransactions.Where(p => p.Reference == "CR-UNK").MaxAsync(p => (int?)p.Id));

        var fields = FormFields(browser);
        var replay = OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit,
            new { path = browser.Path, fields, formToken = pageToken }, Today);
        var result = Assert.Single((await admin.PushAsync(replay)).Results);
        output.WriteLine($"replay: {result.Status} {result.Message}");
        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);

        await using var db = server.CreateDbContext();
        Assert.Equal(1, await db.PaymentTransactions.CountAsync(p => p.Reference == "CR-UNK"));

        // همان معلومات با توکن صفحه‌ای که هرگز به سرور نرسید: یک‌بار (و فقط یک‌بار) ثبت می‌شود.
        var notSent = OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit,
            new { path = browser.Path, fields, formToken = Guid.NewGuid().ToString("N") }, Today);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), Assert.Single((await admin.PushAsync(notSent)).Results).Status);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), Assert.Single((await admin.PushAsync(notSent)).Results).Status);
        Assert.Equal(2, await db.PaymentTransactions.CountAsync(p => p.Reference == "CR-UNK"));
    }

    // مشتری تازه‌ای که آفلاین در فورم اصلی ساخته شد، در دریافتِ آفلاینِ بعدی انتخاب می‌شود (op:) و پس از ساخت جایگزین می‌شود.
    [RealServerFact]
    public async Task Offline_New_Customer_From_The_Main_Form_Is_Used_By_A_Later_Offline_Receipt()
    {
        var shared = await SeedSharedAsync();
        var track = await SeedTrackAsync("REF");
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var customerOp = Guid.NewGuid();
        var receiptForm = await BrowserFormAsync(admin, "CustomerReceiptRequest",
            new { customerId = track.CustomerId, cashAccountId = shared.CashAccountId, amount = 9m, currency = "USD", reference = "CR-REF" });
        var receiptFields = FormFields(receiptForm)
            .Select(f => f[0] == "CustomerId" ? new[] { "CustomerId", $"op:{customerOp}" } : f).ToArray();

        var response = await admin.PushAsync(
            OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit,
                new { path = receiptForm.Path, fields = receiptFields }, Today, dependencies: [customerOp]),
            OfflineClient.Op(customerOp, shared.AdminId, OfflineOperationTypes.FormSubmit,
                new { path = "/customers/create", fields = new[] { new[] { "Name", "مشتری تازهٔ فورم اصلی" }, new[] { "IsActive", "true" } } }, Today));

        foreach (var r in response.Results)
        {
            output.WriteLine($"{r.ClientOperationId}: {r.Status} {r.AppliedReferenceType}/{r.AppliedReferenceId} {r.Message}");
        }

        Assert.All(response.Results, r => Assert.Equal(nameof(OfflineSyncStatus.Applied), r.Status));
        await using var db = server.CreateDbContext();
        var customerId = await db.Customers.Where(c => c.Name == "مشتری تازهٔ فورم اصلی").Select(c => c.Id).SingleAsync();
        Assert.Equal(customerId, await db.PaymentTransactions.Where(p => p.Reference == "CR-REF").Select(p => p.CustomerId).SingleAsync());
    }

    // فورم‌های اصلیِ بدون توکن ضد تکرار (معلومات پایه) در تراکنش بیرونی اجرا می‌شوند؛ هیچ‌کدام نباید با تراکنش داخلی بشکند.
    [RealServerFact]
    public async Task Wrapped_Master_Data_Forms_Are_Created_Once_Through_Their_Real_Actions()
    {
        var shared = await SeedSharedAsync();
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var failures = new List<string>();
        var masterData = new[] { "/customers/create", "/suppliers/create", "/partners/create", "/sarrafs/create", "/serviceproviders/create",
            "/trucks/create", "/drivers/create", "/wagons/create", "/vessels/create", "/locations/create" };
        foreach (var path in masterData)
        {
            var fields = await FillFormAsync(admin, path);
            var op = OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit, new { path, fields }, Today);
            var first = Assert.Single((await admin.PushAsync(op)).Results);
            var again = Assert.Single((await admin.PushAsync(op)).Results);
            output.WriteLine($"{path}: {first.Status} {first.AppliedReferenceType}/{first.AppliedReferenceId} {first.Message}");
            if (first.Status != nameof(OfflineSyncStatus.Applied) || again.Status != nameof(OfflineSyncStatus.Applied))
            {
                failures.Add($"{path}: {first.Status} {first.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        await using var db = server.CreateDbContext();
        Assert.Equal(1, await db.Customers.CountAsync(c => c.Name == "فورم /customers/create"));
        Assert.Equal(1, await db.Suppliers.CountAsync(c => c.Name == "فورم /suppliers/create"));
    }

    // همهٔ فورم‌های مجاز آفلاین: فورم خالیِ صفحهٔ GET (فقط فیلدهای لازم) با همان اکشن اجرا می‌شود. نتیجه یا ثبت است یا
    // پیام تجاری/اعتبارسنجی خود فورم؛ هیچ‌کدام نباید خطای پیش‌بینی‌نشده (مثلاً تراکنش تودرتو) بدهد.
    [RealServerFact]
    public async Task Every_Registered_Main_Form_Replays_Through_Its_Real_Action_Without_Unexpected_Errors()
    {
        var shared = await SeedSharedAsync();
        await SeedTrackAsync("SMOKE");
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var unexpected = new List<string>();
        foreach (var target in OfflineFormRegistry.All)
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, target.Path);
            probe.Headers.Add("Cookie", admin.Cookie);
            var page = await admin.Http.SendAsync(probe);
            if (page.StatusCode != HttpStatusCode.OK)
            {
                output.WriteLine($"{target.Path}: no standalone GET page ({(int)page.StatusCode}); posted from another page");
                continue;
            }

            string[][] fields;
            try
            {
                fields = await FillFormAsync(admin, target.Path);
            }
            catch (InvalidOperationException)
            {
                output.WriteLine($"{target.Path}: page has no POST form");
                continue;
            }

            var op = OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit, new { path = target.Path, fields }, Today);
            var result = Assert.Single((await admin.PushAsync(op)).Results);
            output.WriteLine($"{target.Path}: {result.Status} {result.Message}");
            if (result.Status is not (nameof(OfflineSyncStatus.Applied) or nameof(OfflineSyncStatus.Conflict))
                || (result.Message ?? "").Contains("پیش‌بینی‌نشده", StringComparison.Ordinal)
                || (result.Message ?? "").Contains("transaction", StringComparison.OrdinalIgnoreCase))
            {
                unexpected.Add($"{target.Path}: {result.Status} {result.Message}");
            }
        }

        Assert.True(unexpected.Count == 0, string.Join("\n", unexpected));
    }

    // ثبت عملیاتیِ دارایی (کارکرد، سرویس) آفلاین؛ Redirect همراه پیام خطای TempData ثبت نشده حساب می‌شود، نه «همگام‌شد».
    [RealServerFact]
    public async Task Operational_Asset_Logs_Apply_And_A_Redirect_With_An_Error_Message_Is_Not_Reported_As_Applied()
    {
        var shared = await SeedSharedAsync();
        int assetId;
        await using (var db = server.CreateDbContext())
        {
            var asset = new OperationalAsset { AssetCode = "OA-OFF-1", Name = "Offline Generator" };
            db.Add(asset);
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        object Reading(decimal value) => new
        {
            path = "/operationalassets/addmeterreading",
            fields = new[]
            {
                new[] { "OperationalAssetId", assetId.ToString(CultureInfo.InvariantCulture) },
                new[] { "ReadingDate", Today }, new[] { "ReadingValue", value.ToString(CultureInfo.InvariantCulture) },
                new[] { "Reference", "OFF-METER" }
            }
        };

        var bad = Assert.Single((await admin.PushAsync(OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit, Reading(-5), Today))).Results);
        var good = Assert.Single((await admin.PushAsync(OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit, Reading(1250), Today))).Results);
        var job = Assert.Single((await admin.PushAsync(OfflineClient.Op(Guid.NewGuid(), shared.AdminId, OfflineOperationTypes.FormSubmit, new
        {
            path = "/operationalassets/addmaintenancejob",
            fields = new[]
            {
                new[] { "OperationalAssetId", assetId.ToString(CultureInfo.InvariantCulture) }, new[] { "Title", "سرویس آفلاین" },
                new[] { "ScheduledDate", Today }
            }
        }, Today))).Results);
        output.WriteLine($"bad: {bad.Status} {bad.Message} | good: {good.Status} | job: {job.Status} {job.Message}");

        Assert.Equal(nameof(OfflineSyncStatus.Conflict), bad.Status);
        Assert.Contains("منفی", bad.Message);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), good.Status);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), job.Status);
        await using var check = server.CreateDbContext();
        Assert.Equal(1, await check.AssetMeterReadings.CountAsync(r => r.OperationalAssetId == assetId));
        Assert.Equal(1, await check.AssetMaintenanceJobs.CountAsync(j => j.OperationalAssetId == assetId && j.Title == "سرویس آفلاین"));
    }

    private static string[][] FormFields(BrowserForm browser)
        => browser.Fields.Where(f => f.Value is not null && f.Key is not "__RequestVerificationToken" and not "__FormToken")
            .Select(f => new[] { f.Key, f.Value! }).ToArray();

    private async Task<(List<JournalLineKey> Lines, string? ReferenceId)> MainFormOfflineAsync(
        OfflineClient client, int userId, string typeName, object payload)
    {
        var browser = await BrowserFormAsync(client, typeName, payload);
        var lastJournal = await LastJournalIdAsync();
        var op = OfflineClient.Op(Guid.NewGuid(), userId, OfflineOperationTypes.FormSubmit,
            new { path = browser.Path.ToLowerInvariant(), label = typeName, fields = FormFields(browser) }, Today);
        var result = Assert.Single((await client.PushAsync(op)).Results);
        output.WriteLine($"main form offline {typeName}: {result.Status} {result.AppliedReferenceType}/{result.AppliedReferenceId} {result.Message}");
        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);
        _sentOffline.Add(op);
        return (await JournalLinesAfterAsync(lastJournal), result.AppliedReferenceId);
    }

    /// <summary>فورم خالی صفحهٔ GET، مثل کاربری که فقط فیلدهای لازم را پر می‌کند (متن یکتا، اولین گزینهٔ انتخاب).</summary>
    private async Task<string[][]> FillFormAsync(OfflineClient client, string path)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Add("Cookie", client.Cookie);
        var page = await client.Http.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var form = Regex.Matches(html, "<form\\b[^>]*method=\"post\"[^>]*>(.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m => m.Value).OrderByDescending(f => f.Length).First();
        form = Regex.Replace(form, "<template\\b.*?</template>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var fields = new List<string[]>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            var name = Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value;
            if (string.IsNullOrEmpty(name) || name.StartsWith("__", StringComparison.Ordinal)) continue;
            var type = Regex.Match(tag, "type=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            var value = WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
            if (type is "checkbox" or "radio")
            {
                if (Regex.IsMatch(tag, "\\bchecked\\b", RegexOptions.IgnoreCase)) fields.Add([name, string.IsNullOrEmpty(value) ? "on" : value]);
                continue;
            }

            if (string.IsNullOrEmpty(value) && tag.Contains("data-val-required", StringComparison.Ordinal))
            {
                value = type == "number" ? "1" : $"فورم {path}";
            }

            fields.Add([name, value]);
        }

        foreach (Match select in Regex.Matches(form, "<select\\b([^>]*)>(.*?)</select>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var name = Regex.Match(select.Groups[1].Value, "name=\"([^\"]+)\"").Groups[1].Value;
            if (string.IsNullOrEmpty(name)) continue;
            var options = Regex.Matches(select.Groups[2].Value, "<option\\b([^>]*)>", RegexOptions.IgnoreCase).Select(o => o.Groups[1].Value).ToList();
            var chosen = options.FirstOrDefault(o => Regex.IsMatch(o, "\\bselected\\b", RegexOptions.IgnoreCase))
                ?? (select.Groups[1].Value.Contains("data-val-required", StringComparison.Ordinal)
                    ? options.FirstOrDefault(o => !Regex.IsMatch(o, "value=\"\"")) : null);
            fields.Add([name, chosen is null ? "" : WebUtility.HtmlDecode(Regex.Match(chosen, "value=\"([^\"]*)\"").Groups[1].Value)]);
        }

        return fields.ToArray();
    }

    private readonly List<object> _sentOffline = [];

    private async Task<(List<JournalLineKey> Lines, string ReferenceId)> OfflineAsync(
        OfflineClient client, int userId, string type, object payload)
    {
        var lastJournal = await LastJournalIdAsync();
        var op = OfflineClient.Op(Guid.NewGuid(), userId, type, payload, Today);
        var response = await client.PushAsync(op);
        var result = Assert.Single(response.Results);
        output.WriteLine($"offline {type}: {result.Status} {result.Code} {result.AppliedReferenceType}/{result.AppliedReferenceId} {result.Message}");
        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);
        _sentOffline.Add(op);
        return (await JournalLinesAfterAsync(lastJournal), result.AppliedReferenceId!);
    }

    /// <summary>
    /// مسیر آنلاین: صفحهٔ GET همان فرم را باز می‌کند (کوکی Antiforgery، توکن فرم، FormToken و فیلدهای مخفیِ همان فرم)،
    /// همان فیلدهای کاربر را رویش می‌گذارد و POSTِ HTTP واقعی می‌فرستد؛ دقیقاً کاری که مرورگر می‌کند.
    /// </summary>
    private sealed record BrowserForm(string Path, Dictionary<string, string> Cookies, Dictionary<string, string?> Fields)
    {
        public string CookieHeader => string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}"));
    }

    private async Task<(List<JournalLineKey> Lines, int? CreatedId)> OnlineAsync(
        OfflineClient client, string typeName, object payload, Func<PTGOilSystem.Web.Data.ApplicationDbContext, Task<int?>> findCreated)
    {
        var browser = await BrowserFormAsync(client, typeName, payload);
        return await PostOnlineAsync(client, typeName, browser, findCreated);
    }

    /// <summary>همان کاری که مرورگر با فورم اصلی می‌کند: صفحهٔ GET، فیلدهای مخفی همان فورم و فیلدهای کاربر.</summary>
    private async Task<BrowserForm> BrowserFormAsync(OfflineClient client, string typeName, object payload)
    {
        var type = typeof(OfflineOperationTypes).GetField(typeName)!.GetValue(null)!.ToString()!;
        OfflineApplyPlan plan;
        await using (var planDb = server.CreateDbContext())
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload, OfflineClient.Json);
            var built = await new OfflineOperationApplier(planDb).BuildAsync(new OfflineSyncOperation
            {
                ClientOperationId = Guid.NewGuid(),
                OperationType = type,
                PayloadJson = json,
                BusinessDate = DateTime.UtcNow.Date,
                ClientCreatedAtUtc = DateTime.UtcNow
            }, CancellationToken.None);
            plan = built.Plan ?? throw new InvalidOperationException($"no plan for {type}: {built.Error}");
        }

        var path = $"/{plan.Controller}/{plan.Action}";
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in client.Cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            cookies[part[..index]] = part[(index + 1)..];
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
        var page = await client.Http.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        if (page.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var value in setCookies)
            {
                var pair = value.Split(';')[0];
                var index = pair.IndexOf('=');
                cookies[pair[..index]] = pair[(index + 1)..];
            }
        }

        var html = await page.Content.ReadAsStringAsync();
        var formHtml = Regex.Matches(html, "<form\\b[^>]*>(.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m => m.Value)
            .Where(f => Regex.IsMatch(f, "method=\"post\"", RegexOptions.IgnoreCase))
            .OrderByDescending(f => plan.Fields.Keys.Count(k => f.Contains($"name=\"{k}\"", StringComparison.Ordinal)))
            .First();

        // ردیف‌های پویا (Rows[n]) و <template> را JS صفحه می‌سازد؛ مثل مرورگر فقط سطرِ پرشدهٔ کاربر فرستاده می‌شود.
        formHtml = Regex.Replace(formHtml, "<template\\b.*?</template>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var form = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(formHtml, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            if (!Regex.IsMatch(tag, "type=\"hidden\"", RegexOptions.IgnoreCase)) continue;
            var name = Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value;
            if (string.IsNullOrEmpty(name) || form.ContainsKey(name) || name.StartsWith("Rows[", StringComparison.Ordinal) || name == "Rows.Index") continue;
            form[name] = WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
        }

        Assert.True(form.ContainsKey("__RequestVerificationToken"), $"{path}: antiforgery token not found in form");
        output.WriteLine($"{path} hidden: {string.Join(", ", form.Where(f => !f.Key.StartsWith("__", StringComparison.Ordinal)).Select(f => $"{f.Key}={f.Value}"))}");
        foreach (var (key, value) in plan.Fields)
        {
            form[key] = value;
        }

        return new BrowserForm(path, cookies, form);
    }

    private async Task<(List<JournalLineKey> Lines, int? CreatedId)> PostOnlineAsync(
        OfflineClient client, string typeName, BrowserForm browser, Func<PTGOilSystem.Web.Data.ApplicationDbContext, Task<int?>> findCreated)
    {
        var path = browser.Path;
        var lastJournal = await LastJournalIdAsync();
        using var post = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(browser.Fields.Where(f => f.Value is not null).Select(f => new KeyValuePair<string, string>(f.Key, f.Value!)))
        };
        post.Headers.Add("Cookie", browser.CookieHeader);
        var response = await client.Http.SendAsync(post);
        var body = await response.Content.ReadAsStringAsync();
        var errors = string.Join(" | ", Regex.Matches(body, "class=\"[^\"]*(?:validation-summary-errors|field-validation-error|alert-danger)[^\"]*\"[^>]*>(.*?)</", RegexOptions.Singleline)
            .Select(m => WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")).Trim()).Where(t => t.Length > 0).Take(5));
        output.WriteLine($"online {typeName}: {(int)response.StatusCode} {response.Headers.Location} {errors}");
        Assert.True(response.StatusCode == HttpStatusCode.Redirect || response.StatusCode == HttpStatusCode.Found
            || response.StatusCode == HttpStatusCode.SeeOther, $"online {path} did not commit: {(int)response.StatusCode} {errors}");

        await using var db = server.CreateDbContext();
        var created = await findCreated(db);
        Assert.NotNull(created);
        return (await JournalLinesAfterAsync(lastJournal), created);
    }

    private async Task<int> LastJournalIdAsync()
    {
        await using var db = server.CreateDbContext();
        return await db.JournalEntries.MaxAsync(j => (int?)j.Id) ?? 0;
    }

    private async Task<List<JournalLineKey>> JournalLinesAfterAsync(int lastJournalId)
    {
        await using var db = server.CreateDbContext();
        var lines = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId > lastJournalId)
            .Select(l => new { l.Account!.Code, l.Debit, l.Credit, l.TransactionCurrencyCode, l.PartyType, l.CashAccountId, l.TankId })
            .ToListAsync();
        return lines
            .Select(l => new JournalLineKey(l.Code, l.Debit, l.Credit, l.TransactionCurrencyCode, l.PartyType?.ToString(), l.CashAccountId is not null, l.TankId is not null))
            .OrderBy(l => l.AccountCode, StringComparer.Ordinal).ThenBy(l => l.Debit).ThenBy(l => l.Credit).ThenBy(l => l.PartyType)
            .ToList();
    }

    private static async Task<decimal> TankStockAsync(PTGOilSystem.Web.Data.ApplicationDbContext db, int tankId)
    {
        var movements = await db.InventoryMovements.AsNoTracking()
            .Where(m => m.StorageTankId == tankId)
            .Select(m => new { m.Direction, m.QuantityMt })
            .ToListAsync();
        return movements.Sum(m => m.Direction == MovementDirection.In ? m.QuantityMt : -m.QuantityMt);
    }

    private static async Task<decimal> PartyNetAsync(PTGOilSystem.Web.Data.ApplicationDbContext db, int? customerId = null, int? supplierId = null)
    {
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(e => (customerId != null && e.CustomerId == customerId) || (supplierId != null && e.SupplierId == supplierId))
            .Select(e => new { e.Side, e.AmountUsd })
            .ToListAsync();
        return rows.Sum(r => r.Side == LedgerSide.Debit ? r.AmountUsd : -r.AmountUsd);
    }

    private static async Task<decimal> JournalPartyNetAsync(PTGOilSystem.Web.Data.ApplicationDbContext db, AccountingPartyType type, int partyId)
    {
        var rows = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.PartyType == type && l.PartyId == partyId)
            .Select(l => new { l.Debit, l.Credit })
            .ToListAsync();
        return rows.Sum(r => r.Debit - r.Credit);
    }

    private static readonly SemaphoreSlim SharedLock = new(1, 1);
    private static Shared? _shared;

    private async Task<Shared> SeedSharedAsync()
    {
        await SharedLock.WaitAsync();
        try
        {
            if (_shared is not null)
            {
                return _shared;
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

            var product = new Product { Code = "DSL-ACC", Name = "Diesel Accounting", UnitOfMeasure = "MT" };
            var expenseType = new ExpenseType { Code = "EXP-ACC", Name = "Accounting Expense" };
            var truck = new Truck { PlateNumber = "KBL-ACC-1" };
            db.AddRange(product, expenseType, truck);
            await db.SaveChangesAsync();
            var cash = new CashAccount { Code = "CA-ACC", Name = "Accounting Cash", CompanyId = company.Id, Currency = "USD", AccountType = CashAccountType.Cash };
            db.Add(cash);
            await db.SaveChangesAsync();

            // سال مالی باز برای امروز (فقط در همین دیتابیس موقت).
            var today = DateTime.UtcNow.Date;
            if (!await db.FiscalYears.AnyAsync(y => y.CompanyId == company.Id && y.StartDate <= today && y.EndDate >= today))
            {
                var year = new FiscalYear
                {
                    CompanyId = company.Id,
                    Name = today.Year.ToString(CultureInfo.InvariantCulture),
                    StartDate = new DateTime(today.Year, 1, 1),
                    EndDate = new DateTime(today.Year, 12, 31),
                    Status = FiscalYearStatus.Open,
                    IsCurrent = true
                };
                db.FiscalYears.Add(year);
                await db.SaveChangesAsync();
                for (var month = 1; month <= 12; month++)
                {
                    var start = new DateTime(today.Year, month, 1);
                    db.FiscalPeriods.Add(new FiscalPeriod
                    {
                        CompanyId = company.Id,
                        FiscalYearId = year.Id,
                        PeriodNumber = month,
                        Name = $"P{month}",
                        StartDate = start,
                        EndDate = start.AddMonths(1).AddDays(-1),
                        Status = FiscalPeriodStatus.Open
                    });
                }

                await db.SaveChangesAsync();
            }

            // سرفصل و AccountingSettings با همان Seeder برنامه (همان کاری که --accounting-backfill انجام می‌دهد).
            await new AccountingChartSeeder(db, Options.Create(new AccountingOptions { Enabled = true })).SeedAsync();
            Assert.True(await db.AccountingSettings.AnyAsync(s => s.CompanyId == company.Id), "accounting settings were not seeded");

            var admin = await db.Users.SingleAsync(u => u.Username == "admin");
            return _shared = new Shared(company.Id, product.Id, expenseType.Id, cash.Id, truck.Id, admin.Id);
        }
        finally
        {
            SharedLock.Release();
        }
    }

    private async Task<Track> SeedTrackAsync(string name)
    {
        var shared = _shared!;
        await using var db = server.CreateDbContext();
        var customer = new Customer { Code = $"C-ACC-{name}", Name = $"Accounting Customer {name}" };
        var supplier = new Supplier { Code = $"S-ACC-{name}", Name = $"Accounting Supplier {name}" };
        var terminal = new Terminal { Code = $"T-ACC-{name}", Name = $"Accounting Terminal {name}" };
        db.AddRange(customer, supplier, terminal);
        await db.SaveChangesAsync();

        var tank = new StorageTank { TerminalId = terminal.Id, TankCode = $"TK-ACC-{name}", ProductId = shared.ProductId, CapacityMt = 10000 };
        var contract = new Contract
        {
            ContractName = $"Accounting Purchase {name}", ContractNumber = $"PC-ACC-{name}", ContractType = ContractType.Purchase,
            Status = ContractStatus.Active, CompanyId = shared.CompanyId, ProductId = shared.ProductId, SupplierId = supplier.Id,
            ContractDate = DateTime.UtcNow.Date.AddDays(-30), QuantityMt = 5000, UnitPriceUsd = 600, Currency = "USD",
            // قیمت نهایی قطعی؛ بدون آن، خرید عمداً PURCHASE_PRICE_PENDING می‌ماند و سند ندارد.
            ManualFinalPriceUsd = 600
        };
        db.AddRange(tank, contract);
        await db.SaveChangesAsync();
        return new Track(name, customer.Id, supplier.Id, contract.Id, terminal.Id, tank.Id);
    }
}
