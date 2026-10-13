using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.FormDrafts;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// پیش‌نویس‌های خودکار فرم‌های ثبت. هر کاربر فقط پیش‌نویس‌های خودش را می‌بیند و فقط برای فرمی پیش‌نویس
/// می‌سازد که به بخش آن و ثبت داده دسترسی دارد. هیچ اکشن این کنترلر سند، موجودی یا حسابداری نمی‌سازد.
/// </summary>
[Authorize]
public sealed class FormDraftsController : Controller
{
    private readonly IFormDraftService _drafts;
    private readonly ClientModuleProfile _clientModules;

    public FormDraftsController(IFormDraftService drafts, ClientModuleProfile? clientModules = null)
    {
        _drafts = drafts;
        _clientModules = clientModules ?? ClientModuleProfile.Full;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? formKey = null)
    {
        if (CurrentUserId is not { } userId)
        {
            return Challenge();
        }

        var form = FormDraftForms.Find(formKey);
        var items = (await _drafts.ListAsync(userId, form?.Key, includeSubmitted: true, HttpContext.RequestAborted))
            .Where(item => CanUse(FormDraftForms.Find(item.FormKey)))
            .ToList();
        return View(new FormDraftIndexViewModel(form, items));
    }

    [HttpGet]
    public async Task<IActionResult> List(string formKey)
    {
        var form = FormDraftForms.Find(formKey);
        if (CurrentUserId is not { } userId || !CanUse(form))
        {
            return Forbid();
        }

        var items = await _drafts.ListAsync(userId, form!.Key, includeSubmitted: false, HttpContext.RequestAborted);
        return Json(new
        {
            drafts = items.Select(item => new
            {
                key = item.Key,
                summary = item.Summary,
                rowCount = item.RowCount,
                lastSavedAtUtc = DateTime.SpecifyKind(item.LastSavedAtUtc, DateTimeKind.Utc),
                lastSavedText = item.LastSavedAtUtc.ToDisplayDateTime(),
                resumeUrl = Url.Content("~" + item.ResumeUrl)
            }),
            manageUrl = Url.Action(nameof(Index), new { formKey = form.Key })
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(16_000_000)]
    public async Task<IActionResult> Save([FromBody] FormDraftSaveRequest? request)
    {
        var form = FormDraftForms.Find(request?.FormKey);
        if (CurrentUserId is not { } userId || request is null || !CanUse(form))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "forbidden" });
        }

        var result = await _drafts.SaveAsync(userId, request, HttpContext.RequestAborted);
        var body = new
        {
            status = result.Outcome.ToString().ToLowerInvariant(),
            revision = result.Revision,
            savedAtUtc = result.SavedAtUtc is { } saved ? DateTime.SpecifyKind(saved, DateTimeKind.Utc) : (DateTime?)null,
            message = result.Message
        };
        return result.Outcome switch
        {
            FormDraftSaveOutcome.Saved => Json(body),
            FormDraftSaveOutcome.Invalid => BadRequest(body),
            _ => Conflict(body)
        };
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid key, string? formKey = null)
    {
        if (CurrentUserId is not { } userId)
        {
            return Challenge();
        }

        var deleted = await _drafts.DeleteAsync(userId, key, HttpContext.RequestAborted);
        if (Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { deleted });
        }

        TempData[deleted ? "ok" : "err"] = deleted
            ? "پیش‌نویس حذف شد."
            : "پیش‌نویس پیدا نشد یا همین حالا در حال ثبت نهایی است.";
        return RedirectToAction(nameof(Index), new { formKey = FormDraftForms.Find(formKey)?.Key });
    }

    private int? CurrentUserId
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>همان دسترسی خودِ فرم: بخش ناوبری کنترلر فرم و صلاحیت ثبت داده.</summary>
    private bool CanUse(FormDraftForm? form)
        => form is not null
            && RoleAccessRules.CanManageData(User)
            && RoleAccessRules.CanAccessController(User, form.Controller, _clientModules);
}

public sealed record FormDraftIndexViewModel(FormDraftForm? Form, IReadOnlyList<FormDraftListItem> Items)
{
    public int OpenCount => Items.Count(item => item.Status != FormDraftStatus.Submitted);
}
