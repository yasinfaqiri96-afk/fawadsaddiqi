using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Balance;

public sealed class ContractsBalanceFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "نوع قرارداد")]
    public ContractType[] ContractType { get; set; } = [];

    [Display(Name = "وضعیت")]
    public ContractStatus[] Status { get; set; } = [];

    [Display(Name = "جنس")]
    public int[] ProductId { get; set; } = [];

    [Display(Name = "مشتری")]
    public int[] CustomerId { get; set; } = [];

    [Display(Name = "تأمین‌کننده")]
    public int[] SupplierId { get; set; } = [];

    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Search { get; set; }
}

public sealed class CustomersBalanceFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "مشتری")]
    public int[] CustomerId { get; set; } = [];

    [Display(Name = "کشور")]
    public string[] Country { get; set; } = [];

    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Search { get; set; }
}

public sealed class SuppliersBalanceFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int[] SupplierId { get; set; } = [];

    [Display(Name = "کشور")]
    public string[] Country { get; set; } = [];

    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Search { get; set; }
}

public sealed class ContractBalanceListItemViewModel
{
    public int Id { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public string ContractTypeName { get; init; } = string.Empty;
    public string StatusName { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? ProductName { get; init; }
    public string ContractUnitText { get; init; } = "—";
    public decimal QuantityMt { get; init; }
    public int ShipmentCount { get; init; }
    public decimal TotalSalesUsd { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    public int RelatedLedgerCount { get; init; }
    public decimal BaseBalanceUsd { get; init; }
}

public sealed class ContractsBalanceViewModel
{
    public ContractsBalanceFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ContractBalanceListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    /// <summary>جمع‌های کارت‌های بالای صفحه روی همهٔ نتایجِ فیلتر (نه فقط صفحهٔ جاری).</summary>
    public decimal FilteredTotalSalesUsd { get; init; }
    public decimal FilteredTotalExpensesUsd { get; init; }
    public decimal FilteredBalanceUsd { get; init; }
}

public sealed class CustomerBalanceListItemViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Country { get; init; }
    public int RelatedContractsCount { get; init; }
    public int ActiveContractsCount { get; init; }
    public int ClosedContractsCount { get; init; }
    public decimal TotalSalesUsd { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    /// <summary>خالص پول واقعیِ دریافت‌شده از مشتری؛ از <c>PartyCashSettlementReader</c>، مستقل از فروش.</summary>
    public decimal TotalReceivedUsd { get; init; }
    public int RelatedLedgerCount { get; init; }
    public decimal BaseBalanceUsd { get; init; }
}

public sealed class CustomersBalanceViewModel
{
    public decimal FilteredTotalReceivedUsd { get; init; }
    public CustomersBalanceFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<CustomerBalanceListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    /// <summary>جمع‌های کارت‌های بالای صفحه روی همهٔ نتایجِ فیلتر (نه فقط صفحهٔ جاری).</summary>
    public decimal FilteredTotalSalesUsd { get; init; }
    public decimal FilteredTotalExpensesUsd { get; init; }
    public decimal FilteredBalanceUsd { get; init; }
}

public sealed class SupplierBalanceListItemViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Country { get; init; }
    public int RelatedContractsCount { get; init; }
    public int ActiveContractsCount { get; init; }
    public int ClosedContractsCount { get; init; }
    /// <summary>فروشی که خودِ تأمین‌کننده خریدار آن است (<c>SalesTransaction.SupplierId</c>)، نه فروشِ نفتِ قراردادش به دیگران.</summary>
    public decimal TotalSalesUsd { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    /// <summary>ارزش خرید واقعیِ بارهای قیمت‌دارِ قراردادهای خرید همین تأمین‌کننده؛ از موتور مالی قرارداد.</summary>
    public decimal TotalPurchaseUsd { get; init; }
    /// <summary>خالص پول واقعیِ پرداخت‌شده به تأمین‌کننده؛ از <c>PartyCashSettlementReader</c>.</summary>
    public decimal TotalPaidUsd { get; init; }
    public int RelatedLedgerCount { get; init; }
    public decimal BaseBalanceUsd { get; init; }
}

public sealed class SuppliersBalanceViewModel
{
    public decimal FilteredTotalPurchaseUsd { get; init; }
    public decimal FilteredTotalPaidUsd { get; init; }
    public SuppliersBalanceFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<SupplierBalanceListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    /// <summary>جمع‌های کارت‌های بالای صفحه روی همهٔ نتایجِ فیلتر (نه فقط صفحهٔ جاری).</summary>
    public decimal FilteredTotalSalesUsd { get; init; }
    public decimal FilteredTotalExpensesUsd { get; init; }
    public decimal FilteredBalanceUsd { get; init; }
}
