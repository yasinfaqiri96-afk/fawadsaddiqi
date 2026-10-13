using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class ReportsController
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> ReceivablesPayablesExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildReceivablesPayablesReportAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = model.Rows.ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Receivables_Payables", TitleFa = "طلبات و بدهی‌ها", TitleEn = "Receivables & Payables",
            KnownRowCount = rows.Count,
            Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("طرف حساب", "Party", Width: 24), new("نوع", "Type", Width: 16), new("شرح مانده", "Balance kind", Width: 18),
                new("اول دوره USD", "Opening USD", TabularExportValueType.Number, 16),
                new("رسید USD", "Received USD", TabularExportValueType.Number, 16),
                new("برد USD", "Given USD", TabularExportValueType.Number, 16),
                new("گردش این بازه USD", "Movement in range USD", TabularExportValueType.Number, 17),
                new("آخرین تاریخ", "Last date", TabularExportValueType.Date, 14)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.PartyName), TabularExportCell.Text(r.PartyType), TabularExportCell.Text(r.BalanceKind),
                TabularExportCell.Number(r.OpeningBalanceUsd), TabularExportCell.Number(r.DebitUsd),
                TabularExportCell.Number(r.CreditUsd), TabularExportCell.Number(r.PeriodMovementUsd),
                TabularExportCell.Date(r.LastEntryDate)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(rows.Sum(r => r.OpeningBalanceUsd)),
                TabularExportCell.Number(rows.Sum(r => r.DebitUsd)), TabularExportCell.Number(rows.Sum(r => r.CreditUsd)),
                TabularExportCell.Number(rows.Sum(r => r.PeriodMovementUsd)),
                TabularExportCell.Date(null)
            ])
        });
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> ContractPnlExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildContractPnlAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();
        // صفحه فقط قراردادهای خرید را فهرست می‌کند؛ خروجی هم همان سطرها و همان چهار شاخص را دارد.
        var rows = model.PurchaseRows.ToList();
        var filters = BuildReportExportFilters(filter).ToList();
        filters.AddRange(TabularExportSupport.FilterSummary(
            ("مجموع مبلغ قراردادها USD / Total contract value USD", model.TotalContractValueUsd.ToString("N2")),
            ("مجموع مصارف قراردادها USD / Total contract expenses USD", model.TotalContractExpensesUsd.ToString("N2")),
            ("مجموع فروشات USD / Total sales USD", model.TotalDirectSaleRevenueUsd.ToString("N2")),
            ("مجموع مفاد قراردادها USD / Total contract profit USD", model.TotalContractProfitUsd.ToString("N2"))));

        static string Note(ContractPnlRowViewModel r) =>
            r.HasPendingPurchasePrice ? $"قیمت نامشخص ({r.PendingLoadedMt:N3} MT) / Price pending"
            : r.TotalRevenueUsd <= 0m ? "بدون فروش / No sales"
            : r.TotalLoadedMt > 0m && r.TotalSoldMt < r.TotalLoadedMt ? "فروش ناتمام / Partly sold"
            : "";

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Contract_PnL", TitleFa = "مفاد قراردادها", TitleEn = "Contract Profit",
            KnownRowCount = rows.Count, ForceLandscape = true, Filters = filters,
            Columns =
            [
                new("قرارداد", "Contract", Width: 24),
                new("تأمین‌کننده", "Supplier", Width: 20), new("جنس", "Product", Width: 15),
                new("وضعیت", "Status", Width: 11),
                new("بارگیری MT", "Loaded MT", TabularExportValueType.Number, 14),
                new("قیمت خرید USD", "Purchase price USD", TabularExportValueType.Number, 16),
                new("جمع مصارف USD", "Total expenses USD", TabularExportValueType.Number, 16),
                new("فروش USD", "Sales USD", TabularExportValueType.Number, 16),
                new("مفاد قرارداد USD", "Contract profit USD", TabularExportValueType.Number, 16),
                new("یادداشت", "Note", Width: 18, Wrap: true)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.DisplayLabel), TabularExportCell.Text(r.CounterpartyName),
                TabularExportCell.Text(r.ProductName), TabularExportCell.Text(r.Status.ToString()),
                TabularExportCell.Number(r.TotalLoadedMt),
                TabularExportCell.Number(r.PurchaseValueUsd), TabularExportCell.Number(r.ExpensesUsd),
                TabularExportCell.Number(r.TotalRevenueUsd), TabularExportCell.Number(r.ContractProfitUsd),
                TabularExportCell.Text(Note(r))
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Number(rows.Sum(r => r.TotalLoadedMt)),
                TabularExportCell.Number(model.TotalPurchaseValueUsd),
                TabularExportCell.Number(model.TotalContractExpensesUsd),
                TabularExportCell.Number(model.TotalDirectSaleRevenueUsd),
                TabularExportCell.Number(model.TotalContractProfitUsd),
                TabularExportCell.Text(null)
            ])
        });
    }

    /// <summary>
    /// سود و زیان شرکت. دقیقاً همان <c>BuildCompanyFinancialOverviewAsync</c> صفحه را با همان
    /// فیلتر صدا می‌زند؛ هیچ عدد مالی اینجا دوباره محاسبه نمی‌شود. بخش دوم خروجی، همان
    /// قراردادهای برتر صفحه است تا جمع شرکت با جمع قراردادها قابل تطبیق باشد.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> CompanyOverviewExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildCompanyFinancialOverviewAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();

        // سود ناخالص/خالص فقط وقتی صادر می‌شود که بهای تمام‌شدهٔ همهٔ فروش‌ها ثبت شده باشد؛
        // در غیر این صورت خانهٔ عدد خالی می‌ماند و علتش در ستون منبع نوشته می‌شود.
        var profitPublishable = model.IsProfitPublishable;
        var lines = new (string Fa, string En, decimal? Value)[]
        {
            ("درآمد فروش محقق‌شده", "Realised sales revenue", model.RevenueUsd),
            ("بهای تمام‌شدهٔ فروش", "Cost of goods sold", -model.PurchaseCostUsd),
            ("سود ناخالص", "Gross profit", profitPublishable ? model.GrossProfitUsd : null),
            ("مصارف", "Expenses", -model.ExpenseUsd),
            ("سود تسعیر ارز", "Exchange gain", model.ExchangeGainUsd),
            ("زیان تسعیر ارز", "Exchange loss", -model.ExchangeLossUsd),
            ("سود خالص", "Net profit", profitPublishable ? model.NetProfitUsd : null),
            ("گردش خالص نقدی", "Net cash movement", model.NetCashMovementUsd),
            ("دریافتنی از مشتریان", "Customer receivable", model.CustomerReceivableUsd),
            ("پرداختنی به تأمین‌کنندگان", "Supplier payable", model.SupplierPayableUsd),
            ("خالص صراف", "Sarraf net", model.SarrafNetUsd)
        };

        // شاخص‌های عملیاتی جدا از سطرهای مفاد و با مقدار مثبت می‌آیند؛ در «سود خالص» پایین کسر نشده‌اند.
        var operationalLines = new (string Fa, string En, decimal Value)[]
        {
            ("مصارف بارگیری (حمل/گدام/خط‌آهن/سایر)", "Loading costs (transport/warehouse/railway/other)", model.LoadingOperationalCostUsd),
            ("کسر و ضایعات", "Shortage and losses", model.LossCostUsd)
        };

        var isEn = UiText.IsEn(HttpContext);
        var operationalRows = new[]
            {
                new TabularExportRow(
                [
                    TabularExportCell.Text(isEn
                        ? CompanyFinancialOverviewViewModel.OperationalIndicatorsTitleEn
                        : CompanyFinancialOverviewViewModel.OperationalIndicatorsTitleFa),
                    TabularExportCell.Number(null),
                    TabularExportCell.Text(isEn
                        ? CompanyFinancialOverviewViewModel.OperationalIndicatorsNoteEn
                        : CompanyFinancialOverviewViewModel.OperationalIndicatorsNoteFa)
                ])
            }
            .Concat(operationalLines.Select(line => new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? line.En : line.Fa),
                TabularExportCell.Number(line.Value),
                TabularExportCell.Text(isEn ? "Operational indicator — not deducted from net profit" : "شاخص عملیاتی — در سود خالص کسر نشده")
            ])))
            .ToList();
        var filters = BuildReportExportFilters(filter).ToList();
        filters.AddRange(TabularExportSupport.FilterSummary(
            ("تاریخ تولید (کابل) / Generated (Kabul)", _businessClock.Today.ToString("yyyy-MM-dd")),
            ("اطمینان / Confidence", model.PnlConfidence.ToString()),
            ("فروش بدون بهای تمام‌شده / Sales missing COGS", model.UncostedSaleCount.ToString())));

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Company_PnL",
            TitleFa = "سود و زیان شرکت",
            TitleEn = "Company P&L",
            KnownRowCount = lines.Length + operationalRows.Count + model.TopContracts.Count,
            Filters = filters,
            Columns =
            [
                new("شرح", "Line", Width: 30),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 18),
                new("منبع", "Source", Width: 26)
            ],
            Rows = lines
                .Select(line => new TabularExportRow(
                [
                    TabularExportCell.Text(isEn ? line.En : line.Fa),
                    TabularExportCell.Number(line.Value),
                    TabularExportCell.Text(line.Value.HasValue
                        ? isEn ? "Company total" : "جمع شرکت"
                        : isEn ? model.ProfitUnavailableNoteEn : model.ProfitUnavailableNoteFa)
                ]))
                .Concat(operationalRows)
                .Concat(model.TopContracts.Select(contract => new TabularExportRow(
                [
                    TabularExportCell.Text(contract.ContractNumber),
                    TabularExportCell.Number(contract.GrossMarginUsd),
                    TabularExportCell.Text((isEn ? "Contract — " : "قرارداد — ") + contract.PnlConfidence)
                ]))),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? "Net profit / Total" : "سود خالص / جمع"),
                TabularExportCell.Number(profitPublishable ? model.NetProfitUsd : null),
                TabularExportCell.Text(profitPublishable
                    ? null
                    : isEn ? model.ProfitUnavailableNoteEn : model.ProfitUnavailableNoteFa)
            ])
        });
    }

    /// <summary>
    /// خروجی جریان نقدی. همان <c>BuildCashFlowReportAsync</c> صفحه را با همان فیلتر صدا می‌زند؛
    /// هیچ عدد نقدی اینجا دوباره محاسبه نمی‌شود. خروجی چهار بخش دارد — خلاصه، بابت، حساب و ماه —
    /// و چون بخش‌ها روی هم انباشته‌اند، سطرِ جمع فقط جمعِ بخشِ «بابت» است و در ستونِ بخش هم
    /// همین را می‌گوید تا کسی ستون را سرتاسر جمع نزند.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> CashFlowExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildCashFlowReportAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var isEn = UiText.IsEn(HttpContext);
        var summaryLabel = isEn ? "Summary" : "خلاصه";
        var groupLabel = isEn ? "Purpose" : "بابت";
        var accountLabel = isEn ? "Cash account" : "حساب نقد / بانک";
        var monthLabel = isEn ? "Month" : "ماه";

        var summaryRows = new List<TabularExportRow>
        {
            new(
            [
                TabularExportCell.Text(summaryLabel),
                TabularExportCell.Text(isEn ? "Period cash movement" : "گردش پول دوره"),
                TabularExportCell.Text(null), TabularExportCell.Number(model.OpeningBalanceUsd),
                TabularExportCell.Number(model.TotalInflowUsd), TabularExportCell.Number(model.TotalOutflowUsd),
                TabularExportCell.Number(model.NetCashFlowUsd), TabularExportCell.Number(model.ClosingBalanceUsd),
                TabularExportCell.Integer(null)
            ])
        };

        if (model.PartnerFundedCount > 0)
        {
            summaryRows.Add(new TabularExportRow(
            [
                TabularExportCell.Text(summaryLabel),
                TabularExportCell.Text(isEn
                    ? "Partner-funded (outside company cash, not in totals)"
                    : "پرداخت شریک (خارج از صندوق شرکت، بیرون از جمع‌ها)"),
                TabularExportCell.Text(null), TabularExportCell.Number(null),
                TabularExportCell.Number(null), TabularExportCell.Number(model.PartnerFundedOutflowUsd),
                TabularExportCell.Number(null), TabularExportCell.Number(null),
                TabularExportCell.Integer(model.PartnerFundedCount)
            ]));
        }

        var rows = summaryRows
            .Concat(model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(groupLabel), TabularExportCell.Text(r.GroupName), TabularExportCell.Text(null),
                TabularExportCell.Number(null),
                TabularExportCell.Number(r.InflowUsd), TabularExportCell.Number(r.OutflowUsd),
                TabularExportCell.Number(r.NetUsd), TabularExportCell.Number(null), TabularExportCell.Integer(r.Count)
            ])))
            .Concat(model.AccountRows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(accountLabel), TabularExportCell.Text(r.CashAccountName), TabularExportCell.Text(r.Currency),
                TabularExportCell.Number(r.OpeningUsd),
                TabularExportCell.Number(r.InflowUsd), TabularExportCell.Number(r.OutflowUsd),
                TabularExportCell.Number(r.NetUsd), TabularExportCell.Number(r.ClosingUsd), TabularExportCell.Integer(null)
            ])))
            .Concat(model.PeriodRows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(monthLabel), TabularExportCell.Text(r.Label), TabularExportCell.Text(null),
                TabularExportCell.Number(null),
                TabularExportCell.Number(r.InflowUsd), TabularExportCell.Number(r.OutflowUsd),
                TabularExportCell.Number(r.NetUsd), TabularExportCell.Number(r.ClosingUsd), TabularExportCell.Integer(r.Count)
            ])))
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Cash_Flow", TitleFa = "گردش پول", TitleEn = "Cash Movement",
            KnownRowCount = rows.Count, ForceLandscape = true, Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("بخش", "Section", Width: 16), new("عنوان", "Name", Width: 28), new("ارز حساب", "Account currency", Width: 12),
                new("اول دوره USD", "Opening USD", TabularExportValueType.Number, 16),
                new("ورودی USD", "Inflow USD", TabularExportValueType.Number, 16),
                new("خروجی USD", "Outflow USD", TabularExportValueType.Number, 16),
                new("خالص USD", "Net USD", TabularExportValueType.Number, 16),
                new("آخر دوره USD", "Closing USD", TabularExportValueType.Number, 16),
                new("تعداد", "Count", TabularExportValueType.Integer, 11)
            ],
            Rows = rows,
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? "Total of section: Purpose" : "جمع بخش «بابت»"),
                TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.OpeningBalanceUsd),
                TabularExportCell.Number(model.TotalInflowUsd), TabularExportCell.Number(model.TotalOutflowUsd),
                TabularExportCell.Number(model.NetCashFlowUsd), TabularExportCell.Number(model.ClosingBalanceUsd),
                TabularExportCell.Integer(model.Rows.Sum(r => (long)r.Count))
            ])
        });
    }

    /// <summary>
    /// خروجی موجودی و عملیات. همان <c>BuildInventoryOperationsReportAsync</c> صفحه را با همان
    /// فیلتر صدا می‌زند؛ سه بخش صفحه (جنس، ترمینال، اخطارها) در یک جدول با ستون «بخش» می‌آید.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> InventoryOperationsExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildInventoryOperationsReportAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var isEn = UiText.IsEn(HttpContext);
        var productLabel = isEn ? "Product" : "جنس";
        var terminalLabel = isEn ? "Terminal" : "ترمینال";
        var warningLabel = isEn ? "Warning" : "اخطار";

        var rows = model.ProductRows
            .Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(productLabel), TabularExportCell.Text(r.GroupName), TabularExportCell.Text(r.SecondaryName),
                TabularExportCell.Number(r.QuantityMt), TabularExportCell.Integer(r.MovementCount), TabularExportCell.Date(r.LastMovementDate)
            ]))
            .Concat(model.TerminalRows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(terminalLabel), TabularExportCell.Text(r.GroupName), TabularExportCell.Text(r.SecondaryName),
                TabularExportCell.Number(r.QuantityMt), TabularExportCell.Integer(r.MovementCount), TabularExportCell.Date(r.LastMovementDate)
            ])))
            .Concat(model.Warnings.Select(w => new TabularExportRow(
            [
                TabularExportCell.Text(warningLabel), TabularExportCell.Text(w.Title), TabularExportCell.Text(w.Description),
                TabularExportCell.Number(null), TabularExportCell.Integer(w.Count), TabularExportCell.Date(null)
            ])))
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Inventory_Operations", TitleFa = "موجودی و عملیات", TitleEn = "Inventory & Operations",
            KnownRowCount = rows.Count, ForceLandscape = true, Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("بخش", "Section", Width: 16), new("عنوان", "Name", Width: 26),
                new("شرح", "Detail", Width: 30, Wrap: true),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 16),
                new("تعداد", "Count", TabularExportValueType.Integer, 12),
                new("آخرین حرکت", "Last movement", TabularExportValueType.Date, 14)
            ],
            Rows = rows
        });
    }

    private static IReadOnlyList<TabularExportFilter> BuildReportExportFilters(ManagementReportFilterViewModel filter)
        => TabularExportSupport.FilterSummary(
            ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")), ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
            ("جنس / Product", filter.ProductId), ("قرارداد / Contract", filter.ContractId),
            ("مشتری / Customer", filter.CustomerId), ("تأمین‌کننده / Supplier", filter.SupplierId),
            ("ترمینال / Terminal", filter.TerminalId), ("مخزن / Tank", filter.StorageTankId),
            ("صندوق / Cash account", filter.CashAccountId), ("شرکت / Company", filter.CompanyId),
            ("بابت / Purpose", filter.PaymentKind.HasValue ? (int)filter.PaymentKind.Value : null));
}
