using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services;

// ساختِ مشترکِ ردیفِ لجرِ «فروش» از روی یک SalesTransaction.
// این پنج مسیر فروش (فروش عادی، فروش محموله، فروش از دیسپچ مستقیم، رسید انتقال DirectSale،
// رسید کشتی DirectSale) همگی دقیقاً همین ردیف لجر را می‌ساختند و تنها ContractId بینشان فرق داشت.
// هیچ عدد/رفتاری تغییر نمی‌کند؛ فقط ساختِ تکراری یکجا شده و ContractId پارامتر است.
// نکته: خودِ SalesTransaction عمداً اینجا ساخته نمی‌شود؛ فیلدهایش بین مسیرها واقعاً متفاوت است.
public static class SaleLedgerFactory
{
    // توضیحِ استانداردِ ردیفِ لجرِ فروش (همان رشته‌ای که در همهٔ مسیرها استفاده می‌شد).
    public static string BuildDescription(SalesTransaction sale)
        => $"ثبت فروش {SaleStageLabels.ToPersian(sale.SaleStage)} فاکتور {sale.InvoiceNumber}"
           + (sale.SalePriceAccountingDate.HasValue && sale.SalePriceAccountingDate.Value.Date != sale.SaleDate.Date
               ? $" | تحویل {sale.SaleDate:yyyy-MM-dd}، قیمت‌گذاری در دورهٔ باز"
               : string.Empty);

    public static LedgerPostingRequest BuildSaleLedgerEntry(
        SalesTransaction sale,
        CurrencyConversionResult conversion,
        int? contractId)
        => new()
        {
            // فروشی که بعداً قیمت گرفته تاریخ حسابداریِ خودش را دارد؛ فروش عادی همان تاریخ فروش.
            EntryDate = sale.SalePriceAccountingDate ?? sale.SaleDate,
            Side = LedgerSide.Credit,
            AmountUsd = sale.TotalUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = sale.TotalInCurrency,
            SourceCurrencyCode = sale.Currency,
            AppliedFxRateToUsd = sale.AppliedFxRateToUsd,
            AppliedFxRateDate = conversion.EffectiveDate.Date,
            AppliedFxRateSource = conversion.SourceDescription,
            Description = BuildDescription(sale),
            SourceType = "Sale",
            SourceId = sale.Id,
            Reference = sale.InvoiceNumber,
            ContractId = contractId,
            CustomerId = sale.CustomerId,
            // فروش به تأمین‌کننده: همان سطر فروش به حساب تأمین‌کننده می‌نشیند و طلبش را کم می‌کند.
            SupplierId = sale.SupplierId,
            ShipmentId = sale.ShipmentId
        };
}
