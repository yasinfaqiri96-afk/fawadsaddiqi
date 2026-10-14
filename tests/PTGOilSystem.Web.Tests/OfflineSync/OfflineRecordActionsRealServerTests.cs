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
/// ویرایش، لغو و برگشت اسناد موجود — آنلاین در برابر آفلاین روی برنامهٔ واقعی و PostgreSQL موقت با حسابداری دوطرفهٔ فعال.
/// دو مسیر موازی با اسناد پایهٔ یکسان؛ هر عملیات یک‌بار با POSTِ واقعی مرورگر (همان صفحه، همان فیلدها) و یک‌بار از
/// صف آفلاین (همان فیلدهای همان فورم + زمان سرورِ صفحه) اجرا می‌شود. سطرهای اسناد حسابداری تازه، موجودی مخزن، مانده
/// مشتری/فروشنده و توازن همهٔ اسناد باید یکسان باشد. تکرار، هم‌زمانی، قطع پس از ثبت و تغییر کاربر دیگر هم سنجیده می‌شود.
/// </summary>
[Collection(OfflineRealServerCollection.Name)]
public sealed class OfflineRecordActionsRealServerTests(OfflineRealServerFixture server, ITestOutputHelper output)
{
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record Seed(int CompanyId, int ProductId, int ExpenseTypeId, int CashAccountId, int TruckId, int AdminId);

    private sealed record Docs(string Name, int CustomerId, int TankId, int LoadingId, int ReceiptId, int SaleId, int PaymentId, int ExpenseId);

    private sealed record JournalLineKey(string AccountCode, decimal Debit, decimal Credit, string Currency, string? PartyType);

    /// <summary>فورم یک صفحهٔ واقعی، همان‌طور که مرورگر می‌فرستد (مقادیر پیش‌فرض صفحه + کوکی Antiforgery + زمان رندر سرور).</summary>
    private sealed record PageForm(string ActionPath, List<KeyValuePair<string, string>> Fields, Dictionary<string, string> Cookies, string RenderedAt)
    {
        public PageForm With(params (string Name, string Value)[] values)
        {
            var fields = Fields.Where(f => values.All(v => !string.Equals(v.Name, f.Key, StringComparison.Ordinal))).ToList();
            fields.AddRange(values.Select(v => new KeyValuePair<string, string>(v.Name, v.Value)));
            return this with { Fields = fields };
        }

        public string[][] OfflineFields => Fields.Where(f => f.Key is not "__RequestVerificationToken" and not "__FormToken")
            .Select(f => new[] { f.Key, f.Value }).ToArray();
    }

