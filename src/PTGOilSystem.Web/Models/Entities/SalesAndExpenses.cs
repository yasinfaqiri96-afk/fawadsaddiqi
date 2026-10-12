using System;
using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

public enum SaleStage
{
    TerminalStock = 0,
    PreSale = 1,
    InTransit = 2,
    Border = 3,
    AfterCustoms = 4
}

// Phase 1 — مسئول هزینه/کرایه (فقط ثبت و نمایش، هیچ منطق حسابداری وابسته نیست).
public enum CostResponsibility
{
    [Display(Name = "بدوش خریدار")] Buyer = 1,
    [Display(Name = "بدوش فروشنده")] Seller = 2,
    [Display(Name = "مشترک")] Shared = 3,
    [Display(Name = "نامشخص")] Unspecified = 4
}

// Gap #4 — source of stock for dispatch/sale ticket tracking
public enum StockSourceType
{
    [Display(Name = "واگن")] Wagon = 1,
    [Display(Name = "انبار / مخزن")] Stock = 2,
    [Display(Name = "تانک")] Tank = 3
}

public class SalesTransaction : BaseEntity, IVersionedEntity
{
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    // جواز/شرکت داخلیِ قرارداد منبع. در فروشِ کلیِ محموله‌ای که قراردادهایش جواز متفاوت دارند،
    // هیچ جواز واحدی برای کل فروش وجود ندارد و این فیلد null می‌ماند؛ جواز هر سهم از قرارداد
    // خودش (ShipmentContracts → Contract.CompanyId) خوانده می‌شود.
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    // خریدار: دقیقاً یکی از مشتری یا تأمین‌کننده. فروش به تأمین‌کننده سطر لجرِ فروش را با
    // SupplierId می‌نویسد و از طلبِ تأمین‌کننده کم می‌کند (فروش همیشه «برد» است).
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int? DestinationLocationId { get; set; }
    public Location? DestinationLocation { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public SaleStage SaleStage { get; set; } = SaleStage.TerminalStock;

    // قرارداد خریدِ منبعِ بار (nullable، backward-compatible). یک محموله می‌تواند از چند قرارداد با
    // جواز/شرکت/تأمین‌کنندهٔ متفاوت تخصیص بگیرد؛ این فیلد مشخص می‌کند بارِ این فروش از کدام قرارداد
    // برداشته شده تا جواز و بهای تمام‌شده قابل ردیابی بماند. اگر null باشد (رکوردهای قدیمی یا محموله
    // تک‌قراردادیِ یکنواخت)، تخصیص وزنیِ قبلی بین قراردادهای محموله برقرار می‌ماند.
    public int? SourcePurchaseContractId { get; set; }
    public Contract? SourcePurchaseContract { get; set; }

    // موترِ منبعِ این فروش (nullable، backward-compatible). فروش مستقیم از موتر هیچ InventoryMovement
    // نمی‌سازد (بار هنگام بارگیریِ موتر قبلاً از موجودی خارج شده)، پس بدون این لینک هیچ Lineage واقعی
    // برای رساندنِ عاید به قرارداد خرید/محموله باقی نمی‌ماند. TruckDispatch.SalesTransactionId یکتاست و
    // فقط یک فروش را نگه می‌دارد؛ این فیلد فروشِ قسمتی و فروشِ باقی‌ماندهٔ همان موتر را ممکن می‌کند
    // بدون ساختن مسیر مالی موازی — هر فروش همان SalesTransaction عادی با Ledger خودش است.
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }

