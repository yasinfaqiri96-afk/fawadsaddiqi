using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.OfflineSync;

/// <summary>
/// ضمانت «یک‌بار» برای عملیات آفلاینِ حالت <see cref="OfflineApplyMode.Ambient"/> (ویرایش، لغو، حذف، تأیید…)، بدون
/// تغییر خود اکشن‌ها. فقط در Scopeی که <see cref="MvcFormDispatcher"/> برای همگام‌سازی می‌سازد فعال است
/// (<see cref="Begin"/>)؛ درخواست‌های عادی وب از آن اثر نمی‌گیرند.
/// <list type="bullet">
///   <item>در نخستین SaveChangesِ اکشن، ردیف <see cref="ProcessedFormToken"/> همان عملیات به همان ذخیره افزوده می‌شود.</item>
///   <item>اگر اکشن خودش تراکنش باز کرده باشد، توکن داخل همان تراکنش است (یا همه یا هیچ).</item>
///   <item>
///     اگر اکشن تراکنش ندارد، با نخستین ذخیره تراکنشی باز می‌شود که همهٔ ذخیره‌های اکشن را در بر می‌گیرد و
///     Dispatcher فقط با موفقیت اکشن (Redirect بدون خطا) آن را Commit می‌کند.
///   </item>
/// </list>
/// حقیقت نهایی «ثبت شد» وجود توکن در دیتابیس است؛ پس تکرار ارسال یا قطع وسط کار سند دوم نمی‌سازد.
/// </summary>
public sealed class OfflineApplyScope : SaveChangesInterceptor
{
    private string? _token;
    private string? _purpose;
    private string? _referenceType;
    private int? _userId;
    private bool _stamped;

    /// <summary>تراکنشی که این Scope باز کرد (اکشن بدون تراکنش)؛ Dispatcher آن را Commit/Rollback می‌کند.</summary>
    public IDbContextTransaction? LazyTransaction { get; private set; }

    /// <summary>خاموش: تراکنش تنبل باز نمی‌شود (اکشنی که پس از نخستین ذخیره، خودش تراکنش باز می‌کند).</summary>
    public bool AllowLazyTransaction { get; set; } = true;

    public bool IsActive => _token is not null;

    /// <summary>اکشن دست‌کم یک‌بار SaveChanges کرد (توکن افزوده شد).</summary>
    public bool HasSaved => _stamped;

    public void Begin(string token, string purpose, string? referenceType, int? userId)
    {
        _token = token;
        _purpose = purpose;
        _referenceType = string.IsNullOrWhiteSpace(referenceType) ? null : referenceType;
        _userId = userId;
        _stamped = false;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is DbContext db && IsActive)
        {
            if (AllowLazyTransaction && LazyTransaction is null && db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            {
                LazyTransaction = db.Database.BeginTransaction();
            }

            Stamp(db);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext db && IsActive)
        {
            if (AllowLazyTransaction && LazyTransaction is null && db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            {
                LazyTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
            }

            Stamp(db);
        }

        return result;
    }

    private void Stamp(DbContext db)
    {
        if (_stamped)
        {
            return;
        }

        _stamped = true;
        db.Set<ProcessedFormToken>().Add(new ProcessedFormToken
        {
            Token = _token!,
            Purpose = _purpose ?? "Offline.Ambient",
            UserId = _userId,
            ConsumedAtUtc = DateTime.UtcNow,
            ReferenceType = _referenceType
        });
    }
}
