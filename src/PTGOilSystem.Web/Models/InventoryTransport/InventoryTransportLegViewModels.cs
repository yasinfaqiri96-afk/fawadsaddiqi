using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.InventoryTransport;

public sealed class InventoryTransportLegCreateViewModel
{
    /// <summary>
    /// PTG-P1-05 — نسخهٔ سطری که کاربر هنگامِ بازکردنِ فرم دید. با فرم برمی‌گردد تا ذخیره
    /// روی نسخهٔ کهنه رد شود. صفر یعنی فرم نسخه نفرستاده است.
    /// </summary>
    public long Version { get; set; }

    public int Id { get; set; }

    [Display(Name = "Shipment")]
    public int? ShipmentId { get; set; }

    [Display(Name = "Source Purchase Contract")]
    public int SourcePurchaseContractId { get; set; }

    [Display(Name = "Product")]
    public int ProductId { get; set; }

    [Display(Name = "Source Terminal")]
    public int SourceTerminalId { get; set; }

    [Display(Name = "Source Tank")]
    public int? SourceStorageTankId { get; set; }

    [Display(Name = "Destination Terminal")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "Destination Tank")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "Destination Location")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "Transport Type")]
    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Wagon;

    [Display(Name = "Wagon Number")]
    [StringLength(200)]
    public string? WagonNumber { get; set; }

    [Display(Name = "RWB Number")]
    [StringLength(100)]
    public string? RwbNo { get; set; }

    [Display(Name = "Bill of Lading")]
    [StringLength(100)]
    public string? BillOfLadingNumber { get; set; }

    [Display(Name = "Route")]
    [StringLength(200)]
    public string? RouteDescription { get; set; }

    [Display(Name = "Service Provider")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "Loaded Date")]
    [DataType(DataType.Date)]
    public DateTime LoadedDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "Expected Arrival")]
    [DataType(DataType.Date)]
    public DateTime? ExpectedArrivalDate { get; set; }

    [Display(Name = "Quantity (MT)")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "Chargeable Quantity (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Chargeable quantity cannot be negative.")]
    public decimal? ChargeableQuantityMt { get; set; }

    [Display(Name = "Purchase Unit Cost USD/MT")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "Purchase unit cost must be greater than zero.")]
    public decimal? PurchaseUnitCostUsd { get; set; }

    [Display(Name = "Notes")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    // KPIهای صفحهٔ رسمی انتقال تکی؛ فقط‌نمایشی و محاسبه‌شده از منبع همان محموله.
    public decimal InboundToSourceQuantityMt { get; set; }
    public decimal OutboundFromSourceQuantityMt { get; set; }
    public decimal AvailableForTransferQuantityMt { get; set; }

    public List<InventoryTransportLegAllocationInput> Allocations { get; set; } = [];
}

public sealed class InventoryTransportLegAllocationInput
{
    [Display(Name = "Source Purchase Contract")]
    public int? SourcePurchaseContractId { get; set; }

    [Display(Name = "Product")]
    public int? ProductId { get; set; }

    [Display(Name = "Source Terminal")]
    public int? SourceTerminalId { get; set; }

    [Display(Name = "Source Tank")]
    public int? SourceStorageTankId { get; set; }

    [Display(Name = "Quantity (MT)")]
    public decimal? QuantityMt { get; set; }

    [Display(Name = "Chargeable Quantity (MT)")]
    public decimal? ChargeableQuantityMt { get; set; }

    [Display(Name = "Purchase Unit Cost USD/MT")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "Purchase unit cost must be greater than zero.")]
    public decimal? PurchaseUnitCostUsd { get; set; }

    [Display(Name = "Notes")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public bool HasAnyValue =>
        SourcePurchaseContractId.GetValueOrDefault() > 0
        || ProductId.GetValueOrDefault() > 0
        || SourceTerminalId.GetValueOrDefault() > 0
        || SourceStorageTankId.HasValue
        || QuantityMt.HasValue
        || ChargeableQuantityMt.HasValue
        || PurchaseUnitCostUsd.HasValue
        || !string.IsNullOrWhiteSpace(Notes);
}

public enum InventoryTransportSubmissionMode
{
    Draft = 0,
    Loaded = 1
}

public sealed class InventoryTransportFromInventoryViewModel
{
    public int ActiveStep { get; set; } = 1;

    // وقتی مقدار دارد، فرم در حالت ویرایش همان سند حمل پیش‌نویس است (نه ثبت جدید).
    public int? BatchId { get; set; }

    public int? ShipmentId { get; set; }

    // وقتی مرحلهٔ بعدی از یک مبدأ پیش‌پرشده ساخته می‌شود ولی کشتیِ آن مبدأ به‌طور قطعی
    // مشخص نیست (به چند کشتی وصل است)، این پرچم برای هشدار در UI روشن می‌شود. فقط نمایشی.
    public bool ShipmentLinkAmbiguous { get; set; }

    // آیا فرم در حالتِ «ادامهٔ زنجیره» است (مبدأ از قبل داده شده). برای تصمیمِ نمایشِ هشدار.
    public bool IsChainContinuation { get; set; }

    // true = محموله از بیرون (پروندهٔ محموله / لینک) داده شده و قابل تغییر نیست؛
    // false = کاربر می‌تواند در خود فرم، محموله را به‌عنوان مبدأ انتخاب یا خالی کند. فقط نمایشی.
    public bool ShipmentLocked { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "ترمینال مبدأ را انتخاب کنید.")]
    public int SourceTerminalId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "مخزن مبدأ را انتخاب کنید.")]
    public int SourceStorageTankId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "محصول را انتخاب کنید.")]
    public int ProductId { get; set; }

    [DataType(DataType.Date)]
    public DateTime TransportDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    public InventoryTransportSubmissionMode SubmissionMode { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public List<InventoryTransportSourceSelectionInput> Sources { get; set; } = [];
    public List<InventoryTransportVehicleInput> Vehicles { get; set; } = [];
}

public sealed class InventoryTransportSourceSelectionInput
{
    public int SourceInventoryMovementId { get; set; }
    public decimal? QuantityMt { get; set; }
}

