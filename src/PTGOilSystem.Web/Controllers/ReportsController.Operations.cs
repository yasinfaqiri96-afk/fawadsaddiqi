using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// گزارش‌های عملیاتیِ موجودی و پیش‌فروش. هیچ فرمول مالی/موجودی مستقلی اینجا نیست؛
/// همهٔ ارقام از <see cref="IPreSaleReservationService"/> و
/// <see cref="INegativeStockAnalysisService"/> می‌آید و Export دقیقاً همان Query صفحه را
/// دوباره صدا می‌زند.
/// </summary>
public partial class ReportsController
{
    private const int DiscrepancyPageSize = 50;

    // سرویس هر فراخوانی را روی ۲۰۰ ردیف می‌بندد؛ خروجی همان اندازه را صفحه‌به‌صفحه
    // برمی‌دارد تا هیچ ردیفی خاموش جا نماند، با سقفی که فایل را غیرقابل‌استفاده نکند.
    private const int DiscrepancyExportPageSize = 200;
    private const int DiscrepancyExportMaxRows = 5000;

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> SellableStock(
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeInventory: true);
        return View(await BuildSellableStockAsync(filter, ct));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> SellableStockExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildSellableStockAsync(filter, ct);
        var rows = model.Rows;

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Sellable_Stock",
            TitleFa = "موجودی قابل فروش",
            TitleEn = "Sellable stock",
            KnownRowCount = rows.Count,
            Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("جنس", "Product", Width: 20),
                new("جواز", "Company", Width: 20),
                new("موجودی ملکی MT", "Owned MT", TabularExportValueType.Number, 18),
                new("رزرو پیش‌فروش MT", "Reserved MT", TabularExportValueType.Number, 18),
                new("قابل فروش MT", "Sellable MT", TabularExportValueType.Number, 18),
                new("رزرو بدون جواز MT", "Unallocated reserved MT", TabularExportValueType.Number, 20),
                new("وضعیت انتساب", "Attribution", Width: 16)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.ProductName),
                TabularExportCell.Text(r.CompanyName),
                TabularExportCell.Number(r.PhysicalStockMt),
                TabularExportCell.Number(r.ReservedMt),
                TabularExportCell.Number(r.SellableMt),
                TabularExportCell.Number(r.UnallocatedReservedMt),
                TabularExportCell.Text(r.Attribution.ToString())
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"),
                TabularExportCell.Text(null),
                TabularExportCell.Number(model.TotalPhysicalMt),
                TabularExportCell.Number(model.TotalReservedMt),
                TabularExportCell.Number(model.TotalSellableMt),
                TabularExportCell.Number(model.TotalUnallocatedReservedMt),
                TabularExportCell.Text(null)
            ])
        });
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> PreSaleDiscrepancies(
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        PreSaleDiscrepancyKind kind = PreSaleDiscrepancyKind.OverDelivery,
        int page = 1,
        CancellationToken ct = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeInventory: true);
        return View(await BuildPreSaleDiscrepanciesAsync(filter, kind, page, ct));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> PreSaleDiscrepanciesExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        PreSaleDiscrepancyKind kind = PreSaleDiscrepancyKind.OverDelivery,
        CancellationToken ct = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        // Export همان دستهٔ انتخاب‌شده را کامل می‌گیرد (بدون Paging صفحه) اما با همان
        // Service و همان فیلترها؛ هیچ فرمول جداگانه‌ای ساخته نمی‌شود.
        // سرویس هر بار حداکثر یک صفحه می‌دهد، پس خروجی صفحه‌به‌صفحه کامل می‌شود؛
        // در غیر این صورت هر گزارشی با بیش از یک صفحه مغایرت، خاموش ناقص صادر می‌شد.
        var rows = new List<PreSaleDiscrepancyRow>();
        for (var skip = 0; skip < DiscrepancyExportMaxRows; skip += DiscrepancyExportPageSize)
        {
            var batch = await _preSaleReservations.GetDiscrepanciesAsync(
                filter, kind, skip, DiscrepancyExportPageSize, ct);
            rows.AddRange(batch);
            if (batch.Count < DiscrepancyExportPageSize)
            {
                break;
            }
        }

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_PreSale_Discrepancies",
            TitleFa = $"مغایرت‌های پیش‌فروش — {PreSaleReservationService.TitleOf(kind)}",
            TitleEn = "Pre-sale discrepancies",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("مورد", "Item", Width: 24),
                new("شرح", "Detail", Width: 48),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 16),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new("تاریخ سند", "Document date", TabularExportValueType.Date, 14)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.Title),
                TabularExportCell.Text(r.Detail),
                TabularExportCell.Number(r.QuantityMt),
                TabularExportCell.Number(r.AmountUsd),
                TabularExportCell.Date(r.DocumentDate)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> NegativeStock(
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeInventory: true);
        return View(await BuildNegativeStockAsync(filter, ct));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> NegativeStockExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildNegativeStockAsync(filter, ct);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Negative_Stock",
            TitleFa = "موجودی منفی",
            TitleEn = "Negative stock",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("جنس", "Product", Width: 18),
                new("جواز", "Company", Width: 18),
                new("قرارداد", "Contract", Width: 16),
                new("ترمینال", "Terminal", Width: 16),
                new("مخزن", "Tank", Width: 12),
                new("اولین منفی‌شدن", "First negative", TabularExportValueType.Date, 14),
                new("مانده در آن تاریخ MT", "Balance then MT", TabularExportValueType.Number, 18),
                new("مانده فعلی MT", "Closing MT", TabularExportValueType.Number, 16),
                new("سند ایجادکننده", "Causing movement", Width: 22),
                new("علت احتمالی", "Probable cause", Width: 46),
                new("وضعیت", "Status", Width: 14)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.ProductName),
                TabularExportCell.Text(r.CompanyName),
                TabularExportCell.Text(r.ContractNumber),
                TabularExportCell.Text(r.TerminalName),
                TabularExportCell.Text(r.StorageTankCode),
                TabularExportCell.Date(r.FirstNegativeDate),
                TabularExportCell.Number(r.FirstNegativeBalanceMt),
                TabularExportCell.Number(r.ClosingBalanceMt),
                TabularExportCell.Text($"#{r.CausingMovementId} {r.CausingMovementReference}"),
                TabularExportCell.Text(r.ProbableCause),
                TabularExportCell.Text(r.Status.ToString())
            ]))
        });
    }

    // ------------------------------------------------------------------ builders

    private async Task<SellableStockReportViewModel> BuildSellableStockAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var rows = await _preSaleReservations.GetSellableStockAsync(filter, ct);
        var model = new SellableStockReportViewModel { Filter = filter, Rows = rows };

        return new SellableStockReportViewModel
        {
            Filter = filter,
            Rows = rows,
            Metrics =
            [
                new() { Label = "موجودی ملکی", Value = Quantity(model.TotalPhysicalMt), Detail = "Owned stock", Icon = "bi-box-seam", ToneClass = "" },
                new() { Label = "رزرو پیش‌فروش", Value = Quantity(model.TotalReservedMt), Detail = "Active reservation", Icon = "bi-bookmark-check", ToneClass = "" },
                new() { Label = "قابل فروش", Value = Quantity(model.TotalSellableMt), Detail = "Physical - reservation", Icon = "bi-box-arrow-up-right", ToneClass = model.TotalSellableMt < 0m ? "finance-negative" : "finance-positive" },
                new() { Label = "رزرو بدون منبع", Value = Quantity(model.TotalUnallocatedReservedMt), Detail = "Unallocated — not netted", Icon = "bi-question-circle", ToneClass = model.TotalUnallocatedReservedMt > 0m ? "finance-negative" : "" }
            ]
        };
    }

    private async Task<PreSaleDiscrepancyReportViewModel> BuildPreSaleDiscrepanciesAsync(
        ManagementReportFilterViewModel filter,
        PreSaleDiscrepancyKind kind,
        int page,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        var summary = await _preSaleReservations.GetDiscrepancySummaryAsync(filter, ct);
        var totalCount = summary.FirstOrDefault(s => s.Kind == kind)?.Count ?? 0;
        var rows = await _preSaleReservations.GetDiscrepanciesAsync(
            filter, kind, (page - 1) * DiscrepancyPageSize, DiscrepancyPageSize, ct);

        return new PreSaleDiscrepancyReportViewModel
        {
            Filter = filter,
            Summary = summary,
            SelectedKind = kind,
            Rows = rows,
            Page = page,
            PageSize = DiscrepancyPageSize,
            TotalCount = totalCount
        };
    }

    private async Task<NegativeStockReportViewModel> BuildNegativeStockAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var rows = await _negativeStock.AnalyzeAsync(filter, ct);
        var model = new NegativeStockReportViewModel { Filter = filter, Rows = rows };

        return new NegativeStockReportViewModel
        {
            Filter = filter,
            Rows = rows,
            Metrics =
            [
                new() { Label = "کسری باز", Value = model.OpenCount.ToString("N0"), Detail = "Still negative", Icon = "bi-exclamation-octagon", ToneClass = model.OpenCount > 0 ? "finance-negative" : "finance-positive" },
                new() { Label = "مقدار کسری باز", Value = Quantity(model.TotalOpenShortageMt), Detail = "Open shortage", Icon = "bi-box-seam", ToneClass = model.OpenCount > 0 ? "finance-negative" : "" },
                new() { Label = "ترتیب تاریخ (Legacy)", Value = model.HealedCount.ToString("N0"), Detail = "Dipped then recovered", Icon = "bi-clock-history", ToneClass = "" },
                new() { Label = "مجموع موارد", Value = model.Rows.Count.ToString("N0"), Detail = "Scopes reviewed", Icon = "bi-list-check", ToneClass = "" }
            ]
        };
    }

    private static string Quantity(decimal value) => $"{value:N4} MT";
}
