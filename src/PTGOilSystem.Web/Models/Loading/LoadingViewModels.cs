using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Loading;

public sealed class LoadingCreateViewModel
{
    [Display(Name = "قرارداد خرید")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب قرارداد الزامی است.")]
    public int ContractId { get; set; }

    public List<int> SelectedContractIds { get; set; } = [];

    [Display(Name = "جنس")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "مبدا / محل بارگیری")]
    public int? OriginLocationId { get; set; }

    [Display(Name = "نوع حمل و نقل")]
    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Unspecified;

    // When false, transport company / operational asset / freight columns are hidden and
    // not captured — used when the supplier provides the loading and freight itself.
    [Display(Name = "کرایه ثبت شود")]
    public bool RecordFreight { get; set; } = false;

    // Phase 1 — کرایه/مصرف این بارگیری بدوش کیست (فقط ثبت/نمایش، nullable).
    [Display(Name = "کرایه بدوش کیست")]
    public PTGOilSystem.Web.Models.Entities.CostResponsibility? FreightCostResponsibility { get; set; }

    [Display(Name = "کشتی")]
    public int? VesselId { get; set; }

    [Display(Name = "موتر")]
    public int? TruckId { get; set; }

    [Display(Name = "تاریخ بارگیری")]
    [DataType(DataType.Date)]
    public DateTime LoadingDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مقدار بارگیری‌شده (MT)")]
    public decimal LoadedQuantityMt { get; set; }

    [Display(Name = "شماره بارنامه / RWB")]
    [StringLength(100)]
    public string? BillOfLadingNumber { get; set; }

    [Display(Name = "شماره واگن / Rail ID")]
    [StringLength(200)]
    public string? WagonNumber { get; set; }

    [Display(Name = "Consignee / گیرنده")]
    [StringLength(200)]
    public string? ConsigneeName { get; set; }

    [Display(Name = "Destination / مقصد")]
    [StringLength(200)]
    public string? DestinationName { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [Display(Name = "فایل اکسل بارگیری")]
    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public bool LockContract { get; set; }

    public IFormFile? ImportWorkbookFile { get; set; }

    public string? ImportedSheetName { get; set; }

    // Large imports are posted as one compact JSON value while the browser renders only
    // the current preview page. This avoids thousands of live form controls and the
    // default form-value-count ceiling without changing the persisted loading data.
    public string? ImportedRowsJson { get; set; }

    [Display(Name = "سطرهای بارگیری")]
    public List<LoadingCreateRowViewModel> Rows { get; set; } = [];
}

public sealed record LoadingDriverOption(int Id, string FullName, string? Phone);

public sealed class LoadingCreateRowViewModel
{
    public string RowKey { get; set; } = "";

    [Display(Name = "قرارداد خرید")]
    public int? ContractId { get; set; }