public sealed class InventoryTransportVehicleInput
{
    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Truck;
    public int? TruckId { get; set; }
    public int? WagonId { get; set; }
    public int? VesselId { get; set; }
    [MaxLength(50)] public string? TruckPlateNumberInput { get; set; }
    [MaxLength(50)] public string? WagonNumberInput { get; set; }
    public int? DriverId { get; set; }
    // نام راننده به‌صورت تایپی؛ اگر راننده‌ای با همین نام نباشد هنگام ثبت پروفایلش ساخته می‌شود.
    [MaxLength(200)] public string? DriverNameInput { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal? CapacityMt { get; set; }
    // تاریخ بارگیریِ همین ردیف. خالی = تاریخ سند. یک موتر می‌تواند در یک سند چند سفر
    // با تاریخ‌های متفاوت داشته باشد؛ تکراری فقط وقتی است که موتر و تاریخ هر دو یکی باشند.
    [DataType(DataType.Date)]
    public DateTime? LoadedDate { get; set; }
    public CarrierType CarrierType { get; set; } = CarrierType.ServiceProvider;
    public int? ServiceProviderId { get; set; }
    public int? OperationalAssetId { get; set; }
    public decimal? FreightAmount { get; set; }
    public decimal? FreightRatePerMt { get; set; }
    // وزن محاسبهٔ کرایه (اختیاری): کرایه = FreightRatePerMt × FreightWeightMt.
    // اگر خالی باشد، مقدار اصلی حمل (QuantityMt) مبنای محاسبه است. فقط برای محاسبه؛ در DB ذخیره نمی‌شود.
    public decimal? FreightWeightMt { get; set; }
    public int? FreightCurrencyId { get; set; }
    [MaxLength(100)] public string? RwbNo { get; set; }
    [MaxLength(100)] public string? BillOfLadingNumber { get; set; }
    public List<InventoryTransportVehicleAllocationInput> Allocations { get; set; } = [];
}

public sealed class InventoryTransportVehicleAllocationInput
{
    public int SourceInventoryMovementId { get; set; }
    public decimal QuantityMt { get; set; }
}

public sealed class InventoryTransportModeSelectorViewModel
{
    public string ActiveMode { get; set; } = "single";
    public int? ShipmentId { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class ShipmentTransportCreateViewModel
{
    public int ShipmentId { get; set; }

    [StringLength(100)]
    public string ShipmentCode { get; set; } = "";

    [StringLength(200)]
    public string ShipmentName { get; set; } = "";

    [StringLength(200)]
    public string ProductName { get; set; } = "";

    [StringLength(200)]
    public string? GeneralDestinationName { get; set; }

    [StringLength(200)]
    public string? UnloadLocationName { get; set; }

    public int? SourceTerminalId { get; set; }
    public int? SourceStorageTankId { get; set; }

    [StringLength(100)]
    public string? SourceReceiptScopeKey { get; set; }

    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Truck;

    [DataType(DataType.Date)]
    public DateTime LoadedDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [DataType(DataType.Date)]
    public DateTime? ExpectedArrivalDate { get; set; }

    public int? ServiceProviderId { get; set; }
    public int? OperationalAssetId { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public decimal AvailableLoadingQuantityMt { get; set; }
    public decimal PreviouslyLoadedQuantityMt { get; set; }
    public decimal SourceAvailableQuantityMt { get; set; }
    public decimal AvailableForTransferQuantityMt { get; set; }
    public decimal InboundToSourceQuantityMt { get; set; }
    public decimal OutboundFromSourceQuantityMt { get; set; }
    public decimal SoldQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }

    public List<ShipmentTransportTruckRow> TruckRows { get; set; } = [];
}

public sealed class ShipmentTransportTruckRow
{
    [StringLength(100)]
    public string? TruckName { get; set; }

    [StringLength(200)]
    public string? DriverName { get; set; }

    [StringLength(50)]
    public string? PlateNumber { get; set; }

    public decimal? QuantityMt { get; set; }

    [StringLength(200)]
    public string? DestinationName { get; set; }

    public decimal? FreightUsd { get; set; }

    [StringLength(100)]
    public string? DocumentReference { get; set; }

    [DataType(DataType.Date)]
    public DateTime? LoadedDate { get; set; }
}

public sealed class ShipmentTransportLoadingSourceOption
{
    public string ScopeKey { get; set; } = "";
    public string LocationName { get; set; } = "";
    public int? TerminalId { get; set; }
    public int? StorageTankId { get; set; }
    public decimal InboundToSourceQuantityMt { get; set; }
    public decimal OutboundFromSourceQuantityMt { get; set; }
    public decimal SoldQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }
    public decimal AvailableForTransferQuantityMt { get; set; }
}

public sealed class InventoryTransportSourceAvailabilityViewModel
{
    public int SourceInventoryMovementId { get; set; }
    public int SourcePurchaseContractId { get; set; }
    public string ContractNumber { get; set; } = "";
    public int? SourceLoadingReceiptId { get; set; }
    public string ReceiptReference { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public DateTime SourceDate { get; set; }
    public int ProductId { get; set; }
    public int TerminalId { get; set; }
    public int StorageTankId { get; set; }
    public decimal AvailableQuantityMt { get; set; }
}

public sealed class InventoryTransportLookupOption
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? TerminalId { get; set; }
    public int? ProductId { get; set; }
    public int? Type { get; set; }
    public decimal? CapacityMt { get; set; }
}

public sealed class InventoryTransportLegIndexFilterViewModel
{
    [Display(Name = "از تاریخ")]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    public DateTime? ToDate { get; set; }

    [Display(Name = "قرارداد")]
    public int[] ContractId { get; set; } = [];

    [Display(Name = "جنس")]
    public int[] ProductId { get; set; } = [];

    [Display(Name = "وضعیت")]
    public InventoryTransportLegStatus? Status { get; set; }

    [Display(Name = "نمایش")]
    public string? WorkflowState { get; set; }

    [Display(Name = "نوع وسیله")]
    public LoadingTransportType[] TransportType { get; set; } = [];

    [Display(Name = "مخزن")]
    public int[] StorageTankId { get; set; } = [];

    [Display(Name = "جست‌وجو")]
    public string? Query { get; set; }
}

public sealed class InventoryTransportLegIndexViewModel
{
    public InventoryTransportLegIndexFilterViewModel Filter { get; set; } = new();
    public IReadOnlyList<InventoryTransportLegListItemViewModel> Items { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageCount { get; set; } = 1;
    public int TotalCount { get; set; }
}

public sealed class InventoryTransportFlowDashboardViewModel
{
    public IReadOnlyList<InventoryTransportFlowTransportViewModel> Transports { get; set; } = [];

