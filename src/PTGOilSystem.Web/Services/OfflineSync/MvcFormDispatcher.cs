using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services.OfflineSync;

/// <summary>نتیجهٔ اجرای یک فرم ثبت موجود.</summary>
public sealed record MvcFormResult(
    bool Committed,
    string? ReferenceId,
    IReadOnlyList<string> Errors,
    bool Forbidden,
    bool TransientFailure);

public interface IMvcFormDispatcher
{
    /// <summary>
    /// همان POSTِ فرم موجود را به نام <paramref name="user"/> اجرا می‌کند: سیاست [Authorize]، فیلترهای سراسری
    /// (ناوبری نقش، کد خودکار، قفل دوره، قواعد تجاری)، Antiforgery، Model Binding و اعتبارسنجی، و تراکنش خودِ
    /// اکشن. هیچ منطق تجاری اینجا تکرار نشده است.
    /// </summary>
    /// <param name="formToken">توکن ثابت ضد تکرار؛ اکشن آن را در همان تراکنشِ سند در ProcessedFormTokens ثبت می‌کند.</param>
    /// <param name="fieldList">
    /// فیلدهای فورم با ترتیب و نام تکراری (چک‌باکس، چند انتخابی، سطرها) دقیقاً مثل مرورگر؛ اگر داده شود به‌جای
    /// <paramref name="fields"/> فرستاده می‌شود.
    /// </param>
    /// <param name="query">querystringِ آدرس فورم (مثلاً returnUrl یا شناسهٔ قرارداد).</param>
    /// <param name="wrapInTransaction">
    /// برای اکشنی که خودش تراکنش و توکن ندارد: سند و <paramref name="onCommittedInTransaction"/> در یک تراکنش.
    /// </param>
    Task<MvcFormResult> PostAsync(
        string controller,
        string action,
        IReadOnlyDictionary<string, string?> fields,
        ClaimsPrincipal user,
        string? formToken,
        bool wrapInTransaction,
        Func<ApplicationDbContext, string?, Task>? onCommittedInTransaction,
        CancellationToken ct,
        string? referenceType = null,
        IReadOnlyList<KeyValuePair<string, string?>>? fieldList = null,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null);
}

