using System.Globalization;
using System.Security.Claims;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Services.OfflineSync;

/// <summary>شیوهٔ ثبت قطعیِ یک فورم آفلاین هنگام همگام‌سازی (ضمانت «یک‌بار و فقط یک‌بار»).</summary>
public enum OfflineApplyMode
{
    /// <summary>اکشن توکن ضد تکرار (<c>__FormToken</c>) را خودش در تراکنش سند ثبت می‌کند.</summary>
    Token,

    /// <summary>اکشن بدون تراکنش و توکن: سند، توکن و وضعیت عملیات در یک تراکنش بیرونی.</summary>
    Wrapped,

    /// <summary>
    /// ویرایش/لغو/حذف/تأیید و سایر اکشن‌ها: توکن در نخستین SaveChangesِ همان اکشن ثبت می‌شود — داخل تراکنش خودِ
    /// اکشن، یا اگر اکشن تراکنش ندارد داخل تراکنشی که با نخستین ذخیره باز و فقط با موفقیت اکشن Commit می‌شود
    /// (<see cref="OfflineApplyScope"/>). اجرای آنلاین این اکشن‌ها دست نمی‌خورد.
    /// </summary>
    Ambient
}

/// <summary>
/// یک فورم اصلی MVC که می‌تواند بدون سرور در دستگاه ذخیره و بعداً با همان POST ثبت قطعی شود.
/// <see cref="ReferenceType"/>: موجودیتی که اکشن می‌سازد یا تغییر می‌دهد (شمارهٔ سند، ارجاع <c>op:</c> و بررسی کهنگی).
/// <see cref="RouteId"/>: آدرس فورم شناسهٔ رکورد را در انتها دارد (<c>/contracts/cancel/12</c>).
/// <see cref="CheckStale"/>: اگر رکورد پس از نسخه‌ای که کاربر دیده به‌دست کاربر دیگری تغییر کرده باشد، اجرا نمی‌شود.
/// </summary>
public sealed record OfflineFormTarget(
    string Path,
    string Controller,
    string Action,
    string Label,
    bool UsesFormToken,
    string ReferenceType,
    OfflineApplyMode Mode = OfflineApplyMode.Wrapped,
    bool RouteId = false,
    bool CheckStale = false)
{
    public string? NavigationKey => RoleAccessRules.NavigationKeyForController(Controller);

    /// <summary>
    /// فورم‌های ثبت تازهٔ Token/Wrapped: دستگاه ارسال آنلاین را هم خودش انجام می‌دهد (پاسخ نامعلوم با توکن صفحه).
    /// Ambient: ارسال آنلاین همان مسیر عادی صفحه است و دستگاه فقط وقتی سرور در دسترس نیست درخواست را نگه می‌دارد.
    /// </summary>
    public bool HandledOnlineByDevice => Mode != OfflineApplyMode.Ambient;

    /// <summary>الگوی آدرس برای دستگاه: <c>/contracts/cancel/{id}</c> یا خود آدرس.</summary>
    public string Pattern => RouteId ? Path + "/{id}" : Path;
}

/// <summary>یک اکشن POST که عمداً فقط آنلاین است، با دلیل فنی/امنیتی.</summary>
public sealed record OnlineOnlyAction(string Controller, string Action, string Reason);

/// <summary>
/// فهرست بستهٔ فورم‌های اصلی که آفلاین ثبت می‌شوند، و فهرست بستهٔ اکشن‌های فقط‌آنلاین با دلیل. هر اکشن POSTِ MVC
/// دقیقاً در یکی از این دو است (<c>OfflineFormRegistryCoverageTests</c>). منطق تجاری اینجا نیست: هنگام همگام‌سازی همان
/// اکشن با همان فیلترها، قواعد، Version و صلاحیت اجرا می‌شود؛ تا تأیید سرور هیچ عملیاتی قطعی نیست.
/// </summary>
public static class OfflineFormRegistry
{
    private static OfflineFormTarget Token(string controller, string action, string label, string referenceType)
        => new($"/{controller}/{action}".ToLowerInvariant(), controller, action, label, true, referenceType, OfflineApplyMode.Token);

    private static OfflineFormTarget Wrapped(string controller, string action, string label, string referenceType)
        => new($"/{controller}/{action}".ToLowerInvariant(), controller, action, label, false, referenceType, OfflineApplyMode.Wrapped);

    /// <summary>عملیات روی رکورد موجود؛ شناسه در آدرس؛ کهنگی نسخه سنجیده می‌شود.</summary>
    private static OfflineFormTarget OnRecord(string controller, string action, string label, string referenceType, bool checkStale = true)
        => new($"/{controller}/{action}".ToLowerInvariant(), controller, action, label, false, referenceType,
            OfflineApplyMode.Ambient, RouteId: true, CheckStale: checkStale);

