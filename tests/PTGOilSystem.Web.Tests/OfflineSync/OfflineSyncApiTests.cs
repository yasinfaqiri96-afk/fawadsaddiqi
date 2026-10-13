using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.OfflineSync;
using PTGOilSystem.Web.Services.OperationalPeriod;
using PTGOilSystem.Web.Tests.MobileApi;
using Xunit;
using static PTGOilSystem.Web.Tests.MobileApi.MobileApiTestHost;

namespace PTGOilSystem.Web.Tests.OfflineSync;

/// <summary>
/// مشعل آفلاین — /api/sync/v1 از طریق pipeline واقعی HTTP (احراز هویت، ناوبری، فیلترها) روی SQLite
/// تا unique index و ExecuteUpdate واقعاً اجرا شوند. شمارهٔ TEST‌ها مطابق فهرست تست‌های الزامی است.
/// </summary>
public sealed class OfflineSyncApiTests
{
    private const string Base = "/api/sync/v1";
    private static readonly Dictionary<string, string> OfflineHeader = new() { ["X-PTG-Offline"] = "1" };

    private sealed class Harness : IAsyncDisposable
    {
        public required MobileApiTestHost Host { get; init; }
        public required FakePeriodGuard Period { get; init; }
        public required FakeDispatcher Dispatcher { get; init; }
        public required string Cookie { get; set; }
        public required User User { get; init; }
        public int CompanyId { get; init; }
        public Guid DeviceId { get; } = Guid.NewGuid();

        public Task<HttpResponseMessage> Post(string path, object body, string? cookie = null, bool header = true)
            => Host.SendAsync(HttpMethod.Post, Base + path, cookie: cookie ?? Cookie, body: body,
                headers: header ? OfflineHeader : null);

        public Task<HttpResponseMessage> Get(string path, string? cookie = null)
            => Host.SendAsync(HttpMethod.Get, Base + path, cookie: cookie ?? Cookie);

