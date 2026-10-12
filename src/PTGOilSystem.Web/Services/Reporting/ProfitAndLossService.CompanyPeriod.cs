using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// عملکرد یک دوره در سطح شرکت. همهٔ گزارش‌های مدیریتی (بیلانس کلی، وضعیت مالی شرکت، سود امروزِ
/// موبایل) همین اعداد را نشان می‌دهند تا یک دوره در دو صفحه دو مفاد نداشته باشد.
/// </summary>
/// <param name="ContractCostedSaleCount">فروش‌هایی که بهایشان از بهای واحد قرارداد خرید آمده (برآورد میانگین وزنی، نه Pool ثبت‌شده).</param>
/// <param name="OperatingExpenseUsd">مصارف ثبت‌شدهٔ بر عهدهٔ شرکت داخل دوره، بدون نوع «تفاوت نرخ» (همان قاعدهٔ BuildCompanyAsync).</param>
/// <param name="LoadingPeriodExpenseUsd">گدام و سایرِ ثبت‌شده فقط روی بارگیری (بی‌سند) به تاریخ همان بارگیری.</param>
public sealed record CompanyPeriodPerformanceSnapshot(
    decimal SalesRevenueUsd,
    int SaleCount,
    decimal CostOfSalesUsd,
    int UncostedSaleCount,
    int ContractCostedSaleCount,
    decimal OperatingExpenseUsd,
    decimal LoadingPeriodExpenseUsd,
    decimal ExchangeGainUsd,
    decimal ExchangeLossUsd,
    decimal CustodyIncomeUsd = 0m)
{
    public decimal PeriodExpensesUsd => OperatingExpenseUsd + LoadingPeriodExpenseUsd;
    public decimal NetExchangeResultUsd => ExchangeGainUsd - ExchangeLossUsd;
    public int CostedSaleCount => SaleCount - UncostedSaleCount;

    /// <summary>فروش بی‌بها ⇒ نیازمند بررسی؛ بهای قراردادی ⇒ برآوردی؛ فقط Pool ثبت‌شده ⇒ قطعی.</summary>
    public PnlConfidence Confidence => UncostedSaleCount > 0
        ? PnlConfidence.NeedsReview
        : ContractCostedSaleCount > 0 ? PnlConfidence.Estimated : PnlConfidence.Verified;
    public decimal GrossProfitUsd => PnlMath.GrossProfit(SalesRevenueUsd, CostOfSalesUsd);
    /// <summary>
    /// کمیشن و حق خدماتِ امانی جزوِ مفاد است، نه جزوِ «فروش»: مبلغِ فروشِ امانی متعلق به مالک است و
    /// هرگز در <see cref="SalesRevenueUsd"/> نمی‌آید.
    /// </summary>
    public decimal NetProfitUsd => PnlMath.NetProfit(
        SalesRevenueUsd, CostOfSalesUsd, PeriodExpensesUsd, ExchangeGainUsd, ExchangeLossUsd) + CustodyIncomeUsd;
}

public sealed partial class ProfitAndLossService
{
    public async Task<CompanyPeriodPerformanceSnapshot> BuildCompanyPeriodAsync(
        ManagementReportFilterViewModel filter,
        DateTime asOfDate,
        PurchaseCostBasis? costBasis = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // فروش، مصارف ثبت‌شده و نتیجهٔ ارزی همان قاعده‌های BuildCompanyAsync است. بهای فروش همان قاعدهٔ
        // سطح فروش (ProfitAndLossService.SaleCost) است: بهای قرارداد تا تاریخ خودِ هر فروش، وگرنه Pool.
        var company = await BuildCompanyAsync(filter, ct);
        var saleIds = await CompanySalesQuery(filter).Select(s => s.Id).ToListAsync(ct);
        var saleCost = CostSales(saleIds, await LoadSaleCostInputsAsync(saleIds, ct));
        var costOfSalesUsd = saleCost.CostUsd;
        var contractCostedSaleCount = saleCost.ContractCostedCount;
        var uncostedSaleCount = saleCost.SaleCount - saleCost.ContractCostedCount - saleCost.PoolCostedCount;

        // پایهٔ بها تا «تا تاریخ» فقط برای گدام/سایرِ بی‌سندِ بارگیری‌های همین دوره لازم است.
        if (costBasis is null)
        {
            var purchaseIds = await _db.Contracts.AsNoTracking()
                .Where(c => c.ContractType == ContractType.Purchase)
                .Select(c => c.Id)
                .ToListAsync(ct);
            costBasis = purchaseIds.Count == 0
                ? PurchaseCostBasis.Empty
                : await BuildPurchaseCostBasisAsync(purchaseIds, (filter.ToDate ?? asOfDate).Date, ct);
        }

        var fromDate = filter.FromDate?.Date;
        var toExclusive = filter.ToDate?.Date.AddDays(1);
        var loadingPeriodExpenseUsd = costBasis.Loadings
            .Where(l => (!fromDate.HasValue || l.LoadingDate >= fromDate.Value)
                && (!toExclusive.HasValue || l.LoadingDate < toExclusive.Value)
                && (!filter.ContractId.HasValue || l.ContractId == filter.ContractId.Value))
            .Sum(l => l.PeriodExpenseUsd);

        var custodyFx = await CustodyExchangeAsync(filter, ct);
        return new CompanyPeriodPerformanceSnapshot(
            company.Sales.RevenueUsd,
            company.Sales.SaleCount,
            costOfSalesUsd,
            uncostedSaleCount,
            contractCostedSaleCount,
            company.OperatingExpenseUsd,
            loadingPeriodExpenseUsd,
            company.ExchangeGainUsd + custodyFx.GainUsd,
            company.ExchangeLossUsd + custodyFx.LossUsd,
            await CustodyIncomeAsync(filter, ct));
    }

