using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services.Custody;

/// <summary>چگونه پولِ فروش در لحظهٔ ثبت فروش دریافت شد.</summary>
public enum CustodySaleSettlement
{
    /// <summary>نسیه — بعداً وصول می‌شود.</summary>
    Credit = 1,
    /// <summary>تمام پول به صندوق/بانک شرکت آمد.</summary>
    CashToCompany = 2,
    /// <summary>مشتری تمام پول را مستقیم به مالک داد.</summary>
    DirectToOwner = 3
}

/// <summary>
/// نقش شرکت در فروش امانی. فروش امانی همیشه به نمایندگی مالک است؛ فروشندهٔ اصلی فقط پس از
/// خرید قطعی (تبدیل امانی) و از مسیر فروش ملکیِ موجود ممکن است، نه با انتخاب یک گزینه.
/// </summary>
public enum CustodySaleRole
{
    Agent = 1,
    Principal = 2
}

public sealed class CustodySaleInput
{
    public CustodySaleRole Role { get; init; } = CustodySaleRole.Agent;
    public int LotId { get; init; }
    public required CustodyLocation Location { get; init; }
    /// <summary>
    /// سهم‌های اضافه از بارهای دیگرِ همان مالک و همان جنس. <see cref="QuantityMt"/> سهمِ بار و محلِ
    /// اصلی است؛ مقدار کلِ فروش جمعِ همهٔ سهم‌هاست و کمیشن یک‌بار روی کل حساب می‌شود.
    /// </summary>
    public IReadOnlyList<CustodySaleSource> AdditionalSources { get; init; } = [];
    /// <summary>اندازه‌گیریِ لیتریِ سهمِ بار اصلی (اختیاری)؛ قیمت همچنان فی تُن است.</summary>
    public CustodyMeasurement? Measurement { get; init; }
    public int CustomerId { get; init; }
    public DateTime SaleDate { get; init; }
    public decimal QuantityMt { get; init; }
    /// <summary>قیمت فی تُن به ارز فروش (<see cref="Currency"/>).</summary>
    public decimal UnitPriceUsd { get; init; }
    /// <summary>ارز فروش به مشتری: USD یا AFN.</summary>
    public string Currency { get; init; } = "USD";
    /// <summary>نرخ توافقیِ تأییدشدهٔ همین معامله برای ارز فروش (اختیاری؛ وگرنه نرخ روزانهٔ سیستم).</summary>
    public decimal? AgreedCurrencyPerUsd { get; init; }
    public string? RateReference { get; init; }
    /// <summary>ارز تعهد به مالک؛ خالی یعنی همان ارز فروش.</summary>
    public string? OwnerCurrency { get; init; }
    /// <summary>نرخ قفل‌شدهٔ توافق مالک (افغانی در برابر ۱ دالر) وقتی ارز مالک با ارز فروش فرق دارد؛ خالی یعنی نرخ روزانهٔ سیستم در تاریخ فروش.</summary>
    public decimal? OwnerAgreedPerUsd { get; init; }
    public string? OwnerRateReference { get; init; }
    /// <summary>مسئول تفاوت نرخ طبق توافق مالک؛ پیش‌فرض ندارد.</summary>
    public CustodyFxResponsibility? FxResponsibility { get; init; }
    public decimal? FxOwnerSharePercent { get; init; }
    public string? FxAgreementReference { get; init; }
    public CustodyCommissionMethod CommissionMethod { get; init; }
    public decimal CommissionRate { get; init; }
    public CustodyCommissionRecognition CommissionRecognition { get; init; } = CustodyCommissionRecognition.OnCollection;
    public CustodySaleSettlement Settlement { get; init; } = CustodySaleSettlement.Credit;
    public int? CashAccountId { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodyFinanceInput
{
    public CustodyFinanceKind Kind { get; init; }
    public int? SaleId { get; init; }
    public int? LotId { get; init; }
    /// <summary>برای پرداخت به مالک، مصرف و حق خدمات که به فروش خاصی وصل نیستند.</summary>
    public CustodyOwner? Owner { get; init; }
    public DateTime EntryDate { get; init; }
    /// <summary>مبلغ به ارز سند (<see cref="Currency"/>).</summary>
    public decimal AmountUsd { get; init; }
    /// <summary>ارز سند؛ برای وصول/استرداد همان ارز فروش و برای پرداخت به خدمات‌دهنده همان ارز مصرف. خالی = پیش‌فرض همان سند (یا دالر).</summary>
    public string? Currency { get; init; }
    /// <summary>نرخ توافقیِ تأییدشده (افغانی در برابر ۱ دالر) برای همین سند؛ خالی = نرخ روزانهٔ سیستم.</summary>
    public decimal? AgreedCurrencyPerUsd { get; init; }
    public string? RateReference { get; init; }
    public int? CashAccountId { get; init; }
    public int? ExpenseTypeId { get; init; }
    public string? PayeeName { get; init; }
    /// <summary>خدمات‌دهندهٔ طلبکار؛ فقط برای «مصرف نسیه به حساب مالک».</summary>
    public int? ServiceProviderId { get; init; }
    /// <summary>مصرف نسیه‌ای که این پرداخت (کامل یا قسمی) آن را تسویه می‌کند.</summary>
    public int? ProviderChargeId { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Description { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodySaleReturnInput
{
    public int SaleId { get; init; }
    public DateTime ReturnDate { get; init; }
    /// <summary>مقدارِ برگشت از هر بار/محلِ همان فروش؛ تیل به همان بار و محل برمی‌گردد.</summary>
    public required IReadOnlyList<CustodySaleSource> Lines { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }

    // ---- استرداد وقتی پولِ بخشِ برگشتی قبلاً وصول شده است (جمع باید دقیقاً اضافه‌وصول را بپوشاند).
    // همهٔ مبالغ به ارز فروش‌اند (نام‌های *Usd برای سازگاری مانده‌اند).
    /// <summary>استرداد از صندوق/بانک شرکت؛ فقط با سند واقعی پرداخت.</summary>
    public decimal RefundFromCompanyUsd { get; init; }
    public int? RefundCashAccountId { get; init; }
    public string? RefundDocumentNumber { get; init; }
    /// <summary>بخشی که مالک خودش مستقیم به مشتری پس داد (پولی که مشتری مستقیم به مالک داده بود).</summary>
    public decimal RefundByOwnerUsd { get; init; }
    public string? OwnerRefundReference { get; init; }
    /// <summary>
    /// وقتی پولِ مالک قبلاً پرداخت شده و استرداد از صندوق شرکت حساب مالک را منفی می‌کند، فقط با تأیید
    /// صریح و سندِ قابل وصول از مالک ثبت می‌شود.
    /// </summary>
    public bool OwnerRecoveryConfirmed { get; init; }
    public string? OwnerRecoveryReference { get; init; }
}

public interface ICustodySaleService
{
    Task<int> CreateSaleAsync(CustodySaleInput input, CancellationToken ct = default);
    Task<int> RecordFinanceAsync(CustodyFinanceInput input, CancellationToken ct = default);
    Task CancelSaleAsync(int saleId, string reason, CancellationToken ct = default);
    Task CancelFinanceAsync(int entryId, string reason, CancellationToken ct = default);
    Task<int> RecordReturnAsync(CustodySaleReturnInput input, CancellationToken ct = default);
    Task CancelReturnAsync(int returnId, string reason, CancellationToken ct = default);
    /// <summary>ثبتِ بعدیِ مسئولیت تفاوت نرخ برای فروشِ دوارزی که هنوز توافقش ثبت نشده (فقط یک بار).</summary>
    Task SetFxAgreementAsync(int saleId, CustodyFxResponsibility responsibility, decimal? ownerSharePercent, string reference, CancellationToken ct = default);
}

/// <summary>
/// فروش امانی به نمایندگی مالک و حسابِ مالک. هر سند در یک تراکنش: موجودی امانی، سطرهای
/// LedgerEntry (صورت‌حساب طرف‌ها)، PaymentTransaction (فقط وقتی پول واقعاً از صندوق شرکت
/// گذشته) و ژورنال (<see cref="ICustodyAccountingAdapter"/>). هیچ SalesTransaction، InventoryMovement،
/// درآمد فروش یا بهای تمام‌شده ساخته نمی‌شود.
/// </summary>
public sealed class CustodySaleService(
    ApplicationDbContext db,
    ICustodyService custody,
    ICustodyAccountingAdapter accounting,
    ILedgerPostingService ledger,
    IAuditService audit,
    IFormTokenGuard formTokens,
    ISystemCompanyProvider systemCompany,
    ICurrentUserContext? currentUser = null) : ICustodySaleService
{
    public const string SaleSourceType = "CustodySale";
    public static string FinanceSourceType(CustodyFinanceKind kind) => "Custody" + kind;

    /// <summary>
    /// طرف‌هایی که حساب مالی امانی دارند. دفترِ عملیاتی (LedgerEntry) ستونِ شرکت ندارد و صورت‌حساب
    /// شریک معنای «سهم شراکت» دارد، پس مالکِ شرکت/شریک فعلاً فقط نگهداری و تحویل دارد.
    /// </summary>
    public static readonly AccountingPartyType[] FinancialOwnerTypes = [AccountingPartyType.Customer, AccountingPartyType.Supplier];

    public const string ReturnSourceType = "CustodySaleReturn";

    private const string SaleEntity = "CustodySale";
    private const string OwnerSuffix = "-O";
    private const string CommissionSuffix = "-C";
    private const string ProviderSuffix = "-P";
    private const string FinanceEntity = "CustodyFinanceEntry";
    private const string ReturnEntity = "CustodySaleReturn";

    /// <summary>
    /// هویتِ مالیِ مالک: مشتری و فروشنده خودشان؛ شریکی که مدیر با سند معتبر نقش فروشندهٔ موجودِ همان
    /// شخص را برایش تأیید کرده، حساب مالیِ امانی‌اش در همان نقش فروشنده است (شخص تازه ساخته نمی‌شود و
    /// حساب «سهم شراکت» دست نمی‌خورد). سایر انواع مالک حساب مالی امانی ندارند (null).
    /// </summary>
    public static async Task<CustodyOwner?> ResolveFinancialOwnerAsync(ApplicationDbContext db, CustodyOwner owner, CancellationToken ct = default)
    {
        if (FinancialOwnerTypes.Contains(owner.PartyType))
            return owner;
        if (owner.PartyType is not (AccountingPartyType.Partner or AccountingPartyType.Company))
            return null;
        var supplierId = await db.CustodySupplierIdentityLinks.AsNoTracking()
            .Where(x => owner.PartyType == AccountingPartyType.Partner ? x.PartnerId == owner.PartyId : x.CompanyId == owner.PartyId)
            .Select(x => (int?)x.SupplierId)
            .SingleOrDefaultAsync(ct);
        return supplierId is null ? null : new CustodyOwner(AccountingPartyType.Supplier, supplierId.Value);
    }

    private async Task<CustodyOwner> RequireFinancialOwnerAsync(CustodyOwner owner, CancellationToken ct)
        => await ResolveFinancialOwnerAsync(db, owner, ct)
            ?? throw Rule("CUSTODY_OWNER_FINANCE_UNSUPPORTED", owner.PartyType is AccountingPartyType.Partner or AccountingPartyType.Company
                ? "حساب مالی این مالک فقط پس از پیوند تأییدشده با نقش فروشندهٔ موجودِ همان شخص ممکن است (صفحهٔ «خرید قطعی» همین بار)."
                : "فروش و حساب مالی امانی فقط برای مالکی است که مشتری یا فروشنده (یا شریکِ پیوندشده) باشد.");

    public Task<int> CreateSaleAsync(CustodySaleInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            if (input.Role != CustodySaleRole.Agent)
                throw Rule("CUSTODY_PRINCIPAL_VIA_PURCHASE",
                    "شرکت پیش از فروش مالک این تیل نیست؛ برای فروش به‌عنوان فروشندهٔ اصلی، اول «تبدیل به خرید قطعی» و سپس فروش ملکی از همان خرید را ثبت کنید.");
            var currency = CustodyFx.Normalize(input.Currency);
            var ownerCurrency = CustodyFx.Normalize(string.IsNullOrWhiteSpace(input.OwnerCurrency) ? currency : input.OwnerCurrency);
            var primaryQuantity = input.Measurement?.ToMt() ?? input.QuantityMt;
            if (input.Measurement is not null && input.QuantityMt != 0m && input.QuantityMt != primaryQuantity)
                throw Rule("CUSTODY_MEASUREMENT_MISMATCH", "مقدار MT با لیتر و چگالی سند یکسان نیست.");
            RequirePositive(primaryQuantity, "مقدار فروش", 4);
            RequirePositive(input.UnitPriceUsd, "قیمت فی تُن", 4);
            if (!Enum.IsDefined(input.Settlement) || !Enum.IsDefined(input.CommissionRecognition))
                throw Rule("CUSTODY_SALE_TERMS", "شرایط فروش و زمان کمیشن معتبر نیست.");
            var sources = new List<CustodySaleSource> { new(input.LotId, input.Location, primaryQuantity, input.Measurement) };
            sources.AddRange(input.AdditionalSources ?? []);
            foreach (var source in sources)
                RequirePositive(source.QuantityMt, "مقدار فروش", 4);
            var totalQuantity = sources.Sum(x => x.QuantityMt);

            var lot = await db.CustodyLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == input.LotId, ct)
                ?? throw Rule("CUSTODY_LOT_NOT_FOUND", "بار امانی پیدا نشد.");
            var lotIds = sources.Select(x => x.LotId).Distinct().ToArray();
            var lots = await db.CustodyLots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToListAsync(ct);
            if (lots.Count != lotIds.Length)
                throw Rule("CUSTODY_LOT_NOT_FOUND", "بار امانی پیدا نشد.");
            if (lots.Any(l => l.OwnerPartyType != lot.OwnerPartyType || l.OwnerPartyId != lot.OwnerPartyId))
                throw Rule("CUSTODY_SALE_MIXED_OWNERS", "یک فروش فقط از بارهای همان مالک تأمین می‌شود.");
            if (lots.Any(l => l.ProductId != lot.ProductId))
                throw Rule("CUSTODY_SALE_MIXED_PRODUCTS", "یک فروش فقط از بارهای همان جنس تأمین می‌شود.");
            var owner = await RequireFinancialOwnerAsync(new CustodyOwner(lot.OwnerPartyType, lot.OwnerPartyId), ct);
            await LockOwnerAsync(owner, ct);
            await EnsureAccountingCompleteAsync(owner, ct);
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == input.CustomerId, ct)
                ?? throw Rule("CUSTODY_CUSTOMER", "مشتری را انتخاب کنید.");
            if (!customer.IsActive)
                throw Rule("CUSTODY_CUSTOMER_INACTIVE", "این مشتری غیرفعال است.");
            if (owner.PartyType == AccountingPartyType.Customer && owner.PartyId == customer.Id)
                throw Rule("CUSTODY_SELF_SALE", "مالک نمی‌تواند خریدارِ تیلِ خودش باشد؛ برای این کار «برگشت به مالک» را ثبت کنید.");

            // مبالغ به ارز فروش؛ ارزشِ دالری با نرخ معتبرِ تاریخ فروش. وقتی ارز مالک فرق دارد، نرخِ
            // قفل‌شدهٔ توافق مالک (ثبت‌شده یا توافقیِ با مرجع) لازم است.
            var saleRate = await CustodyFx.ResolveAsync(db, currency, input.SaleDate, input.AgreedCurrencyPerUsd, input.RateReference, ct);
            var cross = currency != ownerCurrency;
            CustodyRate? locked = cross
                ? await CustodyFx.ResolveAsync(db, CustodyFx.Afn, input.SaleDate, input.OwnerAgreedPerUsd, input.OwnerRateReference, ct)
                : null;
            var ownerRate = !cross ? saleRate : ownerCurrency == CustodyFx.Afn ? locked! : CustodyRate.Usd;
            var (responsibility, ownerShare, fxReference) = cross
                ? ValidateFxAgreement(input.FxResponsibility, input.FxOwnerSharePercent, input.FxAgreementReference)
                : (null, null, null);

            var amount = Money(totalQuantity * input.UnitPriceUsd);
            var commission = CalculateCommission(input.CommissionMethod, input.CommissionRate, totalQuantity, amount);
            var ownerCommission = cross ? CustodyFx.Convert(commission, currency, ownerCurrency, locked!.PerUsd) : commission;
            var amountUsd = saleRate.ToUsdAmount(amount);
            var commissionUsd = ownerRate.ToUsdAmount(ownerCommission);
            if (commissionUsd > amountUsd)
                throw Rule("CUSTODY_COMMISSION_EXCEEDS", "کمیشن (به ارز مالک) بیشتر از ارزش مبلغ فروش شده است.");
            var onSale = input.CommissionRecognition == CustodyCommissionRecognition.OnSale;

            // خروج از موجودیِ همان مالک — همهٔ بارها با یک قفلِ مرتب، سقف موجودی، مسدودی و کنترل تاریخ.
            var stockKey = await custody.RecordSaleOutAsync(sources, input.SaleDate, input.DocumentNumber, ct);

            var sale = new CustodySale
            {
                SaleNumber = "TMP-" + Guid.NewGuid().ToString("N"),
                CustodyLotId = lot.Id,
                OwnerPartyType = owner.PartyType,
                OwnerPartyId = owner.PartyId,
                CustomerId = customer.Id,
                ProductId = lot.ProductId,
                TerminalId = input.Location.TerminalId,
                StorageTankId = input.Location.StorageTankId,
                SaleDate = ToUtc(input.SaleDate),
                QuantityMt = totalQuantity,
                UnitPriceUsd = saleRate.IsUsd ? input.UnitPriceUsd : saleRate.ToUsdAmount(input.UnitPriceUsd),
                Currency = currency,
                AmountUsd = amountUsd,
                CommissionMethod = input.CommissionMethod,
                CommissionRate = input.CommissionMethod == CustodyCommissionMethod.None ? 0m : input.CommissionRate,
                CommissionUsd = commissionUsd,
                CommissionRecognition = input.CommissionRecognition,
                CommissionRecognizedAtSaleUsd = onSale ? commissionUsd : 0m,
                UnitPrice = input.UnitPriceUsd,
                SaleAmount = amount,
                SaleCommission = commission,
                RateToUsd = saleRate.ToUsd,
                CurrencyPerUsd = saleRate.PerUsd,
                RateSource = saleRate.IsUsd ? null : saleRate.Source,
                OwnerCurrency = ownerCurrency,
                OwnerAgreedPerUsd = locked?.PerUsd,
                OwnerRateSource = locked?.Source,
                OwnerRateToUsd = ownerRate.ToUsd,
                CommissionRecognizedAtSaleOwner = onSale ? ownerCommission : 0m,
                FxResponsibility = responsibility,
                FxOwnerSharePercent = ownerShare,
                FxAgreementReference = fxReference,
                StockOperationKey = stockKey,
                DocumentNumber = Clean(input.DocumentNumber),
                Notes = Clean(input.Notes),
                CreatedByUserId = currentUser?.UserId
            };
            db.CustodySales.Add(sale);
            formTokens.Stamp(input.FormToken, "Custody.Sale", SaleEntity);
            await db.SaveChangesAsync(ct);
            sale.SaleNumber = $"AS-{sale.Id:D5}";

            // طلبِ مشتری از فروش امانی در دفتر طرف‌ها نمی‌نشیند: مقابلِ آن «عواید در انتظار وصول» است
            // که هنوز بدهیِ بالفعل به مالک نیست؛ ثبتِ یک‌طرفهٔ آن بیلانس شرکت را بیش از واقع نشان می‌داد.
            // این طلب در ژورنال (دریافتنی ↔ در انتظار وصول) و در خودِ سند فروش امانی پیگیری می‌شود.
            var customerName = customer.NamePersian ?? customer.Name;
            if (sale.CommissionRecognizedAtSaleUsd > 0m)
            {
                ledger.Post(PartyRow(SaleSourceType, sale.Id, sale.SaleDate, sale.CommissionRecognizedAtSaleUsd, sale.SaleNumber + CommissionSuffix,
                    sale.OwnerPartyType, sale.OwnerPartyId, CompanyFlowEffect.Outflow,
                    $"کمیشن فروش امانی {sale.SaleNumber} به {customerName}",
                    Source(ownerCommission, ownerRate)));
            }

            await db.SaveChangesAsync(ct);
            RequireJournal(await accounting.TryPostSaleAsync(sale, ct));
            await audit.LogAsync(SaleEntity, sale.Id, AuditAction.Insert, diff: Describe(new
            {
                sale.SaleNumber, Lot = lot.LotNumber,
                Sources = sources.Select(x => new { x.LotId, x.Location.TerminalId, x.Location.StorageTankId, x.QuantityMt }),
                LotOwner = new { lot.OwnerPartyType, lot.OwnerPartyId }, FinancialOwner = owner,
                sale.CustomerId, sale.QuantityMt, sale.Currency, sale.UnitPrice, sale.SaleAmount, sale.AmountUsd, sale.RateToUsd, sale.RateSource,
                CommissionMethod = sale.CommissionMethod.ToString(), sale.CommissionRate, sale.SaleCommission, sale.CommissionUsd,
                Recognition = sale.CommissionRecognition.ToString(), Settlement = input.Settlement.ToString(),
                sale.OwnerCurrency, sale.OwnerAgreedPerUsd, sale.OwnerRateSource,
                FxResponsibility = sale.FxResponsibility?.ToString(), sale.FxOwnerSharePercent, sale.FxAgreementReference
            }), ct: ct);
            await db.SaveChangesAsync(ct);

            if (input.Settlement != CustodySaleSettlement.Credit)
            {
                await RecordFinanceCoreAsync(new CustodyFinanceInput
                {
                    Kind = input.Settlement == CustodySaleSettlement.CashToCompany ? CustodyFinanceKind.Collection : CustodyFinanceKind.DirectToOwner,
                    SaleId = sale.Id,
                    EntryDate = input.SaleDate,
                    AmountUsd = amount,
                    Currency = currency,
                    AgreedCurrencyPerUsd = input.AgreedCurrencyPerUsd,
                    RateReference = input.RateReference,
                    CashAccountId = input.CashAccountId,
                    DocumentNumber = input.DocumentNumber,
                    Description = "دریافت هم‌زمان با فروش"
                }, ct);
            }

            return sale.Id;
        }, ct);

    public Task<int> RecordFinanceAsync(CustodyFinanceInput input, CancellationToken ct = default)
        => InTransactionAsync(() => RecordFinanceCoreAsync(input, ct), ct);

    private async Task<int> RecordFinanceCoreAsync(CustodyFinanceInput input, CancellationToken ct)
    {
        RequirePositive(input.AmountUsd, "مبلغ", 2);
        if (!Enum.IsDefined(input.Kind))
            throw Rule("CUSTODY_FINANCE_KIND", "نوع سند مالی معتبر نیست.");
        if (input.Kind is CustodyFinanceKind.CustomerRefund or CustodyFinanceKind.OwnerDirectRefund)
            throw Rule("CUSTODY_REFUND_VIA_RETURN", "استرداد فقط همراهِ سند برگشت فروش ثبت می‌شود.");

        CustodySale? sale = null;
        CustodyFinanceEntry? charge = null;
        CustodyOwner owner;
        int? lotId = input.LotId;
        if (input.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.DirectToOwner)
        {
            if (!input.SaleId.HasValue)
                throw Rule("CUSTODY_SALE_REQUIRED", "فروش را انتخاب کنید.");
            sale = await db.CustodySales.FirstOrDefaultAsync(s => s.Id == input.SaleId.Value, ct)
                ?? throw Rule("CUSTODY_SALE_NOT_FOUND", "فروش امانی پیدا نشد.");
            owner = new CustodyOwner(sale.OwnerPartyType, sale.OwnerPartyId);
            lotId = sale.CustodyLotId;
        }
        else if (input.Kind == CustodyFinanceKind.ProviderPayment)
        {
            if (!input.ProviderChargeId.HasValue)
                throw Rule("CUSTODY_PROVIDER_CHARGE_REQUIRED", "مصرف نسیه‌ای را که پرداخت می‌شود انتخاب کنید.");
            charge = await db.CustodyFinanceEntries.FirstOrDefaultAsync(e => e.Id == input.ProviderChargeId.Value, ct)
                ?? throw Rule("CUSTODY_PROVIDER_CHARGE_REQUIRED", "مصرف نسیه پیدا نشد.");
            if (charge.Kind != CustodyFinanceKind.ProviderCharge)
                throw Rule("CUSTODY_PROVIDER_CHARGE_REQUIRED", "این سند مصرف نسیهٔ خدمات‌دهنده نیست.");
            owner = new CustodyOwner(charge.OwnerPartyType, charge.OwnerPartyId);
            lotId = charge.CustodyLotId;
            if (charge.CustodySaleId.HasValue)
                sale = await db.CustodySales.FirstOrDefaultAsync(s => s.Id == charge.CustodySaleId.Value, ct);
        }
        else
        {
            if (input.SaleId.HasValue)
            {
                sale = await db.CustodySales.FirstOrDefaultAsync(s => s.Id == input.SaleId.Value, ct)
                    ?? throw Rule("CUSTODY_SALE_NOT_FOUND", "فروش امانی پیدا نشد.");
                owner = new CustodyOwner(sale.OwnerPartyType, sale.OwnerPartyId);
                lotId = sale.CustodyLotId;
            }
            else if (lotId.HasValue)
            {
                var lot = await db.CustodyLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lotId.Value, ct)
                    ?? throw Rule("CUSTODY_LOT_NOT_FOUND", "بار امانی پیدا نشد.");
                owner = new CustodyOwner(lot.OwnerPartyType, lot.OwnerPartyId);
            }
            else
            {
                owner = input.Owner ?? throw Rule("CUSTODY_OWNER_REQUIRED", "مالک را انتخاب کنید.");
            }
        }
        owner = await RequireFinancialOwnerAsync(owner, ct);

        // ترتیبِ قفل: اول حسابِ مالک، بعد فروش، بعد مصرف نسیه — همهٔ مسیرها همین ترتیب را دارند.
        await LockOwnerAsync(owner, ct);
        if (sale is not null)
        {
            await LockSaleAsync(sale.Id, ct);
            await db.Entry(sale).ReloadAsync(ct);
            if (sale.Status != CustodyDocumentStatus.Posted)
                throw Rule("CUSTODY_SALE_CANCELLED", "این فروش لغو شده است.");
        }
        if (charge is not null)
        {
            await LockFinanceEntryAsync(charge.Id, ct);
            await db.Entry(charge).ReloadAsync(ct);
            if (charge.Status != CustodyDocumentStatus.Posted)
                throw Rule("CUSTODY_PROVIDER_CHARGE_CANCELLED", "این مصرف نسیه لغو شده است.");
        }
        await EnsureAccountingCompleteAsync(owner, ct);

        // ارز سند: وصول همان ارز فروش، پرداخت به خدمات‌دهنده همان ارز مصرف؛ سایر اسناد ارزِ انتخابی
        // (حساب مالک در همان ارز تغییر می‌کند).
        var fixedCurrency = input.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.DirectToOwner ? sale!.Currency
            : input.Kind == CustodyFinanceKind.ProviderPayment ? charge!.Currency : null;
        var currency = fixedCurrency is not null ? CustodyFx.Normalize(fixedCurrency) : CustodyFx.Normalize(input.Currency);
        if (fixedCurrency is not null && !string.IsNullOrWhiteSpace(input.Currency) && CustodyFx.Normalize(input.Currency) != currency)
            throw Rule("CUSTODY_CURRENCY_MISMATCH", input.Kind == CustodyFinanceKind.ProviderPayment
                ? $"پرداختِ این مصرف فقط به ارزِ خودِ مصرف ({currency}) ثبت می‌شود."
                : $"وصولِ این فروش فقط به ارز فروش ({currency}) ثبت می‌شود.");
        var rate = await CustodyFx.ResolveAsync(db, currency, input.EntryDate, input.AgreedCurrencyPerUsd, input.RateReference, ct);

        var amount = input.AmountUsd;
        var commission = 0m;
        if (input.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.DirectToOwner)
        {
            var returned = await ReturnTotalsAsync(sale!.Id, ct);
            var netAmount = sale.EffectiveAmount - returned.Amount;
            var netCommission = sale.EffectiveCommission - returned.Commission;
            var collected = await CollectedAsync(sale.Id, excludeEntryId: null, ct);
            var outstanding = netAmount - collected;
            if (amount > outstanding)
                throw Rule("CUSTODY_OVER_COLLECTION", $"باقی‌ماندهٔ این فروش {Amt(outstanding, currency)} است.");
            if (sale.CommissionRecognition == CustodyCommissionRecognition.OnCollection && netCommission > 0m)
            {
                var recognized = await CommissionOnCollectionsAsync(sale.Id, ct);
                var remaining = Math.Max(netCommission - recognized, 0m);
                // آخرین وصول باقیماندهٔ کمیشن را می‌گیرد تا گردکردن، کمیشن را کم یا زیاد نکند.
                commission = amount == outstanding
                    ? remaining
                    : Math.Min(remaining, Money(netCommission * amount / netAmount));
            }
        }

        int? serviceProviderId = null;
        if (input.Kind == CustodyFinanceKind.ProviderCharge)
        {
            if (!input.ServiceProviderId.HasValue)
                throw Rule("CUSTODY_PROVIDER_REQUIRED", "خدمات‌دهندهٔ طلبکار را انتخاب کنید.");
            if (!await db.ServiceProviders.AnyAsync(x => x.Id == input.ServiceProviderId.Value && x.IsActive, ct))
                throw Rule("CUSTODY_PROVIDER_REQUIRED", "خدمات‌دهنده پیدا نشد یا غیرفعال است.");
            if (!input.ExpenseTypeId.HasValue)
                throw Rule("CUSTODY_PROVIDER_EXPENSE_TYPE", "نوع مصرف را انتخاب کنید.");
            var payableKind = await db.ExpenseTypes.AsNoTracking().Where(x => x.Id == input.ExpenseTypeId.Value)
                .Select(x => new { x.PayableAccountKind }).SingleOrDefaultAsync(ct)
                ?? throw Rule("CUSTODY_PROVIDER_EXPENSE_TYPE", "نوع مصرف پیدا نشد.");
            if (accounting.IsEnabled && payableKind.PayableAccountKind is null)
                throw Rule("CUSTODY_PROVIDER_PAYABLE", "برای این نوع مصرف «حساب پرداختنی» تعیین نشده است؛ بدهی خدمات‌دهنده بدون حساب معتبر ثبت نمی‌شود.");
            serviceProviderId = input.ServiceProviderId;
        }
        else if (input.Kind == CustodyFinanceKind.ProviderPayment)
        {
            var paid = await ProviderPaidAsync(charge!.Id, ct);
            var unpaid = charge.EffectiveAmount - paid;
            if (amount > unpaid)
                throw Rule("CUSTODY_PROVIDER_OVERPAID", $"ماندهٔ پرداخت‌نشدهٔ این مصرف {Amt(unpaid, currency)} است.");
            serviceProviderId = charge.ServiceProviderId;
        }

        var paysCash = input.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.OwnerPayout
            or CustodyFinanceKind.ReimbursableExpense or CustodyFinanceKind.ProviderPayment;
        if (paysCash)
        {
            if (!input.CashAccountId.HasValue)
                throw Rule("CUSTODY_CASH_ACCOUNT", "صندوق یا بانک را انتخاب کنید.");
            await RequireCashAccountAsync(input.CashAccountId.Value, currency, ct);
        }

        // اثر بر حساب مالک (به ارز مالک) و تفاوت نرخ.
        var effect = input.Kind switch
        {
            CustodyFinanceKind.Collection => await CashOwnerEffectAsync(sale!, amount, commission, rate, input.EntryDate, input.AgreedCurrencyPerUsd, input.RateReference, inbound: true, ct),
            CustodyFinanceKind.DirectToOwner => await DirectOwnerEffectAsync(sale!, amount, commission, rate, input.EntryDate, input.AgreedCurrencyPerUsd, input.RateReference, ct),
            _ => OwnerEffect.SameCurrency(currency, amount, 0m, rate, rate.ToUsdAmount(amount))
        };

        if (input.Kind == CustodyFinanceKind.OwnerPayout)
        {
            var payable = await OwnerFundsBalanceAsync(owner, currency, ct);
            if (amount > payable)
                throw Rule("CUSTODY_OVER_PAYOUT", payable > 0m
                    ? $"ماندهٔ قابل پرداخت به این مالک به {currency} {Amt(payable, currency)} است."
                    : $"فعلاً پولِ {currency} از این مالک نزد شرکت نیست.");
        }

        var entry = new CustodyFinanceEntry
        {
            Kind = input.Kind,
            CustodySaleId = sale?.Id,
            CustodyLotId = lotId,
            OwnerPartyType = owner.PartyType,
            OwnerPartyId = owner.PartyId,
            CustomerId = sale?.CustomerId,
            EntryDate = ToUtc(input.EntryDate),
            AmountUsd = effect.AmountUsd,
            Currency = currency,
            CommissionUsd = effect.CommissionUsd,
            Amount = amount,
            Commission = commission,
            RateToUsd = rate.ToUsd,
            CurrencyPerUsd = rate.PerUsd,
            RateSource = rate.IsUsd ? null : rate.Source,
            OwnerCurrency = effect.OwnerCurrency,
            OwnerAmount = effect.OwnerAmount,
            OwnerCommission = effect.OwnerCommission,
            OwnerRateToUsd = effect.OwnerRate.ToUsd,
            FxDifference = effect.FxDifference,
            FxOwnerShare = effect.FxOwnerShare,
            FxCompanyUsd = effect.FxCompanyUsd,
            CashAccountId = paysCash ? input.CashAccountId : null,
            ExpenseTypeId = input.Kind is CustodyFinanceKind.ReimbursableExpense or CustodyFinanceKind.ServiceFee or CustodyFinanceKind.ProviderCharge
                ? input.ExpenseTypeId
                : input.Kind == CustodyFinanceKind.ProviderPayment ? charge!.ExpenseTypeId : null,
            ServiceProviderId = serviceProviderId,
            SettlesFinanceEntryId = charge?.Id,
            PayeeName = Clean(input.PayeeName),
            DocumentNumber = Clean(input.DocumentNumber),
            Description = Clean(input.Description),
            CreatedByUserId = currentUser?.UserId
        };
        db.CustodyFinanceEntries.Add(entry);
        formTokens.Stamp(input.FormToken, "Custody.Finance." + input.Kind, FinanceEntity);
        await db.SaveChangesAsync(ct);

        var reference = sale?.SaleNumber ?? $"AF-{entry.Id:D5}";
        if (entry.CashAccountId.HasValue)
        {
            var payment = await NewPaymentAsync(entry, reference, reversal: false, ct);
            await db.SaveChangesAsync(ct);
            entry.PaymentTransactionId = payment.Id;
        }

        foreach (var row in BuildFinanceRows(entry, reference, effect.OwnerRate))
            ledger.Post(row);
        await db.SaveChangesAsync(ct);

        // سند تاریخی فقط در تطبیقِ صریحِ دارای پیش‌نمایش ثبت می‌شود؛ دریافت امروز
        // مجوز ساخت خودکار ژورنالِ فروش یا دریافت‌های تاریخی نیست.
        RequireJournal(await accounting.TryPostFinanceAsync(entry, sale, ct));
        await audit.LogAsync(FinanceEntity, entry.Id, AuditAction.Insert, diff: Describe(new
        {
            Kind = entry.Kind.ToString(), Sale = sale?.SaleNumber, entry.OwnerPartyType, entry.OwnerPartyId,
            entry.Currency, entry.Amount, entry.Commission, entry.RateToUsd, entry.RateSource,
            entry.AmountUsd, entry.CommissionUsd, entry.OwnerCurrency, entry.OwnerAmount, entry.OwnerCommission,
            entry.FxDifference, entry.FxOwnerShare, entry.FxCompanyUsd, entry.CashAccountId, entry.PaymentTransactionId,
            entry.ServiceProviderId, entry.SettlesFinanceEntryId, entry.ExpenseTypeId
        }), ct: ct);
        await db.SaveChangesAsync(ct);
        return entry.Id;
    }

    public Task CancelSaleAsync(int saleId, string reason, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequireReason(reason);
            var sale = await db.CustodySales.FirstOrDefaultAsync(s => s.Id == saleId, ct)
                ?? throw Rule("CUSTODY_SALE_NOT_FOUND", "فروش امانی پیدا نشد.");
            await LockOwnerAsync(new CustodyOwner(sale.OwnerPartyType, sale.OwnerPartyId), ct);
            await LockSaleAsync(sale.Id, ct);
            await db.Entry(sale).ReloadAsync(ct);
            if (sale.Status == CustodyDocumentStatus.Cancelled)
                throw Rule("CUSTODY_SALE_CANCELLED", "این فروش قبلاً لغو شده است.");
            if (await db.CustodyFinanceEntries.AnyAsync(e => e.CustodySaleId == sale.Id && e.Status == CustodyDocumentStatus.Posted, ct))
                throw Rule("CUSTODY_SALE_HAS_RECEIPTS", "برای این فروش دریافت یا سند ثبت شده است؛ اول آن‌ها را لغو کنید.");
            if (await db.CustodySaleReturns.AnyAsync(r => r.CustodySaleId == sale.Id && r.Status == CustodyDocumentStatus.Posted, ct))
                throw Rule("CUSTODY_SALE_HAS_RETURNS", "برای این فروش برگشت ثبت شده است؛ اول برگشت‌ها را لغو کنید.");
            await custody.ReverseSaleOutAsync(sale.StockOperationKey, $"لغو فروش {sale.SaleNumber}: {reason.Trim()}", ct);
            await ReverseLedgerRowsAsync(SaleSourceType, sale.Id, $"لغو فروش امانی {sale.SaleNumber}", sale.SaleNumber, ct);
            var companyId = await systemCompany.GetOwnerCompanyIdAsync(ct);
            await accounting.TryReverseAsync(
                CustodyAccountingAdapter.SaleEventId(sale.Id),
                CustodyAccountingAdapter.SaleJournalNumber(companyId, sale.Id),
                DateTime.UtcNow,
                $"Cancel custody sale {sale.SaleNumber}",
                ct);

            sale.Status = CustodyDocumentStatus.Cancelled;
            sale.CancelledAtUtc = DateTime.UtcNow;
            sale.CancelledByUserId = currentUser?.UserId;
            sale.CancellationReason = reason.Trim();
            await audit.LogAsync(SaleEntity, sale.Id, AuditAction.Reverse, diff: Describe(new { sale.SaleNumber, Reason = reason.Trim() }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    public Task CancelFinanceAsync(int entryId, string reason, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequireReason(reason);
            var entry = await db.CustodyFinanceEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct)
                ?? throw Rule("CUSTODY_FINANCE_NOT_FOUND", "سند پیدا نشد.");
            var owner = new CustodyOwner(entry.OwnerPartyType, entry.OwnerPartyId);
            await LockOwnerAsync(owner, ct);
            if (entry.CustodySaleId.HasValue)
                await LockSaleAsync(entry.CustodySaleId.Value, ct);
            await db.Entry(entry).ReloadAsync(ct);
            if (entry.Status == CustodyDocumentStatus.Cancelled)
                throw Rule("CUSTODY_FINANCE_CANCELLED", "این سند قبلاً لغو شده است.");
            if (entry.Kind == CustodyFinanceKind.ProviderCharge)
            {
                await LockFinanceEntryAsync(entry.Id, ct);
                if (await ProviderPaidAsync(entry.Id, ct) > 0m)
                    throw Rule("CUSTODY_PROVIDER_CHARGE_PAID", "بخشی از این مصرف به خدمات‌دهنده پرداخت شده است؛ اول پرداخت‌ها را لغو کنید.");
            }
            if (entry.Kind is CustodyFinanceKind.CustomerRefund or CustodyFinanceKind.OwnerDirectRefund)
                throw Rule("CUSTODY_REFUND_VIA_RETURN", "استرداد فقط همراهِ لغوِ همان برگشتِ فروش لغو می‌شود.");
            if (entry.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.DirectToOwner && entry.CustodySaleId.HasValue
                && await db.CustodyFinanceEntries.AnyAsync(e => e.CustodySaleId == entry.CustodySaleId && e.Status == CustodyDocumentStatus.Posted
                    && (e.Kind == CustodyFinanceKind.CustomerRefund || e.Kind == CustodyFinanceKind.OwnerDirectRefund), ct))
                throw Rule("CUSTODY_COLLECTION_HAS_REFUNDS", "برای این فروش استرداد ثبت شده است؛ اول برگشتِ دارای استرداد را لغو کنید.");

            // لغوِ وصولِ نقدی پولِ مالک نزد شرکت را کم می‌کند؛ اگر قبلاً همان پول به مالک پرداخت
            // شده باشد، مانده منفی می‌شود — اول باید پرداخت به مالک اصلاح شود. ماندهٔ منفی بدونِ
            // پرداخت (کمیشن یا مصرفِ اسناد دیگر) فقط طلبِ شرکت از مالک است و لغو را نمی‌بندد.
            if (entry.Kind == CustodyFinanceKind.Collection)
            {
                var after = await OwnerFundsBalanceAsync(owner, entry.EffectiveOwnerCurrency, ct)
                    - (entry.EffectiveOwnerAmount - entry.EffectiveOwnerCommission);
                var paidOut = after < 0m && await db.CustodyFinanceEntries.AnyAsync(e =>
                    e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId
                    && (e.OwnerCurrency ?? "USD") == entry.EffectiveOwnerCurrency
                    && e.Kind == CustodyFinanceKind.OwnerPayout && e.Status == CustodyDocumentStatus.Posted, ct);
                if (paidOut)
                    throw Rule("CUSTODY_CANCEL_OVERPAID", "این پول قبلاً به مالک پرداخت شده است؛ اول «پرداخت به مالک» مربوط را لغو کنید.");
            }

            await ReverseFinanceCoreAsync(entry, reason.Trim(), ct);
            return 0;
        }, ct);

    /// <summary>برگشتِ اثرِ یک سند مالی امانی: پرداخت معکوس، سطرهای دفتر طرف‌ها و ژورنال.</summary>
    private async Task ReverseFinanceCoreAsync(CustodyFinanceEntry entry, string reason, CancellationToken ct)
    {
        var reference = entry.CustodySaleId.HasValue
            ? await db.CustodySales.Where(s => s.Id == entry.CustodySaleId).Select(s => s.SaleNumber).FirstAsync(ct)
            : $"AF-{entry.Id:D5}";
        if (entry.PaymentTransactionId.HasValue)
        {
            var reversal = await NewPaymentAsync(entry, reference, reversal: true, ct);
            await db.SaveChangesAsync(ct);
            entry.ReversalPaymentTransactionId = reversal.Id;
        }

        await ReverseLedgerRowsAsync(FinanceSourceType(entry.Kind), entry.Id, $"لغو: {reason}", reference, ct);
        var companyId = await systemCompany.GetOwnerCompanyIdAsync(ct);
        await accounting.TryReverseAsync(
            CustodyAccountingAdapter.FinanceEventId(entry.Id),
            CustodyAccountingAdapter.FinanceJournalNumber(companyId, entry.Id),
            DateTime.UtcNow,
            $"Cancel custody {entry.Kind} {reference}",
            ct);

        entry.Status = CustodyDocumentStatus.Cancelled;
        entry.CancelledAtUtc = DateTime.UtcNow;
        entry.CancelledByUserId = currentUser?.UserId;
        entry.CancellationReason = reason.Length > 500 ? reason[..500] : reason;
        await audit.LogAsync(FinanceEntity, entry.Id, AuditAction.Reverse, diff: Describe(new { Kind = entry.Kind.ToString(), Reason = reason }), ct: ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>سند استرداد همراهِ برگشت؛ همان مسیرِ پرداخت، دفتر طرف‌ها و ژورنالِ اسناد مالی امانی.</summary>
    private async Task<int> PostRefundAsync(CustodySale sale, CustodySaleReturn saleReturn, CustodyFinanceKind kind, decimal amount,
        CustodyRate rate, OwnerEffect effect, int? cashAccountId, string? documentNumber, string description, CancellationToken ct)
    {
        var entry = new CustodyFinanceEntry
        {
            Kind = kind,
            CustodySaleId = sale.Id,
            CustodySaleReturnId = saleReturn.Id,
            CustodyLotId = sale.CustodyLotId,
            OwnerPartyType = sale.OwnerPartyType,
            OwnerPartyId = sale.OwnerPartyId,
            CustomerId = sale.CustomerId,
            EntryDate = saleReturn.ReturnDate,
            AmountUsd = effect.AmountUsd,
            Currency = sale.Currency,
            Amount = amount,
            Commission = 0m,
            RateToUsd = rate.ToUsd,
            CurrencyPerUsd = rate.PerUsd,
            RateSource = rate.IsUsd ? null : rate.Source,
            OwnerCurrency = effect.OwnerCurrency,
            OwnerAmount = effect.OwnerAmount,
            OwnerCommission = 0m,
            OwnerRateToUsd = effect.OwnerRate.ToUsd,
            FxDifference = effect.FxDifference,
            FxOwnerShare = effect.FxOwnerShare,
            FxCompanyUsd = effect.FxCompanyUsd,
            CashAccountId = kind == CustodyFinanceKind.CustomerRefund ? cashAccountId : null,
            DocumentNumber = Clean(documentNumber),
            Description = description,
            CreatedByUserId = currentUser?.UserId
        };
        db.CustodyFinanceEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        if (entry.CashAccountId.HasValue)
        {
            var payment = await NewPaymentAsync(entry, sale.SaleNumber, reversal: false, ct);
            await db.SaveChangesAsync(ct);
            entry.PaymentTransactionId = payment.Id;
        }
        foreach (var row in BuildFinanceRows(entry, sale.SaleNumber, effect.OwnerRate))
            ledger.Post(row);
        await db.SaveChangesAsync(ct);
        RequireJournal(await accounting.TryPostFinanceAsync(entry, sale, ct));
        await audit.LogAsync(FinanceEntity, entry.Id, AuditAction.Insert, diff: Describe(new
        {
            Kind = entry.Kind.ToString(), sale.SaleNumber, saleReturn.ReturnNumber, entry.Currency, entry.Amount, entry.AmountUsd,
            entry.OwnerCurrency, entry.OwnerAmount, entry.FxDifference, entry.FxOwnerShare, entry.FxCompanyUsd, entry.CashAccountId,
            entry.PaymentTransactionId, entry.DocumentNumber
        }), ct: ct);
        await db.SaveChangesAsync(ct);
        return entry.Id;
    }

    private async Task RequireCashAccountAsync(int cashAccountId, string currency, CancellationToken ct)
    {
        var cash = await db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == cashAccountId, ct)
            ?? throw Rule("CUSTODY_CASH_ACCOUNT", "صندوق یا بانک پیدا نشد.");
        if (!cash.IsActive)
            throw Rule("CUSTODY_CASH_INACTIVE", "این صندوق/بانک غیرفعال است.");
        if (cash.AccountType != CashAccountType.Mixed && !string.Equals(cash.Currency, currency, StringComparison.OrdinalIgnoreCase))
            throw Rule("CUSTODY_CASH_CURRENCY", currency == SystemCurrency.BaseCurrencyCode ? "این صندوق/بانک دالری نیست." : $"این صندوق/بانک به ارز {currency} نیست.");
    }

    // ---------------------------------------------------------------- اثر ارزی بر حساب مالک

    /// <summary>
    /// اثرِ یک سند بر حساب مالک به ارز مالک. ستون‌های دالری (AmountUsd/CommissionUsd) ارزشِ دفتر کل‌اند.
    /// FxCompanyUsd سود (+) یا زیانِ (−) تسعیرِ شرکت است که در ژورنال به حساب تفاوت نرخ می‌رود.
    /// </summary>
    private sealed record OwnerEffect(string OwnerCurrency, decimal OwnerAmount, decimal OwnerCommission, CustodyRate OwnerRate,
        decimal AmountUsd, decimal CommissionUsd, decimal? FxDifference, decimal? FxOwnerShare, decimal? FxCompanyUsd)
    {
        public static OwnerEffect SameCurrency(string currency, decimal amount, decimal commission, CustodyRate rate, decimal amountUsd)
            => new(currency, amount, commission, rate, amountUsd, rate.ToUsdAmount(commission), null, null, null);
    }

    /// <summary>
    /// پولی که واقعاً از صندوق شرکت می‌گذرد (وصول یا استرداد). وقتی ارز فروش و ارز مالک یکی است، مالک
    /// همان مبلغ را به همان ارز می‌گیرد/می‌دهد و تفاوت نرخی نیست. وقتی فرق دارد: سهمِ مالک با نرخِ قفل‌شدهٔ
    /// توافق تبدیل می‌شود و تفاوتِ ارزشِ واقعیِ همان پول با آن (به ارز مالک) طبق توافق بین مالک و شرکت
    /// تقسیم می‌شود؛ اگر توافق ثبت نشده و تفاوت صفر نیست، سند ثبت نمی‌شود.
    /// </summary>
    private async Task<OwnerEffect> CashOwnerEffectAsync(CustodySale sale, decimal amount, decimal commission, CustodyRate cashRate,
        DateTime date, decimal? agreedPerUsd, string? rateReference, bool inbound, CancellationToken ct)
    {
        var saleCurrency = CustodyFx.Normalize(sale.Currency);
        var ownerCurrency = CustodyFx.Normalize(sale.EffectiveOwnerCurrency);
        var cashUsd = cashRate.ToUsdAmount(amount);
        if (saleCurrency == ownerCurrency)
            return OwnerEffect.SameCurrency(ownerCurrency, amount, commission, cashRate, cashUsd);

        var afn = saleCurrency == CustodyFx.Afn ? cashRate : await CustodyFx.ResolveAsync(db, CustodyFx.Afn, date, agreedPerUsd, rateReference, ct);
        var ownerRate = ownerCurrency == CustodyFx.Afn ? afn : CustodyRate.Usd;
        var lockedRate = sale.OwnerAgreedPerUsd ?? throw Rule("CUSTODY_FX_LOCKED_RATE", "نرخ قفل‌شدهٔ توافق مالک برای این فروش ثبت نشده است.");
        var locked = CustodyFx.Convert(amount, saleCurrency, ownerCurrency, lockedRate);
        var real = ownerCurrency == SystemCurrency.BaseCurrencyCode ? cashUsd : CustodyFx.Convert(amount, saleCurrency, ownerCurrency, afn.PerUsd);
        var difference = Money(real - locked);
        var share = CustodyFx.OwnerShare(sale);
        if (difference != 0m && share is null)
            throw Rule("CUSTODY_FX_RESPONSIBILITY",
                $"ارزشِ این پول به نرخ روز ({Amt(real, ownerCurrency)}) با نرخ قفل‌شدهٔ توافق مالک ({Amt(locked, ownerCurrency)}) فرق دارد، اما مسئول تفاوت نرخ (مالک، شرکت یا تقسیم) در توافق مالک ثبت نشده است. اول توافق را در صفحهٔ فروش ثبت کنید.");
        var ownerShare = share is null ? 0m : decimal.Round(share.Value * difference, 4, MidpointRounding.AwayFromZero);
        var ownerAmount = locked + ownerShare;
        var ownerCommission = CustodyFx.Convert(commission, saleCurrency, ownerCurrency, lockedRate);
        var ownerUsd = ownerRate.ToUsdAmount(ownerAmount);
        var commissionUsd = ownerRate.ToUsdAmount(ownerCommission);
        if (ownerAmount <= 0m || commissionUsd > cashUsd)
            throw Rule("CUSTODY_FX_AMOUNT", "مبلغ این سند پس از تبدیل ارز معتبر نیست.");
        return new OwnerEffect(ownerCurrency, ownerAmount, ownerCommission, ownerRate, cashUsd, commissionUsd,
            difference, ownerShare, inbound ? cashUsd - ownerUsd : ownerUsd - cashUsd);
    }

    /// <summary>
    /// پولی که مستقیم بین مشتری و مالک رد و بدل شد: صندوق شرکت تغییر نمی‌کند و تفاوت نرخی برای شرکت
    /// نیست؛ طلب مشتری و «در انتظار وصول» با نرخِ همان فروش و کمیشن به ارز مالک (نرخ قفل‌شده) ثبت می‌شود.
    /// </summary>
    private async Task<OwnerEffect> DirectOwnerEffectAsync(CustodySale sale, decimal amount, decimal commission, CustodyRate rate,
        DateTime date, decimal? agreedPerUsd, string? rateReference, CancellationToken ct)
    {
        var saleCurrency = CustodyFx.Normalize(sale.Currency);
        var ownerCurrency = CustodyFx.Normalize(sale.EffectiveOwnerCurrency);
        var receivableUsd = CustodyFx.AtSaleRate(sale, amount);
        if (saleCurrency == ownerCurrency)
        {
            var same = OwnerEffect.SameCurrency(ownerCurrency, amount, commission, rate, receivableUsd);
            if (same.CommissionUsd > receivableUsd) throw Rule("CUSTODY_FX_AMOUNT", "کمیشن این سند از ارزش مبلغ آن بیشتر است.");
            return same;
        }
        // بدون کمیشن، ارزشِ دالریِ بخشِ مالک لازم نیست؛ نرخِ خودِ فروش کافی است و نرخ تازه‌ای خواسته نمی‌شود.
        var ownerRate = commission == 0m ? SaleOwnerRate(sale)
            : ownerCurrency == CustodyFx.Afn
                ? (saleCurrency == CustodyFx.Afn ? rate : await CustodyFx.ResolveAsync(db, CustodyFx.Afn, date, agreedPerUsd, rateReference, ct))
                : CustodyRate.Usd;
        var lockedRate = sale.OwnerAgreedPerUsd ?? throw Rule("CUSTODY_FX_LOCKED_RATE", "نرخ قفل‌شدهٔ توافق مالک برای این فروش ثبت نشده است.");
        var ownerCommission = CustodyFx.Convert(commission, saleCurrency, ownerCurrency, lockedRate);
        var commissionUsd = ownerRate.ToUsdAmount(ownerCommission);
        if (commissionUsd > receivableUsd) throw Rule("CUSTODY_FX_AMOUNT", "کمیشن این سند از ارزش مبلغ آن بیشتر است.");
        return new OwnerEffect(ownerCurrency, CustodyFx.Convert(amount, saleCurrency, ownerCurrency, lockedRate), ownerCommission, ownerRate,
            receivableUsd, commissionUsd, null, null, null);
    }

    private static (CustodyFxResponsibility? Responsibility, decimal? OwnerSharePercent, string? Reference) ValidateFxAgreement(
        CustodyFxResponsibility? responsibility, decimal? ownerSharePercent, string? reference)
    {
        if (responsibility is null)
            return (null, null, null);
        if (!Enum.IsDefined(responsibility.Value))
            throw Rule("CUSTODY_FX_RESPONSIBILITY", "مسئول تفاوت نرخ معتبر نیست.");
        if (string.IsNullOrWhiteSpace(reference))
            throw Rule("CUSTODY_FX_AGREEMENT_REFERENCE", "مرجعِ توافق مالک دربارهٔ تفاوت نرخ الزامی است.");
        if (responsibility == CustodyFxResponsibility.Split)
        {
            if (ownerSharePercent is not > 0m || ownerSharePercent >= 100m || decimal.Round(ownerSharePercent.Value, 4) != ownerSharePercent)
                throw Rule("CUSTODY_FX_SPLIT", "برای تقسیم، سهم مالک باید بین ۰ و ۱۰۰ فیصد (حداکثر چهار رقم اعشار) باشد.");
            return (responsibility, ownerSharePercent, reference.Trim());
        }
        return (responsibility, null, reference.Trim());
    }

    public Task SetFxAgreementAsync(int saleId, CustodyFxResponsibility responsibility, decimal? ownerSharePercent, string reference, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            var sale = await db.CustodySales.FirstOrDefaultAsync(s => s.Id == saleId, ct)
                ?? throw Rule("CUSTODY_SALE_NOT_FOUND", "فروش امانی پیدا نشد.");
            await LockOwnerAsync(new CustodyOwner(sale.OwnerPartyType, sale.OwnerPartyId), ct);
            await LockSaleAsync(sale.Id, ct);
            await db.Entry(sale).ReloadAsync(ct);
            if (sale.Status != CustodyDocumentStatus.Posted)
                throw Rule("CUSTODY_SALE_CANCELLED", "این فروش لغو شده است.");
            if (!sale.IsCrossCurrency)
                throw Rule("CUSTODY_FX_NOT_APPLICABLE", "ارز فروش و ارز مالک یکی است؛ تفاوت نرخی وجود ندارد.");
            if (sale.FxResponsibility.HasValue)
                throw Rule("CUSTODY_FX_AGREEMENT_EXISTS", "توافق تفاوت نرخ این فروش قبلاً ثبت شده و تغییر نمی‌کند.");
            var (value, share, cleanReference) = ValidateFxAgreement(responsibility, ownerSharePercent, reference);
            sale.FxResponsibility = value;
            sale.FxOwnerSharePercent = share;
            sale.FxAgreementReference = cleanReference;
            await audit.LogAsync(SaleEntity, sale.Id, AuditAction.Update, diff: Describe(new
            {
                sale.SaleNumber, FxResponsibility = value?.ToString(), FxOwnerSharePercent = share, FxAgreementReference = cleanReference
            }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    // ---------------------------------------------------------------- برگشتِ قسمیِ فروش

    public Task<int> RecordReturnAsync(CustodySaleReturnInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            if (input.Lines is null || input.Lines.Count == 0)
                throw Rule("CUSTODY_RETURN_LINES", "مقدار برگشت را وارد کنید.");
            // سطرِ لیتری: MT یک‌بار با همان قاعدهٔ اندازه‌گیری محاسبه می‌شود.
            var lines = input.Lines.Select(l =>
            {
                if (l.Measurement is null) return l;
                var mt = l.Measurement.ToMt();
                if (l.QuantityMt != 0m && l.QuantityMt != mt)
                    throw Rule("CUSTODY_MEASUREMENT_MISMATCH", "مقدار MT با لیتر و چگالی سند یکسان نیست.");
                return l with { QuantityMt = mt };
            }).ToList();
            foreach (var line in lines)
                RequirePositive(line.QuantityMt, "مقدار برگشت", 4);
            var sale = await db.CustodySales.FirstOrDefaultAsync(s => s.Id == input.SaleId, ct)
                ?? throw Rule("CUSTODY_SALE_NOT_FOUND", "فروش امانی پیدا نشد.");
            var owner = new CustodyOwner(sale.OwnerPartyType, sale.OwnerPartyId);
            await LockOwnerAsync(owner, ct);
            await LockSaleAsync(sale.Id, ct);
            await db.Entry(sale).ReloadAsync(ct);
            if (sale.Status != CustodyDocumentStatus.Posted)
                throw Rule("CUSTODY_SALE_CANCELLED", "این فروش لغو شده است.");
            await EnsureAccountingCompleteAsync(owner, ct);
            var returnDate = ToUtc(input.ReturnDate);
            if (returnDate.Date < sale.SaleDate.Date)
                throw Rule("CUSTODY_RETURN_DATE", "تاریخ برگشت پیش از تاریخ فروش نمی‌شود.");

            // هر سطر باید به همان بار و همان محلی برگردد که این فروش از آن خارج کرده بود.
            var returnable = await ReturnableBySourceAsync(sale, ct);
            var requested = lines
                .GroupBy(l => (l.LotId, l.Location.TerminalId, l.Location.StorageTankId))
                .Select(g => (Key: g.Key, Quantity: g.Sum(x => x.QuantityMt),
                    Measurement: g.Count() == 1 ? g.Single().Measurement : null))
                .ToList();
            if (lines.Any(l => l.Measurement is not null) && requested.Count != lines.Count)
                throw Rule("CUSTODY_MEASUREMENT_DUPLICATE", "برای یک بار و محل فقط یک سطرِ اندازه‌گیری‌شده به لیتر مجاز است.");
            foreach (var (key, lineQuantity, _) in requested)
            {
                if (!returnable.TryGetValue(key, out var available))
                    throw Rule("CUSTODY_RETURN_SOURCE", "برگشت فقط به بار و محلی ممکن است که همین فروش از آن خارج شده است.");
                if (lineQuantity > available)
                    throw Rule("CUSTODY_RETURN_EXCEEDS", $"از این بار/محل حداکثر {Qty(available)} تُن قابل برگشت است.");
            }

            // همهٔ مبالغِ برگشت به ارز فروش‌اند؛ اثر بر مالک به ارز مالک.
            var currency = CustodyFx.Normalize(sale.Currency);
            var ownerCurrency = CustodyFx.Normalize(sale.EffectiveOwnerCurrency);
            var totals = await ReturnTotalsAsync(sale.Id, ct);
            var quantity = requested.Sum(r => r.Quantity);
            var netQuantityBefore = sale.QuantityMt - totals.QuantityMt;
            var netAmountBefore = sale.EffectiveAmount - totals.Amount;
            var netCommissionBefore = sale.EffectiveCommission - totals.Commission;
            var netQuantityAfter = netQuantityBefore - quantity;
            // آخرین برگشتِ کامل، باقیماندهٔ دقیق مبلغ را می‌گیرد تا گردکردن مبلغ را کم یا زیاد نکند.
            var amount = netQuantityAfter == 0m ? netAmountBefore : Money(quantity * sale.EffectiveUnitPrice);
            var netAmountAfter = netAmountBefore - amount;
            if (amount <= 0m || netAmountAfter < 0m)
                throw Rule("CUSTODY_RETURN_AMOUNT", "مبلغ برگشت معتبر نیست.");

            var collected = await CollectedAsync(sale.Id, excludeEntryId: null, ct);
            var outstanding = netAmountBefore - collected;
            // بخشی از مبلغ برگشت که پولش قبلاً وصول شده، باید در همین سند و با سند واقعی مسترد شود.
            var refundDue = Math.Max(amount - outstanding, 0m);
            var refundCompany = input.RefundFromCompanyUsd;
            var refundOwner = input.RefundByOwnerUsd;
            if (refundCompany < 0m || refundOwner < 0m || decimal.Round(refundCompany, 2) != refundCompany || decimal.Round(refundOwner, 2) != refundOwner)
                throw Rule("CUSTODY_REFUND_AMOUNT", "مبلغ استرداد معتبر نیست.");
            if (refundCompany + refundOwner != refundDue)
                throw Rule("CUSTODY_RETURN_COLLECTED", refundDue == 0m
                    ? "برای این برگشت پولی وصول نشده است؛ استرداد ثبت نمی‌شود."
                    : $"مبلغ این برگشت ({Amt(amount, currency)}) از ماندهٔ وصول‌نشدهٔ فروش ({Amt(outstanding, currency)}) بیشتر است؛ دقیقاً {Amt(refundDue, currency)} باید با سند واقعی مسترد شود (از صندوق شرکت و/یا مستقیم توسط مالک).");
            if (refundCompany > 0m)
            {
                var companyCollected = await NetCollectedAsync(sale.Id, CustodyFinanceKind.Collection, CustodyFinanceKind.CustomerRefund, ct);
                if (refundCompany > companyCollected)
                    throw Rule("CUSTODY_REFUND_EXCEEDS", $"پولی که از این فروش نزد صندوق شرکت آمده {Amt(companyCollected, currency)} است؛ استرداد بیشتر از آن ممکن نیست.");
                if (!input.RefundCashAccountId.HasValue)
                    throw Rule("CUSTODY_CASH_ACCOUNT", "صندوق یا بانکِ استرداد را انتخاب کنید.");
                if (string.IsNullOrWhiteSpace(input.RefundDocumentNumber))
                    throw Rule("CUSTODY_REFUND_DOCUMENT", "شمارهٔ سند واقعیِ پرداختِ استرداد الزامی است.");
                await RequireCashAccountAsync(input.RefundCashAccountId.Value, currency, ct);
            }
            if (refundOwner > 0m)
            {
                var ownerCollected = await NetCollectedAsync(sale.Id, CustodyFinanceKind.DirectToOwner, CustodyFinanceKind.OwnerDirectRefund, ct);
                if (refundOwner > ownerCollected)
                    throw Rule("CUSTODY_REFUND_EXCEEDS", $"پولی که مشتری مستقیم به مالک داده {Amt(ownerCollected, currency)} است؛ استردادِ مالک بیشتر از آن ممکن نیست.");
                if (string.IsNullOrWhiteSpace(input.OwnerRefundReference))
                    throw Rule("CUSTODY_REFUND_DOCUMENT", "سندِ استردادِ مستقیمِ مالک به مشتری الزامی است.");
            }

            // کمیشن با همان روشِ فروش روی مقدار/مبلغِ باقی دوباره سنجیده می‌شود. کمیشنِ ثابت مربوط
            // به خودِ معامله است و تا برگشتِ کامل باقی می‌ماند.
            var netCommissionAfter = netQuantityAfter == 0m
                ? 0m
                : sale.CommissionMethod switch
                {
                    CustodyCommissionMethod.None => 0m,
                    CustodyCommissionMethod.FixedPerSale => netCommissionBefore,
                    _ => CalculateCommission(sale.CommissionMethod, sale.CommissionRate, netQuantityAfter, netAmountAfter)
                };
            if (netCommissionAfter > netAmountAfter)
                throw Rule("CUSTODY_RETURN_COMMISSION", "کمیشنِ ثابت از مبلغِ باقی‌ماندهٔ فروش بیشتر می‌شود؛ برگشتِ کامل یا لغو فروش را ثبت کنید.");
            var commissionReduction = Math.Max(netCommissionBefore - netCommissionAfter, 0m);
            var recognizedReversal = 0m;
            if (sale.CommissionRecognition == CustodyCommissionRecognition.OnSale)
            {
                recognizedReversal = commissionReduction;
            }
            else
            {
                // کمیشنِ درآمدشده باید با پولِ وصول‌شدهٔ باقی (بعد از استرداد) و کمیشنِ مقدارِ باقی هم‌خوان
                // بماند؛ مازادش معکوس می‌شود. بدون استرداد، کمیشنِ پولِ وصول‌شده دست نمی‌خورد.
                var recognized = await CommissionOnCollectionsAsync(sale.Id, ct);
                var collectedAfter = collected - refundDue;
                var target = netAmountAfter == 0m ? 0m
                    : collectedAfter == netAmountAfter ? netCommissionAfter
                    : Math.Min(recognized, Money(netCommissionAfter * collectedAfter / netAmountAfter));
                recognizedReversal = Math.Min(Math.Max(recognized - target, 0m), commissionReduction);
            }

            // کمیشنِ معکوس و کاهشِ کمیشن به ارز مالک (نرخ قفل‌شده وقتی ارزها فرق دارند). ارزشِ دالریِ
            // کمیشنِ «هنگام فروش» با همان نرخِ شناسایی‌اش برمی‌گردد؛ کمیشنِ «به تناسب وصول» با نرخ روز برگشت.
            var cross = currency != ownerCurrency;
            decimal ToOwner(decimal value) => cross ? CustodyFx.Convert(value, currency, ownerCurrency, sale.OwnerAgreedPerUsd!.Value) : value;
            var ownerReversal = ToOwner(recognizedReversal);
            var ownerReduction = ToOwner(commissionReduction);
            var commissionRate = sale.CommissionRecognition == CustodyCommissionRecognition.OnSale || ownerReversal == 0m
                ? SaleOwnerRate(sale)
                : await CustodyFx.ResolveAsync(db, ownerCurrency, returnDate, null, null, ct);

            // استرداد از صندوق، پولِ مالک نزد شرکت را کم می‌کند. اگر آن پول قبلاً به مالک پرداخت شده،
            // بدون تأیید و سندِ قابل وصول از مالک، طلبِ حدسی از مالک ساخته نمی‌شود.
            CustodyRate? refundRate = null;
            OwnerEffect? refundEffect = null;
            if (refundCompany > 0m)
            {
                refundRate = await CustodyFx.ResolveAsync(db, currency, returnDate, null, null, ct);
                refundEffect = await CashOwnerEffectAsync(sale, refundCompany, 0m, refundRate, returnDate, null, null, inbound: false, ct);
                var fundsAfter = await OwnerFundsBalanceAsync(owner, ownerCurrency, ct) - refundEffect.OwnerAmount + ownerReversal;
                if (fundsAfter < 0m && await db.CustodyFinanceEntries.AnyAsync(e =>
                        e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId
                        && (e.OwnerCurrency ?? "USD") == ownerCurrency
                        && e.Kind == CustodyFinanceKind.OwnerPayout && e.Status == CustodyDocumentStatus.Posted, ct)
                    && (!input.OwnerRecoveryConfirmed || string.IsNullOrWhiteSpace(input.OwnerRecoveryReference)))
                {
                    throw Rule("CUSTODY_REFUND_OWNER_PAID",
                        $"پولِ این فروش قبلاً به مالک پرداخت شده و استرداد حساب مالک را {Amt(-fundsAfter, ownerCurrency)} بدهکار می‌کند؛ فقط با تأیید صریح و سندِ قابل وصول از مالک ممکن است.");
                }
            }
            CustodyRate? directRate = null;
            OwnerEffect? directEffect = null;
            if (refundOwner > 0m)
            {
                directRate = SaleRate(sale);
                directEffect = await DirectOwnerEffectAsync(sale, refundOwner, 0m, directRate, returnDate, null, null, ct);
            }

            var stockKey = await custody.RecordSaleReturnInAsync(
                requested.Select(r => new CustodySaleSource(r.Key.LotId, new CustodyLocation(r.Key.TerminalId, r.Key.StorageTankId), r.Quantity, r.Measurement)).ToList(),
                returnDate, input.DocumentNumber, ct);

            var saleReturn = new CustodySaleReturn
            {
                ReturnNumber = "TMP-" + Guid.NewGuid().ToString("N"),
                CustodySaleId = sale.Id,
                OwnerPartyType = sale.OwnerPartyType,
                OwnerPartyId = sale.OwnerPartyId,
                CustomerId = sale.CustomerId,
                ReturnDate = returnDate,
                QuantityMt = quantity,
                AmountUsd = CustodyFx.AtSaleRate(sale, amount),
                CommissionUsd = commissionRate.ToUsdAmount(ownerReduction),
                RecognizedCommissionReversedUsd = commissionRate.ToUsdAmount(ownerReversal),
                Amount = amount,
                Commission = commissionReduction,
                RecognizedCommissionReversed = recognizedReversal,
                OwnerCommissionReversed = ownerReversal,
                OwnerRateToUsd = commissionRate.ToUsd,
                StockOperationKey = stockKey,
                DocumentNumber = Clean(input.DocumentNumber),
                Notes = Clean(input.Notes),
                CreatedByUserId = currentUser?.UserId
            };
            db.CustodySaleReturns.Add(saleReturn);
            formTokens.Stamp(input.FormToken, "Custody.Return", ReturnEntity);
            await db.SaveChangesAsync(ct);
            saleReturn.ReturnNumber = $"AR-{saleReturn.Id:D5}";

            if (saleReturn.RecognizedCommissionReversedUsd > 0m)
            {
                ledger.Post(PartyRow(ReturnSourceType, saleReturn.Id, saleReturn.ReturnDate, saleReturn.RecognizedCommissionReversedUsd,
                    saleReturn.ReturnNumber + CommissionSuffix, sale.OwnerPartyType, sale.OwnerPartyId, CompanyFlowEffect.Receipt,
                    $"برگشت کمیشن فروش امانی {sale.SaleNumber} ({saleReturn.ReturnNumber})",
                    Source(ownerReversal, commissionRate)));
            }
            await db.SaveChangesAsync(ct);
            RequireJournal(await accounting.TryPostReturnAsync(saleReturn, sale, ct));

            var refundIds = new List<int>();
            if (refundCompany > 0m)
            {
                refundIds.Add(await PostRefundAsync(sale, saleReturn, CustodyFinanceKind.CustomerRefund, refundCompany,
                    refundRate!, refundEffect!, input.RefundCashAccountId, input.RefundDocumentNumber,
                    input.OwnerRecoveryConfirmed && !string.IsNullOrWhiteSpace(input.OwnerRecoveryReference)
                        ? $"استرداد برگشت {saleReturn.ReturnNumber}؛ قابل وصول از مالک: {input.OwnerRecoveryReference!.Trim()}"
                        : $"استرداد برگشت {saleReturn.ReturnNumber}", ct));
            }
            if (refundOwner > 0m)
            {
                refundIds.Add(await PostRefundAsync(sale, saleReturn, CustodyFinanceKind.OwnerDirectRefund, refundOwner,
                    directRate!, directEffect!, null, input.OwnerRefundReference, $"مالک مستقیم به مشتری پس داد — برگشت {saleReturn.ReturnNumber}", ct));
            }

            await audit.LogAsync(ReturnEntity, saleReturn.Id, AuditAction.Insert, diff: Describe(new
            {
                saleReturn.ReturnNumber, sale.SaleNumber,
                Lines = requested.Select(r => new { r.Key.LotId, r.Key.TerminalId, r.Key.StorageTankId, r.Quantity }),
                saleReturn.QuantityMt, sale.Currency, saleReturn.Amount, saleReturn.AmountUsd, saleReturn.Commission, saleReturn.CommissionUsd,
                saleReturn.RecognizedCommissionReversed, saleReturn.RecognizedCommissionReversedUsd, sale.OwnerCurrency, saleReturn.OwnerCommissionReversed,
                RefundFromCompany = refundCompany, RefundByOwner = refundOwner, Refunds = refundIds,
                OwnerRecovery = input.OwnerRecoveryConfirmed ? input.OwnerRecoveryReference : null
            }), ct: ct);
            await db.SaveChangesAsync(ct);
            return saleReturn.Id;
        }, ct);

    public Task CancelReturnAsync(int returnId, string reason, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequireReason(reason);
            var saleReturn = await db.CustodySaleReturns.FirstOrDefaultAsync(r => r.Id == returnId, ct)
                ?? throw Rule("CUSTODY_RETURN_NOT_FOUND", "برگشت فروش پیدا نشد.");
            await LockOwnerAsync(new CustodyOwner(saleReturn.OwnerPartyType, saleReturn.OwnerPartyId), ct);
            await LockSaleAsync(saleReturn.CustodySaleId, ct);
            await db.Entry(saleReturn).ReloadAsync(ct);
            if (saleReturn.Status == CustodyDocumentStatus.Cancelled)
                throw Rule("CUSTODY_RETURN_CANCELLED", "این برگشت قبلاً لغو شده است.");
            // کمیشنِ هر برگشت بر پایهٔ برگشت‌های پیش از آن سنجیده شده؛ لغو از آخر به اول.
            if (await db.CustodySaleReturns.AnyAsync(r => r.CustodySaleId == saleReturn.CustodySaleId
                    && r.Status == CustodyDocumentStatus.Posted && r.Id > saleReturn.Id, ct))
                throw Rule("CUSTODY_RETURN_NOT_LATEST", "برگشت‌های بعدیِ همین فروش را اول لغو کنید.");
            var sale = await db.CustodySales.AsNoTracking().SingleAsync(s => s.Id == saleReturn.CustodySaleId, ct);

            // استردادهای همین برگشت با آن برمی‌گردند؛ پرداخت معکوس، دفتر و ژورنال هر کدام برگشت می‌خورد.
            var refunds = await db.CustodyFinanceEntries
                .Where(e => e.CustodySaleReturnId == saleReturn.Id && e.Status == CustodyDocumentStatus.Posted)
                .OrderByDescending(e => e.Id).ToListAsync(ct);
            foreach (var refund in refunds)
                await ReverseFinanceCoreAsync(refund, $"لغو برگشت {saleReturn.ReturnNumber}: {reason.Trim()}", ct);

            await custody.ReverseSaleReturnInAsync(saleReturn.StockOperationKey, $"لغو برگشت {saleReturn.ReturnNumber}: {reason.Trim()}", ct);
            await ReverseLedgerRowsAsync(ReturnSourceType, saleReturn.Id, $"لغو برگشت فروش امانی {saleReturn.ReturnNumber}", saleReturn.ReturnNumber, ct);
            var companyId = await systemCompany.GetOwnerCompanyIdAsync(ct);
            await accounting.TryReverseAsync(
                CustodyAccountingAdapter.ReturnEventId(saleReturn.Id),
                CustodyAccountingAdapter.ReturnJournalNumber(companyId, saleReturn.Id),
                DateTime.UtcNow,
                $"Cancel custody sale return {saleReturn.ReturnNumber} of {sale.SaleNumber}",
                ct);

            saleReturn.Status = CustodyDocumentStatus.Cancelled;
            saleReturn.CancelledAtUtc = DateTime.UtcNow;
            saleReturn.CancelledByUserId = currentUser?.UserId;
            saleReturn.CancellationReason = reason.Trim();
            await audit.LogAsync(ReturnEntity, saleReturn.Id, AuditAction.Reverse,
                diff: Describe(new { saleReturn.ReturnNumber, Reason = reason.Trim() }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    /// <summary>جمعِ برگشت‌های فعالِ یک فروش؛ مبالغ به ارز فروش.</summary>
    public sealed record ReturnTotals(decimal QuantityMt, decimal Amount, decimal Commission, decimal RecognizedCommissionReversed);

    public static async Task<ReturnTotals> ReturnTotalsAsync(ApplicationDbContext db, int saleId, CancellationToken ct)
    {
        var rows = await db.CustodySaleReturns.AsNoTracking()
            .Where(r => r.CustodySaleId == saleId && r.Status == CustodyDocumentStatus.Posted)
            .Select(r => new
            {
                r.QuantityMt, Amount = r.Amount ?? r.AmountUsd, Commission = r.Commission ?? r.CommissionUsd,
                Reversed = r.RecognizedCommissionReversed ?? r.RecognizedCommissionReversedUsd
            })
            .ToListAsync(ct);
        return new ReturnTotals(rows.Sum(r => r.QuantityMt), rows.Sum(r => r.Amount), rows.Sum(r => r.Commission), rows.Sum(r => r.Reversed));
    }

    private Task<ReturnTotals> ReturnTotalsAsync(int saleId, CancellationToken ct) => ReturnTotalsAsync(db, saleId, ct);

    /// <summary>سهمِ هر بار/محل در خروجِ این فروش منهای برگشت‌های فعالِ قبلی — از خودِ دفتر امانی.</summary>
    public static async Task<Dictionary<(int LotId, int TerminalId, int? StorageTankId), decimal>> ReturnableBySourceAsync(
        ApplicationDbContext db, CustodySale sale, CancellationToken ct)
    {
        var sold = await db.CustodyMovements.AsNoTracking()
            .Where(m => m.OperationKey == sale.StockOperationKey && m.ReversalOfCustodyMovementId == null && !m.IsReversed)
            .Select(m => new { m.CustodyLotId, m.TerminalId, m.StorageTankId, m.QuantityMt })
            .ToListAsync(ct);
        var returnKeys = await db.CustodySaleReturns.AsNoTracking()
            .Where(r => r.CustodySaleId == sale.Id && r.Status == CustodyDocumentStatus.Posted)
            .Select(r => r.StockOperationKey).ToListAsync(ct);
        var returned = await db.CustodyMovements.AsNoTracking()
            .Where(m => returnKeys.Contains(m.OperationKey) && m.ReversalOfCustodyMovementId == null && !m.IsReversed)
            .Select(m => new { m.CustodyLotId, m.TerminalId, m.StorageTankId, m.QuantityMt })
            .ToListAsync(ct);
        return sold.GroupBy(m => (m.CustodyLotId, m.TerminalId, m.StorageTankId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt)
                - returned.Where(r => (r.CustodyLotId, r.TerminalId, r.StorageTankId) == g.Key).Sum(r => r.QuantityMt));
    }

    private Task<Dictionary<(int LotId, int TerminalId, int? StorageTankId), decimal>> ReturnableBySourceAsync(CustodySale sale, CancellationToken ct)
        => ReturnableBySourceAsync(db, sale, ct);

    public static Task<decimal> ProviderPaidAsync(ApplicationDbContext db, int chargeId, CancellationToken ct)
        => db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.SettlesFinanceEntryId == chargeId && e.Kind == CustodyFinanceKind.ProviderPayment
                && e.Status == CustodyDocumentStatus.Posted)
            .SumAsync(e => e.Amount ?? e.AmountUsd, ct);

    private Task<decimal> ProviderPaidAsync(int chargeId, CancellationToken ct) => ProviderPaidAsync(db, chargeId, ct);


    // ---------------------------------------------------------------- balances (همان تعریفِ صفحهٔ حساب مالک)

    /// <summary>
    /// پولِ مالک نزد شرکت به یک ارز = وصولِ نقدی (سهمِ مالک به ارز مالک) − همهٔ کمیشن‌های درآمدشده
    /// (+ کمیشنِ برگشت‌خورده با برگشت فروش) − مصارف به حساب مالک (نقد یا نسیهٔ خدمات‌دهنده) − حق خدمات
    /// − پرداخت‌ها − استرداد از صندوق. منفی یعنی مالک به شرکت بدهکار است. پرداخت به خدمات‌دهنده حساب
    /// مالک را تغییر نمی‌دهد. هر ارز جدا حساب می‌شود؛ ارزها با هم جمع یا تهاتر نمی‌شوند.
    /// </summary>
    public static async Task<decimal> OwnerFundsBalanceAsync(ApplicationDbContext db, CustodyOwner owner, string currency, CancellationToken ct)
        => (await OwnerFundsBalancesAsync(db, owner, ct)).GetValueOrDefault(CustodyFx.Normalize(currency));

    /// <summary>ماندهٔ دالری (سازگار با خوانندگانِ قبلی).</summary>
    public static Task<decimal> OwnerFundsBalanceAsync(ApplicationDbContext db, CustodyOwner owner, CancellationToken ct)
        => OwnerFundsBalanceAsync(db, owner, SystemCurrency.BaseCurrencyCode, ct);

    /// <param name="before">«تا تاریخ»: سندِ تا آن تاریخ؛ سندی که بعد از آن لغو شده در آن تاریخ هنوز فعال بوده است.</param>
    public static async Task<Dictionary<string, decimal>> OwnerFundsBalancesAsync(ApplicationDbContext db, CustodyOwner owner, CancellationToken ct,
        DateTime? before = null)
    {
        var entries = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId
                && (e.Status == CustodyDocumentStatus.Posted || (before != null && e.CancelledAtUtc >= before))
                && (before == null || e.EntryDate < before))
            .GroupBy(e => new { e.Kind, Currency = e.OwnerCurrency ?? "USD" })
            .Select(g => new
            {
                g.Key.Kind, g.Key.Currency,
                Amount = g.Sum(e => e.OwnerAmount ?? e.AmountUsd),
                Commission = g.Sum(e => e.OwnerCommission ?? e.CommissionUsd)
            })
            .ToListAsync(ct);
        var saleCommission = await db.CustodySales.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId
                && (s.Status == CustodyDocumentStatus.Posted || (before != null && s.CancelledAtUtc >= before))
                && (before == null || s.SaleDate < before))
            .GroupBy(s => s.OwnerCurrency ?? "USD")
            .Select(g => new { Currency = g.Key, Amount = g.Sum(s => s.CommissionRecognizedAtSaleOwner ?? s.CommissionRecognizedAtSaleUsd) })
            .ToListAsync(ct);
        var returnedCommission = await db.CustodySaleReturns.AsNoTracking()
            .Where(r => r.OwnerPartyType == owner.PartyType && r.OwnerPartyId == owner.PartyId
                && (r.Status == CustodyDocumentStatus.Posted || (before != null && r.CancelledAtUtc >= before))
                && (before == null || r.ReturnDate < before))
            .GroupBy(r => r.CustodySale!.OwnerCurrency ?? "USD")
            .Select(g => new { Currency = g.Key, Amount = g.Sum(r => r.OwnerCommissionReversed ?? r.RecognizedCommissionReversedUsd) })
            .ToListAsync(ct);

        var currencies = entries.Select(e => e.Currency).Concat(saleCommission.Select(x => x.Currency))
            .Concat(returnedCommission.Select(x => x.Currency)).Distinct().ToList();
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var currency in currencies)
        {
            decimal Amount(CustodyFinanceKind kind) => entries.Where(e => e.Kind == kind && e.Currency == currency).Sum(e => e.Amount);
            result[currency] = Amount(CustodyFinanceKind.Collection)
                - entries.Where(e => e.Currency == currency).Sum(e => e.Commission)
                - saleCommission.Where(x => x.Currency == currency).Sum(x => x.Amount)
                + returnedCommission.Where(x => x.Currency == currency).Sum(x => x.Amount)
                - Amount(CustodyFinanceKind.ReimbursableExpense)
                - Amount(CustodyFinanceKind.ProviderCharge)
                - Amount(CustodyFinanceKind.ServiceFee)
                - Amount(CustodyFinanceKind.OwnerPayout)
                - Amount(CustodyFinanceKind.CustomerRefund);
        }
        return result;
    }

    private Task<decimal> OwnerFundsBalanceAsync(CustodyOwner owner, string currency, CancellationToken ct) => OwnerFundsBalanceAsync(db, owner, currency, ct);

    /// <summary>وصولِ خالص فروش (نزد شرکت و مستقیم به مالک) منهای استردادها.</summary>
    private Task<decimal> CollectedAsync(int saleId, int? excludeEntryId, CancellationToken ct)
        => db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.CustodySaleId == saleId && e.Status == CustodyDocumentStatus.Posted
                && (e.Kind == CustodyFinanceKind.Collection || e.Kind == CustodyFinanceKind.DirectToOwner
                    || e.Kind == CustodyFinanceKind.CustomerRefund || e.Kind == CustodyFinanceKind.OwnerDirectRefund)
                && (excludeEntryId == null || e.Id != excludeEntryId))
            .SumAsync(e => e.Kind == CustodyFinanceKind.CustomerRefund || e.Kind == CustodyFinanceKind.OwnerDirectRefund
                ? -(e.Amount ?? e.AmountUsd) : e.Amount ?? e.AmountUsd, ct);

    private Task<decimal> NetCollectedAsync(int saleId, CustodyFinanceKind collection, CustodyFinanceKind refund, CancellationToken ct)
        => db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.CustodySaleId == saleId && e.Status == CustodyDocumentStatus.Posted && (e.Kind == collection || e.Kind == refund))
            .SumAsync(e => e.Kind == refund ? -(e.Amount ?? e.AmountUsd) : e.Amount ?? e.AmountUsd, ct);

    /// <summary>کمیشنِ درآمدشده از وصول‌ها منهای آنچه برگشت‌ها معکوس کرده‌اند.</summary>
    private async Task<decimal> CommissionOnCollectionsAsync(int saleId, CancellationToken ct)
        => await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.CustodySaleId == saleId && e.Status == CustodyDocumentStatus.Posted)
            .SumAsync(e => e.Commission ?? e.CommissionUsd, ct)
            - await db.CustodySaleReturns.AsNoTracking()
                .Where(r => r.CustodySaleId == saleId && r.Status == CustodyDocumentStatus.Posted
                    && r.CustodySale!.CommissionRecognition == CustodyCommissionRecognition.OnCollection)
                .SumAsync(r => r.RecognizedCommissionReversed ?? r.RecognizedCommissionReversedUsd, ct);

    // ---------------------------------------------------------------- ledger rows

    private enum CompanyFlowEffect { Receipt, Outflow }

    /// <summary>
    /// سمتِ سطر با قاعدهٔ موجودِ <c>CompanyFlowDirectionResolver</c>: مشتری حسابِ دریافتنی است
    /// (Credit = برد)، فروشنده پرداختنی (Credit = رسید).
    /// </summary>
    private static LedgerPostingRequest PartyRow(
        string sourceType,
        int sourceId,
        DateTime date,
        decimal amountUsd,
        string reference,
        AccountingPartyType partyType,
        int partyId,
        CompanyFlowEffect effect,
        string description,
        LedgerSource? source = null)
    {
        var isReceivable = partyType == AccountingPartyType.Customer;
        var side = effect == CompanyFlowEffect.Outflow
            ? (isReceivable ? LedgerSide.Credit : LedgerSide.Debit)
            : (isReceivable ? LedgerSide.Debit : LedgerSide.Credit);
        return new LedgerPostingRequest
        {
            SourceType = sourceType,
            SourceId = sourceId,
            EntryDate = date.Date,
            Side = side,
            AmountUsd = amountUsd,
            SourceAmount = source?.Amount ?? amountUsd,
            SourceCurrencyCode = source?.Currency ?? "USD",
            AppliedFxRateToUsd = source?.ToUsd ?? 1m,
            AppliedCurrencyPerUsdRate = source?.PerUsd ?? 1m,
            AppliedFxRateDate = date.Date,
            Description = description,
            Reference = reference,
            CustomerId = partyType == AccountingPartyType.Customer ? partyId : null,
            SupplierId = partyType == AccountingPartyType.Supplier ? partyId : null
        };
    }

    /// <summary>
    /// سطرهای دفتر طرف‌ها؛ سطرِ مالک به ارز مالک با همان ارزش دالری‌ای که در ژورنالِ حساب ۲۶۰۰ می‌نشیند،
    /// تا تطبیقِ دفتر کل و صورت‌حساب مالک به دالر هم برقرار بماند.
    /// </summary>
    private IEnumerable<LedgerPostingRequest> BuildFinanceRows(CustodyFinanceEntry entry, string reference, CustodyRate ownerRate)
    {
        var sourceType = FinanceSourceType(entry.Kind);
        var documentRate = new CustodyRate(entry.Currency, entry.CurrencyPerUsd ?? 1m, entry.EffectiveRateToUsd, entry.EntryDate.Date, entry.RateSource ?? "");
        var document = Source(entry.EffectiveAmount, documentRate);
        var ownerAmount = entry.EffectiveOwnerAmount;
        var ownerUsd = ownerRate.ToUsdAmount(ownerAmount);
        switch (entry.Kind)
        {
            case CustodyFinanceKind.Collection:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, ownerUsd, reference + OwnerSuffix,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Receipt,
                    $"پول فروش امانی {reference} نزد شرکت به حساب مالک", Source(ownerAmount, ownerRate));
                break;
            case CustodyFinanceKind.DirectToOwner:
                // پول به مالک رسید؛ شرکت چیزی به او بدهکار نشد. فقط کمیشن (پایین) اثر دارد.
                break;
            case CustodyFinanceKind.OwnerPayout:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow, "پرداخت به مالک امانی", document);
                break;
            case CustodyFinanceKind.ReimbursableExpense:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow,
                    $"مصرف به حساب مالک{(entry.PayeeName is null ? "" : " — " + entry.PayeeName)}", document);
                break;
            case CustodyFinanceKind.ServiceFee:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow, "حق خدمات شرکت (امانی)", document);
                break;
            case CustodyFinanceKind.ProviderCharge:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow,
                    $"مصرف نسیه به حساب مالک{(entry.PayeeName is null ? "" : " — " + entry.PayeeName)}", document);
                // بدهیِ واقعیِ شرکت به خدمات‌دهنده — همان سمتِ مصرفِ پرداخت‌نشدهٔ عادی (بستانکار).
                yield return ProviderRow(sourceType, entry, reference + ProviderSuffix, LedgerSide.Credit,
                    $"مصرف نسیه برای مالک امانی {reference}", document);
                break;
            case CustodyFinanceKind.ProviderPayment:
                yield return ProviderRow(sourceType, entry, reference + ProviderSuffix, LedgerSide.Debit,
                    $"پرداخت مصرف نسیهٔ امانی AF-{entry.SettlesFinanceEntryId:D5}", document);
                break;
            case CustodyFinanceKind.CustomerRefund:
                // پولِ مالک که نزد شرکت بود به مشتری برگشت؛ تعهد شرکت به مالک کم می‌شود.
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, ownerUsd, reference + OwnerSuffix + "-R" + entry.Id,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow,
                    $"استرداد فروش امانی {reference} به مشتری از پول مالک", Source(ownerAmount, ownerRate));
                break;
            case CustodyFinanceKind.OwnerDirectRefund:
                // مالک خودش به مشتری پس داد؛ شرکت چیزی به مالک بدهکار نبود.
                break;
        }

        if (entry.CommissionUsd > 0m)
        {
            yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.CommissionUsd, reference + CommissionSuffix,
                entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow,
                $"کمیشن فروش امانی {reference}", Source(entry.EffectiveOwnerCommission, ownerRate));
        }
    }

    private static LedgerPostingRequest ProviderRow(string sourceType, CustodyFinanceEntry entry, string reference, LedgerSide side, string description,
        LedgerSource source)
        => new()
        {
            SourceType = sourceType,
            SourceId = entry.Id,
            EntryDate = entry.EntryDate.Date,
            Side = side,
            AmountUsd = entry.AmountUsd,
            SourceAmount = source.Amount,
            SourceCurrencyCode = source.Currency,
            AppliedFxRateToUsd = source.ToUsd,
            AppliedCurrencyPerUsdRate = source.PerUsd,
            AppliedFxRateDate = entry.EntryDate.Date,
            Description = description,
            Reference = reference,
            ServiceProviderId = entry.ServiceProviderId
        };

    /// <summary>
    /// هر سطرِ یک سند مرجعِ یکتای خودش را دارد (مشتری، «-O» مالک، «-C» کمیشن)، چون گاردِ «دو بار
    /// برگشت» در <see cref="ILedgerPostingService.ReverseAsync"/> روی همان مرجع کار می‌کند.
    /// </summary>
    private async Task ReverseLedgerRowsAsync(string sourceType, int sourceId, string description, string reference, CancellationToken ct)
    {
        var suffix = CompanyFlow.CompanyFlowSourceTypes.ReversalReferenceSuffix;
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.SourceType == sourceType && l.SourceId == sourceId
                && (l.Reference == null || !l.Reference.EndsWith(suffix)))
            .OrderBy(l => l.Id)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var reversal = await ledger.ReverseAsync(row, DateTime.UtcNow, description, row.Reference ?? reference, ct);
            if (reversal is null)
                throw Rule("CUSTODY_LEDGER_ALREADY_REVERSED", "سطر دفتر این سند قبلاً برگشت خورده است.");
        }
    }

    // ---------------------------------------------------------------- payments

    private async Task<PaymentTransaction> NewPaymentAsync(CustodyFinanceEntry entry, string reference, bool reversal, CancellationToken ct)
    {
        var companyId = await db.CashAccounts.AsNoTracking()
            .Where(a => a.Id == entry.CashAccountId)
            .Select(a => a.CompanyId)
            .FirstOrDefaultAsync(ct) ?? await systemCompany.GetOwnerCompanyIdAsync(ct);
        var inbound = entry.Kind == CustodyFinanceKind.Collection;
        if (reversal) inbound = !inbound;
        var label = entry.Kind switch
        {
            CustodyFinanceKind.Collection => "دریافت پول فروش امانی",
            CustodyFinanceKind.OwnerPayout => "پرداخت به مالک امانی",
            CustodyFinanceKind.ProviderPayment => "پرداخت مصرف نسیهٔ امانی به خدمات‌دهنده",
            CustodyFinanceKind.CustomerRefund => "استرداد فروش امانی به مشتری",
            _ => "مصرف به حساب مالک امانی"
        };
        var payment = new PaymentTransaction
        {
            PaymentDate = reversal ? DateTime.UtcNow.Date : entry.EntryDate.Date,
            Direction = inbound ? PaymentDirection.In : PaymentDirection.Out,
            // نوعِ Manual عمداً: Adapter پرداخت آن را ژورنال نمی‌کند و تطبیقِ دریافتِ مشتری آن را
            // به فروش‌های عادی نمی‌چسباند؛ سطر صندوقِ ژورنال فقط از Adapter امانی می‌آید.
            PaymentKind = inbound ? PaymentKind.ManualReceipt : PaymentKind.ManualPayment,
            CompanyId = companyId,
            CashAccountId = entry.CashAccountId,
            FundingSource = PaymentFundingSource.Company,
            Amount = entry.EffectiveAmount,
            Currency = entry.Currency,
            AppliedFxRateToUsd = entry.EffectiveRateToUsd,
            AmountUsd = entry.AmountUsd,
            Reference = reversal ? reference + "-REV" : reference,
            Description = reversal ? $"لغو {label} {reference}" : $"{label} {reference}",
            CreatedByUserId = currentUser?.UserId
        };
        db.PaymentTransactions.Add(payment);
        return payment;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>نرخِ ارز فروش که در خودِ سند فروش ثبت شده (طلب مشتری و «در انتظار وصول» با همین ارزش می‌مانند).</summary>
    private static CustodyRate SaleRate(CustodySale sale)
        => SystemCurrency.IsBaseCurrency(sale.Currency) ? CustodyRate.Usd
            : new CustodyRate(sale.Currency, sale.CurrencyPerUsd ?? FxRateMath.PerUsdFromToUsd(sale.EffectiveRateToUsd), sale.EffectiveRateToUsd, sale.SaleDate.Date, sale.RateSource ?? "");

    /// <summary>نرخِ ارز مالک که هنگام فروش به کار رفت (نرخ قفل‌شده وقتی ارزها فرق دارند).</summary>
    private static CustodyRate SaleOwnerRate(CustodySale sale)
    {
        var ownerCurrency = sale.EffectiveOwnerCurrency;
        if (SystemCurrency.IsBaseCurrency(ownerCurrency)) return CustodyRate.Usd;
        if (!sale.IsCrossCurrency) return SaleRate(sale);
        return new CustodyRate(ownerCurrency, sale.OwnerAgreedPerUsd ?? FxRateMath.PerUsdFromToUsd(sale.EffectiveOwnerRateToUsd),
            sale.EffectiveOwnerRateToUsd, sale.SaleDate.Date, sale.OwnerRateSource ?? "");
    }

    private sealed record LedgerSource(decimal Amount, string Currency, decimal ToUsd, decimal PerUsd);

    private static LedgerSource Source(decimal amount, CustodyRate rate) => new(amount, rate.Currency, rate.ToUsd, rate.PerUsd);

    private static string Amt(decimal value, string currency)
        => value.ToString("#,0.00") + (SystemCurrency.IsBaseCurrency(currency) ? " دالر" : " " + (currency == CustodyFx.Afn ? "افغانی" : currency));

    private async Task EnsureAccountingCompleteAsync(CustodyOwner owner, CancellationToken ct)
    {
        if (accounting.IsEnabled && await CustodyAccountingReconciliationService.HasMissingJournalsAsync(db, owner, ct))
            throw Rule("CUSTODY_RECONCILIATION_REQUIRED", "اسناد قبلی این مالک با دفتر کل تطبیق نشده‌اند؛ نخست پیش‌نمایش تطبیق امانی را بررسی و تأیید کنید.");
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return await work();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        await transaction.CommitAsync(ct);
        return result;
    }

    private bool IsPostgres
        => db.Database.IsRelational()
            && string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    /// <summary>همهٔ اسنادی که ماندهٔ یک مالک را تغییر می‌دهند پشتِ هم اجرا می‌شوند.</summary>
    private async Task LockOwnerAsync(CustodyOwner owner, CancellationToken ct)
    {
        if (!IsPostgres) return;
        var key1 = 9275 * 10 + (int)owner.PartyType;
        var key2 = owner.PartyId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key1}, {key2})", ct);
    }

    private async Task LockFinanceEntryAsync(int entryId, CancellationToken ct)
    {
        if (!IsPostgres) return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT ""Id"" FROM ""CustodyFinanceEntries"" WHERE ""Id"" = {entryId} FOR UPDATE", ct);
    }

    private async Task LockSaleAsync(int saleId, CancellationToken ct)
    {
        if (!IsPostgres) return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT ""Id"" FROM ""CustodySales"" WHERE ""Id"" = {saleId} FOR UPDATE", ct);
    }

    public static decimal CalculateCommission(CustodyCommissionMethod method, decimal rate, decimal quantityMt, decimal amountUsd)
    {
        if (rate < 0m)
            throw Rule("CUSTODY_COMMISSION_RATE", "نرخ کمیشن منفی نمی‌شود.");
        var commission = method switch
        {
            CustodyCommissionMethod.None => 0m,
            CustodyCommissionMethod.PercentOfSale => rate > 100m
                ? throw Rule("CUSTODY_COMMISSION_RATE", "فیصدی کمیشن بیشتر از ۱۰۰ نمی‌شود.")
                : Money(amountUsd * rate / 100m),
            CustodyCommissionMethod.PerMt => Money(quantityMt * rate),
            CustodyCommissionMethod.FixedPerSale => Money(rate),
            _ => throw Rule("CUSTODY_COMMISSION_METHOD", "روش کمیشن معتبر نیست.")
        };
        if (commission > amountUsd)
            throw Rule("CUSTODY_COMMISSION_EXCEEDS", "کمیشن بیشتر از مبلغ فروش شده است.");
        return commission;
    }

    /// <summary>
    /// دفتر کل خاموش (Accounting:Enabled=false) = رفتارِ همهٔ ماژول‌ها: فقط دفتر عملیاتی. اما وقتی
    /// دفتر کل روشن است و ژورنالِ امانی ثبت نمی‌شود (pilot خاموش، حساب‌های امانی تعیین‌نشده، ...)،
    /// سند نصفه ثبت نمی‌شود: صندوق و صورت‌حساب بدون ژورنال، دفتر کل را از صندوق جدا می‌کرد.
    /// </summary>
    private static void RequireJournal(CustodyAccountingResult result)
    {
        if (result.Status != CustodyPostingStatus.Skipped || result.Reason == CustodyAccountingAdapter.AccountingDisabledReason)
            return;
        throw Rule("CUSTODY_GL_NOT_READY", result.Reason switch
        {
            "PILOT_DISABLED" => "ثبت دفتر کلِ امانی فعال نیست (Accounting:Pilots:Custody)؛ سند ثبت نشد.",
            "CUSTODY_ACCOUNTS_NOT_CONFIGURED" => "حساب‌های امانی در «نقشه حساب‌ها» تعیین نشده‌اند؛ سند ثبت نشد.",
            CustodyAccountingAdapter.ProviderPayableNotConfiguredReason => "حساب پرداختنیِ نوعِ این مصرف تعیین نشده است؛ سند ثبت نشد.",
            CustodyAccountingAdapter.CustodyAccountsMisclassifiedReason => "طبقه‌بندی حساب‌های امانی درست نیست: ۱۲۱۰ باید دارایی با سمت بستانکار (کاهندهٔ دریافتنی) و ۲۶۰۰ بدهی با سمت بستانکار و هر دو فعال باشند؛ سند ثبت نشد.",
            CustodyAccountingAdapter.ForeignCurrencyNonMonetaryReason => "حساب‌های ۱۲۱۰ و ۲۶۰۰ «پولی» تعیین نشده‌اند؛ سند افغانی بدون تسعیرِ درست ثبت نمی‌شود.",
            _ => $"دفتر کلِ امانی آماده نیست ({result.Reason})؛ سند ثبت نشد."
        });
    }

    private static void RequirePositive(decimal value, string label, int decimals)
    {
        if (value <= 0m)
            throw Rule("CUSTODY_AMOUNT", $"{label} باید بیشتر از صفر باشد.");
        if (decimal.Round(value, decimals) != value)
            throw Rule("CUSTODY_PRECISION", $"{label} حداکثر {decimals} رقم اعشار دارد.");
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw Rule("CUSTODY_REASON", "دلیل لغو را بنویسید.");
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static string Usd(decimal value) => value.ToString("#,0.00") + " دالر";
    private static string Qty(decimal value) => value.ToString("#,0.####");
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Describe(object value) => JsonSerializer.Serialize(value);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static BusinessRuleException Rule(string code, string message) => new(code, message);
}
