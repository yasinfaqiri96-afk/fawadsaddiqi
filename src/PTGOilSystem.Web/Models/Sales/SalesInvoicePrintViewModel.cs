using System.Globalization;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Sales;

public enum InvoiceTemplateKey
{
    Faisal = 1,
    Fawad = 2,
    Mashal = 3
}

public sealed record SalesInvoiceTemplateMetadata(
    InvoiceTemplateKey TemplateKey,
    string SellerName,
    string Address,
    string Phone,
    string Email,
    string Website,
    string LogoPath,
    string WatermarkPath,
    string SignaturePath,
    string StampPath,
    string FooterPath,
    string HeaderPath,
    string PageBackgroundPath,
    string DefaultPersianNote,
    string ThemeColor);

public static class SalesInvoiceTemplateOptions
{
    private const string FaisalNote =
        "نوت: مبلغ متذکره در جدول به صورت صد فیصد پیش پرداخت می باشد، که تاریخ فروش بار {SaleDate} بوده و الی مدت 1 ماه تحویل داده می شود.";

    private const string FawadNote =
        "نوت: از تاریخ فروش مدت 15 روز بعد بار تحویل داده می شود.";

    private static readonly IReadOnlyDictionary<InvoiceTemplateKey, SalesInvoiceTemplateMetadata> Templates =
        new Dictionary<InvoiceTemplateKey, SalesInvoiceTemplateMetadata>
        {
            [InvoiceTemplateKey.Faisal] = new(
                InvoiceTemplateKey.Faisal,
                "Faisal Oil Group LTD",
                "Afghanistan - Herat, Jadah Behzad (4), Market Tejarati Naft va Gaz, 3rd Floor Suite 38",
                "Mob: +93 799 07 07 07 / Office: +93 (040) 23 13 13",
                "faisal@fsiddiqigroup.com",
                "www.fsiddiqigroup.com",
                "/invoice-templates/faisal/logo.png",
                "/invoice-templates/faisal/watermark.png",
                "/invoice-templates/faisal/signature-seller.png",
                "/invoice-templates/faisal/stamp.png",
                "/invoice-templates/faisal/footer-bg.png",
                "/invoice-templates/faisal/header-bg.png",
                "/invoice-templates/faisal/page-bg.jpg",
                FaisalNote,
                "#1d3470"),
            [InvoiceTemplateKey.Fawad] = new(
                InvoiceTemplateKey.Fawad,
                "Fawad Siddiqui Group of Companies",
                "Naft o Gas Market, Behzad Street, Behzad 4, Herat, Afghanistan.",
                "+93 799 07 07 07 / +93 7007 0 7007",
                "info@fawadsgroup.com",
                "www.fawadsgroup.com",
                "/invoice-templates/fawad/logo.png",
                "/invoice-templates/fawad/watermark.png",
                "/invoice-templates/fawad/signature-seller.png",
                "/invoice-templates/fawad/stamp.png",
                "/invoice-templates/fawad/footer-bg.png",
                "/invoice-templates/fawad/header-bg.png",
                "/invoice-templates/fawad/page-bg.jpg",
                FawadNote,
                "#142d57"),
            // فقط برای Instanceهایی که ClientProfile:InvoiceTemplate = "Mashal" دارند.
            // امضای فروشنده فقط وقتی چاپ می‌شود که فایل واقعی آن کنار قالب گذاشته شود.
            [InvoiceTemplateKey.Mashal] = new(
                InvoiceTemplateKey.Mashal,
                "MASHAL",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "/invoice-templates/mashal/logo.png",
                string.Empty,
                "/invoice-templates/mashal/signature-seller.png",
                "/invoice-templates/mashal/stamp.png",
                string.Empty,
                string.Empty,
                "/invoice-templates/mashal/page-bg.jpg",
                string.Empty,
                "#0b2a8a")
        };

