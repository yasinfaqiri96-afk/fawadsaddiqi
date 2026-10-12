using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

// فروش با قیمت باز — قاعدهٔ مشترکِ همهٔ مسیرهای ثبت فروش (فروش تکی، از محموله، گروهی، موتر،
// رسید بارگیری و رسید حمل). هر مسیر فروش را مثل همیشه از ورودی‌هایش می‌سازد؛ برای قیمت باز:
//   ۱) به‌جای تبدیل ارزِ واقعی NeutralConversion را می‌گیرد (نرخی ذخیره نمی‌شود)،
//   ۲) پس از ساختن فروش MarkPending را صدا می‌زند،
//   ۳) سطر دفتر (SaleLedgerFactory) را فقط وقتی می‌سازد که ShouldPostLedger راست باشد.
// ژورنال‌ها نیازی به شرط ندارند: آداپتر حسابداری خودش عاید را برای قیمت باز Skip و بهای کالای
// تحویل‌شده را به حساب واسط می‌برد. قیمت بعداً فقط از SalesController.SetPrice ثبت می‌شود.
public static class SalePendingPricing
{
    public const string NeutralConversionSource = "Sale price pending";

    /// <summary>تبدیلِ بی‌اثر برای ساختن فروش بدون قیمت. هیچ‌کدام از اعدادش ذخیره یا خوانده نمی‌شود.</summary>
    public static CurrencyConversionResult NeutralConversion(string? currency, DateTime saleDate)
        => new(
            SourceCurrencyCode: SystemCurrency.Normalize(currency),
            BaseCurrencyCode: SystemCurrency.BaseCurrencyCode,
            AppliedRateToBase: 1m,
            EffectiveDate: saleDate.Date,
            FallbackApplied: false,
            ManualOverride: false,
            SourceDescription: NeutralConversionSource);

    /// <summary>
    /// ستون‌های مبلغ صفر و پرچم «در انتظار قیمت» روشن می‌شود. صفر این‌جا «مبلغ» نیست؛ هر خواننده‌ای
    /// پیش از خواندن مبلغ IsSalePricePending را می‌بیند.
    /// </summary>
    public static void MarkPending(SalesTransaction sale, string? currency)
    {
        ArgumentNullException.ThrowIfNull(sale);
        sale.IsSalePricePending = true;
        sale.Currency = SystemCurrency.Normalize(currency);
        sale.UnitPriceInCurrency = 0m;
        sale.UnitPriceUsd = 0m;
        sale.TotalInCurrency = 0m;
        sale.TotalUsd = 0m;
        sale.AppliedFxRateToUsd = null;
    }

    /// <summary>فروش با قیمت باز هیچ سطر دفتری (طلب مشتری/عاید) ندارد.</summary>
    public static bool ShouldPostLedger(SalesTransaction sale) => !sale.IsSalePricePending;

    /// <summary>دلیلی که آداپتر حسابداری برای عایدِ فروش قیمت‌باز برمی‌گرداند؛ کمبود نیست، رفتار عمدی است.</summary>
    public const string PendingSkipReason = "SALE_PRICE_PENDING";
}
