using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.FormDrafts;

/// <summary>
/// ثبت نهاییِ فرمی که پیش‌نویس دارد (فیلد <c>__DraftKey</c> در POST).
///
/// منطق ثبت دست نمی‌خورد: اکشن اصلی با همهٔ اعتبارسنجی‌ها، تراکنش، موجودی و حسابداریِ خودش اجرا می‌شود.
/// این فیلتر فقط:
/// <list type="bullet">
///   <item>پیش‌از اجرا: پیش‌نویسِ «ثبت‌شده» را دوباره ثبت نمی‌کند و با قفل شرطی (UPDATE … WHERE) جلوی ثبت هم‌زمان
///   همان پیش‌نویس از دو پنجره را می‌گیرد.</item>
///   <item>پس از اجرا: اگر توکن فرم در <c>ProcessedFormTokens</c> نشسته باشد (یعنی سند در همان تراکنش Commit شد)
///   پیش‌نویس «ثبت‌شده» می‌شود؛ وگرنه قفل آزاد و محتوای ارسال‌شده در پیش‌نویس ذخیره می‌شود تا چیزی از دست نرود.</item>
/// </list>
/// </summary>
public sealed class FormDraftSubmitFilter : IAsyncActionFilter
{
    private readonly ApplicationDbContext _db;
    private readonly ITempDataDictionaryFactory _tempDataFactory;
    private readonly ILogger<FormDraftSubmitFilter> _logger;

    public FormDraftSubmitFilter(
        ApplicationDbContext db,
        ITempDataDictionaryFactory tempDataFactory,
        ILogger<FormDraftSubmitFilter> logger)
    {
        _db = db;
        _tempDataFactory = tempDataFactory;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var request = http.Request;
        if (!HttpMethods.IsPost(request.Method) || !request.HasFormContentType)
        {
            await next();
            return;
        }

        var form = await request.ReadFormAsync(http.RequestAborted);
        var userId = int.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
            ? parsedUserId
            : (int?)null;
        var formDefinition = FormDraftForms.Find(form[FormDraftFields.Form].ToString());
        if (userId is null
            || formDefinition is null
            || !Guid.TryParse(form[FormDraftFields.Key].ToString(), out var draftKey)
            || draftKey == Guid.Empty)
        {
            await next();
            return;
        }

        // دقت میکروثانیه (مثل PostgreSQL) تا مقایسهٔ قفل پس از رفت‌وبرگشت دیتابیس دقیق بماند.
        var now = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
        var draft = await _db.FormDrafts
            .AsNoTracking()
            .Where(d => d.DraftKey == draftKey)
            .Select(d => new { d.Id, d.UserId, d.FormKey, d.Status })
            .FirstOrDefaultAsync(http.RequestAborted);

        if (draft is not null && (draft.UserId != userId.Value || draft.FormKey != formDefinition.Key))
        {
            // کلید مال کس دیگری است: به پیش‌نویس او دست نمی‌زنیم و ثبت عادی ادامه می‌یابد.
            await next();
            return;
        }

        if (draft is not null)
        {
            if (draft.Status == FormDraftStatus.Submitted)
            {
                Block(context, "این پیش‌نویس قبلاً ثبت نهایی شده است و دوباره ثبت نشد.");
                return;
            }

            var staleLockBefore = now - FormDraftService.SubmitLockTimeout;
            var locked = await _db.FormDrafts
                .Where(d => d.Id == draft.Id
                    && (d.Status == FormDraftStatus.Open
                        || (d.Status == FormDraftStatus.Submitting && d.SubmitLockedAtUtc < staleLockBefore)))
                .ExecuteUpdateAsync(setters => setters
                        .SetProperty(d => d.Status, FormDraftStatus.Submitting)
                        .SetProperty(d => d.SubmitLockedAtUtc, (DateTime?)now),
                    http.RequestAborted);
            if (locked == 0)
            {
                var current = await _db.FormDrafts.AsNoTracking()
                    .Where(d => d.Id == draft.Id)
                    .Select(d => (FormDraftStatus?)d.Status)
                    .FirstOrDefaultAsync(http.RequestAborted);
                Block(context, current == FormDraftStatus.Submitted
                    ? "این پیش‌نویس قبلاً ثبت نهایی شده است و دوباره ثبت نشد."
                    : "این پیش‌نویس همین حالا در پنجرهٔ دیگری در حال ثبت است؛ پس از پایان آن، فهرست را بررسی کنید.");
                return;
            }
        }

        var executed = await next();

        try
        {
            await CompleteAsync(executed, form, userId.Value, formDefinition, draftKey, draft?.Id, now);
        }
        catch (Exception ex)
        {
            // نتیجهٔ خودِ ثبت نباید به‌خاطر دفترداری پیش‌نویس تغییر کند.
            _logger.LogWarning(ex, "Form draft bookkeeping failed for draft {DraftKey}.", draftKey);
        }
    }

