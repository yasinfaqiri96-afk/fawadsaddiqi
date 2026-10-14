using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Middleware;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ثبت Audit روی هر GET موفق یک write به جدول می‌ساخت، پس هر رفرش و هر فراخوانی AJAX جدول را
/// بزرگ می‌کرد. این تست قفل می‌کند که بازدیدِ تکراری در بازهٔ کوتاه دوباره نوشته نشود، ولی
/// هر تغییر داده (POST/PUT/PATCH/DELETE) و هر درخواست ناموفق همچنان کامل ثبت شود.
/// </summary>
public sealed class ActivityLogMiddlewareTests
{
    private sealed class RecordingAuditService : IAuditService
    {
        public readonly List<AuditLogEntryInput> Entries = [];

        public Task LogAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogAndSaveAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogActivityAsync(AuditLogEntryInput entry, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogActivityAndSaveAsync(AuditLogEntryInput entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public bool IsAuthenticated => true;
        public int? UserId => 7;
        public string? Username => "tester";
        public string? FullName => "Tester";
        public string? RoleName => "Admin";
        public ClaimsPrincipal Principal { get; } = new();
    }

    private static (ActivityLogMiddleware Middleware, RecordingAuditService Audit) NewMiddleware(
        int statusCode = 200,
        Exception? failure = null)
    {
        var audit = new RecordingAuditService();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditService>(audit);
        services.AddSingleton<ICurrentUserContext>(new FakeUserContext());
        var provider = services.BuildServiceProvider();

        var middleware = new ActivityLogMiddleware(
            context =>
            {
                context.Response.StatusCode = statusCode;
                return failure is null ? Task.CompletedTask : Task.FromException(failure);
            },
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ActivityLogMiddleware>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        return (middleware, audit);
    }

    private static DefaultHttpContext NewRequest(string method, string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Name, "tester")],
            authenticationType: "Test"));
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor
            {
                ControllerName = "Loading",
                ActionName = "Index"
            }),
            "Loading/Index"));
        return context;
    }

    [Fact]
    public async Task A_Repeated_Successful_Get_Is_Only_Audited_Once()
    {
        var (middleware, audit) = NewMiddleware();

        for (var i = 0; i < 5; i++)
        {
            await middleware.InvokeAsync(NewRequest("GET", "/Loading"));
        }

        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task Background_Offline_Prepare_Reads_Are_Not_Audited_But_Their_Failures_Are()
    {
        var (middleware, audit) = NewMiddleware();
        var prepare = NewRequest("GET", "/Customers");
        prepare.Request.Headers[OfflinePrepareMiddleware.HeaderName] = "1";
        await middleware.InvokeAsync(prepare);
        Assert.Empty(audit.Entries);

        var (failing, failingAudit) = NewMiddleware(statusCode: 500);
        var failed = NewRequest("GET", "/Customers");
        failed.Request.Headers[OfflinePrepareMiddleware.HeaderName] = "1";
        await failing.InvokeAsync(failed);
        Assert.Single(failingAudit.Entries);
    }

    [Fact]
    public async Task A_Different_Path_Or_Query_Is_Audited_Separately()
    {
        var (middleware, audit) = NewMiddleware();

        await middleware.InvokeAsync(NewRequest("GET", "/Loading"));
        await middleware.InvokeAsync(NewRequest("GET", "/Loading", "?page=2"));
        await middleware.InvokeAsync(NewRequest("GET", "/LoadingReceipts"));

        Assert.Equal(3, audit.Entries.Count);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Every_Data_Changing_Request_Is_Always_Audited(string method)
    {
        var (middleware, audit) = NewMiddleware();

        for (var i = 0; i < 3; i++)
        {
            await middleware.InvokeAsync(NewRequest(method, "/Loading/Create"));
        }

        Assert.Equal(3, audit.Entries.Count);
        Assert.All(audit.Entries, entry => Assert.Equal(method, entry.HttpMethod));
    }

    [Fact]
    public async Task A_Failing_Get_Is_Always_Audited()
    {
        var (middleware, audit) = NewMiddleware(statusCode: 403);

        for (var i = 0; i < 3; i++)
        {
            await middleware.InvokeAsync(NewRequest("GET", "/Loading"));
        }

        Assert.Equal(3, audit.Entries.Count);
        Assert.All(audit.Entries, entry => Assert.False(entry.IsSuccess));
    }

    [Fact]
    public async Task A_Get_That_Throws_Is_Audited_And_The_Exception_Still_Propagates()
    {
        var (middleware, audit) = NewMiddleware(failure: new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(NewRequest("GET", "/Loading")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(NewRequest("GET", "/Loading")));

        Assert.Equal(2, audit.Entries.Count);
        Assert.All(audit.Entries, entry => Assert.False(entry.IsSuccess));
    }
}
