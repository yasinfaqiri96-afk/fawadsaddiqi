using System.Globalization;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Services.CompanyFlow;

namespace PTGOilSystem.Web.Models.PartyStatements;

public enum PartyStatementPartyType
{
    Customer = 1,
    Supplier = 2,
    ServiceProvider = 3,
    Sarraf = 4,
    Employee = 5,
    Partner = 6,
    Driver = 7,
    Company = 8
}

public readonly record struct PartyRef(
    PartyStatementPartyType PartyType,
    int PartyId,
    int? CompanyId = null);

public sealed class PartyStatementFilter
{
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public int? ContractId { get; init; }
    public int? CompanyId { get; init; }
    public string? CurrencyCode { get; init; }
    public string? SourceType { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    // پیش‌فرض: ستون‌های نفتی مخفی باشند و کاربر در صورت نیاز نمایششان دهد.
    public bool IncludeOperationalColumns { get; init; }
}

public sealed class PartyStatementPolicy
{
    public required PartyStatementPartyType PartyType { get; init; }
    public required string StatementTitleFa { get; init; }
    public required string StatementTitleEn { get; init; }
    public required string PartyInformationTitleFa { get; init; }
    public required string PartyInformationTitleEn { get; init; }
    public required string AccountTypeFa { get; init; }
    public required string AccountTypeEn { get; init; }
    /// <summary>یعنی چه چیزی در ستون «رسید» این طرف‌حساب می‌نشیند.</summary>
    public required string ReceiptMeaningFa { get; init; }
    /// <summary>یعنی چه چیزی در ستون «Debit» این طرف‌حساب می‌نشیند.</summary>
    public required string ReceiptMeaningEn { get; init; }
    /// <summary>یعنی چه چیزی در ستون «برد» این طرف‌حساب می‌نشیند.</summary>
    public required string OutflowMeaningFa { get; init; }
    /// <summary>یعنی چه چیزی در ستون «Credit» این طرف‌حساب می‌نشیند.</summary>
    public required string OutflowMeaningEn { get; init; }
    public bool SupportsOperationalColumns { get; init; }

    /// <summary>نقش طرف‌حساب برای لایهٔ مرکزی تعیین جهت.</summary>
    public required CompanyFlowPartyRole FlowRole { get; init; }

    public string AccountType(bool isEnglish) => isEnglish ? AccountTypeEn : AccountTypeFa;

    public string ReceiptMeaning(bool isEnglish) => isEnglish ? ReceiptMeaningEn : ReceiptMeaningFa;

    public string OutflowMeaning(bool isEnglish) => isEnglish ? OutflowMeaningEn : OutflowMeaningFa;

    /// <summary>
    /// جملهٔ «مثبت یعنی چه» وقتی فاعلِ جمله شرکت نیست. فقط حساب شریک از این استفاده
    /// می‌کند: آنجا عدد ماندهٔ شراکت است، نه طلب و بدهیِ شرکت از یک طرفِ معامله.
    /// خالی یعنی همان جملهٔ استاندارد از دید شرکت.
    /// </summary>
    public CompanyFlowTextKey? PositiveBalanceKeyOverride { get; init; }

    public CompanyFlowTextKey? NegativeBalanceKeyOverride { get; init; }

    /// <summary>
    /// معنی علامت بیلانس — علامت و منطقش مستقل از زبان است و فقط متن ترجمه می‌شود.
    /// همیشه از منبع مرکزی <see cref="CompanyFlowText"/> می‌آید.
    /// </summary>
    public string BalanceMeaning(decimal balance, bool isEnglish = false)
    {
        if (balance > 0m && PositiveBalanceKeyOverride.HasValue)
        {
            return CompanyFlowText.Get(PositiveBalanceKeyOverride.Value, isEnglish);
        }

        if (balance < 0m && NegativeBalanceKeyOverride.HasValue)
        {
            return CompanyFlowText.Get(NegativeBalanceKeyOverride.Value, isEnglish);
        }

        return CompanyFlowText.BalanceMeaning(balance, CompanyFlowAccountKind.PartyAccount, isEnglish);
    }
}

public sealed class PartyStatementSummary
{
    public decimal OpeningBalance { get; init; }
    public decimal TotalReceipt { get; init; }
    public decimal TotalOutflow { get; init; }
    public decimal ClosingBalance { get; init; }
    public decimal ClosingBalanceAbsolute => Math.Abs(ClosingBalance);
    public string ClosingBalanceMeaning { get; init; } = string.Empty;
    public string ClosingBalanceMeaningEn { get; init; } = string.Empty;

