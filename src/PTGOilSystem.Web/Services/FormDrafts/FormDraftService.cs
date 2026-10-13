using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.FormDrafts;

public sealed class FormDraftSaveRequest
{
    public Guid Key { get; set; }
    public string? FormKey { get; set; }
    public string? ResumePath { get; set; }
    public string? Summary { get; set; }
    public int? RowCount { get; set; }

    /// <summary>Revision که این پنجره آخرین بار ذخیره/بارگذاری کرده است؛ 0 یعنی پیش‌نویس تازه.</summary>
    public int BaseRevision { get; set; }

    /// <summary>جفت‌های [نام، مقدار] فیلدهای فرم، دقیقاً مثل بدنهٔ POST.</summary>
    public List<string[]>? Fields { get; set; }
}

public enum FormDraftSaveOutcome
{
    Saved,
    /// <summary>نسخهٔ سرور از نسخهٔ پایهٔ این پنجره تازه‌تر است؛ کلاینت باید جداگانه (کلید تازه) ذخیره کند.</summary>
    Conflict,
    /// <summary>پیش‌نویس قبلاً ثبت نهایی شده است.</summary>
    Submitted,
    /// <summary>همین حالا در حال ثبت نهایی است.</summary>
    Busy,
    Invalid
}

public sealed record FormDraftSaveResult(FormDraftSaveOutcome Outcome, int Revision, DateTime? SavedAtUtc, string? Message = null);

public sealed record FormDraftListItem(
    Guid Key,
    string FormKey,
    string FormTitle,
    string? Summary,
    int RowCount,
    FormDraftStatus Status,
    DateTime CreatedAtUtc,
    DateTime LastSavedAtUtc,
    DateTime? SubmittedAtUtc,
    string ResumeUrl);

/// <summary>پیش‌نویسِ قابل ادامه که برای بازیابی فرم خوانده شده است.</summary>
public sealed class FormDraftSnapshot
{
    public FormDraftSnapshot(Guid key, int revision, IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        Key = key;
        Revision = revision;
        Fields = fields;
    }

    public Guid Key { get; }
    public int Revision { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

    /// <summary>همان Value Provider فرم POST؛ Model Binding پیش‌نویس دقیقاً مثل ثبت فرم انجام می‌شود.</summary>
    public IValueProvider ToValueProvider()
    {
        var values = Fields
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new StringValues(group.Select(pair => pair.Value).ToArray()),
                StringComparer.OrdinalIgnoreCase);
        return new FormValueProvider(BindingSource.Form, new FormCollection(values), CultureInfo.CurrentCulture);
    }

