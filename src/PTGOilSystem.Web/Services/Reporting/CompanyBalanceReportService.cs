using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services.Reporting;

/// <param name="EmployeeBalancesExcluded">کاربر اجازهٔ دیدن معاش ندارد و ردیف کارمند از مانده‌ها حذف شده است.</param>
public sealed record CompanyBalanceReportRequest(
    DateTime FromDate,
    DateTime ToDate,
    string CurrencyCode,
    DateTime GeneratedAt,
    bool EmployeeBalancesExcluded = false);

/// <summary>ارز درخواستی نرخ ثبت‌شده ندارد؛ گزارش به ارز پایه برمی‌گردد.</summary>
public sealed class CompanyBalanceCurrencyException(string messageFa) : Exception(messageFa);

public interface ICompanyBalanceReportService
{
    /// <param name="partyRows">
    /// ردیف‌های همان گزارش «طلبات و بدهی‌ها» تا «تا تاریخ» (مرجع: PartyBalanceReadService به‌همراه
    /// تعدیل تفاوت نرخ تأمین‌کننده). این سرویس ماندهٔ طرف‌حساب را دوباره حساب نمی‌کند؛ فقط
    /// بر اساس علامت ماندهٔ هر ردیف آن را در دارایی یا تعهد می‌نشاند. ردیف شریک اینجا خوانده نمی‌شود:
    /// آن ردیف موقعیتِ شراکت است (با تخصیص هزینه)، و ماندهٔ واقعیِ شریک از
    /// <see cref="PartnerCompanyBalanceReader"/> می‌آید.
    /// </param>
    Task<CompanyBalanceReportViewModel> BuildAsync(
        CompanyBalanceReportRequest request,
        IReadOnlyList<ReceivablePayableRowViewModel> partyRows,
        CancellationToken ct = default);
}

