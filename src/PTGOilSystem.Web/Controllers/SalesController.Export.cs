using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class SalesController
{
    [HttpGet, EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, [FromQuery] SalesIndexFilterViewModel? filter = null)
    {
        var view = (ViewResult)await Index(filter, page: 0);
        var document = TabularExportAuto.Build(view.Model!, "PTG_Sales", "فروش‌ها", "Sales",
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 13, "SaleDate"),
            new("فاکتور", "Invoice", TabularExportValueType.Text, 16, "InvoiceNumber"),
            new("مرحله", "Stage", TabularExportValueType.Text, 13, "SaleStage"),
            new("مشتری/شرکت", "Customer/company", TabularExportValueType.Text, 20, "CustomerName", "CompanyName"),
            new("قرارداد", "Contract", TabularExportValueType.Text, 16, "ContractNumber"),
            new("جنس", "Product", TabularExportValueType.Text, 18, "ProductName"),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 14, "QuantityMt"),
            new("نرخ واحد", "Unit price", TabularExportValueType.Number, 14, "ReportUnitPriceInCurrency"),
            new("مجموع", "Total", TabularExportValueType.Number, 15, "ReportTotalInCurrency"),
            new("ارز", "Currency", TabularExportValueType.Text, 10, "Currency"),
            new("مجموع USD", "Total USD", TabularExportValueType.Number, 15, "ReportTotalUsd"),
            new("وضعیت قیمت", "Price status", TabularExportValueType.Text, 16, "PriceStatusName"),
            new("مقصد", "Destination", TabularExportValueType.Text, 18, "DestinationName"),
            new("نمبر وسیله", "Vehicle no.", TabularExportValueType.Text, 16, "VehicleNumber")
        ], TabularExportSupport.FiltersFromQuery(Request), forceLandscape: true);
        return TabularExportSupport.File(this, format, document);
    }
}
