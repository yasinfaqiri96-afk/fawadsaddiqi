using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;
using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class TabularExportServiceTests
{
    [Fact]
    public async Task Excel_Uses_Reference_View_Typed_Cells_And_Formula_Injection_Protection()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        var document = BuildDocument();
        await using var stream = new MemoryStream();

        await service.WriteAsync(document, TabularExportFormat.Excel, isEnglish: false, stream, CancellationToken.None);
        stream.Position = 0;

        using var workbook = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(workbook.WorkbookPart);
        var worksheetPart = workbookPart.WorksheetParts.Single();
        var worksheet = worksheetPart.Worksheet;
        var sheetView = worksheet.GetFirstChild<SheetViews>()!.Elements<SheetView>().Single();
        Assert.False(sheetView.RightToLeft!.Value);
        Assert.True(sheetView.ShowGridLines!.Value);
        Assert.Null(sheetView.GetFirstChild<Pane>());
        Assert.Null(worksheet.GetFirstChild<AutoFilter>());

        var cells = worksheet.Descendants<Cell>().ToList();
        Assert.Contains(cells, cell => cell.DataType?.Value == CellValues.Number && cell.CellValue?.Text == "1250.5");
        Assert.Contains(cells, cell => cell.InlineString?.InnerText == "'=SUM(A1:A2)");
    }

    [Fact]
    public async Task Excel_Uses_Compact_Organization_Header_Soft_System_Palette_And_Print_Setup()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        await using var stream = new MemoryStream();

        await service.WriteAsync(BuildDocument(), TabularExportFormat.Excel, isEnglish: false, stream, CancellationToken.None);
        stream.Position = 0;

        using var workbook = SpreadsheetDocument.Open(stream, false);
        var workbookPart = workbook.WorkbookPart!;
        var worksheet = workbookPart.WorksheetParts.Single().Worksheet;
        var worksheetPart = workbookPart.WorksheetParts.Single();
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToList();

        Assert.Equal(new[] { 40D, 40D, 25D }, rows.Take(3).Select(row => row.Height!.Value));
        Assert.Equal(ExcelDesignSystem.OrganizationName, rows[0].Elements<Cell>().First().InlineString!.InnerText);
        Assert.Equal("گزارش آزمایشی", rows[1].Elements<Cell>().First().InlineString!.InnerText);
        Assert.Equal("#", rows[2].Elements<Cell>().First().InlineString!.InnerText);
        Assert.Equal("تاریخ", rows[2].Elements<Cell>().Skip(1).First().InlineString!.InnerText);
        Assert.Equal("1", rows[3].Elements<Cell>().First().CellValue!.Text);
        Assert.DoesNotContain(
            rows.SelectMany(row => row.Elements<Cell>()),
            cell => cell.InlineString?.InnerText == "+92 21 711 722 399");

        var mergeReferences = worksheet.GetFirstChild<MergeCells>()!
            .Elements<MergeCell>()
            .Select(merge => merge.Reference!.Value)
            .ToArray();
        Assert.Equal(new[] { "A1:D1", "A2:C2" }, mergeReferences);
        Assert.Single(worksheetPart.DrawingsPart!.ImageParts);
        Assert.NotNull(worksheet.GetFirstChild<Drawing>());

        Assert.Equal(ExcelDesignSystem.Navy, FillColor(workbookPart, rows[1].Elements<Cell>().First()));
        Assert.Equal(ExcelDesignSystem.Blue, FillColor(workbookPart, rows[2].Elements<Cell>().First()));
        Assert.Equal(ExcelDesignSystem.PrimaryText, FontColor(workbookPart, rows[2].Elements<Cell>().First()));
        Assert.Equal(ExcelDesignSystem.Blue, FillColor(workbookPart, rows[3].Elements<Cell>().First()));
        Assert.Equal(ExcelDesignSystem.AmountBackground, FillColor(workbookPart, rows[3].Elements<Cell>().Last()));

        var totalRow = rows.Last();
        Assert.Equal("Total", totalRow.Elements<Cell>().First().InlineString!.InnerText);
        Assert.Equal(ExcelDesignSystem.TotalBlue, FillColor(workbookPart, totalRow.Elements<Cell>().Last()));

        var pageSetup = worksheet.GetFirstChild<PageSetup>()!;
        Assert.Equal(1U, pageSetup.FitToWidth!.Value);
        Assert.Equal(0U, pageSetup.FitToHeight!.Value);
        Assert.Equal(OrientationValues.Portrait, pageSetup.Orientation!.Value);
    }

    [Fact]
    public async Task Pdf_Is_Searchable_Pdf_And_Supports_Persian_Rtl_Content()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        await using var stream = new MemoryStream();

        await service.WriteAsync(BuildDocument(), TabularExportFormat.Pdf, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 1_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task English_Excel_Is_Left_To_Right_And_Preserves_An_Empty_Table()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        var document = BuildDocument();
        document = new TabularExportDocument
        {
            FileNameStem = document.FileNameStem,
            TitleFa = document.TitleFa,
            TitleEn = document.TitleEn,
            Columns = document.Columns,
            Rows = [],
            KnownRowCount = 0
        };
        await using var stream = new MemoryStream();

        await service.WriteAsync(document, TabularExportFormat.Excel, isEnglish: true, stream, CancellationToken.None);
        stream.Position = 0;
        using var workbook = SpreadsheetDocument.Open(stream, false);
        var worksheet = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet;

        Assert.False(worksheet.GetFirstChild<SheetViews>()!.Elements<SheetView>().Single().RightToLeft!.Value);
        Assert.Null(worksheet.GetFirstChild<AutoFilter>());
    }

    [Fact]
    public async Task English_Pdf_And_Long_Multipage_Persian_Pdf_Are_Generated()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        await using var english = new MemoryStream();
        await service.WriteAsync(BuildDocument(), TabularExportFormat.Pdf, isEnglish: true, english, CancellationToken.None);

        var longDocument = BuildLongDocument();
        await using var persian = new MemoryStream();
        await service.WriteAsync(longDocument, TabularExportFormat.Pdf, isEnglish: false, persian, CancellationToken.None);

        Assert.True(english.Length > 1_000);
        Assert.True(persian.Length > english.Length);
    }

    [Fact]
    public async Task Wide_Pdf_With_Many_Fields_Is_Generated_Without_Layout_Exception()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        await using var stream = new MemoryStream();

        await service.WriteAsync(
            BuildWideDocument(),
            TabularExportFormat.Pdf,
            isEnglish: false,
            stream,
            CancellationToken.None);

        Assert.True(stream.Length > 1_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(stream.ToArray(), 0, 4));
    }

    [Fact]
    public async Task Export_Observes_Cancellation()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        await using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.WriteAsync(BuildDocument(), TabularExportFormat.Excel, isEnglish: false, stream, cancellation.Token));
    }

    [Fact]
    public async Task Export_Rejects_Rows_Above_The_Configured_Limit()
    {
        var service = CreateService(excelMaxRows: 1, pdfMaxRows: 1);
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<TabularExportLimitException>(() =>
            service.WriteAsync(BuildDocument(), TabularExportFormat.Excel, isEnglish: true, stream, CancellationToken.None));

        Assert.Equal(2, exception.ActualRows);
        Assert.Equal(1, exception.MaximumRows);
    }

    [Fact]
    public async Task Reference_Samples_Can_Be_Written_For_Visual_Inspection()
    {
        var sampleDirectory = Environment.GetEnvironmentVariable("PTG_EXPORT_SAMPLE_DIR");
        if (string.IsNullOrWhiteSpace(sampleDirectory))
            return;

        Directory.CreateDirectory(sampleDirectory);
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        foreach (var language in new[] { (Code: "fa", IsEnglish: false), (Code: "en", IsEnglish: true) })
        {
            foreach (var format in new[] { TabularExportFormat.Excel, TabularExportFormat.Pdf })
            {
                var extension = format == TabularExportFormat.Excel ? "xlsx" : "pdf";
                await using var output = File.Create(Path.Combine(sampleDirectory, $"PTG_Export_Sample_{language.Code}.{extension}"));
                await service.WriteAsync(BuildDocument(), format, language.IsEnglish, output, CancellationToken.None);
            }
        }

        await using var partyStatement = File.Create(Path.Combine(sampleDirectory, "PTG_Party_Statement_Sample_fa.pdf"));
        await service.WritePartyStatementPdfAsync(
            BuildPartyStatement(),
            false,
            partyStatement,
            CancellationToken.None);

        await using var multipage = File.Create(Path.Combine(sampleDirectory, "PTG_Export_Multipage_fa.pdf"));
        await service.WriteAsync(
            BuildLongDocument(),
            TabularExportFormat.Pdf,
            false,
            multipage,
            CancellationToken.None);

        await using var wide = File.Create(Path.Combine(sampleDirectory, "PTG_Export_Wide_16_Columns_fa.pdf"));
        await service.WriteAsync(
            BuildWideDocument(),
            TabularExportFormat.Pdf,
            false,
            wide,
            CancellationToken.None);

        await using var wideExcel = File.Create(Path.Combine(sampleDirectory, "PTG_Export_Wide_16_Columns_fa.xlsx"));
        await service.WriteAsync(
            BuildWideDocument(),
            TabularExportFormat.Excel,
            false,
            wideExcel,
            CancellationToken.None);
    }

    [Fact]
    public void Views_Only_Allow_Print_Control_In_The_Official_Party_Statement()
    {
        var viewsRoot = Path.Combine(Directory.GetParent(FindWebRoot())!.FullName, "Views");
        var forbidden = new[] { "window.print(", "bi-printer", "data-print-list", "data-receipt-print", "Print / Save PDF" };

        foreach (var view in Directory.EnumerateFiles(viewsRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            if (view.EndsWith(Path.Combine("PartyStatements", "Document.cshtml"), StringComparison.OrdinalIgnoreCase))
                continue;
            var content = File.ReadAllText(view);
            // «ثبت و چاپ بل» فرم فروش یک submit سمت سرور است (PrintAfterSave)، نه چاپ لیست؛
            // فقط آیکن آن مجاز است و بقیهٔ کنترل‌های چاپ همچنان ممنوع‌اند.
            var isSaleFormPrintAfterSave = view.EndsWith(Path.Combine("Ak", "_ShipmentSaleForm.cshtml"), StringComparison.OrdinalIgnoreCase)
                && content.Contains("name=\"PrintAfterSave\"", StringComparison.Ordinal);
            foreach (var token in forbidden)
            {
                if (isSaleFormPrintAfterSave && token == "bi-printer")
                    continue;
                Assert.DoesNotContain(token, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Every_Operations_Index_Uses_Exactly_One_Shared_Export_Menu()
    {
        var viewsRoot = Path.Combine(Directory.GetParent(FindWebRoot())!.FullName, "Views");
        var operationControllers = new[]
        {
            "Loading", "InventoryTransportLegs", "ShipmentPnl", "Dispatch", "TruckSettlements",
            "Expenses", "LossEvents", "LoadingReceipts", "CustomsDeclarations", "Sales"
        };

        foreach (var controller in operationControllers)
        {
            var content = File.ReadAllText(Path.Combine(viewsRoot, controller, "Index.cshtml"));
            Assert.Equal(1, CountOccurrences(content, "_ExportMenu.cshtml"));
        }
    }

    [Fact]
    public async Task Workbook_Excel_Writes_Every_Sheet_With_Unique_Names()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        var first = BuildDocument();
        // عمداً هم‌عنوان تا یکتاسازیِ نام شیت آزموده شود.
        var second = new TabularExportDocument
        {
            FileNameStem = first.FileNameStem,
            TitleFa = first.TitleFa,
            TitleEn = first.TitleEn,
            Columns = first.Columns,
            Rows = first.Rows,
            KnownRowCount = 2
        };
        await using var stream = new MemoryStream();

        await service.WriteWorkbookAsync([first, second], TabularExportFormat.Excel, isEnglish: false, stream, CancellationToken.None);
        stream.Position = 0;

        using var workbook = SpreadsheetDocument.Open(stream, false);
        var sheets = workbook.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().ToList();
        Assert.Equal(2, sheets.Count);
        Assert.Equal(2, workbook.WorkbookPart!.WorksheetParts.Count());
        Assert.Equal(sheets.Count, sheets.Select(sheet => sheet.Name!.Value).Distinct().Count());
    }

    [Fact]
    public async Task Workbook_Pdf_Is_Searchable_With_Multiple_Sections()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        await using var stream = new MemoryStream();

        await service.WriteWorkbookAsync([BuildDocument(), BuildDocument()], TabularExportFormat.Pdf, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.Length > 1_000);
    }

    [Fact]
    public async Task Official_Party_Statement_Pdf_Uses_The_Shared_Reference_Design()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        await using var stream = new MemoryStream();

        await service.WritePartyStatementPdfAsync(
            BuildPartyStatement(),
            false,
            stream,
            CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.Length > 1_000);
    }

    [Fact]
    public void Party_Statement_View_Uses_Server_Pdf_For_All_Party_Types()
    {
        var webRoot = FindWebRoot();
        var projectRoot = Directory.GetParent(webRoot)!.FullName;
        var view = File.ReadAllText(Path.Combine(projectRoot, "Views", "PartyStatements", "_StatementBody.cshtml"));
        var script = File.ReadAllText(Path.Combine(webRoot, "js", "party-statement.js"));
        var controller = File.ReadAllText(Path.Combine(projectRoot, "Controllers", "PartyStatementsController.cs"));

        Assert.Contains("Url.Action(\"Pdf\", \"PartyStatements\"", view);
        Assert.Contains("PartyStatements/{partyType}/{id:int}/Pdf", controller);
        Assert.DoesNotContain("data-statement-print", view);
        Assert.Contains("data-statement-auto-print", view);
        Assert.Contains("window.print", script);
    }

    [Fact]
    public void Shared_Pdf_Design_Matches_The_Transaction_Report_Reference_Tokens()
    {
        Assert.Equal("Tahoma", PdfDesignSystem.PrimaryFont);
        Assert.Equal("#000000", PdfDesignSystem.Ink);
        Assert.Equal("#059669", PdfDesignSystem.Positive);
        Assert.Equal("#DC2626", PdfDesignSystem.Negative);
        Assert.Equal("#F5F5F5", PdfDesignSystem.SummaryBackground);
        Assert.Equal("#F1F5F9", PdfDesignSystem.HeaderBackground);
        Assert.Equal("#E2E8F0", PdfDesignSystem.Border);
        Assert.Equal(10.5f, PdfDesignSystem.TitleSize);
        Assert.Equal(8.25f, PdfDesignSystem.MetaSize);
        Assert.Equal(7.5f, PdfDesignSystem.TableSize);
        Assert.Equal(8f, PdfDesignSystem.NumericTableSize);
        Assert.Equal(7.5f, PdfDesignSystem.DenseTableSize);
        Assert.Equal(7.5f, PdfDesignSystem.ExtraWideTableSize);
        Assert.Equal(
            "1,250.50",
            PdfDesignSystem.FormatPdfCell(TabularExportCell.Number(1_250.5m), isEnglish: false));
        Assert.Equal(
            "-750",
            PdfDesignSystem.FormatPdfCell(TabularExportCell.Number(-750m), isEnglish: false));
        Assert.Equal(
            "1405/04/26",
            PdfDesignSystem.FormatPdfCell(
                TabularExportCell.Date(new DateTime(2026, 7, 17)),
                isEnglish: false));
        Assert.Equal(128f, PdfDesignSystem.BrandLogoWidth);
        Assert.Equal(42f, PdfDesignSystem.BrandRowHeight);
        Assert.Equal(5.75f, PdfDesignSystem.BrandContactSize);
        Assert.Equal(
            "تاریخ چاپ: 1405/5/8",
            PdfDesignSystem.FormatPrintDate(new DateTime(2026, 7, 30), isEnglish: false));
    }

    [Fact]
    public void Generic_And_Official_Pdfs_Use_The_Same_Design_Component()
    {
        var projectRoot = Directory.GetParent(FindWebRoot())!.FullName;
        var generic = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "Exports",
            "TabularExportService.cs"));
        var official = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "Exports",
            "PartyStatementPdfDocument.cs"));

        foreach (var token in new[]
                 {
                     "PdfDesignSystem.ComposeReportHeader",
                     "PdfDesignSystem.HeaderCell"
                 })
        {
            Assert.Contains(token, generic);
            Assert.Contains(token, official);
        }

        // صورت‌حساب رسمی فوتر ویژهٔ برند و «X / Y» را دارد؛ خروجی‌های عمومی فوتر مشترک.
        Assert.Contains("PdfDesignSystem.ComposeFooter", generic);
    }

    private static PartyStatementResult BuildPartyStatement()
    {
        var policy = new PartyStatementPolicyResolver().Resolve(PartyStatementPartyType.Supplier);
        return new PartyStatementResult
        {
            Party = new PartyRef(PartyStatementPartyType.Supplier, 1),
            Policy = policy,
            CompanyInfo = new PartyStatementCompanyInfo { Name = "PTG Oil System" },
            PartyInfo = new PartyStatementPartyInfo
            {
                Id = 1,
                Name = "تأمین‌کننده آزمایشی",
                Code = "SUP-001",
                Phone = "0700000000",
                Address = "هرات"
            },
            DocumentInfo = new PartyStatementDocumentInfo
            {
                StatementNumber = "ST-2026-001",
                StatementDate = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
                PeriodFrom = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                PeriodTo = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
                BaseCurrencyCode = "USD",
                GeneratedAtUtc = new DateTime(2026, 7, 30, 10, 31, 0, DateTimeKind.Utc)
            },
            Summary = new PartyStatementSummary
            {
                OpeningBalance = 250m,
                TotalReceipt = 1_000m,
                TotalOutflow = 400m,
                ClosingBalance = -350m,
                ClosingBalanceMeaning = policy.BalanceMeaning(-350m, isEnglish: false),
                ClosingBalanceMeaningEn = policy.BalanceMeaning(-350m, isEnglish: true),
                BaseCurrencyCode = "USD"
            },
            ColumnOptions = new PartyStatementColumnOptions(),
            Rows =
            [
                new PartyStatementRow
                {
                    Sequence = 0,
                    Date = new DateTime(2026, 7, 1),
                    Description = "بیلانس اول دوره",
                    RunningBalance = 250m,
                    IsOpeningBalance = true
                },
                new PartyStatementRow
                {
                    Sequence = 1,
                    Date = new DateTime(2026, 7, 10),
                    Reference = "CTR-001",
                    Description = "بارگیری قرارداد آزمایشی",
                    ReceiptBase = 1_000m,
                    RunningBalance = -750m
                },
                new PartyStatementRow
                {
                    Sequence = 2,
                    Date = new DateTime(2026, 7, 20),
                    Reference = "PAY-001",
                    Description = "پرداخت به تأمین‌کننده",
                    OutflowBase = 400m,
                    RunningBalance = -350m
                }
            ],
            Authorization = new PartyStatementAuthorization()
        };
    }

    private static string? FillColor(WorkbookPart workbookPart, Cell cell)
    {
        var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet;
        var format = stylesheet.CellFormats!.Elements<CellFormat>().ElementAt((int)cell.StyleIndex!.Value);
        var fill = stylesheet.Fills!.Elements<Fill>().ElementAt((int)format.FillId!.Value);
        return fill.PatternFill?.ForegroundColor?.Rgb?.Value;
    }

    private static string? FontColor(WorkbookPart workbookPart, Cell cell)
    {
        var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet;
        var format = stylesheet.CellFormats!.Elements<CellFormat>().ElementAt((int)cell.StyleIndex!.Value);
        var font = stylesheet.Fonts!.Elements<Font>().ElementAt((int)format.FontId!.Value);
        return font.Color?.Rgb?.Value;
    }

    private static TabularExportService CreateService(int excelMaxRows, int pdfMaxRows)
    {
        var webRoot = FindWebRoot();
        var environment = new TestWebHostEnvironment
        {
            WebRootPath = webRoot,
            ContentRootPath = Directory.GetParent(webRoot)!.FullName
        };
        var options = Options.Create(new TabularExportOptions
        {
            ExcelMaxRows = excelMaxRows,
            PdfMaxRows = pdfMaxRows,
            CompanyLogoPath = "/invoice-templates/fawad/logo.png",
            CompanyPhone = "+92 21 711 722 399",
            CompanyEmail = "",
            CompanyWebsite = "",
            QuestPdfLicense = "Community"
        });
        return new TabularExportService(options, environment);
    }

    private static TabularExportDocument BuildDocument()
        => new()
        {
            FileNameStem = "PTG_Export_Test",
            TitleFa = "گزارش آزمایشی",
            TitleEn = "Export Test",
            KnownRowCount = 2,
            Filters = [new("بازه", "Range", "2026-07-01 تا 2026-07-17")],
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 14),
                new("شرح", "Description", TabularExportValueType.Text, 28, true),
                new("مبلغ", "Amount", TabularExportValueType.Number, 16)
            ],
            Rows =
            [
                new([TabularExportCell.Date(new DateTime(2026, 7, 17)), TabularExportCell.Text("دریافت نقدی"), TabularExportCell.Number(1250.5m)]),
                new([TabularExportCell.Date(new DateTime(2026, 7, 18)), TabularExportCell.Text("=SUM(A1:A2)"), TabularExportCell.Number(75m)])
            ],
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"),
                TabularExportCell.Text(null),
                TabularExportCell.Number(1_325.5m)
            ])
        };

    private static TabularExportDocument BuildLongDocument()
    {
        var source = BuildDocument();
        return new TabularExportDocument
        {
            FileNameStem = "PTG_Long_Persian",
            TitleFa = source.TitleFa,
            TitleEn = source.TitleEn,
            Columns = source.Columns,
            KnownRowCount = 180,
            Rows = Enumerable.Range(1, 180).Select(index => new TabularExportRow(
            [
                TabularExportCell.Date(new DateTime(2026, 7, 1).AddDays(index % 17)),
                TabularExportCell.Text($"شرح فارسی طولانی ردیف {index} برای بررسی اتصال حروف و شکست درست متن"),
                TabularExportCell.Number(index * 12.75m)
            ]))
        };
    }

    private static TabularExportDocument BuildWideDocument()
        => new()
        {
            FileNameStem = "PTG_Wide_Export",
            TitleFa = "گزارش فهرست پُرستون",
            TitleEn = "Wide List Export",
            KnownRowCount = 8,
            Filters = [new("دوره", "Period", "2026-07-01 تا 2026-07-30")],
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 12),
                new("شماره سند", "Document No.", TabularExportValueType.Text, 14),
                new("شرکت", "Company", TabularExportValueType.Text, 18),
                new("طرف حساب", "Counterparty", TabularExportValueType.Text, 18),
                new("قرارداد", "Contract", TabularExportValueType.Text, 14),
                new("موتر", "Truck", TabularExportValueType.Text, 12),
                new("پلیت", "Plate", TabularExportValueType.Text, 12),
                new("مبدأ", "Origin", TabularExportValueType.Text, 14),
                new("مقصد", "Destination", TabularExportValueType.Text, 14),
                new("مقدار", "Quantity", TabularExportValueType.Number, 13),
                new("نرخ واحد", "Unit Price", TabularExportValueType.Number, 13),
                new("ارز", "Currency", TabularExportValueType.Text, 10),
                new("مبلغ", "Amount", TabularExportValueType.Number, 14),
                new("وضعیت", "Status", TabularExportValueType.Text, 13),
                new("کاربر", "User", TabularExportValueType.Text, 14),
                new("توضیحات", "Description", TabularExportValueType.Text, 30, true)
            ],
            Rows = Enumerable.Range(1, 8).Select(index => new TabularExportRow(
            [
                TabularExportCell.Date(new DateTime(2026, 7, index)),
                TabularExportCell.Text($"DOC-2026-{index:0000}"),
                TabularExportCell.Text("شرکت صدیقی"),
                TabularExportCell.Text($"طرف حساب آزمایشی {index}"),
                TabularExportCell.Text($"CTR-{index:000}"),
                TabularExportCell.Text($"TRK-{index:000}"),
                TabularExportCell.Text($"HRT-{index:000}"),
                TabularExportCell.Text("هرات"),
                TabularExportCell.Text("کابل"),
                TabularExportCell.Number(1_250.125m + index),
                TabularExportCell.Number(725.50m + index),
                TabularExportCell.Text("USD"),
                TabularExportCell.Number(906_875.75m + index),
                TabularExportCell.Text(index % 2 == 0 ? "تکمیل" : "در جریان"),
                TabularExportCell.Text($"user-{index:00}"),
                TabularExportCell.Text("توضیحات کامل این ردیف برای بررسی شکست متن، مرزبندی و خوانایی ستون‌های متعدد")
            ]))
        };

    private static string FindWebRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "PTGOilSystem.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += token.Length;
        }
        return count;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
