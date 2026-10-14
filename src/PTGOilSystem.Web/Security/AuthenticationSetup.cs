using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Security.Mobile;

namespace PTGOilSystem.Web.Security;

/// <summary>
/// احراز هویت دوگانه: وب همچنان با کوکی (طرح پیش‌فرض، بدون تغییر رفتار) و موبایل با JWT Bearer
/// فقط روی سیاست <see cref="AuthPolicies.MobileApi"/>. هر دو از همان جدول کاربر، نقش و
/// <see cref="RoleAccessRules"/> استفاده می‌کنند.
/// </summary>
public static class AuthenticationSetup
{
    /// <summary>کوکی یا Bearer، بر اساس هدر Authorization (فقط برای <c>/api/sync</c>).</summary>
    public const string OfflineSyncScheme = "PtgOfflineSync";

    public static IServiceCollection AddPtgAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var mobileSection = configuration.GetSection(MobileAuthOptions.SectionName);
        var mobileOptions = mobileSection.Get<MobileAuthOptions>() ?? new MobileAuthOptions();
        services.Configure<MobileAuthOptions>(mobileSection);

        var signingKey = string.IsNullOrWhiteSpace(mobileOptions.SigningKey)
            ? configuration[MobileAuthOptions.SigningKeyEnvironmentVariable]
            : mobileOptions.SigningKey;
        var keys = MobileJwtKeyProvider.Create(signingKey, environment.IsDevelopment());
        services.AddSingleton(keys);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                // کوکی‌ها به پورت وابسته نیستند: اجرای محلیِ دوم (مثلاً PWA روی 5400) نام جدا می‌گیرد تا ورود/خروج
                // یک نسخه نشست نسخهٔ دیگر را عوض نکند. بدون تنظیم، همان نام همیشگی.
                options.Cookie.Name = string.IsNullOrWhiteSpace(configuration["PTG_AUTH_COOKIE_NAME"])
                    ? "PTGOilSystem.Auth"
                    : configuration["PTG_AUTH_COOKIE_NAME"];
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = "/Auth/Login";
                options.AccessDeniedPath = "/Auth/AccessDenied";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);

                // مسیرهای /api هرگز به صفحهٔ HTML ورود یا عدم دسترسی Redirect نمی‌شوند؛
                // رفتار صفحات وب (از جمله درخواست‌های AJAX) همان پیش‌فرض قبلی است.
                var redirectToLogin = options.Events.OnRedirectToLogin;
                var redirectToAccessDenied = options.Events.OnRedirectToAccessDenied;
                options.Events.OnRedirectToLogin = context => ApiRequest.IsApi(context.HttpContext)
                    ? ApiProblem.WriteAsync(
                        context.HttpContext,
                        StatusCodes.Status401Unauthorized,
                        ApiErrorCodes.Unauthorized,
                        ApiProblem.UnauthorizedMessage)
                    : redirectToLogin(context);
                options.Events.OnRedirectToAccessDenied = context => ApiRequest.IsApi(context.HttpContext)
                    ? ApiProblem.WriteAsync(
                        context.HttpContext,
                        StatusCodes.Status403Forbidden,
                        ApiErrorCodes.Forbidden,
                        ApiProblem.ForbiddenMessage)
                    : redirectToAccessDenied(context);
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = mobileOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = mobileOptions.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = keys.Key,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(Math.Max(0, mobileOptions.ClockSkewSeconds)),
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role
                };
                options.Events = MobileJwtBearerEvents.Create();
            })
            // مشعل آفلاین (/api/sync): PWA با کوکی وب و Flutter با Bearer. یک طرح، تا challenge فقط یک‌بار نوشته شود.
            .AddPolicyScheme(OfflineSyncScheme, OfflineSyncScheme, options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : CookieAuthenticationDefaults.AuthenticationScheme;
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.ManageData,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanManageData(context.User)));
            options.AddPolicy(AuthPolicies.AdminOnly,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanManageUsers(context.User)));
            // پشتیبان‌گیری بالاترین سطح است: مدیریت کاربران به‌تنهایی کافی نیست.
            options.AddPolicy(AuthPolicies.BackupAdmin,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanManageBackups(context.User)));
            // بستن دورهٔ مالی گزارش‌های امضاشدهٔ گذشته را قفل می‌کند؛ همان سطحِ پشتیبان‌گیری.
            options.AddPolicy(AuthPolicies.OperationalPeriodAdmin,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanManageOperationalPeriodLock(context.User)));
            options.AddPolicy(AuthPolicies.HrViewSalary,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanViewEmployeeSalary(context.User)));
            options.AddPolicy(AuthPolicies.HrManageSalary,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanManageEmployeeSalary(context.User)));
            options.AddPolicy(AuthPolicies.HrRunPayroll,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanRunPayroll(context.User)));
            options.AddPolicy(AuthPolicies.HrPaySalary,
                policy => policy.RequireAssertion(context => RoleAccessRules.CanPaySalary(context.User)));
            // API موبایل فقط Bearer را می‌پذیرد؛ کوکی مرورگر روی /api/mobile اعتبار ندارد (بدون سطح CSRF).
            options.AddPolicy(AuthPolicies.MobileApi,
                policy => policy
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser());
        });

        return services;
    }
}
