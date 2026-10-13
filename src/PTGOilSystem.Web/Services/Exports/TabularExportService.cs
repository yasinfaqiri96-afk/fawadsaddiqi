using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Drawing;
using QColors = QuestPDF.Helpers.Colors;

namespace PTGOilSystem.Web.Services.Exports;

public sealed class TabularExportService : ITabularExportService
{
    private static readonly object QuestPdfInitializationLock = new();
    private static bool _questPdfInitialized;

    private readonly TabularExportOptions _options;
    private readonly IWebHostEnvironment _environment;

    // «تاریخ تولید» روی خروجی باید روز کاری کابل باشد، نه ساعت محلی سیستم‌عاملِ سرور.
    private readonly PTGOilSystem.Web.Services.Time.IAfghanistanBusinessClock _businessClock;

    public TabularExportService(
        IOptions<TabularExportOptions> options,
        IWebHostEnvironment environment,
        PTGOilSystem.Web.Services.Time.IAfghanistanBusinessClock? businessClock = null)
    {
        _options = options.Value;
        _environment = environment;
        _businessClock = businessClock
            ?? new PTGOilSystem.Web.Services.Time.AfghanistanBusinessClock(TimeProvider.System);
        InitializeQuestPdf();
    }

    public int GetRowLimit(TabularExportFormat format)
        => format == TabularExportFormat.Excel ? _options.ExcelMaxRows : _options.PdfMaxRows;

    public Task WriteAsync(
        TabularExportDocument document,
        TabularExportFormat format,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteWorkbookAsync([document], format, isEnglish, destination, cancellationToken);
    }

    public Task WriteWorkbookAsync(
        IReadOnlyList<TabularExportDocument> sheets,
        TabularExportFormat format,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
        {
            throw new InvalidOperationException("Export requires at least one sheet.");
        }

        foreach (var sheet in sheets)
        {
            ValidateDocument(sheet, format);
        }

        return format == TabularExportFormat.Excel
            ? WriteExcelWorkbookAsync(sheets, isEnglish, destination, cancellationToken)
            : WritePdfWorkbookAsync(sheets, isEnglish, destination, cancellationToken);
    }

