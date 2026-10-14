using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Security.Mobile;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.OfflineSync;

namespace PTGOilSystem.Web.Controllers.Api.Sync;

/// <summary>
/// <c>/api/sync/v1</c> — همگام‌سازی مشعل آفلاین (PWA نصب‌شده روی همین دامنه، و بعداً Flutter).
/// کوکی وب و Bearer موبایل هر دو پذیرفته می‌شوند؛ نقش و صلاحیت همان <see cref="RoleAccessRules"/> وب است
/// و در هر درخواست از دیتابیس خوانده می‌شود، پس کاربر غیرفعال یا بی‌صلاحیت پس از اتصال چیزی نمی‌فرستد.
/// کوکی وب نقش را از زمان ورود نگه می‌دارد؛ پس این API کاربر و نقش را در هر درخواست از دیتابیس می‌خواند
/// (<see cref="LoadFreshPrincipalAsync"/>) و کاربر غیرفعال را با 401 رد می‌کند.
/// نوشتن‌ها هدر <c>X-PTG-Offline: 1</c> می‌خواهند تا درخواست میان‌دامنه‌ای با کوکی ممکن نباشد.
/// </summary>
[ApiController]
[Route("api/sync/v1")]
[Authorize(AuthenticationSchemes = AuthenticationSetup.OfflineSyncScheme)]
[ApiNavigation(RoleNavigationKeys.Dashboard)]
[TypeFilter(typeof(ApiExceptionFilter))]
public sealed class OfflineSyncApiController(
    ApplicationDbContext db,
    IOfflineSyncService sync,
    IOfflineSnapshotService snapshots,
    ISystemCompanyProvider companies,
    ICurrentUserContext currentUser,
    TimeProvider clock,
    IMobileTokenService mobileTokens,
    MobileJwtKeyProvider mobileKeys,
    ClientModuleProfile? clientModules = null) : ControllerBase
{
    /// <summary>بررسی واقعیِ دسترسی به سرور (نه فقط navigator.onLine). بدون ورود هم پاسخ می‌دهد.</summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    public IActionResult Ping()
    {
        NoStore();
        return Ok(new
        {
            ok = true,
            apiVersion = 1,
            serverTimeUtc = clock.GetUtcNow().UtcDateTime,
            authenticated = User.Identity?.IsAuthenticated == true
        });
    }

    [HttpGet("session")]
    public async Task<ActionResult<OfflineSessionResponse>> Session(CancellationToken ct)
    {
        NoStore();
        var principal = await LoadFreshPrincipalAsync(ct);
        if (principal is null || currentUser.UserId is not { } userId)
        {
            return InactiveUser();
        }

        Company company;
        try
        {
            company = await companies.GetOwnerCompanyAsync(ct);
        }
        catch (SystemCompanyConfigurationException)
        {
            // پروفایل آفلاین به شرکت مالک بسته است؛ بدون آن فعال‌سازی ممکن نیست (پیام روشن به‌جای خطای 500).
            return ApiProblem.Result(HttpContext, StatusCodes.Status409Conflict, ApiErrorCodes.BusinessRule,
                "شرکت مالک سیستم تعیین نشده است (باید دقیقاً یک شرکت مالک باشد). مدیر سیستم آن را در «شرکت‌ها» تعیین کند؛ تا آن زمان کار بدون سرور فعال نمی‌شود.");
        }

        return Ok(new OfflineSessionResponse
        {
            UserId = userId,
            Username = currentUser.Username ?? "",
            FullName = currentUser.FullName,
            Role = currentUser.RoleName,
            CompanyId = company.Id,
            CompanyName = company.Name,
            Navigation = RoleAccessRules.AllowedNavigationForUser(principal).ToList(),
            OperationTypes = OfflineOperationTypes.AllowedFor(principal, clientModules)
                .Select(t => new OfflineOperationTypeDto { Key = t.Key, Label = t.Label, Module = t.NavigationKey })
                .ToList(),
            ServerTimeUtc = clock.GetUtcNow().UtcDateTime
        });
    }

    [HttpGet("snapshot")]
    public async Task<ActionResult<OfflineSnapshotResponse>> Snapshot(CancellationToken ct)
    {
        NoStore();
        var principal = await LoadFreshPrincipalAsync(ct);
        return principal is null ? InactiveUser() : Ok(await snapshots.BuildAsync(principal, ct));
    }

    [HttpPost("devices")]
    [RequireOfflineClient]
    public async Task<IActionResult> RegisterDevice([FromBody] OfflineDeviceRegistrationRequest request, CancellationToken ct)
    {
        NoStore();
        if (await LoadFreshPrincipalAsync(ct) is null || currentUser.UserId is not { } userId)
        {
            return InactiveUser();
        }

        if (request.DeviceId == Guid.Empty)
        {
            return ApiProblem.Result(HttpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.Validation, "شناسهٔ دستگاه لازم است.");
        }

        await sync.RegisterDeviceAsync(userId, request, ct);
        return NoContent();
    }

    /// <summary>
    /// دستگاه مورداعتماد: پس از فعال‌سازی با ورود عادی وب (کوکی)، یک نشست تازه‌سازیِ مخصوص همین دستگاه از همان
    /// <see cref="IMobileTokenService"/> موبایل صادر می‌شود (چرخش توکن، تشخیص استفادهٔ دوباره، ابطال، بررسی فعال‌بودن
    /// کاربر در هر تازه‌سازی). کوکی وب با بستن برنامه از بین می‌رود؛ این توکن (رمزگذاری‌شده در دستگاه) اجازه می‌دهد
    /// همگام‌سازی پس از Restart بدون ورود دوباره انجام شود. با Bearer نمی‌توان نشست دستگاه تازه ساخت.
    /// </summary>
    [HttpPost("device-session")]
    [RequireOfflineClient]
    public async Task<IActionResult> DeviceSession([FromBody] OfflineDeviceRegistrationRequest request, CancellationToken ct)
    {
        NoStore();
        if (Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblem.Result(HttpContext, StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                "نشست دستگاه فقط پس از ورود عادی ساخته می‌شود.");
        }

        if (currentUser.UserId is not { } userId || request.DeviceId == Guid.Empty)
        {
            return InactiveUser();
        }

        var user = await db.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user is null)
        {
            return InactiveUser();
        }

        if (!mobileKeys.CanIssueTokens)
        {
            return ApiProblem.Result(HttpContext, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.MobileAuthUnavailable,
                "نشست دستگاه در این سرور فعال نیست؛ همگام‌سازی با ورود عادی انجام می‌شود.");
        }

        await sync.RegisterDeviceAsync(userId, request, ct);
        var pair = await mobileTokens.IssueForLoginAsync(user,
            new MobileClientInfo(request.DeviceId.ToString("D"), request.DeviceName ?? "Mashal PWA", HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        return Ok(new
        {
            tokenType = "Bearer",
            accessToken = pair.AccessToken,
            accessTokenExpiresAtUtc = pair.AccessTokenExpiresAtUtc,
            refreshToken = pair.RefreshToken,
            refreshTokenExpiresAtUtc = pair.RefreshTokenExpiresAtUtc
        });
    }

    [HttpPost("operations")]
    [RequireOfflineClient]
    public async Task<ActionResult<OfflinePushResponse>> Push([FromBody] OfflinePushRequest request, CancellationToken ct)
    {
        NoStore();
        var principal = await LoadFreshPrincipalAsync(ct);
        if (principal is null || currentUser.UserId is not { } userId)
        {
            return InactiveUser();
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        return Ok(await sync.PushAsync(principal, userId, request, ip, ct));
    }

    [HttpPost("operations/status")]
    [RequireOfflineClient]
    public async Task<ActionResult<OfflineStatusResponse>> Status([FromBody] OfflineStatusRequest request, CancellationToken ct)
    {
        NoStore();
        var principal = await LoadFreshPrincipalAsync(ct);
        if (principal is null || currentUser.UserId is not { } userId)
        {
            return InactiveUser();
        }

        return Ok(await sync.GetStatusesAsync(userId, request.Ids, principal, ct));
    }

    /// <summary>کاربر فعال با نقش فعلیِ دیتابیس؛ null یعنی غیرفعال/حذف‌شده.</summary>
    private async Task<ClaimsPrincipal?> LoadFreshPrincipalAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        return user is null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity(UserClaimsFactory.Build(user), "OfflineSync"));
    }

    private ObjectResult InactiveUser()
        => ApiProblem.Result(HttpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized,
            "نشست معتبر نیست یا کاربر غیرفعال شده است؛ دوباره وارد شوید.");

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}

/// <summary>
/// نوشتن روی <c>/api/sync</c> فقط با هدر سفارشی. فرم یا تصویر میان‌دامنه‌ای نمی‌تواند این هدر را بفرستد
/// و fetch میان‌دامنه‌ای با آن به preflight و سیاست CORS برنامه برمی‌خورد؛ پس کوکی وب سطح CSRF نمی‌سازد.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireOfflineClientAttribute : Attribute, IActionFilter
{
    public const string HeaderName = "X-PTG-Offline";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!string.Equals(context.HttpContext.Request.Headers[HeaderName], "1", StringComparison.Ordinal))
        {
            context.Result = ApiProblem.Result(
                context.HttpContext,
                StatusCodes.Status400BadRequest,
                "offline_client_header_required",
                "درخواست همگام‌سازی معتبر نیست.");
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
