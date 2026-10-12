using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

// منطق واحدِ ثبت رسید انتقال از موجودی برای یک تخصیص/leg.
// هم کنترلر تک‌تخصیص (InventoryTransportReceipts/Create) و هم workflow گروهی (InventoryTransportLegs)
// از همین سرویس استفاده می‌کنند تا هیچ منطق مالی موازی ساخته نشود.
// عملیات گروهی تراکنش فراخوان را حفظ می‌کند؛ فراخوان تکی بدون تراکنش یک مرز مستقل و اتمیک می‌گیرد.
public sealed class InventoryTransportReceiptService
{
    public const string ReceiptFreightExpenseCode = "TRANSPORT-RECEIPT-FREIGHT";
    public const string OperationalAssetFreightIncomeDescriptionPrefix = "Freight income for operational asset — ";

    // نوع مصرف استاندارد «کرایه حمل» که از مودال «ثبت مصرف» انتخاب می‌شود.
    // وقتی کرایه با این نوع ثبت شده باشد، فرم رسید همان مبلغ را به‌عنوان «کرایه نهایی»
    // می‌خواند و کرایهٔ جداگانه روی رسید ذخیره نمی‌کند تا در P&L دوبار شمرده نشود.
    public const string TransportFreightExpenseCode = "TRANSPORT-FREIGHT";

