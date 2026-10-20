using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>منبعِ هر ردیفِ صورت‌حساب شراکت — نه «محاسبهٔ مبهم».</summary>
public static class PartnershipStatementSources
{
    public const string Payment = "روزنامچه";
    public const string Sale = "فروش";
    public const string Loading = "بارگیری";
    public const string Expense = "مصرف";
    public const string Book = "دفتر قرارداد";
    public const string Reconciliation = "تطبیق";
    public const string Settlement = "تسویه بین شرکا";
}

/// <summary>
/// نوعِ رویدادِ یک ردیف صورت‌حساب، به زبانِ کاربر — نه اصطلاح حسابداری.
/// پروفایل شریک همین را در ستون «نوع رویداد» نشان می‌دهد.
/// </summary>
public enum PartnershipStatementLineKind
{
    PartnerFunding = 1,
    PartnerPurchase = 2,
    PartnerExpense = 3,
    SaleProceedsHeld = 4,
    ProfitShare = 5,
    PartnerSettlement = 6,
    Adjustment = 7,
    /// <summary>سهمِ شریک از هزینهٔ کالای هنوز فروخته‌نشده (مفادش هنوز محقق نشده).</summary>
    UnsoldCostShare = 8
}

public sealed record PartnershipStatementLine(
    string Source,
    string Title,
    DateTime? Date,
    int? PartnerId,
    decimal AmountUsd,
    string? Reference,
    int? RecordId,
    PartnershipStatementLineKind Kind = PartnershipStatementLineKind.PartnerFunding,
    /// <summary>مقدار عملیاتی همین سند، فقط اگر خودِ سند آن را دارد (فروش). وگرنه null.</summary>
    decimal? QuantityMt = null,
    /// <summary>نرخ همان سند. هرگز حدس زده نمی‌شود؛ نبودش یعنی null.</summary>
    decimal? UnitPriceUsd = null);

public sealed record PartnershipPartnerTotals(
    int PartnerId,
    string PartnerName,
    /// <summary>
    /// درصدِ <b>جاری</b> این شریک — یک برچسب، نه ضریبِ محاسبه.
    /// وقتی <see cref="SharePeriodCount"/> بیش از ۱ است،
    /// <see cref="ProfitShareUsd"/> از چند درصدِ تاریخی ساخته شده و برابرِ
    /// <c>bookProfit × SharePercent</c> نیست. UI باید همین را بگوید (PTG ۱۲-C).
    /// </summary>
    decimal SharePercent,
    decimal FundingUsd,
    decimal ProceedsHeldUsd,
    decimal ProfitShareUsd,
    decimal SettlementsPaidUsd,
    decimal SettlementsReceivedUsd,
    /// <summary>تعداد بازه‌های سهمی که در ساختِ این ارقام دخیل بوده‌اند. ۱ یعنی یک درصدِ ثابت.</summary>
    int SharePeriodCount = 1)
{
    /// <summary>
    /// PTG ۱۲-C — «آیا این عدد با یک درصدِ واحد ساخته شده؟»
    /// نه: پس نباید کنارش فقط درصدِ امروز نوشته شود.
    /// </summary>
    public bool SpansMultipleSharePeriods => SharePeriodCount > 1;

    /// <summary>
    /// سهمِ این شریک از هزینهٔ کالای هنوز فروخته‌نشده. <see cref="ProfitShareUsd"/> فقط سودِ محقق است؛
    /// هزینهٔ موجودیِ فروخته‌نشده هم باید مثل قبل به نسبتِ سهم بینِ شرکا برابر شود، پس جدا و با نام
    /// در مانده می‌آید (نه پنهان در مفاد).
    /// </summary>
    public decimal UnsoldCostShareUsd { get; init; }

    /// <summary>
    /// مثبت = این شریک طلبکار است، منفی = این شریک بدهکار است.
    /// پرداخت/سرمایه‌ای که داده + سهم مفادِ محقق − سهمِ هزینهٔ فروخته‌نشده − عایدی که نزد خودش مانده
    /// + تسویه‌هایی که پرداخته − تسویه‌هایی که گرفته.
    /// </summary>
    public decimal NetPositionUsd => decimal.Round(
        FundingUsd + ProfitShareUsd - UnsoldCostShareUsd - ProceedsHeldUsd + SettlementsPaidUsd - SettlementsReceivedUsd,
        2,
        MidpointRounding.AwayFromZero);
}

public sealed record PartnershipContractStatement(
    int ContractId,
    string ContractNumber,
    string ContractLabel,
    string Currency,
    decimal SalesUsd,
    decimal PurchaseCostUsd,
    decimal OperationalExpenseUsd,
    decimal TotalPartnerFundingUsd,
    decimal BookProfitUsd,
    decimal PaymentToBookDifferenceUsd,
    int? ProceedsHolderPartnerId,
    string? ProceedsHolderPartnerName,
    IReadOnlyList<PartnershipPartnerTotals> Partners,
    IReadOnlyList<PartnershipStatementLine> Lines)
{
    /// <summary>
    /// هزینهٔ دفتریِ کاملِ قرارداد: خرید + مصارف (چه فروخته شده باشد چه نه). مبنای
    /// <see cref="PaymentToBookDifferenceUsd"/> است، نه مبنای مفاد.
    /// </summary>
    public decimal TotalCostUsd => decimal.Round(
        PurchaseCostUsd + OperationalExpenseUsd,
        2,
        MidpointRounding.AwayFromZero);

    /// <summary>بهای کالای فروخته‌شده — از سودِ محققِ قرارداد (ProfitAndLossService).</summary>
    public decimal RealizedCostOfGoodsSoldUsd { get; init; }

    /// <summary>سهمِ فروخته‌شدهٔ مصارف — از سودِ محققِ قرارداد.</summary>
    public decimal RealizedOperationalCostUsd { get; init; }

    /// <summary>اثرِ ارزیِ محقق (سود − زیان − کسریِ صراف) — از سودِ محققِ قرارداد.</summary>
    public decimal RealizedFxNetUsd { get; init; }

    /// <summary>
    /// بخشی از هزینهٔ دفتری که هنوز به فروش نرسیده (موجودیِ فروخته‌نشده و سهمِ مصارفش). مفادِ آن
    /// هنوز محقق نشده، پس در <see cref="UnreconciledResidualUsd"/> صریح دیده می‌شود.
    /// </summary>
    public decimal UnrealizedCostCarriedUsd => decimal.Round(
        TotalCostUsd - RealizedCostOfGoodsSoldUsd - RealizedOperationalCostUsd,
        2,
        MidpointRounding.AwayFromZero);

    /// <summary>
    /// باقیماندهٔ تطبیق‌نشدهٔ همین قرارداد = جمعِ مانده دو شریک.
    /// برابر است با «تفاوت تطبیق پرداخت با دفتر» + «هزینهٔ هنوز فروخته‌نشده» + «اثرِ ارزیِ محقق»
    /// (به‌علاوهٔ گِردکردن).
    /// عمداً صفر نمی‌شود و داخل مفاد پنهان نمی‌شود.
    /// </summary>
    public decimal UnreconciledResidualUsd => decimal.Round(
        Partners.Sum(p => p.NetPositionUsd),
        2,
        MidpointRounding.AwayFromZero);
}

public sealed record PartnershipSettlementRow(
    int Id,
    DateTime SettlementDate,
    int FromPartnerId,
    string FromPartnerName,
    int ToPartnerId,
    string ToPartnerName,
    int? ContractId,
    string? ContractLabel,
    decimal Amount,
    string Currency,
    decimal AmountUsd,
    string? Reference,
    string? Description,
    bool IsReversed,
    decimal RunningBalanceAfterUsd);

public sealed record PartnershipContractOption(int ContractId, string ContractLabel, bool IsSelected);

public sealed record PartnershipStatement(
    int PartnerAId,
    string PartnerAName,
    int PartnerBId,
    string PartnerBName,
    IReadOnlyList<PartnershipContractOption> ContractOptions,
    IReadOnlyList<PartnershipContractStatement> Contracts,
    IReadOnlyList<PartnershipPartnerTotals> Totals,
    IReadOnlyList<PartnershipSettlementRow> Settlements,
    int? DebtorPartnerId,
    string? DebtorPartnerName,
    int? CreditorPartnerId,
    string? CreditorPartnerName,
    decimal AmountDueUsd,
    decimal CreditorClaimUsd,
    decimal UnreconciledResidualUsd);

