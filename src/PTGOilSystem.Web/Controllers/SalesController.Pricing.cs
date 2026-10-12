using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.OperationalPeriod;

namespace PTGOilSystem.Web.Controllers;

// فروش با قیمت باز — تعیین قیمت فروش.
//
// فروش با IsSalePricePending بار را تحویل داده (موجودی و تخصیص منبع ثبت شده) ولی هیچ سند مالی
// ندارد. این مسیر قیمت را روی همان فروش می‌نشاند و دقیقاً همان اسنادی را می‌سازد که ثبت عادی
// فروش می‌ساخت — سطر دفتر (SaleLedgerFactory)، ژورنال درآمد و بهای تمام‌شده (آداپتر حسابداری)
// — آن هم فقط یک‌بار: پرچم، نسخهٔ سطر (Version) و نبودِ سطر دفتر قبلی هر سه پیش از ثبت بررسی
// می‌شوند و همه در یک تراکنش‌اند. تاریخ سند همان تاریخ فروش (تحویل) است؛ پس اگر آن دوره بسته
// باشد، قفل دوره (ApplicationDbContext) جلوی ثبت را می‌گیرد مگر کاربر مجوز ثبت استثنایی داشته باشد.
// اصلاح قیمتِ قطعی‌شده از این مسیر ممکن نیست و همان «ابطال و فروش جایگزین» است.
public partial class SalesController
{
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> SetPrice(int id, string? returnUrl = null)
    {
        var sale = await _db.SalesTransactions
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Include(s => s.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sale is null)
        {
            return NotFound();
        }

        var blocker = GetSetPriceBlocker(sale);
        if (blocker is not null)
        {
            TempData["err"] = blocker;
            return RedirectToAction(nameof(Details), new { id });
        }

        var model = new SalePriceFixViewModel
        {
            Id = sale.Id,
            Version = sale.Version,
            Currency = sale.Currency,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null
        };
        await FillSetPriceDisplayAsync(model, sale);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrice(
        int id,
        SalePriceFixViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        model.Id = id;
        model.Currency = SystemCurrency.Normalize(model.Currency);

        var sale = await _db.SalesTransactions
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Include(s => s.Product)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sale is null)
        {
            return NotFound();
        }

        var blocker = GetSetPriceBlocker(sale);
        if (blocker is not null)
        {
            // درخواست تکراری (دوبار کلیک، دو کاربر) به این‌جا می‌رسد: قیمت قبلاً ثبت شده و چیزی دوباره ساخته نمی‌شود.
            TempData[sale.IsSalePricePending ? "err" : "ok"] = blocker;
            return RedirectToAction(nameof(Details), new { id });
        }

        if (!model.UnitPriceInCurrency.HasValue)
        {
            ModelState.AddModelError(nameof(model.UnitPriceInCurrency), "قیمت فی تن الزامی است.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        CurrencyConversionResult? conversion = null;
        if (ModelState.IsValid)
        {
            try
            {
                conversion = await _currencyConversion.ResolveToBaseAsync(
                    model.Currency,
                    sale.SaleDate.Date,
                    model.AppliedFxRateToUsd);
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            }
        }

        if (!ModelState.IsValid || conversion is null)
        {
            await FillSetPriceDisplayAsync(model, sale);
            return View(model);
        }

        var unitPrice = model.UnitPriceInCurrency!.Value;
        var totalInCurrency = decimal.Round(sale.QuantityMt * unitPrice, 4, MidpointRounding.AwayFromZero);

        _formTokens.Stamp(formToken, "Sale.SetPrice", nameof(SalesTransaction));

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            // همان قفلِ سطری که لغو می‌گیرد: قیمت‌گذاری و لغو/ویرایشِ هم‌زمانِ یک فروش پشت سر هم اجرا
            // می‌شوند و هر کدام وضعیتِ قطعیِ دیگری را می‌بیند، نه عکسِ لحظهٔ بازکردن فرم را.
            var lockCoordinator = new SalesBatchCancellationCoordinator(_db);
            await lockCoordinator.LockAsync(sale.SalesBatchId, [sale.Id]);
            await _db.Entry(sale).ReloadAsync();
            var lockedBlocker = GetSetPriceBlocker(sale);
            if (lockedBlocker is not null)
            {
                throw new BusinessRuleException("SALE_PRICE_STATE_CHANGED", lockedBlocker);
            }

            _db.UseExpectedVersion(sale, model.Version);

            // نگهبانِ دوم کنار پرچم و نسخه: فروش با قیمت باز هرگز نباید سطر دفتر داشته باشد.
            if (await _db.LedgerEntries.AnyAsync(l => l.SourceType == "Sale" && l.SourceId == sale.Id))
            {
                throw new BusinessRuleException(
                    "SALE_PRICE_LEDGER_EXISTS",
                    "برای این فروش قبلاً سند مالی ثبت شده است؛ قیمت دوباره ثبت نشد.");
            }

            // تاریخ حسابداری: اگر دورهٔ روزِ تحویل باز است همان روز؛ اگر بسته است، امروز در دورهٔ باز.
            // دورهٔ بسته نه سند تازه می‌گیرد نه سندِ قدیمی‌اش تغییر می‌کند.
            var closedThrough = await _db.OperationalPeriodLocks.AsNoTracking()
                .Where(x => x.IsActive)
                .MaxAsync(x => (DateTime?)x.LockedThroughDate);
            var accountingDate = closedThrough.HasValue && sale.SaleDate.Date <= closedThrough.Value.Date
                ? _businessClock.Today
                : sale.SaleDate.Date;

            var before = (sale.Currency, sale.UnitPriceInCurrency, sale.AppliedFxRateToUsd, sale.TotalUsd);
            sale.SalePriceAccountingDate = accountingDate;
            sale.Currency = conversion.SourceCurrencyCode;
            sale.UnitPriceInCurrency = unitPrice;
            sale.AppliedFxRateToUsd = conversion.AppliedRateToBase;
            sale.UnitPriceUsd = conversion.ConvertToBase(unitPrice);
            sale.TotalInCurrency = totalInCurrency;
            sale.TotalUsd = conversion.ConvertToBase(totalInCurrency);
            sale.IsSalePricePending = false;
            sale.SalePriceFixedAtUtc = DateTime.UtcNow;
            sale.SalePriceFixedByUserId = CurrentUserIdOrNull();
            // اول خودِ فروش ذخیره می‌شود تا نسخهٔ سطر (Version) پیش از ساختن هر سندی بررسی شود؛
            // کاربر دوم همین‌جا رد می‌شود و هیچ سطر دفتری برایش ساخته نمی‌شود.
            await _db.SaveChangesAsync();

            // سهم مبلغ هر منبع (قرارداد خرید/حمل/Lot) هنگام ثبت فروش صفر/نامشخص بود؛ اکنون با همان
            // نسبت مقدار از مبلغ قطعی پر می‌شود تا گزارش‌های قرارداد و حمل همان عدد فروش را بخوانند.
            await _sourceAllocations.RepriceSaleAsync(sale);
            await ApplyFixedSaleLotAmountsAsync(sale);
            await AddFixedSaleToBatchTotalsAsync(sale);

            var ledgerEntry = Ledger.Post(SaleLedgerFactory.BuildSaleLedgerEntry(
                sale,
                conversion,
                contractId: sale.ContractId ?? sale.SourcePurchaseContractId));
            await _db.SaveChangesAsync();

            await PostSaleAccountingAsync(sale);

            await _audit.LogAsync(
                nameof(SalesTransaction),
                sale.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("IsSalePricePending", true, false),
                    ("Currency", before.Currency, sale.Currency),
                    ("UnitPriceInCurrency", before.UnitPriceInCurrency, sale.UnitPriceInCurrency),
                    ("AppliedFxRateToUsd", before.AppliedFxRateToUsd, sale.AppliedFxRateToUsd),
                    ("TotalUsd", before.TotalUsd, sale.TotalUsd),
                    ("SalePriceAccountingDate", null, sale.SalePriceAccountingDate),
                    ("LedgerReference", null, ledgerEntry.Reference)));
            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            switch (exception)
            {
                case DbUpdateException dup when _formTokens.IsDuplicate(dup):
                    TempData["ok"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
                    return RedirectToAction(nameof(Details), new { id });
                case DbUpdateConcurrencyException:
                    TempData["err"] = "این فروش هم‌زمان توسط کاربر دیگری تغییر کرد؛ هیچ تغییری ثبت نشد. صفحه را دوباره باز کنید.";
                    return RedirectToAction(nameof(Details), new { id });
                case OperationalPeriodLockedException locked:
                    ModelState.AddModelError(string.Empty, locked.Message);
                    break;
                case BusinessRuleException { Code: "SALE_PRICE_STATE_CHANGED" } changed:
                    TempData["err"] = changed.Message;
                    return RedirectToAction(nameof(Details), new { id });
                case BusinessRuleException rule:
                    ModelState.AddModelError(string.Empty, rule.Message);
                    break;
                default:
                    _logger.LogError(exception, "Failed to fix price of sale {SaleId}.", sale.Id);
                    ModelState.AddModelError(string.Empty, "تعیین قیمت انجام نشد؛ هیچ تغییری ثبت نشد.");
                    break;
            }

            // ردیابی EF هنوز مقادیر جدید را دارد؛ برای نمایش دوبارهٔ فرم از دیتابیس خوانده می‌شود.
            var fresh = await _db.SalesTransactions
                .Include(s => s.Customer)
                .Include(s => s.Supplier)
                .Include(s => s.Product)
                .AsNoTracking()
                .FirstAsync(s => s.Id == id);
            await FillSetPriceDisplayAsync(model, fresh);
            return View(model);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        TempData["ok"] = "قیمت فروش ثبت شد و سند مالی فروش ساخته شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // سربرگ فروش گروهی جمعِ تاریخیِ ردیف‌هاست؛ ردیفی که با قیمت باز ثبت شد صفر به آن افزوده بود.
    private async Task AddFixedSaleToBatchTotalsAsync(SalesTransaction sale)
    {
        if (!sale.SalesBatchId.HasValue)
        {
            return;
        }

        var batch = await _db.SalesBatches.SingleOrDefaultAsync(b => b.Id == sale.SalesBatchId.Value);
        if (batch is null)
        {
            return;
        }

        batch.TotalInCurrency = decimal.Round(batch.TotalInCurrency + sale.TotalInCurrency, 4, MidpointRounding.AwayFromZero);
        batch.TotalUsd = decimal.Round(batch.TotalUsd + sale.TotalUsd, 4, MidpointRounding.AwayFromZero);
    }

    private static string? GetSetPriceBlocker(SalesTransaction sale)
        => sale.IsCancelled
            ? "این فروش لغو شده است و قیمت‌گذاری نمی‌شود."
            : !sale.IsSalePricePending
                ? "قیمت این فروش قبلاً تعیین شده است. برای اصلاح قیمت از «ابطال / اصلاح فروش» استفاده کنید."
                : null;

    private async Task FillSetPriceDisplayAsync(SalePriceFixViewModel model, SalesTransaction sale)
    {
        model.InvoiceNumber = sale.InvoiceNumber;
        model.CustomerName = sale.Customer?.Name ?? sale.Supplier?.Name ?? string.Empty;
        model.ProductName = sale.Product?.Name ?? string.Empty;
        model.SaleDate = sale.SaleDate;
        model.QuantityMt = sale.QuantityMt;
        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => c.Code).ToListAsync(),
            model.Currency);
    }

    // Lot‌های فروش با قیمت باز مبلغ ندارند (null = نامعلوم، نه صفر).
    private async Task ClearPendingSaleLotAmountsAsync(int saleId)
    {
        var lots = await _db.SaleLotAllocations
            .Where(a => a.SalesTransactionId == saleId)
            .ToListAsync();
        if (lots.Count == 0)
        {
            return;
        }

        foreach (var lot in lots)
        {
            lot.AmountUsd = null;
        }

        await _db.SaveChangesAsync();
    }

    private async Task ApplyFixedSaleLotAmountsAsync(SalesTransaction sale)
    {
        var lots = await _db.SaleLotAllocations
            .Where(a => a.SalesTransactionId == sale.Id)
            .OrderBy(a => a.Id)
            .ToListAsync();
        var totalQuantity = lots.Sum(a => a.QuantityMt);
        if (lots.Count == 0 || totalQuantity <= 0m)
        {
            return;
        }

        var allocated = 0m;
        for (var index = 0; index < lots.Count; index++)
        {
            var amount = index == lots.Count - 1
                ? sale.TotalUsd - allocated
                : decimal.Round(sale.TotalUsd * lots[index].QuantityMt / totalQuantity, 4, MidpointRounding.AwayFromZero);
            lots[index].AmountUsd = amount;
            allocated += amount;
        }
    }

    /// <summary>قراردادهای خرید منبعی که هنوز نرخ نهایی ندارند (همان قاعدهٔ ContractPricingAdapter).</summary>
    private async Task<List<SalePendingPurchaseContractViewModel>> LoadPendingPurchasePriceContractsAsync(
        IReadOnlyCollection<int> contractIds)
    {
        if (contractIds.Count == 0)
        {
            return [];
        }

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .ToListAsync();
        return contracts
            .Where(c => ContractPricingAdapter.GetPricingStatus(c) == PricingCompletionStatus.Pending)
            .OrderBy(c => c.Id)
            .Select(c => new SalePendingPurchaseContractViewModel { ContractId = c.Id, Label = c.DisplayLabel })
            .ToList();
    }
}
