using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.Custody;

/// <summary>
/// نرخِ به‌کاررفته در یک سند امانی. <see cref="PerUsd"/> همان چیزی است که ثبت/توافق شده
/// (۱ دالر = چند واحد) و <see cref="ToUsd"/> ضریبِ محاسبهٔ دالر — هر دو جدا نگه داشته می‌شوند
/// (<see cref="FxRateMath"/>).
/// </summary>
public sealed record CustodyRate(string Currency, decimal PerUsd, decimal ToUsd, DateTime? RateDate, string Source)
{
    public static readonly CustodyRate Usd = new(SystemCurrency.BaseCurrencyCode, 1m, 1m, null, "USD");
    public bool IsUsd => SystemCurrency.IsBaseCurrency(Currency);

    /// <summary>ارزشِ دالری با همان قاعدهٔ پرداخت‌ها و ژورنال: مبلغ × ضریب، چهار رقم.</summary>
    public decimal ToUsdAmount(decimal amount)
        => IsUsd ? amount : decimal.Round(amount * ToUsd, 4, MidpointRounding.AwayFromZero);
}

/// <summary>
/// قواعد ارزیِ امانی: فقط USD و AFN. نرخ یا از جدول نرخ روزانهٔ سیستم (همان جدولی که پرداخت‌ها و
/// بیلانس می‌خوانند، به هر دو جهت) یا نرخ توافقیِ تأییدشدهٔ همان معامله با مرجع؛ نرخی حدس زده نمی‌شود.
/// </summary>
public static class CustodyFx
{
    public const string Afn = "AFN";
    public static readonly string[] SupportedCurrencies = [SystemCurrency.BaseCurrencyCode, Afn];

    public static string Normalize(string? currency)
    {
        var code = SystemCurrency.Normalize(currency);
        if (!SupportedCurrencies.Contains(code))
            throw new BusinessRuleException("CUSTODY_CURRENCY", "اسناد امانی فقط به دالر (USD) یا افغانی (AFN) ثبت می‌شوند.");
        return code;
    }

    public static async Task<CustodyRate> ResolveAsync(
        ApplicationDbContext db,
        string currency,
        DateTime date,
        decimal? agreedPerUsd,
        string? agreedReference,
        CancellationToken ct)
    {
        var code = Normalize(currency);
        if (SystemCurrency.IsBaseCurrency(code))
            return CustodyRate.Usd;

        if (agreedPerUsd.HasValue)
        {
            if (agreedPerUsd.Value <= 0m)
                throw new BusinessRuleException("CUSTODY_FX_RATE", "نرخ توافقی باید بیشتر از صفر باشد.");
            if (string.IsNullOrWhiteSpace(agreedReference))
                throw new BusinessRuleException("CUSTODY_FX_AGREED_REFERENCE", "برای نرخ توافقی، مرجعِ تأییدِ همان معامله الزامی است.");
            var rate = FxRateMath.RoundRate(agreedPerUsd.Value);
            return new CustodyRate(code, rate, FxRateMath.ToUsdFromPerUsd(rate), date.Date, "نرخ توافقی: " + agreedReference.Trim());
        }

        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var direct = await db.DailyFxRates.AsNoTracking()
            .Where(r => r.BaseCurrency == SystemCurrency.BaseCurrencyCode && r.QuoteCurrency == code && r.RateDate <= day && r.Rate > 0m)
            .OrderByDescending(r => r.RateDate).ThenByDescending(r => r.Id)
            .Select(r => new { r.Rate, r.RateDate })
            .FirstOrDefaultAsync(ct);
        var inverse = await db.DailyFxRates.AsNoTracking()
            .Where(r => r.BaseCurrency == code && r.QuoteCurrency == SystemCurrency.BaseCurrencyCode && r.RateDate <= day && r.Rate > 0m)
            .OrderByDescending(r => r.RateDate).ThenByDescending(r => r.Id)
            .Select(r => new { r.Rate, r.RateDate })
            .FirstOrDefaultAsync(ct);
        // آخرین نرخِ ثبت‌شده تا همان تاریخ؛ اگر هر دو جهت ثبت شده، تازه‌ترین (و در تساوی، جهتِ مستقیم).
        if (direct is not null && (inverse is null || direct.RateDate >= inverse.RateDate))
        {
            var perUsd = FxRateMath.RoundRate(direct.Rate);
            return new CustodyRate(code, perUsd, FxRateMath.ToUsdFromPerUsd(perUsd), direct.RateDate.Date,
                $"نرخ روزانهٔ سیستم USD/{code} {direct.RateDate:yyyy-MM-dd}");
        }
        if (inverse is not null)
        {
            var toUsd = FxRateMath.RoundRate(inverse.Rate);
            return new CustodyRate(code, FxRateMath.PerUsdFromToUsd(toUsd), toUsd, inverse.RateDate.Date,
                $"نرخ روزانهٔ سیستم {code}/USD {inverse.RateDate:yyyy-MM-dd}");
        }
        throw new BusinessRuleException("CUSTODY_FX_RATE_MISSING",
            $"نرخ {code} تا تاریخ {date:yyyy-MM-dd} در «نرخ روزانهٔ ارز» ثبت نشده است؛ نرخ حدس زده نمی‌شود. نرخ رسمی را ثبت کنید یا نرخ توافقیِ تأییدشده را با مرجع وارد کنید.");
    }

    /// <summary>تبدیلِ USD↔AFN با نرخِ «افغانی در برابر ۱ دالر»؛ چهار رقم.</summary>
    public static decimal Convert(decimal amount, string from, string to, decimal afnPerUsd)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return amount;
        if (afnPerUsd <= 0m) throw new BusinessRuleException("CUSTODY_FX_RATE", "نرخ تبدیل معتبر نیست.");
        return SystemCurrency.IsBaseCurrency(from)
            ? decimal.Round(amount * afnPerUsd, 4, MidpointRounding.AwayFromZero)
            : decimal.Round(amount / afnPerUsd, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// ارزشِ دالریِ بخشی از طلبِ مشتری با نرخِ خودِ فروش. طلب مشتری و «در انتظار وصول» (۱۲۱۰) هر دو
    /// با همین ارزش می‌نشینند و برمی‌گردند، پس هیچ‌کدام تفاوت نرخ نمی‌سازند.
    /// </summary>
    public static decimal AtSaleRate(CustodySale sale, decimal amount)
        => SystemCurrency.IsBaseCurrency(sale.Currency) ? amount
            : decimal.Round(amount * sale.EffectiveRateToUsd, 4, MidpointRounding.AwayFromZero);

    /// <summary>سهم مالک از تفاوت نرخ (۰ تا ۱)؛ null یعنی توافق ثبت نشده.</summary>
    public static decimal? OwnerShare(CustodySale sale) => sale.FxResponsibility switch
    {
        CustodyFxResponsibility.Owner => 1m,
        CustodyFxResponsibility.Company => 0m,
        CustodyFxResponsibility.Split => (sale.FxOwnerSharePercent ?? 0m) / 100m,
        _ => null
    };

    public static string Label(CustodyFxResponsibility? responsibility, decimal? ownerSharePercent) => responsibility switch
    {
        CustodyFxResponsibility.Owner => "مالک",
        CustodyFxResponsibility.Company => "شرکت",
        CustodyFxResponsibility.Split => $"تقسیم: {ownerSharePercent:0.##}٪ مالک، {100m - (ownerSharePercent ?? 0m):0.##}٪ شرکت",
        _ => "تعیین نشده"
    };
}
