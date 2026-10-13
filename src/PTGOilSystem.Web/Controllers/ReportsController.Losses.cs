using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «گزارش کسری‌ها و ضایعات» — فقط‌خواندنی و جدا از لیست عملیاتی ثبت کسری.
///
/// <para>منبع یگانه <see cref="LossEvent"/>های لغونشدهٔ همهٔ مراحل است (بارگیری، مسیر، رسید، مخزن،
/// دیسپچ، گمرک، فروش، تسویهٔ مخزن و تعدیل دستی). فقط کسری شمرده می‌شود: اضافه‌بار با کسری خالص
/// نمی‌شود و جای آن گزارش تخصصی «کسری و اضافه‌بار حمل» است.</para>
///
/// <para>مقدار کسری همان قاعدهٔ اقتصاد قرارداد است: تفاوت مثبت، وگرنه مقدار قابل جریمه. قیمت هر تن
/// از قیمت همان بارگیری، وگرنه میانگین وزنی خریدِ همان قرارداد تا تاریخ گزارش
/// (<see cref="IProfitAndLossService.BuildPurchaseCostBasisAsync"/>). بی‌قیمت یعنی «قیمت نامشخص»؛
/// صفر یا عدد ساختگی ساخته نمی‌شود.</para>
/// </summary>
public partial class ReportsController
{
    private const int LossReportPageSize = 50;

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> LossReport(
        [FromQuery] LossReportFilterViewModel? filter = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new LossReportFilterViewModel();
        await PopulateLookupsAsync(new ManagementReportFilterViewModel { ContractId = filter.ContractId, ProductId = filter.ProductId });
        var pageSize = ListPageSize.Resolve(perPage, LossReportPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = LossReportPageSize;
        return View(await BuildLossReportAsync(filter, page, pageSize, cancellationToken));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> LossReportExport(
        string? format,
        [FromQuery] LossReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new LossReportFilterViewModel();
        var model = await BuildLossReportAsync(filter, 1, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Loss_Report",
            TitleFa = "گزارش کسری‌ها و ضایعات",
            TitleEn = "Shortage & Loss Report",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("جنس / Product", filter.ProductId),
                ("قرارداد / Contract", filter.ContractId),
                ("مرحله / Stage", filter.Stage.HasValue ? LossStageLabel(filter.Stage.Value) : null),
                ("نوع کسری / Kind", filter.Kind == LossReportKind.All ? null : LossKindLabel(filter.Kind)),
                ("جست‌وجو / Search", filter.Search),
                ("مجموع کسری MT / Total loss MT", model.TotalLossMt.ToString("N4")),
                ("قابل جریمه MT / Chargeable MT", model.TotalChargeableMt.ToString("N4")),
                ("ارزش مالی USD / Value USD", model.TotalValuedUsd.ToString("N2")),
                ("بدون قیمت / Unpriced", model.UnpricedCount > 0 ? model.UnpricedCount.ToString("N0") : null)),
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 14),
                new("مرحله / منبع", "Stage", Width: 16),
                new("قرارداد / بار", "Contract / load", Width: 22),
                new("جنس", "Product", Width: 14),
                new("مقدار کسری MT", "Loss MT", TabularExportValueType.Number, 14),
                new("قابل جریمه MT", "Chargeable MT", TabularExportValueType.Number, 14),
                new("قیمت هر تن USD", "Unit price USD", TabularExportValueType.Number, 14),
                new("ارزش مالی USD", "Value USD", TabularExportValueType.Number, 15),
                new("وضعیت قیمت", "Price status", Width: 13),
                new("مسئول / طرف حساب", "Responsible", Width: 20)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Date(r.Date),
                TabularExportCell.Text(LossStageLabel(r.Stage)),
                TabularExportCell.Text(JoinReference(r.ContractLabel, r.LoadReference)),
                TabularExportCell.Text(r.ProductName),
                TabularExportCell.Number(r.LossMt),
                TabularExportCell.Number(r.ChargeableMt),
                TabularExportCell.Number(r.UnitPriceUsd),
                TabularExportCell.Number(r.ValueUsd),
                TabularExportCell.Text(r.UnitPriceUsd.HasValue ? "قیمت معتبر" : "قیمت نامشخص"),
                TabularExportCell.Text(r.ResponsibleName)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Text($"{model.TotalCount:N0} مورد"),
                TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.TotalLossMt),
                TabularExportCell.Number(model.TotalChargeableMt),
                TabularExportCell.Number(null),
                TabularExportCell.Number(model.TotalValuedUsd),
                TabularExportCell.Text(model.UnpricedCount > 0 ? $"{model.UnpricedCount:N0} بدون قیمت" : null),
                TabularExportCell.Text(null)
            ])
        });
    }

    internal static string LossStageLabel(LossEventStage stage) => stage switch
    {
        LossEventStage.LoadingDifference => "کسری بارگیری",
        LossEventStage.TransitLoss => "کسری حمل‌ونقل",
        LossEventStage.ReceiptShortage => "کسری رسید و تخلیه",
        LossEventStage.TankNaturalLoss => "ضایعات مخزن",
        LossEventStage.DispatchShortage => "کسری دیسپچ",
        LossEventStage.CustomsLoss => "کسری گمرکی",
        LossEventStage.SalesDifference => "کسری فروش",
        LossEventStage.ManualAdjustment => "تعدیل دستی",
        LossEventStage.TankFinalSettlement => "تسویهٔ نهایی مخزن",
        _ => stage.ToString()
    };

    internal static string LossKindLabel(LossReportKind kind) => kind switch
    {
        LossReportKind.ChargeableOnly => "فقط قابل جریمه",
        LossReportKind.NonChargeableOnly => "فقط غیرقابل جریمه",
        _ => "همه"
    };

    private async Task<LossReportViewModel> BuildLossReportAsync(
        LossReportFilterViewModel filter,
        int page,
        int? pageSize,
        CancellationToken ct)
    {
        var query = _db.LossEvents.AsNoTracking()
            // فقط کسری: تفاوت مثبت یا مقدار قابل جریمهٔ مثبت. اضافه‌بار این‌جا نمی‌آید.
            .Where(e => !e.IsCancelled && (e.DifferenceQuantityMt > 0m || e.ChargeableLossMt > 0m));
        if (filter.FromDate.HasValue) query = query.Where(e => e.EventDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(e => e.EventDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ProductId.HasValue) query = query.Where(e => e.ProductId == filter.ProductId.Value);
        if (filter.Stage.HasValue) query = query.Where(e => e.Stage == filter.Stage.Value);
        if (filter.Kind == LossReportKind.ChargeableOnly) query = query.Where(e => e.ChargeableLossMt > 0m);
        if (filter.Kind == LossReportKind.NonChargeableOnly) query = query.Where(e => e.ChargeableLossMt <= 0m);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(e => (e.ResponsiblePartyName != null && e.ResponsiblePartyName.Contains(term))
                || (e.Reference != null && e.Reference.Contains(term))
                || (e.Notes != null && e.Notes.Contains(term)));
        }

        var events = await query
            .Select(e => new
            {
                e.Id,
                e.EventDate,
                e.Stage,
                ContractId = e.ContractId
                    ?? (e.LoadingRegister != null ? (int?)e.LoadingRegister.ContractId : null)
                    ?? (e.LoadingReceipt != null && e.LoadingReceipt.LoadingRegister != null
                        ? (int?)e.LoadingReceipt.LoadingRegister.ContractId : null)
                    ?? (e.TransportLeg != null ? (int?)e.TransportLeg.SourcePurchaseContractId : null)
                    ?? (e.Shipment != null ? e.Shipment.ContractId : null),
                LoadingPriceUsd = e.LoadingRegister != null
                    ? e.LoadingRegister.LoadingPriceUsd
                    : e.LoadingReceipt != null && e.LoadingReceipt.LoadingRegister != null
                        ? e.LoadingReceipt.LoadingRegister.LoadingPriceUsd
                        : null,
                LoadRef = e.LoadingRegister != null
                    ? e.LoadingRegister.WagonNumber ?? e.LoadingRegister.RwbNo ?? e.LoadingRegister.BillOfLadingNumber
                    : null,
                e.LoadingRegisterId,
                e.TruckDispatchId,
                e.TransportLegId,
                ProductName = e.Product != null ? (e.Product.NamePersian ?? e.Product.Name) : "",
                e.DifferenceQuantityMt,
                e.ChargeableLossMt,
                e.ResponsiblePartyName,
                e.ResponsiblePartyType,
                e.FinancialTreatment,
                e.Reference
            })
            .ToListAsync(ct);

        if (filter.ContractId.HasValue)
        {
            events = events.Where(e => e.ContractId == filter.ContractId.Value).ToList();
        }

        // میانگین وزنی خرید قرارداد، فقط برای کسری‌ای که قیمت بارگیری ندارد.
        var needContractPrice = events
            .Where(e => !(e.LoadingPriceUsd is > 0m) && e.ContractId.HasValue)
            .Select(e => e.ContractId!.Value)
            .Distinct()
            .ToList();
        var purchaseNeed = needContractPrice.Count == 0
            ? new List<int>()
            : await _db.Contracts.AsNoTracking()
                .Where(c => needContractPrice.Contains(c.Id) && c.ContractType == ContractType.Purchase)
                .Select(c => c.Id)
                .ToListAsync(ct);
        var basis = purchaseNeed.Count == 0
            ? PurchaseCostBasis.Empty
            : await _profitAndLoss.BuildPurchaseCostBasisAsync(purchaseNeed, (filter.ToDate ?? _businessClock.Today).Date, ct);

        var contractIds = events.Where(e => e.ContractId.HasValue).Select(e => e.ContractId!.Value).Distinct().ToList();
        var contractLabels = contractIds.Count == 0
            ? new Dictionary<int, string>()
            : (await _db.Contracts.AsNoTracking()
                    .Where(c => contractIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.ContractName, c.ContractNumber })
                    .ToListAsync(ct))
                .ToDictionary(c => c.Id, c => Contract.BuildDisplayLabel(c.ContractName, c.ContractNumber));

        var rows = events
            .Select(e =>
            {
                decimal? price = e.LoadingPriceUsd is > 0m
                    ? e.LoadingPriceUsd
                    : e.ContractId.HasValue
                        && basis.Contracts.TryGetValue(e.ContractId.Value, out var cb)
                        && cb.WeightedAveragePurchasePriceUsd is > 0m
                            ? cb.WeightedAveragePurchasePriceUsd
                            : null;
                return new LossReportRowViewModel
                {
                    LossEventId = e.Id,
                    Date = e.EventDate,
                    Stage = e.Stage,
                    ContractId = e.ContractId,
                    ContractLabel = e.ContractId.HasValue ? contractLabels.GetValueOrDefault(e.ContractId.Value) : null,
                    LoadReference = e.LoadRef
                        ?? (e.TruckDispatchId.HasValue ? $"دیسپچ #{e.TruckDispatchId}"
                            : e.TransportLegId.HasValue ? $"حمل #{e.TransportLegId}"
                            : e.LoadingRegisterId.HasValue ? $"بارگیری #{e.LoadingRegisterId}" : null),
                    ProductName = e.ProductName,
                    LossMt = e.DifferenceQuantityMt > 0m ? e.DifferenceQuantityMt : Math.Max(e.ChargeableLossMt, 0m),
                    ChargeableMt = Math.Max(e.ChargeableLossMt, 0m),
                    UnitPriceUsd = price,
                    ResponsibleName = e.ResponsiblePartyName,
                    ResponsibleType = e.ResponsiblePartyType,
                    FinancialTreatment = e.FinancialTreatment,
                    Reference = e.Reference
                };
            })
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.LossEventId)
            .ToList();

        var pageCount = pageSize is > 0 ? Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize.Value)) : 1;
        var currentPage = Math.Clamp(page, 1, pageCount);

        return new LossReportViewModel
        {
            Filter = filter,
            Rows = pageSize is > 0 ? rows.Skip((currentPage - 1) * pageSize.Value).Take(pageSize.Value).ToList() : rows,
            TotalCount = rows.Count,
            TotalLossMt = rows.Sum(r => r.LossMt),
            TotalChargeableMt = rows.Sum(r => r.ChargeableMt),
            TotalValuedUsd = rows.Sum(r => r.ValueUsd ?? 0m),
            UnpricedCount = rows.Count(r => !r.UnitPriceUsd.HasValue),
            CurrentPage = currentPage,
            PageCount = pageCount
        };
    }
}
