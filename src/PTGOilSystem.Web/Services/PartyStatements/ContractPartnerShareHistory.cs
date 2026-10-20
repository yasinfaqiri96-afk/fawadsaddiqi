using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>یک بازهٔ سهم: «این شریک از این تاریخ تا این تاریخ، این درصد را داشت».</summary>
public sealed record ContractPartnerShareSlice(
    int ContractId,
    int PartnerId,
    decimal SharePercent,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo);

/// <summary>
/// PTG-P0-03 — تنها مرجعِ پاسخ به این پرسش: «سهمِ این شریک در <b>تاریخِ همان رویداد</b> چند بود؟»
///
/// پیش از این، هر گزارش خودش <c>ContractPartner.SharePercent</c> زنده را می‌خواند، بنابراین
/// تغییر درصد، سهمِ مفادِ دوره‌های گذشته را هم بازنویسی می‌کرد. حالا هر سه مسیرِ پول
/// (<c>PartnershipStatementService</c>، <c>PartyStatementReadService</c>،
/// <c>PartyBalanceReadService</c>) از همین کلاس می‌پرسند تا هیچ گزارشی از بقیه جدا نیفتد.
///
/// قاعدهٔ انتخاب بازه برای تاریخ D:
///   آخرین بازه‌ای که <c>EffectiveFrom &lt;= D</c> باشد.
///   اگر هیچ بازه‌ای پیش از D نبود، نخستین بازه استفاده می‌شود.
///
/// آن fallback عمدی است: پس از Backfill هر قرارداد فقط یک بازه دارد، پس همهٔ رویدادها —
/// حتی رویدادی که تاریخش پیش از آغاز آن بازه باشد — دقیقاً همان عددِ قبلی را می‌گیرند و
/// هیچ رقمِ تاریخی جابه‌جا نمی‌شود.
///
/// چرا انتخاب بر پایهٔ <c>EffectiveFrom</c> است و نه بازهٔ بسته با <c>EffectiveTo</c>:
/// بازه‌ها به‌صورت پیوسته نوشته می‌شوند (بستنِ بازهٔ جاری و بازکردنِ بازهٔ بعدی با همان تاریخ)،
/// پس برای دادهٔ سالم هر دو روش یک جواب می‌دهند. ولی اگر روزی سطری بسته شود و جانشینی
/// نداشته باشد، این روش همچنان آخرین سهمِ معلوم را برمی‌گرداند و شریک را بی‌صدا صفر نمی‌کند.
/// <c>EffectiveTo</c> برای خوانایی و ردیابی نوشته می‌شود.
/// </summary>
public sealed class ContractPartnerShareHistory
{
    private readonly Dictionary<int, List<ContractPartnerShareSlice>> _byContract;

    private ContractPartnerShareHistory(Dictionary<int, List<ContractPartnerShareSlice>> byContract)
        => _byContract = byContract;

    public static ContractPartnerShareHistory FromSlices(IEnumerable<ContractPartnerShareSlice> slices)
    {
        var byContract = slices
            .GroupBy(s => s.ContractId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(s => s.EffectiveFrom).ThenBy(s => s.PartnerId).ToList());

        return new ContractPartnerShareHistory(byContract);
    }

