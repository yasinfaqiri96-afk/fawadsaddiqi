using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Dispatch;

public sealed class DispatchCreateViewModel
{
    /// <summary>
    /// PTG-P1-05 — نسخهٔ سطری که کاربر هنگامِ بازکردنِ فرم دید. با فرم برمی‌گردد تا ذخیره
    /// روی نسخهٔ کهنه رد شود. صفر یعنی فرم نسخه نفرستاده است.
    /// </summary>
    public long Version { get; set; }

    [Display(Name = "قرارداد")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب قرارداد الزامی است.")]
    public int ContractId { get; set; }

    [Display(Name = "جنس")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "موتر")]
    public int TruckId { get; set; }

    [Display(Name = "راننده")]
    public int? DriverId { get; set; }

    [Display(Name = "نمبر پلیت موتر جدید")]
    [StringLength(50)]
    public string? TruckPlateNumberInput { get; set; }

    [Display(Name = "نام راننده جدید")]
    [StringLength(200)]
    public string? DriverNameInput { get; set; }

    [Display(Name = "ترمینال مبدا")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب ترمینال مبدا الزامی است.")]
    public int SourceTerminalId { get; set; }

    [Display(Name = "مخزن مبدا")]
    public int? SourceStorageTankId { get; set; }

    [Display(Name = "مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "Service Provider")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "تاریخ دیسپچ")]
    [DataType(DataType.Date)]
    public DateTime DispatchDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "وزن بارگیری‌شده (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "وزن بارگیری‌شده باید بزرگ‌تر از صفر باشد.")]
    public decimal LoadedQuantityMt { get; set; }

    [Display(Name = "وزن تخلیه‌شده (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "وزن تخلیه‌شده نمی‌تواند منفی باشد.")]
    public decimal? DischargedQuantityMt { get; set; }

    [Display(Name = "تلورانس (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "تلورانس نمی‌تواند منفی باشد.")]
    public decimal? AllowanceMt { get; set; }

    // تفاوت علامت‌دار: عدد مثبت = کسری، عدد منفی = اضافه‌بار (تخلیهٔ بیشتر از بارگیری).
    // بازه عمداً منفی را می‌پذیرد تا اضافه‌بار در فرم قابل ثبت باشد و در گزارش گم نشود.
    [Display(Name = "کسری یا اضافه‌بار (MT)")]
    [Range(typeof(decimal), "-79228162514264337593543950335", "79228162514264337593543950335", ErrorMessage = "مقدار تفاوت خارج از بازه مجاز است.")]
    public decimal? ShortageMt { get; set; }

    [Display(Name = "کرایه (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه نمی‌تواند منفی باشد.")]
    public decimal? FreightCostUsd { get; set; }

    [Display(Name = "نرخ کسری (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "نرخ کسری نمی‌تواند منفی باشد.")]
    public decimal? ShortageRateUsd { get; set; }

    [Display(Name = "کرایه قابل پرداخت (USD)")]
    public decimal? FreightPayableUsd { get; set; }

    // Gap #4 — ticket serial number
    [Display(Name = "سریال تکت")]
    [StringLength(100)]
    public string? TicketSerialNumber { get; set; }

    // Gap #5 — tolerance and chargeable shortage
    [Display(Name = "تلورانس مجاز (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "تلورانس مجاز نمی‌تواند منفی باشد.")]
    public decimal? ToleranceMt { get; set; }

    [Display(Name = "کسری قابل کسر (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کسری قابل کسر نمی‌تواند منفی باشد.")]
    public decimal? ChargeableShortageMt { get; set; }

    [Display(Name = "مرجع")]
    [StringLength(500)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class DispatchDirectFromReceiptCreateViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Loading receipt allocation is required.")]
    public int LoadingReceiptAllocationId { get; set; }

    [Display(Name = "موتر")]
    public int TruckId { get; set; }

    [Display(Name = "راننده")]
    public int? DriverId { get; set; }

    [Display(Name = "نمبر پلیت موتر جدید")]
    [StringLength(50)]
    public string? TruckPlateNumberInput { get; set; }

    [Display(Name = "نام راننده جدید")]
    [StringLength(200)]
    public string? DriverNameInput { get; set; }

    [Display(Name = "مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "Service Provider")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "تاریخ دیسپچ")]
    [DataType(DataType.Date)]
    public DateTime DispatchDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "وزن بارگیری‌شده (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "وزن بارگیری‌شده باید بزرگ‌تر از صفر باشد.")]
    public decimal LoadedQuantityMt { get; set; }

    [Display(Name = "وزن تخلیه‌شده (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "وزن تخلیه‌شده نمی‌تواند منفی باشد.")]
    public decimal? DischargedQuantityMt { get; set; }

    [Display(Name = "تلورانس (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "تلورانس نمی‌تواند منفی باشد.")]
    public decimal? AllowanceMt { get; set; }

    // تفاوت علامت‌دار: عدد مثبت = کسری، عدد منفی = اضافه‌بار (تخلیهٔ بیشتر از بارگیری).
    // بازه عمداً منفی را می‌پذیرد تا اضافه‌بار در فرم قابل ثبت باشد و در گزارش گم نشود.
    [Display(Name = "کسری یا اضافه‌بار (MT)")]
    [Range(typeof(decimal), "-79228162514264337593543950335", "79228162514264337593543950335", ErrorMessage = "مقدار تفاوت خارج از بازه مجاز است.")]
    public decimal? ShortageMt { get; set; }

    [Display(Name = "کرایه (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه نمی‌تواند منفی باشد.")]
    public decimal? FreightCostUsd { get; set; }

    [Display(Name = "نرخ کسری (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "نرخ کسری نمی‌تواند منفی باشد.")]
    public decimal? ShortageRateUsd { get; set; }

    [Display(Name = "کرایه قابل پرداخت (USD)")]
    public decimal? FreightPayableUsd { get; set; }

    [Display(Name = "سریال تکت")]
    [StringLength(100)]
    public string? TicketSerialNumber { get; set; }

    [Display(Name = "تلورانس مجاز (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "تلورانس مجاز نمی‌تواند منفی باشد.")]
    public decimal? ToleranceMt { get; set; }

    [Display(Name = "کسری قابل کسر (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کسری قابل کسر نمی‌تواند منفی باشد.")]
    public decimal? ChargeableShortageMt { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public int LoadingReceiptId { get; set; }
    public int LoadingRegisterId { get; set; }
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SourceTerminalName { get; set; } = "";
    public string? DestinationSummary { get; set; }
    public string AllocationStatusName { get; set; } = "";
    public decimal AllocationQuantityMt { get; set; }
    public decimal TotalDirectDispatchedQuantityMt { get; set; }
    public decimal RemainingQuantityMt { get; set; }
}

public sealed class DispatchDirectFromReceiptSaleCreateViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Truck dispatch is required.")]
    public int TruckDispatchId { get; set; }

    [Display(Name = "خریدار")]
    [Required(ErrorMessage = "Buyer is required.")]
    public string? BuyerKey
    {
        get => PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Build(CustomerId, SupplierId);
        set => (CustomerId, SupplierId) = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Parse(value);
    }
    public int? CustomerId { get; set; }
    public int? SupplierId { get; set; }

    [Display(Name = "تاریخ فروش")]
    [DataType(DataType.Date)]
    public DateTime SaleDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مقدار فروش (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "Sale quantity must be greater than zero.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "واحد پول")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "FX rate must be greater than zero.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "قیمت واحد")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "Unit price must be greater than zero.")]
    public decimal UnitPriceInCurrency { get; set; }

    /// <summary>قیمت فروش هنوز مشخص نیست؛ بار و موجودی ثبت می‌شود، سند مالی پس از تعیین قیمت.</summary>
    [Display(Name = "قیمت فروش هنوز مشخص نیست")]
    public bool IsSalePricePending { get; set; }

    [Display(Name = "فاکتور")]
    [Required(ErrorMessage = "Invoice number is required.")]
    [StringLength(50)]
    public string InvoiceNumber { get; set; } = "";

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public int? LoadingReceiptAllocationId { get; set; }
    public int? LoadingReceiptId { get; set; }
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string TruckPlateNumber { get; set; } = "";
    public string? DriverName { get; set; }
    public string? DestinationName { get; set; }
    public decimal DispatchLoadedQuantityMt { get; set; }

    /// <summary>وزن واقعیِ تخلیه‌شده؛ تا وقتی تخلیه ثبت نشده null است.</summary>
    public decimal? DispatchDischargedQuantityMt { get; set; }

    /// <summary>مبنای فروش = وزن تخلیه‌شده (اگر ثبت شده)، وگرنه وزن بارگیری.</summary>
    public decimal DispatchSellableQuantityMt { get; set; }

    /// <summary>اضافه‌بارِ قابل فروش = تخلیه منهای بارگیری (فقط وقتی مثبت باشد).</summary>
    public decimal DispatchSurplusMt { get; set; }

    /// <summary>مجموع فروش‌های لغونشدهٔ قبلیِ همین موتر (مبنای محاسبهٔ باقی‌مانده).</summary>
    public decimal DispatchAlreadySoldQuantityMt { get; set; }

    /// <summary>باقی‌ماندهٔ قابل فروش = مقدار تخلیه‌شده منهای فروش‌های فعال قبلی.</summary>
    public decimal DispatchRemainingQuantityMt { get; set; }
}

public sealed class DispatchUnloadViewModel
{
    public int TruckDispatchId { get; set; }

    [Display(Name = "تاریخ تخلیه")]
    [DataType(DataType.Date)]
    public DateTime ReceiptDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "ترمینال مقصد")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب ترمینال مقصد الزامی است.")]
    public int DestinationTerminalId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "وزن تخلیه‌شده (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "وزن تخلیه‌شده باید بزرگ‌تر از صفر باشد.")]
    public decimal DischargedQuantityMt { get; set; }

    // تفاوت علامت‌دار: عدد مثبت = کسری، عدد منفی = اضافه‌بار (تخلیهٔ بیشتر از بارگیری).
    // بازه عمداً منفی را می‌پذیرد تا اضافه‌بار در فرم قابل ثبت باشد و در گزارش گم نشود.
    [Display(Name = "کسری یا اضافه‌بار (MT)")]
    [Range(typeof(decimal), "-79228162514264337593543950335", "79228162514264337593543950335", ErrorMessage = "مقدار تفاوت خارج از بازه مجاز است.")]
    public decimal? ShortageMt { get; set; }

    [Display(Name = "حواکت / تلورانس مجاز (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "حواکت نمی‌تواند منفی باشد.")]
    public decimal? AllowanceMt { get; set; }

    [Display(Name = "کرایه ناخالص (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه نمی‌تواند منفی باشد.")]
    public decimal? FreightCostUsd { get; set; }

    [Display(Name = "نرخ کسری (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "نرخ کسری نمی‌تواند منفی باشد.")]
    public decimal? ShortageRateUsd { get; set; }

    [Display(Name = "کسری قابل مجرا (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کسری قابل مجرا نمی‌تواند منفی باشد.")]
    public decimal? ChargeableShortageMt { get; set; }

    [Display(Name = "پول مجرا / قابل پرداخت توسط راننده (USD)")]
    public decimal? DriverShortageChargeUsd { get; set; }

    [Display(Name = "کرایه قابل پرداخت به راننده (USD)")]
    public decimal? FreightPayableUsd { get; set; }

    [Display(Name = "Service Provider")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "تحویل‌گیرنده")]
    [StringLength(200)]
    public string? ReceivedBy { get; set; }

    [Display(Name = "مرجع سند / تکت تخلیه")]
    [StringLength(500)]
    public string? DocumentReference { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string TruckPlateNumber { get; set; } = "";
    public string? DriverName { get; set; }
    public string? DestinationName { get; set; }
    public string? SourceTerminalName { get; set; }
    public string? SourceStorageTankCode { get; set; }
    public string? ExistingDeliveryReference { get; set; }
    public DateTime DispatchDate { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    public TruckDispatchMode DispatchMode { get; set; }
}

public sealed class DispatchDeliveryReceiptItemViewModel
{
    public int Id { get; init; }
    public DateTime ReceiptDate { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public string? ReceivedBy { get; init; }
    public string? DocumentReference { get; init; }
}

public sealed class DispatchIndexFilterViewModel
{
    [Display(Name = "موتر")]
    public int[] TruckId { get; set; } = [];

    [Display(Name = "جنس")]
    public int[] ProductId { get; set; } = [];

    [Display(Name = "قرارداد")]
    public int[] ContractId { get; set; } = [];

    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }
}

public sealed class DispatchListItemViewModel
{
    public int Id { get; init; }
    public DateTime DispatchDate { get; init; }
    public string TruckPlateNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ContractName { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string? DriverName { get; init; }
    public string? DestinationName { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal? ShortageMt { get; init; }
    public decimal? FreightCostUsd { get; init; }
    public int? ServiceProviderId { get; init; }
    public string? ServiceProviderName { get; init; }
    public int? OperationalAssetId { get; init; }
    public string? OperationalAssetName { get; init; }
    public string StatusName { get; init; } = "";
}

public sealed class DispatchIndexViewModel
{
    public DispatchIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<DispatchListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class DispatchDetailsViewModel
{
    public int Id { get; init; }
    public TruckDispatchMode DispatchMode { get; init; } = TruckDispatchMode.FromInventory;
    public int? LoadingReceiptAllocationId { get; init; }
    public int? LoadingReceiptId { get; init; }
    public int? LoadingRegisterId { get; init; }
    public DateTime DispatchDate { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string TruckPlateNumber { get; init; } = "";
    public string? DriverName { get; init; }
    public string? DestinationName { get; init; }
    public int? ServiceProviderId { get; init; }
    public string? ServiceProviderName { get; init; }
    public int? OperationalAssetId { get; init; }
    public string? OperationalAssetName { get; init; }
    public string StatusName { get; init; } = "";
    public decimal LoadedQuantityMt { get; init; }
    public decimal? DischargedQuantityMt { get; init; }
    public decimal? AllowanceMt { get; init; }
    public decimal? ShortageMt { get; init; }
    public decimal? FreightCostUsd { get; init; }
    public decimal? ShortageRateUsd { get; init; }
    public decimal? FreightPayableUsd { get; init; }
    public decimal? PayableUsd { get; init; }
    public string? TicketSerialNumber { get; init; }
    public decimal? ToleranceMt { get; init; }
    public decimal? ChargeableShortageMt { get; init; }
    public string? Notes { get; init; }
    public string? SourceTerminalName { get; init; }
    public string? SourceStorageTankCode { get; init; }
    public string? InventoryReference { get; init; }
    public int? SalesTransactionId { get; init; }
    public string? SaleInvoiceNumber { get; init; }
    public decimal? SaleQuantityMt { get; init; }
    public decimal? SaleTotalUsd { get; init; }

    /// <summary>مجموع فروش‌های لغونشدهٔ این موتر (شامل فروش‌های قسمتی).</summary>
    public decimal SoldQuantityMt { get; init; }

    /// <summary>باقی‌ماندهٔ قابل فروش = مقدار تخلیه‌شده منهای فروش‌های فعال.</summary>
    public decimal RemainingSellableQuantityMt { get; init; }
    public decimal? AllocationQuantityMt { get; init; }
    public decimal? AllocationTotalDirectDispatchedQuantityMt { get; init; }
    public decimal? AllocationRemainingQuantityMt { get; init; }
    public decimal? DriverShortageChargeUsd { get; init; }
    public string? DeliveryInventoryReference { get; init; }
    public IReadOnlyList<DispatchDeliveryReceiptItemViewModel> DeliveryReceipts { get; init; } = [];
    public IReadOnlyList<DispatchCustomsItemViewModel> Customs { get; init; } = [];
    public IReadOnlyList<DispatchExpenseItemViewModel> Expenses { get; init; } = [];
    public decimal DeliveryReceivedTotalMt { get; init; }
    public decimal CustomsTotalUsd { get; init; }
    public decimal ExpenseTotalUsd { get; init; }
}

public sealed class DispatchCustomsItemViewModel
{
    public int Id { get; init; }
    public DateTime DeclarationDate { get; init; }
    public string? DeclarationReference { get; init; }
    public string? WagonOrTruckNumber { get; init; }
    public decimal TotalAfn { get; init; }
    public decimal TotalUsd { get; init; }
}

public sealed class DispatchExpenseItemViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = "";
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "";
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
}
