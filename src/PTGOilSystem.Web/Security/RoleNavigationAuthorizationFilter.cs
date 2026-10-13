using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PTGOilSystem.Web.Infrastructure.Api;

namespace PTGOilSystem.Web.Security;

public sealed class RoleNavigationAuthorizationFilter : IAsyncActionFilter
{
    private readonly ClientModuleProfile _clientModules;

    public RoleNavigationAuthorizationFilter(ClientModuleProfile? clientModules = null)
    {
        _clientModules = clientModules ?? ClientModuleProfile.Full;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        // API صفحهٔ ناوبری نیست: کلید از [ApiNavigation] خوانده می‌شود (نه از نام کنترلر) و با همان
        // RoleAccessRules وب سنجیده می‌شود. ردّ دسترسی 403 JSON است، نه Redirect به صفحهٔ HTML.
        // API بدون کلید صریح رد می‌شود.
        if (ApiRequest.IsApi(context.HttpContext))
        {
            var apiNavigation = context.ActionDescriptor.EndpointMetadata
                .OfType<ApiNavigationAttribute>()
                .LastOrDefault();
            if (apiNavigation is not null
                && RoleAccessRules.CanAccessNavigation(user, apiNavigation.NavigationKey, _clientModules))
            {
                await next();
                return;
            }

            context.Result = ApiProblem.Result(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                ApiProblem.ForbiddenMessage);
            return;
        }

        var controller = context.RouteData.Values["controller"]?.ToString();
        if (string.Equals(controller, "Auth", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // «راهنمای هوشمند» یک صفحهٔ قابل‌ناوبری نیست و کلید ناوبری ندارد، پس این فیلتر
        // آن را برای همه — حتی Admin — به AccessDenied می‌فرستاد. برای هر کاربر واردشده
        // باز است؛ دسترسی واقعی همچنان با [Authorize] روی خودِ Controller کنترل می‌شود.
        // «پیش‌نویس‌ها» هم کلید ناوبری مستقل ندارد؛ هر اکشن آن دسترسی همان فرمِ پیش‌نویس
        // (کنترلر فرم + ثبت داده) را خودش می‌سنجد و فقط پیش‌نویس‌های همان کاربر را نشان می‌دهد.
        if (string.Equals(controller, "Assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controller, "FormDrafts", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (RoleAccessRules.CanAccessController(user, controller, _clientModules))
        {
            await next();
            return;
        }

        context.Result = new RedirectToActionResult("AccessDenied", "Auth", null);
    }
}