    public Task WritePartyStatementPdfAsync(
        Models.PartyStatements.PartyStatementResult statement,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (statement.Rows.Count > _options.PdfMaxRows)
            throw new TabularExportLimitException(statement.Rows.Count, _options.PdfMaxRows);

        var document = new PartyStatementPdfDocument(
            statement,
            _environment.WebRootPath,
            BuildBrandHeader(isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa),
            isEnglish);
        document.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    public Task WriteSupplierContractStatementPdfAsync(
        Models.PartyStatements.PartyStatementResult statement,
        Models.PartyStatements.SupplierContractStatementViewModel contractGrouping,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(contractGrouping);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        // سقف بر اساس سطرهای همین سند است؛ هر سطر یک قرارداد، نه یک تراکنش.
        if (contractGrouping.Rows.Count > _options.PdfMaxRows)
            throw new TabularExportLimitException(contractGrouping.Rows.Count, _options.PdfMaxRows);

        var document = new PartyStatementPdfDocument(
            statement,
            _environment.WebRootPath,
            BuildBrandHeader(isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa),
            isEnglish,
            contractGrouping);
        document.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    public Task WriteContractJourneySummaryPdfAsync(
        Models.ContractJourney.ContractJourneySummaryPdfModel model,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new ContractJourneySummaryPdfDocument(
            model,
            BuildBrandHeader(isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa),
            isEnglish);
        document.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    public Task WriteShipmentSummaryPdfAsync(
        Models.ShipmentPnl.ShipmentSummaryPdfModel model,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        // این سند اجرایی فقط نام گروه را در برند نگه می‌دارد و اطلاعات تماسِ اضافی ندارد.
        var brand = BuildBrandHeader(isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa) with
        {
            Phone = null,
            Email = null,
            Website = null
        };
        var document = new ShipmentSummaryPdfDocument(model, brand, isEnglish);
        document.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    public Task WriteCompanyBalancePdfAsync(
        Models.Reports.CompanyBalanceReportViewModel model,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new CompanyBalancePdfDocument(
            model,
            PdfDesignSystem.ResolveWebAsset(_environment.WebRootPath, CompanyBalancePdfDocument.LogoRelativePath)
                ?? PdfDesignSystem.ResolveWebAsset(_environment.WebRootPath, _options.CompanyLogoPath));
        document.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    private void InitializeQuestPdf()
    {
        lock (QuestPdfInitializationLock)
        {
            if (_questPdfInitialized)
            {
                return;
            }

            QuestPDF.Settings.License = _options.QuestPdfLicense.Trim().ToLowerInvariant() switch
            {
                "professional" => LicenseType.Professional,
                "enterprise" => LicenseType.Enterprise,
                _ => LicenseType.Community
            };

            RegisterFont("vazirmatn-400.ttf");
            RegisterFont("vazirmatn-700.ttf");
            RegisterFont("poppins-400.ttf", "poppins");
            RegisterFont("poppins-700.ttf", "poppins");
            PdfDesignSystem.RegisterReferenceFonts(_environment.WebRootPath);
            _questPdfInitialized = true;
        }
    }

    private void RegisterFont(string fileName, string familyFolder = "vazirmatn")
    {
        var path = Path.Combine(
            _environment.WebRootPath,
            "vendor",
            "fonts",
            familyFolder,
            "files",
            fileName);

        if (!System.IO.File.Exists(path))
        {
            return;
        }

        using var stream = System.IO.File.OpenRead(path);
        FontManager.RegisterFont(stream);
    }

    private void ValidateDocument(TabularExportDocument document, TabularExportFormat format)
    {
        if (document.Columns.Count == 0)
        {
            throw new InvalidOperationException("Export requires at least one column.");
        }

        if (document.KnownRowCount is { } rowCount && rowCount > GetRowLimit(format))
        {
            throw new TabularExportLimitException(rowCount, GetRowLimit(format));
        }

        if (document.Totals is not null && document.Totals.Cells.Count != document.Columns.Count)
        {
            throw new InvalidOperationException("The totals row must match the export column count.");
        }
    }

    private async Task WriteExcelWorkbookAsync(
        IReadOnlyList<TabularExportDocument> documents,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var spreadsheet = SpreadsheetDocument.Create(destination, SpreadsheetDocumentType.Workbook, true);
        var workbookPart = spreadsheet.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = ExcelDesignSystem.BuildStylesheet();
        stylesPart.Stylesheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint sheetId = 1;
        foreach (var document in documents)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            await WriteWorksheetAsync(worksheetPart, document, isEnglish, cancellationToken).ConfigureAwait(false);
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = UniqueSheetName(SanitizeSheetName(isEnglish ? document.TitleEn : document.TitleFa), usedNames)
            });
        }

        workbookPart.Workbook.Save();
    }

    private async Task WriteWorksheetAsync(
        WorksheetPart worksheetPart,
        TabularExportDocument document,
        bool isEnglish,
        CancellationToken cancellationToken)
    {
        var totalColumnCount = document.Columns.Count + 1;
        var lastColumn = GetColumnName(totalColumnCount);
        var titleLastColumn = GetColumnName(totalColumnCount - 1);
        const uint headerRowIndex = 3;
        var rowIndex = headerRowIndex + 1;
        var rowCount = 0;
        var rowLimit = _options.ExcelMaxRows;

        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteElement(new SheetProperties(
            new PageSetupProperties { FitToPage = true, AutoPageBreaks = false }));
        writer.WriteElement(new SheetViews(
            new SheetView
            {
                WorkbookViewId = 0,
                RightToLeft = false,
                ShowGridLines = true,
                ZoomScale = 100U,
                ZoomScaleNormal = 100U
            }));
        writer.WriteElement(new SheetFormatProperties { DefaultRowHeight = ExcelDesignSystem.BodyRowHeight });

        writer.WriteStartElement(new Columns());
        writer.WriteElement(new Column
        {
            Min = 1U,
            Max = 1U,
            Width = ExcelDesignSystem.SequenceColumnWidth,
            CustomWidth = true
        });
        for (var index = 0; index < document.Columns.Count; index++)
        {
            var width = ExcelDesignSystem.NormalizeColumnWidth(document.Columns[index].Width);
            writer.WriteElement(new Column
            {
                Min = (uint)index + 2,
                Max = (uint)index + 2,
                Width = width,
                CustomWidth = true
            });
        }
        writer.WriteEndElement();

        writer.WriteStartElement(new SheetData());
        WriteMergedTextRow(
            writer,
            1,
            totalColumnCount,
            isEnglish ? _options.OrganizationNameEn : _options.OrganizationName,
            ExcelDesignSystem.OrganizationStyle,
            ExcelDesignSystem.OrganizationRowHeight);
        WriteReportTitleRow(
            writer,
            2,
            totalColumnCount,
            isEnglish ? document.TitleEn : document.TitleFa,
            Helpers.DateDisplay.Format(_businessClock.Now.DateTime, "yyyy/M/d"));

        writer.WriteStartElement(new Row { RowIndex = headerRowIndex, Height = ExcelDesignSystem.HeaderRowHeight, CustomHeight = true });
        WriteInlineTextCell(writer, "#", ExcelDesignSystem.HeaderStyle);
        foreach (var column in document.Columns)
        {
            WriteInlineTextCell(writer, isEnglish ? column.TitleEn : column.TitleFa, ExcelDesignSystem.HeaderStyle);
        }
        writer.WriteEndElement();

        // سطرها همان‌طور که از منبع می‌رسند نوشته می‌شوند؛ برای منبعِ صفحه‌به‌صفحه فقط
        // یک chunk در حافظه است، نه کل نتیجه.
        await foreach (var row in document.EnumerateRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowCount++;
            if (rowCount > rowLimit)
            {
                throw new TabularExportLimitException(rowCount, rowLimit);
            }
            if (row.Cells.Count != document.Columns.Count)
            {
                throw new InvalidOperationException($"Export row {rowCount:N0} does not match the column count.");
            }

            writer.WriteStartElement(new Row
            {
                RowIndex = rowIndex++,
                Height = GetBodyRowHeight(document.Columns, row, isEnglish),
                CustomHeight = true
            });
            WriteNumberCell(writer, rowCount.ToString(CultureInfo.InvariantCulture), ExcelDesignSystem.SequenceStyle);
            for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                WriteExcelCell(writer, document.Columns[columnIndex], row.Cells[columnIndex], isEnglish, isTotal: false);
            }
            writer.WriteEndElement();
        }

        if (document.Totals is not null)
        {
            writer.WriteStartElement(new Row
            {
                RowIndex = rowIndex++,
                Height = ExcelDesignSystem.TotalRowHeight,
                CustomHeight = true
            });
            WriteInlineTextCell(writer, "Total", ExcelDesignSystem.TotalTextStyle);
            for (var columnIndex = 0; columnIndex < document.Totals.Cells.Count; columnIndex++)
            {
                var cell = document.Totals.Cells[columnIndex];
                if (IsTotalLabel(cell))
                {
                    cell = new TabularExportCell(cell.ValueType, null);
                }
                WriteExcelCell(writer, document.Columns[columnIndex], cell, isEnglish, isTotal: true);
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteElement(new MergeCells(
            new MergeCell { Reference = $"A1:{lastColumn}1" },
            new MergeCell { Reference = $"A2:{titleLastColumn}2" }));
        writer.WriteElement(new PageMargins
        {
            Left = 0.7D,
            Right = 0.7D,
            Top = 0.75D,
            Bottom = 0.75D,
            Header = 0.3D,
            Footer = 0.3D
        });
        writer.WriteElement(new PageSetup
        {
            PaperSize = 9U,
            Orientation = document.ForceLandscape || totalColumnCount > 7
                ? OrientationValues.Landscape
                : OrientationValues.Portrait,
            FitToWidth = 1U,
            FitToHeight = 0U,
            PageOrder = PageOrderValues.DownThenOver
        });
        writer.WriteEndElement();
    }

    private async Task WritePdfWorkbookAsync(
        IReadOnlyList<TabularExportDocument> documents,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        // PDF ذاتاً باید کل صفحه‌بندی را بشناسد، پس سطرها قبل از ساخت سند جمع می‌شوند؛
        // ولی سقف PdfMaxRows همان‌جا اعمال می‌شود تا حافظه بی‌مرز رشد نکند.
        var collected = new List<IReadOnlyList<TabularExportRow>>(documents.Count);
        foreach (var document in documents)
        {
            collected.Add(await CollectPdfRowsAsync(document, cancellationToken).ConfigureAwait(false));
        }

        var pdf = Document.Create(container =>
        {
            for (var index = 0; index < documents.Count; index++)
            {
                AddPdfPage(container, documents[index], collected[index], isEnglish);
            }
        });

        pdf.GeneratePdf(destination);
    }

    private async Task<IReadOnlyList<TabularExportRow>> CollectPdfRowsAsync(
        TabularExportDocument document,
        CancellationToken cancellationToken)
    {
        var rows = new List<TabularExportRow>(Math.Min(document.KnownRowCount ?? 256, _options.PdfMaxRows));
        await foreach (var row in document.EnumerateRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= _options.PdfMaxRows)
            {
                throw new TabularExportLimitException(rows.Count + 1, _options.PdfMaxRows);
            }
            if (row.Cells.Count != document.Columns.Count)
            {
                throw new InvalidOperationException($"Export row {rows.Count + 1:N0} does not match the column count.");
            }
            rows.Add(row);
        }

        return rows;
    }

    private void AddPdfPage(
        IDocumentContainer container,
        TabularExportDocument document,
        IReadOnlyList<TabularExportRow> rows,
        bool isEnglish)
    {
        var title = isEnglish ? document.TitleEn : document.TitleFa;
        var company = isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa;
        var filters = BuildFilterText(document, isEnglish);
        var generatedAt = _businessClock.Now.DateTime;
        var metrics = BuildPdfMetrics(document, isEnglish);
        var brand = BuildBrandHeader(company);
        var columnCount = document.Columns.Count;
        var dense = columnCount > 6;
        var extraWide = columnCount > 12;
        var landscape = document.ForceLandscape || dense;
        var tableFontSize = PdfDesignSystem.TableFontSize(columnCount);

        container.Page(page =>
        {
            page.Size(extraWide
                ? PageSizes.A3.Landscape()
                : landscape
                    ? PageSizes.Letter.Landscape()
                    : PageSizes.Letter.Portrait());
            page.MarginHorizontal(PdfDesignSystem.HorizontalMargin);
            page.MarginTop(PdfDesignSystem.TopMargin);
            page.MarginBottom(PdfDesignSystem.BottomMargin);
            page.PageColor(QColors.White);
            page.DefaultTextStyle(style => PdfDesignSystem.DefaultTextStyle(style, isEnglish));

            var headerContainer = page.Header();
            if (!isEnglish)
            {
                headerContainer = headerContainer.ContentFromRightToLeft();
            }
            headerContainer.Column(column =>
            {
                column.Item().ShowOnce().Element(full =>
                    PdfDesignSystem.ComposeReportHeader(
                        full,
                        title,
                        generatedAt,
                        filters,
                        metrics,
                        isEnglish,
                        brand));
                column.Item().SkipOnce().Element(compact =>
                    PdfDesignSystem.ComposeReportHeader(
                        compact,
                        title,
                        generatedAt,
                        filters: null,
                        metrics: [],
                        isEnglish: isEnglish,
                        brand: brand,
                        compact: true));
            });

            var contentContainer = page.Content().PaddingTop(8);
            if (!isEnglish)
            {
                contentContainer = contentContainer.ContentFromRightToLeft();
            }
            contentContainer.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    foreach (var column in document.Columns)
                    {
                        columns.RelativeColumn(PdfDesignSystem.ColumnWeight(column));
                    }
                });

                table.Header(header =>
                {
                    foreach (var column in document.Columns)
                    {
                        var target = header.Cell().Element(cell =>
                            PdfDesignSystem.HeaderCell(cell, dense));
                        target = AlignHeaderCell(target, isEnglish);
                        target.Text(isEnglish ? column.TitleEn : column.TitleFa)
                            .Bold()
                            .FontSize(tableFontSize)
                            .FontColor(PdfDesignSystem.Ink);
                    }

                    header.Cell()
                        .ColumnSpan((uint)columnCount)
                        .Element(container => PdfDesignSystem.TableSeparator(container, 1.5f));
                });

                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex];

                    for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
                    {
                        var cell = row.Cells[columnIndex];
                        var column = document.Columns[columnIndex];
                        var target = table.Cell().Element(container =>
                            PdfDesignSystem.BodyCell(container, "#FFFFFF", dense));
                        target = AlignBodyCell(target, column, cell, isEnglish);

                        target.Text(PdfDesignSystem.FormatPdfCell(cell, isEnglish))
                            .FontSize(PdfDesignSystem.CellFontSize(cell, tableFontSize))
                            .FontColor(PdfDesignSystem.ValueColor(cell));
                    }

                    table.Cell()
                        .ColumnSpan((uint)columnCount)
                        .Element(container => PdfDesignSystem.TableSeparator(container));
                }

                if (document.Totals is not null)
                {
                    for (var columnIndex = 0; columnIndex < document.Totals.Cells.Count; columnIndex++)
                    {
                        var cell = document.Totals.Cells[columnIndex];
                        var column = document.Columns[columnIndex];
                        var target = table.Cell().Element(container =>
                            PdfDesignSystem.TotalCell(container, dense));
                        target = AlignBodyCell(target, column, cell, isEnglish);

                        target.Text(PdfDesignSystem.FormatPdfCell(cell, isEnglish))
                            .Bold()
                            .FontSize(PdfDesignSystem.CellFontSize(cell, tableFontSize))
                            .FontColor(PdfDesignSystem.ValueColor(cell));
                    }

                    table.Cell()
                        .ColumnSpan((uint)columnCount)
                        .Element(container => PdfDesignSystem.TableSeparator(container));
                }
            });

            page.Footer().PaddingTop(8).Element(footer =>
                PdfDesignSystem.ComposeFooter(footer, company, isEnglish));
        });
    }

    private PdfBrandHeader BuildBrandHeader(string companyName)
        => new(
            companyName,
            PdfDesignSystem.ResolveWebAsset(_environment.WebRootPath, _options.CompanyLogoPath),
            _options.CompanyPhone,
            _options.CompanyEmail,
            _options.CompanyWebsite);

    private static IContainer AlignHeaderCell(
        IContainer target,
        bool isEnglish)
        => (isEnglish ? target.ContentFromLeftToRight() : target.ContentFromRightToLeft())
            .AlignCenter();

    private static IContainer AlignBodyCell(
        IContainer target,
        TabularExportColumn column,
        TabularExportCell cell,
        bool isEnglish)
        => column.ValueType switch
        {
            TabularExportValueType.Integer
                or TabularExportValueType.Number
                or TabularExportValueType.Percentage
                => target.ContentFromLeftToRight().AlignCenter(),
            TabularExportValueType.Date
                or TabularExportValueType.DateTime
                or TabularExportValueType.Boolean
                => target.ContentFromLeftToRight().AlignCenter(),
            _ when column.Wrap && (PdfDesignSystem.IsLeftToRight(cell) || isEnglish)
                => target.ContentFromLeftToRight().AlignLeft(),
            _ when column.Wrap => target.ContentFromRightToLeft().AlignRight(),
            _ when PdfDesignSystem.IsLeftToRight(cell) || isEnglish
                => target.ContentFromLeftToRight().AlignCenter(),
            _ => target.ContentFromRightToLeft().AlignCenter()
        };

    private static IReadOnlyList<PdfSummaryMetric> BuildPdfMetrics(
        TabularExportDocument document,
        bool isEnglish)
    {
        var metrics = new List<PdfSummaryMetric>(4);
        if (document.Totals is not null)
        {
            for (var index = 0; index < document.Totals.Cells.Count && metrics.Count < 3; index++)
            {
                var cell = document.Totals.Cells[index];
                if (cell.Value is null || cell.ValueType is TabularExportValueType.Text
                    or TabularExportValueType.Date
                    or TabularExportValueType.DateTime
                    or TabularExportValueType.Boolean)
                {
                    continue;
                }

                var label = isEnglish
                    ? document.Columns[index].TitleEn
                    : document.Columns[index].TitleFa;
                var color = PdfDesignSystem.ValueColor(cell) == PdfDesignSystem.Negative
                    ? PdfDesignSystem.Negative
                    : PdfDesignSystem.Positive;
                metrics.Add(new PdfSummaryMetric(
                    label,
                    PdfDesignSystem.FormatPdfCell(cell, isEnglish),
                    color));
            }
        }

        return metrics;
    }

    private static void WriteMergedTextRow(
        OpenXmlWriter writer,
        uint rowIndex,
        int columnCount,
        string value,
        uint styleIndex,
        double height)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex, Height = height, CustomHeight = true });
        WriteInlineTextCell(writer, value, styleIndex);
        for (var index = 1; index < columnCount; index++)
        {
            WriteInlineTextCell(writer, null, styleIndex);
        }
        writer.WriteEndElement();
    }

    private static void WriteReportTitleRow(
        OpenXmlWriter writer,
        uint rowIndex,
        int columnCount,
        string title,
        string exportDate)
    {
        writer.WriteStartElement(new Row
        {
            RowIndex = rowIndex,
            Height = ExcelDesignSystem.ReportTitleRowHeight,
            CustomHeight = true
        });
        WriteInlineTextCell(writer, title, ExcelDesignSystem.ReportTitleStyle);
        for (var index = 1; index < columnCount - 1; index++)
        {
            WriteInlineTextCell(writer, null, ExcelDesignSystem.ReportTitleStyle);
        }
        WriteInlineTextCell(writer, exportDate, ExcelDesignSystem.ExportDateStyle);
        writer.WriteEndElement();
    }

    private static void WriteExcelCell(
        OpenXmlWriter writer,
        TabularExportColumn column,
        TabularExportCell cell,
        bool isEnglish,
        bool isTotal)
    {
        var style = isTotal
            ? ExcelDesignSystem.ResolveTotalStyle(column, cell)
            : ExcelDesignSystem.ResolveBodyStyle(column, cell);
        if (cell.Value is null)
        {
            writer.WriteElement(new Cell { StyleIndex = style });
            return;
        }

        switch (cell.ValueType)
        {
            case TabularExportValueType.Integer:
                WriteNumberCell(writer, Convert.ToInt64(cell.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), style);
                break;
            case TabularExportValueType.Number:
                WriteNumberCell(writer, Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), style);
                break;
            case TabularExportValueType.Percentage:
                WriteNumberCell(writer, Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), style);
                break;
            // Excel تقویم شمسی ندارد؛ در حالت هجری شمسی تاریخ به‌صورت متنِ صفرپرشدهٔ «1405/07/12» نوشته
            // می‌شود تا مرتب‌سازیِ متنی همان ترتیبِ زمانی را بدهد. در حالت میلادی مثل قبل عددِ تاریخ است.
            case TabularExportValueType.Date when cell.Value is DateTime date && Helpers.DateDisplay.IsSolarHijri:
                WriteInlineTextCell(writer, Helpers.DateDisplay.PlainDate(date), style);
                break;
            case TabularExportValueType.DateTime when cell.Value is DateTime dateTime && Helpers.DateDisplay.IsSolarHijri:
                WriteInlineTextCell(writer, Helpers.DateDisplay.PlainDateTime(dateTime), style);
                break;
            case TabularExportValueType.Date when cell.Value is DateTime date:
                WriteNumberCell(writer, date.ToOADate().ToString(CultureInfo.InvariantCulture), style);
                break;
            case TabularExportValueType.DateTime when cell.Value is DateTime dateTime:
                WriteNumberCell(writer, dateTime.ToOADate().ToString(CultureInfo.InvariantCulture), style);
                break;
            case TabularExportValueType.Boolean:
                WriteInlineTextCell(writer, Convert.ToBoolean(cell.Value, CultureInfo.InvariantCulture) ? (isEnglish ? "Yes" : "بلی") : (isEnglish ? "No" : "نخیر"), style);
                break;
            default:
                WriteInlineTextCell(writer, SanitizeSpreadsheetText(Convert.ToString(cell.Value, CultureInfo.InvariantCulture)), style);
                break;
        }
    }

    private static bool IsTotalLabel(TabularExportCell cell)
    {
        if (cell.ValueType != TabularExportValueType.Text || cell.Value is null)
        {
            return false;
        }

        var value = Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        return value.Contains("Total", StringComparison.OrdinalIgnoreCase)
            || value.Contains("جمع", StringComparison.Ordinal);
    }

    private static double GetBodyRowHeight(
        IReadOnlyList<TabularExportColumn> columns,
        TabularExportRow row,
        bool isEnglish)
    {
        var lines = 1;
        for (var index = 0; index < columns.Count && index < row.Cells.Count; index++)
        {
            if (!columns[index].Wrap)
            {
                continue;
            }

            var text = row.Cells[index].ToDisplayText(isEnglish);
            if (text.Length == 0)
            {
                continue;
            }

            // هر خطِ صریح یک خط است و متنِ بلندتر از عرض ستون به چند خط شکسته می‌شود؛
            // بدون این حساب، سطر ارتفاع دو خطی می‌گرفت و متن از خانه بیرون می‌زد.
            var explicitLines = text.Split('\n').Length;
            var charsPerLine = Math.Max(1D, columns[index].Width * 1.35D);
            var wrappedLines = (int)Math.Ceiling(text.Replace("\n", string.Empty).Length / charsPerLine);
            lines = Math.Max(lines, Math.Max(explicitLines, wrappedLines));
        }

        if (lines <= 1)
        {
            return ExcelDesignSystem.BodyRowHeight;
        }

        return Math.Min(lines, ExcelDesignSystem.MaxWrappedBodyLines) * ExcelDesignSystem.WrappedBodyLineHeight;
    }

    private static void WriteNumberCell(OpenXmlWriter writer, string value, uint styleIndex)
        => writer.WriteElement(new Cell
        {
            DataType = CellValues.Number,
            CellValue = new CellValue(value),
            StyleIndex = styleIndex
        });

    private static void WriteInlineTextCell(OpenXmlWriter writer, string? value, uint styleIndex)
        => writer.WriteElement(new Cell
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve }),
            StyleIndex = styleIndex
        });

    internal static string SanitizeSpreadsheetText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var trimmed = value.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0], StringComparison.Ordinal)
            ? "'" + value
            : value;
    }

    // سندی که فیلتر فعالی ندارد، خط فیلتر هم نمی‌گیرد؛ سربرگ فقط عنوان می‌ماند.
    private static string? BuildFilterText(TabularExportDocument document, bool isEnglish)
    {
        var activeFilters = document.Filters
            .Where(filter => !string.IsNullOrWhiteSpace(filter.Value))
            .Select(filter => $"{(isEnglish ? filter.LabelEn : filter.LabelFa)}: {Helpers.DateDisplay.IsoTextToDisplay(filter.Value!.Trim())}")
            .ToArray();

        return activeFilters.Length == 0
            ? null
            : string.Join(" | ", activeFilters);
    }

    private static string GetColumnName(int columnCount)
    {
        var dividend = columnCount;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }
        return columnName;
    }

    private static string SanitizeSheetName(string value)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var sanitized = new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Export";
        }
        return sanitized.Length <= 31 ? sanitized : sanitized[..31];
    }

    private static string UniqueSheetName(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var tag = $" ({suffix})";
            var trimmed = name.Length + tag.Length <= 31 ? name : name[..(31 - tag.Length)];
            var candidate = trimmed + tag;
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        return name;
    }
}

public sealed class TabularExportLimitException(int actualRows, int maximumRows) : Exception
{
    public int ActualRows { get; } = actualRows;
    public int MaximumRows { get; } = maximumRows;
}