    public string? Value(string name)
        => Fields.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public interface IFormDraftService
{
    Task<FormDraftSaveResult> SaveAsync(int userId, FormDraftSaveRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FormDraftListItem>> ListAsync(int userId, string? formKey, bool includeSubmitted, CancellationToken cancellationToken = default);
    Task<FormDraftSnapshot?> FindOpenAsync(int userId, Guid key, string formKey, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int userId, Guid key, CancellationToken cancellationToken = default);
}

public sealed class FormDraftService : IFormDraftService
{
    /// <summary>بیشترین حجم متن فیلدها (حدود ۸ مگابایت)؛ امپورت چندهزار سطری بارگیری در همین اندازه جا می‌شود.</summary>
    public const int MaxPayloadChars = 8_000_000;
    public const int MaxFields = 40_000;
    public const int MaxOpenDraftsPerUser = 200;

    /// <summary>قفل ثبت نهایی که بیش از این مانده باشد رهاشده حساب می‌شود (سرور وسط ثبت متوقف شده).</summary>
    public static readonly TimeSpan SubmitLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>این فیلدها هرگز در پیش‌نویس نگه داشته نمی‌شوند (توکن‌های یک‌بارمصرف و فیلدهای خودِ پیش‌نویس).</summary>
    public static readonly IReadOnlySet<string> ExcludedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "__RequestVerificationToken",
        "__FormToken",
        FormDraftFields.Key,
        FormDraftFields.Form
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _db;

    public FormDraftService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<FormDraftSaveResult> SaveAsync(int userId, FormDraftSaveRequest request, CancellationToken cancellationToken = default)
    {
        var form = FormDraftForms.Find(request.FormKey);
        if (form is null || request.Key == Guid.Empty || request.BaseRevision < 0)
        {
            return Invalid("فرم پیش‌نویس معتبر نیست.");
        }

        var fields = NormalizeFields(request.Fields);
        if (fields is null)
        {
            return Invalid("اطلاعات پیش‌نویس معتبر نیست.");
        }

        var payload = JsonSerializer.Serialize(fields.Select(pair => new[] { pair.Key, pair.Value }), JsonOptions);
        if (payload.Length > MaxPayloadChars)
        {
            return Invalid("حجم پیش‌نویس بیش از حد مجاز است.");
        }

        var resumePath = NormalizeResumePath(form, request.ResumePath);
        var summary = Truncate(request.Summary, 300);
        var rowCount = Math.Clamp(request.RowCount ?? CountRows(fields), 0, 1_000_000);
        var now = DateTime.UtcNow;

        var existing = await _db.FormDrafts
            .AsNoTracking()
            .Where(d => d.DraftKey == request.Key)
            .Select(d => new { d.Id, d.UserId, d.FormKey, d.Status, d.Revision, d.SubmitLockedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            if (request.BaseRevision != 0)
            {
                // پنجره نسخه‌ای را ویرایش می‌کرد که دیگر وجود ندارد (کاربر آن را حذف کرده است).
                return new FormDraftSaveResult(FormDraftSaveOutcome.Conflict, 0, null, "این پیش‌نویس حذف شده است.");
            }

            var openCount = await _db.FormDrafts.CountAsync(
                d => d.UserId == userId && d.Status != FormDraftStatus.Submitted,
                cancellationToken);
            if (openCount >= MaxOpenDraftsPerUser)
            {
                return Invalid("تعداد پیش‌نویس‌های باز زیاد است؛ پیش‌نویس‌های قدیمی را از فهرست پیش‌نویس‌ها حذف کنید.");
            }

            _db.FormDrafts.Add(new FormDraft
            {
                DraftKey = request.Key,
                UserId = userId,
                FormKey = form.Key,
                ResumePath = resumePath,
                Summary = summary,
                PayloadJson = payload,
                RowCount = rowCount,
                Revision = 1,
                Status = FormDraftStatus.Open,
                LastSavedAtUtc = now,
                CreatedAtUtc = now,
                CreatedByUserId = userId
            });

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // درج هم‌زمان همان کلید از پنجرهٔ دیگر (unique index)؛ این پنجره جداگانه ذخیره می‌کند.
                _db.ChangeTracker.Clear();
                return new FormDraftSaveResult(FormDraftSaveOutcome.Conflict, 0, null);
            }

            return new FormDraftSaveResult(FormDraftSaveOutcome.Saved, 1, now);
        }

        if (existing.UserId != userId || !string.Equals(existing.FormKey, form.Key, StringComparison.Ordinal))
        {
            // کلید مال کاربر یا فرم دیگری است؛ چیزی افشا نمی‌شود و کلاینت با کلید تازه ذخیره می‌کند.
            return new FormDraftSaveResult(FormDraftSaveOutcome.Conflict, 0, null);
        }

        if (existing.Status == FormDraftStatus.Submitted)
        {
            return new FormDraftSaveResult(FormDraftSaveOutcome.Submitted, existing.Revision, null, "این پیش‌نویس قبلاً ثبت نهایی شده است.");
        }

        var staleLockBefore = now - SubmitLockTimeout;
        if (existing.Status == FormDraftStatus.Submitting
            && existing.SubmitLockedAtUtc.HasValue
            && existing.SubmitLockedAtUtc.Value >= staleLockBefore)
        {
            return new FormDraftSaveResult(FormDraftSaveOutcome.Busy, existing.Revision, null, "این پیش‌نویس در حال ثبت نهایی است.");
        }

        if (existing.Revision != request.BaseRevision)
        {
            // همان محتوا دوباره رسیده است (ارسال keepalive هنگام بستن صفحه و سپس همگام‌سازی نسخهٔ محلی):
            // تعارض واقعی نیست و پیش‌نویس جدا ساخته نمی‌شود.
            var storedPayload = await _db.FormDrafts.AsNoTracking()
                .Where(d => d.Id == existing.Id)
                .Select(d => d.PayloadJson)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.Equals(storedPayload, payload, StringComparison.Ordinal))
            {
                return new FormDraftSaveResult(FormDraftSaveOutcome.Saved, existing.Revision, null);
            }

            return new FormDraftSaveResult(FormDraftSaveOutcome.Conflict, existing.Revision, null,
                "این پیش‌نویس در پنجرهٔ دیگری تغییر کرده است.");
        }

        var nextRevision = existing.Revision + 1;
        var baseRevision = request.BaseRevision;
        // شرط Revision در خودِ UPDATE است؛ دو ذخیرهٔ هم‌زمان هرگز هر دو پذیرفته نمی‌شوند.
        var updated = await _db.FormDrafts
            .Where(d => d.Id == existing.Id
                && d.Revision == baseRevision
                && (d.Status == FormDraftStatus.Open
                    || (d.Status == FormDraftStatus.Submitting && d.SubmitLockedAtUtc < staleLockBefore)))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.PayloadJson, payload)
                    .SetProperty(d => d.ResumePath, resumePath)
                    .SetProperty(d => d.Summary, summary)
                    .SetProperty(d => d.RowCount, rowCount)
                    .SetProperty(d => d.Revision, nextRevision)
                    .SetProperty(d => d.Status, FormDraftStatus.Open)
                    .SetProperty(d => d.SubmitLockedAtUtc, (DateTime?)null)
                    .SetProperty(d => d.LastSavedAtUtc, now)
                    .SetProperty(d => d.UpdatedAtUtc, now)
                    .SetProperty(d => d.UpdatedByUserId, userId),
                cancellationToken);

