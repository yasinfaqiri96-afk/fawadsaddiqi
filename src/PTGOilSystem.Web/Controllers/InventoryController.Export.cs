using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Inventory;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class InventoryController
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> StockCardExport(
        string? format,
        [Bind(Prefix = "Filter")] InventoryStockCardFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new InventoryStockCardFilterViewModel();
        if (!filter.FromDate.HasValue || !filter.ToDate.HasValue)
            return BadRequest("برای خروجی کارت انبار، تاریخ شروع و پایان را انتخاب کنید.");
        if (filter.FromDate > filter.ToDate)
            return BadRequest("بازه تاریخ معتبر نیست.");

        var stockRows = await _stock.GetStockCardAsync(
            productId: filter.ProductId,
            contractId: filter.ContractId,
            terminalId: filter.TerminalId,
            fromUtc: filter.FromDate,
            toUtc: filter.ToDate?.Date.AddDays(1).AddTicks(-1),
            ct: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var tankIds = stockRows.Where(r => r.StorageTankId.HasValue).Select(r => r.StorageTankId!.Value).Distinct().ToList();
        var tankNames = await StorageTankDisplay.LoadNamesAsync(
            _db.StorageTanks.AsNoTracking().Where(t => tankIds.Contains(t.Id)));

        var rows = stockRows.Select(r => new
        {
            r.MovementDate, r.Direction, r.ProductCode, r.ProductName, r.TerminalCode, r.TerminalName,
            r.ContractNumber, Tank = StorageTankDisplay.Resolve(tankNames, r.StorageTankId, r.StorageTankCode),
            r.QuantityMt, r.SignedQuantityMt, r.RunningBalanceMt, r.ReferenceDocument, r.Notes
        }).ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Stock_Card", TitleFa = "کارت انبار", TitleEn = "Stock Card",
            KnownRowCount = rows.Count, ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")), ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("جنس / Product", filter.ProductId), ("قرارداد / Contract", filter.ContractId), ("ترمینال / Terminal", filter.TerminalId)),
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.DateTime, 16), new("نوع حرکت", "Movement", Width: 12),
                new("جنس", "Product", Width: 20), new("ترمینال", "Terminal", Width: 18), new("قرارداد", "Contract", Width: 16),
                new("مخزن", "Tank", Width: 15), new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 14),
                new("اثر موجودی MT", "Stock effect MT", TabularExportValueType.Number, 15),
                new("مانده MT", "Balance MT", TabularExportValueType.Number, 15), new("مرجع", "Reference", Width: 18),
                new("یادداشت", "Notes", Width: 28, Wrap: true)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.DateTime(r.MovementDate), TabularExportCell.Text(r.Direction.ToString()),
                TabularExportCell.Text($"{r.ProductCode} - {r.ProductName}"), TabularExportCell.Text($"{r.TerminalCode} - {r.TerminalName}"),
                TabularExportCell.Text(r.ContractNumber), TabularExportCell.Text(r.Tank), TabularExportCell.Number(r.QuantityMt),
                TabularExportCell.Number(r.SignedQuantityMt), TabularExportCell.Number(r.RunningBalanceMt),
                TabularExportCell.Text(r.ReferenceDocument), TabularExportCell.Text(r.Notes)
            ]))
        });
    }
}