    public int ActiveTransportCount => Transports.Count;
    public int TotalLegCount => Transports.Sum(t => t.LegCount);
    public int TotalContractCount => Transports
        .SelectMany(t => t.ContractAllocations)
        .Select(a => a.ContractId)
        .Distinct()
        .Count();
    public decimal TotalAllocatedQuantityMt => Transports.Sum(t => t.TotalAllocatedQuantityMt);
    public decimal TotalLoadedQuantityMt => Transports.Sum(t => t.LoadedQuantityMt);
    public decimal TotalPendingQuantityMt => Transports.Sum(t => t.PendingQuantityMt);
    public decimal TotalReceivedQuantityMt => Transports.Sum(t => t.ReceivedQuantityMt);
    public decimal TotalShortageQuantityMt => Transports.Sum(t => t.ShortageQuantityMt);
    public int TotalExpenseCount => Transports.Sum(t => t.ExpenseCount);
    public decimal TotalExpenseUsd => Transports.Sum(t => t.TotalExpenseUsd);
}

public sealed class InventoryTransportFlowTransportViewModel
{
    public string GroupKey { get; set; } = "";
    public int? ShipmentId { get; set; }
    public string? ShipmentCode { get; set; }
    public string PrimaryReference { get; set; } = "";
    public string TransportTypeName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string DestinationLabel { get; set; } = "";
    public string VehicleLabel { get; set; } = "";
    public string? RouteDescription { get; set; }
    public DateTime FirstLoadedDate { get; set; }
    public DateTime? ExpectedArrivalDate { get; set; }
    public decimal TotalAllocatedQuantityMt { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    public decimal PendingQuantityMt { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }
    public int LegCount { get; set; }
    public int ContractCount { get; set; }
    public int OutboundMovementCount { get; set; }
    public int ExpenseCount { get; set; }
    public decimal TotalExpenseUsd { get; set; }
    public string StatusText { get; set; } = "";
    public string ProgressCssClass { get; set; } = "progress-0";
    public IReadOnlyList<InventoryTransportFlowContractAllocationViewModel> ContractAllocations { get; set; } = [];
    public IReadOnlyList<InventoryTransportFlowLegItemViewModel> Legs { get; set; } = [];
    public IReadOnlyList<InventoryTransportFlowExpenseItemViewModel> Expenses { get; set; } = [];
    public int ExpenseCurrentPage { get; set; } = 1;
    public int ExpensePageCount { get; set; } = 1;
    public int ExpensePageSize { get; set; } = 10;
}

public sealed class InventoryTransportFlowContractAllocationViewModel
{
    public int ContractId { get; set; }
    public string ContractNumber { get; set; } = "";
    public decimal AllocatedQuantityMt { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    public decimal PendingQuantityMt { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }
    public decimal SharePercent { get; set; }
    public int LegCount { get; set; }
    public int ExpenseCount { get; set; }
    public decimal ExpenseAmountUsd { get; set; }
}

public sealed class InventoryTransportGroupReceiptCreateViewModel
{
    [Required]
    public string GroupKey { get; set; } = "";

    public string TransportReference { get; set; } = "";

    public int? ShipmentId { get; set; }
    public string ShipmentName { get; set; } = "";
    public string ProductName { get; set; } = "";

    public decimal TotalLoadedQuantityMt { get; set; }
    public decimal RegisteredShortageQuantityMt { get; set; }
    public decimal PreviousSalesQuantityMt { get; set; }
    public decimal PreviousReceiptQuantityMt { get; set; }
    public decimal AvailableQuantityMt { get; set; }

    [Display(Name = "تاریخ رسید")]
    [DataType(DataType.Date)]
    public DateTime ReceiptDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مقدار تخلیه‌شده")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار تخلیه‌شده باید بزرگ‌تر از صفر باشد.")]
    public decimal TotalReceivedQuantityMt { get; set; }

    [Display(Name = "ضایعات / کسری")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "ضایعات نمی‌تواند منفی باشد.")]
    public decimal TotalShortageQuantityMt { get; set; }

    [Display(Name = "تلورانس مجاز")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "تلورانس نمی‌تواند منفی باشد.")]
    public decimal? AllowanceMt { get; set; }

    [Display(Name = "ضایعات قابل مجرا")]
    public decimal? ChargeableShortageMt { get; set; }

