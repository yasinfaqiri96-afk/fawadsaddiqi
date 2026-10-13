using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.OfflineSync;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// پیگیری عملیات آفلاین. ثبت قطعی خودکار است (OfflineSyncService + اکشن‌های موجود)؛ این صفحه فقط وضعیت، دلیل
/// تعارض و شمارهٔ سند را نشان می‌دهد و در صورت نیاز امکان رد یا علامت‌زدنِ ثبت دستی را می‌دهد. خودش سندی نمی‌سازد.
/// </summary>
[Authorize]
public sealed class OfflineSyncController(
    ApplicationDbContext db,
    IOfflineSyncService sync,
    ICurrentUserContext currentUser,
    ClientModuleProfile? clientModules = null) : Controller
{
    private const int PageSize = 50;

    public async Task<IActionResult> Index(OfflineSyncStatus? status, int page = 1, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var query = db.OfflineSyncOperations.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        var counts = await db.OfflineSyncOperations.AsNoTracking()
            .GroupBy(o => o.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(o => o.Status == OfflineSyncStatus.Applied || o.Status == OfflineSyncStatus.Rejected)
            .ThenByDescending(o => o.CreatedAtUtc)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Select(o => new OfflineSyncListRow(
                o.Id, o.ClientOperationId, o.OperationType, o.Status, o.StatusNote, o.BusinessDate,
                o.ClientCreatedAtUtc, o.CreatedAtUtc, o.User != null ? o.User.FullName ?? o.User.Username : "—"))
            .ToListAsync(ct);

        return View(new OfflineSyncIndexViewModel(rows, counts, status, page, total, PageSize));
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct = default)
    {
        var row = await db.OfflineSyncOperations.AsNoTracking()
            .Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (row is null)
        {
            return NotFound();
        }

        var type = OfflineOperationTypes.Find(row.OperationType);
        var fields = await DescribePayloadAsync(row.PayloadJson, ct);
        var reviewer = row.ReviewedByUserId is { } reviewerId
            ? await db.Users.AsNoTracking().Where(u => u.Id == reviewerId).Select(u => u.FullName ?? u.Username).FirstOrDefaultAsync(ct)
            : null;
        var canReview = type is not null && RoleAccessRules.CanAccessNavigation(User, type.NavigationKey, clientModules);

        return View(new OfflineSyncDetailsViewModel(row, type, fields, reviewer, canReview));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(
        int id, OfflineReviewDecision decision, string? note, string? referenceType, string? referenceId,
        CancellationToken ct = default)
    {
        var operationType = await db.OfflineSyncOperations.AsNoTracking()
            .Where(o => o.Id == id).Select(o => o.OperationType).FirstOrDefaultAsync(ct);
        if (operationType is null)
        {
            return NotFound();
        }

        // بررسی‌کننده باید خودش بخشِ همان عملیات را در وب ببیند.
        var type = OfflineOperationTypes.Find(operationType);
        if (type is null || !RoleAccessRules.CanAccessNavigation(User, type.NavigationKey, clientModules)
            || currentUser.UserId is not { } reviewerId)
        {
            return RedirectToAction("AccessDenied", "Auth");
        }

        var result = await sync.ReviewAsync(id, decision, reviewerId, note, referenceType, referenceId, ct);
        switch (result)
        {
            case OfflineReviewResult.Done:
                TempData["ok"] = decision switch
                {
                    OfflineReviewDecision.Applied => "عملیات «اعمال‌شده» علامت خورد.",
                    OfflineReviewDecision.Rejected => "عملیات رد شد.",
                    _ => "عملیات در وضعیت تعارض قرار گرفت."
                };
                break;
            case OfflineReviewResult.NoteRequired:
                TempData["err"] = "برای رد یا تعارض، دلیل را بنویسید.";
                break;
            case OfflineReviewResult.AlreadyFinal:
                TempData["err"] = "این عملیات قبلاً بسته شده است.";
                break;
            default:
                return NotFound();
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.Ordinal)
    {
        ["customerId"] = "مشتری",
        ["supplierId"] = "فروشنده",
        ["productId"] = "محصول",
        ["tankId"] = "مخزن",
        ["cashAccountId"] = "حساب نقدی",
        ["fromCashAccountId"] = "از حساب",
        ["toCashAccountId"] = "به حساب",
        ["expenseTypeId"] = "نوع مصرف",
        ["truckId"] = "موتر",
        ["driverId"] = "راننده",
        ["quantity"] = "مقدار",
        ["unitPrice"] = "نرخ واحد",
        ["amount"] = "مبلغ",
        ["currency"] = "اسعار",
        ["truckPlate"] = "نمبر پلیت",
        ["driverName"] = "نام راننده",
        ["name"] = "نام",
        ["phone"] = "تلفن",
        ["text"] = "متن",
        ["description"] = "شرح",
        ["reference"] = "شماره مرجع",
        ["businessDate"] = "تاریخ کاری"
    };

    private async Task<List<OfflinePayloadField>> DescribePayloadAsync(string payloadJson, CancellationToken ct)
    {
        var fields = new List<OfflinePayloadField>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            fields.Add(new OfflinePayloadField("payload", "محتوا", payloadJson));
            return fields;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return fields;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var raw = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
                var display = raw;
                if (property.Name.EndsWith("Id", StringComparison.Ordinal) && int.TryParse(raw, out var refId))
                {
                    display = await ResolveNameAsync(property.Name, refId, ct) is { } name ? $"{name} (#{refId})" : $"#{refId}";
                }

                fields.Add(new OfflinePayloadField(property.Name, FieldLabels.GetValueOrDefault(property.Name, property.Name), display));
            }
        }

        return fields;
    }

    private Task<string?> ResolveNameAsync(string field, int id, CancellationToken ct) => field switch
    {
        "customerId" => db.Customers.AsNoTracking().Where(x => x.Id == id).Select(x => x.NamePersian ?? x.Name).FirstOrDefaultAsync(ct),
        "supplierId" => db.Suppliers.AsNoTracking().Where(x => x.Id == id).Select(x => x.NamePersian ?? x.Name).FirstOrDefaultAsync(ct),
        "productId" => db.Products.AsNoTracking().Where(x => x.Id == id).Select(x => x.NamePersian ?? x.Name).FirstOrDefaultAsync(ct),
        "tankId" => db.StorageTanks.AsNoTracking().Where(x => x.Id == id).Select(x => x.DisplayName ?? x.TankCode).FirstOrDefaultAsync(ct),
        "cashAccountId" or "fromCashAccountId" or "toCashAccountId"
            => db.CashAccounts.AsNoTracking().Where(x => x.Id == id).Select(x => (string?)x.Name).FirstOrDefaultAsync(ct),
        "expenseTypeId" => db.ExpenseTypes.AsNoTracking().Where(x => x.Id == id).Select(x => x.NamePersian ?? x.Name).FirstOrDefaultAsync(ct),
        "truckId" => db.Trucks.AsNoTracking().Where(x => x.Id == id).Select(x => (string?)x.PlateNumber).FirstOrDefaultAsync(ct),
        "driverId" => db.Drivers.AsNoTracking().Where(x => x.Id == id).Select(x => (string?)x.FullName).FirstOrDefaultAsync(ct),
        _ => Task.FromResult<string?>(null)
    };
}

public sealed record OfflineSyncListRow(
    int Id,
    Guid ClientOperationId,
    string OperationType,
    OfflineSyncStatus Status,
    string? StatusNote,
    DateTime? BusinessDate,
    DateTime ClientCreatedAtUtc,
    DateTime ReceivedAtUtc,
    string UserName);

public sealed record OfflineSyncIndexViewModel(
    IReadOnlyList<OfflineSyncListRow> Rows,
    IReadOnlyDictionary<OfflineSyncStatus, int> Counts,
    OfflineSyncStatus? Status,
    int Page,
    int Total,
    int PageSize);

public sealed record OfflinePayloadField(string Key, string Label, string Value);

public sealed record OfflineSyncDetailsViewModel(
    OfflineSyncOperation Operation,
    OfflineOperationType? Type,
    IReadOnlyList<OfflinePayloadField> Fields,
    string? ReviewerName,
    bool CanReview);

public static class OfflineSyncDisplay
{
    public static string StatusText(OfflineSyncStatus status) => status switch
    {
        OfflineSyncStatus.Received => "در حال ثبت",
        OfflineSyncStatus.Applying => "در حال ثبت",
        OfflineSyncStatus.Applied => "همگام‌شده",
        OfflineSyncStatus.Conflict => "تعارض",
        OfflineSyncStatus.Rejected => "ردشده",
        _ => status.ToString()
    };

    public static string StatusClass(OfflineSyncStatus status) => status switch
    {
        OfflineSyncStatus.Applied => "is-active",
        OfflineSyncStatus.Rejected => "is-inactive",
        OfflineSyncStatus.Conflict => "is-danger",
        _ => "is-pending"
    };

    public static string TypeLabel(string key) => OfflineOperationTypes.Find(key)?.Label ?? key;
}
