using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Custody;

namespace PTGOilSystem.Web.Models.Custody;

public static class CustodyParty
{
    public static string Key(AccountingPartyType type, int id) => $"{(int)type}:{id}";

    public static bool TryParse(string? key, out AccountingPartyType type, out int id)
    {
        type = default;
        id = 0;
        var parts = (key ?? "").Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var rawType)
            || !Enum.IsDefined(typeof(AccountingPartyType), rawType)
            || !int.TryParse(parts[1], out id)
            || id <= 0)
        {
            id = 0;
            return false;
        }
        type = (AccountingPartyType)rawType;
        return true;
    }

    public static string TypeLabel(AccountingPartyType type) => type switch
    {
        AccountingPartyType.Company => "شرکت",
        AccountingPartyType.Customer => "مشتری",
        AccountingPartyType.Supplier => "فروشنده",
        AccountingPartyType.Partner => "شریک",
        AccountingPartyType.ServiceProvider => "خدمات‌دهنده",
        _ => "طرف‌حساب"
    };

    public static string GroupLabel(AccountingPartyType type) => type switch
    {
        AccountingPartyType.Company => "شرکت‌ها",
        AccountingPartyType.Customer => "مشتریان",
        AccountingPartyType.Supplier => "فروشندگان",
        AccountingPartyType.Partner => "شرکا",
        AccountingPartyType.ServiceProvider => "خدمات‌دهندگان",
        _ => "سایر"
    };
}

/// <summary>محل به شکل «ترمینال:مخزن» (مخزن صفر = بدون مخزن).</summary>
public static class CustodyLocationKey
{
    public static string Key(int terminalId, int? tankId) => $"{terminalId}:{tankId ?? 0}";

    public static CustodyLocation? Parse(string? key)
    {
        var parts = (key ?? "").Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var terminalId) || terminalId <= 0
            || !int.TryParse(parts[1], out var tankId) || tankId < 0)
        {
            return null;
        }
        return new CustodyLocation(terminalId, tankId == 0 ? null : tankId);
    }
}

public sealed record CustodyLocationOption(string Key, string Label);

public sealed class CustodyIndexViewModel
{
    public string Tab { get; init; } = "others";
    public string? OwnerKey { get; init; }
    public int? ProductId { get; init; }
    public IReadOnlyList<CustodyLotSummary> Lots { get; init; } = [];
    public IReadOnlyList<CompanyStockAtThirdParty> AtThirdParties { get; init; } = [];
    public IReadOnlyList<PhysicalTankPosition> Physical { get; init; } = [];
    public IReadOnlyList<CustodyOwnerOption> Owners { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> Products { get; init; } = [];
}

public sealed class CustodyReceiveForm
{
    public string QuantityUnit { get; set; } = "MT";
    public decimal? QuantityLiters { get; set; }
    public decimal? DensityKgPerM3 { get; set; }
    public decimal? VolumeTemperatureC { get; set; }
    public decimal? DensityTemperatureC { get; set; }
    [MaxLength(500)] public string? MeasurementReference { get; set; }
    /// <summary>پر = رسیدِ بارِ در راه.</summary>
    public int? LotId { get; set; }

    public string? OwnerKey { get; set; }
    public int? ProductId { get; set; }

    /// <summary>تیل هنوز نرسیده؛ فقط بارِ در راه ثبت شود.</summary>
    public bool InTransitOnly { get; set; }

    public string? LocationKey { get; set; }

    [DataType(DataType.Date)]
    public DateTime Date { get; set; }

    public decimal? QuantityMt { get; set; }

    [DataType(DataType.Date)]
    public DateTime? LoadedDate { get; set; }
    public decimal? LoadedQuantityMt { get; set; }
    public CustodyMeasureForm LoadedMeasure { get; set; } = new();
    [MaxLength(200)] public string? OriginDescription { get; set; }
    [MaxLength(200)] public string? TransportReference { get; set; }
    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public enum CustodyOperation
{
    Return = 1,
    Deliver = 2,
    Transfer = 3,
    Ownership = 4,
    Loss = 5,
    Surplus = 6,
    Hold = 7
}

/// <summary>
/// ورودِ اختیاری به لیتر برای هر عملیات امانی. فقط با چگالی و دمای مستندِ همان حجم پذیرفته
/// می‌شود و MT یک‌بار با <see cref="CustodyMeasurement"/> محاسبه می‌شود؛ هیچ مقدار پیش‌فرض یا
/// تصحیح حرارتی حدسی وجود ندارد.
/// </summary>
public sealed class CustodyMeasureForm
{
    public string Unit { get; set; } = "MT";
    public decimal? Liters { get; set; }
    public decimal? DensityKgPerM3 { get; set; }
    public decimal? VolumeTemperatureC { get; set; }
    public decimal? DensityTemperatureC { get; set; }
    [MaxLength(500)] public string? Reference { get; set; }

