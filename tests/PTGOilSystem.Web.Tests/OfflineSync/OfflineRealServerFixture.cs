using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Services.OfflineSync;
using Xunit;

namespace PTGOilSystem.Web.Tests.OfflineSync;

/// <summary>
/// برنامهٔ واقعی مشعل (همان Program.cs، همهٔ سرویس‌ها، کنترلرها، Viewها و فیلترها) به‌عنوان یک پروسهٔ جدا روی
/// یک دیتابیس PostgreSQL موقتِ آزمایشی با پیشوند محافظت‌شدهٔ <see cref="DatabaseSafetyGuard.AccountingTestDatabasePrefix"/>.
/// Migrationها فقط روی همین دیتابیس موقت اجرا و در پایان دیتابیس حذف می‌شود. دیتابیس اصلی دست نمی‌خورد.
///
/// فقط وقتی اجرا می‌شود که <c>PTG_OFFLINE_REAL_SERVER=1</c> باشد (به PostgreSQL محلی نیاز دارد).
/// </summary>
public sealed class OfflineRealServerFixture : IAsyncLifetime
{
    public const string EnabledVariable = "PTG_OFFLINE_REAL_SERVER";
    public const string AdminPassword = "Offline-Admin-2026!";
    public const string ClerkPassword = "Offline-Clerk-2026!";

    private readonly string _adminConnection =
        Environment.GetEnvironmentVariable("PTG_TEST_POSTGRES_ADMIN")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres;Timeout=10;Command Timeout=120";

    private Process? _server;
    private readonly StringBuilder _serverLog = new();

    public static bool Enabled => Environment.GetEnvironmentVariable(EnabledVariable) == "1";