public sealed class MvcFormDispatcher(
    IServiceScopeFactory scopes,
    IHttpContextAccessor httpContextAccessor,
    IActionDescriptorCollectionProvider descriptors,
    EndpointDataSource endpoints,
    ILogger<MvcFormDispatcher> logger) : IMvcFormDispatcher
{
    public const string FormTokenField = PTGOilSystem.Web.Helpers.FormTokenHtmlHelper.FieldName;

    public async Task<MvcFormResult> PostAsync(
        string controller,
        string action,
        IReadOnlyDictionary<string, string?> fields,
        ClaimsPrincipal user,
        string? formToken,
        bool wrapInTransaction,
        Func<ApplicationDbContext, string?, Task>? onCommittedInTransaction,
        CancellationToken ct,
        string? referenceType = null,
        IReadOnlyList<KeyValuePair<string, string?>>? fieldList = null,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null)
    {
        var descriptor = descriptors.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .FirstOrDefault(d => string.Equals(d.ControllerName, controller, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.ActionName, action, StringComparison.OrdinalIgnoreCase)
                && d.ActionConstraints?.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
                    .Any(c => c.HttpMethods.Contains("POST", StringComparer.OrdinalIgnoreCase)) == true)
            ?? throw new InvalidOperationException($"POST action {controller}.{action} was not found.");

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();

        var context = new DefaultHttpContext { RequestServices = services, User = user };
        var outer = httpContextAccessor.HttpContext;
        context.Connection.RemoteIpAddress = outer?.Connection.RemoteIpAddress;
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = outer?.Request.Scheme ?? "https";
        context.Request.Host = outer?.Request.Host ?? new HostString("localhost");
        context.Request.Path = $"/{descriptor.ControllerName}/{descriptor.ActionName}";
        // نسخهٔ سبکِ Layout برای حالت خطا (همان قرارداد spa-nav)؛ بدنهٔ پاسخ دور ریخته می‌شود.
        context.Request.Headers["X-PTG-SPA"] = "1";
        context.Request.Headers["X-PTG-Offline-Apply"] = "1";
        context.Response.Body = Stream.Null;

        // سیاست‌های [Authorize] اکشن/کنترلر؛ در مسیر عادی این کار را AuthorizationMiddleware می‌کند.
        var authorizeData = descriptor.EndpointMetadata.OfType<IAuthorizeData>().ToList();
        if (authorizeData.Count > 0)
        {
            var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
            var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData);
            if (policy is not null)
            {
                var allowed = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
                if (!allowed.Succeeded)
                {
                    return new MvcFormResult(false, null, ["شما اجازهٔ این ثبت را ندارید."], Forbidden: true, TransientFailure: false);
                }
            }
        }

        // Antiforgery واقعی: توکن برای همین کاربر ساخته و مثل مرورگر فرستاده می‌شود؛ [ValidateAntiForgeryToken] سر جایش می‌ماند.
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var antiforgeryOptions = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        // توکن روی یک HttpContext جدا ساخته می‌شود؛ Antiforgery وضعیت کوکیِ هر درخواست را cache می‌کند.
        var tokens = antiforgery.GetTokens(new DefaultHttpContext { RequestServices = services, User = user });
        var form = (fieldList ?? fields.ToList())
            .Where(f => !string.Equals(f.Key, tokens.FormFieldName, StringComparison.Ordinal)
                && !(formToken is not null && string.Equals(f.Key, FormTokenField, StringComparison.Ordinal)))
            .ToList();
        form.Add(new(tokens.FormFieldName, tokens.RequestToken));
        if (!string.IsNullOrWhiteSpace(formToken))
        {
            form.Add(new(FormTokenField, formToken));
        }

        if (query is { Count: > 0 })
        {
            context.Request.QueryString = QueryString.Create(query.Where(q => q.Value is not null));
        }

        context.Request.Headers.Cookie = $"{antiforgeryOptions.Cookie.Name}={tokens.CookieToken}";
        if (tokens.HeaderName is not null)
        {
            context.Request.Headers[tokens.HeaderName] = tokens.RequestToken;
        }

        var body = string.Join('&', form
            .Where(f => f.Value is not null)
            .Select(f => $"{UrlEncoder.Default.Encode(f.Key)}={UrlEncoder.Default.Encode(f.Value!)}"));
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);

        var routeData = new RouteData();
        routeData.Values["controller"] = descriptor.ControllerName;
        routeData.Values["action"] = descriptor.ActionName;
        context.Request.RouteValues = new RouteValueDictionary(routeData.Values);
        var endpoint = endpoints.Endpoints.FirstOrDefault(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() == descriptor);
        if (endpoint is not null)
        {
            context.SetEndpoint(endpoint);
        }

        var actionContext = new ActionContext(context, routeData, descriptor);
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        httpContextAccessor.HttpContext = context;
        try
        {
            if (wrapInTransaction && db.Database.IsRelational())
            {
                transaction = await db.Database.BeginTransactionAsync(ct);
            }

            var invoker = services.GetRequiredService<IActionInvokerFactory>().CreateInvoker(actionContext)
                ?? throw new InvalidOperationException("No action invoker.");
            await invoker.InvokeAsync();

            var tempData = services.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(context);
            var errors = CollectErrors(actionContext, tempData);
            var location = context.Response.Headers.Location.ToString();
            var redirected = context.Response.StatusCode is StatusCodes.Status302Found or StatusCodes.Status303SeeOther
                or StatusCodes.Status301MovedPermanently or StatusCodes.Status307TemporaryRedirect;
            // شمارهٔ سند: از آدرس Redirect، وگرنه همان موجودیتی که اکشن در همین DbContext ساخت.
            var referenceId = ExtractId(location) ?? TrackedReferenceId(db, referenceType);

            bool committed;
            if (!string.IsNullOrWhiteSpace(formToken))
            {
                // حقیقت نهایی: توکن فقط در همان تراکنشِ سند ذخیره می‌شود.
                committed = await db.ProcessedFormTokens.AsNoTracking().AnyAsync(t => t.Token == formToken, ct);
            }
            else
            {
                committed = redirected && errors.Count == 0 && actionContext.ModelState.IsValid;
            }

            if (committed && transaction is not null)
            {
                if (onCommittedInTransaction is not null)
                {
                    await onCommittedInTransaction(db, referenceId);
                }

                await transaction.CommitAsync(ct);
            }
            else if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            if (!committed && errors.Count == 0)
            {
                errors = [redirected ? "ثبت انجام نشد." : "ثبت انجام نشد؛ اطلاعات فرم کامل یا معتبر نیست."];
            }

            return new MvcFormResult(committed, committed ? referenceId : null, committed ? [] : errors, false, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            var transient = ex is TimeoutException or DbUpdateConcurrencyException
                || ex is Npgsql.NpgsqlException { IsTransient: true }
                || ex.InnerException is Npgsql.NpgsqlException { IsTransient: true };
            logger.LogWarning(ex, "Offline apply of {Controller}.{Action} failed.", controller, action);

            // اگر سند پیش از خطا ثبت شده بود (مثلاً خطا در رندر پاسخ)، توکن آن را نشان می‌دهد.
            if (!string.IsNullOrWhiteSpace(formToken))
            {
                await using var checkScope = scopes.CreateAsyncScope();
                var checkDb = checkScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (await checkDb.ProcessedFormTokens.AsNoTracking().AnyAsync(t => t.Token == formToken, CancellationToken.None))
                {
                    return new MvcFormResult(true, null, [], false, false);
                }
            }

            var message = TranslateException(ex);
            return new MvcFormResult(false, null, [message], false, transient);
        }
        finally
        {
            httpContextAccessor.HttpContext = outer;
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>همان ترجمهٔ پیام کاربرپسند فیلتر قواعد تجاری؛ متن فنی فقط در لاگ.</summary>
    private static string TranslateException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PTGOilSystem.Web.Services.Exceptions.BusinessRuleException business)
            {
                return business.Message;
            }
        }

        return PTGOilSystem.Web.Services.OperationalPeriod.BusinessRuleExceptionFilter.Translate(ex)
            ?? "ثبت در سرور با خطای پیش‌بینی‌نشده روبه‌رو شد؛ هیچ سندی ثبت نشد.";
    }

    private static List<string> CollectErrors(ActionContext actionContext, ITempDataDictionary tempData)
    {
        var errors = new List<string>();
        // اکشن‌ها پیام خطا را با هر دو کلید رایج می‌گذارند؛ Redirect همراه پیام خطا یعنی ثبت انجام نشده است.
        foreach (var key in new[] { "err", "error" })
        {
            if (tempData.Peek(key) is string err && !string.IsNullOrWhiteSpace(err) && !errors.Contains(err.Trim()))
            {
                errors.Add(err.Trim());
            }
        }

        foreach (var entry in actionContext.ModelState.Values)
        {
            foreach (var error in entry.Errors)
            {
                var message = string.IsNullOrWhiteSpace(error.ErrorMessage) ? error.Exception?.Message : error.ErrorMessage;
                if (!string.IsNullOrWhiteSpace(message) && !errors.Contains(message.Trim()))
                {
                    errors.Add(message.Trim());
                }
            }
        }

        return errors.Take(8).ToList();
    }

    private static string? TrackedReferenceId(ApplicationDbContext db, string? referenceType)
    {
        if (string.IsNullOrWhiteSpace(referenceType))
        {
            return null;
        }

        var id = db.ChangeTracker.Entries()
            .Where(e => string.Equals(e.Entity.GetType().Name, referenceType, StringComparison.Ordinal))
            .Select(e => e.Entity)
            .OfType<PTGOilSystem.Web.Models.Entities.BaseEntity>()
            .Select(e => e.Id)
            .Where(value => value > 0)
            .DefaultIfEmpty()
            .Max();
        return id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string? ExtractId(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(location, @"/Details/(\d+)|[?&]id=(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }
}