    public static async Task<ContractPartnerShareHistory> LoadAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct = default)
    {
        if (contractIds.Count == 0)
        {
            return FromSlices([]);
        }

        var rows = await db.ContractPartners
            .AsNoTracking()
            .Where(cp => contractIds.Contains(cp.ContractId))
            .Select(cp => new ContractPartnerShareSlice(
                cp.ContractId,
                cp.PartnerId,
                cp.SharePercent,
                cp.EffectiveFrom,
                cp.EffectiveTo))
            .ToListAsync(ct);

        return FromSlices(rows);
    }

    public static async Task<ContractPartnerShareHistory> LoadForPartnerAsync(
        ApplicationDbContext db,
        int partnerId,
        CancellationToken ct = default)
    {
        var rows = await db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.PartnerId == partnerId)
            .Select(cp => new ContractPartnerShareSlice(
                cp.ContractId,
                cp.PartnerId,
                cp.SharePercent,
                cp.EffectiveFrom,
                cp.EffectiveTo))
            .ToListAsync(ct);

        return FromSlices(rows);
    }

    /// <summary>همهٔ قراردادهایی که این تاریخچه برایشان بازه دارد.</summary>
    public IReadOnlyCollection<int> ContractIds => _byContract.Keys;

    /// <summary>
    /// PTG ۱۲-C — تعداد بازه‌های سهمِ این قرارداد.
    ///
    /// بیش از یک بازه یعنی مفادِ محاسبه‌شده ترکیبی از چند درصدِ مختلف است و
    /// <c>bookProfit × درصدِ امروز</c> دیگر همان عدد را نمی‌دهد. UI باید این را بگوید،
    /// وگرنه عددِ ترکیبی طوری نشان داده می‌شود که انگار با درصدِ امروز حساب شده است.
    /// </summary>
    public int SharePeriodCount(int contractId)
        => _byContract.TryGetValue(contractId, out var slices)
            ? slices.Select(slice => slice.EffectiveFrom).Distinct().Count()
            : 0;

    /// <summary>
    /// درصد سهم این شریک در تاریخ داده‌شده. اگر شریک اصلاً عضو این قرارداد نیست، صفر.
    /// تاریخِ خالی یعنی «امروز/آخرین وضعیت» و آخرین بازه را برمی‌گرداند.
    /// </summary>
    public decimal ShareFor(int contractId, int partnerId, DateTime? onDate)
    {
        if (!_byContract.TryGetValue(contractId, out var slices))
        {
            return 0m;
        }

        var start = ResolvePeriodStart(slices, onDate);
        return slices
            .Where(s => s.PartnerId == partnerId && s.EffectiveFrom.Date == start)
            .Select(s => s.SharePercent)
            .FirstOrDefault();
    }

    /// <summary>
    /// ترکیب شرکا در تاریخ داده‌شده — برای تقسیمِ یک مبلغ بین همهٔ شرکای همان لحظه.
    ///
    /// بازه برای کلِ قرارداد یکی انتخاب می‌شود، نه جدا برای هر شریک: شریکی که در بازهٔ
    /// تازه نیست سهمی از آن ندارد، و شریکی که تازه آمده سهمی از بازهٔ پیش از آمدنش ندارد.
    /// انتخابِ جداگانه برای هر شریک، با عوض‌شدن شریک جمعِ سهم‌ها را بیش از ۱۰۰ می‌کرد.
    /// </summary>
    public IReadOnlyList<(int PartnerId, decimal SharePercent)> SharesOn(int contractId, DateTime? onDate)
    {
        if (!_byContract.TryGetValue(contractId, out var slices))
        {
            return [];
        }

        var start = ResolvePeriodStart(slices, onDate);
        return slices
            .Where(s => s.EffectiveFrom.Date == start)
            .Select(s => (s.PartnerId, s.SharePercent))
            .OrderByDescending(x => x.SharePercent)
            .ThenBy(x => x.PartnerId)
            .ToList();
    }

    /// <summary>آخرین (جاری‌ترین) درصدِ ثبت‌شده — فقط برای نمایش و فرم‌ها، نه برای محاسبهٔ تاریخی.</summary>
    public decimal CurrentShareFor(int contractId, int partnerId)
        => ShareFor(contractId, partnerId, onDate: null);

    /// <summary>
    /// تاریخ‌های آغاز بازه‌های سهم یک قرارداد، مرتب و بدون تکرار. هر تاریخ یعنی «از اینجا
    /// ترکیب شرکا عوض شد». قرارداد بدون تغییر، فقط یک مرز دارد.
    /// </summary>
    public IReadOnlyList<DateTime> PeriodStartsFor(int contractId)
        => _byContract.TryGetValue(contractId, out var slices)
            ? slices.Select(s => s.EffectiveFrom).Distinct().OrderBy(d => d).ToList()
            : [];

    /// <summary>آغازِ بازه‌ای که تاریخ داده‌شده در آن است؛ تاریخِ خالی یعنی آخرین بازه.</summary>
    private static DateTime ResolvePeriodStart(
        IReadOnlyList<ContractPartnerShareSlice> slices,
        DateTime? onDate)
    {
        var starts = slices.Select(s => s.EffectiveFrom.Date).Distinct().OrderBy(d => d).ToList();
        if (onDate is null)
        {
            return starts[^1];
        }

        var date = onDate.Value.Date;
        var match = starts.LastOrDefault(start => start <= date);

        // هیچ بازه‌ای پیش از این تاریخ نبود ⇒ نخستین بازه (سازگاری کامل با دادهٔ Backfill‌شده).
        return match == default ? starts[0] : match;
    }
}