    [Display(Name = "ترمینال مقصد")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [Display(Name = "شماره سند / مرجع")]
    [StringLength(100)]
    public string? DocumentReference { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public IReadOnlyList<InventoryTransportGroupReceiptAllocationPreviewViewModel> Allocations { get; set; } = [];
}

public sealed class InventoryTransportGroupReceiptAllocationPreviewViewModel
{
    public int ContractId { get; set; }
    public string ContractNumber { get; set; } = "";
    public decimal LoadedQuantityMt { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }
    public decimal SharePercent { get; set; }
    public int LegCount { get; set; }
}

public sealed class InventoryTransportGroupExpenseCreateViewModel
{
    [Required]
    public string GroupKey { get; set; } = "";

    public string TransportReference { get; set; } = "";

    public decimal TotalAllocatedQuantityMt { get; set; }

    [Display(Name = "نوع مصرف")]
    public int? ExpenseTypeId { get; set; }

    [Display(Name = "نوع مصرف دستی")]
    [StringLength(200)]
    public string? ManualExpenseTypeName { get; set; }

    [Display(Name = "شرکت خدماتی")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    // موتروانِ مستقل به‌عنوان طرفِ مصرف — همان قاعدهٔ ExpenseLedgerPoster.HasCounterparty.
    [Display(Name = "راننده")]
    public int? DriverId { get; set; }

    [Display(Name = "تاریخ مصرف")]
    [DataType(DataType.Date)]
    public DateTime ExpenseDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مبلغ کل")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مبلغ مصرف باید بزرگ‌تر از صفر باشد.")]
    public decimal Amount { get; set; }

    [Display(Name = "ارز")]
    [Required]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "شرح / مرجع")]
    [Required(ErrorMessage = "ثبت شرح یا مرجع برای trace مصرف الزامی است.")]
    [StringLength(1000)]
    public string Description { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public IReadOnlyList<InventoryTransportGroupExpenseAllocationPreviewViewModel> Allocations { get; set; } = [];
}

public sealed class InventoryTransportGroupExpenseAllocationPreviewViewModel
{
    public int ContractId { get; set; }
    public string ContractNumber { get; set; } = "";
    public decimal AllocatedQuantityMt { get; set; }
    public decimal SharePercent { get; set; }
    public int LegCount { get; set; }
    public decimal AllocatedAmount { get; set; }
}

// مودال چندردیفیِ «ثبت مصارف حمل» — هم‌شکلِ مودال مصارف بارگیری.
// backend روی همان مسیر موجود ExpenseTransaction + TransportLegId سوار می‌شود؛
// هیچ Entity/Migration/Ledger جدیدی ندارد. مبالغ ردیف‌ها USD پایه‌اند (مثل بارگیری).
public sealed class InventoryTransportGroupExpenseModalViewModel
{
    [Required]
    public string GroupKey { get; set; } = "";
    public int? TransportLegId { get; set; }
    public string TransportReference { get; set; } = "";
    public decimal TotalAllocatedQuantityMt { get; set; }
    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
    public List<InventoryTransportGroupExpenseModalRow> Lines { get; set; } = [];
    // مصارف فعالِ همین گروه برای نمایش read-only و جلوگیری از تکرار.
    public IReadOnlyList<InventoryTransportFlowExpenseItemViewModel> ExistingExpenses { get; set; } = [];
    // پیش‌نمایش تقسیم وزنی مصرف بین قراردادهای این گروه (مبلغ سهم در مرورگر محاسبه می‌شود).
    public IReadOnlyList<InventoryTransportGroupExpenseAllocationPreviewViewModel> Allocations { get; set; } = [];
}

public sealed class InventoryTransportGroupExpenseModalRow
{
    public int? ExpenseTypeId { get; set; }
    [StringLength(200)]
    public string? ManualExpenseTypeName { get; set; }
    public LoadingExpenseCalculationMode CalculationMode { get; set; } = LoadingExpenseCalculationMode.FixedAmount;
    public decimal? QuantityMt { get; set; }
    public decimal? UnitRateUsd { get; set; }
    public decimal AmountUsd { get; set; }
    public LoadingExpensePartyType PartyType { get; set; } = LoadingExpensePartyType.None;
    public int? ServiceProviderId { get; set; }
    public int? OperationalAssetId { get; set; }
    // موتروانِ مستقل به‌عنوان طرفِ مصرف. مصرفِ حمل معمولاً بدهی به همان راننده است، نه به
    // شرکت خدماتی؛ بدون این انتخاب سطرِ دفتر بی‌طرف‌حساب می‌ماند و از «طلبات و بدهی‌ها»
    // بیرون می‌افتد. رجوع: ExpenseLedgerPoster.HasCounterparty.
    public int? DriverId { get; set; }
    [StringLength(1000)]
    public string? Notes { get; set; }
    // فقط وقتی برای همین نوع مصرف قبلاً مصرف فعال هست، ساخت دوبارهٔ آن نیاز به انتخاب صریح دارد.
    public bool AllowDuplicate { get; set; }
}

public sealed class InventoryTransportFlowLegItemViewModel
{
    public int Id { get; set; }
    public string ContractNumber { get; set; } = "";
    public string? WagonNumber { get; set; }
    public string? RwbNo { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public decimal? PurchaseUnitCostUsd { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    public DateTime LoadedDate { get; set; }
    public InventoryTransportLegStatus Status { get; set; }
    public int? OutboundInventoryMovementId { get; set; }
}

public sealed class InventoryTransportFlowExpenseItemViewModel
{
    public int Id { get; set; }
    public int? TransportLegId { get; set; }
    public int? ContractId { get; set; }
    public string ContractNumber { get; set; } = "";
    public DateTime ExpenseDate { get; set; }
    public string ExpenseTypeName { get; set; } = "";
    public string? ServiceProviderName { get; set; }
    public string? OperationalAssetName { get; set; }
    public string? DriverName { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal AmountUsd { get; set; }
    public string? Description { get; set; }
}

public sealed class InventoryTransportLegListItemViewModel
{
    public int Id { get; set; }
    public int? ShipmentId { get; set; }
    public string? ShipmentCode { get; set; }
    public string? VesselName { get; set; }
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SourceTerminalName { get; set; } = "";
    public string? SourceTankCode { get; set; }
    public string? WagonNumber { get; set; }
    public string? RwbNo { get; set; }
    public LoadingTransportType TransportType { get; set; }
    public decimal? PurchaseUnitCostUsd { get; set; }
    public int? ServiceProviderId { get; set; }
    public string? ServiceProviderName { get; set; }
    public int? OperationalAssetId { get; set; }
    public string? OperationalAssetName { get; set; }
    public decimal QuantityMt { get; set; }
    public DateTime LoadedDate { get; set; }
    public InventoryTransportLegStatus Status { get; set; }
    public int? OutboundInventoryMovementId { get; set; }
    public decimal RemainingQuantityMt { get; set; }
    public string SourceLabel { get; set; } = "";
    public string CurrentLocationLabel { get; set; } = "";
    public string SourceContractsLabel { get; set; } = "";
    public string PhysicalStatusText { get; set; } = "";
    public string NextActionText { get; set; } = "";
}

public sealed class InventoryTransportLegDetailsViewModel
{
    public int Id { get; set; }
    public int? ShipmentId { get; set; }
    public string? ShipmentCode { get; set; }
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SourceTerminalName { get; set; } = "";
    public string? SourceTankCode { get; set; }
    public string? DestinationTerminalName { get; set; }
    public string? DestinationTankCode { get; set; }
    public string? DestinationLocationName { get; set; }
    public LoadingTransportType TransportType { get; set; }
    public string? WagonNumber { get; set; }
    public string? RwbNo { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public string? RouteDescription { get; set; }
    public int? ServiceProviderId { get; set; }
    public string? ServiceProviderName { get; set; }
    public int? OperationalAssetId { get; set; }
    public string? OperationalAssetName { get; set; }
    // راننده‌ای که هنگام ثبت حمل وارد شده است (اختیاری).
    public int? DriverId { get; set; }
    public string? DriverName { get; set; }
    public DateTime LoadedDate { get; set; }
    public DateTime? ExpectedArrivalDate { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal RemainingQuantityMt { get; set; }
    public decimal? ChargeableQuantityMt { get; set; }
    public decimal? PurchaseUnitCostUsd { get; set; }
    public InventoryTransportLegStatus Status { get; set; }
    public bool IsFreightSettled { get; set; }
    public DateTime? FreightSettledDate { get; set; }
    public int? OutboundInventoryMovementId { get; set; }
    public string? OutboundReferenceDocument { get; set; }
    public IReadOnlyList<InventoryTransportLegExpenseItemViewModel> Expenses { get; set; } = [];
    // مصارفی که مستقیم به این حمل وصل نیستند ولی از مسیر دیسپچِ موترِ ساخته‌شده از رسید همین حمل
    // به آن تعلق می‌گیرند — همان مجموعه‌ای که P&L در ExpenseTransactionsUsd می‌شمارد.
    public IReadOnlyList<InventoryTransportLegExpenseItemViewModel> DispatchExpenses { get; set; } = [];
    public IReadOnlyList<InventoryTransportLegCustomsItemViewModel> CustomsDeclarations { get; set; } = [];
    public IReadOnlyList<InventoryTransportLegLossItemViewModel> Losses { get; set; } = [];
    public InventoryTransportReceiptSummaryViewModel? DestinationReceipt { get; set; }
    // همه رسیدهای مقصد این حمل (چند تخلیه/نقل مستقیم جزئی ممکن است) — برای نمایش کامل و باقیمانده.
    public IReadOnlyList<InventoryTransportReceiptSummaryViewModel> DestinationReceipts { get; set; } = [];
    public InventoryTransportLegPnlSummaryViewModel Pnl { get; set; } = new();
    public IReadOnlyList<InventoryTransportChainItemViewModel> Chain { get; set; } = [];
    // انتقال وسیله‌به‌وسیله: بار این حمل از کدام وسیله‌ها گرفته شده و به کدام وسیله‌ها داده شده.
    public IReadOnlyList<InventoryTransportChainLinkViewModel> ChainSources { get; set; } = [];
    public IReadOnlyList<InventoryTransportChainLinkViewModel> ChainTargets { get; set; } = [];
    public string SourceContractsLabel { get; set; } = "";
    // منبع واقعی بار: هر سهم منبع (بارگیری، رسید بارگیری، مخزن یا وسیلهٔ قبلی) با مرجع و مقدارش.
    // فقط خواندنی از InventoryTransportLegAllocation؛ هیچ مقداری دوباره محاسبه نمی‌شود.
    public IReadOnlyList<InventoryTransportLegSourceItemViewModel> Sources { get; set; } = [];
    public int? CompatibilityDispatchId { get; set; }
    public int? InventoryTransportBatchId { get; set; }
    // نگهبان‌های عملِ صفحهٔ جزئیات. سرور تصمیم می‌گیرد، View فقط نمایش می‌دهد؛ وگرنه دکمه‌ای
    // دیده می‌شود که کلیکش حتماً به خطا می‌خورد.
    public bool CanEditDocument { get; set; }
    public bool CanCancelDocument { get; set; }
    // چرا ویرایش/لغو بسته است — همان فهرست عملیات پایین‌دستی، برای نمایش در tooltip.
    public string? DocumentLockReason { get; set; }
    public string? Notes { get; set; }
}

public enum InventoryTransportLegSourceKind
{
    Loading,
    LoadingReceipt,
    StorageTank,
    Vehicle
}

/// <summary>یک سهم منبع بار حمل: نوع منبع، مرجع قابل کلیک، محل، قرارداد و مقدار همان سهم.</summary>
public sealed class InventoryTransportLegSourceItemViewModel
{
    public InventoryTransportLegSourceKind Kind { get; set; }
    // شناسهٔ رکورد منبع برای لینک: LoadingRegister، LoadingReceipt، StorageTank یا InventoryTransportLeg.
    public int? SourceId { get; set; }
    public string Reference { get; set; } = "";
    public string? Place { get; set; }
    public LoadingTransportType? VehicleType { get; set; }
    public string? ContractLabel { get; set; }
    public decimal QuantityMt { get; set; }
}

/// <summary>یک یال انتقال وسیله‌به‌وسیله: وسیلهٔ طرف مقابل و مقدار همان انتقال.</summary>
public sealed class InventoryTransportChainLinkViewModel
{
    public int LegId { get; set; }
    public LoadingTransportType TransportType { get; set; }
    public string VehicleLabel { get; set; } = "";
    public decimal QuantityMt { get; set; }
    public DateTime LoadedDate { get; set; }
}

public sealed class InventoryTransportChainItemViewModel
{
    public int Id { get; set; }
    public IReadOnlyList<int> ParentLegIds { get; set; } = [];
    public LoadingTransportType TransportType { get; set; }
    public string VehicleLabel { get; set; } = "";
    public DateTime LoadedDate { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal RemainingQuantityMt { get; set; }
    public InventoryTransportLegStatus Status { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class InventoryTransportLegPnlSummaryViewModel
{
    public decimal? PurchaseUnitCostUsd { get; set; }
    public string PurchaseCostSource { get; set; } = "Missing purchase cost";
    public decimal PurchaseCostUsd { get; set; }
    public decimal ExpenseTransactionsUsd { get; set; }
    public decimal SharedShipmentExpensesUsd { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }
    public decimal CustomsUsd { get; set; }
    public decimal ReceiptFreightCostUsd { get; set; }
    public decimal ShortageChargeUsd { get; set; }
    public decimal ReceiptFreightExpenseUsd { get; set; }
    public decimal OperationalExpensesUsd { get; set; }
    public decimal TotalCostUsd { get; set; }
    public decimal SoldQuantityMt { get; set; }
    public decimal SalesUsd { get; set; }
    public decimal UnsoldQuantityMt { get; set; }
    public decimal LossQuantityMt { get; set; }
    public decimal LossCostUsd { get; set; }
    public decimal GrossMarginUsd { get; set; }
    public string SalesTraceNote { get; set; } = "No traceable sale";
    public IReadOnlyList<InventoryTransportLegPnlSaleItemViewModel> Sales { get; set; } = [];
}

public sealed class InventoryTransportLegPnlSaleItemViewModel
{
    public int SaleId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime SaleDate { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal AmountUsd { get; set; }
    public string TraceKind { get; set; } = string.Empty;
}

public sealed class InventoryTransportLegExpenseItemViewModel
{
    public int Id { get; set; }
    public DateTime ExpenseDate { get; set; }
    public string ExpenseTypeName { get; set; } = "";
    public string? ExpenseTypeCode { get; set; }
    public string? ServiceProviderName { get; set; }
    public string? OperationalAssetName { get; set; }
    public string? DriverName { get; set; }
    public decimal AmountUsd { get; set; }
    public string? Description { get; set; }
}

public sealed class InventoryTransportLegCustomsItemViewModel
{
    public int Id { get; set; }
    public DateTime DeclarationDate { get; set; }
    public string? WagonOrTruckNumber { get; set; }
    public string? DeclarationReference { get; set; }
    public decimal? ConsignmentWeightMt { get; set; }
    public decimal TotalAfn { get; set; }
    public decimal TotalUsd { get; set; }
}

public sealed class InventoryTransportLegLossItemViewModel
{
    public int Id { get; set; }
    public DateTime EventDate { get; set; }
    public string StageName { get; set; } = "";
    public decimal ExpectedQuantityMt { get; set; }
    public decimal ActualQuantityMt { get; set; }
    public decimal DifferenceQuantityMt { get; set; }
    public decimal ChargeableLossMt { get; set; }
    public string? Reference { get; set; }
}

public sealed class InventoryTransportReceiptCreateViewModel
{
    public int InventoryTransportLegId { get; set; }

    /// <summary>Remaining cargo shown when this form was opened; detects stale submissions.</summary>
    public decimal? ExpectedRemainingMt { get; set; }

    [Display(Name = "Receipt Date")]
    [DataType(DataType.Date)]
    public DateTime ReceiptDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "Received Quantity (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "Received quantity must be greater than zero.")]
    public decimal ReceivedQuantityMt { get; set; }

    [Display(Name = "Shortage Quantity (MT)")]
    [Range(typeof(decimal), "-79228162514264337593543950335", "79228162514264337593543950335", ErrorMessage = "Shortage quantity is outside the allowed range.")]
    public decimal ShortageQuantityMt { get; set; }

    [Display(Name = "Allowance (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Allowance cannot be negative.")]
    public decimal? AllowanceMt { get; set; }

    [Display(Name = "Chargeable Shortage (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Chargeable shortage cannot be negative.")]
    public decimal? ChargeableShortageMt { get; set; }

    [Display(Name = "Freight Rate (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Freight rate cannot be negative.")]
    public decimal? FreightRateUsdPerMt { get; set; }

    [Display(Name = "Freight Cost (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Freight cost cannot be negative.")]
    public decimal? FreightCostUsd { get; set; }

    // کم‌شدن کسری از کرایه اختیاری است: فقط وقتی این تیک فعال باشد، کسری از کرایه کم می‌شود.
    [Display(Name = "Deduct Shortage From Freight")]
    public bool DeductShortageFromFreight { get; set; }

    // به‌جای کسر از کرایه، خسارتِ کسری به‌عنوان بدهیِ مستقل روی حساب مسئول (راننده/شرکت خدماتی) ثبت شود.
    // با DeductShortageFromFreight هم‌زمان فعال نمی‌شود؛ اگر این فعال باشد کرایه دست‌نخورده می‌ماند.
    [Display(Name = "Shortage As Separate Debt")]
    public bool ShortageAsSeparateDebt { get; set; }

    [Display(Name = "Shortage Rate (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Shortage rate cannot be negative.")]
    public decimal? ShortageRateUsd { get; set; }

    [Display(Name = "Shortage Charge (USD)")]
    public decimal? ShortageChargeUsd { get; set; }

    [Display(Name = "Freight Payable (USD)")]
    public decimal? FreightPayableUsd { get; set; }

    [Display(Name = "Service Provider")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "Receipt Destination")]
    public InventoryTransportReceiptDestination ReceiptDestination { get; set; } = InventoryTransportReceiptDestination.ToInventory;

    [Display(Name = "Destination Terminal")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "Destination Tank")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "Customer")]
    public int? SaleCustomerId { get; set; }

    // فقط فروش گروهی به تأمین‌کننده این را پر می‌کند؛ با SaleCustomerId هم‌زمان مقدار نمی‌گیرد.
    public int? SaleSupplierId { get; set; }

    [Display(Name = "Buyer")]
    public string? SaleBuyerKey
    {
        get => PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Build(SaleCustomerId, SaleSupplierId);
        set => (SaleCustomerId, SaleSupplierId) = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Parse(value);
    }

    [Display(Name = "Invoice Number")]
    [StringLength(50)]
    public string? SaleInvoiceNumber { get; set; }

    [Display(Name = "Sale Date")]
    [DataType(DataType.Date)]
    public DateTime? SaleDate { get; set; }

    [Display(Name = "Sale Currency")]
    [StringLength(10)]
    public string SaleCurrency { get; set; } = "USD";

    [Display(Name = "Unit Price")]
    public decimal? SaleUnitPriceInCurrency { get; set; }

    /// <summary>قیمت فروش مستقیم هنوز مشخص نیست؛ فروش بدون سند مالی ثبت و بعداً قیمت‌گذاری می‌شود.</summary>
    [Display(Name = "قیمت فروش هنوز مشخص نیست")]
    public bool SaleIsPricePending { get; set; }

    [Display(Name = "FX Rate to USD")]
    public decimal? SaleAppliedFxRateToUsd { get; set; }

    // وسیلهٔ مقصدِ انتقال. پیش‌فرض موتر است تا همهٔ فراخوانی‌های فعلی بدون تغییر بمانند؛
    // واگن و کشتی از همین مسیر عمومی عبور می‌کنند و موتور تجاری جدا ندارند.
    public LoadingTransportType DirectDispatchTransportType { get; set; } = LoadingTransportType.Truck;

    [Display(Name = "Truck")]
    public int? DirectDispatchTruckId { get; set; }

    [Display(Name = "Wagon")]
    public int? DirectDispatchWagonId { get; set; }

    [Display(Name = "Wagon Number")]
    [StringLength(200)]
    public string? DirectDispatchWagonNumber { get; set; }

    [Display(Name = "Vessel")]
    public int? DirectDispatchVesselId { get; set; }

    [Display(Name = "Driver")]
    public int? DirectDispatchDriverId { get; set; }

    [Display(Name = "Dispatch Date")]
    [DataType(DataType.Date)]
    public DateTime? DirectDispatchDate { get; set; }

    [Display(Name = "Loaded Quantity (MT)")]
    public decimal? DirectDispatchLoadedQuantityMt { get; set; }

    [Display(Name = "Destination")]
    public int? DirectDispatchDestinationLocationId { get; set; }

    [Display(Name = "Ticket Serial")]
    [StringLength(100)]
    public string? DirectDispatchTicketSerialNumber { get; set; }

    // انتقال گروهی چندواگنه (فقط سرور، از فرم نمی‌آید): برای رسیدهای «همراه» یک موترِ چندواگنه
    // دیسپچ جدا ساخته نشود — دیسپچِ واحد با وزن کامل موتر روی رسید اول ثبت می‌شود.
    public bool SkipDirectDispatchRecord { get; set; }

    // انتقال گروهی چندواگنه (فقط سرور): اجازهٔ دیسپچ بزرگ‌تر از دریافتِ همین رسید
    // (وزن کامل موتر روی رسید اول؛ بقیهٔ وزن در رسیدهای همراه حساب می‌شود).
    public bool AllowDirectDispatchBeyondReceipt { get; set; }

    // «فقط تسویه» (فقط سرور، از فرم رسید/تسویه/تخلیه وسایط): کرایه/کسری ثبت می‌شود ولی
    // تخلیه‌ای رخ نمی‌دهد — ReceivedQuantityMt صفر می‌ماند، InventoryMovement ساخته نمی‌شود
    // و بار داخل وسیله برای تخلیهٔ بعدی باقی است.
    public bool SettlementOnly { get; set; }

    [Display(Name = "Notes")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public string LegLabel { get; set; } = "";
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SourceStorageTankName { get; set; } = "";
    public LoadingTransportType TransportType { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    public bool IsTruckTransport => TransportType == LoadingTransportType.Truck;
    // موتر و واگن هر دو فیلدهای تخلیه/کرایه را دارند.
    public bool IsTruckOrWagonTransport =>
        TransportType is LoadingTransportType.Truck or LoadingTransportType.Wagon;

    // فیلدهای فقط‌نمایشی (read-only) که از مرحلهٔ حمل پر می‌شوند؛ هیچ ذخیره/منطقی ندارند.
    public string? VehicleNumber { get; set; }      // نمبر موتر/وسیله (leg.WagonNumber)
    public string? DocumentReference { get; set; }  // سیمیر/CMR (leg.RwbNo یا BillOfLadingNumber)
    // کرایه‌های ثبت‌شدهٔ همین مرحلهٔ حمل (نوع TRANSPORT-RECEIPT-FREIGHT) — فقط نمایش/لینک، بدون ساخت دوباره.
    public IReadOnlyList<InventoryTransportLegExpenseItemViewModel> ExistingFreightExpenses { get; set; } = [];
    public bool HasExistingFreightExpense => ExistingFreightExpenses.Count > 0;
}

public sealed class InventoryTransportReceiptSummaryViewModel
{
    public int Id { get; set; }
    public DateTime ReceiptDate { get; set; }
    public InventoryTransportReceiptDestination ReceiptDestination { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }
    public decimal? AllowanceMt { get; set; }
    public decimal? ChargeableShortageMt { get; set; }
    public decimal? FreightRateUsdPerMt { get; set; }
    public decimal? FreightCostUsd { get; set; }
    public decimal? ShortageRateUsd { get; set; }
    public decimal? ShortageChargeUsd { get; set; }
    public decimal? FreightPayableUsd { get; set; }
    public string? ServiceProviderName { get; set; }
    public string? OperationalAssetName { get; set; }
    // طرفِ کرایه وقتی نه شرکت خدماتی است نه دارایی ملکی: رانندهٔ همان حمل — همان قاعده‌ای
    // که InventoryTransportReceiptService.SyncReceiptFreightExpenseAsync برای بدهی به کار می‌برد.
    public string? DriverName { get; set; }
    public string? DestinationTerminalName { get; set; }
    public string? DestinationTankCode { get; set; }
    public int? InventoryMovementId { get; set; }
    public int? SalesTransactionId { get; set; }
    public string? SaleInvoiceNumber { get; set; }
    public int DirectTruckDispatchCount { get; set; }
    public decimal DirectTruckDispatchedQuantityMt { get; set; }
    public int? FirstDirectTruckDispatchId { get; set; }
    public string? Notes { get; set; }
}

// صفحه «جریان کشتی»: یک حمل (گروه) را با کل سود/زیان، مراحل و سرنوشت بار در یک جا نشان می‌دهد.
public sealed class InventoryTransportJourneyViewModel
{
    public InventoryTransportFlowTransportViewModel Transport { get; set; } = new();
    public InventoryTransportJourneyPnlViewModel Pnl { get; set; } = new();
    public IReadOnlyList<InventoryTransportJourneyStageViewModel> Stages { get; set; } = [];
    public IReadOnlyList<InventoryTransportJourneySaleItemViewModel> Sales { get; set; } = [];
    public IReadOnlyList<InventoryTransportLegLossItemViewModel> Losses { get; set; } = [];
    // فهرست فقط‌خواندنیِ گمرکِ ثبت‌شده برای ردیف‌های این حمل (Phase 1 — فقط نمایش، بدون منطق ذخیره).
    public IReadOnlyList<InventoryTransportJourneyCustomsItemViewModel> Customs { get; set; } = [];
    public bool HasCustoms => Customs.Count > 0;
    public bool HasDraftAllocations { get; set; }
    public bool HasReceivableAllocations { get; set; }
    public string? ReturnUrl { get; set; }
}

// نمایش فقط‌خواندنیِ یک اظهارنامهٔ گمرکی در صفحهٔ جریان حمل (هیچ ذخیره/محاسبه‌ای ندارد).
public sealed class InventoryTransportJourneyCustomsItemViewModel
{
    public int Id { get; set; }
    public int LegId { get; set; }
    public string ContractNumber { get; set; } = "";
    public DateTime DeclarationDate { get; set; }
    public string? WagonOrTruckNumber { get; set; }
    public string? DeclarationReference { get; set; }
    public string? PermitNumber { get; set; }
    public string? GoodsName { get; set; }
    public string? CustomsType { get; set; }
    public decimal? ConsignmentWeightMt { get; set; }
    public decimal TotalAfn { get; set; }
    public decimal TotalUsd { get; set; }
    public int ItemCount { get; set; }
    public int DocumentCount { get; set; }
}

public sealed class InventoryTransportJourneyPnlViewModel
{
    public decimal PurchaseCostUsd { get; set; }
    public decimal ExpenseTransactionsUsd { get; set; }
    public decimal SharedShipmentExpensesUsd { get; set; }
    public decimal CustomsUsd { get; set; }
    public decimal ReceiptFreightExpenseUsd { get; set; }
    public decimal OperationalExpensesUsd { get; set; }
    public decimal TotalCostUsd { get; set; }
    public decimal SoldQuantityMt { get; set; }
    public decimal SalesUsd { get; set; }
    public decimal UnsoldQuantityMt { get; set; }
    public decimal LossQuantityMt { get; set; }
    public decimal LossCostUsd { get; set; }
    public decimal GrossMarginUsd { get; set; }
    public bool HasMissingPurchaseCost { get; set; }
}

public sealed class InventoryTransportJourneyStageViewModel
{
    public string Title { get; set; } = "";
    public string QuantityText { get; set; } = "";
    public string? Note { get; set; }
    public bool IsDone { get; set; }
}

public sealed class InventoryTransportJourneySaleItemViewModel
{
    public int SaleId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public DateTime SaleDate { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal AmountUsd { get; set; }
    public string TraceKind { get; set; } = "";
}

// «ثبت کلی این حمل»: از نظر UI یک عملیات گروهی است، اما هنگام ذخیره روی هر تخصیص/leg جدا
// و با همان منطق InventoryTransportReceipts/Create اعمال می‌شود (یک تراکنش واحد، بدون partial save).
public sealed class InventoryTransportGroupOperationViewModel
{
    [Required]
    public string GroupKey { get; set; } = "";

    public string TransportReference { get; set; } = "";
    public decimal TotalLoadedQuantityMt { get; set; }

    [Display(Name = "نوع عملیات")]
    public InventoryTransportReceiptDestination Mode { get; set; } = InventoryTransportReceiptDestination.ToInventory;

    [Display(Name = "تاریخ")]
    [DataType(DataType.Date)]
    public DateTime OperationDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    // مشترک — رسید به مخزن
    [Display(Name = "ترمینال مقصد")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    // مشترک — کرایه بر اساس فی‌تن (در مجموع وزن حمل ضرب می‌شود)
    [Display(Name = "فی تن کرایه (USD/MT)")]
    public decimal? FreightRateUsdPerMt { get; set; }

    [Display(Name = "فی تن کسری (USD/MT)")]
    public decimal? ShortageRateUsd { get; set; }

    // مشترک — شرکت خدماتی/دارایی برای کرایه
    [Display(Name = "شرکت خدماتی")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "دارایی عملیاتی")]
    public int? OperationalAssetId { get; set; }

    // مشترک — فروش مستقیم
    [Display(Name = "مشتری")]
    public int? SaleCustomerId { get; set; }
    public int? SaleSupplierId { get; set; }
    [Display(Name = "خریدار")]
    public string? SaleBuyerKey
    {
        get => PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Build(SaleCustomerId, SaleSupplierId);
        set => (SaleCustomerId, SaleSupplierId) = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Parse(value);
    }

    [Display(Name = "ارز فروش")]
    [StringLength(10)]
    public string SaleCurrency { get; set; } = "USD";

    [Display(Name = "قیمت واحد")]
    public decimal? SaleUnitPriceInCurrency { get; set; }

    [Display(Name = "نرخ تبدیل به USD")]
    public decimal? SaleAppliedFxRateToUsd { get; set; }

    // مشترک — دیسپچ با موتر
    [Display(Name = "مقصد دیسپچ")]
    public int? DirectDispatchDestinationLocationId { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public List<InventoryTransportGroupOperationLegRow> Legs { get; set; } = [];
}

public sealed class InventoryTransportGroupOperationLegRow
{
    public int LegId { get; set; }
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string TransportLabel { get; set; } = "";
    public LoadingTransportType TransportType { get; set; }
    public decimal QuantityMt { get; set; }

    // کرایهٔ قبلاً ثبت‌شدهٔ این حمل (مصرف نوع «کرایه حمل»). سرور-محاسبه و فقط‌نمایشی.
    // اگر مقدار داشته باشد، «کرایه نهایی» از همین خوانده می‌شود و کرایه روی رسید ذخیره نمی‌شود.
    public decimal RegisteredFreightUsd { get; set; }
    public bool HasRegisteredFreight => RegisteredFreightUsd > 0m;

    [Display(Name = "مقدار رسید")]
    public decimal ReceivedQuantityMt { get; set; }

    [Display(Name = "کسری")]
    public decimal ShortageQuantityMt { get; set; }

    [Display(Name = "تلورانس مجاز")]
    public decimal? AllowanceMt { get; set; }

    // نحوهٔ محاسبهٔ خسارتِ کسری این ردیف (وقتی کسری قابل مطالبه > 0):
    //   • DeductShortageFromFreight (پیش‌فرض) ⇒ خسارت از کرایه کم می‌شود.
    //   • ShortageAsSeparateDebt ⇒ کرایه ناخالص می‌ماند و خسارت به‌عنوان بدهیِ جدا روی حساب مسئول ثبت می‌شود.
    // هر دو هم‌زمان اجرا نمی‌شوند؛ سرویس InventoryTransportReceiptService این را تضمین می‌کند.
    [Display(Name = "کسر از کرایه")]
    public bool DeductShortageFromFreight { get; set; } = true;

    [Display(Name = "بدهی جداگانهٔ خسارت")]
    public bool ShortageAsSeparateDebt { get; set; }

    // فروش مستقیم — هر تخصیص فاکتور جدا دارد
    [Display(Name = "شماره فاکتور")]
    [StringLength(50)]
    public string? SaleInvoiceNumber { get; set; }

    // دیسپچ با موتر — هر تخصیص موتر جدا دارد
    [Display(Name = "موتر")]
    public int? DirectDispatchTruckId { get; set; }

    [Display(Name = "راننده")]
    public int? DirectDispatchDriverId { get; set; }

    [Display(Name = "سریال تکت")]
    [StringLength(100)]
    public string? DirectDispatchTicketSerialNumber { get; set; }
}

// ── انتقال گروهی از حمل‌های در جریان (واگن → موتر) ──
// فقط انتقال موجودی بین حمل‌های در جریان است: هر تخصیص (واگن → موتر) روی همان مسیر
// InventoryTransportReceiptService با ReceiptDestination=DirectDispatch سوار می‌شود؛
// هیچ موجودی/حرکت جدید و هیچ فروشی ساخته نمی‌شود (فقط TruckDispatch تولید می‌گردد).
public enum InventoryTransportTransferSplitMode
{
    // مقدار انتخاب‌شده مساوی بین موترها تقسیم می‌شود.
    Equal = 0,
    // به نسبت ظرفیت هر موتر تقسیم می‌شود.
    ByCapacity = 1,
    // مقدار هر موتر دستی وارد می‌شود.
    Manual = 2
}

public sealed class InventoryTransportGroupTransferViewModel
{
    // 1=انتخاب کشتی، 2=انتخاب واگن‌ها، 3=موترها، 4=تقسیم، 5=پیش‌نمایش
    public int ActiveStep { get; set; } = 1;

    [Display(Name = "محموله / کشتی")]
    [Range(1, int.MaxValue, ErrorMessage = "محموله (کشتی) را انتخاب کنید.")]
    public int ShipmentId { get; set; }

    public string ShipmentName { get; set; } = "";
    public string ProductName { get; set; } = "";

    [Display(Name = "تاریخ انتقال")]
    [DataType(DataType.Date)]
    public DateTime TransferDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "روش تقسیم")]
    public InventoryTransportTransferSplitMode SplitMode { get; set; } = InventoryTransportTransferSplitMode.Equal;

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    // مجموع باقیماندهٔ واگن‌های انتخاب‌شده (سرور-محاسبه، فقط‌نمایشی/راهنمای اعتبارسنجی).
    public decimal SelectedRemainingQuantityMt { get; set; }

    public List<InventoryTransportGroupTransferWagonRow> Wagons { get; set; } = [];
    public List<InventoryTransportGroupTransferTruckRow> Trucks { get; set; } = [];

    // انتقال‌های ثبت‌شدهٔ قبلی همین کشتی (رسیدهای DirectDispatch فعال) برای نمایش و لغو.
    public List<InventoryTransportGroupTransferHistoryRow> RegisteredTransfers { get; set; } = [];
}

// یک انتقال ثبت‌شده (رسید DirectDispatch + دیسپچ موتر) که در پایین فرم گروهی نمایش و لغو می‌شود.
public sealed class InventoryTransportGroupTransferHistoryRow
{
    public int ReceiptId { get; set; }
    public DateTime ReceiptDate { get; set; }
    public string WagonLabel { get; set; } = "";
    public string TruckPlateNumber { get; set; } = "";
    public string? DriverName { get; set; }
    public string? TicketSerialNumber { get; set; }
    public decimal QuantityMt { get; set; }

    // اگر روی دیسپچ این انتقال فروش ثبت شده باشد، لغو مسدود است تا اول فروش لغو شود.
    public bool HasLinkedSale { get; set; }
}

public sealed class InventoryTransportGroupTransferWagonRow
{
    public int LegId { get; set; }
    public bool Selected { get; set; }

    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string WagonLabel { get; set; } = "";
    public string? RwbNo { get; set; }

    // مقدار اولیهٔ حمل، مقدار انتقال‌شدهٔ قبلی و باقیماندهٔ واقعی (سرور-محاسبه).
    public decimal InitialQuantityMt { get; set; }
    public decimal TransferredQuantityMt { get; set; }
    public decimal RemainingQuantityMt { get; set; }
}

public sealed class InventoryTransportGroupTransferTruckRow
{
    // نمبر پلیت موتر به‌صورت متنی؛ اگر موتر با این نمبر نبود، هنگام ثبت ساخته می‌شود.
    [Display(Name = "موتر")]
    [StringLength(50)]
    public string? TruckPlateNumber { get; set; }

    // نام راننده به‌صورت متنی؛ اگر راننده با این نام نبود، هنگام ثبت ساخته می‌شود (اختیاری).
    [Display(Name = "راننده")]
    [StringLength(200)]
    public string? DriverName { get; set; }

    [Display(Name = "ظرفیت (MT)")]
    public decimal? CapacityMt { get; set; }

    // مقدار تخصیص‌یافته به این موتر (در روش دستی توسط کاربر، در بقیه سرور/کلاینت پر می‌کند).
    [Display(Name = "مقدار انتقال (MT)")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "سریال تکت")]
    [StringLength(100)]
    public string? TicketSerialNumber { get; set; }

    // شناسه‌های resolve/ساخته‌شدهٔ موتر و راننده (post-only، از فرم نمی‌آید).
    public int ResolvedTruckId { get; set; }
    public int? ResolvedDriverId { get; set; }
}

// یک قطعهٔ انتقال (واگن → موتر) که خروجی الگوریتم FIFO است و به یک رسید DirectDispatch نگاشت می‌شود.
public sealed class InventoryTransportTransferChunk
{
    public int WagonLegId { get; set; }
    public string WagonLabel { get; set; } = "";
    public int TruckRowIndex { get; set; }
    public int TruckId { get; set; }
    public string TruckLabel { get; set; } = "";
    public int? DriverId { get; set; }
    public string? TicketSerialNumber { get; set; }
    public decimal QuantityMt { get; set; }
}