    /// <summary>عملیات بدون شناسه در آدرس (شناسه‌ها در فیلدهای فورم) یا ثبت تازهٔ بدون توکن.</summary>
    private static OfflineFormTarget Act(string controller, string action, string label, string referenceType = "", bool checkStale = false)
        => new($"/{controller}/{action}".ToLowerInvariant(), controller, action, label, false, referenceType,
            OfflineApplyMode.Ambient, RouteId: false, CheckStale: checkStale);

    public static readonly IReadOnlyList<OfflineFormTarget> All =
    [
        // ---------------- ثبت‌های تازه (فورم‌های اصلی ثبت؛ صفحهٔ GET آن‌ها برای کار بدون سرور آماده می‌شود)
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
        Token("CommodityLoans", "Create", "قرض تیل", "CommodityLoan"),
        Token("CommodityLoans", "Settle", "برگشت قرض تیل", "CommodityLoan"),

        // دارایی‌های عملیاتی: ثبت‌های عملیاتیِ بدون اثر مالی (سند مصرفِ موجود فقط ارجاع می‌شود)
        Wrapped("OperationalAssets", "AddMeterReading", "ثبت کارکرد موتر/دستگاه", "AssetMeterReading"),
        Wrapped("OperationalAssets", "AddMaintenanceJob", "ثبت سرویس و ترمیم", "AssetMaintenanceJob"),

        // منابع بشری (درخواست‌ها)
        Wrapped("Leave", "Create", "درخواست رخصتی", "LeaveRequest"),

        // ---------------- عملیات روی اسناد و رکوردهای موجود + ثبت‌های بدون توکن (حالت Ambient)
        // قرارداد
        OnRecord("Contracts", "Edit", "ویرایش قرارداد", "Contract"),
        OnRecord("Contracts", "EditPricing", "اصلاح قیمت‌گذاری قرارداد", "Contract"),
        OnRecord("Contracts", "RepricePurchaseLoadings", "اصلاح قیمت بارگیری‌های قرارداد", "Contract"),
        OnRecord("Contracts", "Cancel", "لغو قرارداد", "Contract"),
        OnRecord("Contracts", "Close", "بستن قرارداد", "Contract"),
        OnRecord("Contracts", "Reopen", "بازکردن دوبارهٔ قرارداد", "Contract"),
        OnRecord("Contracts", "Delete", "حذف قرارداد", "Contract"),
        OnRecord("Contracts", "DeleteCancelled", "حذف قرارداد لغوشده", "Contract"),
        Act("ShipmentContracts", "Create", "قرارداد حمل کشتی", "ShipmentContract"),
        OnRecord("ShipmentContracts", "Delete", "حذف قرارداد حمل کشتی", "ShipmentContract"),
        Act("Shipments", "Create", "کشتی/محموله جدید", "Shipment"),
        Act("Shipments", "Edit", "ویرایش کشتی/محموله", "Shipment", checkStale: true),
        Act("ShipmentPnl", "RegisterDirectLoss", "ثبت ضایعات مستقیم محموله", "LossEvent"),

        // بارگیری و رسید
        OnRecord("Loading", "Edit", "ویرایش بارگیری", "LoadingRegister"),
        OnRecord("Loading", "EditPrice", "اصلاح قیمت بارگیری", "LoadingRegister"),
        OnRecord("Loading", "SetRubleRate", "نرخ روبل بارگیری", "LoadingRegister"),
        OnRecord("Loading", "EditExpenses", "مصارف بارگیری", "LoadingRegister"),
        OnRecord("Loading", "Cancel", "لغو بارگیری", "LoadingRegister"),
        OnRecord("Loading", "DeleteCancelled", "حذف بارگیری لغوشده", "LoadingRegister"),
        Act("Loading", "BulkDelete", "حذف گروهی بارگیری‌ها", "LoadingRegister"),
        OnRecord("LoadingReceipts", "Edit", "ویرایش رسید بار", "LoadingReceipt"),
        OnRecord("LoadingReceipts", "Cancel", "لغو رسید بار", "LoadingReceipt"),
        OnRecord("LoadingReceipts", "DeleteCancelled", "حذف رسید لغوشده", "LoadingReceipt"),
        Act("LoadingReceipts", "BulkCancel", "لغو گروهی رسیدها", "LoadingReceipt"),

        // حمل
        OnRecord("InventoryTransportLegs", "Edit", "ویرایش حمل", "InventoryTransportLeg"),
        OnRecord("InventoryTransportLegs", "EditBatch", "ویرایش حمل گروهی", "InventoryTransportBatch"),
        OnRecord("InventoryTransportLegs", "MarkLoaded", "ثبت بارشدن حمل", "InventoryTransportLeg"),
        Act("InventoryTransportLegs", "MarkGroupLoaded", "ثبت بارشدن گروهی", "InventoryTransportLeg"),
        OnRecord("InventoryTransportLegs", "CancelBatch", "لغو حمل گروهی", "InventoryTransportBatch"),
        OnRecord("InventoryTransportLegs", "CancelGroupTransfer", "لغو انتقال گروهی", "InventoryTransportBatch", checkStale: false),
        OnRecord("InventoryTransportLegs", "DeleteCancelled", "حذف حمل لغوشده", "InventoryTransportLeg"),
        Act("InventoryTransportLegs", "GroupTransfer", "انتقال گروهی", "InventoryTransportLeg"),
        Act("InventoryTransportLegs", "CreateGroupExpense", "مصرف گروهی حمل", "ExpenseTransaction"),
        Act("InventoryTransportLegs", "SaveGroupExpenses", "مصارف حمل گروهی", "ExpenseTransaction"),
        Act("Transports", "CancelContinued", "لغو ادامهٔ حمل", "InventoryTransportLeg"),
        Act("Transports", "CancelFromLoading", "لغو حمل از بارگیری", "InventoryTransportLeg"),
        Act("TruckSettlements", "Settle", "تسویهٔ گروهی موتر/واگن", "LoadingReceipt"),

        // گمرک و کیفیت
        Act("CustomsDeclarations", "Edit", "ویرایش اظهارنامهٔ گمرکی", "CustomsDeclaration", checkStale: true),
        OnRecord("CustomsDeclarations", "Delete", "حذف اظهارنامهٔ گمرکی", "CustomsDeclaration"),
        Act("CustomsDeclarations", "DeleteAll", "حذف اظهارنامه‌های یک سند", "CustomsDeclaration"),
        OnRecord("CustomsDeclarations", "DeleteDocument", "حذف ضمیمهٔ گمرکی", "CustomsDeclarationDocument", checkStale: false),
        Act("QualityInspections", "Edit", "ویرایش بررسی کیفیت", "QualityInspection", checkStale: true),

        // موجودی، مخزن، توزیع، ضایعات
        Token("Inventory", "Create", "سند موجودی", "InventoryMovement"),
        OnRecord("Dispatch", "Edit", "ویرایش توزیع", "TruckDispatch"),
        OnRecord("Dispatch", "Unload", "تخلیهٔ توزیع", "TruckDispatch"),
        OnRecord("Dispatch", "Cancel", "لغو توزیع", "TruckDispatch"),
        Act("Dispatch", "SaveExpenses", "مصارف توزیع", "TruckDispatch"),
        Act("Dispatch", "CreateSaleFromDirectDispatch", "فروش از توزیع مستقیم", "SalesTransaction"),
        OnRecord("LossEvents", "Edit", "ویرایش ضایعات", "LossEvent"),
        OnRecord("LossEvents", "Cancel", "لغو ضایعات", "LossEvent"),
        Act("StorageTanks", "Create", "مخزن جدید", "StorageTank"),
        OnRecord("StorageTanks", "Edit", "ویرایش مخزن", "StorageTank"),
        OnRecord("StorageTanks", "Delete", "حذف مخزن", "StorageTank"),
        Act("StorageTanks", "SettleFinal", "تسویهٔ نهایی مخزن", "StorageTank"),
        Act("Terminals", "Create", "ترمینال جدید", "Terminal"),
        OnRecord("Terminals", "Edit", "ویرایش ترمینال", "Terminal"),
        OnRecord("Terminals", "Delete", "حذف ترمینال", "Terminal"),

        // فروش، پیش‌فروش، تخصیص دریافت
        OnRecord("Sales", "Edit", "ویرایش فروش", "SalesTransaction"),
        OnRecord("Sales", "Cancel", "لغو/برگشت فروش", "SalesTransaction"),
        OnRecord("Sales", "CancelGroup", "لغو فروش گروهی", "SalesBatch"),
        Act("Sales", "CreateFromShipment", "فروش از محموله", "SalesTransaction"),
        Act("Sales", "ApplyReceipt", "تخصیص دریافت به فروش", "CustomerPaymentAllocationApplication"),
        Act("Sales", "ReverseReceiptApplication", "برگشت تخصیص دریافت", "CustomerPaymentAllocationApplication"),
        OnRecord("Sales", "PreSaleAllocatePayment", "تخصیص پرداخت پیش‌فروش", "PreSaleOrder", checkStale: false),
        OnRecord("Sales", "PreSaleReverseAllocation", "برگشت تخصیص پیش‌فروش", "PreSaleOrder", checkStale: false),
        Act("Sales", "PreSaleDeliver", "تحویل پیش‌فروش", "PreSaleOrder"),
        OnRecord("Sales", "PreSaleDeliveryCancel", "لغو تحویل پیش‌فروش", "PreSaleOrder", checkStale: false),
        OnRecord("Sales", "PreSaleCancel", "لغو پیش‌فروش", "PreSaleOrder"),
        OnRecord("Sales", "PreSaleClose", "بستن پیش‌فروش", "PreSaleOrder"),

        // روزنامچه، صندوق، بانک، صرافی، تسویه‌ها
        OnRecord("Payments", "Edit", "ویرایش دریافت/پرداخت", "PaymentTransaction"),
        OnRecord("Payments", "EditSarrafHawala", "ویرایش حوالهٔ صراف", "PaymentTransaction"),
        Act("Payments", "AllocateToContract", "تخصیص پرداخت به قرارداد", "SupplierPaymentAllocation"),
        Act("Payments", "ReverseAllocation", "برگشت تخصیص پرداخت", "SupplierPaymentAllocation"),
        OnRecord("Expenses", "Edit", "ویرایش مصرف", "ExpenseTransaction"),
        OnRecord("Expenses", "Cancel", "لغو مصرف", "ExpenseTransaction"),
        OnRecord("Expenses", "CancelGroup", "لغو مصرف گروهی", "ExpenseBatch"),
        Act("Expenses", "CancelAllGroups", "لغو همهٔ مصارف گروهی", "ExpenseBatch"),
        Act("Expenses", "CustomsBatch", "مصارف گمرکی گروهی", "ExpenseTransaction"),
        Act("ExpenseRules", "Create", "قاعدهٔ مصرف جدید", "ExpenseRule"),
        OnRecord("ExpenseRules", "Edit", "ویرایش قاعدهٔ مصرف", "ExpenseRule"),
        OnRecord("ExpenseRules", "GenerateExpense", "ساخت مصرف از قاعده", "ExpenseRule", checkStale: false),
        Act("ExpenseTypes", "Create", "نوع مصرف جدید", "ExpenseType"),
        OnRecord("ExpenseTypes", "Edit", "ویرایش نوع مصرف", "ExpenseType"),
        OnRecord("ExpenseTypes", "Delete", "حذف نوع مصرف", "ExpenseType"),
        OnRecord("CustomerToCustomerPayments", "Edit", "ویرایش پرداخت مشتری به مشتری", "CustomerToCustomerPayment"),
        OnRecord("CustomerToCustomerPayments", "Delete", "حذف پرداخت مشتری به مشتری", "CustomerToCustomerPayment"),
        OnRecord("PartySettlements", "Edit", "ویرایش تسویهٔ طرف‌حساب", "PartySettlement"),
        OnRecord("PartySettlements", "Cancel", "لغو تسویهٔ طرف‌حساب", "PartySettlement"),
        OnRecord("SupplierBalanceTransfers", "Reverse", "برگشت انتقال مانده فروشنده", "SupplierBalanceTransfer"),
        OnRecord("SarrafSettlements", "Cancel", "لغو تسویهٔ صراف", "SarrafSettlement"),
        OnRecord("ThreeWaySettlement", "Cancel", "لغو تسویهٔ سه‌جانبه", "ThreeWaySettlement"),
        Act("ThreeWaySettlement", "Confirm", "تأیید تسویهٔ سه‌جانبه", "ThreeWaySettlement"),
        Act("ViaSarrafPayments", "AssignContract", "تعیین قرارداد پرداخت صرافی", "PaymentTransaction"),
        Act("ViaSarrafPayments", "ConfirmPair", "تأیید جفت پرداخت صرافی", "PaymentTransaction"),
        Act("ViaSarrafPayments", "ApplyConfirmed", "اعمال پرداخت‌های صرافیِ تأییدشده", "PaymentTransaction"),
        Act("AccountStatements", "Create", "سند صورت‌حساب", "LedgerEntry"),
        Act("CashAccounts", "Create", "صندوق/حساب بانکی جدید", "CashAccount"),
        OnRecord("CashAccounts", "Edit", "ویرایش صندوق/حساب بانکی", "CashAccount"),
        OnRecord("CashAccounts", "ToggleActive", "فعال/غیرفعال صندوق", "CashAccount"),
        OnRecord("Sarrafs", "Edit", "ویرایش صراف", "Sarraf"),
        OnRecord("Sarrafs", "ToggleActive", "فعال/غیرفعال صراف", "Sarraf"),

        // شرکا و نفت امانتی و قرض جنسی
        OnRecord("PartnershipStatement", "ReverseSettlement", "برگشت تسویهٔ شرکا", "PartnerSettlement"),
        Act("PartnershipStatement", "SetProceedsHolder", "تعیین نگهدارندهٔ عواید", "Contract"),
        OnRecord("Partners", "Edit", "ویرایش شریک", "Partner"),
        OnRecord("Partners", "ToggleActive", "فعال/غیرفعال شریک", "Partner"),
        OnRecord("Partners", "Delete", "حذف شریک", "Partner"),
        OnRecord("Custody", "CancelSale", "لغو فروش امانی", "CustodySale", checkStale: false),
        OnRecord("Custody", "CancelReturn", "لغو برگشت امانی", "CustodySaleReturn", checkStale: false),
        OnRecord("Custody", "CancelFinance", "لغو تمویل امانی", "CustodyFinanceEntry", checkStale: false),
        OnRecord("Custody", "CancelConversion", "لغو تبدیل امانی", "CustodyConversion", checkStale: false),
        OnRecord("Custody", "Reverse", "برگشت عملیات امانی", "CustodyMovement", checkStale: false),
        OnRecord("Custody", "ReleaseHold", "آزادسازی امانت توقیف‌شده", "CustodyHold", checkStale: false),
        OnRecord("Custody", "TransitResponsibility", "مسئولیت کسر در راه امانی", "CustodyLot", checkStale: false),
        Act("Custody", "FxAgreement", "توافق اسعار امانی", "CustodyLot"),
        Act("Custody", "Reconcile", "تطبیق امانی", "CustodyLot"),
        OnRecord("CommodityLoans", "Cancel", "لغو قرض تیل", "CommodityLoan"),
        OnRecord("CommodityLoans", "CancelSettlement", "لغو برگشت قرض تیل", "CommodityLoanSettlement", checkStale: false),

        // طرف‌حساب‌ها و معلومات پایه
        OnRecord("Customers", "Edit", "ویرایش مشتری", "Customer"),
        OnRecord("Customers", "ToggleActive", "فعال/غیرفعال مشتری", "Customer"),
        OnRecord("Customers", "Delete", "حذف مشتری", "Customer"),
        OnRecord("Suppliers", "Edit", "ویرایش فروشنده", "Supplier"),
        OnRecord("Suppliers", "ToggleActive", "فعال/غیرفعال فروشنده", "Supplier"),
        OnRecord("Suppliers", "Delete", "حذف فروشنده", "Supplier"),
        OnRecord("Suppliers", "RecognizeFxDifference", "شناسایی تفاوت اسعار فروشنده", "Supplier"),
        OnRecord("ServiceProviders", "Edit", "ویرایش شرکت خدماتی", "ServiceProviderEntity"),
        OnRecord("ServiceProviders", "ToggleActive", "فعال/غیرفعال شرکت خدماتی", "ServiceProviderEntity"),
        OnRecord("Trucks", "Edit", "ویرایش موتر", "Truck"),
        OnRecord("Trucks", "ToggleActive", "فعال/غیرفعال موتر", "Truck"),
        OnRecord("Drivers", "Edit", "ویرایش راننده", "Driver"),
        OnRecord("Drivers", "ToggleActive", "فعال/غیرفعال راننده", "Driver"),
        OnRecord("Wagons", "Edit", "ویرایش واگن", "Wagon"),
        OnRecord("Wagons", "ToggleActive", "فعال/غیرفعال واگن", "Wagon"),
        OnRecord("Vessels", "Edit", "ویرایش کشتی", "Vessel"),
        OnRecord("Vessels", "ToggleActive", "فعال/غیرفعال کشتی", "Vessel"),
        OnRecord("Locations", "Edit", "ویرایش موقعیت", "Location"),
        OnRecord("Locations", "Delete", "حذف موقعیت", "Location"),
        OnRecord("DailyFxRates", "Edit", "ویرایش نرخ روزانه", "DailyFxRate"),
        OnRecord("DailyFxRates", "Delete", "حذف نرخ روزانه", "DailyFxRate"),
        Act("PlattsRates", "SaveDaily", "نرخ روزانهٔ پلاتس", "DailyPlattsPrice"),
        Act("PlattsRates", "SaveMonthlyManual", "نرخ ماهانهٔ پلاتس", "PlattsMonthlyManual"),
        OnRecord("PlattsRates", "DeleteDaily", "حذف نرخ روزانهٔ پلاتس", "DailyPlattsPrice"),
        OnRecord("PlattsRates", "DeleteMonthlyManual", "حذف نرخ ماهانهٔ پلاتس", "PlattsMonthlyManual"),
        Act("Products", "Create", "محصول جدید", "Product"),
        OnRecord("Products", "Edit", "ویرایش محصول", "Product"),
        OnRecord("Products", "Delete", "حذف محصول", "Product"),
        Act("Units", "Create", "واحد جدید", "Unit"),
        OnRecord("Units", "Edit", "ویرایش واحد", "Unit"),
        OnRecord("Units", "Delete", "حذف واحد", "Unit"),
        Act("Currencies", "Create", "اسعار جدید", "Currency"),
        OnRecord("Currencies", "Edit", "ویرایش اسعار", "Currency"),
        OnRecord("Currencies", "Delete", "حذف اسعار", "Currency"),
        Act("Companies", "Create", "شرکت جدید", "Company"),
        OnRecord("Companies", "Edit", "ویرایش شرکت", "Company"),
        OnRecord("Companies", "ToggleActive", "فعال/غیرفعال شرکت", "Company"),
        OnRecord("Companies", "SetSystemOwner", "تعیین شرکت مالک سیستم", "Company"),
        OnRecord("Companies", "Delete", "حذف شرکت", "Company"),

        // حسابداری و سال مالی
        Act("ChartOfAccounts", "Create", "سرفصل حساب جدید", "Account"),
        OnRecord("ChartOfAccounts", "Edit", "ویرایش سرفصل حساب", "Account"),
        Act("ChartOfAccounts", "Mapping", "حساب‌های خودکار", "AccountingSettings"),
        Act("FiscalYears", "CreateInitialYear", "سال مالی نخست", "FiscalYear"),
        Act("FiscalYears", "CreateNextYear", "سال مالی بعدی", "FiscalYear"),
        OnRecord("FiscalYears", "ChangePeriodLock", "قفل دورهٔ مالی", "FiscalYear", checkStale: false),
        Act("OperationalPeriodLocks", "Close", "بستن دورهٔ عملیاتی", "OperationalPeriodLock"),
        Act("OperationalPeriodLocks", "Reopen", "بازکردن دورهٔ عملیاتی", "OperationalPeriodLock"),
        Act("TrialClose", "RunTrialClose", "بستن آزمایشی سال", "FiscalYearCloseRun"),
        Act("TrialClose", "ApplyRevaluation", "تسعیر اسعار پایان سال", "FiscalYearCloseRun"),
        Act("FinalClose", "Close", "بستن نهایی سال مالی", "FiscalYear"),

        // دارایی‌های عملیاتی
        Act("OperationalAssets", "Create", "دارایی عملیاتی جدید", "OperationalAsset"),
        OnRecord("OperationalAssets", "Edit", "ویرایش دارایی عملیاتی", "OperationalAsset"),
        OnRecord("OperationalAssets", "ToggleActive", "فعال/غیرفعال دارایی", "OperationalAsset"),
        Act("OperationalAssets", "AddAssignment", "واگذاری دارایی", "AssetAssignment"),
        Act("OperationalAssets", "AddOwnershipShare", "سهم مالکیت دارایی", "AssetOwnershipShare"),
        Act("OperationalAssets", "CreateRent", "کرایهٔ دارایی", "AssetRentTransaction"),
        OnRecord("OperationalAssets", "CancelRent", "لغو کرایهٔ دارایی", "AssetRentTransaction"),

        // منابع بشری و معاشات
        Act("Employees", "Create", "کارمند جدید", "Employee"),
        OnRecord("Employees", "Edit", "ویرایش کارمند", "Employee"),
        OnRecord("Employees", "ToggleActive", "فعال/غیرفعال کارمند", "Employee"),
        OnRecord("Employees", "ChangeSalary", "تغییر معاش", "Employee"),
        OnRecord("Employees", "Offboard", "ختم کار کارمند", "Employee"),
        OnRecord("Employees", "CreateSalaryTransaction", "پرداخت/کسر معاش", "Employee", checkStale: false),
        Act("Employees", "CancelSalaryTransaction", "لغو تراکنش معاش", "EmployeeSalaryTransaction"),
        OnRecord("Employees", "DeleteDocument", "حذف سند کارمند", "Employee", checkStale: false),
        Act("EmploymentContracts", "Create", "قرارداد کاری", "EmploymentContract"),
        OnRecord("EmploymentContracts", "Renew", "تمدید قرارداد کاری", "EmploymentContract"),
        OnRecord("EmploymentContracts", "Terminate", "فسخ قرارداد کاری", "EmploymentContract"),
        OnRecord("EmploymentContracts", "UpdateNotes", "یادداشت قرارداد کاری", "EmploymentContract"),
        Act("EmployeeLoans", "Create", "قرضهٔ کارمند", "EmployeeLoan"),
        OnRecord("EmployeeLoans", "Repay", "بازپرداخت قرضهٔ کارمند", "EmployeeLoan", checkStale: false),
        OnRecord("EmployeeLoans", "Cancel", "لغو قرضهٔ کارمند", "EmployeeLoan"),
        Act("Attendance", "Save", "حاضری روزانه", "DailyAttendance"),
        Act("Payroll", "Generate", "ساخت معاشات ماه", "PayrollRun"),
        Act("Payroll", "SaveLines", "اصلاح سطرهای معاش", "PayrollRun"),
        Act("Payroll", "Finalize", "نهایی‌کردن معاشات", "PayrollRun"),
        Act("Payroll", "PayLines", "پرداخت معاشات", "PayrollRun"),
        Act("Payroll", "Reopen", "بازکردن معاشات", "PayrollRun"),
        OnRecord("Leave", "Approve", "تأیید رخصتی", "LeaveRequest"),
        OnRecord("Leave", "Reject", "رد رخصتی", "LeaveRequest"),
        OnRecord("Leave", "Cancel", "لغو رخصتی", "LeaveRequest"),
        Act("HrOrganization", "EditDepartment", "دیپارتمنت", "Department"),
        Act("HrOrganization", "EditPosition", "بست کاری", "Position"),
        Act("HrOrganization", "LinkLegacyText", "پیوند دیپارتمنت/بست قدیمی", "Employee"),
        Act("HrSettings", "SaveSettings", "تنظیمات منابع بشری", "HrSettings"),
        Act("HrSettings", "AddHoliday", "رخصتی عمومی", "HrHoliday"),
        OnRecord("HrSettings", "RemoveHoliday", "حذف رخصتی عمومی", "HrHoliday", checkStale: false),
        OnRecord("HrSettings", "SaveLeaveType", "نوع رخصتی", "LeaveType")
    ];

