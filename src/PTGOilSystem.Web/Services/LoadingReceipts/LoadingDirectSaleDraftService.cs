using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.LoadingReceipts;

public sealed record LoadingDirectSaleDraft(SalesTransaction Sale, CurrencyConversionResult Conversion);

// Extracted from LoadingReceiptsController: the single and group paths share the same
// historical conversion and sale construction, while retaining the receipt/allocation trace.
public sealed class LoadingDirectSaleDraftService(ICurrencyConversionService currencyConversion)
{
    public async Task<LoadingDirectSaleDraft> BuildAsync(LoadingReceiptAllocationLineInput line, LoadingRegister loading)
    {
        if (loading.Contract is null)
            throw new BusinessRuleException("DIRECT_SALE_SOURCE_CONTRACT_REQUIRED", "قرارداد خرید منبع باید مشخص باشد.");
        // قیمت باز: نرخ ارز با خودِ قیمت و در «تعیین قیمت فروش» گرفته می‌شود.
        var conversion = line.SaleIsPricePending
            ? SalePendingPricing.NeutralConversion(line.SaleCurrency, line.SaleDate!.Value)
            : await currencyConversion.ResolveToBaseAsync(line.SaleCurrency,
                line.SaleDate!.Value.Date, line.SaleAppliedFxRateToUsd);
        return Build(line, loading, conversion);
    }

    public static LoadingDirectSaleDraft Build(LoadingReceiptAllocationLineInput line,
        LoadingRegister loading, CurrencyConversionResult conversion)
    {
        if (loading.Contract is null)
            throw new BusinessRuleException("DIRECT_SALE_SOURCE_CONTRACT_REQUIRED", "قرارداد خرید منبع باید مشخص باشد.");
        var unitPrice = line.SaleIsPricePending ? 0m : line.SaleUnitPriceInCurrency!.Value;
        var totalInCurrency = decimal.Round(line.QuantityMt * unitPrice,
            4, MidpointRounding.AwayFromZero);
        var draft = new LoadingDirectSaleDraft(new SalesTransaction
        {
            ContractId = null, CompanyId = loading.Contract.CompanyId,
            SourcePurchaseContractId = loading.ContractId,
            CustomerId = line.SaleCustomerId, SupplierId = line.SaleSupplierId,
            ProductId = loading.ProductId, DestinationLocationId = line.DestinationLocationId,
            ShipmentId = null, SaleStage = SaleStage.InTransit,
            InvoiceNumber = line.SaleInvoiceNumber!, SaleDate = line.SaleDate!.Value.Date,
            QuantityMt = line.QuantityMt, Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = unitPrice,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(unitPrice),
            TotalInCurrency = totalInCurrency, TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = line.SaleNotes
        }, conversion);
        if (line.SaleIsPricePending)
            SalePendingPricing.MarkPending(draft.Sale, line.SaleCurrency);
        return draft;
    }
}