    public bool IsLiters => Unit == "L";
    public bool IsValidUnit => Unit is "MT" or "L";
    public bool IsComplete => Liters is > 0m && DensityKgPerM3 is > 0m && VolumeTemperatureC.HasValue
        && DensityTemperatureC.HasValue && !string.IsNullOrWhiteSpace(Reference);

    public CustodyMeasurement? ToMeasurement() => IsLiters && IsComplete
        ? new CustodyMeasurement(Liters!.Value, DensityKgPerM3!.Value, VolumeTemperatureC!.Value, DensityTemperatureC!.Value, Reference!)
        : null;

    public const string IncompleteMessage = "لیتر، چگالی، دمای حجم و چگالی و مرجع معتبر همین محصول را کامل کنید.";
}

/// <summary>مدلِ partialِ فیلدهای واحد: پیشوندِ نامِ فرم، فیلدِ MT و برچسب آن.</summary>
public sealed record CustodyMeasureFieldsModel(string Prefix, CustodyMeasureForm Form, string MtName, decimal? MtValue, string MtLabel);

public sealed class CustodyOperationForm
{
    public int LotId { get; set; }
    public CustodyMeasureForm Measure { get; set; } = new();
    public CustodyOperation Operation { get; set; }
    public string? LocationKey { get; set; }
    public string? ToLocationKey { get; set; }
    public string? NewOwnerKey { get; set; }

    [DataType(DataType.Date)]
    public DateTime Date { get; set; }

    public decimal? QuantityMt { get; set; }
    [MaxLength(200)] public string? CounterpartyName { get; set; }
    [MaxLength(200)] public string? VehicleReference { get; set; }
    public CustodyLossResponsibility Responsibility { get; set; } = CustodyLossResponsibility.Undetermined;
    [MaxLength(500)] public string? Reason { get; set; }
    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public static string Title(CustodyOperation operation) => operation switch
    {
        CustodyOperation.Return => "برگشت به مالک",
        CustodyOperation.Deliver => "تحویل به دستور مالک",
        CustodyOperation.Transfer => "انتقال بین مخازن",
        CustodyOperation.Ownership => "انتقال مالکیت",
        CustodyOperation.Loss => "ثبت کسر / ضایعات",
        CustodyOperation.Surplus => "ثبت اضافه موجودی",
        CustodyOperation.Hold => "مسدودکردن مقدار",
        _ => "عملیات امانی"
    };

    public static string Explanation(CustodyOperation operation) => operation switch
    {
        CustodyOperation.Return => "تیل از مخزن خارج و به مالک برگردانده می‌شود. فروش، درآمد یا سند مالی ساخته نمی‌شود.",
        CustodyOperation.Deliver => "مالک دستور داده تیل به شخص دیگری تحویل شود (مثلاً مشتریِ خودِ مالک). این فروشِ شرکت نیست و مفاد نمی‌سازد.",
        CustodyOperation.Transfer => "فقط محل تغییر می‌کند؛ مالک و مقدار کل همان می‌ماند.",
        CustodyOperation.Ownership => "تیل در همان مخزن می‌ماند و فقط مالکش عوض می‌شود. موجودی فزیکی مخزن تغییر نمی‌کند. تسویهٔ پولی میان دو مالک بیرون از شرکت است.",
        CustodyOperation.Loss => "کسرِ مقدار در نگهداری. فقط مقدار ثبت می‌شود؛ تا تصمیم جداگانه، هیچ مصرف یا بدهی برای کسی ساخته نمی‌شود.",
        CustodyOperation.Surplus => "اضافهٔ اندازه‌گیری‌شده به همین بار افزوده می‌شود. هیچ اثر مالی ندارد.",
        CustodyOperation.Hold => "مقدار مسدود تا رفع مسدودیت قابل خروج یا تحویل نیست (مثلاً اختلاف کیفیت، اسناد یا مالکیت).",
        _ => ""
    };
}

public sealed class CustodyOperationViewModel
{
    public required CustodyOperationForm Form { get; init; }
    public required CustodyLotSummary Lot { get; init; }
    public IReadOnlyList<CustodyLocationOption> SourceLocations { get; init; } = [];
    public IReadOnlyList<CustodyLocationOption> TargetLocations { get; init; } = [];
    public IReadOnlyList<CustodyOwnerOption> Owners { get; init; } = [];
}

public sealed class CustodyReceiveViewModel
{
    public required CustodyReceiveForm Form { get; init; }
    public CustodyLotSummary? TransitLot { get; init; }
    public IReadOnlyList<CustodyOwnerOption> Owners { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> Products { get; init; } = [];
    public IReadOnlyList<CustodyLocationOption> Locations { get; init; } = [];
}

public sealed class CustodySaleForm
{
    public int LotId { get; set; }
    public CustodyMeasureForm Measure { get; set; } = new();
    public string? LocationKey { get; set; }
    public int? CustomerId { get; set; }

