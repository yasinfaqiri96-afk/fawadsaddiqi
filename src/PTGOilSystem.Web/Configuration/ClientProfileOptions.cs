namespace PTGOilSystem.Web.Configuration;

/// <summary>
/// Profile مخصوص هر Instance مشتری. پیش‌فرض (هیچ مقداری تنظیم نشده) یعنی سیستم کامل با رفتار فعلی:
/// هیچ ماژولی مخفی نمی‌شود و Simple Purchase خاموش است. Profile فقط UI/دسترسی را محدود می‌کند؛
/// هیچ Service یا Backend با آن خاموش نمی‌شود.
/// </summary>
public sealed class ClientProfileOptions
{
    public const string SectionName = "ClientProfile";

    /// <summary>
    /// نام Profile. اگر تنظیم شود، فایل <c>appsettings.{Name}.json</c> کنار برنامه بارگذاری می‌شود.
    /// </summary>
    public string? Name { get; set; }

    public bool SimplePurchaseEnabled { get; set; }

    // UI-only, opt-in per instance. Existing operational services remain enabled.
    public bool TruckOnlyEnabled { get; set; }

    /// <summary>کلیدهای ناوبری (<see cref="Security.RoleNavigationKeys"/>) که برای این مشتری کلاً مخفی‌اند.</summary>
    public string[] HiddenModules { get; set; } = [];

    /// <summary>نام Controllerهایی که برای این مشتری مخفی و از URL مستقیم هم بسته‌اند.</summary>
    public string[] HiddenControllers { get; set; } = [];

    /// <summary>
    /// قالب فاکتور فروش این Instance (مثلاً "Mashal"). خالی یعنی همان دو قالب پیش‌فرض.
    /// </summary>
    public string? InvoiceTemplate { get; set; }

    /// <summary>هشدار سرخ نسخهٔ آزمایشی که بالای همهٔ صفحات نمایش داده می‌شود؛ خالی یعنی خاموش.</summary>
    public string? TrialWarningMessage { get; set; }
}