/// <summary>
/// «بیلانس کلی شرکت». دو محاسبهٔ مستقل:
/// <list type="bullet">
/// <item>بیلانس تا «تا تاریخ»: دارایی‌ها − تعهدات = خالص بیلانس.</item>
/// <item>عملکرد بین دو تاریخ: فروش − بهای فروش − مصارف ± نتیجهٔ ارزی = مفاد/ضرر.</item>
/// </list>
/// مفاد هرگز داخل دارایی/تعهد جمع نمی‌شود و مصارف دوره هم از دارایی کم نمی‌شود.
/// هیچ عددی اینجا از صفر ساخته نمی‌شود؛ هر بخش از مرجعِ موجود همان مفهوم خوانده می‌شود:
/// <list type="bullet">
/// <item>نقد و بانک: <see cref="CashPositionReader"/>.</item>
/// <item>طرف‌حساب‌ها: ردیف‌های «طلبات و بدهی‌ها» (PartyBalanceReadService)، جز شریک.</item>
/// <item>شرکا: <see cref="PartnerCompanyBalanceReader"/> — فقط رویدادهای مالی؛ شریکِ مالک دفتر بیرون.</item>
/// <item>مقدار موجودی: <see cref="IStockService.GetStockSummaryAsync"/>؛ مقدار بار در راه:
/// <see cref="GoodsInTransitQuantityReader"/> تا «تا تاریخ» (همان مرجع راپور بارهای در مسیر).</item>
/// <item>قیمت موجودی و بار در راه: پایهٔ بهای قرارداد خرید
/// (<see cref="IProfitAndLossService.BuildPurchaseCostBasisAsync"/>) — تصمیم کاربر، چون Pool
/// ارزش‌گذاری و دفتر حسابداری هنوز داده ندارند.</item>
/// <item>فروش، بهای فروش، مصارف و نتیجهٔ ارزی دوره: <see cref="IProfitAndLossService.BuildCompanyPeriodAsync"/>
/// با همان پایهٔ بها (موتور مشترک گزارش‌های مدیریتی).</item>
/// </list>
/// </summary>
public sealed class CompanyBalanceReportService(
    ApplicationDbContext db,
    IProfitAndLossService profitAndLoss,
    IStockService stock,
    IPricingService pricing,
    IPartnershipStatementService? partnerships = null) : ICompanyBalanceReportService
{
    private const decimal QuantityEpsilon = 0.0001m;

    public async Task<CompanyBalanceReportViewModel> BuildAsync(
        CompanyBalanceReportRequest request,
        IReadOnlyList<ReceivablePayableRowViewModel> partyRows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(partyRows);

        var fromDate = request.FromDate.Date;
        var toDate = request.ToDate.Date;
        var toExclusive = toDate.AddDays(1);
        var currency = SystemCurrency.Normalize(request.CurrencyCode);
        var (rate, rateDate) = await ResolveRateAsync(currency, toDate, ct);
        decimal Convert(decimal usd) => decimal.Round(usd * rate, 2, MidpointRounding.AwayFromZero);

        var notes = new List<string>();
        var details = new Dictionary<CompanyBalanceSection, List<CompanyBalanceDetailRowViewModel>>();
        List<CompanyBalanceDetailRowViewModel> Bucket(CompanyBalanceSection section)
        {
            if (!details.TryGetValue(section, out var rows))
            {
                rows = [];
                details[section] = rows;
            }

            return rows;
        }

        void Add(CompanyBalanceSection section, string name, string? secondary, decimal? quantityMt, decimal amountUsd,
            string? controller = null, string? action = null, int? id = null, bool isValued = true)
            => Bucket(section).Add(new CompanyBalanceDetailRowViewModel(
                name, secondary, quantityMt, amountUsd, Convert(amountUsd), controller, action, id, isValued));

        // ── نقد و بانک ──────────────────────────────────────────────────────
        var cashTotals = await new CashPositionReader(db).ReadAccountTotalsAsync(null, toExclusive, ct);
        var cashIds = cashTotals.Select(t => t.CashAccountId).ToArray();
        var cashNames = await db.CashAccounts.AsNoTracking()
            .Where(a => cashIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.AccountType })
            .ToDictionaryAsync(a => a.Id, ct);
        foreach (var total in cashTotals.OrderByDescending(t => t.BalanceUsd))
        {
            var account = cashNames.GetValueOrDefault(total.CashAccountId);
            var kind = account?.AccountType == CashAccountType.Cash ? "صندوق" : "بانک";
            Add(CompanyBalanceSection.CashAndBank,
                account?.Name ?? $"#{total.CashAccountId}",
                $"{kind} · {total.Balance:N2} {total.TotalsCurrency}",
                null,
                total.BalanceUsd,
                "CashAccounts", "Details", total.CashAccountId);
        }

        var missingCashUsd = CashPositionReader.TotalMissingUsdEquivalentCount(cashTotals);
        if (missingCashUsd > 0)
        {
            notes.Add($"{missingCashUsd:N0} سند ارزی صندوق/بانک معادل دالری ندارد و در ماندهٔ نقد صفر شمرده شده است.");
        }

        // ── امانی: فقط شفافیت؛ مبالغ همان مانده‌های طرف‌حساب‌اند و دوباره جمع نمی‌شوند ─────
        var custodyBalances = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.SourceType.StartsWith("Custody") && l.ServiceProviderId == null && l.EntryDate < toExclusive)
            .GroupBy(l => new { l.CustomerId, l.SupplierId })
            .Select(g => g.Sum(l => l.CustomerId.HasValue
                ? (l.Side == LedgerSide.Debit ? l.AmountUsd : -l.AmountUsd)
                : (l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd)))
            .ToListAsync(ct);
        var custodyPayableUsd = custodyBalances.Where(b => b > 0m).Sum();
        var custodyReceivableUsd = -custodyBalances.Where(b => b < 0m).Sum();
        if (custodyPayableUsd != 0m || custodyReceivableUsd != 0m)
        {
            notes.Add($"از مانده‌های طرف‌حساب، {custodyPayableUsd:N2} دالر وجوه امانیِ متعلق به مالکان (بدهی) و {custodyReceivableUsd:N2} دالر طلب شرکت از مالکان امانی است؛ تیلِ امانیِ دیگران در موجودی و دارایی شرکت شمرده نشده است. تفکیک هر مالک: «راپور دفتر کل امانی».");
        }

        // ── طرف‌حساب‌ها (فقط بر اساس علامت مانده) ────────────────────────────
        foreach (var row in partyRows
                     .Where(r => r.BalanceUsd != 0m && r.PartyType != nameof(PartyStatementPartyType.Partner))
                     .OrderByDescending(r => Math.Abs(r.BalanceUsd)))
        {
            var section = CompanyBalanceClassifier.Classify(row.PartyType, row.BalanceUsd);
            if (section is null)
            {
                continue;
            }

            Add(section.Value,
                row.PartyName,
                CompanyBalanceClassifier.PartyTypeLabel(row.PartyType),
                null,
                Math.Abs(row.BalanceUsd),
                row.DetailsController,
                row.DetailsController is null ? null : "Details",
                row.PartyId);
        }

        if (request.EmployeeBalancesExcluded)
        {
            notes.Add("ماندهٔ حساب کارمندان به دلیل نداشتن اجازهٔ دیدن معاش در این گزارش نیامده است.");
        }

        // ── شرکا: فقط رویدادهای مالیِ واقعی تا «تا تاریخ» ───────────────────
        // شریکِ مالک دفتر خودِ صاحب شرکت است؛ ماندهٔ او سرمایهٔ مالک است، نه طلب یا بدهی.
        var partnerBalances = await new PartnerCompanyBalanceReader(
                db,
                partnerships ?? new PartnershipStatementService(db, profitAndLoss: profitAndLoss))
            .ReadAsync(toDate, ct);
        foreach (var partner in partnerBalances
                     .Where(p => !p.IsBookOwner && p.BalanceUsd != 0m)
                     .OrderByDescending(p => Math.Abs(p.BalanceUsd)))
        {
            Add(CompanyBalanceClassifier.Classify(nameof(PartyStatementPartyType.Partner), partner.BalanceUsd)!.Value,
                partner.PartnerName,
                CompanyBalanceClassifier.PartyTypeLabel(nameof(PartyStatementPartyType.Partner)),
                null,
                Math.Abs(partner.BalanceUsd),
                "Partners", "Details", partner.PartnerId);
        }

        if (partnerBalances.Count > 0)
        {
            notes.Add("ماندهٔ شرکا فقط از رویدادهای مالی است (پرداخت شریک، تسویه، عاید فروش نزد شریک و سهم مفاد محقق)؛ سهم شریک از هزینهٔ کالای هنوز فروخته‌نشده تخصیص بین شرکاست و فقط در صورت‌حساب شراکت می‌آید.");
        }

        var ownerBalances = partnerBalances.Where(p => p.IsBookOwner && p.BalanceUsd != 0m).ToList();
        if (ownerBalances.Count > 0)
        {
            notes.Add($"ماندهٔ شریکِ مالک دفتر ({string.Join("، ", ownerBalances.Select(o => $"{o.PartnerName}: {o.BalanceUsd:N2} USD"))}) سرمایهٔ مالک است و در طلب یا بدهی شرکا نیامده است.");
        }

        // ── پایهٔ بهای قرارداد خرید ──────────────────────────────────────────
        var purchaseIds = await db.Contracts.AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase)
            .Select(c => c.Id)
            .ToListAsync(ct);
        // بهای واحد فقط از بارگیری‌های تا «تا تاریخ» ساخته می‌شود (بدون نگاه به آینده): میانگین وزنی
        // قیمت خرید + کرایهٔ مستقیمِ بی‌سند بر هر تن. همین پایه به موتور عملکرد دوره هم داده می‌شود.
        var costBasis = purchaseIds.Count == 0
            ? PurchaseCostBasis.Empty
            : await profitAndLoss.BuildPurchaseCostBasisAsync(purchaseIds, toDate, ct);
        decimal? PriceOf(int? contractId)
            => contractId.HasValue && costBasis.Contracts.TryGetValue(contractId.Value, out var basis)
                ? basis.UnitCostUsd
                : null;

        var unvaluedMt = 0m;

        void AddQuantity(CompanyBalanceSection section, string name, string? secondary, decimal quantityMt, int? contractId,
            string? controller = null, int? id = null)
        {
            var price = PriceOf(contractId);
            if (price is > 0m)
            {
                Add(section, name, secondary, quantityMt,
                    decimal.Round(quantityMt * price.Value, 2, MidpointRounding.AwayFromZero),
                    controller, controller is null ? null : "Details", id);
                return;
            }

            unvaluedMt += quantityMt;
            Add(section, name, secondary, quantityMt, 0m, controller, controller is null ? null : "Details", id, isValued: false);
        }

        // ── موجودی تانک‌ها تا «تا تاریخ» ─────────────────────────────────────
        // بار در راه هنوز حرکت ورودی تانک ندارد (رسید = ورود؛ حمل داخلی = خروج از مبدأ هنگام بارگیری)،
        // پس موجودی و بار در راه همپوشانی ندارند.
        var stockRows = await stock.GetStockSummaryAsync(
            asOfUtc: DateTime.SpecifyKind(toExclusive.AddTicks(-1), DateTimeKind.Utc),
            ct: ct);
        var negativeStockCount = 0;
        foreach (var item in stockRows.OrderBy(r => r.ProductName).ThenBy(r => r.TerminalName))
        {
            if (item.FreeQuantityMt < -QuantityEpsilon)
            {
                negativeStockCount++;
                continue;
            }

            if (item.FreeQuantityMt <= QuantityEpsilon)
            {
                continue;
            }

            AddQuantity(CompanyBalanceSection.InventoryValue,
                $"{item.ProductName} — {item.TerminalName}",
                item.ContractNumber,
                decimal.Round(item.FreeQuantityMt, 4, MidpointRounding.AwayFromZero),
                item.ContractId);
        }

        if (negativeStockCount > 0)
        {
            notes.Add($"{negativeStockCount:N0} دامنهٔ موجودی منفی است و در ارزش موجودی نیامده؛ گزارش «موجودی منفی» را ببینید.");
        }

        // ── بار در راه تا «تا تاریخ» ────────────────────────────────────────
        // هر ردیف دلیل بودنش در مسیر را نشان می‌دهد: «بدون رسید» یا «رسید جزئی» و چند روز از بارگیری.
        // مقدار هر سه نوع بار از همان مرجع راپور «بارهای در مسیر» می‌آید، تا «تا تاریخ».
        var transit = await new GoodsInTransitQuantityReader(db).ReadAsync(toDate, ct: ct);
        var originLoads = await LoadOriginLoadsInTransitAsync(transit, ct);
        foreach (var load in originLoads)
        {
            var age = Math.Max(0, (toDate - load.LoadingDate.Date).Days);
            var state = load.ReceivedMt > QuantityEpsilon
                ? $"رسید جزئی {load.ReceivedMt:N3} از {load.LoadedMt:N3} تن"
                : "بدون رسید";
            AddQuantity(CompanyBalanceSection.GoodsInTransit,
                $"بارگیری {load.Label}",
                JoinParts(load.ProductName, load.ContractNumber, state, $"{age:N0} روز"),
                load.RemainingMt,
                load.ContractId,
                "Loading", load.Id);
        }

        // بارِ بدون هیچ رسیدی که از آستانه قدیمی‌تر است. با دادهٔ موجود نمی‌توان گفت رسیده یا نه، پس از
        // دارایی حذف نمی‌شود؛ فقط برای بررسی علامت می‌خورد.
        var longTransit = originLoads
            .Where(l => l.ReceivedMt <= QuantityEpsilon
                && (toDate - l.LoadingDate.Date).Days > CompanyBalanceReportViewModel.LongTransitDays)
            .ToList();

        foreach (var leg in await LoadTransportLegsInTransitAsync(transit, ct))
        {
            AddQuantity(CompanyBalanceSection.GoodsInTransit,
                $"حمل داخلی #{leg.Id}",
                JoinParts(leg.ProductName, leg.ContractNumber),
                leg.RemainingMt,
                leg.ContractId,
                "InventoryTransportLegs", leg.Id);
        }

        foreach (var dispatch in await LoadUnsoldDispatchesInTransitAsync(transit, ct))
        {
            AddQuantity(CompanyBalanceSection.GoodsInTransit,
                $"موتر به مشتری #{dispatch.Id}",
                JoinParts(dispatch.ProductName, dispatch.ContractNumber),
                dispatch.QuantityMt,
                dispatch.ContractId,
                "Dispatch", dispatch.Id);
        }

        if (unvaluedMt > QuantityEpsilon)
        {
            notes.Add($"{unvaluedMt:N3} تن موجودی یا بار در راه قیمت خرید ثبت‌شده ندارد و در جمع دارایی‌ها نیامده است.");
        }

        var longTransitUsd = longTransit.Sum(l => PriceOf(l.ContractId) is { } unit
            ? decimal.Round(l.RemainingMt * unit, 2, MidpointRounding.AwayFromZero)
            : 0m);
        if (longTransit.Count > 0)
        {
            notes.Add($"{longTransit.Count:N0} بارگیری ({longTransit.Sum(l => l.RemainingMt):N3} تن) بیش از {CompanyBalanceReportViewModel.LongTransitDays:N0} روز بدون رسید ثبت‌شده در راه است و نیاز به بررسی دارد.");
        }

        // ── عملکرد دوره: فقط از موتور مشترک گزارش‌های مدیریتی ───────────────
        var period = await profitAndLoss.BuildCompanyPeriodAsync(
            new ManagementReportFilterViewModel { FromDate = fromDate, ToDate = toDate }, toDate, costBasis, ct);
        if (period.UncostedSaleCount > 0)
        {
            notes.Add($"{period.UncostedSaleCount:N0} فروش داخل دوره قیمت خرید منتسب ندارد؛ بهای تمام‌شدهٔ فروش برای آن‌ها صفر است.");
        }

        // همان فیلتر فروش BuildCompanyAsync (لغونشده، تاریخ فروش داخل دوره)؛ جمع ردیف‌ها = فروشات دوره.
        var periodSales = await db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.SaleDate >= fromDate && s.SaleDate < toExclusive)
            .OrderBy(s => s.SaleDate).ThenBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.SaleDate,
                s.QuantityMt,
                s.TotalUsd,
                BuyerName = s.Customer != null ? s.Customer.Name : s.Supplier != null ? s.Supplier.Name : null,
                ProductName = s.Product != null ? s.Product.Name : null
            })
            .ToListAsync(ct);
        foreach (var sale in periodSales)
        {
            Add(CompanyBalanceSection.SalesRevenue,
                JoinParts(sale.InvoiceNumber, sale.BuyerName) ?? $"#{sale.Id}",
                JoinParts(sale.ProductName, DateDisplay.Format(sale.SaleDate, "yyyy/MM/dd")),
                sale.QuantityMt,
                sale.TotalUsd,
                "Sales", "Details", sale.Id);
        }

        foreach (var expense in await LoadExpenseBreakdownAsync(fromDate, toExclusive, ct))
        {
            Add(CompanyBalanceSection.PeriodExpenses, expense.Name, null, null, expense.AmountUsd);
        }

        // گدام و سایرِ ثبت‌شده فقط روی بارگیری (بی‌سند): بهای کالا نیستند و هیچ سند مصرفی هم ندارند، پس
        // به تاریخ همان بارگیری مصرف دوره‌اند. هزینهٔ دارای سند از ردیف‌های بالا آمده و اینجا نمی‌آید.
        if (period.LoadingPeriodExpenseUsd != 0m)
        {
            Add(CompanyBalanceSection.PeriodExpenses, "گدام و سایر ثبت‌شده روی بارگیری (بدون سند مصرف)", null, null, period.LoadingPeriodExpenseUsd);
        }

        var costOfSalesUsd = period.CostOfSalesUsd;
        var periodExpensesUsd = period.PeriodExpensesUsd;
        var netExchangeUsd = period.NetExchangeResultUsd;

        // ── ردیف‌های اصلی ───────────────────────────────────────────────────
        CompanyBalanceLineViewModel Line(CompanyBalanceSection section, string fa, string en)
        {
            var usd = details.TryGetValue(section, out var rows) ? rows.Where(r => r.IsValued).Sum(r => r.AmountUsd) : 0m;
            return new CompanyBalanceLineViewModel(section, fa, en, usd, Convert(usd));
        }

        notes.Add("ارزش موجودی و بار در راه = مقدار × (میانگین وزنی قیمت خرید قرارداد تا تاریخ گزارش + کرایهٔ مستقیم بی‌سند بر هر تن).");
        if (!SystemCurrency.IsBaseCurrency(currency))
        {
            notes.Add($"نرخ تبدیل: 1 USD = {rate:N4} {currency}" + (rateDate.HasValue ? $" (نرخ {DateDisplay.Format(rateDate.Value, "yyyy/MM/dd")})" : ""));
        }

        return new CompanyBalanceReportViewModel
        {
            ReportFromDate = fromDate,
            ReportToDate = toDate,
            GeneratedAt = request.GeneratedAt,
            CurrencyCode = currency,
            UsdToReportRate = rate,
            RateEffectiveDate = rateDate,
            AssetLines =
            [
                Line(CompanyBalanceSection.CashAndBank, "موجودی نقد و بانک", "Cash and bank"),
                Line(CompanyBalanceSection.CustomerReceivables, "طلب از مشتریان", "Customer receivables"),
                Line(CompanyBalanceSection.InventoryValue, "موجودی مواد نفتی در تانک‌ها", "Inventory in tanks"),
                Line(CompanyBalanceSection.GoodsInTransit, "ارزش بارهای در راه", "Goods in transit"),
                Line(CompanyBalanceSection.Prepayments, "پیش‌پرداخت‌ها", "Prepayments"),
                Line(CompanyBalanceSection.OtherAssets, "سایر دارایی‌ها", "Other assets")
            ],
            LiabilityLines =
            [
                Line(CompanyBalanceSection.SupplierPayables, "بدهی به فروشندگان", "Supplier payables"),
                Line(CompanyBalanceSection.CustomerAdvances, "پیش‌دریافت از مشتریان", "Customer advances"),
                Line(CompanyBalanceSection.TransportPayables, "بدهی به شرکت‌های ترانسپورتی", "Transport payables"),
                Line(CompanyBalanceSection.PartnerPayables, "بدهی به شرکا", "Partner payables"),
                Line(CompanyBalanceSection.OtherLiabilities, "سایر تعهدات", "Other liabilities")
            ],
            SalesRevenueUsd = period.SalesRevenueUsd,
            CostOfSalesUsd = costOfSalesUsd,
            PeriodExpensesUsd = periodExpensesUsd,
            NetExchangeResultUsd = netExchangeUsd,
            SalesRevenue = Convert(period.SalesRevenueUsd),
            CostOfSales = Convert(costOfSalesUsd),
            PeriodExpenses = Convert(periodExpensesUsd),
            NetExchangeResult = Convert(netExchangeUsd),
            CustodyIncomeUsd = period.CustodyIncomeUsd,
            CustodyIncome = Convert(period.CustodyIncomeUsd),
            UncostedSaleCount = period.UncostedSaleCount,
            LongTransitLoadCount = longTransit.Count,
            LongTransitQuantityMt = longTransit.Sum(l => l.RemainingMt),
            LongTransitAmount = Convert(longTransitUsd),
            Details = details.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<CompanyBalanceDetailRowViewModel>)pair.Value),
            NotesFa = notes
        };
    }

    /// <summary>
    /// هر ۱ USD چند واحد ارز گزارش، از جدول نرخ روزانهٔ سیستم (IPricingService) تا «تا تاریخ».
    /// نرخ ثبت‌شده به هر دو جهت پذیرفته می‌شود؛ هیچ نرخی حدس زده نمی‌شود.
    /// </summary>
    private async Task<(decimal Rate, DateTime? EffectiveDate)> ResolveRateAsync(string currency, DateTime toDate, CancellationToken ct)
    {
        if (SystemCurrency.IsBaseCurrency(currency))
        {
            return (1m, null);
        }

        try
        {
            var direct = await pricing.GetFxRateAsync(SystemCurrency.BaseCurrencyCode, currency, toDate, ct);
            if (direct.Value > 0m)
            {
                return (direct.Value, direct.EffectiveDate);
            }
        }
        catch (BusinessRuleException)
        {
        }

        try
        {
            var inverse = await pricing.GetFxRateAsync(currency, SystemCurrency.BaseCurrencyCode, toDate, ct);
            if (inverse.Value > 0m)
            {
                return (FxRateMath.RoundRate(1m / inverse.Value), inverse.EffectiveDate);
            }
        }
        catch (BusinessRuleException)
        {
        }

        throw new CompanyBalanceCurrencyException(
            $"نرخ {SystemCurrency.BaseCurrencyCode}/{currency} تا تاریخ {DateDisplay.Format(toDate, "yyyy/MM/dd")} ثبت نشده است؛ گزارش به {SystemCurrency.BaseCurrencyCode} نمایش داده شد.");
    }

    /// <summary>
    /// تفکیک «مصارف عملیاتی» همان BuildCompanyAsync به نوع مصرف: لغو‌نشده، بر عهدهٔ شرکت،
    /// داخل بازه و بدون نوع «تفاوت نرخ» (که در نتیجهٔ ارزی می‌نشیند).
    /// </summary>
    private async Task<List<(string Name, decimal AmountUsd)>> LoadExpenseBreakdownAsync(
        DateTime fromDate,
        DateTime toExclusive,
        CancellationToken ct)
    {
        var fxTypeIds = await ProfitAndLossService.FxDifferenceExpenseTypeIds(db).ToListAsync(ct);
        var grouped = await db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled)
            .Where(CostResponsibilityPolicy.IsCompanyBorne)
            .Where(e => e.ExpenseDate >= fromDate && e.ExpenseDate < toExclusive && !fxTypeIds.Contains(e.ExpenseTypeId))
            .GroupBy(e => e.ExpenseTypeId)
            .Select(g => new { ExpenseTypeId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToListAsync(ct);
        var typeIds = grouped.Select(g => g.ExpenseTypeId).ToArray();
        var names = await db.ExpenseTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.NamePersian })
            .ToDictionaryAsync(t => t.Id, t => string.IsNullOrWhiteSpace(t.NamePersian) ? t.Name : t.NamePersian!, ct);

        return grouped
            .Where(g => g.AmountUsd != 0m)
            .OrderByDescending(g => g.AmountUsd)
            .Select(g => (names.GetValueOrDefault(g.ExpenseTypeId, $"#{g.ExpenseTypeId}"), g.AmountUsd))
            .ToList();
    }

    private sealed record TransitLoad(
        int Id,
        int ContractId,
        DateTime LoadingDate,
        string Label,
        string ProductName,
        string? ContractNumber,
        decimal LoadedMt,
        decimal ReceivedMt,
        decimal RemainingMt);

    private sealed record TransitLeg(int Id, int ContractId, string ProductName, string? ContractNumber, decimal RemainingMt);

    private sealed record TransitDispatch(int Id, int? ContractId, string ProductName, string? ContractNumber, decimal QuantityMt);

    /// <summary>
    /// بارگیری‌های در مسیر تا «تا تاریخ» از <see cref="GoodsInTransitQuantityReader"/>؛ اینجا فقط برچسب
    /// نمایش خوانده می‌شود و هیچ مقداری دوباره حساب نمی‌شود.
    /// </summary>
    private async Task<List<TransitLoad>> LoadOriginLoadsInTransitAsync(
        IReadOnlyList<GoodsInTransitQuantityRow> transit,
        CancellationToken ct)
    {
        var quantities = transit.Where(r => r.Kind == GoodsInTransitKind.FromOrigin).ToDictionary(r => r.SourceId);
        if (quantities.Count == 0)
        {
            return [];
        }

        var ids = quantities.Keys.ToArray();
        var loads = await db.LoadingRegisters.AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new
            {
                l.Id,
                l.ContractId,
                l.LoadingDate,
                ProductName = l.Product != null ? l.Product.Name : "",
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                VesselName = l.Vessel != null ? l.Vessel.Name : null,
                TruckPlate = l.Truck != null ? l.Truck.PlateNumber : null,
                l.WagonNumber,
                l.BillOfLadingNumber,
                l.RwbNo
            })
            .ToListAsync(ct);

        return loads
            .Select(l => new TransitLoad(
                l.Id,
                l.ContractId,
                l.LoadingDate,
                FirstText(l.VesselName, l.WagonNumber, l.TruckPlate, l.BillOfLadingNumber, l.RwbNo) ?? $"#{l.Id}",
                l.ProductName,
                l.ContractNumber,
                quantities[l.Id].LoadedMt,
                quantities[l.Id].ReceivedMt,
                quantities[l.Id].RemainingMt))
            .OrderBy(l => l.Id)
            .ToList();
    }

    /// <summary>حمل داخلی در مسیر تا «تا تاریخ» از <see cref="GoodsInTransitQuantityReader"/>.</summary>
    private async Task<List<TransitLeg>> LoadTransportLegsInTransitAsync(
        IReadOnlyList<GoodsInTransitQuantityRow> transit,
        CancellationToken ct)
    {
        var quantities = transit.Where(r => r.Kind == GoodsInTransitKind.InternalTransfer).ToDictionary(r => r.SourceId);
        if (quantities.Count == 0)
        {
            return [];
        }

        var ids = quantities.Keys.ToArray();
        return (await db.InventoryTransportLegs.AsNoTracking()
                .Where(l => ids.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    l.SourcePurchaseContractId,
                    ProductName = l.Product != null ? l.Product.Name : "",
                    ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null
                })
                .ToListAsync(ct))
            .OrderBy(l => l.Id)
            .Select(l => new TransitLeg(
                l.Id,
                l.SourcePurchaseContractId,
                l.ProductName,
                l.ContractNumber,
                quantities[l.Id].RemainingMt))
            .ToList();
    }

    /// <summary>
    /// موتری که به مشتری حرکت کرده و هنوز تخلیه نشده و فروشِ فعال ندارد. موجودی هنگام دیسپچ خارج
    /// شده، پس این مقدار فقط اینجا شمرده می‌شود؛ دیسپچی که فروش فعال دارد در «طلب از مشتری» آمده
    /// و دوباره دارایی شمرده نمی‌شود. مقدار از <see cref="GoodsInTransitQuantityReader"/> می‌آید.
    /// </summary>
    private async Task<List<TransitDispatch>> LoadUnsoldDispatchesInTransitAsync(
        IReadOnlyList<GoodsInTransitQuantityRow> transit,
        CancellationToken ct)
    {
        var quantities = transit
            .Where(r => r.Kind == GoodsInTransitKind.CustomerDelivery && !r.HasActiveSale)
            .ToDictionary(r => r.SourceId);
        if (quantities.Count == 0)
        {
            return [];
        }

        var ids = quantities.Keys.ToArray();
        return (await db.TruckDispatches.AsNoTracking()
                .Where(d => ids.Contains(d.Id))
                .Select(d => new
                {
                    d.Id,
                    d.ContractId,
                    ProductName = d.Product != null ? d.Product.Name : "",
                    ContractNumber = d.Contract != null ? d.Contract.ContractNumber : null
                })
                .ToListAsync(ct))
            .OrderBy(d => d.Id)
            .Select(d => new TransitDispatch(
                d.Id,
                d.ContractId,
                d.ProductName,
                d.ContractNumber,
                quantities[d.Id].RemainingMt))
            .ToList();
    }

    private static string? FirstText(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    private static string? JoinParts(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }
}