    // بدون تنظیم Profile همان دو قالب قبلی؛ با تنظیم، فقط قالب همان Instance.
    private static readonly InvoiceTemplateKey[] DefaultTemplates = [InvoiceTemplateKey.Faisal, InvoiceTemplateKey.Fawad];

    public static IReadOnlyList<InvoiceTemplateKey> AllowedFor(string? instanceTemplate)
        => Enum.TryParse<InvoiceTemplateKey>(instanceTemplate?.Trim(), ignoreCase: true, out var key)
            && Templates.ContainsKey(key)
                ? [key]
                : DefaultTemplates;

    public static bool TryGet(string? template, string? instanceTemplate, out SalesInvoiceTemplateMetadata metadata)
    {
        if (TryGet(template, out metadata) && AllowedFor(instanceTemplate).Contains(metadata.TemplateKey))
        {
            return true;
        }

        metadata = null!;
        return false;
    }

    public static bool TryGet(string? template, out SalesInvoiceTemplateMetadata metadata)
    {
        if (Enum.TryParse<InvoiceTemplateKey>(template, ignoreCase: true, out var key)
            && Templates.TryGetValue(key, out metadata!))
        {
            return true;
        }

        metadata = null!;
        return false;
    }
}

public sealed class SalesInvoicePrintViewModel
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    public int SalesTransactionId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime InvoiceDateGregorian { get; init; }
    public string InvoiceDateDisplay { get; init; } = string.Empty;
    public string SellerName { get; init; } = string.Empty;
    public string SellerAddress { get; init; } = string.Empty;
    public string SellerPhone { get; init; } = string.Empty;
    public string SellerEmail { get; init; } = string.Empty;
    public string SellerWebsite { get; init; } = string.Empty;
    public string BuyerCompanyName { get; init; } = string.Empty;
    public string BuyerRepresentativeName { get; init; } = string.Empty;
    public string BuyerPhoneNumber { get; init; } = string.Empty;
    public string BuyerAddress { get; init; } = string.Empty;
    public string ProductType { get; init; } = string.Empty;
    public string BorderOrLocation { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal TotalPriceUsd { get; init; }
    public string Currency { get; init; } = "USD";
    public string PersianNote { get; init; } = string.Empty;
    public string EnglishNote { get; init; } = string.Empty;
    public InvoiceTemplateKey TemplateKey { get; init; }
    public string SignatureOfSellerLabel { get; init; } = "Signature of Seller";
    public string SignatureOfBuyerLabel { get; init; } = "Signature of Buyer";
    public string LogoPath { get; init; } = string.Empty;
    public string WatermarkPath { get; init; } = string.Empty;
    public string SignaturePath { get; init; } = string.Empty;
    public string StampPath { get; init; } = string.Empty;
    public string FooterPath { get; init; } = string.Empty;
    public string HeaderPath { get; init; } = string.Empty;
    public string PageBackgroundPath { get; init; } = string.Empty;
    public string ThemeColor { get; init; } = "#142d57";

    public string TemplateCssClass => TemplateKey switch
    {
        InvoiceTemplateKey.Faisal => "template-faisal",
        InvoiceTemplateKey.Mashal => "template-mashal",
        _ => "template-fawad"
    };
    public string FormattedQuantityMt => FormatDecimal(QuantityMt);
    public string FormattedUnitPriceUsd => FormatMoney(UnitPriceUsd);
    public string FormattedTotalPriceUsd => FormatMoney(TotalPriceUsd);

    // فقط نمایشی: همان اعداد بالا، ولی علامت پول و رقم جدا هستند تا قالب چاپی بتواند
    // فاصلهٔ بین «$» و عدد را با یک قاعدهٔ واحد (.money) کنترل کند و در چاپ/PDF نشکند.
    // هیچ محاسبه، گرد کردن یا واحد پولی تغییر نمی‌کند؛ همان FormatDecimal قبلی است.
    public string MoneySymbol => "$";
    public string UnitPriceFigure => FormatDecimal(UnitPriceUsd);
    public string TotalPriceFigure => FormatDecimal(TotalPriceUsd);

    private static string FormatMoney(decimal amount)
        => "$ " + FormatDecimal(amount);

    private static string FormatDecimal(decimal amount)
    {
        var rounded = decimal.Round(amount, 4);
        return rounded % 1m == 0m
            ? rounded.ToString("0", InvariantCulture)
            : rounded.ToString("0.####", InvariantCulture);
    }
}

