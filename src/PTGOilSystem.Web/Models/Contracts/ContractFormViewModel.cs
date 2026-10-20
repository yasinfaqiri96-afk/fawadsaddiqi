using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Contracts;

public sealed class ContractPartnerShareInput
{
    [Display(Name = "شریک")]
    public int? PartnerId { get; set; }

    [Display(Name = "درصد سهم")]
    [Range(typeof(decimal), "0.0001", "100", ErrorMessage = "درصد سهم باید بین 0 و 100 باشد.")]
    public decimal? SharePercent { get; set; }
}

public sealed class ContractFormViewModel
{
    /// <summary>
    /// PTG-P1-05 — نسخهٔ سطری که کاربر هنگامِ بازکردنِ فرم دید. با فرم برمی‌گردد تا ذخیره
    /// روی نسخهٔ کهنه رد شود. صفر یعنی فرم نسخه نفرستاده است.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// PTG ۱۲-B — تاریخِ اجرای درصدهای تازهٔ شراکت.
    ///
    /// خالی یعنی «امروزِ کاری» — یعنی دقیقاً همان رفتاری که پیش از این تنها گزینه بود.
    /// تاریخِ آینده مجاز است. تاریخِ گذشته فقط تا آغازِ آخرین بازهٔ موجود عقب می‌رود و از
    /// دورهٔ بستهٔ عملیاتی رد نمی‌شود؛ وگرنه صورت‌حسابِ یک دورهٔ بسته بی‌صدا عوض می‌شد.
    /// </summary>
    [Display(Name = "اجرای سهم‌های جدید از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? PartnerSharesEffectiveFrom { get; set; }

    /// <summary>
    /// شرکای قبلی اشتباه ثبت شده بودند: همهٔ بازه‌های سهم پاک و ترکیب تازه از آغازِ
    /// نخستین بازه ثبت می‌شود، انگار از اول همین بوده است. <see cref="PartnerSharesEffectiveFrom"/>
    /// در این حالت نادیده گرفته می‌شود.
    /// </summary>
    [Display(Name = "اصلاح اشتباه: این شرکا از ابتدای قرارداد بوده‌اند")]
    public bool PartnerSharesCorrectFromStart { get; set; }

    public int Id { get; set; }

    [Display(Name = "نام قرارداد")]
    [StringLength(200)]
    public string? ContractName { get; set; }

    [Display(Name = "شماره قرارداد")]
    [Required(ErrorMessage = "شماره قرارداد الزامی است.")]
    [StringLength(50)]
    public string ContractNumber { get; set; } = string.Empty;

    [Display(Name = "نوع قرارداد")]
    public ContractType ContractType { get; set; } = ContractType.Purchase;

    [Display(Name = "وضعیت")]
    public ContractStatus Status { get; set; } = ContractStatus.Draft;

    // اختیاری. خالی = قرارداد مستقل/اصلی؛ مقداردار = زیرقراردادِ همان قرارداد اصلی.
    // فقط یک سطح مجاز است، پس قراردادی که خودش زیرقرارداد است در این فهرست نمی‌آید.
    [Display(Name = "قرارداد اصلی")]
    public int? ParentContractId { get; set; }

    [Display(Name = "شرکت")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب شرکت الزامی است.")]
    public int CompanyId { get; set; }

    [Display(Name = "کالا")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب کالا الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "واحد قرارداد")]
    [Required(ErrorMessage = "انتخاب واحد الزامی است.")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب واحد الزامی است.")]
    public int? UnitId { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "منبع / محل خرید")]
    public int? PurchaseSourceLocationId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "دلیل لغو")]
    [StringLength(500)]
    public string? CancellationReason { get; set; }

    [Display(Name = "مالکیت")]
    public ContractOwnershipType OwnershipType { get; set; } = ContractOwnershipType.Personal;

