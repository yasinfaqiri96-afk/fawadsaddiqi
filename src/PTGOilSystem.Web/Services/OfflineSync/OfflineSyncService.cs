using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.OperationalPeriod;

namespace PTGOilSystem.Web.Services.OfflineSync;

public interface IOfflineSyncService
{
    Task RegisterDeviceAsync(int userId, OfflineDeviceRegistrationRequest request, CancellationToken ct = default);

    Task<OfflinePushResponse> PushAsync(
        ClaimsPrincipal user, int userId, OfflinePushRequest request, string? clientIp, CancellationToken ct = default);

    /// <summary>وضعیت عملیات خود کاربر. اگر <paramref name="user"/> داده شود، عملیاتِ معلق دوباره برای ثبت قطعی تلاش می‌شوند.</summary>
    Task<OfflineStatusResponse> GetStatusesAsync(
        int userId, IReadOnlyCollection<Guid> ids, ClaimsPrincipal? user = null, CancellationToken ct = default);

    Task<OfflineReviewResult> ReviewAsync(
        int operationId, OfflineReviewDecision decision, int reviewerUserId, string? note,
        string? referenceType, string? referenceId, CancellationToken ct = default);
}

public enum OfflineReviewDecision
{
    Applied,
    Rejected,
    Conflict
}

public enum OfflineReviewResult
{
    Done,
    NotFound,
    AlreadyFinal,
    NoteRequired
}

public static class OfflineSyncOutcomes
{
    public const string Stored = "stored";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";
    public const string Retry = "retry";
}

public static class OfflineSyncCodes
{
    public const string InvalidOperation = "invalid_operation";
    public const string UnknownType = "unknown_operation_type";
    public const string Forbidden = "operation_forbidden";
    public const string UserMismatch = "user_mismatch";
    public const string CompanyMismatch = "company_mismatch";
    public const string HashMismatch = "payload_hash_mismatch";
    public const string PayloadMismatch = "payload_mismatch";
    public const string PayloadTooLarge = "payload_too_large";
    public const string Validation = "payload_invalid";
    public const string DeviceNotRegistered = "device_not_registered";
    public const string DeviceRevoked = "device_revoked";
    public const string DependencyMissing = "dependency_missing";
    public const string IdTaken = "operation_id_taken";
}

