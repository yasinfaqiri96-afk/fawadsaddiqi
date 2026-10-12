using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public sealed record CustomerReceiptApplicationLine(int SalesTransactionId, decimal AmountUsd);

public sealed record CustomerReceiptApplyRequest(
    int PaymentTransactionId,
    IReadOnlyList<CustomerReceiptApplicationLine> Lines,
    string? CreatedByUserName = null);

public sealed record CustomerReceiptOpenSale(
    int SalesTransactionId,
    string InvoiceNumber,
    DateTime SaleDate,
    decimal TotalUsd,
    decimal AppliedUsd,
    decimal OpenUsd);

/// <summary>
/// وصولیِ یک فروش. <see cref="ReceivedUsd"/> = تطبیق‌های فعال + دریافتِ مستقیمِ قدیمی (بی‌ردیفِ
/// تطبیق)، <see cref="RefundedUsd"/> = پرداختِ مستقیمِ قدیمی به مشتری روی همان فروش.
/// </summary>
public sealed record CustomerSaleSettlement(int SalesTransactionId, decimal ReceivedUsd, decimal RefundedUsd)
{
    public decimal NetReceivedUsd => decimal.Round(ReceivedUsd - RefundedUsd, 4, MidpointRounding.AwayFromZero);

    public decimal OpenReceivableUsd(decimal saleTotalUsd)
        => decimal.Round(saleTotalUsd - NetReceivedUsd, 4, MidpointRounding.AwayFromZero);
}

public interface ICustomerReceiptApplicationService
{
    /// <summary>
    /// وصولیِ هر فروش — تنها تعریفِ «این فروش چقدر وصول شده»: ردیف‌های تطبیقِ فعال (از جمله
    /// مصرفِ پیش‌دریافت) به‌علاوهٔ پرداخت‌های مستقیمِ قدیمی که ردیفِ تطبیق ندارند. صفحهٔ فروش،
    /// صفحهٔ مشتری و داشبورد همه از همین می‌خوانند. فروشی که وصولی ندارد در نتیجه با صفر می‌آید.
    /// </summary>
    Task<IReadOnlyDictionary<int, CustomerSaleSettlement>> GetSaleSettlementsAsync(
        IReadOnlyCollection<int> salesTransactionIds, CancellationToken ct = default);

    /// <summary>مانده تطبیق‌نشدهٔ یک دریافت، به ارز خودِ دریافت.</summary>
    Task<decimal> GetUnappliedReceiptAmountAsync(int paymentTransactionId, CancellationToken ct = default);

    /// <summary>طلبِ باز یک فروش به USD: ارزش فروش منهای هر چه واقعاً روی آن نشسته است.</summary>
    Task<decimal> GetOpenReceivableUsdAsync(int salesTransactionId, CancellationToken ct = default);

    /// <summary>فروش‌های همان مشتری که هنوز طلبِ باز دارند، قدیمی‌ترین اول.</summary>
    Task<List<CustomerReceiptOpenSale>> GetOpenSalesAsync(
        int customerId, int? salesBatchId = null, CancellationToken ct = default);

    /// <summary>پیشنهاد توزیع FIFO یک دریافت روی فروش‌های باز؛ چیزی ذخیره نمی‌شود.</summary>
    Task<List<CustomerReceiptApplicationLine>> BuildFifoPlanAsync(
        int paymentTransactionId, int? salesBatchId = null, CancellationToken ct = default);

    Task<int> ApplyAsync(CustomerReceiptApplyRequest request, CancellationToken ct = default);

    Task ReverseApplicationAsync(
        int applicationId, string? reason = null, string? userName = null, CancellationToken ct = default);
}

