using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Services.Exports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ContractJourneySummaryPdfTests
{
    [Fact]
    public void Summary_Pdf_Model_Mirrors_The_Summary_Tab_Numbers()
    {
        var model = ContractJourneyController.BuildSummaryPdfModel(
            BuildDetails(),
            isEnglish: false,
            generatedAt: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        Assert.Equal("گشت قرارداد PUR-17", model.JourneyName);
        Assert.Equal(7, model.Stages.Count);
        Assert.Equal(4, model.HeadlineMetrics.Count);

        // مرحلهٔ ۱ همان مقدار و ارزش قرارداد صفحه را نشان می‌دهد (۱۰۰ تن × ۵۰۰ دالر).
        // عدد و واحد جدا هستند تا ستون «مقدار» در PDF هم‌تراز بماند.
        var contractStage = model.Stages[0];
        Assert.Equal(2, contractStage.Metrics.Count);
        Assert.Equal(("مقدار کل قرارداد", "100.000", "تن"),
            (contractStage.Metrics[0].Label, contractStage.Metrics[0].Value, contractStage.Metrics[0].Unit));
        Assert.Equal(("ارزش قرارداد", "50,000.00", "USD"),
            (contractStage.Metrics[1].Label, contractStage.Metrics[1].Value, contractStage.Metrics[1].Unit));

        var quantityFlow = model.Sections.Single(section => section.Title == "جریان مقدار");
        Assert.Contains(quantityFlow.Lines, line => line.Label == "بارگیری‌شده" && line.Value == "10.000" && line.Unit == "تن");
        Assert.Contains(quantityFlow.Lines, line => line.Label == "باقی‌مانده برای بارگیری" && line.Value == "90.000" && line.Unit == "تن");

        var finance = model.Sections.Single(section => section.Title == "خلاصه مالی");
        Assert.Contains(finance.Lines, line => line.Label == "فروش" && line.Value == "2,500.00" && line.Unit == "USD");
    }

    [Fact]
    public async Task Summary_Pdf_Is_Generated_For_The_Summary_Tab()
    {
        var service = CreateService();
        var pdfModel = ContractJourneyController.BuildSummaryPdfModel(
            BuildDetails(),
            isEnglish: false,
            generatedAt: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        await using var stream = new MemoryStream();
        await service.WriteContractJourneySummaryPdfAsync(pdfModel, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 1_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    private static ContractJourneyDetailsViewModel BuildDetails()
        => new()
        {
            ContractId = 17,
            ContractNumber = "PUR-17",
            ContractTypeName = "خرید",
            CompanyName = "PTG",
            ProductName = "دیزل",
            SupplierName = "تأمین‌کننده نمونه",
            IsPurchaseContract = true,
            ContractQuantityMt = 100m,
            PricingFinalUnitPriceUsd = 500m,
            PriceDisplay = "500.00 USD/MT",
            PricingMethodName = "ثابت",
            PricingStatusName = "نهایی",
            StatusName = "فعال",
            ContractDate = new DateTime(2026, 7, 1),
            LoadingItems =
            [
                new ContractJourneyLoadingItemViewModel
                {
                    Id = 1,
                    LoadingDate = new DateTime(2026, 8, 1),
                    LoadedQuantityMt = 10m,
                    LoadingPriceUsd = 500m
                }
            ],
            ReceiptItems =
            [
                new ContractJourneyReceiptItemViewModel
                {
                    Id = 1,
                    LoadingRegisterId = 1,
                    ReceiptDate = new DateTime(2026, 8, 2),
                    ReceivedQuantityMt = 9m
                }
            ],
            SalesItems =
            [
                new ContractJourneySaleItemViewModel
                {
                    SalesTransactionId = 1,
                    SaleDate = new DateTime(2026, 8, 4),
                    QuantityMt = 5m,
                    AmountUsd = 2_500m
                }
            ],
            ExpenseItems =
            [
                new ContractJourneyExpenseItemViewModel
                {
                    ExpenseTransactionId = 1,
                    ExpenseDate = new DateTime(2026, 8, 4),
                    AmountUsd = 100m
                }
            ],
            LossItems =
            [
                new ContractJourneyLossItemViewModel
                {
                    LossEventId = 1,
                    EventDate = new DateTime(2026, 8, 4),
                    DifferenceQuantityMt = 1m
                }
            ],
            PaymentItems =
            [
                new ContractJourneyPaymentItemViewModel
                {
                    PaymentTransactionId = 1,
                    PaymentDate = new DateTime(2026, 8, 5),
                    AmountUsd = 500m
                }
            ],
            Warnings = ["یک بارگیری هنوز رسید ندارد."],
            NextRecommendedActionTitle = "ثبت رسید بارگیری",
            NextRecommendedActionDescription = "برای یک بارگیری رسید ثبت نشده است."
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
            CompanyLogoPath = "/invoice-templates/fawad/logo.png",
            CompanyPhone = "+92 21 711 722 399",
            CompanyEmail = "",
            CompanyWebsite = "",
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