    [DataType(DataType.Date)]
    public DateTime Date { get; set; }

    public decimal? QuantityMt { get; set; }
    public decimal? UnitPriceUsd { get; set; }
    public CustodyCommissionMethod CommissionMethod { get; set; } = CustodyCommissionMethod.PercentOfSale;
    public decimal? CommissionRate { get; set; }
    public CustodyCommissionRecognition CommissionRecognition { get; set; } = CustodyCommissionRecognition.OnCollection;
    public CustodySaleSettlement Settlement { get; set; } = CustodySaleSettlement.Credit;
    public int? CashAccountId { get; set; }
    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    /// <summary>سهم‌های اختیاری از بارهای دیگرِ همین مالک و جنس (کلید «بار:ترمینال:مخزن»).</summary>
    public List<CustodySourceQuantity> Extra { get; set; } = [];
}

/// <summary>مقدار برای یک بار و محلِ مشخص؛ کلید با <see cref="CustodySourceKey"/> ساخته می‌شود.</summary>
public sealed class CustodySourceQuantity
{
    public string? Key { get; set; }
    public decimal? QuantityMt { get; set; }
}

public static class CustodySourceKey
{
    public static string Key(int lotId, int terminalId, int? tankId) => $"{lotId}:{terminalId}:{tankId?.ToString() ?? "-"}";

