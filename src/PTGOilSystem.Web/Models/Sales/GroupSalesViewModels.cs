using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Sales;

// یک منبعِ قابل‌فروش برای انتخاب در فروش گروهی (موجودی مخزن یا وسیلهٔ در جریان).
public sealed class GroupSaleSourceItem
{
    // کلید یکتای ردیف در UI: برای مخزن ترکیبی، برای وسیله = Kind:Id.
    public string Key { get; init; } = "";
    public GroupSaleSourceKind Kind { get; init; }
    public string KindLabel { get; init; } = "";       // موجودی مخزن / موتر / واگن / انتقال
    public int Id { get; init; }                        // dispatch/leg id (0 برای مخزن)
    public string VehicleKind { get; init; } = "";      // مخزن / موتر / واگن
    public string Number { get; init; } = "";           // پلاک / شماره واگن / کد مخزن
    public string Route { get; init; } = "";            // مبدأ ← مقصد
    public string ProductName { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public decimal AvailableMt { get; init; }           // موجودی آزاد (مخزن) یا مقدار کامل (وسیله)
    public bool IsFullVehicle { get; init; }            // وسیله: فروش کامل، مقدار قابل ویرایش نیست
    public string StatusLabel { get; init; } = "";
    public DateTime MoveDate { get; init; }

    // شناسه‌های منبعِ موجودی مخزن (برای اعتبارسنجی سمت سرور؛ برای وسیله صفر می‌ماند).
    public int ProductId { get; init; }
    public int TerminalId { get; init; }
    public int StorageTankId { get; init; }
    public int SourcePurchaseContractId { get; init; }
    public int CompanyId { get; init; }
}

// یک ردیفِ انتخاب‌شده که همراه فرم POST می‌شود. هویتِ منبع دوباره سمت سرور اعتبارسنجی می‌شود.
public sealed class GroupSaleSelectedInput
{
    public GroupSaleSourceKind Kind { get; set; }
    public int Id { get; set; }                          // dispatch/leg id (وسیله)
    // مقدار فروش (فقط موجودی مخزن؛ برای وسیله = مقدار کامل و نادیده گرفته می‌شود).
    public decimal? QuantityMt { get; set; }
    // هویتِ منبع موجودی مخزن.
    public int ProductId { get; set; }
    public int TerminalId { get; set; }
    public int StorageTankId { get; set; }
    public int SourcePurchaseContractId { get; set; }
    public int CompanyId { get; set; }
}

public sealed class GroupSaleCreateViewModel
{
    // خریدار: مشتری یا تأمین‌کننده (SaleBuyerKey). دقیقاً یکی از دو شناسه مقدار دارد.
    [Display(Name = "خریدار")]
    [Required(ErrorMessage = "انتخاب خریدار الزامی است.")]
    public string? BuyerKey
    {
        get => SaleBuyerKey.Build(CustomerId, SupplierId);
        set => (CustomerId, SupplierId) = SaleBuyerKey.Parse(value);
    }

    public int? CustomerId { get; set; }

    public int? SupplierId { get; set; }

    [Display(Name = "تاریخ فروش")]
    [DataType(DataType.Date)]
    public DateTime SaleDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "نرخ فروش هر تن")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "نرخ فروش هر تن باید بزرگ‌تر از صفر باشد.")]
    public decimal UnitPriceInCurrency { get; set; }

    /// <summary>قیمت فروش هنوز مشخص نیست؛ بار و موجودی ثبت می‌شود، سند مالی پس از تعیین قیمت.</summary>
    [Display(Name = "قیمت فروش هنوز مشخص نیست")]
    public bool IsSalePricePending { get; set; }

    [Display(Name = "یادداشت پرداخت")]
    [StringLength(500)]
    public string? PaymentNote { get; set; }

    [Display(Name = "توضیحات")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [Display(Name = "محل فروش مستقیم بارگیری")]
    public int? LoadingSaleTerminalId { get; set; }

    public List<GroupSaleSelectedInput> Items { get; set; } = [];

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

// یک دریافتِ مشتری که هنوز مانده تطبیق‌نشده دارد و می‌تواند روی فروش‌ها بنشیند.
public sealed class SaleApplicableReceiptViewModel
{
    public int PaymentTransactionId { get; init; }
    public DateTime PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public decimal UnappliedAmount { get; init; }
    public decimal UnappliedAmountUsd { get; init; }
    public string Currency { get; init; } = "USD";
    public string? Reference { get; init; }
}

// یک ردیفِ ثبت‌شدهٔ تطبیق: این دریافت، این مبلغ، روی این فروش.
public sealed class SaleReceiptApplicationViewModel
{
    public int ApplicationId { get; init; }
    public int PaymentTransactionId { get; init; }
    public int SalesTransactionId { get; init; }
    public string InvoiceNumber { get; init; } = "";
    public DateTime PaymentDate { get; init; }
    public DateTime AppliedAt { get; init; }
    public decimal AppliedPaymentAmount { get; init; }
    public string PaymentCurrencyCode { get; init; } = "USD";
    public decimal AppliedAmountUsd { get; init; }
    public string? Reference { get; init; }
    // مصرفِ پیش‌دریافت با لغو تحویل آزاد می‌شود، نه با برگشتِ دستیِ تطبیق.
    public bool IsAdvanceApplication { get; init; }
}

public sealed class GroupSaleLineViewModel
{
    public int SalesTransactionId { get; init; }
    public string KindLabel { get; init; } = "";
    public string VehicleKind { get; init; } = "";
    public string Number { get; init; } = "";
    public string Route { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string InvoiceNumber { get; init; } = "";
    public decimal QuantityMt { get; init; }
    public decimal TotalInCurrency { get; init; }
    public decimal TotalUsd { get; init; }
    public bool IsCancelled { get; init; }
    public bool IsSalePricePending { get; init; }
    public decimal ReceivedUsd { get; init; }
    public decimal OpenReceivableUsd { get; init; }
}

public sealed class GroupSaleDetailsViewModel
{
    public int Id { get; init; }
    public string BatchNumber { get; init; } = "";
    public string CustomerName { get; init; } = "";
    public DateTime SaleDate { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal UnitPriceInCurrency { get; init; }
    public decimal TotalQuantityMt { get; init; }
    public decimal TotalInCurrency { get; init; }
    public decimal TotalUsd { get; init; }
    public int LineCount { get; init; }
    public string? PaymentNote { get; init; }
    public string? Notes { get; init; }
    public bool IsCancelled { get; init; }
    public IReadOnlyList<GroupSaleLineViewModel> Lines { get; init; } = [];

    // تطبیق نقد در سطح گروه: یک دریافت روی چند ردیف فروش می‌نشیند و هر سهم ردیف واقعی دارد.
    public int? CustomerId { get; init; }
    public bool IsSupplierBuyer { get; init; }
    public decimal ReceivedUsd { get; init; }
    public decimal OpenReceivableUsd { get; init; }
    public IReadOnlyList<SaleReceiptApplicationViewModel> Applications { get; init; } = [];
    public IReadOnlyList<SaleApplicableReceiptViewModel> ApplicableReceipts { get; init; } = [];
}