    [Required, MaxLength(50)] public string InvoiceNumber { get; set; } = "";
    // Gap #4 — ticket serial number for dispatch / sales tracking
    [MaxLength(100)] public string? TicketSerialNumber { get; set; }
    // Gap #4 — source of stock (wagon / stock / tank)
    public StockSourceType? StockSourceType { get; set; }
    public DateTime SaleDate { get; set; }
    public decimal QuantityMt { get; set; }
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal UnitPriceInCurrency { get; set; }
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal UnitPriceUsd { get; set; }
    public decimal TotalInCurrency { get; set; }
    public decimal TotalUsd { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    // ---- فروش با قیمت باز ----------------------------------------------------------------
    //
    // بار تحویل شده (موجودی کم شده) ولی قیمت فروش هنوز با مشتری قطعی نشده است. تا وقتی این
    // پرچم روشن است، ستون‌های قیمت/مبلغ بالا «عدد» نیستند و نباید خوانده شوند: هیچ سطر دفتر
    // (طلب مشتری/عاید)، هیچ ژورنال درآمد/بهای تمام‌شده و هیچ سود قطعی‌ای برای این فروش ساخته
    // نمی‌شود. قطعی‌سازی (SalesController.SetPrice) قیمت را می‌نشاند و همان مسیرهای همیشگیِ
    // ثبت فروش را یک‌بار اجرا می‌کند. «قیمت خرید» این‌جا ذخیره نمی‌شود: بهای خرید همیشه از
    // قرارداد خرید منبع (ContractPricingAdapter) خوانده می‌شود.

    /// <summary>قیمت فروش هنوز تعیین نشده است.</summary>
    public bool IsSalePricePending { get; set; }

    /// <summary>زمان (UTC) قطعی‌شدن قیمت فروشی که با قیمت باز ثبت شده بود.</summary>
    public DateTime? SalePriceFixedAtUtc { get; set; }

    /// <summary>کاربری که قیمت فروش را قطعی کرد.</summary>
    public int? SalePriceFixedByUserId { get; set; }

    /// <summary>
    /// تاریخ حسابداریِ اسناد مالیِ قیمت‌گذاری (سطر دفتر، ژورنال عاید و بهای تمام‌شده). اگر دورهٔ تاریخِ
    /// فروش باز باشد همان تاریخ فروش است؛ اگر بسته باشد، روزِ تعیین قیمت در دورهٔ باز — دورهٔ بسته
    /// دست نمی‌خورد. تاریخ معامله (SaleDate) و زمان تعیین قیمت (SalePriceFixedAtUtc) جدا می‌مانند.
    /// برای فروشی که از اول با قیمت قطعی ثبت شده null است و تاریخ فروش ملاک است.
    /// </summary>
    public DateTime? SalePriceAccountingDate { get; set; }

    // فروش گروهی — سهمِ یک رکورد اصلی SalesBatch (nullable، backward-compatible).
    // هر تخصیص یک SalesTransaction عادی با Ledger/موجودی خودش است؛ منطق مالی موازی ساخته نمی‌شود.
    public int? SalesBatchId { get; set; }
    public SalesBatch? SalesBatch { get; set; }

    // پیش‌فروش مرحله‌ای — این فروش یک «تحویل» از تعهدِ PreSaleOrder است (nullable، backward-compatible).
    // خودِ فروش کاملاً عادی می‌ماند: موجودی، Ledger، درآمد و COGS همان مسیر همیشگی را می‌روند و
    // فقط به اندازهٔ همین تحویل ثبت می‌شوند. تعهدِ پیش‌فروش هیچ سند مالی‌ای نمی‌سازد.
    public int? PreSaleOrderId { get; set; }
    public PreSaleOrder? PreSaleOrder { get; set; }

    public bool IsCancelled { get; set; }

    // ---- PTG-P2-03 — زنجیرهٔ اصلاح فروش -------------------------------------------------
    //
    // فروشِ ثبت‌شده هرگز بازنویسی نمی‌شود. اصلاح یعنی: سندِ اصلی ابطال می‌شود (با برگشتِ
    // دفتر کل و حرکتِ موجودی) و یک فروشِ تازه با هویتِ مستقل ثبت می‌گردد. هر دو سر پیوند
    // دارند تا حسابرس بتواند «چه بود، چه شد و چرا» را از خودِ داده بخواند، نه از حافظهٔ کاربر.

    /// <summary>دلیل ابطال — هنگام ابطال اجباری است. بدون دلیل، ابطال انجام نمی‌شود.</summary>
    [MaxLength(500)] public string? CancelReason { get; set; }

    /// <summary>زمان ابطال (UTC).</summary>
    public DateTime? CancelledAtUtc { get; set; }

    /// <summary>کاربری که ابطال کرد.</summary>
    public int? CancelledByUserId { get; set; }

    /// <summary>
    /// روی سندِ ابطال‌شده: فروشِ جایگزین. خالی یعنی ابطال شد ولی جایگزینی ثبت نشد
    /// (ابطالِ ساده) — که حالتِ کاملاً مجازی است.
    /// </summary>
    public int? ReplacementSaleId { get; set; }

    /// <summary>روی فروشِ جایگزین: سندِ اصلیِ ابطال‌شده‌ای که این سند اصلاحِ آن است.</summary>
    public int? CorrectedFromSaleId { get; set; }

    public ICollection<SalesTransactionSourceAllocation> SourceAllocations { get; set; } = new List<SalesTransactionSourceAllocation>();
}

// وضعیت‌های ذخیره‌شدهٔ پیش‌فروش. وضعیت‌های مشتق (رزرو، مانده پرداخت، آماده تحویل) عمداً
// enum نمی‌شوند و از روی تحویل‌ها/پرداخت‌های معتبر محاسبه می‌شوند.
public enum PreSaleOrderStatus
{
    [Display(Name = "پیش‌نویس")] Draft = 1,
    [Display(Name = "تأییدشده")] Confirmed = 2,
    [Display(Name = "تحویل جزئی")] PartiallyDelivered = 3,
    [Display(Name = "تحویل کامل")] FullyDelivered = 4,
    [Display(Name = "بسته‌شده")] Closed = 5,
    [Display(Name = "لغوشده")] Cancelled = 6
}

// تعهدِ فروشِ آینده به مشتری. این «فروش» نیست: نه موجودی کم می‌کند، نه InventoryMovement می‌سازد،
// نه درآمد/بهای تمام‌شده ثبت می‌کند. تنها مقدار و قیمتِ قفل‌شده را نگه می‌دارد و هر تحویلِ واقعی
// یک SalesTransaction عادی با PreSaleOrderId است (همان الگوی SalesBatch) تا منطق فروش دوپاره نشود.
// مقدار تحویل‌شده عمداً ستون جداگانه ندارد و همیشه از مجموع تحویل‌های لغونشده محاسبه می‌شود.
public class PreSaleOrder : BaseEntity
{
    [MaxLength(64)] public string OrderNumber { get; set; } = "";

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    // جوازِ تعهد (nullable مثل SalesTransaction؛ اگر منابع تحویل چند شرکتی باشند null می‌ماند).
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    public DateTime OrderDate { get; set; }
    public DateTime? ExpectedDeliveryFrom { get; set; }
    public DateTime? ExpectedDeliveryTo { get; set; }

