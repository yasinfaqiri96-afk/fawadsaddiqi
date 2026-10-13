using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.OfflineSync;

// قرارداد JSON (camelCase) میان مشعل آفلاین (PWA) و /api/sync/v1. زمان‌ها UTC ISO-8601 هستند.

public sealed class OfflineDeviceRegistrationRequest
{
    [Required] public Guid DeviceId { get; set; }
    [MaxLength(100)] public string? DeviceName { get; set; }
}

public sealed class OfflinePushRequest
{
    [Required] public Guid DeviceId { get; set; }

    [Required, MinLength(1), MaxLength(OfflineSyncLimits.MaxBatchSize)]
    public List<OfflineOperationDto> Operations { get; set; } = [];
}

public sealed class OfflineOperationDto
{
    public Guid ClientOperationId { get; set; }
    public int UserId { get; set; }
    public int? CompanyId { get; set; }
    [MaxLength(64)] public string OperationType { get; set; } = "";
    [MaxLength(64)] public string? EntityId { get; set; }
    [MaxLength(64)] public string? BaseVersion { get; set; }
    public string PayloadJson { get; set; } = "";
    [MaxLength(64)] public string PayloadHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>تاریخ کاری میلادی <c>yyyy-MM-dd</c>.</summary>
    [MaxLength(10)] public string? BusinessDate { get; set; }

    public int SchemaVersion { get; set; } = 1;
    public List<Guid>? Dependencies { get; set; }

    /// <summary>نسخهٔ اصلاح؛ برای ارسال دوبارهٔ عملیاتِ دارای تعارض با همان شناسه بزرگ‌تر می‌شود.</summary>
    public int Revision { get; set; } = 1;
}

public sealed class OfflinePushResponse
{
    public DateTime ServerTimeUtc { get; set; }
    public List<OfflineOperationResult> Results { get; set; } = [];
}

/// <summary>
/// نتیجهٔ هر عملیات. <c>outcome</c>: <c>stored</c> (تازه ذخیره شد)، <c>duplicate</c> (قبلاً رسیده؛
/// همان وضعیت)، <c>rejected</c> (ذخیره نشد؛ تکرار بی‌فایده است) یا <c>retry</c> (فعلاً نه؛ بعداً
/// با همان شناسه دوباره بفرست). <c>status</c> وضعیت نهایی پس از تلاش ثبت قطعی است:
/// <c>Applied</c> (همگام‌شده)، <c>Conflict</c> (با دلیل؛ قابل اصلاح)، <c>Received</c>/<c>Applying</c> (هنوز در جریان).
/// </summary>
public sealed class OfflineOperationResult
{
    public Guid ClientOperationId { get; set; }
    public string Outcome { get; set; } = "";
    public string? Status { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public int? Revision { get; set; }
    public string? AppliedReferenceType { get; set; }
    public string? AppliedReferenceId { get; set; }
}

public sealed class OfflineStatusRequest
{
    [Required, MinLength(1), MaxLength(OfflineSyncLimits.MaxStatusQuery)]
    public List<Guid> Ids { get; set; } = [];
}

public sealed class OfflineStatusResponse
{
    public DateTime ServerTimeUtc { get; set; }
    public List<OfflineOperationStatusDto> Operations { get; set; } = [];
}

public sealed class OfflineOperationStatusDto
{
    public Guid ClientOperationId { get; set; }
    public string Status { get; set; } = "";
    public string? Note { get; set; }
    public string? AppliedReferenceType { get; set; }
    public string? AppliedReferenceId { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public int Revision { get; set; }
}

public sealed class OfflineSessionResponse
{
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public string? FullName { get; set; }
    public string? Role { get; set; }
    public int CompanyId { get; set; }
    public string CompanyName { get; set; } = "";
    public IReadOnlyCollection<string> Navigation { get; set; } = [];
    public IReadOnlyList<OfflineOperationTypeDto> OperationTypes { get; set; } = [];
    public DateTime ServerTimeUtc { get; set; }
}

public sealed class OfflineOperationTypeDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Module { get; set; } = "";
}

public sealed class OfflineReferenceItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public string? Extra { get; set; }
}

public sealed class OfflineSnapshotResponse
{
    public DateTime AsOfUtc { get; set; }
    public Dictionary<string, List<OfflineReferenceItem>> Reference { get; set; } = [];

    /// <summary>همان پاسخ داشبورد موبایل (اعداد از مرجع رسمی). اگر ساخته نشود null است.</summary>
    public object? Dashboard { get; set; }
}

public static class OfflineSyncLimits
{
    public const int MaxBatchSize = 25;
    public const int MaxStatusQuery = 200;
    public const int MaxPayloadChars = 32_000;
    public const int MaxDependencies = 20;
    public const int MaxReferenceRows = 5_000;
}