public sealed record PartnershipPairOption(
    int PartnerAId,
    string PartnerAName,
    int PartnerBId,
    string PartnerBName,
    int ContractCount);

/// <summary>جهتِ مانده یک شریک — همان چیزی که پروفایل با جمله نشان می‌دهد.</summary>
public enum PartnerBalanceDirection
{
    Settled = 0,
    Creditor = 1,
    Debtor = 2
}

public sealed record PartnerCoPartner(int PartnerId, string PartnerName);

/// <summary>وضعیت همین شریک در یک قرارداد شراکتی.</summary>
public sealed record PartnerContractPosition(
    int ContractId,
    string ContractNumber,
    string ContractLabel,
    string Currency,
    decimal SharePercent,
    decimal FundingUsd,
    decimal ProceedsHeldUsd,
    decimal ProfitShareUsd,
    decimal SettlementsPaidUsd,
    decimal SettlementsReceivedUsd,
    decimal NetPositionUsd,
    IReadOnlyList<PartnerCoPartner> CoPartners)
{
    /// <summary>سهمِ این شریک از هزینهٔ کالای هنوز فروخته‌نشدهٔ همین قرارداد.</summary>
    public decimal UnsoldCostShareUsd { get; init; }
}

/// <summary>
/// یک رویداد در گردش حساب شریک. <paramref name="EffectUsd"/> اثرِ علامت‌دار روی مانده است
/// (مثبت = به نفع شریک) و <paramref name="RunningBalanceUsd"/> جمعِ تجمعی همان اثرهاست،
/// پس آخرین ردیف دقیقاً همان «مانده فعلی» خلاصه حساب می‌شود. این دو، علامتِ داخلیِ سیستم‌اند؛
/// ستون‌های بدهکار/بستانکار و «مانده»ِ نمایشی از نگاشتِ پایین می‌آیند و قرینهٔ آن‌اند.
/// </summary>
public sealed record PartnerAccountEntry(
    DateTime? Date,
    int? ContractId,
    string? ContractLabel,
    string Description,
    PartnershipStatementLineKind Kind,
    decimal AmountUsd,
    decimal EffectUsd,
    decimal RunningBalanceUsd,
    string? Reference,
    string SourceType,
    int? SourceId,
    /// <summary>شریکِ طرفِ همین ردیف — برای تسویه‌ها. ردیف‌های دیگر طرف مقابل ندارند.</summary>
    int? CounterpartyPartnerId = null,
    /// <summary>مقدار MT همان سند، اگر سند دارد. پرداخت و تسویه ندارند و null می‌مانند.</summary>
    decimal? QuantityMt = null,
    /// <summary>نرخ همان سند، اگر سند دارد.</summary>
    decimal? UnitPriceUsd = null)
{
    // ── نگاشتِ نمایشیِ دفترِ حسابدار ──
    // علامتِ داخلی (EffectUsd/NetPositionUsd) دست‌نخورده می‌ماند؛ فقط ستون‌های صورت‌حساب
    // به قاعدهٔ همان دفترِ کاغذی نوشته می‌شوند: آنچه شریک آورده (پرداخت، مصرف، سهم مفاد)
    // بدهکار است و آنچه به شریک رسیده (تسویهٔ پرداخت‌شده، عاید فروش نزد شریک) بستانکار.
    // مانده = بستانکار − بدهکار، پس دقیقاً معکوسِ اثرِ داخلی است.

    /// <summary>ستون بدهکار: آنچه شریک به حساب گذاشته. هر ردیف فقط یکی از دو ستون را پر می‌کند.</summary>
    public decimal DebitUsd => EffectUsd > 0m ? EffectUsd : 0m;

    /// <summary>ستون بستانکار: آنچه به شریک رسیده، به‌صورت مثبت نمایش داده می‌شود.</summary>
    public decimal CreditUsd => EffectUsd < 0m ? -EffectUsd : 0m;

    /// <summary>مانده تجمعیِ صورت‌حساب به قاعدهٔ بستانکار − بدهکار — همان ستون «مانده».</summary>
    public decimal AccountantBalanceUsd => -RunningBalanceUsd;
}