    /// <summary>
    /// سهمِ شرکت از تفاوت نرخِ اسناد امانیِ دوارزی (طبق توافقِ مالک) — همان مبلغی که در ژورنال به
    /// حساب‌های سود/زیان تسعیر رفته؛ جزوِ نتیجهٔ ارزی است، نه کمیشن. سند لغوشده حساب نمی‌شود.
    /// </summary>
    private async Task<(decimal GainUsd, decimal LossUsd)> CustodyExchangeAsync(ManagementReportFilterViewModel filter, CancellationToken ct)
    {
        if (filter.ContractId.HasValue || filter.CustomerId.HasValue)
            return (0m, 0m);
        var entries = _db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.Status == CustodyDocumentStatus.Posted && e.FxCompanyUsd != null && e.FxCompanyUsd != 0m);
        if (filter.FromDate.HasValue) entries = entries.Where(e => e.EntryDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) entries = entries.Where(e => e.EntryDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ProductId.HasValue)
            entries = entries.Where(e => e.CustodyLot != null && e.CustodyLot.ProductId == filter.ProductId.Value);
        var gain = await entries.Where(e => e.FxCompanyUsd > 0m).SumAsync(e => e.FxCompanyUsd!.Value, ct);
        var loss = await entries.Where(e => e.FxCompanyUsd < 0m).SumAsync(e => -e.FxCompanyUsd!.Value, ct);
        return (gain, loss);
    }

    /// <summary>
    /// درآمدِ امانیِ دوره: کمیشنِ درآمدشده (هنگام فروش یا به تناسب وصول) و حق خدمات. فیلترِ قرارداد یا
    /// مشتری به اسناد امانی ربط ندارد، پس در آن حالت صفر است تا عددِ یک قرارداد/مشتری آلوده نشود.
    /// </summary>
    private async Task<decimal> CustodyIncomeAsync(ManagementReportFilterViewModel filter, CancellationToken ct)
    {
        if (filter.ContractId.HasValue || filter.CustomerId.HasValue)
            return 0m;

        var from = filter.FromDate?.Date;
        var toExclusive = filter.ToDate?.Date.AddDays(1);

        var sales = _db.CustodySales.AsNoTracking().Where(s => s.Status == CustodyDocumentStatus.Posted);
        if (from.HasValue) sales = sales.Where(s => s.SaleDate >= from.Value);
        if (toExclusive.HasValue) sales = sales.Where(s => s.SaleDate < toExclusive.Value);
        if (filter.ProductId.HasValue) sales = sales.Where(s => s.ProductId == filter.ProductId.Value);

        var entries = _db.CustodyFinanceEntries.AsNoTracking().Where(e => e.Status == CustodyDocumentStatus.Posted);
        if (from.HasValue) entries = entries.Where(e => e.EntryDate >= from.Value);
        if (toExclusive.HasValue) entries = entries.Where(e => e.EntryDate < toExclusive.Value);
        if (filter.ProductId.HasValue)
            entries = entries.Where(e => e.CustodyLot != null && e.CustodyLot.ProductId == filter.ProductId.Value);

        // برگشتِ فروش بخشی از کمیشنِ «هنگام فروش» را در تاریخِ خودِ برگشت معکوس می‌کند.
        var returns = _db.CustodySaleReturns.AsNoTracking().Where(r => r.Status == CustodyDocumentStatus.Posted);
        if (from.HasValue) returns = returns.Where(r => r.ReturnDate >= from.Value);
        if (toExclusive.HasValue) returns = returns.Where(r => r.ReturnDate < toExclusive.Value);
        if (filter.ProductId.HasValue) returns = returns.Where(r => r.CustodySale!.ProductId == filter.ProductId.Value);

        var commissionAtSale = await sales.SumAsync(s => s.CommissionRecognizedAtSaleUsd, ct);
        var commissionReversedByReturns = await returns.SumAsync(r => r.RecognizedCommissionReversedUsd, ct);
        var commissionOnCollection = await entries.SumAsync(e => e.CommissionUsd, ct);
        var serviceFees = await entries.Where(e => e.Kind == CustodyFinanceKind.ServiceFee).SumAsync(e => e.AmountUsd, ct);
        return commissionAtSale - commissionReversedByReturns + commissionOnCollection + serviceFees;
    }

    /// <summary>فروش‌های لغونشدهٔ فیلتر؛ تنها تعریفِ «فروش دوره» در سطح شرکت.</summary>
    private IQueryable<SalesTransaction> CompanySalesQuery(ManagementReportFilterViewModel filter)
    {
        var salesQuery = _db.SalesTransactions.AsNoTracking().Where(s => !s.IsCancelled);
        if (filter.FromDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ProductId.HasValue) salesQuery = salesQuery.Where(s => s.ProductId == filter.ProductId.Value);
        if (filter.ContractId.HasValue) salesQuery = salesQuery.Where(s => s.ContractId == filter.ContractId.Value);
        if (filter.CustomerId.HasValue) salesQuery = salesQuery.Where(s => s.CustomerId == filter.CustomerId.Value);
        return salesQuery;
    }
}