    /// <summary>
    /// اکشن‌های POST که عمداً بدون سرور ثبت نمی‌شوند. فورم آن‌ها در حالت قطع سرور معلومات را روی صفحه نگه می‌دارد و
    /// پیام می‌دهد (form-bridge)؛ دلیل هر کدام همین‌جا ثبت است.
    /// </summary>
    public static readonly IReadOnlyList<OnlineOnlyAction> OnlineOnly =
    [
        new("Auth", "Login", Reasons.Authentication),
        new("Auth", "Logout", "خروج روی دستگاه بی‌درنگ انجام می‌شود (کلید نشست محلی پاک می‌شود)؛ کوکی سرور با اتصال بعدی پایان می‌یابد."),
        new("Auth", "ChangePassword", Reasons.Credential),
        new("Users", "Create", Reasons.Credential),
        new("Users", "ResetPassword", Reasons.Credential),
        new("Users", "Edit", Reasons.Security),
        new("Users", "Delete", Reasons.Security),
        new("Roles", "Create", Reasons.Security),
        new("Roles", "Edit", Reasons.Security),
        new("SystemSettings", "Save", Reasons.Security),
        new("Backups", "CreateNow", Reasons.ServerSystem),
        new("Backups", "Delete", Reasons.ServerSystem),
        new("Backups", "SaveSettings", Reasons.ServerSystem),
        new("Backups", "ConnectDrive", Reasons.ServerSystem),
        new("Backups", "DisconnectDrive", Reasons.ServerSystem),
        new("Backups", "RetryDriveUpload", Reasons.ServerSystem),
        new("BackupRestore", "Restore", Reasons.ServerSystem),
        new("Maintenance", "BackfillCanonicalSearchKeys", Reasons.ServerSystem),
        new("Maintenance", "BackfillDispatchFreightExpenses", Reasons.ServerSystem),
        new("Maintenance", "BackfillSupplierUsdLoadingLedger", Reasons.ServerSystem),
        new("Maintenance", "ClearDataExceptUsers", Reasons.ServerSystem),
        new("InventoryLineage", "Backfill", Reasons.ServerSystem),
        new("OfflineSync", "Review", "بررسی عملیات آفلاینِ دیگران خودش تصمیم نهایی سرور است و فقط آنلاین معنا دارد."),
        new("FormDrafts", "Save", "پیش‌نویس سرور؛ بدون سرور، معلومات فورم در خود دستگاه (پیش‌نویس محلی/Outbox) می‌ماند."),
        new("FormDrafts", "Delete", "پیش‌نویس سرور؛ بدون سرور، معلومات فورم در خود دستگاه (پیش‌نویس محلی/Outbox) می‌ماند."),
        new("FiscalYearContext", "Select", "انتخاب سال مالیِ نمایش (حالت نشست سرور)، نه ثبت معلومات؛ صفحات ذخیره‌شده همان سال جاری‌اند."),
        new("Assistant", "Ask", "پاسخ دستیار هوشمند را فقط سرور (هوش مصنوعی) همان لحظه می‌سازد."),
        new("Transports", "Create", Reasons.Router),
        new("InventoryTransportLegs", "CreateBatch", Reasons.Router),
        new("InventoryTransportLegs", "CreateForShipment", Reasons.Router),
        new("ContractBalanceTransfers", "Create", Reasons.Disabled),
        new("SarrafSettlements", "Create", Reasons.Disabled),
        new("InventoryTransportLegs", "Create", Reasons.Disabled),
        new("CustomsDeclarations", "ImportExcelPreview", Reasons.FileImport),
        new("CustomsDeclarations", "ImportExcel", Reasons.FileImport),
        new("Expenses", "ImportPreview", Reasons.FileImport),
        new("Expenses", "ImportConfirm", Reasons.FileImport),
        new("Expenses", "ImportGroupAmountsExcel", Reasons.FileImport),
        new("InventoryTransportLegs", "ImportVehiclesExcel", Reasons.FileImport),
        new("Transports", "ImportContinueExcel", Reasons.FileImport),
        new("TruckSettlements", "ImportExcel", Reasons.FileImport),
        new("Loading", "ImportWorkbook", Reasons.FileImport),
        new("Transports", "BulkFromLoading", "تبدیل گروهی بارگیری‌ها به حمل سطربه‌سطر با توکن‌های مشتق پیش می‌رود و ثبت قطعیِ کل درخواست با یک توکن قابل تأیید نیست؛ حمل تکی از بارگیری آفلاین است."),
        new("ReopenFiscalYear", "Reopen", "بازگشایی سالِ بستهٔ نهایی فقط با Claim ویژه و عبارت تأیید است، در منوی هیچ نقشی نیست و اسناد بسته را برمی‌گرداند؛ فقط آنلاین."),
        new("ThreeWaySettlement", "Preview", "پیش‌نمایش تسویه را سرور از مانده‌های لحظه‌ای می‌سازد؛ «تأیید» همان پیش‌نمایش آفلاین هم ثبت می‌شود."),
        new("CustomsDeclarations", "UploadDocument", Reasons.Attachment),
        new("QualityInspections", "UploadDocument", Reasons.Attachment),
        new("Employees", "UploadDocument", Reasons.Attachment),
        new("OperationalAssets", "UploadDocument", Reasons.Attachment)
    ];

