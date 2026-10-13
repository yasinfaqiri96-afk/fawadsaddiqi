using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class LoadingController
{
    [HttpGet, EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, string? q = null, int[]? contractId = null, DateTime? fromDate = null, DateTime? toDate = null, int[]? productId = null, bool withoutReceipt = false, int[]? transportType = null, string? receiptStatus = null, string? priceStatus = null)
    {
        var view = (ViewResult)await Index(
            q: q,
            contractId: contractId,
            productId: productId,
            withoutReceipt: withoutReceipt,
            transportType: transportType,
            receiptStatus: receiptStatus,
            priceStatus: priceStatus,
            fromDate: fromDate,
            toDate: toDate,
            page: 0);
        var document = TabularExportAuto.Build(view.Model!, "PTG_Loadings", "بارگیری‌ها", "Loadings",
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 13, "LoadingDate"),
            new("قرارداد", "Contract", TabularExportValueType.Text, 16, "ContractNumber"),
            new("جنس", "Product", TabularExportValueType.Text, 18, "ProductName"),
            new("نوع حمل", "Transport", TabularExportValueType.Text, 15, "TransportTypeLabel"),
            new("وسیله", "Vehicle", TabularExportValueType.Text, 18, "VehicleSummary"),
            new("نام راننده", "Driver name", TabularExportValueType.Text, 18, "DriverName"),
            new("شماره تماس راننده", "Driver phone", TabularExportValueType.Text, 17, "DriverPhone"),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 14, "LoadedQuantityMt"),
            new("رسید MT", "Received MT", TabularExportValueType.Number, 14, "TotalReceivedQuantityMt"),
            new("ارزش USD", "Value USD", TabularExportValueType.Number, 15, "LoadingValueUsd"),
            new("مقصد", "Destination", TabularExportValueType.Text, 18, "DestinationName"),
            new("بارنامه", "Bill of lading", TabularExportValueType.Text, 17, "BillOfLadingNumber")
        ], TabularExportSupport.FiltersFromQuery(Request), forceLandscape: true);
        return TabularExportSupport.File(this, format, document);
    }
}