    /// <summary>معنی بیلانس نهایی در زبان انتخاب‌شده — عدد و علامت تغییر نمی‌کند.</summary>
    public string ClosingBalanceMeaningFor(bool isEnglish)
        => isEnglish ? ClosingBalanceMeaningEn : ClosingBalanceMeaning;

    /// <summary>
    /// رنگ همان معنیِ بالا. چون متن از علامت <see cref="ClosingBalance"/> ساخته می‌شود،
    /// رنگ هم باید از همان علامت بیاید: مثبت = طلبکاریم (سبز)، منفی = بدهکاریم (سرخ).
    /// صفحه‌های طرف‌حساب قبلاً رنگ را از قرارداد علامتِ مدل خودشان می‌ساختند و رنگ با
    /// متن وارونه می‌شد.
    /// </summary>
    public string? ClosingBalanceTone
        => ClosingBalance > 0m ? "success" : ClosingBalance < 0m ? "danger" : null;
    public string BaseCurrencyCode { get; init; } = "USD";

    // نمایش روبلی: وقتی ارز روبل انتخاب شود، جمع‌ها با ارزش روبلی واقعیِ هر سند
    // (نرخ تاریخی همان سند) محاسبه می‌شوند. اسنادی که به روبل ثبت نشده‌اند ارزش
    // روبلی ندارند و در این جمع‌ها شرکت نمی‌کنند (در سطر با «—» نمایش داده می‌شوند).
    public bool IsRubPresentation { get; init; }
    public decimal? OpeningBalanceRub { get; init; }
    public decimal? TotalReceiptRub { get; init; }
    public decimal? TotalOutflowRub { get; init; }
    public decimal? ClosingBalanceRub { get; init; }
    public decimal? ClosingBalanceRubAbsolute => ClosingBalanceRub.HasValue ? Math.Abs(ClosingBalanceRub.Value) : null;

    // معنی/رنگِ مانده در نمایش روبلی. حسابِ روبلی می‌تواند بدهکار باشد در حالی که همان
    // مانده به دالر طلبکار در می‌آید (نرخ روز پرداخت با نرخ روز بارگیری فرق دارد)، پس
    // متن و رنگ باید از علامتِ همان ارزی بیاید که نمایش داده می‌شود.
    public string ClosingBalanceRubMeaning { get; init; } = string.Empty;
    public string ClosingBalanceRubMeaningEn { get; init; } = string.Empty;

    public string ClosingBalanceMeaningFor(bool isEnglish, bool isRub)
        => isRub && ClosingBalanceRub.HasValue
            ? (isEnglish ? ClosingBalanceRubMeaningEn : ClosingBalanceRubMeaning)
            : ClosingBalanceMeaningFor(isEnglish);

    public string? ClosingBalanceToneFor(bool isRub)
    {
        var value = isRub && ClosingBalanceRub.HasValue ? ClosingBalanceRub.Value : ClosingBalance;
        return value > 0m ? "success" : value < 0m ? "danger" : null;
    }

    // جمع‌های ستون‌های تجارتی (معامله / دریافت / پرداخت). از همان مبالغ سطرها با
    // PartyStatementPresentation.AmountsFor ساخته می‌شوند و فقط تفکیک نمایشیِ همان
    // TotalReceipt/TotalOutflow هستند؛ بیلانس از اینها محاسبه نمی‌شود.
    public decimal TotalTrade { get; init; }
    public decimal TotalReceived { get; init; }
    public decimal TotalPaid { get; init; }
    public decimal? TotalTradeRub { get; init; }
    public decimal? TotalReceivedRub { get; init; }
    public decimal? TotalPaidRub { get; init; }

