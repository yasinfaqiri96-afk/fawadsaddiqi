using System.Security.Claims;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Services.OfflineSync;

/// <summary>
/// یک فورم اصلی MVC که می‌تواند بدون سرور در دستگاه ذخیره و بعداً با همان POST ثبت قطعی شود.
/// <see cref="UsesFormToken"/>: اکشن توکن ضد تکرار (<c>__FormToken</c>) را در همان تراکنش سند ثبت می‌کند؛
/// غیر از آن، اکشن در یک تراکنش بیرونی همراه با توکن/وضعیت عملیات اجرا می‌شود (یا همه یا هیچ).
/// <see cref="ReferenceType"/>: موجودیتی که اکشن می‌سازد (شمارهٔ سند و ارجاع <c>op:</c> در فورم‌های بعدی).
/// </summary>
public sealed record OfflineFormTarget(
    string Path,
    string Controller,
    string Action,
    string Label,
    bool UsesFormToken,
    string ReferenceType)
{
    public string? NavigationKey => RoleAccessRules.NavigationKeyForController(Controller);
}

/// <summary>
/// فهرست بستهٔ فورم‌های اصلی که آفلاین ثبت می‌شوند. عمداً بیرون: POSTهای غیرفعال که فقط Redirect می‌کنند
/// (تسویهٔ صرافِ قدیمی، انتقال مانده میان قراردادها، مرحلهٔ حملِ قدیمی) و حمل گروهی از بارگیری (پیشرفت سطربه‌سطر با
/// توکن‌های مشتق؛ ثبت قطعیِ کل درخواست با یک توکن قابل تأیید نیست). هر فورم دیگری (ویرایش، حذف، لغو، برگشت، بستن دوره،
/// تسویه‌های نیازمند پیش‌نمایش، ایمپورت/فایل، مدیریت سیستم و کاربران، و اکشن‌های دارای تراکنش داخلی بدون توکن)
/// فقط آنلاین است؛ چون یا به وضعیت لحظه‌ای سرور وابسته است یا بدون توکن ضد تکرار قابل تکرارِ امن نیست.
/// منطق تجاری اینجا نیست: هنگام همگام‌سازی همان اکشن با همان فیلترها، قواعد و صلاحیت اجرا می‌شود.
/// </summary>
public static class OfflineFormRegistry
{
    private static OfflineFormTarget Token(string controller, string action, string label, string referenceType)
        => new($"/{controller}/{action}".ToLowerInvariant(), controller, action, label, true, referenceType);

    private static OfflineFormTarget Wrapped(string controller, string action, string label, string referenceType)
        => new($"/{controller}/{action}".ToLowerInvariant(), controller, action, label, false, referenceType);

