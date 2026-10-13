using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Services.OfflineSync;

public enum OfflineFieldKind
{
    /// <summary>شناسهٔ رکورد مرجع (عدد صحیح مثبت).</summary>
    ReferenceId,

    /// <summary>عدد مثبت (مقدار، مبلغ).</summary>
    Positive,

    /// <summary>متن غیرخالی.</summary>
    Text
}

public sealed record OfflineFieldRule(string Name, OfflineFieldKind Kind);

/// <summary>
/// یک نوع عملیات آفلاین. <see cref="NavigationKey"/> همان کلید ناوبری وب است: کاربری که آن بخش را
/// در وب نمی‌بیند، نمی‌تواند عملیاتش را آفلاین بفرستد یا بررسی کند. <see cref="ApplyController"/> و
/// <see cref="ApplyAction"/> فرم موجودی است که ثبت قطعی با آن انجام می‌شود.
/// </summary>
public sealed record OfflineOperationType(
    string Key,
    string Label,
    string NavigationKey,
    string ApplyController,
    string ApplyAction,
    IReadOnlyList<OfflineFieldRule> Fields,
    bool AutoApply = true);

/// <summary>
/// فهرست بستهٔ عملیات مجاز آفلاین. پس از رسیدن به سرور، <see cref="OfflineOperationApplier"/> هر نوع را با
/// اکشن ثبتِ موجودِ همان ماژول ثبت قطعی می‌کند؛ اینجا فقط بررسی ساختاری ورودی است.
/// </summary>
public static class OfflineOperationTypes
{
    public const string SaleRequest = "Sale.Request";
    public const string CustomerReceiptRequest = "CustomerReceipt.Request";
    public const string SupplierPaymentRequest = "SupplierPayment.Request";
    public const string ExpenseRequest = "Expense.Request";
    public const string MoneyTransferRequest = "MoneyTransfer.Request";
    public const string LoadingNote = "Loading.Note";
    public const string ReceiptNote = "Receipt.Note";
    public const string CustomerDraft = "Customer.Draft";
    public const string GeneralNote = "General.Note";

    public static readonly IReadOnlyList<OfflineOperationType> All =
    [
        new(SaleRequest, "فروش از مخزن", RoleNavigationKeys.Sales, "Sales", "Create",
            [new("customerId", OfflineFieldKind.ReferenceId), new("productId", OfflineFieldKind.ReferenceId),
             new("tankId", OfflineFieldKind.ReferenceId), new("quantity", OfflineFieldKind.Positive),
             new("currency", OfflineFieldKind.Text)]),
        new(CustomerReceiptRequest, "دریافت از مشتری", RoleNavigationKeys.Payments, "Payments", "Create",
            [new("customerId", OfflineFieldKind.ReferenceId), new("cashAccountId", OfflineFieldKind.ReferenceId),
             new("amount", OfflineFieldKind.Positive), new("currency", OfflineFieldKind.Text)]),
        new(SupplierPaymentRequest, "پرداخت به فروشنده", RoleNavigationKeys.Payments, "Payments", "Create",
            [new("supplierId", OfflineFieldKind.ReferenceId), new("cashAccountId", OfflineFieldKind.ReferenceId),
             new("amount", OfflineFieldKind.Positive), new("currency", OfflineFieldKind.Text)]),
        new(ExpenseRequest, "مصرف", RoleNavigationKeys.Operations, "Expenses", "Create",
            [new("expenseTypeId", OfflineFieldKind.ReferenceId), new("amount", OfflineFieldKind.Positive),
             new("currency", OfflineFieldKind.Text), new("description", OfflineFieldKind.Text)]),
        // مشعل فرم انتقال میان حساب‌ها ندارد؛ نوع فقط برای عملیات قدیمی شناخته می‌شود و ثبت خودکار نمی‌شود.
        new(MoneyTransferRequest, "انتقال پول (پشتیبانی نمی‌شود)", RoleNavigationKeys.Payments, "Payments", "Create",
            [new("amount", OfflineFieldKind.Positive)], AutoApply: false),
        new(LoadingNote, "بارگیری", RoleNavigationKeys.Operations, "Loading", "Create",
            [new("contractId", OfflineFieldKind.ReferenceId), new("productId", OfflineFieldKind.ReferenceId),
             new("quantity", OfflineFieldKind.Positive)]),
        new(ReceiptNote, "رسید و تخلیه به مخزن", RoleNavigationKeys.Operations, "LoadingReceipts", "Create",
            [new("loadingId", OfflineFieldKind.ReferenceId), new("tankId", OfflineFieldKind.ReferenceId),
             new("quantity", OfflineFieldKind.Positive)]),
        new(CustomerDraft, "مشتری جدید", RoleNavigationKeys.Partners, "Customers", "Create",
            [new("name", OfflineFieldKind.Text)]),
        new(GeneralNote, "یادداشت عملیاتی", RoleNavigationKeys.Dashboard, "Home", "Index",
            [new("text", OfflineFieldKind.Text)])
    ];

    private static readonly Dictionary<string, OfflineOperationType> ByKey =
        All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    public static OfflineOperationType? Find(string? key)
        => key is not null && ByKey.TryGetValue(key, out var type) ? type : null;

    public static IReadOnlyList<OfflineOperationType> AllowedFor(ClaimsPrincipal user, ClientModuleProfile? modules)
        => All.Where(t => t.AutoApply && RoleAccessRules.CanAccessNavigation(user, t.NavigationKey, modules)).ToList();

    /// <summary>بررسی ساختاری payload. منطق تجاری اینجا نیست؛ فقط ورودیِ ناقص یا نامعتبر رد می‌شود.</summary>
    public static string? Validate(OfflineOperationType type, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return "محتوای عملیات باید یک شیء JSON باشد.";
        }

        foreach (var field in type.Fields)
        {
            if (!payload.TryGetProperty(field.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return $"فیلد «{field.Name}» لازم است.";
            }

            var valid = field.Kind switch
            {
                OfflineFieldKind.ReferenceId => IsOperationReference(value)
                    || (TryReadDecimal(value, out var id) && id > 0 && id == decimal.Truncate(id) && id <= int.MaxValue),
                OfflineFieldKind.Positive => TryReadDecimal(value, out var number) && number > 0,
                OfflineFieldKind.Text => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
                _ => false
            };

            if (!valid)
            {
                return $"مقدار فیلد «{field.Name}» معتبر نیست.";
            }
        }

        return null;
    }

    /// <summary><c>op:{guid}</c> — رکوردی که عملیات پیش‌نیاز (مثلاً مشتری تازه) پس از ثبت قطعی می‌سازد.</summary>
    private static bool IsOperationReference(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && text.StartsWith(OfflineOperationApplier.OperationReferencePrefix, StringComparison.Ordinal)
            && Guid.TryParse(text[OfflineOperationApplier.OperationReferencePrefix.Length..], out _);

    private static bool TryReadDecimal(JsonElement value, out decimal number)
    {
        number = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number),
            _ => false
        };
    }
}
