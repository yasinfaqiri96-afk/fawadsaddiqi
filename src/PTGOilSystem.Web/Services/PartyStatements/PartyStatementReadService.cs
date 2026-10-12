using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.PartyStatements;

public sealed class PartyStatementReadService : IPartyStatementReadService
{
    private const string BaseCurrency = "USD";
    private const string ViaSarrafPayableLedgerSourceType = LedgerEntryOwnership.ViaSarrafPayableSourceType;
    private const CompanyFlowAccountKind AccountKind = CompanyFlowAccountKind.PartyAccount;
    private readonly ApplicationDbContext _db;
    private readonly IPartyStatementPolicyResolver _policyResolver;
    private readonly ICompanyFlowDirectionResolver _flowResolver;
    private readonly ICompanyFlowBalanceService _balanceService;
    private readonly PartyStatementOptions _options;
    private readonly IAfghanistanBusinessClock _businessClock;
    private readonly IPartyDirectory _parties;

    /// <summary>تنها منبعِ ماندهٔ شریک. این سرویس فرمول جداگانه‌ای برای شریک ندارد.</summary>
    private readonly IPartnershipStatementService _partnershipStatements;

    /// <summary>
    /// همان سرویسِ ثبت‌شده در DI با قواعدِ پیش‌فرض. هر جا سرویس تزریق نشده (ساخت دستی/تست)
    /// از همین استفاده می‌شود تا هیچ صفحه‌ای به فرمولِ خامِ Debit/Credit برنگردد.
    /// </summary>
    public static PartyStatementReadService CreateDefault(ApplicationDbContext db)
        => new(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            Options.Create(new PartyStatementOptions()),
            new PartyDirectory(db));

    public PartyStatementReadService(
        ApplicationDbContext db,
        IPartyStatementPolicyResolver policyResolver,
        ICompanyFlowDirectionResolver flowResolver,
        ICompanyFlowBalanceService balanceService,
        IOptions<PartyStatementOptions> options,
        IPartyDirectory parties,
        IAfghanistanBusinessClock? businessClock = null,
        IPartnershipStatementService? partnershipStatements = null)
    {
        _partnershipStatements = partnershipStatements ?? new PartnershipStatementService(db);
        _db = db;
        _policyResolver = policyResolver;
        _flowResolver = flowResolver;
        _balanceService = balanceService;
        _options = options.Value;
        _parties = parties;
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
    }

    /// <summary>
    /// نشاندنِ مبلغ در ستون درست («رسید» یا «برد») با جهتی که لایهٔ مرکزی تعیین کرده است.
    /// هیچ‌جای دیگری اجازه ندارد Debit/Credit را مستقیم به ستون نمایش تبدیل کند.
    /// </summary>
    private void ApplyFlow(PartyStatementRow row, in CompanyFlowEvent flowEvent, decimal amountUsd)
    {
        var direction = _flowResolver.Resolve(flowEvent);
        row.FlowDirection = direction;
        row.IsReversalRow = flowEvent.Lifecycle == CompanyFlowLifecycle.Reversal;
        row.IsCancelled = flowEvent.Lifecycle == CompanyFlowLifecycle.Cancelled;

        var amount = Math.Abs(amountUsd);
        if (direction == CompanyFlowDirection.Receipt)
        {
            row.ReceiptBase = amount;
            row.OutflowBase = null;
        }
        else
        {
            row.OutflowBase = amount;
            row.ReceiptBase = null;
        }
    }

    /// <summary>
    /// چرخهٔ عمر سطر. مرجع هم خوانده می‌شود چون برگشتِ بارگیری/فروش/مصرف عمداً SourceType
    /// سند اصلی را نگه می‌دارد و فقط با پسوندِ مرجع علامت می‌خورد؛ بدون آن، سطرِ برگشت
    /// «اصلی» خوانده می‌شد و به‌جای صفرکردن، اثر سند را دو برابر می‌کرد.
    /// </summary>
    private static CompanyFlowLifecycle LifecycleOf(string? sourceType, string? reference)
        => CompanyFlowSourceTypes.IsReversal(sourceType, reference)
            ? CompanyFlowLifecycle.Reversal
            : CompanyFlowLifecycle.Original;

    public async Task<PartyStatementResult> GetStatementAsync(
        PartyRef party,
        PartyStatementFilter filter,
        CancellationToken cancellationToken = default)
    {
        ValidateFilter(filter);
        var policy = _policyResolver.Resolve(party.PartyType);
        var partyInfo = await LoadPartyInfoAsync(party, cancellationToken)
            ?? throw new KeyNotFoundException("طرف‌حساب موردنظر پیدا نشد.");

        var calculation = party.PartyType switch
        {
            PartyStatementPartyType.Partner => await BuildPartnerRowsAsync(party, filter, policy, cancellationToken),
            PartyStatementPartyType.Employee => await BuildEmployeeRowsAsync(party, filter, cancellationToken),
            PartyStatementPartyType.Sarraf => await BuildSarrafRowsAsync(party, filter, cancellationToken),
            _ => await BuildLedgerRowsAsync(party, filter, policy, cancellationToken)
        };

        if (filter.IncludeOperationalColumns && policy.SupportsOperationalColumns)
        {
            await AddOperationalColumnsAsync(calculation.PeriodRows, cancellationToken);
        }

        // شرح تجارتیِ هر سطر از سند اصلی (جنس، مقدار × نرخ، موتر، طرف، فاکتور/رسید). فقط نمایشی است.
        await new PartyStatementNarrativeBuilder(_db)
            .ApplyAsync(party, partyInfo.Name, calculation.PeriodRows, cancellationToken);

        // نمایش روبلی: کاربر ارز روبل را انتخاب کرده تا مانده و جمع‌های روبلیِ واقعی
        // (به نرخ تاریخی هر سند) نمایش داده شوند. مقادیر USD دست‌نخورده باقی می‌مانند.
        var presentInRub = IsRubPresentation(filter);
        foreach (var row in calculation.PeriodRows)
        {
            ApplyRubValues(row);
        }

        var resultRows = BuildRunningRows(calculation.OpeningBalance, calculation.PeriodRows, filter.FromDate);
        // جمع‌ها از لایهٔ مرکزی می‌آیند: بیلانس = اول دوره + Σبرد − Σرسید.
        var summary = _balanceService.Summarize(
            calculation.OpeningBalance,
            calculation.PeriodRows.Select(r => new CompanyFlowAmount(
                r.FlowDirection ?? CompanyFlowDirection.Outflow,
                r.ReceiptBase ?? r.OutflowBase ?? 0m)),
            AccountKind);
        var totalReceipt = summary.TotalReceipt;
        var totalOutflow = summary.TotalOutflow;
        var closing = summary.ClosingBalance;

        // جمع‌ها و بیلانس جاری روبلی — فقط از اسناد روبلی؛ اسناد غیرروبلی ارزش روبلی
        // ندارند و در این محاسبه شرکت نمی‌کنند (در سطر «—» نمایش داده می‌شوند).
        decimal? openingRub = null, totalReceiptRub = null, totalOutflowRub = null, closingRub = null;
        if (presentInRub)
        {
            openingRub = calculation.OpeningBalanceRub;
            totalReceiptRub = calculation.PeriodRows.Sum(r => r.ReceiptRub ?? 0m);
            totalOutflowRub = calculation.PeriodRows.Sum(r => r.OutflowRub ?? 0m);
            var runningRub = openingRub ?? 0m;
            var runningRubKnown = openingRub.HasValue;
            foreach (var row in resultRows)
            {
                if (row.IsOpeningBalance)
                {
                    row.RunningBalanceRub = openingRub;
                    continue;
                }
                if (runningRubKnown && row.SignedAmountRub.HasValue)
                {
                    runningRub += row.SignedAmountRub.Value;
                    row.RunningBalanceRub = runningRub;
                }
            }
            closingRub = openingRub.HasValue
                ? _balanceService.Close(openingRub.Value, totalReceiptRub.Value, totalOutflowRub.Value, AccountKind)
                : null;
        }

        var columnTotals = SummarizeColumns(party.PartyType, calculation.PeriodRows, presentInRub);

        var companyInfo = await LoadCompanyInfoAsync(party, filter.ContractId, cancellationToken);
        var periodRows = resultRows.Where(r => !r.IsOpeningBalance).ToList();
        var periodFrom = filter.FromDate?.Date ?? periodRows.FirstOrDefault()?.Date.Date;
        var periodTo = filter.ToDate?.Date ?? periodRows.LastOrDefault()?.Date.Date ?? _businessClock.Today;
        var displayCurrency = presentInRub ? "RUB" : NormalizeCurrency(_options.BaseCurrencyCode);

        return new PartyStatementResult
        {
            Party = party,
            Policy = policy,
            CompanyInfo = companyInfo,
            PartyInfo = partyInfo,
            DocumentInfo = new PartyStatementDocumentInfo
            {
                StatementNumber = BuildStatementNumber(party, periodTo),
                StatementDate = _businessClock.Today,
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                BaseCurrencyCode = displayCurrency,
                GeneratedAtUtc = DateTime.UtcNow
            },
            Summary = new PartyStatementSummary
            {
                OpeningBalance = calculation.OpeningBalance,
                TotalReceipt = totalReceipt,
                TotalOutflow = totalOutflow,
                ClosingBalance = closing,
                ClosingBalanceMeaning = policy.BalanceMeaning(closing, isEnglish: false),
                ClosingBalanceMeaningEn = policy.BalanceMeaning(closing, isEnglish: true),
                BaseCurrencyCode = displayCurrency,
                IsRubPresentation = presentInRub,
                OpeningBalanceRub = openingRub,
                TotalReceiptRub = totalReceiptRub,
                TotalOutflowRub = totalOutflowRub,
                ClosingBalanceRub = closingRub,
                ClosingBalanceRubMeaning = closingRub.HasValue
                    ? policy.BalanceMeaning(closingRub.Value, isEnglish: false)
                    : string.Empty,
                ClosingBalanceRubMeaningEn = closingRub.HasValue
                    ? policy.BalanceMeaning(closingRub.Value, isEnglish: true)
                    : string.Empty,
                TotalTrade = columnTotals.Trade,
                TotalReceived = columnTotals.Received,
                TotalPaid = columnTotals.Paid,
                TotalTradeRub = columnTotals.TradeRub,
                TotalReceivedRub = columnTotals.ReceivedRub,
                TotalPaidRub = columnTotals.PaidRub,
                TradeQuantity = columnTotals.Quantity,
                TradeQuantityUnit = columnTotals.QuantityUnit
            },
            ColumnOptions = ResolveColumns(periodRows, filter),
            Rows = resultRows,
            Note = _options.Note,
            CourtesyText = _options.CourtesyText,
            Authorization = new PartyStatementAuthorization
            {
                AuthorizedByName = _options.AuthorizedByName,
                AuthorizedByTitle = _options.AuthorizedByTitle,
                SignatureImagePath = _options.SignatureImagePath
            }
        };
    }