    private async Task CompleteAsync(
        ActionExecutedContext executed,
        IFormCollection form,
        int userId,
        FormDraftForm formDefinition,
        Guid draftKey,
        int? draftId,
        DateTime lockedAt)
    {
        var formToken = form[FormTokenHtmlHelper.FieldName].ToString().Trim();
        var submitted = formToken.Length > 0
            && await _db.ProcessedFormTokens.AsNoTracking().AnyAsync(t => t.Token == formToken);
        var now = DateTime.UtcNow;

        if (submitted)
        {
            if (draftId.HasValue)
            {
                await _db.FormDrafts
                    .Where(d => d.Id == draftId.Value)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(d => d.Status, FormDraftStatus.Submitted)
                        .SetProperty(d => d.SubmittedAtUtc, (DateTime?)now)
                        .SetProperty(d => d.SubmittedFormToken, formToken)
                        .SetProperty(d => d.SubmitLockedAtUtc, (DateTime?)null)
                        .SetProperty(d => d.UpdatedAtUtc, (DateTime?)now)
                        .SetProperty(d => d.UpdatedByUserId, (int?)userId));
            }
            else
            {
                // پیش‌نویس هنوز به سرور نرسیده بود (ثبت پیش از نخستین ذخیرهٔ خودکار یا نسخهٔ محلی آفلاین).
                // ردیف «ثبت‌شده» نگه داشته می‌شود تا همان کلید بعداً دوباره پیش‌نویس باز نسازد.
                await InsertAsync(new FormDraft
                {
                    DraftKey = draftKey,
                    UserId = userId,
                    FormKey = formDefinition.Key,
                    ResumePath = formDefinition.Path,
                    PayloadJson = "[]",
                    Revision = 1,
                    Status = FormDraftStatus.Submitted,
                    SubmittedAtUtc = now,
                    SubmittedFormToken = formToken,
                    LastSavedAtUtc = now,
                    CreatedAtUtc = now,
                    CreatedByUserId = userId
                });
            }

            return;
        }

        // ثبت انجام نشد (اعتبارسنجی، قاعدهٔ تجاری، خطا): قفل آزاد و همان چیزی که کاربر فرستاد ذخیره می‌شود.
        var fields = FormDraftService.FieldsFromForm(form);
        var payload = FormDraftService.SerializeFields(fields);
        if (payload.Length > FormDraftService.MaxPayloadChars)
        {
            payload = null;
        }

        int revision;
        if (draftId.HasValue)
        {
            if (payload is null)
            {
                await _db.FormDrafts
                    .Where(d => d.Id == draftId.Value && d.Status == FormDraftStatus.Submitting && d.SubmitLockedAtUtc == lockedAt)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(d => d.Status, FormDraftStatus.Open)
                        .SetProperty(d => d.SubmitLockedAtUtc, (DateTime?)null));
            }
            else
            {
                var rowCount = FormDraftService.CountRows(fields);
                await _db.FormDrafts
                    .Where(d => d.Id == draftId.Value && d.Status == FormDraftStatus.Submitting && d.SubmitLockedAtUtc == lockedAt)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(d => d.Status, FormDraftStatus.Open)
                        .SetProperty(d => d.SubmitLockedAtUtc, (DateTime?)null)
                        .SetProperty(d => d.PayloadJson, payload)
                        .SetProperty(d => d.RowCount, d => rowCount > 0 ? rowCount : d.RowCount)
                        .SetProperty(d => d.Revision, d => d.Revision + 1)
                        .SetProperty(d => d.LastSavedAtUtc, now)
                        .SetProperty(d => d.UpdatedAtUtc, (DateTime?)now)
                        .SetProperty(d => d.UpdatedByUserId, (int?)userId));
            }

            revision = await _db.FormDrafts.AsNoTracking()
                .Where(d => d.Id == draftId.Value)
                .Select(d => d.Revision)
                .FirstOrDefaultAsync();
        }
        else if (payload is not null)
        {
            revision = 1;
            await InsertAsync(new FormDraft
            {
                DraftKey = draftKey,
                UserId = userId,
                FormKey = formDefinition.Key,
                ResumePath = formDefinition.Path,
                PayloadJson = payload,
                RowCount = FormDraftService.CountRows(fields),
                Revision = revision,
                Status = FormDraftStatus.Open,
                LastSavedAtUtc = now,
                CreatedAtUtc = now,
                CreatedByUserId = userId
            });
        }
        else
        {
            return;
        }

        // فرم با خطا دوباره نمایش داده می‌شود: همان پیش‌نویس و Revision تازه به صفحه برمی‌گردد. Items برای
        // Viewی است که فیلتر استثنا (پس از این فیلتر) می‌سازد.
        var state = new FormDraftViewState(draftKey, revision);
        executed.HttpContext.Items[FormDraftViewState.ViewDataKey] = state;
        if (executed.Result is ViewResult view)
        {
            view.ViewData[FormDraftViewState.ViewDataKey] = state;
        }
    }

    private async Task InsertAsync(FormDraft draft)
    {
        // DbContext اکشن ممکن است موجودیت‌های ناموفق در Change Tracker داشته باشد؛ درج با Context جدا نیست،
        // پس فقط همین ردیف ذخیره می‌شود: بقیه از ردیابی جدا می‌شوند.
        _db.ChangeTracker.Clear();
        _db.FormDrafts.Add(draft);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // همان کلید هم‌زمان درج شده است؛ چیزی برای انجام نیست.
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    private void Block(ActionExecutingContext context, string message)
    {
        var tempData = _tempDataFactory.GetTempData(context.HttpContext);
        tempData["err"] = message;
        context.Result = new RedirectToActionResult("Index", "FormDrafts", null);
    }
}

/// <summary>پیش‌نویسی که View فعلی باید ادامه دهد (بازیابی یا نمایش دوباره پس از ثبت ناموفق).</summary>
public sealed record FormDraftViewState(Guid Key, int Revision)
{
    public const string ViewDataKey = "__PtgFormDraft";
}
