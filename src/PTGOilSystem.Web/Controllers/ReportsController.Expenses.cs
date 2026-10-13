using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
/// «گزارش مصارف» — فقط‌خواندنی و جدا از لیست عملیاتیِ ثبت مصارف.
///
/// <para>دو منبع، هر مصرف با یک هویت:</para>
/// <list type="bullet">
/// <item><b>سند مصرف</b> (<see cref="ExpenseTransaction"/>، کلید «E-شناسه») — مصارف عمومی، قرارداد،
/// بارگیری، گمرک (آینهٔ اظهارنامه)، حمل/انتقال، دیسپچ، محموله و دارایی عملیاتی همه همین‌جا سند دارند.
/// سند لغوشده شمرده نمی‌شود. سندِ «تفاوت نرخ» زیان ارزی است، نه مصرف عملیاتی، و بیرون می‌ماند.</item>
/// <item><b>مصرف درون‌خطیِ بارگیری بدون سند</b> (کلید «L-شناسهٔ بارگیری») — از همان
/// <see cref="IProfitAndLossService.BuildPurchaseCostBasisAsync"/> که بخشِ دارای سند رسمی یا آینه‌شده
/// را کنار می‌گذارد؛ پس همان پول از دو مسیر دو بار نمی‌آید.</item>
/// </list>
/// قیمت خرید نفت و انتقال پول بین حساب‌ها مصرف نیستند و در هیچ‌یک از دو منبع نمی‌آیند.
/// </summary>
public partial class ReportsController
{
    private const int ExpenseReportPageSize = 50;

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> ExpenseReport(
        [FromQuery] ExpenseReportFilterViewModel? filter = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ExpenseReportFilterViewModel();
        await PopulateExpenseReportLookupsAsync(filter, cancellationToken);
        var pageSize = ListPageSize.Resolve(perPage, ExpenseReportPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = ExpenseReportPageSize;
        return View(await BuildExpenseReportAsync(filter, page, pageSize, cancellationToken));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> ExpenseReportExport(
        string? format,
        [FromQuery] ExpenseReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ExpenseReportFilterViewModel();
        // pageSize: null — خروجی همهٔ نتایج فیلترشده است، نه فقط صفحهٔ جاری.
        var model = await BuildExpenseReportAsync(filter, 1, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var filters = TabularExportSupport.FilterSummary(
            ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
            ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
            ("نوع مصرف / Type", filter.ExpenseTypeId),
            ("قرارداد / Contract", filter.ContractId),
            ("شرکت خدماتی / Service provider", filter.ServiceProviderId),
            ("منبع ثبت / Source", filter.Source.HasValue ? ExpenseSourceLabel(filter.Source.Value) : null),
            ("وضعیت پرداخت / Payment", filter.PaymentStatus.HasValue ? ExpensePaymentStatusLabel(filter.PaymentStatus.Value) : null),
            ("جست‌وجو / Search", filter.Search),
            ("مجموع مصارف USD / Total USD", model.TotalAmountUsd.ToString("N2")),
            ("تعداد ثبت / Count", model.TotalCount.ToString("N0")),
            ("پرداخت‌شده USD / Paid USD", model.TotalPaidUsd.ToString("N2")),
            ("پرداخت‌نشده USD / Unpaid USD", model.TotalUnpaidUsd.ToString("N2")));

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Expense_Report",
            TitleFa = "گزارش مصارف",
            TitleEn = "Expense Report",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = filters,
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 14),
                new("نوع مصرف", "Expense type", Width: 18),
                new("منبع / بخش ثبت", "Source", Width: 16),
                new("قرارداد یا مرجع", "Contract / reference", Width: 20),
                new("شرح", "Description", Width: 28, Wrap: true),
                new("طرف حساب", "Party", Width: 18),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 15),
                new("مبلغ اصلی", "Original amount", TabularExportValueType.Number, 15),
                new("ارز", "Currency", Width: 8),
                new("وضعیت پرداخت", "Payment status", Width: 14),
                new("پرداخت‌شده USD", "Paid USD", TabularExportValueType.Number, 15),
                new("پرداخت‌نشده USD", "Unpaid USD", TabularExportValueType.Number, 15)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Date(r.Date),
                TabularExportCell.Text(r.TypeName),
                TabularExportCell.Text(ExpenseSourceLabel(r.Source)),
                TabularExportCell.Text(JoinReference(r.ContractLabel, r.Reference)),
                TabularExportCell.Text(r.Description),
                TabularExportCell.Text(r.PartyName),
                TabularExportCell.Number(r.AmountUsd),
                TabularExportCell.Number(r.Amount),
                TabularExportCell.Text(r.Currency),
                TabularExportCell.Text(ExpensePaymentStatusLabel(r.PaymentStatus)),
                TabularExportCell.Number(r.PaidUsd),
                TabularExportCell.Number(r.UnpaidUsd)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text($"{model.TotalCount:N0} ثبت / entries"),
                TabularExportCell.Text(null),
                TabularExportCell.Number(model.TotalAmountUsd),
                TabularExportCell.Number(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.TotalPaidUsd),
                TabularExportCell.Number(model.TotalUnpaidUsd)
            ])
        });
    }

    internal static string JoinReference(string? contract, string? reference)
        => string.Join(" · ", new[] { contract, reference }.Where(v => !string.IsNullOrWhiteSpace(v)));

    internal static string ExpenseSourceLabel(ExpenseReportSource source) => source switch
    {
        ExpenseReportSource.Contract => "قرارداد",
        ExpenseReportSource.Loading => "بارگیری",
        ExpenseReportSource.LoadingInline => "بارگیری (ثبت روی بار)",
        ExpenseReportSource.Customs => "گمرک",
        ExpenseReportSource.TransportLeg => "حمل و انتقال داخلی",
        ExpenseReportSource.Dispatch => "ترانسپورت / موتر",
        ExpenseReportSource.Shipment => "محموله",
        ExpenseReportSource.OperationalAsset => "دارایی عملیاتی",
        _ => "مصارف عمومی شرکت"
    };

    internal static string ExpensePaymentStatusLabel(ExpenseReportPaymentStatus status) => status switch
    {
        ExpenseReportPaymentStatus.Paid => "پرداخت‌شده",
        ExpenseReportPaymentStatus.PartlyPaid => "پرداخت قسمتی",
        ExpenseReportPaymentStatus.Unpaid => "پرداخت‌نشده",
        ExpenseReportPaymentStatus.NonCash => "غیرنقدی",
        _ => "نامشخص"
    };

    private async Task<ExpenseReportViewModel> BuildExpenseReportAsync(
        ExpenseReportFilterViewModel filter,
        int page,
        int? pageSize,
        CancellationToken ct)
    {
        var from = filter.FromDate?.Date;
        var toExclusive = filter.ToDate?.Date.AddDays(1);
        var rows = new List<ExpenseReportRowViewModel>();

        // ── ۱) اسناد مصرف ──
        var fxTypeIds = await ProfitAndLossService.FxDifferenceExpenseTypeIds(_db).ToListAsync(ct);
        var expenseQuery = _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled && !fxTypeIds.Contains(e.ExpenseTypeId));
        if (from.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate >= from.Value);
        if (toExclusive.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate < toExclusive.Value);
        if (filter.ExpenseTypeId.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseTypeId == filter.ExpenseTypeId.Value);
        if (filter.ServiceProviderId.HasValue) expenseQuery = expenseQuery.Where(e => e.ServiceProviderId == filter.ServiceProviderId.Value);

        var documents = await expenseQuery
            .Select(e => new
            {
                e.Id,
                e.ExpenseDate,
                TypeName = e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : "",
                e.CustomsDeclarationId,
                e.OperationalAssetId,
                e.TruckDispatchId,
                e.TransportLegId,
                e.LoadingRegisterId,
                e.ShipmentId,
                e.ContractId,
                ResolvedContractId = e.ContractId
                    ?? (e.LoadingRegister != null ? (int?)e.LoadingRegister.ContractId : null)
                    ?? (e.TransportLeg != null ? (int?)e.TransportLeg.SourcePurchaseContractId : null)
                    ?? (e.Shipment != null ? e.Shipment.ContractId : null),
                e.Description,
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                DriverName = e.Driver != null ? e.Driver.FullName : null,
                AssetName = e.OperationalAsset != null ? e.OperationalAsset.Name : null,
                CashAccountName = e.CashAccount != null ? e.CashAccount.Name : null,
                e.Amount,
                e.Currency,
                e.AppliedFxRateToUsd,
                e.AmountUsd,
                e.SettlementMode,
                LinkedPaidUsd = _db.PaymentTransactions
                    .Where(p => p.ExpenseTransactionId == e.Id && p.Direction == PaymentDirection.Out)
                    .Sum(p => (decimal?)p.AmountUsd) ?? 0m
            })
            .ToListAsync(ct);

        foreach (var e in documents)
        {
            var source = e.CustomsDeclarationId.HasValue ? ExpenseReportSource.Customs
                : e.OperationalAssetId.HasValue ? ExpenseReportSource.OperationalAsset
                : e.TruckDispatchId.HasValue ? ExpenseReportSource.Dispatch
                : e.TransportLegId.HasValue ? ExpenseReportSource.TransportLeg
                : e.LoadingRegisterId.HasValue ? ExpenseReportSource.Loading
                : e.ShipmentId.HasValue ? ExpenseReportSource.Shipment
                : e.ContractId.HasValue ? ExpenseReportSource.Contract
                : ExpenseReportSource.General;

            var (status, paid) = e.SettlementMode switch
            {
                ExpenseSettlementMode.PaidImmediately => (ExpenseReportPaymentStatus.Paid, e.AmountUsd),
                ExpenseSettlementMode.Payable when e.LinkedPaidUsd >= e.AmountUsd - 0.005m && e.AmountUsd > 0m
                    => (ExpenseReportPaymentStatus.Paid, e.AmountUsd),
                ExpenseSettlementMode.Payable when e.LinkedPaidUsd > 0m
                    => (ExpenseReportPaymentStatus.PartlyPaid, e.LinkedPaidUsd),
                ExpenseSettlementMode.Payable => (ExpenseReportPaymentStatus.Unpaid, 0m),
                ExpenseSettlementMode.NonCash => (ExpenseReportPaymentStatus.NonCash, 0m),
                _ => (ExpenseReportPaymentStatus.Unknown, 0m)
            };

            rows.Add(new ExpenseReportRowViewModel
            {
                Key = $"E-{e.Id}",
                Date = e.ExpenseDate,
                TypeName = e.TypeName,
                Source = source,
                ContractId = e.ResolvedContractId,
                Reference = $"#{e.Id}",
                Description = e.Description,
                PartyName = e.ServiceProviderName ?? e.DriverName ?? e.AssetName ?? e.CashAccountName,
                AmountUsd = e.AmountUsd,
                Amount = e.Amount,
                Currency = string.IsNullOrWhiteSpace(e.Currency) ? "USD" : e.Currency,
                FxRateToUsd = e.AppliedFxRateToUsd,
                PaymentStatus = status,
                PaidUsd = paid,
                UnpaidUsd = status is ExpenseReportPaymentStatus.Unpaid or ExpenseReportPaymentStatus.PartlyPaid
                    ? Math.Max(e.AmountUsd - paid, 0m)
                    : 0m,
                DetailsController = "Expenses",
                DetailsId = e.Id
            });
        }

        // ── ۲) مصرف درون‌خطیِ بارگیری که سند مصرف ندارد ──
        // نوع مصرف، شرکت خدماتی و جست‌وجوی متنی مخصوص سند مصرف‌اند؛ با آن فیلترها این منبع کنار می‌رود.
        var includeLoadingInline = !filter.ExpenseTypeId.HasValue && !filter.ServiceProviderId.HasValue;
        if (includeLoadingInline)
        {
            var purchaseIds = await _db.Contracts.AsNoTracking()
                .Where(c => c.ContractType == ContractType.Purchase)
                .Select(c => c.Id)
                .ToListAsync(ct);
            var asOf = (filter.ToDate ?? _businessClock.Today).Date;
            var basis = purchaseIds.Count == 0
                ? PurchaseCostBasis.Empty
                : await _profitAndLoss.BuildPurchaseCostBasisAsync(purchaseIds, asOf, ct);
            var loadings = basis.Loadings
                .Where(l => (!from.HasValue || l.LoadingDate >= from.Value)
                    && (!toExclusive.HasValue || l.LoadingDate < toExclusive.Value))
                .ToList();
            var loadingIds = loadings.Select(l => l.LoadingRegisterId).ToList();
            var loadingRefs = loadingIds.Count == 0
                ? new Dictionary<int, string?>()
                : await _db.LoadingRegisters.AsNoTracking()
                    .Where(l => loadingIds.Contains(l.Id))
                    .Select(l => new { l.Id, Ref = l.WagonNumber ?? l.RwbNo ?? l.BillOfLadingNumber })
                    .ToDictionaryAsync(l => l.Id, l => l.Ref, ct);

            foreach (var l in loadings)
            {
                void AddInline(string typeName, decimal amountUsd)
                {
                    if (amountUsd == 0m)
                    {
                        return;
                    }

                    rows.Add(new ExpenseReportRowViewModel
                    {
                        Key = $"L-{l.LoadingRegisterId}-{typeName}",
                        Date = l.LoadingDate,
                        TypeName = typeName,
                        Source = ExpenseReportSource.LoadingInline,
                        ContractId = l.ContractId,
                        Reference = loadingRefs.GetValueOrDefault(l.LoadingRegisterId) ?? $"#{l.LoadingRegisterId}",
                        Description = "ثبت‌شده روی بارگیری، بدون سند مصرف جداگانه",
                        AmountUsd = amountUsd,
                        Amount = amountUsd,
                        Currency = "USD",
                        FxRateToUsd = 1m,
                        PaymentStatus = ExpenseReportPaymentStatus.Unknown,
                        DetailsController = "Loading",
                        DetailsId = l.LoadingRegisterId
                    });
                }

                AddInline("کرایه حمل و خط‌آهن بارگیری", l.CapitalizedUsd);
                AddInline("گدام و سایر مصارف بارگیری", l.PeriodExpenseUsd);
            }
        }

        // ── نام قرارداد ──
        var contractIds = rows.Where(r => r.ContractId.HasValue).Select(r => r.ContractId!.Value).Distinct().ToList();
        var contractLabels = contractIds.Count == 0
            ? new Dictionary<int, string>()
            : (await _db.Contracts.AsNoTracking()
                    .Where(c => contractIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.ContractName, c.ContractNumber })
                    .ToListAsync(ct))
                .ToDictionary(c => c.Id, c => Contract.BuildDisplayLabel(c.ContractName, c.ContractNumber));

        IEnumerable<ExpenseReportRowViewModel> filtered = rows
            .Select(r => r.ContractId.HasValue && contractLabels.TryGetValue(r.ContractId.Value, out var label)
                ? CloneWithContract(r, label)
                : r);
        if (filter.ContractId.HasValue) filtered = filtered.Where(r => r.ContractId == filter.ContractId.Value);
        if (filter.Source.HasValue) filtered = filtered.Where(r => r.Source == filter.Source.Value);
        if (filter.PaymentStatus.HasValue) filtered = filtered.Where(r => r.PaymentStatus == filter.PaymentStatus.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            filtered = filtered.Where(r =>
                (r.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.PartyName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.ContractLabel?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Reference?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || r.TypeName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var all = filtered
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.Key, StringComparer.Ordinal)
            .ToList();

        // جمع‌ها و خلاصهٔ گروهی همیشه روی همهٔ نتایج فیلترشده است، نه فقط صفحهٔ جاری.
        var groups = all
            .GroupBy(r => filter.GroupBy switch
            {
                ExpenseReportGroupBy.Source => ExpenseSourceLabel(r.Source),
                ExpenseReportGroupBy.Contract => r.ContractLabel ?? "بدون قرارداد",
                ExpenseReportGroupBy.Month => r.Date.ToString("yyyy-MM"),
                _ => string.IsNullOrWhiteSpace(r.TypeName) ? "بدون نوع" : r.TypeName
            })
            .Select(g => new ExpenseReportGroupRowViewModel(
                g.Key, g.Count(), g.Sum(r => r.AmountUsd), g.Sum(r => r.PaidUsd), g.Sum(r => r.UnpaidUsd)))
            .OrderBy(g => filter.GroupBy == ExpenseReportGroupBy.Month ? g.Label : null, StringComparer.Ordinal)
            .ThenByDescending(g => filter.GroupBy == ExpenseReportGroupBy.Month ? 0m : g.AmountUsd)
            .ToList();

        var pageCount = pageSize is > 0 ? Math.Max(1, (int)Math.Ceiling(all.Count / (double)pageSize.Value)) : 1;
        var currentPage = Math.Clamp(page, 1, pageCount);
        var pageRows = pageSize is > 0
            ? all.Skip((currentPage - 1) * pageSize.Value).Take(pageSize.Value).ToList()
            : all;

        return new ExpenseReportViewModel
        {
            Filter = filter,
            Rows = pageRows,
            Groups = groups,
            TotalCount = all.Count,
            TotalAmountUsd = all.Sum(r => r.AmountUsd),
            TotalPaidUsd = all.Sum(r => r.PaidUsd),
            TotalUnpaidUsd = all.Sum(r => r.UnpaidUsd),
            CurrentPage = currentPage,
            PageCount = pageCount
        };
    }

    private static ExpenseReportRowViewModel CloneWithContract(ExpenseReportRowViewModel r, string contractLabel) => new()
    {
        Key = r.Key, Date = r.Date, TypeName = r.TypeName, Source = r.Source, ContractId = r.ContractId,
        ContractLabel = contractLabel, Reference = r.Reference, Description = r.Description, PartyName = r.PartyName,
        AmountUsd = r.AmountUsd, Amount = r.Amount, Currency = r.Currency, FxRateToUsd = r.FxRateToUsd,
        PaymentStatus = r.PaymentStatus, PaidUsd = r.PaidUsd, UnpaidUsd = r.UnpaidUsd,
        DetailsController = r.DetailsController, DetailsId = r.DetailsId
    };

    private async Task PopulateExpenseReportLookupsAsync(ExpenseReportFilterViewModel filter, CancellationToken ct)
    {
        ViewBag.ExpenseTypes = new SelectList(
            await _db.ExpenseTypes.AsNoTracking()
                .OrderBy(t => t.NamePersian ?? t.Name)
                .Select(t => new LookupOption(t.Id, t.NamePersian ?? t.Name))
                .ToListAsync(ct),
            "Id", "Name", filter.ExpenseTypeId);
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new LookupOption(s.Id, s.Name))
                .ToListAsync(ct),
            "Id", "Name", filter.ServiceProviderId);
        await PopulateLookupsAsync(new ManagementReportFilterViewModel { ContractId = filter.ContractId });
    }
}
