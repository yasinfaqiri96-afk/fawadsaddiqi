using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Reports;

/// <summary>پارامترهای «بیلانس کلی شرکت»: بیلانس تا «تا تاریخ»، عملکرد بین دو تاریخ.</summary>
public sealed class CompanyBalanceReportFilterViewModel
{
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    /// <summary>ارز نمایش؛ خالی یعنی ارز پایهٔ سیستم.</summary>
    public string? Currency { get; set; }
}

/// <summary>هر ردیف اصلی بیلانس یک بخش دارد؛ جزئیات هر ردیف با همین کلید باز می‌شود.</summary>
public enum CompanyBalanceSection
{
    CashAndBank = 1,
    CustomerReceivables = 2,
    InventoryValue = 3,
    GoodsInTransit = 4,
    Prepayments = 5,
    OtherAssets = 6,
    SupplierPayables = 11,
    CustomerAdvances = 12,
    TransportPayables = 13,
    PartnerPayables = 14,
    OtherLiabilities = 15,
    PeriodExpenses = 21,
    SalesRevenue = 22
}

/// <summary>یک ردیف جدول دارایی یا تعهد. مبلغ هم به USD و هم به ارز گزارش نگه داشته می‌شود.</summary>
public sealed record CompanyBalanceLineViewModel(
    CompanyBalanceSection Section,
    string LabelFa,
    string LabelEn,
    decimal AmountUsd,
    decimal Amount);

/// <summary>یک ردیف جزئیات (طرف‌حساب، صندوق، موجودی، بار در راه یا نوع مصرف).</summary>
public sealed record CompanyBalanceDetailRowViewModel(
    string Name,
    string? Secondary,
    decimal? QuantityMt,
    decimal AmountUsd,
    decimal Amount,
    string? LinkController = null,
    string? LinkAction = null,
    int? LinkId = null,
    bool IsValued = true);

public sealed record CompanyBalanceCurrencyOption(string Code, string Name);

public sealed class CompanyBalanceReportViewModel
{
    public CompanyBalanceReportFilterViewModel Filter { get; set; } = new();

    public DateTime ReportFromDate { get; init; }
    public DateTime ReportToDate { get; init; }
    public DateTime GeneratedAt { get; init; }

    public string CurrencyCode { get; init; } = "USD";

    /// <summary>هر ۱ USD چند واحد ارز گزارش. برای USD همیشه ۱.</summary>
    public decimal UsdToReportRate { get; init; } = 1m;
    public DateTime? RateEffectiveDate { get; init; }

    /// <summary>ارز درخواستی نرخ نداشت و گزارش به ارز پایه نمایش داده شد.</summary>
    public string? CurrencyErrorFa { get; set; }

    public IReadOnlyList<CompanyBalanceCurrencyOption> Currencies { get; set; } = [];

    public IReadOnlyList<CompanyBalanceLineViewModel> AssetLines { get; init; } = [];
    public IReadOnlyList<CompanyBalanceLineViewModel> LiabilityLines { get; init; } = [];

    public decimal CashAndBank => Amount(CompanyBalanceSection.CashAndBank);
    public decimal CustomerReceivables => Amount(CompanyBalanceSection.CustomerReceivables);
    public decimal InventoryValue => Amount(CompanyBalanceSection.InventoryValue);
    public decimal GoodsInTransit => Amount(CompanyBalanceSection.GoodsInTransit);
    public decimal Prepayments => Amount(CompanyBalanceSection.Prepayments);
    public decimal OtherAssets => Amount(CompanyBalanceSection.OtherAssets);

    public decimal SupplierPayables => Amount(CompanyBalanceSection.SupplierPayables);
    public decimal CustomerAdvances => Amount(CompanyBalanceSection.CustomerAdvances);
    public decimal TransportPayables => Amount(CompanyBalanceSection.TransportPayables);
    public decimal PartnerPayables => Amount(CompanyBalanceSection.PartnerPayables);
    public decimal OtherLiabilities => Amount(CompanyBalanceSection.OtherLiabilities);

    /// <summary>جمع همان ردیف‌های گردشده‌ای که چاپ می‌شوند؛ پس جمع کاغذ با ردیف‌ها یکی است.</summary>
    public decimal TotalAssets => AssetLines.Sum(l => l.Amount);
    public decimal TotalLiabilities => LiabilityLines.Sum(l => l.Amount);
    public decimal NetCompanyBalance => TotalAssets - TotalLiabilities;

    public decimal TotalAssetsUsd => AssetLines.Sum(l => l.AmountUsd);
    public decimal TotalLiabilitiesUsd => LiabilityLines.Sum(l => l.AmountUsd);
    public decimal NetCompanyBalanceUsd => TotalAssetsUsd - TotalLiabilitiesUsd;

    // ── عملکرد دوره (مستقل از بیلانس؛ هیچ‌کدام داخل دارایی/تعهد جمع نمی‌شود) ──
    public decimal SalesRevenue { get; init; }
    public decimal CostOfSales { get; init; }
    public decimal GrossProfit => SalesRevenue - CostOfSales;
    public decimal PeriodExpenses { get; init; }

    /// <summary>سود منهای زیان ارزیِ محقق همان دوره (از ProfitAndLossService).</summary>
    public decimal NetExchangeResult { get; init; }