    private readonly ApplicationDbContext _db;
    private readonly Dictionary<int, decimal> _validatedRemaining = new();

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);
    private IExpenseLedgerPoster? _expenseLedgerPoster;
    private IExpenseLedgerPoster ExpenseLedger => _expenseLedgerPoster ??= new ExpenseLedgerPoster(Ledger);
    // PTG-P1-04 — قاعدهٔ «هر مصرف دقیقاً یک هویت تسویه دارد». بی‌حالت است، پس ساختِ محلی بی‌خطر است.
    private readonly IExpenseSettlementValidator _settlementValidator = new ExpenseSettlementValidator();
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IInventoryLineageWriter _lineage;
    private readonly IInventoryMovementWriter _movements;
    private readonly ITransportQuantityService _quantities;
    private readonly ITransportSourceAllocationService _sourceAllocations;

    // writer اختیاری: call siteهای موجود بدون تغییر می‌مانند و در نبودِ آن writerِ خاموش (no-op) استفاده می‌شود.
    // مراحل ۵، ۷ و ۸ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Accounting.IExpenseAccountingAdapter? _expenseAccounting;
    private readonly Accounting.ISalesAccountingAdapter? _salesAccounting;
    private readonly Accounting.IShortageChargeAccountingAdapter? _shortageAccounting;
    private readonly Accounting.IInventoryTransferAccountingAdapter? _transferAccounting;
    private readonly Accounting.IPurchaseAccountingAdapter? _purchaseAccounting;

    public InventoryTransportReceiptService(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IInventoryLineageWriter? lineage = null,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        Accounting.ISalesAccountingAdapter? salesAccounting = null,
        Accounting.IShortageChargeAccountingAdapter? shortageAccounting = null,
        Accounting.IInventoryTransferAccountingAdapter? transferAccounting = null,
        Accounting.IPurchaseAccountingAdapter? purchaseAccounting = null,
        IInventoryMovementWriter? movements = null,
        ITransportQuantityService? quantities = null,
        ITransportSourceAllocationService? sourceAllocations = null)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _lineage = lineage ?? InventoryLineageWriterFactory.Disabled(db);
        _movements = movements ?? new InventoryMovementWriter(db, new StockService(db));
        _quantities = quantities ?? new TransportQuantityService(db);
        _sourceAllocations = sourceAllocations ?? new TransportSourceAllocationService(db);
        _expenseAccounting = expenseAccounting;
        _salesAccounting = salesAccounting;
        _shortageAccounting = shortageAccounting;
        _transferAccounting = transferAccounting;
        _purchaseAccounting = purchaseAccounting;
    }

    public async Task<InventoryTransportLeg?> LoadLegAsync(int id, bool tracking)
    {
        var query = _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Include(l => l.DestinationLocation)
            .Where(l => l.Id == id);

        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync();
    }

    // اعتبارسنجی کامل یک رسید. خطاها با کلیدِ پیشوندی (برای فرم گروهی) به ModelState اضافه می‌شوند.
    public async Task ValidateAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        ModelStateDictionary modelState,
        string keyPrefix = "")
    {
        NormalizeReceiptQuantities(model);
        // باقیمانده حمل = مقدار کل منهای مجموع رسیدهای قبلی (دریافت + کسری). چند رسید جزئی مجاز است تا باقیمانده صفر شود.
        var remainingMt = await GetRemainingQuantityAsync(leg);
        _validatedRemaining[leg.Id] = remainingMt;

        NormalizeTruckReceiptFields(model, leg, remainingMt);

        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);

        if (leg.Status is not (InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit))
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "فقط حمل‌های Loaded یا InTransit می‌توانند رسید مقصد بگیرند.");
        }

        if (remainingMt <= 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "این حمل کاملاً تخلیه شده است؛ باقیمانده‌ای برای رسید جدید وجود ندارد.");
        }

        if (model.ReceiptDate == default)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceiptDate), "Receipt date is required.");
        }

        if (!model.SettlementOnly && model.ReceivedQuantityMt <= 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceivedQuantityMt), "Received quantity must be greater than zero.");
        }

        // وزن واقعی مقصد می‌تواند بیشتر باشد؛ مازاد باید با کسری منفی ثبت شود،
        // بنابراین مصرف واقعی مبدأ (دریافت + کسری) از مانده تجاوز نمی‌کند.
        if (decimal.Round(model.ReceivedQuantityMt + model.ShortageQuantityMt, 4, MidpointRounding.AwayFromZero) > remainingMt)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceivedQuantityMt),
                "مجموع دریافت و کسری از ماندهٔ قابل دریافت بیشتر است؛ اضافه‌وزن ترازو را جدا ثبت کنید.");
        }
        if (model.ExpectedRemainingMt.HasValue && model.ExpectedRemainingMt.Value != remainingMt)
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId),
                "ماندهٔ این بار تغییر کرده است؛ صفحه را تازه و مقدار را دوباره بررسی کنید.");
        }

        if (UsesUnloadFreightFlow(leg))
        {
            ValidateTruckReceiptFields(model, leg, remainingMt, modelState, keyPrefix);
            await ValidateFreightAsync(model, leg, modelState, keyPrefix);
        }

        await ValidateOperationalPartyAsync(model.ServiceProviderId, model.OperationalAssetId, modelState, keyPrefix);

        // «فقط تسویه»: تخلیه‌ای در کار نیست، پس مقصد/مخزن لازم نیست و اعتبارسنجی مقصد رد می‌شود.
        if (model.SettlementOnly)
        {
            return;
        }

        if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale)
        {
            await ValidateDirectSaleAsync(model, leg, modelState, keyPrefix);
            return;
        }

        if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch)
        {
            await ValidateDirectDispatchAsync(model, leg, modelState, keyPrefix);
            return;
        }

        if (model.ReceiptDestination != InventoryTransportReceiptDestination.ToInventory)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceiptDestination), "DirectDispatch و Mixed برای رسید انتقال از موجودی در این فاز فعال نیستند.");
            return;
        }

        if (!model.DestinationTerminalId.HasValue)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DestinationTerminalId), "Destination terminal is required for ToInventory receipt.");
            return;
        }

        var terminalExists = await _db.Terminals.AsNoTracking()
            .AnyAsync(t => t.Id == model.DestinationTerminalId.Value);
        if (!terminalExists)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DestinationTerminalId), "Destination terminal is invalid.");
        }

        if (model.DestinationStorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.DestinationStorageTankId.Value);
            if (tank is null)
            {
                modelState.AddModelError(keyPrefix + nameof(model.DestinationStorageTankId), "Destination tank is invalid.");
            }
            else if (tank.TerminalId != model.DestinationTerminalId)
            {
                modelState.AddModelError(keyPrefix + nameof(model.DestinationStorageTankId), "Destination tank must belong to destination terminal.");
            }
        }
    }

    // FX فروش مستقیم را resolve می‌کند؛ در صورت خطا به ModelState اضافه می‌کند و null برمی‌گرداند.
    public async Task<CurrencyConversionResult?> ResolveSaleConversionAsync(
        InventoryTransportReceiptCreateViewModel model,
        ModelStateDictionary modelState,
        string keyPrefix = "")
    {
        if (model.ReceiptDestination != InventoryTransportReceiptDestination.DirectSale)
        {
            return null;
        }

        if (model.SaleIsPricePending)
        {
            return SalePendingPricing.NeutralConversion(model.SaleCurrency, model.SaleDate!.Value);
        }

        try
        {
            return await _currencyConversion.ResolveToBaseAsync(
                model.SaleCurrency,
                model.SaleDate!.Value.Date,
                model.SaleAppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleAppliedFxRateToUsd), ex.Message);
            return null;
        }
    }

    // ساختِ تمام رکوردهای یک رسید (رسید، کرایه، کسری، حرکت موجودی/فروش/دیسپچ، وضعیت leg).
    // تراکنش موجود را حفظ می‌کند یا برای فراخوان مستقل تراکنش می‌سازد. leg باید tracked باشد.
    public async Task<InventoryTransportReceipt> ApplyAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult? saleConversion)
    {
        NormalizeReceiptQuantities(model);
        // Callers batching several outcomes keep ownership of their transaction. A direct
        // service call also gets an atomic boundary; a lock without a transaction is unsafe.
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            if (model.InventoryTransportLegId != leg.Id || model.ReceivedQuantityMt < 0m
                || (!model.SettlementOnly && model.ReceivedQuantityMt <= 0m))
                throw new BusinessRuleException("TRANSPORT_RECEIPT_INVALID_QUANTITY", "بار و مقدار دریافت معتبر نیست.");
            await LockLegsAsync([leg.Id]);
            var remaining = await GetRemainingQuantityAsync(leg);
            if (leg.Status is not (InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit)
                || remaining <= 0m)
                throw new BusinessRuleException("TRANSPORT_RECEIPT_NO_REMAINING", "این بار ماندهٔ قابل دریافت ندارد.");
            if ((model.ExpectedRemainingMt.HasValue && model.ExpectedRemainingMt.Value != remaining)
                || (_validatedRemaining.TryGetValue(leg.Id, out var validated) && validated != remaining))
                throw new BusinessRuleException("TRANSPORT_RECEIPT_STALE", "ماندهٔ این بار تغییر کرده است؛ صفحه را تازه و مقدار را دوباره بررسی کنید.");
            if (decimal.Round(model.ReceivedQuantityMt + model.ShortageQuantityMt, 4, MidpointRounding.AwayFromZero) > remaining)
                throw new BusinessRuleException("TRANSPORT_RECEIPT_EXCEEDS_REMAINING", "مجموع دریافت و کسری از ماندهٔ قابل دریافت بیشتر است.");
            var receipt = await ApplyCoreAsync(model, leg, saleConversion);
            if (transaction is not null) await transaction.CommitAsync();
            _validatedRemaining.Remove(leg.Id);
            return receipt;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    // Ordered locks also protect continuation/group commands, before their first write.
    public async Task LockLegsAsync(IEnumerable<int> legIds, CancellationToken ct = default)
    {
        var ids = legIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0 || _db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Transport mutation locks require a transaction.");
        var fresh = await _db.InventoryTransportLegs
            .FromSqlInterpolated($@"SELECT * FROM ""InventoryTransportLegs"" WHERE ""Id"" = ANY({ids}) ORDER BY ""Id"" FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        foreach (var current in fresh)
        {
            var tracked = _db.InventoryTransportLegs.Local.FirstOrDefault(l => l.Id == current.Id);
            if (tracked is null) continue;
            // Keep intentional freight/party changes made by callers; refresh the fields
            // determining eligibility and the optimistic concurrency token only.
            tracked.Status = current.Status;
            tracked.QuantityMt = current.QuantityMt;
            tracked.Version = current.Version;
            _db.Entry(tracked).Property(l => l.Version).OriginalValue = current.Version;
        }
    }

    private async Task<InventoryTransportReceipt> ApplyCoreAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult? saleConversion)
    {
        var receipt = new InventoryTransportReceipt
        {
            InventoryTransportLegId = model.InventoryTransportLegId,
            ReceiptDate = model.ReceiptDate,
            ReceivedQuantityMt = model.ReceivedQuantityMt,
            ShortageQuantityMt = model.ShortageQuantityMt,
            AllowanceMt = model.AllowanceMt,
            ChargeableShortageMt = model.ChargeableShortageMt,
            FreightRateUsdPerMt = model.FreightRateUsdPerMt,
            FreightCostUsd = model.FreightCostUsd,
            ShortageRateUsd = model.ShortageRateUsd,
            ShortageChargeUsd = model.ShortageChargeUsd,
            FreightPayableUsd = model.FreightPayableUsd,
            ServiceProviderId = model.ServiceProviderId,
            OperationalAssetId = model.OperationalAssetId,
            ReceiptDestination = model.ReceiptDestination,
            DestinationTerminalId = model.DestinationTerminalId,
            DestinationStorageTankId = model.DestinationStorageTankId,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };
        var carrierParty = await new AssetUsageChargeService(_db).ResolveCarrierPartyAsync(
            receipt.ServiceProviderId ?? leg.ServiceProviderId,
            leg.DriverId,
            receipt.OperationalAssetId ?? leg.OperationalAssetId,
            receipt.ReceiptDate);
        receipt.CarrierPartyType = carrierParty?.PartyType;
        receipt.CarrierPartyId = carrierParty?.PartyId;

        _db.InventoryTransportReceipts.Add(receipt);
        await _db.SaveChangesAsync();
        await new AssetUsageChargeService(_db).SyncOperationAsync(receipt, leg);

        await SyncReceiptFreightExpenseAsync(receipt, leg);
        await SyncShortageDebtAsync(receipt, leg, model.ShortageAsSeparateDebt);

        LossEvent? shortageLoss = null;
        TransportSourcePlan shortageSourcePlan = TransportSourcePlan.Empty;
        InventoryMovement? inboundMovement = null;
        SalesTransaction? directSale = null;

        // اختلاف تخلیه در هر دو جهت یک رویداد ثبت می‌شود تا راپور «کسری و اضافه‌بار حمل» کامل باشد.
        // قرارداد علامت با تخلیهٔ موتر یکی است (TransportVarianceMath):
        // مقدار مثبت = کسری (قابل جریمه)، مقدار منفی = اضافه‌بار (هرگز قابل جریمه نیست).
        // اضافه‌بار فقط وقتی رویداد می‌سازد که تخلیهٔ واقعی باشد؛ در «فقط تسویه» بار هنوز داخل
        // وسیله است و کسری منفی صرفاً باقیماندهٔ قابل تخلیه را بالا می‌برد.
        var recordsVariance = TransportVarianceMath.IsShortage(model.ShortageQuantityMt)
            || (TransportVarianceMath.IsSurplus(model.ShortageQuantityMt) && !model.SettlementOnly);
        if (recordsVariance)
        {
            var isSurplus = TransportVarianceMath.IsSurplus(model.ShortageQuantityMt);
            var allowanceMt = model.AllowanceMt ?? 0m;
            shortageSourcePlan = await _sourceAllocations.BuildFromLegAsync(
                leg.Id,
                Math.Abs(model.ShortageQuantityMt));
            shortageLoss = new LossEvent
            {
                Stage = LossEventStage.ReceiptShortage,
                ProductId = leg.ProductId,
                ContractId = leg.SourcePurchaseContractId,
                ShipmentId = leg.ShipmentId,
                TransportLegId = leg.Id,
                TerminalId = model.DestinationTerminalId,
                StorageTankId = model.DestinationStorageTankId,
                EventDate = model.ReceiptDate,
                ExpectedQuantityMt = model.ReceivedQuantityMt + model.ShortageQuantityMt,
                ActualQuantityMt = model.ReceivedQuantityMt,
                DifferenceQuantityMt = model.ShortageQuantityMt,
                ToleranceQuantityMt = allowanceMt,
                AllowableLossMt = TransportVarianceMath.AllowableShortage(model.ShortageQuantityMt, allowanceMt),
                ChargeableLossMt = isSurplus
                    ? 0m
                    : model.ChargeableShortageMt ?? TransportVarianceMath.ChargeableShortage(model.ShortageQuantityMt, allowanceMt),
                AffectsInventory = false,
                Reference = $"TRANSPORT-RECEIPT:{receipt.Id}",
                Notes = isSurplus
                    ? "Inventory transport receipt positive variance (surplus)"
                    : "Inventory transport receipt shortage"
            };
            _sourceAllocations.ApplyLegacyHeader(shortageLoss, shortageSourcePlan);
            _db.LossEvents.Add(shortageLoss);
            await _db.SaveChangesAsync();
            await _sourceAllocations.PersistLossAsync(
                shortageLoss,
                shortageSourcePlan,
                leg.Id);
        }

        // «فقط تسویه» (ReceivedQuantityMt=0) هیچ حرکت موجودی نمی‌سازد؛ بار داخل وسیله می‌ماند.
        if (model.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
            && model.ReceivedQuantityMt > 0m)
        {
            // خروجی مبدأ برای هر قرارداد جداگانه ثبت شده است (یک سند به‌ازای هر Allocation).
            // پس ورودی مقصد هم باید به همان نسبت تقسیم شود، وگرنه حملی که از دو قرارداد تغذیه
            // شده کل مقدار را به قرارداد سرصفحه می‌بندد و موجودیِ قراردادی هر دو طرف غلط می‌شود.
            var sourcePlan = await _sourceAllocations.BuildFromLegAsync(leg.Id, model.ReceivedQuantityMt);
            var preparedMovements = sourcePlan.Shares
                .OrderByDescending(share => share.QuantityMt)
                .ThenBy(share => share.SourcePurchaseContractId)
                .Select(share => new InventoryMovement
                {
                    ProductId = leg.ProductId,
                    ContractId = share.SourcePurchaseContractId,
                    TerminalId = model.DestinationTerminalId!.Value,
                    StorageTankId = model.DestinationStorageTankId,
                    MovementDate = model.ReceiptDate,
                    QuantityMt = share.QuantityMt,
                    ReferenceDocument = $"TRANSPORT-RECEIPT:{receipt.Id}",
                    Notes = "Inventory transport leg destination receipt"
                })
                .ToList();
            var inboundMovements = await _movements.PostInboundRangeAsync(preparedMovements);

            // بزرگ‌ترین سهم سند اصلی است؛ کلید یکتای رسید فقط به همان وصل می‌شود و بقیهٔ سهم‌ها
            // با همان ReferenceDocument قابل ردیابی‌اند.
            inboundMovement = inboundMovements[0];
            receipt.InventoryMovementId = inboundMovement.Id;
        }
        else if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale)
        {
            var saleSourcePlan = await _sourceAllocations.BuildFromLegAsync(leg.Id, model.ReceivedQuantityMt);
            directSale = BuildDirectSale(model, leg, saleConversion!);
            _sourceAllocations.ApplyLegacyHeader(directSale, saleSourcePlan);
            _db.SalesTransactions.Add(directSale);
            await _db.SaveChangesAsync();
            await _sourceAllocations.PersistSaleAsync(directSale, saleSourcePlan, leg.Id);

            receipt.SalesTransactionId = directSale.Id;

            if (SalePendingPricing.ShouldPostLedger(directSale))
            {
                Ledger.Post(BuildDirectSaleLedgerEntry(
                    directSale,
                    saleSourcePlan.SingleContractId,
                    saleConversion!));
            }
            await _db.SaveChangesAsync();

            // مرحله ۷ — Dual-write داخل همان Transaction قدیمی.
            if (_salesAccounting is not null)
            {
                await _salesAccounting.TryPostSaleAsync(directSale);
                await _salesAccounting.TryPostCogsAsync(directSale);
            }
        }
        else if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch)
        {
            // رسیدهای «همراهِ» موتر چندواگنه دیسپچ جدا نمی‌سازند؛ دیسپچ واحد روی رسید اول ثبت شده است.
            // TruckDispatch فقط رکورد سازگاری برای مقصدِ موتر است؛ واگن/کشتی معادلی ندارند و
            // زنجیرهٔ حمل برای هر سه نوع از مرحلهٔ فرزند خوانده می‌شود.
            if (!model.SkipDirectDispatchRecord
                && model.DirectDispatchTransportType == LoadingTransportType.Truck)
            {
                var dispatch = BuildDirectDispatch(model, leg, receipt.Id);
                _db.TruckDispatches.Add(dispatch);
                await _db.SaveChangesAsync();
            }
        }

        // Dual-write داخل همان Transaction قدیمی: سهمِ این رسید از بهای «کالای در راه» خارج و به حوضچهٔ
        // ترمینال مقصد وارد می‌شود؛ بهای کسری هم همان‌جا به حساب ضایعات می‌رود. آداپتر خودش فقط مسیر
        // ToInventory با دریافتِ مثبت را می‌پذیرد و بقیهٔ مقصدها را Skip می‌کند.
        if (_transferAccounting is not null)
        {
            await _transferAccounting.TryPostReceiptAsync(receipt);
        }

        // همان رسید، وقتی منبعش خرید است نه ترمینال: حملی که سهم‌هایش از بارگیری خرید می‌آید
        // LoadingReceipt ندارد، پس ورودش به موجودی را همین آداپتر ثبت می‌کند. دو آداپتر روی یک
        // رسید همپوشانی ندارند — هرکدام نبودِ منبعِ دیگری را Skip می‌کند.
        if (_purchaseAccounting is not null)
        {
            await _purchaseAccounting.TryPostTransportReceiptAsync(receipt);
        }

        // فقط وقتی باقیمانده حمل صفر شد، حمل «تکمیل» می‌شود؛ در تخلیهٔ جزئی حمل باز می‌ماند تا باقیمانده هم رسید بگیرد.
        var remainingAfterMt = await GetRemainingQuantityAsync(leg);
        if (remainingAfterMt <= 0m)
        {
            leg.Status = InventoryTransportLegStatus.Received;
        }
        // A partial receipt also changes the source balance. Touch its versioned row so
        // a concurrent cancellation/edit cannot use an unchanged source concurrency token.
        leg.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // لایهٔ Lineage (پشت flag Lineage:WriteLots؛ با flag خاموش no-op). فقط رکوردهای نسب‌نامه insert می‌شود.
        await _lineage.OnLegReceiptAsync(leg, receipt, inboundMovement, shortageLoss);
        if (directSale is not null)
        {
            await _lineage.OnDirectSaleAsync(leg, directSale);
        }

        return receipt;
    }

    private async Task ValidateDirectSaleAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        if (model.SaleSupplierId is > 0)
        {
            if (model.SaleCustomerId.HasValue)
            {
                modelState.AddModelError(keyPrefix + nameof(model.SaleCustomerId), "Sale buyer must be either a customer or a supplier.");
            }
            else if (!await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == model.SaleSupplierId.Value && s.IsActive))
            {
                modelState.AddModelError(keyPrefix + nameof(model.SaleSupplierId), "Supplier is invalid.");
            }
        }
        else if (!model.SaleCustomerId.HasValue || model.SaleCustomerId.Value <= 0)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleCustomerId), "Customer is required for direct sale.");
        }
        else if (!await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.SaleCustomerId.Value && c.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleCustomerId), "Customer is invalid.");
        }

        var normalizedInvoice = model.SaleInvoiceNumber?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInvoice))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleInvoiceNumber), "Invoice number is required for direct sale.");
        }
        else if (await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.InvoiceNumber == normalizedInvoice))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleInvoiceNumber), "Invoice number already exists.");
        }
        else
        {
            model.SaleInvoiceNumber = normalizedInvoice;
        }

        if (!model.SaleDate.HasValue || model.SaleDate.Value == default)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleDate), "Sale date is required for direct sale.");
        }

        // قیمت باز: قیمت و نرخ ارز بعداً از صفحهٔ همان فروش ثبت می‌شود.
        if (model.SaleIsPricePending)
        {
            model.SaleUnitPriceInCurrency = null;
            model.SaleAppliedFxRateToUsd = null;
        }
        else if (!model.SaleUnitPriceInCurrency.HasValue || model.SaleUnitPriceInCurrency.Value <= 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleUnitPriceInCurrency), "Unit price is required for direct sale.");
        }

        model.SaleCurrency = SystemCurrency.Normalize(model.SaleCurrency);
        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.SaleCurrency && c.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleCurrency), "Invalid currency selection.");
        }

        if (leg.SourcePurchaseContract is null)
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "Source purchase contract context is missing.");
        }
    }

    private async Task ValidateDirectDispatchAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        // وسیلهٔ مقصد هر سه نوع می‌تواند باشد؛ منطق یکی است و فقط شناسهٔ وسیله فرق می‌کند.
        switch (model.DirectDispatchTransportType)
        {
            case LoadingTransportType.Wagon:
                if ((!model.DirectDispatchWagonId.HasValue || model.DirectDispatchWagonId.Value <= 0)
                    && string.IsNullOrWhiteSpace(model.DirectDispatchWagonNumber))
                {
                    modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchWagonId), "Wagon is required for this transfer.");
                }
                else if (model.DirectDispatchWagonId is > 0
                    && !await _db.Wagons.AsNoTracking().AnyAsync(w => w.Id == model.DirectDispatchWagonId.Value && w.IsActive))
                {
                    modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchWagonId), "Wagon is invalid.");
                }
                break;

            case LoadingTransportType.Vessel:
                if (!model.DirectDispatchVesselId.HasValue || model.DirectDispatchVesselId.Value <= 0)
                {
                    modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchVesselId), "Vessel is required for this transfer.");
                }
                else if (!await _db.Vessels.AsNoTracking().AnyAsync(v => v.Id == model.DirectDispatchVesselId.Value && v.IsActive))
                {
                    modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchVesselId), "Vessel is invalid.");
                }
                break;

            default:
                if (!model.DirectDispatchTruckId.HasValue || model.DirectDispatchTruckId.Value <= 0)
                {
                    modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchTruckId), "Truck is required for direct dispatch.");
                }
                else if (!await _db.Trucks.AsNoTracking().AnyAsync(t => t.Id == model.DirectDispatchTruckId.Value && t.IsActive))
                {
                    modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchTruckId), "Truck is invalid.");
                }
                break;
        }

        if (model.DirectDispatchDriverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == model.DirectDispatchDriverId.Value && d.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchDriverId), "Driver is invalid.");
        }

        if (model.DirectDispatchDestinationLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DirectDispatchDestinationLocationId.Value && l.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchDestinationLocationId), "Destination is invalid.");
        }

        if (!model.DirectDispatchDate.HasValue || model.DirectDispatchDate.Value == default)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchDate), "Dispatch date is required.");
        }

        if (!model.DirectDispatchLoadedQuantityMt.HasValue || model.DirectDispatchLoadedQuantityMt.Value <= 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchLoadedQuantityMt), "Loaded quantity is required for direct dispatch.");
        }
        // در انتقال گروهیِ چندواگنه، دیسپچِ واحدِ موتر وزن کامل را دارد و از دریافتِ رسید اول بیشتر است؛
        // بقیهٔ وزن در رسیدهای همراه (بدون دیسپچ) حساب می‌شود — پس این سقف فقط با flag خاموش اعمال می‌شود.
        else if (!model.AllowDirectDispatchBeyondReceipt && model.DirectDispatchLoadedQuantityMt.Value > model.ReceivedQuantityMt)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchLoadedQuantityMt), "Loaded quantity cannot exceed received quantity for direct dispatch.");
        }

        if (leg.SourcePurchaseContract is null)
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "Source purchase contract context is missing.");
        }
    }

    private static SalesTransaction BuildDirectSale(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult conversion)
    {
        var unitPrice = model.SaleIsPricePending ? 0m : model.SaleUnitPriceInCurrency!.Value;
        var totalInCurrency = decimal.Round(
            model.ReceivedQuantityMt * unitPrice,
            4,
            MidpointRounding.AwayFromZero);

        var sale = new SalesTransaction
        {
            ContractId = null,
            CompanyId = leg.SourcePurchaseContract!.CompanyId,
            CustomerId = model.SaleSupplierId is > 0 ? null : model.SaleCustomerId!.Value,
            SupplierId = model.SaleSupplierId is > 0 ? model.SaleSupplierId : null,
            ProductId = leg.ProductId,
            DestinationLocationId = leg.DestinationLocationId,
            ShipmentId = leg.ShipmentId,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = model.SaleInvoiceNumber!,
            SaleDate = model.SaleDate!.Value.Date,
            QuantityMt = model.ReceivedQuantityMt,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = unitPrice,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(unitPrice),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };
        if (model.SaleIsPricePending)
        {
            SalePendingPricing.MarkPending(sale, model.SaleCurrency);
        }

        return sale;
    }

    private static LedgerPostingRequest BuildDirectSaleLedgerEntry(
        SalesTransaction sale,
        int? sourcePurchaseContractId,
        CurrencyConversionResult conversion)
        => SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: sourcePurchaseContractId);

    private static TruckDispatch BuildDirectDispatch(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        int receiptId)
        => new()
        {
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            InventoryTransportReceiptId = receiptId,
            ContractId = leg.SourcePurchaseContractId,
            ProductId = leg.ProductId,
            TruckId = model.DirectDispatchTruckId!.Value,
            DriverId = model.DirectDispatchDriverId,
            DestinationLocationId = model.DirectDispatchDestinationLocationId ?? leg.DestinationLocationId,
            ServiceProviderId = model.ServiceProviderId,
            OperationalAssetId = model.OperationalAssetId,
            DispatchDate = model.DirectDispatchDate!.Value.Date,
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = model.DirectDispatchLoadedQuantityMt!.Value,
            FreightCostUsd = model.FreightCostUsd,
            FreightPayableUsd = model.FreightPayableUsd,
            TicketSerialNumber = string.IsNullOrWhiteSpace(model.DirectDispatchTicketSerialNumber)
                ? null
                : model.DirectDispatchTicketSerialNumber.Trim(),
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };

    private static void NormalizeReceiptQuantities(InventoryTransportReceiptCreateViewModel model)
    {
        // PostgreSQL rounds each numeric(…,4) column separately. Validate those same
        // stored units, not a rounded sum of higher-precision client values.
        model.ReceivedQuantityMt = decimal.Round(model.ReceivedQuantityMt, 4, MidpointRounding.AwayFromZero);
        model.ShortageQuantityMt = decimal.Round(model.ShortageQuantityMt, 4, MidpointRounding.AwayFromZero);
    }

    // باقیمانده حمل = مقدار کل منهای مجموع رسیدهای فعال (دریافت + کسری). مبنای مجاز بودن رسید جزئی بعدی.
    private Task<decimal> GetRemainingQuantityAsync(InventoryTransportLeg leg)
        => _quantities.GetRemainingMtAsync(leg.Id);

    // موتر و واگن هر دو از جریان «تخلیه + کرایه» (تلورانس، کسری قابل مجرا، کرایهٔ فی‌تن) استفاده می‌کنند.
    private static bool UsesUnloadFreightFlow(InventoryTransportLeg leg)
        => leg.TransportType is LoadingTransportType.Truck or LoadingTransportType.Wagon or LoadingTransportType.Vessel;

    private static void NormalizeTruckReceiptFields(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        decimal remainingMt)
    {
        model.TransportType = leg.TransportType;
        model.LoadedQuantityMt = leg.QuantityMt;

        // «فقط تسویه»: دریافت صفر و مقصد ToInventory (بدون Movement) اجبار می‌شود.
        if (model.SettlementOnly)
        {
            model.ReceivedQuantityMt = 0m;
            model.ReceiptDestination = InventoryTransportReceiptDestination.ToInventory;
        }

        if (!UsesUnloadFreightFlow(leg))
        {
            model.AllowanceMt = null;
            model.ChargeableShortageMt = null;
            model.FreightRateUsdPerMt = null;
            model.FreightCostUsd = null;
            model.ShortageRateUsd = null;
            model.ShortageChargeUsd = null;
            model.FreightPayableUsd = null;
            return;
        }

        // کسری دیگر «مقدار کل منهای دریافت» نیست؛ کاربر آن را در همان تخلیه دستی ثبت می‌کند (باقیمانده در وسیله می‌ماند).
        // در «فقط تسویه» کسری منفی نگه داشته می‌شود: اضافه‌وزن ترازوی مقصد، باقیماندهٔ قابل تخلیه/فروش را بالا می‌برد.
        // کسری منفی یعنی مازاد (تخلیهٔ بیشتر از بارگیری) و پاک نمی‌شود؛
        // «کسری قابل شارژ» خودش مقدار منفی را صفر می‌کند، پس مازاد هرگز جریمه نمی‌گیرد.

        // مقدار مبنای این رسید = دریافت + کسری همین تخلیه (در تخلیهٔ کامل برابر با مقدار حمل می‌شود).
        // در «فقط تسویه» کرایه بر کل باقیماندهٔ حمل (بارِ رسیده + کسری مسیر) محاسبه می‌شود.
        var baseQuantityMt = model.SettlementOnly
            ? remainingMt
            : model.ReceivedQuantityMt + model.ShortageQuantityMt;

        var allowanceMt = model.AllowanceMt ?? 0m;
        model.AllowanceMt = allowanceMt;

        var chargeableShortageMt = ComputeChargeableShortage(model.ShortageQuantityMt, allowanceMt);
        model.ChargeableShortageMt = chargeableShortageMt;

        // کرایهٔ ناخالص (Gross): اگر مستقیم وارد نشده باشد، از نرخ فی‌تن × مقدار مبنای همین تخلیه ساخته می‌شود.
        if (!model.FreightCostUsd.HasValue && model.FreightRateUsdPerMt.HasValue)
        {
            model.FreightCostUsd = FreightShortageMath.GrossFreightUsd(
                baseQuantityMt,
                model.FreightRateUsdPerMt.Value);
        }

        // دو حالتِ ناسازگار برای خسارتِ کسری (کاربر یکی را انتخاب می‌کند):
        //   • DeductShortageFromFreight ⇒ خسارت از کرایه کم می‌شود؛ کرایهٔ پرداختنی = Gross − خسارت.
        //   • ShortageAsSeparateDebt    ⇒ کرایه دست‌نخورده (Gross) می‌ماند و خسارت جدا به‌عنوان بدهیِ
        //                                 مسئول در ApplyAsync ثبت می‌شود.
        // اگر هر دو تیک بخورند، «بدهیِ جدا» اولویت دارد و کسر از کرایه خنثی می‌شود (بدون double-count).
        if (model.ShortageAsSeparateDebt)
        {
            model.DeductShortageFromFreight = false;
        }

        // مبلغِ خسارت فقط وقتی محاسبه/ذخیره می‌شود که قرار باشد از مسئول وصول شود (کسر یا بدهیِ جدا).
        // این مبلغ مبنای «خالص‌شدنِ» کرایه در P&L هم هست، پس در هر دو حالت پر می‌شود.
        if (model.DeductShortageFromFreight || model.ShortageAsSeparateDebt)
        {
            model.ShortageChargeUsd = model.ShortageRateUsd.HasValue
                ? decimal.Round(chargeableShortageMt * model.ShortageRateUsd.Value, 4, MidpointRounding.AwayFromZero)
                : 0m;
        }
        else
        {
            model.ShortageChargeUsd = null;
        }

        // فقط در حالتِ کسر از کرایه، خسارت از کرایهٔ پرداختنی کم می‌شود؛ در «بدهیِ جدا» کرایه کامل می‌ماند.
        var freightDeduction = model.DeductShortageFromFreight ? (model.ShortageChargeUsd ?? 0m) : 0m;
        model.FreightPayableUsd = model.FreightCostUsd.HasValue
            ? decimal.Round(model.FreightCostUsd.Value - freightDeduction, 4, MidpointRounding.AwayFromZero)
            : null;
    }

    // دو قاعدهٔ کرایه که هر مسیرِ رسید/تسویه باید رعایت کند. تا پیش از این فقط فرمِ رسیدِ
    // گروهی آن‌ها را (به‌صورت محلی) داشت و مسیرهای «تسویهٔ کرایه» بدون محافظ بودند.
    private async Task ValidateFreightAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        var grossFreightUsd = model.FreightCostUsd ?? 0m;
        if (grossFreightUsd <= 0m)
        {
            return;
        }

        // (۱) ضدِ دوباره‌شماری. کرایه‌ای که قبلاً با نوع مصرفِ «کرایه حمل» ثبت شده، در P&L
        // داخل ExpenseTransactionsUsd است؛ کرایهٔ دوم روی رسید همان مبلغ را بار دیگر از
        // راه ReceiptFreightExpenseUsd وارد می‌کند و «مصارف» دو برابر دیده می‌شود.
        var registeredFreightUsd = await GetRegisteredFreightUsdAsync(leg.Id);
        if (registeredFreightUsd > 0m)
        {
            modelState.AddModelError(
                keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.FreightCostUsd),
                $"کرایهٔ این حمل قبلاً به‌صورت مصرف «کرایه حمل» به مبلغ {registeredFreightUsd:N2} USD ثبت شده است؛ کرایهٔ دوباره روی رسید ثبت نمی‌شود.");
        }

        // (۲) هویتِ طرفِ کرایه. همان ترتیبِ SyncReceiptFreightExpenseAsync: دارایی ملکی ⇒
        // درآمد داخلی و بدونِ بدهی بیرونی؛ وگرنه شرکت خدماتی، وگرنه رانندهٔ همین حمل.
        // بدون هیچ‌کدام، مبلغ در P&L می‌نشیند ولی هیچ مصرف/سطر دفتری ساخته نمی‌شود و
        // کرایه بدهیِ کسی نمی‌گردد.
        if (!model.OperationalAssetId.HasValue
            && !model.ServiceProviderId.HasValue
            && !leg.DriverId.HasValue)
        {
            modelState.AddModelError(
                keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.ServiceProviderId),
                "طرف کرایه مشخص نیست. شرکت خدماتی، دارایی عملیاتی یا رانندهٔ حمل را انتخاب کنید؛ وگرنه کرایه به حساب هیچ‌کس ثبت نمی‌شود.");
        }
    }

    /// <summary>
    /// مجموع کرایهٔ ثبت‌شدهٔ هر حمل از مصارفِ نوع «کرایه حمل» (فقط مصارف فعال/غیرلغوشده).
    /// تنها مالکِ این پرسش؛ فرمِ رسید (برای نمایش «کرایه نهایی») و اعتبارسنجیِ بالا هر دو
    /// از همین‌جا می‌خوانند تا دو تعریف موازی ساخته نشود.
    /// </summary>
    public async Task<Dictionary<int, decimal>> GetRegisteredFreightByLegAsync(IReadOnlyCollection<int> legIds)
    {
        if (legIds is null || legIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        return await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.TransportLegId.HasValue
                && legIds.Contains(e.TransportLegId.Value)
                && !e.IsCancelled
                && e.ExpenseType != null
                && e.ExpenseType.Code == TransportFreightExpenseCode)
            .GroupBy(e => e.TransportLegId!.Value)
            .Select(g => new { LegId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToDictionaryAsync(x => x.LegId, x => x.AmountUsd);
    }

    public async Task<decimal> GetRegisteredFreightUsdAsync(int legId)
        => await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.TransportLegId == legId
                && !e.IsCancelled
                && e.ExpenseType != null
                && e.ExpenseType.Code == TransportFreightExpenseCode)
            .SumAsync(e => (decimal?)e.AmountUsd) ?? 0m;

    private void ValidateTruckReceiptFields(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        decimal remainingMt,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        // کسری دستی است؛ فقط منفی نبودن و اینکه مجموع دریافت + کسری از باقیمانده حمل بیشتر نشود بررسی می‌شود.
        // استثنا: «فقط تسویه» کسری منفی (اضافه‌وزن ترازو) را مجاز می‌داند.
        // تخلیهٔ واقعی می‌تواند از بارگیری کمتر (کسری) یا بیشتر (مازاد) باشد؛ هیچ‌کدام مسدود
        // نمی‌شود. حدِ واقعی فقط این است که حملِ کاملاً تخلیه‌شده رسید جدید نگیرد، که در
        // ValidateAsync بررسی شده است.

        if (model.AllowanceMt is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.AllowanceMt), "Allowance cannot be negative.");
        if (model.ChargeableShortageMt is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.ChargeableShortageMt), "Chargeable shortage cannot be negative.");
        if (model.FreightRateUsdPerMt is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.FreightRateUsdPerMt), "Freight rate cannot be negative.");
        if (model.FreightCostUsd is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.FreightCostUsd), "Freight cost cannot be negative.");
        if (model.ShortageRateUsd is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.ShortageRateUsd), "Shortage rate cannot be negative.");
    }

    private async Task ValidateOperationalPartyAsync(
        int? serviceProviderId,
        int? operationalAssetId,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        if (serviceProviderId.HasValue && operationalAssetId.HasValue)
        {
            modelState.AddModelError(keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.OperationalAssetId), "Select either a service provider or an operational asset, not both.");
        }

        if (serviceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == serviceProviderId.Value && p.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.ServiceProviderId), "Service provider selection is invalid.");
        }

        if (operationalAssetId.HasValue
            && !await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == operationalAssetId.Value && a.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.OperationalAssetId), "Operational asset selection is invalid.");
        }
    }

    private async Task SyncReceiptFreightExpenseAsync(InventoryTransportReceipt receipt, InventoryTransportLeg leg)
    {
        // طرفِ کرایه: شرکت خدماتی یا (اگر انتخاب نشد) موتروانِ مستقلِ همین حمل.
        // موترِ خودِ شرکت (دارایی عملیاتی) کرایه نمی‌گیرد — کرایه‌ای به خودش پرداخت نمی‌شود؛
        // سوخت/حق‌سفر/راه به‌صورت مصرف جدا ثبت می‌شود، نه اینجا.
        if (receipt.OperationalAssetId.HasValue)
        {
            return;
        }

        var serviceProviderId = receipt.ServiceProviderId;
        var driverId = serviceProviderId.HasValue ? (int?)null : leg.DriverId;
        if (!serviceProviderId.HasValue && !driverId.HasValue)
        {
            return;
        }

        var amountUsd = GetFreightExpenseAmountUsd(receipt.FreightPayableUsd, receipt.FreightCostUsd);
        if (amountUsd <= 0m)
        {
            return;
        }

        var expenseType = await EnsureReceiptFreightExpenseTypeAsync();
        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = leg.SourcePurchaseContractId,
            ShipmentId = leg.ShipmentId,
            TransportLegId = leg.Id,
            ServiceProviderId = serviceProviderId,
            DriverId = driverId,
            ExpenseDate = receipt.ReceiptDate.Date,
            Amount = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = $"Truck receipt freight for transport leg #{leg.Id}, receipt #{receipt.Id}"
        };

        // PTG-P1-04 — هویت تسویه. بالاتر ثابت شد که یکی از دو طرفِ حمل‌کننده هست (وگرنه متد
        // برگشته بود) و کرایه بدهیِ ماست ⇒ Payable روی همان طرف.
        ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);
        _settlementValidator.Validate(expense);

        _db.ExpenseTransactions.Add(expense);
        await _db.SaveChangesAsync();

        // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseAsync(expense);
        }

        // کرایه بدهیِ ما به حمل‌کننده است ⇒ همیشه Credit روی حساب همان طرف (شرکت خدماتی یا راننده).
        ExpenseLedger.Post(new ExpenseLedgerRequest
        {
            Expense = expense,
            Description = expense.Description ?? "Truck receipt freight",
            Reference = $"TRANSPORT-RECEIPT:{receipt.Id}",
            FxRateSource = "Base currency"
        });
        await _db.SaveChangesAsync();
    }

    // «بدهیِ جدا»: خسارتِ کسری را به‌عنوان مطالبه (Debit) روی حساب مسئول (شرکت خدماتی یا موتروانِ مستقل) ثبت می‌کند.
    // موترِ خودِ شرکت (دارایی عملیاتی) مطالبه نمی‌گیرد. کرایه اینجا دست‌نمی‌خورد؛ P&L به‌صورت خالص می‌بیند.
    //
    // مالکیت: LedgerEntry ستون شرکت ندارد و انتسابِ شرکتیِ یک سطر فقط از راه ContractId خوانده
    // می‌شود. پس در حملِ چندقراردادی/چندشرکتی یک سطرِ واحد با قراردادِ سرصفحه یعنی کلِ خسارت
    // روی همان یک شرکت می‌نشیند. سهم‌های منبع همان تقسیمی را می‌دهند که کسری و ورودی مقصد
    // با آن ثبت شده‌اند، پس مطالبه هم به همان نسبت به سطرهای per-contract تقسیم می‌شود
    // (جمع سطرها دقیقاً برابر ShortageChargeUsd می‌ماند). حملِ تک‌قراردادی دقیقاً همان یک
    // سطرِ قبلی را با همان Reference می‌سازد.
    private async Task SyncShortageDebtAsync(InventoryTransportReceipt receipt, InventoryTransportLeg leg, bool asSeparateDebt)
    {
        if (!asSeparateDebt || receipt.OperationalAssetId.HasValue)
        {
            return;
        }

        var amountUsd = receipt.ShortageChargeUsd ?? 0m;
        if (amountUsd <= 0m)
        {
            return;
        }

        var serviceProviderId = receipt.ServiceProviderId;
        var driverId = serviceProviderId.HasValue ? (int?)null : leg.DriverId;
        if (!serviceProviderId.HasValue && !driverId.HasValue)
        {
            return;
        }

        // محافظِ ثبت تکراری: retry روی همان رسید نباید مطالبه را دوباره بسازد.
        if (await _db.LedgerEntries.AnyAsync(l => l.SourceType == ShortageDebtSourceType && l.SourceId == receipt.Id))
        {
            return;
        }

        foreach (var (contractId, shareUsd) in await BuildShortageDebtSharesAsync(receipt, leg))
        {
            Ledger.Post(new LedgerPostingRequest
            {
                EntryDate = receipt.ReceiptDate.Date,
                Side = LedgerSide.Debit,
                AmountUsd = shareUsd,
                Currency = SystemCurrency.BaseCurrencyCode,
                SourceAmount = shareUsd,
                SourceCurrencyCode = SystemCurrency.BaseCurrencyCode,
                AppliedFxRateToUsd = 1m,
                AppliedFxRateDate = receipt.ReceiptDate.Date,
                AppliedFxRateSource = "Base currency",
                Description = $"Shortage charge for transport leg #{leg.Id}, receipt #{receipt.Id}",
                SourceType = ShortageDebtSourceType,
                SourceId = receipt.Id,
                Reference = ShortageDebtReference(receipt.Id, contractId, leg.SourcePurchaseContractId),
                ContractId = contractId,
                ShipmentId = leg.ShipmentId,
                ServiceProviderId = serviceProviderId,
                DriverId = driverId
            });
        }

        await _db.SaveChangesAsync();

        if (_shortageAccounting is not null)
        {
            await _shortageAccounting.TryPostShortageChargeAsync(receipt);
        }
    }

    // سهم‌های پولیِ مطالبهٔ کسری به تفکیک قرارداد منبع. مبنای نسبت همان سهم‌های منبعِ حمل است
    // که کسری فیزیکی هم با آن‌ها تقسیم شده؛ باقیماندهٔ گِرد به بزرگ‌ترین سهم می‌رود تا جمعِ
    // سطرها دقیقاً برابر مبلغ کل بماند. حملِ بدون سهمِ منبع به قرارداد سرصفحه برمی‌گردد.
    private async Task<IReadOnlyList<(int? ContractId, decimal AmountUsd)>> BuildShortageDebtSharesAsync(
        InventoryTransportReceipt receipt,
        InventoryTransportLeg leg)
    {
        var amountUsd = receipt.ShortageChargeUsd ?? 0m;
        var chargeableMt = receipt.ChargeableShortageMt ?? Math.Abs(receipt.ShortageQuantityMt);
        var plan = chargeableMt > 0m
            ? await _sourceAllocations.BuildFromLegAsync(leg.Id, chargeableMt)
            : TransportSourcePlan.Empty;

        var byContract = plan.Shares
            .GroupBy(share => share.SourcePurchaseContractId)
            .Select(g => (ContractId: g.Key, QuantityMt: g.Sum(share => share.QuantityMt)))
            .OrderBy(x => x.ContractId)
            .ToList();

        if (byContract.Count <= 1)
        {
            return [(byContract.Count == 1 ? byContract[0].ContractId : leg.SourcePurchaseContractId, amountUsd)];
        }

        var amounts = Accounting.InventoryTransportLegOwnershipResolver.ProportionalSplit(
            amountUsd,
            byContract.Select(x => x.QuantityMt).ToList());

        return byContract
            .Select((row, index) => ((int?)row.ContractId, amounts[index]))
            .Where(row => row.Item2 > 0m)
            .ToList();
    }

    private const string ShortageDebtSourceType = "ShortageCharge";

    // حملِ تک‌قراردادی دقیقاً همان Reference قبلی را نگه می‌دارد؛ فقط سطرهای اضافیِ حملِ
    // چندقراردادی پسوند قرارداد می‌گیرند تا هر سطر کلید یکتای خودش را داشته باشد.
    private static string ShortageDebtReference(int receiptId, int? contractId, int? headerContractId)
        => contractId is null || contractId == headerContractId
            ? $"TRANSPORT-SHORTAGE:{receiptId}"
            : $"TRANSPORT-SHORTAGE:{receiptId}:C{contractId}";

    private async Task<ExpenseType> EnsureReceiptFreightExpenseTypeAsync()
    {
        var expenseType = await _db.ExpenseTypes.FirstOrDefaultAsync(e => e.Code == ReceiptFreightExpenseCode);
        if (expenseType is not null)
        {
            if (!expenseType.IsActive)
            {
                expenseType.IsActive = true;
                await _db.SaveChangesAsync();
            }

            return expenseType;
        }

        expenseType = new ExpenseType
        {
            Code = ReceiptFreightExpenseCode,
            Name = "Transport Receipt Freight",
            NamePersian = "کرایه رسید حمل",
            Category = "Transport",
            IsActive = true
        };
        _db.ExpenseTypes.Add(expenseType);
        await _db.SaveChangesAsync();
        return expenseType;
    }

    // نوع مصرف استاندارد «کرایه حمل» را تضمین می‌کند تا در مودال «ثبت مصرف» قابل انتخاب باشد.
    public async Task<ExpenseType> EnsureTransportFreightExpenseTypeAsync()
    {
        var expenseType = await _db.ExpenseTypes.FirstOrDefaultAsync(e => e.Code == TransportFreightExpenseCode);
        if (expenseType is not null)
        {
            if (!expenseType.IsActive)
            {
                expenseType.IsActive = true;
                await _db.SaveChangesAsync();
            }

            return expenseType;
        }

        expenseType = new ExpenseType
        {
            Code = TransportFreightExpenseCode,
            Name = "Transport Freight",
            NamePersian = "کرایه حمل",
            Category = "Transport",
            IsActive = true
        };
        _db.ExpenseTypes.Add(expenseType);
        await _db.SaveChangesAsync();
        return expenseType;
    }

    // کرایهٔ وسیلهٔ ملکی شرکت بدهیِ بیرونی و LedgerEntry نمی‌سازد؛ پروفایل دارایی
    // همین رکورد را به‌عنوان عاید کرایه می‌خواند. مسیر قدیمی تسویه و Facade جدید
    // هر دو از این قرارداد idempotent استفاده می‌کنند تا رفتار مالی یکسان بماند.
    public async Task RecordOperationalAssetFreightIncomeAsync(
        int operationalAssetId,
        decimal amountUsd,
        DateTime date,
        int? contractId,
        int? shipmentId,
        int? transportLegId,
        int? truckDispatchId,
        string reference,
        CancellationToken ct = default)
    {
        if (amountUsd <= 0m)
        {
            return;
        }

        var expenseType = await EnsureTransportFreightExpenseTypeAsync();
        var description = $"{OperationalAssetFreightIncomeDescriptionPrefix}{reference}";
        if (await _db.ExpenseTransactions.AnyAsync(e => !e.IsCancelled
            && e.OperationalAssetId == operationalAssetId
            && e.ExpenseTypeId == expenseType.Id
            && e.Description == description, ct))
        {
            return;
        }

        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = contractId,
            ShipmentId = shipmentId,
            TransportLegId = transportLegId,
            TruckDispatchId = truckDispatchId,
            OperationalAssetId = operationalAssetId,
            ExpenseDate = date.Date,
            Amount = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = description,

            // PTG-P1-04 — هویت تسویه. وسیلهٔ ملکیِ خودِ شرکت است: نه طرف‌حسابِ بیرونی،
            // نه حرکتِ پول، و همان‌طور که بالای متد نوشته شده LedgerEntry هم ساخته نمی‌شود.
            // پروفایلِ دارایی این ردیف را به‌عنوان عایدِ کرایه می‌خواند.
            SettlementMode = ExpenseSettlementMode.NonCash
        };
        _settlementValidator.Validate(expense);
        _db.ExpenseTransactions.Add(expense);
        await _db.SaveChangesAsync(ct);

        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseAsync(expense);
        }
    }

    private static decimal GetFreightExpenseAmountUsd(decimal? payableUsd, decimal? grossUsd)
        => payableUsd.HasValue && payableUsd.Value > 0m
            ? payableUsd.Value
            : grossUsd.GetValueOrDefault() > 0m
                ? grossUsd!.Value
                : 0m;

    private static int? NormalizePositiveInt(int? value)
        => value.HasValue && value.Value > 0 ? value.Value : null;

    private static decimal ComputeChargeableShortage(decimal shortageMt, decimal allowanceMt)
        => FreightShortageMath.ChargeableShortage(shortageMt, allowanceMt);

    private static bool QuantitiesMatch(decimal left, decimal right)
        => decimal.Abs(left - right) <= 0.0001m;
}