    [RealServerFact]
    public async Task Edit_Cancel_And_Reverse_Offline_Post_The_Same_Journals_Stock_And_Balances_As_Online()
    {
        var seed = await SeedAsync();
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var on = await DocsAsync(admin, seed, "RA-ON");
        var off = await DocsAsync(admin, seed, "RA-OFF");
        var steps = new List<(string Name, List<JournalLineKey> Online, List<JournalLineKey> Offline)>();

        async Task Step(string name, Func<Docs, Task<PageForm>> form, Func<Docs, string> offlinePath)
        {
            var onLines = await OnlineAsync(admin, await form(on));
            var offLines = await OfflineAsync(admin, seed.AdminId, await form(off), offlinePath(off));
            steps.Add((name, onLines, offLines));
        }

        // ۱) ویرایش دریافت: مبلغ 125.5 → 200 (همان فورم ویرایش با Version همان صفحه).
        await Step("Payment.Edit",
            async d => (await FormAsync(admin, $"/Payments/Edit/{d.PaymentId}", $"/payments/edit/{d.PaymentId}")).With(("Amount", "200")),
            d => $"/payments/edit/{d.PaymentId}");
        // ۲) لغو مصرف از فهرست (شناسه در فیلد مخفی id).
        await Step("Expense.Cancel",
            d => FormAsync(admin, "/Expenses", "/expenses/cancel", ("id", d.ExpenseId.ToString(CultureInfo.InvariantCulture))),
            _ => "/expenses/cancel");
        // ۳) ویرایش مقدار فروشِ دارای سند: قاعدهٔ سرور («لغو و ثبت جایگزین») آنلاین و آفلاین یکسان رد می‌کند؛ بدون اثر.
        var journalsBeforeEdit = await JournalCountAsync();
        var onEdit = (await FormAsync(admin, $"/Sales/Edit/{on.SaleId}", $"/sales/edit/{on.SaleId}")).With(("QuantityMt", "12"));
        var onEditMessage = await OnlineRejectedAsync(admin, onEdit);
        var offEdit = (await FormAsync(admin, $"/Sales/Edit/{off.SaleId}", $"/sales/edit/{off.SaleId}")).With(("QuantityMt", "12"));
        var offEditResult = Assert.Single((await admin.PushAsync(OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.FormSubmit,
            new { path = $"/sales/edit/{off.SaleId}", label = "ویرایش فروش", fields = offEdit.OfflineFields }, Today,
            baseVersion: "t:" + offEdit.RenderedAt))).Results);
        output.WriteLine($"sale edit: online «{onEditMessage}» | offline {offEditResult.Status} «{offEditResult.Message}»");
        // هر دو مسیر رد شدند (متن پیام به ترتیب اعتبارسنجی سرور بستگی دارد)؛ هیچ اثری بر سند، فروش یا موجودی نیست.
        Assert.Equal(nameof(OfflineSyncStatus.Conflict), offEditResult.Status);
        Assert.False(string.IsNullOrWhiteSpace(offEditResult.Message));
        Assert.False(string.IsNullOrWhiteSpace(onEditMessage));
        Assert.Equal(journalsBeforeEdit, await JournalCountAsync());
        await using (var afterEdit = server.CreateDbContext())
        {
            Assert.Equal(10m, (await afterEdit.SalesTransactions.SingleAsync(x => x.Id == on.SaleId)).QuantityMt);
            Assert.Equal(10m, (await afterEdit.SalesTransactions.SingleAsync(x => x.Id == off.SaleId)).QuantityMt);
        }
        // ۴) ابطال فروش (برگشت موجودی و سند فروش/بهای تمام‌شده).
        await Step("Sale.Cancel",
            async d => (await FormAsync(admin, $"/Sales/Correct/{d.SaleId}", $"/sales/cancel/{d.SaleId}")).With(("cancelReason", "ابطال آزمایشی")),
            d => $"/sales/cancel/{d.SaleId}");
        // ۵) لغو رسید بار (پس از ابطال فروش؛ موجودی مخزن به صفر برمی‌گردد).
        await Step("LoadingReceipt.Cancel",
            async d => (await FormAsync(admin, $"/LoadingReceipts/Details/{d.ReceiptId}", $"/loadingreceipts/cancel/{d.ReceiptId}"))
                .With(("reason", "لغو آزمایشی")),
            d => $"/loadingreceipts/cancel/{d.ReceiptId}");
        // ۶) ویرایش طرف‌حساب (معلومات پایه، بدون سند مالی).
        await Step("Customer.Edit",
            async d => (await FormAsync(admin, $"/Customers/Edit/{d.CustomerId}", $"/customers/edit/{d.CustomerId}"))
                .With(("Phone", "0700000000")),
            d => $"/customers/edit/{d.CustomerId}");

        foreach (var (name, online, offline) in steps)
        {
            output.WriteLine($"== {name}: online {online.Count} lines, offline {offline.Count} lines");
            Assert.True(online.SequenceEqual(offline),
                $"{name}: online and offline journals differ.\nonline: {string.Join("; ", online)}\noffline: {string.Join("; ", offline)}");
        }

