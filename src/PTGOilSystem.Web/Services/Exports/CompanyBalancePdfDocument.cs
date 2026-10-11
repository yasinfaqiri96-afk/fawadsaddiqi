using PTGOilSystem.Web.Models.Reports;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Text = PTGOilSystem.Web.Models.Reports.CompanyBalanceReportText;

namespace PTGOilSystem.Web.Services.Exports;

/// <summary>
/// «بیلانس کلی شرکت» روی A4 عمودی. چیدمان، رنگ، برچسب و قالب عدد همان صفحهٔ وب است
/// (<c>Views/Reports/_CompanyBalanceSheet.cshtml</c> و <c>64-company-balance.css</c>): لوگو و
/// مشخصات، عنوان، عدد اصلی، دو جدول هم‌ارتفاع، عملکرد دوره، جمع‌بندی و فوتر. هیچ query یا
/// محاسبه‌ای اینجا نیست؛ فقط مدل آماده چاپ می‌شود.
/// </summary>
internal sealed class CompanyBalancePdfDocument(CompanyBalanceReportViewModel model, string? logoPath) : IDocument
{
    public const string LogoRelativePath = "/images/mashal-logo-blue.webp";

    // همان توکن‌های --cbr-* در 64-company-balance.css.
    private const string Navy = "#0B1B3A";
    private const string Secondary = "#526077";
    private const string Teal = "#008B87";
    private const string HeaderFill = "#F5F7FA";
    private const string Border = "#D9E0E8";
    private const string HeroFill = "#F1FAF9";
    private const string Negative = "#A53A3A";

