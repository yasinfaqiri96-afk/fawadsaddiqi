using PTGOilSystem.Web.Models.ShipmentPnl;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PTGOilSystem.Web.Services.Exports;

/// <summary>
/// Formal shipment summary using the shared PDF design system. The document only
/// presents values prepared by ShipmentPnlDetailsViewModel and performs no queries
/// or business calculations.
/// </summary>
internal sealed class ShipmentSummaryPdfDocument(
    ShipmentSummaryPdfModel model,
    PdfBrandHeader brandHeader,
    bool isEnglish = false) : IDocument
{
    private const float SectionTitleSize = PdfDesignSystem.MetaSize;
    private const float LabelSize = PdfDesignSystem.ReportMetaSize;
    private const float ValueSize = PdfDesignSystem.NumericTableSize;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = Label("خلاصه محموله", "Shipment summary"),
        Author = brandHeader.CompanyName,
        Subject = model.ShipmentCode
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.MarginHorizontal(PdfDesignSystem.HorizontalMargin);
            page.MarginTop(PdfDesignSystem.TopMargin);
            page.MarginBottom(PdfDesignSystem.BottomMargin);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => PdfDesignSystem.DefaultTextStyle(style, isEnglish)
                .FontFamily(isEnglish
                    ? PdfDesignSystem.EnglishFallbackFont
                    : PdfDesignSystem.PersianFallbackFont));
            page.Content().Element(Direct).Column(ComposePage);
            page.Footer().PaddingTop(8).Element(footer => PdfDesignSystem.ComposeFooter(
                footer,
                brandHeader.CompanyName,
                isEnglish));
        });
    }

    private IContainer Direct(IContainer container)
        => isEnglish ? container.ContentFromLeftToRight() : container.ContentFromRightToLeft();

    private static IContainer Ltr(IContainer container) => container.ContentFromLeftToRight();

    private void ComposePage(ColumnDescriptor column)
    {
        column.Spacing(9);
        column.Item().Element(header => PdfDesignSystem.ComposeBrandHeader(header, brandHeader));
        column.Item().Element(ComposeDocumentTitle);
        column.Item().Element(ComposeShipmentIdentity);
        column.Item().PaddingTop(2).Element(container => ComposeSectionTitle(
            container,
            Label("خلاصه عملیات محموله", "Shipment operations summary")));
        column.Item().Element(ComposeSummaryTable);
    }

    private void ComposeDocumentTitle(IContainer container)
    {
        container.ContentFromLeftToRight().Column(column =>
        {
            column.Item().Row(row =>
            {
                if (isEnglish)
                {
                    row.RelativeItem().AlignLeft().Text(Label("خلاصه محموله", "Shipment summary"))
                        .Bold().FontSize(PdfDesignSystem.TitleSize).FontColor(PdfDesignSystem.Ink);
                    row.ConstantItem(160).AlignRight().Element(Ltr)
                        .Text(FormatGregorianPrintDate(model.GeneratedAt))
                        .FontSize(PdfDesignSystem.ReportMetaSize).FontColor(PdfDesignSystem.Muted);
                }
                else
                {
                    row.ConstantItem(160).AlignLeft().ContentFromRightToLeft()
                        .Text(FormatGregorianPrintDate(model.GeneratedAt))
                        .FontSize(PdfDesignSystem.ReportMetaSize).FontColor(PdfDesignSystem.Muted);
                    row.RelativeItem().AlignRight().ContentFromRightToLeft()
                        .Text(Label("خلاصه محموله", "Shipment summary"))
                        .Bold().FontSize(PdfDesignSystem.TitleSize).FontColor(PdfDesignSystem.Ink);
                }
            });
            column.Item().PaddingTop(4).Height(0.8f).Background(PdfDesignSystem.StrongRule);
        });
    }

    private void ComposeShipmentIdentity(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(72);
                columns.RelativeColumn();
                columns.ConstantColumn(72);
                columns.RelativeColumn();
            });

            MetaRow(table,
                Label("نام کشتی", "Vessel"), model.VesselName, false,
                Label("محصول", "Product"), model.ProductName, false);
            MetaRow(table,
                Label("کد محموله", "Shipment code"), model.ShipmentCode, true,
                Label("قرارداد", "Contract"), model.ContractNumber, true);
            MetaRow(table,
                Label("تاریخ حرکت", "Departure"), model.DepartureDateText, true,
                Label("تاریخ رسیدن", "Arrival"), model.ArrivalDateText, true);

            // مسیر یک سطر کامل می‌گیرد تا نام‌های طولانی مبدأ/مقصد شکسته نشوند.
            table.Cell().Element(MetaLabelCell).Text(Label("مسیر", "Route")).SemiBold().FontSize(LabelSize);
            table.Cell().ColumnSpan(3).Element(cell => MetaValueCell(cell, false))
                .Text($"{model.Origin} - {model.Destination}").FontSize(ValueSize);
        });
    }

    private void MetaRow(
        TableDescriptor table,
        string firstLabel,
        string firstValue,
        bool firstIsLtr,
        string secondLabel,
        string secondValue,
        bool secondIsLtr)
    {
        table.Cell().Element(MetaLabelCell).Text(firstLabel).SemiBold().FontSize(LabelSize);
        table.Cell().Element(cell => MetaValueCell(cell, firstIsLtr)).Text(firstValue).FontSize(ValueSize);
        table.Cell().Element(MetaLabelCell).Text(secondLabel).SemiBold().FontSize(LabelSize);
        table.Cell().Element(cell => MetaValueCell(cell, secondIsLtr)).Text(secondValue).FontSize(ValueSize);
    }

    private static IContainer MetaLabelCell(IContainer container)
        => PdfDesignSystem.HeaderCell(container)
            .BorderBottom(0.75f).BorderColor(PdfDesignSystem.Border)
            .AlignRight();

    private IContainer MetaValueCell(IContainer container, bool ltr)
    {
        var target = PdfDesignSystem.BodyCell(container)
            .BorderBottom(0.75f).BorderColor(PdfDesignSystem.Border);
        return ltr ? target.Element(Ltr).AlignRight() : target.AlignRight();
    }

    private static void ComposeSectionTitle(IContainer container, string title)
    {
        container.PaddingBottom(3).Text(title)
            .SemiBold().FontSize(SectionTitleSize).FontColor(PdfDesignSystem.Ink);
    }

    // ستون‌ها عیناً همان ستون‌های خروجی اکسل تب خلاصه‌اند.
    private void ComposeSummaryTable(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1.7f);
                columns.RelativeColumn(1.05f);
                columns.RelativeColumn(1.15f);
                columns.RelativeColumn(1.4f);
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), Label("شرح", "Line"));
                HeaderCell(header.Cell(), Label("مقدار MT", "Quantity MT"));
                HeaderCell(header.Cell(), Label("مبلغ USD", "Amount USD"));
                HeaderCell(header.Cell(), Label("جزئیات", "Details"));
                header.Cell().ColumnSpan(4).Element(cell => PdfDesignSystem.TableSeparator(cell, 1.5f));
            });

            foreach (var row in model.Rows)
            {
                var background = row.IsTotal ? PdfDesignSystem.TotalsBackground : "#FFFFFF";

                SummaryTextCell(table.Cell(), row.Label, background, semiBold: row.IsTotal);
                SummaryNumberCell(table.Cell(), row.QuantityText, background, row.IsTotal, PdfDesignSystem.Ink);
                SummaryNumberCell(table.Cell(), row.AmountText, background, row.IsTotal, ToneColor(row.Tone));
                SummaryTextCell(table.Cell(), row.DetailText, background);

                table.Cell().ColumnSpan(4).Element(cell => PdfDesignSystem.TableSeparator(
                    cell,
                    row.IsTotal ? 1.5f : 0.75f));
            }
        });
    }

    private static void HeaderCell(IContainer container, string text)
        => container.Element(cell => PdfDesignSystem.HeaderCell(cell)).AlignCenter()
            .Text(text).SemiBold().FontSize(PdfDesignSystem.TableSize).FontColor(PdfDesignSystem.Ink);

    private static void SummaryTextCell(
        IContainer container,
        string text,
        string background,
        bool semiBold = false)
    {
        var value = container.Element(cell => PdfDesignSystem.BodyCell(cell, background)).AlignRight();
        var descriptor = value.Text(text).FontSize(PdfDesignSystem.TableSize).FontColor(PdfDesignSystem.Ink);
        if (semiBold)
        {
            descriptor.SemiBold();
        }
    }

    // اعداد همیشه چپ‌به‌راست و با ارقام انگلیسی نوشته می‌شوند، مثل بقیهٔ خروجی‌های سیستم.
    private static void SummaryNumberCell(
        IContainer container,
        string text,
        string background,
        bool semiBold,
        string color)
    {
        container.Element(cell => PdfDesignSystem.BodyCell(cell, background))
            .Element(Ltr).AlignRight().Text(numeric =>
            {
                var value = numeric.Span(PdfDesignSystem.ToEnglishDigits(text))
                    .FontSize(ValueSize).FontColor(color);
                if (semiBold)
                {
                    value.SemiBold();
                }
            });
    }

    private string Label(string fa, string en) => isEnglish ? en : fa;

    private string FormatGregorianPrintDate(DateTime value)
        => (isEnglish ? "Print date: " : "تاریخ چاپ: ")
            + Helpers.DateDisplay.Format(value, "yyyy/MM/dd");

    private static string ToneColor(ShipmentSummaryPdfTone tone) => tone switch
    {
        ShipmentSummaryPdfTone.Positive => PdfDesignSystem.Positive,
        ShipmentSummaryPdfTone.Negative => PdfDesignSystem.Negative,
        ShipmentSummaryPdfTone.Warning => PdfDesignSystem.Muted,
        _ => PdfDesignSystem.Ink
    };
}
