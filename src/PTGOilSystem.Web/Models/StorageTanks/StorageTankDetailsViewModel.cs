using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.StorageTanks;

public sealed class StorageTankDetailsViewModel
{
    public int Id { get; init; }
    public string TankCode { get; init; } = "";
    public string? DisplayName { get; init; }
    public int TerminalId { get; init; }
    public string TerminalName { get; init; } = "";
    public int? ProductId { get; init; }
    public string? ProductName { get; init; }
    public string UnitOfMeasure { get; init; } = "MT";
    public decimal CapacityMt { get; init; }
    public bool IsActive { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    public decimal CurrentQuantityMt { get; init; }
    public decimal CustodyQuantityMt { get; init; }
    public decimal PhysicalQuantityMt => CurrentQuantityMt + CustodyQuantityMt;
    public decimal TotalInQuantityMt { get; init; }
    public decimal TotalOutQuantityMt { get; init; }
    public decimal NetMovementQuantityMt { get; init; }
    public decimal FillPercent { get; init; }
    public decimal EmptyQuantityMt { get; init; }
    public int MovementCount { get; init; }
    public DateTime? LastMovementDate { get; init; }

    public IReadOnlyList<StorageTankBalanceRowViewModel> Balances { get; init; } = [];
    public IReadOnlyList<StorageTankContractStockBreakdownRowViewModel> ContractStockBreakdownRows { get; init; } = [];
    public IReadOnlyList<StorageTankMovementRowViewModel> RecentMovements { get; init; } = [];
    public IReadOnlyList<StorageTankMovementRowViewModel> Movements { get; init; } = [];

    // وضعیت تسویه/ضایعات معوق این مخزن (سناریوی «چند واگن → یک مخزن → ضایعه هنگام خالی‌شدن»).
    public StorageTankSettlementStatusViewModel SettlementStatus { get; init; } = new();
}

// خلاصهٔ وضعیت تسویهٔ نهایی و ضایعات معوق یک مخزن (فقط نمایشی/read-only).
public sealed class StorageTankSettlementStatusViewModel
{
    // مجموع موجودی مثبت قراردادهای منبع که قابل تسویه است.
    public decimal SettleableQuantityMt { get; init; }
    // تعداد قراردادهای منبع دارای موجودی مثبت در مخزن.
    public int SourceContractCount { get; init; }
    public bool IsShared => SourceContractCount > 1;

    // رسیدهای ورود به این مخزن که با حالت «ضایعات بعداً از تسویه مخزن» ثبت شده‌اند.
    public int DeferredReceiptCount { get; init; }
    // قراردادهای معوق که هنوز موجودی مثبت در مخزن دارند (یعنی هنوز تسویه نشده‌اند).
    public int PendingSettlementContractCount { get; init; }
    // مجموع موجودی معوقِ هنوز در مخزن (در انتظار تسویهٔ نهایی).
    public decimal PendingDeferredQuantityMt { get; init; }
    public bool HasPendingSettlement => PendingSettlementContractCount > 0 && PendingDeferredQuantityMt > 0m;

    // ضایعات تسویه‌شدهٔ قبلی روی این مخزن.
    public int SettlementEventCount { get; init; }
    public decimal SettledLossQuantityMt { get; init; }
    public DateTime? LastSettlementDate { get; init; }
    public bool HasMeasuredSettlement { get; init; }
    public bool HasEstimatedSettlement { get; init; }

