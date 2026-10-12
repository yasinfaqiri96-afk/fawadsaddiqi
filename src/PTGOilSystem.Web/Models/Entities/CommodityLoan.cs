using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

// قرض جنسی — تیلِ شرکت که به دیگری قرض داده می‌شود یا تیلی که شرکت از دیگری قرض می‌گیرد و باید
// به همان جنس و کیفیت برگرداند. با «امانی» مخلوط نمی‌شود: در امانی جنس هرگز مال شرکت نیست؛ در قرض
// جنسی، قرض‌دادن از موجودیِ ملکی کم می‌کند و قرض‌گرفتن (با حق مصرف یا مالکیت قراردادی) به موجودیِ
// قابل مصرفِ شرکت اضافه می‌شود و تعهدِ برگشتِ جنس می‌سازد. حرکت‌ها از همان InventoryMovementWriter
// و ارزش از همان Pool میانگین موجودی (InventoryValuationService) می‌گذرد؛ مسیر موازی ساخته نمی‌شود.

public enum CommodityLoanDirection
{
    [Display(Name = "قرض‌دادن (از موجودی ملکی)")] Lend = 1,
    [Display(Name = "قرض‌گرفتن")] Borrow = 2
}

/// <summary>مبنای حقوقیِ قرض‌گرفتن؛ هر دو اجازهٔ مصرف می‌دهند و جنس وارد موجودی قابل مصرف می‌شود.</summary>
public enum CommodityLoanBorrowBasis
{
    [Display(Name = "حق مصرف")] RightOfUse = 1,
    [Display(Name = "مالکیت قراردادی")] ContractualOwnership = 2
}

/// <summary>
/// سیاستِ ارزش‌گذاریِ تأییدشده. قرض‌دادن فقط به بهای دفتری (میانگینِ موجودیِ همان ترمینال) و
/// قرض‌گرفتن فقط به ارزشِ توافقیِ قرارداد با مرجع؛ پیش‌فرض ندارد.
/// </summary>
public enum CommodityLoanValuation
{
    [Display(Name = "بهای دفتری موجودی")] BookCost = 1,
    [Display(Name = "ارزش توافقی قرارداد")] AgreedValue = 2
}

public enum CommodityLoanStatus
{
    [Display(Name = "فعال")] Active = 1,
    [Display(Name = "لغو شده")] Cancelled = 2
}

public enum CommodityLoanSettlementKind
{
    /// <summary>برگشتِ جنس (کامل یا قسمی).</summary>
    [Display(Name = "برگشت جنس")] InKindReturn = 1,
    /// <summary>تسویهٔ نقدی؛ فقط وقتی قرارداد آن را با قیمت و مرجع مجاز کرده.</summary>
    [Display(Name = "تسویه نقدی")] CashSettlement = 2,
    /// <summary>بستنِ تفاوتِ مقدارِ باقی‌مانده در حدِ تلورانسِ قرارداد.</summary>
    [Display(Name = "تفاوت مقدار")] QuantityDifference = 3
}

public class CommodityLoan : BaseEntity
{
    [Required, MaxLength(50)] public string LoanNumber { get; set; } = "";
    public CommodityLoanDirection Direction { get; set; }

    public AccountingPartyType CounterpartyType { get; set; }
    public int CounterpartyId { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>کیفیت/مشخصاتِ جنس که باید به همان شکل برگردد.</summary>
    [Required, MaxLength(300)] public string QualitySpec { get; set; } = "";
    public decimal QuantityMt { get; set; }
    public DateTime LoanDate { get; set; }
    public DateTime DueDate { get; set; }
    /// <summary>تعهدِ برگشت: جنس، کیفیت، محل و شرایط تحویل.</summary>
    [Required, MaxLength(500)] public string ReturnObligation { get; set; } = "";
    public CommodityLoanBorrowBasis? BorrowBasis { get; set; }

    /// <summary>محلِ خروج (قرض‌دادن) یا ورود (قرض‌گرفتن) جنس.</summary>
    public int TerminalId { get; set; }
    public int? StorageTankId { get; set; }
    public int InventoryMovementId { get; set; }

    // ---- قواعدِ قرارداد (تنظیم در خودِ قرارداد؛ بدون آن‌ها اسناد مالیِ مربوط ثبت نمی‌شوند)
    public CommodityLoanValuation? ValuationPolicy { get; set; }
    /// <summary>ارزشِ توافقیِ هر تُن (قرض‌گرفتن).</summary>
    public decimal? AgreedUnitValueUsd { get; set; }
    [MaxLength(300)] public string? ValuationReference { get; set; }
    /// <summary>ارزشِ دفتریِ ثبت‌شده هنگام قرض (null وقتی دفتر کل خاموش است).</summary>
    public decimal? CarryingValueUsd { get; set; }
    /// <summary>حسابِ طلبِ جنسی (قرض‌دادن، دارایی) یا بدهیِ جنسی (قرض‌گرفتن، بدهی) از نقشهٔ حساب‌ها.</summary>
    public int? LoanAccountId { get; set; }
    /// <summary>حسابِ سود/زیانِ تفاوتِ ارزش یا مقدار هنگام تسویه.</summary>
    public int? DifferenceAccountId { get; set; }
    /// <summary>حداکثر تفاوت مقدارِ قابل بستن (فیصدِ مقدار قرض).</summary>
    public decimal? QuantityTolerancePercent { get; set; }
    public bool CashSettlementAllowed { get; set; }
    public decimal? CashSettlementUnitPriceUsd { get; set; }
    [MaxLength(300)] public string? CashSettlementReference { get; set; }

    [Required, MaxLength(300)] public string AgreementReference { get; set; } = "";
    [MaxLength(1000)] public string? Notes { get; set; }

    public CommodityLoanStatus Status { get; set; } = CommodityLoanStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    [MaxLength(500)] public string? CancellationReason { get; set; }

    public ICollection<CommodityLoanSettlement> Settlements { get; set; } = new List<CommodityLoanSettlement>();
}

public class CommodityLoanSettlement : BaseEntity
{
    public int CommodityLoanId { get; set; }
    public CommodityLoan? CommodityLoan { get; set; }
    public CommodityLoanSettlementKind Kind { get; set; }
    public DateTime SettlementDate { get; set; }
    public decimal QuantityMt { get; set; }

    public int? TerminalId { get; set; }
    public int? StorageTankId { get; set; }
    public int? InventoryMovementId { get; set; }

    /// <summary>بخشی از ارزش دفتریِ قرض که با این تسویه آزاد شد.</summary>
    public decimal? CarryingValueUsd { get; set; }
    /// <summary>ارزشِ موجودیِ جابه‌جاشده (برگشت جنس).</summary>
    public decimal? InventoryValueUsd { get; set; }
    public decimal? CashAmountUsd { get; set; }
    public int? CashAccountId { get; set; }
    public int? PaymentTransactionId { get; set; }
    public int? ReversalPaymentTransactionId { get; set; }
    /// <summary>سود (+) یا زیان (−) شرکت از تفاوت ارزش/مقدار در همین تسویه.</summary>
    public decimal? DifferenceUsd { get; set; }

    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public CommodityLoanStatus Status { get; set; } = CommodityLoanStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    [MaxLength(500)] public string? CancellationReason { get; set; }
}