    /// <summary>کمیشن و حق خدمات امانی؛ مبلغِ فروشِ امانی از آنِ مالک است و اینجا نمی‌آید.</summary>
    public decimal CustodyIncome { get; init; }
    public decimal NetProfitLoss => GrossProfit - PeriodExpenses + NetExchangeResult + CustodyIncome;

    public decimal SalesRevenueUsd { get; init; }
    public decimal CostOfSalesUsd { get; init; }
    public decimal PeriodExpensesUsd { get; init; }
    public decimal NetExchangeResultUsd { get; init; }
    public decimal CustodyIncomeUsd { get; init; }
    public int UncostedSaleCount { get; init; }

    public IReadOnlyDictionary<CompanyBalanceSection, IReadOnlyList<CompanyBalanceDetailRowViewModel>> Details { get; init; }
        = new Dictionary<CompanyBalanceSection, IReadOnlyList<CompanyBalanceDetailRowViewModel>>();

    /// <summary>توضیح محدودیت داده (موجودی بی‌قیمت، فروش بی‌بها و...). فقط واقعیت، بدون عدد ساختگی.</summary>
    public IReadOnlyList<string> NotesFa { get; init; } = [];

    /// <summary>
    /// آستانهٔ «بار در راهِ طولانی بدون رسید» برای هشدار کیفیت داده. فقط علامت می‌گذارد؛ مبلغ همچنان
    /// در دارایی می‌ماند چون با دادهٔ موجود نمی‌توان گفت بار رسیده است.
    /// </summary>
    public const int LongTransitDays = 60;

    public int LongTransitLoadCount { get; init; }
    public decimal LongTransitQuantityMt { get; init; }
    public decimal LongTransitAmount { get; init; }

    public IReadOnlyList<CompanyBalanceDetailRowViewModel> DetailsFor(CompanyBalanceSection section)
        => Details.TryGetValue(section, out var rows) ? rows : [];

    public CompanyBalanceLineViewModel? Line(CompanyBalanceSection section)
        => AssetLines.Concat(LiabilityLines).FirstOrDefault(l => l.Section == section);

    private decimal Amount(CompanyBalanceSection section) => Line(section)?.Amount ?? 0m;
}

/// <summary>
/// متن و قالب عدد/تاریخِ مشترک صفحهٔ وب و PDF. هر دو خروجی از همین‌جا می‌خوانند تا عنوان،
/// برچسب و شکل عدد در دو نسخه از هم جدا نشوند.
/// </summary>
public static class CompanyBalanceReportText
{
    public const string Title = "بیلانس کلی شرکت";
    public const string Subtitle = "گزارش خلاصه وضعیت مالی و عملکرد شرکت";
    public const string HeroLabel = "خالص وضعیت شرکت";
    public const string HeroFormula = "دارایی‌ها − تعهدات = خالص بیلانس شرکت";
    public const string AssetsTitle = "دارایی‌های شرکت";
    public const string LiabilitiesTitle = "تعهدات شرکت";
    public const string DescriptionHeader = "شرح";
    public const string TotalAssets = "کل دارایی‌ها";
    public const string TotalLiabilities = "کل تعهدات";
    public const string NetBalance = "خالص بیلانس شرکت";
    public const string PerformanceTitle = "خلاصه عملکرد دوره";
    public const string Sales = "فروشات";
    public const string CostOfSales = "بهای تمام‌شده فروش";
    public const string GrossProfit = "مفاد ناخالص";
    public const string PeriodExpenses = "مصارف دوره";
    public const string ExchangeResult = "نتیجه تبدیل ارز";
    public const string CustodyIncome = "درآمد کمیشن و خدمات امانی";
    public const string NetProfit = "مفاد خالص دوره";
    public const string NetLoss = "ضرر خالص دوره";
    public const string GrossLoss = "ضرر ناخالص";
    public const string BrandLatin = "FAWAD SADDIQI GROUP OF COMPANIES";
    public const string BrandName = "گروپ کمپنی‌های فواد صدیقی";
    public const string BrandTagline = "سیستم مدیریت تجارت نفت و گاز";

    public static string AmountHeader(string currency) => $"مبلغ ({currency})";

    /// <summary>جداکنندهٔ هزارگان و دو رقم اعشار؛ منفی با علامت منفی.</summary>
    public static string Amount(decimal value)
        => value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    public static string Money(decimal value, string currency) => $"{Amount(value)} {currency}";

    public static string Quantity(decimal value)
        => value.ToString("N3", System.Globalization.CultureInfo.InvariantCulture);

    public static string Date(DateTime value) => Helpers.DateDisplay.Format(value, "yyyy/MM/dd");

    public static string PrintDate(DateTime value) => Helpers.DateDisplay.Format(value, "yyyy/MM/dd - HH:mm");

    public static string Period(DateTime from, DateTime to) => $"از {Date(from)} تا {Date(to)}";

    public static string NetResultLabel(decimal netProfitLoss) => netProfitLoss < 0m ? NetLoss : NetProfit;
}

/// <summary>صفحهٔ جزئیات یک ردیف بیلانس (فقط وب).</summary>
public sealed class CompanyBalanceDetailsViewModel
{
    public CompanyBalanceReportViewModel Report { get; init; } = new();
    public CompanyBalanceSection Section { get; init; }
    public string TitleFa { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public IReadOnlyList<CompanyBalanceDetailRowViewModel> Rows { get; init; } = [];
    public bool HasQuantity => Rows.Any(r => r.QuantityMt.HasValue);
    public decimal Total => Rows.Where(r => r.IsValued).Sum(r => r.Amount);
}