/// <summary>
/// صورت‌حساب یک شریک در همهٔ قراردادهای شراکتی‌اش — همان فرمول و همان علامتِ
/// صورت‌حساب شراکت، فقط از دید یک شریک.
/// </summary>
public sealed record PartnerAccountStatement(
    int PartnerId,
    string PartnerName,
    decimal FundingUsd,
    decimal ProceedsHeldUsd,
    decimal ProfitShareUsd,
    decimal SettlementsPaidUsd,
    decimal SettlementsReceivedUsd,
    decimal NetPositionUsd,
    PartnerBalanceDirection Direction,
    decimal AmountUsd,
    DateTime? LastActivityDate,
    IReadOnlyList<PartnershipContractOption> ContractOptions,
    IReadOnlyList<PartnerContractPosition> Contracts,
    IReadOnlyList<PartnerCoPartner> CoPartners,
    IReadOnlyList<PartnerAccountEntry> Entries)
{
    /// <summary>سهمِ این شریک از هزینهٔ کالای هنوز فروخته‌نشده، در همهٔ قراردادها.</summary>
    public decimal UnsoldCostShareUsd { get; init; }

    /// <summary>جمع بستانکار — از همان اثرهای ردیف‌ها، نه فرمول تازه.</summary>
    public decimal TotalCreditUsd => decimal.Round(
        Entries.Sum(e => e.CreditUsd), 2, MidpointRounding.AwayFromZero);

    /// <summary>جمع بدهکار — از همان اثرهای ردیف‌ها. بستانکار منهای بدهکار = مانده.</summary>
    public decimal TotalDebitUsd => decimal.Round(
        Entries.Sum(e => e.DebitUsd), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// ماندهٔ نهاییِ صورت‌حساب = بستانکار − بدهکار. همان عددِ آخرین ردیفِ گردش حساب و
    /// قرینهٔ <see cref="NetPositionUsd"/> است، چون نگاشتِ بدهکار/بستانکار قاعدهٔ دفترِ
    /// حسابدار را دنبال می‌کند و علامتِ داخلی دست‌نخورده می‌ماند.
    /// </summary>
    public decimal AccountantBalanceUsd => decimal.Round(
        TotalCreditUsd - TotalDebitUsd, 2, MidpointRounding.AwayFromZero);
}

public interface IPartnershipStatementService
{
    Task<IReadOnlyList<PartnershipPairOption>> ListPairsAsync(CancellationToken ct = default);

    Task<PartnershipStatement?> BuildAsync(
        int partnerAId,
        int partnerBId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// وضعیت یک شریک در همهٔ قراردادهای شراکتی‌اش. تنها منبعِ ارقامِ پروفایل شریک.
    /// </summary>
    Task<PartnerAccountStatement?> BuildForPartnerAsync(
        int partnerId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// همان صورت‌حسابِ <see cref="BuildForPartnerAsync"/> برای چند شریک با یک بار خواندن
    /// دادهٔ مشترک. فرمول یکی است: نسخهٔ تک‌شریک خودش همین را با یک شناسه صدا می‌زند، پس
    /// دو محاسبهٔ جدا وجود ندارد که بتوانند از هم جدا بیفتند.
    ///
    /// خواننده‌ای که مانده چند شریک را می‌خواهد باید از این استفاده کند، نه از حلقه روی
    /// <see cref="BuildForPartnerAsync"/>؛ آن حلقه به ازای هر شریک کلِ دادهٔ قراردادهای
    /// مشترک را دوباره می‌خواند.
    /// </summary>
    Task<IReadOnlyDictionary<int, PartnerAccountStatement>> BuildForPartnersAsync(
        IReadOnlyCollection<int> partnerIds,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// یک قرارداد شراکتی و سهم همهٔ اعضایش — همان ارقامی که صورت‌حساب دونفره و پروفایل شریک
    /// نشان می‌دهند، بدون اینکه لازم باشد جفتِ شرکا از قبل معلوم باشد. ثبتِ تخصیص سود در دفتر
    /// کل از همین می‌خواند تا عددِ ژورنال و عددِ صورت‌حساب از یک محاسبه بیایند.
    /// </summary>
    Task<PartnershipContractStatement?> BuildForContractAsync(
        int contractId,
        CancellationToken ct = default);
}

/// <summary>
/// صورت‌حساب شراکت بین دو شریک.
///
/// هیچ Revenue و هیچ Expense تازه‌ای نمی‌سازد: فروش از <see cref="SalesTransaction"/> واقعی،
/// خرید از <see cref="IPurchaseAggregationService"/>، مصارف از <see cref="ExpenseTransaction"/>
/// و «کدام شریک واقعاً پرداخت کرد» از <see cref="PartnerFundingReader"/> می‌آید — همان منابعی
/// که پروفایل شریک و صورت‌حساب رسمی هم می‌خوانند.
///
/// مفادِ قرارداد فقط و فقط از دادهٔ عملیاتی می‌آید: «فروش − خرید − مصارف». پرداختِ شرکا
/// (<see cref="PaymentTransaction"/>) هرگز جای هزینه را در فرمول مفاد نمی‌گیرد؛ یک مفهوم
/// جداست و فقط می‌گوید هر شریک چقدر تأمین مالی کرده.
///
/// چون «جمع پرداخت شرکا» و «خرید + مصارف دفتری» لزوماً برابر نیستند، جمع مانده دو شریک
/// دقیقاً صفر نمی‌شود. آن باقیمانده به‌زور صفر نمی‌شود و داخل مفاد پنهان نمی‌شود؛ به‌صورت
/// <see cref="PartnershipContractStatement.PaymentToBookDifferenceUsd"/> و
/// <see cref="PartnershipContractStatement.UnreconciledResidualUsd"/> صریح گزارش می‌شود.
/// </summary>
public sealed class PartnershipStatementService : IPartnershipStatementService
{
    private readonly ApplicationDbContext _db;
    private readonly IProfitAndLossService _profitAndLoss;

    public PartnershipStatementService(
        ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null,
        IProfitAndLossService? profitAndLoss = null)
    {
        _db = db;
        // مفادِ قرارداد فقط از سودِ محققِ ProfitAndLossService؛ این سرویس فقط قاعدهٔ سهمِ شرکا را اعمال می‌کند.
        _profitAndLoss = profitAndLoss ?? new ProfitAndLossService(db, purchaseAggregation ?? new PurchaseAggregationService(db));
    }

    private static decimal Round(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public async Task<IReadOnlyList<PartnershipPairOption>> ListPairsAsync(CancellationToken ct = default)
    {
        var links = await _db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.Contract != null && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            .Select(cp => new
            {
                cp.ContractId,
                cp.PartnerId,
                PartnerName = cp.Partner != null ? cp.Partner.Name : string.Empty
            })
            .ToListAsync(ct);

        var pairs = new Dictionary<(int A, int B), (string AName, string BName, HashSet<int> Contracts)>();
        foreach (var group in links.GroupBy(l => l.ContractId))
        {
            var members = group.DistinctBy(m => m.PartnerId).OrderBy(m => m.PartnerId).ToList();
            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    var key = (members[i].PartnerId, members[j].PartnerId);
                    if (!pairs.TryGetValue(key, out var entry))
                    {
                        entry = (members[i].PartnerName, members[j].PartnerName, []);
                        pairs[key] = entry;
                    }

                    entry.Contracts.Add(group.Key);
                }
            }
        }

        return pairs
            .Select(p => new PartnershipPairOption(
                p.Key.A,
                p.Value.AName,
                p.Key.B,
                p.Value.BName,
                p.Value.Contracts.Count))
            .OrderByDescending(p => p.ContractCount)
            .ThenBy(p => p.PartnerAId)
            .ToList();
    }

    public async Task<PartnershipStatement?> BuildAsync(
        int partnerAId,
        int partnerBId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default)
    {
        if (partnerAId <= 0 || partnerBId <= 0 || partnerAId == partnerBId)
        {
            return null;
        }

        var partners = await _db.Partners
            .AsNoTracking()
            .Where(p => p.Id == partnerAId || p.Id == partnerBId)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        if (partners.Count != 2)
        {
            return null;
        }

        var nameById = partners.ToDictionary(p => p.Id, p => p.Name);

        // قراردادهای شراکتیِ مشترکِ همین دو شریک — هیچ شناسه‌ای هاردکد نیست.
        var sharedLinks = await LoadMemberLinksAsync(
            cp => cp.PartnerId == partnerAId || cp.PartnerId == partnerBId,
            ct);

        var sharedContracts = sharedLinks
            .GroupBy(l => l.ContractId)
            .Where(g => g.Any(x => x.PartnerId == partnerAId) && g.Any(x => x.PartnerId == partnerBId))
            .OrderBy(g => g.First().ContractNumber, StringComparer.Ordinal)
            .ToList();

        var allContractIds = sharedContracts.Select(g => g.Key).ToList();
        var selectedIds = contractIds is { Count: > 0 }
            ? allContractIds.Where(contractIds.Contains).ToList()
            : allContractIds;
        if (selectedIds.Count == 0)
        {
            selectedIds = allContractIds;
        }

        var options = sharedContracts
            .Select(g => new PartnershipContractOption(
                g.Key,
                Contract.BuildDisplayLabel(g.First().ContractName, g.First().ContractNumber),
                selectedIds.Contains(g.Key)))
            .ToList();

        var selected = sharedContracts.Where(g => selectedIds.Contains(g.Key)).ToList();

        var contractStatements = await BuildContractStatementsAsync(selected, nameById, ct);

        var settlements = await LoadSettlementsAsync(
            partnerAId, partnerBId, selectedIds, allContractIds, nameById, ct);

        var totals = new[] { partnerAId, partnerBId }
            .Select(pid =>
            {
                var rows = contractStatements
                    .SelectMany(c => c.Partners)
                    .Where(p => p.PartnerId == pid)
                    .ToList();
                var paid = Round(settlements
                    .Where(s => !s.IsReversed && s.FromPartnerId == pid)
                    .Sum(s => s.AmountUsd));
                var received = Round(settlements
                    .Where(s => !s.IsReversed && s.ToPartnerId == pid)
                    .Sum(s => s.AmountUsd));

                return new PartnershipPartnerTotals(
                    PartnerId: pid,
                    PartnerName: nameById.GetValueOrDefault(pid) ?? string.Empty,
                    SharePercent: rows.Count == 0 ? 0m : rows.Average(r => r.SharePercent),
                    FundingUsd: Round(rows.Sum(r => r.FundingUsd)),
                    ProceedsHeldUsd: Round(rows.Sum(r => r.ProceedsHeldUsd)),
                    ProfitShareUsd: Round(rows.Sum(r => r.ProfitShareUsd)),
                    SettlementsPaidUsd: paid,
                    SettlementsReceivedUsd: received,
                    // PTG ۱۲-C — جمعِ چند قرارداد: اگر حتی یکیِ آن‌ها چند بازه داشته، عدد ترکیبی است.
                    SharePeriodCount: rows.Count == 0 ? 1 : rows.Max(r => r.SharePeriodCount))
                {
                    UnsoldCostShareUsd = Round(rows.Sum(r => r.UnsoldCostShareUsd))
                };
            })
            .ToList();

        var settlement = ResolveSettlement(totals.Select(t => (t.PartnerId, t.NetPositionUsd)).ToList());
        var debtor = totals.FirstOrDefault(t => t.PartnerId == settlement.DebtorPartnerId);
        var creditor = totals.FirstOrDefault(t => t.PartnerId == settlement.CreditorPartnerId);
        var amountDue = settlement.AmountDueUsd;
        var creditorClaim = creditor is null ? 0m : Round(creditor.NetPositionUsd);
        var unreconciledResidual = Round(totals.Sum(t => t.NetPositionUsd));
        var hasDirection = debtor is not null && creditor is not null && amountDue > 0m;

        return new PartnershipStatement(
            PartnerAId: partnerAId,
            PartnerAName: nameById.GetValueOrDefault(partnerAId) ?? string.Empty,
            PartnerBId: partnerBId,
            PartnerBName: nameById.GetValueOrDefault(partnerBId) ?? string.Empty,
            ContractOptions: options,
            Contracts: contractStatements,
            Totals: totals,
            Settlements: settlements,
            DebtorPartnerId: hasDirection ? debtor!.PartnerId : null,
            DebtorPartnerName: hasDirection ? debtor!.PartnerName : null,
            CreditorPartnerId: hasDirection ? creditor!.PartnerId : null,
            CreditorPartnerName: hasDirection ? creditor!.PartnerName : null,
            AmountDueUsd: hasDirection ? amountDue : 0m,
            CreditorClaimUsd: hasDirection ? creditorClaim : 0m,
            UnreconciledResidualUsd: unreconciledResidual);
    }

    /// <summary>
    /// جهتِ تسویه بین دو شریک فقط وقتی هست که یکی مانده مثبت (طلب) و دیگری مانده منفی (بدهی) دارد.
    /// مبلغ = کوچک‌ترِ بدهیِ بدهکار و طلبِ طلبکار؛ بیش از آن را هیچ‌کدام نمی‌تواند از دیگری بخواهد.
    /// اگر هر دو منفی یا هر دو مثبت‌اند (مثلاً هر دو سهمِ خود از هزینهٔ پرداخت‌نشده را دارند)، هیچ‌کدام
    /// به دیگری بدهکار نیست؛ یک سنتِ گرد کردن جهت نمی‌سازد.
    /// </summary>
    internal static (int? DebtorPartnerId, int? CreditorPartnerId, decimal AmountDueUsd) ResolveSettlement(
        IReadOnlyList<(int PartnerId, decimal NetPositionUsd)> positions)
    {
        var debtor = positions.Where(p => p.NetPositionUsd < 0m).OrderBy(p => p.NetPositionUsd).FirstOrDefault();
        var creditor = positions.Where(p => p.NetPositionUsd > 0m).OrderByDescending(p => p.NetPositionUsd).FirstOrDefault();
        if (debtor == default || creditor == default || debtor.PartnerId == creditor.PartnerId)
        {
            return (null, null, 0m);
        }

        var amountDue = Round(Math.Min(Math.Abs(debtor.NetPositionUsd), creditor.NetPositionUsd));
        return amountDue > 0m ? (debtor.PartnerId, creditor.PartnerId, amountDue) : (null, null, 0m);
    }


    /// <summary>
    /// وضعیت یک شریک در همهٔ قراردادهای شراکتی‌اش.
    ///
    /// هیچ فرمول تازه‌ای اینجا نیست: قرارداد به قرارداد از همان
    /// <see cref="BuildContractStatementsAsync"/> می‌خواند و مانده را با همان تعریفِ
    /// <see cref="PartnershipPartnerTotals.NetPositionUsd"/> می‌سازد. برای همین عددِ پروفایل
    /// و عددِ صورت‌حساب شراکت هرگز از هم جدا نمی‌شوند.
    /// </summary>
    public async Task<PartnershipContractStatement?> BuildForContractAsync(
        int contractId,
        CancellationToken ct = default)
    {
        if (contractId <= 0)
        {
            return null;
        }

        // همان مسیرِ ساختِ صورت‌حساب، فقط با دامنهٔ یک قرارداد. هیچ محاسبهٔ موازی‌ای اینجا نیست.
        var links = await LoadMemberLinksAsync(cp => cp.ContractId == contractId, ct);
        if (links.Count == 0)
        {
            return null;
        }

        var memberIds = links.Select(l => l.PartnerId).Distinct().ToList();
        var nameById = (await _db.Partners
                .AsNoTracking()
                .Where(p => memberIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(ct))
            .ToDictionary(p => p.Id, p => p.Name);

        var statements = await BuildContractStatementsAsync(
            links.GroupBy(l => l.ContractId).ToList(),
            nameById,
            ct);

        return statements.SingleOrDefault();
    }

    public async Task<PartnerAccountStatement?> BuildForPartnerAsync(
        int partnerId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default)
    {
        if (partnerId <= 0)
        {
            return null;
        }

        // تک‌شریک همان مسیر چندشریک است با یک شناسه. عمداً فرمول جداگانه‌ای ندارد تا
        // پروفایل شریک و گزارش‌های مانده هرگز دو عدد متفاوت ندهند.
        var statements = await BuildForPartnersAsync([partnerId], contractIds, ct);
        return statements.GetValueOrDefault(partnerId);
    }

    public async Task<IReadOnlyDictionary<int, PartnerAccountStatement>> BuildForPartnersAsync(
        IReadOnlyCollection<int> partnerIds,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partnerIds);

        var result = new Dictionary<int, PartnerAccountStatement>();
        var requestedIds = partnerIds.Where(id => id > 0).Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            return result;
        }

        var partners = await _db.Partners
            .AsNoTracking()
            .Where(p => requestedIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        if (partners.Count == 0)
        {
            return result;
        }

        // عضویتِ همهٔ شرکای خواسته‌شده با یک کوئری. هر شریک بعداً فقط قراردادهای خودش
        // را می‌بیند، پس دامنهٔ محاسبهٔ او دقیقاً همان دامنهٔ مسیر تک‌شریک است.
        var membership = await _db.ContractPartners
            .AsNoTracking()
            .Where(cp => requestedIds.Contains(cp.PartnerId)
                && cp.Contract != null
                && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            .Select(cp => new { cp.PartnerId, cp.ContractId })
            .Distinct()
            .ToListAsync(ct);

        var contractIdsByPartner = membership
            .GroupBy(m => m.PartnerId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.ContractId).Distinct().ToList());

        // همهٔ اعضای همان قراردادها (برای نام شریک مقابل) — یک بار برای اجتماع قراردادها.
        var unionContractIds = membership.Select(m => m.ContractId).Distinct().ToList();
        var allLinks = unionContractIds.Count == 0
            ? []
            : await LoadMemberLinksAsync(cp => unionContractIds.Contains(cp.ContractId), ct);

        var memberIds = allLinks.Select(l => l.PartnerId).Distinct().ToList();
        var nameById = memberIds.Count == 0
            ? new Dictionary<int, string>()
            : (await _db.Partners
                .AsNoTracking()
                .Where(p => memberIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(ct))
                .ToDictionary(p => p.Id, p => p.Name);

        var groupsByPartner = new Dictionary<int, List<IGrouping<int, ContractMemberLink>>>();
        var selectedByPartner = new Dictionary<int, List<int>>();
        foreach (var partner in partners)
        {
            var ownContractIds = contractIdsByPartner.GetValueOrDefault(partner.Id) ?? [];
            var contractGroups = allLinks
                .Where(l => ownContractIds.Contains(l.ContractId))
                .GroupBy(l => l.ContractId)
                .OrderBy(g => g.First().ContractNumber, StringComparer.Ordinal)
                .ToList();
            groupsByPartner[partner.Id] = contractGroups;

            var allContractIds = contractGroups.Select(g => g.Key).ToList();
            var selectedIds = contractIds is { Count: > 0 }
                ? allContractIds.Where(contractIds.Contains).ToList()
                : allContractIds;
            if (selectedIds.Count == 0)
            {
                selectedIds = allContractIds;
            }
            selectedByPartner[partner.Id] = selectedIds;
        }

        // محاسبهٔ هر قرارداد فقط یک بار. ریاضیِ قرارداد به شریکِ پرسنده وابسته نیست، پس
        // نتیجهٔ مشترک همان چیزی است که مسیر تک‌شریک جداگانه می‌ساخت.
        var unionSelectedIds = selectedByPartner.Values.SelectMany(ids => ids).Distinct().ToList();
        var unionSelectedGroups = allLinks
            .Where(l => unionSelectedIds.Contains(l.ContractId))
            .GroupBy(l => l.ContractId)
            .OrderBy(g => g.First().ContractNumber, StringComparer.Ordinal)
            .ToList();
        var statementByContract = (await BuildContractStatementsAsync(unionSelectedGroups, nameById, ct))
            .ToDictionary(statement => statement.ContractId);

        var allSettlements = await LoadPartnerSettlementRowsAsync(requestedIds, ct);

        foreach (var partner in partners)
        {
            var partnerId = partner.Id;
            var links = allLinks
                .Where(l => (contractIdsByPartner.GetValueOrDefault(partnerId) ?? []).Contains(l.ContractId))
                .ToList();
            var contractGroups = groupsByPartner[partnerId];
            var allContractIds = contractGroups.Select(g => g.Key).ToList();
            var selectedIds = selectedByPartner[partnerId];

            var options = contractGroups
                .Select(g => new PartnershipContractOption(
                    g.Key,
                    Contract.BuildDisplayLabel(g.First().ContractName, g.First().ContractNumber),
                    selectedIds.Contains(g.Key)))
                .ToList();

            // همان ترتیبِ مسیر تک‌شریک: قراردادهای انتخاب‌شده به ترتیب شمارهٔ قرارداد.
            var contractStatements = contractGroups
                .Where(g => selectedIds.Contains(g.Key))
                .Select(g => statementByContract.GetValueOrDefault(g.Key))
                .Where(statement => statement is not null)
                .Select(statement => statement!)
                .ToList();

            // تسویهٔ بدون قرارداد، تسویهٔ کلیِ حساب است و فقط در نمای «همهٔ قراردادها» شمرده می‌شود.
            var selectedSet = selectedIds.ToHashSet();
            var showsEveryContract = selectedSet.SetEquals(allContractIds);
            var settlements = allSettlements
                .Where(s => s.FromPartnerId == partnerId || s.ToPartnerId == partnerId)
                .Where(s => s.ContractId.HasValue
                    ? selectedSet.Contains(s.ContractId.Value)
                    : showsEveryContract)
                .ToList();

            var coPartners = links
                .Where(l => selectedIds.Contains(l.ContractId) && l.PartnerId != partnerId)
                .Select(l => l.PartnerId)
                .Distinct()
                .Select(id => new PartnerCoPartner(id, nameById.GetValueOrDefault(id) ?? string.Empty))
                .OrderBy(c => c.PartnerName, StringComparer.Ordinal)
                .ToList();

            var positions = new List<PartnerContractPosition>();
            foreach (var contract in contractStatements)
            {
                var own = contract.Partners.FirstOrDefault(x => x.PartnerId == partnerId);
                if (own is null)
                {
                    continue;
                }

                var paid = Round(settlements
                    .Where(x => !x.IsReversed && x.ContractId == contract.ContractId && x.FromPartnerId == partnerId)
                    .Sum(x => x.AmountUsd));
                var received = Round(settlements
                    .Where(x => !x.IsReversed && x.ContractId == contract.ContractId && x.ToPartnerId == partnerId)
                    .Sum(x => x.AmountUsd));
                var withSettlements = own with { SettlementsPaidUsd = paid, SettlementsReceivedUsd = received };

                positions.Add(new PartnerContractPosition(
                    ContractId: contract.ContractId,
                    ContractNumber: contract.ContractNumber,
                    ContractLabel: contract.ContractLabel,
                    Currency: contract.Currency,
                    SharePercent: own.SharePercent,
                    FundingUsd: own.FundingUsd,
                    ProceedsHeldUsd: own.ProceedsHeldUsd,
                    ProfitShareUsd: own.ProfitShareUsd,
                    SettlementsPaidUsd: paid,
                    SettlementsReceivedUsd: received,
                    NetPositionUsd: withSettlements.NetPositionUsd,
                    CoPartners: contract.Partners
                        .Where(x => x.PartnerId != partnerId)
                        .Select(x => new PartnerCoPartner(x.PartnerId, x.PartnerName))
                        .ToList())
                {
                    UnsoldCostShareUsd = own.UnsoldCostShareUsd
                });
            }

            var settlementsPaidUsd = Round(settlements
                .Where(x => !x.IsReversed && x.FromPartnerId == partnerId)
                .Sum(x => x.AmountUsd));
            var settlementsReceivedUsd = Round(settlements
                .Where(x => !x.IsReversed && x.ToPartnerId == partnerId)
                .Sum(x => x.AmountUsd));

            var totals = new PartnershipPartnerTotals(
                PartnerId: partnerId,
                PartnerName: partner.Name,
                SharePercent: positions.Count == 0 ? 0m : positions.Average(p => p.SharePercent),
                FundingUsd: Round(positions.Sum(p => p.FundingUsd)),
                ProceedsHeldUsd: Round(positions.Sum(p => p.ProceedsHeldUsd)),
                ProfitShareUsd: Round(positions.Sum(p => p.ProfitShareUsd)),
                SettlementsPaidUsd: settlementsPaidUsd,
                SettlementsReceivedUsd: settlementsReceivedUsd,
                // PTG ۱۲-C — پروفایل شریک هم باید بداند عددِ مفاد ترکیبی است یا نه.
                SharePeriodCount: contractStatements.Count == 0
                    ? 1
                    : contractStatements
                        .SelectMany(c => c.Partners)
                        .Where(x => x.PartnerId == partnerId)
                        .Select(x => x.SharePeriodCount)
                        .DefaultIfEmpty(1)
                        .Max())
            {
                UnsoldCostShareUsd = Round(positions.Sum(p => p.UnsoldCostShareUsd))
            };

            var entries = BuildPartnerEntries(partnerId, contractStatements, positions, settlements);

            var net = totals.NetPositionUsd;
            var direction = net > 0m
                ? PartnerBalanceDirection.Creditor
                : net < 0m
                    ? PartnerBalanceDirection.Debtor
                    : PartnerBalanceDirection.Settled;

            result[partnerId] = new PartnerAccountStatement(
                PartnerId: partnerId,
                PartnerName: partner.Name,
                FundingUsd: totals.FundingUsd,
                ProceedsHeldUsd: totals.ProceedsHeldUsd,
                ProfitShareUsd: totals.ProfitShareUsd,
                SettlementsPaidUsd: totals.SettlementsPaidUsd,
                SettlementsReceivedUsd: totals.SettlementsReceivedUsd,
                NetPositionUsd: net,
                Direction: direction,
                AmountUsd: Round(Math.Abs(net)),
                LastActivityDate: entries.Where(e => e.Date.HasValue).Select(e => e.Date).DefaultIfEmpty(null).Max(),
                ContractOptions: options,
                Contracts: positions,
                CoPartners: coPartners,
                Entries: entries)
            {
                UnsoldCostShareUsd = totals.UnsoldCostShareUsd
            };
        }

        return result;
    }

    /// <summary>
    /// گردش حساب شریک. اثرِ هر ردیف دقیقاً همان جزءِ فرمول مانده است، بنابراین ماندهٔ تجمعیِ
    /// آخرین ردیف با «مانده فعلی» خلاصه حساب یکی می‌شود.
    /// </summary>
    private static List<PartnerAccountEntry> BuildPartnerEntries(
        int partnerId,
        IReadOnlyList<PartnershipContractStatement> contracts,
        IReadOnlyList<PartnerContractPosition> positions,
        IReadOnlyList<PartnerSettlementRecord> settlements)
    {
        var draft = new List<(DateTime? Date, int? ContractId, string? Label, string Description,
            PartnershipStatementLineKind Kind, decimal Amount, decimal Effect, string SourceType, int? SourceId,
            string? Reference, int? CounterpartyPartnerId, decimal? QuantityMt, decimal? UnitPriceUsd)>();

        foreach (var contract in contracts)
        {
            var partnerLines = contract.Lines.Where(l => l.PartnerId == partnerId).ToList();
            foreach (var line in partnerLines.Where(l => l.Kind != PartnershipStatementLineKind.SaleProceedsHeld))
            {
                draft.Add((
                    line.Date,
                    contract.ContractId,
                    contract.ContractLabel,
                    line.Title,
                    line.Kind,
                    Math.Abs(line.AmountUsd),
                    line.AmountUsd,
                    line.Source == PartnershipStatementSources.Sale ? "Sale" : "Payment",
                    line.RecordId,
                    line.Reference,
                    null,
                    line.QuantityMt,
                    line.UnitPriceUsd));
            }

            AppendPartnerSaleProceedsDraft(
                draft,
                contract.ContractId,
                contract.ContractLabel,
                partnerLines.Where(l => l.Kind == PartnershipStatementLineKind.SaleProceedsHeld).ToList());

            var position = positions.FirstOrDefault(p => p.ContractId == contract.ContractId);
            if (position is null)
            {
                continue;
            }

            if (position.UnsoldCostShareUsd != 0m)
            {
                // هزینهٔ کالای هنوز فروخته‌نشده تاریخِ تحقق ندارد؛ بی‌تاریخ و جدا از مفاد می‌ماند.
                draft.Add((
                    null,
                    contract.ContractId,
                    contract.ContractLabel,
                    $"سهم {position.SharePercent:0.##}٪ از هزینهٔ کالای هنوز فروخته‌نشده",
                    PartnershipStatementLineKind.UnsoldCostShare,
                    Math.Abs(position.UnsoldCostShareUsd),
                    -position.UnsoldCostShareUsd,
                    "Contract",
                    contract.ContractId,
                    null,
                    null,
                    null,
                    null));
            }

            if (position.ProfitShareUsd == 0m)
            {
                continue;
            }

            // سهم مفاد در تاریخ آخرین فروشِ همان قرارداد دیده می‌شود؛ اگر فروشی نیست، بی‌تاریخ می‌ماند.
            var profitDate = contract.Lines
                .Where(l => l.Kind == PartnershipStatementLineKind.SaleProceedsHeld && l.Date.HasValue)
                .Select(l => l.Date)
                .DefaultIfEmpty(null)
                .Max();

            draft.Add((
                profitDate,
                contract.ContractId,
                contract.ContractLabel,
                $"سهم مفاد {position.SharePercent:0.##}٪ از مفاد محقق قرارداد",
                PartnershipStatementLineKind.ProfitShare,
                Math.Abs(position.ProfitShareUsd),
                position.ProfitShareUsd,
                "Contract",
                contract.ContractId,
                null,
                null,
                null,
                null));
        }

        foreach (var settlement in settlements)
        {
            var isPayer = settlement.FromPartnerId == partnerId;
            var effect = settlement.IsReversed
                ? 0m
                : isPayer ? settlement.AmountUsd : -settlement.AmountUsd;
            var counterparty = isPayer ? settlement.ToPartnerName : settlement.FromPartnerName;
            var description = string.IsNullOrWhiteSpace(settlement.Description)
                ? isPayer ? $"تسویه پرداخت‌شده به {counterparty}" : $"تسویه دریافت‌شده از {counterparty}"
                : settlement.Description!;

            draft.Add((
                settlement.SettlementDate,
                settlement.ContractId,
                settlement.ContractLabel,
                settlement.IsReversed ? $"{description} — برگشت‌خورده" : description,
                settlement.IsReversed
                    ? PartnershipStatementLineKind.Adjustment
                    : PartnershipStatementLineKind.PartnerSettlement,
                settlement.AmountUsd,
                effect,
                "Settlement",
                settlement.Id,
                settlement.Reference,
                isPayer ? settlement.ToPartnerId : settlement.FromPartnerId,
                null,
                null));
        }

        var ordered = draft
            .OrderBy(d => d.Date.HasValue ? 0 : 1)
            .ThenBy(d => d.Date ?? DateTime.MaxValue)
            .ThenBy(d => d.ContractId ?? int.MaxValue)
            .ThenBy(d => d.SourceId ?? int.MaxValue)
            .ToList();

        var entries = new List<PartnerAccountEntry>(ordered.Count);
        var running = 0m;
        foreach (var row in ordered)
        {
            running += row.Effect;
            entries.Add(new PartnerAccountEntry(
                Date: row.Date,
                ContractId: row.ContractId,
                ContractLabel: row.Label,
                Description: row.Description,
                Kind: row.Kind,
                AmountUsd: decimal.Round(row.Amount, 2, MidpointRounding.AwayFromZero),
                EffectUsd: decimal.Round(row.Effect, 2, MidpointRounding.AwayFromZero),
                RunningBalanceUsd: decimal.Round(running, 2, MidpointRounding.AwayFromZero),
                Reference: row.Reference,
                SourceType: row.SourceType,
                SourceId: row.SourceId,
                CounterpartyPartnerId: row.CounterpartyPartnerId,
                QuantityMt: row.QuantityMt,
                UnitPriceUsd: row.UnitPriceUsd));
        }

        return entries;
    }

    /// <summary>
    /// عاید فروش در گردش حساب شریک به‌صورت خلاصهٔ هر قرارداد دیده می‌شود؛ جزئیات هر فاکتور
    /// همان‌جا که بود در صورت‌حساب شراکت/قرارداد باقی می‌ماند. جمع‌ها همان فرمول مانده‌اند.
    /// </summary>
    private static void AppendPartnerSaleProceedsDraft(
        List<(DateTime? Date, int? ContractId, string? Label, string Description,
            PartnershipStatementLineKind Kind, decimal Amount, decimal Effect, string SourceType, int? SourceId,
            string? Reference, int? CounterpartyPartnerId, decimal? QuantityMt, decimal? UnitPriceUsd)> draft,
        int contractId,
        string contractLabel,
        IReadOnlyList<PartnershipStatementLine> saleLines)
    {
        if (saleLines.Count == 0)
        {
            return;
        }

        if (saleLines.Count == 1)
        {
            var line = saleLines[0];
            var amount = Math.Abs(line.AmountUsd);
            draft.Add((
                line.Date,
                contractId,
                contractLabel,
                line.Title,
                PartnershipStatementLineKind.SaleProceedsHeld,
                amount,
                -amount,
                "Sale",
                line.RecordId,
                line.Reference,
                null,
                line.QuantityMt,
                line.UnitPriceUsd));
            return;
        }

        var totalAmount = Round(saleLines.Sum(l => Math.Abs(l.AmountUsd)));
        var totalQuantityMt = saleLines.Sum(l => l.QuantityMt ?? 0m);
        var lastSaleDate = saleLines
            .Where(l => l.Date.HasValue)
            .Select(l => l.Date)
            .DefaultIfEmpty(null)
            .Max();

        draft.Add((
            lastSaleDate,
            contractId,
            contractLabel,
            $"جمع عاید فروش ({saleLines.Count:N0} فروش)",
            PartnershipStatementLineKind.SaleProceedsHeld,
            totalAmount,
            -totalAmount,
            "Contract",
            contractId,
            null,
            null,
            totalQuantityMt == 0m ? null : Round(totalQuantityMt),
            totalQuantityMt == 0m ? null : Round(totalAmount / totalQuantityMt)));
    }

    private sealed record PartnerSettlementRecord(
        int Id,
        DateTime SettlementDate,
        int FromPartnerId,
        string FromPartnerName,
        int ToPartnerId,
        string ToPartnerName,
        int? ContractId,
        string? ContractLabel,
        decimal AmountUsd,
        string? Reference,
        string? Description,
        bool IsReversed);

    /// <summary>
    /// تسویهٔ همهٔ شرکای خواسته‌شده با یک کوئری، به همان ترتیبِ قبلی (تاریخ، بعد شناسه).
    /// جداکردنِ سهم هر شریک و قاعدهٔ «تسویهٔ بدون قرارداد» سرِ جای خودش در
    /// <see cref="BuildForPartnersAsync"/> اعمال می‌شود.
    /// </summary>
    private async Task<List<PartnerSettlementRecord>> LoadPartnerSettlementRowsAsync(
        IReadOnlyCollection<int> partnerIds,
        CancellationToken ct)
    {
        var rows = await _db.PartnerSettlements
            .AsNoTracking()
            .Include(s => s.Contract)
            .Include(s => s.FromPartner)
            .Include(s => s.ToPartner)
            .Where(s => partnerIds.Contains(s.FromPartnerId) || partnerIds.Contains(s.ToPartnerId))
            .OrderBy(s => s.SettlementDate)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        return rows
            .Select(s => new PartnerSettlementRecord(
                s.Id,
                s.SettlementDate,
                s.FromPartnerId,
                s.FromPartner?.Name ?? string.Empty,
                s.ToPartnerId,
                s.ToPartner?.Name ?? string.Empty,
                s.ContractId,
                s.Contract?.DisplayLabel,
                s.AmountUsd,
                s.Reference,
                s.Description,
                s.IsReversed))
            .ToList();
    }

    /// <summary>
    /// محاسبهٔ هر قرارداد شراکتی — تنها جای این فرمول در سیستم. هم صورت‌حساب بین دو شریک
    /// و هم پروفایل یک شریک از همین‌جا می‌خوانند، بنابراین دو عددِ متفاوت ساخته نمی‌شود.
    /// </summary>
    private async Task<List<PartnershipContractStatement>> BuildContractStatementsAsync(
        IReadOnlyList<IGrouping<int, ContractMemberLink>> selected,
        IReadOnlyDictionary<int, string> nameById,
        CancellationToken ct)
    {
        var selectedIds = selected.Select(g => g.Key).ToList();
        // فروش‌ها، هزینه‌ها و مفاد همه از یک مرجع: ProfitAndLossService.BuildContractEconomicsAsync —
        // همان اعدادِ گزارش مفاد قراردادها و پروندهٔ قرارداد.
        var economicsByContract = await _profitAndLoss.BuildContractEconomicsAsync(selectedIds, ct);
        var fundingRows = await PartnerFundingReader.LoadPartnerFundedPaymentsAsync(
            _db, selectedIds, partnerId: null, toDate: null, ct);

        // PTG-P0-03 — تاریخچهٔ سهم، تنها مرجعِ «در تاریخ هر رویداد چه سهمی بود».
        var shareHistory = await ContractPartnerShareHistory.LoadAsync(_db, selectedIds, ct);

        var contractStatements = new List<PartnershipContractStatement>();
        foreach (var group in selected)
        {
            var contractId = group.Key;
            var head = group.First();

            var economics = economicsByContract.GetValueOrDefault(contractId)
                ?? new ContractEconomicsSnapshot { ContractId = contractId };
            var sales = economics.Sales
                .Select(s => new SaleRow(s.SalesTransactionId, s.SaleDate, s.InvoiceNumber, s.QuantityMt, s.AmountUsd))
                .ToList();
            var salesUsd = Round(economics.RevenueUsd);
            // هزینهٔ دفتریِ کامل (مبنای تطبیقِ پرداختِ شرکا): کلِ خرید + کلِ مصارف.
            var purchaseCostUsd = Round(economics.ContractType == ContractType.Purchase
                ? economics.PurchaseValueUsd
                : economics.RealizedCostOfGoodsSoldUsd);
            // مصارفِ پولی؛ ارزشِ ضایعه بهای همان نفتی است که در خرید آمده و دوباره شمرده نمی‌شود.
            var operationalExpenseUsd = Round(economics.RecordedExpenseCostUsd);

            var contractFunding = fundingRows.Where(f => f.ContractId == contractId).ToList();
            var fundingByPartner = contractFunding
                .GroupBy(f => f.PartnerId)
                .ToDictionary(
                    g => g.Key,
                    g => Round(g.Sum(f => f.Direction == PaymentDirection.Out ? f.AmountUsd : -f.AmountUsd)));
            var totalFundingUsd = Round(fundingByPartner.Values.Sum());

            // تنها مبنای مفاد: سودِ محققِ قرارداد (فقط بخشِ فروخته‌شده). پرداختِ شرکا اینجا هیچ نقشی ندارد.
            var bookProfitUsd = economics.RealizedNetProfitUsd;
            // مفهوم جدا: پولِ شرکا چقدر با هزینهٔ ثبت‌شدهٔ دفتر فرق دارد. صفر نمی‌شود.
            var paymentToBookDifferenceUsd = Round(totalFundingUsd - purchaseCostUsd - operationalExpenseUsd);

            var holderId = head.SaleProceedsHolderPartnerId;

            // PTG-P0-03 — سهم مفاد دیگر با درصدِ امروز روی کلِ تاریخ قرارداد اعمال نمی‌شود.
            // مفاد به نسبتِ فروشِ هر بازهٔ سهم تقسیم می‌شود (مفاد هنگام فروش محقق می‌شود)،
            // و در هر بازه همان درصدی که در آن زمان توافق شده بود. برای قراردادی که فقط یک
            // بازه دارد (همهٔ دادهٔ موجود پس از Backfill) نتیجه دقیقاً همان فرمولِ قبلی است.
            var profitByPartner = AllocateProfitBySharePeriod(
                contractId,
                bookProfitUsd,
                sales,
                shareHistory);
            // هزینهٔ کالای هنوز فروخته‌نشده (کلِ هزینهٔ دفتری منهای بخشِ فروخته‌شده) با همان قاعدهٔ سهم؛
            // جمعِ «سهم مفاد − سهم هزینهٔ فروخته‌نشده» همان چیزی است که شرکا پیش‌تر به‌عنوان مفادِ
            // کامل می‌دیدند، پس برابرسازیِ هزینه بینِ شرکا تغییر نمی‌کند.
            var unsoldCostUsd = Round(purchaseCostUsd + operationalExpenseUsd
                - Round(economics.RealizedCostOfGoodsSoldUsd)
                - Round(economics.RealizedOperationalCostUsd));
            var unsoldCostByPartner = AllocateProfitBySharePeriod(
                contractId,
                unsoldCostUsd,
                sales,
                shareHistory);

            var partnerTotals = group
                .OrderByDescending(x => x.SharePercent)
                .ThenBy(x => x.PartnerId)
                .Select(x => new PartnershipPartnerTotals(
                    PartnerId: x.PartnerId,
                    PartnerName: nameById.GetValueOrDefault(x.PartnerId) ?? string.Empty,
                    SharePercent: x.SharePercent,
                    FundingUsd: fundingByPartner.GetValueOrDefault(x.PartnerId),
                    ProceedsHeldUsd: holderId == x.PartnerId ? salesUsd : 0m,
                    ProfitShareUsd: profitByPartner.GetValueOrDefault(x.PartnerId),
                    SettlementsPaidUsd: 0m,
                    SettlementsReceivedUsd: 0m,
                    // PTG ۱۲-C — اگر قرارداد بیش از یک بازهٔ سهم دارد، مفاد ترکیبی است.
                    SharePeriodCount: shareHistory.SharePeriodCount(contractId))
                {
                    UnsoldCostShareUsd = unsoldCostByPartner.GetValueOrDefault(x.PartnerId)
                })
                .ToList();

            var lines = new List<PartnershipStatementLine>();
            lines.AddRange(contractFunding.Select(f => new PartnershipStatementLine(
                PartnershipStatementSources.Payment,
                DescribeFunding(f),
                f.PaymentDate,
                f.PartnerId,
                f.Direction == PaymentDirection.Out ? f.AmountUsd : -f.AmountUsd,
                f.Reference,
                f.PaymentId,
                ResolveFundingKind(f))));
            lines.AddRange(sales.Select(s => new PartnershipStatementLine(
                PartnershipStatementSources.Sale,
                string.IsNullOrWhiteSpace(s.InvoiceNumber) ? "فروش" : $"فروش {s.InvoiceNumber}",
                s.SaleDate,
                holderId,
                s.TotalUsd,
                s.InvoiceNumber,
                s.SaleId,
                PartnershipStatementLineKind.SaleProceedsHeld,
                // مقدار و نرخ از خودِ سند فروش می‌آید. نرخ فقط وقتی معنا دارد که مقدار صفر نباشد.
                QuantityMt: s.QuantityMt == 0m ? null : s.QuantityMt,
                UnitPriceUsd: s.QuantityMt == 0m ? null : Round(s.TotalUsd / s.QuantityMt))));

            contractStatements.Add(new PartnershipContractStatement(
                ContractId: contractId,
                ContractNumber: head.ContractNumber,
                ContractLabel: Contract.BuildDisplayLabel(head.ContractName, head.ContractNumber),
                Currency: head.Currency,
                SalesUsd: salesUsd,
                PurchaseCostUsd: purchaseCostUsd,
                OperationalExpenseUsd: operationalExpenseUsd,
                TotalPartnerFundingUsd: totalFundingUsd,
                BookProfitUsd: bookProfitUsd,
                PaymentToBookDifferenceUsd: paymentToBookDifferenceUsd,
                ProceedsHolderPartnerId: holderId,
                ProceedsHolderPartnerName: holderId.HasValue
                    ? nameById.GetValueOrDefault(holderId.Value)
                    : null,
                Partners: partnerTotals,
                Lines: lines.OrderBy(l => l.Date).ThenBy(l => l.RecordId).ToList())
            {
                RealizedCostOfGoodsSoldUsd = Round(economics.RealizedCostOfGoodsSoldUsd),
                RealizedOperationalCostUsd = Round(economics.RealizedOperationalCostUsd),
                RealizedFxNetUsd = Round(economics.RealizedFxNetUsd)
            });
        }

        return contractStatements;
    }

    /// <summary>
    /// «خرید» یا «مصرف» — همان تفکیکی که کاربر در گردش حساب می‌بیند. پرداختِ برگشتی
    /// (جهت ورودی) اصلاح است، نه پرداخت تازه.
    /// </summary>
    private static PartnershipStatementLineKind ResolveFundingKind(PartnerFundingPaymentRow row)
        => row.Direction == PaymentDirection.In
            ? PartnershipStatementLineKind.Adjustment
            : row.PaymentKind == PaymentKind.SupplierPayment
                ? PartnershipStatementLineKind.PartnerPurchase
                : PartnershipStatementLineKind.PartnerExpense;

    /// <summary>
    /// PTG-P0-03 — تقسیمِ مفادِ یک قرارداد بین شرکا، بر پایهٔ درصدی که در زمانِ تحققِ
    /// مفاد توافق شده بود، نه درصدِ امروز.
    ///
    /// مبنای تقسیم، فروش است: مفاد هنگام فروش محقق می‌شود، پس سهمِ هر بازه به
    /// نسبتِ عایدِ فروش‌های همان بازه است. اگر قرارداد هنوز فروشی ندارد، همهٔ مفاد
    /// (که آن وقت فقط هزینه است) به نخستین بازه می‌رود — همان توافقی که زیر آن هزینه شده است.
    ///
    /// سازگاری عددی: وقتی قرارداد فقط یک بازهٔ سهم دارد، نتیجه دقیقاً
    /// <c>Round(bookProfit * share / 100)</c> است — همان فرمولی که پیش از این اجرا می‌شد.
    /// </summary>
    private static Dictionary<int, decimal> AllocateProfitBySharePeriod(
        int contractId,
        decimal bookProfitUsd,
        IReadOnlyList<SaleRow> sales,
        ContractPartnerShareHistory shareHistory)
    {
        var result = new Dictionary<int, decimal>();

        // مرزهای بازه: هر تاریخِ آغازی که ترکیب شرکا از آنجا عوض می‌شود.
        var boundaries = shareHistory.PeriodStartsFor(contractId);
        if (boundaries.Count == 0)
        {
            return result;
        }

        // وزنِ هر بازه بر پایهٔ عایدِ فروشِ همان بازه.
        var totalSalesUsd = sales.Sum(x => x.TotalUsd);
        var weights = new Dictionary<DateTime, decimal>();

        if (boundaries.Count == 1 || totalSalesUsd == 0m)
        {
            weights[boundaries[0]] = 1m;
        }
        else
        {
            foreach (var sale in sales)
            {
                var start = ResolvePeriodStart(boundaries, sale.SaleDate);
                weights[start] = weights.GetValueOrDefault(start) + sale.TotalUsd;
            }

            foreach (var key in weights.Keys.ToList())
            {
                weights[key] /= totalSalesUsd;
            }
        }

        foreach (var (start, weight) in weights)
        {
            if (weight == 0m)
            {
                continue;
            }

            foreach (var (partnerId, sharePercent) in shareHistory.SharesOn(contractId, start))
            {
                result[partnerId] = result.GetValueOrDefault(partnerId)
                    + (bookProfitUsd * weight * sharePercent / 100m);
            }
        }

        // گِردکردنِ جداگانهٔ هر سهم، یک سِنت جا می‌گذاشت: دو سهمِ ۵۰٪ از ۹۷٬۱۸۱٫۰۱ هرکدام
        // ۴۸٬۵۹۰٫۵۰۵ می‌شد و هر دو به ۴۸٬۵۹۰٫۵۱ گِرد می‌شدند، یعنی جمعِ سهم‌ها یک سِنت از خودِ
        // سود بیشتر. حالا باقیمانده به‌صورت قطعی به یک شریک می‌رسد و همان تقسیم عیناً در
        // ژورنالِ تخصیص سود هم ثبت می‌شود.
        return PartnerProfitAllocationPolicy.Settle(result);
    }

    private static DateTime ResolvePeriodStart(IReadOnlyList<DateTime> boundaries, DateTime date)
    {
        var target = date.Date;
        var chosen = boundaries[0];
        foreach (var boundary in boundaries)
        {
            if (boundary.Date <= target)
            {
                chosen = boundary;
            }
        }

        return chosen;
    }

    /// <summary>عضویتِ شرکا در قراردادهای شراکتی، با همان اطلاعات سرقراردادی که محاسبه لازم دارد.</summary>
    private async Task<List<ContractMemberLink>> LoadMemberLinksAsync(
        System.Linq.Expressions.Expression<Func<ContractPartner, bool>> predicate,
        CancellationToken ct)
    {
        // PTG-P0-03 — سهم شرکا اکنون تاریخ‌دار است، پس هر (قرارداد، شریک) می‌تواند چند
        // بازه داشته باشد. این فهرست فقط برای «عضویت و برچسب درصد» است، پس به آخرین
        // بازه تقلیل می‌شود. ریاضیِ پول از ContractPartnerShareHistory می‌آید، نه اینجا.
        var rows = await _db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.Contract != null && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            .Where(predicate)
            .Select(cp => new
            {
                cp.ContractId,
                cp.PartnerId,
                cp.SharePercent,
                cp.EffectiveFrom,
                ContractNumber = cp.Contract!.ContractNumber,
                ContractName = cp.Contract!.ContractName,
                Currency = cp.Contract!.Currency,
                cp.Contract!.SaleProceedsHolderPartnerId
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => new { r.ContractId, r.PartnerId })
            .Select(g => g.OrderByDescending(r => r.EffectiveFrom).First())
            .Select(r => new ContractMemberLink(
                r.ContractId,
                r.PartnerId,
                r.SharePercent,
                r.ContractNumber,
                r.ContractName,
                r.Currency,
                r.SaleProceedsHolderPartnerId))
            .ToList();
    }

    private sealed record ContractMemberLink(
        int ContractId,
        int PartnerId,
        decimal SharePercent,
        string ContractNumber,
        string ContractName,
        string Currency,
        int? SaleProceedsHolderPartnerId);

    private static string DescribeFunding(PartnerFundingPaymentRow row)
        => string.IsNullOrWhiteSpace(row.Description)
            ? PaymentKindLabels.ToPersian(row.PaymentKind)
            : row.Description!;

    private sealed record SaleRow(
        int SaleId,
        DateTime SaleDate,
        string? InvoiceNumber,
        decimal QuantityMt,
        decimal TotalUsd);

    private async Task<List<PartnershipSettlementRow>> LoadSettlementsAsync(
        int partnerAId,
        int partnerBId,
        IReadOnlyCollection<int> selectedContractIds,
        IReadOnlyCollection<int> allContractIds,
        IReadOnlyDictionary<int, string> nameById,
        CancellationToken ct)
    {
        var selected = selectedContractIds.ToHashSet();
        var showsEveryContract = selected.SetEquals(allContractIds);

        var rows = await _db.PartnerSettlements
            .AsNoTracking()
            .Include(s => s.Contract)
            .Where(s => (s.FromPartnerId == partnerAId && s.ToPartnerId == partnerBId)
                || (s.FromPartnerId == partnerBId && s.ToPartnerId == partnerAId))
            .OrderBy(s => s.SettlementDate)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        // تسویهٔ بدون قرارداد، تسویهٔ کلیِ حساب شراکت است و فقط در نمای «همهٔ قراردادها» شمرده می‌شود.
        var filtered = rows
            .Where(s => s.ContractId.HasValue
                ? selected.Contains(s.ContractId.Value)
                : showsEveryContract)
            .ToList();

        var result = new List<PartnershipSettlementRow>(filtered.Count);
        var running = 0m;
        foreach (var s in filtered)
        {
            if (!s.IsReversed)
            {
                running += s.FromPartnerId == partnerAId ? s.AmountUsd : -s.AmountUsd;
            }

            result.Add(new PartnershipSettlementRow(
                Id: s.Id,
                SettlementDate: s.SettlementDate,
                FromPartnerId: s.FromPartnerId,
                FromPartnerName: nameById.GetValueOrDefault(s.FromPartnerId) ?? string.Empty,
                ToPartnerId: s.ToPartnerId,
                ToPartnerName: nameById.GetValueOrDefault(s.ToPartnerId) ?? string.Empty,
                ContractId: s.ContractId,
                ContractLabel: s.Contract?.DisplayLabel,
                Amount: s.Amount,
                Currency: s.Currency,
                AmountUsd: s.AmountUsd,
                Reference: s.Reference,
                Description: s.Description,
                IsReversed: s.IsReversed,
                RunningBalanceAfterUsd: Round(running)));
        }

        return result;
    }
}