    private static class Reasons
    {
        public const string Authentication = "ورود باید با سرور سنجیده شود؛ بدون سرور، ورود محلی با رمز دستگاه انجام می‌شود.";
        public const string Credential = "رمز عبور هرگز روی دستگاه ذخیره نمی‌شود (حتی رمزگذاری‌شده)؛ تغییر رمز فقط آنلاین.";
        public const string Security = "مدیریت کاربران، نقش‌ها و تنظیمات امنیتی فقط آنلاین است و صفحات آن روی دستگاه ذخیره نمی‌شوند.";
        public const string ServerSystem = "عملیات خودِ سرور (بکاپ، بازیابی، نگهداری، بازسازی داده) است و بدون سرور معنا ندارد.";
        public const string Router = "فقط مسیریاب به فورم حمل از موجودی است (سندی نمی‌سازد)؛ خود آن فورم آفلاین است.";
        public const string Disabled = "فورم قدیمیِ غیرفعال است و فقط به فورم جدید هدایت می‌کند؛ فورم جدید آفلاین است.";
        public const string FileImport = "فایل Excel باید همان لحظه در سرور خوانده، پیش‌نمایش و اعتبارسنجی شود؛ ورود فایل فقط آنلاین.";
        public const string Attachment = "ضمیمهٔ فایل در صف آفلاین نگه داشته نمی‌شود؛ پس از اتصال ضمیمه کنید (معلومات خود سند آفلاین ثبت می‌شود).";
    }

