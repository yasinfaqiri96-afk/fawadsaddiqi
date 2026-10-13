using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PTGOilSystem.Web.Controllers.Api.Mobile;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Middleware;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Mobile;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Mobile;
using PTGOilSystem.Web.Services.OperationalPeriod;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests.MobileApi;

/// <summary>
/// میزبان TestServer با همان تنظیمات احراز هویت، سیاست‌ها، فیلترها و میان‌افزارهای API برنامهٔ اصلی
/// (AddPtgAuthentication / AddPtgMobileApi / ApiErrorMiddleware / DevAutoSignInMiddleware)، روی SQLite
/// تا unique index و ExecuteUpdate واقعاً اجرا شوند. داشبورد با نسخهٔ جعلی جایگزین می‌شود.
/// </summary>
internal sealed class MobileApiTestHost : IAsyncDisposable
{
    public const string SigningKey = "test-signing-key-for-mashal-mobile-0123456789abcdef";
    public const string Password = "StrongPass123!";
    public const string LoginPath = "/api/mobile/v1/auth/login";
    public const string RefreshPath = "/api/mobile/v1/auth/refresh";
    public const string LogoutPath = "/api/mobile/v1/auth/logout";
    public const string MePath = "/api/mobile/v1/me";
    public const string DashboardPath = "/api/mobile/v1/dashboard";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;

    private MobileApiTestHost(WebApplication app, SqliteConnection connection)
    {
        App = app;
        _connection = connection;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public static async Task<MobileApiTestHost> StartAsync(
        string environment = "Production",
        string? signingKey = SigningKey,
        IMobileDashboardService? dashboard = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(MobileApiTestHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MobileAuth:SigningKey"] = signingKey,
            [MobileAuthOptionsKey] = signingKey,
            ["PTG_BOOTSTRAP_ADMIN_USERNAME"] = "admin",
            ["PTG_DEV_AUTO_SIGNIN"] = "true"
        });

        var services = builder.Services;
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ILoginAttemptGuard, AuditLoginAttemptGuard>();
        services.AddScoped<ISystemCompanyProvider, SystemCompanyProvider>();
        services.AddScoped<IFormTokenGuard, FormTokenGuard>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAfghanistanBusinessClock, AfghanistanBusinessClock>();
        services.AddScoped<RoleNavigationAuthorizationFilter>();
        services.AddScoped<BusinessRuleExceptionFilter>();

        services.AddPtgAuthentication(builder.Configuration, builder.Environment);
        services.AddPtgMobileApi();
        services.RemoveAll<IMobileDashboardService>();
        services.AddScoped(_ => dashboard ?? new FakeMobileDashboardService());
        configureServices?.Invoke(services);

        services.AddControllers(options =>
            {
                options.Filters.AddService<RoleNavigationAuthorizationFilter>();
                options.Filters.AddService<BusinessRuleExceptionFilter>();
            })
            .AddApplicationPart(typeof(MobileAuthController).Assembly)
            .AddApplicationPart(typeof(MobileApiTestHost).Assembly);

        var app = builder.Build();
        app.UseMiddleware<ApiErrorMiddleware>();
        app.UseRouting();
        app.UseAuthentication();
        if (app.Environment.IsDevelopment())
        {
            app.UseMiddleware<DevAutoSignInMiddleware>();
        }

        app.UseAuthorization();

        // Stand-ins for cookie-protected web pages (default authorization policy = cookie scheme).
        app.MapGet("/test/web/sign-in/{username}", async (HttpContext http, string username, ApplicationDbContext db) =>
        {
            var user = await db.Users.Include(u => u.Role).SingleAsync(u => u.Username == username);
            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(
                    UserClaimsFactory.Build(user),
                    CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok();
        });
        app.MapGet("/test/web/whoami", (HttpContext http) => Results.Text(http.User.Identity?.Name ?? ""))
            .RequireAuthorization();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        }

        await app.StartAsync();
        return new MobileApiTestHost(app, connection);
    }

    private const string MobileAuthOptionsKey = "PTG_MOBILE_JWT_SIGNING_KEY";

    public async Task<User> SeedUserAsync(
        string username,
        string roleName = AuthRoles.Admin,
        string? allowedNavigation = null,
        bool isActive = true)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role is null)
        {
            role = new Role { Name = roleName, AllowedNavigationItems = allowedNavigation };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = await scope.ServiceProvider.GetRequiredService<IUserService>()
            .CreateUserAsync(username, $"{username} Full", Password, role.Id);

        if (!isActive)
        {
            await SetUserActiveAsync(user.Id, false);
        }

        return user;
    }

    public async Task SetUserActiveAsync(int userId, bool isActive)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    public async Task<MobileAuthResponse> LoginAsync(string username, string password = Password, string deviceName = "Test phone")
    {
        var response = await Client.PostAsJsonAsync(LoginPath, new { username, password, deviceName });
        Assert.Equal(StatusCodes.Status200OK, (int)response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MobileAuthResponse>(JsonOptions))!;
    }

    public Task<HttpResponseMessage> RefreshAsync(string refreshToken)
        => Client.PostAsJsonAsync(RefreshPath, new { refreshToken });

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? accessToken = null,
        string? cookie = null,
        string? accept = null,
        object? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return Client.SendAsync(request);
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        => WithScopeAsync(services => action(services.GetRequiredService<ApplicationDbContext>()));

    public static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(ApiProblem.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static string ProblemCode(JsonElement problem) => problem.GetProperty("code").GetString()!;

    public static string ExtractAuthCookie(HttpResponseMessage response)
        => response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("PTGOilSystem.Auth=", StringComparison.Ordinal))
            .Split(';')[0];

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class FakeMobileDashboardService : IMobileDashboardService
{
    public ClaimsPrincipal? LastUser { get; private set; }

    public Task<MobileDashboardResponse> BuildAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        LastUser = user;
        return Task.FromResult(new MobileDashboardResponse
        {
            AsOfUtc = new DateTime(2026, 9, 15, 6, 30, 0, DateTimeKind.Utc),
            BusinessDate = "2026-09-15",
            Inventory = new MobileInventoryKpi { TotalMt = 12.5m, LowStockTankCount = 1 }
        });
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Test-only API endpoints used to exercise navigation, error mapping and idempotency.</summary>
[Route("api/mobile/v1/test-probe")]
public sealed class MobileTestProbeController : MobileApiControllerBase
{
    [HttpGet("reports")]
    [ApiNavigation(RoleNavigationKeys.Reports)]
    public IActionResult Reports() => Ok(new { ok = true });

    [HttpGet("business-rule")]
    public IActionResult BusinessRule() => throw new BusinessRuleException("TEST_RULE", "قاعدهٔ آزمایشی نقض شد.");

    [HttpGet("concurrency")]
    public IActionResult Concurrency() => throw new DbUpdateConcurrencyException("Row version mismatch on table X.");

    [HttpGet("crash")]
    public IActionResult Crash() => throw new InvalidOperationException("Host=db.internal;Password=super-secret");

    [HttpPost("write")]
    [RequireIdempotencyKey("Mobile.Test.Write")]
    public IActionResult Write() => Ok(new { token = MobileIdempotency.Get(HttpContext)?.Token });
}
