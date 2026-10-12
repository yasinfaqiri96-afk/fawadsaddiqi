using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Sales;

public static class SaleStageLabels
{
    public static string ToPersian(SaleStage stage) => stage switch
    {
        SaleStage.PreSale => "پیش‌فروش",
        SaleStage.InTransit => "فروش در مسیر",
        SaleStage.Border => "فروش مرزی",
        SaleStage.AfterCustoms => "فروش بعد از گمرک",
        SaleStage.TerminalStock => "فروش از مخزن",
        _ => stage.ToString()
    };

    public static string ToGuide(SaleStage stage) => stage switch
    {
        SaleStage.PreSale => "موجودی مخزن کم نمی‌شود و ردیابی در سطح قرارداد انجام می‌شود.",
        SaleStage.InTransit => "موجودی مخزن کم نمی‌شود و ردیابی روی قرارداد یا Shipment انجام می‌شود.",
        SaleStage.Border => "موجودی مخزن کم نمی‌شود و فروش در مرز یا کشور ثالث ردیابی می‌شود.",
        SaleStage.AfterCustoms => "موجودی مخزن کم نمی‌شود مگر فروش از موجودی واقعی ترمینال انجام شده باشد.",
        SaleStage.TerminalStock => "موجودی مخزن کم می‌شود و InventoryMovement خروجی ثبت می‌گردد.",
        _ => string.Empty
    };
}

public static class SalesContractText
{
    public const string WithoutSalesContract = "فروش فاکتوری / بدون قرارداد فروش";

    // فروش کلیِ محموله‌ای که قراردادهایش جواز متفاوت دارند؛ CompanyId=null یعنی «چند جواز»،
    // نه «بدون جواز». جواز واقعی هر سهم از قراردادهای محموله قابل استخراج است.
    public const string MultiLicense = "چند جواز";

    public static string Display(string? contractNumber)
        => string.IsNullOrWhiteSpace(contractNumber) ? WithoutSalesContract : contractNumber;
}

// وضعیت قیمت‌گذاری یک فروش از دو پرچم مستقل ساخته می‌شود: قیمت فروش (روی خودِ فروش ذخیره
// می‌شود) و قیمت خرید (از قرارداد خرید منبع خوانده می‌شود، هرگز روی فروش کپی نمی‌شود).
public enum SalePricingState
{
    Final = 0,
    AwaitingSalePrice = 1,
    AwaitingPurchasePrice = 2,
    AwaitingBothPrices = 3
}

public static class SalePricingText
{
    public const string Unknown = "نامشخص";
    public const string PendingSalePrice = "در انتظار قیمت فروش";

    public static SalePricingState Resolve(bool salePricePending, bool purchasePricePending)
        => (salePricePending, purchasePricePending) switch
        {
            (true, true) => SalePricingState.AwaitingBothPrices,
            (true, false) => SalePricingState.AwaitingSalePrice,
            (false, true) => SalePricingState.AwaitingPurchasePrice,
            _ => SalePricingState.Final
        };

    public static string Label(SalePricingState state) => state switch
    {
        SalePricingState.AwaitingSalePrice => PendingSalePrice,
        SalePricingState.AwaitingPurchasePrice => "در انتظار قیمت خرید",
        SalePricingState.AwaitingBothPrices => "در انتظار قیمت فروش و خرید",
        _ => "قیمت قطعی"
    };
}

public sealed class SalesCreateViewModel
{
    /// <summary>
    /// PTG-P1-05 — نسخهٔ سطری که کاربر هنگامِ بازکردنِ فرم دید. با فرم برمی‌گردد تا ذخیره
    /// روی نسخهٔ کهنه رد شود. صفر یعنی فرم نسخه نفرستاده است.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// PTG-P2-03 — این فروش، جایگزینِ کدام سندِ ابطال‌شده است. خالی یعنی فروشِ عادی.
    /// </summary>
    public int? CorrectedFromSaleId { get; set; }