    public decimal QuantityMt { get; set; }
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    // قیمت واحد در لحظهٔ تأیید قفل می‌شود و همهٔ تحویل‌ها با همین قیمت محاسبه می‌شوند.
    public decimal UnitPriceInCurrency { get; set; }
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal UnitPriceUsd { get; set; }
    public decimal TotalInCurrency { get; set; }
    public decimal TotalUsd { get; set; }

    [MaxLength(200)] public string? ReferenceNumber { get; set; }
    [MaxLength(500)] public string? PaymentTerms { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public PreSaleOrderStatus Status { get; set; } = PreSaleOrderStatus.Confirmed;
    public DateTime? CancelledAtUtc { get; set; }
    [MaxLength(500)] public string? CancelReason { get; set; }
    [MaxLength(150)] public string? CreatedByUserName { get; set; }

    public System.Collections.Generic.ICollection<SalesTransaction> Deliveries { get; set; }
        = new System.Collections.Generic.List<SalesTransaction>();
}

// منبعِ یک ردیفِ فروش گروهی. تعیین می‌کند فروش از کجا انجام می‌شود و مکانیزم موجودی/لجر آن.
public enum GroupSaleSourceKind
{
    // موجودی مخزن — فروش بر اساس تن؛ InventoryMovement خروج + Ledger.
    [Display(Name = "موجودی مخزن")] TerminalStock = 1,
    // موتر در جریان (TruckDispatch) — فروش کامل؛ فقط Ledger + لینک دیسپچ (موجودی قبلاً خارج شده).
    [Display(Name = "موتر در جریان")] TruckDispatch = 2,
    // واگن در جریان (InventoryTransportLeg نوع واگن) — فروش کامل؛ رسید DirectSale.
    [Display(Name = "واگن در جریان")] WagonLeg = 3,
    // انتقال موجودی در مسیر (InventoryTransportLeg) — فروش کامل؛ رسید DirectSale.
    [Display(Name = "انتقال در مسیر")] TransportLeg = 4,
    // مستقیم از بارگیری، با رسید و تخصیص واقعیِ فروش؛ بدون حرکت مخزن.
    [Display(Name = "بارگیری")] LoadingRegister = 5
}

// رکورد اصلی فروش گروهی. مشتری/ارز/تاریخ/نرخ مشترک است؛ هر ردیف یک SalesTransaction عادی
// با SalesBatchId می‌شود تا Ledger/موجودی/سود‌وزیان بدون تغییر کار کنند.
public class SalesBatch : BaseEntity
{
    [MaxLength(64)] public string BatchNumber { get; set; } = "";
    // خریدار: دقیقاً یکی از مشتری یا تأمین‌کننده (همان قاعدهٔ SalesTransaction).
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public DateTime SaleDate { get; set; }
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal UnitPriceInCurrency { get; set; }
    public decimal TotalQuantityMt { get; set; }
    public decimal TotalInCurrency { get; set; }
    public decimal TotalUsd { get; set; }
    public int LineCount { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    // شرایط/یادداشت پرداخت (فقط نمایشی؛ سند پرداخت جداگانه ساخته نمی‌شود).
    [MaxLength(500)] public string? PaymentNote { get; set; }
    public bool IsCancelled { get; set; }
    public System.Collections.Generic.ICollection<SalesTransaction> Sales { get; set; } = new System.Collections.Generic.List<SalesTransaction>();
}

public class ExpenseRule : BaseEntity
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public int ExpenseTypeId { get; set; }
    public ExpenseType? ExpenseType { get; set; }
    [MaxLength(50)] public string CalculationKind { get; set; } = "PerMt"; // PerMt / Flat / Percent
    public decimal Amount { get; set; }
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
}

// ثبت مصرف گروهی — روش تقسیم مبلغ بین عملیات‌های انتخاب‌شده.
public enum ExpenseAllocationMethod
{
    [Display(Name = "مبلغ برای هر عملیات")] FixedPerOperation = 1,
    [Display(Name = "تقسیم مساوی")] EqualSplit = 2,
    [Display(Name = "بر اساس مقدار")] ByQuantity = 3,
    [Display(Name = "دستی")] Manual = 4
}

// رکورد اصلی مصرف گروهی. سهم هر عملیات یک ExpenseTransaction عادی است
// (با ExpenseBatchId) تا Ledger/P&L/گزارش‌ها بدون تغییر کار کنند.
public class ExpenseBatch : BaseEntity
{
    [MaxLength(64)] public string BatchNumber { get; set; } = "";
    public int ExpenseTypeId { get; set; }
    public ExpenseType? ExpenseType { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    public DateTime ExpenseDate { get; set; }
    public ExpenseAllocationMethod AllocationMethod { get; set; } = ExpenseAllocationMethod.EqualSplit;
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalAmountUsd { get; set; }
    public int OperationCount { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }
    public bool IsCancelled { get; set; }
    public System.Collections.Generic.ICollection<ExpenseTransaction> Expenses { get; set; } = new System.Collections.Generic.List<ExpenseTransaction>();
}

/// <summary>
/// فاز ۱ — قرارداد تسویهٔ هر رویداد هزینه‌ای. یک مصرف مالی دقیقاً یکی از این حالت‌هاست.
/// </summary>
public enum ExpenseSettlementMode
{
    /// <summary>
    /// رکوردهای پیش از فاز ۱. هیچ‌وقت حدس زده نمی‌شوند و برای رکورد تازه پذیرفته نمی‌شوند؛
    /// در گزارش‌ها زیر ردیف «طبقه‌بندی‌نشده» دیده می‌شوند تا هیچ پولی خاموش گم نشود.
    /// </summary>
    Unknown = 0,

