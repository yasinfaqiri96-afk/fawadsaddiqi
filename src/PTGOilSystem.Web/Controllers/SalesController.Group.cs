using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Operations;
using PTGOilSystem.Web.Services.LoadingReceipts;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

// ثبت فروش گروهی — یک رکورد اصلی SalesBatch + هر ردیف یک SalesTransaction عادی با SalesBatchId.
// هر ردیف از همان primitiveهای فروشِ موجود استفاده می‌کند تا Ledger/موجودی/سود‌وزیان دقیقاً یکسان بماند:
//   • موجودی مخزن → SalesTransaction(TerminalStock) + InventoryMovement خروج + Ledger + Lineage.
//   • موتر در جریان → SalesTransaction(InTransit) + Ledger + لینک دیسپچ (بدون خروج مجدد موجودی).
//   • واگن / انتقال در مسیر → رسید DirectSale از InventoryTransportReceiptService (بدون خروج مجدد موجودی).
public partial class SalesController
{
    private const decimal QtyEpsilon = 0.0001m;
    private readonly Dictionary<string, int> _groupAccountingSkippedReasons = new(StringComparer.Ordinal);

    // مالکِ ردیفِ فروشی که با primitiveهای زیر ساخته می‌شود: یا یک SalesBatch (فروش گروهی)
    // یا یک PreSaleOrder (تحویل پیش‌فروش). خودِ ردیف در هر دو حالت SalesTransaction عادی است.
    private sealed record SaleLineOwner(int? SalesBatchId, int? PreSaleOrderId, string Reference);

    // ---------- بارگذاری منابع قابل‌فروش ----------

    private sealed record StockTupleKey(int ProductId, int TerminalId, int StorageTankId, int ContractId);

    private CargoSourceQueryService CargoSources => new(_db, _stock, _quantities, _businessClock);

    private async Task<List<GroupSaleSourceItem>> LoadSellableSourcesAsync(bool includeLoadings = true)
    {
        var action = includeLoadings ? CargoAction.DirectSale : CargoAction.PreSaleDelivery;
        var stock = await CargoSources.LoadStockSourcesAsync(_businessClock.Today);
        var dispatches = await CargoSources.LoadDispatchSourcesAsync();
        var transports = await CargoSources.LoadTransportSourcesAsync();
        var all = stock.Concat(dispatches).Concat(transports);
        if (includeLoadings) all = all.Concat(await CargoSources.LoadLoadingSourcesAsync());
        var eligible = all.Where(s => CargoOperationEligibility.Evaluate(s, action).Allowed).ToList();
        var tankIds = eligible.Where(s => s.Kind == CargoSourceKind.Stock && s.StorageTankId.HasValue)
            .Select(s => s.StorageTankId!.Value).Distinct().ToArray();
        var tanks = await _db.StorageTanks.AsNoTracking().Where(t => tankIds.Contains(t.Id))
            .Select(t => new { t.Id, Name = t.DisplayName ?? t.TankCode }).ToDictionaryAsync(t => t.Id, t => t.Name);
        var terminalIds = eligible.Where(s => s.TerminalId.HasValue).Select(s => s.TerminalId!.Value).Distinct().ToArray();
        var terminals = await _db.Terminals.AsNoTracking().Where(t => terminalIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);
        return eligible.Select(s =>
        {
            var kind = s.Kind switch
            {
                CargoSourceKind.Loading => GroupSaleSourceKind.LoadingRegister,
                CargoSourceKind.Stock => GroupSaleSourceKind.TerminalStock,
                CargoSourceKind.Dispatch => GroupSaleSourceKind.TruckDispatch,
                _ => s.VehicleLabel == "واگن" ? GroupSaleSourceKind.WagonLeg : GroupSaleSourceKind.TransportLeg
            };
            var label = s.Kind switch { CargoSourceKind.Loading => "بارگیری", CargoSourceKind.Stock => "موجودی مخزن",
                CargoSourceKind.Dispatch => "موتر در جریان", _ => s.VehicleLabel == "واگن" ? "واگن در جریان" : "انتقال در مسیر" };
            var key = s.Kind switch { CargoSourceKind.Loading => $"Loading:{s.Id}", CargoSourceKind.Dispatch => $"Dispatch:{s.Id}",
                CargoSourceKind.Stock => $"Stock:{s.ProductId}-{s.TerminalId}-{s.StorageTankId}-{s.ContractId}", _ => $"Leg:{s.Id}" };
            return new GroupSaleSourceItem
            {
                Key = key, Kind = kind, Id = s.Id, KindLabel = label, VehicleKind = s.VehicleLabel,
                Number = s.Kind == CargoSourceKind.Stock ? tanks.GetValueOrDefault(s.StorageTankId ?? 0, s.Number) : s.Number,
                Route = s.Kind == CargoSourceKind.Stock ? terminals.GetValueOrDefault(s.TerminalId ?? 0, "") : s.Route,
                ProductName = s.ProductName, CompanyName = s.CompanyName, ContractNumber = s.ContractNumber,
                AvailableMt = decimal.Round(s.Kind == CargoSourceKind.Stock
                    ? (action == CargoAction.PreSaleDelivery ? s.PhysicalStockMt ?? 0m : s.SellableStockMt ?? 0m)
                    : s.RemainingQuantityMt, 4, MidpointRounding.AwayFromZero),
                IsFullVehicle = s.Kind is CargoSourceKind.Transport or CargoSourceKind.Dispatch,
                StatusLabel = s.Kind == CargoSourceKind.Loading ? "ماندهٔ بارگیری" : s.StatusLabel,
                MoveDate = s.Date, ProductId = s.ProductId, CompanyId = s.CompanyId,
                TerminalId = s.TerminalId ?? 0, StorageTankId = s.StorageTankId ?? 0, SourcePurchaseContractId = s.ContractId
            };
        }).OrderByDescending(s => s.MoveDate).ThenBy(s => s.KindLabel).ThenByDescending(s => s.Id).ToList();
    }

    // شمارهٔ محمولهٔ منبعِ فروش را از قرارداد خرید پیدا می‌کند تا فروش گروهی در «فروشات محموله» شمرده شود.
    // (فروش حمل‌ها ShipmentId را از خود leg می‌گیرد؛ این فقط برای موجودی مخزن و موتر است.)
    // اگر قرارداد به چند محموله وصل باشد (مبهم) یا هیچ‌کدام، null برمی‌گرداند و رفتار قبلی حفظ می‌شود.
    private async Task<int?> ResolveShipmentIdForContractAsync(int purchaseContractId)
    {
        if (purchaseContractId <= 0)
        {
            return null;
        }

        var fromShipmentContracts = await _db.ShipmentContracts.AsNoTracking()
            .Where(sc => sc.ContractId == purchaseContractId)
            .Select(sc => sc.ShipmentId)
            .Distinct()
            .ToListAsync();
        if (fromShipmentContracts.Count == 1)
        {
            return fromShipmentContracts[0];
        }

        if (fromShipmentContracts.Count == 0)
        {
            var fromLegs = await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => l.SourcePurchaseContractId == purchaseContractId && l.ShipmentId != null)
                .Select(l => l.ShipmentId!.Value)
                .Distinct()
                .ToListAsync();
            if (fromLegs.Count == 1)
            {
                return fromLegs[0];
            }
        }