    // واگن/بارگیری‌هایی که این مخزن را تغذیه کرده‌اند (به‌ویژه واگن‌های معوق).
    public IReadOnlyList<StorageTankFeedingLoadingRowViewModel> FeedingLoadings { get; init; } = [];
}

// یک ردیف بارگیری/واگن که موجودی وارد این مخزن کرده است.
public sealed class StorageTankFeedingLoadingRowViewModel
{
    public int LoadingReceiptId { get; init; }
    public int LoadingRegisterId { get; init; }
    public string? ContractNumber { get; init; }
    public string? TransportLabel { get; init; }   // شماره واگن / کشتی / موتر
    public string? RwbNo { get; init; }
    public string? BillOfLadingNumber { get; init; }
    public LoadingTransportType TransportType { get; init; }
    public DateTime ReceiptDate { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public ReceiptLossMode LossMode { get; init; }
    public bool IsDeferred => LossMode == ReceiptLossMode.DeferredTankSettlement;
}

public sealed class StorageTankIndexViewModel
{
    public IReadOnlyList<int> TerminalId { get; init; } = [];
    public IReadOnlyList<int> ProductId { get; init; } = [];
    public bool? IsActive { get; init; }
    public string? Query { get; init; }
    public int TotalTanks { get; init; }
    public int ActiveTanks { get; init; }
    public decimal TotalCapacityMt { get; init; }
    public decimal TotalCurrentQuantityMt { get; init; }
    public decimal TotalCustodyQuantityMt { get; init; }
    public decimal TotalPhysicalQuantityMt => TotalCurrentQuantityMt + TotalCustodyQuantityMt;
    public decimal AverageFillPercent { get; init; }
    public IReadOnlyList<StorageTankListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class StorageTankListItemViewModel
{
    public int Id { get; init; }
    public string TankCode { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string UnitOfMeasure { get; init; } = "MT";
    public decimal CapacityMt { get; init; }
    public decimal CurrentQuantityMt { get; init; }
    public decimal CustodyQuantityMt { get; init; }
    public decimal PhysicalQuantityMt => CurrentQuantityMt + CustodyQuantityMt;
    public decimal FillPercent { get; init; }
    public bool IsActive { get; init; }
    public int ContractCount { get; init; }
}

public sealed class StorageTankBalanceRowViewModel
{
    public int ProductId { get; init; }
    public string ProductCode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public decimal QuantityMt { get; init; }
}

public sealed class StorageTankMovementRowViewModel
{
    public int MovementId { get; init; }
    public DateTime MovementDate { get; init; }
    public MovementDirection Direction { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal SignedQuantityMt { get; init; }
    public decimal RunningBalanceMt { get; init; }
    public int ProductId { get; init; }
    public string ProductCode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public int TerminalId { get; init; }
    public string TerminalName { get; init; } = "";
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public int? StorageTankId { get; init; }
    public string? ReferenceDocument { get; init; }
    public string SourceName { get; init; } = "";
    // شماره و نوع وسیلهٔ ورودی («واگن: 1234») برای ستون «از کجا».
    public string? SourceTransport { get; init; }
    public string DestinationName { get; init; } = "";
    public string MovementContext { get; init; } = "";
    public string? Notes { get; init; }
}

public sealed class StorageTankContractStockBreakdownRowViewModel
{
    public int ProductId { get; init; }
    public string ProductCode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public ContractType? ContractType { get; init; }
    public decimal TotalInQuantityMt { get; init; }
    public decimal TotalOutQuantityMt { get; init; }
    public decimal BalanceQuantityMt { get; init; }
    public decimal SharePercent { get; init; }
}

// روش تقسیم ضایعهٔ مخزن بین قراردادهای منبع.
public enum TankLossAllocationMode
{
    // تقسیم خودکار به نسبت سهم دفتری هر قرارداد (پیش‌فرض و رفتار قبلی).
    [Display(Name = "تقسیم نسبتی (خودکار)")]
    Proportional = 0,
    // ورود دستی ضایعهٔ هر قرارداد وقتی مقدار واقعی هر منبع اندازه‌گیری شده باشد.
    [Display(Name = "ورود دستی هر قرارداد")]
    Manual = 1
}

// تسویهٔ نهایی مخزن: ضایعات واقعی هنگام خالی/تسویه شدن مخزن.
public sealed class StorageTankSettlementViewModel
{
    public int TankId { get; set; }
    public string TankCode { get; set; } = "";
    public string TerminalName { get; set; } = "";
    public string UnitOfMeasure { get; set; } = "MT";

    // کل موجودی دفتری مخزن (شامل قراردادهای منبع و مقدار بدون قرارداد).
    public decimal CurrentQuantityMt { get; set; }
    // مجموع موجودی قراردادهای منبع که قابل تسویه است.
    public decimal SettleableQuantityMt { get; set; }

    // روش تقسیم ضایعه؛ پیش‌فرض نسبتی تا رفتار قبلی حفظ شود.
    [Display(Name = "روش تقسیم ضایعه")]
    public TankLossAllocationMode AllocationMode { get; set; } = TankLossAllocationMode.Proportional;

    [Display(Name = "تاریخ تسویه")]
    [DataType(DataType.Date)]
    public DateTime EventDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    // مقدار واقعی باقیمانده در مخزن؛ پیش‌فرض ۰ = مخزن کاملاً خالی شده است.
    // فقط در حالت «تقسیم نسبتی» استفاده می‌شود.
    [Display(Name = "مقدار واقعی باقیمانده (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مقدار باقیمانده نمی‌تواند منفی باشد.")]
    public decimal ActualRemainingMt { get; set; }

    [Display(Name = "یادداشت تسویه")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public string? ReturnUrl { get; set; }

    public IReadOnlyList<StorageTankSettlementContractRowViewModel> ContractRows { get; set; } = [];

    // ورودی دستی ضایعهٔ هر قرارداد (فقط در حالت Manual پر و خوانده می‌شود).
    public List<StorageTankSettlementManualLossInput> ManualLosses { get; set; } = [];
}

public sealed class StorageTankSettlementContractRowViewModel
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public int ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public decimal BookBalanceMt { get; init; }
    public decimal SharePercent { get; init; }
    // ضایعهٔ پیش‌بینی‌شده وقتی مخزن کاملاً خالی شود (برابر موجودی همان قرارداد).
    public decimal ProjectedLossMt { get; init; }
}

// یک ردیف ورودی دستی برای ضایعهٔ یک قرارداد منبع در حالت Manual.
public sealed class StorageTankSettlementManualLossInput
{
    public int ContractId { get; set; }
    public int ProductId { get; set; }
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "ضایعه نمی‌تواند منفی باشد.")]
    public decimal LossMt { get; set; }
}
