using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.CommodityLoans;

public sealed class CommodityLoanInput
{
    public CommodityLoanDirection Direction { get; init; }
    public AccountingPartyType CounterpartyType { get; init; }
    public int CounterpartyId { get; init; }
    public int ProductId { get; init; }
    public string? QualitySpec { get; init; }
    public decimal QuantityMt { get; init; }
    public DateTime LoanDate { get; init; }
    public DateTime DueDate { get; init; }
    public string? ReturnObligation { get; init; }
    public CommodityLoanBorrowBasis? BorrowBasis { get; init; }
    public int TerminalId { get; init; }
    public int? StorageTankId { get; init; }
    public CommodityLoanValuation? ValuationPolicy { get; init; }
    public decimal? AgreedUnitValueUsd { get; init; }
    public string? ValuationReference { get; init; }
    public int? LoanAccountId { get; init; }
    public int? DifferenceAccountId { get; init; }
    public decimal? QuantityTolerancePercent { get; init; }
    public bool CashSettlementAllowed { get; init; }
    public decimal? CashSettlementUnitPriceUsd { get; init; }
    public string? CashSettlementReference { get; init; }
    public string? AgreementReference { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CommodityLoanSettlementInput
{
    public int LoanId { get; init; }
    public CommodityLoanSettlementKind Kind { get; init; }
    public DateTime SettlementDate { get; init; }
    public decimal QuantityMt { get; init; }
    public int? TerminalId { get; init; }
    public int? StorageTankId { get; init; }
    public int? CashAccountId { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public interface ICommodityLoanService
{
    Task<int> CreateAsync(CommodityLoanInput input, CancellationToken ct = default);
    Task<int> SettleAsync(CommodityLoanSettlementInput input, CancellationToken ct = default);
    Task CancelSettlementAsync(int settlementId, string reason, CancellationToken ct = default);
    Task CancelLoanAsync(int loanId, string reason, CancellationToken ct = default);
}

/// <summary>
/// قرض تیل. هر سند در یک تراکنش: حرکتِ موجودیِ ملکی از همان <see cref="IInventoryMovementWriter"/>
/// (با نگهبان موجودی و ظرفیت)، ارزش از همان Pool اوسط (<see cref="IInventoryValuationService"/>)
/// و ژورنال روی همان <see cref="IAccountingPostingService"/>. وقتی دفتر کل روشن است و سیاست تعیین قیمت،
/// حساب‌های قرارداد یا ارزشِ معتبر موجودی کامل نیست، سند ثبت نمی‌شود — نه موجودی و نه ژورنال.
/// </summary>
public sealed class CommodityLoanService(
    ApplicationDbContext db,
    IInventoryMovementWriter movements,
    IInventoryValuationService valuation,
    IAccountingPostingService posting,
    ISystemCompanyProvider systemCompany,
    IOptions<AccountingOptions> options,
    IAuditService audit,
    IFormTokenGuard formTokens,
    ICurrentUserContext? currentUser = null) : ICommodityLoanService
{
    public const string SourceModule = "CommodityLoan";
    public const string LoanReferencePrefix = "COMMODITY-LOAN:";
    public const string SettlementReferencePrefix = "COMMODITY-LOAN-SETTLEMENT:";
    private const string LoanEntity = nameof(CommodityLoan);
    private const string SettlementEntity = nameof(CommodityLoanSettlement);
    private readonly AccountingOptions _options = options.Value;

    public static readonly AccountingPartyType[] CounterpartyTypes =
        [AccountingPartyType.Customer, AccountingPartyType.Supplier, AccountingPartyType.Company, AccountingPartyType.Partner];

    public static string LoanEventId(int loanId) => $"CommodityLoan:Loan:{loanId}:Created";
    public static string SettlementEventId(int settlementId) => $"CommodityLoan:Settlement:{settlementId}:Created";
    public static string Reversed(string eventId) => eventId.Replace(":Created", ":Reversed", StringComparison.Ordinal);

    private bool GlEnabled => _options.Enabled;

    // ---------------------------------------------------------------- ثبت قرض

    public Task<int> CreateAsync(CommodityLoanInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            if (!Enum.IsDefined(input.Direction))
                throw Rule("LOAN_DIRECTION", "نوع قرض (دادن یا گرفتن) را انتخاب کنید.");
            RequirePositive(input.QuantityMt, "مقدار قرض");
            var loanDate = ToUtc(input.LoanDate);
            var dueDate = ToUtc(input.DueDate);
            if (dueDate.Date < loanDate.Date)
                throw Rule("LOAN_DUE_DATE", "وقت برگشت پیش از تاریخ قرض نمی‌شود.");
            var quality = Required(input.QualitySpec, "کیفیت/مشخصات جنس", 300);
            var obligation = Required(input.ReturnObligation, "تعهد برگشت", 500);
            var agreement = Required(input.AgreementReference, "شماره سند قرض", 300);
            await RequireCounterpartyAsync(input.CounterpartyType, input.CounterpartyId, ct);
            if (!await db.Products.AnyAsync(p => p.Id == input.ProductId, ct))
                throw Rule("LOAN_PRODUCT", "جنس را انتخاب کنید.");
            await RequireLocationAsync(input.TerminalId, input.StorageTankId, ct);

            var borrow = input.Direction == CommodityLoanDirection.Borrow;
            if (borrow && (input.BorrowBasis is null || !Enum.IsDefined(input.BorrowBasis.Value)))
                throw Rule("LOAN_BORROW_BASIS", "مبنای قرض گرفتن تیل (اجازه مصرف تیل یا مالکیت طبق قرارداد) را تعیین کنید.");
            if (input.ValuationPolicy is { } policy)
            {
                if (!Enum.IsDefined(policy))
                    throw Rule("LOAN_VALUATION", "روش تعیین قیمت معتبر نیست.");
                if (!borrow && policy != CommodityLoanValuation.BookCost)
                    throw Rule("LOAN_VALUATION", "قرض دادن تیل از موجودی ملکی فقط به قیمت ثبت‌شده موجودی تعیین قیمت می‌شود.");
                if (borrow && policy != CommodityLoanValuation.AgreedValue)
                    throw Rule("LOAN_VALUATION", "قرض گرفتن تیل فقط با ارزش توافقیِ قرارداد (با سند) شناسایی می‌شود.");
            }
            if (borrow && input.ValuationPolicy == CommodityLoanValuation.AgreedValue)
            {
                if (input.AgreedUnitValueUsd is not > 0m)
                    throw Rule("LOAN_AGREED_VALUE", "ارزش توافقیِ هر تُن را وارد کنید.");
                Required(input.ValuationReference, "سند قیمت توافق‌شده", 300);
            }
            if (input.QuantityTolerancePercent is < 0m or > 10m)
                throw Rule("LOAN_TOLERANCE", "فرق مجاز مقدار مقدار باید بین ۰ و ۱۰ فیصد باشد.");
            if (input.CashSettlementAllowed)
            {
                if (input.CashSettlementUnitPriceUsd is not > 0m)
                    throw Rule("LOAN_CASH_TERMS", "برای پرداخت پول به‌جای تیل، قیمتِ توافقیِ هر تُن را وارد کنید.");
                Required(input.CashSettlementReference, "سند توافق پرداخت پول به‌جای تیل", 300);
            }
            var companyId = await systemCompany.GetOwnerCompanyIdAsync(ct);
            await RequireAccountAsync(input.LoanAccountId, companyId, borrow ? AccountType.Liability : AccountType.Asset,
                borrow ? "حساب قرض گرفته‌شده باید از نوع بدهی باشد." : "حساب قرض داده‌شده باید از نوع دارایی باشد.", ct);
            await RequireAccountAsync(input.DifferenceAccountId, companyId, null, null, ct);

            AccountingSettings? settings = null;
            if (GlEnabled)
            {
                settings = await RequireGlReadyAsync(companyId, ct);
                if (input.ValuationPolicy is null)
                    throw Rule("LOAN_VALUATION_REQUIRED", "دفتر کل فعال است؛ روش تعیین قیمتِ تأییدشده باید در قرارداد تعیین شود.");
                if (input.LoanAccountId is null)
                    throw Rule("LOAN_ACCOUNT_REQUIRED", borrow
                        ? "دفتر کل فعال است؛ حسابِ تیل قرضداریم را در قرارداد تعیین کنید."
                        : "دفتر کل فعال است؛ حسابِ تیل طلب داریم را در قرارداد تعیین کنید.");
            }

            var key = Guid.NewGuid();
            var request = new InventoryMovementRequest
            {
                ProductId = input.ProductId,
                TerminalId = input.TerminalId,
                StorageTankId = input.StorageTankId,
                MovementDate = loanDate,
                QuantityMt = input.QuantityMt,
                ReferenceDocument = LoanReferencePrefix + key.ToString("N"),
                Notes = borrow ? "قرض گرفتن تیلِ جنس" : "قرض دادن تیلِ جنس از موجودی ملکی"
            };
            // قرض دادن تیل از موجودیِ آزادِ همان محل؛ هیچ نقطه‌ای از خط زمانی منفی نمی‌شود.
            var movement = borrow
                ? await movements.PostInboundAsync(request, ct)
                : await movements.PostOutboundAsync(request, StockGuard.Full, ct);

            decimal? carrying = null;
            if (settings is not null)
            {
                if (borrow)
                {
                    carrying = Round4(input.QuantityMt * input.AgreedUnitValueUsd!.Value);
                    await valuation.ApplyReceiptAsync(companyId, input.ProductId, input.TerminalId, input.QuantityMt, carrying.Value, ct);
                }
                else
                {
                    var consumption = await valuation.TryConsumeAsync(companyId, input.ProductId, input.TerminalId, input.QuantityMt, ct);
                    if (!consumption.Succeeded || consumption.CostUsd <= 0m)
                        throw Rule("LOAN_NOT_VALUED", "موجودیِ این جنس در این ترمینال ارزش ثبت‌شدهِ کافی ندارد؛ قیمت ثبت‌شده حدس زده نمی‌شود و قرض ثبت نشد.");
                    carrying = consumption.CostUsd;
                }
            }

            var loan = new CommodityLoan
            {
                LoanNumber = "TMP-" + key.ToString("N"),
                Direction = input.Direction,
                CounterpartyType = input.CounterpartyType,
                CounterpartyId = input.CounterpartyId,
                ProductId = input.ProductId,
                QualitySpec = quality,
                QuantityMt = input.QuantityMt,
                LoanDate = loanDate,
                DueDate = dueDate,
                ReturnObligation = obligation,
                BorrowBasis = borrow ? input.BorrowBasis : null,
                TerminalId = input.TerminalId,
                StorageTankId = input.StorageTankId,
                InventoryMovementId = movement.Id,
                ValuationPolicy = input.ValuationPolicy,
                AgreedUnitValueUsd = borrow ? input.AgreedUnitValueUsd : null,
                ValuationReference = Clean(input.ValuationReference),
                CarryingValueUsd = carrying,
                LoanAccountId = input.LoanAccountId,
                DifferenceAccountId = input.DifferenceAccountId,
                QuantityTolerancePercent = input.QuantityTolerancePercent,
                CashSettlementAllowed = input.CashSettlementAllowed,
                CashSettlementUnitPriceUsd = input.CashSettlementAllowed ? input.CashSettlementUnitPriceUsd : null,
                CashSettlementReference = input.CashSettlementAllowed ? Clean(input.CashSettlementReference) : null,
                AgreementReference = agreement,
                Notes = Clean(input.Notes),
                CreatedByUserId = currentUser?.UserId
            };
            db.CommodityLoans.Add(loan);
            formTokens.Stamp(input.FormToken, "CommodityLoan.Create", LoanEntity);
            await db.SaveChangesAsync(ct);
            loan.LoanNumber = $"CL-{loan.Id:D5}";
            await db.SaveChangesAsync(ct);

            if (settings is not null)
            {
                var value = carrying!.Value;
                var lines = new List<AccountingPostLine>
                {
                    Line(loan.LoanAccountId!.Value, borrow ? 0m : value, borrow ? value : 0m, loan.CounterpartyType, loan.CounterpartyId, loan, $"Commodity loan {loan.LoanNumber}"),
                    Line(settings.InventoryAccountId, borrow ? value : 0m, borrow ? 0m : value, null, null, loan, $"Commodity loan {loan.LoanNumber} inventory", loan.StorageTankId)
                };
                await PostJournalAsync(companyId, $"CML-{companyId:D6}-L{loan.Id:D9}", loan.LoanDate, LoanEventId(loan.Id), LoanEntity, loan.Id,
                    $"Commodity loan {loan.Direction} {loan.LoanNumber}", lines, ct);
            }

            await audit.LogAsync(LoanEntity, loan.Id, AuditAction.Insert, diff: Describe(new
            {
                loan.LoanNumber, Direction = loan.Direction.ToString(), loan.CounterpartyType, loan.CounterpartyId, loan.ProductId, loan.QualitySpec,
                loan.QuantityMt, loan.LoanDate, loan.DueDate, BorrowBasis = loan.BorrowBasis?.ToString(), loan.TerminalId, loan.StorageTankId,
                Valuation = loan.ValuationPolicy?.ToString(), loan.AgreedUnitValueUsd, loan.CarryingValueUsd, loan.LoanAccountId, loan.DifferenceAccountId,
                loan.QuantityTolerancePercent, loan.CashSettlementAllowed, loan.CashSettlementUnitPriceUsd, loan.AgreementReference, MovementId = movement.Id
            }), ct: ct);
            await db.SaveChangesAsync(ct);
            return loan.Id;
        }, ct);

    // ---------------------------------------------------------------- برگشت قرض (برگشت قرض، نقدی، تفاوت مقدار)

    public Task<int> SettleAsync(CommodityLoanSettlementInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            if (!Enum.IsDefined(input.Kind))
                throw Rule("LOAN_SETTLEMENT_KIND", "نوع برگشت قرض معتبر نیست.");
            RequirePositive(input.QuantityMt, "مقدار برگشت قرض");
            var loan = await LockLoanAsync(input.LoanId, ct);
            if (loan.Status != CommodityLoanStatus.Active)
                throw Rule("LOAN_CANCELLED", "این قرض لغو شده است.");
            var date = ToUtc(input.SettlementDate);
            if (date.Date < loan.LoanDate.Date)
                throw Rule("LOAN_SETTLEMENT_DATE", "تاریخ برگشت قرض پیش از تاریخ قرض نمی‌شود.");

            var active = await db.CommodityLoanSettlements.AsNoTracking()
                .Where(s => s.CommodityLoanId == loan.Id && s.Status == CommodityLoanStatus.Active)
                .Select(s => new { s.QuantityMt, s.CarryingValueUsd })
                .ToListAsync(ct);
            var outstanding = loan.QuantityMt - active.Sum(s => s.QuantityMt);
            if (input.QuantityMt > outstanding)
                throw Rule("LOAN_OVER_SETTLEMENT", $"باقی قرض {Qty(outstanding)} تُن است؛ بیشتر از آن ثبت نمی‌شود.");
            decimal? carried = loan.CarryingValueUsd is { } carrying
                ? input.QuantityMt == outstanding
                    ? carrying - active.Sum(s => s.CarryingValueUsd ?? 0m)
                    : Round4(carrying * input.QuantityMt / loan.QuantityMt)
                : null;

            var borrow = loan.Direction == CommodityLoanDirection.Borrow;
            var companyId = await systemCompany.GetOwnerCompanyIdAsync(ct);
            AccountingSettings? settings = null;
            if (GlEnabled)
            {
                settings = await RequireGlReadyAsync(companyId, ct);
                if (carried is null || loan.LoanAccountId is null)
                    throw Rule("LOAN_NOT_VALUED", "این قرض ارزش ثبت‌شده ندارد (پیش از فعال‌شدن دفتر کل ثبت شده)؛ برگشت قرضٔ مالی بدون مبنای ارزش ثبت نمی‌شود.");
            }

            var key = Guid.NewGuid();
            var settlement = new CommodityLoanSettlement
            {
                CommodityLoanId = loan.Id,
                Kind = input.Kind,
                SettlementDate = date,
                QuantityMt = input.QuantityMt,
                CarryingValueUsd = carried,
                DocumentNumber = Clean(input.DocumentNumber),
                Notes = Clean(input.Notes),
                CreatedByUserId = currentUser?.UserId
            };
            var lines = new List<AccountingPostLine>();
            var label = loan.LoanNumber;

            switch (input.Kind)
            {
                case CommodityLoanSettlementKind.InKindReturn:
                {
                    if (input.TerminalId is not { } terminalId)
                        throw Rule("LOAN_LOCATION", "محلِ برگشت قرض را انتخاب کنید.");
                    await RequireLocationAsync(terminalId, input.StorageTankId, ct);
                    var request = new InventoryMovementRequest
                    {
                        ProductId = loan.ProductId,
                        TerminalId = terminalId,
                        StorageTankId = input.StorageTankId,
                        MovementDate = date,
                        QuantityMt = input.QuantityMt,
                        ReferenceDocument = SettlementReferencePrefix + key.ToString("N"),
                        Notes = borrow ? $"برگشتِ جنسِ قرض گرفته‌شده {label}" : $"برگشتِ جنسِ قرض داده‌شده {label}"
                    };
                    var movement = borrow
                        ? await movements.PostOutboundAsync(request, StockGuard.Full, ct)
                        : await movements.PostInboundAsync(request, ct);
                    settlement.TerminalId = terminalId;
                    settlement.StorageTankId = input.StorageTankId;
                    settlement.InventoryMovementId = movement.Id;
                    if (settings is not null)
                    {
                        if (borrow)
                        {
                            // جنسِ برگشتی از موجودیِ همان محل به بهای اوسط خارج می‌شود؛ تفاوتش با ارزش
                            // دفتریِ بدهی فقط با حسابِ تفاوتِ قرارداد ثبت می‌شود.
                            var consumption = await valuation.TryConsumeAsync(companyId, loan.ProductId, terminalId, input.QuantityMt, ct);
                            if (!consumption.Succeeded || consumption.CostUsd <= 0m)
                                throw Rule("LOAN_NOT_VALUED", "موجودیِ این محل ارزش ثبت‌شدهِ کافی ندارد؛ برگشت قرض ثبت نشد.");
                            settlement.InventoryValueUsd = consumption.CostUsd;
                            settlement.DifferenceUsd = carried!.Value - consumption.CostUsd;
                            lines.Add(Line(loan.LoanAccountId!.Value, carried.Value, 0m, loan.CounterpartyType, loan.CounterpartyId, loan, $"Commodity loan {label} returned"));
                            lines.Add(Line(settings.InventoryAccountId, 0m, consumption.CostUsd, null, null, loan, $"Commodity loan {label} returned", input.StorageTankId));
                        }
                        else
                        {
                            // جنسِ قرض داده‌شده با همان ارزش ثبت‌شده که خارج شده بود به موجودی برمی‌گردد.
                            await valuation.ApplyReceiptAsync(companyId, loan.ProductId, terminalId, input.QuantityMt, carried!.Value, ct);
                            settlement.InventoryValueUsd = carried.Value;
                            settlement.DifferenceUsd = 0m;
                            lines.Add(Line(settings.InventoryAccountId, carried.Value, 0m, null, null, loan, $"Commodity loan {label} returned", input.StorageTankId));
                            lines.Add(Line(loan.LoanAccountId!.Value, 0m, carried.Value, loan.CounterpartyType, loan.CounterpartyId, loan, $"Commodity loan {label} returned"));
                        }
                    }
                    break;
                }
                case CommodityLoanSettlementKind.CashSettlement:
                {
                    if (!loan.CashSettlementAllowed || loan.CashSettlementUnitPriceUsd is not > 0m)
                        throw Rule("LOAN_CASH_NOT_ALLOWED", "قرارداد این قرض پرداخت پول به‌جای تیل را با قیمت و سندِ توافق مجاز نکرده است.");
                    if (input.CashAccountId is not { } cashAccountId)
                        throw Rule("LOAN_CASH_ACCOUNT", "صندوق یا بانک را انتخاب کنید.");
                    await RequireUsdCashAccountAsync(cashAccountId, ct);
                    var amount = Money(input.QuantityMt * loan.CashSettlementUnitPriceUsd.Value);
                    settlement.CashAmountUsd = amount;
                    settlement.CashAccountId = cashAccountId;
                    if (settings is not null)
                    {
                        settlement.DifferenceUsd = borrow ? carried!.Value - amount : amount - carried!.Value;
                        if (borrow)
                        {
                            lines.Add(Line(loan.LoanAccountId!.Value, carried.Value, 0m, loan.CounterpartyType, loan.CounterpartyId, loan, $"Commodity loan {label} cash settlement"));
                            lines.Add(Line(settings.CashBankControlAccountId, 0m, amount, null, null, loan, $"Commodity loan {label} cash settlement", cashAccountId: cashAccountId));
                        }
                        else
                        {
                            lines.Add(Line(settings.CashBankControlAccountId, amount, 0m, null, null, loan, $"Commodity loan {label} cash settlement", cashAccountId: cashAccountId));
                            lines.Add(Line(loan.LoanAccountId!.Value, 0m, carried.Value, loan.CounterpartyType, loan.CounterpartyId, loan, $"Commodity loan {label} cash settlement"));
                        }
                    }
                    break;
                }
                case CommodityLoanSettlementKind.QuantityDifference:
                {
                    if (loan.QuantityTolerancePercent is not { } tolerance)
                        throw Rule("LOAN_TOLERANCE_NOT_SET", "فرق مجاز مقدارِ تفاوت مقدار در قرارداد تعیین نشده است؛ بستنِ تفاوت مقدار ممکن نیست.");
                    if (input.QuantityMt != outstanding)
                        throw Rule("LOAN_DIFFERENCE_REMAINDER", "فرق مقدار فقط برای تمام باقی قرض ثبت می‌شود.");
                    if (outstanding > loan.QuantityMt * tolerance / 100m)
                        throw Rule("LOAN_DIFFERENCE_EXCEEDS", $"باقی قرض {Qty(outstanding)} تُن است و از فرق مجاز قرارداد ({tolerance:0.####} فیصد) بیشتر است؛ تیل باید برگردد یا طبق قرارداد با پول پرداخت شود.");
                    if (string.IsNullOrWhiteSpace(input.DocumentNumber) && string.IsNullOrWhiteSpace(input.Notes))
                        throw Rule("LOAN_DIFFERENCE_DOCUMENT", "برای بستن باقی کم قرض، سند یا توضیحِ تأیید لازم است.");
                    if (settings is not null)
                    {
                        // قرض دادن تیل: طلبِ باقی ضرر است؛ قرض گرفتن تیل: بدهیِ باقی مفاد است.
                        settlement.DifferenceUsd = borrow ? carried!.Value : -carried!.Value;
                        if (carried.Value > 0m)
                        {
                            if (borrow)
                                lines.Add(Line(loan.LoanAccountId!.Value, carried.Value, 0m, loan.CounterpartyType, loan.CounterpartyId, loan, $"Commodity loan {label} quantity difference"));
                            else
                                lines.Add(Line(loan.LoanAccountId!.Value, 0m, carried.Value, loan.CounterpartyType, loan.CounterpartyId, loan, $"Commodity loan {label} quantity difference"));
                        }
                    }
                    break;
                }
            }

            if (settings is not null && settlement.DifferenceUsd is { } difference && difference != 0m)
            {
                if (loan.DifferenceAccountId is not { } differenceAccount)
                    throw Rule("LOAN_DIFFERENCE_ACCOUNT", "این برگشت قرض تفاوتِ ارزش دارد اما «حساب تفاوت» در قرارداد تعیین نشده است؛ سند ثبت نشد.");
                lines.Add(difference > 0m
                    ? Line(differenceAccount, 0m, difference, null, null, loan, $"Commodity loan {label} settlement difference")
                    : Line(differenceAccount, -difference, 0m, null, null, loan, $"Commodity loan {label} settlement difference"));
            }

            db.CommodityLoanSettlements.Add(settlement);
            formTokens.Stamp(input.FormToken, "CommodityLoan.Settle", SettlementEntity);
            await db.SaveChangesAsync(ct);

            if (settlement.CashAccountId.HasValue)
            {
                var payment = NewPayment(loan, settlement, companyId, reversal: false);
                db.PaymentTransactions.Add(payment);
                await db.SaveChangesAsync(ct);
                settlement.PaymentTransactionId = payment.Id;
                await db.SaveChangesAsync(ct);
            }

            if (settings is not null && lines.Count > 0)
            {
                await PostJournalAsync(companyId, $"CML-{companyId:D6}-S{settlement.Id:D9}", settlement.SettlementDate,
                    SettlementEventId(settlement.Id), SettlementEntity, settlement.Id,
                    $"Commodity loan {settlement.Kind} {label}", lines, ct);
            }

            await audit.LogAsync(SettlementEntity, settlement.Id, AuditAction.Insert, diff: Describe(new
            {
                loan.LoanNumber, Kind = settlement.Kind.ToString(), settlement.QuantityMt, settlement.SettlementDate, settlement.TerminalId,
                settlement.StorageTankId, settlement.InventoryMovementId, settlement.CarryingValueUsd, settlement.InventoryValueUsd,
                settlement.CashAmountUsd, settlement.CashAccountId, settlement.PaymentTransactionId, settlement.DifferenceUsd, settlement.DocumentNumber
            }), ct: ct);
            await db.SaveChangesAsync(ct);
            return settlement.Id;
        }, ct);

    // ---------------------------------------------------------------- لغو

    public Task CancelSettlementAsync(int settlementId, string reason, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            var why = Required(reason, "دلیل لغو", 500);
            var settlement = await db.CommodityLoanSettlements.FirstOrDefaultAsync(s => s.Id == settlementId, ct)
                ?? throw Rule("LOAN_SETTLEMENT_NOT_FOUND", "برگشت قرض پیدا نشد.");
            var loan = await LockLoanAsync(settlement.CommodityLoanId, ct);
            await db.Entry(settlement).ReloadAsync(ct);
            if (settlement.Status != CommodityLoanStatus.Active)
                throw Rule("LOAN_SETTLEMENT_CANCELLED", "این برگشت قرض قبلاً لغو شده است.");
            // ارزشِ هر برگشت قرض بر پایهٔ برگشت قرض‌های پیش از آن است؛ لغو از آخر به اول.
            if (await db.CommodityLoanSettlements.AnyAsync(s => s.CommodityLoanId == loan.Id && s.Status == CommodityLoanStatus.Active
                    && s.Id > settlement.Id, ct))
                throw Rule("LOAN_SETTLEMENT_NOT_LATEST", "برگشت قرض‌های بعدیِ همین قرض را اول لغو کنید.");

            var companyId = await systemCompany.GetOwnerCompanyIdAsync(ct);
            var journaled = await JournalExistsAsync(SettlementEventId(settlement.Id), ct);
            var borrow = loan.Direction == CommodityLoanDirection.Borrow;
            if (settlement.InventoryMovementId is { } movementId)
            {
                if (journaled)
                {
                    if (borrow)
                        await valuation.ReturnAsync(companyId, loan.ProductId, settlement.TerminalId!.Value, settlement.QuantityMt,
                            settlement.InventoryValueUsd ?? 0m, ct);
                    else
                    {
                        var reversed = await valuation.TryReverseReceiptAsync(companyId, loan.ProductId, settlement.TerminalId!.Value,
                            settlement.QuantityMt, settlement.InventoryValueUsd ?? 0m, ct);
                        if (!reversed.Succeeded)
                            throw Rule("LOAN_CANCEL_CONSUMED", "جنسِ برگشتی دیگر در موجودیِ با قیمت ثبت‌شدهٔ این محل نیست (فروخته یا مصرف شده)؛ لغو ممکن نیست.");
                    }
                }
                var original = await db.InventoryMovements.SingleAsync(m => m.Id == movementId, ct);
                // برگشتِ ورود (جنسی که به موجودی آمده بود) نباید موجودی را منفی کند.
                var reversal = await movements.PostReversalAsync(original, DateTime.UtcNow.Date, $"لغو برگشت قرضٔ قرض {loan.LoanNumber}: {why}", ct,
                    original.Direction == MovementDirection.In ? StockGuard.Full : StockGuard.None)
                    ?? throw Rule("LOAN_ALREADY_REVERSED", "حرکت موجودیِ این برگشت قرض قبلاً برگشت خورده است.");
                _ = reversal;
            }
            if (settlement.PaymentTransactionId.HasValue)
            {
                var payment = NewPayment(loan, settlement, companyId, reversal: true);
                db.PaymentTransactions.Add(payment);
                await db.SaveChangesAsync(ct);
                settlement.ReversalPaymentTransactionId = payment.Id;
            }
            if (journaled)
                await ReverseJournalAsync(SettlementEventId(settlement.Id), $"CML-{companyId:D6}-S{settlement.Id:D9}-R",
                    $"Cancel commodity loan settlement {loan.LoanNumber}", ct);

            settlement.Status = CommodityLoanStatus.Cancelled;
            settlement.CancelledAtUtc = DateTime.UtcNow;
            settlement.CancelledByUserId = currentUser?.UserId;
            settlement.CancellationReason = why;
            await audit.LogAsync(SettlementEntity, settlement.Id, AuditAction.Reverse, diff: Describe(new { loan.LoanNumber, Reason = why }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    public Task CancelLoanAsync(int loanId, string reason, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            var why = Required(reason, "دلیل لغو", 500);
            var loan = await LockLoanAsync(loanId, ct);
            if (loan.Status != CommodityLoanStatus.Active)
                throw Rule("LOAN_CANCELLED", "این قرض قبلاً لغو شده است.");
            if (await db.CommodityLoanSettlements.AnyAsync(s => s.CommodityLoanId == loan.Id && s.Status == CommodityLoanStatus.Active, ct))
                throw Rule("LOAN_HAS_SETTLEMENTS", "برای این قرض برگشت قرض ثبت شده است؛ اول برگشت قرض‌ها را لغو کنید.");

            var companyId = await systemCompany.GetOwnerCompanyIdAsync(ct);
            var journaled = await JournalExistsAsync(LoanEventId(loan.Id), ct);
            var borrow = loan.Direction == CommodityLoanDirection.Borrow;
            if (journaled)
            {
                if (borrow)
                {
                    var reversed = await valuation.TryReverseReceiptAsync(companyId, loan.ProductId, loan.TerminalId, loan.QuantityMt,
                        loan.CarryingValueUsd ?? 0m, ct);
                    if (!reversed.Succeeded)
                        throw Rule("LOAN_CANCEL_CONSUMED", "جنسِ قرض گرفته‌شده دیگر در موجودیِ با قیمت ثبت‌شدهٔ این محل نیست (فروخته یا مصرف شده)؛ لغو ممکن نیست.");
                }
                else
                {
                    await valuation.ReturnAsync(companyId, loan.ProductId, loan.TerminalId, loan.QuantityMt, loan.CarryingValueUsd ?? 0m, ct);
                }
            }
            var original = await db.InventoryMovements.SingleAsync(m => m.Id == loan.InventoryMovementId, ct);
            _ = await movements.PostReversalAsync(original, DateTime.UtcNow.Date, $"لغو قرض تیل {loan.LoanNumber}: {why}", ct,
                    original.Direction == MovementDirection.In ? StockGuard.Full : StockGuard.None)
                ?? throw Rule("LOAN_ALREADY_REVERSED", "حرکت موجودیِ این قرض قبلاً برگشت خورده است.");
            if (journaled)
                await ReverseJournalAsync(LoanEventId(loan.Id), $"CML-{companyId:D6}-L{loan.Id:D9}-R", $"Cancel commodity loan {loan.LoanNumber}", ct);

            loan.Status = CommodityLoanStatus.Cancelled;
            loan.CancelledAtUtc = DateTime.UtcNow;
            loan.CancelledByUserId = currentUser?.UserId;
            loan.CancellationReason = why;
            await audit.LogAsync(LoanEntity, loan.Id, AuditAction.Reverse, diff: Describe(new { loan.LoanNumber, Reason = why }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    // ---------------------------------------------------------------- helpers

    private async Task<AccountingSettings> RequireGlReadyAsync(int companyId, CancellationToken ct)
    {
        if (!_options.Pilots.CommodityLoan)
            throw Rule("LOAN_GL_NOT_READY", "ثبت دفتر کلِ قرض تیل فعال نیست (Accounting:Pilots:CommodityLoan)؛ سند ثبت نشد.");
        var settings = await db.AccountingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct)
            ?? throw Rule("LOAN_GL_NOT_READY", "تنظیمات حسابداری شرکت ثبت نشده است؛ سند ثبت نشد.");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            throw Rule("LOAN_GL_NOT_READY", "ارز عملیاتی دفتر کل USD نیست؛ ارزش قرض حدس زده نمی‌شود.");
        return settings;
    }

    private async Task RequireCounterpartyAsync(AccountingPartyType type, int id, CancellationToken ct)
    {
        if (!CounterpartyTypes.Contains(type))
            throw Rule("LOAN_COUNTERPARTY", "طرف قرض باید مشتری، فروشنده، شرکتِ دیگر یا شریک باشد.");
        var exists = type switch
        {
            AccountingPartyType.Customer => await db.Customers.AnyAsync(x => x.Id == id && x.IsActive, ct),
            AccountingPartyType.Supplier => await db.Suppliers.AnyAsync(x => x.Id == id && x.IsActive, ct),
            AccountingPartyType.Company => await db.Companies.AnyAsync(x => x.Id == id && x.IsActive && !x.IsSystemOwner, ct),
            AccountingPartyType.Partner => await db.Partners.AnyAsync(x => x.Id == id && x.IsActive, ct),
            _ => false
        };
        if (!exists)
            throw Rule("LOAN_COUNTERPARTY", "طرف قرض پیدا نشد یا غیرفعال است.");
    }

    private async Task RequireLocationAsync(int terminalId, int? tankId, CancellationToken ct)
    {
        if (!await db.Terminals.AnyAsync(t => t.Id == terminalId && t.IsActive, ct))
            throw Rule("LOAN_LOCATION", "ترمینال پیدا نشد یا غیرفعال است.");
        if (tankId.HasValue && !await db.StorageTanks.AnyAsync(t => t.Id == tankId && t.TerminalId == terminalId, ct))
            throw Rule("LOAN_LOCATION", "مخزن به این ترمینال تعلق ندارد.");
    }

    private async Task RequireAccountAsync(int? accountId, int companyId, AccountType? type, string? typeMessage, CancellationToken ct)
    {
        if (accountId is null) return;
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw Rule("LOAN_ACCOUNT", "حساب انتخاب‌شده پیدا نشد.");
        if (!account.IsActive || account.CompanyId != companyId)
            throw Rule("LOAN_ACCOUNT", "حساب انتخاب‌شده فعال نیست یا به شرکت تعلق ندارد.");
        if (type.HasValue && account.AccountType != type)
            throw Rule("LOAN_ACCOUNT", typeMessage!);
        if (type is null && account.AccountType is not (AccountType.Revenue or AccountType.Expense))
            throw Rule("LOAN_ACCOUNT", "حساب تفاوت باید حساب درآمد یا مصرف (مفاد/ضرر) باشد.");
    }

    private async Task RequireUsdCashAccountAsync(int cashAccountId, CancellationToken ct)
    {
        var cash = await db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == cashAccountId, ct)
            ?? throw Rule("LOAN_CASH_ACCOUNT", "صندوق یا بانک پیدا نشد.");
        if (!cash.IsActive)
            throw Rule("LOAN_CASH_ACCOUNT", "این صندوق/بانک غیرفعال است.");
        if (cash.AccountType != CashAccountType.Mixed && !string.Equals(cash.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            throw Rule("LOAN_CASH_ACCOUNT", "پرداخت پول به‌جای تیلِ قرض تیل به دالر است؛ این صندوق/بانک دالری نیست.");
    }

    private async Task<CommodityLoan> LockLoanAsync(int loanId, CancellationToken ct)
    {
        if (IsPostgres)
            await db.Database.ExecuteSqlInterpolatedAsync($@"SELECT ""Id"" FROM ""CommodityLoans"" WHERE ""Id"" = {loanId} FOR UPDATE", ct);
        var loan = await db.CommodityLoans.FirstOrDefaultAsync(l => l.Id == loanId, ct)
            ?? throw Rule("LOAN_NOT_FOUND", "قرض تیل پیدا نشد.");
        await db.Entry(loan).ReloadAsync(ct);
        return loan;
    }

    private static AccountingPostLine Line(int accountId, decimal debit, decimal credit, AccountingPartyType? partyType, int? partyId,
        CommodityLoan loan, string description, int? tankId = null, int? cashAccountId = null)
        => new(accountId, debit, credit, "USD", debit + credit, 1m, partyType, partyId,
            TankId: tankId, ProductId: loan.ProductId, CashAccountId: cashAccountId, Description: description);

    private async Task PostJournalAsync(int companyId, string journalNumber, DateTime date, string eventId, string entityType, int entityId,
        string description, IReadOnlyCollection<AccountingPostLine> lines, CancellationToken ct)
    {
        if (await JournalExistsAsync(eventId, ct)) return;
        await posting.PostAsync(new AccountingPostRequest(companyId, journalNumber, date.Date, date.Date, date.Date, SourceModule, lines,
            SourceEventId: eventId, SourceEntityType: entityType, SourceEntityId: entityId, Description: description,
            PostedByUserId: currentUser?.UserId), ct);
    }

    private Task<bool> JournalExistsAsync(string eventId, CancellationToken ct)
        => db.JournalEntries.AsNoTracking().AnyAsync(j => j.SourceModule == SourceModule && j.SourceEventId == eventId
            && j.Status == JournalEntryStatus.Posted, ct);

    private async Task ReverseJournalAsync(string eventId, string journalNumber, string description, CancellationToken ct)
    {
        var original = await db.JournalEntries.AsNoTracking()
            .SingleAsync(j => j.SourceModule == SourceModule && j.SourceEventId == eventId, ct);
        if (await db.JournalEntries.AsNoTracking().AnyAsync(j => j.SourceModule == SourceModule && j.SourceEventId == Reversed(eventId), ct))
            return;
        await posting.ReverseAsync(new AccountingReversalRequest(original.Id, journalNumber, DateTime.UtcNow.Date, SourceModule,
            Reversed(eventId), description, currentUser?.UserId), ct);
    }

    private PaymentTransaction NewPayment(CommodityLoan loan, CommodityLoanSettlement settlement, int companyId, bool reversal)
    {
        // قرض دادن تیل: طرف به شرکت می‌پردازد؛ قرض گرفتن تیل: شرکت به طرف می‌پردازد. نوع Manual عمداً: سطر صندوقِ
        // ژورنال فقط از همین سرویس می‌آید و تطبیق دریافت/پرداختِ عادی آن را به قراردادها نمی‌چسباند.
        var inbound = loan.Direction == CommodityLoanDirection.Lend;
        if (reversal) inbound = !inbound;
        var reference = $"{loan.LoanNumber}-S{settlement.Id}";
        return new PaymentTransaction
        {
            PaymentDate = reversal ? DateTime.UtcNow.Date : settlement.SettlementDate.Date,
            Direction = inbound ? PaymentDirection.In : PaymentDirection.Out,
            PaymentKind = inbound ? PaymentKind.ManualReceipt : PaymentKind.ManualPayment,
            CompanyId = companyId,
            CashAccountId = settlement.CashAccountId,
            FundingSource = PaymentFundingSource.Company,
            Amount = settlement.CashAmountUsd!.Value,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = settlement.CashAmountUsd.Value,
            Reference = reversal ? reference + "-REV" : reference,
            Description = (reversal ? "لغو پرداخت پول به‌جای تیل قرض تیل " : "پرداخت پول به‌جای تیل قرض تیل ") + loan.LoanNumber,
            CreatedByUserId = currentUser?.UserId
        };
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

    private bool IsPostgres => db.Database.IsRelational()
        && string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    private static void RequirePositive(decimal value, string label)
    {
        if (value <= 0m) throw Rule("LOAN_QUANTITY", $"{label} باید بیشتر از صفر باشد.");
        if (decimal.Round(value, 4) != value) throw Rule("LOAN_PRECISION", $"{label} بیشترین چهار رقم اعشار دارد.");
    }

    private static string Required(string? value, string label, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Rule("LOAN_REQUIRED", $"{label} الزامی است.");
        var trimmed = value.Trim();
        return trimmed.Length > max ? throw Rule("LOAN_REQUIRED", $"{label} بیش از {max} حرف است.") : trimmed;
    }

    private static decimal Round4(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
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