    /// <summary>بدهی واقعی به یک طرف‌حساب مشخص. طرف‌حساب اجباری است.</summary>
    Payable = 1,

    /// <summary>از صندوق/بانک پرداخت شده. حساب نقدی اجباری است و بدهی‌ای نمی‌ماند.</summary>
    PaidImmediately = 2,

    /// <summary>
    /// بدون حرکت پول و بدون طرف‌حساب بیرونی — استهلاک، تعدیل داخلی، تخصیصِ بینِ حساب‌های خودی.
    /// حسابِ مقابل از <see cref="ExpenseType.PayableAccountKind"/> می‌آید.
    /// </summary>
    NonCash = 3
}

public class ExpenseTransaction : BaseEntity, IVersionedEntity
{
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    /// <summary>
    /// PTG-P2-02 — کلیدِ ضدتکراریِ ایمپورت اکسل. فقط سطرهایی که از فایل آمده‌اند
    /// مقدار دارند؛ مصارفِ دستی و همهٔ رکوردهای موجود null می‌مانند و از یکتایی
    /// معاف‌اند (PostgreSQL مقدار NULL را تکراری نمی‌شمارد). ببینید <see cref="Helpers.ExpenseImportKey"/>.
    /// </summary>
    [MaxLength(300)] public string? ImportUniqueKey { get; set; }