/// <summary>
/// همگام‌سازی و ثبت قطعی خودکار عملیات آفلاین.
/// <list type="number">
///   <item>دریافت: هر <c>ClientOperationId</c> یک‌بار ذخیره می‌شود؛ کاربر، شرکت، دستگاه، صلاحیت، هش و ساختار سنجیده می‌شوند.</item>
///   <item>ثبت قطعی: <see cref="OfflineOperationApplier"/> نوع عملیات را به فرم POSTِ موجودِ ماژول نگاشت می‌کند و
///     <see cref="IMvcFormDispatcher"/> همان اکشن را به نام کاربر با همهٔ فیلترها، اعتبارسنجی‌ها و تراکنش خودش اجرا می‌کند.
///     سند، موجودی و حسابداری را فقط کد موجود می‌سازد.</item>
///   <item>ضد تکرار: توکن ثابت <c>offline:{id}</c> در همان تراکنشِ سند در <c>ProcessedFormTokens</c> ثبت می‌شود؛
///     هر تلاش دوباره (قطع شبکه، بازیابی بکاپ، دو درخواست هم‌زمان) پیش از اجرا همین توکن را می‌بیند.</item>
///   <item>خطای تجاری (موجودی ناکافی، دورهٔ بسته، اعتبارسنجی فرم...) → «تعارض» با همان پیام فرم؛ کاربر همان
///     عملیات را با Revision بزرگ‌تر اصلاح و دوباره ارسال می‌کند.</item>
/// </list>
/// </summary>
public sealed class OfflineSyncService(
    ApplicationDbContext db,
    IAuditService audit,
    IOperationalPeriodGuard periodGuard,
    ISystemCompanyProvider companies,
    TimeProvider clock,
    OfflineOperationApplier applier,
    IMvcFormDispatcher dispatcher,
    ClientModuleProfile? clientModules = null) : IOfflineSyncService
{
    public const string AuditEntityName = nameof(OfflineSyncOperation);

    /// <summary>پس از این مدت، قفل «در حال ثبت» رهاشده (مثلاً قطع برق سرور) دوباره قابل تلاش است.</summary>
    public static readonly TimeSpan StaleApplyingAfter = TimeSpan.FromMinutes(5);

    /// <summary>خطای موقتِ تکراری (مثلاً تعارض نسخه‌ای که با تکرار رفع نمی‌شود) پس از این تعداد تلاش «نیازمند اصلاح» می‌شود.</summary>
    public const int MaxTransientAttempts = 6;

    /// <summary>نسخهٔ پایهٔ عملیات روی رکورد موجود: زمان سرورِ صفحه‌ای که کاربر دید (<c>t:2026-10-14T09:00:00.0000000Z</c>).</summary>
    public const string BaseTimePrefix = "t:";

    /// <summary>فاصلهٔ حداقل میان دو تلاش خودکار برای عملیاتی که هنوز «دریافت‌شده» مانده است.</summary>
    public static readonly TimeSpan RetryReceivedAfter = TimeSpan.FromSeconds(30);

    private sealed record ExistingRow(
        int Id, int UserId, string OperationType, string PayloadHash, OfflineSyncStatus Status, string? StatusNote,
        int Revision, DateTime? LastApplyAttemptAtUtc, string? AppliedReferenceType, string? AppliedReferenceId);

    public async Task RegisterDeviceAsync(int userId, OfflineDeviceRegistrationRequest request, CancellationToken ct = default)
    {
        if (request.DeviceId == Guid.Empty)
        {
            throw new ArgumentException("DeviceId is required.", nameof(request));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var device = await db.OfflineSyncDevices
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == request.DeviceId, ct);
        if (device is null)
        {
            db.OfflineSyncDevices.Add(new OfflineSyncDevice
            {
                UserId = userId,
                DeviceId = request.DeviceId,
                DeviceName = Trim(request.DeviceName, 100),
                LastSeenAtUtc = now,
                CreatedByUserId = userId
            });
        }
        else
        {
            // دستگاهِ باطل‌شده با ثبت دوباره فعال نمی‌شود.
            device.LastSeenAtUtc = now;
            device.DeviceName = Trim(request.DeviceName, 100) ?? device.DeviceName;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (device is null)
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<OfflinePushResponse> PushAsync(
        ClaimsPrincipal user, int userId, OfflinePushRequest request, string? clientIp, CancellationToken ct = default)
    {
        var response = new OfflinePushResponse { ServerTimeUtc = clock.GetUtcNow().UtcDateTime };

        var device = await db.OfflineSyncDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == request.DeviceId, ct);
        if (device is null || device.RevokedAtUtc is not null)
        {
            var code = device is null ? OfflineSyncCodes.DeviceNotRegistered : OfflineSyncCodes.DeviceRevoked;
            var message = device is null
                ? "این دستگاه هنوز برای کار آفلاین ثبت نشده است."
                : "دسترسی آفلاین این دستگاه باطل شده است.";
            response.Results = request.Operations
                .Select(o => Result(o.ClientOperationId, device is null ? OfflineSyncOutcomes.Retry : OfflineSyncOutcomes.Rejected, code, message))
                .ToList();
            return response;
        }

        var companyId = await companies.GetOwnerCompanyIdAsync(ct);
        var periodStatus = await periodGuard.GetStatusAsync(ct);

        // پیش‌نیازِ هم‌دسته اول ذخیره و ثبت می‌شود تا فرزند شناسهٔ واقعی آن را داشته باشد.
        foreach (var operation in OrderByDependencies(request.Operations))
        {
            response.Results.Add(await PushOneAsync(user, userId, companyId, periodStatus, request.DeviceId, operation, clientIp, ct));
        }

        await db.OfflineSyncDevices
            .Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAtUtc, response.ServerTimeUtc), ct);

        var order = request.Operations.Select((o, i) => (o.ClientOperationId, i)).GroupBy(x => x.ClientOperationId)
            .ToDictionary(g => g.Key, g => g.First().i);
        response.Results = response.Results.OrderBy(r => order.GetValueOrDefault(r.ClientOperationId)).ToList();
        return response;
    }

    private async Task<OfflineOperationResult> PushOneAsync(
        ClaimsPrincipal user,
        int userId,
        int companyId,
        OperationalPeriodLockStatus periodStatus,
        Guid deviceId,
        OfflineOperationDto operation,
        string? clientIp,
        CancellationToken ct)
    {
        var id = operation.ClientOperationId;
        if (id == Guid.Empty)
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.InvalidOperation, "شناسهٔ عملیات معتبر نیست.");
        }

        if (operation.UserId != userId)
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.UserMismatch,
                "این عملیات با کاربر دیگری ثبت شده است؛ با همان کاربر وارد شوید.");
        }

        if (operation.CompanyId is { } sentCompany && sentCompany != companyId)
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.CompanyMismatch,
                "این عملیات به شرکت دیگری تعلق دارد.");
        }

        var payloadJson = operation.PayloadJson ?? "";
        var hash = ComputeHash(payloadJson);
        var revision = Math.Max(1, operation.Revision);

        var existing = await LoadRowAsync(id, ct);
        if (existing is not null)
        {
            if (existing.UserId != userId)
            {
                return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.IdTaken, "شناسهٔ عملیات تکراری است.");
            }

            // اصلاحِ عملیاتِ دارای تعارض: همان شناسه، نسخهٔ تازه.
            if (existing.Status == OfflineSyncStatus.Conflict && revision > existing.Revision)
            {
                return await ResubmitAsync(user, userId, existing, operation, payloadJson, hash, revision, ct);
            }

            if (existing.PayloadHash != hash && revision <= existing.Revision)
            {
                return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.PayloadMismatch,
                    existing.Status == OfflineSyncStatus.Applied
                        ? "این عملیات قبلاً ثبت قطعی شده است و تغییر نمی‌کند."
                        : "این عملیات قبلاً با محتوای دیگری رسیده است؛ تغییر بی‌صدا پذیرفته نمی‌شود.");
            }

            // پاسخِ گم‌شده: همان نتیجه؛ اگر هنوز ثبت نشده، اکنون تلاش می‌شود.
            var current = NeedsApply(existing) ? await ApplyAsync(user, existing.Id, ct) : existing;
            return Result(id, OfflineSyncOutcomes.Duplicate, null, current.StatusNote, current);
        }

        var type = OfflineOperationTypes.Find(operation.OperationType);
        var validationFailure = Validate(user, type, operation, payloadJson, hash, out var businessDate, out var dependencies);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var status = OfflineSyncStatus.Received;
        string? note = null;

        if (dependencies.Count > 0)
        {
            var found = await db.OfflineSyncOperations.AsNoTracking()
                .Where(o => o.UserId == userId && dependencies.Contains(o.ClientOperationId))
                .Select(o => new { o.ClientOperationId, o.Status })
                .ToListAsync(ct);
            if (found.Count < dependencies.Count)
            {
                return Result(id, OfflineSyncOutcomes.Retry, OfflineSyncCodes.DependencyMissing,
                    "عملیات پیش‌نیاز هنوز به سرور نرسیده است.");
            }

            if (found.Any(f => f.Status == OfflineSyncStatus.Rejected))
            {
                status = OfflineSyncStatus.Conflict;
                note = "یکی از عملیات پیش‌نیاز رد شده است.";
            }
        }

        if (status == OfflineSyncStatus.Received && businessDate is { } date && periodStatus.Covers(date))
        {
            status = OfflineSyncStatus.Conflict;
            note = $"تاریخ کاری در دورهٔ بسته‌شده است (تا {periodStatus.LockedThroughDate:yyyy-MM-dd}).";
        }

        if (status == OfflineSyncStatus.Received && revision == 1)
        {
            // همان محتوا با شناسهٔ دیگر (دو دستگاه یا ثبت دوبارهٔ دستی) → پیش از ثبت قطعی متوقف شود.
            var possibleDuplicate = await db.OfflineSyncOperations.AsNoTracking()
                .AnyAsync(o => o.UserId == userId
                    && o.OperationType == type!.Key
                    && o.PayloadHash == hash
                    && o.Status != OfflineSyncStatus.Rejected, ct);
            if (possibleDuplicate)
            {
                status = OfflineSyncStatus.Conflict;
                note = "عملیاتی با همین محتوا قبلاً با شناسهٔ دیگری رسیده است (احتمال ثبت تکراری). اگر واقعاً معاملهٔ جداگانه است، «ارسال دوباره» بزنید.";
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var row = new OfflineSyncOperation
        {
            ClientOperationId = id,
            DeviceId = deviceId,
            UserId = userId,
            CompanyId = companyId,
            OperationType = type!.Key,
            EntityId = Trim(operation.EntityId, 64),
            BaseVersion = Trim(operation.BaseVersion, 64),
            PayloadJson = payloadJson,
            PayloadHash = hash,
            ClientCreatedAtUtc = operation.CreatedAtUtc.Kind == DateTimeKind.Utc
                ? operation.CreatedAtUtc
                : DateTime.SpecifyKind(operation.CreatedAtUtc, DateTimeKind.Utc),
            BusinessDate = businessDate,
            SchemaVersion = operation.SchemaVersion,
            Revision = revision,
            DependenciesCsv = dependencies.Count == 0 ? null : string.Join(',', dependencies),
            Status = status,
            StatusNote = note,
            ReceivedFromIp = Trim(clientIp, 64),
            CreatedAtUtc = now,
            CreatedByUserId = userId
        };
        db.OfflineSyncOperations.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // همان شناسه هم‌زمان از درخواست دیگری ذخیره شد (unique index).
            db.ChangeTracker.Clear();
            var winner = await LoadRowAsync(id, ct) ?? throw new InvalidOperationException("Offline operation insert failed.");
            return winner.UserId == userId && winner.PayloadHash == hash
                ? Result(id, OfflineSyncOutcomes.Duplicate, null, winner.StatusNote, winner)
                : Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.PayloadMismatch, "شناسهٔ عملیات تکراری است.");
        }

        db.ChangeTracker.Clear();
        await audit.LogAndSaveAsync(AuditEntityName, row.Id, AuditAction.Insert, userId,
            $"OfflineReceived: {type.Key} | Op={id:D} | Device={deviceId:D} | Status={status}", ct);

        var final = status == OfflineSyncStatus.Received
            ? await ApplyAsync(user, row.Id, ct)
            : (await LoadRowAsync(id, ct))!;
        return Result(id, OfflineSyncOutcomes.Stored, null, final.StatusNote, final);
    }

    private async Task<OfflineOperationResult> ResubmitAsync(
        ClaimsPrincipal user, int userId, ExistingRow existing, OfflineOperationDto operation,
        string payloadJson, string hash, int revision, CancellationToken ct)
    {
        var id = operation.ClientOperationId;
        if (!string.Equals(existing.OperationType, operation.OperationType, StringComparison.Ordinal))
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.Validation, "نوع عملیات در اصلاح قابل تغییر نیست.");
        }

        var type = OfflineOperationTypes.Find(operation.OperationType);
        var validationFailure = Validate(user, type, operation, payloadJson, hash, out var businessDate, out var dependencies);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var affected = await db.OfflineSyncOperations
            .Where(o => o.Id == existing.Id && o.Status == OfflineSyncStatus.Conflict && o.Revision < revision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.PayloadJson, payloadJson)
                .SetProperty(o => o.PayloadHash, hash)
                .SetProperty(o => o.Revision, revision)
                .SetProperty(o => o.BusinessDate, businessDate)
                .SetProperty(o => o.DependenciesCsv, dependencies.Count == 0 ? null : string.Join(',', dependencies))
                // اصلاح از نسخهٔ تازهٔ صفحه: نسخهٔ پایه هم تازه می‌شود (بررسی کهنگی روی همان چیزی که کاربر دوباره دید).
                .SetProperty(o => o.BaseVersion, Trim(operation.BaseVersion, 64))
                .SetProperty(o => o.Status, OfflineSyncStatus.Received)
                .SetProperty(o => o.StatusNote, (string?)null)
                .SetProperty(o => o.UpdatedAtUtc, now)
                .SetProperty(o => o.UpdatedByUserId, userId), ct);

        if (affected > 0)
        {
            await audit.LogAndSaveAsync(AuditEntityName, existing.Id, AuditAction.Update, userId,
                $"OfflineResubmitted: Op={id:D} | Revision={revision}", ct);
        }

        var final = await ApplyAsync(user, existing.Id, ct);
        return Result(id, OfflineSyncOutcomes.Stored, null, final.StatusNote, final);
    }

    private OfflineOperationResult? Validate(
        ClaimsPrincipal user, OfflineOperationType? type, OfflineOperationDto operation, string payloadJson, string hash,
        out DateTime? businessDate, out List<Guid> dependencies)
    {
        businessDate = null;
        dependencies = [];
        var id = operation.ClientOperationId;

        if (type is null)
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.UnknownType, "نوع عملیات پشتیبانی نمی‌شود.");
        }

        var (navigationKey, label) = OfflineOperationTypes.AccessOf(type, payloadJson);
        if (navigationKey is null || !RoleAccessRules.CanAccessNavigation(user, navigationKey, clientModules))
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.Forbidden, $"شما اجازهٔ ثبت «{label}» را ندارید.");
        }

        if (payloadJson.Length > OfflineSyncLimits.MaxPayloadChars)
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.PayloadTooLarge, "حجم عملیات بیش از حد مجاز است.");
        }

        if (!string.Equals(hash, operation.PayloadHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.HashMismatch, "صحت محتوای عملیات تأیید نشد (هش نادرست).");
        }

        string? validationError;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            validationError = OfflineOperationTypes.Validate(type, document.RootElement);
        }
        catch (JsonException)
        {
            validationError = "محتوای عملیات JSON معتبر نیست.";
        }

        if (validationError is not null)
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.Validation, validationError);
        }

        if (!string.IsNullOrWhiteSpace(operation.BusinessDate))
        {
            if (!DateTime.TryParseExact(operation.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.Validation, "تاریخ کاری معتبر نیست.");
            }

            businessDate = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
        }

        dependencies = (operation.Dependencies ?? []).Where(d => d != Guid.Empty && d != id).Distinct().ToList();
        if (dependencies.Count > OfflineSyncLimits.MaxDependencies)
        {
            return Result(id, OfflineSyncOutcomes.Rejected, OfflineSyncCodes.Validation, "تعداد پیش‌نیازها بیش از حد است.");
        }

        return null;
    }

    private bool NeedsApply(ExistingRow row)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return row.Status == OfflineSyncStatus.Received
            || (row.Status == OfflineSyncStatus.Applying
                && (row.LastApplyAttemptAtUtc is null || row.LastApplyAttemptAtUtc < now - StaleApplyingAfter));
    }

    /// <summary>
    /// ثبت قطعی یک عملیات با اکشن موجود. ابتدا ردیف با UPDATE شرطی «قفل» می‌شود (Received → Applying) تا دو
    /// درخواست هم‌زمان یک عملیات را دوبار اجرا نکنند؛ سپس توکن ضد تکرار، پیش‌نیازها و صلاحیت دوباره سنجیده می‌شوند.
    /// </summary>
    private async Task<ExistingRow> ApplyAsync(ClaimsPrincipal user, int rowId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var staleBefore = now - StaleApplyingAfter;
        var claimed = await db.OfflineSyncOperations
            .Where(o => o.Id == rowId
                && (o.Status == OfflineSyncStatus.Received
                    || (o.Status == OfflineSyncStatus.Applying
                        && (o.LastApplyAttemptAtUtc == null || o.LastApplyAttemptAtUtc < staleBefore))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OfflineSyncStatus.Applying)
                .SetProperty(o => o.ApplyAttempts, o => o.ApplyAttempts + 1)
                .SetProperty(o => o.LastApplyAttemptAtUtc, now), ct);

        var row = await db.OfflineSyncOperations.AsNoTracking().FirstAsync(o => o.Id == rowId, ct);
        if (claimed == 0)
        {
            return ToExisting(row);
        }

        var token = OfflineOperationApplier.FormTokenFor(row.ClientOperationId);
        try
        {
            var type = OfflineOperationTypes.Find(row.OperationType);
            if (type is null)
            {
                return await FinishAsync(row, OfflineSyncStatus.Rejected, "نوع عملیات پشتیبانی نمی‌شود.", null, null, ct);
            }

            // سند قبلاً ثبت شده (پاسخ قبلی گم شد یا سرور وسط کار متوقف شد): دوباره اجرا نمی‌شود.
            var committedToken = await db.ProcessedFormTokens.AsNoTracking()
                .Where(t => t.Token == token)
                .Select(t => new { t.ReferenceType })
                .FirstOrDefaultAsync(ct);
            if (committedToken is not null)
            {
                return await FinishAsync(row, OfflineSyncStatus.Applied, null, committedToken.ReferenceType ?? type.ApplyController,
                    row.AppliedReferenceId, ct);
            }

            var (navigationKey, label) = OfflineOperationTypes.AccessOf(type, row.PayloadJson);
            if (navigationKey is null || !RoleAccessRules.CanAccessNavigation(user, navigationKey, clientModules))
            {
                return await FinishAsync(row, OfflineSyncStatus.Conflict, $"شما اجازهٔ ثبت «{label}» را ندارید.", null, null, ct);
            }

            var dependencies = ParseDependencies(row.DependenciesCsv);
            if (dependencies.Count > 0)
            {
                var states = await db.OfflineSyncOperations.AsNoTracking()
                    .Where(o => o.UserId == row.UserId && dependencies.Contains(o.ClientOperationId))
                    .Select(o => o.Status)
                    .ToListAsync(ct);
                if (states.Any(s => s == OfflineSyncStatus.Rejected))
                {
                    return await FinishAsync(row, OfflineSyncStatus.Conflict, "یکی از عملیات پیش‌نیاز رد شده است.", null, null, ct);
                }

                if (states.Count < dependencies.Count || states.Any(s => s != OfflineSyncStatus.Applied))
                {
                    return await FinishAsync(row, OfflineSyncStatus.Received, "در انتظار ثبت قطعیِ عملیات پیش‌نیاز.", null, null, ct);
                }
            }

            var plan = await applier.BuildAsync(row, ct);
            if (plan.NoDocument)
            {
                return await FinishAsync(row, OfflineSyncStatus.Applied, null, "Note", null, ct);
            }

            if (plan.Unsupported)
            {
                return await FinishAsync(row, OfflineSyncStatus.Rejected, plan.Error, null, null, ct);
            }

            if (plan.Plan is null)
            {
                return await FinishAsync(row, OfflineSyncStatus.Conflict, plan.Error ?? "اطلاعات عملیات کامل نیست.", null, null, ct);
            }

            var target = plan.Plan;
            // همان فورم پیش‌تر آنلاین با این توکن فرستاده شد و پاسخ نامعلوم ماند: اگر سند ثبت شده، دوباره ساخته نمی‌شود.
            var formToken = target.UsesFormToken ? target.FormToken ?? token : null;
            if (target.FormToken is { } original
                && await db.ProcessedFormTokens.AsNoTracking().AnyAsync(t => t.Token == original, ct))
            {
                return await FinishAsync(row, OfflineSyncStatus.Applied, "پیش‌تر به‌صورت آنلاین ثبت شده بود؛ سند دوم ساخته نشد.",
                    target.ReferenceType, row.AppliedReferenceId, ct);
            }

            // توکن صفحه پیدا نشد. اگر ارسال نامعلوم قدیمی‌تر از پنجرهٔ نگهداری توکن باشد، نبودِ توکن ثابت نمی‌کند سند ساخته
            // نشده است (شاید توکن پاک شده)؛ پس خودکار ثبت نمی‌شود. کاربر پس از بررسی فهرست، همان عملیات را اصلاح/دوباره
            // می‌فرستد (Revision بزرگ‌تر) و آن‌گاه ثبت می‌شود. ساعت دستگاه با حاشیهٔ یک هفته‌ای سنجیده می‌شود.
            if (target.FormToken is not null && row.Revision <= 1
                && clock.GetUtcNow().UtcDateTime - row.ClientCreatedAtUtc
                    > TimeSpan.FromDays(ProcessedFormTokenRetentionService.OfflineReplayRetentionDays - 7))
            {
                return await FinishAsync(row, OfflineSyncStatus.Conflict,
                    "این فورم پیش از قطع اتصال به سرور فرستاده شده بود و پاسخش نامعلوم ماند؛ از آن زمان بیش از یک سال گذشته و سرور "
                    + "دیگر نمی‌تواند خودکار تشخیص دهد سند ساخته شده یا نه. فهرست را بررسی کنید؛ اگر سند نیست، همان عملیات را "
                    + "«اصلاح در فورم اصلی» یا «ارسال دوباره» کنید.", null, null, ct);
            }

            // ویرایش/لغو/حذف روی رکورد موجود: اگر کاربر دیگری پس از نسخهٔ دستگاه آن را تغییر داده، کورکورانه اجرا نمی‌شود.
            if (target.IsAmbient && await StaleReasonAsync(row, target, ct) is { } stale)
            {
                return await FinishAsync(row, OfflineSyncStatus.Conflict, stale, null, null, ct);
            }

            var result = await dispatcher.PostAsync(
                target.Controller,
                target.Action,
                target.Fields,
                user,
                target.IsAmbient ? null : formToken,
                wrapInTransaction: !target.UsesFormToken && !target.IsAmbient,
                onCommittedInTransaction: target.UsesFormToken || target.IsAmbient
                    ? null
                    : async (scopeDb, referenceId) =>
                    {
                        // اکشنِ بدون توکن: توکن و وضعیت در همان تراکنشِ سند (یا همه یا هیچ).
                        scopeDb.ProcessedFormTokens.Add(new ProcessedFormToken
                        {
                            Token = token,
                            Purpose = $"Offline.{row.OperationType}",
                            UserId = row.UserId,
                            ConsumedAtUtc = clock.GetUtcNow().UtcDateTime,
                            ReferenceType = target.ReferenceType
                        });
                        await scopeDb.SaveChangesAsync(ct);
                        await scopeDb.OfflineSyncOperations.Where(o => o.Id == row.Id)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(o => o.Status, OfflineSyncStatus.Applied)
                                .SetProperty(o => o.StatusNote, (string?)null)
                                .SetProperty(o => o.AppliedReferenceType, target.ReferenceType)
                                .SetProperty(o => o.AppliedReferenceId, referenceId), ct);
                    },
                ct,
                target.ReferenceType,
                target.FieldList,
                target.Query,
                target.IsAmbient
                    ? new OfflineAmbientApply(token, $"Offline.{row.OperationType}", row.UserId, target.RouteId)
                    : null);

            if (result.Committed)
            {
                return await FinishAsync(row, OfflineSyncStatus.Applied, null, target.ReferenceType,
                    result.ReferenceId ?? target.EntityId?.ToString(CultureInfo.InvariantCulture), ct);
            }

            if (result.TransientFailure && row.ApplyAttempts >= MaxTransientAttempts)
            {
                return await FinishAsync(row, OfflineSyncStatus.Conflict,
                    $"پس از {row.ApplyAttempts} تلاش خودکار ثبت نشد؛ معلومات را بررسی و دوباره بفرستید. "
                    + string.Join(" • ", result.Errors), null, null, ct);
            }

            if (result.TransientFailure)
            {
                return await FinishAsync(row, OfflineSyncStatus.Received,
                    "خطای موقت سرور؛ دوباره خودکار تلاش می‌شود. " + string.Join(" • ", result.Errors), null, null, ct);
            }

            return await FinishAsync(row, OfflineSyncStatus.Conflict, string.Join(" • ", result.Errors), null, null, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // قفل را رها کن تا تلاش بعدی ممکن باشد؛ توکن از تکرار سند جلوگیری می‌کند.
            await db.OfflineSyncOperations.Where(o => o.Id == rowId && o.Status == OfflineSyncStatus.Applying)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OfflineSyncStatus.Received), CancellationToken.None);
            throw;
        }
    }

    private async Task<ExistingRow> FinishAsync(
        OfflineSyncOperation row, OfflineSyncStatus status, string? note, string? referenceType, string? referenceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        note = Trim(note, 1000);
        await db.OfflineSyncOperations
            .Where(o => o.Id == row.Id && o.Status != OfflineSyncStatus.Rejected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, status)
                .SetProperty(o => o.StatusNote, note)
                .SetProperty(o => o.AppliedReferenceType, status == OfflineSyncStatus.Applied ? referenceType : null)
                .SetProperty(o => o.AppliedReferenceId, status == OfflineSyncStatus.Applied ? referenceId : null)
                .SetProperty(o => o.UpdatedAtUtc, now), ct);

        if (status is OfflineSyncStatus.Applied or OfflineSyncStatus.Conflict or OfflineSyncStatus.Rejected)
        {
            await audit.LogAndSaveAsync(AuditEntityName, row.Id,
                status == OfflineSyncStatus.Applied ? AuditAction.Approve : AuditAction.Update, row.UserId,
                $"OfflineApply: {row.OperationType} | Op={row.ClientOperationId:D} | Rev={row.Revision} | {status}"
                    + (referenceId is null ? "" : $" | Ref={referenceType}/{referenceId}"), ct);
        }

        return ToExisting(await db.OfflineSyncOperations.AsNoTracking().FirstAsync(o => o.Id == row.Id, ct));
    }

    public async Task<OfflineStatusResponse> GetStatusesAsync(
        int userId, IReadOnlyCollection<Guid> ids, ClaimsPrincipal? user = null, CancellationToken ct = default)
    {
        var wanted = ids.Where(i => i != Guid.Empty).Distinct().Take(OfflineSyncLimits.MaxStatusQuery).ToList();

        if (user is not null)
        {
            // عملیاتی که با خطای موقت یا انتظار پیش‌نیاز «دریافت‌شده» مانده‌اند، دوباره ثبت می‌شوند.
            var now = clock.GetUtcNow().UtcDateTime;
            var retryBefore = now - RetryReceivedAfter;
            var staleBefore = now - StaleApplyingAfter;
            var retryIds = await db.OfflineSyncOperations.AsNoTracking()
                .Where(o => o.UserId == userId && wanted.Contains(o.ClientOperationId)
                    && ((o.Status == OfflineSyncStatus.Received && (o.LastApplyAttemptAtUtc == null || o.LastApplyAttemptAtUtc < retryBefore))
                        || (o.Status == OfflineSyncStatus.Applying && o.LastApplyAttemptAtUtc < staleBefore)))
                .OrderBy(o => o.Id)
                .Select(o => o.Id)
                .Take(OfflineSyncLimits.MaxBatchSize)
                .ToListAsync(ct);
            foreach (var rowId in retryIds)
            {
                await ApplyAsync(user, rowId, ct);
            }
        }

        var rows = await db.OfflineSyncOperations.AsNoTracking()
            .Where(o => o.UserId == userId && wanted.Contains(o.ClientOperationId))
            .Select(o => new OfflineOperationStatusDto
            {
                ClientOperationId = o.ClientOperationId,
                Status = o.Status.ToString(),
                Note = o.StatusNote,
                AppliedReferenceType = o.AppliedReferenceType,
                AppliedReferenceId = o.AppliedReferenceId,
                ReviewedAtUtc = o.ReviewedAtUtc,
                Revision = o.Revision
            })
            .ToListAsync(ct);

        var known = rows.Select(r => r.ClientOperationId).ToHashSet();
        rows.AddRange(wanted.Where(w => !known.Contains(w))
            .Select(w => new OfflineOperationStatusDto { ClientOperationId = w, Status = "Unknown" }));

        return new OfflineStatusResponse { ServerTimeUtc = clock.GetUtcNow().UtcDateTime, Operations = rows };
    }

    public async Task<OfflineReviewResult> ReviewAsync(
        int operationId,
        OfflineReviewDecision decision,
        int reviewerUserId,
        string? note,
        string? referenceType,
        string? referenceId,
        CancellationToken ct = default)
    {
        note = Trim(note, 1000);
        if (decision is OfflineReviewDecision.Rejected or OfflineReviewDecision.Conflict && note is null)
        {
            return OfflineReviewResult.NoteRequired;
        }

        var exists = await db.OfflineSyncOperations.AnyAsync(o => o.Id == operationId, ct);
        if (!exists)
        {
            return OfflineReviewResult.NotFound;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var newStatus = decision switch
        {
            OfflineReviewDecision.Applied => OfflineSyncStatus.Applied,
            OfflineReviewDecision.Rejected => OfflineSyncStatus.Rejected,
            _ => OfflineSyncStatus.Conflict
        };
        var refType = decision == OfflineReviewDecision.Applied ? Trim(referenceType, 64) : null;
        var refId = decision == OfflineReviewDecision.Applied ? Trim(referenceId, 64) : null;

        // فقط از وضعیت باز (نه در حال ثبت)؛ «اعمال‌شده» و «ردشده» نهایی‌اند.
        var affected = await db.OfflineSyncOperations
            .Where(o => o.Id == operationId
                && (o.Status == OfflineSyncStatus.Received || o.Status == OfflineSyncStatus.Conflict))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, newStatus)
                .SetProperty(o => o.StatusNote, note)
                .SetProperty(o => o.ReviewedByUserId, reviewerUserId)
                .SetProperty(o => o.ReviewedAtUtc, now)
                .SetProperty(o => o.AppliedReferenceType, refType)
                .SetProperty(o => o.AppliedReferenceId, refId)
                .SetProperty(o => o.UpdatedAtUtc, now)
                .SetProperty(o => o.UpdatedByUserId, reviewerUserId), ct);

        if (affected == 0)
        {
            return OfflineReviewResult.AlreadyFinal;
        }

        await audit.LogAndSaveAsync(
            AuditEntityName,
            operationId,
            decision == OfflineReviewDecision.Applied ? AuditAction.Approve : AuditAction.Update,
            reviewerUserId,
            $"OfflineReview: {newStatus}" + (refType is null ? "" : $" | Ref={refType}/{refId}"),
            ct);

        return OfflineReviewResult.Done;
    }

    public static string ComputeHash(string payloadJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();

    internal static IEnumerable<OfflineOperationDto> OrderByDependencies(IReadOnlyList<OfflineOperationDto> operations)
    {
        var byId = operations.GroupBy(o => o.ClientOperationId).ToDictionary(g => g.Key, g => g.First());
        var visited = new HashSet<Guid>();
        var ordered = new List<OfflineOperationDto>(operations.Count);

        void Visit(OfflineOperationDto operation, HashSet<Guid> path)
        {
            if (visited.Contains(operation.ClientOperationId) || !path.Add(operation.ClientOperationId))
            {
                return;
            }

            foreach (var dependency in operation.Dependencies ?? [])
            {
                if (byId.TryGetValue(dependency, out var parent))
                {
                    Visit(parent, path);
                }
            }

            visited.Add(operation.ClientOperationId);
            ordered.Add(operation);
        }

        foreach (var operation in operations)
        {
            if (ReferenceEquals(byId[operation.ClientOperationId], operation))
            {
                Visit(operation, []);
            }
            else
            {
                ordered.Add(operation); // تکرار در همان دسته → نتیجهٔ duplicate.
            }
        }

        return ordered;
    }

    private async Task<ExistingRow?> LoadRowAsync(Guid clientOperationId, CancellationToken ct)
    {
        var row = await db.OfflineSyncOperations.AsNoTracking().FirstOrDefaultAsync(o => o.ClientOperationId == clientOperationId, ct);
        return row is null ? null : ToExisting(row);
    }

    private static ExistingRow ToExisting(OfflineSyncOperation o)
        => new(o.Id, o.UserId, o.OperationType, o.PayloadHash, o.Status, o.StatusNote, o.Revision, o.LastApplyAttemptAtUtc,
            o.AppliedReferenceType, o.AppliedReferenceId);

    private static List<Guid> ParseDependencies(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();

    private static OfflineOperationResult Result(Guid id, string outcome, string? code, string? message, ExistingRow? row = null)
        => new()
        {
            ClientOperationId = id,
            Outcome = outcome,
            Code = code,
            Message = message,
            Status = row?.Status.ToString(),
            Revision = row?.Revision,
            AppliedReferenceType = row?.AppliedReferenceType,
            AppliedReferenceId = row?.AppliedReferenceId
        };

    private sealed class RecordStamp
    {
        public DateTime? UpdatedAtUtc { get; set; }
        public int? UpdatedByUserId { get; set; }
    }

    /// <summary>
    /// بررسی کهنگی: رکورد هدف پس از زمان سرورِ صفحه‌ای که کاربر دید (BaseVersion) به‌دست کاربر دیگری تغییر کرده است؟
    /// تغییرهای خودِ همین کاربر (عملیات آفلاین قبلی یا کار آنلاینش) مانع نیستند؛ Version و قواعد خود اکشن هم سر جایشان‌اند.
    /// </summary>
    private async Task<string?> StaleReasonAsync(OfflineSyncOperation row, OfflineApplyPlan plan, CancellationToken ct)
    {
        if (plan.Target is not { CheckStale: true } target || plan.EntityId is not { } entityId
            || row.BaseVersion is not { } baseVersion || !baseVersion.StartsWith(BaseTimePrefix, StringComparison.Ordinal)
            || !DateTime.TryParse(baseVersion[BaseTimePrefix.Length..], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var seenAtUtc))
        {
            return null;
        }

        var entityType = db.Model.GetEntityTypes()
            .FirstOrDefault(t => t.ClrType.Name == target.ReferenceType && typeof(BaseEntity).IsAssignableFrom(t.ClrType));
        if (entityType?.GetTableName() is not { } table)
        {
            return null;
        }

        var schema = entityType.GetSchema();
        var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, schema);
        string Column(string property) => entityType.FindProperty(property)!.GetColumnName(store)!;
        var from = schema is null ? $"\"{table}\"" : $"\"{schema}\".\"{table}\"";
        var sql = $"SELECT \"{Column(nameof(BaseEntity.UpdatedAtUtc))}\" AS \"UpdatedAtUtc\", "
            + $"\"{Column(nameof(BaseEntity.UpdatedByUserId))}\" AS \"UpdatedByUserId\" "
            + $"FROM {from} WHERE \"{Column(nameof(BaseEntity.Id))}\" = {{0}}";
        var stamp = await db.Database.SqlQueryRaw<RecordStamp>(sql, entityId).FirstOrDefaultAsync(ct);
        if (stamp?.UpdatedAtUtc is not { } updated)
        {
            return null;
        }

        var updatedUtc = DateTime.SpecifyKind(updated, DateTimeKind.Utc);
        if (updatedUtc <= seenAtUtc.AddSeconds(1) || stamp.UpdatedByUserId == row.UserId)
        {
            return null;
        }

        return "این رکورد پس از نسخه‌ای که در دستگاه دیده شد، به‌دست کاربر دیگری تغییر کرده است "
            + $"(نسخهٔ دستگاه: {seenAtUtc:yyyy-MM-dd HH:mm:ss} UTC، تغییر سرور: {updatedUtc:yyyy-MM-dd HH:mm:ss} UTC). "
            + "برای جلوگیری از بازنویسی کار دیگران اجرا نشد؛ صفحهٔ تازهٔ همین رکورد را باز کنید و تغییر را دوباره ثبت کنید.";
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
