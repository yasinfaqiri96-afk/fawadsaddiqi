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

public sealed class CustodyOperationForm
{
    public int LotId { get; set; }
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
}

public sealed class CustodySaleViewModel
{
    public required CustodySaleForm Form { get; init; }
    public required CustodyLotSummary Lot { get; init; }
    public IReadOnlyList<CustodyLocationOption> SourceLocations { get; init; } = [];
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
    [MaxLength(100)] public string? DocumentNumber { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }

    public static string Title(CustodyFinanceKind kind) => kind switch
    {
        CustodyFinanceKind.Collection => "دریافت پول از مشتری",
        CustodyFinanceKind.DirectToOwner => "مشتری مستقیم به مالک پرداخت کرد",
        CustodyFinanceKind.OwnerPayout => "پرداخت به مالک",
        CustodyFinanceKind.ReimbursableExpense => "مصرف به حساب مالک",
        CustodyFinanceKind.ServiceFee => "حق خدمات شرکت",
        _ => "سند امانی"
    };

    public static string Explanation(CustodyFinanceKind kind) => kind switch
    {
        CustodyFinanceKind.Collection => "پول فروش به صندوق یا بانک شرکت آمد. این پول از آنِ مالک است و درآمد شرکت نیست؛ فقط کمیشنِ مربوط به همین مبلغ درآمد شرکت می‌شود.",
        CustodyFinanceKind.DirectToOwner => "مشتری پول را مستقیم به مالک داد. صندوق و بانک شرکت تغییر نمی‌کند و شرکت چیزی به مالک بدهکار نمی‌شود؛ کمیشنِ مربوط از مالک طلب می‌شود.",
        CustodyFinanceKind.OwnerPayout => "پرداخت پولِ مالک از صندوق یا بانک شرکت. بیشتر از ماندهٔ قابل پرداخت ممکن نیست.",
        CustodyFinanceKind.ReimbursableExpense => "مصرفی که شرکت از صندوق خود به حساب مالک پرداخت کرده و از پولِ مالک کسر می‌شود. مصرف شرکت نیست. اگر مالک خودش پرداخته، اینجا ثبت نکنید. مصرفی که بر عهدهٔ شرکت است از بخش «مصارف» ثبت شود.",
        CustodyFinanceKind.ServiceFee => "حق مخزن، بارگیری یا خدمتِ دیگرِ شرکت که از پولِ مالک کسر می‌شود و درآمد شرکت است.",
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
}

public sealed class CustodyConversionForm
{
    public int LotId { get; set; }
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