public static class SalesInvoiceMapper
{
    public static SalesInvoicePrintViewModel Build(
        SalesTransaction sale,
        SalesInvoiceTemplateMetadata template,
        string? borderOrLocation)
    {
        var invoiceNumber = string.IsNullOrWhiteSpace(sale.InvoiceNumber)
            ? $"SALE-{sale.Id:D6}"
            : sale.InvoiceNumber.Trim();

        var unitPriceUsd = ResolveUnitPriceUsd(sale);
        var totalUsd = sale.TotalUsd != 0m ? sale.TotalUsd : sale.QuantityMt * unitPriceUsd;
        var displayDate = sale.SaleDate.ToString(DateDisplay.DisplayDatePattern, CultureInfo.InvariantCulture);

        // TODO: If a shared Jalali date utility is introduced, use it for the note date.
        var persianNote = template.DefaultPersianNote.Replace("{SaleDate}", displayDate, StringComparison.Ordinal);

        return new SalesInvoicePrintViewModel
        {
            SalesTransactionId = sale.Id,
            InvoiceNumber = invoiceNumber,
            InvoiceDateGregorian = sale.SaleDate,
            InvoiceDateDisplay = displayDate,
            SellerName = template.SellerName,
            SellerAddress = template.Address,
            SellerPhone = template.Phone,
            SellerEmail = template.Email,
            SellerWebsite = template.Website,
            BuyerCompanyName = sale.SupplierId.HasValue
                ? DisplayName(sale.Supplier?.NamePersian, sale.Supplier?.Name)
                : DisplayName(sale.Customer?.NamePersian, sale.Customer?.Name),
            BuyerRepresentativeName = Clean(sale.SupplierId.HasValue ? sale.Supplier?.ContactPerson : sale.Customer?.ContactPerson),
            BuyerPhoneNumber = Clean(sale.SupplierId.HasValue ? sale.Supplier?.Phone : sale.Customer?.Phone),
            BuyerAddress = Clean(sale.SupplierId.HasValue ? sale.Supplier?.Address : sale.Customer?.Address),
            ProductType = DisplayName(sale.Product?.NamePersian, sale.Product?.Name),
            BorderOrLocation = Clean(borderOrLocation),
            QuantityMt = sale.QuantityMt,
            UnitPriceUsd = unitPriceUsd,
            TotalPriceUsd = totalUsd,
            Currency = "USD",
            PersianNote = persianNote,
            EnglishNote = string.Empty,
            TemplateKey = template.TemplateKey,
            LogoPath = template.LogoPath,
            WatermarkPath = template.WatermarkPath,
            SignaturePath = template.SignaturePath,
            StampPath = template.StampPath,
            FooterPath = template.FooterPath,
            HeaderPath = template.HeaderPath,
            PageBackgroundPath = template.PageBackgroundPath,
            ThemeColor = template.ThemeColor
        };
    }

    private static decimal ResolveUnitPriceUsd(SalesTransaction sale)
    {
        if (sale.UnitPriceUsd != 0m)
        {
            return sale.UnitPriceUsd;
        }

        return string.Equals(sale.Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? sale.UnitPriceInCurrency
            : 0m;
    }

    private static string DisplayName(string? persian, string? english)
        => !string.IsNullOrWhiteSpace(persian)
            ? persian.Trim()
            : Clean(english);

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