        return updated == 1
            ? new FormDraftSaveResult(FormDraftSaveOutcome.Saved, nextRevision, now)
            : new FormDraftSaveResult(FormDraftSaveOutcome.Conflict, existing.Revision, null,
                "این پیش‌نویس در پنجرهٔ دیگری تغییر کرده است.");
    }

    public async Task<IReadOnlyList<FormDraftListItem>> ListAsync(
        int userId,
        string? formKey,
        bool includeSubmitted,
        CancellationToken cancellationToken = default)
    {
        var query = _db.FormDrafts.AsNoTracking().Where(d => d.UserId == userId);
        if (!string.IsNullOrWhiteSpace(formKey))
        {
            query = query.Where(d => d.FormKey == formKey);
        }

        if (!includeSubmitted)
        {
            query = query.Where(d => d.Status != FormDraftStatus.Submitted);
        }

        var rows = await query
            .OrderByDescending(d => d.LastSavedAtUtc)
            .Select(d => new
            {
                d.DraftKey,
                d.FormKey,
                d.Summary,
                d.RowCount,
                d.Status,
                d.CreatedAtUtc,
                d.LastSavedAtUtc,
                d.SubmittedAtUtc,
                d.ResumePath
            })
            .Take(500)
            .ToListAsync(cancellationToken);

        return rows
            .Select(d =>
            {
                var form = FormDraftForms.Find(d.FormKey);
                return new FormDraftListItem(
                    d.DraftKey,
                    d.FormKey,
                    form?.Title ?? d.FormKey,
                    d.Summary,
                    d.RowCount,
                    d.Status,
                    d.CreatedAtUtc,
                    d.LastSavedAtUtc,
                    d.SubmittedAtUtc,
                    BuildResumeUrl(d.ResumePath, d.DraftKey));
            })
            .ToList();
    }

    public async Task<FormDraftSnapshot?> FindOpenAsync(int userId, Guid key, string formKey, CancellationToken cancellationToken = default)
    {
        if (key == Guid.Empty)
        {
            return null;
        }

        var draft = await _db.FormDrafts
            .AsNoTracking()
            .Where(d => d.DraftKey == key && d.UserId == userId && d.FormKey == formKey && d.Status != FormDraftStatus.Submitted)
            .Select(d => new { d.Revision, d.PayloadJson })
            .FirstOrDefaultAsync(cancellationToken);
        if (draft is null)
        {
            return null;
        }

        return new FormDraftSnapshot(key, draft.Revision, ParsePayload(draft.PayloadJson));
    }

    public async Task<bool> DeleteAsync(int userId, Guid key, CancellationToken cancellationToken = default)
    {
        var staleLockBefore = DateTime.UtcNow - SubmitLockTimeout;
        // پیش‌نویسی که همین حالا در حال ثبت نهایی است حذف نمی‌شود؛ وضعیت آن پس از ثبت روشن می‌شود.
        var deleted = await _db.FormDrafts
            .Where(d => d.DraftKey == key
                && d.UserId == userId
                && (d.Status != FormDraftStatus.Submitting || d.SubmitLockedAtUtc < staleLockBefore))
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public static string BuildResumeUrl(string resumePath, Guid key)
    {
        var separator = resumePath.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{resumePath}{separator}{FormDraftFields.QueryKey}={key:D}";
    }

    /// <summary>جفت‌های فیلد را پاک‌سازی می‌کند؛ null یعنی ساختار نامعتبر.</summary>
    public static List<KeyValuePair<string, string>>? NormalizeFields(IEnumerable<string[]>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var result = new List<KeyValuePair<string, string>>();
        foreach (var pair in raw)
        {
            if (pair is null || pair.Length != 2 || string.IsNullOrEmpty(pair[0]) || pair[0].Length > 300)
            {
                return null;
            }

            if (ExcludedFields.Contains(pair[0]))
            {
                continue;
            }

            result.Add(new KeyValuePair<string, string>(pair[0], pair[1] ?? string.Empty));
            if (result.Count > MaxFields)
            {
                return null;
            }
        }

        return result;
    }

    /// <summary>فیلدهای یک POST واقعی فرم (برای به‌روزکردن پیش‌نویس وقتی ثبت نهایی انجام نشد).</summary>
    public static List<KeyValuePair<string, string>> FieldsFromForm(IFormCollection form)
        => form
            .Where(entry => !ExcludedFields.Contains(entry.Key))
            .SelectMany(entry => entry.Value.Select(value => new KeyValuePair<string, string>(entry.Key, value ?? string.Empty)))
            .Take(MaxFields)
            .ToList();

    public static string SerializeFields(IEnumerable<KeyValuePair<string, string>> fields)
        => JsonSerializer.Serialize(fields.Select(pair => new[] { pair.Key, pair.Value }), JsonOptions);

    public static IReadOnlyList<KeyValuePair<string, string>> ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        try
        {
            return NormalizeFields(JsonSerializer.Deserialize<List<string[]>>(payloadJson, JsonOptions)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>تعداد سطرهای جدول: بزرگ‌ترین گروه فیلدهای <c>X.Index</c> (قرارداد Model Binding سطرها).</summary>
    public static int CountRows(IEnumerable<KeyValuePair<string, string>> fields)
        => fields
            .Where(pair => pair.Key.EndsWith(".Index", StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();

    public static string NormalizeResumePath(FormDraftForm form, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)
            || !requested.StartsWith('/')
            || requested.StartsWith("//", StringComparison.Ordinal)
            || requested.Contains('\\', StringComparison.Ordinal)
            || requested.Length > 1000)
        {
            return form.Path;
        }

        var queryIndex = requested.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex < 0 ? requested : requested[..queryIndex];
        if (!string.Equals(path.TrimEnd('/'), form.Path, StringComparison.OrdinalIgnoreCase))
        {
            return form.Path;
        }

        if (queryIndex < 0)
        {
            return form.Path;
        }

        // کلید پیش‌نویس قبلی از آدرس برداشته می‌شود؛ بقیهٔ پارامترهای GET فرم (قرارداد، returnUrl…) می‌مانند.
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(requested[queryIndex..]);
        var kept = query
            .Where(entry => !string.Equals(entry.Key, FormDraftFields.QueryKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => entry.Value.Select(value => new KeyValuePair<string, string?>(entry.Key, value)))
            .ToList();
        var rebuilt = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(form.Path, kept);
        return rebuilt.Length <= 1000 ? rebuilt : form.Path;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static FormDraftSaveResult Invalid(string message)
        => new(FormDraftSaveOutcome.Invalid, 0, null, message);
}

/// <summary>نام فیلدها و پارامترهای مشترک پیش‌نویس بین Razor، فیلتر و JavaScript.</summary>
public static class FormDraftFields
{
    public const string Key = "__DraftKey";
    public const string Form = "__DraftForm";
    public const string QueryKey = "draft";
}
