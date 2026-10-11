using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

// امانی — جنسِ متعلق به دیگران که نزد ما نگهداری می‌شود.
//
// چرا دفترِ جدا و نه یک ستونِ مالک روی InventoryMovement:
// InventoryMovement در حال حاضر «موجودی ملکی شرکت» است و حدود چهل خواننده (مفاد و زیان،
// قیمت تمام‌شده، داشبورد، تطبیق، lineage، ...) آن را بی‌شرط ملکِ شرکت می‌شمارند. افزودنِ
// مالک به آن جدول یعنی هر خواننده‌ای که فیلتر را جا بیندازد، تیل مردم را وارد مفاد و ارزش
// موجودی شرکت می‌کند. پس هر مقدار فقط در یک دفتر ثبت می‌شود:
//   ملکِ شرکت        → InventoryMovement (بدون تغییر)
//   ملکِ دیگران نزد ما → CustodyMovement (این فایل)
// موجودی فزیکی یک مخزن = مجموع هر دو. هیچ مقداری هم‌زمان در هر دو دفتر نیست.

/// <summary>وضعیت یک بارِ امانی. «تمام‌شده» ذخیره نمی‌شود؛ از مانده خوانده می‌شود.</summary>
public enum CustodyLotStatus
{
    [Display(Name = "در راه")] InTransit = 1,
    [Display(Name = "در نگهداری")] Active = 2,
    [Display(Name = "لغو شده")] Cancelled = 3
}

public enum CustodyDirection
{
    In = 1,
    Out = 2
}

public enum CustodyMovementKind
{
    [Display(Name = "ورود")] Receipt = 1,
    [Display(Name = "برگشت به مالک")] ReturnToOwner = 2,
    [Display(Name = "تحویل به دستور مالک")] DeliveryToThirdParty = 3,
    [Display(Name = "انتقال بین مخازن")] LocationTransfer = 4,
    [Display(Name = "انتقال مالکیت")] OwnershipTransfer = 5,
    [Display(Name = "کسر / ضایعات")] Loss = 6,
    [Display(Name = "اضافه موجودی")] Surplus = 7,
    /// <summary>خروجِ فروش امانی — فقط از مسیر <c>CustodySaleService</c> ساخته و لغو می‌شود.</summary>
    [Display(Name = "فروش امانی")] Sale = 8,
    [Display(Name = "تبدیل به خرید قطعی")] ConvertedToCompany = 9
}

/// <summary>مسئولِ کسر طبق توافق. تا تعیین نشود «در حال بررسی» می‌ماند و هیچ اثر مالی ندارد.</summary>
public enum CustodyLossResponsibility
{
    [Display(Name = "در حال بررسی")] Undetermined = 1,
    [Display(Name = "مالک")] Owner = 2,
    [Display(Name = "نگهدارنده (ما)")] Holder = 3,
    [Display(Name = "حمل‌کننده")] Carrier = 4
}

/// <summary>
/// یک بارِ امانی: یک مالک، یک جنس. مانده از حرکت‌ها محاسبه می‌شود، نه از ستونِ ذخیره‌شده.
/// </summary>
public class CustodyLot : BaseEntity
{
    [Required, MaxLength(50)] public string LotNumber { get; set; } = "";

    /// <summary>مالک: مشتری، فروشنده، شریک یا شرکتِ دیگر.</summary>
    public AccountingPartyType OwnerPartyType { get; set; }
    public int OwnerPartyId { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public CustodyLotStatus Status { get; set; } = CustodyLotStatus.Active;

    /// <summary>بارِ اصلی وقتی این بار از انتقال مالکیت ساخته شده است.</summary>
    public int? ParentCustodyLotId { get; set; }
    public CustodyLot? ParentCustodyLot { get; set; }

    // اطلاعات حمل — اختیاری؛ فقط برای بارِ در راه یا ثبت اختلاف بارگیری و رسید.
    public DateTime? LoadedDate { get; set; }
    public decimal? LoadedQuantityMt { get; set; }
    [MaxLength(200)] public string? OriginDescription { get; set; }
    [MaxLength(200)] public string? TransportReference { get; set; }
    public CustodyLossResponsibility TransitLossResponsibility { get; set; } = CustodyLossResponsibility.Undetermined;

    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public ICollection<CustodyMovement> Movements { get; set; } = new List<CustodyMovement>();
    public ICollection<CustodyHold> Holds { get; set; } = new List<CustodyHold>();
}

/// <summary>
/// حرکتِ append-only. لغو با یک حرکتِ معکوس (<see cref="ReversalOfCustodyMovementId"/>) ثبت
/// می‌شود؛ هیچ ردیفی حذف یا بازنویسی نمی‌شود. دو ردیفِ یک عملیات (انتقال) یک
/// <see cref="OperationKey"/> مشترک دارند.
/// </summary>
public class CustodyMovement : BaseEntity
{
    public int CustodyLotId { get; set; }
    public CustodyLot? CustodyLot { get; set; }