    [Display(Name = "تاریخ قرارداد")]
    [DataType(DataType.Date)]
    public DateTime ContractDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "شروع")]
    [DataType(DataType.Date)]
    public DateTime? StartDate { get; set; }

    [Display(Name = "پایان")]
    [DataType(DataType.Date)]
    public DateTime? EndDate { get; set; }

    [Display(Name = "روش قیمت‌گذاری")]
    public PricingMethod PricingMethod { get; set; } = PricingMethod.Fixed;

    [Display(Name = "نوع نرخ")]
    public UiPricingType UiPricingType { get; set; } = UiPricingType.Agreed;

    [Display(Name = "نوع Platt's")]
    public PlattsUiMode PlattsUiMode { get; set; } = PlattsUiMode.ManualDescriptive;

    [Display(Name = "قیمت نهایی USD/MT")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت نهایی باید بزرگ‌تر از صفر باشد.")]
    public decimal? FinalPriceUsdPerMt { get; set; }

    [Display(Name = "یادداشت نرخ")]
    [StringLength(500)]
    public string? PricingNote { get; set; }

    public bool IsPricingCompleted { get; set; }

    [Display(Name = "مقدار (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار باید بزرگ‌تر از صفر باشد.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(50)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "Settlement currency")]
    [StringLength(10)]
    public string SettlementCurrencyCode { get; set; } = "USD";

    [Display(Name = "RUB rate method")]
    public RubSettlementRatePolicy RubRatePolicy { get; set; } = RubSettlementRatePolicy.NotApplicable;

    [Display(Name = "RUB per 1 USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "RUB/USD rate must be greater than zero.")]
    public decimal? ContractRubPerUsdRate { get; set; }

    [Display(Name = "RUB rate date")]
    [DataType(DataType.Date)]
    public DateTime? ContractRubRateDate { get; set; }

    [Display(Name = "RUB rate source")]
    [StringLength(200)]
    public string? ContractRubRateSource { get; set; }

    [Display(Name = "قیمت واحد")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت واحد باید بزرگ‌تر از صفر باشد.")]
    public decimal? UnitPriceInCurrency { get; set; }

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    public decimal? UnitPriceUsd { get; set; }

    [Display(Name = "Benchmark")]
    [StringLength(100)]
    public string? BenchmarkCode { get; set; }

    [Display(Name = "نوع دوره پلتس")]
    public PlattsPeriodType? PlattsPeriodType { get; set; }

    [Display(Name = "Premium / Discount USD/MT")]
    public decimal? PremiumDiscountUsd { get; set; }

    [Display(Name = "قیمت پایه Platt's")]
    public decimal? PlattsManualPriceUsd { get; set; }

    [Display(Name = "حداقل قیمت USD/MT")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "حداقل قیمت نمی‌تواند منفی باشد.")]
    public decimal? MinimumPriceUsd { get; set; }

    [Display(Name = "تاریخ مرجع")]
    [DataType(DataType.Date)]
    public DateTime? PlattsBasisDate { get; set; }

    [Display(Name = "ماه مرجع")]
    [DataType(DataType.Date)]
    public DateTime? PlattsBasisMonth { get; set; }

    [Display(Name = "قیمت نهایی توافقی USD/MT")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت نهایی باید بزرگ‌تر از صفر باشد.")]
    public decimal? ManualFinalPriceUsd { get; set; }

    [Display(Name = "توضیح فرمول قیمت")]
    [StringLength(500)]
    public string? PricingFormulaNote { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public List<ContractPartnerShareInput> PartnerShares { get; set; } = [];

    /// <summary>
    /// «سهم شرکت %» در فرم قرارداد شراکتی. ستون دیتابیس نیست: همان
    /// <see cref="ContractPartner.SharePercent"/>ِ مالک دفترِ شرکتِ قرارداد
    /// (<see cref="Company.OwnerPartnerId"/>) است و پیش از ذخیره به یک سطر شریک تبدیل می‌شود.
    /// خالی یعنی فرم این فیلد را نفرستاده و <see cref="PartnerShares"/> مثل قبل همهٔ شرکاست.
    /// </summary>
    [Display(Name = "سهم شرکت %")]
    public decimal? CompanySharePercent { get; set; }

    /// <summary>مالک دفترِ شرکتِ انتخاب‌شده — همیشه از دیتابیس خوانده می‌شود، نه از فرم.</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public int? CompanyOwnerPartnerId { get; set; }

    public string? CompanyName { get; set; }
    public string? ProductName { get; set; }
    public string? UnitName { get; set; }
    public string? CounterpartyName { get; set; }
}
