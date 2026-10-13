using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Middleware;

public sealed class ActivityLogMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// بازهٔ ثبتِ «بازدید» برای یک کاربر روی یک مسیر. هر GET موفق یک سطر Audit می‌ساخت، پس
    /// هر رفرش/پیمایش/فراخوانی AJAX یک write به جدول اضافه می‌کرد و جدول بی‌دلیل بزرگ می‌شد.
    /// حالا همان بازدید در این بازه یک‌بار ثبت می‌شود. هیچ نوشتن/تغییر داده‌ای مشمول این
    /// قاعده نیست: POST/PUT/PATCH/DELETE و هر درخواست ناموفق همیشه کامل ثبت می‌شوند.
    /// </summary>
    private static readonly TimeSpan ReadAuditWindow = TimeSpan.FromMinutes(5);

    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ActivityLogMiddleware> _logger;
    private readonly IMemoryCache _readAuditWindows;

    public ActivityLogMiddleware(
        RequestDelegate next,
        IServiceScopeFactory scopeFactory,
        ILogger<ActivityLogMiddleware> logger,
        IMemoryCache readAuditWindows)
    {
        _next = next;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _readAuditWindows = readAuditWindows;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? pipelineException = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            pipelineException = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();

            if (ShouldLog(context) && ShouldPersistThisRequest(context, pipelineException))
            {
                try
                {
                    await PersistAuditLogAsync(context, stopwatch.ElapsedMilliseconds, pipelineException);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Activity log write failed for {Method} {Path}.", context.Request.Method, context.Request.Path);
                }
            }
        }
    }

    private static bool ShouldLog(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var descriptor = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (descriptor is null)
        {
            return false;
        }

        if (string.Equals(descriptor.ControllerName, "Auth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return context.User.Identity?.IsAuthenticated == true;
    }

    /// <summary>
    /// فقط «بازدیدهای تکراری و موفق» را کنار می‌گذارد. هر درخواستی که داده را تغییر می‌دهد،
    /// یا با خطا/وضعیت غیرموفق تمام شده، همیشه ثبت می‌شود؛ پس ردّ حسابرسی برای تغییرها و
    /// خطاها کامل باقی می‌ماند.
    /// </summary>
    private bool ShouldPersistThisRequest(HttpContext context, Exception? pipelineException)
    {
        if (pipelineException is not null)
        {
            return true;
        }

        var method = context.Request.Method;
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var statusCode = context.Response.StatusCode;

        // ذخیرهٔ خودکار پیش‌نویس هنگام تایپ هر چند ثانیه یک POST است و دادهٔ تجاری نمی‌سازد؛
        // نسخهٔ موفق آن سابقه نمی‌گیرد. حذف پیش‌نویس و هر خطا مثل همیشه ثبت می‌شود.
        var descriptor = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (statusCode is >= 200 and < 300
            && string.Equals(descriptor?.ControllerName, "FormDrafts", StringComparison.OrdinalIgnoreCase)
            && string.Equals(descriptor?.ActionName, "Save", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!isRead)
        {
            return true;
        }

        if (statusCode is < 200 or >= 400)
        {
            return true;
        }

        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"audit-read:{context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value}:{context.User.Identity?.Name}:{method}:{context.Request.Path.Value}:{context.Request.QueryString.Value}");

        if (_readAuditWindows.TryGetValue(key, out _))
        {
            return false;
        }

        _readAuditWindows.Set(key, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ReadAuditWindow
        });
        return true;
    }

    private async Task PersistAuditLogAsync(HttpContext context, long durationMs, Exception? pipelineException)
    {
        using var scope = _scopeFactory.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var currentUser = scope.ServiceProvider.GetRequiredService<PTGOilSystem.Web.Security.ICurrentUserContext>();
        var endpoint = context.GetEndpoint();
        var descriptor = endpoint!.Metadata.GetMetadata<ControllerActionDescriptor>()!;
        var statusCode = context.Response.StatusCode;
        var metadataJson = BuildMetadataJson(context, descriptor, pipelineException);
        var actionLabel = ResolveRequestAction(context.Request.Method);

        await audit.LogActivityAndSaveAsync(new AuditLogEntryInput
        {
            Category = AuditLogCategories.Request,
            EntityName = descriptor.ControllerName,
            EntityId = 0,
            Action = actionLabel,
            ActorUserId = currentUser.UserId,
            ActorUsername = currentUser.Username,
            Module = descriptor.ControllerName,
            Description = BuildDescription(context, descriptor, pipelineException),
            HttpMethod = context.Request.Method,
            RequestPath = context.Request.Path.Value,
            ControllerName = descriptor.ControllerName,
            ActionName = descriptor.ActionName,
            StatusCode = statusCode,
            IsSuccess = pipelineException is null && statusCode is >= 200 and < 400,
            CorrelationId = context.TraceIdentifier,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            DurationMs = durationMs,
            MetadataJson = metadataJson,
        });
    }

    private static string ResolveRequestAction(string method)
        => method.ToUpperInvariant() switch
        {
            "GET" => "GET",
            "POST" => "POST",
            "PUT" => "PUT",
            "PATCH" => "PATCH",
            "DELETE" => "DELETE",
            _ => method.ToUpperInvariant()
        };

    private static string BuildDescription(HttpContext context, ControllerActionDescriptor descriptor, Exception? pipelineException)
    {
        var baseText = $"{context.Request.Method.ToUpperInvariant()} {descriptor.ControllerName}/{descriptor.ActionName}";
        if (pipelineException is null)
        {
            return baseText;
        }

        return $"{baseText} failed: {pipelineException.GetType().Name}";
    }

    private static string BuildMetadataJson(HttpContext context, ControllerActionDescriptor descriptor, Exception? pipelineException)
    {
        var metadata = new
        {
            RouteValues = context.Request.RouteValues
                .Where(item => item.Value is not null)
                .ToDictionary(item => item.Key, item => item.Value?.ToString()),
            Query = context.Request.Query.ToDictionary(
                item => item.Key,
                item => item.Value.ToString()),
            Endpoint = descriptor.DisplayName,
            Exception = pipelineException is null
                ? null
                : new
                {
                    Type = pipelineException.GetType().FullName,
                    pipelineException.Message
                }
        };

        return JsonSerializer.Serialize(metadata, JsonOptions);
    }
}