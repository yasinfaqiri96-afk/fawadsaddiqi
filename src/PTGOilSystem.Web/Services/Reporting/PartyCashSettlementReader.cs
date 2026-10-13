using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// پولِ واقعیِ ردوبدل‌شده با مشتری یا تأمین‌کننده — فقط‌خواندنی، برای ستون‌های «مجموع رسید»
/// و «جمع پرداخت» گزارش‌های گردش حساب. مانده از این‌جا ساخته نمی‌شود؛ مانده همیشه از
/// صورت‌حساب رسمی می‌آید و این عدد فقط بخشِ پولی آن را جدا نشان می‌دهد.
///
/// <para>سه منبع، هر پول فقط یک بار:</para>
/// <list type="bullet">
/// <item>پرداخت/دریافت روزنامچه (<see cref="PaymentTransaction"/>) — نقد، بانک و پرداخت شریک؛
/// برگشت پول به همان طرف با علامت منفی خالص می‌شود. ویرایش/حذف سند در خودِ جدول اعمال است.</item>
/// <item>حوالهٔ صراف (<see cref="SarrafSettlement"/> ثبت‌شده) — همان مبلغی که به حساب طرف نشسته است.
/// حواله‌ای که به یک پرداخت روزنامچهٔ همان نوع وصل است شمرده نمی‌شود تا دوباره نیاید.</item>
/// <item>تسویهٔ سه‌جانبه (<see cref="ThreeWaySettlement"/> ثبت‌شده) — مشتری مستقیم به تأمین‌کننده پرداخته است.</item>
/// </list>
/// اسناد لغوشده (Cancelled) و پیش‌نویس در هیچ منبعی شمرده نمی‌شوند.
/// </summary>
public sealed class PartyCashSettlementReader(ApplicationDbContext db)
{
    /// <summary>خالص پولی که هر مشتری واقعاً به شرکت داده است (دریافت − برگشت به مشتری).</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> GetCustomerNetReceiptsAsync(
        IReadOnlyCollection<int> customerIds,
        DateTime? fromDate,
        DateTime? toDate,
        CancellationToken ct = default)
    {
        var result = new Dictionary<int, decimal>();
        if (customerIds.Count == 0)
        {
            return result;
        }

        var ids = customerIds.Distinct().ToArray();
        var from = fromDate?.Date;
        var to = toDate?.Date;

        // همان قاعدهٔ پروفایل مشتری: مشتری از خود سند، از فروش یا از قرارداد فروشِ آن خوانده می‌شود.
        var payments = db.PaymentTransactions.AsNoTracking()
            .Where(p => p.PaymentKind == PaymentKind.CustomerReceipt || p.PaymentKind == PaymentKind.CustomerPayment)
            .Select(p => new
            {
                CustomerId = p.CustomerId
                    ?? (p.SalesTransaction != null ? p.SalesTransaction.CustomerId : null)
                    ?? (p.Contract != null && p.Contract.ContractType == ContractType.Sale ? p.Contract.CustomerId : null),
                p.PaymentDate,
                p.Direction,
                p.AmountUsd
            })
            .Where(p => p.CustomerId != null && ids.Contains(p.CustomerId.Value));
        if (from.HasValue) payments = payments.Where(p => p.PaymentDate >= from.Value);
        if (to.HasValue) payments = payments.Where(p => p.PaymentDate <= to.Value);

        foreach (var row in await payments
                     .GroupBy(p => p.CustomerId!.Value)
                     .Select(g => new
                     {
                         PartyId = g.Key,
                         NetUsd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : -p.AmountUsd)
                     })
                     .ToListAsync(ct))
        {
            Add(result, row.PartyId, row.NetUsd);
        }

        var sarraf = db.SarrafSettlements.AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && s.CounterpartyType == SarrafSettlementCounterpartyType.Customer
                && s.CustomerId != null && ids.Contains(s.CustomerId.Value)
                && !(s.PaymentTransaction != null
                    && (s.PaymentTransaction.PaymentKind == PaymentKind.CustomerReceipt
                        || s.PaymentTransaction.PaymentKind == PaymentKind.CustomerPayment)));
        if (from.HasValue) sarraf = sarraf.Where(s => s.SettlementDate >= from.Value);
        if (to.HasValue) sarraf = sarraf.Where(s => s.SettlementDate <= to.Value);

        foreach (var row in await sarraf
                     .GroupBy(s => s.CustomerId!.Value)
                     .Select(g => new
                     {
                         PartyId = g.Key,
                         NetUsd = g.Sum(s => (s.Direction == SarrafSettlementDirection.In ? 1m : -1m)
                             * (s.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
                                 ? s.RequestedAmountUsd
                                 : s.SupplierAcceptedAmountUsd))
                     })
                     .ToListAsync(ct))
        {
            Add(result, row.PartyId, row.NetUsd);
        }

        var threeWay = db.ThreeWaySettlements.AsNoTracking()
            .Where(t => t.Status == ThreeWaySettlementStatus.Posted && ids.Contains(t.CustomerId));
        if (from.HasValue) threeWay = threeWay.Where(t => t.SettlementDate >= from.Value);
        if (to.HasValue) threeWay = threeWay.Where(t => t.SettlementDate <= to.Value);

        foreach (var row in await threeWay
                     .GroupBy(t => t.CustomerId)
                     .Select(g => new { PartyId = g.Key, NetUsd = g.Sum(t => t.CustomerPaidUsd) })
                     .ToListAsync(ct))
        {
            Add(result, row.PartyId, row.NetUsd);
        }

        return result;
    }

    /// <summary>خالص پولی که شرکت واقعاً به هر تأمین‌کننده داده است (پرداخت − پول برگشتی از تأمین‌کننده).</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> GetSupplierNetPaymentsAsync(
        IReadOnlyCollection<int> supplierIds,
        DateTime? fromDate,
        DateTime? toDate,
        CancellationToken ct = default)
    {
        var result = new Dictionary<int, decimal>();
        if (supplierIds.Count == 0)
        {
            return result;
        }

        var ids = supplierIds.Distinct().ToArray();
        var from = fromDate?.Date;
        var to = toDate?.Date;

        var payments = db.PaymentTransactions.AsNoTracking()
            .Where(p => (p.PaymentKind == PaymentKind.SupplierPayment || p.PaymentKind == PaymentKind.SupplierReceipt)
                && p.SupplierId != null && ids.Contains(p.SupplierId.Value));
        if (from.HasValue) payments = payments.Where(p => p.PaymentDate >= from.Value);
        if (to.HasValue) payments = payments.Where(p => p.PaymentDate <= to.Value);

        foreach (var row in await payments
                     .GroupBy(p => p.SupplierId!.Value)
                     .Select(g => new
                     {
                         PartyId = g.Key,
                         NetUsd = g.Sum(p => p.Direction == PaymentDirection.Out ? p.AmountUsd : -p.AmountUsd)
                     })
                     .ToListAsync(ct))
        {
            Add(result, row.PartyId, row.NetUsd);
        }

        var sarraf = db.SarrafSettlements.AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && s.CounterpartyType == SarrafSettlementCounterpartyType.Supplier
                && s.SupplierId != null && ids.Contains(s.SupplierId.Value)
                && !(s.PaymentTransaction != null
                    && (s.PaymentTransaction.PaymentKind == PaymentKind.SupplierPayment
                        || s.PaymentTransaction.PaymentKind == PaymentKind.SupplierReceipt)));
        if (from.HasValue) sarraf = sarraf.Where(s => s.SettlementDate >= from.Value);
        if (to.HasValue) sarraf = sarraf.Where(s => s.SettlementDate <= to.Value);

        foreach (var row in await sarraf
                     .GroupBy(s => s.SupplierId!.Value)
                     .Select(g => new
                     {
                         PartyId = g.Key,
                         NetUsd = g.Sum(s => (s.Direction == SarrafSettlementDirection.Out ? 1m : -1m)
                             * (s.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
                                 ? s.RequestedAmountUsd
                                 : s.SupplierAcceptedAmountUsd))
                     })
                     .ToListAsync(ct))
        {
            Add(result, row.PartyId, row.NetUsd);
        }

        var threeWay = db.ThreeWaySettlements.AsNoTracking()
            .Where(t => t.Status == ThreeWaySettlementStatus.Posted
                && t.PayeeType == ThreeWayPayeeType.Supplier
                && t.SupplierId != null && ids.Contains(t.SupplierId.Value));
        if (from.HasValue) threeWay = threeWay.Where(t => t.SettlementDate >= from.Value);
        if (to.HasValue) threeWay = threeWay.Where(t => t.SettlementDate <= to.Value);

        foreach (var row in await threeWay
                     .GroupBy(t => t.SupplierId!.Value)
                     .Select(g => new { PartyId = g.Key, NetUsd = g.Sum(t => t.SupplierAcceptedUsd) })
                     .ToListAsync(ct))
        {
            Add(result, row.PartyId, row.NetUsd);
        }

        return result;
    }

    private static void Add(Dictionary<int, decimal> target, int partyId, decimal amountUsd)
        => target[partyId] = target.GetValueOrDefault(partyId) + amountUsd;
}
