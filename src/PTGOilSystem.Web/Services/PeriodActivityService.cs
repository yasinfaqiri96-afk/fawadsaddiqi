using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public interface IPeriodActivityService
{
    /// <summary>
    /// خلاصهٔ فقط‌خواندنیِ همهٔ عملیاتِ یک دورهٔ مالی در بازهٔ تاریخیِ همان دوره و برای همان شرکت.
    /// اگر دوره وجود نداشته باشد یا به شرکتِ داده‌شده تعلق نداشته باشد، <c>null</c> برمی‌گردد.
    /// هیچ چیزی نوشته نمی‌شود و هیچ منطقِ مالی‌ای اجرا نمی‌شود.
    /// </summary>
    Task<PeriodActivityViewModel?> BuildAsync(
        int fiscalPeriodId,
        int companyId,
        bool accountingEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// همان گزارش، ولی با صفحه‌بندیِ واقعیِ دیتابیس. فقط بخشِ <paramref name="section"/> صفحه
    /// می‌خورد و بقیهٔ بخش‌ها صفحهٔ اول خود را نشان می‌دهند. با <paramref name="allRows"/>
    /// هیچ برش و هیچ سقفی اعمال نمی‌شود (مسیر Export).
    /// </summary>
    Task<PeriodActivityViewModel?> BuildAsync(
        int fiscalPeriodId,
        int companyId,
        bool accountingEnabled,
        string? section,
        int page,
        int pageSize,
        bool allRows,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// دورهٔ پیش‌فرض وقتی کاربر بدون شناسهٔ دوره وارد صفحه می‌شود (مثلاً از «مرکز گزارشات»):
    /// دوره‌ای که تاریخِ امروز داخلِ بازهٔ آن است، وگرنه تازه‌ترین دورهٔ همان شرکت.
    /// اگر شرکت هیچ دوره‌ای نداشته باشد <c>null</c> برمی‌گردد. فقط‌خواندنی.
    /// </summary>
    Task<int?> FindDefaultPeriodIdAsync(
        int companyId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// گزارشِ خلاصهٔ فعالیتِ دورهٔ مالی. **کاملاً فقط‌خواندنی.**
///
/// فیلترِ تاریخ = بازهٔ [StartDate, EndDate] همان دوره (نیمه‌باز: <c>&gt;= start &amp;&amp; &lt; endExclusive</c>
/// تا کلِّ روزِ پایانی هم شامل شود). فیلترِ شرکت فقط روی موجودیت‌هایی اعمال می‌شود که واقعاً
/// <c>CompanyId</c> دارند (Sales, Payment, Contract, JournalEntry)؛ بقیه در این سیستمِ تک‌شرکتی
/// فیلدِ شرکت ندارند و فقط با تاریخ فیلتر می‌شوند — این از روی مدلِ واقعی است، نه حدس.
/// </summary>
/// <summary>نام بخش‌های صفحهٔ «فعالیت‌های دوره» — همان کلیدهایی که در آدرس صفحه می‌آیند.</summary>
public static class PeriodActivitySections
{
    public const string Purchases = "purchases";
    public const string Loadings = "loadings";
    public const string Sales = "sales";
    public const string Receipts = "receipts";
    public const string Payments = "payments";
    public const string Expenses = "expenses";
    public const string Movements = "movements";
    public const string Journals = "journals";

    public static readonly IReadOnlyList<string> All =
        [Purchases, Loadings, Sales, Receipts, Payments, Expenses, Movements, Journals];

    /// <summary>هر مقدار ناشناخته به بخش پیش‌فرض (خرید) برمی‌گردد.</summary>
    public static string Normalize(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return Purchases;
        }

        var trimmed = section.Trim().ToLowerInvariant();
        return All.Contains(trimmed) ? trimmed : Purchases;
    }
}

public sealed class PeriodActivityService(ApplicationDbContext db) : IPeriodActivityService
{
    /// <summary>برشِ یک بخش. <c>Take = int.MaxValue</c> یعنی «همه» (مسیر Export).</summary>
    private sealed record Paging(
        int Skip,
        int Take,
        string Section,
        Dictionary<string, bool> HasMore,
        bool AllRows);

    /// <summary>
    /// یک سطر بیشتر از اندازهٔ صفحه خوانده می‌شود تا وجودِ صفحهٔ بعد بدون COUNT اضافه معلوم شود؛
    /// سطرِ اضافه قبل از ساختن خروجی دور انداخته می‌شود.
    /// </summary>
    private static List<T> TrimPage<T>(List<T> rows, Paging paging)
    {
        if (paging.AllRows)
        {
            paging.HasMore[paging.Section] = false;
            return rows;
        }

        var hasMore = rows.Count > paging.Take;
        paging.HasMore[paging.Section] = hasMore;
        return hasMore ? rows.GetRange(0, paging.Take) : rows;
    }

    /// <summary>اندازهٔ پیش‌فرض صفحه در هر بخش (جای سقفِ خاموشِ قبلی).</summary>
    public const int DefaultPageSize = 100;

    public Task<PeriodActivityViewModel?> BuildAsync(
        int fiscalPeriodId,
        int companyId,
        bool accountingEnabled,
        CancellationToken cancellationToken = default)
        => BuildAsync(
            fiscalPeriodId, companyId, accountingEnabled,
            section: null, page: 1, pageSize: DefaultPageSize, allRows: false, cancellationToken);

    public async Task<PeriodActivityViewModel?> BuildAsync(
        int fiscalPeriodId,
        int companyId,
        bool accountingEnabled,
        string? section,
        int page,
        int pageSize,
        bool allRows,
        CancellationToken cancellationToken = default)
    {
        var period = await db.FiscalPeriods.AsNoTracking()
            .Where(p => p.Id == fiscalPeriodId && p.CompanyId == companyId)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.StartDate,
                p.EndDate,
                p.FiscalYearId,
                FiscalYearName = p.FiscalYear != null ? p.FiscalYear.Name : "",
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (period is null)
            return null;

        var companyName = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? $"Company {companyId}";

        // بازهٔ نیمه‌باز. Kind را صریحاً Utc می‌کنیم چون ستون‌های تاریخِ عملیاتی روی PostgreSQL
        // از نوع timestamptz هستند و پارامترِ با Kind=Unspecified را رد می‌کنند (همان قراردادی که
        // DashboardService با AfghanistanBusinessClock.SystemToday رعایت می‌کند). InMemory این را نادیده می‌گیرد.
        var start = DateTime.SpecifyKind(period.StartDate.Date, DateTimeKind.Utc);
        var endExclusive = DateTime.SpecifyKind(period.EndDate.Date, DateTimeKind.Utc).AddDays(1);

        // صفحه‌بندی: فقط بخشِ انتخاب‌شده صفحه می‌خورد؛ بقیه صفحهٔ اول خود را نشان می‌دهند.
        // هر بخش یک سطر بیشتر از اندازهٔ صفحه می‌خواند تا «صفحهٔ بعدی دارد یا نه» بدون
        // یک COUNT اضافه معلوم شود. در حالت Export هیچ برشی اعمال نمی‌شود.
        var activeSection = PeriodActivitySections.Normalize(section);
        var safePageSize = allRows ? int.MaxValue : Math.Max(1, pageSize);
        var safePage = allRows ? 1 : Math.Max(1, page);
        var hasMore = new Dictionary<string, bool>(StringComparer.Ordinal);

        Paging PageFor(string sectionName)
            => allRows
                ? new Paging(0, int.MaxValue, sectionName, hasMore, true)
                : new Paging(
                    string.Equals(sectionName, activeSection, StringComparison.Ordinal) ? (safePage - 1) * safePageSize : 0,
                    safePageSize,
                    sectionName,
                    hasMore,
                    false);

        var purchases = await BuildPurchasesAsync(companyId, start, endExclusive, PageFor(PeriodActivitySections.Purchases), cancellationToken);
        var loadings = await BuildLoadingsAsync(start, endExclusive, PageFor(PeriodActivitySections.Loadings), cancellationToken);
        var sales = await BuildSalesAsync(companyId, start, endExclusive, PageFor(PeriodActivitySections.Sales), cancellationToken);
        var receipts = await BuildPaymentsAsync(companyId, PaymentDirection.In, start, endExclusive, PageFor(PeriodActivitySections.Receipts), cancellationToken);
        var payments = await BuildPaymentsAsync(companyId, PaymentDirection.Out, start, endExclusive, PageFor(PeriodActivitySections.Payments), cancellationToken);
        var expenses = await BuildExpensesAsync(start, endExclusive, PageFor(PeriodActivitySections.Expenses), cancellationToken);
        var movements = await BuildMovementsAsync(start, endExclusive, PageFor(PeriodActivitySections.Movements), cancellationToken);
        var journals = accountingEnabled
            ? await BuildJournalsAsync(companyId, start, endExclusive, PageFor(PeriodActivitySections.Journals), cancellationToken)
            : [];

        var kpis = await BuildKpisAsync(companyId, start, endExclusive, accountingEnabled, cancellationToken);

        return new PeriodActivityViewModel(
            period.Id, period.FiscalYearId, period.FiscalYearName, period.Name,
            period.StartDate, period.EndDate, companyName, accountingEnabled,
            kpis, purchases, loadings, sales, receipts, payments, expenses, movements, journals,
            activeSection, safePage, allRows ? 0 : safePageSize, hasMore);
    }

    public async Task<int?> FindDefaultPeriodIdAsync(
        int companyId,
        CancellationToken cancellationToken = default)
    {
        // همان قرارداد Kind=Utc که BuildAsync رعایت می‌کند (ستون‌های timestamptz روی PostgreSQL).
        var today = DateTime.SpecifyKind(AfghanistanBusinessClock.SystemToday.Date, DateTimeKind.Utc);

        var containingToday = await db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.StartDate <= today && p.EndDate >= today)
            .OrderByDescending(p => p.StartDate)
            .ThenByDescending(p => p.Id)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (containingToday is not null)
            return containingToday;

        // امروز در هیچ دوره‌ای نیست (سال مالی هنوز شروع نشده یا تمام شده) → تازه‌ترین دوره.
        return await db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId)
            .OrderByDescending(p => p.StartDate)
            .ThenByDescending(p => p.Id)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // خرید — قراردادهای خرید که تاریخشان در بازهٔ دوره است. شرکت دارد → فیلترِ شرکت اعمال می‌شود.
    private async Task<IReadOnlyList<PeriodActivityRow>> BuildPurchasesAsync(
        int companyId, DateTime start, DateTime endExclusive, Paging paging, CancellationToken cancellationToken)
    {
        var rows = await db.Contracts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.ContractType == ContractType.Purchase
                && c.ContractDate >= start && c.ContractDate < endExclusive)
            .OrderByDescending(c => c.ContractDate).ThenByDescending(c => c.Id)
            .Skip(paging.Skip)
            .Take(paging.AllRows ? int.MaxValue : paging.Take + 1)
            .Select(c => new
            {
                c.ContractDate,
                c.ContractNumber,
                ProductCode = c.Product != null ? c.Product.Code : null,
                c.QuantityMt,
                c.Status
            })
            .ToListAsync(cancellationToken);

        return TrimPage(rows, paging).Select(c => new PeriodActivityRow(
            c.ContractDate, c.ContractNumber, c.ProductCode, c.QuantityMt, null, c.Status.ToString())).ToList();
    }

    // بارگیری — LoadingRegister فیلدِ شرکت ندارد؛ فقط با تاریخ فیلتر می‌شود.
    private async Task<IReadOnlyList<PeriodActivityRow>> BuildLoadingsAsync(
        DateTime start, DateTime endExclusive, Paging paging, CancellationToken cancellationToken)
    {
        var rows = await db.LoadingRegisters.AsNoTracking()
            .Where(l => l.LoadingDate >= start && l.LoadingDate < endExclusive)
            .OrderByDescending(l => l.LoadingDate).ThenByDescending(l => l.Id)
            .Skip(paging.Skip)
            .Take(paging.AllRows ? int.MaxValue : paging.Take + 1)
            .Select(l => new
            {
                l.Id,
                l.LoadingDate,
                l.BillOfLadingNumber,
                l.RwbNo,
                ProductCode = l.Product != null ? l.Product.Code : null,
                l.LoadedQuantityMt
            })
            .ToListAsync(cancellationToken);

        return TrimPage(rows, paging).Select(l => new PeriodActivityRow(
            l.LoadingDate,
            FirstNonEmpty(l.BillOfLadingNumber, l.RwbNo, $"#{l.Id}"),
            l.ProductCode, l.LoadedQuantityMt, null, null)).ToList();
    }

    // فروش — SalesTransaction شرکت دارد.
    private async Task<IReadOnlyList<PeriodActivityRow>> BuildSalesAsync(
        int companyId, DateTime start, DateTime endExclusive, Paging paging, CancellationToken cancellationToken)
    {
        var rows = await db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.CompanyId == companyId
                && s.SaleDate >= start && s.SaleDate < endExclusive)
            .OrderByDescending(s => s.SaleDate).ThenByDescending(s => s.Id)
            .Skip(paging.Skip)
            .Take(paging.AllRows ? int.MaxValue : paging.Take + 1)
            .Select(s => new
            {
                s.Id,
                s.SaleDate,
                s.InvoiceNumber,
                ProductCode = s.Product != null ? s.Product.Code : null,
                s.QuantityMt,
                s.TotalUsd,
                s.IsSalePricePending
            })
            .ToListAsync(cancellationToken);

        // فروش با قیمت باز: مقدار در فعالیت دوره می‌ماند؛ مبلغ خالی و وضعیت «در انتظار قیمت».
        return TrimPage(rows, paging).Select(s => new PeriodActivityRow(
            s.SaleDate, FirstNonEmpty(s.InvoiceNumber, $"#{s.Id}"),
            s.ProductCode, s.QuantityMt,
            s.IsSalePricePending ? null : s.TotalUsd,
            s.IsSalePricePending ? "در انتظار قیمت" : null)).ToList();
    }

    // دریافت/پرداخت — PaymentTransaction شرکت دارد.
    private async Task<IReadOnlyList<PeriodActivityRow>> BuildPaymentsAsync(
        int companyId, PaymentDirection direction, DateTime start, DateTime endExclusive, Paging paging, CancellationToken cancellationToken)
    {
        var rows = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.Direction == direction && p.CompanyId == companyId
                && p.PaymentDate >= start && p.PaymentDate < endExclusive)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Id)
            .Skip(paging.Skip)
            .Take(paging.AllRows ? int.MaxValue : paging.Take + 1)
            .Select(p => new
            {
                p.Id,
                p.PaymentDate,
                p.Reference,
                p.PaymentKind,
                p.AmountUsd
            })
            .ToListAsync(cancellationToken);

        return TrimPage(rows, paging).Select(p => new PeriodActivityRow(
            p.PaymentDate, FirstNonEmpty(p.Reference, $"#{p.Id}"),
            p.PaymentKind.ToString(), null, p.AmountUsd, null)).ToList();
    }

    // مصارف — ExpenseTransaction فیلدِ شرکت ندارد؛ فقط با تاریخ فیلتر می‌شود.
    private async Task<IReadOnlyList<PeriodActivityRow>> BuildExpensesAsync(
        DateTime start, DateTime endExclusive, Paging paging, CancellationToken cancellationToken)
    {
        var rows = await db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled && e.ExpenseDate >= start && e.ExpenseDate < endExclusive)
            .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Id)
            .Skip(paging.Skip)
            .Take(paging.AllRows ? int.MaxValue : paging.Take + 1)
            .Select(e => new
            {
                e.Id,
                e.ExpenseDate,
                ExpenseCode = e.ExpenseType != null ? e.ExpenseType.Code : null,
                e.AmountUsd
            })
            .ToListAsync(cancellationToken);

        return TrimPage(rows, paging).Select(e => new PeriodActivityRow(
            e.ExpenseDate, FirstNonEmpty(e.ExpenseCode, $"#{e.Id}"), null, null, e.AmountUsd, null)).ToList();
    }

    // تغییرات موجودی — InventoryMovement فیلدِ شرکت ندارد؛ فقط با تاریخ فیلتر می‌شود.
    private async Task<IReadOnlyList<PeriodActivityRow>> BuildMovementsAsync(
        DateTime start, DateTime endExclusive, Paging paging, CancellationToken cancellationToken)
    {
        var rows = await db.InventoryMovements.AsNoTracking()
            .Where(m => m.MovementDate >= start && m.MovementDate < endExclusive)
            .OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id)
            .Skip(paging.Skip)
            .Take(paging.AllRows ? int.MaxValue : paging.Take + 1)
            .Select(m => new
            {
                m.Id,
                m.MovementDate,
                m.Direction,
                ProductCode = m.Product != null ? m.Product.Code : null,
                m.QuantityMt,
                m.ReferenceDocument
            })
            .ToListAsync(cancellationToken);

        return TrimPage(rows, paging).Select(m => new PeriodActivityRow(
            m.MovementDate, FirstNonEmpty(m.ReferenceDocument, $"#{m.Id}"),
            m.ProductCode, m.QuantityMt, null, MovementLabel(m.Direction))).ToList();
    }

    // اسناد حسابداری — JournalEntry شرکت دارد. فقط اسنادِ Posted.
    private async Task<IReadOnlyList<PeriodActivityRow>> BuildJournalsAsync(
        int companyId, DateTime start, DateTime endExclusive, Paging paging, CancellationToken cancellationToken)
    {
        var rows = await db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted
                && j.AccountingDate >= start && j.AccountingDate < endExclusive)
            .OrderByDescending(j => j.AccountingDate).ThenByDescending(j => j.Id)
            .Skip(paging.Skip)
            .Take(paging.AllRows ? int.MaxValue : paging.Take + 1)
            .Select(j => new
            {
                j.AccountingDate,
                j.JournalNumber,
                j.Description,
                Debit = j.Lines.Sum(l => (decimal?)l.Debit) ?? 0m
            })
            .ToListAsync(cancellationToken);

        return TrimPage(rows, paging).Select(j => new PeriodActivityRow(
            j.AccountingDate, j.JournalNumber, j.Description, null, j.Debit, null)).ToList();
    }

    private async Task<PeriodActivityKpis> BuildKpisAsync(
        int companyId, DateTime start, DateTime endExclusive, bool accountingEnabled, CancellationToken cancellationToken)
    {
        var purchaseQuery = db.Contracts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.ContractType == ContractType.Purchase
                && c.ContractDate >= start && c.ContractDate < endExclusive);
        var purchaseCount = await purchaseQuery.CountAsync(cancellationToken);
        var purchaseQty = await purchaseQuery.SumAsync(c => (decimal?)c.QuantityMt, cancellationToken) ?? 0m;

        var loadingQuery = db.LoadingRegisters.AsNoTracking()
            .Where(l => l.LoadingDate >= start && l.LoadingDate < endExclusive);
        var loadingCount = await loadingQuery.CountAsync(cancellationToken);
        var loadingQty = await loadingQuery.SumAsync(l => (decimal?)l.LoadedQuantityMt, cancellationToken) ?? 0m;

        var salesQuery = db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.CompanyId == companyId
                && s.SaleDate >= start && s.SaleDate < endExclusive);
        var salesCount = await salesQuery.CountAsync(cancellationToken);
        var salesQty = await salesQuery.SumAsync(s => (decimal?)s.QuantityMt, cancellationToken) ?? 0m;
        var salesUsd = await salesQuery.Where(s => !s.IsSalePricePending).SumAsync(s => (decimal?)s.TotalUsd, cancellationToken) ?? 0m;

        var receiptsUsd = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.Direction == PaymentDirection.In && p.CompanyId == companyId
                && p.PaymentDate >= start && p.PaymentDate < endExclusive)
            .SumAsync(p => (decimal?)p.AmountUsd, cancellationToken) ?? 0m;
        var paymentsUsd = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.Direction == PaymentDirection.Out && p.CompanyId == companyId
                && p.PaymentDate >= start && p.PaymentDate < endExclusive)
            .SumAsync(p => (decimal?)p.AmountUsd, cancellationToken) ?? 0m;

        var expensesUsd = await db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled && e.ExpenseDate >= start && e.ExpenseDate < endExclusive)
            .SumAsync(e => (decimal?)e.AmountUsd, cancellationToken) ?? 0m;

        // ورودی = In + Adjustment، خروجی = Out + Transfer (همان قرارداد علامتِ داشبورد).
        var inventoryQuery = db.InventoryMovements.AsNoTracking()
            .Where(m => m.MovementDate >= start && m.MovementDate < endExclusive);
        var inventoryInMt = await inventoryQuery
            .Where(m => m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment)
            .SumAsync(m => (decimal?)m.QuantityMt, cancellationToken) ?? 0m;
        var inventoryOutMt = await inventoryQuery
            .Where(m => m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer)
            .SumAsync(m => (decimal?)m.QuantityMt, cancellationToken) ?? 0m;

        var journalCount = 0;
        var journalDebit = 0m;
        if (accountingEnabled)
        {
            var journalQuery = db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted
                    && j.AccountingDate >= start && j.AccountingDate < endExclusive);
            journalCount = await journalQuery.CountAsync(cancellationToken);
            journalDebit = await db.JournalEntryLines.AsNoTracking()
                .Where(l => l.JournalEntry!.CompanyId == companyId
                    && l.JournalEntry.Status == JournalEntryStatus.Posted
                    && l.JournalEntry.AccountingDate >= start && l.JournalEntry.AccountingDate < endExclusive)
                .SumAsync(l => (decimal?)l.Debit, cancellationToken) ?? 0m;
        }

        return new PeriodActivityKpis(
            purchaseCount, purchaseQty, loadingCount, loadingQty,
            salesCount, salesQty, salesUsd, receiptsUsd, paymentsUsd, expensesUsd,
            inventoryInMt, inventoryOutMt, journalCount, journalDebit);
    }

    private static string MovementLabel(MovementDirection direction) => direction switch
    {
        MovementDirection.In => "ورود",
        MovementDirection.Out => "خروج",
        MovementDirection.Transfer => "انتقال",
        MovementDirection.Adjustment => "اصلاح",
        _ => direction.ToString()
    };

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "-";
}
