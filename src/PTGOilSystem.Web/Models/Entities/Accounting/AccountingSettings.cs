using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

public class AccountingSettings : BaseEntity
{
    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    [Required, MaxLength(10)]
    public string FunctionalCurrencyCode { get; set; } = "USD";

    public int CashBankControlAccountId { get; set; }
    public Account? CashBankControlAccount { get; set; }
    public int AccountsReceivableAccountId { get; set; }
    public Account? AccountsReceivableAccount { get; set; }
    public int AccountsPayableAccountId { get; set; }
    public Account? AccountsPayableAccount { get; set; }
    public int InventoryAccountId { get; set; }
    public Account? InventoryAccount { get; set; }
    public int InventoryInTransitAccountId { get; set; }
    public Account? InventoryInTransitAccount { get; set; }
    public int SupplierPrepaymentAccountId { get; set; }
    public Account? SupplierPrepaymentAccount { get; set; }
    public int CustomerAdvanceAccountId { get; set; }
    public Account? CustomerAdvanceAccount { get; set; }
    public int FreightPayableAccountId { get; set; }
    public Account? FreightPayableAccount { get; set; }
    public int CommissionPayableAccountId { get; set; }
    public Account? CommissionPayableAccount { get; set; }
    public int EmployeeAdvanceAccountId { get; set; }
    public Account? EmployeeAdvanceAccount { get; set; }
    public int EmployeePayableAccountId { get; set; }
    public Account? EmployeePayableAccount { get; set; }
    public int AccruedExpenseAccountId { get; set; }
    public Account? AccruedExpenseAccount { get; set; }
    public int SalesRevenueAccountId { get; set; }
    public Account? SalesRevenueAccount { get; set; }
    public int CostOfGoodsSoldAccountId { get; set; }
    public Account? CostOfGoodsSoldAccount { get; set; }
    public int GeneralExpenseAccountId { get; set; }
    public Account? GeneralExpenseAccount { get; set; }
    public int ExchangeGainAccountId { get; set; }
    public Account? ExchangeGainAccount { get; set; }
    public int ExchangeLossAccountId { get; set; }
    public Account? ExchangeLossAccount { get; set; }
    public int InventoryLossAccountId { get; set; }
    public Account? InventoryLossAccount { get; set; }
    public int CurrentYearProfitLossAccountId { get; set; }
    public Account? CurrentYearProfitLossAccount { get; set; }
    public int RetainedEarningsAccountId { get; set; }
    public Account? RetainedEarningsAccount { get; set; }

    /// <summary>
    /// حساب کنترلیِ جاریِ شرکا (۳۳۰۰). یک حساب برای همهٔ شرکا؛ هویتِ شریک از
    /// <see cref="JournalEntryLine.PartyType"/> = Partner و <see cref="JournalEntryLine.PartyId"/>
    /// می‌آید، نه از حسابِ جداگانه به‌ازای هر شریک.
    ///
    /// Nullable است چون شرکت‌هایی که تنظیماتشان پیش از این حساب ساخته شده بود آن را ندارند و
    /// Adapterها در آن حالت با دلیلِ صریح Skip می‌کنند به‌جای اینکه سند نادرست بسازند.
    /// </summary>
    public int? PartnerCurrentAccountId { get; set; }
    public Account? PartnerCurrentAccount { get; set; }

    public int? FixedAssetAccountId { get; set; }
    public Account? FixedAssetAccount { get; set; }
    public int? AccumulatedDepreciationAccountId { get; set; }
    public Account? AccumulatedDepreciationAccount { get; set; }
    public int? DepreciationExpenseAccountId { get; set; }
    public Account? DepreciationExpenseAccount { get; set; }
    public int? AssetRentalRevenueAccountId { get; set; }
    public Account? AssetRentalRevenueAccount { get; set; }
    public int? InternalAssetRecoveryAccountId { get; set; }
    public Account? InternalAssetRecoveryAccount { get; set; }
    public int? AssetOperatingExpenseAccountId { get; set; }
    public Account? AssetOperatingExpenseAccount { get; set; }

    /// <summary>
    /// مصرف معاش (۵۷۰۰) — بدهکارِ سندِ ثبتِ معاش در برابرِ «بدهی به کارمند». Nullable مثل
    /// <see cref="PartnerCurrentAccountId"/>: تا تعیین نشده، سندِ معاش با دلیلِ صریح Skip می‌شود.
    /// </summary>
    public int? SalaryExpenseAccountId { get; set; }
    public Account? SalaryExpenseAccount { get; set; }

    // امانی — Nullable مثل بقیهٔ حساب‌های بعداً افزوده‌شده: تا تعیین نشده، سند ژورنالِ امانی با
    // دلیلِ صریح Skip می‌شود و دفتر عملیاتی (Ledger/روزنامچه) کامل کار می‌کند.
    /// <summary>وجوه امانی پرداختنی به مالکان (۲۶۰۰) — مالک روی سطر سند.</summary>
    public int? CustodyFundsPayableAccountId { get; set; }
    public Account? CustodyFundsPayableAccount { get; set; }
    /// <summary>طلب امانی به نفع مالکان (۱۲۱۰، کاهندهٔ دریافتنی) — تا وصول تعهدِ بالفعل به مالک نیست.</summary>
    public int? CustodyPendingCollectionAccountId { get; set; }
    public Account? CustodyPendingCollectionAccount { get; set; }
    /// <summary>درآمد کمیشن امانی (۴۵۰۰).</summary>
    public int? CustodyCommissionRevenueAccountId { get; set; }
    public Account? CustodyCommissionRevenueAccount { get; set; }
    /// <summary>درآمد خدمات امانی — حق مخزن، بارگیری و مانند آن (۴۵۱۰).</summary>
    public int? CustodyServiceRevenueAccountId { get; set; }
    public Account? CustodyServiceRevenueAccount { get; set; }

    /// <summary>
    /// کالای تحویل‌شده در انتظار قیمت فروش (۱۳۲۰). بهای کالایی که با قیمت باز تحویل شده هنگامِ
    /// تحویل از موجودی (۱۳۰۰) به این حساب می‌آید و هنگامِ تعیین قیمت، با همان مبلغ، بهای تمام‌شده
    /// (۵۱۰۰) می‌شود؛ پس بهای فروش به میانگینِ روزِ تحویل قفل است، نه روزِ قیمت‌گذاری.
    /// </summary>
    public int? DeliveredPendingPricingAccountId { get; set; }
    public Account? DeliveredPendingPricingAccount { get; set; }

    public uint RowVersion { get; set; }
}
