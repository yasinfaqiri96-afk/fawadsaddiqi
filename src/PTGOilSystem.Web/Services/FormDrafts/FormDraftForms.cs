namespace PTGOilSystem.Web.Services.FormDrafts;

/// <summary>یک فرم ثبت که پیش‌نویس خودکار دارد.</summary>
/// <param name="Key">کلید پایدار فرم؛ در HTML و دیتابیس نوشته می‌شود.</param>
/// <param name="Title">نام فارسی فرم در فهرست پیش‌نویس‌ها.</param>
/// <param name="Controller">کنترلر فرم؛ دسترسی پیش‌نویس با همان کلید ناوبری همین کنترلر سنجیده می‌شود.</param>
/// <param name="Path">مسیر GET فرم (بدون query).</param>
public sealed record FormDraftForm(string Key, string Title, string Controller, string Path);

/// <summary>
/// فهرست بسته‌ی فرم‌هایی که پیش‌نویس خودکار دارند. فرم فقط وقتی اضافه می‌شود که GET آن بازیابی
/// پیش‌نویس را پشتیبانی کند و POST آن توکن فرم را در همان تراکنش سند ثبت کند (نشانهٔ ثبت موفق).
/// </summary>
public static class FormDraftForms
{
    public const string LoadingCreate = "Loading.Create";
    public const string ContractsCreate = "Contracts.Create";
    public const string SalesCreate = "Sales.Create";
    public const string PaymentsCreate = "Payments.Create";
    public const string ExpensesCreate = "Expenses.Create";

    public static readonly IReadOnlyList<FormDraftForm> All =
    [
        new(LoadingCreate, "ثبت بارگیری", "Loading", "/Loading/Create"),
        new(ContractsCreate, "ثبت قرارداد / خرید", "Contracts", "/Contracts/Create"),
        new(SalesCreate, "ثبت فاکتور فروش", "Sales", "/Sales/Create"),
        new(PaymentsCreate, "ثبت دریافت / پرداخت", "Payments", "/Payments/Create"),
        new(ExpensesCreate, "ثبت مصرف", "Expenses", "/Expenses/Create")
    ];

    public static FormDraftForm? Find(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(form => string.Equals(form.Key, key.Trim(), StringComparison.Ordinal));
}
