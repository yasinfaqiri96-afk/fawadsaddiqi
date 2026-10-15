using DocumentFormat.OpenXml.Spreadsheet;
using SColor = DocumentFormat.OpenXml.Spreadsheet.Color;
using SFonts = DocumentFormat.OpenXml.Spreadsheet.Fonts;

namespace PTGOilSystem.Web.Services.Exports;

/// <summary>
/// قرارداد مشترک طراحی Excel براساس پنج فایل مرجع موجود در پوشهٔ exel.
/// این کلاس فقط نمایش را تعیین می‌کند و به داده، جمع‌ها یا منطق گزارش دست نمی‌زند.
/// </summary>
public static class ExcelDesignSystem
{
    /// <summary>هویت مشترک همهٔ خروجی‌ها (سطر اول Excel، سربرگ و پابرگ PDF).</summary>
    public const string OrganizationName = "گروپ کمپنی‌های فواد صدیقی";
    public const string OrganizationNameEn = "Fawad Saddiqi Group of Companies";
    /// <summary>لوگوی رسمی صدیقی برای سندهای سفید و خروجی‌های قابل چاپ.</summary>
    public const string LogoRelativePath = "/invoice-templates/fawad/logo.png";
    /// <summary>نشان مربع صدیقی برای سربرگ فشردهٔ Excel.</summary>
    public const string ExcelLogoRelativePath = "/images/saddiqi-loader-symbol.png";

    // Soft variants of the shared PTG palette: primary-lighter, table-head and divider.
    public const string Navy = "FFDCE2F9";
    public const string Blue = "FFF4F6FB";
    public const string TotalBlue = "FFE7ECFA";
    public const string PrimaryText = "FF173F73";
    public const string Border = "FFE5E7EB";
    public const string QuantityBackground = "FFEFF5BC";
    public const string AmountBackground = "FFC1F1F7";
    public const string PositiveBackground = "FFBFFFC2";
    public const string BalanceBackground = "FFFCD7D7";

    public const double SequenceColumnWidth = 4.285714D;
    public const double OrganizationRowHeight = 40D;
    public const double ReportTitleRowHeight = 40D;
    public const double HeaderRowHeight = 25D;
    public const double BodyRowHeight = 15D;
    public const double WrappedBodyRowHeight = 30D;
    // ارتفاع هر خطِ متنِ شکسته، و سقف خطوطی که برای یک سطر باز می‌شود. بدون این، متن
    // بلند در خانه جا نمی‌شد و از سطر بیرون می‌زد.
    public const double WrappedBodyLineHeight = 15D;
    public const int MaxWrappedBodyLines = 5;
    public const double TotalRowHeight = 25D;

    public const uint OrganizationStyle = 1;
    public const uint ReportTitleStyle = 4;
    public const uint ExportDateStyle = 5;
    public const uint HeaderStyle = 6;
    public const uint SequenceStyle = 7;
    public const uint BodyTextStyle = 8;
    public const uint BodyIntegerStyle = 9;
    public const uint BodyPercentageStyle = 10;
    public const uint BodyDateStyle = 11;
    public const uint BodyDateTimeStyle = 12;
    public const uint BodyQuantityStyle = 13;
    public const uint BodyAmountStyle = 14;
    public const uint BodyPositiveStyle = 15;
    public const uint BodyBalanceStyle = 16;
    public const uint TotalTextStyle = 17;
    public const uint TotalIntegerStyle = 18;
    public const uint TotalNumberStyle = 19;
    public const uint TotalPercentageStyle = 20;
    public const uint TotalDateStyle = 21;
    public const uint TotalDateTimeStyle = 22;
    public const uint TotalQuantityStyle = 23;

    private const uint IntegerNumberFormat = 164;
    private const uint DecimalNumberFormat = 165;
    private const uint PercentageNumberFormat = 166;
    private const uint DateNumberFormat = 167;
    private const uint DateTimeNumberFormat = 168;
    private const uint QuantityNumberFormat = 169;

    public static Stylesheet BuildStylesheet()
    {
        var fonts = new SFonts(
            Font(11D),
            Font(20D, bold: true),
            Font(24D, bold: true),
            Font(18D, bold: true, color: PrimaryText),
            Font(16D, bold: true, color: PrimaryText),
            Font(12D, bold: true, color: PrimaryText),
            Font(11D, color: PrimaryText),
            Font(12D, bold: true, color: PrimaryText));

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            SolidFill(Navy),
            SolidFill(Blue),
            SolidFill(TotalBlue),
            SolidFill(QuantityBackground),
            SolidFill(AmountBackground),
            SolidFill(PositiveBackground),
            SolidFill(BalanceBackground));

        var borders = new Borders(
            new Border(),
            new Border(
                new LeftBorder { Style = BorderStyleValues.Thin, Color = new SColor { Rgb = Border } },
                new RightBorder { Style = BorderStyleValues.Thin, Color = new SColor { Rgb = Border } },
                new TopBorder { Style = BorderStyleValues.Thin, Color = new SColor { Rgb = Border } },
                new BottomBorder { Style = BorderStyleValues.Thin, Color = new SColor { Rgb = Border } },
                new DiagonalBorder()));

