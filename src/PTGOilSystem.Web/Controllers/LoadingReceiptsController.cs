using PTGOilSystem.Web.Services.LoadingReceipts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Helpers;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class LoadingReceiptsController : Controller
{
    private const decimal QuantityPrecisionUnit = 0.0001m;
    private sealed record DirectTransportResolution(int TruckId, int? DriverId, Truck? CreatedTruck, Driver? CreatedDriver);
    private sealed record LoadingReceiptQuantitySnapshot(
        decimal ReceivedQuantityMt,
        decimal ReceiptShortageLossMt,
        decimal TransportedFromLoadingMt)
    {
        public decimal AccountedQuantityMt => ReceivedQuantityMt + ReceiptShortageLossMt + TransportedFromLoadingMt;
    }
    private sealed record BulkReceiptOpenLoading(LoadingRegister Loading, decimal AlreadyReceivedQuantityMt, decimal RemainingQuantityMt);
    private sealed record BulkReceiptQuantityAllocation(BulkReceiptOpenLoading OpenLoading, decimal QuantityMt);
    private sealed record ReceiptGraphResult(
        List<InventoryMovement> Movements,
        List<LoadingReceiptAllocation> Allocations,
        List<(LoadingReceiptAllocation Allocation, LoadingDirectSaleDraft Draft)> DirectSaleDrafts,
        List<TruckDispatch> Dispatches);

    private readonly ApplicationDbContext _db;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);
    private readonly IAuditService _audit;
    private readonly ILossEventWorkflowService _lossWorkflow;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly ILogger<LoadingReceiptsController> _logger;
    // مراحل ۶ و ۷ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IPurchaseAccountingAdapter? _purchaseAccounting;
    private readonly Services.Accounting.ISalesAccountingAdapter? _salesAccounting;
    // لغو امن رسید (لغو تکی، لغو گروهی، و مسیر «اصلاح مقدار» = لغو + ثبت دوباره).
    private readonly Services.LoadingReceipts.ILoadingReceiptCancellationService? _cancellation;
    private readonly Security.ICurrentUserContext? _currentUser;
    private readonly IInventoryMovementWriter _movements;

    private readonly IFormTokenGuard _formTokens;

    // کش اختیاری فقط برای گزینه‌های کشویی فیلتر؛ هیچ دادهٔ عملیاتی در آن نگه داشته نمی‌شود.
    private readonly IMemoryCache? _cache;

    public LoadingReceiptsController(
        ApplicationDbContext db,
        IAuditService audit,
        ILogger<LoadingReceiptsController> logger,
        ILossEventWorkflowService? lossWorkflow = null,
        ICurrencyConversionService? currencyConversion = null,
        Services.Accounting.IPurchaseAccountingAdapter? purchaseAccounting = null,
        Services.Accounting.ISalesAccountingAdapter? salesAccounting = null,
        Services.LoadingReceipts.ILoadingReceiptCancellationService? cancellation = null,
        Security.ICurrentUserContext? currentUser = null,
        IInventoryMovementWriter? movements = null,
        IFormTokenGuard? formTokens = null,
        IMemoryCache? cache = null)
    {
        // PTG-P0-01 — نگهبان ثبت دوباره (Refresh/تب دوم/تلاش پس از Timeout).
        _formTokens = formTokens ?? new FormTokenGuard(db);
        _purchaseAccounting = purchaseAccounting;
        _salesAccounting = salesAccounting;
        _cancellation = cancellation;
        _currentUser = currentUser;
        _db = db;
        _audit = audit;
        _lossWorkflow = lossWorkflow ?? new LossEventWorkflowService(db, new StockService(db), audit);
        _currencyConversion = currencyConversion ?? new CurrencyConversionService(new PricingService(db));
        _logger = logger;
        _movements = movements ?? new InventoryMovementWriter(db, new StockService(db));
        _cache = cache;
    }

    private bool TryGetLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        {
            localReturnUrl = returnUrl;
            return true;
        }

        localReturnUrl = string.Empty;
        return false;
    }

    private bool IsPageModalRequest()
    {
        var httpContext = ControllerContext.HttpContext;
        if (httpContext is null)
        {
            return false;
        }

        var request = httpContext.Request;
        if (string.Equals(request.Query["modal"].ToString(), "1", StringComparison.Ordinal))
        {
            return true;
        }

        return request.HasFormContentType
            && string.Equals(request.Form["modal"].ToString(), "1", StringComparison.Ordinal);
    }

    private IActionResult PageModalComplete(string redirectUrl)
    {
        var safeRedirectUrl = Url.IsLocalUrl(redirectUrl)
            ? redirectUrl
            : Url.Action("Index", "Home") ?? "/";
        var encodedRedirectUrl = JsonSerializer.Serialize(safeRedirectUrl);
        var html = $$"""
            <!doctype html>
            <html lang="fa" dir="rtl">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>ثبت شد</title>
                <style>
                    body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f6f8fc;color:#0f172a;font-family:Tahoma,Arial,sans-serif}
                    .box{padding:1.25rem 1.5rem;border:1px solid #dbe4f0;border-radius:1rem;background:#fff;box-shadow:0 18px 40px rgba(15,23,42,.08);text-align:center}
                    strong{display:block;margin-bottom:.35rem}
                </style>
            </head>
            <body>
                <div class="box"><strong>ثبت رسید انجام شد</strong><span>در حال برگشت به صفحه اصلی...</span></div>
                <script>
                    (function () {
                        var redirectUrl = {{encodedRedirectUrl}};
                        if (window.parent && window.parent !== window && window.parent.PTG && typeof window.parent.PTG.closePageModal === "function") {
                            window.parent.PTG.closePageModal({ redirectUrl: redirectUrl });
                            return;
                        }
                        window.location.href = redirectUrl;
                    })();
                </script>
            </body>
            </html>
            """;

        return Content(html, "text/html; charset=utf-8");
    }

    private static IReadOnlyList<SelectListItem> GetReceiptDestinationItems(LoadingReceiptDestination selectedDestination)
        => Enum.GetValues<LoadingReceiptDestination>()
            .Select(destination => new SelectListItem
            {
                Value = ((int)destination).ToString(),
                Text = ToReceiptDestinationLabel(destination),
                Selected = destination == selectedDestination
            })
            .ToList();

    private static string ToReceiptDestinationLabel(LoadingReceiptDestination destination)
        => destination switch
        {
            LoadingReceiptDestination.ToInventory => "ورود به موجودی / تانک",
            LoadingReceiptDestination.DirectDispatch => "تخلیه مستقیم / بدون ورود به موجودی",
            LoadingReceiptDestination.Mixed => "ترکیبی / allocation line-based",
            _ => "نامشخص"
        };

    private static string ToAllocationDestinationLabel(LoadingReceiptAllocationDestination destination)
        => destination switch
        {
            LoadingReceiptAllocationDestination.ToInventory => "ورود به موجودی / تانک",
            LoadingReceiptAllocationDestination.DirectSale => "فروش مستقیم / فقط Trace",
            LoadingReceiptAllocationDestination.DirectDispatchToTruck => "بارگیری مستقیم در موتر / فقط Trace",
            LoadingReceiptAllocationDestination.TransferToOtherTerminal => "انتقال به ترمینال یا مخزن دیگر / در مسیر",
            _ => "نامشخص"
        };

    private static string GetUnsupportedReceiptDestinationMessage(LoadingReceiptDestination destination)
        => destination switch
        {
            _ => "مقصد رسید انتخاب‌شده معتبر نیست."
        };

    private static void ValidateReceiptDestination(LoadingReceiptCreateViewModel model, ModelStateDictionary modelState)
    {
        if (!Enum.IsDefined(typeof(LoadingReceiptDestination), model.ReceiptDestination))
        {
            modelState.AddModelError(nameof(model.ReceiptDestination), "مقصد رسید انتخاب‌شده معتبر نیست.");
            return;
        }

        // رسید ترکیبی دیگر ساخته نمی‌شود؛ رسیدهای ترکیبیِ قدیمی فقط خوانده می‌شوند.
        if (model.ReceiptDestination == LoadingReceiptDestination.Mixed)
        {
            modelState.AddModelError(
                nameof(model.ReceiptDestination),
                "رسید ترکیبی پشتیبانی نمی‌شود. برای هر رسید یک مقصد انتخاب کنید.");
        }
    }

    private static void NormalizeAndValidateAllocationDestination(LoadingReceiptCreateViewModel model, ModelStateDictionary modelState)
    {
        if (!Enum.IsDefined(typeof(LoadingReceiptAllocationDestination), model.AllocationDestination))
        {
            modelState.AddModelError(nameof(model.AllocationDestination), "مقصد دقیق allocation معتبر نیست.");
            return;
        }

        if (model.ReceiptDestination == LoadingReceiptDestination.ToInventory)
        {
            model.AllocationDestination = LoadingReceiptAllocationDestination.ToInventory;
            return;
        }

        if (model.ReceiptDestination != LoadingReceiptDestination.DirectDispatch)
        {
            return;
        }

        if (model.AllocationDestination == LoadingReceiptAllocationDestination.ToInventory)
        {
            model.AllocationDestination = LoadingReceiptAllocationDestination.DirectDispatchToTruck;
        }
    }

    private static LoadingReceiptAllocationStatus ResolveInitialAllocationStatus(LoadingReceiptAllocationDestination destination)
        => destination switch
        {
            LoadingReceiptAllocationDestination.ToInventory => LoadingReceiptAllocationStatus.Completed,
            LoadingReceiptAllocationDestination.DirectSale => LoadingReceiptAllocationStatus.Completed,
            LoadingReceiptAllocationDestination.TransferToOtherTerminal => LoadingReceiptAllocationStatus.InTransit,
            _ => LoadingReceiptAllocationStatus.TraceOnly
        };

    public async Task<IActionResult> Index(
        string? q = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int[]? productId = null,
        int[]? terminalId = null,
        int[]? destination = null,
        string? status = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        var normalizedQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var query = _db.LoadingReceipts
            .Where(receipt => !receipt.IsArchived)
            .AsNoTracking()
            .AsQueryable();

        if (fromDate.HasValue)
        {
            query = query.Where(receipt => receipt.ReceiptDate >= fromDate.Value.Date);
        }

        if (toDate.HasValue)
        {
            var exclusiveToDate = toDate.Value.Date.AddDays(1);
            query = query.Where(receipt => receipt.ReceiptDate < exclusiveToDate);
        }

        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (productId is { Length: > 0 })
        {
            query = query.Where(receipt => receipt.LoadingRegister != null
                && productId.Contains(receipt.LoadingRegister.ProductId));
        }

        if (terminalId is { Length: > 0 })
        {
            query = query.Where(receipt => terminalId.Contains(receipt.TerminalId));
        }

        var destinationValues = (destination ?? [])
            .Where(value => Enum.IsDefined(typeof(LoadingReceiptDestination), value))
            .Select(value => (LoadingReceiptDestination)value)
            .ToArray();
        if (destinationValues.Length > 0)
        {
            query = query.Where(receipt => destinationValues.Contains(receipt.ReceiptDestination));
        }

        // وضعیت: رسیدهای لغوشده در فهرست دیده می‌شوند، این فیلتر فقط نمایش را محدود می‌کند.
        var normalizedStatus = status?.Trim().ToLowerInvariant();
        if (normalizedStatus == "active")
        {
            query = query.Where(receipt => !receipt.IsCancelled);
        }
        else if (normalizedStatus == "cancelled")
        {
            query = query.Where(receipt => receipt.IsCancelled);
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            query = query.Where(receipt =>
                (receipt.ReferenceDocument != null && receipt.ReferenceDocument.Contains(normalizedQuery))
                || (receipt.LoadingRegister != null
                    && receipt.LoadingRegister.Contract != null
                    && (receipt.LoadingRegister.Contract.ContractName.Contains(normalizedQuery)
                        || receipt.LoadingRegister.Contract.ContractNumber.Contains(normalizedQuery)))
                || (receipt.LoadingRegister != null
                    && receipt.LoadingRegister.Product != null
                    && receipt.LoadingRegister.Product.Name.Contains(normalizedQuery))
                || (receipt.Terminal != null && receipt.Terminal.Name.Contains(normalizedQuery))
                || (receipt.StorageTank != null && (
                    receipt.StorageTank.TankCode.Contains(normalizedQuery)
                    || (receipt.StorageTank.DisplayName != null && receipt.StorageTank.DisplayName.Contains(normalizedQuery)))));
        }

        // شمارش صفحه و هر سه آمارِ کارت‌ها در یک رفت‌وبرگشت؛ قبلاً چهار query جدا روی همین
        // مجموعه اجرا می‌شد. قاعدهٔ «لغوشده در جمع‌ها شمرده نمی‌شود» دست‌نخورده است.
        var stats = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalCount = group.Count(),
                SumQuantity = group.Sum(receipt => receipt.IsCancelled ? 0m : receipt.ReceivedQuantityMt),
                WithTankCount = group.Count(receipt => !receipt.IsCancelled && receipt.StorageTank != null),
                WithReferenceCount = group.Count(receipt => !receipt.IsCancelled
                    && receipt.ReferenceDocument != null
                    && receipt.ReferenceDocument != ""
                    && !receipt.ReferenceDocument.ToUpper().StartsWith("BULK-RCPT-"))
            })
            .SingleOrDefaultAsync();

        var totalCount = stats?.TotalCount ?? 0;
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(receipt => receipt.ReceiptDate)
            .ThenByDescending(receipt => receipt.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(receipt => new LoadingReceiptIndexItemViewModel
            {
                Id = receipt.Id,
                ReceiptDate = receipt.ReceiptDate,
                ContractName = receipt.LoadingRegister != null && receipt.LoadingRegister.Contract != null
                    ? receipt.LoadingRegister.Contract.ContractName
                    : "",
                ContractNumber = receipt.LoadingRegister != null && receipt.LoadingRegister.Contract != null
                    ? receipt.LoadingRegister.Contract.ContractNumber
                    : "",
                ProductName = receipt.LoadingRegister != null && receipt.LoadingRegister.Product != null
                    ? receipt.LoadingRegister.Product.Name
                    : "",
                TerminalName = receipt.Terminal != null ? receipt.Terminal.Name : "",
                StorageTankCode = receipt.StorageTank == null
                    ? null
                    : receipt.StorageTank.DisplayName == null || receipt.StorageTank.DisplayName == ""
                        ? receipt.StorageTank.TankCode
                        : receipt.StorageTank.DisplayName,
                ReceivedQuantityMt = receipt.ReceivedQuantityMt,
                // نمبر وسیلهٔ همان بارگیری تا در خروجی معلوم باشد بار با کدام وسیله رسیده.
                VehicleNumber = receipt.LoadingRegister == null
                    ? null
                    : receipt.LoadingRegister.Truck != null
                        ? receipt.LoadingRegister.Truck.PlateNumber
                        : receipt.LoadingRegister.WagonNumber ?? receipt.LoadingRegister.RwbNo,
                ReferenceDocument = receipt.ReferenceDocument,
                IsCancelled = receipt.IsCancelled,
                CancellationReason = receipt.CancellationReason
            })
            .ToListAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        // رسیدهای لغوشده در فهرست دیده می‌شوند اما در هیچ جمعی شمرده نمی‌شوند.
        // مقادیر از همان query ترکیبیِ بالا می‌آیند (بدون رفت‌وبرگشت اضافه).
        ViewBag.SumQuantity = stats?.SumQuantity ?? 0m;
        ViewBag.WithTankCount = stats?.WithTankCount ?? 0;
        ViewBag.WithReferenceCount = stats?.WithReferenceCount ?? 0;

        // گزینه‌های فیلتر از روی همان رسیدهای موجود ساخته می‌شوند تا فهرست کوتاه و مرتبط بماند.
        // این دو فهرست به فیلترِ کاربر وابسته نیستند، پس برای یک بازهٔ کوتاه کش می‌شوند و
        // اسکن DISTINCT در هر باز شدن صفحه تکرار نمی‌شود.
        ViewBag.FilterProducts = await FilterOptionCache.GetOrCreateAsync(
            _cache,
            "loading-receipts:filter-products",
            async () => (await _db.LoadingReceipts
                    .AsNoTracking()
                    .Where(receipt => receipt.LoadingRegister != null && receipt.LoadingRegister.Product != null)
                    .Select(receipt => new { receipt.LoadingRegister!.ProductId, receipt.LoadingRegister.Product!.Name })
                    .Distinct()
                    .OrderBy(product => product.Name)
                    .ToListAsync())
                .Select(product => new SelectListItem(product.Name, product.ProductId.ToString()))
                .ToList());

        ViewBag.FilterTerminals = await FilterOptionCache.GetOrCreateAsync(
            _cache,
            "loading-receipts:filter-terminals",
            async () => (await _db.LoadingReceipts
                    .AsNoTracking()
                    .Where(receipt => receipt.Terminal != null)
                    .Select(receipt => new { receipt.TerminalId, receipt.Terminal!.Name })
                    .Distinct()
                    .OrderBy(terminal => terminal.Name)
                    .ToListAsync())
                .Select(terminal => new SelectListItem(terminal.Name, terminal.TerminalId.ToString()))
                .ToList());

        return View(new LoadingReceiptIndexViewModel
        {
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            Query = normalizedQuery,
            FromDate = fromDate,
            ToDate = toDate
        });
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeTruckPlateNumber(string? value)
    {
        var normalized = NormalizeNullable(value)?.ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private Task<DirectTransportResolution?> ResolveDirectTransportAsync(LoadingReceiptCreateViewModel model)
    {
        model.DirectTruckPlateNumber = NormalizeTruckPlateNumber(model.DirectTruckPlateNumber);
        model.DirectDriverName = NormalizeNullable(model.DirectDriverName);
        return ResolveDirectTransportCoreAsync(
            model.DirectTruckId,
            model.DirectTruckPlateNumber,
            model.DirectDriverId,
            model.DirectDriverName,
            nameof(model.DirectTruckId),
            nameof(model.DirectTruckPlateNumber),
            nameof(model.DirectDriverId),
            nameof(model.DirectDriverName));
    }

    // هستهٔ مشترک resolve موتر/راننده برای «ارسال با موتر»؛ کلیدهای خطا پارامتری‌اند تا رسید جمعی هم با prefix هر خط استفاده کند.
    private async Task<DirectTransportResolution?> ResolveDirectTransportCoreAsync(
        int? directTruckId,
        string? directTruckPlateNumber,
        int? directDriverId,
        string? directDriverName,
        string truckIdKey,
        string truckPlateKey,
        string driverIdKey,
        string driverNameKey)
    {
        directTruckPlateNumber = NormalizeTruckPlateNumber(directTruckPlateNumber);
        directDriverName = NormalizeNullable(directDriverName);

        var resolvedTruckId = directTruckId.GetValueOrDefault();
        Truck? createdTruck = null;
        if (!string.IsNullOrWhiteSpace(directTruckPlateNumber))
        {
            var existingTruck = await _db.Trucks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.PlateNumber == directTruckPlateNumber);
            if (existingTruck is null)
            {
                createdTruck = new Truck
                {
                    PlateNumber = directTruckPlateNumber,
                    IsActive = true
                };
                resolvedTruckId = 0;
            }
            else if (!existingTruck.IsActive)
            {
                ModelState.AddModelError(truckPlateKey, "این نمبر پلیت قبلا غیرفعال ثبت شده است. اول آن را از داده‌های پایه فعال کنید.");
            }
            else
            {
                resolvedTruckId = existingTruck.Id;
            }
        }
        else if (!directTruckId.HasValue)
        {
            ModelState.AddModelError(truckIdKey, "نمبر پلیت موتر را انتخاب یا تایپ کنید.");
        }
        else
        {
            var truckExists = await _db.Trucks
                .AsNoTracking()
                .AnyAsync(t => t.Id == directTruckId.Value && t.IsActive);
            if (!truckExists)
            {
                ModelState.AddModelError(truckIdKey, "موتر انتخاب‌شده معتبر نیست.");
            }
        }

        int? resolvedDriverId = directDriverId;
        Driver? createdDriver = null;
        if (!string.IsNullOrWhiteSpace(directDriverName))
        {
            var existingDriver = await _db.Drivers
                .AsNoTracking()
                .Where(d => d.FullName == directDriverName && d.IsActive)
                .OrderBy(d => d.Id)
                .FirstOrDefaultAsync();
            if (existingDriver is null)
            {
                createdDriver = new Driver
                {
                    FullName = directDriverName,
                    IsActive = true
                };
                resolvedDriverId = null;
            }
            else
            {
                resolvedDriverId = existingDriver.Id;
            }
        }
        else if (directDriverId.HasValue)
        {
            var driverExists = await _db.Drivers
                .AsNoTracking()
                .AnyAsync(d => d.Id == directDriverId.Value && d.IsActive);
            if (!driverExists)
            {
                ModelState.AddModelError(driverIdKey, "راننده انتخاب‌شده معتبر نیست.");
            }
        }

        if (ModelState.TryGetValue(truckIdKey, out var truckEntry) && truckEntry.Errors.Count > 0
            || ModelState.TryGetValue(truckPlateKey, out var truckPlateEntry) && truckPlateEntry.Errors.Count > 0
            || ModelState.TryGetValue(driverIdKey, out var driverEntry) && driverEntry.Errors.Count > 0
            || ModelState.TryGetValue(driverNameKey, out var driverNameEntry) && driverNameEntry.Errors.Count > 0)
        {
            return null;
        }

        return new DirectTransportResolution(resolvedTruckId, resolvedDriverId, createdTruck, createdDriver);
    }

    private async Task LogCreatedDirectTransportAsync(DirectTransportResolution? transport)
    {
        if (transport?.CreatedTruck is not null)
        {
            await _audit.LogAsync(
                nameof(Truck),
                transport.CreatedTruck.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("PlateNumber", transport.CreatedTruck.PlateNumber),
                    ("IsActive", transport.CreatedTruck.IsActive)));
        }

        if (transport?.CreatedDriver is not null)
        {
            await _audit.LogAsync(
                nameof(Driver),
                transport.CreatedDriver.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("FullName", transport.CreatedDriver.FullName),
                    ("IsActive", transport.CreatedDriver.IsActive)));
        }
    }

    private static void NormalizeReceiptInputs(LoadingReceiptCreateViewModel model)
    {
        model.SaleCurrency = SystemCurrency.Normalize(model.SaleCurrency);
        model.SaleInvoiceNumber = NormalizeNullable(model.SaleInvoiceNumber);
        model.SaleNotes = NormalizeNullable(model.SaleNotes);
        model.DirectTruckTicketSerialNumber = NormalizeNullable(model.DirectTruckTicketSerialNumber);
        model.DirectTruckPlateNumber = NormalizeTruckPlateNumber(model.DirectTruckPlateNumber);
        model.DirectDriverName = NormalizeNullable(model.DirectDriverName);
    }

    private static LoadingReceiptAllocationLineInput BuildScalarAllocationLine(
        LoadingReceiptCreateViewModel model,
        string? normalizedReference,
        string? normalizedNotes,
        string? normalizedDestinationName,
        string? normalizedDestinationReference,
        LoadingRegister loading)
        => model.ReceiptDestination == LoadingReceiptDestination.ToInventory
            ? new LoadingReceiptAllocationLineInput
            {
                Destination = LoadingReceiptAllocationDestination.ToInventory,
                QuantityMt = model.ReceivedQuantityMt,
                TerminalId = model.TerminalId,
                StorageTankId = model.StorageTankId,
                ReferenceDocument = normalizedReference ?? loading.BillOfLadingNumber,
                Notes = normalizedNotes
            }
            : new LoadingReceiptAllocationLineInput
            {
                Destination = model.AllocationDestination,
                QuantityMt = model.ReceivedQuantityMt,
                TerminalId = model.TerminalId,
                DestinationTerminalId = model.DestinationTerminalId,
                DestinationStorageTankId = model.DestinationStorageTankId,
                DestinationLocationId = model.DestinationLocationId,
                DestinationName = normalizedDestinationName,
                DestinationReference = normalizedDestinationReference,
                SaleCustomerId = model.SaleCustomerId,
                SaleSupplierId = model.SaleSupplierId,
                SaleDate = model.SaleDate,
                SaleCurrency = model.SaleCurrency,
                SaleUnitPriceInCurrency = model.SaleUnitPriceInCurrency,
                SaleIsPricePending = model.SaleIsPricePending,
                SaleAppliedFxRateToUsd = model.SaleAppliedFxRateToUsd,
                SaleInvoiceNumber = model.SaleInvoiceNumber,
                SaleNotes = model.SaleNotes,
                ReferenceDocument = normalizedReference ?? loading.BillOfLadingNumber,
                Notes = normalizedNotes
            };

    private static void EnsureToInventoryAllocationInvariant(
        LoadingReceipt receipt,
        InventoryMovement movement,
        LoadingReceiptAllocation allocation)
    {
        if (receipt.ReceiptDestination is not LoadingReceiptDestination.ToInventory
            and not LoadingReceiptDestination.Mixed
            || allocation.Destination != LoadingReceiptAllocationDestination.ToInventory)
        {
            throw new InvalidOperationException("ToInventory receipt allocation must use the ToInventory destination.");
        }

        if (allocation.QuantityMt <= 0m)
        {
            throw new InvalidOperationException("ToInventory receipt allocation quantity must be positive.");
        }

        if (allocation.QuantityMt != movement.QuantityMt)
        {
            throw new InvalidOperationException("ToInventory receipt allocation quantity must match the inventory movement quantity.");
        }

        if (movement.Direction != MovementDirection.In)
        {
            throw new InvalidOperationException("ToInventory receipt allocation must link to an inbound inventory movement.");
        }

        var linksToMovement = ReferenceEquals(allocation.InventoryMovement, movement)
            || (movement.Id > 0 && allocation.InventoryMovementId == movement.Id);
        if (!linksToMovement)
        {
            throw new InvalidOperationException("ToInventory receipt allocation must link to the created inventory movement.");
        }
    }

    private static void EnsureDirectDispatchAllocationInvariant(
        LoadingReceipt receipt,
        LoadingReceiptAllocation allocation)
    {
        if (receipt.ReceiptDestination == LoadingReceiptDestination.ToInventory
            || allocation.Destination == LoadingReceiptAllocationDestination.ToInventory)
        {
            throw new InvalidOperationException("DirectDispatch receipt allocation must use a non-inventory allocation destination.");
        }

        if (allocation.QuantityMt <= 0m)
        {
            throw new InvalidOperationException("Trace-only receipt allocation quantity must be positive.");
        }

        if (!allocation.SourcePurchaseContractId.HasValue || allocation.SourcePurchaseContractId.Value <= 0)
        {
            throw new InvalidOperationException("DirectDispatch receipt allocation must keep the source purchase contract.");
        }

        if (allocation.TerminalId != receipt.TerminalId)
        {
            throw new InvalidOperationException("DirectDispatch receipt allocation terminal must match the receipt terminal.");
        }

        if (receipt.StorageTankId.HasValue || allocation.StorageTankId.HasValue)
        {
            throw new InvalidOperationException("DirectDispatch receipt must not enter a storage tank in this phase.");
        }

        if (allocation.InventoryMovement is not null || allocation.InventoryMovementId.HasValue)
        {
            throw new InvalidOperationException("DirectDispatch receipt allocation must not create or link inventory movement in this phase.");
        }

        if (allocation.TruckDispatchId.HasValue)
        {
            throw new InvalidOperationException("DirectDispatch receipt allocation must not link dispatch in this phase.");
        }

        if (allocation.Destination == LoadingReceiptAllocationDestination.DirectSale)
        {
            if (allocation.SalesTransaction is null && !allocation.SalesTransactionId.HasValue)
            {
                throw new InvalidOperationException("DirectSale receipt allocation must link to the created sale.");
            }
        }
        else if (allocation.SalesTransaction is not null || allocation.SalesTransactionId.HasValue)
        {
            throw new InvalidOperationException("Only DirectSale receipt allocation can link sale in this phase.");
        }

        var hasDirectTruckDispatch = allocation.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
            && allocation.DirectTruckDispatches.Any(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.Status != DispatchStatus.Cancelled);
        if (hasDirectTruckDispatch)
        {
            if (allocation.Status is not LoadingReceiptAllocationStatus.Completed and not LoadingReceiptAllocationStatus.InTransit)
            {
                throw new InvalidOperationException("Direct truck dispatch allocation must be completed or in transit after dispatch creation.");
            }
        }
        else if (allocation.Status != ResolveInitialAllocationStatus(allocation.Destination))
        {
            throw new InvalidOperationException("DirectDispatch receipt allocation must keep the trace-only phase status.");
        }
    }

    // موتور مشترک ساخت گراف رسید برای یک بارگیری: از روی allocation lineها رکوردهای موجودی/فروش/دیسپچ/allocation را می‌سازد.
    // هم رسید تکی و هم رسید جمعی از همین متد استفاده می‌کنند تا منطق سناریوها یکسان بماند.
    private async Task<ReceiptGraphResult> BuildReceiptGraphAsync(
        LoadingRegister loading,
        LoadingReceipt receipt,
        IReadOnlyList<LoadingReceiptAllocationLineInput> lines,
        bool shouldCreateDirectTruckDispatch,
        DirectTransportResolution? directTransport,
        DateTime? directDispatchDate,
        string? directTruckTicketSerialNumber,
        string? normalizedReference,
        string? normalizedNotes)
    {
        var inventoryMovements = new List<InventoryMovement>();
        var allocations = new List<LoadingReceiptAllocation>();
        var directSaleDrafts = new List<(LoadingReceiptAllocation Allocation, LoadingDirectSaleDraft Draft)>();
        var directTruckDispatches = new List<TruckDispatch>();
        var receiptMovementLinked = false;

        foreach (var line in lines)
        {
            InventoryMovement? lineMovement = null;
            LoadingDirectSaleDraft? directSaleDraft = null;
            var lineReference = NormalizeNullable(line.ReferenceDocument) ?? normalizedReference ?? loading.BillOfLadingNumber;
            var lineNotes = NormalizeNullable(line.Notes) ?? normalizedNotes;
            var lineTerminalId = line.Destination == LoadingReceiptAllocationDestination.ToInventory
                ? line.TerminalId!.Value
                : receipt.TerminalId;
            var lineStorageTankId = line.Destination == LoadingReceiptAllocationDestination.ToInventory
                ? line.StorageTankId
                : null;

            if (line.Destination == LoadingReceiptAllocationDestination.ToInventory)
            {
                lineMovement = new InventoryMovement
                {
                    ProductId = loading.ProductId,
                    ContractId = loading.ContractId,
                    TerminalId = lineTerminalId,
                    StorageTankId = lineStorageTankId,
                    Direction = MovementDirection.In,
                    MovementDate = receipt.ReceiptDate,
                    QuantityMt = line.QuantityMt,
                    ReferenceDocument = lineReference,
                    Notes = lineNotes
                };

                if (!receiptMovementLinked)
                {
                    lineMovement.LoadingReceipt = receipt;
                    receiptMovementLinked = true;
                }

                inventoryMovements.Add(lineMovement);
            }
            else if (line.Destination == LoadingReceiptAllocationDestination.DirectSale)
            {
                directSaleDraft = await BuildLoadingDirectSaleDraftAsync(line, loading);
            }

            var allocation = new LoadingReceiptAllocation
            {
                LoadingReceipt = receipt,
                Destination = line.Destination,
                Status = ResolveInitialAllocationStatus(line.Destination),
                QuantityMt = line.QuantityMt,
                SourcePurchaseContractId = loading.ContractId,
                TerminalId = lineTerminalId,
                StorageTankId = lineStorageTankId,
                DestinationTerminalId = line.DestinationTerminalId,
                DestinationStorageTankId = line.DestinationStorageTankId,
                DestinationLocationId = line.DestinationLocationId,
                DestinationName = NormalizeNullable(line.DestinationName),
                DestinationReference = NormalizeNullable(line.DestinationReference),
                InventoryMovement = lineMovement,
                TruckDispatchId = null,
                SalesTransaction = directSaleDraft?.Sale,
                ReferenceDocument = lineReference,
                Notes = lineNotes
            };

            if (lineMovement is not null)
            {
                EnsureToInventoryAllocationInvariant(receipt, lineMovement, allocation);
            }
            else
            {
                EnsureDirectDispatchAllocationInvariant(receipt, allocation);
            }

            if (shouldCreateDirectTruckDispatch
                && line.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                && directTransport is not null)
            {
                var directTruckDispatch = new TruckDispatch
                {
                    DispatchMode = TruckDispatchMode.DirectFromReceipt,
                    LoadingReceiptAllocation = allocation,
                    ContractId = allocation.SourcePurchaseContractId!.Value,
                    ProductId = loading.ProductId,
                    TruckId = directTransport.CreatedTruck is null ? directTransport.TruckId : 0,
                    Truck = directTransport.CreatedTruck,
                    DriverId = directTransport.CreatedDriver is null ? directTransport.DriverId : null,
                    Driver = directTransport.CreatedDriver,
                    DestinationLocationId = line.DestinationLocationId,
                    DispatchDate = directDispatchDate ?? receipt.ReceiptDate,
                    Status = DispatchStatus.Loaded,
                    LoadedQuantityMt = line.QuantityMt,
                    TicketSerialNumber = directTruckTicketSerialNumber,
                    Notes = lineNotes
                };
                if (directTruckDispatch.DriverId is > 0)
                {
                    directTruckDispatch.CarrierPartyType = AccountingPartyType.Driver;
                    directTruckDispatch.CarrierPartyId = directTruckDispatch.DriverId;
                }

                allocation.Status = LoadingReceiptAllocationStatus.Completed;
                allocation.DirectTruckDispatches.Add(directTruckDispatch);
                directTruckDispatches.Add(directTruckDispatch);
            }

            allocations.Add(allocation);
            if (directSaleDraft is not null)
            {
                directSaleDrafts.Add((allocation, directSaleDraft));
            }
        }

        return new ReceiptGraphResult(inventoryMovements, allocations, directSaleDrafts, directTruckDispatches);
    }

    private async Task PopulateLookupsAsync(LoadingReceiptCreateViewModel model)
    {
        ViewBag.Terminals = new SelectList(
            await _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .ToListAsync(),
            "Id",
            "Name",
            model.TerminalId);

        ViewBag.StorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                .AsNoTracking()
                .OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display",
            model.StorageTankId);

        ViewBag.ReceiptDestinations = GetReceiptDestinationItems(model.ReceiptDestination);
        ViewBag.DestinationTerminals = new SelectList(
            await _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .ToListAsync(),
            "Id",
            "Name",
            model.DestinationTerminalId);
        ViewBag.DestinationStorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                .AsNoTracking()
                .OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display",
            model.DestinationStorageTankId);
        ViewBag.DestinationLocations = new SelectList(
            await _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .ToListAsync(),
            "Id",
            "Name",
            model.DestinationLocationId);

        var saleCustomers = await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name }).ToListAsync();
        var saleSuppliers = await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name }).ToListAsync();
        ViewBag.SaleBuyers = PTGOilSystem.Web.Models.Sales.SaleBuyerKey.BuildOptions(
            saleCustomers.Select(c => (c.Id, c.Name)), saleSuppliers.Select(s => (s.Id, s.Name)), model.SaleBuyerKey);

        ViewBag.SaleCurrencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            model.SaleCurrency);

        ViewBag.DirectTrucks = new SelectList(
            await _db.Trucks
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.PlateNumber)
                .Select(t => new { t.Id, t.PlateNumber })
                .ToListAsync(),
            "Id",
            "PlateNumber",
            model.DirectTruckId);

        ViewBag.DirectDrivers = new SelectList(
            await _db.Drivers
                .AsNoTracking()
                .Where(d => d.IsActive)
                .OrderBy(d => d.FullName)
                .Select(d => new { d.Id, d.FullName })
                .ToListAsync(),
            "Id",
            "FullName",
            model.DirectDriverId);
    }

    private async Task ValidateDirectSaleLineAsync(
        LoadingReceiptAllocationLineInput line,
        string prefix,
        ISet<string> seenInvoiceNumbers,
        ModelStateDictionary modelState)
    {
        static string Key(string prefix, string field)
            => string.IsNullOrWhiteSpace(prefix) ? field : $"{prefix}.{field}";

        if (line.SaleCustomerId.HasValue == line.SaleSupplierId.HasValue)
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleBuyerKey)), "برای DirectSale دقیقاً یک خریدار الزامی است.");
        }
        else if (line.SaleCustomerId.HasValue)
        {
            var customerExists = await _db.Customers
                .AsNoTracking()
                .AnyAsync(c => c.Id == line.SaleCustomerId.Value && c.IsActive);
            if (!customerExists)
            {
                modelState.AddModelError(Key(prefix, nameof(line.SaleCustomerId)), "مشتری فروش مستقیم معتبر نیست.");
            }
        }
        else if (!await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == line.SaleSupplierId!.Value && s.IsActive))
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleBuyerKey)), "تأمین‌کننده فروش مستقیم معتبر نیست.");
        }

        if (!line.SaleDate.HasValue)
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleDate)), "برای DirectSale تاریخ فروش الزامی است.");
        }

        // قیمت باز: قیمت و نرخ ارز بعداً از صفحهٔ همان فروش ثبت می‌شود.
        if (line.SaleIsPricePending)
        {
            line.SaleUnitPriceInCurrency = null;
            line.SaleAppliedFxRateToUsd = null;
        }
        else if (!line.SaleUnitPriceInCurrency.HasValue || line.SaleUnitPriceInCurrency.Value <= 0m)
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleUnitPriceInCurrency)), "برای DirectSale قیمت واحد فروش باید بزرگ‌تر از صفر باشد.");
        }

        if (line.SaleAppliedFxRateToUsd.HasValue && line.SaleAppliedFxRateToUsd.Value <= 0m)
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleAppliedFxRateToUsd)), "نرخ تبدیل فروش مستقیم باید بزرگ‌تر از صفر باشد.");
        }

        line.SaleCurrency = SystemCurrency.Normalize(line.SaleCurrency);
        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == line.SaleCurrency && c.IsActive))
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleCurrency)), "ارز فروش مستقیم معتبر نیست.");
        }

        if (string.IsNullOrWhiteSpace(line.SaleInvoiceNumber))
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleInvoiceNumber)), "برای DirectSale شماره فاکتور الزامی است.");
            return;
        }

        line.SaleInvoiceNumber = line.SaleInvoiceNumber.Trim();
        if (!seenInvoiceNumbers.Add(line.SaleInvoiceNumber))
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleInvoiceNumber)), "شماره فاکتور DirectSale در همین رسید تکراری است.");
            return;
        }

        var invoiceExists = await _db.SalesTransactions
            .AsNoTracking()
            .AnyAsync(s => s.InvoiceNumber == line.SaleInvoiceNumber);
        if (invoiceExists)
        {
            modelState.AddModelError(Key(prefix, nameof(line.SaleInvoiceNumber)), "این شماره فاکتور قبلاً ثبت شده است.");
        }
    }

    private Task<LoadingDirectSaleDraft> BuildLoadingDirectSaleDraftAsync(
        LoadingReceiptAllocationLineInput line,
        LoadingRegister loading)
        => new LoadingDirectSaleDraftService(_currencyConversion).BuildAsync(line, loading);

    private static LedgerPostingRequest BuildDirectSaleLedgerEntry(
        SalesTransaction sale,
        int sourcePurchaseContractId,
        CurrencyConversionResult conversion)
        => SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: sourcePurchaseContractId);

    private async Task<(LoadingRegister Loading, LoadingReceiptQuantitySnapshot Quantities)?> GetLoadingContextAsync(int loadingRegisterId)
    {
        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(l => l.Contract)
                .ThenInclude(c => c!.Customer)
            .Include(l => l.Product)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == loadingRegisterId && !l.IsCancelled && !l.IsArchived);

        if (loading is null)
        {
            return null;
        }

        return (loading, await GetCommittedReceiptQuantitySnapshotAsync(loadingRegisterId));
    }

    private async Task<LoadingRegister?> LockLoadingRegisterAsync(int loadingRegisterId)
    {
        if (_db.Database.IsRelational()
            && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return await _db.LoadingRegisters
                .FromSqlInterpolated($@"SELECT * FROM ""LoadingRegisters"" WHERE ""Id"" = {loadingRegisterId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync();
        }

        return await _db.LoadingRegisters
            .AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == loadingRegisterId);
    }

    private async Task<LoadingReceiptQuantitySnapshot> GetCommittedReceiptQuantitySnapshotAsync(int loadingRegisterId)
    {
        var receivedQuantityMt = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => r.LoadingRegisterId == loadingRegisterId && !r.IsCancelled)
            .SumAsync(r => (decimal?)r.ReceivedQuantityMt) ?? 0m;

        var receiptShortageLossMt = await GetCommittedReceiptShortageQuantityMtAsync(loadingRegisterId);
        var transportedFromLoadingMt = await GetCommittedLoadingTransportQuantityMtAsync(loadingRegisterId);

        return new LoadingReceiptQuantitySnapshot(receivedQuantityMt, receiptShortageLossMt, transportedFromLoadingMt);
    }

    private async Task<Dictionary<int, LoadingRegister>> LockLoadingRegistersAsync(IReadOnlyCollection<int> loadingRegisterIds)
    {
        if (loadingRegisterIds.Count == 0)
        {
            return new Dictionary<int, LoadingRegister>();
        }

        // ترتیب صعودی شناسه برای جلوگیری از بن‌بست هنگام قفل هم‌زمان چند بارگیری.
        var orderedIds = loadingRegisterIds.Distinct().OrderBy(id => id).ToArray();

        if (_db.Database.IsRelational()
            && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            var lockedRows = await _db.LoadingRegisters
                .FromSqlInterpolated($@"SELECT * FROM ""LoadingRegisters"" WHERE ""Id"" = ANY({orderedIds}) ORDER BY ""Id"" FOR UPDATE")
                .AsNoTracking()
                .ToListAsync();
            return lockedRows.ToDictionary(l => l.Id);
        }

        return await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => orderedIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id);
    }

    private async Task<Dictionary<int, LoadingReceiptQuantitySnapshot>> GetCommittedReceiptQuantitySnapshotsAsync(
        IReadOnlyCollection<int> loadingRegisterIds)
    {
        if (loadingRegisterIds.Count == 0)
        {
            return new Dictionary<int, LoadingReceiptQuantitySnapshot>();
        }

        var receivedByLoadingId = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => loadingRegisterIds.Contains(r.LoadingRegisterId) && !r.IsCancelled)
            .GroupBy(r => r.LoadingRegisterId)
            .Select(g => new { LoadingRegisterId = g.Key, ReceivedQuantityMt = g.Sum(r => r.ReceivedQuantityMt) })
            .ToDictionaryAsync(g => g.LoadingRegisterId, g => g.ReceivedQuantityMt);

        var shortageByLoadingId = await GetCommittedReceiptShortageQuantityByLoadingIdAsync(loadingRegisterIds);
        var transportedByLoadingId = await GetCommittedLoadingTransportQuantityByLoadingIdAsync(loadingRegisterIds);

        return loadingRegisterIds
            .Distinct()
            .ToDictionary(
                id => id,
                id => new LoadingReceiptQuantitySnapshot(
                    receivedByLoadingId.GetValueOrDefault(id),
                    shortageByLoadingId.GetValueOrDefault(id),
                    transportedByLoadingId.GetValueOrDefault(id)));
    }

    private async Task<decimal> GetCommittedLoadingTransportQuantityMtAsync(int loadingRegisterId)
    {
        var quantities = await GetCommittedLoadingTransportQuantityByLoadingIdAsync([loadingRegisterId]);
        return quantities.GetValueOrDefault(loadingRegisterId);
    }

    private async Task<Dictionary<int, decimal>> GetCommittedLoadingTransportQuantityByLoadingIdAsync(
        IReadOnlyCollection<int> loadingRegisterIds)
    {
        if (loadingRegisterIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        return await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId.HasValue
                && loadingRegisterIds.Contains(a.SourceLoadingRegisterId.Value)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(a => a.SourceLoadingRegisterId!.Value)
            .Select(g => new { LoadingRegisterId = g.Key, QuantityMt = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(x => x.LoadingRegisterId, x => x.QuantityMt);
    }

    private async Task<decimal> GetCommittedReceiptShortageQuantityMtAsync(int loadingRegisterId)
    {
        var quantitiesByLoadingId = await GetCommittedReceiptShortageQuantityByLoadingIdAsync([loadingRegisterId]);
        return quantitiesByLoadingId.GetValueOrDefault(loadingRegisterId);
    }

    private async Task<Dictionary<int, decimal>> GetCommittedReceiptShortageQuantityByLoadingIdAsync(IReadOnlyCollection<int> loadingRegisterIds)
    {
        if (loadingRegisterIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var lossRows = await _db.LossEvents
            .AsNoTracking()
            .Where(e => (e.LoadingRegisterId.HasValue && loadingRegisterIds.Contains(e.LoadingRegisterId.Value)
                    || e.LoadingReceiptId.HasValue
                        && e.LoadingReceipt != null
                        && loadingRegisterIds.Contains(e.LoadingReceipt.LoadingRegisterId))
                && e.Stage == LossEventStage.ReceiptShortage
                && !e.IsCancelled)
            .Select(e => new
            {
                LoadingRegisterId = e.LoadingRegisterId
                    ?? (e.LoadingReceipt != null ? e.LoadingReceipt.LoadingRegisterId : (int?)null),
                e.DifferenceQuantityMt,
                e.ChargeableLossMt
            })
            .ToListAsync();

        return lossRows
            .Where(e => e.LoadingRegisterId.HasValue)
            .GroupBy(e => e.LoadingRegisterId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(e => ResolveLossQuantityMt(e.DifferenceQuantityMt, e.ChargeableLossMt)));
    }

    private static decimal ResolveLossQuantityMt(decimal differenceQuantityMt, decimal chargeableLossMt)
        => differenceQuantityMt > 0m
            ? differenceQuantityMt
            : Math.Max(chargeableLossMt, 0m);

    private static long ToQuantityUnits(decimal value)
        => checked((long)decimal.Round(value / QuantityPrecisionUnit, 0, MidpointRounding.AwayFromZero));

    private static decimal FromQuantityUnits(long units)
        => units * QuantityPrecisionUnit;

    private static IReadOnlyList<BulkReceiptQuantityAllocation> AllocateBulkReceiptQuantities(
        IReadOnlyList<BulkReceiptOpenLoading> openLoadings,
        decimal requestedQuantityMt)
    {
        var quantities = ProportionalQuantityAllocator.Allocate(
            openLoadings.Select(x => x.RemainingQuantityMt).ToArray(), requestedQuantityMt);
        return openLoadings.Select((row, index) => new BulkReceiptQuantityAllocation(row, quantities[index]))
            .Where(x => x.QuantityMt > 0m).ToList();
    }

    private static string? BuildBulkReceiptReference(string? normalizedReference, LoadingRegister loading)
    {
        var sourceReference = normalizedReference
            ?? $"BULK-RCPT-{loading.ContractId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var loadingReference = loading.BillOfLadingNumber ?? loading.RwbNo ?? loading.WagonNumber ?? $"LOAD-{loading.Id}";
        var reference = $"{sourceReference} / {loadingReference}";
        return reference.Length <= 500 ? reference : reference[..500];
    }

    private static void ApplyLoadingContext(
        LoadingReceiptCreateViewModel model,
        LoadingRegister loading,
        LoadingReceiptQuantitySnapshot quantities)
    {
        model.ContractNumber = loading.Contract?.DisplayLabel ?? "";
        model.ProductName = loading.Product?.Name ?? "";
        model.LoadingDate = loading.LoadingDate;
        model.LoadedQuantityMt = loading.LoadedQuantityMt;
        // Phase 1 — «کرایه بدوش کیست» فقط برای نمایش از بارگیری والد (read-only).
        model.SourceFreightCostResponsibility = loading.FreightCostResponsibility;
        model.AlreadyReceivedQuantityMt = quantities.ReceivedQuantityMt;
        model.RemainingToReceiveMt = Math.Max(loading.LoadedQuantityMt - quantities.AccountedQuantityMt, 0m);
        model.LoadingPriceUsd = loading.LoadingPriceUsd;
        model.BillOfLadingNumber = loading.BillOfLadingNumber;
        model.RwbNo = loading.RwbNo;
        model.WagonNumber = loading.WagonNumber;
        model.VesselName = loading.Vessel?.Name;
        model.TruckPlateNumber = loading.Truck?.PlateNumber;
        model.SupplierName = loading.Contract?.Supplier?.Name;
        model.CustomerName = loading.Contract?.Customer?.Name;
        model.ConsigneeName = loading.ConsigneeName;
        model.DestinationName = loading.DestinationName;
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? loadingId, string? returnUrl = null, bool modal = false)
    {
        if (!loadingId.HasValue || loadingId.Value <= 0)
        {
            var sourceSelectionModel = new LoadingReceiptCreateViewModel
            {
                ReceiptDate = AfghanistanBusinessClock.SystemToday,
                DirectDispatchDate = AfghanistanBusinessClock.SystemToday,
                ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null
            };
            await PopulateReceiptSourceOptionsAsync();
            return View(sourceSelectionModel);
        }

        var loadingContext = await GetLoadingContextAsync(loadingId.Value);
        if (loadingContext is null)
        {
            return NotFound();
        }

        var model = new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = loadingId.Value,
            ReceiptDate = AfghanistanBusinessClock.SystemToday,
            DirectDispatchDate = AfghanistanBusinessClock.SystemToday,
            ReturnUrl = returnUrl
        };

        ApplyLoadingContext(model, loadingContext.Value.Loading, loadingContext.Value.Quantities);
        if (model.RemainingToReceiveMt <= 0m)
        {
            if (modal)
            {
                return Conflict("برای این بارگیری تمام رسیدها قبلاً ثبت شده‌اند.");
            }
            TempData["err"] = "برای این loading تمام receiptها قبلاً ثبت شده‌اند.";
            return RedirectToAction("Details", "Loading", new { id = loadingId.Value });
        }

        await PopulateLookupsAsync(model);
        if (modal)
        {
            ViewData["IsReceiptModal"] = true;
            ViewData["CancelUrl"] = returnUrl;
            return PartialView("_ReceiptCreateForm", model);
        }
        return View(model);
    }

    private async Task PopulateReceiptSourceOptionsAsync()
    {
        var sources = await _db.LoadingRegisters.AsNoTracking()
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .OrderByDescending(l => l.LoadingDate)
            .ThenByDescending(l => l.Id)
            .Take(200)
            .Select(l => new
            {
                l.Id,
                l.LoadingDate,
                l.LoadedQuantityMt,
                ReceivedQuantityMt = l.Receipts.Where(r => !r.IsCancelled).Sum(r => (decimal?)r.ReceivedQuantityMt) ?? 0m,
                ContractName = l.Contract != null ? l.Contract.ContractName : "",
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                Reference = l.WagonNumber ?? l.BillOfLadingNumber ?? l.RwbNo
            })
            .ToListAsync();

        var sourceLoadingIds = sources.Select(s => s.Id).ToList();
        var receiptShortageByLoadingId = await GetCommittedReceiptShortageQuantityByLoadingIdAsync(sourceLoadingIds);

        ViewBag.LoadingReceiptSourceOptions = sources
            .Select(s => new
            {
                s.Id,
                s.LoadingDate,
                LoadedQuantityMt = Math.Max(
                    s.LoadedQuantityMt - receiptShortageByLoadingId.GetValueOrDefault(s.Id),
                    0m),
                s.ReceivedQuantityMt,
                ContractNumber = ContractUiText.FormatDisplayLabel(s.ContractName, s.ContractNumber),
                s.ProductName,
                s.Reference
            })
            .Where(s => s.LoadedQuantityMt - s.ReceivedQuantityMt > 0m)
            .Select(s => new SelectListItem
            {
                Value = s.Id.ToString(),
                Text = $"#{s.Id} — {s.LoadingDate.ToCalendarString("yyyy-MM-dd")} — {s.ContractNumber} — {s.ProductName} — {s.Reference} — {(s.LoadedQuantityMt - s.ReceivedQuantityMt):N3} MT"
            })
            .ToList();
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        LoadingReceiptCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var loadingContext = await GetLoadingContextAsync(model.LoadingRegisterId);
        if (loadingContext is null)
        {
            return NotFound();
        }

        var loading = loadingContext.Value.Loading;
        ApplyLoadingContext(model, loading, loadingContext.Value.Quantities);
        NormalizeReceiptInputs(model);

        var normalizedReference = string.IsNullOrWhiteSpace(model.ReferenceDocument)
            ? null
            : model.ReferenceDocument.Trim();
        var normalizedNotes = string.IsNullOrWhiteSpace(model.Notes)
            ? null
            : model.Notes.Trim();
        var normalizedDestinationName = string.IsNullOrWhiteSpace(model.AllocationDestinationName)
            ? null
            : model.AllocationDestinationName.Trim();
        var normalizedDestinationReference = string.IsNullOrWhiteSpace(model.DestinationReference)
            ? null
            : model.DestinationReference.Trim();

        ValidateReceiptDestination(model, ModelState);
        NormalizeAndValidateAllocationDestination(model, ModelState);
        if (model.ReceiptDate.Date < loading.LoadingDate.Date)
        {
            ModelState.AddModelError(nameof(model.ReceiptDate), BuildReceiptBeforeLoadingMessage(model.ReceiptDate, [loading]));
        }
        var shouldCreateScalarDirectTruckDispatch = model.ReceiptDestination == LoadingReceiptDestination.DirectDispatch
            && model.AllocationDestination == LoadingReceiptAllocationDestination.DirectDispatchToTruck;
        if (shouldCreateScalarDirectTruckDispatch && !model.DirectDispatchDate.HasValue)
        {
            model.DirectDispatchDate = model.ReceiptDate;
        }

        if (model.ReceiptDestination == LoadingReceiptDestination.DirectDispatch
            && loading.ContractId <= 0)
        {
            ModelState.AddModelError(nameof(model.ReceiptDestination), "برای مسیرهای trace بعد از رسید، قرارداد خرید منبع باید روی Loading مشخص باشد.");
        }

        var terminal = await _db.Terminals
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == model.TerminalId && t.IsActive);
        if (terminal is null)
        {
            ModelState.AddModelError(nameof(model.TerminalId), "ترمینال انتخاب‌شده معتبر نیست.");
        }

        // ثبت در موجودی بدون مخزن، موجودی بی‌مکان می‌سازد. مخزن الزامی است.
        if (model.ReceiptDestination == LoadingReceiptDestination.ToInventory && !model.StorageTankId.HasValue)
        {
            ModelState.AddModelError(nameof(model.StorageTankId), "برای ثبت در موجودی، انتخاب مخزن الزامی است.");
        }

        if (model.ReceiptDestination == LoadingReceiptDestination.ToInventory && model.StorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.StorageTankId.Value);
            if (tank is null)
            {
                ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (tank.TerminalId != model.TerminalId)
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده به ترمینال انتخابی تعلق ندارد.");
                }

                if (tank.ProductId.HasValue && tank.ProductId != loading.ProductId)
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده برای کالای این loading تعریف نشده است.");
                }
            }
        }

        DirectTransportResolution? directTransport = null;
        if (model.ReceiptDestination == LoadingReceiptDestination.DirectDispatch)
        {
            if (model.DestinationTerminalId.HasValue)
            {
                var destinationTerminal = await _db.Terminals
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == model.DestinationTerminalId.Value && t.IsActive);
                if (destinationTerminal is null)
                {
                    ModelState.AddModelError(nameof(model.DestinationTerminalId), "ترمینال مقصد انتخاب‌شده معتبر نیست.");
                }
            }

            if (model.DestinationStorageTankId.HasValue)
            {
                var destinationTank = await _db.StorageTanks
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == model.DestinationStorageTankId.Value);
                if (destinationTank is null)
                {
                    ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد انتخاب‌شده معتبر نیست.");
                }
                else
                {
                    if (!model.DestinationTerminalId.HasValue)
                    {
                        ModelState.AddModelError(nameof(model.DestinationTerminalId), "برای انتخاب مخزن مقصد، ترمینال مقصد نیز باید مشخص شود.");
                    }
                    else if (destinationTank.TerminalId != model.DestinationTerminalId.Value)
                    {
                        ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد به ترمینال مقصد انتخاب‌شده تعلق ندارد.");
                    }

                    if (destinationTank.ProductId.HasValue && destinationTank.ProductId != loading.ProductId)
                    {
                        ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد برای کالای این loading تعریف نشده است.");
                    }
                }
            }

            if (model.DestinationLocationId.HasValue)
            {
                var destinationLocationExists = await _db.Locations
                    .AsNoTracking()
                    .AnyAsync(l => l.Id == model.DestinationLocationId.Value);
                if (!destinationLocationExists)
                {
                    ModelState.AddModelError(nameof(model.DestinationLocationId), "شهر/موقعیت مقصد انتخاب‌شده معتبر نیست.");
                }
            }

            if (model.AllocationDestination == LoadingReceiptAllocationDestination.TransferToOtherTerminal
                && !model.DestinationTerminalId.HasValue)
            {
                ModelState.AddModelError(nameof(model.DestinationTerminalId), "برای انتقال به ترمینال دیگر، انتخاب ترمینال مقصد الزامی است.");
            }

            if (shouldCreateScalarDirectTruckDispatch)
            {
                directTransport = await ResolveDirectTransportAsync(model);
            }
        }

        var directSaleInvoiceNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (model.ReceiptDestination == LoadingReceiptDestination.DirectDispatch
            && model.AllocationDestination == LoadingReceiptAllocationDestination.DirectSale)
        {
            var scalarDirectSaleLine = BuildScalarAllocationLine(
                model,
                normalizedReference,
                normalizedNotes,
                normalizedDestinationName,
                normalizedDestinationReference,
                loading);
            await ValidateDirectSaleLineAsync(scalarDirectSaleLine, string.Empty, directSaleInvoiceNumbers, ModelState);
        }

        if (model.RemainingToReceiveMt <= 0m)
        {
            ModelState.AddModelError(string.Empty, "برای این loading ظرفیتی برای receipt جدید باقی نمانده است.");
        }
        else if (model.ReceivedQuantityMt > model.RemainingToReceiveMt)
        {
            ModelState.AddModelError(string.Empty, $"مقدار رسید بیشتر از باقیمانده loading است. باقیمانده فعلی: {model.RemainingToReceiveMt:N4} MT.");
        }

        // کاربر فقط «کسری دارد / ندارد» را انتخاب می‌کند؛ حالت ثبت ضایعات از همان استنتاج
        // می‌شود: کسری معلوم = همین حالا ثبت، بدون کسری = واگذاری به تسویهٔ نهایی مخزن.
        model.LossMode = model.Loss.Enabled
            ? ReceiptLossMode.ImmediateKnownLoss
            : ReceiptLossMode.DeferredTankSettlement;

        // «ضایعات بعداً از تسویه مخزن» فقط برای رسید ورود به موجودی معنا دارد.
        if (model.ReceiptDestination != LoadingReceiptDestination.ToInventory)
        {
            model.LossMode = ReceiptLossMode.ImmediateKnownLoss;
        }

        // در حالت معوق، ضایعه هنگام رسید ثبت نمی‌شود؛ بعداً از «تسویهٔ نهایی مخزن» محاسبه می‌شود.
        if (model.LossMode == ReceiptLossMode.DeferredTankSettlement)
        {
            model.Loss.Enabled = false;
        }

        StageLossCaptureMapper.Validate(
            model.Loss,
            (field, error) => ModelState.AddModelError(BuildLossFieldKey(field), error));

        if (model.Loss.Enabled)
        {
            await _lossWorkflow.ValidateAsync(
                BuildReceiptLossSubmission(model, loading, normalizedReference),
                (field, error) => ModelState.AddModelError(BuildLossFieldKey(field), error));
        }

        if (!ModelState.IsValid)
        {
            model.ReferenceDocument = normalizedReference;
            model.Notes = normalizedNotes;
            model.AllocationDestinationName = normalizedDestinationName;
            model.DestinationReference = normalizedDestinationReference;
            await PopulateLookupsAsync(model);
            return View(model);
        }

        try
        {
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync()
                : null;

            try
            {
                var lockedLoading = await LockLoadingRegisterAsync(loading.Id);
                if (lockedLoading is null || lockedLoading.IsCancelled || lockedLoading.IsArchived)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    return NotFound();
                }

                // «اصلاح مقدار»: رسید قبلی داخل همین تراکنش لغو می‌شود تا ظرفیت باقی‌مانده و
                // اثرهای موجودی/مالی آن پیش از ثبت رسید اصلاح‌شده برگردد.
                if (model.CorrectionOfReceiptId is > 0)
                {
                    var correctionReason = string.IsNullOrWhiteSpace(model.CorrectionReason)
                        ? $"اصلاح رسید #{model.CorrectionOfReceiptId.Value} و ثبت دوباره"
                        : model.CorrectionReason!.Trim();

                    var cancellationResult = await Cancellation.CancelWithinCurrentTransactionAsync(
                        [model.CorrectionOfReceiptId.Value],
                        correctionReason,
                        _currentUser?.UserId);

                    if (!cancellationResult.Succeeded)
                    {
                        if (transaction is not null)
                        {
                            await transaction.RollbackAsync();
                        }

                        ModelState.AddModelError(string.Empty, BuildBlockerMessage(cancellationResult.Blockers));
                        model.ReferenceDocument = normalizedReference;
                        model.Notes = normalizedNotes;
                        model.AllocationDestinationName = normalizedDestinationName;
                        model.DestinationReference = normalizedDestinationReference;
                        await PopulateLookupsAsync(model);
                        return View(model);
                    }
                }

                var committedQuantities = await GetCommittedReceiptQuantitySnapshotAsync(loading.Id);
                loading.LoadedQuantityMt = lockedLoading.LoadedQuantityMt;
                ApplyLoadingContext(model, loading, committedQuantities);

                if (model.RemainingToReceiveMt <= 0m)
                {
                    ModelState.AddModelError(string.Empty, "برای این loading ظرفیتی برای receipt جدید باقی نمانده است.");
                }
                else if (model.ReceivedQuantityMt > model.RemainingToReceiveMt)
                {
                    ModelState.AddModelError(string.Empty, $"مقدار رسید بیشتر از باقیمانده loading است. باقیمانده فعلی: {model.RemainingToReceiveMt:N4} MT.");
                }

                if (!ModelState.IsValid)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    model.ReferenceDocument = normalizedReference;
                    model.Notes = normalizedNotes;
                    model.AllocationDestinationName = normalizedDestinationName;
                    model.DestinationReference = normalizedDestinationReference;
                    await PopulateLookupsAsync(model);
                    return View(model);
                }

                var receiptStorageTankId = model.ReceiptDestination == LoadingReceiptDestination.ToInventory
                    ? model.StorageTankId
                    : null;
                var receipt = new LoadingReceipt
                {
                    LoadingRegisterId = loading.Id,
                    TerminalId = model.TerminalId,
                    StorageTankId = receiptStorageTankId,
                    ReceiptDestination = model.ReceiptDestination,
                    LossMode = model.LossMode,
                    ReceiptDate = model.ReceiptDate,
                    ReceivedQuantityMt = model.ReceivedQuantityMt,
                    ArrivalDate = model.ArrivalDate,
                    LeakDate = model.LeakDate,
                    ActualArrivedQuantityMt = model.ActualArrivedQuantityMt,
                    ReferenceDocument = normalizedReference,
                    Notes = normalizedNotes
                };

                // یک رسید = یک مقصد. تخصیص چندخطی (ترکیبی) از فرم حذف شده است؛ ساختار
                // چندتخصیصی فقط برای خواندن رسیدهای قدیمی باقی مانده است.
                IReadOnlyList<LoadingReceiptAllocationLineInput> effectiveAllocationLines =
                [
                    BuildScalarAllocationLine(
                        model,
                        normalizedReference,
                        normalizedNotes,
                        normalizedDestinationName,
                        normalizedDestinationReference,
                        loading)
                ];

                var graph = await BuildReceiptGraphAsync(
                    loading,
                    receipt,
                    effectiveAllocationLines,
                    shouldCreateScalarDirectTruckDispatch,
                    directTransport,
                    model.DirectDispatchDate,
                    model.DirectTruckTicketSerialNumber,
                    normalizedReference,
                    normalizedNotes);
                var inventoryMovements = graph.Movements;
                var allocations = graph.Allocations;
                var directSaleDrafts = graph.DirectSaleDrafts;
                var directTruckDispatches = graph.Dispatches;

                foreach (var movement in inventoryMovements.Where(m => string.IsNullOrWhiteSpace(m.ReferenceDocument)))
                {
                    movement.ReferenceDocument = $"LOADING-RECEIPT:{loading.Id}:{Guid.NewGuid():N}";
                }

                // PTG-P0-01 — توکن با همان SaveChanges و همان Transaction مصرف می‌شود.
                _formTokens.Stamp(formToken, "LoadingReceipt.Create", nameof(LoadingReceipt));

                _db.LoadingReceipts.Add(receipt);
                _db.SalesTransactions.AddRange(directSaleDrafts.Select(d => d.Draft.Sale));
                _db.LoadingReceiptAllocations.AddRange(allocations);
                _db.TruckDispatches.AddRange(directTruckDispatches);
                if (inventoryMovements.Count > 0)
                {
                    await _movements.PostInboundRangeAsync(inventoryMovements);
                }
                else
                {
                    await _db.SaveChangesAsync();
                }

                // هم‌ردیف با directSaleDrafts: فروش با قیمت باز سطر دفتر ندارد (null) تا ایندکس‌ها جابه‌جا نشوند.
                var directSaleLedgerEntries = directSaleDrafts
                    .Select(d => SalePendingPricing.ShouldPostLedger(d.Draft.Sale)
                        ? Ledger.Post(BuildDirectSaleLedgerEntry(
                            d.Draft.Sale,
                            d.Allocation.SourcePurchaseContractId!.Value,
                            d.Draft.Conversion))
                        : null)
                    .ToList();
                await _db.SaveChangesAsync();

                // مرحله ۶ — Dual-write داخل همان Transaction قدیمی: کالای رسیده از «در راه»
                // به موجودی منتقل می‌شود. Adapter خودش پست‌نشدنِ خریدِ متناظر را Skip می‌کند.
                var receiptAccountingOutcomes = await new Services.Accounting.LoadingReceiptAccountingCoordinator(_purchaseAccounting)
                    .PostAsync([receipt]);

                // فروش مستقیم موجودی مخزن نمی‌سازد؛ آداپتر فروش بهای منبع معتبر خودش را می‌خواند.
                if (_salesAccounting is not null)
                {
                    foreach (var directSale in directSaleDrafts.Select(d => d.Draft.Sale))
                    {
                        await _salesAccounting.TryPostSaleAsync(directSale);
                        await _salesAccounting.TryPostCogsAsync(directSale);
                    }
                }

                foreach (var allocation in allocations)
                {
                    if (allocation.InventoryMovement is not null)
                    {
                        EnsureToInventoryAllocationInvariant(receipt, allocation.InventoryMovement, allocation);
                    }
                    else
                    {
                        EnsureDirectDispatchAllocationInvariant(receipt, allocation);
                    }
                }

                var hasEmbeddedLoss = model.Loss.Enabled && model.Loss.QuantityMt.GetValueOrDefault() > 0m;
                if (hasEmbeddedLoss)
                {
                    var lossSubmission = BuildReceiptLossSubmission(model, loading, normalizedReference);
                    lossSubmission.LoadingReceiptId = receipt.Id;
                    await _lossWorkflow.CreateAsync(lossSubmission);
                }

                await LogCreatedDirectTransportAsync(directTransport);
                await _audit.LogAsync(
                    nameof(LoadingReceipt),
                    receipt.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("LoadingRegisterId", receipt.LoadingRegisterId),
                        ("TerminalId", receipt.TerminalId),
                        ("StorageTankId", receipt.StorageTankId),
                        ("ReceiptDestination", receipt.ReceiptDestination),
                        ("LossMode", receipt.LossMode),
                        ("ReceiptDate", receipt.ReceiptDate),
                        ("ReceivedQuantityMt", receipt.ReceivedQuantityMt),
                        ("ReferenceDocument", receipt.ReferenceDocument),
                        ("InventoryMovementId", inventoryMovements.FirstOrDefault()?.Id)));

                foreach (var movement in inventoryMovements)
                {
                    await _audit.LogAsync(
                        nameof(InventoryMovement),
                        movement.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ProductId", movement.ProductId),
                            ("ContractId", movement.ContractId),
                            ("TerminalId", movement.TerminalId),
                            ("StorageTankId", movement.StorageTankId),
                            ("Direction", movement.Direction),
                            ("QuantityMt", movement.QuantityMt),
                            ("MovementDate", movement.MovementDate),
                            ("ReferenceDocument", movement.ReferenceDocument),
                            ("LoadingReceiptId", movement.LoadingReceiptId)));
                }

                for (var i = 0; i < directSaleDrafts.Count; i++)
                {
                    var sale = directSaleDrafts[i].Draft.Sale;
                    var allocation = directSaleDrafts[i].Allocation;
                    var ledger = directSaleLedgerEntries[i];
                    await _audit.LogAsync(
                        nameof(SalesTransaction),
                        sale.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ReceiptAllocationId", allocation.Id),
                            ("SourcePurchaseContractId", allocation.SourcePurchaseContractId),
                            ("CompanyId", sale.CompanyId),
                            ("CustomerId", sale.CustomerId),
                            ("SupplierId", sale.SupplierId),
                            ("ProductId", sale.ProductId),
                            ("DestinationLocationId", sale.DestinationLocationId),
                            ("SaleStage", sale.SaleStage),
                            ("InvoiceNumber", sale.InvoiceNumber),
                            ("SaleDate", sale.SaleDate),
                            ("QuantityMt", sale.QuantityMt),
                            ("Currency", sale.Currency),
                            ("UnitPriceInCurrency", sale.UnitPriceInCurrency),
                            ("AppliedFxRateToUsd", sale.AppliedFxRateToUsd),
                            ("UnitPriceUsd", sale.UnitPriceUsd),
                            ("TotalInCurrency", sale.TotalInCurrency),
                            ("TotalUsd", sale.TotalUsd),
                            ("LedgerReference", ledger?.Reference)));
                }

                foreach (var dispatch in directTruckDispatches)
                {
                    await _audit.LogAsync(
                        nameof(TruckDispatch),
                        dispatch.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("DispatchMode", dispatch.DispatchMode),
                            ("LoadingReceiptAllocationId", dispatch.LoadingReceiptAllocationId),
                            ("ContractId", dispatch.ContractId),
                            ("ProductId", dispatch.ProductId),
                            ("TruckId", dispatch.TruckId),
                            ("DriverId", dispatch.DriverId),
                            ("DestinationLocationId", dispatch.DestinationLocationId),
                            ("DispatchDate", dispatch.DispatchDate),
                            ("LoadedQuantityMt", dispatch.LoadedQuantityMt),
                            ("TicketSerialNumber", dispatch.TicketSerialNumber)));
                }

                foreach (var allocation in allocations)
                {
                    await _audit.LogAsync(
                        nameof(LoadingReceiptAllocation),
                        allocation.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("LoadingReceiptId", receipt.Id),
                            ("Destination", allocation.Destination),
                            ("Status", allocation.Status),
                            ("QuantityMt", allocation.QuantityMt),
                            ("SourcePurchaseContractId", allocation.SourcePurchaseContractId),
                            ("TerminalId", allocation.TerminalId),
                            ("StorageTankId", allocation.StorageTankId),
                            ("DestinationTerminalId", allocation.DestinationTerminalId),
                            ("DestinationStorageTankId", allocation.DestinationStorageTankId),
                            ("DestinationLocationId", allocation.DestinationLocationId),
                            ("DestinationName", allocation.DestinationName),
                            ("DestinationReference", allocation.DestinationReference),
                            ("InventoryMovementId", allocation.InventoryMovement?.Id),
                            ("TruckDispatchId", allocation.TruckDispatchId),
                            ("SalesTransactionId", allocation.SalesTransactionId),
                            ("ReferenceDocument", allocation.ReferenceDocument)));
                }

                await _db.SaveChangesAsync();

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                if (receipt.ReceiptDestination == LoadingReceiptDestination.ToInventory)
                    RecordReceiptAccountingOutcomes(receiptAccountingOutcomes);

                TempData["ok"] = model.ReceiptDestination == LoadingReceiptDestination.DirectDispatch
                    ? model.AllocationDestination switch
                    {
                        LoadingReceiptAllocationDestination.DirectSale =>
                            "رسید ثبت شد و فروش مستقیم ساخته شد. موجودی مخزن تغییر نکرد.",
                        LoadingReceiptAllocationDestination.TransferToOtherTerminal =>
                            "رسید ثبت شد. کالا تا رسیدن به ترمینال مقصد «در مسیر» است.",
                        _ => directTruckDispatches.Count > 0
                            ? "رسید ثبت شد و ارسال با موتر ساخته شد. موجودی مخزن تغییر نکرد."
                            : "رسید ارسال مستقیم ثبت شد. موجودی مخزن تغییر نکرد."
                    }
                    : hasEmbeddedLoss
                        ? "رسید و کسری آن با موفقیت ثبت شد و موجودی مخزن افزایش یافت."
                        : "رسید ثبت شد و موجودی مخزن افزایش یافت.";
                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return IsPageModalRequest()
                        ? PageModalComplete(localReturnUrl)
                        : Redirect(localReturnUrl);
                }

                if (IsPageModalRequest())
                {
                    var redirectUrl = Url.Action(nameof(Details), new { id = receipt.Id })
                        ?? $"/LoadingReceipts/Details/{receipt.Id}";
                    return PageModalComplete(redirectUrl);
                }

                return RedirectToAction(nameof(Details), new { id = receipt.Id });
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }

                throw;
            }
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            TempData["err"] = "این رسید قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create loading receipt.");
            ModelState.AddModelError(string.Empty, "ثبت receipt موجودی انجام نشد. دوباره تلاش کنید.");
        }

        model.ReferenceDocument = normalizedReference;
        model.Notes = normalizedNotes;
        model.AllocationDestinationName = normalizedDestinationName;
        model.DestinationReference = normalizedDestinationReference;
        await PopulateLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkCreate(
        LoadingReceiptBulkCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (await IsReceiptRequestProcessedAsync(formToken, "LoadingReceipt.BulkCreate"))
            return BulkReceiptAlreadyProcessed(model);
        model.ReferenceDocument = NormalizeNullable(model.ReferenceDocument);
        model.Notes = NormalizeNullable(model.Notes);
        model.LoadingRegisterIds = (model.LoadingRegisterIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (model.LoadingRegisterIds.Count == 0)
        {
            ModelState.AddModelError(nameof(model.LoadingRegisterIds), "حداقل یک بارگیری را برای رسید جمعی انتخاب کنید.");
        }

        var requestedQuantityMt = FromQuantityUnits(ToQuantityUnits(model.TotalReceivedQuantityMt));
        if (requestedQuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.TotalReceivedQuantityMt), "مقدار کل رسید باید بزرگ‌تر از صفر باشد.");
        }

        var requestedLossMt = FromQuantityUnits(ToQuantityUnits(model.TotalLossQuantityMt.GetValueOrDefault()));
        var requestedLossToleranceMt = FromQuantityUnits(ToQuantityUnits(model.TotalLossToleranceQuantityMt.GetValueOrDefault()));
        model.LossResponsiblePartyName = NormalizeNullable(model.LossResponsiblePartyName);
        if (!Enum.IsDefined(typeof(BulkReceiptLossMode), model.LossMode))
        {
            ModelState.AddModelError(nameof(model.LossMode), "نحوه ثبت ضایعات معتبر نیست.");
        }

        var isImmediateLossMode = model.LossMode == BulkReceiptLossMode.ImmediateKnownLoss;
        var isDeferredLossMode = model.LossMode == BulkReceiptLossMode.DeferredTankSettlement;
        var accountedLossMt = isImmediateLossMode ? requestedLossMt : 0m;

        if (requestedLossMt < 0m)
        {
            ModelState.AddModelError(nameof(model.TotalLossQuantityMt), "ضایعات کل نمی‌تواند منفی باشد.");
        }
        else if (isDeferredLossMode && requestedLossMt > 0m)
        {
            ModelState.AddModelError(nameof(model.TotalLossQuantityMt), "در حالت ضایعات معوق، ضایعات فوری وارد نکنید؛ ضایعات بعداً از تسویه نهایی مخزن ثبت می‌شود.");
        }
        else if (model.LossMode == BulkReceiptLossMode.None && requestedLossMt > 0m)
        {
            ModelState.AddModelError(nameof(model.TotalLossQuantityMt), "برای ثبت ضایعات جمعی، گزینه «ثبت ضایعات همین حالا» را انتخاب کنید.");
        }

        if (requestedLossToleranceMt < 0m)
        {
            ModelState.AddModelError(nameof(model.TotalLossToleranceQuantityMt), "تلورانس ضایعات نمی‌تواند منفی باشد.");
        }
        else if (requestedLossToleranceMt > requestedLossMt)
        {
            ModelState.AddModelError(nameof(model.TotalLossToleranceQuantityMt), "تلورانس ضایعات نمی‌تواند از ضایعات کل بیشتر باشد.");
        }

        var contractExists = await _db.Contracts
            .AsNoTracking()
            .AnyAsync(c => c.Id == model.ContractId && c.ContractType == ContractType.Purchase);
        if (!contractExists)
        {
            ModelState.AddModelError(nameof(model.ContractId), "قرارداد خرید معتبر نیست.");
        }

        var terminal = await _db.Terminals
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == model.TerminalId && t.IsActive);
        if (terminal is null)
        {
            ModelState.AddModelError(nameof(model.TerminalId), "ترمینال انتخاب‌شده معتبر نیست.");
        }

        if (model.StorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.StorageTankId.Value);
            if (tank is null)
            {
                ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (tank.TerminalId != model.TerminalId)
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده به ترمینال انتخابی تعلق ندارد.");
                }

                var productIds = await _db.LoadingRegisters
                    .AsNoTracking()
                    .Where(l => model.LoadingRegisterIds.Contains(l.Id) && l.ContractId == model.ContractId)
                    .Select(l => l.ProductId)
                    .Distinct()
                    .ToListAsync();
                if (tank.ProductId.HasValue && productIds.Any(productId => productId != tank.ProductId.Value))
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده برای کالای بارگیری‌های انتخاب‌شده تعریف نشده است.");
                }
            }
        }
        else if (isDeferredLossMode)
        {
            ModelState.AddModelError(nameof(model.StorageTankId), "برای حالت ضایعات معوق، انتخاب مخزن الزامی است.");
        }

        var selectedLoadings = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => model.LoadingRegisterIds.Contains(l.Id) && l.ContractId == model.ContractId && !l.IsCancelled && !l.IsArchived)
            .OrderBy(l => l.LoadingDate)
            .ThenBy(l => l.Id)
            .ToListAsync();

        if (selectedLoadings.Count != model.LoadingRegisterIds.Count)
        {
            ModelState.AddModelError(nameof(model.LoadingRegisterIds), "یک یا چند بارگیری انتخاب‌شده مربوط به این قرارداد نیست یا پیدا نشد.");
        }

        var loadingsAfterReceipt = selectedLoadings
            .Where(l => model.ReceiptDate.Date < l.LoadingDate.Date)
            .ToList();
        if (loadingsAfterReceipt.Count > 0)
        {
            ModelState.AddModelError(nameof(model.ReceiptDate), BuildReceiptBeforeLoadingMessage(model.ReceiptDate, loadingsAfterReceipt));
        }

        var selectedLoadingIds = selectedLoadings.Select(l => l.Id).ToList();
        var alreadyReceivedByLoadingId = selectedLoadingIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.LoadingReceipts
                .AsNoTracking()
                .Where(r => selectedLoadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled)
                .GroupBy(r => r.LoadingRegisterId)
                .Select(g => new { LoadingRegisterId = g.Key, ReceivedQuantityMt = g.Sum(r => r.ReceivedQuantityMt) })
                .ToDictionaryAsync(g => g.LoadingRegisterId, g => g.ReceivedQuantityMt);
        var receiptShortageBySelectedLoadingId = await GetCommittedReceiptShortageQuantityByLoadingIdAsync(selectedLoadingIds);

        var openLoadings = selectedLoadings
            .Select(l =>
            {
                var alreadyReceived = alreadyReceivedByLoadingId.GetValueOrDefault(l.Id);
                var receiptShortage = receiptShortageBySelectedLoadingId.GetValueOrDefault(l.Id);
                return new BulkReceiptOpenLoading(
                    l,
                    alreadyReceived,
                    Math.Max(l.LoadedQuantityMt - alreadyReceived - receiptShortage, 0m));
            })
            .Where(l => l.RemainingQuantityMt > 0m)
            .ToList();

        var totalRemainingQuantityMt = openLoadings.Sum(l => l.RemainingQuantityMt);
        if (openLoadings.Count == 0)
        {
            ModelState.AddModelError(nameof(model.LoadingRegisterIds), "برای بارگیری‌های انتخاب‌شده مقدار باقی‌مانده برای رسید وجود ندارد.");
        }
        else if (requestedQuantityMt > totalRemainingQuantityMt)
        {
            ModelState.AddModelError(
                nameof(model.TotalReceivedQuantityMt),
                $"مقدار کل رسید از باقی‌مانده بارگیری‌های انتخاب‌شده بیشتر است. باقی‌مانده فعلی: {totalRemainingQuantityMt:N4} MT.");
        }
        else if (requestedQuantityMt + accountedLossMt > totalRemainingQuantityMt)
        {
            ModelState.AddModelError(
                nameof(model.TotalLossQuantityMt),
                $"مجموع رسید و ضایعات از باقی‌مانده بارگیری‌های انتخاب‌شده بیشتر است. باقی‌مانده فعلی: {totalRemainingQuantityMt:N4} MT.");
        }

        if (!ModelState.IsValid)
        {
            return RedirectAfterBulkReceiptError(model);
        }

        try
        {
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync()
                : null;

            try
            {
                // قفل و خواندن مقادیر ثبت‌شده به‌صورت دسته‌ای انجام می‌شود تا به‌ازای هر بارگیری
                // چند رفت‌وبرگشت جداگانه به دیتابیس نداشته باشیم.
                var lockedLoadingsById = await LockLoadingRegistersAsync(selectedLoadingIds);
                if (await IsReceiptRequestProcessedAsync(formToken, "LoadingReceipt.BulkCreate"))
                {
                    if (transaction is not null) await transaction.RollbackAsync();
                    return BulkReceiptAlreadyProcessed(model);
                }
                var committedSnapshotsByLoadingId = await GetCommittedReceiptQuantitySnapshotsAsync(
                    lockedLoadingsById.Values
                        .Where(l => l.ContractId == model.ContractId)
                        .Select(l => l.Id)
                        .ToList());

                var lockedOpenLoadings = new List<BulkReceiptOpenLoading>();
                foreach (var loadingId in selectedLoadingIds)
                {
                    if (!lockedLoadingsById.TryGetValue(loadingId, out var lockedLoading)
                        || lockedLoading.ContractId != model.ContractId || lockedLoading.IsCancelled || lockedLoading.IsArchived)
                    {
                        ModelState.AddModelError(nameof(model.LoadingRegisterIds), "یک یا چند بارگیری انتخاب‌شده در زمان ثبت قابل تایید نبود.");
                        continue;
                    }

                    var committedQuantities = committedSnapshotsByLoadingId.GetValueOrDefault(lockedLoading.Id)
                        ?? new LoadingReceiptQuantitySnapshot(0m, 0m, 0m);
                    var remainingQuantityMt = Math.Max(lockedLoading.LoadedQuantityMt - committedQuantities.AccountedQuantityMt, 0m);
                    if (remainingQuantityMt > 0m)
                    {
                        lockedOpenLoadings.Add(new BulkReceiptOpenLoading(
                            lockedLoading,
                            committedQuantities.ReceivedQuantityMt,
                            remainingQuantityMt));
                    }
                }

                var lockedRemainingQuantityMt = lockedOpenLoadings.Sum(l => l.RemainingQuantityMt);
                if (lockedOpenLoadings.Count == 0)
                {
                    ModelState.AddModelError(nameof(model.LoadingRegisterIds), "برای بارگیری‌های انتخاب‌شده مقدار باقی‌مانده برای رسید وجود ندارد.");
                }
                else if (requestedQuantityMt > lockedRemainingQuantityMt)
                {
                    ModelState.AddModelError(
                        nameof(model.TotalReceivedQuantityMt),
                        $"مقدار کل رسید از باقی‌مانده فعلی بیشتر است. باقی‌مانده فعلی: {lockedRemainingQuantityMt:N4} MT.");
                }
                else if (requestedQuantityMt + accountedLossMt > lockedRemainingQuantityMt)
                {
                    ModelState.AddModelError(
                        nameof(model.TotalLossQuantityMt),
                        $"مجموع رسید و ضایعات از باقی‌مانده فعلی بیشتر است. باقی‌مانده فعلی: {lockedRemainingQuantityMt:N4} MT.");
                }

                if (!ModelState.IsValid)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    return RedirectAfterBulkReceiptError(model);
                }

                var quantityAllocations = AllocateBulkReceiptQuantities(lockedOpenLoadings, requestedQuantityMt);
                if (quantityAllocations.Count == 0 || quantityAllocations.Sum(a => a.QuantityMt) != requestedQuantityMt)
                {
                    ModelState.AddModelError(nameof(model.TotalReceivedQuantityMt), "تقسیم متناسب مقدار رسید انجام نشد. مقدار را با چهار رقم اعشار بررسی کنید.");
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    return RedirectAfterBulkReceiptError(model);
                }

                var createdRows = new List<(LoadingReceipt Receipt, InventoryMovement Movement, LoadingReceiptAllocation Allocation)>();
                foreach (var quantityAllocation in quantityAllocations)
                {
                    var loading = quantityAllocation.OpenLoading.Loading;
                    var receiptReference = BuildBulkReceiptReference(model.ReferenceDocument, loading);
                    var receiptNotes = NormalizeNullable(string.Join(
                        " ",
                        new[]
                        {
                            model.Notes,
                            $"Bulk receipt proportional allocation from selected loadings. Selected total: {requestedQuantityMt:N4} MT."
                        }.Where(note => !string.IsNullOrWhiteSpace(note))));

                    var receipt = new LoadingReceipt
                    {
                        LoadingRegisterId = loading.Id,
                        TerminalId = model.TerminalId,
                        StorageTankId = model.StorageTankId,
                        ReceiptDestination = LoadingReceiptDestination.ToInventory,
                        LossMode = isDeferredLossMode
                            ? ReceiptLossMode.DeferredTankSettlement
                            : ReceiptLossMode.ImmediateKnownLoss,
                        ReceiptDate = model.ReceiptDate,
                        ReceivedQuantityMt = quantityAllocation.QuantityMt,
                        ActualArrivedQuantityMt = quantityAllocation.QuantityMt,
                        ReferenceDocument = receiptReference,
                        Notes = receiptNotes
                    };

                    var movement = new InventoryMovement
                    {
                        ProductId = loading.ProductId,
                        ContractId = loading.ContractId,
                        TerminalId = model.TerminalId,
                        StorageTankId = model.StorageTankId,
                        Direction = MovementDirection.In,
                        MovementDate = model.ReceiptDate,
                        QuantityMt = quantityAllocation.QuantityMt,
                        ReferenceDocument = receiptReference,
                        Notes = receiptNotes,
                        LoadingReceipt = receipt
                    };

                    var allocation = new LoadingReceiptAllocation
                    {
                        LoadingReceipt = receipt,
                        Destination = LoadingReceiptAllocationDestination.ToInventory,
                        Status = LoadingReceiptAllocationStatus.Completed,
                        QuantityMt = quantityAllocation.QuantityMt,
                        SourcePurchaseContractId = loading.ContractId,
                        TerminalId = model.TerminalId,
                        StorageTankId = model.StorageTankId,
                        InventoryMovement = movement,
                        ReferenceDocument = receiptReference,
                        Notes = receiptNotes
                    };

                    EnsureToInventoryAllocationInvariant(receipt, movement, allocation);
                    createdRows.Add((receipt, movement, allocation));
                }

                // The saved operational rows are only read from here on; releasing them keeps
                // each receipt journal's saves from re-scanning every row of a large batch.
                using (var operationalRows = new SavedRowTrackingScope(_db))
                {
                    _formTokens.Stamp(formToken, "LoadingReceipt.BulkCreate", nameof(LoadingReceipt));
                    _db.LoadingReceipts.AddRange(createdRows.Select(r => r.Receipt));
                    _db.LoadingReceiptAllocations.AddRange(createdRows.Select(r => r.Allocation));
                    await _movements.PostInboundRangeAsync(createdRows.Select(r => r.Movement).ToList());
                    operationalRows.ReleaseSaved();
                }

                var receiptAccounting = new Services.Accounting.LoadingReceiptAccountingCoordinator(_purchaseAccounting);
                var accountingOutcomes = new List<Services.Accounting.ReceiptAccountingOutcome>(createdRows.Count);
                foreach (var row in createdRows)
                {
                    using var posting = new SavedRowTrackingScope(_db);
                    accountingOutcomes.AddRange(await receiptAccounting.PostAsync([row.Receipt]));
                    posting.ReleaseSaved();
                }
                foreach (var row in createdRows)
                {
                    await _audit.LogAsync(
                        nameof(LoadingReceipt),
                        row.Receipt.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("LoadingRegisterId", row.Receipt.LoadingRegisterId),
                            ("TerminalId", row.Receipt.TerminalId),
                            ("StorageTankId", row.Receipt.StorageTankId),
                            ("ReceiptDestination", row.Receipt.ReceiptDestination),
                            ("LossMode", row.Receipt.LossMode),
                            ("ReceiptDate", row.Receipt.ReceiptDate),
                            ("ReceivedQuantityMt", row.Receipt.ReceivedQuantityMt),
                            ("ReferenceDocument", row.Receipt.ReferenceDocument),
                            ("InventoryMovementId", row.Movement.Id)));

                    await _audit.LogAsync(
                        nameof(InventoryMovement),
                        row.Movement.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ProductId", row.Movement.ProductId),
                            ("ContractId", row.Movement.ContractId),
                            ("TerminalId", row.Movement.TerminalId),
                            ("StorageTankId", row.Movement.StorageTankId),
                            ("Direction", row.Movement.Direction),
                            ("QuantityMt", row.Movement.QuantityMt),
                            ("MovementDate", row.Movement.MovementDate),
                            ("ReferenceDocument", row.Movement.ReferenceDocument),
                            ("LoadingReceiptId", row.Movement.LoadingReceiptId)));

                    await _audit.LogAsync(
                        nameof(LoadingReceiptAllocation),
                        row.Allocation.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("LoadingReceiptId", row.Receipt.Id),
                            ("Destination", row.Allocation.Destination),
                            ("Status", row.Allocation.Status),
                            ("QuantityMt", row.Allocation.QuantityMt),
                            ("SourcePurchaseContractId", row.Allocation.SourcePurchaseContractId),
                            ("TerminalId", row.Allocation.TerminalId),
                            ("StorageTankId", row.Allocation.StorageTankId),
                            ("InventoryMovementId", row.Movement.Id),
                            ("ReferenceDocument", row.Allocation.ReferenceDocument)));
                }

                var createdLossEventCount = 0;
                if (isImmediateLossMode && requestedLossMt > 0m)
                {
                    var receivedByLoadingId = quantityAllocations
                        .ToDictionary(a => a.OpenLoading.Loading.Id, a => a.QuantityMt);
                    var receiptByLoadingId = createdRows
                        .ToDictionary(r => r.Receipt.LoadingRegisterId, r => r.Receipt);

                    // ضایعات کل را متناسب با ظرفیت باقی‌ماندهٔ هر بارگیری پس از رسید تقسیم می‌کنیم
                    // تا برای هیچ بارگیری مجموع «رسید + کسری» از بارگیری بیشتر نشود.
                    var lossResidualLoadings = lockedOpenLoadings
                        .Select(l => new BulkReceiptOpenLoading(
                            l.Loading,
                            l.AlreadyReceivedQuantityMt,
                            Math.Max(l.RemainingQuantityMt - receivedByLoadingId.GetValueOrDefault(l.Loading.Id), 0m)))
                        .Where(l => l.RemainingQuantityMt > 0m)
                        .ToList();

                    var lossAllocations = AllocateBulkReceiptQuantities(lossResidualLoadings, requestedLossMt);
                    if (lossAllocations.Count == 0 || lossAllocations.Sum(a => a.QuantityMt) != requestedLossMt)
                    {
                        ModelState.AddModelError(nameof(model.TotalLossQuantityMt), "تقسیم متناسب ضایعات انجام نشد. مقدار را با چهار رقم اعشار بررسی کنید.");
                        if (transaction is not null)
                        {
                            await transaction.RollbackAsync();
                        }

                        return RedirectAfterBulkReceiptError(model);
                    }

                    var toleranceByLoadingId = new Dictionary<int, decimal>();
                    if (requestedLossToleranceMt > 0m)
                    {
                        var toleranceWeights = lossAllocations
                            .Select(a => new BulkReceiptOpenLoading(a.OpenLoading.Loading, 0m, a.QuantityMt))
                            .ToList();
                        toleranceByLoadingId = AllocateBulkReceiptQuantities(toleranceWeights, requestedLossToleranceMt)
                            .ToDictionary(a => a.OpenLoading.Loading.Id, a => a.QuantityMt);
                    }

                    // ساخت تمام درخواست‌ها و سپس یک فراخوانی گروهی، تا تعداد SaveChanges
                    // مستقل از تعداد بارگیری‌ها ثابت بماند.
                    var lossSubmissions = new List<LossEventSubmission>(lossAllocations.Count);
                    foreach (var lossAllocation in lossAllocations)
                    {
                        var loading = lossAllocation.OpenLoading.Loading;
                        var receivedForLoading = receivedByLoadingId.GetValueOrDefault(loading.Id);
                        receiptByLoadingId.TryGetValue(loading.Id, out var receiptForLoading);
                        var lossReference = BuildBulkReceiptReference(model.ReferenceDocument, loading);
                        var lossNotes = NormalizeNullable(string.Join(
                            " ",
                            new[]
                            {
                                model.Notes,
                                $"Bulk receipt shortage allocation. Selected total loss: {requestedLossMt:N4} MT."
                            }.Where(note => !string.IsNullOrWhiteSpace(note))));

                        lossSubmissions.Add(new LossEventSubmission
                        {
                            Stage = LossEventStage.ReceiptShortage,
                            ProductId = loading.ProductId,
                            ContractId = loading.ContractId,
                            LoadingRegisterId = loading.Id,
                            LoadingReceiptId = receiptForLoading?.Id,
                            TerminalId = model.TerminalId,
                            StorageTankId = model.StorageTankId,
                            EventDate = model.ReceiptDate,
                            ExpectedQuantityMt = receivedForLoading + lossAllocation.QuantityMt,
                            ActualQuantityMt = receivedForLoading,
                            ToleranceQuantityMt = toleranceByLoadingId.GetValueOrDefault(loading.Id),
                            ResponsiblePartyName = model.LossResponsiblePartyName,
                            AffectsInventory = false,
                            Reference = lossReference,
                            Notes = lossNotes
                        });
                    }

                    var lossResults = await _lossWorkflow.CreateBatchAsync(lossSubmissions);
                    createdLossEventCount = lossResults.Count;
                }

                await _db.SaveChangesAsync();

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                RecordReceiptAccountingOutcomes(accountingOutcomes);

                TempData["ok"] = createdLossEventCount > 0
                    ? $"رسید جمعی با موفقیت ثبت شد. {createdRows.Count:N0} رسید با مجموع {requestedQuantityMt:N4} MT و {createdLossEventCount:N0} رکورد کسری با مجموع {requestedLossMt:N4} MT ثبت شد."
                    : $"رسید جمعی با موفقیت ثبت شد. {createdRows.Count:N0} رسید جداگانه با مجموع {requestedQuantityMt:N4} MT ساخته شد.";
                return RedirectAfterBulkReceipt(model, accountingOutcomes);
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }

                throw;
            }
        }
        catch (DbUpdateException ex) when (_formTokens.IsDuplicate(ex))
        {
            return BulkReceiptAlreadyProcessed(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create bulk loading receipts for contract {ContractId}.", model.ContractId);
            ModelState.AddModelError(string.Empty, "ثبت رسید جمعی انجام نشد. داده‌ها را بررسی کنید و دوباره تلاش کنید.");
            return RedirectAfterBulkReceiptError(model);
        }
    }

    private Task<bool> IsReceiptRequestProcessedAsync(string? token, string purpose)
        => string.IsNullOrWhiteSpace(token) ? Task.FromResult(false)
            : _db.ProcessedFormTokens.AsNoTracking().AnyAsync(x => x.Token == token.Trim() && x.Purpose == purpose);

    private IActionResult BulkReceiptAlreadyProcessed(LoadingReceiptBulkCreateViewModel model)
    {
        TempData["ok"] = "این درخواست قبلاً ثبت شده است؛ رسید تازه ساخته نشد.";
        return RedirectAfterBulkReceipt(model, duplicate: true);
    }

    private void RecordReceiptAccountingOutcomes(IReadOnlyList<Services.Accounting.ReceiptAccountingOutcome> outcomes)
    {
        var skipped = outcomes.Where(x => x.Status == Services.Accounting.PaymentPostingStatus.Skipped.ToString()).ToList();
        foreach (var outcome in skipped)
            _logger.LogWarning("Receipt {ReceiptId} accounting skipped: {Reason}; event {SourceEventId}",
                outcome.ReceiptId, outcome.Reason, outcome.SourceEventId);
        if (skipped.Count == 0) return;
        var reasons = skipped.Select(x => x.Reason switch
        {
            "ACCOUNTING_DISABLED" or "PILOT_DISABLED" or "ACCOUNTING_ADAPTER_UNAVAILABLE" => "حسابداری این عملیات فعال نیست",
            "PURCHASE_NOT_POSTED" => "سند حسابداری خرید هنوز ثبت نشده است",
            "PURCHASE_PRICE_PENDING" or "PRICE_NOT_FINAL" or "PRICE_PENDING" or "MISSING_PRICE" => "قیمت خرید هنوز قطعی نشده است",
            _ => "قیمت، تنظیمات حسابداری یا شرایط سند نیاز به بررسی دارد"
        }).Distinct();
        TempData["warn"] = $"رسید عملیاتی ثبت شد، اما حسابداری {skipped.Count:N0} رسید ثبت نشد: {string.Join("؛ ", reasons)}.";
    }

    private IActionResult RedirectAfterBulkReceiptError(LoadingReceiptBulkCreateViewModel model)
    {
        var message = string.Join(" ", ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .DefaultIfEmpty("ثبت رسید جمعی انجام نشد."));

        if (IsAjaxRequest())
        {
            return BadRequest(new { success = false, message });
        }

        TempData["err"] = message;

        return RedirectAfterBulkReceipt(model);
    }

    private IActionResult RedirectAfterBulkReceipt(
        LoadingReceiptBulkCreateViewModel model,
        IReadOnlyList<Services.Accounting.ReceiptAccountingOutcome>? accounting = null,
        bool duplicate = false)
    {
        var redirectUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl)
            ? localReturnUrl
            : Url.Action(
                "Details",
                "ContractJourney",
                new { contractId = model.ContractId, tab = ContractJourneyTabs.Details.Receipts })
                ?? $"/ContractJourney/Details?contractId={model.ContractId}&tab={ContractJourneyTabs.Details.Receipts}";

        if (IsAjaxRequest())
        {
            return Ok(new
            {
                success = true,
                redirectUrl,
                duplicate,
                accounting,
                accountingWarning = TempData.Peek("warn")?.ToString(),
                message = string.Join(" ", new[] { TempData["ok"]?.ToString() ?? "رسید با موفقیت ثبت شد.", TempData.Peek("warn")?.ToString() }.Where(x => !string.IsNullOrWhiteSpace(x)))
            });
        }

        return Redirect(redirectUrl);
    }

    private bool IsAjaxRequest()
        => ControllerContext.HttpContext is not null
            && string.Equals(Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var receipt = await _db.LoadingReceipts
            .Include(r => r.LoadingRegister)
                .ThenInclude(l => l!.Contract)
            .Include(r => r.LoadingRegister)
                .ThenInclude(l => l!.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (receipt is null || receipt.LoadingRegister is null)
        {
            return NotFound();
        }

        // رسید لغوشده ویرایش نمی‌شود؛ برای اصلاح باید رسید جدید ثبت شود.
        if (receipt.IsCancelled)
        {
            TempData["err"] = "این رسید لغو شده است و ویرایش نمی‌شود.";
            return RedirectToAction(nameof(Details), new { id = receipt.Id, returnUrl });
        }

        var model = new LoadingReceiptEditViewModel
        {
            Id = receipt.Id,
            LoadingRegisterId = receipt.LoadingRegisterId,
            ArrivalDate = receipt.ArrivalDate,
            LeakDate = receipt.LeakDate,
            ActualArrivedQuantityMt = receipt.ActualArrivedQuantityMt,
            ReferenceDocument = receipt.ReferenceDocument,
            Notes = receipt.Notes,
            ReceiptDate = receipt.ReceiptDate,
            ReceivedQuantityMt = receipt.ReceivedQuantityMt,
            ContractNumber = receipt.LoadingRegister.Contract?.DisplayLabel ?? "",
            ProductName = receipt.LoadingRegister.Product?.Name ?? "",
            WagonNumber = receipt.LoadingRegister.WagonNumber,
            BillOfLadingNumber = receipt.LoadingRegister.BillOfLadingNumber,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null
        };

        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, LoadingReceiptEditViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var receipt = await _db.LoadingReceipts
            .Include(r => r.LoadingRegister)
                .ThenInclude(l => l!.Contract)
            .Include(r => r.LoadingRegister)
                .ThenInclude(l => l!.Product)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (receipt is null || receipt.LoadingRegister is null)
        {
            return NotFound();
        }

        // رسید لغوشده ویرایش نمی‌شود.
        if (receipt.IsCancelled)
        {
            TempData["err"] = "این رسید لغو شده است و ویرایش نمی‌شود.";
            return RedirectToAction(nameof(Details), new { id = receipt.Id });
        }

        // restore read-only context for re-render on error
        model.ReceiptDate = receipt.ReceiptDate;
        model.ReceivedQuantityMt = receipt.ReceivedQuantityMt;
        // مقدار واقعی رسیده اثر عملیاتی دارد (کسری/مازاد) و با Update ساده تغییر نمی‌کند؛
        // اصلاح آن فقط از مسیر «لغو + ثبت دوباره» انجام می‌شود.
        model.ActualArrivedQuantityMt = receipt.ActualArrivedQuantityMt;
        model.ContractNumber = receipt.LoadingRegister.Contract?.DisplayLabel ?? "";
        model.ProductName = receipt.LoadingRegister.Product?.Name ?? "";
        model.WagonNumber = receipt.LoadingRegister.WagonNumber;
        model.BillOfLadingNumber = receipt.LoadingRegister.BillOfLadingNumber;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var oldArrivalDate = receipt.ArrivalDate;
        var oldLeakDate = receipt.LeakDate;
        var oldRef = receipt.ReferenceDocument;
        var oldNotes = receipt.Notes;

        receipt.ArrivalDate = model.ArrivalDate;
        receipt.LeakDate = model.LeakDate;
        receipt.ReferenceDocument = string.IsNullOrWhiteSpace(model.ReferenceDocument) ? null : model.ReferenceDocument.Trim();
        receipt.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(
            nameof(LoadingReceipt),
            receipt.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("ArrivalDate", oldArrivalDate, receipt.ArrivalDate),
                ("LeakDate", oldLeakDate, receipt.LeakDate),
                ("ReferenceDocument", oldRef, receipt.ReferenceDocument),
                ("Notes", oldNotes, receipt.Notes)));

        TempData["ok"] = "اطلاعات رسید با موفقیت به‌روزرسانی شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = receipt.Id });
    }

    private static string BuildLossFieldKey(string fieldName)
        => $"Loss.{fieldName}";

    // رسید قبل از بارگیری ممکن نیست؛ چنین رسیدی بهای فروش‌های بعدی را از بهای قرارداد محروم می‌کند
    // (بهای فروش فقط از بارگیری‌های تا تاریخ همان فروش ساخته می‌شود).
    private static string BuildReceiptBeforeLoadingMessage(DateTime receiptDate, IEnumerable<LoadingRegister> loadings)
        => $"تاریخ رسید ({receiptDate.ToCalendarString("yyyy-MM-dd")}) نمی‌تواند قبل از تاریخ بارگیری باشد: "
            + string.Join("، ", loadings.Select(l => $"بارگیری #{l.Id} ({l.LoadingDate.ToCalendarString("yyyy-MM-dd")})"))
            + ". اگر تاریخ بارگیری اشتباه است، اول آن را اصلاح کنید.";

    private static LossEventSubmission BuildReceiptLossSubmission(
        LoadingReceiptCreateViewModel model,
        LoadingRegister loading,
        string? normalizedReference)
        => StageLossCaptureMapper.ToSubmission(
            model.Loss,
            new StageLossCaptureContext
            {
                Stage = LossEventStage.ReceiptShortage,
                ActualQuantityMt = model.ReceivedQuantityMt,
                EventDate = model.ReceiptDate,
                ProductId = loading.ProductId,
                ContractId = loading.ContractId,
                LoadingRegisterId = loading.Id,
                TerminalId = model.TerminalId,
                StorageTankId = model.ReceiptDestination == LoadingReceiptDestination.DirectDispatch ? null : model.StorageTankId,
                DefaultReference = normalizedReference ?? loading.BillOfLadingNumber
            });

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var receipt = await _db.LoadingReceipts
            .Include(r => r.LoadingRegister)
                .ThenInclude(l => l!.Contract)
            .Include(r => r.LoadingRegister)
                .ThenInclude(l => l!.Product)
            .Include(r => r.Terminal)
            .Include(r => r.StorageTank)
            .Include(r => r.InventoryMovement)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.SourcePurchaseContract)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.Terminal)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.StorageTank)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.DestinationTerminal)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.DestinationStorageTank)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.DestinationLocation)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.InventoryMovement)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.TruckDispatch)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.DirectTruckDispatches)
            .Include(r => r.Allocations)
                .ThenInclude(a => a.SalesTransaction)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (receipt is null || receipt.LoadingRegister is null)
        {
            return NotFound();
        }

        var totalReceivedQuantityMt = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => r.LoadingRegisterId == receipt.LoadingRegisterId && !r.IsCancelled)
            .SumAsync(r => (decimal?)r.ReceivedQuantityMt) ?? 0m;
        var receiptShortageLossMt = await GetCommittedReceiptShortageQuantityMtAsync(receipt.LoadingRegisterId);

        var lossItems = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.LoadingReceiptId == receipt.Id && !l.IsCancelled)
            .OrderBy(l => l.EventDate)
            .Select(l => new LossEventSummaryItem
            {
                Id = l.Id,
                Stage = l.Stage,
                EventDate = l.EventDate,
                DifferenceQuantityMt = l.DifferenceQuantityMt,
                ToleranceQuantityMt = l.ToleranceQuantityMt,
                AllowableLossMt = l.AllowableLossMt,
                ChargeableLossMt = l.ChargeableLossMt,
                ResponsiblePartyName = l.ResponsiblePartyName,
                Reference = l.Reference,
                Notes = l.Notes
            })
            .ToListAsync();

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        var cancelledByUserName = receipt.CancelledByUserId is null
            ? null
            : await _db.Users.AsNoTracking()
                .Where(u => u.Id == receipt.CancelledByUserId.Value)
                .Select(u => u.Username)
                .FirstOrDefaultAsync();

        return View(new LoadingReceiptDetailsViewModel
        {
            Id = receipt.Id,
            LoadingRegisterId = receipt.LoadingRegisterId,
            IsCancelled = receipt.IsCancelled,
            CancelledAtUtc = receipt.CancelledAtUtc,
            CancellationReason = receipt.CancellationReason,
            CancelledByUserName = cancelledByUserName,
            RowVersion = receipt.RowVersion,
            ReceiptDestination = receipt.ReceiptDestination,
            ReceiptDate = receipt.ReceiptDate,
            ArrivalDate = receipt.ArrivalDate,
            LeakDate = receipt.LeakDate,
            ActualArrivedQuantityMt = receipt.ActualArrivedQuantityMt,
            ContractId = receipt.LoadingRegister.ContractId,
            ContractNumber = receipt.LoadingRegister.Contract?.DisplayLabel ?? "",
            ProductName = receipt.LoadingRegister.Product?.Name ?? "",
            LoadingDate = receipt.LoadingRegister.LoadingDate,
            LoadedQuantityMt = receipt.LoadingRegister.LoadedQuantityMt,
            TotalReceivedQuantityMt = totalReceivedQuantityMt,
            RemainingToReceiveMt = Math.Max(receipt.LoadingRegister.LoadedQuantityMt - totalReceivedQuantityMt - receiptShortageLossMt, 0m),
            TerminalName = receipt.Terminal?.Name ?? "",
            StorageTankCode = StorageTankDisplay.BuildOptional(receipt.StorageTank),
            ReceivedQuantityMt = receipt.ReceivedQuantityMt,
            BillOfLadingNumber = receipt.LoadingRegister.BillOfLadingNumber,
            WagonNumber = receipt.LoadingRegister.WagonNumber,
            ConsigneeName = receipt.LoadingRegister.ConsigneeName,
            DestinationName = receipt.LoadingRegister.DestinationName,
            ReferenceDocument = receipt.ReferenceDocument ?? receipt.InventoryMovement?.ReferenceDocument,
            Notes = receipt.Notes,
            InventoryMovementId = receipt.InventoryMovement?.Id ?? 0,
            Allocations = receipt.Allocations
                .OrderBy(a => a.Id)
                .Select(a => new LoadingReceiptAllocationSummaryViewModel
                {
                    Id = a.Id,
                    Destination = a.Destination,
                    Status = a.Status,
                    QuantityMt = a.QuantityMt,
                    SourcePurchaseContractNumber = a.SourcePurchaseContract?.DisplayLabel,
                    TerminalName = a.Terminal?.Name ?? "",
                    StorageTankCode = StorageTankDisplay.BuildOptional(a.StorageTank),
                    DestinationTerminalName = a.DestinationTerminal?.Name,
                    DestinationStorageTankCode = StorageTankDisplay.BuildOptional(a.DestinationStorageTank),
                    DestinationLocationName = a.DestinationLocation?.Name,
                    DestinationName = a.DestinationName,
                    DestinationReference = a.DestinationReference,
                    InventoryMovementId = a.InventoryMovementId,
                    TruckDispatchId = a.TruckDispatchId,
                    DirectTruckDispatchCount = a.DirectTruckDispatches
                        .Count(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.Status != DispatchStatus.Cancelled),
                    DirectTruckDispatchedQuantityMt = a.DirectTruckDispatches
                        .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.Status != DispatchStatus.Cancelled)
                        .Sum(d => d.LoadedQuantityMt),
                    SalesTransactionId = a.SalesTransactionId,
                    ReferenceDocument = a.ReferenceDocument,
                    Notes = a.Notes
                })
                .ToList(),
            LossItems = lossItems
        });
    }
}