    public string DatabaseName { get; } = $"{DatabaseSafetyGuard.AccountingTestDatabasePrefix}offline_{Guid.NewGuid():N}"[..58];
    public string ConnectionString { get; private set; } = "";
    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1:5191");
    public string ServerLog => _serverLog.ToString();

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseSafetyGuard.EnsureIntegrationTestCreateAllowed(DatabaseName);
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = DatabaseName }.ConnectionString;

        var root = FindRepositoryRoot();
        var webDir = Path.Combine(root, "src", "PTGOilSystem.Web");
        var dll = Path.Combine(webDir, "bin", "Debug", "net8.0", "PTGOilSystem.Web.dll");
        Assert.True(File.Exists(dll), $"Build the Web project first: {dll}");

        var port = 5100 + Random.Shared.Next(0, 800);
        BaseAddress = new Uri($"http://127.0.0.1:{port}");
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString);
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = webDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--contentRoot");
        info.ArgumentList.Add(webDir);
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ASPNETCORE_URLS"] = BaseAddress.ToString().TrimEnd('/');
        info.Environment["DATABASE_URL"] = $"postgres://{csb.Username}:{csb.Password}@{csb.Host}:{csb.Port}/{DatabaseName}";
        info.Environment["PTG_AUTO_MIGRATE"] = "true";
        info.Environment["PTG_BOOTSTRAP_ADMIN_USERNAME"] = "admin";
        info.Environment["PTG_BOOTSTRAP_ADMIN_PASSWORD"] = AdminPassword;
        info.Environment["PTG_DEV_AUTO_SIGNIN"] = "false";
        info.Environment["PTG_UI_PICK"] = "0";
        info.Environment["Backup__SchedulerEnabled"] = "false";
        info.Environment["Logging__LogLevel__Default"] = "Warning";

        _server = Process.Start(info) ?? throw new InvalidOperationException("Could not start the Web server.");
        _server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_serverLog) _serverLog.AppendLine(e.Data); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_serverLog) _serverLog.AppendLine(e.Data); };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var probe = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddMinutes(6);
        while (true)
        {
            if (_server.HasExited)
            {
                throw new InvalidOperationException("Web server exited during startup:\n" + ServerLog);
            }

            try
            {
                if ((await probe.GetAsync("/health")).IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Web server did not become healthy:\n" + ServerLog);
            }

            await Task.Delay(1000);
        }
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
        {
            // سابقهٔ سرور برای عیب‌یابی (بدون رمز؛ لاگ برنامه رمز را نمی‌نویسد).
            var logPath = Path.Combine(Path.GetTempPath(), "mashal-offline-real-server.log");
            await File.WriteAllTextAsync(logPath, ServerLog);
        }

        if (_server is { HasExited: false })
        {
            _server.Kill(entireProcessTree: true);
            await _server.WaitForExitAsync();
        }

        _server?.Dispose();
        if (!Enabled || string.IsNullOrEmpty(ConnectionString))
        {
            return;
        }

        DatabaseSafetyGuard.EnsureIntegrationTestDropAllowed(DatabaseName);
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public ApplicationDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>ورود با همان فرم /Auth/Login (Antiforgery واقعی) و برگرداندن کوکی نشست.</summary>
    public async Task<OfflineClient> LoginAsync(string username, string password)
    {
        var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        var http = new HttpClient(handler) { BaseAddress = BaseAddress, Timeout = TimeSpan.FromMinutes(3) };
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        var page = await http.GetAsync("/Auth/Login");
        Collect(page, cookies);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "Login antiforgery token not found.");

        using var login = new HttpRequestMessage(HttpMethod.Post, "/Auth/Login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = username,
                ["Password"] = password,
                ["__RequestVerificationToken"] = token
            })
        };
        login.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
        var response = await http.SendAsync(login);
        Collect(response, cookies);
        Assert.True(cookies.ContainsKey("PTGOilSystem.Auth"), $"Login failed for {username}: {(int)response.StatusCode}");
        return new OfflineClient(http, $"PTGOilSystem.Auth={cookies["PTGOilSystem.Auth"]}");
    }

    private static void Collect(HttpResponseMessage response, Dictionary<string, string> cookies)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            var pair = value.Split(';')[0];
            var index = pair.IndexOf('=');
            if (index > 0)
            {
                cookies[pair[..index]] = pair[(index + 1)..];
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ptg-oil-system.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>کلاینت /api/sync/v1 با کوکی وب، مثل PWA.</summary>
public sealed class OfflineClient(HttpClient http, string cookie)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid DeviceId { get; } = Guid.NewGuid();
    public HttpClient Http => http;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, "/api/sync/v1" + path);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-PTG-Offline", "1");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await http.SendAsync(request);
    }

    public async Task<OfflineSessionResponse> SessionAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OfflineSessionResponse>(Json))!;
    }

    public async Task RegisterAsync()
    {
        var response = await SendAsync(HttpMethod.Post, "/devices", new { deviceId = DeviceId, deviceName = "Integration" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static object Op(Guid id, int userId, string type, object payload, string businessDate,
        int revision = 1, IEnumerable<Guid>? dependencies = null)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        return new
        {
            clientOperationId = id,
            userId,
            operationType = type,
            payloadJson = json,
            payloadHash = OfflineSyncService.ComputeHash(json),
            createdAtUtc = DateTime.UtcNow,
            businessDate,
            schemaVersion = 1,
            revision,
            dependencies = dependencies?.ToArray() ?? []
        };
    }

    public async Task<OfflinePushResponse> PushAsync(params object[] operations)
    {
        var response = await SendAsync(HttpMethod.Post, "/operations", new { deviceId = DeviceId, operations });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"push failed {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<OfflinePushResponse>(text, Json)!;
    }

    public async Task<OfflineStatusResponse> StatusAsync(params Guid[] ids)
    {
        var response = await SendAsync(HttpMethod.Post, "/operations/status", new { ids });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OfflineStatusResponse>(Json))!;
    }
}

/// <summary>[Fact] که بدون PostgreSQL محلی و <c>PTG_OFFLINE_REAL_SERVER=1</c> رد (Skip) می‌شود.</summary>
public sealed class RealServerFactAttribute : FactAttribute
{
    public RealServerFactAttribute()
    {
        if (!OfflineRealServerFixture.Enabled)
        {
            Skip = $"Set {OfflineRealServerFixture.EnabledVariable}=1 (needs local PostgreSQL) to run the real-server offline tests.";
        }
    }
}
