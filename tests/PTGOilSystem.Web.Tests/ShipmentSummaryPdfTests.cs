using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Exports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ShipmentSummaryPdfTests
{
    [Fact]
    public void Summary_Pdf_Model_Uses_The_Existing_Shipment_Details_Values()
    {
        var model = ShipmentPnlController.BuildShipmentSummaryPdfModel(
            BuildDetails(),
            isEnglish: false,
            generatedAt: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        Assert.Equal("MV SADIQI", model.VesselName);
        Assert.Equal("SHP-2026-041", model.ShipmentCode);

        // یک سطر «منبع بار» به‌علاوهٔ هفت قلم خلاصه — دقیقاً همان چیزی که اکسل می‌دهد.
        Assert.Equal(8, model.Rows.Count);

        Assert.Equal("منبع بار: PUR-41", model.Rows[0].Label);
        Assert.Equal("100.000", model.Rows[0].QuantityText);
        Assert.Equal("55,000.00", model.Rows[0].AmountText);
        Assert.Equal("تأمین‌کننده نمونه", model.Rows[0].DetailText);

        AssertRow(model.Rows[1], "کل بار", "100.000", "-", "MV SADIQI");
        AssertRow(model.Rows[2], "تخلیه‌شده", "90.000", "-", "-");
        AssertRow(model.Rows[3], "ضایعات ثبت‌شده", "0.000", "-", "-");
        AssertRow(model.Rows[4], "هزینه خرید", "-", "55,000.00", "-");
        AssertRow(model.Rows[5], "مصارف عملیاتی", "-", "5,000.00", "-");
        AssertRow(model.Rows[6], "درآمد فروش", "60.000", "62,000.00", "-");
        AssertRow(model.Rows[7], "نتیجه خالص محموله", "-", "2,000.00", "-");

        // فقط سطر آخر جمع‌بندی است و رنگ می‌گیرد؛ بقیه خنثی می‌مانند.
        Assert.True(model.Rows[7].IsTotal);
        Assert.Equal(ShipmentSummaryPdfTone.Positive, model.Rows[7].Tone);
        Assert.All(model.Rows.Take(7), row =>
        {
            Assert.False(row.IsTotal);
            Assert.Equal(ShipmentSummaryPdfTone.Neutral, row.Tone);
        });

        static void AssertRow(ShipmentSummaryPdfRow row, string label, string quantity, string amount, string detail)
        {
            Assert.Equal(label, row.Label);
            Assert.Equal(quantity, row.QuantityText);
            Assert.Equal(amount, row.AmountText);
            Assert.Equal(detail, row.DetailText);
        }
    }

    /// <summary>
    /// قفلِ اصلی این تغییر: PDF تب خلاصه باید همان سطرها و همان اعداد خروجی اکسل را
    /// داشته باشد. اگر روزی یکی از دو مسیر عوض شود، این تست می‌شکند.
    /// </summary>
    [Fact]
    public void Summary_Pdf_Shows_Exactly_The_Same_Figures_As_The_Excel_Export()
    {
        var details = BuildDetails();
        var excel = ShipmentPnlController.BuildDetailsTabExportDocument(details, "summary", isEnglish: false);
        var pdf = ShipmentPnlController.BuildShipmentSummaryPdfModel(
            details,
            isEnglish: false,
            generatedAt: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        var excelRows = excel.Rows.ToList();
        Assert.Equal(excelRows.Count, pdf.Rows.Count);

        for (var index = 0; index < excelRows.Count; index++)
        {
            var cells = excelRows[index].Cells;
            var row = pdf.Rows[index];

            Assert.Equal(Text(cells[0]), row.Label);
            Assert.Equal(Text(cells[3]), row.DetailText);
            Assert.Equal(Number(cells[1], 3), row.QuantityText);
            Assert.Equal(Number(cells[2], 2), row.AmountText);
        }

        static string Text(TabularExportCell cell)
            => cell.Value as string is { Length: > 0 } value ? value : "-";

        static string Number(TabularExportCell cell, int decimals)
            => cell.Value is decimal value
                ? value.ToString("N" + decimals, System.Globalization.CultureInfo.InvariantCulture)
                : "-";
    }

    [Fact]
    public void Summary_Pdf_Uses_The_Shared_Office_Layout_Without_Decorative_Icons()
    {
        var webRoot = FindWebRoot();
        var source = File.ReadAllText(Path.Combine(
            Directory.GetParent(webRoot)!.FullName,
            "Services",
            "Exports",
            "ShipmentSummaryPdfDocument.cs"));

        Assert.Contains("PdfDesignSystem.PersianFallbackFont", source);
        Assert.Contains("PdfDesignSystem.HeaderCell", source);
        Assert.Contains("PdfDesignSystem.BodyCell", source);
        Assert.Contains("PdfDesignSystem.TableSeparator", source);
        Assert.DoesNotContain(".Svg(", source);
        Assert.DoesNotContain("ComposeShipIcon", source);
        Assert.DoesNotContain("CornerRadius", source);
        Assert.DoesNotContain("ToneSurface", source);
    }

    [Fact]
    public async Task Summary_Pdf_Is_A_Valid_Graphical_Document()
    {
        var service = CreateService();
        var pdfModel = ShipmentPnlController.BuildShipmentSummaryPdfModel(
            BuildDetails(),
            isEnglish: false,
            generatedAt: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        await using var stream = new MemoryStream();
        await service.WriteShipmentSummaryPdfAsync(pdfModel, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 1_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        var sampleDirectory = Environment.GetEnvironmentVariable("PTG_SHIPMENT_PDF_SAMPLE_DIR");
        if (!string.IsNullOrWhiteSpace(sampleDirectory))
        {
            Directory.CreateDirectory(sampleDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(sampleDirectory, "shipment-summary-sample.pdf"),
                bytes);
        }
    }

    private static ShipmentPnlDetailsViewModel BuildDetails()
        => new()
        {
            Id = 41,
            ShipmentCode = "SHP-2026-041",
            VesselName = "MV SADIQI",
            CompanyName = "گروه کمپنی‌های فواد صدیقی",
            ProductName = "دیزل",
            ContractNumber = "PUR-41",
            SupplierName = "تأمین‌کننده نمونه",
            CustomerName = "مشتری نمونه",
            OriginName = "بندر مبدأ",
            DestinationName = "ترمینال مقصد",
            DepartureDate = new DateTime(2026, 7, 1),
            ArrivalDate = new DateTime(2026, 7, 12),
            OriginalShipmentQuantityMt = 100m,
            VesselUnloadedQuantityMt = 90m,
            InventoryTransportedOutQuantityMt = 70m,
            DeliveredAtDestinationQuantityMt = 65m,
            InTransitQuantityMt = 4m,
            RemainingInSourceTankQuantityMt = 20m,
            InventoryTransportShortageQuantityMt = 1m,
            ShipmentSalesQuantityMt = 60m,
            LossQuantityMt = 0m,
            TotalSalesUsd = 62_000m,
            TotalPurchaseCostUsd = 55_000m,
            TotalOperationalExpensesUsd = 5_000m,
            CustomerReceiptsUsd = 50_000m,
            Notes = "این یادداشت فقط از پروندهٔ محموله آمده است.",
            NeedsReviewCount = 1,
            Warnings = ["یک انتقال داخلی هنوز در مسیر است."],
            ContractLines =
            [
                new ShipmentContractLineViewModel
                {
                    ContractId = 41,
                    ContractNumber = "PUR-41",
                    SupplierName = "تأمین‌کننده نمونه",
                    AllocatedQuantityMt = 100m,
                    UsedQuantityMt = 100m,
                    TransportShortageQuantityMt = 1m,
                    HasFinalPrice = true,
                    UnitPriceUsd = 550m,
                    TotalValueUsd = 55_000m
                }
            ],
            RegisteredVesselReceipts =
            [
                new ShipmentPnlRegisteredVesselReceiptItemViewModel
                {
                    Id = 1,
                    ReceiptDate = new DateTime(2026, 7, 12),
                    ContractNumber = "PUR-41",
                    DestinationTerminalName = "ترمینال مقصد",
                    DestinationTankName = "مخزن 1",
                    ReceivedQuantityMt = 89m
                }
            ],
            TransportLegs =
            [
                new ShipmentPnlTransportLegItemViewModel
                {
                    Id = 2,
                    LoadedDate = new DateTime(2026, 7, 15),
                    SourceIsStorageTank = true,
                    SourceName = "مخزن 1",
                    DestinationName = "انبار مقصد",
                    QuantityMt = 70m,
                    ReceivedQuantityMt = 65m,
                    ShortageQuantityMt = 1m
                }
            ],
            Sales =
            [
                new ShipmentPnlSalesItemViewModel
                {
                    Id = 8,
                    SaleDate = new DateTime(2026, 7, 20),
                    InvoiceNumber = "INV-8",
                    CustomerName = "مشتری نمونه",
                    QuantityMt = 20m,
                    TotalUsd = 20_000m,
                    IsDirectShipmentSale = true
                },
                new ShipmentPnlSalesItemViewModel
                {
                    Id = 9,
                    SaleDate = new DateTime(2026, 7, 21),
                    InvoiceNumber = "INV-9",
                    CustomerName = "مشتری نمونه",
                    QuantityMt = 40m,
                    TotalUsd = 42_000m,
                    IsDirectShipmentSale = false
                }
            ],
            CustomerReceipts =
            [
                new ShipmentPnlCustomerReceiptItemViewModel
                {
                    Id = 9,
                    PaymentDate = new DateTime(2026, 7, 25),
                    CustomerName = "مشتری نمونه",
                    AmountUsd = 50_000m,
                    IsInflow = true
                }
            ]
        };

    private static TabularExportService CreateService()
    {
        var webRoot = FindWebRoot();
        var environment = new TestWebHostEnvironment
        {
            WebRootPath = webRoot,
            ContentRootPath = Directory.GetParent(webRoot)!.FullName
        };
        var options = Options.Create(new TabularExportOptions
        {
            ExcelMaxRows = 100,
            PdfMaxRows = 100,
            CompanyNameFa = "گروه کمپنی‌های فواد صدیقی",
            CompanyNameEn = "MASHAL | Oil & Gas Management Software",
            CompanyLogoPath = "/images/mashal-logo-blue.webp",
            CompanyPhone = "+93 000 000 000",
            CompanyEmail = "hidden@example.com",
            CompanyWebsite = "hidden.example.com",
            QuestPdfLicense = "Community"
        });
        return new TabularExportService(options, environment);
    }

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

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