    private async Task<StatementCalculation> BuildLedgerRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        PartyStatementPolicy policy,
        CancellationToken ct)
    {
        // سطرهای منسوخ/برگشتیِ تسویهٔ صراف (ثبت اصلی که با repost جایگزین شده و سطرِ برگشت)
        // نباید در صورت‌حساب رسمی جدا نمایش داده یا در جمع‌ها دوبار شمرده شوند؛ فقط اثرِ جاری
        // (سطری که تسویهٔ Posted به آن اشاره می‌کند) می‌ماند. رجوع: SarrafSettlementLedgerEffectiveness.
        var baseQuery = BuildPartyLedgerQuery(party, filter)
            .WhereEffectiveSarrafSettlementLegs(_db);

        // بیلانس اول دوره دیگر با یک SUM از روی Side محاسبه نمی‌شود: جهت هر سند از لایهٔ
        // مرکزی می‌آید و ممکن است با سمت حسابداری‌اش یکی نباشد (مثلاً «مصرف»). بنابراین
        // سطرهای پیش از دوره هم از همان مسیرِ نگاشت عبور می‌کنند و بعد جدا می‌شوند.
        var entries = await baseQuery
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.CreatedAtUtc)
            .ThenBy(l => l.Id)
            .Select(l => new LedgerStatementProjection
            {
                Id = l.Id,
                Date = l.EntryDate,
                CreatedAtUtc = l.CreatedAtUtc,
                Side = l.Side,
                AmountUsd = l.AmountUsd,
                OriginalAmount = l.SourceCurrencyCode == null || l.SourceCurrencyCode == "USD"
                    ? l.SourceAmount ?? l.AmountUsd
                    : l.SourceAmount,
                OriginalCurrency = l.SourceCurrencyCode ?? l.Currency,
                FxRateToUsd = l.AppliedFxRateToUsd,
                Reference = l.Reference,
                Description = l.Description,
                SourceType = l.SourceType,
                SourceId = l.SourceId,
                ContractId = l.ContractId,
                ShipmentId = l.ShipmentId,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null
            })
            .ToListAsync(ct);

        // نمای روبلی: پای مقصدِ «انتقال مانده» با ارز قرارداد ثبت شده (اغلب USD)، پس مبلغ
        // روبلی از خودِ سطر دفتر بازیابی نمی‌شود و ستون روبل خالی می‌ماند. مبلغ اصلی هر دو
        // پای انتقال مستقیماً از سند SupplierBalanceTransfer خوانده می‌شود تا همان مقداری که
        // از پیش‌پرداخت آزاد کم شده، روی قرارداد مقصد هم دیده شود. AmountUsd و جهت سند
        // دست‌نخورده می‌ماند؛ فقط «مبلغ اصلی» سطر کامل می‌شود.
        if (IsRubPresentation(filter))
        {
            await ApplyBalanceTransferOriginalAmountsAsync(entries, ct);
        }

        var allRows = entries.Select(e => MapLedgerRow(e, policy)).ToList();
        if (party.PartyType == PartyStatementPartyType.Customer && !filter.ContractId.HasValue && !filter.CompanyId.HasValue)
            allRows.AddRange(await BuildCustodyCustomerRowsAsync(party.PartyId, filter, ct));

        // سند فروش همیشه ContractId را روی خودِ سطر دفتر ندارد؛ قرارداد از خودِ فاکتور
        // فروش خوانده می‌شود تا در نمای «قراردادها» زیر گروهِ اشتباه یا «بدون قرارداد»
        // ننشیند. فقط برچسبِ قرارداد کامل می‌شود؛ هیچ مبلغ، جهت یا مانده‌ای تغییر نمی‌کند.
        await ResolveSaleContractLabelsAsync(entries, allRows, ct);

        // یک خدمتِ ثبت‌شده روی یک محموله، هنگام ثبت به‌تناسبِ هر قرارداد تقسیم می‌شود و
        // چند سند مصرف می‌سازد. در حساب شرکت خدماتی طرفِ واقعی یکی است و باید یک سطر با
        // مبلغ کل دیده شود، نه سهمِ هر قرارداد. تقسیم در Ledger دست‌نخورده می‌ماند.
        if (party.PartyType == PartyStatementPartyType.ServiceProvider)
        {
            // برچسب «نوع مصرف» فقط برای گروه‌بندی نمایشیِ نمای خلاصه خوانده می‌شود و باید
            // پیش از ادغام ست شود تا سطرهای هم‌ردیفِ entries از دست نروند.
            await ApplyExpenseCategoryLabelsAsync(entries, allRows, ct);
            allRows = await MergeShipmentExpenseSharesAsync(entries, allRows, ct);
        }

        return SplitAtPeriodStart(allRows, filter.FromDate);
    }

    /// <summary>
    /// گردش امانی مشتری در همان صورت‌حساب و خروجی‌ها دیده می‌شود، با شرح مبلغ و مالک.
    /// ستون‌های Receipt/Outflow تهی می‌مانند: این طلبِ مالک است و مانده ملکی شرکت نیست.
    /// </summary>
    private async Task<List<PartyStatementRow>> BuildCustodyCustomerRowsAsync(int customerId, PartyStatementFilter filter, CancellationToken ct)
    {
        var salesQuery = _db.CustodySales.AsNoTracking().Where(s => s.CustomerId == customerId);
        var financeQuery = _db.CustodyFinanceEntries.AsNoTracking().Where(e => e.CustomerId == customerId
            && (e.Kind == CustodyFinanceKind.Collection || e.Kind == CustodyFinanceKind.DirectToOwner
                || e.Kind == CustodyFinanceKind.CustomerRefund || e.Kind == CustodyFinanceKind.OwnerDirectRefund));
        var returnsQuery = _db.CustodySaleReturns.AsNoTracking().Where(r => r.CustomerId == customerId);
        if (filter.ToDate.HasValue)
        {
            var toExclusive = DateTime.SpecifyKind(filter.ToDate.Value.Date.AddDays(1), DateTimeKind.Utc);
            salesQuery = salesQuery.Where(s => s.SaleDate < toExclusive);
            financeQuery = financeQuery.Where(e => e.EntryDate < toExclusive);
            returnsQuery = returnsQuery.Where(r => r.ReturnDate < toExclusive);
        }
        var sales = await salesQuery.OrderBy(s => s.Id).ToListAsync(ct);
        var finance = await financeQuery.OrderBy(e => e.Id).ToListAsync(ct);
        var returns = await returnsQuery.OrderBy(r => r.Id).ToListAsync(ct);
        var keys = sales.Select(s => new PartyKey(s.OwnerPartyType == AccountingPartyType.Customer
            ? PartyStatementPartyType.Customer : PartyStatementPartyType.Supplier, s.OwnerPartyId)).Distinct().ToArray();
        var names = await _parties.GetNamesAsync(keys, ct);
        var rows = new List<PartyStatementRow>();
        foreach (var sale in sales)
        {
            var ownerKey = new PartyKey(sale.OwnerPartyType == AccountingPartyType.Customer ? PartyStatementPartyType.Customer : PartyStatementPartyType.Supplier, sale.OwnerPartyId);
            var paid = finance.Where(e => e.CustodySaleId == sale.Id && e.Status == CustodyDocumentStatus.Posted)
                    .Sum(e => e.Kind is CustodyFinanceKind.CustomerRefund or CustodyFinanceKind.OwnerDirectRefund ? -e.AmountUsd : e.AmountUsd)
                + returns.Where(r => r.CustodySaleId == sale.Id && r.Status == CustodyDocumentStatus.Posted).Sum(r => r.AmountUsd);
            rows.Add(new PartyStatementRow
            {
                Date = sale.SaleDate, CreatedAtUtc = sale.CreatedAtUtc, SourceType = "CustodyCustomerSale", SourceId = sale.Id,
                Reference = sale.SaleNumber, OriginalCurrency = sale.Currency,
                Title = sale.Status == CustodyDocumentStatus.Cancelled ? "فروش امانی لغو شده" : "فروش امانی به نمایندگی مالک",
                Description = $"فروش امانی {sale.SaleNumber} · مالک: {names.GetValueOrDefault(ownerKey, "—")} · {sale.QuantityMt:N4} تُن × {sale.UnitPriceUsd:N4} دالر · مبلغ {sale.AmountUsd:N2} دالر · وصول‌نشده {(sale.Status == CustodyDocumentStatus.Cancelled ? 0m : sale.AmountUsd - paid):N2} دالر؛ این طلب مربوط به مالک است.",
                Detail = $"مبلغ {sale.AmountUsd:N2} دالر · وصول‌نشده {(sale.Status == CustodyDocumentStatus.Cancelled ? 0m : sale.AmountUsd - paid):N2} دالر · مالک: {names.GetValueOrDefault(ownerKey, "—")}",
                IsCancelled = sale.Status == CustodyDocumentStatus.Cancelled
            });
        }
        foreach (var entry in finance)
        {
            var reference = sales.FirstOrDefault(s => s.Id == entry.CustodySaleId)?.SaleNumber ?? $"AF-{entry.Id:D5}";
            var title = entry.Kind switch
            {
                CustodyFinanceKind.DirectToOwner => "پرداخت مستقیم مشتری به مالک",
                CustodyFinanceKind.CustomerRefund => "استرداد پول فروش امانی به مشتری",
                CustodyFinanceKind.OwnerDirectRefund => "استرداد مستقیم مالک به مشتری",
                _ => "دریافت مشتری برای مالک امانی"
            };
            if (entry.Status == CustodyDocumentStatus.Cancelled) title += " — لغو شده";
            rows.Add(new PartyStatementRow
            {
                Date = entry.EntryDate, CreatedAtUtc = entry.CreatedAtUtc, SourceType = "CustodyCustomerFinance", SourceId = entry.Id,
                Reference = reference, OriginalCurrency = entry.Currency, Title = title,
                Description = $"{title} · {reference} · {entry.AmountUsd:N2} دالر؛ مربوط به حساب امانی مالک.",
                Detail = $"{entry.AmountUsd:N2} دالر · فروش {reference}", IsCancelled = entry.Status == CustodyDocumentStatus.Cancelled
            });
        }
        foreach (var saleReturn in returns)
        {
            var reference = sales.FirstOrDefault(s => s.Id == saleReturn.CustodySaleId)?.SaleNumber ?? saleReturn.ReturnNumber;
            var title = saleReturn.Status == CustodyDocumentStatus.Cancelled ? "برگشت فروش امانی — لغو شده" : "برگشت فروش امانی";
            rows.Add(new PartyStatementRow
            {
                Date = saleReturn.ReturnDate, CreatedAtUtc = saleReturn.CreatedAtUtc, SourceType = "CustodyCustomerReturn", SourceId = saleReturn.Id,
                Reference = saleReturn.ReturnNumber, OriginalCurrency = "USD", Title = title,
                Description = $"{title} {saleReturn.ReturnNumber} · فروش {reference} · {saleReturn.QuantityMt:N4} تُن · {saleReturn.AmountUsd:N2} دالر از طلب کم شد؛ مربوط به حساب امانی مالک.",
                Detail = $"{saleReturn.QuantityMt:N4} تُن · {saleReturn.AmountUsd:N2} دالر · فروش {reference}",
                IsCancelled = saleReturn.Status == CustodyDocumentStatus.Cancelled
            });
        }
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
            rows = rows.Where(r => r.SourceType == filter.SourceType).ToList();
        if (!string.IsNullOrWhiteSpace(filter.Search))
            rows = rows.Where(r => r.Description.Contains(filter.Search.Trim(), StringComparison.OrdinalIgnoreCase)
                || (r.Reference?.Contains(filter.Search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        return rows;
    }

    private const string SaleSourceType = "Sale";

    // نگاشت «سند فروش → قرارداد» فقط برای سطرهایی که در دفتر قرارداد ندارند. یک کوئری
    // projection (بدون N+1) و کاملاً نمایشی است.
    private async Task ResolveSaleContractLabelsAsync(
        List<LedgerStatementProjection> entries,
        List<PartyStatementRow> rows,
        CancellationToken ct)
    {
        var saleIds = entries
            .Where(e => e.SourceType == SaleSourceType && !e.ContractId.HasValue)
            .Select(e => e.SourceId)
            .Distinct()
            .ToList();
        if (saleIds.Count == 0)
        {
            return;
        }

        var saleContracts = await _db.SalesTransactions
            .AsNoTracking()
            .Where(x => saleIds.Contains(x.Id) && x.ContractId.HasValue)
            .Select(x => new
            {
                x.Id,
                ContractId = x.ContractId!.Value,
                ContractNumber = x.Contract != null ? x.Contract.ContractNumber : null
            })
            .ToListAsync(ct);
        if (saleContracts.Count == 0)
        {
            return;
        }

        var map = saleContracts.ToDictionary(x => x.Id);
        for (var i = 0; i < rows.Count; i++)
        {
            var entry = entries[i];
            if (entry.SourceType != SaleSourceType
                || entry.ContractId.HasValue
                || !map.TryGetValue(entry.SourceId, out var sale))
            {
                continue;
            }

            rows[i].ContractId = sale.ContractId;
            rows[i].ContractNumber ??= sale.ContractNumber;
        }
    }

    private const string ExpenseSourceType = "Expense";

    // دو پای «انتقال مانده» و برگشت آن. سطر تفاوت نرخ عمداً اینجا نیست: آن یک اثر
    // دالریِ سود/زیان است و مبلغ اصلی روبلی ندارد.
    private static readonly string[] BalanceTransferSourceTypes =
    [
        SupplierBalanceTransferService.LedgerSourceType,
        SupplierBalanceTransferService.ReversalLedgerSourceType
    ];

    /// <summary>
    /// مبلغ اصلی سطرهای «انتقال مانده» را از خودِ سند می‌خواند (نه از ستون SourceAmount دفتر).
    /// مقدار مرجع <see cref="SupplierBalanceTransfer.TransferOriginalAmount"/> است — همان
    /// مقداری که از مانده قابل انتقال کم شده؛ جمعِ <see cref="SupplierBalanceTransferSource"/>
    /// فقط وقتی به‌کار می‌رود که سند مقدار مستقیم نداشته باشد (داده ناقص قدیمی).
    /// هیچ مبلغ دالری، جهت یا سطر دفتری تغییر نمی‌کند.
    /// </summary>
    private async Task ApplyBalanceTransferOriginalAmountsAsync(
        List<LedgerStatementProjection> entries,
        CancellationToken ct)
    {
        var transferIds = entries
            .Where(e => Array.IndexOf(BalanceTransferSourceTypes, e.SourceType) >= 0 && e.SourceId > 0)
            .Select(e => e.SourceId)
            .Distinct()
            .ToList();
        if (transferIds.Count == 0)
        {
            return;
        }

        var transfers = await _db.SupplierBalanceTransfers
            .AsNoTracking()
            .Where(t => transferIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.OriginalCurrencyCode,
                t.TransferOriginalAmount,
                ConsumedOriginalAmount = t.Sources
                    .Where(s => s.OriginalCurrencyCode == t.OriginalCurrencyCode)
                    .Sum(s => (decimal?)s.ConsumedOriginalAmount)
            })
            .ToDictionaryAsync(t => t.Id, ct);

        foreach (var entry in entries)
        {
            if (Array.IndexOf(BalanceTransferSourceTypes, entry.SourceType) < 0
                || !transfers.TryGetValue(entry.SourceId, out var transfer))
            {
                continue;
            }

            var original = transfer.TransferOriginalAmount > 0m
                ? transfer.TransferOriginalAmount
                : transfer.ConsumedOriginalAmount ?? 0m;
            if (original <= 0m)
            {
                continue;
            }

            entry.OriginalAmount = original;
            entry.OriginalCurrency = NormalizeCurrency(transfer.OriginalCurrencyCode);
            // نرخ نمایشی از خودِ همین سطر ساخته می‌شود تا «مبلغ اصلی × نرخ = مبلغ دالری»
            // برای هر دو پا صادق بماند: پای منبع نرخ تاریخی و پای مقصد نرخ روز انتقال.
            entry.FxRateToUsd = FxRateMath.RoundRate(entry.AmountUsd / original);
        }
    }

    /// <summary>
    /// خواندن نام نوع مصرف برای سطرهای مصرفِ شرکت خدماتی. فقط برچسب نمایشی است و در
    /// نمای «خلاصه» کلید گروه‌بندی می‌شود؛ هیچ مبلغ، جهت یا مانده‌ای تغییر نمی‌کند.
    /// یک کوئری projection (بدون N+1).
    /// </summary>
    private async Task ApplyExpenseCategoryLabelsAsync(
        List<LedgerStatementProjection> entries,
        List<PartyStatementRow> rows,
        CancellationToken ct)
    {
        var expenseIds = entries
            .Where(e => e.SourceType == ExpenseSourceType && e.SourceId > 0)
            .Select(e => e.SourceId)
            .Distinct()
            .ToList();
        if (expenseIds.Count == 0)
        {
            return;
        }

        var names = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => expenseIds.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                Name = e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : null
            })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        for (var i = 0; i < rows.Count; i++)
        {
            var entry = entries[i];
            if (entry.SourceType != ExpenseSourceType
                || !names.TryGetValue(entry.SourceId, out var name)
                || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            rows[i].CategoryLabel = name;
        }
    }

    /// <summary>
    /// ادغامِ صرفاً نمایشیِ سهم‌های یک خدمت روی یک محموله. کلید ادغام = (محموله، نوع مصرف،
    /// تاریخ، ارز، جهت). جمعِ سهم‌ها دقیقاً همان مبلغ کل است، پس جمع‌ها و بیلانس تغییر نمی‌کند.
    /// </summary>
    private async Task<List<PartyStatementRow>> MergeShipmentExpenseSharesAsync(
        List<LedgerStatementProjection> entries,
        List<PartyStatementRow> rows,
        CancellationToken ct)
    {
        var expenseIds = entries
            .Where(e => e.SourceType == ExpenseSourceType && e.ShipmentId.HasValue)
            .Select(e => e.SourceId)
            .Distinct()
            .ToList();
        if (expenseIds.Count == 0)
        {
            return rows;
        }

        var expenseMeta = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => expenseIds.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                e.ExpenseTypeId,
                TypeCode = e.ExpenseType != null ? e.ExpenseType.Code : null
            })
            .ToDictionaryAsync(x => x.Id, ct);

        var merged = new List<PartyStatementRow>(rows.Count);
        var heads = new Dictionary<string, PartyStatementRow>(StringComparer.Ordinal);

        for (var i = 0; i < rows.Count; i++)
        {
            var entry = entries[i];
            var row = rows[i];

            if (entry.SourceType != ExpenseSourceType
                || !entry.ShipmentId.HasValue
                || !expenseMeta.TryGetValue(entry.SourceId, out var meta))
            {
                merged.Add(row);
                continue;
            }

            var key = string.Join(
                '|',
                entry.ShipmentId.Value,
                meta.ExpenseTypeId,
                row.Date.ToString("yyyyMMdd"),
                row.OriginalCurrency,
                row.FlowDirection);

            if (!heads.TryGetValue(key, out var head))
            {
                heads[key] = row;
                merged.Add(row);
                continue;
            }

            head.ReceiptBase = Add(head.ReceiptBase, row.ReceiptBase);
            head.OutflowBase = Add(head.OutflowBase, row.OutflowBase);
            head.OriginalAmount = Add(head.OriginalAmount, row.OriginalAmount);
            // سطرِ ادغام‌شده به یک قرارداد تعلق ندارد؛ مرجع هم کلیدِ نوع مصرف می‌شود
            // تا به سهمِ یک قرارداد اشاره نکند.
            head.ContractId = null;
            head.ContractNumber = null;
            if (!string.IsNullOrWhiteSpace(meta.TypeCode))
            {
                head.Reference = meta.TypeCode;
            }
        }

        return merged;
    }

    private static decimal? Add(decimal? left, decimal? right)
        => left.HasValue || right.HasValue ? (left ?? 0m) + (right ?? 0m) : null;

    /// <summary>
    /// جدا کردن «بیلانس اول دوره» از سطرهای دوره. اثر هر سطر با فرمول مرکزی
    /// (بیلانس = Σبرد − Σرسید) جمع می‌شود، نه با تفریق خام Debit/Credit.
    /// </summary>
    private StatementCalculation SplitAtPeriodStart(List<PartyStatementRow> allRows, DateTime? fromDate)
    {
        foreach (var row in allRows)
        {
            ApplyRubValues(row);
        }

        if (!fromDate.HasValue)
        {
            return new StatementCalculation(0m, 0m, allRows);
        }

        var from = fromDate.Value.Date;
        var opening = 0m;
        var openingRub = 0m;
        var openingRubKnown = true;
        var periodRows = new List<PartyStatementRow>(allRows.Count);
        foreach (var row in allRows)
        {
            if (row.Date < from)
            {
                opening += _balanceService.SignedEffect(
                    row.FlowDirection ?? CompanyFlowDirection.Outflow,
                    row.ReceiptBase ?? row.OutflowBase ?? 0m,
                    AccountKind);

                if (string.Equals(row.OriginalCurrency, "RUB", StringComparison.OrdinalIgnoreCase))
                {
                    if (row.SignedAmountRub.HasValue)
                    {
                        openingRub += row.SignedAmountRub.Value;
                    }
                    else
                    {
                        openingRubKnown = false;
                    }
                }
            }
            else
            {
                periodRows.Add(row);
            }
        }

        return new StatementCalculation(opening, openingRubKnown ? openingRub : null, periodRows);
    }

    private IQueryable<LedgerEntry> BuildPartyLedgerQuery(PartyRef party, PartyStatementFilter filter)
    {
        var query = _db.LedgerEntries.AsNoTracking().AsQueryable();

        query = party.PartyType switch
        {
            // انتساب مشتری هم مثل تأمین‌کننده از تعریف مرکزی می‌آید: هزینه و اسناد نقدیِ
            // بدون طرف‌حساب از راهِ قرارداد به مشتری نمی‌چسبند.
            // رجوع: LedgerEntryOwnership.CustomerOwnedByContract.
            PartyStatementPartyType.Customer => query.Where(l =>
                (l.CustomerId == party.PartyId
                    || (l.CustomerId == null
                        && l.SupplierId == null
                        && l.ServiceProviderId == null
                        && l.DriverId == null
                        && l.EmployeeId == null
                        && l.SourceType != LedgerEntryOwnership.ExpenseSourceType
                        && !LedgerEntryOwnership.CashSourceTypesWithoutContractParty.Contains(l.SourceType)
                        && l.Contract != null
                        && l.Contract.ContractType == ContractType.Sale
                        && l.Contract.CustomerId == party.PartyId)
                    || (l.SourceType == SaleSourceType
                        && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.CustomerId == party.PartyId)))
                // اصل فروش لغوشده و سطر برگشت آن برای audit در Ledger می‌مانند، اما چون
                // اثر خالصشان صفر است در صورت‌حساب رسمی مشتری نمایش داده نمی‌شوند.
                && (l.SourceType != SaleSourceType
                    || !_db.SalesTransactions.Any(s => s.Id == l.SourceId && s.IsCancelled))),
            // انتساب تأمین‌کننده از تعریف مرکزی می‌آید تا اسنادِ متعلق به طرف‌حسابِ دیگر
            // (مثلاً کرایهٔ حملِ ServiceProvider/Driver روی همان قرارداد خرید) وارد
            // صورت‌حساب تأمین‌کننده نشوند. رجوع: LedgerEntryOwnership.SupplierOwned.
            PartyStatementPartyType.Supplier => query.Where(LedgerEntryOwnership.SupplierOwned(party.PartyId)),
            PartyStatementPartyType.ServiceProvider => query.Where(l => l.ServiceProviderId == party.PartyId),
            PartyStatementPartyType.Driver => query.Where(l => l.DriverId == party.PartyId),
            // حساب جاریِ جواز. سطرِ پرداختی که شریک از جیب خودش داده اینجا نمی‌آید:
            // صندوق/بانکِ شرکت اصلاً حرکت نکرده (CashAccountId خالی است)، پس شمردنش
            // به‌عنوان خروجِ پولِ شرکت یعنی یک خرج دو بار. خودِ مصرف/بارگیری سطرِ جدا
            // دارد و دست‌نخورده در گزارش عملیاتی شرکت می‌ماند.
            PartyStatementPartyType.Company => query.Where(l =>
                ((l.Contract != null && l.Contract.CompanyId == party.PartyId)
                    || (l.SourceType == "Sale" && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.CompanyId == party.PartyId)))
                && !_db.PaymentTransactions.Any(p =>
                    p.LedgerEntryId == l.Id && p.FundingSource == PaymentFundingSource.Partner)),
            _ => throw new ArgumentOutOfRangeException(nameof(party), party.PartyType, "این نوع از Ledger عمومی خوانده نمی‌شود.")
        };

        // قرارداد لغوشده‌ای که از فهرست حذف (آرشیف) شده، با جفت سند ثبت/برگشتِ صفرشده‌اش در
        // صورت‌حساب تأمین‌کننده نمی‌آید. آرشیف فقط بعد از لغو و برگشت همهٔ اسناد ممکن است، پس مانده تغییر نمی‌کند.
        if (party.PartyType == PartyStatementPartyType.Supplier)
        {
            query = query.Where(l => l.Contract == null || !l.Contract.IsArchived);
        }

        // ویرایش مقدار فروش (SalesController.ApplyQuantityEditAsync): سطر فروشِ قبلی و برگشتش (جمع صفر)
        // جای خود را به ثبتِ تازه با مرجع «فاکتور/E{n}» داده‌اند؛ فقط ثبتِ جاری در صورت‌حساب می‌آید.
        // فقط سطری پنهان می‌شود که خودش برگشت است یا برگشت دارد، پس مانده تغییر نمی‌کند.
        var reversalSuffix = CompanyFlowSourceTypes.ReversalReferenceSuffix;
        query = query.Where(l => l.SourceType != SaleSourceType
            || !(_db.LedgerEntries.Any(r => r.SourceType == SaleSourceType
                    && r.SourceId == l.SourceId
                    && r.Id > l.Id
                    && r.Reference != null
                    && r.Reference.Contains("/E")
                    && !r.Reference.EndsWith(reversalSuffix))
                && ((l.Reference != null && l.Reference.EndsWith(reversalSuffix))
                    || _db.LedgerEntries.Any(x => x.SourceType == SaleSourceType
                        && x.SourceId == l.SourceId
                        && x.Reference == l.Reference + reversalSuffix))));

        if (filter.ContractId.HasValue)
        {
            var contractId = filter.ContractId.Value;
            query = query.Where(l =>
                l.ContractId == contractId
                || (l.SourceType == "Sale" && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.ContractId == contractId)));
        }

        if (party.CompanyId.HasValue && party.PartyType != PartyStatementPartyType.Company)
        {
            var companyId = party.CompanyId.Value;
            query = query.Where(l =>
                (l.Contract != null && l.Contract.CompanyId == companyId)
                || (l.SourceType == "Sale" && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.CompanyId == companyId)));
        }

        // در نمایش روبلی فیلتر ارز اعمال نمی‌شود تا همهٔ اسناد دیده شوند؛ ارزش روبلی
        // بعداً per-row محاسبه می‌گردد. برای سایر ارزها رفتار فیلتر بدون تغییر است.
        var currency = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currency is not null && !IsRubPresentation(filter))
        {
            query = query.Where(l =>
                (l.SourceCurrencyCode != null && l.SourceCurrencyCode == currency)
                || (l.SourceCurrencyCode == null && l.Currency == currency));
        }

        if (filter.ToDate.HasValue)
        {
            var exclusiveEnd = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < exclusiveEnd);
        }
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
        {
            var sourceType = filter.SourceType.Trim();
            query = query.Where(l => l.SourceType == sourceType);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(l =>
                l.Description.Contains(search)
                || (l.Reference != null && l.Reference.Contains(search))
                || (l.Contract != null && l.Contract.ContractNumber.Contains(search)));
        }

        return query;
    }

    private PartyStatementRow MapLedgerRow(LedgerStatementProjection entry, PartyStatementPolicy policy)
    {
        var currency = NormalizeCurrency(entry.OriginalCurrency);

        var row = new PartyStatementRow
        {
            Date = entry.Date,
            CreatedAtUtc = entry.CreatedAtUtc,
            Reference = entry.Reference,
            LedgerEntryId = entry.Id,
            Description = entry.Description,
            OriginalAmount = entry.OriginalAmount,
            OriginalCurrency = currency,
            FxRate = ResolveHistoricalRate(entry.FxRateToUsd, currency),
            FxRateDisplay = PartyStatementFormatting.FxDisplay(entry.FxRateToUsd, currency),
            SourceType = entry.SourceType,
            SourceId = entry.SourceId,
            PostingSequence = entry.Id,
            ContractId = entry.ContractId,
            ContractNumber = entry.ContractNumber
        };

        ApplyFlow(
            row,
            new CompanyFlowEvent(
                entry.SourceType,
                entry.Side,
                policy.FlowRole,
                LifecycleOf(entry.SourceType, entry.Reference)),
            entry.AmountUsd);

        return row;
    }

    /// <summary>
    /// صورت‌حساب شریک — از همان <see cref="IPartnershipStatementService"/> که پروفایل شریک
    /// و صورت‌حساب شراکت می‌خوانند. اینجا هیچ فرمولِ ماندهٔ تازه‌ای ساخته نمی‌شود.
    ///
    /// پیش از این همین متد یک محاسبهٔ مستقل داشت: هر سطرِ لجرِ قرارداد بر SharePercent
    /// تقسیم می‌شد و پرداختِ خودِ شریک ۱۰۰٪ به او می‌خورد. آن فرمول نه سهم مفاد را
    /// می‌شناخت، نه <see cref="Contract.SaleProceedsHolderPartnerId"/> و نه
    /// <see cref="PartnerSettlement"/>، پس ماندهٔ همین شریک در این صفحه با پروفایل او
    /// فرق می‌کرد — دقیقاً به اندازهٔ کلِ عایدِ فروشی که نزد یکی از شرکا مانده بود.
    ///
    /// اثرِ هر ردیف (<see cref="PartnerAccountEntry.EffectUsd"/>) خودش جزءِ فرمول مانده است:
    /// مثبت یعنی شریک ارزشی آورده (سرمایه، سهم مفاد، تسویهٔ پرداخت‌شده) و در ستون «برد»
    /// می‌نشیند؛ منفی یعنی ارزشی به او رسیده و در ستون «رسید». چون بیلانسِ حسابِ طرف‌حساب
    /// «اول دوره + Σبرد − Σرسید» است، ماندهٔ نهایی دقیقاً همان
    /// <see cref="PartnerAccountStatement.NetPositionUsd"/> در می‌آید.
    /// </summary>
    private async Task<StatementCalculation> BuildPartnerRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        PartyStatementPolicy policy,
        CancellationToken ct)
    {
        // صورت‌حساب شراکت فقط به دالر است. اگر کاربر ارز دیگری خواسته، عددِ ساختگی
        // ساخته نمی‌شود و صفحه خالی می‌ماند.
        var currency = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currency is not null
            && !string.Equals(currency, BaseCurrency, StringComparison.OrdinalIgnoreCase)
            && !IsRubPresentation(filter))
        {
            return new StatementCalculation(0m, 0m, []);
        }

        int[]? contractIds = null;
        if (filter.ContractId.HasValue)
        {
            contractIds = [filter.ContractId.Value];
        }
        else if (party.CompanyId.HasValue)
        {
            // نمای یک جواز: فقط قراردادهای همان شرکت.
            contractIds = await _db.ContractPartners
                .AsNoTracking()
                .Where(cp => cp.PartnerId == party.PartyId
                    && cp.Contract != null
                    && cp.Contract.CompanyId == party.CompanyId!.Value)
                .Select(cp => cp.ContractId)
                .Distinct()
                .ToArrayAsync(ct);
            if (contractIds.Length == 0)
            {
                return new StatementCalculation(0m, 0m, []);
            }
        }

        var statement = await _partnershipStatements.BuildForPartnerAsync(party.PartyId, contractIds, ct);
        if (statement is null)
        {
            return new StatementCalculation(0m, 0m, []);
        }

        var toDate = filter.ToDate?.Date;
        var sourceTypeFilter = string.IsNullOrWhiteSpace(filter.SourceType) ? null : filter.SourceType.Trim();
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        var allRows = new List<PartyStatementRow>();
        foreach (var entry in statement.Entries)
        {
            if (toDate.HasValue && entry.Date.HasValue && entry.Date.Value.Date > toDate.Value)
            {
                continue;
            }

            if (sourceTypeFilter is not null
                && !string.Equals(entry.SourceType, sourceTypeFilter, StringComparison.Ordinal))
            {
                continue;
            }

            if (search is not null
                && !(entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (entry.Reference?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (entry.ContractLabel?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                continue;
            }

            allRows.Add(MapPartnerEntryRow(entry));
        }

        return SplitAtPeriodStart(allRows, filter.FromDate);
    }

    /// <summary>
    /// یک ردیفِ گردش حساب شریک به سطرِ صورت‌حساب. جهت از علامتِ همان اثری می‌آید که
    /// فرمول مانده استفاده می‌کند، پس ستون‌ها و مانده هرگز از هم جدا نمی‌شوند.
    /// </summary>
    private PartyStatementRow MapPartnerEntryRow(PartnerAccountEntry entry)
    {
        // ردیفِ بی‌تاریخ فقط وقتی ساخته می‌شود که قرارداد هنوز هیچ فروشی ندارد (سهم مفاد).
        // آن ردیف به ابتدای گردش می‌رود تا هیچ‌وقت خاموش از جمع حذف نشود.
        var date = entry.Date?.Date ?? DateTime.MinValue;
        var row = new PartyStatementRow
        {
            Date = date,
            CreatedAtUtc = date,
            Reference = entry.Reference,
            Description = entry.Description,
            SourceType = entry.SourceType,
            SourceId = entry.SourceId ?? 0,
            ContractId = entry.ContractId,
            ContractNumber = entry.ContractLabel,
            OriginalAmount = Math.Abs(entry.AmountUsd),
            OriginalCurrency = BaseCurrency,
            Quantity = entry.QuantityMt,
            QuantityUnit = entry.QuantityMt.HasValue ? "MT" : null,
            UnitPrice = entry.UnitPriceUsd
        };

        var amount = Math.Abs(entry.EffectUsd);
        if (entry.EffectUsd < 0m)
        {
            row.FlowDirection = CompanyFlowDirection.Receipt;
            row.ReceiptBase = amount;
        }
        else
        {
            row.FlowDirection = CompanyFlowDirection.Outflow;
            row.OutflowBase = amount;
        }

        return row;
    }

    private async Task<StatementCalculation> BuildEmployeeRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        CancellationToken ct)
    {
        var query = _db.EmployeeSalaryTransactions
            .AsNoTracking()
            .Where(t => t.EmployeeId == party.PartyId && !t.IsCancelled)
            // وصولِ مساعده فقط جابه‌جاییِ «بدهی معاش ↔ طلب مساعده» است و ماندهٔ خالص را عوض نمی‌کند.
            .Where(t => t.TransactionType != EmployeeSalaryTransactionType.AdvanceRecovery
                && t.TransactionType != EmployeeSalaryTransactionType.LoanRecovery);
        var currency = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currency is not null && !IsRubPresentation(filter))
        {
            query = query.Where(t => t.Currency == currency);
        }
        if (filter.ToDate.HasValue)
        {
            var exclusiveEnd = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(t => t.TransactionDate < exclusiveEnd);
        }
        if (!string.IsNullOrWhiteSpace(filter.SourceType)
            && Enum.TryParse<EmployeeSalaryTransactionType>(filter.SourceType, true, out var transactionType))
        {
            query = query.Where(t => t.TransactionType == transactionType);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(t =>
                (t.Description != null && t.Description.Contains(search))
                || (t.Reference != null && t.Reference.Contains(search)));
        }

        var transactions = await query
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);
        var allRows = transactions.Select(MapEmployeeRow).ToList();
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
        {
            var sourceType = filter.SourceType.Trim();
            allRows = allRows.Where(r => string.Equals(r.SourceType, sourceType, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            allRows = allRows.Where(r =>
                r.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.Reference?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        return SplitAtPeriodStart(allRows, filter.FromDate);
    }

    /// <summary>
    /// سطر صورت‌حساب کارمند. ثبت معاش/بونس «رسید» است (شرکت کار و خدمت گرفته) و پرداخت
    /// معاش/مساعده «برد». پیش‌تر علامت این صورت‌حساب وارونهٔ بقیهٔ طرف‌حساب‌ها بود.
    /// «اصلاح حساب» جهت ثابتی ندارد و از علامت مبلغِ خودش خوانده می‌شود.
    /// </summary>
    private PartyStatementRow MapEmployeeRow(EmployeeSalaryTransaction transaction)
    {
        var amount = Math.Abs(transaction.AmountUsd);
        var currency = NormalizeCurrency(transaction.Currency);

        var row = new PartyStatementRow
        {
            Date = transaction.TransactionDate,
            CreatedAtUtc = transaction.CreatedAtUtc,
            Reference = transaction.Reference,
            Description = string.IsNullOrWhiteSpace(transaction.Description)
                ? EmployeeTransactionDescription(transaction.TransactionType)
                : transaction.Description,
            OriginalAmount = Math.Abs(transaction.Amount),
            OriginalCurrency = currency,
            FxRate = ResolveHistoricalRate(transaction.AppliedFxRateToUsd, currency),
            FxRateDisplay = PartyStatementFormatting.FxDisplay(transaction.AppliedFxRateToUsd, currency),
            SourceType = transaction.TransactionType.ToString(),
            SourceId = transaction.Id,
            PostingSequence = transaction.Id
        };

        // «اصلاح حساب» با مبلغ مثبت مثل ثبت معاش عمل می‌کند (تعهد بیشتر) و با مبلغ منفی
        // مثل پرداخت. برای بقیه، خودِ نوع تراکنش در نگاشت مرکزی تعریف شده است.
        var adjustmentSide = transaction.AmountUsd > 0m ? LedgerSide.Credit : LedgerSide.Debit;
        ApplyFlow(
            row,
            new CompanyFlowEvent(
                transaction.TransactionType.ToString(),
                adjustmentSide,
                CompanyFlowPartyRole.Employee),
            amount);

        return row;
    }

    private async Task<StatementCalculation> BuildSarrafRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        CancellationToken ct)
    {
        var allRows = new List<PartyStatementRow>();
        var settlementsQuery = _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.SarrafId == party.PartyId && s.Status == SarrafSettlementStatus.Posted);
        var paymentsQuery = _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.SarrafId == party.PartyId);
        var viaQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == ViaSarrafPayableLedgerSourceType && l.SourceId == party.PartyId);
        var currencyFilter = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currencyFilter is not null && !IsRubPresentation(filter))
        {
            settlementsQuery = settlementsQuery.Where(s => s.SarrafCurrency == currencyFilter);
            paymentsQuery = paymentsQuery.Where(p => p.Currency == currencyFilter);
            viaQuery = viaQuery.Where(l =>
                (l.SourceCurrencyCode != null && l.SourceCurrencyCode == currencyFilter)
                || (l.SourceCurrencyCode == null && l.Currency == currencyFilter));
        }

        if (filter.ToDate.HasValue)
        {
            var exclusiveEnd = filter.ToDate.Value.Date.AddDays(1);
            settlementsQuery = settlementsQuery.Where(s => s.SettlementDate < exclusiveEnd);
            paymentsQuery = paymentsQuery.Where(p => p.PaymentDate < exclusiveEnd);
            viaQuery = viaQuery.Where(l => l.EntryDate < exclusiveEnd);
        }
        if (filter.ContractId.HasValue)
        {
            settlementsQuery = settlementsQuery.Where(s => s.ContractId == filter.ContractId.Value);
            viaQuery = viaQuery.Where(l => l.ContractId == filter.ContractId.Value);
            paymentsQuery = paymentsQuery.Where(p => p.ContractId == filter.ContractId.Value);
        }
        if (party.CompanyId.HasValue)
        {
            paymentsQuery = paymentsQuery.Where(p => p.CompanyId == party.CompanyId.Value);
            settlementsQuery = settlementsQuery.Where(s => s.Contract != null && s.Contract.CompanyId == party.CompanyId.Value);
            viaQuery = viaQuery.Where(l => l.Contract != null && l.Contract.CompanyId == party.CompanyId.Value);
        }

        var settlements = await settlementsQuery.ToListAsync(ct);
        foreach (var settlement in settlements)
        {
            var currency = NormalizeCurrency(settlement.SarrafCurrency);
            // Out = صراف از طرف شرکت پرداخت کرد ⇒ صراف برای شرکت ارزش فراهم کرده ⇒ «رسید».
            // In  = صراف برای شرکت پول گرفت ⇒ پول نزد صراف است ⇒ «برد».
            var sarrafProvidedValue = settlement.Direction == SarrafSettlementDirection.Out;
            var row = new PartyStatementRow
            {
                Date = settlement.SettlementDate,
                CreatedAtUtc = settlement.CreatedAtUtc,
                Reference = settlement.ReferenceNumber,
                Description = string.IsNullOrWhiteSpace(settlement.Description)
                    ? (sarrafProvidedValue ? "پرداخت صراف از طرف شرکت" : "دریافت صراف برای شرکت")
                    : settlement.Description,
                OriginalAmount = settlement.SarrafChargedAmount,
                OriginalCurrency = currency,
                FxRate = ResolveHistoricalRate(settlement.SarrafFxRateToUsd, currency),
                FxRateDisplay = PartyStatementFormatting.FxDisplay(settlement.SarrafFxRateToUsd, currency),
                SourceType = nameof(SarrafSettlement),
                SourceId = settlement.Id,
                PostingSequence = settlement.Id,
                ContractId = settlement.ContractId
            };
            ApplyFlow(
                row,
                new CompanyFlowEvent(
                    nameof(SarrafSettlement),
                    sarrafProvidedValue ? LedgerSide.Credit : LedgerSide.Debit,
                    CompanyFlowPartyRole.Sarraf),
                settlement.SarrafChargedAmountUsd);
            allRows.Add(row);
        }

        var viaRows = await viaQuery.ToListAsync(ct);
        foreach (var ledger in viaRows)
        {
            var row = new PartyStatementRow
            {
                Date = ledger.EntryDate,
                CreatedAtUtc = ledger.CreatedAtUtc,
                Reference = ledger.Reference,
                Description = ledger.Description,
                OriginalAmount = ledger.SourceCurrencyCode == null
                    || string.Equals(ledger.SourceCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase)
                        ? ledger.SourceAmount ?? ledger.AmountUsd
                        : ledger.SourceAmount,
                OriginalCurrency = NormalizeCurrency(ledger.SourceCurrencyCode ?? ledger.Currency),
                FxRate = ResolveHistoricalRate(ledger.AppliedFxRateToUsd, ledger.SourceCurrencyCode ?? ledger.Currency),
                FxRateDisplay = PartyStatementFormatting.FxDisplay(ledger.AppliedFxRateToUsd, ledger.SourceCurrencyCode ?? ledger.Currency),
                SourceType = ledger.SourceType,
                SourceId = ledger.Id,
                PostingSequence = ledger.Id,
                ContractId = ledger.ContractId
            };
            // صراف به‌جای شرکت به تأمین‌کننده پرداخت کرده ⇒ ارزش را او فراهم کرده ⇒ «رسید».
            ApplyFlow(
                row,
                new CompanyFlowEvent(ledger.SourceType, ledger.Side, CompanyFlowPartyRole.Sarraf, LifecycleOf(ledger.SourceType, ledger.Reference)),
                ledger.AmountUsd);
            allRows.Add(row);
        }

        var payments = await paymentsQuery.ToListAsync(ct);
        foreach (var payment in payments)
        {
            var isPaymentToSarraf = payment.Direction == PaymentDirection.Out;
            var currency = NormalizeCurrency(payment.Currency);
            var row = new PartyStatementRow
            {
                Date = payment.PaymentDate,
                CreatedAtUtc = payment.CreatedAtUtc,
                Reference = payment.Reference,
                Description = string.IsNullOrWhiteSpace(payment.Description)
                    ? (isPaymentToSarraf ? "پرداخت شرکت به صراف" : "برگشت وجه از صراف")
                    : payment.Description,
                OriginalAmount = payment.Amount,
                OriginalCurrency = currency,
                FxRate = ResolveHistoricalRate(payment.AppliedFxRateToUsd, currency),
                FxRateDisplay = PartyStatementFormatting.FxDisplay(payment.AppliedFxRateToUsd, currency),
                SourceType = nameof(PaymentTransaction),
                SourceId = payment.Id,
                PostingSequence = payment.Id,
                ContractId = payment.ContractId
            };
            // حرکت واقعی پول: خروج از شرکت = برد، ورود به شرکت = رسید.
            ApplyFlow(
                row,
                new CompanyFlowEvent(
                    nameof(PaymentTransaction),
                    partyRole: CompanyFlowPartyRole.Sarraf,
                    paymentDirection: payment.Direction),
                payment.AmountUsd);
            allRows.Add(row);
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceType))
        {
            var sourceType = filter.SourceType.Trim();
            allRows = allRows.Where(r => string.Equals(r.SourceType, sourceType, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            allRows = allRows.Where(r =>
                r.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.Reference?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        return SplitAtPeriodStart(allRows, filter.FromDate);
    }

    private async Task AddOperationalColumnsAsync(List<PartyStatementRow> rows, CancellationToken ct)
    {
        var saleIds = rows.Where(r => r.SourceType == "Sale").Select(r => r.SourceId).Distinct().ToList();
        if (saleIds.Count > 0)
        {
            var sales = await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => saleIds.Contains(s.Id))
                .Select(s => new
                {
                    s.Id,
                    s.QuantityMt,
                    s.UnitPriceInCurrency,
                    s.UnitPriceUsd,
                    s.Currency,
                    ProductName = s.Product != null
                        ? (s.Product.NamePersian ?? s.Product.Name)
                        : null
                })
                .ToDictionaryAsync(s => s.Id, ct);
            foreach (var row in rows.Where(r => r.SourceType == "Sale"))
            {
                if (sales.TryGetValue(row.SourceId, out var sale))
                {
                    row.Quantity = sale.QuantityMt;
                    row.QuantityUnit = "MT";
                    row.UnitPrice = sale.UnitPriceUsd;
                    var currency = NormalizeCurrency(sale.Currency);
                    var unitPrice = sale.UnitPriceInCurrency > 0m
                        ? sale.UnitPriceInCurrency
                        : sale.UnitPriceUsd;
                    var productName = string.IsNullOrWhiteSpace(sale.ProductName)
                        ? "محصول نفتی"
                        : sale.ProductName;
                    var lifecycle = row.IsReversalRow ? "لغو فروش" : "فروش";
                    row.Description = $"{lifecycle} {productName}؛ مقدار {sale.QuantityMt:0.###} MT؛ نرخ {unitPrice:0.####} {currency}/MT";
                }
            }
        }

        var loadingIds = rows.Where(r => r.SourceType == "Loading").Select(r => r.SourceId).Distinct().ToList();
        if (loadingIds.Count > 0)
        {
            var loadings = await _db.LoadingRegisters
                .AsNoTracking()
                .Where(l => loadingIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    l.LoadedQuantityMt,
                    l.PlattsUsd,
                    l.LoadingPriceUsd,
                    Premium = l.Contract != null ? l.Contract.PremiumDiscountUsd : null
                })
                .ToDictionaryAsync(l => l.Id, ct);
            foreach (var row in rows.Where(r => r.SourceType == "Loading"))
            {
                if (loadings.TryGetValue(row.SourceId, out var loading))
                {
                    row.Quantity = loading.LoadedQuantityMt;
                    row.QuantityUnit = "MT";
                    row.PlattsPrice = loading.PlattsUsd;
                    row.PremiumOrDiscount = loading.Premium;
                    row.UnitPrice = loading.LoadingPriceUsd;
                }
            }
        }
    }

    private Task<PartyStatementPartyInfo?> LoadPartyInfoAsync(PartyRef party, CancellationToken ct)
        => _parties.GetProfileAsync(new PartyKey(party.PartyType, party.PartyId), ct);

    private async Task<PartyStatementCompanyInfo> LoadCompanyInfoAsync(
        PartyRef party,
        int? contractId,
        CancellationToken ct)
    {
        int? companyId = party.PartyType == PartyStatementPartyType.Company ? party.PartyId : party.CompanyId;
        if (!companyId.HasValue && contractId.HasValue)
        {
            companyId = await _db.Contracts.AsNoTracking()
                .Where(c => c.Id == contractId.Value)
                .Select(c => (int?)c.CompanyId)
                .FirstOrDefaultAsync(ct);
        }

        var company = companyId.HasValue
            ? await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId.Value)
                .Select(c => new { Name = c.NamePersian ?? c.Name, c.Address })
                .FirstOrDefaultAsync(ct)
            : null;

        return new PartyStatementCompanyInfo
        {
            Name = company?.Name ?? _options.CompanyName,
            Subtitle = _options.CompanySubtitle,
            Address = company?.Address ?? _options.Address,
            Phone = _options.Phone,
            Email = _options.Email,
            Website = _options.Website,
            LogoPath = _options.LogoPath
        };
    }

    private List<PartyStatementRow> BuildRunningRows(
        decimal opening,
        List<PartyStatementRow> periodRows,
        DateTime? fromDate)
    {
        var ordered = periodRows
            .OrderBy(r => r.Date)
            .ThenBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.PostingSequence)
            .ThenBy(r => r.SourceType, StringComparer.Ordinal)
            .ThenBy(r => r.SourceId)
            .ToList();
        var result = new List<PartyStatementRow>(ordered.Count + 1);
        var balance = opening;

        if (fromDate.HasValue || opening != 0m)
        {
            result.Add(new PartyStatementRow
            {
                Sequence = 0,
                Date = fromDate?.Date ?? ordered.FirstOrDefault()?.Date.Date ?? _businessClock.Today,
                CreatedAtUtc = DateTime.MinValue,
                Reference = "OB",
                Description = CompanyFlowText.Get(CompanyFlowTextKey.OpeningBalance, isEnglish: false),
                RunningBalance = opening,
                OriginalCurrency = BaseCurrency,
                SourceType = "OpeningBalance",
                IsOpeningBalance = true
            });
        }

        var sequence = 1;
        foreach (var row in ordered)
        {
            balance += row.SignedAmount;
            row.Sequence = sequence++;
            row.RunningBalance = balance;
            // دنبالهٔ ردیابیِ Ledger در سند رسمی نمایش داده نمی‌شود، اما متن انسانی هرگز با «…»
            // بریده نمی‌شود؛ خانهٔ شرح در صفحه و PDF چندخطی است.
            row.Reference = PartyStatementFormatting.CleanReference(row.Reference);
            row.Description = PartyStatementFormatting.CleanDescription(row.Description);
            result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// جمع ستون‌های «معامله / دریافت / پرداخت» از همان مبالغ سطرها. تفکیکِ نمایشیِ همان
    /// Σرسید/Σبرد است و بیلانس از آن ساخته نمی‌شود.
    /// </summary>
    private static ColumnTotals SummarizeColumns(
        PartyStatementPartyType partyType,
        IReadOnlyList<PartyStatementRow> rows,
        bool presentInRub)
    {
        decimal trade = 0m, received = 0m, paid = 0m;
        decimal tradeRub = 0m, receivedRub = 0m, paidRub = 0m;
        decimal quantity = 0m;
        var quantityKnown = true;
        var hasTrade = false;
        string? unit = null;
        foreach (var row in rows)
        {
            var amounts = PartyStatementPresentation.AmountsFor(row, partyType);
            trade += amounts.Trade ?? 0m;
            received += amounts.Received ?? 0m;
            paid += amounts.Paid ?? 0m;

            if (amounts.Trade.HasValue)
            {
                hasTrade = true;
                if (!row.TradeQuantity.HasValue
                    || (unit is not null && !string.Equals(unit, row.TradeQuantityUnit, StringComparison.OrdinalIgnoreCase)))
                {
                    quantityKnown = false;
                }
                else
                {
                    unit ??= row.TradeQuantityUnit;
                    quantity += Math.Sign(amounts.Trade.Value) * row.TradeQuantity.Value;
                }
            }

            if (presentInRub)
            {
                var rub = PartyStatementPresentation.AmountsFor(row, partyType, isRub: true);
                tradeRub += rub.Trade ?? 0m;
                receivedRub += rub.Received ?? 0m;
                paidRub += rub.Paid ?? 0m;
            }
        }

        return new ColumnTotals(
            trade,
            received,
            paid,
            presentInRub ? tradeRub : null,
            presentInRub ? receivedRub : null,
            presentInRub ? paidRub : null,
            hasTrade && quantityKnown ? quantity : null,
            hasTrade && quantityKnown ? unit : null);
    }

    private sealed record ColumnTotals(
        decimal Trade,
        decimal Received,
        decimal Paid,
        decimal? TradeRub,
        decimal? ReceivedRub,
        decimal? PaidRub,
        decimal? Quantity,
        string? QuantityUnit);

    private static PartyStatementColumnOptions ResolveColumns(
        IReadOnlyCollection<PartyStatementRow> rows,
        PartyStatementFilter filter)
    {
        var currencies = rows
            .Select(r => NormalizeCurrency(r.OriginalCurrency))
            .Where(c => c != BaseCurrency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var showOperational = filter.IncludeOperationalColumns;

        return new PartyStatementColumnOptions
        {
            ShowRub = currencies.Contains("RUB", StringComparer.OrdinalIgnoreCase),
            ShowAed = currencies.Contains("AED", StringComparer.OrdinalIgnoreCase),
            ShowOriginalAmount = currencies.Any(c => c is not "RUB" and not "AED"),
            ShowCurrency = currencies.Count > 0,
            ShowFxRate = rows.Any(r => !string.Equals(r.OriginalCurrency, BaseCurrency, StringComparison.OrdinalIgnoreCase)),
            ShowQuantity = showOperational && rows.Any(r => r.Quantity.HasValue),
            ShowPlatts = showOperational && rows.Any(r => r.PlattsPrice.HasValue),
            ShowPremiumOrDiscount = showOperational && rows.Any(r => r.PremiumOrDiscount.HasValue),
            ShowUnitPrice = showOperational && rows.Any(r => r.UnitPrice.HasValue)
        };
    }

    private static void ValidateFilter(PartyStatementFilter filter)
    {
        if (filter.FromDate.HasValue && filter.ToDate.HasValue && filter.FromDate.Value.Date > filter.ToDate.Value.Date)
        {
            throw new ArgumentException("تاریخ شروع نمی‌تواند بعد از تاریخ پایان باشد.", nameof(filter));
        }
        if (filter.Page < 1)
        {
            throw new ArgumentException("شماره صفحه باید حداقل یک باشد.", nameof(filter));
        }
        if (filter.PageSize is < 10 or > 100)
        {
            throw new ArgumentException("تعداد ردیف صفحه باید بین ۱۰ و ۱۰۰ باشد.", nameof(filter));
        }
    }

    private static decimal? ResolveHistoricalRate(decimal? fxRateToUsd, string? currency)
        => string.Equals(currency, BaseCurrency, StringComparison.OrdinalIgnoreCase)
            ? 1m
            : fxRateToUsd is > 0m ? fxRateToUsd : null;

    // آیا کاربر روبل را برای «نمایش» انتخاب کرده؟ در این حالت به‌جای فیلترِ ارز،
    // همهٔ اسناد نمایش داده می‌شوند و ارزش روبلی هر سند (نرخ تاریخی خودش) محاسبه می‌شود.
    private static bool IsRubPresentation(PartyStatementFilter filter)
        => string.Equals(NormalizeOptionalCurrency(filter.CurrencyCode), "RUB", StringComparison.OrdinalIgnoreCase);

    // ارزش روبلی سطر: فقط برای اسناد ذاتاً روبلی (OriginalCurrency == RUB) که مبلغ
    // اصلی روبلی‌شان ذخیره شده است. سایر اسناد ارزش روبلیِ تاریخی ندارند و null می‌مانند.
    private static void ApplyRubValues(PartyStatementRow row)
    {
        if (!string.Equals(row.OriginalCurrency, "RUB", StringComparison.OrdinalIgnoreCase)
            || !row.OriginalAmount.HasValue)
        {
            return;
        }

        var amount = Math.Abs(row.OriginalAmount.Value);
        row.ReceiptRub = row.ReceiptBase.HasValue ? amount : null;
        row.OutflowRub = row.OutflowBase.HasValue ? amount : null;
    }

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? BaseCurrency : currency.Trim().ToUpperInvariant();

    private static string? NormalizeOptionalCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? null : NormalizeCurrency(currency);

    private static string BuildStatementNumber(PartyRef party, DateTime statementDate)
        => $"STMT-{party.PartyType.ToString()[..3].ToUpperInvariant()}-{party.PartyId:000000}-{statementDate:yyyyMMdd}";

    private static string EmployeeTransactionDescription(EmployeeSalaryTransactionType type)
        => type switch
        {
            EmployeeSalaryTransactionType.SalaryAccrual => "ثبت معاش دوره",
            EmployeeSalaryTransactionType.SalaryPayment => "پرداخت معاش",
            EmployeeSalaryTransactionType.SalaryAdvance => "پیش‌پرداخت معاش",
            EmployeeSalaryTransactionType.SalaryDeduction => "کسر معاش",
            EmployeeSalaryTransactionType.Bonus => "بونس",
            EmployeeSalaryTransactionType.Adjustment => "اصلاح حساب",
            _ => "تراکنش معاش"
        };

    private sealed record StatementCalculation(
        decimal OpeningBalance,
        decimal? OpeningBalanceRub,
        List<PartyStatementRow> PeriodRows);

    private sealed class LedgerStatementProjection
    {
        public int Id { get; init; }
        public DateTime Date { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public LedgerSide Side { get; init; }
        public decimal AmountUsd { get; set; }
        public decimal? OriginalAmount { get; set; }
        public string OriginalCurrency { get; set; } = BaseCurrency;
        public decimal? FxRateToUsd { get; set; }
        public string? Reference { get; init; }
        public string Description { get; init; } = string.Empty;
        public string SourceType { get; init; } = string.Empty;
        public int SourceId { get; init; }
        public int? ContractId { get; set; }
        public int? ShipmentId { get; init; }
        public string? ContractNumber { get; init; }
    }
}