/// <summary>
/// تطبیقِ نقدِ دریافت مشتری با فروش‌ها (cash application).
///
/// این سرویس هیچ سند مالی نمی‌سازد و هیچ مانده‌ای را جابه‌جا نمی‌کند: دریافتِ عادی مشتری سند خودش
/// را همان لحظه گرفته است (نقد/بانک بدهکار، مطالبات مشتری بستانکار) و مانده کل مشتری از همان‌جا
/// درست است. چیزی که کم بود «کدام دریافت روی کدام فاکتور نشست» بود و همین جدول آن را می‌گوید؛
/// پس تطبیق فقط اطلاعات ردیابی‌پذیر اضافه می‌کند و هرگز ژورنال دوباره نمی‌زند.
///
/// به همین دلیل فروش گروهی هم بدون ساختار تازه درست کار می‌کند: یک دریافت روی ۵۲ فروش پخش
/// می‌شود، هر سهم یک ردیف واقعیِ ذخیره‌شده است و هیچ سهمی از روی تناسب حدس زده نمی‌شود.
/// </summary>
public sealed class CustomerReceiptApplicationService(ApplicationDbContext db)
    : ICustomerReceiptApplicationService
{
    private const decimal Epsilon = 0.0001m;

    public async Task<decimal> GetUnappliedReceiptAmountAsync(
        int paymentTransactionId, CancellationToken ct = default)
    {
        var payment = await db.PaymentTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentTransactionId, ct);
        if (payment is null)
        {
            return 0m;
        }

        // مانده قابل تطبیق در ارز خودِ دریافت: هم رزروِ پیش‌فروش و هم تطبیقِ مستقیم آن را مصرف می‌کنند.
        var reserved = await db.CustomerPaymentAllocations
            .AsNoTracking()
            .Where(a => a.PaymentTransactionId == paymentTransactionId
                && a.Status == CustomerPaymentAllocationStatus.Active)
            .SumAsync(a => (decimal?)a.AllocatedPaymentAmount, ct) ?? 0m;

        var appliedDirect = await db.CustomerPaymentAllocationApplications
            .AsNoTracking()
            .Where(a => a.PaymentTransactionId == paymentTransactionId
                && a.CustomerPaymentAllocationId == null
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .SumAsync(a => (decimal?)a.AppliedPaymentAmount, ct) ?? 0m;

        return decimal.Round(payment.Amount - reserved - appliedDirect, 4, MidpointRounding.AwayFromZero);
    }

    public async Task<decimal> GetOpenReceivableUsdAsync(
        int salesTransactionId, CancellationToken ct = default)
    {
        var sale = await db.SalesTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == salesTransactionId, ct);
        if (sale is null || sale.IsCancelled || sale.IsSalePricePending)
        {
            return 0m;
        }

        var settlement = (await GetSaleSettlementsAsync([salesTransactionId], ct))[salesTransactionId];
        return settlement.OpenReceivableUsd(sale.TotalUsd);
    }

    public async Task<List<CustomerReceiptOpenSale>> GetOpenSalesAsync(
        int customerId, int? salesBatchId = null, CancellationToken ct = default)
    {
        var sales = await db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.CustomerId == customerId
                && !s.IsCancelled
                && !s.IsSalePricePending
                && (salesBatchId == null || s.SalesBatchId == salesBatchId))
            .OrderBy(s => s.SaleDate).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.InvoiceNumber, s.SaleDate, s.TotalUsd })
            .ToListAsync(ct);

        var settlements = await GetSaleSettlementsAsync(sales.Select(s => s.Id).ToList(), ct);
        var result = new List<CustomerReceiptOpenSale>();
        foreach (var sale in sales)
        {
            var received = settlements[sale.Id].NetReceivedUsd;
            var open = settlements[sale.Id].OpenReceivableUsd(sale.TotalUsd);
            if (open <= Epsilon)
            {
                continue;
            }

            result.Add(new CustomerReceiptOpenSale(
                sale.Id, sale.InvoiceNumber ?? $"#{sale.Id}", sale.SaleDate, sale.TotalUsd, received, open));
        }

        return result;
    }

    public async Task<List<CustomerReceiptApplicationLine>> BuildFifoPlanAsync(
        int paymentTransactionId, int? salesBatchId = null, CancellationToken ct = default)
    {
        var plan = new List<CustomerReceiptApplicationLine>();

        var payment = await db.PaymentTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentTransactionId, ct);
        if (payment?.CustomerId is null)
        {
            return plan;
        }

        var rate = GetValidRate(payment);
        var remainingUsd = decimal.Round(
            await GetUnappliedReceiptAmountAsync(paymentTransactionId, ct) * rate,
            4,
            MidpointRounding.AwayFromZero);
        if (remainingUsd <= Epsilon)
        {
            return plan;
        }

        foreach (var sale in await GetOpenSalesAsync(payment.CustomerId.Value, salesBatchId, ct))
        {
            if (remainingUsd <= Epsilon)
            {
                break;
            }

            var take = Math.Min(sale.OpenUsd, remainingUsd);
            plan.Add(new CustomerReceiptApplicationLine(sale.SalesTransactionId, take));
            remainingUsd -= take;
        }

        return plan;
    }

    public async Task<int> ApplyAsync(CustomerReceiptApplyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lines = (request.Lines ?? new List<CustomerReceiptApplicationLine>())
            .Where(l => l.AmountUsd > 0m)
            .GroupBy(l => l.SalesTransactionId)
            .Select(g => new CustomerReceiptApplicationLine(g.Key, g.Sum(x => x.AmountUsd)))
            .ToList();
        if (lines.Count == 0)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_EMPTY", "هیچ مبلغی برای تطبیق وارد نشده است.");
        }

        var payment = await db.PaymentTransactions
            .FirstOrDefaultAsync(p => p.Id == request.PaymentTransactionId, ct)
            ?? throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_PAYMENT_NOT_FOUND", "دریافت انتخاب‌شده یافت نشد.");

        if (payment.PaymentKind != PaymentKind.CustomerReceipt || payment.Direction != PaymentDirection.In)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_NOT_CUSTOMER_RECEIPT", "فقط دریافت از مشتری قابل تطبیق است.");
        }

        if (payment.IsCustomerAdvance == true)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_IS_ADVANCE",
                "این دریافت پیش‌دریافت است و از مسیر تخصیص به پیش‌فروش مصرف می‌شود.");
        }

        if (payment.CustomerId is null)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_NO_CUSTOMER", "این دریافت مشتری مشخصی ندارد.");
        }

        var rate = GetValidRate(payment);

        var availableUsd = decimal.Round(
            await GetUnappliedReceiptAmountAsync(payment.Id, ct) * rate, 4, MidpointRounding.AwayFromZero);
        var requestedUsd = decimal.Round(lines.Sum(l => l.AmountUsd), 4, MidpointRounding.AwayFromZero);
        if (requestedUsd > availableUsd + Epsilon)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_OVER_RECEIPT",
                $"مجموع تطبیق از مانده تطبیق‌نشدهٔ این دریافت ({availableUsd:N2} USD) بیشتر است.");
        }

        var today = AfghanistanBusinessClock.SystemToday;
        var created = 0;

        foreach (var line in lines)
        {
            var sale = await db.SalesTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == line.SalesTransactionId, ct)
                ?? throw new BusinessRuleException(
                    "CUSTOMER_APPLICATION_SALE_NOT_FOUND", $"فروش #{line.SalesTransactionId} یافت نشد.");

            if (sale.IsCancelled)
            {
                throw new BusinessRuleException(
                    "CUSTOMER_APPLICATION_SALE_CANCELLED",
                    $"فروش {sale.InvoiceNumber} لغو شده و تطبیق نمی‌پذیرد.");
            }

            if (sale.IsSalePricePending)
            {
                throw new BusinessRuleException(
                    "CUSTOMER_APPLICATION_SALE_PRICE_PENDING",
                    $"قیمت فروش {sale.InvoiceNumber} هنوز تعیین نشده است؛ دریافت به‌عنوان پیش‌دریافت روی حساب مشتری می‌ماند و پس از تعیین قیمت تطبیق شود.");
            }

            if (sale.SupplierId.HasValue)
            {
                throw new BusinessRuleException(
                    "CUSTOMER_APPLICATION_SUPPLIER_SALE",
                    $"فروش {sale.InvoiceNumber} به تأمین‌کننده ثبت شده و دریافت مشتری روی آن قابل تطبیق نیست.");
            }

            if (sale.CustomerId != payment.CustomerId)
            {
                throw new BusinessRuleException(
                    "CUSTOMER_APPLICATION_CUSTOMER_MISMATCH",
                    $"فروش {sale.InvoiceNumber} متعلق به مشتری این دریافت نیست.");
            }

            // پرداختی که از قدیم مستقیم به همین فروش وصل است، همین حالا در «دریافت‌شده» شمرده می‌شود؛
            // ردیف تطبیق برای آن یعنی شمارش دوباره.
            if (payment.SalesTransactionId == sale.Id)
            {
                throw new BusinessRuleException(
                    "CUSTOMER_APPLICATION_ALREADY_LINKED",
                    $"این دریافت هم‌اکنون مستقیماً به فروش {sale.InvoiceNumber} وصل است.");
            }

            var openUsd = await GetOpenReceivableUsdAsync(sale.Id, ct);
            if (line.AmountUsd > openUsd + Epsilon)
            {
                throw new BusinessRuleException(
                    "CUSTOMER_APPLICATION_OVER_SALE",
                    $"مبلغ تطبیق از طلبِ باز فروش {sale.InvoiceNumber} ({openUsd:N2} USD) بیشتر است.");
            }

            db.CustomerPaymentAllocationApplications.Add(new CustomerPaymentAllocationApplication
            {
                PaymentTransactionId = payment.Id,
                CustomerPaymentAllocationId = null,
                SalesTransactionId = sale.Id,
                AppliedAt = today,
                AppliedPaymentAmount = decimal.Round(line.AmountUsd / rate, 4, MidpointRounding.AwayFromZero),
                PaymentCurrencyCode = payment.Currency,
                AppliedAmountUsd = decimal.Round(line.AmountUsd, 4, MidpointRounding.AwayFromZero),
                Status = CustomerPaymentAllocationApplicationStatus.Active,
                CompanyId = sale.CompanyId,
                // اثر مالی همان لحظهٔ دریافت ثبت شده است؛ این ردیف فقط تطبیق است، پس ژورنال ندارد.
                JournalEntryId = null,
                CreatedByUserName = request.CreatedByUserName
            });
            created++;
        }

        await db.SaveChangesAsync(ct);
        return created;
    }

    public async Task ReverseApplicationAsync(
        int applicationId, string? reason = null, string? userName = null, CancellationToken ct = default)
    {
        var application = await db.CustomerPaymentAllocationApplications
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct)
            ?? throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_NOT_FOUND", "ردیف تطبیق یافت نشد.");

        if (application.CustomerPaymentAllocationId != null)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_NOT_DIRECT",
                "این ردیف مصرفِ پیش‌دریافت است و فقط با لغو همان تحویل آزاد می‌شود.");
        }

        if (application.Status == CustomerPaymentAllocationApplicationStatus.Reversed)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_ALREADY_REVERSED", "این تطبیق قبلاً برگشت خورده است.");
        }

        // رکورد حذف نمی‌شود؛ فقط برگشت می‌خورد تا تاریخچه دست‌نخورده بماند.
        application.Status = CustomerPaymentAllocationApplicationStatus.Reversed;
        application.ReversedAtUtc = DateTime.UtcNow;
        application.ReversedByUserName = userName;
        application.ReversalReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, CustomerSaleSettlement>> GetSaleSettlementsAsync(
        IReadOnlyCollection<int> salesTransactionIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(salesTransactionIds);
        var ids = salesTransactionIds.Distinct().ToArray();
        var result = ids.ToDictionary(id => id, id => new CustomerSaleSettlement(id, 0m, 0m));
        if (ids.Length == 0)
        {
            return result;
        }

        var applied = await db.CustomerPaymentAllocationApplications
            .AsNoTracking()
            .Where(a => ids.Contains(a.SalesTransactionId)
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .GroupBy(a => a.SalesTransactionId)
            .Select(g => new { SaleId = g.Key, AmountUsd = g.Sum(a => a.AppliedAmountUsd) })
            .ToListAsync(ct);

        // پرداختِ مستقیمِ قدیمی که ردیفِ تطبیقِ فعال روی همین فروش دارد از این‌جا کنار می‌رود تا
        // مبلغش فقط یک بار (از مسیرِ تطبیق) شمرده شود.
        var legacy = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.SalesTransactionId != null
                && ids.Contains(p.SalesTransactionId.Value)
                && !db.CustomerPaymentAllocationApplications.Any(a =>
                    a.PaymentTransactionId == p.Id
                    && a.SalesTransactionId == p.SalesTransactionId
                    && a.Status == CustomerPaymentAllocationApplicationStatus.Active))
            .GroupBy(p => new { SaleId = p.SalesTransactionId!.Value, p.Direction })
            .Select(g => new { g.Key.SaleId, g.Key.Direction, AmountUsd = g.Sum(p => p.AmountUsd) })
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            var receivedUsd = applied.Where(a => a.SaleId == id).Sum(a => a.AmountUsd)
                + legacy.Where(p => p.SaleId == id && p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd);
            var refundedUsd = legacy.Where(p => p.SaleId == id && p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd);
            result[id] = new CustomerSaleSettlement(id, receivedUsd, refundedUsd);
        }

        return result;
    }

    private static decimal GetValidRate(PaymentTransaction payment)
    {
        var rate = payment.AppliedFxRateToUsd;
        if (!rate.HasValue || rate.Value <= 0m)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_INVALID_FX",
                "این دریافت نرخ تبدیل معتبر ندارد و تا اصلاح نرخ قابل تطبیق نیست.");
        }

        if (SystemCurrency.IsBaseCurrency(payment.Currency) && rate.Value != 1m)
        {
            throw new BusinessRuleException(
                "CUSTOMER_APPLICATION_INVALID_FX", "نرخ تبدیل دریافت دالری معتبر نیست.");
        }

        return rate.Value;
    }
}