/// <summary>
/// جای هر ماندهٔ طرف‌حساب در بیلانس فقط از علامت خودِ مانده تعیین می‌شود، نه از نوع طرف‌حساب
/// به‌تنهایی: مشتریِ طلبکار تعهد است و تأمین‌کنندهٔ بدهکار دارایی. علامت همان قاعدهٔ صورت‌حسابِ
/// همان طرف‌حساب است: برای همه «مثبت = طلب شرکت»، جز شریک که «مثبت = شریک طلبکار» است
/// (PartyStatementPolicyResolver؛ همان <see cref="ReceivablePayableRowViewModel.CompanyClaimUsd"/>).
/// </summary>
public static class CompanyBalanceClassifier
{
    public static CompanyBalanceSection? Classify(string partyType, decimal balanceUsd)
    {
        if (balanceUsd == 0m || !Enum.TryParse<PartyStatementPartyType>(partyType, out var type))
        {
            return null;
        }

        var receivable = type == PartyStatementPartyType.Partner ? balanceUsd < 0m : balanceUsd > 0m;
        return type switch
        {
            PartyStatementPartyType.Company => null,
            PartyStatementPartyType.Customer => receivable
                ? CompanyBalanceSection.CustomerReceivables
                : CompanyBalanceSection.CustomerAdvances,
            PartyStatementPartyType.Supplier => receivable
                ? CompanyBalanceSection.Prepayments
                : CompanyBalanceSection.SupplierPayables,
            PartyStatementPartyType.ServiceProvider or PartyStatementPartyType.Driver => receivable
                ? CompanyBalanceSection.Prepayments
                : CompanyBalanceSection.TransportPayables,
            PartyStatementPartyType.Partner => receivable
                ? CompanyBalanceSection.OtherAssets
                : CompanyBalanceSection.PartnerPayables,
            _ => receivable
                ? CompanyBalanceSection.OtherAssets
                : CompanyBalanceSection.OtherLiabilities
        };
    }

    public static string PartyTypeLabel(string partyType) => partyType switch
    {
        nameof(PartyStatementPartyType.Customer) => "مشتری",
        nameof(PartyStatementPartyType.Supplier) => "فروشنده",
        nameof(PartyStatementPartyType.ServiceProvider) => "شرکت ترانسپورتی/خدماتی",
        nameof(PartyStatementPartyType.Driver) => "راننده",
        nameof(PartyStatementPartyType.Partner) => "شریک",
        nameof(PartyStatementPartyType.Sarraf) => "صراف",
        nameof(PartyStatementPartyType.Employee) => "کارمند",
        _ => partyType
    };
}