    [Display(Name = "مرحله فروش")]
    public SaleStage SaleStage { get; set; } = SaleStage.TerminalStock;

    [Display(Name = "قرارداد فروش / قرارداد مشتری (اختیاری)")]
    public int? ContractId { get; set; }

    [Display(Name = "شرکت")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب شرکت الزامی است.")]
    public int CompanyId { get; set; }

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

    [Display(Name = "جنس")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "مقصد / بازار")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "Shipment")]
    public int? ShipmentId { get; set; }

    [Display(Name = "ترمینال مبدا")]
    public int? SourceTerminalId { get; set; }

    [Display(Name = "مخزن مبدا")]
    public int? SourceStorageTankId { get; set; }

    [Display(Name = "قرارداد خرید منبع موجودی")]
    public int? SourcePurchaseContractId { get; set; }

    [Display(Name = "مقدار (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار فروش باید بزرگ‌تر از صفر باشد.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "قیمت واحد")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت واحد باید بزرگ‌تر از صفر باشد.")]
    public decimal UnitPriceInCurrency { get; set; }

    public decimal UnitPriceUsd
    {
        get => UnitPriceInCurrency;
        set => UnitPriceInCurrency = value;
    }

    /// <summary>
    /// قیمت فروش هنوز با مشتری قطعی نشده است. فروش و خروج موجودی ثبت می‌شود، ولی هیچ سند مالی
    /// تا تعیین قیمت ساخته نمی‌شود (قیمت واحد در این حالت نادیده گرفته می‌شود).
    /// </summary>
    [Display(Name = "قیمت فروش هنوز مشخص نیست")]
    public bool IsSalePricePending { get; set; }

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "شماره فاکتور / مرجع")]
    [Required(ErrorMessage = "شماره فاکتور الزامی است.")]
    [StringLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    // Gap #4 — ticket serial + stock source type
    [Display(Name = "سریال تکت")]
    [StringLength(100)]
    public string? TicketSerialNumber { get; set; }

    [Display(Name = "منبع موجودی")]
    public PTGOilSystem.Web.Models.Entities.StockSourceType? StockSourceType { get; set; }

    [Display(Name = "تاریخ فروش")]
    [DataType(DataType.Date)]
    public DateTime SaleDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public StageLossCaptureInput Loss { get; set; } = new()
    {
        Stage = LossEventStage.SalesDifference
    };
}

public sealed class ShipmentFlowSaleCreateViewModel
{
    public int ShipmentId { get; set; }
    public string ShipmentCode { get; set; } = string.Empty;
    public string? VesselName { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string CurrentStageName { get; set; } = "در جریان حمل";

    public decimal LoadedQuantityMt { get; set; }
    public decimal RegisteredShortageQuantityMt { get; set; }
    public decimal PreviousSalesQuantityMt { get; set; }
    public decimal AvailableQuantityMt { get; set; }

    [Display(Name = "خریدار")]
    [Required(ErrorMessage = "انتخاب خریدار الزامی است.")]
    public string? BuyerKey
    {
        get => SaleBuyerKey.Build(CustomerId, SupplierId);
        set => (CustomerId, SupplierId) = SaleBuyerKey.Parse(value);
    }
    public int? CustomerId { get; set; }
    public int? SupplierId { get; set; }

    [Display(Name = "مقدار فروش (تن)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار فروش باید بزرگ‌تر از صفر باشد.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "قیمت هر تن")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت هر تن باید بزرگ‌تر از صفر باشد.")]
    public decimal UnitPriceInCurrency { get; set; }

    /// <summary>قیمت فروش هنوز مشخص نیست؛ بار و موجودی ثبت می‌شود، سند مالی پس از تعیین قیمت.</summary>
    [Display(Name = "قیمت فروش هنوز مشخص نیست")]
    public bool IsSalePricePending { get; set; }

    [Display(Name = "ارز فروش")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "تاریخ فروش")]
    [DataType(DataType.Date)]
    public DateTime SaleDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "شماره فاکتور / مرجع")]
    [Required(ErrorMessage = "شماره فاکتور الزامی است.")]
    [StringLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    [Display(Name = "مقصد / محل تحویل")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "توضیح")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public bool PrintAfterSave { get; set; }

    // قرارداد خریدی که بارِ این فروش از آن برداشته می‌شود. یک محموله می‌تواند چند قرارداد با
    // جواز/شرکت/تأمین‌کنندهٔ متفاوت داشته باشد؛ در آن حالت انتخاب قرارداد منبع الزامی است.
    [Display(Name = "قرارداد منبع بار")]
    public int? SourcePurchaseContractId { get; set; }

    // true وقتی قراردادهای محموله جواز/شرکت یا محصول یکسان ندارند و فروش سهمی معنا ندارد.
    public bool SourceContractRequired { get; set; }

    public IReadOnlyList<ShipmentFlowSaleContractRowViewModel> Contracts { get; set; } = [];
}

public sealed class ShipmentFlowSaleContractRowViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public string? CompanyName { get; init; }
    public string? ProductName { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal ShortageQuantityMt { get; init; }
    public decimal PreviousSalesQuantityMt { get; init; }
    public decimal AvailableQuantityMt { get; init; }
    public decimal? PurchaseUnitCostUsd { get; init; }
    public decimal TotalCostUsd { get; init; }
}

public sealed class SalesIndexFilterViewModel
{
    [Display(Name = "قرارداد فروش")]
    public int[] ContractId { get; set; } = [];

    [Display(Name = "شرکت")]
    public int[] CompanyId { get; set; } = [];

    [Display(Name = "مشتری")]
    public int[] CustomerId { get; set; } = [];

    [Display(Name = "جنس")]
    public int[] ProductId { get; set; } = [];

    [Display(Name = "فاکتور")]
    [StringLength(50)]
    public string? InvoiceNumber { get; set; }

    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    /// <summary>«pending» فقط فروش‌های در انتظار قیمت فروش؛ «final» فقط قیمت‌قطعی؛ خالی همه.</summary>
    [Display(Name = "وضعیت قیمت")]
    [StringLength(20)]
    public string? PriceStatus { get; set; }
}

public sealed class SalesListItemViewModel
{
    public int Id { get; init; }
    public DateTime SaleDate { get; init; }
    public SaleStage SaleStage { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string CompanyName { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string? DestinationName { get; init; }
    /// <summary>نمبر وسیله: پلاک موتری که این فروش از آن انجام شده (فروش مستقیم از موتر).</summary>
    public string? VehicleNumber { get; init; }
    public decimal QuantityMt { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal UnitPriceInCurrency { get; init; }
    public decimal TotalInCurrency { get; init; }
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal TotalUsd { get; init; }
    public bool IsSalePricePending { get; init; }

    // ستون‌های خروجی (Excel/PDF/CSV): فروش با قیمت باز خانهٔ خالی می‌گیرد، نه صفر.
    public decimal? ReportUnitPriceInCurrency => IsSalePricePending ? null : UnitPriceInCurrency;
    public decimal? ReportTotalInCurrency => IsSalePricePending ? null : TotalInCurrency;
    public decimal? ReportTotalUsd => IsSalePricePending ? null : TotalUsd;
    public string PriceStatusName => IsSalePricePending ? SalePricingText.PendingSalePrice : "قیمت قطعی";
}

public sealed class SalesIndexViewModel
{
    public SalesIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<SalesListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class SalesDetailsViewModel
{
    public int Id { get; init; }
    public int? CustomerId { get; init; }
    public bool IsSupplierBuyer { get; init; }
    public int? ContractId { get; init; }
    public int? ShipmentId { get; init; }
    public int? PreSaleOrderId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string? TicketSerialNumber { get; init; }
    public PTGOilSystem.Web.Models.Entities.StockSourceType? StockSourceType { get; init; }
    public bool IsCancelled { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string? CreatedByName { get; init; }
    public DateTime SaleDate { get; init; }
    public SaleStage SaleStage { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string? DestinationName { get; init; }
    public string? ShipmentCode { get; init; }
    public decimal QuantityMt { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal UnitPriceInCurrency { get; init; }
    public decimal TotalInCurrency { get; init; }
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal TotalUsd { get; init; }
    public decimal ReceivedUsd { get; init; }
    public decimal ReceivableBalanceUsd { get; init; }
    public decimal OverpaymentUsd { get; init; }
    public IReadOnlyList<SalesPaymentDetailsViewModel> Payments { get; init; } = [];
    // دریافت‌های همان مشتری که هنوز مانده تطبیق‌نشده دارند و می‌توانند روی این فروش بنشینند.
    public IReadOnlyList<SaleApplicableReceiptViewModel> ApplicableReceipts { get; init; } = [];
    public string? Notes { get; init; }
    public int? LedgerEntryId { get; init; }
    public string? LedgerReference { get; init; }
    public string? LedgerDescription { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public string? LedgerSideName { get; init; }
    public int? InventoryMovementId { get; init; }
    public int InventoryMovementCount { get; init; }
    public string? InventoryMovementIdsText { get; init; }
    public int? LoadingReceiptId { get; init; }
    public int? LoadingReceiptAllocationId { get; init; }
    public int? InventoryTransportLegId { get; init; }
    public int? InventoryTransportReceiptId { get; init; }
    public string? InventoryTransportReference { get; init; }
    public string? SourcePurchaseContractNumber { get; init; }
    public int? SourcePurchaseContractId { get; init; }
    public string? SourceTerminalName { get; init; }
    public string? SourceStorageTankCode { get; init; }

    // ---- وضعیت قیمت‌گذاری ----
    public bool IsSalePricePending { get; init; }
    /// <summary>دست‌کم یکی از قراردادهای خرید منبع هنوز نرخ نهایی ندارد.</summary>
    public bool IsPurchasePricePending { get; init; }
    /// <summary>قراردادهای خرید منبعی که نرخ نهایی ندارند (برای پیوند به صفحهٔ نرخ قرارداد).</summary>
    public IReadOnlyList<SalePendingPurchaseContractViewModel> PendingPurchaseContracts { get; init; } = [];
    public SalePricingState PricingState => SalePricingText.Resolve(IsSalePricePending, IsPurchasePricePending);
    public DateTime? SalePriceFixedAtUtc { get; init; }
    public DateTime? SalePriceAccountingDate { get; init; }
    public string? SalePriceFixedByName { get; init; }
    public long Version { get; init; }
}

public sealed class SalePendingPurchaseContractViewModel
{
    public int ContractId { get; init; }
    public string Label { get; init; } = string.Empty;
}

// فرم تعیین قیمت فروشی که با قیمت باز ثبت شده است. فقط قیمت/ارز/نرخ تبدیل؛ مقدار، خریدار،
// تاریخ و منبع همان سند اصلی می‌مانند و از این فرم تغییر نمی‌کنند.
public sealed class SalePriceFixViewModel
{
    public int Id { get; set; }
    public long Version { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "قیمت فی تن")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت واحد باید بزرگ‌تر از صفر باشد.")]
    public decimal? UnitPriceInCurrency { get; set; }

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    // فقط نمایشی
    public string InvoiceNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public DateTime SaleDate { get; set; }
    public decimal QuantityMt { get; set; }
}

public sealed class SalesPaymentDetailsViewModel
{
    // فقط برای ردیف‌های تطبیق پر می‌شود؛ پرداختِ مستقیمِ قدیمی ردیف تطبیق ندارد.
    public int? ApplicationId { get; init; }
    public int PaymentTransactionId { get; init; }
    public DateTime PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
    public bool IsIncoming { get; init; }
    public bool IsAdvanceApplication { get; init; }
}
