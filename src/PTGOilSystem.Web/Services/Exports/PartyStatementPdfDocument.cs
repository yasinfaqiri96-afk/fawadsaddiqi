using System.Globalization;
using PTGOilSystem.Web.Models.PartyStatements;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PTGOilSystem.Web.Services.Exports;

/// <summary>
/// صورت‌حساب رسمی طرف حساب (PDF) به شکل «صورت‌حساب تجارتی»: سربرگ با مشخصات طرف‌حساب و
/// لوگو، کارت‌های خلاصه، و جدول «تاریخ | نوع | شرح | مقدار | قیمت فی تن | مبلغ معامله |
/// دریافت/پرداخت | بیلانس». ستون‌ها فقط تفکیک نمایشیِ همان رسید/برد سرویس‌اند
/// (<see cref="PartyStatementPresentation"/>)؛ اعداد، جهت و فرمول بیلانس دست‌نخورده‌اند.
/// جدول خلاصهٔ قراردادها (تب «قراردادها») همان شبکهٔ قبلی را نگه می‌دارد.
/// </summary>
internal sealed class PartyStatementPdfDocument(
    PartyStatementResult statement,
    string webRootPath,
    PdfBrandHeader brandHeader,
    bool isEnglish = false,
    SupplierContractStatementViewModel? contractGrouping = null) : IDocument
{
    private string Flow(string fa, string en) => isEnglish ? en : fa;

    private const string Ink = PdfDesignSystem.Ink;
    private const string Muted = PdfDesignSystem.Muted;
    private const string Grid = PdfDesignSystem.SheetGrid;
    private const string HeaderInk = PdfDesignSystem.SheetHeaderInk;
    private const string HeaderFill = PdfDesignSystem.SheetHeaderFill;
    private const string LabelFill = PdfDesignSystem.SheetLabelFill;
    private const string TotalFill = PdfDesignSystem.SheetTotalFill;
    private const string PlainFill = PdfDesignSystem.SheetBodyFill;
    private const string AmountFill = PdfDesignSystem.SheetAmountFill;
    private const string PositiveFill = PdfDesignSystem.SheetPositiveFill;
    private const string BalanceFill = PdfDesignSystem.SheetBalanceFill;

    private const float BodySize = PdfDesignSystem.SheetBodySize;
    private const float HeaderSize = PdfDesignSystem.SheetHeaderSize;
    private const float CaptionSize = PdfDesignSystem.SheetCaptionSize;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = statement.Policy.StatementTitleFa,
        Author = brandHeader.CompanyName,
        Subject = statement.DocumentInfo.StatementNumber
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    // رنگ‌های محدود و رسمی صورت‌حساب: سرمه‌ای برای نام و اعداد کلیدی، آبی کم‌رنگ برای سربرگ
    // و جمع جدول، خاکستری برای برچسب‌ها و خطوط بسیار روشن.
    private const string Navy = "#14295A";
    private const string Accent = "#1F4E9C";
    private const string TextInk = "#1F2A37";
    private const string LabelInk = "#5B6576";
    private const string Line = "#D9E0EA";
    private const string SoftFill = "#E9EFF8";

    // لوگوی رسمی برند خروجی؛ اگر فایل نباشد، نام برند نمایش داده می‌شود.
    private const string BrandLogoRelativePath = ExcelDesignSystem.LogoRelativePath;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            // A4 عمودی مثل نمونهٔ مرجع؛ فقط ستون‌های عملیاتی (Platts/Premium) و جدول قراردادها افقی‌اند.
            page.Size(OperationalColumns.Count > 0 || contractGrouping is not null
                ? PageSizes.A4.Landscape()
                : PageSizes.A4);
            page.MarginHorizontal(28);
            page.MarginTop(26);
            page.MarginBottom(18);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => PdfDesignSystem.SheetTextStyle(style, isEnglish));
            page.Header().Column(column =>
            {
                column.Item().ShowOnce().Element(ComposeStatementHeader);
                column.Item().SkipOnce().Element(ComposeCompactHeader);
            });
            page.Content().PaddingTop(4).ContentFromRightToLeft().Column(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    /* ------------------------------------------------------------------
       سربرگ صفحهٔ اول: در یک سو دوره و مشخصات طرف‌حساب، در سوی دیگر لوگو و زیر آن تاریخ
       چاپ و شمارهٔ گزارش؛ یک خط نازک زیر کل سربرگ.
       ------------------------------------------------------------------ */
    private void ComposeStatementHeader(IContainer container)
    {
        var info = statement.DocumentInfo;
        var partyType = PartyStatementPresentation.PartyTypeLabel(statement.Party.PartyType, isEnglish);
        container.ContentFromRightToLeft().Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().PaddingTop(4).Column(identity =>
                {
                    identity.Item().AlignRight().Text(text =>
                    {
                        text.DefaultTextStyle(style => style.FontSize(11).FontColor(LabelInk));
                        text.Span(Flow("از ", "From "));
                        text.Span(info.PeriodFrom.HasValue ? FormatDate(info.PeriodFrom.Value) : Flow("ابتدای حساب", "start"));
                        text.Span(Flow("   تا ", "   to "));
                        text.Span(info.PeriodTo.HasValue ? FormatDate(info.PeriodTo.Value) : Flow("امروز", "today"));
                    });
                    identity.Item().PaddingTop(22).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(78);
                            columns.ConstantColumn(150);
                            columns.RelativeColumn();
                        });
                        InfoRow(table, Flow($"نام {partyType}:", "Name:"), statement.PartyInfo.Name, isName: true);
                        InfoRow(table, Flow($"کد {partyType}:", "Code:"), ValueOrDash(statement.PartyInfo.Code));
                        InfoRow(table, Flow("نوع حساب:", "Account type:"), partyType);
                        InfoRow(table, Flow("واحد پول:", "Currency:"), info.BaseCurrencyCode);
                    });
                });
                row.ConstantItem(200).Column(side =>
                {
                    side.Item().Height(72).AlignLeft().Element(ComposeLogo);
                    side.Item().PaddingTop(16).LineHorizontal(0.6f).LineColor(Line);
                    side.Item().PaddingTop(8).Element(cell => SideInfo(cell, Flow("تاریخ چاپ:", "Printed:"), FormatDate(info.StatementDate)));
                    side.Item().PaddingTop(4).Element(cell => SideInfo(cell, Flow("شماره گزارش:", "Report no.:"), info.StatementNumber));
                });
            });
            column.Item().PaddingTop(12).LineHorizontal(0.8f).LineColor(Line);
        });
    }

    private void InfoRow(TableDescriptor table, string label, string value, bool isName = false)
    {
        table.Cell().PaddingVertical(isName ? 2 : 2.5f).AlignRight().AlignMiddle()
            .Text(label).FontSize(9.5f).FontColor(LabelInk);
        var text = table.Cell().PaddingVertical(isName ? 2 : 2.5f).AlignRight().AlignMiddle()
            .Text(PdfDesignSystem.ToEnglishDigits(value)).FontColor(Navy);
        if (isName)
            text.Bold().FontSize(13);
        else
            text.SemiBold().FontSize(10);
        table.Cell();
    }

    private static void SideInfo(IContainer container, string label, string value)
    {
        container.Row(row =>
        {
            row.AutoItem().AlignRight().Text(label).FontSize(9.5f).FontColor(LabelInk);
            row.RelativeItem().AlignLeft().ContentFromLeftToRight()
                .Text(PdfDesignSystem.ToEnglishDigits(value)).FontSize(9.5f).FontColor(TextInk);
        });
    }

    private void ComposeLogo(IContainer container)
    {
        var configuredLogo = ResolveWebAsset(BrandLogoRelativePath);
        var logo = configuredLogo is not null && File.Exists(configuredLogo) ? configuredLogo : brandHeader.LogoPath;
        if (!string.IsNullOrWhiteSpace(logo) && File.Exists(logo))
        {
            container.AlignLeft().Image(logo).FitArea();
            return;
        }

        container.AlignLeft().AlignMiddle().Text(brandHeader.CompanyName).Bold().FontSize(14).FontColor(Navy);
    }

    // صفحه‌های بعد: فقط نام طرف‌حساب و تاریخ چاپ با یک خط؛ سربرگ کامل تکرار نمی‌شود.
    private void ComposeCompactHeader(IContainer container)
    {
        PdfDesignSystem.ComposeReportHeader(
            container,
            statement.PartyInfo.Name,
            statement.DocumentInfo.GeneratedAtUtc,
            filters: null,
            metrics: [],
            isEnglish: isEnglish,
            brand: null,
            compact: true);
    }

    private void ComposeContent(ColumnDescriptor column)
    {
        column.Spacing(12);
        column.Item().Element(ComposeSummaryStrip);
        // تب «قراردادها» جدول خلاصهٔ قراردادی دارد، بقیهٔ تب‌ها جدول گردش حساب.
        column.Item().Element(contractGrouping is null ? ComposeLedgerTable : ComposeContractTable);
    }

    /* ------------------------------------------------------------------
       خلاصه: یک نوار با چهار عدد کلیدی، جداشده با خط عمودی نازک و یک خط زیر آن.
       اعداد از همان Summary سرویس می‌آیند.
       ------------------------------------------------------------------ */
    private sealed record SummaryMetric(string Label, decimal? Value);

    private void ComposeSummaryStrip(IContainer container)
    {
        var metrics = BuildSummaryMetrics();
        var currency = statement.DocumentInfo.BaseCurrencyCode;
        container.BorderBottom(0.8f).BorderColor(Line).PaddingBottom(10).Row(row =>
        {
            for (var index = 0; index < metrics.Count; index++)
            {
                if (index > 0)
                    row.ConstantItem(0.8f).Background(Line);
                var metric = metrics[index];
                row.RelativeItem().PaddingHorizontal(4).Column(item =>
                {
                    item.Item().AlignCenter().Text(metric.Label).SemiBold().FontSize(9.5f).FontColor(Accent);
                    var value = $"{FormatAmount(metric.Value)} {currency}";
                    item.Item().PaddingTop(4).AlignCenter().ContentFromLeftToRight()
                        .Text(value).Bold().FontSize(SummaryValueSize(value, metrics.Count)).FontColor(Navy);
                });
            }
        });
    }

    // عدد خلاصه همیشه در یک خط و داخل خانهٔ خودش: اندازه از عرض خانه و طول متن حساب می‌شود
    // (عرض تقریبی هر رقم Bold ≈ ۰٫۵۸ اندازهٔ فونت) و هرگز از ۱۵pt بزرگ‌تر نمی‌شود.
    private static float SummaryValueSize(string value, int itemCount)
    {
        const float usableWidth = 539f;
        var itemWidth = usableWidth / Math.Max(1, itemCount) - 12f;
        var fitted = itemWidth / (Math.Max(1, value.Length) * 0.58f);
        return (float)Math.Round(Math.Clamp(fitted, 9f, 15f), 1);
    }

    private IReadOnlyList<SummaryMetric> BuildSummaryMetrics()
    {
        var summary = statement.Summary;
        var isRub = summary.IsRubPresentation;
        var plan = Layout;
        var metrics = new List<SummaryMetric> { new(Flow("بیلانس اول دوره", "Opening balance"), OpeningBalance()) };
        if (plan.ShowTrade)
            metrics.Add(new(Labels.TradeTotal(isEnglish)!, summary.TradeTotalFor(isRub)));
        if (plan.HasReceived || !plan.HasPaid)
            metrics.Add(new(Labels.ReceivedTotal(isEnglish), summary.ReceivedTotalFor(isRub)));
        if (plan.HasPaid)
            metrics.Add(new(Labels.PaidTotal(isEnglish), summary.PaidTotalFor(isRub)));
        metrics.Add(new(Flow("بیلانس فعلی", "Current balance"), ClosingBalance()));
        return metrics;
    }

    private PartyStatementColumnLabels Labels => PartyStatementPresentation.ColumnsFor(statement.Party.PartyType);

    /* ------------------------------------------------------------------
       اطلاعات طرف حساب و سند: دو بلوکِ برچسب/مقدار با همان شبکه.
       ------------------------------------------------------------------ */
    private void ComposePartyInfo(IContainer container)
    {
        ComposeInfoBlock(
            container,
            statement.Policy.PartyInformationTitleFa,
            [
                ("نام", statement.PartyInfo.Name, false),
                ("کد حساب", ValueOrDash(statement.PartyInfo.Code), true),
                ("تلفن", ValueOrDash(statement.PartyInfo.Phone), true),
                ("آدرس", ValueOrDash(statement.PartyInfo.Address), false)
            ]);
    }

    private void ComposeStatementInfo(IContainer container)
    {
        ComposeInfoBlock(
            container,
            "اطلاعات صورت حساب",
            [
                ("شماره", statement.DocumentInfo.StatementNumber, true),
                ("تاریخ", FormatDate(statement.DocumentInfo.StatementDate), true),
                ("دوره", FormatPeriod(statement.DocumentInfo.PeriodFrom, statement.DocumentInfo.PeriodTo), true),
                ("ارز", statement.DocumentInfo.BaseCurrencyCode, true)
            ]);
    }

    private static void ComposeInfoBlock(
        IContainer container,
        string title,
        IReadOnlyList<(string Label, string Value, bool Ltr)> lines)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(74);
                columns.RelativeColumn();
            });

            table.Cell().ColumnSpan(2)
                .Element(cell => PdfDesignSystem.HeaderCell(cell, HeaderFill, 5f))
                .Text(title).Bold().FontSize(HeaderSize).FontColor(HeaderInk);

            foreach (var (label, value, ltr) in lines)
            {
                table.Cell().Element(cell => PdfDesignSystem.SheetCell(cell, LabelFill))
                    .Text(label).SemiBold().FontSize(BodySize).FontColor(HeaderInk);

                // مقدارهای لاتین (کد، تاریخ، ارز) هم کنار برچسب خودشان می‌مانند؛
                // فقط ترتیب نویسه‌ها چپ‌به‌راست می‌شود، نه جای خانه.
                var target = table.Cell().Element(cell => PdfDesignSystem.SheetCell(cell, PlainFill));
                if (ltr)
                    target = target.AlignRight().ContentFromLeftToRight();
                target.Text(PdfDesignSystem.ToEnglishDigits(value)).FontSize(BodySize);
            }
        });
    }

    // ستون‌های عملیاتیِ تب «بارگیری‌ها» که در شرح جای ندارند (Platts و Premium). مقدار و نرخ
    // واحد در خط دوم شرح می‌آیند و ستون جدا نمی‌گیرند.
    private sealed record OperationalColumn(
        string Title,
        Func<PartyStatementRow, decimal?> Value,
        int Decimals);

    private IReadOnlyList<OperationalColumn>? operationalColumns;

    private IReadOnlyList<OperationalColumn> OperationalColumns => operationalColumns ??= BuildOperationalColumns();

    private IReadOnlyList<OperationalColumn> BuildOperationalColumns()
    {
        var options = statement.ColumnOptions;
        var columns = new List<OperationalColumn>();
        if (options.ShowPlatts)
            columns.Add(new OperationalColumn("Platts", row => row.PlattsPrice, 2));
        if (options.ShowPremiumOrDiscount)
            columns.Add(new OperationalColumn("Premium / Discount", row => row.PremiumOrDiscount, 2));
        return columns;
    }

    /* ------------------------------------------------------------------
       جدول (راست به چپ): # | تاریخ | شرح معامله | مبلغ معامله | دریافت | پرداخت | بیلانس
       ستون‌ها فقط تفکیک نمایشیِ همان رسید/برد سرویس‌اند. دریافت (سبز) و پرداخت (سرخ) همیشه
       دو ستون جدا هستند، مثل صفحهٔ وب. هیچ متنی بریده نمی‌شود و هر سطر یک‌جا به صفحهٔ بعد می‌رود.
       ------------------------------------------------------------------ */
    private sealed record LedgerLayout(
        IReadOnlyList<PartyStatementAmounts> Amounts,
        bool ShowTrade,
        bool HasReceived,
        bool HasPaid);

    private LedgerLayout? layout;

    private LedgerLayout Layout => layout ??= BuildLayout();

    private LedgerLayout BuildLayout()
    {
        var isRub = statement.Summary.IsRubPresentation;
        var amounts = statement.Rows
            .Select(row => PartyStatementPresentation.AmountsFor(row, statement.Party.PartyType, isRub))
            .ToList();
        return new LedgerLayout(
            amounts,
            Labels.HasTradeColumn && amounts.Any(a => a.Trade.HasValue),
            amounts.Any(a => a.Received.HasValue),
            amounts.Any(a => a.Paid.HasValue));
    }

    private void ComposeLedgerTable(IContainer container)
    {
        var operational = OperationalColumns;
        var currency = statement.DocumentInfo.BaseCurrencyCode;
        var labels = Labels;
        var plan = Layout;
        var isRub = statement.Summary.IsRubPresentation;
        const int moneyColumns = 2;
        var columnCount = (uint)(3 + (plan.ShowTrade ? 1 : 0) + operational.Count + moneyColumns + 1);

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(26);
                columns.ConstantColumn(72);
                columns.RelativeColumn();
                if (plan.ShowTrade)
                    columns.ConstantColumn(80);
                foreach (var _ in operational)
                    columns.ConstantColumn(62);
                for (var i = 0; i < moneyColumns; i++)
                    columns.ConstantColumn(74);
                columns.ConstantColumn(82);
            });

            table.Header(header =>
            {
                ColumnHeader(header.Cell(), "#");
                ColumnHeader(header.Cell(), Flow("تاریخ", "Date"));
                ColumnHeader(header.Cell(), Flow("شرح معامله", "Description"));
                if (plan.ShowTrade)
                    ColumnHeader(header.Cell(), labels.Trade(isEnglish)!, currency);
                foreach (var column in operational)
                    ColumnHeader(header.Cell(), column.Title);
                ColumnHeader(header.Cell(), labels.Received(isEnglish), currency);
                ColumnHeader(header.Cell(), labels.Paid(isEnglish), currency);
                ColumnHeader(header.Cell(), Flow("بیلانس", "Balance"), currency);
            });

            if (statement.Rows.Count == 0)
            {
                table.Cell().ColumnSpan(columnCount)
                    .Element(BodyCell)
                    .PaddingVertical(8).AlignCenter()
                    .Text(Flow("در این دوره معامله‌ای ثبت نشده است.", "No transactions in this period."))
                    .FontSize(10).FontColor(LabelInk);
            }

            for (var index = 0; index < statement.Rows.Count; index++)
            {
                var row = statement.Rows[index];
                var amounts = plan.Amounts[index];

                table.Cell().Element(BodyCell).AlignCenter()
                    .Text((index + 1).ToString(CultureInfo.InvariantCulture)).FontSize(9.5f).FontColor(TextInk);
                table.Cell().Element(BodyCell).AlignCenter().ContentFromLeftToRight()
                    .Text(FormatDate(row.Date)).FontSize(9.5f).FontColor(TextInk);
                table.Cell().Element(BodyCell).Element(cell => ComposeDescription(cell, row));
                if (plan.ShowTrade)
                    AmountCell(table.Cell(), amounts.Trade);
                foreach (var column in operational)
                    AmountCell(table.Cell(), column.Value(row), column.Decimals);
                AmountCell(table.Cell(), amounts.Received, color: PdfDesignSystem.Positive);
                AmountCell(table.Cell(), amounts.Paid, color: PdfDesignSystem.Negative);
                AmountCell(table.Cell(), RowBalance(row));
            }

            var summary = statement.Summary;
            table.Cell().ColumnSpan(2).Element(TotalCell).AlignCenter()
                .Text(Flow("جمع دوره", "Period total")).Bold().FontSize(9.5f).FontColor(Navy);
            TotalTextCell(table.Cell(), null);
            if (plan.ShowTrade)
                TotalTextCell(table.Cell(), FormatAmount(summary.TradeTotalFor(isRub)));
            foreach (var _ in operational)
                TotalTextCell(table.Cell(), null);
            TotalTextCell(table.Cell(), FormatAmount(summary.ReceivedTotalFor(isRub)), PdfDesignSystem.Positive);
            TotalTextCell(table.Cell(), FormatAmount(summary.PaidTotalFor(isRub)), PdfDesignSystem.Negative);
            TotalTextCell(table.Cell(), FormatAmount(ClosingBalance()));
        });
    }

    private static void ColumnHeader(IContainer container, string title, string? unit = null)
    {
        container.Background(SoftFill).Border(0.6f).BorderColor(Line)
            .PaddingVertical(5).PaddingHorizontal(3).AlignMiddle().AlignCenter()
            .Column(column =>
            {
                column.Item().AlignCenter().Text(title).Bold().FontSize(9).FontColor(Navy);
                if (!string.IsNullOrWhiteSpace(unit))
                    column.Item().AlignCenter().Text($"({unit})").SemiBold().FontSize(8).FontColor(Navy);
            });
    }

    private static IContainer BodyCell(IContainer container)
        => container.ShowEntire()
            .Border(0.6f).BorderColor(Line)
            .PaddingVertical(6)
            .PaddingHorizontal(6)
            .AlignMiddle();

    private static IContainer TotalCell(IContainer container)
        => container.ShowEntire()
            .Background(SoftFill)
            .Border(0.6f).BorderColor(Line)
            .PaddingVertical(7)
            .PaddingHorizontal(5)
            .AlignMiddle();

    // شرح مطابق نمونهٔ مرجع: عنوان پررنگ، یک خط «مقدار × نرخ» (یا مبلغ به ارز اصلی)، و شمارهٔ
    // سند کم‌رنگ. هیچ توضیح، یادداشت یا متن تکراری دیگری نمی‌آید.
    private void ComposeDescription(IContainer container, PartyStatementRow row)
    {
        container.AlignRight().Column(column =>
        {
            column.Item().AlignRight().Text(PdfDesignSystem.ToEnglishDigits(row.TitleFor(isEnglish)))
                .Bold().FontSize(9.5f).FontColor(TextInk);
            if (row.IsOpeningBalance)
                return;

            var second = PartyStatementPresentation.DescriptionSecondLine(row, statement.DocumentInfo.BaseCurrencyCode, isEnglish);
            if (!string.IsNullOrWhiteSpace(second))
            {
                column.Item().PaddingTop(1.5f).AlignRight()
                    .Text(PartyStatementPresentation.IsolateLeftToRightSegments(PdfDesignSystem.ToEnglishDigits(second)))
                    .FontSize(9).FontColor(TextInk);
            }

            var document = row.DocumentLine;
            if (!string.IsNullOrWhiteSpace(document))
            {
                column.Item().PaddingTop(1.5f).AlignRight().Text(PdfDesignSystem.ToEnglishDigits(document))
                    .FontSize(8).FontColor(LabelInk);
            }
        });
    }

    private void AmountCell(IContainer container, decimal? value, int decimals = 2, string color = TextInk)
    {
        container.Element(BodyCell).AlignCenter().ContentFromLeftToRight()
            .Text(value.HasValue ? FormatAmount(value, decimals) : "-")
            .FontSize(9.5f).FontColor(color);
    }

    private static void TotalTextCell(IContainer container, string? text, string color = Navy)
    {
        container.Element(TotalCell).AlignCenter().ContentFromLeftToRight()
            .Text(text ?? "-").Bold().FontSize(9.5f).FontColor(color);
    }

    // مبلغ با دو رقم اعشار؛ منفی در پرانتز (روش معمول صورت‌حساب‌های تجارتی).
    private string FormatAmount(decimal? value, int decimals = 2)
    {
        if (!value.HasValue)
            return "-";
        var formatted = PdfDesignSystem.FormatPdfNumber(Math.Abs(value.Value), isEnglish, decimals);
        return value.Value < 0m ? $"({formatted})" : formatted;
    }

    // جدول تب «قراردادها»: هر سطر یک قرارداد، دقیقاً همان ستون‌هایی که خود تب نشان می‌دهد.
    private void ComposeContractTable(IContainer container)
    {
        var grouping = contractGrouping!;
        decimal? Money(decimal usd, decimal? rub) => grouping.IsRub ? rub : usd;

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(36);
                columns.RelativeColumn(2.4f);
                columns.ConstantColumn(86);
                columns.ConstantColumn(96);
                columns.ConstantColumn(86);
                columns.ConstantColumn(90);
            });
            table.Header(header =>
            {
                HeaderCell(header.Cell(), "شماره");
                HeaderCell(header.Cell(), "قرارداد");
                HeaderCell(header.Cell(), "مبلغ کل قرارداد (USD)");
                HeaderCell(header.Cell(), "ارزش مقدار بارگیری‌شده");
                HeaderCell(header.Cell(), "پرداخت / دریافت");
                HeaderCell(header.Cell(), "بیلانس قرارداد");
            });
            if (grouping.Rows.Count == 0)
            {
                table.Cell().ColumnSpan(6)
                    .Element(cell => PdfDesignSystem.SheetCell(cell, PlainFill))
                    .PaddingVertical(10).AlignCenter()
                    .Text("در این دوره قراردادی با گردش مالی ثبت نشده است.")
                    .FontSize(BodySize).FontColor(Muted);
            }
            else
            {
                foreach (var row in grouping.Rows)
                {
                    TextCell(
                        table.Cell(),
                        row.Sequence.ToString(CultureInfo.InvariantCulture),
                        PlainFill,
                        center: true,
                        ltr: true);
                    ContractTitleCell(table.Cell(), row);
                    NumberCell(table.Cell(), row.ContractValueUsd, AmountFill);
                    NumberCell(table.Cell(), Money(row.ConfirmedValue, row.ConfirmedValueRub), AmountFill);
                    NumberCell(table.Cell(), Money(row.SettlementTotal, row.SettlementTotalRub), PositiveFill);
                    NumberCell(table.Cell(), Money(row.Balance, row.BalanceRub), BalanceFill);
                }
            }
            table.Cell().ColumnSpan(2).Element(TotalCellStyle)
                .AlignCenter().Text(Flow("جمع دوره", "Period total"))
                .Bold().FontSize(BodySize).FontColor(HeaderInk);
            TotalMoneyCell(table.Cell(), null);
            TotalMoneyCell(table.Cell(), Money(grouping.TotalConfirmedValue, grouping.TotalConfirmedValueRub));
            TotalMoneyCell(table.Cell(), Money(grouping.TotalSettlement, grouping.TotalSettlementRub));
            TotalMoneyCell(table.Cell(), grouping.IsRub
                ? statement.Summary.ClosingBalanceRub
                : statement.Summary.ClosingBalance);
        });
    }

    private void ContractTitleCell(IContainer container, SupplierContractStatementRow row)
    {
        container.ShowEntire().Element(cell => PdfDesignSystem.SheetCell(cell, PlainFill)).Column(column =>
            {
                column.Item().Text(PdfDesignSystem.ToEnglishDigits(row.Title))
                    .SemiBold().FontSize(BodySize);
                if (row.ContractQuantityMt.HasValue || row.LoadedQuantityMt.HasValue)
                {
                    column.Item().Text(
                            $"قرارداد {FormatNumber(row.ContractQuantityMt, 3)} MT / بارگیری {FormatNumber(row.LoadedQuantityMt, 3)} MT")
                        .FontSize(CaptionSize).FontColor(Muted);
                }
            });
    }

    private static void HeaderCell(IContainer container, string text)
    {
        PdfDesignSystem.HeaderCell(container, HeaderFill, 6f)
            .AlignCenter()
            .Text(PdfDesignSystem.ToEnglishDigits(text))
            .Bold().FontSize(HeaderSize).FontColor(HeaderInk);
    }

    private static void TextCell(
        IContainer container,
        string text,
        string background,
        bool center = false,
        bool ltr = false)
    {
        var cell = container.ShowEntire().Element(target => PdfDesignSystem.SheetCell(target, background));
        if (center)
            cell = cell.AlignCenter();
        if (ltr)
            cell = cell.ContentFromLeftToRight();
        cell.Text(PdfDesignSystem.ToEnglishDigits(text)).FontSize(BodySize);
    }

    private void NumberCell(IContainer container, decimal? value, string background, int? decimals = null)
    {
        container.ShowEntire().Element(target => PdfDesignSystem.SheetCell(target, background))
            .AlignCenter().ContentFromLeftToRight()
            .Text(decimals.HasValue ? FormatNumber(value, decimals.Value) : FormatMoney(value))
            .FontSize(BodySize);
    }

    private static IContainer TotalCellStyle(IContainer container)
        => container.ShowEntire().Element(target => PdfDesignSystem.SheetCell(target, TotalFill));

    private void TotalMoneyCell(IContainer container, decimal? value, int? decimals = null)
    {
        TotalCellStyle(container).AlignCenter().ContentFromLeftToRight()
            .Text(decimals.HasValue ? FormatNumber(value, decimals.Value) : FormatMoney(value))
            .Bold().FontSize(BodySize).FontColor(HeaderInk);
    }

    private void ComposeClosingSection(IContainer container)
    {
        container.PaddingTop(2).Row(row =>
        {
            row.RelativeItem(1.55f).Border(PdfDesignSystem.SheetGridThickness).BorderColor(Grid).Padding(9)
                .ContentFromRightToLeft().Column(note =>
                {
                    note.Item().Text("یادداشت")
                        .Bold().FontSize(HeaderSize).FontColor(HeaderInk);
                    note.Item().PaddingTop(5).Text(ValueOrDash(statement.Note))
                        .FontSize(BodySize).FontColor(Muted);
                });
            row.ConstantItem(9);
            row.RelativeItem().Border(PdfDesignSystem.SheetGridThickness).BorderColor(Grid).Padding(9)
                .ContentFromRightToLeft().Column(signature =>
                {
                    signature.Item().Text("تأیید بخش مالی")
                        .Bold().FontSize(HeaderSize).FontColor(HeaderInk);
                    var signaturePath = ResolveWebAsset(statement.Authorization.SignatureImagePath);
                    if (signaturePath is not null)
                        signature.Item().PaddingTop(3).Height(28).AlignRight().Image(signaturePath).FitArea();
                    else
                        signature.Item().PaddingTop(20);
                    signature.Item().PaddingTop(2).LineHorizontal(0.7f).LineColor("#B7BEC8");
                    signature.Item().PaddingTop(3).Text(ValueOrDash(statement.Authorization.AuthorizedByName))
                        .SemiBold().FontSize(BodySize);
                    signature.Item().Text(ValueOrDash(statement.Authorization.AuthorizedByTitle))
                        .FontSize(CaptionSize).FontColor(Muted);
                });
        });
    }

    // فوتر: نام برند مشترک خروجی‌ها و «X / Y» با یک خط نازک.
    private void ComposeFooter(IContainer container)
    {
        container.PaddingTop(6).BorderTop(0.8f).BorderColor(Line).PaddingTop(5).ContentFromLeftToRight().Row(row =>
        {
            row.RelativeItem().Column(brand =>
            {
                brand.Item().Text(brandHeader.CompanyName).Bold().FontSize(10).FontColor(Navy);
                brand.Item().Text("سیستم مدیریت تجارت نفت و گاز").FontSize(7.5f).FontColor(LabelInk);
            });
            row.RelativeItem().AlignRight().AlignMiddle().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(9.5f).FontColor(TextInk));
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });
    }

    private decimal? OpeningBalance() => statement.Summary.IsRubPresentation
        ? statement.Summary.OpeningBalanceRub
        : statement.Summary.OpeningBalance;

    private decimal? ClosingBalance() => statement.Summary.IsRubPresentation
        ? statement.Summary.ClosingBalanceRub
        : statement.Summary.ClosingBalance;

    private decimal? RowBalance(PartyStatementRow row)
        => statement.Summary.IsRubPresentation ? row.RunningBalanceRub : row.RunningBalance;

    private string? ResolveWebAsset(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || string.IsNullOrWhiteSpace(webRootPath))
            return null;

        return PdfDesignSystem.ResolveWebAsset(webRootPath, configuredPath);
    }

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private string FormatDate(DateTime value)
        => PdfDesignSystem.FormatPdfDate(value, isEnglish);

    private string FormatPeriod(DateTime? from, DateTime? to)
        => $"{(from.HasValue ? FormatDate(from.Value) : "ابتدای حساب")} - {(to.HasValue ? FormatDate(to.Value) : "امروز")}";

    private string FormatMoney(decimal? value)
        => value.HasValue
            ? PdfDesignSystem.FormatPdfNumber(value.Value, isEnglish)
            : "—";

    private string FormatNumber(decimal? value, int decimals)
        => value.HasValue
            ? PdfDesignSystem.FormatPdfNumber(value.Value, isEnglish, decimals)
            : "—";
}
