using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// مشعل آفلاین — یک عملیاتِ ثبت‌شده روی دستگاه در زمان قطع اینترنت که پس از اتصال به سرور رسیده است.
///
/// این جدول خودش دفتر مالی نیست. پس از دریافت، <c>OfflineOperationApplier</c> همان اکشن ثبتِ موجودِ
/// ماژول (فروش، روزنامچه، مصارف، بارگیری، رسید، مشتری) را به نام همین کاربر و با همهٔ فیلترها،
/// اعتبارسنجی‌ها و تراکنش خودِ آن اکشن اجرا می‌کند؛ سند واقعی همان‌جا ساخته می‌شود و این ردیف فقط
/// نتیجه (اعمال‌شده با شمارهٔ سند، یا تعارض با دلیل) را نگه می‌دارد.
///
/// <see cref="ClientOperationId"/> یکتا است؛ ارسال دوبارهٔ همان عملیات (قطع شبکه وسط ارسال، بازیابی
/// بکاپ) ردیف تازه نمی‌سازد. سند واقعی هم با توکن ثابت <c>offline:{ClientOperationId}</c> در جدول
/// موجود <c>ProcessedFormTokens</c> فقط یک‌بار ثبت می‌شود.
/// </summary>
public class OfflineSyncOperation : BaseEntity
{
    public Guid ClientOperationId { get; set; }
    public Guid DeviceId { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int? CompanyId { get; set; }

    [Required, MaxLength(64)] public string OperationType { get; set; } = "";
    [MaxLength(64)] public string? EntityId { get; set; }
    [MaxLength(64)] public string? BaseVersion { get; set; }

    /// <summary>متن JSON دقیقاً همان‌طور که کلاینت فرستاده؛ هش روی همین متن محاسبه می‌شود.</summary>
    [Required] public string PayloadJson { get; set; } = "{}";

    /// <summary>SHA-256 هگزِ کوچکِ <see cref="PayloadJson"/> (۶۴ کاراکتر).</summary>
    [Required, MaxLength(64)] public string PayloadHash { get; set; } = "";

    public DateTime ClientCreatedAtUtc { get; set; }
    public DateTime? BusinessDate { get; set; }
    public int SchemaVersion { get; set; } = 1;

    /// <summary>نسخهٔ اصلاح: عملیاتِ دارای تعارض با همان شناسه و Revision بزرگ‌تر دوباره فرستاده می‌شود.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>تعداد تلاش ثبت قطعی در سرور.</summary>
    public int ApplyAttempts { get; set; }
    public DateTime? LastApplyAttemptAtUtc { get; set; }

    /// <summary>شناسه‌های ClientOperationIdِ عملیات‌های پیش‌نیاز، با کاما.</summary>
    [MaxLength(2000)] public string? DependenciesCsv { get; set; }

    public OfflineSyncStatus Status { get; set; } = OfflineSyncStatus.Received;

    /// <summary>دلیل تعارض یا رد؛ متن کوتاه فارسی که به کاربر دستگاه هم نشان داده می‌شود.</summary>
    [MaxLength(1000)] public string? StatusNote { get; set; }

    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }

    /// <summary>سند واقعی که با فرم‌های موجود برای این عملیات ساخته شد (مثلاً Sale / 125).</summary>
    [MaxLength(64)] public string? AppliedReferenceType { get; set; }
    [MaxLength(64)] public string? AppliedReferenceId { get; set; }

    [MaxLength(64)] public string? ReceivedFromIp { get; set; }
}

/// <summary>دستگاهِ فعال‌شده برای کار آفلاین. دستگاهِ باطل‌شده دیگر نمی‌تواند عملیات بفرستد.</summary>
public class OfflineSyncDevice : BaseEntity
{
    public int UserId { get; set; }
    public User? User { get; set; }

    public Guid DeviceId { get; set; }
    [MaxLength(100)] public string? DeviceName { get; set; }

    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public int? RevokedByUserId { get; set; }
}

public enum OfflineSyncStatus
{
    /// <summary>به سرور رسیده و ذخیره شده؛ در انتظار ثبت قطعی (یا نوعی که ثبت خودکار ندارد).</summary>
    Received = 1,

    /// <summary>با اکشن ثبتِ موجودِ ماژول ثبت قطعی شد (همگام‌شده).</summary>
    Applied = 2,

    /// <summary>خطای تجاری یا تعارض (موجودی، دورهٔ بسته، اعتبارسنجی...)؛ کاربر اصلاح و دوباره ارسال می‌کند.</summary>
    Conflict = 3,

    /// <summary>رد شد؛ هیچ اثری در سیستم ندارد.</summary>
    Rejected = 4,

    /// <summary>در حال ثبت قطعی در سرور (قفل کوتاه‌مدت تا دو درخواست هم‌زمان یک عملیات را دوبار اجرا نکنند).</summary>
    Applying = 5
}
