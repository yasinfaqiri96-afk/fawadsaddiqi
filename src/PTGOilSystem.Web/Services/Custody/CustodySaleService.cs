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

public sealed class CustodySaleInput
{
    public int LotId { get; init; }
    public required CustodyLocation Location { get; init; }
    public int CustomerId { get; init; }
    public DateTime SaleDate { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public string Currency { get; init; } = "USD";
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
    public decimal AmountUsd { get; init; }
    public string Currency { get; init; } = "USD";
    public int? CashAccountId { get; init; }
    public int? ExpenseTypeId { get; init; }
    public string? PayeeName { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Description { get; init; }
    public string? FormToken { get; init; }
}

public interface ICustodySaleService
{
    Task<int> CreateSaleAsync(CustodySaleInput input, CancellationToken ct = default);
    Task<int> RecordFinanceAsync(CustodyFinanceInput input, CancellationToken ct = default);
    Task CancelSaleAsync(int saleId, string reason, CancellationToken ct = default);
    Task CancelFinanceAsync(int entryId, string reason, CancellationToken ct = default);
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

    private const string SaleEntity = "CustodySale";
    private const string OwnerSuffix = "-O";
    private const string CommissionSuffix = "-C";
    private const string FinanceEntity = "CustodyFinanceEntry";

    public Task<int> CreateSaleAsync(CustodySaleInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequireUsd(input.Currency);
            RequirePositive(input.QuantityMt, "مقدار فروش", 4);
            RequirePositive(input.UnitPriceUsd, "قیمت فی تُن", 4);
            if (!Enum.IsDefined(input.Settlement) || !Enum.IsDefined(input.CommissionRecognition))
                throw Rule("CUSTODY_SALE_TERMS", "شرایط فروش و زمان کمیشن معتبر نیست.");

            var lot = await db.CustodyLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == input.LotId, ct)
                ?? throw Rule("CUSTODY_LOT_NOT_FOUND", "بار امانی پیدا نشد.");
            RequireFinancialOwner(lot.OwnerPartyType);
            var owner = new CustodyOwner(lot.OwnerPartyType, lot.OwnerPartyId);
            await LockOwnerAsync(owner, ct);
            await EnsureAccountingCompleteAsync(owner, ct);
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == input.CustomerId, ct)
                ?? throw Rule("CUSTODY_CUSTOMER", "مشتری را انتخاب کنید.");
            if (!customer.IsActive)
                throw Rule("CUSTODY_CUSTOMER_INACTIVE", "این مشتری غیرفعال است.");
            if (lot.OwnerPartyType == AccountingPartyType.Customer && lot.OwnerPartyId == customer.Id)
                throw Rule("CUSTODY_SELF_SALE", "مالک نمی‌تواند خریدارِ تیلِ خودش باشد؛ برای این کار «برگشت به مالک» را ثبت کنید.");

            var amountUsd = Money(input.QuantityMt * input.UnitPriceUsd);
            var commissionUsd = CalculateCommission(input.CommissionMethod, input.CommissionRate, input.QuantityMt, amountUsd);

            // خروج از موجودیِ همان مالک — با همان قفل، سقف موجودی، مسدودی و کنترل تاریخ.
            var stockKey = await custody.RecordSaleOutAsync(lot.Id, input.Location, input.SaleDate, input.QuantityMt, input.DocumentNumber, ct);

            var sale = new CustodySale
            {
                SaleNumber = "TMP-" + Guid.NewGuid().ToString("N"),
                CustodyLotId = lot.Id,
                OwnerPartyType = lot.OwnerPartyType,
                OwnerPartyId = lot.OwnerPartyId,
                CustomerId = customer.Id,
                ProductId = lot.ProductId,
                TerminalId = input.Location.TerminalId,
                StorageTankId = input.Location.StorageTankId,
                SaleDate = ToUtc(input.SaleDate),
                QuantityMt = input.QuantityMt,
                UnitPriceUsd = input.UnitPriceUsd,
                Currency = "USD",
                AmountUsd = amountUsd,
                CommissionMethod = input.CommissionMethod,
                CommissionRate = input.CommissionMethod == CustodyCommissionMethod.None ? 0m : input.CommissionRate,
                CommissionUsd = commissionUsd,
                CommissionRecognition = input.CommissionRecognition,
                CommissionRecognizedAtSaleUsd = input.CommissionRecognition == CustodyCommissionRecognition.OnSale ? commissionUsd : 0m,
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
                    $"کمیشن فروش امانی {sale.SaleNumber} به {customerName}"));
            }

            await db.SaveChangesAsync(ct);
            RequireJournal(await accounting.TryPostSaleAsync(sale, ct));
            await audit.LogAsync(SaleEntity, sale.Id, AuditAction.Insert, diff: Describe(new
            {
                sale.SaleNumber, Lot = lot.LotNumber, sale.CustomerId, sale.QuantityMt, sale.UnitPriceUsd, sale.AmountUsd,
                CommissionMethod = sale.CommissionMethod.ToString(), sale.CommissionRate, sale.CommissionUsd,
                Recognition = sale.CommissionRecognition.ToString(), Settlement = input.Settlement.ToString()
            }), ct: ct);
            await db.SaveChangesAsync(ct);

            if (input.Settlement != CustodySaleSettlement.Credit)
            {
                await RecordFinanceCoreAsync(new CustodyFinanceInput
                {
                    Kind = input.Settlement == CustodySaleSettlement.CashToCompany ? CustodyFinanceKind.Collection : CustodyFinanceKind.DirectToOwner,
                    SaleId = sale.Id,
                    EntryDate = input.SaleDate,
                    AmountUsd = sale.AmountUsd,
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
        RequireUsd(input.Currency);
        RequirePositive(input.AmountUsd, "مبلغ", 2);
        if (!Enum.IsDefined(input.Kind))
            throw Rule("CUSTODY_FINANCE_KIND", "نوع سند مالی معتبر نیست.");

        CustodySale? sale = null;
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
        RequireFinancialOwner(owner.PartyType);

        // ترتیبِ قفل: اول حسابِ مالک، بعد فروش — همهٔ مسیرها همین ترتیب را دارند.
        await LockOwnerAsync(owner, ct);
        if (sale is not null)
        {
            await LockSaleAsync(sale.Id, ct);
            await db.Entry(sale).ReloadAsync(ct);
            if (sale.Status != CustodyDocumentStatus.Posted)
                throw Rule("CUSTODY_SALE_CANCELLED", "این فروش لغو شده است.");
        }
        await EnsureAccountingCompleteAsync(owner, ct);

        var amount = input.AmountUsd;
        var commission = 0m;
        if (input.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.DirectToOwner)
        {
            var collected = await CollectedAsync(sale!.Id, excludeEntryId: null, ct);
            var outstanding = sale.AmountUsd - collected;
            if (amount > outstanding)
                throw Rule("CUSTODY_OVER_COLLECTION", $"باقی‌ماندهٔ این فروش {Usd(outstanding)} است.");
            if (sale.CommissionRecognition == CustodyCommissionRecognition.OnCollection && sale.CommissionUsd > 0m)
            {
                var recognized = await CommissionOnCollectionsAsync(sale.Id, ct);
                var remaining = sale.CommissionUsd - recognized;
                // آخرین وصول باقیماندهٔ کمیشن را می‌گیرد تا گردکردن، کمیشن را کم یا زیاد نکند.
                commission = amount == outstanding
                    ? remaining
                    : Math.Min(remaining, Money(sale.CommissionUsd * amount / sale.AmountUsd));
            }
        }

        if (input.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.OwnerPayout or CustodyFinanceKind.ReimbursableExpense)
        {
            if (!input.CashAccountId.HasValue)
                throw Rule("CUSTODY_CASH_ACCOUNT", "صندوق یا بانک را انتخاب کنید.");
            var cash = await db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == input.CashAccountId.Value, ct)
                ?? throw Rule("CUSTODY_CASH_ACCOUNT", "صندوق یا بانک پیدا نشد.");
            if (!cash.IsActive)
                throw Rule("CUSTODY_CASH_INACTIVE", "این صندوق/بانک غیرفعال است.");
            if (cash.AccountType != CashAccountType.Mixed && !string.Equals(cash.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                throw Rule("CUSTODY_CASH_CURRENCY", "این صندوق/بانک دالری نیست.");
        }

        if (input.Kind == CustodyFinanceKind.OwnerPayout)
        {
            var payable = await OwnerFundsBalanceAsync(owner, ct);
            if (amount > payable)
                throw Rule("CUSTODY_OVER_PAYOUT", payable > 0m
                    ? $"ماندهٔ قابل پرداخت به این مالک {Usd(payable)} است."
                    : "فعلاً پولی از این مالک نزد شرکت نیست.");
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
            AmountUsd = amount,
            Currency = "USD",
            CommissionUsd = commission,
            CashAccountId = input.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.OwnerPayout or CustodyFinanceKind.ReimbursableExpense
                ? input.CashAccountId
                : null,
            ExpenseTypeId = input.Kind is CustodyFinanceKind.ReimbursableExpense or CustodyFinanceKind.ServiceFee ? input.ExpenseTypeId : null,
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

        foreach (var row in BuildFinanceRows(entry, reference))
            ledger.Post(row);
        await db.SaveChangesAsync(ct);

        // سند تاریخی فقط در تطبیقِ صریحِ دارای پیش‌نمایش ثبت می‌شود؛ دریافت امروز
        // مجوز ساخت خودکار ژورنالِ فروش یا دریافت‌های تاریخی نیست.
        RequireJournal(await accounting.TryPostFinanceAsync(entry, sale, ct));
        await audit.LogAsync(FinanceEntity, entry.Id, AuditAction.Insert, diff: Describe(new
        {
            Kind = entry.Kind.ToString(), Sale = sale?.SaleNumber, entry.OwnerPartyType, entry.OwnerPartyId,
            entry.AmountUsd, entry.CommissionUsd, entry.CashAccountId, entry.PaymentTransactionId
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

            // لغوِ وصولِ نقدی پولِ مالک نزد شرکت را کم می‌کند؛ اگر قبلاً همان پول به مالک پرداخت
            // شده باشد، مانده منفی می‌شود — اول باید پرداخت به مالک اصلاح شود. ماندهٔ منفی بدونِ
            // پرداخت (کمیشن یا مصرفِ اسناد دیگر) فقط طلبِ شرکت از مالک است و لغو را نمی‌بندد.
            if (entry.Kind == CustodyFinanceKind.Collection)
            {
                var after = await OwnerFundsBalanceAsync(owner, ct) - (entry.AmountUsd - entry.CommissionUsd);
                var paidOut = after < 0m && await db.CustodyFinanceEntries.AnyAsync(e =>
                    e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId
                    && e.Kind == CustodyFinanceKind.OwnerPayout && e.Status == CustodyDocumentStatus.Posted, ct);
                if (paidOut)
                    throw Rule("CUSTODY_CANCEL_OVERPAID", "این پول قبلاً به مالک پرداخت شده است؛ اول «پرداخت به مالک» مربوط را لغو کنید.");
            }

            var reference = entry.CustodySaleId.HasValue
                ? await db.CustodySales.Where(s => s.Id == entry.CustodySaleId).Select(s => s.SaleNumber).FirstAsync(ct)
                : $"AF-{entry.Id:D5}";
            if (entry.PaymentTransactionId.HasValue)
            {
                var reversal = await NewPaymentAsync(entry, reference, reversal: true, ct);
                await db.SaveChangesAsync(ct);
                entry.ReversalPaymentTransactionId = reversal.Id;
            }

            await ReverseLedgerRowsAsync(FinanceSourceType(entry.Kind), entry.Id, $"لغو: {reason.Trim()}", reference, ct);
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
            entry.CancellationReason = reason.Trim();
            await audit.LogAsync(FinanceEntity, entry.Id, AuditAction.Reverse, diff: Describe(new { Kind = entry.Kind.ToString(), Reason = reason.Trim() }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    // ---------------------------------------------------------------- balances (همان تعریفِ صفحهٔ حساب مالک)

    /// <summary>
    /// پولِ مالک نزد شرکت = وصولِ نقدی − همهٔ کمیشن‌های درآمدشده − مصارف به حساب مالک − حق خدمات − پرداخت‌ها.
    /// منفی یعنی مالک به شرکت بدهکار است.
    /// </summary>
    public static async Task<decimal> OwnerFundsBalanceAsync(ApplicationDbContext db, CustodyOwner owner, CancellationToken ct)
    {
        var entries = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId && e.Status == CustodyDocumentStatus.Posted)
            .GroupBy(e => e.Kind)
            .Select(g => new { Kind = g.Key, Amount = g.Sum(e => e.AmountUsd), Commission = g.Sum(e => e.CommissionUsd) })
            .ToListAsync(ct);
        var saleCommission = await db.CustodySales.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId && s.Status == CustodyDocumentStatus.Posted)
            .SumAsync(s => s.CommissionRecognizedAtSaleUsd, ct);

        decimal Amount(CustodyFinanceKind kind) => entries.Where(e => e.Kind == kind).Sum(e => e.Amount);
        return Amount(CustodyFinanceKind.Collection)
            - entries.Sum(e => e.Commission)
            - saleCommission
            - Amount(CustodyFinanceKind.ReimbursableExpense)
            - Amount(CustodyFinanceKind.ServiceFee)
            - Amount(CustodyFinanceKind.OwnerPayout);
    }

    private Task<decimal> OwnerFundsBalanceAsync(CustodyOwner owner, CancellationToken ct) => OwnerFundsBalanceAsync(db, owner, ct);

    private Task<decimal> CollectedAsync(int saleId, int? excludeEntryId, CancellationToken ct)
        => db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.CustodySaleId == saleId && e.Status == CustodyDocumentStatus.Posted
                && (e.Kind == CustodyFinanceKind.Collection || e.Kind == CustodyFinanceKind.DirectToOwner)
                && (excludeEntryId == null || e.Id != excludeEntryId))
            .SumAsync(e => e.AmountUsd, ct);

    private Task<decimal> CommissionOnCollectionsAsync(int saleId, CancellationToken ct)
        => db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.CustodySaleId == saleId && e.Status == CustodyDocumentStatus.Posted)
            .SumAsync(e => e.CommissionUsd, ct);

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
        string description)
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
            SourceAmount = amountUsd,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedCurrencyPerUsdRate = 1m,
            AppliedFxRateDate = date.Date,
            Description = description,
            Reference = reference,
            CustomerId = partyType == AccountingPartyType.Customer ? partyId : null,
            SupplierId = partyType == AccountingPartyType.Supplier ? partyId : null
        };
    }

    private IEnumerable<LedgerPostingRequest> BuildFinanceRows(CustodyFinanceEntry entry, string reference)
    {
        var sourceType = FinanceSourceType(entry.Kind);
        switch (entry.Kind)
        {
            case CustodyFinanceKind.Collection:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference + OwnerSuffix,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Receipt,
                    $"پول فروش امانی {reference} نزد شرکت به حساب مالک");
                break;
            case CustodyFinanceKind.DirectToOwner:
                // پول به مالک رسید؛ شرکت چیزی به او بدهکار نشد. فقط کمیشن (پایین) اثر دارد.
                break;
            case CustodyFinanceKind.OwnerPayout:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow, "پرداخت به مالک امانی");
                break;
            case CustodyFinanceKind.ReimbursableExpense:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow,
                    $"مصرف به حساب مالک{(entry.PayeeName is null ? "" : " — " + entry.PayeeName)}");
                break;
            case CustodyFinanceKind.ServiceFee:
                yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.AmountUsd, reference,
                    entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow, "حق خدمات شرکت (امانی)");
                break;
        }

        if (entry.CommissionUsd > 0m)
        {
            yield return PartyRow(sourceType, entry.Id, entry.EntryDate, entry.CommissionUsd, reference + CommissionSuffix,
                entry.OwnerPartyType, entry.OwnerPartyId, CompanyFlowEffect.Outflow,
                $"کمیشن فروش امانی {reference}");
        }
    }

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
            Amount = entry.AmountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = entry.AmountUsd,
            Reference = reversal ? reference + "-REV" : reference,
            Description = reversal ? $"لغو {label} {reference}" : $"{label} {reference}",
            CreatedByUserId = currentUser?.UserId
        };
        db.PaymentTransactions.Add(payment);
        return payment;
    }

    // ---------------------------------------------------------------- helpers

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
            _ => $"دفتر کلِ امانی آماده نیست ({result.Reason})؛ سند ثبت نشد."
        });
    }

    private static void RequireFinancialOwner(AccountingPartyType ownerType)
    {
        if (!FinancialOwnerTypes.Contains(ownerType))
            throw Rule("CUSTODY_OWNER_FINANCE_UNSUPPORTED",
                "فروش و حساب مالی امانی فعلاً فقط برای مالکی است که مشتری یا فروشنده ثبت شده است.");
    }

    private static void RequireUsd(string? currency)
    {
        if (!string.Equals(currency?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            throw Rule("CUSTODY_CURRENCY", "سند امانی فعلاً فقط به دالر ثبت می‌شود؛ قاعدهٔ تفاوت نرخ برای امانی هنوز تعیین نشده است.");
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