    private static readonly Dictionary<string, OfflineFormTarget> ByPath =
        All.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);

    /// <summary>مسیر فورم (بدون querystring، بدون / پایانی) → فورم مجاز، یا null.</summary>
    public static OfflineFormTarget? Find(string? path) => Find(path, out _);

    /// <summary>همان؛ برای فورم روی رکورد (<see cref="OfflineFormTarget.RouteId"/>) شناسهٔ انتهای آدرس هم برمی‌گردد.</summary>
    public static OfflineFormTarget? Find(string? path, out int? routeId)
    {
        routeId = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Split('?', '#')[0].TrimEnd('/');
        if (ByPath.TryGetValue(normalized, out var target))
        {
            // فورم روی رکورد که شناسه را در فیلد مخفی (id) می‌فرستد، نه در آدرس.
            return target;
        }

        var slash = normalized.LastIndexOf('/');
        if (slash > 0
            && int.TryParse(normalized[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0
            && ByPath.TryGetValue(normalized[..slash], out var withId)
            && withId.RouteId)
        {
            routeId = id;
            return withId;
        }

        return null;
    }

    /// <summary>فورم‌هایی که این کاربر در وب می‌بیند (همان کلید ناوبری نقش).</summary>
    public static IReadOnlyList<OfflineFormTarget> AllowedFor(ClaimsPrincipal user, ClientModuleProfile? modules)
        => All.Where(t => t.NavigationKey is { } key && RoleAccessRules.CanAccessNavigation(user, key, modules)).ToList();
}
