using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Inventory;

public class InventoryMovementCreateViewModel
{
    [Display(Name = "جنس")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "قرارداد خرید منبع موجودی")]
    public int? ContractId { get; set; }

    [Display(Name = "ترمینال")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب ترمینال الزامی است.")]
    public int TerminalId { get; set; }

    [Display(Name = "مخزن")]
    public int? StorageTankId { get; set; }

    [Display(Name = "ترمینال مقصد")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "نوع اصلاح موجودی")]
    public bool NegativeAdjustment { get; set; }

    [Display(Name = "دلیل اصلاح / انتقال")]
    [StringLength(700)]
    public string? Reason { get; set; }

    [Display(Name = "ارزش مجموع ورودی (دالر)")]
    [Range(typeof(decimal), "0.0001", "9999999999999999")]
    public decimal? ValueUsd { get; set; }

    [Display(Name = "حساب مقابل موجودی")]
    public int? OffsetAccountId { get; set; }

    [Display(Name = "نوع حرکت")]
    [Required(ErrorMessage = "نوع حرکت الزامی است.")]
    public MovementDirection Direction { get; set; } = MovementDirection.In;

    [Display(Name = "مقدار (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار باید بزرگ‌تر از صفر باشد.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "تاریخ حرکت")]
    [DataType(DataType.Date)]
    public DateTime MovementDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "شماره سند")]
    [StringLength(500)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class InventoryMovementListItemViewModel
{
    public int Id { get; init; }
    public DateTime MovementDate { get; init; }
    public MovementDirection Direction { get; init; }
    public decimal QuantityMt { get; init; }
    public string ProductName { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public string? ContractName { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string? StorageTankCode { get; init; }
    public string? ReferenceDocument { get; init; }
    public string? Notes { get; init; }
}

public sealed class InventoryIndexViewModel
{
    public string? Query { get; init; }
    public IReadOnlyList<InventoryMovementListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class InventoryStockSummaryIndexViewModel
{
    public IReadOnlyList<InventoryStockSummaryRowViewModel> Rows { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class InventoryStockSummaryRowViewModel
{
    public int ProductId { get; init; }
    public string ProductCode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public int TerminalId { get; init; }
    public string TerminalCode { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public decimal FreeQuantityMt { get; init; }
    public DateTime LastMovementDate { get; init; }
    public int MovementCount { get; init; }
}

public sealed class InventoryStockCardFilterViewModel
{
    [Display(Name = "جنس")]
    public int? ProductId { get; set; }

    [Display(Name = "قرارداد خرید منبع موجودی")]
    public int? ContractId { get; set; }

    [Display(Name = "ترمینال")]
    public int? TerminalId { get; set; }

    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }
}

public sealed class InventoryStockCardRowViewModel
{
    public int MovementId { get; init; }
    public DateTime MovementDate { get; init; }
    public MovementDirection Direction { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal SignedQuantityMt { get; init; }
    public decimal RunningBalanceMt { get; init; }
    public string ProductCode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string TerminalCode { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public string? ContractNumber { get; init; }
    public string? StorageTankCode { get; init; }
    public string? ReferenceDocument { get; init; }
    public string? Notes { get; init; }
}

public sealed class InventoryStockCardViewModel
{
    public InventoryStockCardFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<InventoryStockCardRowViewModel> Rows { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}