    private const float RowHeight = 21f;
    private const float BodySize = 8.6f;
    private const float SmallSize = 7.4f;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = Text.Title,
        Author = Text.BrandLatin,
        Subject = Text.Period(model.ReportFromDate, model.ReportToDate)
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            // حدود ۱۱ میلی‌متر.
            page.MarginHorizontal(31);
            page.MarginTop(28);
            page.MarginBottom(22);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => style
                .FontFamily(PdfDesignSystem.PersianFallbackFont, PdfDesignSystem.PrimaryFont)
                .FontSize(BodySize)
                .FontColor(Navy));
            page.Header().Element(ComposeHeader);
            page.Content().PaddingTop(10).ContentFromRightToLeft().Column(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    // ── سربرگ: لوگو چپ، مشخصات راست، خط نازک سرمه‌ای، عنوان وسط ───────────
    private void ComposeHeader(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().ContentFromLeftToRight().Row(row =>
            {
                row.ConstantItem(150).Height(54).AlignLeft().AlignMiddle().Element(ComposeLogo);
                row.RelativeItem();
                row.ConstantItem(230).AlignMiddle().ContentFromRightToLeft().Column(meta =>
                {
                    meta.Spacing(2);
                    MetaLine(meta, "تاریخ گزارش:", Text.Date(model.ReportToDate), ltr: true);
                    MetaLine(meta, "دوره گزارش:", Text.Period(model.ReportFromDate, model.ReportToDate), ltr: false);
                    MetaLine(meta, "واحد پول:", model.CurrencyCode, ltr: true);
                    MetaLine(meta, "تاریخ چاپ:", Text.PrintDate(model.GeneratedAt), ltr: true);
                });
            });
            column.Item().PaddingTop(8).LineHorizontal(0.8f).LineColor(Navy);
            column.Item().PaddingTop(12).AlignCenter().Text(Text.Title).Bold().FontSize(16).FontColor(Navy);
            column.Item().PaddingTop(2).AlignCenter().Text(Text.Subtitle).FontSize(SmallSize + 0.6f).FontColor(Secondary);
        });
    }

    // تاریخ و ساعت چپ‌به‌راست نوشته می‌شوند تا «تاریخ - ساعت» در متن راست‌به‌چپ وارونه نشود.
    private static void MetaLine(ColumnDescriptor column, string label, string value, bool ltr)
    {
        column.Item().Row(row =>
        {
            row.ConstantItem(62).AlignRight().Text(label).FontSize(SmallSize).FontColor(Secondary);
            var cell = row.RelativeItem().AlignRight();
            (ltr ? cell.ContentFromLeftToRight() : cell)
                .Text(PdfDesignSystem.ToEnglishDigits(value)).FontSize(SmallSize + 0.4f).FontColor(Navy);
        });
    }

    private void ComposeLogo(IContainer container)
    {
        if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
        {
            container.Image(logoPath).FitArea();
            return;
        }

        container.Text(Text.BrandName).Bold().FontSize(16).FontColor(Navy);
    }

    private void ComposeContent(ColumnDescriptor column)
    {
        column.Spacing(14);
        column.Item().Element(ComposeHero);
        column.Item().Row(row =>
        {
            // در جهت راست‌به‌چپ اولین آیتم سمت راست می‌نشیند: دارایی راست، تعهد چپ.
            var rowCount = Math.Max(model.AssetLines.Count, model.LiabilityLines.Count);
            row.RelativeItem().Element(c => ComposeBalanceTable(c, Text.AssetsTitle, model.AssetLines, rowCount, Text.TotalAssets, model.TotalAssets));
            row.ConstantItem(14);
            row.RelativeItem().Element(c => ComposeBalanceTable(c, Text.LiabilitiesTitle, model.LiabilityLines, rowCount, Text.TotalLiabilities, model.TotalLiabilities));
        });
        column.Item().Element(ComposePerformance);
        column.Item().Element(ComposeSummary);
        if (model.NotesFa.Count > 0)
        {
            column.Item().Element(ComposeNotes);
        }
    }

    // ── عدد اصلی ───────────────────────────────────────────────────────────
    private void ComposeHero(IContainer container)
    {
        var tone = model.NetCompanyBalance < 0m ? Negative : Teal;
        container.Background(HeroFill).Border(0.6f).BorderColor(Border).PaddingVertical(12).Column(hero =>
        {
            hero.Item().AlignCenter().Text(Text.HeroLabel).FontSize(9).FontColor(Secondary);
            hero.Item().PaddingTop(3).AlignCenter().ContentFromLeftToRight()
                .Text(Text.Money(model.NetCompanyBalance, model.CurrencyCode)).Bold().FontSize(21).FontColor(tone);
            hero.Item().PaddingTop(3).AlignCenter().Text(Text.HeroFormula).FontSize(SmallSize).FontColor(Secondary);
        });
    }

    // ── جدول دارایی/تعهد؛ هر دو جدول یک تعداد ردیف دارند تا جمع‌ها در یک خط بنشینند ──
    private void ComposeBalanceTable(
        IContainer container,
        string title,
        IReadOnlyList<CompanyBalanceLineViewModel> lines,
        int rowCount,
        string totalLabel,
        decimal total)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(5).Text(title).Bold().FontSize(10).FontColor(Navy);
            column.Item().Border(0.6f).BorderColor(Border).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.45f);
                    columns.RelativeColumn(1f);
                });

                table.Cell().Element(HeaderCell).AlignRight().Text(Text.DescriptionHeader).SemiBold().FontSize(SmallSize + 0.4f).FontColor(Secondary);
                table.Cell().Element(HeaderCell).AlignLeft().Text(Text.AmountHeader(model.CurrencyCode)).SemiBold().FontSize(SmallSize + 0.4f).FontColor(Secondary);

                for (var index = 0; index < rowCount; index++)
                {
                    if (index < lines.Count)
                    {
                        var line = lines[index];
                        table.Cell().Element(BodyCell).AlignRight().Text(line.LabelFa).FontSize(BodySize);
                        table.Cell().Element(BodyCell).AlignLeft().ContentFromLeftToRight()
                            .Text(Text.Amount(line.Amount)).FontSize(BodySize).FontColor(line.Amount < 0m ? Negative : Navy);
                    }
                    else
                    {
                        table.Cell().Element(BodyCell).Text(string.Empty);
                        table.Cell().Element(BodyCell).Text(string.Empty);
                    }
                }

                table.Cell().Element(TotalCell).AlignRight().Text(totalLabel).Bold().FontSize(BodySize + 0.4f);
                table.Cell().Element(TotalCell).AlignLeft().ContentFromLeftToRight()
                    .Text(Text.Amount(total)).Bold().FontSize(BodySize + 0.4f);
            });
        });
    }

    // ── عملکرد دوره ─────────────────────────────────────────────────────────
    private void ComposePerformance(IContainer container)
    {
        var cells = new List<(string Label, decimal Value, bool Emphasis)>
        {
            (Text.Sales, model.SalesRevenue, false),
            (Text.CostOfSales, model.CostOfSales, false),
            (model.GrossProfit < 0m ? Text.GrossLoss : Text.GrossProfit, Math.Abs(model.GrossProfit), false),
            (Text.PeriodExpenses, model.PeriodExpenses, false)
        };
        if (model.NetExchangeResult != 0m)
        {
            cells.Add((Text.ExchangeResult, model.NetExchangeResult, false));
        }
        if (model.CustodyIncome != 0m)
        {
            cells.Add((Text.CustodyIncome, model.CustodyIncome, false));
        }

        cells.Add((Text.NetResultLabel(model.NetProfitLoss), Math.Abs(model.NetProfitLoss), true));

        container.Column(column =>
        {
            column.Item().PaddingBottom(5).Row(row =>
            {
                row.RelativeItem().AlignRight().Text(Text.PerformanceTitle).Bold().FontSize(10).FontColor(Navy);
                row.RelativeItem().AlignLeft().Text(PdfDesignSystem.ToEnglishDigits(Text.Period(model.ReportFromDate, model.ReportToDate)))
                    .FontSize(SmallSize).FontColor(Secondary);
            });
            column.Item().Border(0.6f).BorderColor(Border).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    foreach (var _ in cells)
                    {
                        columns.RelativeColumn();
                    }
                });

                foreach (var cell in cells)
                {
                    table.Cell().Element(HeaderCell).AlignCenter().Text(cell.Label).SemiBold().FontSize(SmallSize + 0.2f)
                        .FontColor(cell.Emphasis ? ResultTone() : Secondary);
                }

                foreach (var cell in cells)
                {
                    var text = table.Cell().Element(BodyCell).AlignCenter().ContentFromLeftToRight()
                        .Text(Text.Amount(cell.Value)).FontSize(BodySize + (cell.Emphasis ? 0.6f : 0f))
                        .FontColor(cell.Emphasis ? ResultTone() : Navy);
                    if (cell.Emphasis)
                    {
                        text.Bold();
                    }
                }
            });
        });
    }

    private string ResultTone() => model.NetProfitLoss < 0m ? Negative : Teal;

    // ── جمع‌بندی نهایی ─────────────────────────────────────────────────────
    private void ComposeSummary(IContainer container)
    {
        container.AlignLeft().Width(300).Column(summary =>
        {
            SummaryLine(summary, Text.TotalAssets, model.TotalAssets, bold: false, Navy);
            SummaryLine(summary, Text.TotalLiabilities, model.TotalLiabilities, bold: false, Navy);
            summary.Item().PaddingVertical(4).LineHorizontal(0.8f).LineColor(Navy);
            SummaryLine(summary, Text.NetBalance, model.NetCompanyBalance, bold: true,
                model.NetCompanyBalance < 0m ? Negative : Navy);
        });
    }

    private void SummaryLine(ColumnDescriptor column, string label, decimal value, bool bold, string color)
    {
        column.Item().PaddingVertical(2.5f).Row(row =>
        {
            var labelText = row.RelativeItem().AlignRight().Text(label).FontSize(bold ? 10.5f : 9).FontColor(Navy);
            var valueText = row.RelativeItem().AlignLeft().ContentFromLeftToRight()
                .Text(Text.Money(value, model.CurrencyCode)).FontSize(bold ? 10.5f : 9).FontColor(color);
            if (bold)
            {
                labelText.Bold();
                valueText.Bold();
            }
        });
    }

    private void ComposeNotes(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(1.5f);
            foreach (var note in model.NotesFa)
            {
                column.Item().AlignRight().Text("• " + PdfDesignSystem.ToEnglishDigits(note)).FontSize(6.6f).FontColor(Secondary);
            }
        });
    }

    // ── فوتر ───────────────────────────────────────────────────────────────
    private static void ComposeFooter(IContainer container)
    {
        container.BorderTop(0.8f).BorderColor(Navy).PaddingTop(5).ContentFromLeftToRight().Row(row =>
        {
            row.RelativeItem().Column(brand =>
            {
                brand.Item().Text(text =>
                {
                    text.Span(Text.BrandLatin).Bold().FontSize(9).FontColor(Navy);
                    text.Span("  |  ").FontSize(8).FontColor(Secondary);
                    text.Span(Text.BrandName).Bold().FontSize(9).FontColor(Navy);
                });
                brand.Item().Text(Text.BrandTagline).FontSize(SmallSize - 0.4f).FontColor(Secondary);
            });
            row.RelativeItem().AlignRight().AlignMiddle().ContentFromRightToLeft().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8.5f).FontColor(Navy));
                text.Span("صفحه ");
                text.CurrentPageNumber();
                text.Span(" از ");
                text.TotalPages();
            });
        });
    }

    private static IContainer HeaderCell(IContainer container)
        => container.Background(HeaderFill).BorderBottom(0.6f).BorderColor(Border)
            .MinHeight(RowHeight).PaddingHorizontal(8).AlignMiddle();

    private static IContainer BodyCell(IContainer container)
        => container.BorderBottom(0.5f).BorderColor(Border)
            .MinHeight(RowHeight).PaddingHorizontal(8).AlignMiddle();

    private static IContainer TotalCell(IContainer container)
        => container.BorderTop(0.8f).BorderColor(Navy)
            .MinHeight(RowHeight + 2).PaddingHorizontal(8).AlignMiddle();
}