    public static readonly IReadOnlyList<OfflineFormTarget> All =
    [
        // طرف‌حساب‌ها و معلومات پایهٔ عملیاتی
        Wrapped("Customers", "Create", "مشتری جدید", "Customer"),
        Wrapped("Suppliers", "Create", "فروشندهٔ جدید", "Supplier"),
        Wrapped("Partners", "Create", "شریک جدید", "Partner"),
        Wrapped("Sarrafs", "Create", "صراف جدید", "Sarraf"),
        Wrapped("ServiceProviders", "Create", "شرکت خدماتی جدید", "ServiceProvider"),
        Wrapped("Trucks", "Create", "موتر جدید", "Truck"),
        Wrapped("Drivers", "Create", "رانندهٔ جدید", "Driver"),
        Wrapped("Wagons", "Create", "واگن جدید", "Wagon"),
        Wrapped("Vessels", "Create", "کشتی جدید", "Vessel"),
        Wrapped("Locations", "Create", "موقعیت جدید", "Location"),
        Wrapped("DailyFxRates", "Create", "نرخ روزانهٔ اسعار", "DailyFxRate"),

        // قرارداد، بارگیری، حمل، گمرک، رسید
        Token("Contracts", "Create", "قرارداد جدید", "Contract"),
        Wrapped("ContractAmendments", "Create", "اصلاحیهٔ قرارداد", "ContractAmendment"),
        Token("Loading", "Create", "بارگیری", "LoadingRegister"),
        Token("LoadingReceipts", "Create", "رسید بار", "LoadingReceipt"),
        Token("LoadingReceipts", "BulkCreate", "رسید گروهی بار", "LoadingReceipt"),
        Token("Transports", "FromLoading", "حمل از بارگیری", "InventoryTransportLeg"),
        Token("Transports", "FromReceipt", "حمل از رسید", "InventoryTransportLeg"),
        Token("Transports", "Continue", "ادامهٔ حمل", "InventoryTransportLeg"),
        Token("Transports", "SettleFreight", "تسویهٔ کرایهٔ حمل", "InventoryTransportLeg"),
        Token("InventoryTransportLegs", "CreateFromInventory", "حمل از موجودی", "InventoryTransportLeg"),
        Token("InventoryTransportLegs", "CreateGroupReceipt", "رسید گروهی حمل", "InventoryTransportLeg"),
        Token("InventoryTransportLegs", "CreateGroupOperation", "عملیات گروهی حمل", "InventoryTransportLeg"),
        Token("InventoryTransportReceipts", "Create", "رسید حمل", "InventoryTransportLeg"),
        Token("TruckSettlements", "GroupUnload", "تخلیهٔ گروهی موترها", "LoadingReceipt"),
        Wrapped("CustomsDeclarations", "Create", "اظهارنامهٔ گمرکی", "CustomsDeclaration"),
        Wrapped("QualityInspections", "Create", "بررسی کیفیت", "QualityInspection"),

        // موجودی، توزیع و ضایعات
        Token("Dispatch", "Create", "توزیع از مخزن", "TruckDispatch"),
        Token("Dispatch", "CreateDirectFromReceipt", "توزیع مستقیم از رسید", "TruckDispatch"),
        Token("LossEvents", "Create", "ضایعات / کسر", "LossEvent"),

        // فروش و پیش‌فروش
        Token("Sales", "Create", "فروش", "SalesTransaction"),
        Token("Sales", "CreateGroup", "فروش گروهی", "SalesTransaction"),
        Token("Sales", "SetPrice", "تعیین قیمت فروش", "SalesTransaction"),
        Wrapped("Sales", "PreSaleCreate", "پیش‌فروش", "PreSaleOrder"),

        // روزنامچه، مصارف، تسویه‌ها
        Token("Payments", "Create", "دریافت / پرداخت", "PaymentTransaction"),
        Token("Expenses", "Create", "مصرف", "ExpenseTransaction"),
        Token("Expenses", "CreateGroup", "مصرف گروهی", "ExpenseTransaction"),
        Token("Expenses", "CreateWagonRent", "کرایهٔ واگن", "ExpenseTransaction"),
        Wrapped("CustomerToCustomerPayments", "Create", "پرداخت مشتری به مشتری", "CustomerToCustomerPayment"),
        Token("PartySettlements", "Create", "تسویهٔ طرف‌حساب", "PartySettlement"),
        Token("SupplierBalanceTransfers", "Create", "انتقال مانده فروشنده", "SupplierBalanceTransfer"),
        Token("PartnershipStatement", "CreateSettlement", "تسویهٔ شرکا", "PartnerSettlement"),

        // نفت امانتی و قرض جنسی
        Token("Custody", "Receive", "دریافت امانی", "CustodyMovement"),
        Token("Custody", "Operation", "عملیات امانی", "CustodyMovement"),
        Token("Custody", "Sale", "فروش امانی", "CustodyMovement"),
        Token("Custody", "SaleReturn", "برگشت فروش امانی", "CustodyMovement"),
        Token("Custody", "Finance", "تمویل امانی", "CustodyMovement"),
        Token("Custody", "ConvertPurchase", "تبدیل امانی به خرید", "CustodyMovement"),
        Token("CommodityLoans", "Create", "قرض جنسی", "CommodityLoan"),
        Token("CommodityLoans", "Settle", "تسویهٔ قرض جنسی", "CommodityLoan"),

        // دارایی‌های عملیاتی: ثبت‌های عملیاتیِ بدون اثر مالی (سند مصرفِ موجود فقط ارجاع می‌شود)
        Wrapped("OperationalAssets", "AddMeterReading", "ثبت کارکرد موتر/دستگاه", "AssetMeterReading"),
        Wrapped("OperationalAssets", "AddMaintenanceJob", "ثبت سرویس و ترمیم", "AssetMaintenanceJob"),

        // منابع بشری (درخواست‌ها)
        Wrapped("Leave", "Create", "درخواست رخصتی", "LeaveRequest")
    ];

    private static readonly Dictionary<string, OfflineFormTarget> ByPath =
        All.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);

    /// <summary>مسیر فورم (بدون querystring، بدون / پایانی) → فورم مجاز، یا null.</summary>
    public static OfflineFormTarget? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Split('?', '#')[0].TrimEnd('/');
        return ByPath.TryGetValue(normalized, out var target) ? target : null;
    }

    /// <summary>فورم‌هایی که این کاربر در وب می‌بیند (همان کلید ناوبری نقش).</summary>
    public static IReadOnlyList<OfflineFormTarget> AllowedFor(ClaimsPrincipal user, ClientModuleProfile? modules)
        => All.Where(t => t.NavigationKey is { } key && RoleAccessRules.CanAccessNavigation(user, key, modules)).ToList();
}