        var numberFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = IntegerNumberFormat, FormatCode = "#,##0" },
            new NumberingFormat { NumberFormatId = DecimalNumberFormat, FormatCode = "#,##0.00" },
            new NumberingFormat { NumberFormatId = PercentageNumberFormat, FormatCode = "0.00%" },
            new NumberingFormat { NumberFormatId = DateNumberFormat, FormatCode = "yyyy-mm-dd" },
            new NumberingFormat { NumberFormatId = DateTimeNumberFormat, FormatCode = "yyyy-mm-dd hh:mm" },
            new NumberingFormat { NumberFormatId = QuantityNumberFormat, FormatCode = "#,##0.000" });

        var cellFormats = new CellFormats(
            CellFormat(0, 0, border: false),
            CellFormat(1, 0, border: false),
            CellFormat(2, 0, border: false),
            CellFormat(2, 0, border: false),
            CellFormat(3, 2, border: false),
            CellFormat(4, 3, border: false),
            CellFormat(5, 3),
            CellFormat(6, 3),
            CellFormat(0, 0),
            CellFormat(0, 0, IntegerNumberFormat),
            CellFormat(0, 6, PercentageNumberFormat),
            CellFormat(0, 0, DateNumberFormat),
            CellFormat(0, 0, DateTimeNumberFormat),
            CellFormat(0, 5, QuantityNumberFormat),
            CellFormat(0, 6, DecimalNumberFormat),
            CellFormat(0, 7, DecimalNumberFormat),
            CellFormat(0, 8, DecimalNumberFormat),
            CellFormat(7, 4),
            CellFormat(7, 4, IntegerNumberFormat),
            CellFormat(7, 4, DecimalNumberFormat),
            CellFormat(7, 4, PercentageNumberFormat),
            CellFormat(7, 4, DateNumberFormat),
            CellFormat(7, 4, DateTimeNumberFormat),
            CellFormat(7, 4, QuantityNumberFormat));

        return new Stylesheet(numberFormats, fonts, fills, borders, cellFormats);
    }

    public static uint ResolveBodyStyle(TabularExportColumn column, TabularExportCell cell)
        => cell.ValueType switch
        {
            TabularExportValueType.Integer => BodyIntegerStyle,
            TabularExportValueType.Percentage => BodyPercentageStyle,
            TabularExportValueType.Date => BodyDateStyle,
            TabularExportValueType.DateTime => BodyDateTimeStyle,
            TabularExportValueType.Number => ResolveNumberStyle(column),
            _ => BodyTextStyle
        };

    public static uint ResolveTotalStyle(TabularExportColumn column, TabularExportCell cell)
        => cell.ValueType switch
        {
            TabularExportValueType.Integer => TotalIntegerStyle,
            TabularExportValueType.Number when Classify(column) == ColumnAccent.Quantity => TotalQuantityStyle,
            TabularExportValueType.Number => TotalNumberStyle,
            TabularExportValueType.Percentage => TotalPercentageStyle,
            TabularExportValueType.Date => TotalDateStyle,
            TabularExportValueType.DateTime => TotalDateTimeStyle,
            _ => TotalTextStyle
        };

    public static double NormalizeColumnWidth(double width)
        => Math.Clamp(width, 8D, 59.285714D);

    private static uint ResolveNumberStyle(TabularExportColumn column)
        => Classify(column) switch
        {
            ColumnAccent.Quantity => BodyQuantityStyle,
            ColumnAccent.Positive => BodyPositiveStyle,
            ColumnAccent.Balance => BodyBalanceStyle,
            _ => BodyAmountStyle
        };

    private static ColumnAccent Classify(TabularExportColumn column)
    {
        var title = $"{column.TitleFa} {column.TitleEn}".ToLowerInvariant();
        if (ContainsAny(title, "بیلانس", "مانده", "باقی", "تفاوت", "اختلاف", "کسری", "balance", "outstanding", "deficit", "variance"))
        {
            return ColumnAccent.Balance;
        }
        if (ContainsAny(title, "رسید", "دریافت", "وصول", "مجموعه", "received", "receipt", "income", "collection"))
        {
            return ColumnAccent.Positive;
        }
        if (ContainsAny(title, "مبلغ", "مقدار کل", "مقدار کُل", "قیمت", "نرخ", "مصرف", "پرداخت", "فی", "amount", "price", "rate", "cost", "payment", "paid", "debit", "credit"))
        {
            return ColumnAccent.Amount;
        }
        if (ContainsAny(title, "مقدار", "وزن", "تناژ", "quantity", "weight", "volume", "ton"))
        {
            return ColumnAccent.Quantity;
        }
        return ColumnAccent.Amount;
    }

    private static bool ContainsAny(string value, params string[] tokens)
        => tokens.Any(token => value.Contains(token, StringComparison.Ordinal));

    private static Font Font(double size, bool bold = false, string? color = null)
    {
        var font = new Font();
        if (bold)
        {
            font.Append(new Bold());
        }
        if (color is not null)
        {
            font.Append(new SColor { Rgb = color });
        }
        font.Append(new FontName { Val = "Calibri" });
        font.Append(new FontSize { Val = size });
        font.Append(new FontFamilyNumbering { Val = 2 });
        return font;
    }

    private static Fill SolidFill(string color)
        => new(new PatternFill(new ForegroundColor { Rgb = color }) { PatternType = PatternValues.Solid });

    private static CellFormat CellFormat(
        uint fontId,
        uint fillId,
        uint? numberFormatId = null,
        bool border = true)
        => new()
        {
            FontId = fontId,
            FillId = fillId,
            BorderId = border ? 1U : 0U,
            NumberFormatId = numberFormatId,
            ApplyFont = true,
            ApplyFill = fillId != 0,
            ApplyBorder = border,
            ApplyNumberFormat = numberFormatId.HasValue,
            ApplyAlignment = true,
            Alignment = new Alignment
            {
                Horizontal = HorizontalAlignmentValues.Center,
                Vertical = VerticalAlignmentValues.Center
            }
        };

    private enum ColumnAccent
    {
        Amount,
        Quantity,
        Positive,
        Balance
    }
}
