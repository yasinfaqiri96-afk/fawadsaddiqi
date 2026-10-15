using PTGOilSystem.Web.Models.ContractJourney;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PTGOilSystem.Web.Services.Exports;

/// <summary>
/// سند رسمی «خلاصهٔ گشت قرارداد» با همان زبان گرافیکی صورت‌حساب طرف‌حساب:
/// سربرگ شرکت، نوار خلاصه، جعبه‌های اطلاعات، جدول‌های عنوان/عدد، امضا و فوتر.
/// اندازهٔ قلم‌ها و قالب اعداد از PdfDesignSystem می‌آید؛ اینجا هیچ محاسبه‌ای نیست.
/// </summary>
internal sealed class ContractJourneySummaryPdfDocument(
    ContractJourneySummaryPdfModel model,
    PdfBrandHeader brandHeader,
    bool isEnglish = false) : IDocument
{
    private const string Ink = PdfDesignSystem.Ink;
    private const string Muted = PdfDesignSystem.Muted;
    private const string Border = PdfDesignSystem.Border;
    private const string Green = PdfDesignSystem.Positive;
    private const string Red = PdfDesignSystem.Negative;
    private const string Amber = "#B45309";
    private const string ValueInk = "#3D4655";

    // نام گشت باید در یک نگاه دیده شود؛ بقیهٔ اندازه‌ها همان مقیاس بقیهٔ خروجی‌هاست.
    private const float HeroTitleSize = 16f;
    private const float SectionTitleSize = PdfDesignSystem.MetaSize;
    private const float LabelSize = 7f;
    private const float UnitSize = 6.5f;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = model.DocumentTitle,
        Author = brandHeader.CompanyName,
        Subject = model.JourneyName
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
            page.DefaultTextStyle(style => PdfDesignSystem.DefaultTextStyle(style, isEnglish));
            page.Header().Column(column =>
            {
                column.Item().ShowOnce().Element(ComposeFullHeader);
                column.Item().SkipOnce().Element(ComposeCompactHeader);
            });
            page.Content().PaddingTop(10).Element(Direct).Column(ComposeContent);
            page.Footer().PaddingTop(8).Element(footer =>
                PdfDesignSystem.ComposeFooter(footer, brandHeader.CompanyName, isEnglish));
        });
    }

    private IContainer Direct(IContainer container)
        => isEnglish ? container.ContentFromLeftToRight() : container.ContentFromRightToLeft();

    private static IContainer Ltr(IContainer container) => container.ContentFromLeftToRight();

    private void ComposeFullHeader(IContainer container)
    {
        Direct(container).Column(column =>
        {
            column.Item().Element(brand => PdfDesignSystem.ComposeBrandHeader(brand, brandHeader));
            column.Item().PaddingTop(9).Element(ComposeJourneyTitle);
            column.Item().PaddingTop(7).Height(0.8f).Background(PdfDesignSystem.StrongRule);
            if (model.HeadlineMetrics.Count > 0)
            {
                column.Item().PaddingTop(9).Element(strip => PdfDesignSystem.ComposeSummaryStrip(
                    strip,
                    model.HeadlineMetrics
                        .Select(metric => new PdfSummaryMetric(
                            metric.Label,
                            string.IsNullOrWhiteSpace(metric.Unit)
                                ? metric.Value
                                : $"{metric.Value} {metric.Unit}",
                            ToneColor(metric.Tone),
                            metric.Detail))
                        .ToList(),
                    isEnglish));
            }
        });
    }

    private void ComposeCompactHeader(IContainer container)
    {
        Direct(container).Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem(1.6f).AlignMiddle().Text(model.JourneyName)
                    .Bold().FontSize(PdfDesignSystem.TitleSize).FontColor(Ink);
                row.RelativeItem().AlignMiddle().AlignLeft().Element(Ltr)
                    .Text(PdfDesignSystem.FormatPrintDate(model.GeneratedAt, isEnglish))
                    .FontSize(PdfDesignSystem.ReportMetaSize).FontColor(Muted);
            });
            column.Item().PaddingTop(4).Height(0.8f).Background(PdfDesignSystem.StrongRule);
        });
    }

    private void ComposeJourneyTitle(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem(2.4f).Column(column =>
            {
                column.Item().Text(model.JourneyName).Bold().FontSize(HeroTitleSize).FontColor(Ink);
                if (!string.IsNullOrWhiteSpace(model.JourneySubtitle))
                {
                    column.Item().PaddingTop(3).Text(model.JourneySubtitle)
                        .FontSize(PdfDesignSystem.MetaSize).FontColor(Muted);
                }
            });
            row.RelativeItem().AlignMiddle().AlignLeft().Column(column =>
            {
                if (!string.IsNullOrWhiteSpace(model.StatusText))
                {
                    column.Item().AlignLeft().Element(Direct)
                        .Background(PdfDesignSystem.SummaryBackground)
                        .CornerRadius(9).PaddingVertical(4).PaddingHorizontal(9)
                        .Text(model.StatusText).SemiBold()
                        .FontSize(PdfDesignSystem.MetaSize).FontColor(ToneColor(model.StatusTone));
                }
                column.Item().PaddingTop(4).AlignLeft().Element(Ltr)
                    .Text(PdfDesignSystem.FormatPrintDate(model.GeneratedAt, isEnglish))
                    .FontSize(PdfDesignSystem.ReportMetaSize).FontColor(Muted);
            });
        });
    }

    private void ComposeContent(ColumnDescriptor column)
    {
        column.Spacing(11);

        if (model.ContractInfo.Count > 0 || model.PartyInfo.Count > 0)
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Element(box => ComposeInfoBox(
                    box, Label("مشخصات قرارداد", "Contract details"), Red, model.ContractInfo));
                row.ConstantItem(10);
                row.RelativeItem().Element(box => ComposeInfoBox(
                    box, Label("طرف قرارداد", "Counterparty"), Green, model.PartyInfo));
            });
        }

        if (model.Stages.Count > 0)
        {
            column.Item().Element(ComposeStageTable);
        }

        foreach (var section in model.Sections.Where(section => section.Lines.Count > 0))
        {
            column.Item().Element(container => ComposeSectionTable(container, section));
        }

        if (model.Warnings.Count > 0)
        {
            column.Item().ShowEntire().Element(ComposeWarnings);
        }

        column.Item().ShowEntire().Element(ComposeClosing);
    }

    private void ComposeInfoBox(
        IContainer container,
        string title,
        string accent,
        IReadOnlyList<ContractJourneySummaryPdfLine> lines)
    {
        container.Border(0.7f).BorderColor(Border).CornerRadius(4).Padding(10)
            .Element(Direct).Column(column =>
            {
                column.Item().PaddingBottom(6).Text(title)
                    .SemiBold().FontSize(SectionTitleSize).FontColor(accent);
                foreach (var line in lines)
                {
                    column.Item().PaddingBottom(3).Row(row =>
                    {
                        row.ConstantItem(78).Text(line.Label).SemiBold().FontSize(LabelSize).FontColor(Ink);
                        row.RelativeItem().AlignRight().Text(PdfDesignSystem.ToEnglishDigits(line.Value))
                            .FontSize(LabelSize)
                            .FontColor(line.Tone == ContractJourneySummaryPdfTone.Neutral ? ValueInk : ToneColor(line.Tone));
                    });
                }
            });
    }

    // چرخه قرارداد: هر مرحله دو سطر دارد — هر آمار دقیقاً زیر عنوان خودش، در ستون «مقدار».
    private void ComposeStageTable(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(20);
                    columns.RelativeColumn(1.25f);
                    columns.RelativeColumn(2f);
                    columns.ConstantColumn(96);
                    columns.ConstantColumn(64);
                });
                table.Header(header =>
                {
                    // عنوان داخل هدر است تا در صفحهٔ بعد هم تکرار شود و جدول بی‌عنوان نماند.
                    header.Cell().Row(1).Column(1).ColumnSpan(5)
                        .Element(title => SectionTitle(title, Label("چرخه قرارداد", "Contract lifecycle")));
                    HeaderCell(header.Cell().Row(2).Column(1), "#");
                    HeaderCell(header.Cell().Row(2).Column(2), Label("مرحله", "Stage"));
                    HeaderCell(header.Cell().Row(2).Column(3), Label("شرح", "Line"));
                    HeaderCell(header.Cell().Row(2).Column(4), Label("مقدار", "Value"));
                    HeaderCell(header.Cell().Row(2).Column(5), Label("وضعیت", "Status"));
                    header.Cell().Row(3).Column(1).ColumnSpan(5)
                        .Element(cell => PdfDesignSystem.TableSeparator(cell, 1.5f));
                });

                // اندیس سطر/ستون صریح است تا سلول‌های چندسطری هرگز جابه‌جا نشوند.
                uint row = 1;
                foreach (var stage in model.Stages)
                {
                    var tone = ToneColor(stage.Tone);
                    var span = (uint)Math.Max(stage.Metrics.Count, 1);

                    table.Cell().Row(row).Column(1).RowSpan(span)
                        .Element(BodyCell).AlignCenter().Element(Ltr)
                        .Text(stage.Number.ToString())
                        .SemiBold().FontSize(PdfDesignSystem.NumericTableSize).FontColor(tone);
                    table.Cell().Row(row).Column(2).RowSpan(span).Element(BodyCell)
                        .Text(stage.Title).SemiBold().FontSize(PdfDesignSystem.TableSize).FontColor(Ink);
                    table.Cell().Row(row).Column(5).RowSpan(span).Element(BodyCell).AlignCenter()
                        .Text(stage.StatusText)
                        .SemiBold().FontSize(PdfDesignSystem.TableSize).FontColor(tone);

                    for (var index = 0; index < stage.Metrics.Count; index++)
                    {
                        var metric = stage.Metrics[index];
                        table.Cell().Row(row + (uint)index).Column(3).Element(BodyCell)
                            .Text(metric.Label).FontSize(PdfDesignSystem.TableSize).FontColor(ValueInk);
                        ValueCell(table.Cell().Row(row + (uint)index).Column(4), metric);
                    }

                    row += span;
                    table.Cell().Row(row).Column(1).ColumnSpan(5)
                        .Element(cell => PdfDesignSystem.TableSeparator(cell));
                    row++;
                }
            });
        });
    }

    private void ComposeSectionTable(IContainer container, ContractJourneySummaryPdfSection section)
    {
        var hasNotes = section.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Note));
        var columnCount = hasNotes ? 3u : 2u;

        container.Column(column =>
        {
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2f);
                    columns.ConstantColumn(104);
                    if (hasNotes)
                    {
                        columns.RelativeColumn(1.8f);
                    }
                });
                table.Header(header =>
                {
                    header.Cell().Row(1).Column(1).ColumnSpan(columnCount)
                        .Element(title => SectionTitle(title, section.Title));
                    HeaderCell(header.Cell().Row(2).Column(1), Label("شرح", "Line"));
                    HeaderCell(header.Cell().Row(2).Column(2), Label("مقدار", "Value"));
                    if (hasNotes)
                    {
                        HeaderCell(header.Cell().Row(2).Column(3), Label("توضیح", "Note"));
                    }
                    header.Cell().Row(3).Column(1).ColumnSpan(columnCount)
                        .Element(cell => PdfDesignSystem.TableSeparator(cell, 1.5f));
                });

                foreach (var line in section.Lines)
                {
                    table.Cell().Element(BodyCell)
                        .Text(line.Label).FontSize(PdfDesignSystem.TableSize).FontColor(Ink);
                    ValueCell(table.Cell(), line);
                    if (hasNotes)
                    {
                        table.Cell().Element(BodyCell)
                            .Text(PdfDesignSystem.ToEnglishDigits(line.Note ?? string.Empty))
                            .FontSize(PdfDesignSystem.ReportMetaSize).FontColor(Muted);
                    }
                    table.Cell().ColumnSpan(columnCount)
                        .Element(cell => PdfDesignSystem.TableSeparator(cell));
                }
            });
        });
    }

    // عدد همیشه چپ‌به‌راست و با قالب مرکزی؛ واحد کوچک و کم‌رنگ کنارش تا ستون هم‌تراز بماند.
    private void ValueCell(IContainer container, ContractJourneySummaryPdfLine line)
    {
        var color = line.Tone == ContractJourneySummaryPdfTone.Neutral ? Ink : ToneColor(line.Tone);
        container.ShowEntire().Element(BodyCell).AlignRight().Element(Ltr).Text(text =>
        {
            text.Span(PdfDesignSystem.ToEnglishDigits(line.Value))
                .SemiBold().FontSize(PdfDesignSystem.NumericTableSize).FontColor(color);
            if (!string.IsNullOrWhiteSpace(line.Unit))
            {
                text.Span(" " + line.Unit).FontSize(UnitSize).FontColor(Muted);
            }
        });
    }

    private void SectionTitle(IContainer container, string title)
    {
        container.PaddingBottom(5).Row(row =>
        {
            row.ConstantItem(3).Height(11).Background(Green);
            row.ConstantItem(6);
            row.RelativeItem().AlignMiddle().Text(title)
                .SemiBold().FontSize(SectionTitleSize).FontColor(Ink);
        });
    }

    private void ComposeWarnings(IContainer container)
    {
        container.Border(0.7f).BorderColor(Red).CornerRadius(4).Padding(9)
            .Element(Direct).Column(column =>
            {
                column.Item().Text(Label("نکات نیازمند توجه", "Needs attention"))
                    .SemiBold().FontSize(SectionTitleSize).FontColor(Red);
                foreach (var warning in model.Warnings)
                {
                    column.Item().PaddingTop(3).Text("• " + warning)
                        .FontSize(LabelSize).FontColor(ValueInk);
                }
            });
    }

    private void ComposeClosing(IContainer container)
    {
        container.PaddingTop(2).Row(row =>
        {
            row.RelativeItem(1.55f).Border(0.7f).BorderColor(Border).CornerRadius(4).Padding(9)
                .Element(Direct).Column(note =>
                {
                    note.Item().Text(Label("یادداشت", "Note"))
                        .SemiBold().FontSize(SectionTitleSize).FontColor(Green);
                    note.Item().PaddingTop(5)
                        .Text(string.IsNullOrWhiteSpace(model.Note) ? "—" : model.Note!.Trim())
                        .FontSize(LabelSize).FontColor(Muted);
                });
            row.ConstantItem(12);
            row.RelativeItem().BorderLeft(0.7f).BorderColor(Border).PaddingLeft(12)
                .Element(Direct).Column(signature =>
                {
                    signature.Item().Text(Label("تأیید مدیریت", "Management approval"))
                        .SemiBold().FontSize(PdfDesignSystem.TableSize).FontColor(Red);
                    signature.Item().PaddingTop(22).LineHorizontal(0.7f).LineColor("#B7BEC8");
                    signature.Item().PaddingTop(3).Text(brandHeader.CompanyName)
                        .SemiBold().FontSize(LabelSize).FontColor(Ink);
                });
        });
    }

    private static void HeaderCell(IContainer container, string text)
        => container.Element(cell => PdfDesignSystem.HeaderCell(cell)).AlignCenter()
            .Text(text).Bold().FontSize(PdfDesignSystem.TableSize).FontColor(Ink);

    private static IContainer BodyCell(IContainer container)
        => PdfDesignSystem.BodyCell(container);

    private string Label(string fa, string en) => isEnglish ? en : fa;

    private static string ToneColor(ContractJourneySummaryPdfTone tone) => tone switch
    {
        ContractJourneySummaryPdfTone.Positive => Green,
        ContractJourneySummaryPdfTone.Negative => Red,
        ContractJourneySummaryPdfTone.Warning => Amber,
        _ => Ink
    };
}
