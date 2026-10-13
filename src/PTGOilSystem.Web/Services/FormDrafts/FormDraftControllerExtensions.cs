using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace PTGOilSystem.Web.Services.FormDrafts;

public static class FormDraftControllerExtensions
{
    /// <summary>
    /// پیش‌نویس باز همین کاربر را برای GET فرم می‌خواند و View را به ادامهٔ همان پیش‌نویس وصل می‌کند.
    /// پیش‌نویس ناموجود، ثبت‌شده یا مال کاربر دیگر: null و یک هشدار؛ فرم خالی عادی باز می‌شود.
    /// </summary>
    public static async Task<FormDraftSnapshot?> LoadFormDraftAsync(this Controller controller, Guid? draftKey, string formKey)
    {
        if (draftKey is null || draftKey == Guid.Empty)
        {
            return null;
        }

        var drafts = controller.HttpContext.RequestServices.GetRequiredService<IFormDraftService>();
        var snapshot = int.TryParse(controller.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? await drafts.FindOpenAsync(userId, draftKey.Value, formKey, controller.HttpContext.RequestAborted)
            : null;
        if (snapshot is null)
        {
            controller.TempData["Toast.Warning"] = "این پیش‌نویس پیدا نشد یا قبلاً ثبت نهایی شده است؛ فرم تازه باز شد.";
            return null;
        }

        controller.ViewData[FormDraftViewState.ViewDataKey] = new FormDraftViewState(snapshot.Key, snapshot.Revision);
        return snapshot;
    }
}