        return null;
    }

    // محمولهٔ یک فروشِ از موجودی مخزن، از منشأ فیزیکیِ خودِ مخزن استنباط می‌شود، نه از قرارداد.
    // کاربرِ صفحهٔ فروش نمی‌داند بارِ داخل مخزن از کدام قرارداد خرید آمده (به همین دلیل انتخاب
    // قرارداد منبع اختیاری است)، و یک قرارداد می‌تواند به چند محموله وصل باشد که
    // ResolveShipmentIdForContractAsync را به‌درستی مبهم و بی‌جواب می‌گذارد.
    // اما موجودی فقط از راه رسیدهای انتقال به مخزن می‌رسد و هر رسید محمولهٔ خودش را می‌داند؛
    // اگر همهٔ رسیدهای یک مخزن به یک محموله برسند، فروشِ آن مخزن هم به همان محموله تعلق دارد.
    // در حالت مخلوط (چند محموله در یک مخزن) عمداً null برمی‌گردد تا حدس زده نشود.
    private async Task<int?> ResolveShipmentIdForTankAsync(int storageTankId)
    {
        if (storageTankId <= 0)
        {
            return null;
        }

        var fromReceipts = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.DestinationStorageTankId == storageTankId
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId != null)
            .Select(r => r.InventoryTransportLeg!.ShipmentId!.Value)
            .Distinct()
            .ToListAsync();

        return fromReceipts.Count == 1 ? fromReceipts[0] : null;
    }

    private static string BuildGroupRoute(string? source, string? destination)
    {
        if (!string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(destination))
        {
            return $"{(string.IsNullOrWhiteSpace(source) ? "؟" : source)} ← {(string.IsNullOrWhiteSpace(destination) ? "؟" : destination)}";
        }

        return "-";
    }

    private async Task PopulateGroupSaleLookupsAsync(GroupSaleCreateViewModel model)
    {
        await PopulateBuyerLookupAsync(model.BuyerKey);
        ViewBag.LoadingSaleTerminals = new SelectList(await _db.Terminals.AsNoTracking()
            .OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync(),
            "Id", "Name", model.LoadingSaleTerminalId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code)
                .Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
    }

    // ---------- CreateGroup ----------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroup(string? returnUrl = null)
    {
        var model = new GroupSaleCreateViewModel
        {
            SaleDate = _businessClock.Today,
            Currency = SystemCurrency.BaseCurrencyCode,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var local) ? local : null
        };

        await PopulateGroupSaleLookupsAsync(model);
        ViewBag.Sources = await LoadSellableSourcesAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(GroupSaleCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        _groupAccountingSkippedReasons.Clear();
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        model.PaymentNote = string.IsNullOrWhiteSpace(model.PaymentNote) ? null : model.PaymentNote.Trim();

        await ValidateBuyerAsync(model.CustomerId, model.SupplierId);

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        if (model.IsSalePricePending)
        {
            // قیمت باز برای همهٔ ردیف‌های این فروش گروهی؛ هر ردیف بعداً جدا قیمت‌گذاری می‌شود.
            ModelState.Remove(nameof(model.UnitPriceInCurrency));
            ModelState.Remove(nameof(model.AppliedFxRateToUsd));
            model.UnitPriceInCurrency = 0m;
            model.AppliedFxRateToUsd = null;
        }
        else if (model.UnitPriceInCurrency <= 0m)
        {
            ModelState.AddModelError(nameof(model.UnitPriceInCurrency), "نرخ فروش هر تن باید بزرگ‌تر از صفر باشد.");
        }

        if (model.SaleDate == default)
        {
            ModelState.AddModelError(nameof(model.SaleDate), "تاریخ فروش الزامی است.");
        }

        // انتخاب‌ها: dedupe (وسیله با Kind:Id، مخزن با tuple).
        var selections = (model.Items ?? [])
            .Where(i => i.Kind == GroupSaleSourceKind.TerminalStock
                ? (i.ProductId > 0 && i.TerminalId > 0 && i.StorageTankId > 0 && i.SourcePurchaseContractId > 0)
                : i.Id > 0)
            .GroupBy(i => i.Kind == GroupSaleSourceKind.TerminalStock
                ? $"Stock:{i.ProductId}-{i.TerminalId}-{i.StorageTankId}-{i.SourcePurchaseContractId}"
                : $"{i.Kind}:{i.Id}")
            .Select(g => g.First())
            .ToList();

        if (selections.Any(i => !Enum.IsDefined(i.Kind)))
            ModelState.AddModelError(string.Empty, "نوع منبع فروش معتبر نیست.");

        if (selections.Any(i => i.Kind == GroupSaleSourceKind.LoadingRegister)
            && !(model.LoadingSaleTerminalId is > 0 && await _db.Terminals.AsNoTracking()
                .AnyAsync(t => t.Id == model.LoadingSaleTerminalId)))
            ModelState.AddModelError(nameof(model.LoadingSaleTerminalId), "محل فروش مستقیم بارگیری را انتخاب کنید.");

        if (selections.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک منبع فروش را انتخاب کنید.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateGroupSaleLookupsAsync(model);
            ViewBag.Sources = await LoadSellableSourcesAsync();
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = model.IsSalePricePending
                ? SalePendingPricing.NeutralConversion(model.Currency, model.SaleDate)
                : await _currencyConversion.ResolveToBaseAsync(model.Currency, model.SaleDate.Date, model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            await PopulateGroupSaleLookupsAsync(model);
            ViewBag.Sources = await LoadSellableSourcesAsync();
            return View(model);
        }

        var receiptService = _receiptService;

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            // Lock all selected loadings in stable order before any remainder is re-read.
            var loadingIds = selections.Where(i => i.Kind == GroupSaleSourceKind.LoadingRegister)
                .Select(i => i.Id).Distinct().OrderBy(i => i).ToArray();
            if (loadingIds.Length > 0 && _db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"SELECT 1 FROM ""LoadingRegisters"" WHERE ""Id"" = ANY({loadingIds}) ORDER BY ""Id"" FOR UPDATE");

            var loadingSources = (await CargoSources.LoadLoadingSourcesAsync(loadingIds))
                .ToDictionary(s => s.Id);
            var sourceLoadings = await _db.LoadingRegisters.AsNoTracking().Include(l => l.Contract)
                .Where(l => loadingIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);

            var batch = new SalesBatch
            {
                CustomerId = model.CustomerId,
                SupplierId = model.SupplierId,
                SaleDate = model.SaleDate.Date,
                Currency = conversion.SourceCurrencyCode,
                AppliedFxRateToUsd = model.IsSalePricePending ? null : conversion.AppliedRateToBase,
                UnitPriceInCurrency = model.UnitPriceInCurrency,
                LineCount = selections.Count,
                Notes = model.Notes,
                PaymentNote = model.PaymentNote
            };
            _formTokens.Stamp(formToken, "Sale.CreateGroup", nameof(SalesBatch));
            _db.SalesBatches.Add(batch);
            await _db.SaveChangesAsync();

            batch.BatchNumber = $"GSALE-{batch.Id}";
            await _db.SaveChangesAsync();

            decimal totalQty = 0m, totalInCurrency = 0m, totalUsd = 0m;
            var lineNo = 0;

            foreach (var selection in selections)
            {
                lineNo++;
                var invoice = $"{batch.BatchNumber}-{lineNo}";

                var owner = new SaleLineOwner(batch.Id, null, batch.BatchNumber);
                using var row = new SavedRowTrackingScope(_db);
                var sale = selection.Kind switch
                {
                    GroupSaleSourceKind.TerminalStock =>
                        await CreateTerminalStockLineAsync(owner, selection, model, conversion, invoice),
                    GroupSaleSourceKind.TruckDispatch =>
                        await CreateTruckDispatchLineAsync(owner, selection, model, conversion, invoice),
                    GroupSaleSourceKind.LoadingRegister =>
                        await CreateLoadingLineAsync(owner, selection, model, conversion, invoice,
                            loadingSources.GetValueOrDefault(selection.Id), sourceLoadings.GetValueOrDefault(selection.Id)),
                    GroupSaleSourceKind.WagonLeg or GroupSaleSourceKind.TransportLeg =>
                        await CreateLegLineAsync(owner, selection, model, conversion, invoice, receiptService),
                    _ => throw new BusinessRuleException("GROUP_SALE_SOURCE_INVALID", "نوع منبع فروش معتبر نیست.")
                };

                totalQty += sale.QuantityMt;
                totalInCurrency += sale.TotalInCurrency;
                totalUsd += sale.TotalUsd;
                row.ReleaseSaved();
            }

            batch.TotalQuantityMt = decimal.Round(totalQty, 4, MidpointRounding.AwayFromZero);
            batch.TotalInCurrency = decimal.Round(totalInCurrency, 4, MidpointRounding.AwayFromZero);
            batch.TotalUsd = decimal.Round(totalUsd, 4, MidpointRounding.AwayFromZero);
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(SalesBatch),
                batch.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("BatchNumber", batch.BatchNumber),
                    ("CustomerId", batch.CustomerId),
                    ("SupplierId", batch.SupplierId),
                    ("SaleDate", batch.SaleDate),
                    ("Currency", batch.Currency),
                    ("UnitPriceInCurrency", batch.UnitPriceInCurrency),
                    ("TotalQuantityMt", batch.TotalQuantityMt),
                    ("TotalUsd", batch.TotalUsd),
                    ("LineCount", batch.LineCount)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = model.IsSalePricePending
                ? $"فروش گروهی {batch.BatchNumber} برای {selections.Count} منبع با قیمت باز ثبت شد؛ قیمت هر ردیف از صفحهٔ همان فروش تعیین می‌شود."
                : $"فروش گروهی {batch.BatchNumber} برای {selections.Count} منبع ثبت شد.";
            if (_groupAccountingSkippedReasons.Count > 0)
                TempData["warn"] = "فروش ثبت شد؛ برخی اسناد حسابداری ثبت نشده‌اند: "
                    + string.Join("؛ ", _groupAccountingSkippedReasons.Select(r => $"{r.Key} ({r.Value} مورد)"));
            return RedirectToAction(nameof(GroupDetails), new { id = batch.Id });
        }
        catch (Exception ex) when (_formTokens.IsDuplicate(ex))
        {
            if (transaction is not null) await transaction.RollbackAsync();
            TempData["ok"] = "این درخواست فروش گروهی قبلاً ثبت شده است.";
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to create group sale batch.");
            ModelState.AddModelError(string.Empty, "ثبت فروش گروهی انجام نشد. لطفاً منابع و مقادیر را بررسی و دوباره تلاش کنید.");
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        await PopulateGroupSaleLookupsAsync(model);
        ViewBag.Sources = await LoadSellableSourcesAsync();
        return View(model);
    }

    // ---------- ساخت ردیف‌ها (هر کدام از primitiveهای فروشِ موجود) ----------

    private async Task<SalesTransaction> CreateLoadingLineAsync(
        SaleLineOwner owner, GroupSaleSelectedInput input, GroupSaleCreateViewModel model, CurrencyConversionResult conversion, string invoice,
        CargoSourceSnapshot? source, LoadingRegister? loading)
    {
        if (source is null || loading is null)
            throw new BusinessRuleException("GROUP_SALE_LOADING_MISSING", "بارگیری انتخاب‌شده یافت نشد.");
        var eligibility = CargoOperationEligibility.Evaluate(source, CargoAction.DirectSale);
        if (!eligibility.Allowed)
            throw new BusinessRuleException("GROUP_SALE_LOADING_NOT_ELIGIBLE", eligibility.Reason!);
        if (model.SaleDate.Date < loading.LoadingDate.Date)
            throw new BusinessRuleException("GROUP_SALE_LOADING_DATE_INVALID", "تاریخ فروش نمی‌تواند قبل از تاریخ بارگیری باشد؛ نخست تاریخ بارگیری را بررسی کنید.");
        var quantity = input.QuantityMt ?? 0m;
        if (quantity <= 0m || decimal.Round(quantity, 4, MidpointRounding.AwayFromZero) != quantity)
            throw new BusinessRuleException("GROUP_SALE_LOADING_QUANTITY", "مقدار فروش بارگیری باید مثبت و حداکثر چهار رقم اعشار باشد.");
        if (quantity > source.RemainingQuantityMt)
            throw new BusinessRuleException("GROUP_SALE_LOADING_REMAINDER", "مقدار فروش از ماندهٔ معتبر بارگیری بیشتر است.");
        var draft = LoadingDirectSaleDraftService.Build(new LoadingReceiptAllocationLineInput
        {
            QuantityMt = quantity, SaleDate = model.SaleDate, SaleCurrency = model.Currency,
            SaleAppliedFxRateToUsd = conversion.AppliedRateToBase, SaleUnitPriceInCurrency = model.UnitPriceInCurrency,
            SaleIsPricePending = model.IsSalePricePending,
            SaleCustomerId = model.CustomerId, SaleSupplierId = model.SupplierId,
            SaleInvoiceNumber = invoice, SaleNotes = model.Notes
        }, loading, conversion);
        var sale = draft.Sale;
        sale.SalesBatchId = owner.SalesBatchId;
        sale.PreSaleOrderId = owner.PreSaleOrderId;
        var receipt = new LoadingReceipt
        {
            LoadingRegisterId = loading.Id, TerminalId = model.LoadingSaleTerminalId!.Value,
            ReceiptDate = model.SaleDate.Date, ReceivedQuantityMt = quantity,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch, LossMode = ReceiptLossMode.ImmediateKnownLoss,
            ReferenceDocument = invoice, Notes = model.Notes
        };
        var allocation = new LoadingReceiptAllocation
        {
            LoadingReceipt = receipt, SalesTransaction = sale,
            SourcePurchaseContractId = loading.ContractId,
            Destination = LoadingReceiptAllocationDestination.DirectSale,
            Status = LoadingReceiptAllocationStatus.Completed, QuantityMt = quantity,
            TerminalId = receipt.TerminalId, ReferenceDocument = invoice, Notes = model.Notes
        };
        _db.LoadingReceipts.Add(receipt);
        _db.SalesTransactions.Add(sale);
        _db.LoadingReceiptAllocations.Add(allocation);
        await _db.SaveChangesAsync();
        if (SalePendingPricing.ShouldPostLedger(sale))
            Ledger.Post(SaleLedgerFactory.BuildSaleLedgerEntry(sale, draft.Conversion, loading.ContractId));
        await _db.SaveChangesAsync();
        if (_salesAccounting is null)
            RecordGroupAccountingSkip("حسابداری این مسیر فعال نیست");
        else
        {
            var revenue = await _salesAccounting.TryPostSaleAsync(sale);
            var cogs = await _salesAccounting.TryPostCogsAsync(sale);
            foreach (var result in new[] { revenue, cogs })
            {
                if (result.Status != Services.Accounting.PaymentPostingStatus.Skipped) continue;
                // عایدِ فروش قیمت‌باز عمداً تا تعیین قیمت ثبت نمی‌شود؛ کمبود نیست.
                if (result.Reason == SalePendingPricing.PendingSkipReason) continue;
                RecordGroupAccountingSkip(result.Reason switch
                {
                    "ACCOUNTING_DISABLED" or "PILOT_DISABLED" => "ثبت حسابداری این عملیات فعال نیست",
                    "SOURCE_PURCHASE_NOT_POSTED_AT_SALE" => "قیمت یا سند خرید در زمان فروش قطعی نشده بود",
                    "SOURCE_PURCHASE_NOT_VALUED" => "بهای خرید منبع مشخص نیست",
                    "DIRECT_SALE_RECEIPT_HAS_INVENTORY_JOURNAL_NEEDS_REVIEW" or "DIRECT_SALE_HISTORICAL_QUANTITY_NEEDS_REVIEW" => "سند تاریخی رسید نیاز به بررسی مالی دارد",
                    "DIRECT_SALE_SOURCE_QUANTITY_MISMATCH" => "مقدار فروش با سهم بار منبع مطابقت ندارد",
                    "ACCOUNTING_SETTINGS_MISSING" or "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS" or "IN_TRANSIT_ACCOUNT_MISSING" => "حساب‌های لازم برای ثبت مالی تنظیم نشده‌اند",
                    "SALE_COMPANY_UNKNOWN" => "شرکت مالک فروش مشخص نیست",
                    "INVALID_SALE_FX" or "INVALID_SALE_CONVERSION" => "نرخ یا تبدیل ارز فروش نیاز به بررسی دارد",
                    "SALE_CANCELLED" => "فروش لغو شده است",
                    _ => "ثبت حسابداری این فروش نیاز به بررسی مالی دارد"
                });
            }
        }
        await _audit.LogAndSaveAsync(nameof(LoadingReceipt), receipt.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(("LoadingRegisterId", loading.Id),
                ("ReceivedQuantityMt", quantity), ("ReceiptDestination", receipt.ReceiptDestination)));
        await _audit.LogAndSaveAsync(nameof(LoadingReceiptAllocation), allocation.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(("LoadingReceiptId", receipt.Id),
                ("SourcePurchaseContractId", loading.ContractId), ("SalesTransactionId", sale.Id), ("QuantityMt", quantity)));
        return sale;
    }

    private void RecordGroupAccountingSkip(string reason)
        => _groupAccountingSkippedReasons[reason] = _groupAccountingSkippedReasons.GetValueOrDefault(reason) + 1;

    private async Task<SalesTransaction> CreateTerminalStockLineAsync(
        SaleLineOwner owner,
        GroupSaleSelectedInput input,
        GroupSaleCreateViewModel model,
        CurrencyConversionResult conversion,
        string invoice)
    {
        var qty = input.QuantityMt ?? 0m;
        if (qty <= 0m)
        {
            throw new BusinessRuleException("GROUP_SALE_QTY_REQUIRED", "برای فروش از موجودی مخزن، مقدار هر ردیف باید بزرگ‌تر از صفر باشد.");
        }

        var contract = await _db.Contracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == input.SourcePurchaseContractId && c.ContractType == ContractType.Purchase)
            ?? throw new BusinessRuleException("GROUP_SALE_STOCK_CONTRACT_INVALID", "قرارداد خرید منبع موجودی معتبر نیست.");

        if (contract.ProductId != input.ProductId)
        {
            throw new BusinessRuleException("GROUP_SALE_STOCK_PRODUCT_MISMATCH", "کالای منبع موجودی با قرارداد هم‌خوان نیست.");
        }

        var lockedTank = await LockStorageTankAsync(input.StorageTankId)
            ?? throw new BusinessRuleException("GROUP_SALE_STOCK_TANK_NOT_FOUND", "مخزن منبع دیگر معتبر نیست.");
        if (lockedTank.TerminalId != input.TerminalId)
        {
            throw new BusinessRuleException("GROUP_SALE_STOCK_TANK_TERMINAL", "مخزن به ترمینال انتخابی تعلق ندارد.");
        }

        var source = (await CargoSources.LoadStockSourcesAsync(model.SaleDate.Date,
            productId: input.ProductId, terminalId: input.TerminalId, storageTankId: input.StorageTankId,
            contractId: input.SourcePurchaseContractId)).SingleOrDefault()
            ?? throw new BusinessRuleException("GROUP_SALE_STOCK_SOURCE_MISSING", "منبع موجودی انتخاب‌شده ماندهٔ معتبر ندارد.");
        var action = owner.PreSaleOrderId.HasValue ? CargoAction.PreSaleDelivery : CargoAction.DirectSale;
        var eligibility = CargoOperationEligibility.Evaluate(source, action);
        if (!eligibility.Allowed)
            throw new BusinessRuleException("GROUP_SALE_STOCK_SOURCE_NOT_ELIGIBLE", eligibility.Reason!);
        if (!owner.PreSaleOrderId.HasValue && qty > source.SellableStockMt)
            throw new BusinessRuleException("GROUP_SALE_STOCK_RESERVED", "مقدار فروش از موجودی قابل فروش پس از پیش‌فروش‌ها بیشتر است.");

        var allocations = await EnsureSufficientTerminalStockAsync(
            input.ProductId, qty, model.SaleDate.Date,
            input.TerminalId, input.StorageTankId, contract.CompanyId, input.SourcePurchaseContractId);

        var totalInCurrency = decimal.Round(qty * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var sale = new SalesTransaction
        {
            ContractId = null,
            CompanyId = contract.CompanyId,
            CustomerId = model.CustomerId,
            SupplierId = model.SupplierId,
            ProductId = input.ProductId,
            ShipmentId = await ResolveShipmentIdForContractAsync(input.SourcePurchaseContractId),
            SaleStage = SaleStage.TerminalStock,
            SalesBatchId = owner.SalesBatchId,
            PreSaleOrderId = owner.PreSaleOrderId,
            InvoiceNumber = invoice,
            SaleDate = model.SaleDate.Date,
            QuantityMt = qty,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = model.Notes
        };
        if (model.IsSalePricePending)
            SalePendingPricing.MarkPending(sale, model.Currency);
        _db.SalesTransactions.Add(sale);
        await _db.SaveChangesAsync();

        var movements = allocations.Select(allocation => new InventoryMovement
        {
            ProductId = input.ProductId,
            ContractId = allocation.ContractId,
            TerminalId = input.TerminalId,
            StorageTankId = input.StorageTankId,
            SalesTransactionId = sale.Id,
            MovementDate = sale.SaleDate,
            QuantityMt = allocation.QuantityMt,
            ReferenceDocument = sale.InvoiceNumber,
            Notes = BuildSaleInventoryNotes(sale.SaleStage, sale.InvoiceNumber, $"SaleId={sale.Id} | {owner.Reference}")
        }).ToList();
        await _movements.PostOutboundRangeAsync(movements, StockGuard.Full);

        var sourcePlan = await _sourceAllocations.BuildFromInventoryMovementsAsync(movements, sale.QuantityMt);
        _sourceAllocations.ApplyLegacyHeader(sale, sourcePlan);
        await _sourceAllocations.PersistSaleAsync(sale, sourcePlan);

        await _lineage.AllocateSaleAsync(sale, input.SourcePurchaseContractId, input.TerminalId, input.StorageTankId);
        if (sale.IsSalePricePending)
            await ClearPendingSaleLotAmountsAsync(sale.Id);

        // AUD-06: مثل مسیر فروش تکی، قرارداد Ledger از همان planِ FIFO می‌آید (که
        // ApplyLegacyHeader روی SourcePurchaseContractId نشانده) نه از انتخاب کاربر.
        if (SalePendingPricing.ShouldPostLedger(sale))
            Ledger.Post(
                SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: sale.SourcePurchaseContractId));
        await _db.SaveChangesAsync();

        await PostSaleAccountingAsync(sale);

        return sale;
    }

    private async Task<SalesTransaction> CreateTruckDispatchLineAsync(
        SaleLineOwner owner,
        GroupSaleSelectedInput input,
        GroupSaleCreateViewModel model,
        CurrencyConversionResult conversion,
        string invoice)
    {
        var dispatch = await _db.TruckDispatches
            .Include(d => d.Contract)
            .FirstOrDefaultAsync(d => d.Id == input.Id)
            ?? throw new BusinessRuleException("GROUP_SALE_DISPATCH_NOT_FOUND", "موتر انتخاب‌شده یافت نشد.");

        if (dispatch.SalesTransactionId.HasValue)
        {
            throw new BusinessRuleException("GROUP_SALE_DISPATCH_ALREADY_SOLD", $"موتر #{dispatch.Id} قبلاً فروخته شده است.");
        }

        if (dispatch.Status is not (DispatchStatus.Loaded or DispatchStatus.InTransit))
        {
            throw new BusinessRuleException("GROUP_SALE_DISPATCH_NOT_IN_TRANSIT", $"موتر #{dispatch.Id} دیگر در جریان نیست.");
        }

        if (await TransportChainProjection.IsContinuationProjectionAsync(_db, dispatch))
        {
            throw new BusinessRuleException(
                "GROUP_SALE_DISPATCH_CHAIN_PROJECTION",
                $"موتر #{dispatch.Id} رکورد سازگاریِ ادامهٔ حمل است؛ فروش را از خودِ حملِ در جریان ثبت کنید.");
        }

        var sourceContract = dispatch.Contract
            ?? throw new BusinessRuleException("GROUP_SALE_DISPATCH_CONTRACT", "قرارداد خرید این موتر معتبر نیست.");

        var source = (await CargoSources.LoadDispatchSourcesAsync([dispatch.Id])).SingleOrDefault()
            ?? throw new BusinessRuleException("GROUP_SALE_DISPATCH_SOURCE_MISSING", "موتر انتخاب‌شده منبع فروش فعال ندارد.");
        var eligibility = CargoOperationEligibility.Evaluate(source,
            owner.PreSaleOrderId.HasValue ? CargoAction.PreSaleDelivery : CargoAction.DirectSale);
        if (!eligibility.Allowed)
            throw new BusinessRuleException("GROUP_SALE_DISPATCH_SOURCE_NOT_ELIGIBLE", eligibility.Reason!);
        var qty = source.RemainingQuantityMt;
        var currentLegId = await _sourceAllocations.ResolveCurrentLegIdAsync(dispatch);
        var sourcePlan = currentLegId.HasValue
            ? await _sourceAllocations.BuildFromLegAsync(currentLegId.Value, qty)
            : TransportSourcePlan.Empty;
        var totalInCurrency = decimal.Round(qty * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var sale = new SalesTransaction
        {
            ContractId = null,
            CompanyId = sourceContract.CompanyId,
            CustomerId = model.CustomerId,
            SupplierId = model.SupplierId,
            ProductId = dispatch.ProductId,
            DestinationLocationId = dispatch.DestinationLocationId,
            ShipmentId = currentLegId.HasValue
                ? await _db.InventoryTransportLegs.AsNoTracking()
                    .Where(l => l.Id == currentLegId.Value)
                    .Select(l => l.ShipmentId)
                    .FirstOrDefaultAsync()
                : await ResolveShipmentIdForContractAsync(dispatch.ContractId),
            SourcePurchaseContractId = sourceContract.Id,
            TruckDispatchId = dispatch.Id,
            SaleStage = SaleStage.InTransit,
            SalesBatchId = owner.SalesBatchId,
            PreSaleOrderId = owner.PreSaleOrderId,
            InvoiceNumber = invoice,
            SaleDate = model.SaleDate.Date,
            QuantityMt = qty,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = model.Notes,
            TicketSerialNumber = dispatch.TicketSerialNumber
        };
        if (model.IsSalePricePending)
            SalePendingPricing.MarkPending(sale, model.Currency);
        if (sourcePlan.Shares.Count > 0)
        {
            _sourceAllocations.ApplyLegacyHeader(sale, sourcePlan);
        }
        _db.SalesTransactions.Add(sale);
        await _db.SaveChangesAsync();
        await _sourceAllocations.PersistSaleAsync(sale, sourcePlan, currentLegId);

        dispatch.SalesTransactionId = sale.Id;

        if (SalePendingPricing.ShouldPostLedger(sale))
            Ledger.Post(SaleLedgerFactory.BuildSaleLedgerEntry(
                sale,
                conversion,
                contractId: sourcePlan.Shares.Count > 0 ? sourcePlan.SingleContractId : sourceContract.Id));
        await _db.SaveChangesAsync();

        await PostSaleAccountingAsync(sale);

        return sale;
    }

    private async Task<SalesTransaction> CreateLegLineAsync(
        SaleLineOwner owner,
        GroupSaleSelectedInput input,
        GroupSaleCreateViewModel model,
        CurrencyConversionResult conversion,
        string invoice,
        InventoryTransportReceiptService receiptService,
        // فقط مسیر تحویل پیش‌فروش از حملِ کشتی (TransportLeg) این را می‌فرستد → تحویل جزئی.
        // null یعنی مسیر تاریخیِ فروش گروهی/واگن: کل باقیماندهٔ حمل فروخته می‌شود (بدون تغییر رفتار).
        decimal? requestedQtyMt = null)
    {
        var leg = await receiptService.LoadLegAsync(input.Id, tracking: true)
            ?? throw new BusinessRuleException("GROUP_SALE_LEG_NOT_FOUND", "حمل انتخاب‌شده یافت نشد.");

        if (leg.Status is not (InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit))
        {
            throw new BusinessRuleException("GROUP_SALE_LEG_NOT_IN_TRANSIT", $"حمل #{leg.Id} دیگر در جریان نیست.");
        }

        var source = (await CargoSources.LoadTransportSourcesAsync([leg.Id])).Single();
        var eligibility = CargoOperationEligibility.Evaluate(source,
            owner.PreSaleOrderId.HasValue ? CargoAction.PreSaleDelivery : CargoAction.DirectSale);
        if (!eligibility.Allowed)
            throw new BusinessRuleException("GROUP_SALE_LEG_SOURCE_NOT_ELIGIBLE", eligibility.Reason!);
        var sellableMt = source.RemainingQuantityMt;

        // مقدار این تحویل: در مسیر تاریخی کل باقیمانده؛ در مسیر جزئی فقط مقدار واردشدهٔ کاربر،
        // سقف‌گذاری‌شده به باقیماندهٔ واقعی حمل (سقف مانده پیش‌فروش جداگانه در PreSaleDeliver کنترل می‌شود).
        var receiptQtyMt = sellableMt;
        if (requestedQtyMt is not null)
        {
            receiptQtyMt = decimal.Round(requestedQtyMt.Value, 4, MidpointRounding.AwayFromZero);
            if (receiptQtyMt <= 0m)
            {
                throw new BusinessRuleException("GROUP_SALE_LEG_QTY_REQUIRED", "مقدار تحویل باید بزرگ‌تر از صفر باشد.");
            }

            if (receiptQtyMt > sellableMt + 0.0001m)
            {
                throw new BusinessRuleException(
                    "GROUP_SALE_LEG_OVER_AVAILABLE",
                    $"مقدار تحویل از موجودی قابل فروش این حمل ({sellableMt:N4} تن) بیشتر است.");
            }
        }

        var receiptModel = new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDate = model.SaleDate.Date,
            ReceivedQuantityMt = receiptQtyMt,
            ShortageQuantityMt = 0m,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            SaleCustomerId = model.CustomerId,
            SaleSupplierId = model.SupplierId,
            SaleInvoiceNumber = invoice,
            SaleDate = model.SaleDate.Date,
            SaleCurrency = model.Currency,
            SaleUnitPriceInCurrency = model.IsSalePricePending ? null : model.UnitPriceInCurrency,
            SaleIsPricePending = model.IsSalePricePending,
            SaleAppliedFxRateToUsd = model.AppliedFxRateToUsd,
            Notes = model.Notes
        };

        await receiptService.ValidateAsync(receiptModel, leg, ModelState, keyPrefix: $"leg{leg.Id}.");
        if (!ModelState.IsValid)
        {
            var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage
                ?? "اطلاعات فروش حمل معتبر نیست.";
            throw new BusinessRuleException("GROUP_SALE_LEG_INVALID", $"حمل #{leg.Id}: {firstError}");
        }

        var saleConversion = await receiptService.ResolveSaleConversionAsync(receiptModel, ModelState, keyPrefix: $"leg{leg.Id}.");
        if (saleConversion is null)
        {
            throw new BusinessRuleException("GROUP_SALE_LEG_FX", $"حمل #{leg.Id}: نرخ تبدیل ارز فروش قابل محاسبه نیست.");
        }

        var receipt = await receiptService.ApplyAsync(receiptModel, leg, saleConversion);

        var sale = await _db.SalesTransactions.FirstOrDefaultAsync(s => s.Id == receipt.SalesTransactionId)
            ?? throw new BusinessRuleException("GROUP_SALE_LEG_SALE_MISSING", $"سند فروش حمل #{leg.Id} ساخته نشد.");
        sale.SalesBatchId = owner.SalesBatchId;
        sale.PreSaleOrderId = owner.PreSaleOrderId;
        await _db.SaveChangesAsync();

        return sale;
    }

    // ---------- GroupDetails ----------

    public async Task<IActionResult> GroupDetails(int id)
    {
        var batch = await _db.SalesBatches
            .AsNoTracking()
            .Include(b => b.Customer)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (batch is null)
        {
            return NotFound();
        }

        var lines = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.SalesBatchId == batch.Id)
            .OrderBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.SaleStage,
                s.ProductId,
                ProductName = s.Product != null ? s.Product.Name : "",
                s.InvoiceNumber,
                s.QuantityMt,
                s.TotalInCurrency,
                s.TotalUsd,
                s.IsCancelled,
                s.IsSalePricePending
            })
            .ToListAsync();

        var saleIds = lines.Select(l => l.Id).ToArray();

        var dispatchBySale = await _db.TruckDispatches.AsNoTracking()
            .Where(d => d.SalesTransactionId != null && saleIds.Contains(d.SalesTransactionId!.Value))
            .Select(d => new { SaleId = d.SalesTransactionId!.Value, Plate = d.Truck != null ? d.Truck.PlateNumber : null })
            .ToDictionaryAsync(x => x.SaleId, x => x.Plate);

        var legBySale = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => r.SalesTransactionId != null && saleIds.Contains(r.SalesTransactionId!.Value) && !r.IsCancelled)
            .Select(r => new
            {
                SaleId = r.SalesTransactionId!.Value,
                r.InventoryTransportLeg!.TransportType,
                Number = r.InventoryTransportLeg.WagonNumber ?? r.InventoryTransportLeg.RwbNo,
                Plate = r.InventoryTransportLeg.Truck != null ? r.InventoryTransportLeg.Truck.PlateNumber : null
            })
            .ToDictionaryAsync(x => x.SaleId);

        var loadingBySale = await _db.LoadingReceiptAllocations.AsNoTracking()
            .Where(a => a.SalesTransactionId.HasValue && saleIds.Contains(a.SalesTransactionId.Value))
            .Select(a => new { SaleId = a.SalesTransactionId!.Value,
                Number = a.LoadingReceipt!.LoadingRegister!.WagonNumber
                    ?? a.LoadingReceipt.LoadingRegister.RwbNo ?? a.ReferenceDocument,
                a.LoadingReceipt.LoadingRegister.TransportType })
            .ToDictionaryAsync(a => a.SaleId);

        // تطبیقِ نقد: هر ردیف می‌داند چه مقدار از دریافت‌ها روی خودش نشسته و چه مقدار باز مانده.
        var applications = await LoadReceiptApplicationsAsync(saleIds);
        var appliedBySale = applications
            .GroupBy(a => a.SalesTransactionId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AppliedAmountUsd));
        var legacyBySale = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.SalesTransactionId != null
                && saleIds.Contains(p.SalesTransactionId!.Value)
                && !_db.CustomerPaymentAllocationApplications.Any(a =>
                    a.PaymentTransactionId == p.Id
                    && a.SalesTransactionId == p.SalesTransactionId
                    && a.Status == CustomerPaymentAllocationApplicationStatus.Active))
            .GroupBy(p => p.SalesTransactionId!.Value)
            .Select(g => new
            {
                SaleId = g.Key,
                Usd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : -p.AmountUsd)
            })
            .ToDictionaryAsync(x => x.SaleId, x => x.Usd);

        decimal ReceivedUsdOf(int saleId)
            => decimal.Round(
                (appliedBySale.TryGetValue(saleId, out var applied) ? applied : 0m)
                    + (legacyBySale.TryGetValue(saleId, out var legacy) ? legacy : 0m),
                4,
                MidpointRounding.AwayFromZero);

        var applicableReceipts = batch.IsCancelled || batch.SupplierId.HasValue || !batch.CustomerId.HasValue
            ? []
            : await LoadApplicableReceiptsAsync(batch.CustomerId.Value);

        var vm = new GroupSaleDetailsViewModel
        {
            Id = batch.Id,
            BatchNumber = batch.BatchNumber,
            CustomerName = batch.Customer?.Name ?? batch.Supplier?.Name ?? "",
            SaleDate = batch.SaleDate,
            Currency = batch.Currency,
            AppliedFxRateToUsd = batch.AppliedFxRateToUsd,
            UnitPriceInCurrency = batch.UnitPriceInCurrency,
            TotalQuantityMt = batch.TotalQuantityMt,
            TotalInCurrency = batch.TotalInCurrency,
            TotalUsd = batch.TotalUsd,
            LineCount = batch.LineCount,
            PaymentNote = batch.PaymentNote,
            Notes = batch.Notes,
            IsCancelled = batch.IsCancelled,
            Lines = lines.Select(l =>
            {
                string kindLabel, vehicleKind, number;
                if (l.SaleStage == SaleStage.TerminalStock)
                {
                    kindLabel = "موجودی مخزن"; vehicleKind = "مخزن"; number = "-";
                }
                else if (dispatchBySale.TryGetValue(l.Id, out var plate))
                {
                    kindLabel = "موتر در جریان"; vehicleKind = "موتر"; number = plate ?? "-";
                }
                else if (legBySale.TryGetValue(l.Id, out var leg))
                {
                    var isWagon = leg.TransportType == LoadingTransportType.Wagon;
                    kindLabel = isWagon ? "واگن در جریان" : "انتقال در مسیر";
                    vehicleKind = isWagon ? "واگن" : "انتقال";
                    number = leg.Number ?? leg.Plate ?? "-";
                }
                else if (loadingBySale.TryGetValue(l.Id, out var loading))
                {
                    kindLabel = "بارگیری";
                    vehicleKind = loading.TransportType == LoadingTransportType.Wagon ? "واگن"
                        : loading.TransportType == LoadingTransportType.Truck ? "موتر"
                        : loading.TransportType == LoadingTransportType.Vessel ? "کشتی" : "نامشخص";
                    number = loading.Number ?? "-";
                }
                else
                {
                    kindLabel = "فروش"; vehicleKind = "-"; number = "-";
                }

                return new GroupSaleLineViewModel
                {
                    SalesTransactionId = l.Id,
                    KindLabel = kindLabel,
                    VehicleKind = vehicleKind,
                    Number = number,
                    ProductName = l.ProductName,
                    InvoiceNumber = l.InvoiceNumber,
                    QuantityMt = l.QuantityMt,
                    TotalInCurrency = l.TotalInCurrency,
                    TotalUsd = l.TotalUsd,
                    IsCancelled = l.IsCancelled,
                    IsSalePricePending = l.IsSalePricePending,
                    ReceivedUsd = l.IsCancelled ? 0m : ReceivedUsdOf(l.Id),
                    // قیمت باز: طلبی هنوز وجود ندارد.
                    OpenReceivableUsd = l.IsCancelled || l.IsSalePricePending
                        ? 0m
                        : Math.Max(decimal.Round(l.TotalUsd - ReceivedUsdOf(l.Id), 4, MidpointRounding.AwayFromZero), 0m)
                };
            }).ToList(),
            CustomerId = batch.CustomerId,
            IsSupplierBuyer = batch.SupplierId.HasValue,
            ReceivedUsd = decimal.Round(
                lines.Where(l => !l.IsCancelled).Sum(l => ReceivedUsdOf(l.Id)), 4, MidpointRounding.AwayFromZero),
            OpenReceivableUsd = Math.Max(decimal.Round(
                lines.Where(l => !l.IsCancelled && !l.IsSalePricePending).Sum(l => l.TotalUsd - ReceivedUsdOf(l.Id)),
                4,
                MidpointRounding.AwayFromZero), 0m),
            Applications = applications,
            ApplicableReceipts = applicableReceipts
        };

        return View(vm);
    }

    // ---------- CancelGroup ----------
    // هر ردیف را با همان الگوی لغوِ فروش تکی برمی‌گرداند (لجرِ معکوس + بازگشت موجودی)،
    // و لینک وسیله را آزاد می‌کند تا موتر/واگن دوباره «در جریان» شود.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelGroup(int id)
    {
        var batch = await _db.SalesBatches.FirstOrDefaultAsync(b => b.Id == id);
        if (batch is null)
        {
            return NotFound();
        }

        if (batch.IsCancelled)
        {
            TempData["ok"] = "این فروش گروهی قبلاً لغو شده است.";
            return RedirectToAction(nameof(GroupDetails), new { id });
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var coordinator = new SalesBatchCancellationCoordinator(_db);
            await coordinator.LockAsync(batch.Id, []);
            await _db.Entry(batch).ReloadAsync();
            if (batch.IsCancelled)
            {
                TempData["ok"] = "این فروش گروهی قبلاً لغو شده است.";
                return RedirectToAction(nameof(GroupDetails), new { id });
            }
            var saleIds = await _db.SalesTransactions.AsNoTracking()
                .Where(s => s.SalesBatchId == batch.Id && !s.IsCancelled)
                .OrderBy(s => s.Id).Select(s => s.Id).ToListAsync();
            await coordinator.LockAsync(null, saleIds);
            foreach (var saleId in saleIds)
            {
                // Each line is loaded under the locks and released once saved, so a large
                // batch does not re-scan every earlier line on each save.
                using var row = new SavedRowTrackingScope(_db, includeLoaded: true);
                var sale = await _db.SalesTransactions.SingleAsync(s => s.Id == saleId);
                await ReverseGroupSaleLineAsync(sale);
                sale.IsCancelled = true;
                await _db.SaveChangesAsync();
                if (_salesAccounting is not null)
                {
                    await _salesAccounting.TryReverseSaleAsync(sale, _businessClock.Today);
                    await _salesAccounting.TryReverseCogsAsync(sale, _businessClock.Today);
                    await _salesAccounting.TryReleaseAdvanceApplicationsAsync(sale, _businessClock.Today);
                }
                row.ReleaseSaved();
            }

            await _db.SaveChangesAsync();
            await coordinator.FinalizeAsync(batch.Id);

            await _audit.LogAndSaveAsync(
                nameof(SalesBatch),
                batch.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("IsCancelled", false, true)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"فروش گروهی {batch.BatchNumber} لغو شد.";
        }
        catch (Exception ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to cancel group sale batch {BatchId}.", batch.Id);
            TempData["err"] = "لغو فروش گروهی انجام نشد.";
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        return RedirectToAction(nameof(GroupDetails), new { id });
    }

    private Task<int?> FindSingleDirectLoadingReceiptAsync(int saleId)
        => _db.LoadingReceiptAllocations.AsNoTracking()
            .Where(a => a.SalesTransactionId == saleId && a.Destination == LoadingReceiptAllocationDestination.DirectSale
                && a.LoadingReceipt != null && !a.LoadingReceipt.IsCancelled
                && !_db.LoadingReceiptAllocations.Any(other => other.LoadingReceiptId == a.LoadingReceiptId && other.Id != a.Id))
            .Select(a => (int?)a.LoadingReceiptId).FirstOrDefaultAsync();

    private async Task ReverseGroupSaleLineAsync(SalesTransaction sale)
    {
        var directReceiptId = await FindSingleDirectLoadingReceiptAsync(sale.Id);
        if (directReceiptId.HasValue)
        {
            var result = await _loadingReceiptCancellation.CancelWithinCurrentTransactionAsync(
                [directReceiptId.Value], "لغو فروش گروهی مستقیم بارگیری", CurrentUserIdOrNull());
            if (!result.Succeeded)
                throw new BusinessRuleException("GROUP_SALE_LOADING_CANCEL_BLOCKED", string.Join(" ", result.Blockers.Select(b => b.Reason)));
            return;
        }

        if (await _db.LoadingReceiptAllocations.AsNoTracking().AnyAsync(a => a.SalesTransactionId == sale.Id))
            throw new BusinessRuleException("GROUP_SALE_MIXED_RECEIPT_CANCEL_REQUIRED",
                "این فروش بخشی از یک رسید چندبخشی است؛ لغو یا اصلاح را از صفحهٔ همان رسید انجام دهید.");

        // لجرِ معکوس (مطابق لغوِ فروش تکی).
        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        if (originalLedger is not null)
        {
            await LedgerReversalWriter.ReverseAsync(
                _db,
                originalLedger,
                _businessClock.Today,
                $"لغو فروش گروهی #{sale.Id} | {originalLedger.Description}",
                sale.InvoiceNumber);
        }

        // بازگشت موجودی برای فروش از مخزن (خروج → ورود معکوس).
        var stockOutMovements = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .ToListAsync();
        foreach (var m in stockOutMovements)
        {
            await _movements.PostReversalAsync(
                m,
                _businessClock.Today,
                $"Reversal for cancelled group SaleId={sale.Id}");
        }

        // آزادسازی موترِ لینک‌شده تا دوباره «در جریان» شود.
        var dispatch = await _db.TruckDispatches.FirstOrDefaultAsync(d => d.SalesTransactionId == sale.Id);
        if (dispatch is not null)
        {
            dispatch.SalesTransactionId = null;
        }

        // لغو رسیدِ فروش حمل و بازگرداندن وضعیت حمل به «در راه».
        var receipt = await _db.InventoryTransportReceipts
            .FirstOrDefaultAsync(r => r.SalesTransactionId == sale.Id && !r.IsCancelled);
        if (receipt is not null)
        {
            receipt.IsCancelled = true;
            var leg = await _db.InventoryTransportLegs.FirstOrDefaultAsync(l => l.Id == receipt.InventoryTransportLegId);
            if (leg is not null && leg.Status == InventoryTransportLegStatus.Received)
            {
                leg.Status = InventoryTransportLegStatus.InTransit;
            }
        }
    }
}
