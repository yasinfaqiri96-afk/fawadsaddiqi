using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using PTGOilSystem.Web.Services.FormDrafts;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// فیلدهای پیش‌نویس خودکار را داخل فرم ثبت رندر می‌کند. <c>form-draft.js</c> فقط فرم‌هایی را پیش‌نویس می‌کند
/// که این helper را دارند؛ کلید و Revision پیش‌نویسِ در حال ادامه از <see cref="FormDraftViewState"/> می‌آید.
/// </summary>
public static class FormDraftHtmlHelper
{
    public static IHtmlContent FormDraft(this IHtmlHelper html, string formKey)
    {
        var form = FormDraftForms.Find(formKey)
            ?? throw new ArgumentException($"Form '{formKey}' is not registered in {nameof(FormDraftForms)}.", nameof(formKey));
        var context = html.ViewContext.HttpContext;
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return HtmlString.Empty;
        }

        var state = html.ViewData[FormDraftViewState.ViewDataKey] as FormDraftViewState
            ?? context.Items[FormDraftViewState.ViewDataKey] as FormDraftViewState;
        var encoder = HtmlEncoder.Default;
        var baseUrl = $"{context.Request.PathBase}/FormDrafts";
        return new HtmlString(
            $"<input type=\"hidden\" name=\"{FormDraftFields.Key}\" value=\"{(state is null ? "" : state.Key.ToString("D"))}\" autocomplete=\"off\""
            + $" data-ptg-draft=\"{encoder.Encode(form.Key)}\" data-ptg-draft-revision=\"{state?.Revision ?? 0}\""
            + $" data-ptg-draft-user=\"{encoder.Encode(userId)}\" data-ptg-draft-url=\"{encoder.Encode(baseUrl)}\" />"
            + $"<input type=\"hidden\" name=\"{FormDraftFields.Form}\" value=\"{encoder.Encode(form.Key)}\" />");
    }
}