    public CustodyMovementKind Kind { get; set; }
    public CustodyDirection Direction { get; set; }

    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    public int? StorageTankId { get; set; }
    public StorageTank? StorageTank { get; set; }

    public DateTime MovementDate { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal? SourceLiters { get; set; }
    public decimal? DensityKgPerM3 { get; set; }
    public decimal? MeasurementTemperatureC { get; set; }
    public decimal? DensityTemperatureC { get; set; }
    [MaxLength(500)] public string? MeasurementReference { get; set; }

    public Guid OperationKey { get; set; }

    public int? ReversalOfCustodyMovementId { get; set; }
    public CustodyMovement? ReversalOfCustodyMovement { get; set; }
    public bool IsReversed { get; set; }

    /// <summary>فقط برای کسر/اضافه.</summary>
    public CustodyLossResponsibility? LossResponsibility { get; set; }

    [MaxLength(100)] public string? DocumentNumber { get; set; }
    /// <summary>گیرنده در تحویل به شخص سوم.</summary>
    [MaxLength(200)] public string? CounterpartyName { get; set; }
    /// <summary>موتر، پلیت یا راننده.</summary>
    [MaxLength(200)] public string? VehicleReference { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

/// <summary>مقدارِ مسدود از یک بار — برای فروش و تحویل قابل استفاده نیست تا رفع شود.</summary>
public class CustodyHold : BaseEntity
{
    public int CustodyLotId { get; set; }
    public CustodyLot? CustodyLot { get; set; }
    public decimal QuantityMt { get; set; }
    [Required, MaxLength(500)] public string Reason { get; set; } = "";
    public DateTime? ReleasedAtUtc { get; set; }
    public int? ReleasedByUserId { get; set; }
    [MaxLength(500)] public string? ReleaseNote { get; set; }
}

// ---------------------------------------------------------------- مرحلهٔ ۲: فروش امانی و حساب مالک
//
// فروش امانی «فروش شرکت» نیست و SalesTransaction نمی‌سازد: درآمد، بهای تمام‌شده و موجودی ملکی
// شرکت دست نمی‌خورد. شرکت به نمایندگی مالک می‌فروشد؛ درآمدِ شرکت فقط کمیشن و خدمات است.
// اثر مالی از همان زیرساخت موجود می‌گذرد: PaymentTransaction برای پول نقد، LedgerEntry برای
// صورت‌حساب طرف‌ها و JournalEntry (Adapter امانی) برای دفتر کل.

public enum CustodyDocumentStatus
{
    [Display(Name = "ثبت شده")] Posted = 1,
    [Display(Name = "لغو شده")] Cancelled = 2
}

public enum CustodyCommissionMethod
{
    [Display(Name = "بدون کمیشن")] None = 0,
    [Display(Name = "فیصدی فروش")] PercentOfSale = 1,
    [Display(Name = "مبلغ ثابت فی تُن")] PerMt = 2,
    [Display(Name = "مبلغ ثابت برای معامله")] FixedPerSale = 3
}

/// <summary>چه زمانی کمیشن درآمدِ قطعی است.</summary>
public enum CustodyCommissionRecognition
{
    /// <summary>به تناسبِ پولی که از مشتری وصول می‌شود (پیش‌فرض محتاطانه).</summary>
    [Display(Name = "به تناسب وصول پول")] OnCollection = 1,
    /// <summary>با انجامِ فروش، مستقل از وصول.</summary>
    [Display(Name = "هنگام فروش")] OnSale = 2
}

public class CustodySale : BaseEntity
{
    [Required, MaxLength(50)] public string SaleNumber { get; set; } = "";

    public int CustodyLotId { get; set; }
    public CustodyLot? CustodyLot { get; set; }
    public AccountingPartyType OwnerPartyType { get; set; }
    public int OwnerPartyId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int ProductId { get; set; }

    public int TerminalId { get; set; }
    public int? StorageTankId { get; set; }

    public DateTime SaleDate { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal UnitPriceUsd { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal AmountUsd { get; set; }

    public CustodyCommissionMethod CommissionMethod { get; set; }
    /// <summary>فیصدی (مثلاً ۳)، یا دالر فی تُن، یا مبلغ ثابت — بسته به روش.</summary>
    public decimal CommissionRate { get; set; }
    public decimal CommissionUsd { get; set; }
    public CustodyCommissionRecognition CommissionRecognition { get; set; } = CustodyCommissionRecognition.OnCollection;
    /// <summary>کمیشنی که همراهِ خودِ فروش درآمد شد (فقط روش «هنگام فروش»).</summary>
    public decimal CommissionRecognizedAtSaleUsd { get; set; }

    /// <summary>OperationKeyِ حرکتِ خروجِ همین فروش در دفتر امانی.</summary>
    public Guid StockOperationKey { get; set; }

    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public CustodyDocumentStatus Status { get; set; } = CustodyDocumentStatus.Posted;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    [MaxLength(500)] public string? CancellationReason { get; set; }
}

public enum CustodyFinanceKind
{
    /// <summary>پولِ مشتری نزد صندوق/بانکِ شرکت آمد.</summary>
    [Display(Name = "دریافت از مشتری")] Collection = 1,
    /// <summary>مشتری مستقیم به مالک پرداخت کرد؛ صندوق شرکت تغییر نمی‌کند.</summary>
    [Display(Name = "پرداخت مستقیم مشتری به مالک")] DirectToOwner = 2,
    [Display(Name = "پرداخت به مالک")] OwnerPayout = 3,
    [Display(Name = "مصرف به حساب مالک")] ReimbursableExpense = 4,
    [Display(Name = "حق خدمات شرکت")] ServiceFee = 5
}

/// <summary>
/// یک سند مالی امانی. هر نوع دقیقاً یک بار اثر می‌گذارد و لغوش با برگشتِ همان اسناد انجام می‌شود.
/// </summary>
public class CustodyFinanceEntry : BaseEntity
{
    public CustodyFinanceKind Kind { get; set; }

    public int? CustodySaleId { get; set; }
    public CustodySale? CustodySale { get; set; }
    public int? CustodyLotId { get; set; }
    public CustodyLot? CustodyLot { get; set; }

    public AccountingPartyType OwnerPartyType { get; set; }
    public int OwnerPartyId { get; set; }
    public int? CustomerId { get; set; }

    public DateTime EntryDate { get; set; }
    public decimal AmountUsd { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";

    /// <summary>بخشی از کمیشنِ فروش که با همین وصول درآمد شد (روش «به تناسب وصول»).</summary>
    public decimal CommissionUsd { get; set; }

    public int? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }
    public int? PaymentTransactionId { get; set; }
    public PaymentTransaction? PaymentTransaction { get; set; }
    /// <summary>پرداختِ معکوس هنگام لغو سندی که پول نقد جابه‌جا کرده بود.</summary>
    public int? ReversalPaymentTransactionId { get; set; }

    public int? ExpenseTypeId { get; set; }
    public ExpenseType? ExpenseType { get; set; }
    /// <summary>گیرندهٔ پولِ مصرف (مثلاً حمل‌کننده) — فقط برای اطلاع؛ حسابِ او اینجا تغییر نمی‌کند.</summary>
    [MaxLength(200)] public string? PayeeName { get; set; }

    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }

    public CustodyDocumentStatus Status { get; set; } = CustodyDocumentStatus.Posted;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    [MaxLength(500)] public string? CancellationReason { get; set; }
}