    public static CustodySaleSource? Parse(string? key, decimal quantity)
    {
        var parts = key?.Split(':');
        if (parts is not { Length: 3 } || !int.TryParse(parts[0], out var lotId) || !int.TryParse(parts[1], out var terminalId))
            return null;
        int? tankId = parts[2] == "-" ? null : int.TryParse(parts[2], out var tank) ? tank : -1;
        return tankId == -1 ? null : new CustodySaleSource(lotId, new CustodyLocation(terminalId, tankId), quantity);
    }
}

public sealed record CustodySourceOption(string Key, string LotNumber, string Location, decimal AvailableMt);

public sealed class CustodySaleViewModel
{
    public required CustodySaleForm Form { get; init; }
    public required CustodyLotSummary Lot { get; init; }
    public IReadOnlyList<CustodyLocationOption> SourceLocations { get; init; } = [];
    /// <summary>بارهای دیگرِ همان مالک و جنس که می‌توانند بخشی از همین فروش را تأمین کنند.</summary>
    public IReadOnlyList<CustodySourceOption> OtherSources { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> Customers { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> CashAccounts { get; init; } = [];
}

public sealed class CustodyFinanceForm
{
    public CustodyFinanceKind Kind { get; set; }
    public string? OwnerKey { get; set; }
    public int? SaleId { get; set; }

    [DataType(DataType.Date)]
    public DateTime Date { get; set; }

    public decimal? AmountUsd { get; set; }
    public int? CashAccountId { get; set; }
    public int? ExpenseTypeId { get; set; }
    [MaxLength(200)] public string? PayeeName { get; set; }
    public int? ServiceProviderId { get; set; }
    public int? ProviderChargeId { get; set; }
    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }

    public static string Title(CustodyFinanceKind kind) => kind switch
    {
        CustodyFinanceKind.Collection => "دریافت پول از مشتری",
        CustodyFinanceKind.DirectToOwner => "مشتری مستقیم به مالک پرداخت کرد",
        CustodyFinanceKind.OwnerPayout => "پرداخت به مالک",
        CustodyFinanceKind.ReimbursableExpense => "مصرف به حساب مالک",
        CustodyFinanceKind.ServiceFee => "حق خدمات شرکت",
        CustodyFinanceKind.ProviderCharge => "مصرف نسیه به حساب مالک",
        CustodyFinanceKind.ProviderPayment => "پرداخت به خدمات‌دهنده",
        CustodyFinanceKind.CustomerRefund => "استرداد به مشتری",
        CustodyFinanceKind.OwnerDirectRefund => "استرداد مستقیم مالک به مشتری",
        _ => "سند امانی"
    };

    public static string Explanation(CustodyFinanceKind kind) => kind switch
    {
        CustodyFinanceKind.Collection => "پول فروش به صندوق یا بانک شرکت آمد. این پول از آنِ مالک است و درآمد شرکت نیست؛ فقط کمیشنِ مربوط به همین مبلغ درآمد شرکت می‌شود.",
        CustodyFinanceKind.DirectToOwner => "مشتری پول را مستقیم به مالک داد. صندوق و بانک شرکت تغییر نمی‌کند و شرکت چیزی به مالک بدهکار نمی‌شود؛ کمیشنِ مربوط از مالک طلب می‌شود.",
        CustodyFinanceKind.OwnerPayout => "پرداخت پولِ مالک از صندوق یا بانک شرکت. بیشتر از ماندهٔ قابل پرداخت ممکن نیست.",
        CustodyFinanceKind.ReimbursableExpense => "مصرفی که شرکت از صندوق خود به حساب مالک پرداخت کرده و از پولِ مالک کسر می‌شود. مصرف شرکت نیست. اگر مالک خودش پرداخته، اینجا ثبت نکنید. مصرفی که بر عهدهٔ شرکت است از بخش «مصارف» ثبت شود.",
        CustodyFinanceKind.ServiceFee => "حق مخزن، بارگیری یا خدمتِ دیگرِ شرکت که از پولِ مالک کسر می‌شود و درآمد شرکت است.",
        CustodyFinanceKind.ProviderCharge => "خدمتی که برای تیلِ مالک گرفته شده و پولش هنوز پرداخت نشده است. مالک بدهکار می‌شود و شرکت به خدمات‌دهنده بدهکار؛ صندوق تغییر نمی‌کند. پرداخت بعداً (کامل یا قسمی) از همین حساب ثبت می‌شود.",
        CustodyFinanceKind.ProviderPayment => "پرداختِ کامل یا قسمیِ مصرف نسیه به خدمات‌دهنده از صندوق یا بانک شرکت. حساب مالک دوباره کم نمی‌شود.",
        _ => ""
    };
}

public sealed class CustodyFinanceViewModel
{
    public required CustodyFinanceForm Form { get; init; }
    public required CustodyOwnerAccount Account { get; init; }
    public IReadOnlyList<CustodySaleRow> OpenSales { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> CashAccounts { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> ExpenseTypes { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> ServiceProviders { get; init; } = [];
    public CustodyFinanceRow? ProviderCharge { get; init; }
}

public sealed class CustodySaleReturnForm
{
    public int SaleId { get; set; }
    /// <summary>فقط وقتی فروش یک بار/محل دارد: برگشت به لیتر با چگالی و دمای مستند.</summary>
    public CustodyMeasureForm Measure { get; set; } = new();
    public string? OwnerKey { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; }
    public List<CustodySourceQuantity> Lines { get; set; } = [];
    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    // استرداد پولِ وصول‌شده (فقط وقتی مبلغ برگشت از ماندهٔ وصول‌نشده بیشتر است)
    public decimal? RefundFromCompanyUsd { get; set; }
    public int? RefundCashAccountId { get; set; }
    [MaxLength(100)] public string? RefundDocumentNumber { get; set; }
    public decimal? RefundByOwnerUsd { get; set; }
    [MaxLength(100)] public string? OwnerRefundReference { get; set; }
    public bool OwnerRecoveryConfirmed { get; set; }
    [MaxLength(300)] public string? OwnerRecoveryReference { get; set; }
}

public sealed class CustodySaleReturnViewModel
{
    public required CustodySaleReturnForm Form { get; init; }
    public required CustodySaleRow Sale { get; init; }
    public IReadOnlyList<CustodySourceOption> Sources { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> CashAccounts { get; init; } = [];
}

public sealed class CustodyConversionForm
{
    public int LotId { get; set; }
    public CustodyMeasureForm Measure { get; set; } = new();
    public int StorageTankId { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal UnitPriceUsd { get; set; }
    [Required, MaxLength(500)] public string AgreementReference { get; set; } = "";
    public int? SupplierId { get; set; }
    public bool ConfirmIdentity { get; set; }
    [MaxLength(500)] public string? IdentityEvidence { get; set; }
}

public sealed record CustodyConversionViewModel(CustodyConversionForm Form, CustodyLotSummary Lot,
    int? ResolvedSupplierId, IReadOnlyList<CustodyPosition> Positions,
    IReadOnlyList<(int Id, string Name)> Suppliers, IReadOnlyList<CustodyConversion> Conversions);