        // دریافت و مصرفِ بدون قرارداد در هر دو مسیر سند GL ندارند (تصمیم تجاریِ باز)؛ اثرشان در دفتر عملیاتی سنجیده می‌شود.
        Assert.NotEmpty(steps.Single(s => s.Name == "Sale.Cancel").Offline);
        Assert.NotEmpty(steps.Single(s => s.Name == "LoadingReceipt.Cancel").Offline);
        await using var check = server.CreateDbContext();
        Assert.Equal(await TankStockAsync(check, on.TankId), await TankStockAsync(check, off.TankId));
        Assert.Equal(0m, await TankStockAsync(check, off.TankId));
        Assert.Equal(await PartyNetAsync(check, on.CustomerId), await PartyNetAsync(check, off.CustomerId));
        Assert.Equal(200m, (await check.PaymentTransactions.SingleAsync(p => p.Id == off.PaymentId)).Amount);
        Assert.True((await check.SalesTransactions.SingleAsync(s => s.Id == off.SaleId)).IsCancelled);
        Assert.Equal("0700000000", (await check.Customers.SingleAsync(c => c.Id == off.CustomerId)).Phone);
        Assert.Equal(
            (await check.ExpenseTransactions.SingleAsync(e => e.Id == on.ExpenseId)).IsCancelled,
            (await check.ExpenseTransactions.SingleAsync(e => e.Id == off.ExpenseId)).IsCancelled);
        Assert.Empty(await check.JournalEntryLines.AsNoTracking()
            .GroupBy(l => l.JournalEntryId)
            .Select(g => new { g.Key, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) })
            .Where(x => x.Debit != x.Credit)
            .ToListAsync());
    }

    [RealServerFact]
    public async Task Repeated_Concurrent_And_Crash_Recovered_Record_Actions_Apply_Exactly_Once()
    {
        var seed = await SeedAsync();
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var docs = await DocsAsync(admin, seed, "RA-ONCE");
        var form = await FormAsync(admin, $"/Payments/Edit/{docs.PaymentId}", $"/payments/edit/{docs.PaymentId}");
        var id = Guid.NewGuid();
        var op = OfflineClient.Op(id, seed.AdminId, OfflineOperationTypes.FormSubmit,
            new { path = $"/payments/edit/{docs.PaymentId}", label = "ویرایش دریافت", fields = form.With(("Amount", "300")).OfflineFields },
            Today, baseVersion: "t:" + form.RenderedAt, entityId: docs.PaymentId.ToString(CultureInfo.InvariantCulture));
        int LedgerRows(PTGOilSystem.Web.Data.ApplicationDbContext db) => db.LedgerEntries.Count(e => e.CustomerId == docs.CustomerId);

        var results = await Task.WhenAll(admin.PushAsync(op), admin.PushAsync(op), admin.PushAsync(op));
        var final = Assert.Single((await admin.StatusAsync(id)).Operations);
        output.WriteLine($"concurrent: {string.Join(", ", results.Select(r => r.Results[0].Status))} final {final.Status} {final.Note}");
        Assert.Equal(nameof(OfflineSyncStatus.Applied), final.Status);
        var journalsAfterFirst = await JournalCountAsync();
        int ledgerAfterFirst;
        await using (var db = server.CreateDbContext())
        {
            ledgerAfterFirst = LedgerRows(db);
            Assert.Equal(300m, (await db.PaymentTransactions.SingleAsync(p => p.Id == docs.PaymentId)).Amount);
        }

        // قطع پس از ثبت: سند و توکن ثبت شد ولی وضعیت عملیات ثبت نشد (سرور وسط کار متوقف شد).
        await using (var db = server.CreateDbContext())
        {
            await db.OfflineSyncOperations.Where(o => o.ClientOperationId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OfflineSyncStatus.Received));
        }

        var again = Assert.Single((await admin.PushAsync(op)).Results);
        output.WriteLine($"after crash: {again.Status} {again.Message}");
        Assert.Equal(nameof(OfflineSyncStatus.Applied), again.Status);
        Assert.Equal(journalsAfterFirst, await JournalCountAsync());
        await using var check = server.CreateDbContext();
        Assert.Equal(ledgerAfterFirst, LedgerRows(check));
        Assert.Equal(300m, (await check.PaymentTransactions.SingleAsync(p => p.Id == docs.PaymentId)).Amount);
        Assert.Equal(1, await check.ProcessedFormTokens.CountAsync(t => t.Token == OfflineOperationApplier.FormTokenFor(id)));
    }

    [RealServerFact]
    public async Task Record_Changed_Online_By_Another_User_Is_Kept_As_Conflict_And_Applies_After_Review()
    {
        var seed = await SeedAsync();
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var docs = await DocsAsync(admin, seed, "RA-STALE");
        // نسخهٔ دستگاه: فورم ویرایش مصرف پیش از تغییر کاربر دیگر.
        var deviceCopy = await FormAsync(admin, $"/Customers/Edit/{docs.CustomerId}", $"/customers/edit/{docs.CustomerId}");
        await Task.Delay(1500);
        await using (var other = server.CreateDbContext())
        {
            // کاربر دیگر (بدون کاربر جاری در این DbContext) همان مشتری را آنلاین تغییر داد.
            var customer = await other.Customers.SingleAsync(c => c.Id == docs.CustomerId);
            customer.Notes = "تغییر هم‌زمان کاربر دیگر";
            await other.SaveChangesAsync();
        }

        var id = Guid.NewGuid();
        object Edit(int revision, string renderedAt) => OfflineClient.Op(id, seed.AdminId, OfflineOperationTypes.FormSubmit,
            new { path = $"/customers/edit/{docs.CustomerId}", label = "ویرایش مشتری", fields = deviceCopy.With(("Phone", "0799999999")).OfflineFields },
            Today, revision: revision, baseVersion: "t:" + renderedAt);

        var stale = Assert.Single((await admin.PushAsync(Edit(1, deviceCopy.RenderedAt))).Results);
        output.WriteLine($"stale: {stale.Status} {stale.Message}");
        Assert.Equal(nameof(OfflineSyncStatus.Conflict), stale.Status);
        Assert.Contains("کاربر دیگری", stale.Message);
        await using (var db = server.CreateDbContext())
        {
            Assert.NotEqual("0799999999", (await db.Customers.SingleAsync(c => c.Id == docs.CustomerId)).Phone);
        }

        // کاربر صفحهٔ تازه را دید و همان عملیات را دوباره فرستاد.
        var fresh = await FormAsync(admin, $"/Customers/Edit/{docs.CustomerId}", $"/customers/edit/{docs.CustomerId}");
        var resent = Assert.Single((await admin.PushAsync(Edit(2, fresh.RenderedAt))).Results);
        output.WriteLine($"resent: {resent.Status} {resent.Message}");
        Assert.Equal(nameof(OfflineSyncStatus.Applied), resent.Status);
        await using var check = server.CreateDbContext();
        Assert.Equal("0799999999", (await check.Customers.SingleAsync(c => c.Id == docs.CustomerId)).Phone);
    }

    [RealServerFact]
    public async Task Every_Record_Action_Target_Replays_Through_Its_Real_Action_Without_Unexpected_Errors()
    {
        var seed = await SeedAsync();
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var unexpected = new List<string>();
        // عملیات سراسری که در این دیتابیسِ مشترکِ تست، کار تست‌های دیگر را تغییر می‌دهد (قفل دوره، بستن سال، معاشات…)
        // فقط با شناسهٔ ناموجود/فیلد خالی سنجیده نمی‌شوند؛ مسیرشان همان مسیر Ambient است.
        var global = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OperationalPeriodLocks.Close", "OperationalPeriodLocks.Reopen", "FiscalYears.CreateInitialYear", "FiscalYears.CreateNextYear",
            "TrialClose.RunTrialClose", "TrialClose.ApplyRevaluation", "FinalClose.Close", "ReopenFiscalYear.Reopen",
            "Payroll.Generate", "Payroll.Finalize", "Payroll.PayLines", "Payroll.Reopen", "Payroll.SaveLines",
            "Expenses.CancelAllGroups", "ViaSarrafPayments.ApplyConfirmed", "ChartOfAccounts.Mapping", "HrOrganization.LinkLegacyText",
            "HrSettings.SaveSettings", "Attendance.Save", "CustomsDeclarations.DeleteAll"
        };
        var tested = 0;
        foreach (var target in OfflineFormRegistry.All.Where(t => t.Mode == OfflineApplyMode.Ambient))
        {
            if (global.Contains($"{target.Controller}.{target.Action}"))
            {
                continue;
            }

            var path = target.RouteId ? $"{target.Path}/987654" : target.Path;
            var op = OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.FormSubmit,
                new { path, label = target.Label, fields = target.RouteId ? Array.Empty<string[]>() : new[] { new[] { "id", "987654" } } },
                Today, baseVersion: "t:" + DateTime.UtcNow.ToString("O"));
            var result = Assert.Single((await admin.PushAsync(op)).Results);
            tested++;
            output.WriteLine($"{path}: {result.Status} {result.Message}");
            if (result.Status is not (nameof(OfflineSyncStatus.Applied) or nameof(OfflineSyncStatus.Conflict))
                || (result.Message ?? "").Contains("پیش‌بینی‌نشده", StringComparison.Ordinal)
                || (result.Message ?? "").Contains("transaction", StringComparison.OrdinalIgnoreCase))
            {
                unexpected.Add($"{path}: {result.Status} {result.Message}");
            }
        }

        output.WriteLine($"tested {tested} record-action targets");
        Assert.True(unexpected.Count == 0, string.Join("\n", unexpected));
        Assert.True(tested > 150);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<List<JournalLineKey>> OnlineAsync(OfflineClient client, PageForm form)
    {
        var last = await LastJournalIdAsync();
        using var post = new HttpRequestMessage(HttpMethod.Post, form.ActionPath)
        {
            Content = new FormUrlEncodedContent(form.Fields)
        };
        post.Headers.Add("Cookie", string.Join("; ", form.Cookies.Select(c => $"{c.Key}={c.Value}")));
        var response = await client.Http.SendAsync(post);
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"online {form.ActionPath}: {(int)response.StatusCode} {response.Headers.Location}");
        var errors = string.Join(" | ", Regex.Matches(body,
                "class=\"[^\"]*(?:validation-summary-errors|field-validation-error|alert-danger|ak-form-alert)[^\"]*\"[^>]*>(.*?)</(?:div|span|ul)>",
                RegexOptions.Singleline)
            .Select(m => WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")).Trim()).Where(t => t.Length > 0).Take(6));
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther,
            $"online {form.ActionPath} did not commit: {(int)response.StatusCode} {errors}");
        await AssertNoErrorFlashAsync(client, response);
        return await JournalLinesAfterAsync(last);
    }

    /// <summary>POSTِ واقعی که قاعدهٔ سرور ردش می‌کند (همان فورم با پیام خطا، بدون Redirect)؛ پیام برمی‌گردد.</summary>
    private async Task<string> OnlineRejectedAsync(OfflineClient client, PageForm form)
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, form.ActionPath) { Content = new FormUrlEncodedContent(form.Fields) };
        post.Headers.Add("Cookie", string.Join("; ", form.Cookies.Select(c => $"{c.Key}={c.Value}")));
        var response = await client.Http.SendAsync(post);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return string.Join(" | ", Regex.Matches(body,
                "class=\"[^\"]*(?:validation-summary-errors|field-validation-error|alert-danger|ak-form-alert)[^\"]*\"[^>]*>(.*?)</(?:div|span|ul)>",
                RegexOptions.Singleline)
            .Select(m => WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")).Trim()).Where(t => t.Length > 0).Distinct().Take(3));
    }

    /// <summary>Redirect همراه پیام خطای TempData یعنی ثبت نشده؛ صفحهٔ مقصد همان پیام را نشان می‌دهد.</summary>
    private static async Task AssertNoErrorFlashAsync(OfflineClient client, HttpResponseMessage response)
    {
        if (response.Headers.Location is not { } location)
        {
            return;
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, location);
        get.Headers.Add("Cookie", client.Cookie);
        var page = await (await client.Http.SendAsync(get)).Content.ReadAsStringAsync();
        Assert.DoesNotMatch("alert-danger[^>]*>\\s*[^<\\s]", page);
    }

    private async Task<List<JournalLineKey>> OfflineAsync(OfflineClient client, int userId, PageForm form, string path)
    {
        var last = await LastJournalIdAsync();
        var op = OfflineClient.Op(Guid.NewGuid(), userId, OfflineOperationTypes.FormSubmit,
            new { path, label = path, fields = form.OfflineFields, page = form.ActionPath }, Today, baseVersion: "t:" + form.RenderedAt);
        var result = Assert.Single((await client.PushAsync(op)).Results);
        output.WriteLine($"offline {path}: {result.Status} {result.AppliedReferenceType}/{result.AppliedReferenceId} {result.Message}");
        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);
        return await JournalLinesAfterAsync(last);
    }

    /// <summary>صفحهٔ GET واقعی، فورمِ POST با همین آدرس اکشن (و در صورت نیاز فیلد مشخص)، با همهٔ مقادیر پیش‌فرض.</summary>
    private async Task<PageForm> FormAsync(OfflineClient client, string pagePath, string actionPath, (string Name, string Value)? having = null)
    {
        var cookies = client.Cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split('=', 2)).ToDictionary(c => c[0], c => c.Length > 1 ? c[1] : "");
        using var get = new HttpRequestMessage(HttpMethod.Get, pagePath);
        get.Headers.Add("Cookie", client.Cookie);
        var page = await client.Http.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        if (page.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var pair in setCookies.Select(v => v.Split(';')[0]))
            {
                var index = pair.IndexOf('=');
                cookies[pair[..index]] = pair[(index + 1)..];
            }
        }

        var html = await page.Content.ReadAsStringAsync();
        var renderedAt = Regex.Match(html, "data-ptg-rendered=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(renderedAt), $"{pagePath}: page has no server render time marker");
        var form = Regex.Matches(html, "<form\\b([^>]*)>(.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Where(m => Regex.Match(m.Groups[1].Value, "action=\"([^\"]*)\"").Groups[1].Value is { Length: > 0 } action
                && string.Equals(WebUtility.HtmlDecode(action).Split('?')[0].TrimEnd('/'), actionPath, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[2].Value)
            .FirstOrDefault(body => having is not { } h
                || Regex.IsMatch(body, $"name=\"{h.Name}\"[^>]*value=\"{h.Value}\"|value=\"{h.Value}\"[^>]*name=\"{h.Name}\"", RegexOptions.IgnoreCase));
        Assert.True(form is not null, $"{pagePath}: no POST form for {actionPath}");
        form = Regex.Replace(form!, "<template\\b.*?</template>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            var name = WebUtility.HtmlDecode(Regex.Match(tag, "\\bname=\"([^\"]+)\"").Groups[1].Value);
            var type = Regex.Match(tag, "\\btype=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || type is "submit" or "button" or "file" || Regex.IsMatch(tag, "\\bdisabled\\b")) continue;
            var value = WebUtility.HtmlDecode(Regex.Match(tag, "\\bvalue=\"([^\"]*)\"").Groups[1].Value);
            if (type is "checkbox" or "radio")
            {
                if (Regex.IsMatch(tag, "\\bchecked\\b", RegexOptions.IgnoreCase)) fields.Add(new(name, string.IsNullOrEmpty(value) ? "on" : value));
                continue;
            }

            fields.Add(new(name, value));
        }

        foreach (Match select in Regex.Matches(form, "<select\\b([^>]*)>(.*?)</select>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var name = WebUtility.HtmlDecode(Regex.Match(select.Groups[1].Value, "\\bname=\"([^\"]+)\"").Groups[1].Value);
            if (string.IsNullOrEmpty(name) || Regex.IsMatch(select.Groups[1].Value, "\\bdisabled\\b")) continue;
            var options = Regex.Matches(select.Groups[2].Value, "<option\\b([^>]*)>", RegexOptions.IgnoreCase).Select(o => o.Groups[1].Value).ToList();
            var chosen = options.FirstOrDefault(o => Regex.IsMatch(o, "\\bselected\\b", RegexOptions.IgnoreCase)) ?? options.FirstOrDefault();
            fields.Add(new(name, chosen is null ? "" : WebUtility.HtmlDecode(Regex.Match(chosen, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        foreach (Match area in Regex.Matches(form, "<textarea\\b([^>]*)>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var name = WebUtility.HtmlDecode(Regex.Match(area.Groups[1].Value, "\\bname=\"([^\"]+)\"").Groups[1].Value);
            if (!string.IsNullOrEmpty(name)) fields.Add(new(name, WebUtility.HtmlDecode(area.Groups[2].Value)));
        }

        Assert.Contains(fields, f => f.Key == "__RequestVerificationToken");
        return new PageForm(actionPath, fields, cookies, renderedAt);
    }

    private async Task<Docs> DocsAsync(OfflineClient client, Seed seed, string name)
    {
        int customerId, supplierId, contractId, tankId;
        await using (var db = server.CreateDbContext())
        {
            var customer = new Customer { Code = $"C-{name}", Name = $"Record Customer {name}" };
            var supplier = new Supplier { Code = $"S-{name}", Name = $"Record Supplier {name}" };
            var terminal = new Terminal { Code = $"T-{name}", Name = $"Record Terminal {name}" };
            db.AddRange(customer, supplier, terminal);
            await db.SaveChangesAsync();
            var tank = new StorageTank { TerminalId = terminal.Id, TankCode = $"TK-{name}", ProductId = seed.ProductId, CapacityMt = 10000 };
            var contract = new Contract
            {
                ContractName = $"Record Purchase {name}", ContractNumber = $"PC-{name}", ContractType = ContractType.Purchase,
                Status = ContractStatus.Active, CompanyId = seed.CompanyId, ProductId = seed.ProductId, SupplierId = supplier.Id,
                ContractDate = DateTime.UtcNow.Date.AddDays(-30), QuantityMt = 5000, UnitPriceUsd = 600, Currency = "USD",
                ManualFinalPriceUsd = 600
            };
            db.AddRange(tank, contract);
            await db.SaveChangesAsync();
            (customerId, supplierId, contractId, tankId) = (customer.Id, supplier.Id, contract.Id, tank.Id);
        }

        async Task<string> Apply(string type, object payload)
        {
            var result = Assert.Single((await client.PushAsync(OfflineClient.Op(Guid.NewGuid(), seed.AdminId, type, payload, Today))).Results);
            Assert.True(result.Status == nameof(OfflineSyncStatus.Applied), $"{name} {type}: {result.Status} {result.Message}");
            return result.AppliedReferenceId!;
        }

        await Apply(OfflineOperationTypes.LoadingNote, new { contractId, productId = seed.ProductId, quantity = 40m, truckId = seed.TruckId, truckPlate = "KBL-REC-1", reference = $"BL-{name}" });
        await using var read = server.CreateDbContext();
        var loadingId = await read.LoadingRegisters.Where(l => l.ContractId == contractId).MaxAsync(l => l.Id);
        await Apply(OfflineOperationTypes.ReceiptNote, new { loadingId, tankId, quantity = 40m, reference = $"RC-{name}" });
        var receiptId = await read.LoadingReceipts.Where(r => r.LoadingRegisterId == loadingId).MaxAsync(r => r.Id);
        await Apply(OfflineOperationTypes.SaleRequest, new { customerId, productId = seed.ProductId, tankId, quantity = 10m, unitPrice = 700m, currency = "USD", reference = $"S-{name}" });
        var saleId = await read.SalesTransactions.Where(s => s.TicketSerialNumber == $"S-{name}").MaxAsync(s => s.Id);
        await Apply(OfflineOperationTypes.CustomerReceiptRequest, new { customerId, cashAccountId = seed.CashAccountId, amount = 125.5m, currency = "USD", reference = $"CR-{name}" });
        var paymentId = await read.PaymentTransactions.Where(p => p.Reference == $"CR-{name}").MaxAsync(p => p.Id);
        await Apply(OfflineOperationTypes.ExpenseRequest, new { expenseTypeId = seed.ExpenseTypeId, amount = 15m, currency = "USD", cashAccountId = seed.CashAccountId, description = $"مصرف {name}" });
        var expenseId = await read.ExpenseTransactions.Where(e => e.Description.Contains($"مصرف {name}")).MaxAsync(e => e.Id);
        _ = supplierId;
        return new Docs(name, customerId, tankId, loadingId, receiptId, saleId, paymentId, expenseId);
    }

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static Seed? _seed;

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

            var product = new Product { Code = "DSL-REC", Name = "Diesel Record Actions", UnitOfMeasure = "MT" };
            var expenseType = new ExpenseType { Code = "EXP-REC", Name = "Record Actions Expense" };
            var truck = new Truck { PlateNumber = "KBL-REC-1" };
            db.AddRange(product, expenseType, truck);
            await db.SaveChangesAsync();
            var cash = new CashAccount { Code = "CA-REC", Name = "Record Actions Cash", CompanyId = company.Id, Currency = "USD", AccountType = CashAccountType.Cash };
            db.Add(cash);
            await db.SaveChangesAsync();

            var today = DateTime.UtcNow.Date;
            if (!await db.FiscalYears.AnyAsync(y => y.CompanyId == company.Id && y.StartDate <= today && y.EndDate >= today))
            {
                var year = new FiscalYear
                {
                    CompanyId = company.Id, Name = today.Year.ToString(CultureInfo.InvariantCulture),
                    StartDate = new DateTime(today.Year, 1, 1), EndDate = new DateTime(today.Year, 12, 31),
                    Status = FiscalYearStatus.Open, IsCurrent = true
                };
                db.FiscalYears.Add(year);
                await db.SaveChangesAsync();
                for (var month = 1; month <= 12; month++)
                {
                    var start = new DateTime(today.Year, month, 1);
                    db.FiscalPeriods.Add(new FiscalPeriod
                    {
                        CompanyId = company.Id, FiscalYearId = year.Id, PeriodNumber = month, Name = $"P{month}",
                        StartDate = start, EndDate = start.AddMonths(1).AddDays(-1), Status = FiscalPeriodStatus.Open
                    });
                }

                await db.SaveChangesAsync();
            }

            await new AccountingChartSeeder(db, Options.Create(new AccountingOptions { Enabled = true })).SeedAsync();
            var admin = await db.Users.SingleAsync(u => u.Username == "admin");
            return _seed = new Seed(company.Id, product.Id, expenseType.Id, cash.Id, truck.Id, admin.Id);
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private async Task<int> LastJournalIdAsync()
    {
        await using var db = server.CreateDbContext();
        return await db.JournalEntries.MaxAsync(j => (int?)j.Id) ?? 0;
    }

    private async Task<int> JournalCountAsync()
    {
        await using var db = server.CreateDbContext();
        return await db.JournalEntries.CountAsync();
    }

    private async Task<List<JournalLineKey>> JournalLinesAfterAsync(int lastJournalId)
    {
        await using var db = server.CreateDbContext();
        var lines = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId > lastJournalId)
            .Select(l => new { l.Account!.Code, l.Debit, l.Credit, l.TransactionCurrencyCode, l.PartyType })
            .ToListAsync();
        return lines
            .Select(l => new JournalLineKey(l.Code, l.Debit, l.Credit, l.TransactionCurrencyCode, l.PartyType?.ToString()))
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

    private static async Task<decimal> PartyNetAsync(PTGOilSystem.Web.Data.ApplicationDbContext db, int customerId)
    {
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.CustomerId == customerId)
            .Select(e => new { e.Side, e.AmountUsd })
            .ToListAsync();
        return rows.Sum(r => r.Side == LedgerSide.Debit ? r.AmountUsd : -r.AmountUsd);
    }
}