    [Display(Name = "تاریخ بارگیری")]
    [DataType(DataType.Date)]
    public DateTime LoadingDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "کشتی")]
    public int? VesselId { get; set; }

    [Display(Name = "موتر")]
    public int? TruckId { get; set; }

    [Display(Name = "شماره موتر ایمپورت‌شده")]
    [StringLength(200)]
    public string? ImportedTransportReference { get; set; }

    [Display(Name = "مرجع حمل / RWB / CMR")]
    [StringLength(100)]
    public string? BillOfLadingNumber { get; set; }

    [Display(Name = "شماره واگن / Rail ID")]
    [StringLength(200)]
    public string? WagonNumber { get; set; }

    [Display(Name = "مسیر")]
    [StringLength(200)]
    public string? RouteDescription { get; set; }

    [Display(Name = "شرکت لوژستیک")]
    [StringLength(200)]
    public string? LogisticsCompanyName { get; set; }

    [Display(Name = "شرکت خدماتی / لوجستیکی")]
    public int? LogisticsServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "Consignee / گیرنده")]
    [StringLength(200)]
    public string? ConsigneeName { get; set; }

    [Display(Name = "Destination / مقصد")]
    [StringLength(200)]
    public string? DestinationName { get; set; }

    [Display(Name = "نمبر ترانزیت")]
    [StringLength(100)]
    public string? TransitNumber { get; set; }

    [Display(Name = "نام راننده")]
    [StringLength(200)]
    public string? DriverName { get; set; }

    [Display(Name = "شماره تماس راننده")]
    [StringLength(50)]
    public string? DriverPhone { get; set; }

    // «راننده آزاد»: همان فیلد نوعیت ترانسپورت (owned / free / driver). فقط حالت driver روی
    // سرور معنا دارد؛ دو حالت دیگر مثل قبل از LogisticsServiceProviderId / OperationalAssetId خوانده می‌شوند.
    [StringLength(20)]
    public string? LogisticsMode { get; set; }

    [Display(Name = "راننده")]
    public int? DriverId { get; set; }

    [Display(Name = "مقدار بارگیری‌شده (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار بارگیری باید بزرگ‌تر از صفر باشد.")]
    public decimal LoadedQuantityMt { get; set; }

    [Display(Name = "Platts")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مقدار Platts نمی‌تواند منفی باشد.")]
    public decimal? PlattsUsd { get; set; }

    [Display(Name = "قیمت بارگیری (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "قیمت بارگیری نمی‌تواند منفی باشد.")]
    public decimal? LoadingPriceUsd { get; set; }

    [Display(Name = "کرایه فی تن (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه فی تن نمی‌تواند منفی باشد.")]
    public decimal? FreightRateUsdPerMt { get; set; }

    [Display(Name = "کرایه حمل (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه حمل نمی‌تواند منفی باشد.")]
    public decimal? TransportExpenseUsd { get; set; }

    [Display(Name = "کرایه مخزن (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه مخزن نمی‌تواند منفی باشد.")]
    public decimal? WarehouseExpenseUsd { get; set; }

    [Display(Name = "سایر مصارف (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "سایر مصارف نمی‌تواند منفی باشد.")]
    public decimal? OtherExpenseUsd { get; set; }

    // Gap #2 — chargeable quantity for railway expense calculation
    [Display(Name = "مقدار قابل حساب خط آهن (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? ChargeableQuantityMt { get; set; }

    [Display(Name = "نرخ خط آهن (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? RailwayRateUsd { get; set; }

    [Display(Name = "مصارف خط آهن (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? RailwayExpenseUsd { get; set; }

    [StringLength(10)]
    public string SettlementCurrencyCode { get; set; } = "USD";
    public RubSettlementRateStatus RubRateStatus { get; set; } = RubSettlementRateStatus.NotRequired;
    public RubSettlementRatePolicy RubRatePolicy { get; set; } = RubSettlementRatePolicy.NotApplicable;

    [Display(Name = "RUB per 1 USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "RUB/USD rate must be greater than zero.")]
    public decimal? RubPerUsdRate { get; set; }

    [Display(Name = "RUB rate date")]
    [DataType(DataType.Date)]
    public DateTime? RubRateDate { get; set; }

    [Display(Name = "RUB rate source")]
    [StringLength(200)]
    public string? RubRateSource { get; set; }

    public decimal? AmountUsdAtRubLock { get; set; }
    public decimal? AmountRubAtRubLock { get; set; }

    // ارقام روبلی خوانده‌شده از فایل (قیمت فی تن و مجموع). فقط ایمپورت/نمایش.
    [Display(Name = "قیمت روبلی فی تن (RUB/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "قیمت روبلی نمی‌تواند منفی باشد.")]
    public decimal? SettlementUnitPriceRub { get; set; }

    [Display(Name = "ارزش روبلی (RUB)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "ارزش روبلی نمی‌تواند منفی باشد.")]
    public decimal? SettlementValueRub { get; set; }

    public StageLossCaptureInput Loss { get; set; } = new()
    {
        Stage = LossEventStage.LoadingDifference
    };
}

public sealed class LoadingCreateRowEditorViewModel
{
    public string RowKey { get; init; } = "row_0";
    public int Sequence { get; init; }
    public LoadingTransportType TransportType { get; init; }
    public LoadingCreateRowViewModel Row { get; init; } = new();
    public bool ShowContractSelector { get; init; }
    public bool RecordFreight { get; init; } = false;
    public IReadOnlyList<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem> ContractOptions { get; init; } = [];
}

public sealed class LoadingListItemViewModel
{
    public bool IsCancelled { get; init; }
    public int Id { get; init; }
    public int ContractId { get; init; }
    public DateTime LoadingDate { get; init; }
    public LoadingTransportType TransportType { get; init; }
    public string TransportTypeLabel { get; init; } = "";
    public string ContractName { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string ProductName { get; init; } = "";
    public string? OriginLocationName { get; init; }
    public string VehicleSummary { get; init; } = "";
    public decimal LoadedQuantityMt { get; init; }
    public decimal TotalReceivedQuantityMt { get; init; }
    public string? BillOfLadingNumber { get; init; }
    public string? WagonNumber { get; init; }
    public string? RouteDescription { get; init; }
    public int? LogisticsServiceProviderId { get; init; }
    public string? LogisticsCompanyName { get; init; }
    public string? ConsigneeName { get; init; }
    public string? DestinationName { get; init; }
    public decimal? PlattsUsd { get; init; }
    public decimal? LoadingPriceUsd { get; init; }
    public decimal? LoadingValueUsd { get; init; }
    public bool IsPricePending => !LoadingPriceUsd.HasValue || LoadingPriceUsd.Value <= 0m;
    public decimal? FreightRateUsdPerMt { get; init; }
    public decimal? TransportExpenseUsd { get; init; }
    public decimal? WarehouseExpenseUsd { get; init; }
    public decimal? OtherExpenseUsd { get; init; }
    public decimal? ChargeableQuantityMt { get; init; }
    public decimal? RailwayRateUsd { get; init; }
    public decimal? RailwayExpenseUsd { get; init; }
    public string SettlementCurrencyCode { get; init; } = "USD";
    public RubSettlementRateStatus RubRateStatus { get; init; } = RubSettlementRateStatus.NotRequired;
    public decimal? RubPerUsdRate { get; init; }
    public DateTime? RubRateDate { get; init; }
    public string? RubRateSource { get; init; }
    public decimal? AmountUsdAtRubLock { get; init; }
    public decimal? AmountRubAtRubLock { get; init; }
    public decimal? SettlementUnitPriceRub { get; init; }
    public decimal? SettlementValueRub { get; init; }
    public bool HasFileRub => SettlementValueRub.HasValue || SettlementUnitPriceRub.HasValue;
    public decimal? CurrentRubAmount => AmountRubAtRubLock;
    public string? Notes { get; init; }
}

public sealed class LoadingIndexViewModel
{
    public IReadOnlyList<LoadingListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public int? ProductId { get; init; }
    public bool WithoutReceipt { get; init; }
    public string? Query { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
}

public sealed class LoadingDetailsViewModel
{
    public int Id { get; init; }
    public DateTime LoadingDate { get; init; }
    public LoadingTransportType TransportType { get; init; }
    public string TransportTypeLabel { get; init; } = "";
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string? OriginLocationName { get; init; }
    public string? VesselName { get; init; }
    public string? TruckPlateNumber { get; init; }
    public string VehicleSummary { get; init; } = "";
    public decimal LoadedQuantityMt { get; init; }
    public string? BillOfLadingNumber { get; init; }
    public string? WagonNumber { get; init; }
    public string? RouteDescription { get; init; }
    public int? LogisticsServiceProviderId { get; init; }
    public string? LogisticsCompanyName { get; init; }
    public string? ConsigneeName { get; init; }
    public string? DestinationName { get; init; }
    public string? TransitNumber { get; init; }
    public string? DriverName { get; init; }
    public string? DriverPhone { get; init; }
    public decimal? PlattsUsd { get; init; }
    public decimal? LoadingPriceUsd { get; init; }
    public decimal? LoadingValueUsd { get; init; }
    public bool IsPricePending => !LoadingPriceUsd.HasValue || LoadingPriceUsd.Value <= 0m;
    public decimal? FreightRateUsdPerMt { get; init; }
    public decimal? TransportExpenseUsd { get; init; }
    public decimal? WarehouseExpenseUsd { get; init; }
    public decimal? OtherExpenseUsd { get; init; }
    public string? Notes { get; init; }
    public decimal TotalReceivedQuantityMt { get; init; }
    public decimal RemainingToReceiveMt { get; init; }
    public int ActiveTransportCount { get; init; }
    public int? FirstActiveTransportLegId { get; init; }
    public bool CanRegisterReceipt { get; init; }
    public decimal? ChargeableQuantityMt { get; init; }
    public decimal? RailwayRateUsd { get; init; }
    public decimal? RailwayExpenseUsd { get; init; }
    public string SettlementCurrencyCode { get; init; } = "USD";
    public RubSettlementRateStatus RubRateStatus { get; init; } = RubSettlementRateStatus.NotRequired;
    public decimal? RubPerUsdRate { get; init; }
    public DateTime? RubRateDate { get; init; }
    public string? RubRateSource { get; init; }
    public decimal? AmountUsdAtRubLock { get; init; }
    public decimal? AmountRubAtRubLock { get; init; }
    public decimal? SettlementUnitPriceRub { get; init; }
    public decimal? SettlementValueRub { get; init; }
    public bool HasFileRub => SettlementValueRub.HasValue || SettlementUnitPriceRub.HasValue;
    public DateTime? RubRateLockedAtUtc { get; init; }
    public string? RubRateLockedByUserName { get; init; }
    public LoadingRubleRateEditViewModel RubleRateEditor { get; init; } = new();
    public LoadingExpenseEditViewModel ExpenseEditor { get; init; } = new();
    public LoadingReceiptCreateViewModel? ReceiptEditor { get; init; }
    public IReadOnlyList<LoadingReceiptListItemViewModel> ReceiptItems { get; init; } = [];
    public IReadOnlyList<LossEventSummaryItem> LossItems { get; init; } = [];
    public IReadOnlyList<LoadingCustomsSummaryItem> CustomsItems { get; init; } = [];

    // Presentation-ready totals for Details. Keeping these aggregates outside
    // Razor prevents the view from becoming a second financial calculation path.
    public decimal LoadingExpenseTotalUsd { get; set; }
    public decimal CustomsTotalUsd { get; set; }
    public decimal ChargeableLossTotalMt { get; set; }
    public decimal LoadingCostsGrandTotalUsd { get; set; }

    // کرایه: مسئول، حالت ترانسپورت و طرف‌حساب واقعی هر سند (نه یک نام عمومی برای همهٔ سطرها).
    public CostResponsibility? FreightCostResponsibility { get; init; }
    public string? FreightTransportModeLabel { get; set; }
    public int? DriverId { get; init; }
    public IReadOnlyList<LoadingFreightPartyItem> FreightParties { get; set; } = [];
}

/// <summary>
/// یک سطر «طرف‌حساب کرایه» در جزئیات بارگیری. سند مصرف رسمی ⇒ طلب طرف‌حساب در دفتر کل؛
/// کرایهٔ داخلی دارایی ملکی ⇒ فقط گزارش مدیریتی، بدون بدهی بیرونی.
/// </summary>
public sealed class LoadingFreightPartyItem
{
    public string PartyKindLabel { get; init; } = "";
    public string PartyName { get; init; } = "";
    public int? ServiceProviderId { get; init; }
    public int? DriverId { get; init; }
    public int? ExpenseTransactionId { get; init; }
    public string ExpenseTypeName { get; init; } = "";
    public decimal? QuantityMt { get; init; }
    public decimal? RateUsdPerMt { get; init; }
    public decimal AmountUsd { get; init; }
    public bool IsInternal { get; init; }
}

public sealed class LoadingRubleRateEditViewModel
{
    public int Id { get; set; }
    public string ContractNumber { get; set; } = "";
    public decimal LoadedQuantityMt { get; set; }
    public decimal? LoadingPriceUsd { get; set; }
    public decimal? LoadingValueUsd { get; set; }
    public string SettlementCurrencyCode { get; set; } = "USD";
    public RubSettlementRateStatus RubRateStatus { get; set; } = RubSettlementRateStatus.Pending;

    [Display(Name = "RUB per 1 USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "RUB/USD rate must be greater than zero.")]
    public decimal? RubPerUsdRate { get; set; }

    [Display(Name = "RUB rate date")]
    [DataType(DataType.Date)]
    public DateTime? RubRateDate { get; set; }

    [Display(Name = "RUB rate source")]
    [StringLength(200)]
    public string? RubRateSource { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class LoadingExpenseEditViewModel
{
    public int Id { get; set; }
    public DateTime LoadingDate { get; set; }
    public LoadingTransportType TransportType { get; set; }
    public string TransportTypeLabel { get; set; } = "";
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string VehicleSummary { get; set; } = "";
    public decimal LoadedQuantityMt { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public string? WagonNumber { get; set; }

    [Display(Name = "شرکت خدماتی")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "دارایی عملیاتی ملکی")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "کرایه حمل (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه حمل نمی‌تواند منفی باشد.")]
    public decimal? TransportExpenseUsd { get; set; }

    [Display(Name = "کرایه حمل هر MT (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه حمل هر واحد نمی‌تواند منفی باشد.")]
    public decimal? TransportRateUsd { get; set; }

    [Display(Name = "مصارف مخزن (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مصارف مخزن نمی‌تواند منفی باشد.")]
    public decimal? WarehouseExpenseUsd { get; set; }

    [Display(Name = "سایر مصارف (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "سایر مصارف نمی‌تواند منفی باشد.")]
    public decimal? OtherExpenseUsd { get; set; }

    [Display(Name = "مقدار قابل حساب خط آهن (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مقدار قابل حساب نمی‌تواند منفی باشد.")]
    public decimal? ChargeableQuantityMt { get; set; }

    [Display(Name = "کرایه هر MT خط آهن (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه هر واحد نمی‌تواند منفی باشد.")]
    public decimal? RailwayRateUsd { get; set; }

    [Display(Name = "مجموع کرایه خط آهن (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مجموع کرایه خط آهن نمی‌تواند منفی باشد.")]
    public decimal? RailwayExpenseUsd { get; set; }

    // ---- Row-based loading expenses (new modal model) ----
    public string? RwbNo { get; set; }
    public string? DestinationName { get; set; }
    public string? RouteDescription { get; set; }

    public List<LoadingExpenseLineInputModel> Lines { get; set; } = [];

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

/// <summary>
/// One editable expense row inside the loading expenses modal. Maps to a
/// <see cref="PTGOilSystem.Web.Models.Entities.LoadingExpenseLine"/> on save.
/// </summary>
public sealed class LoadingExpenseLineInputModel
{
    public int Id { get; set; }

    [Display(Name = "نوع مصرف")]
    public int ExpenseTypeId { get; set; }

    [Display(Name = "روش محاسبه")]
    public LoadingExpenseCalculationMode CalculationMode { get; set; } = LoadingExpenseCalculationMode.FixedAmount;

    [Display(Name = "مقدار")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مقدار نمی‌تواند منفی باشد.")]
    public decimal? QuantityMt { get; set; }

    [Display(Name = "نرخ فی تن")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "نرخ فی تن نمی‌تواند منفی باشد.")]
    public decimal? UnitRateUsd { get; set; }

    [Display(Name = "مبلغ کل")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مبلغ کل نمی‌تواند منفی باشد.")]
    public decimal AmountUsd { get; set; }

    [Display(Name = "طرف حساب")]
    public LoadingExpensePartyType PartyType { get; set; } = LoadingExpensePartyType.None;

    [Display(Name = "شرکت خدماتی")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "دارایی ملکی")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "توضیح")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    // Hidden links carried for seeded/existing lines so save can update in place.
    public int? ExpenseTransactionId { get; set; }
    public int? AssetRentTransactionId { get; set; }
}

public sealed class LoadingPriceEditViewModel
{
    public int Id { get; set; }
    public DateTime LoadingDate { get; set; }
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string VehicleSummary { get; set; } = "";
    public decimal LoadedQuantityMt { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public string? WagonNumber { get; set; }

    [Display(Name = "قیمت نهایی بارگیری (USD/MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت بارگیری باید بزرگ‌تر از صفر باشد.")]
    public decimal? LoadingPriceUsd { get; set; }

    [Display(Name = "یادداشت قیمت")]
    [StringLength(1000)]
    public string? PricingNote { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class LoadingEditViewModel
{
    /// <summary>
    /// PTG-P1-05 — نسخهٔ سطری که کاربر هنگامِ بازکردنِ فرم دید. با فرم برمی‌گردد تا ذخیره
    /// روی نسخهٔ کهنه رد شود. صفر یعنی فرم نسخه نفرستاده است.
    /// </summary>
    public long Version { get; set; }

    public int Id { get; set; }

    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string VehicleSummary { get; set; } = "";

    [Display(Name = "وزن بارگیری‌شده (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "وزن بارگیری باید بزرگ‌تر از صفر باشد.")]
    public decimal LoadedQuantityMt { get; set; }

    // مقدار رسیدشده — فقط برای نمایش حداقل مجاز هنگام اصلاح وزن (در کنترلر دوباره محاسبه می‌شود)
    public decimal TotalReceivedQuantityMt { get; set; }

    [Display(Name = "قیمت فی تن (USD/MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت بارگیری باید بزرگ‌تر از صفر باشد.")]
    public decimal? LoadingPriceUsd { get; set; }

    [Display(Name = "تاریخ بارگیری")]
    [Required(ErrorMessage = "تاریخ بارگیری اجباری است.")]
    public DateTime LoadingDate { get; set; }

    [Display(Name = "شماره بارنامه")]
    [StringLength(100)]
    public string? BillOfLadingNumber { get; set; }

    [Display(Name = "شماره RWB")]
    [StringLength(100)]
    public string? RwbNo { get; set; }

    [Display(Name = "شماره واگن")]
    [StringLength(200)]
    public string? WagonNumber { get; set; }

    [Display(Name = "مسیر")]
    [StringLength(200)]
    public string? RouteDescription { get; set; }

    [Display(Name = "گیرنده / Consignee")]
    [StringLength(200)]
    public string? ConsigneeName { get; set; }

    [Display(Name = "مقصد")]
    [StringLength(200)]
    public string? DestinationName { get; set; }

    [Display(Name = "نمبر ترانزیت")]
    [StringLength(100)]
    public string? TransitNumber { get; set; }

    [Display(Name = "نام راننده")]
    [StringLength(200)]
    public string? DriverName { get; set; }

    [Display(Name = "شماره تماس راننده")]
    [StringLength(50)]
    public string? DriverPhone { get; set; }

    [Display(Name = "نام شرکت لجستیک (دستی)")]
    [StringLength(200)]
    public string? LogisticsCompanyName { get; set; }

    // کرایه — تغییر هرکدام سند مصرف کرایهٔ همین بارگیری را هماهنگ می‌کند (لغو + ثبت دوباره).
    [Display(Name = "مسئول کرایه")]
    public CostResponsibility? FreightCostResponsibility { get; set; }

    [Display(Name = "کرایه فی تن (USD/MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "کرایه فی تن نمی‌تواند منفی باشد.")]
    public decimal? FreightRateUsdPerMt { get; set; }

    [Display(Name = "شرکت خدماتی طرف‌حساب کرایه")]
    public int? LogisticsServiceProviderId { get; set; }

    /// <summary>
    /// نوعیت ترانسپورت: free (شرکت خدماتی) / driver (راننده آزاد) / owned (ترانسپورت شخصی شرکت) /
    /// none. خالی (فرم قدیمی) ⇒ همان نوعیت فعلی بارگیری حفظ می‌شود.
    /// </summary>
    [Display(Name = "نوعیت ترانسپورت")]
    [StringLength(20)]
    public string? LogisticsMode { get; set; }

    [Display(Name = "دارایی ملکی (ترانسپورت شخصی)")]
    public int? OperationalAssetId { get; set; }

    /// <summary>مصارف این بارگیری از «ثبت مصارف» (ردیفی) مدیریت می‌شود؛ فیلدهای کرایه اینجا اعمال نمی‌شوند.</summary>
    public bool HasExpenseLines { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class LoadingCustomsSummaryItem
{
    public int Id { get; init; }
    public DateTime DeclarationDate { get; init; }
    public string? DeclarationReference { get; init; }
    public string? WagonOrTruckNumber { get; init; }
    public decimal TotalAfn { get; init; }
    public decimal TotalUsd { get; init; }
    // AFN-equivalent calculated server-side (USD * rate). Mirrors CustomsPermitTurnover logic.
    public decimal TotalAfnEquivalent { get; set; }
}

public sealed class LoadingReceiptListItemViewModel
{
    public int Id { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string TerminalName { get; init; } = "";
    public string? StorageTankCode { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public string? ReferenceDocument { get; init; }
    public int InventoryMovementId { get; init; }

    // رسید لغوشده در فهرست دیده می‌شود اما در هیچ جمع یا محاسبه‌ای شمرده نمی‌شود.
    public bool IsCancelled { get; init; }
    public DateTime? CancelledAtUtc { get; init; }
    public string? CancellationReason { get; init; }
    public string? CancelledByUserName { get; init; }
    public uint RowVersion { get; init; }
}

public sealed class LoadingReceiptIndexItemViewModel
{
    public int Id { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string ContractName { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string ProductName { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public string? StorageTankCode { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    /// <summary>نمبر وسیلهٔ بارگیریِ این رسید: پلاک موتر یا شمارهٔ واگن/بارنامه.</summary>
    public string? VehicleNumber { get; init; }
    public string? ReferenceDocument { get; init; }
    public bool IsCancelled { get; init; }
    public string? CancellationReason { get; init; }
}

public sealed class LoadingReceiptIndexViewModel
{
    public IReadOnlyList<LoadingReceiptIndexItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public string? Query { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
}

public sealed class LoadingReceiptCreateViewModel
{
    public int LoadingRegisterId { get; set; }

    // مسیر «اصلاح مقدار»: رسید قبلی در همان تراکنش لغو و رسید اصلاح‌شده ثبت می‌شود تا
    // Audit Trail کامل بماند (هیچ فیلد اثرگذاری با Update ساده تغییر نمی‌کند).
    public int? CorrectionOfReceiptId { get; set; }

    [Display(Name = "دلیل اصلاح")]
    [StringLength(500)]
    public string? CorrectionReason { get; set; }

    [Display(Name = "مقصد رسید")]
    public LoadingReceiptDestination ReceiptDestination { get; set; } = LoadingReceiptDestination.ToInventory;

    // نحوهٔ ثبت ضایعات: معوق تا تسویهٔ نهایی مخزن (پیش‌فرض) یا حالا معلوم.
    [Display(Name = "نحوهٔ ثبت ضایعات")]
    public ReceiptLossMode LossMode { get; set; } = ReceiptLossMode.DeferredTankSettlement;

    [Display(Name = "مقصد دقیق Allocation")]
    public LoadingReceiptAllocationDestination AllocationDestination { get; set; } = LoadingReceiptAllocationDestination.ToInventory;

    [Display(Name = "تاریخ رسید / تخلیه")]
    [DataType(DataType.Date)]
    public DateTime ReceiptDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    // Gap #3 — arrival date tracking
    [Display(Name = "تاریخ رسیدن (ورود به مقصد)")]
    [DataType(DataType.Date)]
    public DateTime? ArrivalDate { get; set; }

    [Display(Name = "تاریخ نشت / خرابی")]
    [DataType(DataType.Date)]
    public DateTime? LeakDate { get; set; }

    [Display(Name = "مقدار واقعی رسیده (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? ActualArrivedQuantityMt { get; set; }

    [Display(Name = "ترمینال")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب ترمینال الزامی است.")]
    public int TerminalId { get; set; }

    [Display(Name = "مخزن")]
    public int? StorageTankId { get; set; }

    [Display(Name = "ترمینال مقصد")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "موقعیت / شهر مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "نام مقصد Allocation")]
    [StringLength(200)]
    public string? AllocationDestinationName { get; set; }

    [Display(Name = "مرجع مقصد")]
    [StringLength(500)]
    public string? DestinationReference { get; set; }

    [Display(Name = "مشتری فروش مستقیم")]
    public int? SaleCustomerId { get; set; }
    public int? SaleSupplierId { get; set; }
    [Display(Name = "خریدار فروش مستقیم")]
    public string? SaleBuyerKey
    {
        get => PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Build(SaleCustomerId, SaleSupplierId);
        set => (SaleCustomerId, SaleSupplierId) = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Parse(value);
    }

    [Display(Name = "تاریخ فروش مستقیم")]
    [DataType(DataType.Date)]
    public DateTime? SaleDate { get; set; }

    [Display(Name = "ارز فروش مستقیم")]
    [StringLength(10)]
    public string SaleCurrency { get; set; } = "USD";

    [Display(Name = "قیمت واحد فروش مستقیم")]
    public decimal? SaleUnitPriceInCurrency { get; set; }

    public decimal? SaleUnitPriceUsd
    {
        get => SaleUnitPriceInCurrency;
        set => SaleUnitPriceInCurrency = value;
    }

    /// <summary>قیمت فروش مستقیم هنوز مشخص نیست؛ فروش بدون سند مالی ثبت و بعداً قیمت‌گذاری می‌شود.</summary>
    [Display(Name = "قیمت فروش هنوز مشخص نیست")]
    public bool SaleIsPricePending { get; set; }

    [Display(Name = "نرخ تبدیل فروش مستقیم به USD")]
    public decimal? SaleAppliedFxRateToUsd { get; set; }

    [Display(Name = "فاکتور فروش مستقیم")]
    [StringLength(50)]
    public string? SaleInvoiceNumber { get; set; }

    [Display(Name = "یادداشت فروش مستقیم")]
    [StringLength(1000)]
    public string? SaleNotes { get; set; }

    [Display(Name = "نمبر پلیت موتر")]
    public int? DirectTruckId { get; set; }

    [Display(Name = "راننده")]
    public int? DirectDriverId { get; set; }

    [Display(Name = "نمبر پلیت موتر جدید")]
    [StringLength(50)]
    public string? DirectTruckPlateNumber { get; set; }

    [Display(Name = "نام راننده جدید")]
    [StringLength(200)]
    public string? DirectDriverName { get; set; }

    [Display(Name = "تاریخ دیسپچ موتر")]
    [DataType(DataType.Date)]
    public DateTime? DirectDispatchDate { get; set; }

    [Display(Name = "سریال تکت موتر")]
    [StringLength(100)]
    public string? DirectTruckTicketSerialNumber { get; set; }

    [Display(Name = "مقدار دریافت‌شده (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار رسید باید بزرگ‌تر از صفر باشد.")]
    public decimal ReceivedQuantityMt { get; set; }

    [Display(Name = "مرجع رسید")]
    [StringLength(500)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public DateTime LoadingDate { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    public decimal AlreadyReceivedQuantityMt { get; set; }
    public decimal RemainingToReceiveMt { get; set; }
    public decimal? LoadingPriceUsd { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public string? RwbNo { get; set; }
    public string? WagonNumber { get; set; }
    public string? VesselName { get; set; }
    public string? TruckPlateNumber { get; set; }
    public string? SupplierName { get; set; }
    public string? CustomerName { get; set; }
    public string? ConsigneeName { get; set; }
    public string? DestinationName { get; set; }

    // Phase 1 — «کرایه بدوش کیست» از بارگیری والد، فقط نمایش read-only (رسید فیلد جداگانه ندارد).
    public PTGOilSystem.Web.Models.Entities.CostResponsibility? SourceFreightCostResponsibility { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public StageLossCaptureInput Loss { get; set; } = new()
    {
        Stage = LossEventStage.ReceiptShortage
    };
}

public enum BulkReceiptLossMode
{
    None = 0,
    ImmediateKnownLoss = (int)ReceiptLossMode.ImmediateKnownLoss,
    DeferredTankSettlement = (int)ReceiptLossMode.DeferredTankSettlement
}

public sealed class LoadingReceiptBulkCreateViewModel
{
    [Display(Name = "قرارداد")]
    [Range(1, int.MaxValue, ErrorMessage = "قرارداد معتبر نیست.")]
    public int ContractId { get; set; }

    [Display(Name = "بارگیری‌های انتخاب‌شده")]
    public List<int> LoadingRegisterIds { get; set; } = [];

    [Display(Name = "تاریخ رسید")]
    [DataType(DataType.Date)]
    public DateTime ReceiptDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "ترمینال رسید")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب ترمینال رسید الزامی است.")]
    public int TerminalId { get; set; }

    [Display(Name = "مخزن")]
    public int? StorageTankId { get; set; }

    [Display(Name = "مقدار کل رسید شده (MT)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار کل رسید باید بزرگ‌تر از صفر باشد.")]
    public decimal TotalReceivedQuantityMt { get; set; }

    [Display(Name = "نحوه ثبت ضایعات")]
    public BulkReceiptLossMode LossMode { get; set; } = BulkReceiptLossMode.None;

    [Display(Name = "ضایعات کل (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "ضایعات کل نمی‌تواند منفی باشد.")]
    public decimal? TotalLossQuantityMt { get; set; }

    [Display(Name = "تلورانس مجاز ضایعات (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "تلورانس ضایعات نمی‌تواند منفی باشد.")]
    public decimal? TotalLossToleranceQuantityMt { get; set; }

    [Display(Name = "مسئول ضایعات")]
    [StringLength(200)]
    public string? LossResponsiblePartyName { get; set; }

    [Display(Name = "شماره رسید / مرجع")]
    [StringLength(500)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    // رسید جمعی چند-سناریو: هر بارگیری یک سرنوشت مستقل دارد (به مخزن / فروش مستقیم / انتقال / ارسال با موتر).
    // مقدار «Total» مشترک کنار رفته؛ هر خط مقدار صریح خودش را دارد.
    public List<LoadingReceiptBulkLineInput> Lines { get; set; } = [];

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

// یک بارگیری انتخاب‌شده در رسید جمعی + سرنوشت بار آن. فیلدها هم‌نام رسید تکی تا منطق reuse شود.
public sealed class LoadingReceiptBulkLineInput
{
    public int LoadingRegisterId { get; set; }

    [Display(Name = "انتخاب‌شده")]
    public bool Selected { get; set; }

    [Display(Name = "مقدار رسید (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal ReceivedQuantityMt { get; set; }

    [Display(Name = "مقصد / سرنوشت بار")]
    public LoadingReceiptAllocationDestination Destination { get; set; } = LoadingReceiptAllocationDestination.ToInventory;

    // رسید به مخزن
    [Display(Name = "مخزن")]
    public int? StorageTankId { get; set; }

    // انتقال / ارسال با موتر — مقصد
    [Display(Name = "ترمینال مقصد")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "موقعیت / شهر مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "نام مقصد")]
    [StringLength(200)]
    public string? DestinationName { get; set; }

    [Display(Name = "مرجع مقصد")]
    [StringLength(500)]
    public string? DestinationReference { get; set; }

    // فروش مستقیم
    [Display(Name = "مشتری فروش مستقیم")]
    public int? SaleCustomerId { get; set; }
    public int? SaleSupplierId { get; set; }
    [Display(Name = "خریدار فروش مستقیم")]
    public string? SaleBuyerKey
    {
        get => PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Build(SaleCustomerId, SaleSupplierId);
        set => (SaleCustomerId, SaleSupplierId) = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Parse(value);
    }

    [Display(Name = "تاریخ فروش مستقیم")]
    [DataType(DataType.Date)]
    public DateTime? SaleDate { get; set; }

    [Display(Name = "ارز فروش مستقیم")]
    [StringLength(10)]
    public string SaleCurrency { get; set; } = "USD";

    [Display(Name = "قیمت واحد فروش مستقیم")]
    public decimal? SaleUnitPriceInCurrency { get; set; }

    [Display(Name = "نرخ تبدیل فروش مستقیم به USD")]
    public decimal? SaleAppliedFxRateToUsd { get; set; }

    [Display(Name = "فاکتور فروش مستقیم")]
    [StringLength(50)]
    public string? SaleInvoiceNumber { get; set; }

    [Display(Name = "یادداشت فروش مستقیم")]
    [StringLength(1000)]
    public string? SaleNotes { get; set; }

    // ارسال با موتر
    [Display(Name = "نمبر پلیت موتر")]
    public int? DirectTruckId { get; set; }

    [Display(Name = "نمبر پلیت موتر جدید")]
    [StringLength(50)]
    public string? DirectTruckPlateNumber { get; set; }

    [Display(Name = "راننده")]
    public int? DirectDriverId { get; set; }

    [Display(Name = "نام راننده جدید")]
    [StringLength(200)]
    public string? DirectDriverName { get; set; }

    [Display(Name = "تاریخ حرکت موتر")]
    [DataType(DataType.Date)]
    public DateTime? DirectDispatchDate { get; set; }

    [Display(Name = "سریال تکت موتر")]
    [StringLength(100)]
    public string? DirectTruckTicketSerialNumber { get; set; }

    // کسری / ضایعات — فقط برای رسید به مخزن
    [Display(Name = "ثبت ضایعات همین رسید")]
    public bool LossEnabled { get; set; }

    [Display(Name = "ضایعات (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? LossQuantityMt { get; set; }

    [Display(Name = "تلورانس ضایعات (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? LossToleranceQuantityMt { get; set; }

    [Display(Name = "مسئول ضایعات")]
    [StringLength(200)]
    public string? LossResponsiblePartyName { get; set; }

    [Display(Name = "مرجع خط")]
    [StringLength(500)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت خط")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class LoadingReceiptAllocationLineInput
{
    [Display(Name = "مقصد")]
    public LoadingReceiptAllocationDestination Destination { get; set; } = LoadingReceiptAllocationDestination.ToInventory;

    [Display(Name = "مقدار (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "ترمینال")]
    public int? TerminalId { get; set; }

    [Display(Name = "مخزن")]
    public int? StorageTankId { get; set; }

    [Display(Name = "ترمینال مقصد")]
    public int? DestinationTerminalId { get; set; }

    [Display(Name = "مخزن مقصد")]
    public int? DestinationStorageTankId { get; set; }

    [Display(Name = "موقعیت / شهر مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "نام مقصد")]
    [StringLength(200)]
    public string? DestinationName { get; set; }

    [Display(Name = "مرجع مقصد")]
    [StringLength(500)]
    public string? DestinationReference { get; set; }

    [Display(Name = "مشتری فروش مستقیم")]
    public int? SaleCustomerId { get; set; }
    public int? SaleSupplierId { get; set; }
    [Display(Name = "خریدار فروش مستقیم")]
    public string? SaleBuyerKey
    {
        get => PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Build(SaleCustomerId, SaleSupplierId);
        set => (SaleCustomerId, SaleSupplierId) = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.Parse(value);
    }

    [Display(Name = "تاریخ فروش مستقیم")]
    [DataType(DataType.Date)]
    public DateTime? SaleDate { get; set; }

    [Display(Name = "ارز فروش مستقیم")]
    [StringLength(10)]
    public string SaleCurrency { get; set; } = "USD";

    [Display(Name = "قیمت واحد فروش مستقیم")]
    public decimal? SaleUnitPriceInCurrency { get; set; }

    public decimal? SaleUnitPriceUsd
    {
        get => SaleUnitPriceInCurrency;
        set => SaleUnitPriceInCurrency = value;
    }

    /// <summary>قیمت فروش مستقیم هنوز مشخص نیست؛ فروش بدون سند مالی ثبت و بعداً قیمت‌گذاری می‌شود.</summary>
    [Display(Name = "قیمت فروش هنوز مشخص نیست")]
    public bool SaleIsPricePending { get; set; }

    [Display(Name = "نرخ تبدیل فروش مستقیم به USD")]
    public decimal? SaleAppliedFxRateToUsd { get; set; }

    [Display(Name = "فاکتور فروش مستقیم")]
    [StringLength(50)]
    public string? SaleInvoiceNumber { get; set; }

    [Display(Name = "یادداشت فروش مستقیم")]
    [StringLength(1000)]
    public string? SaleNotes { get; set; }

    [Display(Name = "مرجع خط")]
    [StringLength(500)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت خط")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class LoadingReceiptEditViewModel
{
    public int Id { get; set; }
    public int LoadingRegisterId { get; set; }
    public string? ReturnUrl { get; set; }

    // Gap #3 — arrival tracking (editable)
    [Display(Name = "تاریخ ورود به مقصد")]
    [DataType(DataType.Date)]
    public DateTime? ArrivalDate { get; set; }

    [Display(Name = "تاریخ نشت / خرابی")]
    [DataType(DataType.Date)]
    public DateTime? LeakDate { get; set; }

    [Display(Name = "مقدار واقعی رسیده (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? ActualArrivedQuantityMt { get; set; }

    [Display(Name = "مرجع رسید")]
    [StringLength(500)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    // read-only context shown in the form
    public DateTime ReceiptDate { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string? WagonNumber { get; set; }
    public string? BillOfLadingNumber { get; set; }
}

public sealed class LoadingReceiptDetailsViewModel
{
    public int Id { get; init; }
    public int LoadingRegisterId { get; init; }

    // وضعیت لغو رسید؛ رکورد حذف نمی‌شود و دلیل/تاریخ/کاربر لغو قابل مشاهده می‌ماند.
    public bool IsCancelled { get; init; }
    public DateTime? CancelledAtUtc { get; init; }
    public string? CancellationReason { get; init; }
    public string? CancelledByUserName { get; init; }
    public uint RowVersion { get; init; }
    public LoadingReceiptDestination ReceiptDestination { get; init; } = LoadingReceiptDestination.ToInventory;
    public DateTime ReceiptDate { get; init; }
    // Gap #3 — arrival tracking
    public DateTime? ArrivalDate { get; init; }
    public DateTime? LeakDate { get; init; }
    public decimal? ActualArrivedQuantityMt { get; init; }
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public DateTime LoadingDate { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal TotalReceivedQuantityMt { get; init; }
    public decimal RemainingToReceiveMt { get; init; }
    public string TerminalName { get; init; } = "";
    public string? StorageTankCode { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public string? BillOfLadingNumber { get; init; }
    public string? WagonNumber { get; init; }
    public string? ConsigneeName { get; init; }
    public string? DestinationName { get; init; }
    public string? ReferenceDocument { get; init; }
    public string? Notes { get; init; }
    public int InventoryMovementId { get; init; }
    public IReadOnlyList<LoadingReceiptAllocationSummaryViewModel> Allocations { get; init; } = [];
    public decimal TotalAllocatedQuantityMt => Allocations.Sum(a => a.QuantityMt);
    public decimal AllocationDifferenceMt => ReceivedQuantityMt - TotalAllocatedQuantityMt;
    public IReadOnlyList<LossEventSummaryItem> LossItems { get; init; } = [];
}

public sealed class LoadingReceiptAllocationSummaryViewModel
{
    public int Id { get; init; }
    public LoadingReceiptAllocationDestination Destination { get; init; } = LoadingReceiptAllocationDestination.ToInventory;
    public LoadingReceiptAllocationStatus Status { get; init; } = LoadingReceiptAllocationStatus.TraceOnly;
    public decimal QuantityMt { get; init; }
    public string? SourcePurchaseContractNumber { get; init; }
    public string TerminalName { get; init; } = "";
    public string? StorageTankCode { get; init; }
    public string? DestinationTerminalName { get; init; }
    public string? DestinationStorageTankCode { get; init; }
    public string? DestinationLocationName { get; init; }
    public string? DestinationName { get; init; }
    public string? DestinationReference { get; init; }
    public int? InventoryMovementId { get; init; }
    public int? TruckDispatchId { get; init; }
    public int DirectTruckDispatchCount { get; init; }
    public decimal DirectTruckDispatchedQuantityMt { get; init; }
    public decimal DirectTruckDispatchRemainingQuantityMt => Math.Max(QuantityMt - DirectTruckDispatchedQuantityMt, 0m);
    public int? SalesTransactionId { get; init; }
    public string? ReferenceDocument { get; init; }
    public string? Notes { get; init; }
}

public sealed class LossEventSummaryItem
{
    public int Id { get; init; }
    public LossEventStage Stage { get; init; }
    public DateTime EventDate { get; init; }
    public decimal DifferenceQuantityMt { get; init; }
    public decimal ToleranceQuantityMt { get; init; }
    public decimal AllowableLossMt { get; init; }
    public decimal ChargeableLossMt { get; init; }
    public string? ResponsiblePartyName { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
}

// ── حذف گروهی بارگیری‌های اشتباه ایمپورت‌شده ──────────────────────────────────
// فقط برای پاک‌کردن یک ایمپورت اشتباه است، نه یک ابزار حذف عمومی. هر ردیفی که
// پایین‌دستش کاری انجام شده (رسید، اظهارنامه، کنترل کیفیت، ضایعه، مصرف/کرایه فعال)
// «قفل» می‌شود و هرگز حذف نمی‌گردد؛ دلیل قفل روی همان ردیف نشان داده می‌شود.

public sealed class LoadingBulkDeleteViewModel
{
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    // پنجرهٔ زمان ثبت (CreatedAtUtc) — «همان ایمپورتی که چند دقیقه پیش انجام شد».
    public DateTime? ImportedFrom { get; init; }
    public DateTime? ImportedTo { get; init; }
    // فقط ردیف‌هایی که از اکسل آمده‌اند (ImportUniqueKey دارند).
    public bool OnlyImported { get; init; } = true;
    public bool HasSearched { get; init; }
    public IReadOnlyList<LoadingBulkDeleteRowViewModel> Rows { get; init; } = [];
    public string? ReturnUrl { get; init; }

    public int DeletableCount => Rows.Count(r => r.CanDelete);
    public int BlockedCount => Rows.Count(r => !r.CanDelete);
    public decimal DeletableQuantityMt => Rows.Where(r => r.CanDelete).Sum(r => r.LoadedQuantityMt);
}

public sealed class LoadingBulkDeleteRowViewModel
{
    public int Id { get; init; }
    public DateTime LoadingDate { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string VehicleSummary { get; init; } = "";
    public string? BillOfLadingNumber { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal? LoadingValueUsd { get; init; }
    public bool IsImported { get; init; }
    // دلایل قفل‌شدن ردیف؛ خالی یعنی حذف امن است.
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public bool CanDelete => Blockers.Count == 0;
}

public sealed class LoadingBulkDeleteResultViewModel
{
    public int DeletedCount { get; init; }
    public int SkippedCount { get; init; }
    public IReadOnlyList<string> SkippedMessages { get; init; } = [];
}