    /// <summary>
    /// مقدار خالص معاملات دوره، فقط وقتی همهٔ سطرهای معامله مقدارِ ثبت‌شده با یک واحد دارند.
    /// جمعِ ناقص گمراه‌کننده است، پس در غیر آن null می‌ماند.
    /// </summary>
    public decimal? TradeQuantity { get; init; }
    public string? TradeQuantityUnit { get; init; }

    public decimal TradeTotalFor(bool isRub) => isRub ? TotalTradeRub ?? 0m : TotalTrade;
    public decimal ReceivedTotalFor(bool isRub) => isRub ? TotalReceivedRub ?? 0m : TotalReceived;
    public decimal PaidTotalFor(bool isRub) => isRub ? TotalPaidRub ?? 0m : TotalPaid;
}

public sealed class PartyStatementRow
{
    public int Sequence { get; set; }
    public DateTime Date { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Reference { get; set; }
    /// <summary>شناسهٔ سند Ledger برای پیوند نمایشی به جزئیات؛ در محاسبه شرکت نمی‌کند.</summary>
    public int? LedgerEntryId { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// شرح سطر در زبان نمایش. تنها سطری که ترجمه می‌شود سطر «بیلانس اول دوره» است؛ بقیه
    /// شرحِ خودِ سند هستند و ترجمه نمی‌شوند تا داده تاریخی دست‌نخورده بماند.
    /// </summary>
    public string DescriptionFor(bool isEnglish)
        => IsOpeningBalance
            ? CompanyFlowText.Get(CompanyFlowTextKey.OpeningBalance, isEnglish)
            : Description;

    // شرح تجارتیِ سطر که از سند اصلی (فروش، بارگیری، پرداخت، تسویه) ساخته می‌شود.
    // فقط نمایشی است و Description/Reference دست‌نخورده برای جستجو و خروجی‌ها می‌ماند.
    // null یعنی سند اصلی در دسترس نبود و همان Description نمایش داده می‌شود.

    /// <summary>خط اول شرح: نوع معامله و جنس، مثلاً «فروش دیزل».</summary>
    public string? Title { get; set; }

    /// <summary>خط دوم: مقدار × نرخ، موتر/واگن و طرف معامله — فقط اطلاعاتِ قطعی.</summary>
    public string? Detail { get; set; }

    /// <summary>خط سوم (کم‌رنگ): مرجع سند برای بررسی، مثلاً «فاکتور INV001».</summary>
    public string? DocumentLabel { get; set; }

    /// <summary>موترِ معامله (فقط فروش/خرید، از پیوند مستقیم سند)، مثلاً «موتر 12345»؛ کنار مرجع در خط سوم.</summary>
    public string? VehicleLabel { get; set; }

    /// <summary>مقدار سند اصلی برای جمعِ مقدار در خلاصه؛ ستون‌های عملیاتی را تغییر نمی‌دهد.</summary>
    public decimal? TradeQuantity { get; set; }
    public string? TradeQuantityUnit { get; set; }

    /// <summary>
    /// نرخ فی واحدِ همان سند، فقط وقتی مقدار × نرخ دقیقاً مبلغ همین سطر را می‌سازد؛ وگرنه null
    /// تا هیچ نرخ ساختگی نمایش داده نشود.
    /// </summary>
    public decimal? TradeUnitPrice { get; set; }
    public string? TradeUnitPriceCurrency { get; set; }

    public string TitleFor(bool isEnglish)
        => IsOpeningBalance
            ? CompanyFlowText.Get(CompanyFlowTextKey.OpeningBalance, isEnglish)
            : string.IsNullOrWhiteSpace(Title) ? Description : Title;

    public string? DocumentLabelOrReference
        => IsOpeningBalance ? null : string.IsNullOrWhiteSpace(DocumentLabel) ? Reference : DocumentLabel;

    /// <summary>خط سوم کامل: «فاکتور INV001 · موتر 12345»؛ بدون موتر فقط مرجع سند.</summary>
    public string? DocumentLine
    {
        get
        {
            if (IsOpeningBalance)
            {
                return null;
            }

            var parts = new[] { DocumentLabelOrReference, VehicleLabel }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
            return parts.Count == 0 ? null : string.Join(PartyStatementPresentation.DocumentVehicleSeparator, parts);
        }
    }

    /// <summary>سطر معامله (کالا/خدمت) است یا حرکت پول — فقط برای انتخاب ستون نمایش.</summary>
    public PartyStatementRowKind Kind => PartyStatementPresentation.KindOf(SourceType);

    /// <summary>رسید — ارزشی که شرکت در این سطر دریافت کرده است (USD).</summary>
    public decimal? ReceiptBase { get; set; }
    /// <summary>برد — ارزشی که شرکت در این سطر داده است (USD).</summary>
    public decimal? OutflowBase { get; set; }
    public decimal RunningBalance { get; set; }
    // ارزش روبلی سطر با نرخ تاریخی همان سند (فقط اسناد ذاتاً روبلی). null یعنی سند
    // روبلی نیست و در نمایش روبلی «—» نشان داده می‌شود.
    public decimal? ReceiptRub { get; set; }
    public decimal? OutflowRub { get; set; }
    public decimal? RunningBalanceRub { get; set; }
    public decimal? OriginalAmount { get; set; }
    public string OriginalCurrency { get; set; } = "USD";
    public decimal? FxRate { get; set; }
    public string? FxRateDisplay { get; set; }
    public decimal? Quantity { get; set; }
    public string? QuantityUnit { get; set; }
    public decimal? PlattsPrice { get; set; }
    public decimal? PremiumOrDiscount { get; set; }
    public decimal? UnitPrice { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public int SourceId { get; set; }
    public long PostingSequence { get; set; }

    /// <summary>
    /// برچسب دستهٔ نمایشی سطر (برای مصرف: نام نوع مصرف). فقط در نمای «خلاصه» برای
    /// گروه‌بندی نمایشی به کار می‌رود و روی هیچ مبلغ، جهت یا مانده‌ای اثر ندارد.
    /// </summary>
    public string? CategoryLabel { get; set; }
    public int? ContractId { get; set; }
    public string? ContractNumber { get; set; }
    public bool IsOpeningBalance { get; set; }

    /// <summary>سند لغوشده/جایگزین‌شده — فقط برای Audit نمایش داده می‌شود و در جمع‌ها نمی‌آید.</summary>
    public bool IsCancelled { get; set; }

    /// <summary>این سطر خودش سند برگشت است (جهتش وارونهٔ سند اصلی محاسبه شده).</summary>
    public bool IsReversalRow { get; set; }

    /// <summary>جهت تجاری سطر از دید شرکت — از لایهٔ مرکزی می‌آید، نه از Debit/Credit.</summary>
    public CompanyFlowDirection? FlowDirection { get; set; }

    // بیلانس طرف‌حساب: اول دوره + Σبرد − Σرسید (مطابق فایل کاری شرکت).
    public decimal SignedAmount => (OutflowBase ?? 0m) - (ReceiptBase ?? 0m);

    // اثر روبلی سطر روی بیلانس. null یعنی سند روبلی نیست (در جمع روبلی شرکت نمی‌کند).
    public decimal? SignedAmountRub =>
        OutflowRub.HasValue || ReceiptRub.HasValue ? (OutflowRub ?? 0m) - (ReceiptRub ?? 0m) : null;
}

public sealed class PartyStatementPartyInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Address { get; init; }
}

public sealed class PartyStatementDocumentInfo
{
    public string StatementNumber { get; init; } = string.Empty;
    public DateTime StatementDate { get; init; }
    public DateTime? PeriodFrom { get; init; }
    public DateTime? PeriodTo { get; init; }
    public string BaseCurrencyCode { get; init; } = "USD";
    public DateTime GeneratedAtUtc { get; init; }
}

public sealed class PartyStatementColumnOptions
{
    public bool ShowRub { get; init; }
    public bool ShowAed { get; init; }
    public bool ShowOriginalAmount { get; init; }
    public bool ShowCurrency { get; init; }
    public bool ShowFxRate { get; init; }
    public bool ShowQuantity { get; init; }
    public bool ShowPlatts { get; init; }
    public bool ShowPremiumOrDiscount { get; init; }
    public bool ShowUnitPrice { get; init; }

    public bool HasOperationalColumns => ShowQuantity || ShowPlatts || ShowPremiumOrDiscount || ShowUnitPrice;
    public bool UseLandscape => HasOperationalColumns || ShowRub || ShowAed || ShowOriginalAmount;
}

public sealed class PartyStatementAuthorization
{
    public string? AuthorizedByName { get; init; }
    public string? AuthorizedByTitle { get; init; }
    public string? SignatureImagePath { get; init; }
}

public sealed class PartyStatementCompanyInfo
{
    public string Name { get; init; } = Services.Exports.ExcelDesignSystem.OrganizationNameEn;
    public string Subtitle { get; init; } = "";
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Website { get; init; }
    public string LogoPath { get; init; } = Services.Exports.ExcelDesignSystem.LogoRelativePath;
}

public sealed class PartyStatementResult
{
    public required PartyRef Party { get; init; }
    public required PartyStatementPolicy Policy { get; init; }
    public required PartyStatementCompanyInfo CompanyInfo { get; init; }
    public required PartyStatementPartyInfo PartyInfo { get; init; }
    public required PartyStatementDocumentInfo DocumentInfo { get; init; }
    public required PartyStatementSummary Summary { get; init; }
    public required PartyStatementColumnOptions ColumnOptions { get; init; }
    public required IReadOnlyList<PartyStatementRow> Rows { get; init; }
    public string? Note { get; init; }
    public required PartyStatementAuthorization Authorization { get; init; }
    public string CourtesyText { get; init; } = "از همکاری دوامدار شما سپاس‌گزاریم.";
}

// نمای صورت‌حساب طرف‌حساب‌های قراردادی. پیش‌فرض «خلاصهٔ قراردادها» است؛ دو نمای دیگر
// جزئیات‌اند: Ledger = گردش فشرده (بارگیری/فروش هر قرارداد در یک سطر جمع می‌شود)،
// Loadings = تک‌تک اسناد. نام‌ها برای سازگاری routeهای موجود دست‌نخورده مانده‌اند.
public enum SupplierStatementView
{
    Contracts = 0,
    Ledger = 1,
    Loadings = 2
}

public sealed class PartyStatementViewModel
{
    public required PartyStatementResult Statement { get; init; }
    public required PartyStatementFilter Filter { get; init; }
    public bool IsPrintMode { get; init; }

    /// <summary>صورت‌حساب داخل تب پروفایل طرف‌حساب رندر شده، نه در صفحهٔ مستقل.</summary>
    public bool IsEmbedded { get; init; }
    public bool IsRtl { get; init; } = true;
    public PartyStatementPartyType PartyType => Statement.Party.PartyType;

    // گزینه‌های دراپ‌داون نوار فیلتر (قرارداد/شرکت/ارز).
    public IReadOnlyList<PartyStatementFilterOption> ContractOptions { get; init; } = [];
    public IReadOnlyList<PartyStatementFilterOption> CompanyOptions { get; init; } = [];
    public IReadOnlyList<string> CurrencyOptions { get; init; } = [];

    // فهرست طرف‌حساب‌های هم‌نوع برای جابه‌جایی بین صورت‌حساب‌ها (فعلاً فقط تأمین‌کننده).
    public IReadOnlyList<PartyStatementFilterOption> PartyOptions { get; init; } = [];

    // حالت نمایش برای طرف‌حساب‌های قراردادی؛ نام قدیمی برای سازگاری routeها حفظ شده است.
    public SupplierStatementView SupplierView { get; init; } = SupplierStatementView.Contracts;

    /// <summary>در این دوره دست‌کم یک سند به قرارداد وصل است.</summary>
    public bool HasContractRows { get; init; }

    /// <summary>
    /// شمار بارگیری‌های برگشت‌شدهٔ همین دوره، پیش از فشرده‌سازی. فقط برای توضیح زیر
    /// خلاصه است و در هیچ جمع یا بیلانسی وارد نمی‌شود.
    /// </summary>
    public int ReversedLoadingCount { get; init; }

    public bool ShowSupplierViewTabs => ShowContractViewTabs;

    // تب خلاصهٔ «قراردادها» فقط برای تأمین‌کننده و شریک، و فقط وقتی در این دوره دست‌کم یک
    // سند به قرارداد وصل باشد. بقیهٔ طرف‌حساب‌ها حتی با ContractId هم این تب را نمی‌بینند؛
    // مشتری، شرکت و شرکت خدماتی خلاصهٔ نوع سند دارند (HasExpenseSummary) و صراف، راننده و
    // کارمند همیشه گردش حساب می‌بینند.
    public bool ShowContractViewTabs => SupportsContractSummary(PartyType) && HasContractRows;

    // فقط تأمین‌کننده و شریک نمای «خلاصهٔ قراردادها» دارند. گروه‌بندی صرفاً نمایشی است
    // و روی هیچ مبلغ یا مانده‌ای اثر ندارد.
    public static bool SupportsContractSummary(PartyStatementPartyType partyType)
        => partyType is PartyStatementPartyType.Supplier
            or PartyStatementPartyType.Partner;

    // نمای پیش‌فرض: طرف‌حساب قراردادی → خلاصهٔ قراردادها، بقیه → گردش حساب.
    public static SupplierStatementView DefaultViewFor(PartyStatementPartyType partyType)
        => SupportsContractSummary(partyType)
            ? SupplierStatementView.Contracts
            : SupplierStatementView.Ledger;

    // نمای خلاصه: گروه‌بندی نمایشیِ همان سطرهای مالی؛ بدون محاسبهٔ مالی جدید.
    public SupplierContractStatementViewModel? ContractGrouping { get; init; }

    /// <summary>
    /// شرکت خدماتی، شرکت و مشتری: سطرهای این دوره در نمای «خلاصه» بر اساس نوع سند/مصرف جمع شده‌اند.
    /// گروه‌بندی صرفاً نمایشی است؛ جمع دوره و بیلانس تغییر نمی‌کند.
    /// </summary>
    public bool HasExpenseSummary { get; init; }

    // تب «خلاصه/جزئیات» برای شرکت خدماتی، مستقل از خلاصهٔ قراردادها.
    public bool ShowExpenseSummaryTabs => HasExpenseSummary;

    public bool ShowViewTabs => ShowContractViewTabs || ShowExpenseSummaryTabs;
}

// خلاصهٔ نمای «قراردادها» — گروه‌بندیِ نمایشی روی Rows موجود. جمع‌ها عیناً برابر نمای خطی‌اند.
public sealed class SupplierContractStatementViewModel
{
    public required IReadOnlyList<SupplierContractStatementRow> Rows { get; init; }
    public bool HasOpeningRow { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal? OpeningBalanceRub { get; init; }
    public bool IsRub { get; init; }
    public decimal TotalReceipt { get; init; }
    public decimal TotalOutflow { get; init; }
    public decimal ClosingBalance { get; init; }
    public decimal? TotalReceiptRub { get; init; }
    public decimal? TotalOutflowRub { get; init; }
    public decimal? ClosingBalanceRub { get; init; }
    public decimal TotalConfirmedValue { get; init; }
    public decimal TotalSettlement { get; init; }
    public decimal? TotalConfirmedValueRub { get; init; }
    public decimal? TotalSettlementRub { get; init; }
    public int TotalLoadingCount { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public int TotalRows => Rows.Count;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalRows / (decimal)Math.Max(1, PageSize)));
    public int CurrentPage => Math.Clamp(Page, 1, TotalPages);
    public IReadOnlyList<SupplierContractStatementRow> PagedRows =>
        Rows.Skip((CurrentPage - 1) * Math.Max(1, PageSize))
            .Take(Math.Max(1, PageSize))
            .ToList();
}

// مدلِ ردیف جزئیات (lazy): همان صورت‌حساب قرارداد + اطلاعات نمایشیِ قرارداد برای هدرِ جزئیات.
public sealed class SupplierContractDetailsViewModel
{
    public required PartyStatementResult Statement { get; init; }
    public PartyStatementPartyType PartyType { get; init; } = PartyStatementPartyType.Supplier;
    public int PartyId { get; init; }
    public int ContractId { get; init; }
    public string? ProductName { get; init; }
    public decimal? ContractQuantityMt { get; init; }
    public decimal? UnitPriceUsd { get; init; }
    public decimal? ContractValueUsd { get; init; }
    public decimal? LoadedQuantityMt { get; init; }
    public decimal? RemainingQuantityMt =>
        ContractQuantityMt.HasValue && LoadedQuantityMt.HasValue
            ? ContractQuantityMt.Value - LoadedQuantityMt.Value
            : null;
    public IReadOnlyList<PartyStatementRow> DetailRows { get; init; } = [];
    public IReadOnlyList<PartyStatementRow> LoadingRows { get; init; } = [];
    public int DetailPage { get; init; } = 1;
    public int DetailPageSize { get; init; } = 25;
    public int DetailTotalRows { get; init; }
    public int DetailTotalPages =>
        Math.Max(1, (int)Math.Ceiling(DetailTotalRows / (decimal)Math.Max(1, DetailPageSize)));
}

// یک سطر = یک قرارداد (یا گروهِ «بدون قرارداد»). Debit/Credit/Balance جمعِ سطرهای مالیِ همان قرارداد است.
public sealed class SupplierContractStatementRow
{
    public int Sequence { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public DateTime? FirstDate { get; init; }
    public DateTime? LastDate { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public decimal? ContractQuantityMt { get; init; }
    public decimal? UnitPriceUsd { get; init; }
    public decimal? ContractValueUsd { get; init; }
    public decimal? LoadedQuantityMt { get; init; }
    // فقط برای شریک پر می‌شود: درصد سهمِ همین شریک از این قرارداد. صرفاً برچسب است و
    // در هیچ جمعی وارد نمی‌شود؛ مبالغِ سطر از قبل سهم‌بندی‌شده‌اند.
    public decimal? SharePercent { get; init; }
    public decimal? RemainingQuantityMt =>
        ContractQuantityMt.HasValue && LoadedQuantityMt.HasValue
            ? ContractQuantityMt.Value - LoadedQuantityMt.Value
            : null;
    public decimal ConfirmedValue { get; init; }
    public decimal SettlementTotal { get; init; }
    public decimal? ConfirmedValueRub { get; init; }
    public decimal? SettlementTotalRub { get; init; }
    public int LoadingCount { get; init; }
    // فقط نمایشی: سندهای بارگیری/فروشِ این قرارداد که هنوز فعال‌اند و آن‌هایی که برگشت شده‌اند.
    public int ActiveLoadingCount { get; init; }
    public int ReversedLoadingCount { get; init; }
    public string OperationLabel { get; init; } = "بارگیری";
    public decimal Receipt { get; init; }
    public decimal Outflow { get; init; }
    public decimal Balance { get; init; }
    public decimal? ReceiptRub { get; init; }
    public decimal? OutflowRub { get; init; }
    public decimal? BalanceRub { get; init; }

    // نمایش یکسانِ مانده در همهٔ صفحات: عدد بدون علامت + عنوانِ معنا.
    // Balance اینجا «برد − رسید» است، یعنی پرداخت منهای ارزش قرارداد؛ مثبت = اضافه‌پرداخت.
    public decimal? BalanceFor(bool isRub) => isRub ? BalanceRub : Balance;

    public decimal? BalanceAbsoluteFor(bool isRub)
        => ContractBalanceText.Absolute(BalanceFor(isRub));

    public string? BalanceTitleFor(bool isRub, bool isEnglish = false)
        => ContractBalanceText.Title(BalanceFor(isRub), isEnglish, hasContract: ContractId.HasValue);
}

public sealed record PartyStatementFilterOption(int Id, string Text);

public sealed class PartyStatementOptions
{
    public const string SectionName = "PartyStatements";

    public string CompanyName { get; set; } = Services.Exports.ExcelDesignSystem.OrganizationNameEn;
    public string CompanySubtitle { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string LogoPath { get; set; } = Services.Exports.ExcelDesignSystem.LogoRelativePath;
    public string CourtesyText { get; set; } = "از همکاری دوامدار شما سپاس‌گزاریم.";
    public string Note { get; set; } = "لطفاً صورت‌حساب را بررسی کرده و هرگونه مغایرت را با بخش مالی در میان بگذارید.";
    public string? AuthorizedByName { get; set; }
    public string? AuthorizedByTitle { get; set; }
    public string? SignatureImagePath { get; set; }
    public string BaseCurrencyCode { get; set; } = "USD";
}

public static class PartyStatementFormatting
{
    public static string? FxDisplay(decimal? basePerTransactionCurrency, string? currency)
    {
        if (!basePerTransactionCurrency.HasValue
            || basePerTransactionCurrency.Value <= 0m
            || string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var perUsd = 1m / basePerTransactionCurrency.Value;
        return $"1 USD = {perUsd.ToString("0.####", CultureInfo.InvariantCulture)} {currency?.ToUpperInvariant()}";
    }

    // ── کوتاه‌سازی متن‌های صورت‌حساب رسمی ────────────────────────────────────────────
    // شرح/مرجعِ ذخیره‌شده در Ledger برای ردیابی ساخته شده و دنبالهٔ ماشینی دارد
    // (GroupKey / Contract / Leg / Quantity / Share / …). در سند رسمی فقط متنِ انسانی
    // لازم است. این کوتاه‌سازی فقط نمایشی است؛ دادهٔ Ledger دست‌نخورده می‌ماند.
    public const int DescriptionMaxLength = 60;
    public const int ReferenceMaxLength = 24;

    private static readonly string[] MachineSegmentPrefixes =
    [
        "GroupKey:",
        "Transport group:",
        "Original total:",
        "Original total USD:",
        "Total USD:",
        "Contract:",
        "Leg:",
        "Quantity:",
        "Share:",
        "Qty=",
        "Rate=",
        "Base=",
        "Percent=",
        "Flat=",
        "RuleAmount=",
        "EXP-"
    ];

    /// <summary>طول شرح در خروجی Excel/PDF — کوتاه‌تر از صفحه، چون خانهٔ جدول عرض ثابت دارد.</summary>
    public const int ExportDescriptionMaxLength = 42;

    /// <summary>شرح کوتاهِ سطر: بخش‌های ماشینی حذف و طول محدود می‌شود.</summary>
    public static string ShortDescription(string? text)
        => ShortDescription(text, DescriptionMaxLength);

    /// <summary>همان کوتاه‌سازی، با سقف طولِ دلخواه (خروجی‌ها سقف کوتاه‌تری دارند).</summary>
    public static string ShortDescription(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var kept = text
            .Split('|')
            .Select(part => CollapseWhitespace(part))
            .Where(part => part.Length > 0 && !IsMachineSegment(part))
            .ToList();

        var joined = kept.Count == 0 ? CollapseWhitespace(text) : string.Join(" – ", kept);
        return Truncate(joined, maxLength);
    }

    /// <summary>
    /// شرح سند رسمی: بخش‌های ماشینی حذف می‌شوند ولی متنِ انسانی هرگز با «…» بریده نمی‌شود؛
    /// خانهٔ جدول در صفحه و PDF چندخطی می‌شود.
    /// </summary>
    public static string CleanDescription(string? text)
        => ShortDescription(text, int.MaxValue);

    /// <summary>مرجع کوتاهِ سطر: فقط کلید سند، بدون دنبالهٔ شرح.</summary>
    public static string? ShortReference(string? text)
        => CleanReference(text, ReferenceMaxLength);

    /// <summary>کلید سند بدون دنبالهٔ شرح و بدون بریدن — برای سند رسمی.</summary>
    public static string? CleanReference(string? text)
        => CleanReference(text, int.MaxValue);

    private static string? CleanReference(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var head = CollapseWhitespace(text.Split('|')[0]);
        return Truncate(head.Length == 0 ? CollapseWhitespace(text) : head, maxLength);
    }

    private static bool IsMachineSegment(string segment)
        => MachineSegmentPrefixes.Any(prefix => segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string CollapseWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)].TrimEnd() + "…";
}