        public async Task RegisterDeviceAsync(Guid? deviceId = null, string? cookie = null)
        {
            var response = await Post("/devices", new { deviceId = deviceId ?? DeviceId, deviceName = "Test PC" }, cookie);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public object Op(Guid id, string type, object payload, int? userId = null, int? companyId = null,
            string? businessDate = "2026-10-01", IEnumerable<Guid>? dependencies = null, string? hashOverride = null, int revision = 1,
            DateTime? createdAtUtc = null)
        {
            var json = JsonSerializer.Serialize(payload);
            return new
            {
                clientOperationId = id,
                userId = userId ?? User.Id,
                companyId = companyId ?? CompanyId,
                operationType = type,
                payloadJson = json,
                payloadHash = hashOverride ?? OfflineSyncService.ComputeHash(json),
                createdAtUtc = createdAtUtc ?? DateTime.UtcNow,
                businessDate,
                schemaVersion = 1,
                revision,
                dependencies = dependencies?.ToArray() ?? []
            };
        }

        public async Task<OfflinePushResponse> PushAsync(params object[] operations)
        {
            var response = await Post("/operations", new { deviceId = DeviceId, operations });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<OfflinePushResponse>(JsonOptions))!;
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    internal sealed class FakePeriodGuard : IOperationalPeriodGuard
    {
        public OperationalPeriodLockStatus Status { get; set; } = OperationalPeriodLockStatus.Open;

        public Task<OperationalPeriodLockStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

        public Task EnsureOpenAsync(DateTime transactionDate, string documentKind, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureOpenAsync(DateTime transactionDate, string documentKind, ClosedPeriodOverride? approvedOverride, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApproveOverrideAsync(ClosedPeriodOverride requestedOverride, string requestPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly object SalePayload = new { customerId = 7, productId = 3, tankId = 4, quantity = 12.5, currency = "USD" };

    /// <summary>
    /// اجرای اکشن‌های واقعی MVC در میزبان SQLite ممکن نیست (سرویس‌های کامل برنامه ثبت نشده‌اند)؛ اینجا منطق صندوق
    /// (قفل، توکن، تعارض، اصلاح، تکرار) با یک dispatcher جعلی سنجیده می‌شود. اجرای واقعی اکشن‌ها:
    /// <see cref="OfflineAutoApplyRealServerTests"/>.
    /// </summary>
    internal sealed class FakeDispatcher : IMvcFormDispatcher
    {
        public List<(string Controller, string Action, IReadOnlyDictionary<string, string?> Fields, string? Token)> Calls { get; } = [];
        public List<(IReadOnlyList<KeyValuePair<string, string?>>? FieldList, IReadOnlyList<KeyValuePair<string, string?>>? Query, bool Wrapped)> Extras { get; } = [];
        public Dictionary<string, MvcFormResult> ResultByController { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ReferenceByController { get; } = new(StringComparer.Ordinal);

        public Task<MvcFormResult> PostAsync(string controller, string action, IReadOnlyDictionary<string, string?> fields,
            ClaimsPrincipal user, string? formToken, bool wrapInTransaction,
            Func<ApplicationDbContext, string?, Task>? onCommittedInTransaction, CancellationToken ct, string? referenceType = null,
            IReadOnlyList<KeyValuePair<string, string?>>? fieldList = null, IReadOnlyList<KeyValuePair<string, string?>>? query = null)
        {
            Calls.Add((controller, action, fields, formToken));
            Extras.Add((fieldList, query, wrapInTransaction));
            var result = ResultByController.TryGetValue(controller, out var configured)
                ? configured
                : new MvcFormResult(true, ReferenceByController.GetValueOrDefault(controller, "101"), [], false, false);
            return Task.FromResult(result);
        }

        public void Fail(string controller, string message, bool transient = false)
            => ResultByController[controller] = new MvcFormResult(false, null, [message], false, transient);
    }

    private static async Task<Harness> StartAsync(string role = AuthRoles.Operator, string? allowedNavigation = null)
    {
        var period = new FakePeriodGuard();
        var dispatcher = new FakeDispatcher();
        var host = await MobileApiTestHost.StartAsync(configureServices: services =>
        {
            services.AddSingleton<IOperationalPeriodGuard>(period);
            services.AddSingleton<IMvcFormDispatcher>(dispatcher);
            services.AddScoped<OfflineOperationApplier>();
            services.AddScoped<IOfflineSyncService, OfflineSyncService>();
            services.AddScoped<IOfflineSnapshotService, OfflineSnapshotService>();
        });

        var companyId = await host.WithDbAsync(async db =>
        {
            var company = new Company { Name = "Mashal Test Co", IsSystemOwner = true };
            db.Companies.Add(company);
            db.Customers.Add(new Customer { Name = "Customer A" });
            db.Products.Add(new Product { Code = "D", Name = "Diesel" });
            await db.SaveChangesAsync();
            return company.Id;
        });

        var user = await host.SeedUserAsync("offline1", role, allowedNavigation);
        var cookie = await SignInAsync(host, "offline1");
        return new Harness { Host = host, Period = period, Dispatcher = dispatcher, Cookie = cookie, User = user, CompanyId = companyId };
    }

    private static async Task<string> SignInAsync(MobileApiTestHost host, string username)
    {
        var response = await host.Client.GetAsync($"/test/web/sign-in/{username}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractAuthCookie(response);
    }

    private static async Task<OfflineOperationResult> Single(OfflinePushResponse response)
        => await Task.FromResult(Assert.Single(response.Results));

    [Fact]
    public async Task Ping_Is_Anonymous_And_Never_Cached()
    {
        await using var h = await StartAsync();

        var response = await h.Host.SendAsync(HttpMethod.Get, Base + "/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(body.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Session_Requires_Authentication_And_Returns_Profile_With_Allowed_Types()
    {
        await using var h = await StartAsync();

        var anonymous = await h.Host.SendAsync(HttpMethod.Get, Base + "/session");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);

        var response = await h.Get("/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<OfflineSessionResponse>(JsonOptions))!;
        Assert.Equal(h.User.Id, session.UserId);
        Assert.Equal(h.CompanyId, session.CompanyId);
        Assert.Contains(session.OperationTypes, t => t.Key == OfflineOperationTypes.SaleRequest);
    }

    [Fact]
    public async Task Session_Is_Also_Available_With_A_Mobile_Bearer_Token()
    {
        await using var h = await StartAsync();
        var auth = await h.Host.LoginAsync("offline1");

        var response = await h.Host.SendAsync(HttpMethod.Get, Base + "/session", accessToken: auth.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Writes_Without_The_Offline_Client_Header_Are_Rejected()
    {
        await using var h = await StartAsync();

        var response = await h.Post("/devices", new { deviceId = h.DeviceId }, header: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("offline_client_header_required", ProblemCode(await ReadProblemAsync(response)));
        Assert.Equal(0, await h.Host.WithDbAsync(db => db.OfflineSyncDevices.CountAsync()));
    }

    [Fact]
    public async Task Push_Before_Device_Registration_Asks_The_Client_To_Retry()
    {
        await using var h = await StartAsync();

        var result = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload)));

        Assert.Equal(OfflineSyncOutcomes.Retry, result.Outcome);
        Assert.Equal(OfflineSyncCodes.DeviceNotRegistered, result.Code);
        Assert.Equal(0, await h.Host.WithDbAsync(db => db.OfflineSyncOperations.CountAsync()));
    }

    // TEST 05/10 — عملیات آفلاین پس از اتصال ذخیره و بی‌درنگ با اکشن موجود (Sales/Create) ثبت قطعی می‌شود.
    [Fact]
    public async Task Push_Stores_And_Applies_The_Operation_Through_The_Existing_Create_Action()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var id = Guid.NewGuid();

        var result = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.SaleRequest, SalePayload)));

        Assert.Equal(OfflineSyncOutcomes.Stored, result.Outcome);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);
        Assert.Equal("101", result.AppliedReferenceId);
        var call = Assert.Single(h.Dispatcher.Calls);
        Assert.Equal(("Sales", "Create"), (call.Controller, call.Action));
        Assert.Equal(OfflineOperationApplier.FormTokenFor(id), call.Token);
        Assert.Equal("7", call.Fields["CustomerId"]);
        Assert.Equal("12.5", call.Fields["QuantityMt"]);
        Assert.Equal("4", call.Fields["SourceStorageTankId"]);
        Assert.Equal("true", call.Fields["IsSalePricePending"]); // قیمت خالی = گردش کار موجود «قیمت بعداً»
        Assert.Equal("2026-10-01", call.Fields["SaleDate"]);

        var row = await h.Host.WithDbAsync(db => db.OfflineSyncOperations.AsNoTracking().SingleAsync());
        Assert.Equal(OfflineSyncStatus.Applied, row.Status);
        Assert.Equal(1, row.ApplyAttempts);
        Assert.Equal(h.CompanyId, row.CompanyId);
        Assert.True(await h.Host.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.EntityName == nameof(OfflineSyncOperation))) >= 2);
    }

    // TEST 11/12 — پاسخ گم‌شده یا ارسال تکراری: همان شناسه ردیف دوم نمی‌سازد.
    [Fact]
    public async Task Resending_The_Same_Operation_Is_Idempotent()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var op = h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload);

        var first = await Single(await h.PushAsync(op));
        var second = await Single(await h.PushAsync(op));
        var third = await h.PushAsync(op, op);

        Assert.Equal(OfflineSyncOutcomes.Stored, first.Outcome);
        Assert.Equal(OfflineSyncOutcomes.Duplicate, second.Outcome);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), second.Status);
        Assert.Single(h.Dispatcher.Calls);
        Assert.All(third.Results, r => Assert.Equal(OfflineSyncOutcomes.Duplicate, r.Outcome));
        Assert.Equal(1, await h.Host.WithDbAsync(db => db.OfflineSyncOperations.CountAsync()));
    }

    [Fact]
    public async Task Same_Id_With_Different_Payload_Is_Rejected_And_Original_Is_Kept()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var id = Guid.NewGuid();
        await h.PushAsync(h.Op(id, OfflineOperationTypes.SaleRequest, SalePayload));

        var result = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.SaleRequest,
            new { customerId = 7, productId = 3, tankId = 4, quantity = 99, currency = "USD" })));

        Assert.Equal(OfflineSyncOutcomes.Rejected, result.Outcome);
        Assert.Equal(OfflineSyncCodes.PayloadMismatch, result.Code);
        var row = await h.Host.WithDbAsync(db => db.OfflineSyncOperations.AsNoTracking().SingleAsync());
        Assert.Contains("12.5", row.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Integrity_User_Company_And_Validation_Failures_Are_Rejected_Without_Storing()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();

        var response = await h.PushAsync(
            h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload, hashOverride: new string('0', 64)),
            h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload, userId: h.User.Id + 100),
            h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload, companyId: h.CompanyId + 100),
            h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, new { customerId = 7, tankId = 4, quantity = 1, currency = "USD" }),
            h.Op(Guid.NewGuid(), OfflineOperationTypes.ExpenseRequest, new { expenseTypeId = 1, amount = -5, currency = "USD" }),
            h.Op(Guid.NewGuid(), "Ledger.Post", new { amount = 1 }),
            h.Op(Guid.NewGuid(), OfflineOperationTypes.GeneralNote, new { text = "x" }, businessDate: "13/10/2026"));

        Assert.Collection(response.Results,
            r => Assert.Equal(OfflineSyncCodes.HashMismatch, r.Code),
            r => Assert.Equal(OfflineSyncCodes.UserMismatch, r.Code),
            r => Assert.Equal(OfflineSyncCodes.CompanyMismatch, r.Code),
            r => Assert.Equal(OfflineSyncCodes.Validation, r.Code),
            r => Assert.Equal(OfflineSyncCodes.Validation, r.Code),
            r => Assert.Equal(OfflineSyncCodes.UnknownType, r.Code),
            r => Assert.Equal(OfflineSyncCodes.Validation, r.Code));
        Assert.All(response.Results, r => Assert.Equal(OfflineSyncOutcomes.Rejected, r.Outcome));
        Assert.Equal(0, await h.Host.WithDbAsync(db => db.OfflineSyncOperations.CountAsync()));
    }

    [Fact]
    public async Task Operation_Outside_The_Users_Navigation_Is_Forbidden()
    {
        await using var h = await StartAsync("OfflineLimited", allowedNavigation: RoleNavigationKeys.Dashboard);
        await h.RegisterDeviceAsync();

        var response = await h.PushAsync(
            h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload),
            h.Op(Guid.NewGuid(), OfflineOperationTypes.GeneralNote, new { text = "پمپ شماره ۲ خراب است" }));

        Assert.Equal(OfflineSyncCodes.Forbidden, response.Results[0].Code);
        Assert.Equal(OfflineSyncOutcomes.Stored, response.Results[1].Outcome);
    }

    // TEST 15 + بند ۱۱ — کاربر غیرفعال‌شده پس از اتصال نمی‌تواند چیزی بفرستد، حتی با کوکی قدیمی.
    [Fact]
    public async Task Deactivated_User_With_An_Old_Cookie_Cannot_Push()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        await h.Host.SetUserActiveAsync(h.User.Id, false);

        var response = await h.Post("/operations", new { deviceId = h.DeviceId, operations = new[] { h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload) } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await h.Host.WithDbAsync(db => db.OfflineSyncOperations.CountAsync()));
    }

    [Fact]
    public async Task Revoked_Device_Cannot_Push()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        await h.Host.WithDbAsync(async db =>
        {
            var device = await db.OfflineSyncDevices.SingleAsync();
            device.RevokedAtUtc = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });

        var result = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload)));

        Assert.Equal(OfflineSyncOutcomes.Rejected, result.Outcome);
        Assert.Equal(OfflineSyncCodes.DeviceRevoked, result.Code);
    }

    // بند ۷ — دورهٔ مالی بسته: تعارض، نه ثبت.
    [Fact]
    public async Task Business_Date_In_A_Closed_Period_Becomes_A_Conflict()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        h.Period.Status = new OperationalPeriodLockStatus(true, new DateTime(2026, 10, 5), "Closed");

        var result = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload, businessDate: "2026-10-01")));

        Assert.Equal(OfflineSyncOutcomes.Stored, result.Outcome);
        Assert.Equal(nameof(OfflineSyncStatus.Conflict), result.Status);
        Assert.Contains("دورهٔ بسته", result.Message, StringComparison.Ordinal);
        Assert.Empty(h.Dispatcher.Calls);
    }

    // TEST 13 — دو دستگاه همان معامله را جداگانه ثبت کرده‌اند: دومی تعارض است (نه LWW، نه بازنویسی).
    [Fact]
    public async Task Same_Content_From_A_Second_Device_Is_Flagged_As_Possible_Duplicate()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var secondDevice = Guid.NewGuid();
        await h.RegisterDeviceAsync(secondDevice);

        await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload));
        var response = await h.Post("/operations", new { deviceId = secondDevice, operations = new[] { h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload) } });
        var result = Assert.Single((await response.Content.ReadFromJsonAsync<OfflinePushResponse>(JsonOptions))!.Results);

        Assert.Equal(nameof(OfflineSyncStatus.Conflict), result.Status);
        Assert.Equal(2, await h.Host.WithDbAsync(db => db.OfflineSyncOperations.CountAsync()));
        Assert.Single(h.Dispatcher.Calls);
    }

    [Fact]
    public async Task Dependencies_Are_Applied_Parent_First_And_The_New_Record_Id_Flows_Into_The_Child()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        h.Dispatcher.ReferenceByController["Customers"] = "55";
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var orphan = Guid.NewGuid();

        // فرزند پیش از والد در همان دسته؛ مشتریِ تازه اول ساخته می‌شود و شناسه‌اش در فروش می‌نشیند.
        var response = await h.PushAsync(
            h.Op(child, OfflineOperationTypes.SaleRequest,
                new { customerId = $"op:{parent}", productId = 3, tankId = 4, quantity = 2, currency = "USD" }, dependencies: [parent]),
            h.Op(parent, OfflineOperationTypes.CustomerDraft, new { name = "مشتری تازه" }),
            h.Op(orphan, OfflineOperationTypes.GeneralNote, new { text = "x" }, dependencies: [Guid.NewGuid()]));

        Assert.Equal(nameof(OfflineSyncStatus.Applied), response.Results[0].Status);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), response.Results[1].Status);
        Assert.Equal("55", response.Results[1].AppliedReferenceId);
        Assert.Equal(OfflineSyncOutcomes.Retry, response.Results[2].Outcome);
        Assert.Equal(OfflineSyncCodes.DependencyMissing, response.Results[2].Code);
        Assert.Equal(["Customers", "Sales"], h.Dispatcher.Calls.Select(c => c.Controller));
        Assert.Equal("55", h.Dispatcher.Calls[1].Fields["CustomerId"]);
    }

    // فورم اصلی ثبت مشتری (Customers/Create) آفلاین: همان فیلدهای فورم به همان اکشن می‌رسند.
    [Fact]
    public async Task Customer_From_Main_Form_Maps_All_Form_Fields_To_Customers_Create()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();

        await h.PushAsync(
            h.Op(Guid.NewGuid(), OfflineOperationTypes.CustomerDraft,
                new { code = "c-01", name = "مشتری فورم اصلی", country = "AF", phone = "0700111222", isActive = false }),
            h.Op(Guid.NewGuid(), OfflineOperationTypes.CustomerDraft, new { name = "مشتری قدیمی دستگاه" }));

        Assert.Equal(2, h.Dispatcher.Calls.Count);
        var form = h.Dispatcher.Calls[0];
        Assert.Equal(("Customers", "Create"), (form.Controller, form.Action));
        Assert.Equal("c-01", form.Fields["Code"]);
        Assert.Equal("مشتری فورم اصلی", form.Fields["Name"]);
        Assert.Equal("AF", form.Fields["Country"]);
        Assert.Equal("0700111222", form.Fields["Phone"]);
        Assert.Equal("false", form.Fields["IsActive"]);
        // عملیات قدیمی بدون isActive/کد/کشور همچنان مشتری فعال می‌سازد.
        Assert.Equal("true", h.Dispatcher.Calls[1].Fields["IsActive"]);
        Assert.Null(h.Dispatcher.Calls[1].Fields["Code"]);
    }

    // فورم اصلی MVC (Form.Submit): همان جفت‌های نام/مقدار فورم، بدون نگاشت، به همان اکشن می‌رسند.
    [Fact]
    public async Task Main_Form_Submit_Replays_The_Exact_Form_Fields_On_The_Registered_Action()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();

        var result = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit, new
        {
            path = "/customers/create",
            label = "مشتری جدید",
            fields = new[]
            {
                new[] { "__RequestVerificationToken", "stale" }, new[] { "__FormToken", "page-token" },
                new[] { "Name", "مشتری فورم" }, new[] { "IsActive", "true" }, new[] { "IsActive", "false" }
            },
            query = new[] { new[] { "returnUrl", "/Customers" } }
        })));

        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);
        var call = Assert.Single(h.Dispatcher.Calls);
        Assert.Equal(("Customers", "Create"), (call.Controller, call.Action));
        Assert.Null(call.Token); // بدون توکن ضد تکرار: در تراکنش بیرونی همراه با وضعیت عملیات
        var extra = Assert.Single(h.Dispatcher.Extras);
        Assert.True(extra.Wrapped);
        Assert.Equal(["Name=مشتری فورم", "IsActive=true", "IsActive=false"], extra.FieldList!.Select(f => $"{f.Key}={f.Value}"));
        Assert.Equal("/Customers", Assert.Single(extra.Query!).Value);
    }

    [Fact]
    public async Task Main_Form_Submit_Of_A_Token_Form_Uses_The_Offline_Token_Or_The_Original_Page_Token()
    {
        await using var h = await StartAsync(AuthRoles.Admin);
        await h.RegisterDeviceAsync();
        object Payment(string? token) => new
        {
            path = "/Payments/Create",
            fields = new[] { new[] { "Amount", "10" } },
            formToken = token
        };

        var fresh = Guid.NewGuid();
        await h.PushAsync(h.Op(fresh, OfflineOperationTypes.FormSubmit, Payment(null)));
        Assert.Equal(OfflineOperationApplier.FormTokenFor(fresh), h.Dispatcher.Calls[0].Token);
        Assert.False(h.Dispatcher.Extras[0].Wrapped);

        // همان فورم پیش‌تر آنلاین با توکن صفحه فرستاده شد و پاسخ نیامد: همان توکن؛ اگر ثبت شده بود، دوباره اجرا نمی‌شود.
        await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit, Payment("pagetoken1")));
        Assert.Equal("pagetoken1", h.Dispatcher.Calls[1].Token);

        await h.Host.WithDbAsync(async db =>
        {
            db.ProcessedFormTokens.Add(new ProcessedFormToken
            {
                Token = "pagetoken2", Purpose = "Payments.Create", ConsumedAtUtc = DateTime.UtcNow, ReferenceType = nameof(PaymentTransaction)
            });
            return await db.SaveChangesAsync();
        });
        var committed = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit, Payment("pagetoken2"))));
        Assert.Equal(nameof(OfflineSyncStatus.Applied), committed.Status);
        Assert.Equal(2, h.Dispatcher.Calls.Count);
    }

    // پاسخ نامعلومِ بسیار قدیمی: نبودِ توکن صفحه دیگر ثابت نمی‌کند سند ساخته نشده؛ خودکار ثبت نمی‌شود تا کاربر بررسی کند.
    [Fact]
    public async Task Very_Old_Unknown_Outcome_Is_Held_For_Review_And_Sent_Only_After_An_Explicit_Resend()
    {
        await using var h = await StartAsync(AuthRoles.Admin);
        await h.RegisterDeviceAsync();
        var id = Guid.NewGuid();
        var payload = new { path = "/payments/create", fields = new[] { new[] { "Amount", "10" } }, formToken = "pagetokenold" };
        var created = DateTime.UtcNow.AddDays(-(ProcessedFormTokenRetentionService.OfflineReplayRetentionDays - 1));

        var held = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.FormSubmit, payload, createdAtUtc: created)));
        Assert.Equal(nameof(OfflineSyncStatus.Conflict), held.Status);
        Assert.Empty(h.Dispatcher.Calls);

        // کاربر پس از بررسی فهرست، همان عملیات را دوباره می‌فرستد (Revision ۲): همان توکن، یک‌بار.
        var resent = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.FormSubmit, payload, createdAtUtc: created, revision: 2)));
        Assert.Equal(nameof(OfflineSyncStatus.Applied), resent.Status);
        Assert.Equal("pagetokenold", Assert.Single(h.Dispatcher.Calls).Token);

        // همان ارسال نامعلوم اما تازه (کمتر از یک سال): خودکار و بدون بررسی.
        await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit,
            payload with { formToken = "pagetokenrecent" }, createdAtUtc: DateTime.UtcNow.AddDays(-300)));
        Assert.Equal(2, h.Dispatcher.Calls.Count);
    }

    [Fact]
    public async Task Main_Form_Submit_Rejects_Unregistered_Forms_And_Forms_Outside_The_Users_Navigation()
    {
        await using var h = await StartAsync("OfflineFormsLimited", allowedNavigation: RoleNavigationKeys.Partners);
        await h.RegisterDeviceAsync();

        var unknown = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit,
            new { path = "/users/create", fields = new[] { new[] { "Username", "x" } } })));
        var forbidden = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit,
            new { path = "/payments/create", fields = new[] { new[] { "Amount", "1" } } })));
        var malformed = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit,
            new { path = "/customers/create", fields = new object[] { new object[] { "Name", 5 } } })));

        Assert.Equal(OfflineSyncOutcomes.Rejected, unknown.Outcome);
        Assert.Equal(OfflineSyncOutcomes.Rejected, forbidden.Outcome);
        Assert.Equal(OfflineSyncCodes.Forbidden, forbidden.Code);
        Assert.Equal(OfflineSyncOutcomes.Rejected, malformed.Outcome);
        Assert.Empty(h.Dispatcher.Calls);
    }

    [Fact]
    public async Task Main_Form_Submit_Resolves_A_Record_Created_By_An_Earlier_Offline_Form()
    {
        await using var h = await StartAsync(AuthRoles.Admin);
        await h.RegisterDeviceAsync();
        h.Dispatcher.ReferenceByController["Customers"] = "77";
        var customer = Guid.NewGuid();

        var response = await h.PushAsync(
            h.Op(Guid.NewGuid(), OfflineOperationTypes.FormSubmit,
                new { path = "/payments/create", fields = new[] { new[] { "CustomerId", $"op:{customer}" }, new[] { "Amount", "5" } } },
                dependencies: [customer]),
            h.Op(customer, OfflineOperationTypes.FormSubmit,
                new { path = "/customers/create", fields = new[] { new[] { "Name", "مشتری تازه" } } }));

        Assert.All(response.Results, r => Assert.Equal(nameof(OfflineSyncStatus.Applied), r.Status));
        Assert.Equal(["Customers", "Payments"], h.Dispatcher.Calls.Select(c => c.Controller));
        Assert.Equal("CustomerId=77", h.Dispatcher.Extras[1].FieldList![0].Key + "=" + h.Dispatcher.Extras[1].FieldList![0].Value);
    }

    [Fact]
    public void Every_Registered_Offline_Form_Is_A_Real_Post_Action_With_A_Navigation_Key()
    {
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.Controller).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods().Select(m => (Controller: t.Name[..^"Controller".Length], Method: m)))
            .Where(x => x.Method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), true).Length > 0)
            .Select(x => $"{x.Controller}/{x.Method.Name}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var target in OfflineFormRegistry.All)
        {
            Assert.Contains($"{target.Controller}/{target.Action}", actions);
            Assert.False(string.IsNullOrEmpty(target.NavigationKey), $"{target.Path} has no navigation key");
        }
    }

    [Fact]
    public async Task Dependent_Of_A_Rejected_Operation_Becomes_A_Conflict()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var parent = Guid.NewGuid();
        h.Dispatcher.Fail("Customers", "نام مشتری تکراری است.");
        await h.PushAsync(h.Op(parent, OfflineOperationTypes.CustomerDraft, new { name = "مشتری تکراری" }));
        var parentId = await h.Host.WithDbAsync(db => db.OfflineSyncOperations.Select(o => o.Id).SingleAsync());
        await h.Host.WithScopeAsync(sp => sp.GetRequiredService<IOfflineSyncService>()
            .ReviewAsync(parentId, OfflineReviewDecision.Rejected, h.User.Id, "تکراری است", null, null));

        var result = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload, dependencies: [parent])));

        Assert.Equal(nameof(OfflineSyncStatus.Conflict), result.Status);
    }

    // TEST 22 + بند ۶ — وضعیت‌ها فقط برای عملیات خود کاربر؛ ناشناخته = Unknown.
    [Fact]
    public async Task Status_Query_Returns_Review_Outcome_Only_For_Own_Operations()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var mine = Guid.NewGuid();
        await h.PushAsync(h.Op(mine, OfflineOperationTypes.SaleRequest, SalePayload));
        var rowId = await h.Host.WithDbAsync(db => db.OfflineSyncOperations.Select(o => o.Id).SingleAsync());

        await h.Host.SeedUserAsync("offline2", AuthRoles.Operator);
        var otherCookie = await SignInAsync(h.Host, "offline2");

        Assert.True(rowId > 0);

        var own = await h.Post("/operations/status", new { ids = new[] { mine } });
        var ownBody = (await own.Content.ReadFromJsonAsync<OfflineStatusResponse>(JsonOptions))!;
        var status = Assert.Single(ownBody.Operations);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), status.Status);
        Assert.Equal("101", status.AppliedReferenceId);

        var other = await h.Post("/operations/status", new { ids = new[] { mine } }, cookie: otherCookie);
        var otherBody = (await other.Content.ReadFromJsonAsync<OfflineStatusResponse>(JsonOptions))!;
        Assert.Equal("Unknown", Assert.Single(otherBody.Operations).Status);
    }

    [Fact]
    public async Task Review_Is_Final_Once_And_Rejection_Requires_A_Reason()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        h.Dispatcher.Fail("Sales", "موجودی مخزن کافی نیست.");
        var pushed = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.SaleRequest, SalePayload)));
        Assert.Equal(nameof(OfflineSyncStatus.Conflict), pushed.Status);
        Assert.Equal("موجودی مخزن کافی نیست.", pushed.Message);
        var rowId = await h.Host.WithDbAsync(db => db.OfflineSyncOperations.Select(o => o.Id).SingleAsync());

        Task<OfflineReviewResult> Review(OfflineReviewDecision decision, string? note)
            => h.Host.WithScopeAsync(sp => sp.GetRequiredService<IOfflineSyncService>()
                .ReviewAsync(rowId, decision, h.User.Id, note, null, null));

        Assert.Equal(OfflineReviewResult.NoteRequired, await Review(OfflineReviewDecision.Rejected, " "));
        Assert.Equal(OfflineReviewResult.Done, await Review(OfflineReviewDecision.Conflict, "موجودی کافی نیست"));
        Assert.Equal(OfflineReviewResult.Done, await Review(OfflineReviewDecision.Rejected, "تکراری"));
        Assert.Equal(OfflineReviewResult.AlreadyFinal, await Review(OfflineReviewDecision.Applied, null));

        var row = await h.Host.WithDbAsync(db => db.OfflineSyncOperations.AsNoTracking().SingleAsync());
        Assert.Equal(OfflineSyncStatus.Rejected, row.Status);
        Assert.Equal(h.User.Id, row.ReviewedByUserId);
    }

    // بند ۶ — تعارض محفوظ می‌ماند و همان عملیات (همان شناسه) با Revision تازه اصلاح و دوباره ثبت می‌شود.
    [Fact]
    public async Task Conflict_Can_Be_Corrected_And_Resent_With_The_Same_Id()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var id = Guid.NewGuid();
        h.Dispatcher.Fail("Sales", "موجودی مخزن کافی نیست.");
        var first = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.SaleRequest, SalePayload)));
        Assert.Equal(nameof(OfflineSyncStatus.Conflict), first.Status);

        // همان نسخه با محتوای دیگر پذیرفته نمی‌شود (تغییر بی‌صدا نه).
        var corrected = new { customerId = 7, productId = 3, tankId = 4, quantity = 5, currency = "USD" };
        var silent = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.SaleRequest, corrected)));
        Assert.Equal(OfflineSyncCodes.PayloadMismatch, silent.Code);

        h.Dispatcher.ResultByController.Clear();
        var fixedResult = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.SaleRequest, corrected, revision: 2)));

        Assert.Equal(nameof(OfflineSyncStatus.Applied), fixedResult.Status);
        Assert.Equal(2, fixedResult.Revision);
        Assert.Equal("5", h.Dispatcher.Calls.Last().Fields["QuantityMt"]);
        var row = await h.Host.WithDbAsync(db => db.OfflineSyncOperations.AsNoTracking().SingleAsync());
        Assert.Equal(2, row.Revision);
        Assert.Contains("\"quantity\":5", row.PayloadJson, StringComparison.Ordinal);
    }

    // خطای موقت سرور: عملیات «دریافت‌شده» می‌ماند و با پرسش وضعیت دوباره خودکار ثبت می‌شود.
    [Fact]
    public async Task Transient_Failure_Is_Retried_Automatically_On_Status_Poll()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var id = Guid.NewGuid();
        h.Dispatcher.Fail("Payments", "timeout", transient: true);
        var first = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.CustomerReceiptRequest,
            new { customerId = 7, cashAccountId = 2, amount = 10, currency = "USD" })));
        Assert.Equal(nameof(OfflineSyncStatus.Received), first.Status);

        h.Dispatcher.ResultByController.Clear();
        await h.Host.WithDbAsync(db => db.OfflineSyncOperations
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LastApplyAttemptAtUtc, DateTime.UtcNow.AddMinutes(-2))));
        var status = await h.Post("/operations/status", new { ids = new[] { id } });
        var body = (await status.Content.ReadFromJsonAsync<OfflineStatusResponse>(JsonOptions))!;

        Assert.Equal(nameof(OfflineSyncStatus.Applied), Assert.Single(body.Operations).Status);
        Assert.Equal(2, h.Dispatcher.Calls.Count);
    }

    // سند قبلاً با همین توکن ثبت شده (پاسخ گم شد / سرور وسط کار متوقف شد) → دوباره اجرا نمی‌شود.
    [Fact]
    public async Task Existing_Form_Token_Means_Applied_Without_Running_The_Action_Again()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var id = Guid.NewGuid();
        await h.Host.WithDbAsync(async db =>
        {
            db.ProcessedFormTokens.Add(new ProcessedFormToken
            {
                Token = OfflineOperationApplier.FormTokenFor(id),
                Purpose = "Sale.Create",
                ConsumedAtUtc = DateTime.UtcNow,
                ReferenceType = nameof(SalesTransaction)
            });
            return await db.SaveChangesAsync();
        });

        var result = await Single(await h.PushAsync(h.Op(id, OfflineOperationTypes.SaleRequest, SalePayload)));

        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);
        Assert.Empty(h.Dispatcher.Calls);
    }

    [Fact]
    public async Task Money_Transfer_Is_Not_Offered_And_Old_Ones_Are_Rejected_With_A_Reason()
    {
        await using var h = await StartAsync();
        await h.RegisterDeviceAsync();
        var session = (await (await h.Get("/session")).Content.ReadFromJsonAsync<OfflineSessionResponse>(JsonOptions))!;
        Assert.DoesNotContain(session.OperationTypes, t => t.Key == OfflineOperationTypes.MoneyTransferRequest);

        var result = await Single(await h.PushAsync(h.Op(Guid.NewGuid(), OfflineOperationTypes.MoneyTransferRequest,
            new { fromCashAccountId = 1, toCashAccountId = 2, amount = 5, currency = "USD" })));

        Assert.Equal(nameof(OfflineSyncStatus.Rejected), result.Status);
        Assert.Contains("انتقال پول", result.Message, StringComparison.Ordinal);
        Assert.Empty(h.Dispatcher.Calls);
    }

    // دستگاه مورداعتماد: کوکی (پس از ورود عادی) → نشست دستگاه؛ همگام‌سازی پس از بسته‌شدن برنامه با Bearer همان دستگاه.
    [Fact]
    public async Task Device_Session_Is_Issued_From_A_Cookie_Login_And_Then_Works_Without_The_Cookie()
    {
        await using var h = await StartAsync();

        var response = await h.Post("/device-session", new { deviceId = h.DeviceId, deviceName = "PWA" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var access = body.RootElement.GetProperty("accessToken").GetString()!;
        var refresh = body.RootElement.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(refresh));
        Assert.Equal(h.DeviceId.ToString("D"), await h.Host.WithDbAsync(db => db.MobileRefreshTokens.Select(t => t.DeviceId).SingleAsync()));

        // بدون کوکی، فقط Bearer: ارسال عملیات کار می‌کند.
        var push = await h.Host.SendAsync(HttpMethod.Post, Base + "/operations", accessToken: access, headers: OfflineHeader,
            body: new { deviceId = h.DeviceId, operations = new[] { h.Op(Guid.NewGuid(), OfflineOperationTypes.GeneralNote, new { text = "پس از Restart" }) } });
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        var result = Assert.Single((await push.Content.ReadFromJsonAsync<OfflinePushResponse>(JsonOptions))!.Results);
        Assert.Equal(nameof(OfflineSyncStatus.Applied), result.Status);

        // با Bearer نمی‌توان نشست دستگاه تازه ساخت.
        var mint = await h.Host.SendAsync(HttpMethod.Post, Base + "/device-session", accessToken: access, headers: OfflineHeader,
            body: new { deviceId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, mint.StatusCode);

        // کاربر غیرفعال: تازه‌سازی رد می‌شود.
        await h.Host.SetUserActiveAsync(h.User.Id, false);
        var refreshed = await h.Host.RefreshAsync(refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task Snapshot_Returns_Reference_Data_Filtered_By_Navigation_And_Not_Cached()
    {
        await using var limited = await StartAsync("OfflineLimited2", allowedNavigation: RoleNavigationKeys.Dashboard);

        var response = await limited.Get("/snapshot");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = (await response.Content.ReadFromJsonAsync<OfflineSnapshotResponse>(JsonOptions))!;
        Assert.True(body.Reference.ContainsKey("products"));
        Assert.False(body.Reference.ContainsKey("customers"));
        Assert.NotNull(body.Dashboard);

        await using var full = await StartAsync();
        var fullBody = (await (await full.Get("/snapshot")).Content.ReadFromJsonAsync<OfflineSnapshotResponse>(JsonOptions))!;
        Assert.Equal("Customer A", Assert.Single(fullBody.Reference["customers"]).Name);
    }

    [Fact]
    public void Dependency_Ordering_Puts_Parents_First_And_Survives_Cycles()
    {
        var a = new OfflineOperationDto { ClientOperationId = Guid.NewGuid() };
        var b = new OfflineOperationDto { ClientOperationId = Guid.NewGuid(), Dependencies = [a.ClientOperationId] };
        var c = new OfflineOperationDto { ClientOperationId = Guid.NewGuid(), Dependencies = [b.ClientOperationId] };
        var ordered = OfflineSyncService.OrderByDependencies([c, b, a]).ToList();
        Assert.Equal([a, b, c], ordered);

        var x = new OfflineOperationDto { ClientOperationId = Guid.NewGuid() };
        var y = new OfflineOperationDto { ClientOperationId = Guid.NewGuid(), Dependencies = [x.ClientOperationId] };
        x.Dependencies = [y.ClientOperationId];
        Assert.Equal(2, OfflineSyncService.OrderByDependencies([x, y]).Count());
    }
}