    public int ExpenseTypeId { get; set; }
    public ExpenseType? ExpenseType { get; set; }
    public int? ExpenseRuleId { get; set; }
    public ExpenseRule? ExpenseRule { get; set; }

    // مصرف گروهی — سهمِ یک رکورد اصلی ExpenseBatch (nullable، backward-compatible).
    public int? ExpenseBatchId { get; set; }
    public ExpenseBatch? ExpenseBatch { get; set; }

    // Operational reference (rule #8 — every expense must be traceable)
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }
    public int? LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }
    public int? TransportLegId { get; set; }
    public InventoryTransportLeg? TransportLeg { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    public int? OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    // راننده/موتروانِ مستقل به‌عنوان طرفِ کرایه، وقتی حمل با موترِ شخصی است (نه شرکت خدماتی، نه دارایی خودی).
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }

    public DateTime ExpenseDate { get; set; }
    public decimal Amount { get; set; }
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal AmountUsd { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }

    // Phase 1 — مسئول این هزینه/کرایه (فقط ثبت/نمایش، nullable). به Ledger وابسته نیست.
    public CostResponsibility? CostResponsibility { get; set; }

    // کمیسیون — لینک ردیابی به پرداخت/دریافت اصلیِ روزنامچه که این مصرف کمیسیونِ آن است.
    // ستون سادهٔ nullable بدون navigation/FK (backward-compatible).
    public int? RelatedPaymentTransactionId { get; set; }

    // ---------------------------------------------------------------------
    // فاز ۱ — هویت تسویه. یک مصرف مالی دقیقاً یکی از این حالت‌ها را دارد و حالتِ
    // «نه پرداخت‌شده، نه بدهکار به کسی» دیگر ذخیره نمی‌شود. رجوع:
    // Services/Expenses/ExpenseSettlementValidator.cs
    // ---------------------------------------------------------------------

    /// <summary>
    /// وضعیت تسویهٔ این مصرف. رکوردهای پیش از فاز ۱ روی <see cref="ExpenseSettlementMode.Unknown"/>
    /// می‌مانند و هرگز حدس زده نمی‌شوند؛ گزارش‌ها آن‌ها را جدا نشان می‌دهند.
    /// </summary>
    public ExpenseSettlementMode SettlementMode { get; set; } = ExpenseSettlementMode.Unknown;

    /// <summary>
    /// طرف‌حسابی که این مصرف بدهیِ ما به اوست. فقط در حالت
    /// <see cref="ExpenseSettlementMode.Payable"/> مقدار دارد و اجباری است.
    /// از همان <see cref="AccountingPartyType"/> دفتر کل استفاده می‌کند — طرف‌حسابِ تازه‌ای
    /// ساخته نمی‌شود.
    /// </summary>
    public AccountingPartyType? CounterpartyType { get; set; }

    public int? CounterpartyId { get; set; }

    /// <summary>
    /// صندوق/بانکی که این مصرف از آن پرداخت شده است. فقط در حالت
    /// <see cref="ExpenseSettlementMode.PaidImmediately"/> مقدار دارد و اجباری است.
    /// خودِ حرکتِ پول در <see cref="PaymentTransaction"/> ثبت می‌شود؛ این فیلد «اعلامِ سند» است.
    /// </summary>
    public int? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    /// <summary>
    /// PTG-P1-04 — اظهارنامهٔ گمرکی‌ای که این مصرف از آن ساخته شده.
    ///
    /// پیش از این، هزینهٔ گمرک یک انبارِ مالیِ موازی بود: گزارش سود و زیان مبلغ را
    /// مستقیم از <see cref="CustomsDeclaration.TotalUsd"/> می‌خواند، بی‌آنکه سطرِ دفتری
    /// وجود داشته باشد. این پیوند همان مبلغ را به دفتر کل می‌آورد و در گزارش‌ها جای
    /// خواندنِ مستقیم را می‌گیرد، پس ستونِ «گمرک» حفظ می‌شود بی‌آنکه دو بار شمرده شود.
    /// </summary>
    public int? CustomsDeclarationId { get; set; }
    public CustomsDeclaration? CustomsDeclaration { get; set; }

    /// <summary>کدام جنسِ اظهارنامه — تا هر گروه دقیقاً یک مصرفِ فعال داشته باشد.</summary>
    public Services.Customs.CustomsComponentGroup? CustomsComponentGroup { get; set; }

    public bool IsCancelled { get; set; }
}
