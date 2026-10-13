using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// پیش‌نویسِ خودکارِ یک فرم ثبت (مثلاً بارگیری) که کاربر هنوز ثبت نهایی نکرده است.
///
/// این جدول هیچ اثر تجاری ندارد: فقط همان فیلدهای فرم (نام/مقدار، دقیقاً مثل بدنهٔ POST) را نگه می‌دارد.
/// ثبت نهایی همیشه از مسیر عادیِ همان فرم و با همهٔ اعتبارسنجی‌ها، تراکنش، موجودی و حسابداریِ خودِ آن انجام
/// می‌شود؛ پیش‌نویس فقط پس از ثبت موفق (توکن فرم در <c>ProcessedFormTokens</c>) «ثبت‌شده» علامت می‌خورد.
///
/// هر پیش‌نویس مال یک کاربر است و فقط همان کاربر آن را می‌بیند. <see cref="DraftKey"/> را مرورگر می‌سازد تا
/// چند پیش‌نویس مستقل (چند پنجره) هرگز روی هم نوشته نشوند؛ <see cref="Revision"/> جلوی بازنویسی نسخهٔ
/// تازه‌تر با نسخهٔ کهنه را می‌گیرد.
/// </summary>
public class FormDraft : BaseEntity
{
    public Guid DraftKey { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>کلید فرم ثبت‌شده در فهرست فرم‌های دارای پیش‌نویس، مثلاً <c>Loading.Create</c>.</summary>
    [Required, MaxLength(64)] public string FormKey { get; set; } = "";

    /// <summary>آدرس GET همان فرم (مسیر محلی + query) برای ادامهٔ پیش‌نویس.</summary>
    [Required, MaxLength(1000)] public string ResumePath { get; set; } = "";

    /// <summary>خلاصهٔ کوتاه برای فهرست پیش‌نویس‌ها (مثلاً قرارداد و تعداد سطر).</summary>
    [MaxLength(300)] public string? Summary { get; set; }

    /// <summary>فیلدهای فرم به‌صورت آرایهٔ JSON از جفت‌های [نام، مقدار].</summary>
    [Required] public string PayloadJson { get; set; } = "[]";

    public int RowCount { get; set; }

    /// <summary>با هر ذخیره یکی بالا می‌رود؛ ذخیره فقط با Revision پایهٔ برابر پذیرفته می‌شود.</summary>
    public int Revision { get; set; }

    public FormDraftStatus Status { get; set; } = FormDraftStatus.Open;

    public DateTime LastSavedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>زمان قفل کوتاه‌مدت ثبت نهایی (جلوی ثبت هم‌زمان یک پیش‌نویس از دو پنجره).</summary>
    public DateTime? SubmitLockedAtUtc { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    [MaxLength(64)] public string? SubmittedFormToken { get; set; }
}

public enum FormDraftStatus
{
    /// <summary>قابل ادامه و ویرایش.</summary>
    Open = 1,

    /// <summary>درخواست ثبت نهایی همین حالا در جریان است.</summary>
    Submitting = 2,

    /// <summary>ثبت نهایی موفق شد؛ دیگر قابل ادامه یا ثبت دوباره نیست.</summary>
    Submitted = 3
}
