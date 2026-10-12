using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ShipmentPnlController : Controller
{
    private static readonly SalesPnlSnapshot EmptySalesPnl =
        new(0m, 0m, 0, 0, 0, PnlConfidence.Verified);
    private const int IndexPageSize = 5;
    private readonly ApplicationDbContext _db;
    private readonly InventoryLineagePnlService _lineagePnl;
    private readonly IProfitAndLossService _profitAndLoss;
    private readonly LineageOptions _lineageOptions;

    // کش کارت‌های آمار لازم نیست: این اعداد اکنون از query سبک می‌آیند و باید همیشه
    // با همان چیزی که لیست نشان می‌دهد یکی باشند.
    public ShipmentPnlController(
        ApplicationDbContext db,
        InventoryLineagePnlService? lineagePnl = null,
        IOptions<LineageOptions>? lineageOptions = null,
        IProfitAndLossService? profitAndLoss = null)
    {
        _db = db;
        _lineagePnl = lineagePnl ?? new InventoryLineagePnlService(db);
        _profitAndLoss = profitAndLoss ?? new ProfitAndLossService(db);
        _lineageOptions = lineageOptions?.Value ?? new LineageOptions();
    }

    public async Task<IActionResult> Index(int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var shipmentQuery = _db.Shipments.AsNoTracking();
        var totalCount = await shipmentQuery.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var shipmentIds = await shipmentQuery
            .OrderByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => s.Id)
            .ToListAsync();

        var items = await BuildIndexRowsAsync(shipmentIds);
        var totals = await BuildIndexTotalsAsync(totalCount);

        return View(new ShipmentPnlIndexViewModel
        {
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            Totals = totals
        });
    }

    /// <summary>
    /// ردیف‌های لیست محموله‌ها. لیست فقط شناسه، مسیر، مقدار و تعداد رکوردهای مرتبط را نشان
    /// می‌دهد، پس رول‌آپ کامل مالی (که برای پروندهٔ هر محموله و خروجی لازم است) اینجا ساخته
    /// نمی‌شود؛ همان مجموعه‌ها با query سبک شمرده می‌شوند. درآمد و سود محقق‌شده مثل بقیهٔ
    /// صفحات فقط از ProfitAndLossService می‌آید.
    /// شمارش‌ها باید همیشه با BuildFinancialRollupsAsync یکی بمانند؛ تست تطبیقی
    /// ShipmentPnlIndexRowTests این را نگه می‌دارد.
    /// </summary>
    private async Task<IReadOnlyList<ShipmentPnlIndexRowViewModel>> BuildIndexRowsAsync(IReadOnlyList<int> shipmentIds)
    {
        if (shipmentIds.Count == 0)
        {
            return [];
        }

        var shipments = await _db.Shipments
            .AsNoTracking()
            .Where(s => shipmentIds.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                s.ShipmentCode,
                VesselName = s.Vessel != null ? s.Vessel.Name : null,
                s.DepartureDate,
                s.ArrivalDate,
                s.QuantityMt,
                ContractQuantityMt = s.ShipmentContracts.Sum(sc => sc.QuantityMt ?? 0m),
                ContractProductName = s.Contract != null && s.Contract.Product != null ? s.Contract.Product.Name : null,
                OriginName = s.OriginLocation != null ? s.OriginLocation.Name : null,
                DestinationName = s.DestinationLocation != null ? s.DestinationLocation.Name : null
            })
            .ToListAsync();

        // همان مجموعهٔ legهایی که رول‌آپ استفاده می‌کند: وصل به محموله و غیرلغو.
        var legRows = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId.HasValue
                && shipmentIds.Contains(l.ShipmentId.Value)
                && l.Status != InventoryTransportLegStatus.Cancelled)
            .Select(l => new
            {
                l.Id,
                ShipmentId = l.ShipmentId!.Value,
                ProductName = l.SourcePurchaseContract != null && l.SourcePurchaseContract.Product != null
                    ? l.SourcePurchaseContract.Product.Name
                    : (l.Product != null ? l.Product.Name : null)
            })
            .ToListAsync();

        var legToShipment = legRows.ToDictionary(l => l.Id, l => l.ShipmentId);
        var legIds = legToShipment.Keys.ToList();

        var transportLegCount = legRows
            .GroupBy(l => l.ShipmentId)
            .ToDictionary(g => g.Key, g => g.Count());

        var productNames = legRows
            .GroupBy(l => l.ShipmentId)
            .ToDictionary(g => g.Key, g => ResolveSingleOrMixed(g.Select(l => l.ProductName), "Mixed products"));

        // فروش‌های وصل به محموله: مستقیم (ShipmentId) یا از راه رسیدهای حمل همان محموله.
        var saleToShipmentFromLeg = await BuildSaleShipmentMapFromTransportReceiptsAsync(legIds, legToShipment);
        var saleIdsFromLeg = saleToShipmentFromLeg.Keys.ToList();

        var saleRows = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && s.SaleStage != SaleStage.PreSale
                && ((s.ShipmentId.HasValue && shipmentIds.Contains(s.ShipmentId.Value))
                    || saleIdsFromLeg.Contains(s.Id)))
            .Select(s => new { s.Id, s.ShipmentId })
            .ToListAsync();

        var shipmentIdSet = shipmentIds.ToHashSet();
        var saleToShipment = new Dictionary<int, int>();
        foreach (var sale in saleRows)
        {
            if (sale.ShipmentId.HasValue && shipmentIdSet.Contains(sale.ShipmentId.Value))
            {
                saleToShipment[sale.Id] = sale.ShipmentId.Value;
            }
            else if (saleToShipmentFromLeg.TryGetValue(sale.Id, out var lineageShipmentId))
            {
                saleToShipment[sale.Id] = lineageShipmentId;
            }
        }

        var salesCount = saleToShipment
            .GroupBy(pair => pair.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var realisedByShipment = await _profitAndLoss.BuildForSaleGroupsAsync(saleToShipment);

        var expenseCount = await BuildIndexExpenseCountsAsync(shipmentIds, legIds, legToShipment);

        // ترتیب لیست همان ترتیب صفحه‌بندی است، نه ترتیب برگشتی دیتابیس.
        var order = shipmentIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        return shipments
            .OrderBy(s => order[s.Id])
            .Select(shipment =>
            {
                var realised = realisedByShipment.TryGetValue(shipment.Id, out var value) ? value : EmptySalesPnl;
                return new ShipmentPnlIndexRowViewModel
                {
                    Id = shipment.Id,
                    ShipmentCode = shipment.ShipmentCode,
                    VesselName = shipment.VesselName,
                    DepartureDate = shipment.DepartureDate,
                    ArrivalDate = shipment.ArrivalDate,
                    QuantityMt = RoundQuantity(shipment.QuantityMt > 0m
                        ? shipment.QuantityMt
                        : shipment.ContractQuantityMt),
                    ProductName = productNames.GetValueOrDefault(shipment.Id) ?? shipment.ContractProductName,
                    OriginName = shipment.OriginName,
                    DestinationName = shipment.DestinationName,
                    RelatedTransportLegCount = transportLegCount.GetValueOrDefault(shipment.Id),
                    RelatedSalesCount = salesCount.GetValueOrDefault(shipment.Id),
                    RelatedExpensesCount = expenseCount.GetValueOrDefault(shipment.Id),
                    TotalSalesUsd = realised.RevenueUsd,
                    RealisedPnl = realised.ToViewModel()
                };
            })
            .ToList();
    }

    /// <summary>
    /// تعداد رکوردهای مصرف هر محموله، با همان چهار منبعی که رول‌آپ می‌شمارد:
    /// کرایهٔ رسید حمل، مصرف‌های عملیاتی، گمرک روی حمل و گمرک ثبت‌شده از مسیر موتر.
    /// فقط شمارش است؛ هیچ مبلغی اینجا محاسبه یا نمایش داده نمی‌شود.
    /// </summary>
    private async Task<Dictionary<int, int>> BuildIndexExpenseCountsAsync(
        IReadOnlyList<int> shipmentIds,
        IReadOnlyList<int> legIds,
        IReadOnlyDictionary<int, int> legToShipment)
    {
        var counts = new Dictionary<int, int>();

        void Add(int shipmentId, int value = 1)
            => counts[shipmentId] = counts.GetValueOrDefault(shipmentId) + value;

        if (legIds.Count > 0)
        {
            // کرایهٔ رسید حمل: به‌ازای هر legی که مبلغ کرایهٔ خالص مثبت دارد یک ردیف مصرف ساخته می‌شود.
            var freightByLeg = await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => !r.IsCancelled && legIds.Contains(r.InventoryTransportLegId))
                .GroupBy(r => r.InventoryTransportLegId)
                .Select(g => new
                {
                    LegId = g.Key,
                    AmountUsd = g.Sum(r => (r.FreightCostUsd ?? 0m) - (r.ShortageChargeUsd ?? 0m))
                })
                .ToListAsync();

            foreach (var leg in freightByLeg.Where(l => RoundMoney(l.AmountUsd) > 0m))
            {
                if (legToShipment.TryGetValue(leg.LegId, out var shipmentId))
                {
                    Add(shipmentId);
                }
            }
        }

        // مصرفِ گمرک پایین‌تر از خودِ اظهارنامه شمرده می‌شود؛ اینجا کنار می‌رود تا شمارنده
        // با ردیف‌های صفحهٔ جزئیات یکی بماند و یک اظهارنامه دو بار شمرده نشود.
        var expenseRows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && (e.ExpenseType == null || e.ExpenseType.Code != InventoryTransportReceiptService.ReceiptFreightExpenseCode)
                && ((e.ShipmentId.HasValue && shipmentIds.Contains(e.ShipmentId.Value))
                    || (e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value))
                    || (!e.TransportLegId.HasValue
                        && e.TruckDispatchId.HasValue
                        && e.TruckDispatch!.InventoryTransportReceipt != null
                        && legIds.Contains(e.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))))
            .Select(e => new
            {
                e.ShipmentId,
                e.TransportLegId,
                DispatchLegId = e.TruckDispatch != null && e.TruckDispatch.InventoryTransportReceipt != null
                    ? (int?)e.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId
                    : null
            })
            .ToListAsync();

        var shipmentIdSet = shipmentIds.ToHashSet();
        foreach (var expense in expenseRows)
        {
            if (expense.ShipmentId.HasValue && shipmentIdSet.Contains(expense.ShipmentId.Value))
            {
                Add(expense.ShipmentId.Value);
            }
            else if (expense.TransportLegId.HasValue
                && legToShipment.TryGetValue(expense.TransportLegId.Value, out var legShipmentId))
            {
                Add(legShipmentId);
            }
            else if (expense.DispatchLegId.HasValue
                && legToShipment.TryGetValue(expense.DispatchLegId.Value, out var dispatchShipmentId))
            {
                Add(dispatchShipmentId);
            }
        }

        if (legIds.Count == 0)
        {
            return counts;
        }

        var customsLegIds = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => c.TransportLegId.HasValue && legIds.Contains(c.TransportLegId.Value))
            .Select(c => c.TransportLegId!.Value)
            .ToListAsync();

        foreach (var legId in customsLegIds)
        {
            if (legToShipment.TryGetValue(legId, out var shipmentId))
            {
                Add(shipmentId);
            }
        }

        var dispatchCustomsLegIds = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => !c.TransportLegId.HasValue
                && c.TruckDispatchId.HasValue
                && c.TruckDispatch!.InventoryTransportReceipt != null
                && legIds.Contains(c.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))
            .Select(c => c.TruckDispatch!.InventoryTransportReceipt!.InventoryTransportLegId)
            .ToListAsync();

        foreach (var legId in dispatchCustomsLegIds)
        {
            if (legToShipment.TryGetValue(legId, out var shipmentId))
            {
                Add(shipmentId);
            }
        }

        return counts;
    }

    /// <summary>
    /// کارت‌های آمار بالای لیست محموله‌ها. این اعداد روی همهٔ محموله‌ها هستند، پس فقط با
    /// query سبک و جمع‌بندی سمت دیتابیس ساخته می‌شوند؛ هیچ رول‌آپ کامل P&L برای همهٔ
    /// محموله‌ها اینجا اجرا نمی‌شود. سود همچنان فقط از ProfitAndLossService می‌آید.
    /// </summary>
    private async Task<ShipmentPnlIndexTotalsViewModel> BuildIndexTotalsAsync(int totalCount)
    {
        // مقدار هر محموله با همان قاعدهٔ ResolveOriginalShipmentQuantity:
        // مقدار خود محموله، و اگر صفر بود جمع مقدار قراردادهای تخصیص‌یافتهٔ همان محموله.
        var quantityRows = await _db.Shipments
            .AsNoTracking()
            .Select(s => new
            {
                s.QuantityMt,
                ContractQuantityMt = s.ShipmentContracts.Sum(sc => sc.QuantityMt ?? 0m)
            })
            .ToListAsync();

        var sumQuantity = quantityRows.Sum(row => RoundQuantity(
            row.QuantityMt > 0m ? row.QuantityMt : row.ContractQuantityMt));

        // نگاشت فروش → محموله: همان دو مسیر لیست (ShipmentId مستقیم، و رسیدهای حملِ
        // legهای همان محموله)، فقط بدون ساختن رول‌آپ مالی.
        var legToShipment = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId.HasValue && l.Status != InventoryTransportLegStatus.Cancelled)
            .Select(l => new { l.Id, ShipmentId = l.ShipmentId!.Value })
            .ToDictionaryAsync(l => l.Id, l => l.ShipmentId);

        var saleToShipmentFromLeg = await BuildSaleShipmentMapFromTransportReceiptsAsync(
            legToShipment.Keys.ToList(),
            legToShipment);
        var saleIdsFromLeg = saleToShipmentFromLeg.Keys.ToList();

        var relatedSaleIds = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && s.SaleStage != SaleStage.PreSale
                && (s.ShipmentId.HasValue || saleIdsFromLeg.Contains(s.Id)))
            .Select(s => s.Id)
            .ToListAsync();

        // درآمد و سود محقق‌شده تنها از منبع مجاز خوانده می‌شود.
        var realised = await _profitAndLoss.BuildForSalesAsync(relatedSaleIds);

        return new ShipmentPnlIndexTotalsViewModel
        {
            TotalCount = totalCount,
            SumQuantityMt = sumQuantity,
            SumRelatedSalesCount = relatedSaleIds.Count,
            RealisedGrossProfitUsd = realised.GrossProfitUsd
        };
    }

    internal async Task<IReadOnlyList<ShipmentPnlListItemViewModel>> BuildAllIndexItemsAsync()
    {
        var shipmentIds = await _db.Shipments
            .AsNoTracking()
            .OrderByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync();

        return await BuildIndexItemsAsync(shipmentIds);
    }

    private async Task<IReadOnlyList<ShipmentPnlListItemViewModel>> BuildIndexItemsAsync(IReadOnlyList<int> shipmentIds)
    {
        if (shipmentIds.Count == 0)
        {
            return [];
        }

        var shipments = await _db.Shipments
            .Include(s => s.Vessel)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Product)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Unit)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Customer)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(s => s.ShipmentContracts)
                .ThenInclude(sc => sc.Contract)
                    .ThenInclude(c => c!.Product)
            .Include(s => s.ShipmentContracts)
                .ThenInclude(sc => sc.Contract)
                    .ThenInclude(c => c!.Unit)
            .Include(s => s.ShipmentContracts)
                .ThenInclude(sc => sc.Contract)
                    .ThenInclude(c => c!.Supplier)
            .Include(s => s.OriginLocation)
            .Include(s => s.DestinationLocation)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(s => shipmentIds.Contains(s.Id))
            .ToListAsync();

        var order = shipmentIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        shipments = shipments
            .OrderBy(s => order[s.Id])
            .ToList();

        var rollups = await BuildFinancialRollupsAsync(shipmentIds);

        return shipments.Select(shipment =>
        {
            var rollup = rollups[shipment.Id];
            var contractCount = rollup.TransportLegs
                .Select(l => l.ContractNumber)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var totalExpensesUsd = rollup.TotalPurchaseCostUsd + rollup.TotalOperationalExpensesUsd;

            return new ShipmentPnlListItemViewModel
            {
                Id = shipment.Id,
                ShipmentCode = shipment.ShipmentCode,
                VesselName = shipment.Vessel?.Name,
                DepartureDate = shipment.DepartureDate,
                ArrivalDate = shipment.ArrivalDate,
                QuantityMt = ResolveOriginalShipmentQuantity(shipment),
                ContractNumber = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.ContractNumber),
                    contractCount > 0 ? $"{contractCount} contracts" : "Mixed contracts")
                    ?? shipment.Contract?.ContractNumber,
                ContractUnitText = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.ContractUnitText),
                    "Mixed units")
                    ?? (shipment.Contract != null ? ContractUiText.ResolveUnitText(shipment.Contract.Unit) : "-"),
                ProductName = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.ProductName),
                    "Mixed products")
                    ?? shipment.Contract?.Product?.Name,
                CustomerName = shipment.Contract?.Customer?.Name,
                SupplierName = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.SupplierName),
                    "Mixed suppliers")
                    ?? shipment.Contract?.Supplier?.Name,
                OriginName = shipment.OriginLocation?.Name,
                DestinationName = shipment.DestinationLocation?.Name,
                TotalSalesUsd = rollup.TotalSalesUsd,
                TotalPurchaseCostUsd = rollup.TotalPurchaseCostUsd,
                TotalOperationalExpensesUsd = rollup.TotalOperationalExpensesUsd,
                TotalExpensesUsd = totalExpensesUsd,
                GrossMarginUsd = PnlMath.GrossProfit(rollup.TotalSalesUsd, totalExpensesUsd),
                RelatedTransportLegCount = rollup.TransportLegs.Count,
                RelatedSalesCount = rollup.Sales.Count,
                RelatedExpensesCount = rollup.Expenses.Count,
                RelatedLedgerCount = rollup.LedgerEntries.Count,
                RealisedPnl = rollup.RealisedSales.ToViewModel()
            };
        }).ToList();
    }

    public async Task<IActionResult> Details(int id)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Vessel)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Company)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Product)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Unit)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Customer)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(s => s.OriginLocation)
            .Include(s => s.DestinationLocation)
            .Include(s => s.ShipmentContracts)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (shipment is null)
        {
            return NotFound();
        }

        var rollups = await BuildFinancialRollupsAsync([id]);
        var rollup = rollups[id];

        var dispatchTraces = await _db.DeliveryReceipts
            .Include(r => r.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .AsNoTracking()
            .Where(r => r.ShipmentId == id)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new ShipmentPnlDispatchTraceItemViewModel
            {
                DeliveryReceiptId = r.Id,
                ReceiptDate = r.ReceiptDate,
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                DocumentReference = r.DocumentReference,
                TruckDispatchId = r.TruckDispatchId,
                DispatchDate = r.TruckDispatch != null ? r.TruckDispatch.DispatchDate : null,
                TruckPlateNumber = r.TruckDispatch != null ? r.TruckDispatch.Truck!.PlateNumber : null,
                LoadedQuantityMt = r.TruckDispatch != null ? r.TruckDispatch.LoadedQuantityMt : null
            })
            .ToListAsync();

        // جدول تب تخلیه و مرحلهٔ VesselUnloaded باید یک مجموعهٔ یکسان را ببینند؛
        // پیش‌تر دو شرط جدا داشتند و تخلیه‌های ثبت‌شده بیرون از فورم گروهی فقط در
        // یکی از دو طرف شمرده می‌شدند. حالا هر دو از VesselDischargeReceiptFilter می‌آیند.
        var registeredVesselReceiptRows = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(VesselDischargeReceiptFilter(id))
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new
            {
                Item = new ShipmentPnlRegisteredVesselReceiptItemViewModel
                {
                    Id = r.Id,
                    InventoryTransportLegId = r.InventoryTransportLegId,
                    ReceiptDate = r.ReceiptDate,
                    ContractNumber = r.InventoryTransportLeg!.SourcePurchaseContract != null
                        ? r.InventoryTransportLeg.SourcePurchaseContract.DisplayLabel
                        : $"#{r.InventoryTransportLeg.SourcePurchaseContractId}",
                    DestinationTerminalName = r.DestinationTerminal != null ? r.DestinationTerminal.Name : "-",
                    DestinationTankName = r.DestinationStorageTank != null
                        ? (r.DestinationStorageTank.DisplayName ?? r.DestinationStorageTank.TankCode)
                        : null,
                    ReceivedQuantityMt = r.ReceivedQuantityMt,
                    DestinationTerminalId = r.DestinationTerminalId,
                    DestinationStorageTankId = r.DestinationStorageTankId,
                    ProductId = r.InventoryTransportLeg!.ProductId
                },
                // یادداشتِ رسید گروهی برای همهٔ قراردادهای یک بار ثبت یکسان ساخته می‌شود،
                // پس کلید تشخیصِ «یک بار ثبت» است؛ فقط برای گروه‌بندی نمایشی استفاده می‌شود.
                r.Notes
            })
            .ToListAsync();
        var registeredVesselReceipts = registeredVesselReceiptRows
            .Select(row => row.Item)
            .ToList();
        // نمایش: رسیدهای هم‌ثبت (تاریخ + مقصد + یادداشت گروهی یکسان) یک سطر جمع می‌شوند و
        // سهم هر قرارداد داخل همان سطر باز می‌شود. هیچ رکوردی ادغام یا حذف نمی‌شود.
        var registeredVesselReceiptGroups = registeredVesselReceiptRows
            .GroupBy(row => new
            {
                row.Item.ReceiptDate,
                row.Item.DestinationTerminalId,
                row.Item.DestinationStorageTankId,
                Notes = row.Notes ?? string.Empty
            })
            .Select(group => new ShipmentPnlRegisteredVesselReceiptGroupViewModel
            {
                Key = group.Max(row => row.Item.Id),
                ReceiptDate = group.Key.ReceiptDate,
                DestinationTerminalName = group.First().Item.DestinationTerminalName,
                DestinationTankName = group.First().Item.DestinationTankName,
                ReceivedQuantityMt = decimal.Round(
                    group.Sum(row => row.Item.ReceivedQuantityMt),
                    4,
                    MidpointRounding.AwayFromZero),
                Items = group
                    .Select(row => row.Item)
                    .OrderBy(item => item.ContractNumber, StringComparer.Ordinal)
                    .ThenBy(item => item.Id)
                    .ToList()
            })
            .OrderByDescending(group => group.ReceiptDate)
            .ThenByDescending(group => group.Key)
            .ToList();

        // دریافت‌های نقدیِ مشتری که مستقیماً به همین کشتی وصل شده‌اند (PaymentTransaction.ShipmentId).
        // فقط نمایش/تجمیع؛ محاسبهٔ حاشیهٔ سود تغییری نمی‌کند (درآمد همچنان از فروش است، نه پول رسیده).
        var customerReceiptRows = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.ShipmentId == id && p.CustomerId.HasValue)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new ShipmentPnlCustomerReceiptItemViewModel
            {
                Id = p.Id,
                PaymentDate = p.PaymentDate,
                CustomerName = p.Customer != null ? p.Customer.Name : null,
                Reference = p.Reference,
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : null,
                AmountUsd = p.AmountUsd,
                IsInflow = p.Direction == PaymentDirection.In
            })
            .ToListAsync();
        var customerReceiptsUsd = decimal.Round(
            customerReceiptRows.Sum(r => r.SignedAmountUsd),
            2,
            MidpointRounding.AwayFromZero);

        // قراردادهای خرید داخل محموله + مقدار استفاده‌شده/باقی‌مانده.
        // استفاده‌شده = جمع مقدار همهٔ حمل‌های غیرلغوِ همان قرارداد در همین محموله.
        var contractLines = await BuildShipmentContractLinesAsync(id);

        // ضایعات مستقیم محموله (همان منطق نمای کشتی؛ فقط نمایش، بدون محاسبهٔ مالی جدید).
        var losses = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == id && !l.IsCancelled)
            .OrderByDescending(l => l.EventDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new ShipmentJourneyLossItem
            {
                Id = l.Id,
                EventDate = l.EventDate,
                StageName = LossEventStageLabels.ToPersian(l.Stage),
                DifferenceQuantityMt = l.DifferenceQuantityMt,
                ChargeableLossMt = l.ChargeableLossMt,
                ContractId = l.ContractId,
                ContractName = l.Contract != null ? l.Contract.ContractName : null,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                ResponsiblePartyType = l.ResponsiblePartyType,
                ResponsiblePartyName = l.ResponsiblePartyName,
                FinancialTreatment = l.FinancialTreatment,
                Reference = l.Reference,
                Notes = l.Notes,
                // نمبر وسیله از همان مرحله‌ای که کسری روی آن ثبت شده: ارسال، حمل، بارگیری، رسید.
                VehicleNumber = l.TruckDispatch != null && l.TruckDispatch.Truck != null
                    ? l.TruckDispatch.Truck.PlateNumber
                    : l.TransportLeg != null
                        ? (l.TransportLeg.Truck != null
                            ? l.TransportLeg.Truck.PlateNumber
                            : l.TransportLeg.WagonNumber ?? l.TransportLeg.RwbNo)
                        : l.LoadingRegister != null
                            ? (l.LoadingRegister.Truck != null
                                ? l.LoadingRegister.Truck.PlateNumber
                                : l.LoadingRegister.WagonNumber ?? l.LoadingRegister.RwbNo)
                            : l.LoadingReceipt != null && l.LoadingReceipt.LoadingRegister != null
                                ? (l.LoadingReceipt.LoadingRegister.Truck != null
                                    ? l.LoadingReceipt.LoadingRegister.Truck.PlateNumber
                                    : l.LoadingReceipt.LoadingRegister.WagonNumber ?? l.LoadingReceipt.LoadingRegister.RwbNo)
                                : null
            })
            .ToListAsync();

        var directLossQuantityMt = await GetDirectShipmentLossQuantityMtAsync(id);
        var quantityFlow = await BuildShipmentQuantityFlowAsync(shipment, contractLines);
        var totalExpensesUsd = rollup.TotalPurchaseCostUsd + rollup.TotalOperationalExpensesUsd;

        // اخطارهای نمایشی (منتقل‌شده از نمای کشتی؛ صرفاً راهنما، بدون منطق مالی).
        var declaredQuantityMt = ResolveOriginalShipmentQuantity(shipment);
        var allocatedFromContractsMt = contractLines.Sum(c => c.AllocatedQuantityMt);
        var warnings = new List<string>();
        if (contractLines.Count == 0)
        {
            warnings.Add("قراردادی به این محموله وصل نشده است. در فرم محموله، قراردادهای خرید را تخصیص بدهید.");
        }
        if (rollup.TransportLegs.Count == 0)
        {
            warnings.Add("هیچ جریان حملی به این محموله وصل نیست؛ بارگیری اولیه فقط از طریق قرارداد منبع دیده می‌شود.");
        }
        if (declaredQuantityMt > 0m && allocatedFromContractsMt > 0m
            && Math.Abs(declaredQuantityMt - allocatedFromContractsMt) > 0.001m)
        {
            warnings.Add("مقدار اعلام‌شدهٔ محموله با جمع مقدار قراردادها برابر نیست؛ نیاز به بررسی.");
        }
        if (rollup.Sales.Count == 0)
        {
            warnings.Add("هنوز فروشی برای این محموله ثبت نشده است.");
        }
        if (quantityFlow.VesselUnloadedQuantityMt > 0m && !quantityFlow.HasExactSourceTankLineage)
        {
            warnings.Add("تخلیه و مانده مخزن از رسیدها و سهم قراردادهای همین محموله برآورد شده است؛ این بخش هنوز ردیابی مستقیم ندارد.");
        }
        if (quantityFlow.DraftTransportQuantityMt > 0m)
        {
            warnings.Add($"{quantityFlow.DraftTransportQuantityMt:N4} MT انتقال پیش‌نویس جدا نمایش داده می‌شود و از مانده واقعی مخزن کم نشده است.");
        }

        // جواز (Company) متعلق به قرارداد است نه محموله؛ یک محموله می‌تواند چند قرارداد با جواز متفاوت
        // داشته باشد. نمایش از روی قراردادهای تخصیص‌یافته ساخته می‌شود، نه فقط قرارداد اصلی.
        var shipmentCompanyNames = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == id && sc.Contract != null && sc.Contract.Company != null)
            .Select(sc => sc.Contract!.Company!.Name)
            .Distinct()
            .ToListAsync();

        ShipmentLineageRollup? lineageRollup = null;
        if (_lineageOptions.UseInPnl)
        {
            lineageRollup = await _lineagePnl.BuildAsync(id);
        }

        var lineageBadge = lineageRollup is null
            ? ((string Text, string Tone)?)null
            : ResolveLineageBadge(lineageRollup.OverallConfidence);

        return View(new ShipmentPnlDetailsViewModel
        {
            ContractLines = contractLines,
            PurchaseSourceLines = rollup.PurchaseCost.Lines,
            Losses = losses,
            Warnings = warnings,
            HasLineageData = lineageRollup?.HasLineageData == true,
            IsLineageCalculationActive = _lineageOptions.UseInPnl,
            LineageBadgeText = lineageBadge?.Text,
            LineageBadgeTone = lineageBadge?.Tone,
            OverallLineageConfidence = lineageRollup?.OverallConfidence.ToString(),
            HasVerifiedLineage = lineageRollup?.HasVerified == true,
            HasEstimatedLineage = lineageRollup?.HasEstimated == true,
            HasLegacyLineage = lineageRollup?.HasLegacy == true,
            NeedsReviewCount = lineageRollup?.NeedsReviewCount ?? 0,
            LineageWarnings = lineageRollup?.Warnings ?? [],
            LineageCargoInVesselMt = lineageRollup?.CargoInVesselMt,
            LineageArrivedReceivedMt = lineageRollup?.ArrivedReceivedMt,
            LineageInTransitMt = lineageRollup?.InTransitMt,
            LineageInStockMt = lineageRollup?.InStockMt,
            LineageSoldMt = lineageRollup?.SoldMt,
            LineageSoldUsd = lineageRollup?.SoldUsd,
            LineageLossMt = lineageRollup?.LossMt,
            LineageExpenseUsd = lineageRollup?.ExpenseUsd,
            LineageCustomsUsd = lineageRollup?.CustomsUsd,
            LossQuantityMt = losses.Sum(l => l.DifferenceQuantityMt),
            DirectLossQuantityMt = directLossQuantityMt,
            CompanyLossQuantityMt = losses
                .Where(l => l.ResponsiblePartyType == ShipmentShortageResponsibilityTypes.CompanyLoss)
                .Sum(l => l.DifferenceQuantityMt),
            Id = shipment.Id,
            ShipmentCode = shipment.ShipmentCode,
            VesselId = shipment.VesselId,
            VesselName = shipment.Vessel?.Name,
            DepartureDate = shipment.DepartureDate,
            ArrivalDate = shipment.ArrivalDate,
            QuantityMt = quantityFlow.OriginalShipmentQuantityMt,
            OriginalShipmentQuantityMt = quantityFlow.OriginalShipmentQuantityMt,
            VesselUnloadedQuantityMt = quantityFlow.VesselUnloadedQuantityMt,
            InventoryTransportedOutQuantityMt = quantityFlow.InventoryTransportedOutQuantityMt,
            InTransitQuantityMt = quantityFlow.InTransitQuantityMt,
            DeliveredAtDestinationQuantityMt = quantityFlow.DeliveredAtDestinationQuantityMt,
            RemainingInSourceTankQuantityMt = quantityFlow.RemainingInSourceTankQuantityMt,
            InventoryTransportShortageQuantityMt = quantityFlow.InventoryTransportShortageQuantityMt,
            CancelledTransportQuantityMt = quantityFlow.CancelledTransportQuantityMt,
            DraftTransportQuantityMt = quantityFlow.DraftTransportQuantityMt,
            HasExactSourceTankLineage = quantityFlow.HasExactSourceTankLineage,
            Notes = shipment.Notes,
            ContractNumber = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.ContractNumber), "Mixed contracts")
                ?? shipment.Contract?.ContractNumber,
            ContractUnitText = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.ContractUnitText), "Mixed units")
                ?? (shipment.Contract != null ? ContractUiText.ResolveUnitText(shipment.Contract.Unit) : "-"),
            CompanyName = ResolveSingleOrMixed(
                shipmentCompanyNames,
                shipmentCompanyNames.Count > 0 ? string.Join("، ", shipmentCompanyNames) : "چند جواز")
                ?? shipment.Contract?.Company?.Name,
            ProductName = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.ProductName), "Mixed products")
                ?? shipment.Contract?.Product?.Name,
            CustomerName = shipment.Contract?.Customer?.Name,
            SupplierName = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.SupplierName), "Mixed suppliers")
                ?? shipment.Contract?.Supplier?.Name,
            OriginName = shipment.OriginLocation?.Name,
            DestinationName = shipment.DestinationLocation?.Name,
            TotalSalesUsd = rollup.TotalSalesUsd,
            TotalPurchaseCostUsd = rollup.TotalPurchaseCostUsd,
            TotalOperationalExpensesUsd = rollup.TotalOperationalExpensesUsd,
            TotalExpensesUsd = totalExpensesUsd,
            GrossMarginUsd = PnlMath.GrossProfit(rollup.TotalSalesUsd, totalExpensesUsd),
            RealisedPnl = rollup.RealisedSales.ToViewModel(),
            LedgerEntriesCount = rollup.LedgerEntries.Count,
            LedgerDebitTotalUsd = rollup.LedgerEntries.Where(l => l.SideName == "Debit").Sum(l => l.AmountUsd),
            LedgerCreditTotalUsd = rollup.LedgerEntries.Where(l => l.SideName == "Credit").Sum(l => l.AmountUsd),
            Sales = rollup.Sales,
            ShipmentSalesQuantityMt = rollup.SoldQuantityMt,
            Expenses = rollup.Expenses,
            TransportLegs = rollup.TransportLegs,
            LedgerEntries = rollup.LedgerEntries,
            DispatchTraces = dispatchTraces,
            RegisteredVesselReceipts = registeredVesselReceipts,
            RegisteredVesselReceiptGroups = registeredVesselReceiptGroups,
            CustomerReceipts = customerReceiptRows,
            CustomerReceiptsUsd = customerReceiptsUsd
        });
    }

    private static (string Text, string Tone) ResolveLineageBadge(LineageConfidence confidence) => confidence switch
    {
        LineageConfidence.Verified => ("ردیابی قطعی", "success"),
        LineageConfidence.Estimated => ("داده تخمینی", "warning"),
        LineageConfidence.Legacy => ("داده قدیمی", "muted"),
        LineageConfidence.NeedsReview => ("نیازمند بازبینی", "danger"),
        _ => ("داده تخمینی", "warning")
    };

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterDirectLoss(ShipmentDirectLossCreateViewModel model)
    {
        var redirectUrl = !string.IsNullOrWhiteSpace(model.ReturnUrl) && Url?.IsLocalUrl(model.ReturnUrl) == true
            ? model.ReturnUrl
            : Url?.Action(nameof(Details), new { id = model.ShipmentId }) ?? $"/ShipmentPnl/Details/{model.ShipmentId}";

        var shipment = await _db.Shipments
            .Include(s => s.Contract)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == model.ShipmentId);

        if (shipment is null)
        {
            return NotFound();
        }

        var contractLines = await BuildShipmentContractLinesAsync(model.ShipmentId);
        var shortageRegisterableQuantityMt = contractLines.Sum(c => c.ShortageRegisterableQuantityMt);
        var lossQuantityMt = decimal.Round(model.LossQuantityMt, 4, MidpointRounding.AwayFromZero);
        var responsibilityType = NormalizeShipmentShortageResponsibility(model.ResponsibilityType);

        if (lossQuantityMt <= 0m)
        {
            TempData["error"] = "مقدار ضایعات باید بزرگ‌تر از صفر باشد.";
            return Redirect(redirectUrl!);
        }

        if (responsibilityType is null)
        {
            TempData["error"] = "Ù†ÙˆØ¹ Ø«Ø¨Øª Ú©Ø³Ø±ÛŒ Ù…Ø¹ØªØ¨Ø± Ù†ÛŒØ³Øª.";
            return Redirect(redirectUrl!);
        }

        int? contractId = null;
        if (model.ContractId.HasValue)
        {
            if (!contractLines.Any(c => c.ContractId == model.ContractId.Value))
            {
                TempData["error"] = "Ù‚Ø±Ø§Ø±Ø¯Ø§Ø¯ Ø§Ù†ØªØ®Ø§Ø¨â€ŒØ´Ø¯Ù‡ Ø¯Ø± Ø§ÛŒÙ† Ù…Ø­Ù…ÙˆÙ„Ù‡ ÙˆØ¬ÙˆØ¯ Ù†Ø¯Ø§Ø±Ø¯.";
                return Redirect(redirectUrl!);
            }

            contractId = model.ContractId.Value;
        }
        else if (contractLines.Count == 1)
        {
            contractId = contractLines[0].ContractId;
        }

        var maxAvailableQuantityMt = contractId.HasValue
            ? contractLines.First(c => c.ContractId == contractId.Value).ShortageRegisterableQuantityMt
            : shortageRegisterableQuantityMt;

        if (lossQuantityMt - maxAvailableQuantityMt > 0.0001m)
        {
            TempData["error"] = $"مقدار ضایعات ({lossQuantityMt:N4} MT) از مقدار قابل ثبت کسری ({maxAvailableQuantityMt:N4} MT) بیشتر است.";
            return Redirect(redirectUrl!);
        }

        var partyName = NormalizeFreeText(model.ResponsiblePartyName);
        if (RequiresResponsiblePartyName(responsibilityType) && string.IsNullOrWhiteSpace(partyName))
        {
            TempData["error"] = "Ø¨Ø±Ø§ÛŒ Ø§ÛŒÙ† Ù†ÙˆØ¹ Ø«Ø¨ØªØŒ Ù†Ø§Ù… Ø·Ø±Ù Ù…Ø³Ø¦ÙˆÙ„ Ø§Ù„Ø²Ø§Ù…ÛŒ Ø§Ø³Øª.";
            return Redirect(redirectUrl!);
        }

        var splitLines = NormalizeSplitLines(model.SplitLines);
        if (responsibilityType == ShipmentShortageResponsibilityTypes.Split)
        {
            var splitError = ValidateSplitLines(splitLines, lossQuantityMt);
            if (splitError is not null)
            {
                TempData["error"] = splitError;
                return Redirect(redirectUrl!);
            }

            partyName = "Ú†Ù†Ø¯ Ø·Ø±Ù";
        }
        else
        {
            splitLines = [];
        }

        var productId = await ResolveShipmentProductIdAsync(model.ShipmentId, shipment);
        if (!productId.HasValue)
        {
            TempData["error"] = "جنس این محموله مشخص نیست؛ اول قرارداد یا حمل محموله را کامل کنید.";
            return Redirect(redirectUrl!);
        }

        var eventDate = model.EventDate == default
            ? AfghanistanBusinessClock.SystemToday
            : model.EventDate.Date;
        var estimatedAmountUsd = model.LossAmountUsd.HasValue && model.LossAmountUsd.Value > 0m
            ? decimal.Round(model.LossAmountUsd.Value, 2, MidpointRounding.AwayFromZero)
            : EstimateShipmentLossValueUsd(lossQuantityMt, contractLines, contractId);
        var claimStatus = NormalizeClaimStatus(model.ClaimStatus);
        var notes = BuildShipmentShortageNotes(model.Notes, estimatedAmountUsd, claimStatus, splitLines);
        var reference = BuildShipmentShortageReference(model.ShipmentId, eventDate, lossQuantityMt, responsibilityType, contractId);

        var duplicateExists = await _db.LossEvents
            .AsNoTracking()
            .AnyAsync(l => l.ShipmentId == model.ShipmentId
                && !l.IsCancelled
                && l.Reference == reference);
        if (duplicateExists)
        {
            TempData["error"] = "Ø§ÛŒÙ† Ú©Ø³Ø±ÛŒ Ø¨Ø§ Ù‡Ù…ÛŒÙ† Ù…Ø´Ø®ØµØ§Øª Ù‚Ø¨Ù„Ø§Ù‹ Ø«Ø¨Øª Ø´Ø¯Ù‡ Ø§Ø³Øª.";
            return Redirect(redirectUrl!);
        }

        _db.LossEvents.Add(new LossEvent
        {
            Stage = LossEventStage.TransitLoss,
            ProductId = productId.Value,
            ContractId = contractId,
            ShipmentId = model.ShipmentId,
            EventDate = eventDate,
            ExpectedQuantityMt = maxAvailableQuantityMt,
            ActualQuantityMt = maxAvailableQuantityMt - lossQuantityMt,
            DifferenceQuantityMt = lossQuantityMt,
            ToleranceQuantityMt = 0m,
            AllowableLossMt = 0m,
            ChargeableLossMt = lossQuantityMt,
            ResponsiblePartyType = responsibilityType,
            ResponsiblePartyName = partyName ?? ResponsibilityLabelFa(responsibilityType),
            FinancialTreatment = FinancialTreatmentLabelFa(responsibilityType),
            AffectsInventory = false,
            LossCertainty = contractId.HasValue ? LossCertaintyLevel.Measured : LossCertaintyLevel.Estimated,
            Reference = reference,
            Notes = notes
        });
        await _db.SaveChangesAsync();

        TempData["ok"] = "ضایعات محموله ثبت شد و از باقی‌مانده محموله کم شد.";
        return Redirect(redirectUrl!);
    }

    // قراردادهای خرید داخل یک محموله با مقدار تخصیص/جریان اصلی/باقی‌مانده.
    // «استفاده‌شده» فقط مرحلهٔ اصلی کشتی است؛ انتقال موجودی جدا گزارش می‌شود.
    private async Task<IReadOnlyList<ShipmentContractLineViewModel>> BuildShipmentContractLinesAsync(int shipmentId)
    {
        var shipmentContracts = await _db.ShipmentContracts
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Product)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Unit)
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId)
            .OrderBy(sc => sc.Id)
            .ToListAsync();

        if (shipmentContracts.Count == 0)
        {
            return [];
        }

        var shipmentTransportLegs = await _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId
                && l.Status != InventoryTransportLegStatus.Cancelled)
            .ToListAsync();

        var shipmentRootLegs = ResolveShipmentRootLegs(
            shipmentTransportLegs,
            await LoadVesselDischargeLegIdsAsync(shipmentId));
        var shipmentRootLegIds = shipmentRootLegs.Select(l => l.Id).ToHashSet();
        var hasVesselRootLegs = shipmentRootLegs.Any(l => l.TransportType == LoadingTransportType.Vessel);

        var loadedByContract = shipmentRootLegs
            .GroupBy(l => l.SourcePurchaseContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                LoadedMt = g.Sum(x => x.QuantityMt)
            })
            .ToDictionary(x => x.ContractId, x => x.LoadedMt);

        if (!hasVesselRootLegs)
        {
            foreach (var shipmentContract in shipmentContracts)
            {
                if (!loadedByContract.TryGetValue(shipmentContract.ContractId, out var loadedMt))
                {
                    continue;
                }

                var allocationCap = Math.Max(shipmentContract.QuantityMt ?? 0m, 0m);
                loadedByContract[shipmentContract.ContractId] = decimal.Round(
                    Math.Min(loadedMt, allocationCap),
                    4,
                    MidpointRounding.AwayFromZero);
            }
        }

        var transportedFromInventoryByContract = shipmentTransportLegs
            .Where(l => l.TransportType != LoadingTransportType.Vessel
                && l.Status != InventoryTransportLegStatus.Draft
                && l.OutboundInventoryMovementId.HasValue
                && !shipmentRootLegIds.Contains(l.Id))
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));

        var contractIds = shipmentTransportLegs
            .Select(l => l.SourcePurchaseContractId)
            .Concat(shipmentContracts.Select(sc => sc.ContractId))
            .Distinct()
            .ToList();

        var finalPriceByContract = shipmentContracts
            .Where(sc => sc.Contract is not null)
            .ToDictionary(sc => sc.ContractId, sc => ContractPricingAdapter.GetCanonicalFinalPrice(sc.Contract!));

        var purchaseSnapshots = await new PurchaseAggregationService(_db)
            .AggregateForContractsAsync(contractIds, finalPriceByContract);

        // همان منبع واحدی که TotalPurchaseCostUsd با آن ساخته می‌شود؛ ردیف‌های این تب هرگز
        // نباید نرخی متفاوت از جمع کل نشان بدهند.
        var purchaseCost = await new ShipmentPurchaseCostService(_db).BuildAsync(shipmentId);
        var purchaseUnitCostByContract = purchaseCost.UnitCostByContract;
        var purchaseValueByContract = purchaseCost.CostByContract;
        var purchaseSourceByContract = purchaseCost.CostSourceByContract;

        var actualPurchaseUnitCostByContract = shipmentTransportLegs
            .Select(l =>
            {
                var (unitCost, _) = ResolvePurchaseUnitCost(l, purchaseSnapshots);
                return new { l.SourcePurchaseContractId, l.QuantityMt, UnitCost = unitCost };
            })
            .Where(x => x.UnitCost.HasValue && x.UnitCost.Value > 0m && x.QuantityMt > 0m)
            .GroupBy(x => x.SourcePurchaseContractId)
            .ToDictionary(
                g => g.Key,
                g => decimal.Round(
                    g.Sum(x => x.QuantityMt * x.UnitCost!.Value) / g.Sum(x => x.QuantityMt),
                    6,
                    MidpointRounding.AwayFromZero));

        // کسری تخلیهٔ ریشه (کشتی/واگن → مخزن) از همان «قرارداد مصرف» TransportQuantityService
        // ساخته می‌شود:  بارگیری = فروش + رسید به موجودی + انتقال به وسیله + کسری + باقیمانده.
        // هر رسیدِ لغو‌نشده به‌اندازهٔ ReceivedQuantityMt + ShortageQuantityMt از لِج مصرف می‌کند.
        //
        //   کسری = کسریِ صریحِ ثبت‌شده  +  «کسری خاموش»
        //
        // «کسری خاموش» فقط برای لِجی معنی دارد که تخلیه‌اش بسته شده (Status=Received) ولی مقدارش
        // کامل مصرف نشده — دادهٔ قدیمی که کسری را ثبت نکرده است. تا وقتی لِج باز است (Loaded/InTransit)
        // مقدارِ مصرف‌نشده هنوز روی کشتی است و «بار تخلیه‌نشده» به حساب می‌آید، نه کسری؛ وگرنه در
        // تخلیهٔ مرحله‌ای، تمام بارِ هنوز تخلیه‌نشده به‌عنوان کسری گزارش می‌شد.
        //
        // فقط لِج‌هایی که رسید (تخلیه) دارند؛ لِج بدون رسید هنوز در راه است و کسری قطعی ندارد.
        var rootReceiptTotals = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipmentId
                && shipmentRootLegIds.Contains(r.InventoryTransportLegId)
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Draft
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(r => r.InventoryTransportLegId)
            .Select(g => new
            {
                LegId = g.Key,
                ConsumedMt = g.Sum(x => x.ReceivedQuantityMt + x.ShortageQuantityMt),
                RecordedShortageMt = g.Sum(x => x.ShortageQuantityMt)
            })
            .ToDictionaryAsync(x => x.LegId, x => new { x.ConsumedMt, x.RecordedShortageMt });

        var transportShortageByContract = shipmentRootLegs
            .Where(l => rootReceiptTotals.ContainsKey(l.Id))
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(l =>
                {
                    var totals = rootReceiptTotals[l.Id];
                    var unconsumedMt = Math.Max(0m, l.QuantityMt - totals.ConsumedMt);
                    var silentShortageMt = l.Status == InventoryTransportLegStatus.Received
                        ? unconsumedMt
                        : 0m;
                    return decimal.Round(
                        totals.RecordedShortageMt + silentShortageMt,
                        4,
                        MidpointRounding.AwayFromZero);
                }));

        var directLosses = await GetDirectShipmentLossesAsync(shipmentId);
        var lossCapacityByContract = shipmentContracts
            .ToDictionary(
                sc => sc.ContractId,
                sc =>
                {
                    var loaded = loadedByContract.TryGetValue(sc.ContractId, out var loadedMt) ? loadedMt : 0m;
                    var shortage = transportShortageByContract.TryGetValue(sc.ContractId, out var shortageMt) ? shortageMt : 0m;
                    return decimal.Round(Math.Max(loaded - shortage, 0m), 4, MidpointRounding.AwayFromZero);
                });
        var directLossByContract = AllocateDirectLossesByContract(directLosses, lossCapacityByContract);

        return shipmentContracts
            .Select(sc =>
            {
                var allocated = sc.QuantityMt ?? 0m;
                // نرخ و ارزش دقیقاً از ShipmentPurchaseCostService؛ اگر محموله برای این قرارداد
                // سهم بارگیری دارد این عدد Source-exact است، وگرنه همان fallback قرارداد.
                var hasCentralCost = purchaseUnitCostByContract.TryGetValue(sc.ContractId, out var centralUnitPrice)
                    && centralUnitPrice is > 0m;
                var unitPrice = hasCentralCost
                    ? centralUnitPrice
                    : actualPurchaseUnitCostByContract.TryGetValue(sc.ContractId, out var actualUnitCost)
                        ? actualUnitCost
                        : null;
                var unitPriceSource = hasCentralCost
                    ? purchaseSourceByContract.GetValueOrDefault(sc.ContractId, ShipmentPurchaseCostResolver.SourceMissing)
                    : actualPurchaseUnitCostByContract.ContainsKey(sc.ContractId)
                        && shipmentTransportLegs.Any(l => l.SourcePurchaseContractId == sc.ContractId
                            && l.PurchaseUnitCostUsd is > 0m)
                        ? ShipmentPurchaseCostResolver.SourceLegActualCost
                        : purchaseSourceByContract.GetValueOrDefault(sc.ContractId, ShipmentPurchaseCostResolver.SourceMissing);
                // ارزش خرید برای قراردادِ دارای سهم دقیق، جمعِ همان سطرهاست (نه مقدار تخصیص × میانگین).
                var totalValue = hasCentralCost && purchaseValueByContract.TryGetValue(sc.ContractId, out var centralValue) && centralValue > 0m
                    ? centralValue
                    : unitPrice is > 0m && allocated > 0m
                        ? RoundMoney(allocated * unitPrice.Value)
                        : (decimal?)null;
                return new ShipmentContractLineViewModel
                {
                    ContractId = sc.ContractId,
                    ContractName = sc.Contract?.ContractName ?? string.Empty,
                    ContractNumber = sc.Contract?.ContractNumber ?? $"#{sc.ContractId}",
                    SupplierName = sc.Contract?.Supplier?.Name,
                    ProductName = sc.Contract?.Product?.Name,
                    ContractUnitText = sc.Contract != null ? ContractUiText.ResolveUnitText(sc.Contract.Unit) : "-",
                    AllocatedQuantityMt = allocated,
                    // «نرخ دارد» = همان معیاری که TotalPurchaseCostUsd با آن جمع می‌زند:
                    // میانگین وزنی بارگیری‌های قرارداد یا نرخ نهایی هدر (ShipmentPurchaseCostResolver).
                    HasFinalPrice = unitPrice is > 0m,
                    UnitPriceSource = unitPriceSource,
                    UsedQuantityMt = loadedByContract.TryGetValue(sc.ContractId, out var loaded) ? loaded : 0m,
                    TransportedFromInventoryQuantityMt = transportedFromInventoryByContract.TryGetValue(sc.ContractId, out var transported) ? transported : 0m,
                    TransportShortageQuantityMt = transportShortageByContract.TryGetValue(sc.ContractId, out var shortage) ? shortage : 0m,
                    DirectLossQuantityMt = directLossByContract.TryGetValue(sc.ContractId, out var loss) ? loss : 0m,
                    UnitPriceUsd = unitPrice,
                    TotalValueUsd = totalValue
                };
            })
            .ToList();
    }

    /// <summary>
    /// تنها تعریف «تخلیهٔ کشتی» در پروندهٔ محموله. یک رسیدِ ورود به موجودی که روی
    /// لِج فعالِ همین محموله ثبت شده و یا لِجِ کشتی است یا از فورم «ثبت تخلیهٔ گروهی»
    /// همین صفحه آمده است.
    ///
    /// جدول تب تخلیه، مقدار مرحلهٔ VesselUnloaded و خروجی همان تب همگی از این یک
    /// شرط تغذیه می‌شوند. پیش‌تر دو شرط جداگانه وجود داشت (یکی فقط یادداشت گروهی را
    /// می‌پذیرفت و دیگری لِجِ کشتی را هم) و همین باعث می‌شد کارت آماری و جدول دو عدد
    /// متفاوت بدهند.
    /// </summary>
    private static Expression<Func<InventoryTransportReceipt, bool>> VesselDischargeReceiptFilter(int shipmentId)
    {
        var groupReceiptTag = $"Group receipt: SHIP:{shipmentId} |";
        return r => !r.IsCancelled
            && r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
            && r.InventoryMovementId.HasValue
            && r.InventoryTransportLeg != null
            && r.InventoryTransportLeg.ShipmentId == shipmentId
            && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Draft
            && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled
            && (r.InventoryTransportLeg.TransportType == LoadingTransportType.Vessel
                || (r.Notes != null && r.Notes.Contains(groupReceiptTag)));
    }

    /// <summary>شناسهٔ لِج‌هایی که رسید تخلیهٔ کشتی روی آن‌ها ثبت شده (همان تعریف واحد بالا).</summary>
    private async Task<HashSet<int>> LoadVesselDischargeLegIdsAsync(int shipmentId)
        => (await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(VesselDischargeReceiptFilter(shipmentId))
                .Select(r => r.InventoryTransportLegId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

    /// <summary>
    /// لِج‌های «ریشه» محموله: مرحلهٔ ورود بار (کشتی/واگن → مخزن). مقدار بارگیری‌شدهٔ هر قرارداد
    /// و «کسری تخلیه» فقط از این لِج‌ها ساخته می‌شوند.
    ///
    /// لِجِ نوع Vessel حالت عادی است. جریان فعلیِ محموله ریشه را با نوع Unspecified ثبت می‌کند،
    /// پس آن حالت هم پذیرفته می‌شود — اما فقط با یک نشانهٔ قطعی: یا حرکت خروجیِ ثبت‌شده از مخزن،
    /// یا رسید تخلیهٔ کشتیِ ثبت‌شده روی همان لِج. پیش‌تر فقط شرط اول بود؛ بارِ کشتی حرکت خروجیِ
    /// موجودی ندارد، پس ریشه خالی می‌ماند و «کسری تخلیه»، «بار تخلیه‌نشده»، «مجموع کسری و ضایعات»
    /// و ظرفیت دکمهٔ «ثبت کسری» همگی صفر نشان داده می‌شدند.
    /// </summary>
    private static List<InventoryTransportLeg> ResolveShipmentRootLegs(
        IReadOnlyList<InventoryTransportLeg> shipmentLegsExcludingCancelled,
        IReadOnlySet<int> vesselDischargeLegIds)
    {
        var originalVesselLegs = shipmentLegsExcludingCancelled
            .Where(l => l.TransportType == LoadingTransportType.Vessel
                && l.Status != InventoryTransportLegStatus.Draft)
            .ToList();
        if (originalVesselLegs.Count > 0)
        {
            return originalVesselLegs;
        }

        return shipmentLegsExcludingCancelled
            .Where(l => l.TransportType == LoadingTransportType.Unspecified
                && l.Status != InventoryTransportLegStatus.Draft
                && ((l.OutboundInventoryMovementId.HasValue && l.SourceStorageTankId.HasValue)
                    || vesselDischargeLegIds.Contains(l.Id)))
            .ToList();
    }

    private sealed record ShipmentSourceReceiptRow(
        int ContractId,
        int ProductId,
        int TerminalId,
        int? StorageTankId,
        decimal QuantityMt);

    private sealed record ShipmentQuantityFlowSnapshot(
        decimal OriginalShipmentQuantityMt,
        decimal VesselUnloadedQuantityMt,
        decimal InventoryTransportedOutQuantityMt,
        decimal InTransitQuantityMt,
        decimal DeliveredAtDestinationQuantityMt,
        decimal RemainingInSourceTankQuantityMt,
        decimal InventoryTransportShortageQuantityMt,
        decimal CancelledTransportQuantityMt,
        decimal DraftTransportQuantityMt,
        bool HasExactSourceTankLineage);

    private async Task<ShipmentQuantityFlowSnapshot> BuildShipmentQuantityFlowAsync(
        Shipment shipment,
        IReadOnlyList<ShipmentContractLineViewModel> contractLines)
    {
        var originalShipmentQuantityMt = ResolveOriginalShipmentQuantity(shipment);
        var shipmentLegs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipment.Id)
            .ToListAsync();
        // مرحلهٔ ورودِ محموله (کشتی → مخزن) حملِ داخلی نیست. اگر همان لِج‌ها اینجا هم شمرده شوند،
        // یک کسری دو بار دیده می‌شود: یک بار زیر «کسری تخلیه» و یک بار زیر «کسری حمل داخلی»؛
        // در نتیجه «مجموع کسری و ضایعات» دوبرابر می‌شود و رسیدِ تخلیه به‌جای تخلیه زیر
        // «تحویل‌شده در مقصد»/«در مسیر» می‌نشیند. همان قاعدهٔ TransportedFromInventory است.
        var rootLegIds = ResolveShipmentRootLegs(
                shipmentLegs.Where(l => l.Status != InventoryTransportLegStatus.Cancelled).ToList(),
                await LoadVesselDischargeLegIdsAsync(shipment.Id))
            .Select(l => l.Id)
            .ToHashSet();
        var inventoryLegs = shipmentLegs
            .Where(l => l.TransportType != LoadingTransportType.Vessel
                && !rootLegIds.Contains(l.Id))
            .ToList();

        var activeInventoryLegs = inventoryLegs
            .Where(l => l.Status != InventoryTransportLegStatus.Draft
                && l.Status != InventoryTransportLegStatus.Cancelled
                && l.OutboundInventoryMovementId.HasValue)
            .ToList();
        var activeInventoryLegIds = activeInventoryLegs.Select(l => l.Id).ToList();

        var destinationReceipts = activeInventoryLegIds.Count == 0
            ? []
            : await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => activeInventoryLegIds.Contains(r.InventoryTransportLegId)
                    && !r.IsCancelled)
                .Select(r => new
                {
                    r.InventoryTransportLegId,
                    r.ReceivedQuantityMt,
                    r.ShortageQuantityMt
                })
                .ToListAsync();

        var receivedByLeg = destinationReceipts
            .GroupBy(r => r.InventoryTransportLegId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    ReceivedQuantityMt = group.Sum(r => r.ReceivedQuantityMt),
                    ShortageQuantityMt = group.Sum(r => r.ShortageQuantityMt)
                });
        // ===== تعریف واحد «سفر داخلی» =====
        // سفر = حرکت واقعی وسیله (موتر/واگون) پس از تخلیهٔ کشتی. legهای Unspecified
        // منبعِ ورودِ محموله‌اند (بدون وسیله و مقصد) و سفر نیستند؛ مرحلهٔ کشتی و legهای
        // ریشه هم پیش‌تر از inventoryLegs بیرون رفته‌اند.
        // postedTripLegs = سفرهایی که واقعاً روی موجودی نشسته‌اند و تنها مبنای هر سه
        // عدد «خارج‌شده از مخزن / در مسیر / تحویل‌شده در مقصد» هستند؛ پیش‌نویس‌ها و
        // سفرهای بدون خروج ثبت‌شده جداگانه در DraftTransportQuantityMt دیده می‌شوند.
        var tripLegs = inventoryLegs
            .Where(l => l.Status != InventoryTransportLegStatus.Cancelled
                && l.TransportType is LoadingTransportType.Wagon or LoadingTransportType.Truck)
            .ToList();
        var postedTripLegs = tripLegs
            .Where(l => l.Status != InventoryTransportLegStatus.Draft
                && l.OutboundInventoryMovementId.HasValue)
            .ToList();

        // باقی‌ماندهٔ هر سفر = بارگیری منهای آنچه تخلیه یا کسری ثبت شده. فیلتر وضعیت
        // (Loaded/InTransit) برداشته شد؛ سفری که وضعیتش «رسیده» است ولی رسید ناقص دارد
        // هم باید دیده شود، وگرنه مابه‌التفاوت در هیچ کارتی نمی‌آمد و معادلهٔ
        // «خارج‌شده = در مسیر + تحویل‌شده + کسری» بسته نمی‌شد.
        var inTransitQuantityMt = postedTripLegs
            .Sum(l =>
            {
                var receipt = receivedByLeg.GetValueOrDefault(l.Id);
                return Math.Max(
                    l.QuantityMt
                    - (receipt?.ReceivedQuantityMt ?? 0m)
                    - (receipt?.ShortageQuantityMt ?? 0m),
                    0m);
            });
        var deliveredAtDestinationQuantityMt = postedTripLegs
            .Sum(l => receivedByLeg.GetValueOrDefault(l.Id)?.ReceivedQuantityMt ?? 0m);

        var exactVesselSourceRows = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(VesselDischargeReceiptFilter(shipment.Id))
            .Where(r => r.DestinationTerminalId.HasValue)
            .Select(r => new ShipmentSourceReceiptRow(
                r.InventoryTransportLeg!.SourcePurchaseContractId,
                r.InventoryTransportLeg.ProductId,
                r.DestinationTerminalId!.Value,
                r.DestinationStorageTankId,
                r.ReceivedQuantityMt))
            .ToListAsync();

        var sourceRows = exactVesselSourceRows;
        var hasExactSourceTankLineage = sourceRows.Count > 0;
        if (sourceRows.Count == 0)
        {
            var allocatedByContract = contractLines
                .Where(line => line.AllocatedQuantityMt > 0m)
                .ToDictionary(line => line.ContractId, line => line.AllocatedQuantityMt);
            if (allocatedByContract.Count == 0
                && shipment.ContractId.HasValue
                && originalShipmentQuantityMt > 0m)
            {
                allocatedByContract[shipment.ContractId.Value] = originalShipmentQuantityMt;
            }

            var contractIds = allocatedByContract.Keys.ToList();
            if (contractIds.Count > 0)
            {
                // LoadingReceipt/InventoryMovement currently has no ShipmentId. Prefer
                // the same vessel, then fall back to contract-scoped rows and cap them
                // to this shipment's allocation. This is conservative, not exact lineage.
                var loadingReceiptCandidates = await _db.LoadingReceiptAllocations
                    .AsNoTracking()
                    .Where(a => a.Destination == LoadingReceiptAllocationDestination.ToInventory
                        && a.Status != LoadingReceiptAllocationStatus.Cancelled
                        && a.InventoryMovementId.HasValue
                        && a.LoadingReceipt != null
                        && a.LoadingReceipt.LoadingRegister != null
                        && contractIds.Contains(a.SourcePurchaseContractId ?? a.LoadingReceipt.LoadingRegister.ContractId))
                    .Select(a => new
                    {
                        ContractId = a.SourcePurchaseContractId ?? a.LoadingReceipt!.LoadingRegister!.ContractId,
                        ProductId = a.LoadingReceipt!.LoadingRegister!.ProductId,
                        a.TerminalId,
                        a.StorageTankId,
                        a.QuantityMt,
                        a.LoadingReceipt.LoadingRegister.VesselId
                    })
                    .ToListAsync();

                var preferredCandidates = shipment.VesselId.HasValue
                    ? loadingReceiptCandidates.Where(row => row.VesselId == shipment.VesselId).ToList()
                    : [];
                var selectedCandidates = preferredCandidates.Count > 0
                    ? preferredCandidates
                    : loadingReceiptCandidates;
                sourceRows = CapSourceRowsToShipmentAllocation(
                    selectedCandidates.Select(row => new ShipmentSourceReceiptRow(
                        row.ContractId,
                        row.ProductId,
                        row.TerminalId,
                        row.StorageTankId,
                        row.QuantityMt)).ToList(),
                    allocatedByContract);
            }
        }

        var sourceScopes = sourceRows
            .GroupBy(row => new { row.ContractId, row.ProductId, row.TerminalId, row.StorageTankId })
            .Select(group => new ShipmentSourceReceiptRow(
                group.Key.ContractId,
                group.Key.ProductId,
                group.Key.TerminalId,
                group.Key.StorageTankId,
                group.Sum(row => row.QuantityMt)))
            .ToList();

        var outboundByScope = activeInventoryLegs
            .Where(l => l.SourceTerminalId.HasValue)
            .GroupBy(l => new { ContractId = l.SourcePurchaseContractId, l.ProductId, TerminalId = l.SourceTerminalId!.Value, StorageTankId = l.SourceStorageTankId })
            .ToDictionary(group => group.Key, group => group.Sum(l => l.QuantityMt));
        var salesOutRows = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.Direction == MovementDirection.Out
                && m.SalesTransaction != null
                && m.SalesTransaction.ShipmentId == shipment.Id
                && !m.SalesTransaction.IsCancelled)
            .GroupBy(m => new { ContractId = m.ContractId, m.ProductId, m.TerminalId, m.StorageTankId })
            .Select(group => new
            {
                group.Key.ContractId,
                group.Key.ProductId,
                group.Key.TerminalId,
                group.Key.StorageTankId,
                QuantityMt = group.Sum(m => m.QuantityMt)
            })
            .ToListAsync();
        var salesOutByScope = salesOutRows.ToDictionary(
            row => (row.ContractId, row.ProductId, row.TerminalId, row.StorageTankId),
            row => row.QuantityMt);
        var inventoryLossRows = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipment.Id
                && !l.IsCancelled
                && l.AffectsInventory
                && l.ContractId.HasValue
                && l.TerminalId.HasValue)
            .GroupBy(l => new { l.ContractId, l.ProductId, l.TerminalId, l.StorageTankId })
            .Select(group => new
            {
                group.Key.ContractId,
                group.Key.ProductId,
                group.Key.TerminalId,
                group.Key.StorageTankId,
                QuantityMt = group.Sum(l => l.DifferenceQuantityMt)
            })
            .ToListAsync();
        var inventoryLossByScope = inventoryLossRows.ToDictionary(
            row => (row.ContractId, row.ProductId, row.TerminalId, row.StorageTankId),
            row => row.QuantityMt);

        var stockService = new StockService(_db);
        var remainingInSourceTankQuantityMt = 0m;
        foreach (var source in sourceScopes)
        {
            var scope = new
            {
                ContractId = source.ContractId,
                source.ProductId,
                source.TerminalId,
                source.StorageTankId
            };
            var calculatedRemaining = Math.Max(
                source.QuantityMt
                - outboundByScope.GetValueOrDefault(scope)
                - salesOutByScope.GetValueOrDefault((source.ContractId, source.ProductId, source.TerminalId, source.StorageTankId))
                - inventoryLossByScope.GetValueOrDefault(((int?)source.ContractId, source.ProductId, (int?)source.TerminalId, source.StorageTankId)),
                0m);
            // Physical stock also captures adjustments and other stock-affecting
            // movements that cannot be tied exactly to ShipmentId in the current schema.
            var physicalStock = await stockService.GetFreeQuantityMtAsync(
                source.ProductId,
                terminalId: source.TerminalId,
                contractId: source.ContractId,
                storageTankId: source.StorageTankId);
            remainingInSourceTankQuantityMt += Math.Min(calculatedRemaining, Math.Max(physicalStock, 0m));
        }

        // «حمل از موجودی» فقط legهای واقعیِ وسیله (موتر/واگون) است. legهای منبع/ورودیِ محموله
        // (TransportType=Unspecified، بدون وسیله و مقصد) نباید به‌عنوان حملِ خارج‌شده شمرده شوند،
        // وگرنه کل بار محموله دوباره از «باقی‌مانده» کسر می‌شود و منفی/صفر می‌شود.
        var transportedToVehicleQuantityMt = postedTripLegs.Sum(l => l.QuantityMt);

        return new ShipmentQuantityFlowSnapshot(
            RoundQuantity(originalShipmentQuantityMt),
            RoundQuantity(sourceScopes.Sum(row => row.QuantityMt)),
            RoundQuantity(transportedToVehicleQuantityMt),
            RoundQuantity(inTransitQuantityMt),
            RoundQuantity(deliveredAtDestinationQuantityMt),
            RoundQuantity(remainingInSourceTankQuantityMt),
            RoundQuantity(destinationReceipts.Sum(r => r.ShortageQuantityMt)),
            RoundQuantity(inventoryLegs.Where(l => l.Status == InventoryTransportLegStatus.Cancelled).Sum(l => l.QuantityMt)),
            RoundQuantity(inventoryLegs.Where(l => l.Status == InventoryTransportLegStatus.Draft).Sum(l => l.QuantityMt)),
            hasExactSourceTankLineage);
    }

    private static List<ShipmentSourceReceiptRow> CapSourceRowsToShipmentAllocation(
        IReadOnlyList<ShipmentSourceReceiptRow> rows,
        IReadOnlyDictionary<int, decimal> allocatedByContract)
    {
        var result = new List<ShipmentSourceReceiptRow>();
        foreach (var contractGroup in rows.GroupBy(row => row.ContractId))
        {
            var sourceRows = contractGroup.ToList();
            var total = sourceRows.Sum(row => row.QuantityMt);
            var allocation = allocatedByContract.GetValueOrDefault(contractGroup.Key);
            if (allocation <= 0m || total <= allocation + 0.0001m)
            {
                result.AddRange(sourceRows);
                continue;
            }

            var allocated = 0m;
            for (var index = 0; index < sourceRows.Count; index++)
            {
                var row = sourceRows[index];
                var quantityMt = index == sourceRows.Count - 1
                    ? RoundQuantity(allocation - allocated)
                    : RoundQuantity(allocation * row.QuantityMt / total);
                allocated += quantityMt;
                result.Add(row with { QuantityMt = quantityMt });
            }
        }

        return result;
    }

    private static decimal ResolveOriginalShipmentQuantity(Shipment shipment)
    {
        if (shipment.QuantityMt > 0m)
        {
            return RoundQuantity(shipment.QuantityMt);
        }

        return RoundQuantity(shipment.ShipmentContracts.Sum(sc => sc.QuantityMt ?? 0m));
    }

    private async Task<Dictionary<int, ShipmentFinancialRollup>> BuildFinancialRollupsAsync(IReadOnlyCollection<int> shipmentIds)
    {
        var shipmentIdList = shipmentIds.Distinct().ToList();
        var rollups = shipmentIdList.ToDictionary(id => id, id => new ShipmentFinancialRollup(id));
        if (shipmentIdList.Count == 0)
        {
            return rollups;
        }

        var shipmentContractRows = await _db.ShipmentContracts
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Product)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Unit)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Supplier)
            .AsNoTracking()
            .Where(sc => shipmentIdList.Contains(sc.ShipmentId)
                && sc.QuantityMt.HasValue
                && sc.QuantityMt.Value > 0m)
            .ToListAsync();

        var transportLegs = await _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Product)
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Unit)
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Supplier)
            .Include(l => l.Product)
            .Include(l => l.SourceTerminal)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Include(l => l.DestinationLocation)
            .AsNoTracking()
            .Where(l => l.ShipmentId.HasValue
                && shipmentIdList.Contains(l.ShipmentId.Value)
                && l.Status != InventoryTransportLegStatus.Cancelled)
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToListAsync();

        var contractIds = transportLegs
            .Select(l => l.SourcePurchaseContractId)
            .Concat(shipmentContractRows.Select(sc => sc.ContractId))
            .Distinct()
            .ToList();

        var finalPriceByContract = transportLegs
            .Where(l => l.SourcePurchaseContract is not null)
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(
                g => g.Key,
                g => ContractPricingAdapter.GetCanonicalFinalPrice(g.First().SourcePurchaseContract!));
        foreach (var allocation in shipmentContractRows)
        {
            if (allocation.Contract is not null && !finalPriceByContract.ContainsKey(allocation.ContractId))
            {
                finalPriceByContract[allocation.ContractId] = ContractPricingAdapter.GetCanonicalFinalPrice(allocation.Contract);
            }
        }

        var purchaseSnapshots = await new PurchaseAggregationService(_db)
            .AggregateForContractsAsync(contractIds, finalPriceByContract);

        // بهای خرید محموله فقط از یک جا می‌آید: ShipmentPurchaseCostService.
        // ترتیب آن: سهم دقیق بارگیری × نرخ قطعی همان بارگیری، و برای قراردادهای بدون سهم
        // بارگیری (دادهٔ قدیمی) همان fallback سطح قرارداد.
        var purchaseCostByShipment = await new ShipmentPurchaseCostService(_db)
            .BuildForShipmentsAsync(shipmentIdList);
        foreach (var shipmentId in shipmentIdList)
        {
            var purchaseCost = purchaseCostByShipment.TryGetValue(shipmentId, out var snapshot)
                ? snapshot
                : ShipmentPurchaseCostSnapshot.Empty(shipmentId);
            rollups[shipmentId].PurchaseCost = purchaseCost;
            rollups[shipmentId].TotalPurchaseCostUsd = purchaseCost.TotalPurchaseCostUsd;
        }

        var transportPnlByLegId = await new InventoryTransportPnlService(_db)
            .BuildForLegsAsync(transportLegs.Select(l => l.Id).ToList());
        var transportLegById = transportLegs.ToDictionary(leg => leg.Id);
        var saleBreakdownBySaleId = transportPnlByLegId
            .SelectMany(pair =>
            {
                var leg = transportLegById[pair.Key];
                var contractNumber = leg.SourcePurchaseContract?.DisplayLabel
                    ?? $"#{leg.SourcePurchaseContractId}";
                return pair.Value.Sales.Select(sale => new
                {
                    sale.SaleId,
                    Line = new ShipmentContractBreakdownLine
                    {
                        ContractNumber = contractNumber,
                        QuantityMt = sale.QuantityMt,
                        AmountUsd = sale.AmountUsd,
                        Description = sale.TraceKind
                    }
                });
            })
            .GroupBy(row => row.SaleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ShipmentContractBreakdownLine>)group
                    .Select(row => row.Line)
                    .ToList());

        var legToShipment = new Dictionary<int, int>();
        foreach (var leg in transportLegs)
        {
            var shipmentId = leg.ShipmentId!.Value;
            legToShipment[leg.Id] = shipmentId;

            transportPnlByLegId.TryGetValue(leg.Id, out var legPnl);

            var (fallbackUnitCost, fallbackCostSource) = ResolvePurchaseUnitCost(leg, purchaseSnapshots);
            var unitCost = legPnl?.PurchaseUnitCostUsd ?? fallbackUnitCost;
            var costSource = legPnl?.PurchaseCostSource ?? fallbackCostSource;
            var purchaseCost = legPnl?.PurchaseCostUsd
                ?? (unitCost.HasValue
                    ? RoundMoney(leg.QuantityMt * unitCost.Value)
                    : 0m);

            var contract = leg.SourcePurchaseContract;
            rollups[shipmentId].TransportLegs.Add(new ShipmentPnlTransportLegItemViewModel
            {
                Id = leg.Id,
                LoadedDate = leg.LoadedDate,
                ContractNumber = contract?.DisplayLabel,
                ContractUnitText = contract != null ? ContractUiText.ResolveUnitText(contract.Unit) : "-",
                ProductName = contract?.Product?.Name ?? leg.Product?.Name,
                SupplierName = contract?.Supplier?.Name,
                TransportReference = ResolveTransportReference(leg),
                DocumentReference = leg.RwbNo ?? leg.BillOfLadingNumber,
                TransportGroupKey = leg.TransportGroupKey,
                TransportTypeName = ToTransportTypeName(leg.TransportType),
                TransportStatusName = ToTransportStatusName(leg.Status),
                IsOriginalVesselMovement = leg.TransportType == LoadingTransportType.Vessel,
                SourceIsStorageTank = leg.SourceStorageTankId.HasValue,
                IsDraft = leg.Status == InventoryTransportLegStatus.Draft,
                SourceName = BuildInventoryLocationName(leg.SourceTerminal, leg.SourceStorageTank),
                DestinationName = BuildDestinationName(leg),
                Notes = leg.Notes,
                QuantityMt = leg.QuantityMt,
                HasOutboundInventoryMovement = leg.OutboundInventoryMovementId.HasValue,
                PurchaseUnitCostUsd = unitCost,
                PurchaseCostUsd = purchaseCost,
                CostSource = costSource,
                ReceivedQuantityMt = legPnl?.ReceivedQuantityMt ?? 0m,
                ShortageQuantityMt = legPnl?.ShortageQuantityMt ?? 0m,
                SoldQuantityMt = legPnl?.SoldQuantityMt ?? 0m,
                SalesUsd = legPnl?.SalesUsd ?? 0m,
                OperationalExpensesUsd = legPnl?.OperationalExpensesUsd ?? 0m,
                TotalCostUsd = legPnl?.TotalCostUsd ?? purchaseCost,
                GrossMarginUsd = legPnl?.GrossMarginUsd ?? PnlMath.GrossProfit(0m, purchaseCost),
                UnsoldQuantityMt = legPnl?.UnsoldQuantityMt ?? leg.QuantityMt,
                SalesTraceNote = legPnl?.SalesTraceNote ?? "No traceable sale"
            });
            if (legPnl is not null && legPnl.ReceiptFreightExpenseUsd > 0m)
            {
                rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
                {
                    Id = leg.Id,
                    ExpenseDate = leg.LoadedDate,
                    ExpenseTypeName = "کرایه رسید حمل",
                    AmountUsd = legPnl.ReceiptFreightExpenseUsd,
                    Description = $"حمل #{leg.Id}",
                    ContractNumber = contract?.DisplayLabel,
                    VehicleNumber = leg.WagonNumber ?? leg.RwbNo,
                    AllocationQuantityMt = leg.QuantityMt,
                    SourceKey = $"RECEIPT-FREIGHT:{leg.Id}",
                    ExpenseTypeCategory = "Transport"
                });
                rollups[shipmentId].TotalOperationalExpensesUsd += legPnl.ReceiptFreightExpenseUsd;
            }
        }

        foreach (var shipmentId in shipmentIdList)
        {
            var rollup = rollups[shipmentId];
            if (rollup.TransportLegs.Count == 0)
            {
                continue;
            }

            // سهم دقیق بارگیری، حقیقتِ خریدِ همین محموله است و هیچ برآورد leg-محوری
            // نباید جای آن را بگیرد. فقط محموله‌های بدون سهم دقیق به مسیر قدیمی می‌روند.
            if (rollup.PurchaseCost.HasLoadingExactSources)
            {
                continue;
            }

            // Purchase cost belongs to the original vessel cargo. Repeated
            // tank-to-truck/wagon movement must not purchase the same oil again.
            var originalVesselLegs = rollup.TransportLegs
                .Where(l => l.IsOriginalVesselMovement && !l.IsDraft)
                .ToList();
            if (originalVesselLegs.Count > 0)
            {
                rollup.TotalPurchaseCostUsd = originalVesselLegs.Sum(l => l.PurchaseCostUsd);
            }
            else if (rollup.TotalPurchaseCostUsd <= 0m)
            {
                // Backward-compatible fallback for old shipments that only have
                // a non-vessel leg and no ShipmentContract allocation snapshot.
                rollup.TotalPurchaseCostUsd = rollup.TransportLegs
                    .Where(l => !l.IsDraft)
                    .Sum(l => l.PurchaseCostUsd);
            }
        }

        var legIds = legToShipment.Keys.ToList();
        var saleToShipmentFromLeg = await BuildSaleShipmentMapFromTransportReceiptsAsync(legIds, legToShipment);
        var saleIdsFromLeg = saleToShipmentFromLeg.Keys.ToList();

        var sales = await _db.SalesTransactions
            .Include(s => s.Contract)
            .Include(s => s.Customer)
            // برای نمایش «نمبر وسیله» در فروش‌های مستقیم از موتر.
            .Include(s => s.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && s.SaleStage != SaleStage.PreSale
                && ((s.ShipmentId.HasValue && shipmentIdList.Contains(s.ShipmentId.Value))
                    || saleIdsFromLeg.Contains(s.Id)))
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        var saleToShipment = new Dictionary<int, int>();
        foreach (var sale in sales)
        {
            int shipmentId;
            // فروش مستقیم از محموله = فروشی که ShipmentId روی خودش ست است. فروش پس از ورود به مخزن فقط از
            // مسیر Lineage رسیدهای حمل به محموله وصل می‌شود. این تفکیک فقط برای نمایش است؛ dedup روی sale.Id
            // تضمین می‌کند هر فروش دقیقاً یک بار در جمع بیاید.
            bool isDirectShipmentSale;
            if (sale.ShipmentId.HasValue && rollups.ContainsKey(sale.ShipmentId.Value))
            {
                shipmentId = sale.ShipmentId.Value;
                isDirectShipmentSale = true;
            }
            else if (!saleToShipmentFromLeg.TryGetValue(sale.Id, out shipmentId))
            {
                continue;
            }
            else
            {
                isDirectShipmentSale = false;
            }

            saleToShipment[sale.Id] = shipmentId;
            rollups[shipmentId].Sales.Add(new ShipmentPnlSalesItemViewModel
            {
                Id = sale.Id,
                SaleDate = sale.SaleDate,
                InvoiceNumber = sale.InvoiceNumber,
                CustomerName = sale.Customer?.Name,
                ContractNumber = sale.Contract?.DisplayLabel,
                QuantityMt = sale.QuantityMt,
                UnitPriceUsd = sale.UnitPriceUsd,
                TotalUsd = sale.TotalUsd,
                IsSalePricePending = sale.IsSalePricePending,
                IsDirectShipmentSale = isDirectShipmentSale,
                VehicleNumber = sale.TruckDispatch?.Truck?.PlateNumber,
                ContractBreakdownLines = saleBreakdownBySaleId.TryGetValue(sale.Id, out var breakdown)
                    ? breakdown
                    : sale.Contract is null
                        ? []
                        :
                        [
                            new ShipmentContractBreakdownLine
                            {
                                ContractNumber = sale.Contract.DisplayLabel,
                                QuantityMt = sale.QuantityMt,
                                AmountUsd = sale.TotalUsd,
                                Description = sale.InvoiceNumber
                            }
                        ]
            });
            rollups[shipmentId].SoldQuantityMt += sale.QuantityMt;
        }

        // درآمد و بهای تمام‌شدهٔ فروشِ محقق‌شده فقط از ProfitAndLossService می‌آید.
        // نگاشت فروش→محموله (lineage) مسئولیت همین کنترلر است، اما هیچ فرمول مستقل
        // درآمد/COGS اینجا نوشته نمی‌شود.
        var realisedByShipment = await _profitAndLoss.BuildForSaleGroupsAsync(saleToShipment);
        foreach (var rollup in rollups.Values)
        {
            rollup.RealisedSales = realisedByShipment.TryGetValue(rollup.ShipmentId, out var realised)
                ? realised
                : EmptySalesPnl;
            rollup.TotalSalesUsd = rollup.RealisedSales.RevenueUsd;
        }

        // مصرف‌هایی که از مسیر موتر ثبت شده‌اند (کرایه موتر، مصرف دستی/گروهی روی دیسپچ) فقط TruckDispatchId دارند؛
        // آن‌ها را از مسیر امن dispatch → رسید حمل → leg → کشتی وصل می‌کنیم (همان الگوی گمرک موتر پایین‌تر).
        var expenses = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .Include(e => e.Contract)
            .Include(e => e.TransportLeg)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.InventoryTransportReceipt)
            .AsNoTracking()
            // کرایهٔ رسیدِ حمل جداگانه به‌عنوان «کرایه رسید حمل» از legPnl.ReceiptFreightExpenseUsd افزوده می‌شود
            // (بالاتر). این نوع مصرف (TRANSPORT-RECEIPT-FREIGHT) با TransportLegId هم ثبت می‌شود؛ اگر اینجا هم
            // شمرده شود کرایهٔ حمل دوبار حساب می‌شود. مثل InventoryTransportPnlService این نوع را کنار می‌گذاریم.
            // گمرک پایین‌تر یک بار از خودِ CustomsDeclaration.TotalUsd افزوده می‌شود. مصرفی که
            // CustomsDeclarationExpenseSync از همان اظهارنامه ساخته اگر اینجا هم بیاید، همان پول
            // دو بار در «مصارف عملیاتی» می‌نشیند و ردیفش هم دوبار در جدول دیده می‌شود.
            .Where(e => !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && (e.ExpenseType == null || e.ExpenseType.Code != InventoryTransportReceiptService.ReceiptFreightExpenseCode)
                && ((e.ShipmentId.HasValue && shipmentIdList.Contains(e.ShipmentId.Value))
                    || (e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value))
                    || (!e.TransportLegId.HasValue
                        && e.TruckDispatchId.HasValue
                        && e.TruckDispatch!.InventoryTransportReceipt != null
                        && legIds.Contains(e.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))))
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToListAsync();

        var expenseToShipment = new Dictionary<int, int>();
        foreach (var expense in expenses)
        {
            int shipmentId;
            if (expense.ShipmentId.HasValue && rollups.ContainsKey(expense.ShipmentId.Value))
            {
                shipmentId = expense.ShipmentId.Value;
            }
            else if (expense.TransportLegId.HasValue
                     && legToShipment.TryGetValue(expense.TransportLegId.Value, out var legShipmentId))
            {
                shipmentId = legShipmentId;
            }
            else if (expense.TruckDispatch?.InventoryTransportReceipt is { } dispatchReceipt
                     && legToShipment.TryGetValue(dispatchReceipt.InventoryTransportLegId, out var dispatchShipmentId))
            {
                shipmentId = dispatchShipmentId;
            }
            else
            {
                continue;
            }

            expenseToShipment[expense.Id] = shipmentId;
            rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
            {
                Id = expense.Id,
                ExpenseDate = expense.ExpenseDate,
                ExpenseTypeName = expense.ExpenseType != null ? (expense.ExpenseType.NamePersian ?? expense.ExpenseType.Name) : string.Empty,
                AmountUsd = expense.AmountUsd,
                Description = UserDescription(expense.Description),
                ContractNumber = expense.Contract?.DisplayLabel,
                TruckDispatchLabel = expense.TruckDispatch == null
                    ? null
                    : $"#{expense.TruckDispatch.Id} - {expense.TruckDispatch.Truck!.PlateNumber}",
                VehicleNumber = expense.TruckDispatch?.Truck?.PlateNumber
                    ?? expense.TransportLeg?.WagonNumber
                    ?? expense.TransportLeg?.RwbNo,
                AllocationQuantityMt = expense.TransportLeg?.QuantityMt
                    ?? expense.TruckDispatch?.LoadedQuantityMt
                    ?? 0m,
                SourceKey = ExpenseDisplaySourceKey(expense.Description, expense.Id),
                ExpenseTypeCategory = expense.ExpenseType?.Category
            });
            rollups[shipmentId].TotalOperationalExpensesUsd += expense.AmountUsd;
        }

        var customsRows = legIds.Count == 0
            ? []
            : await _db.CustomsDeclarations
                .Include(c => c.TransportLeg)
                    .ThenInclude(l => l!.SourcePurchaseContract)
                .AsNoTracking()
                .Where(c => c.TransportLegId.HasValue && legIds.Contains(c.TransportLegId.Value))
                .OrderByDescending(c => c.DeclarationDate)
                .ThenByDescending(c => c.Id)
                .ToListAsync();

        foreach (var customs in customsRows)
        {
            if (!customs.TransportLegId.HasValue
                || !legToShipment.TryGetValue(customs.TransportLegId.Value, out var shipmentId))
            {
                continue;
            }

            rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
            {
                Id = customs.Id,
                ExpenseDate = customs.DeclarationDate,
                ExpenseTypeName = "مصارف محصولی",
                AmountUsd = customs.TotalUsd,
                Description = BuildCustomsDescription(customs),
                ContractNumber = customs.TransportLeg?.SourcePurchaseContract?.DisplayLabel,
                VehicleNumber = customs.TransportLeg?.WagonNumber ?? customs.TransportLeg?.RwbNo,
                AllocationQuantityMt = customs.TransportLeg?.QuantityMt ?? 0m,
                SourceKey = $"CUSTOMS:{customs.Id}",
                IsCustoms = true
            });
            rollups[shipmentId].TotalOperationalExpensesUsd += customs.TotalUsd;
        }

        // گمرک‌هایی که از مسیر موتر (Dispatch) ثبت شده‌اند فقط TruckDispatchId دارند و TransportLegId ندارند،
        // پس در حلقهٔ بالا خوانده نمی‌شوند. آن‌ها را از مسیر امن dispatch → رسید حمل → leg → کشتی وصل می‌کنیم.
        // فیلترِ !TransportLegId.HasValue تضمین می‌کند گمرکی که هر دو مسیر را دارد فقط یک‌بار (در حلقهٔ leg بالا) حساب شود.
        var dispatchCustomsRows = legIds.Count == 0
            ? []
            : await _db.CustomsDeclarations
                .Include(c => c.TruckDispatch)
                    .ThenInclude(d => d!.InventoryTransportReceipt)
                // برای نمایش «نمبر وسیله» در گمرک‌های ثبت‌شده از مسیر موتر.
                .Include(c => c.TruckDispatch)
                    .ThenInclude(d => d!.Truck)
                .AsNoTracking()
                .Where(c => !c.TransportLegId.HasValue
                    && c.TruckDispatchId.HasValue
                    && c.TruckDispatch!.InventoryTransportReceipt != null
                    && legIds.Contains(c.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))
                .OrderByDescending(c => c.DeclarationDate)
                .ThenByDescending(c => c.Id)
                .ToListAsync();

        foreach (var customs in dispatchCustomsRows)
        {
            var legId = customs.TruckDispatch!.InventoryTransportReceipt!.InventoryTransportLegId;
            if (!legToShipment.TryGetValue(legId, out var shipmentId))
            {
                continue;
            }

            rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
            {
                Id = customs.Id,
                ExpenseDate = customs.DeclarationDate,
                ExpenseTypeName = "مصارف محصولی",
                AmountUsd = customs.TotalUsd,
                Description = BuildCustomsDescription(customs),
                ContractNumber = null,
                VehicleNumber = customs.TruckDispatch.Truck?.PlateNumber,
                AllocationQuantityMt = customs.TruckDispatch.LoadedQuantityMt,
                SourceKey = $"CUSTOMS:{customs.Id}",
                IsCustoms = true
            });
            rollups[shipmentId].TotalOperationalExpensesUsd += customs.TotalUsd;
        }

        var saleIds = saleToShipment.Keys.ToList();
        var expenseIds = expenseToShipment.Keys.ToList();
        var ledgerEntries = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => (l.ShipmentId.HasValue && shipmentIdList.Contains(l.ShipmentId.Value))
                || (l.SourceType == "Sale" && saleIds.Contains(l.SourceId))
                || (l.SourceType == "Expense" && expenseIds.Contains(l.SourceId)))
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .ToListAsync();

        foreach (var ledger in ledgerEntries)
        {
            int shipmentId;
            if (ledger.ShipmentId.HasValue && rollups.ContainsKey(ledger.ShipmentId.Value))
            {
                shipmentId = ledger.ShipmentId.Value;
            }
            else if (ledger.SourceType == "Sale" && saleToShipment.TryGetValue(ledger.SourceId, out shipmentId))
            {
            }
            else if (ledger.SourceType == "Expense" && expenseToShipment.TryGetValue(ledger.SourceId, out shipmentId))
            {
            }
            else
            {
                continue;
            }

            rollups[shipmentId].LedgerEntries.Add(new ShipmentPnlLedgerItemViewModel
            {
                Id = ledger.Id,
                EntryDate = ledger.EntryDate,
                SideName = ledger.Side == LedgerSide.Debit ? "Debit" : "Credit",
                AmountUsd = ledger.AmountUsd,
                Currency = ledger.Currency,
                SourceType = LedgerSourceFa(ledger.SourceType),
                SourceId = ledger.SourceId,
                Reference = UserDescription(ledger.Reference),
                Description = ledger.Description
            });
        }

        return rollups;
    }

    private async Task<Dictionary<int, int>> BuildSaleShipmentMapFromTransportReceiptsAsync(
        IReadOnlyCollection<int> legIds,
        IReadOnlyDictionary<int, int> legToShipment)
    {
        if (legIds.Count == 0)
        {
            return [];
        }

        // فروش مستقیم روی رسید واگن
        var receiptLinks = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.SalesTransactionId.HasValue
                && legIds.Contains(r.InventoryTransportLegId))
            .Select(r => new { r.InventoryTransportLegId, SalesTransactionId = r.SalesTransactionId!.Value })
            .ToListAsync();

        // فروش موتری که با نقل مستقیم از رسید همان محموله بارگیری شده
        var dispatchLinks = await (
            from d in _db.TruckDispatches.AsNoTracking()
            where d.Status != DispatchStatus.Cancelled
                && d.SalesTransactionId.HasValue
                && d.InventoryTransportReceiptId.HasValue
            join r in _db.InventoryTransportReceipts.AsNoTracking()
                on d.InventoryTransportReceiptId!.Value equals r.Id
            where !r.IsCancelled && legIds.Contains(r.InventoryTransportLegId)
            select new { r.InventoryTransportLegId, SalesTransactionId = d.SalesTransactionId!.Value })
            .ToListAsync();

        // فروش‌های قسمتیِ همان موتر که از راه SalesTransaction.TruckDispatchId وصل‌اند
        // (TruckDispatch.SalesTransactionId یکتاست و فقط اولین فروش را نگه می‌دارد).
        var partialDispatchLinks = await (
            from s in _db.SalesTransactions.AsNoTracking()
            where !s.IsCancelled && s.TruckDispatchId.HasValue
            join d in _db.TruckDispatches.AsNoTracking()
                on s.TruckDispatchId!.Value equals d.Id
            where d.Status != DispatchStatus.Cancelled && d.InventoryTransportReceiptId.HasValue
            join r in _db.InventoryTransportReceipts.AsNoTracking()
                on d.InventoryTransportReceiptId!.Value equals r.Id
            where !r.IsCancelled && legIds.Contains(r.InventoryTransportLegId)
            select new { r.InventoryTransportLegId, SalesTransactionId = s.Id })
            .ToListAsync();

        return receiptLinks
            .Concat(dispatchLinks)
            .Concat(partialDispatchLinks)
            .Where(r => legToShipment.ContainsKey(r.InventoryTransportLegId))
            .GroupBy(r => r.SalesTransactionId)
            .ToDictionary(g => g.Key, g => legToShipment[g.First().InventoryTransportLegId]);
    }

    private static (decimal? UnitCost, string Source) ResolvePurchaseUnitCost(
        InventoryTransportLeg leg,
        IReadOnlyDictionary<int, PurchaseAggregationSnapshot> purchaseSnapshots)
        => ShipmentPurchaseCostResolver.ResolveLegUnitCost(leg, purchaseSnapshots);

    private static string ResolveTransportReference(InventoryTransportLeg leg)
        => leg.WagonNumber
            ?? leg.BillOfLadingNumber
            ?? leg.RwbNo
            ?? $"#{leg.Id}";

    private static string ToTransportTypeName(LoadingTransportType transportType)
        => transportType switch
        {
            LoadingTransportType.Vessel => "کشتی",
            LoadingTransportType.Wagon => "واگن",
            LoadingTransportType.Truck => "موتر",
            _ => "نامشخص"
        };

    private static string ToTransportStatusName(InventoryTransportLegStatus status)
        => status switch
        {
            InventoryTransportLegStatus.Draft => "پیش‌نویس",
            InventoryTransportLegStatus.Loaded => "بارگیری‌شده",
            InventoryTransportLegStatus.InTransit => "در مسیر",
            InventoryTransportLegStatus.Received => "تحویل‌شده",
            InventoryTransportLegStatus.Cancelled => "لغوشده",
            _ => status.ToString()
        };

    private static string BuildInventoryLocationName(Terminal? terminal, StorageTank? tank)
    {
        var terminalName = terminal?.Name ?? "-";
        var tankName = StorageTankDisplay.BuildOptional(tank);
        return string.IsNullOrWhiteSpace(tankName)
            ? terminalName
            : $"{terminalName} / {tankName}";
    }

    private static string BuildDestinationName(InventoryTransportLeg leg)
    {
        if (leg.DestinationTerminal is not null)
        {
            return BuildInventoryLocationName(leg.DestinationTerminal, leg.DestinationStorageTank);
        }

        return leg.DestinationLocation?.Name
            ?? leg.RouteDescription
            ?? "-";
    }

    // شرحی که کاربر هنگام ثبت مصرف نوشته، اول رشتهٔ ذخیره‌شده است؛ متن فنی ردیابی بعد از « | » می‌آید.
    // برای نمایش فقط همان بخش کاربر را برمی‌گردانیم (دادهٔ کامل در دیتابیس دست‌نخورده می‌ماند).
    private static string? UserDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var separatorIndex = description.IndexOf('|');
        return (separatorIndex >= 0 ? description[..separatorIndex] : description).Trim();
    }

    private static string ExpenseDisplaySourceKey(string? description, int expenseId)
    {
        const string tag = "GroupKey:";
        if (!string.IsNullOrWhiteSpace(description))
        {
            var taggedPart = description
                .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(part => part.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
            if (taggedPart is not null)
            {
                var groupKey = taggedPart[tag.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(groupKey))
                {
                    return $"GROUP:{groupKey}";
                }
            }
        }

        return $"EXPENSE:{expenseId}";
    }

    // نوع منبعِ ثبت مالی را برای نمایش فارسی می‌کند؛ مقدار ناشناخته بدون تغییر برمی‌گردد.
    private static string LedgerSourceFa(string? sourceType) => sourceType switch
    {
        "Expense" => "مصرف",
        "Sale" => "فروش",
        "Loading" => "بارگیری",
        "Payment" => "پرداخت",
        "Receipt" => "رسید",
        "Purchase" => "خرید",
        _ => sourceType ?? "-"
    };

    private static string BuildCustomsDescription(CustomsDeclaration customs)
    {
        var parts = new[]
        {
            "CustomsDeclaration",
            customs.DeclarationReference,
            customs.WagonOrTruckNumber,
            customs.ConsignmentWeightMt > 0m ? $"{customs.ConsignmentWeightMt:N4} MT" : null,
            customs.Notes
        };

        return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? NormalizeShipmentShortageResponsibility(string? value)
        => value switch
        {
            ShipmentShortageResponsibilityTypes.CompanyLoss => ShipmentShortageResponsibilityTypes.CompanyLoss,
            ShipmentShortageResponsibilityTypes.SupplierDeduction => ShipmentShortageResponsibilityTypes.SupplierDeduction,
            ShipmentShortageResponsibilityTypes.ServiceProviderClaim => ShipmentShortageResponsibilityTypes.ServiceProviderClaim,
            ShipmentShortageResponsibilityTypes.PartnerShareDeduction => ShipmentShortageResponsibilityTypes.PartnerShareDeduction,
            ShipmentShortageResponsibilityTypes.Split => ShipmentShortageResponsibilityTypes.Split,
            _ => null
        };

    private static string? NormalizeFreeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeClaimStatus(string? value)
        => NormalizeFreeText(value) ?? "در انتظار";

    private static bool RequiresResponsiblePartyName(string responsibilityType)
        => responsibilityType is ShipmentShortageResponsibilityTypes.SupplierDeduction
            or ShipmentShortageResponsibilityTypes.ServiceProviderClaim
            or ShipmentShortageResponsibilityTypes.PartnerShareDeduction;

    private static string ResponsibilityLabelFa(string responsibilityType) => responsibilityType switch
    {
        ShipmentShortageResponsibilityTypes.CompanyLoss => "ضرر شرکت",
        ShipmentShortageResponsibilityTypes.SupplierDeduction => "کسر از حساب تأمین‌کننده",
        ShipmentShortageResponsibilityTypes.ServiceProviderClaim => "طلب از شرکت خدماتی",
        ShipmentShortageResponsibilityTypes.PartnerShareDeduction => "کسر از سهم شریک",
        ShipmentShortageResponsibilityTypes.Split => "تقسیم بین چند طرف",
        _ => "کسری کشتی"
    };

    private static string FinancialTreatmentLabelFa(string responsibilityType) => responsibilityType switch
    {
        ShipmentShortageResponsibilityTypes.CompanyLoss => "ثبت ضرر شرکت؛ صندوق و بانک تغییر نمی‌کند",
        ShipmentShortageResponsibilityTypes.SupplierDeduction => "کسر از حساب تأمین‌کننده؛ بدون پرداخت نقدی",
        ShipmentShortageResponsibilityTypes.ServiceProviderClaim => "ثبت طلب از شرکت خدماتی؛ تا زمان وصول نقد تغییر نمی‌کند",
        ShipmentShortageResponsibilityTypes.PartnerShareDeduction => "کسر از سهم شریک؛ صندوق و بانک تغییر نمی‌کند",
        ShipmentShortageResponsibilityTypes.Split => "تقسیم مسئولیت بین چند طرف؛ بدون پرداخت نقدی",
        _ => "ثبت کسری کشتی"
    };

    private static List<ShipmentDirectLossSplitLineInput> NormalizeSplitLines(IEnumerable<ShipmentDirectLossSplitLineInput>? lines)
        => lines?
            .Select(line => new ShipmentDirectLossSplitLineInput
            {
                ResponsibilityType = NormalizeShipmentShortageResponsibility(line.ResponsibilityType) is { } type
                    && type != ShipmentShortageResponsibilityTypes.Split
                        ? type
                        : null,
                ResponsiblePartyName = NormalizeFreeText(line.ResponsiblePartyName),
                QuantityMt = decimal.Round(Math.Max(line.QuantityMt, 0m), 4, MidpointRounding.AwayFromZero),
                AmountUsd = line.AmountUsd.HasValue && line.AmountUsd.Value > 0m
                    ? decimal.Round(line.AmountUsd.Value, 2, MidpointRounding.AwayFromZero)
                    : null,
                Status = NormalizeFreeText(line.Status),
                Notes = NormalizeFreeText(line.Notes)
            })
            .Where(line => line.QuantityMt > 0m)
            .ToList()
            ?? [];

    private static string? ValidateSplitLines(IReadOnlyList<ShipmentDirectLossSplitLineInput> lines, decimal expectedQuantityMt)
    {
        if (lines.Count == 0)
        {
            return "برای تقسیم کسری، حداقل یک ردیف مسئولیت لازم است.";
        }

        foreach (var line in lines)
        {
            if (line.ResponsibilityType is null)
            {
                return "نوع حساب در ردیف‌های تقسیم معتبر نیست.";
            }

            if (RequiresResponsiblePartyName(line.ResponsibilityType)
                && string.IsNullOrWhiteSpace(line.ResponsiblePartyName))
            {
                return "در ردیف‌های تقسیم، نام طرف مسئول برای تأمین‌کننده/خدماتی/شریک الزامی است.";
            }
        }

        var total = lines.Sum(line => line.QuantityMt);
        return Math.Abs(total - expectedQuantityMt) > 0.0001m
            ? "جمع مقدار ردیف‌های تقسیم باید برابر مقدار کل کسری باشد."
            : null;
    }

    private static decimal EstimateShipmentLossValueUsd(
        decimal lossQuantityMt,
        IReadOnlyList<ShipmentContractLineViewModel> contractLines,
        int? contractId)
    {
        if (lossQuantityMt <= 0m)
        {
            return 0m;
        }

        if (contractId.HasValue)
        {
            var line = contractLines.FirstOrDefault(c => c.ContractId == contractId.Value);
            return line?.UnitPriceUsd.HasValue == true && line.UnitPriceUsd.Value > 0m
                ? RoundMoney(lossQuantityMt * line.UnitPriceUsd.Value)
                : 0m;
        }

        var weights = contractLines
            .Select(line => new
            {
                Line = line,
                Weight = line.ShortageRegisterableQuantityMt
            })
            .Where(x => x.Weight > 0m && x.Line.UnitPriceUsd.HasValue && x.Line.UnitPriceUsd.Value > 0m)
            .ToList();
        var totalWeight = weights.Sum(x => x.Weight);
        if (totalWeight <= 0m)
        {
            return 0m;
        }

        var value = 0m;
        foreach (var weight in weights)
        {
            var quantity = lossQuantityMt * weight.Weight / totalWeight;
            value += quantity * weight.Line.UnitPriceUsd!.Value;
        }

        return RoundMoney(value);
    }

    private static string BuildShipmentShortageReference(
        int shipmentId,
        DateTime eventDate,
        decimal lossQuantityMt,
        string responsibilityType,
        int? contractId)
    {
        var quantity = lossQuantityMt.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return $"SHIPMENT-LOSS:{shipmentId}:{eventDate:yyyyMMdd}:{quantity}:{responsibilityType}:{contractId?.ToString() ?? "ALL"}";
    }

    private static string? BuildShipmentShortageNotes(
        string? userNotes,
        decimal estimatedAmountUsd,
        string claimStatus,
        IReadOnlyList<ShipmentDirectLossSplitLineInput> splitLines)
    {
        var parts = new List<string>();
        if (estimatedAmountUsd > 0m)
        {
            parts.Add($"ارزش تخمینی: {estimatedAmountUsd:N2} USD");
        }

        if (!string.IsNullOrWhiteSpace(claimStatus))
        {
            parts.Add($"وضعیت: {claimStatus}");
        }

        if (splitLines.Count > 0)
        {
            var splitText = string.Join("؛ ", splitLines.Select(line =>
                $"{ResponsibilityLabelFa(line.ResponsibilityType!)}"
                + (string.IsNullOrWhiteSpace(line.ResponsiblePartyName) ? "" : $" - {line.ResponsiblePartyName}")
                + $" - {line.QuantityMt:N4} MT"
                + (line.AmountUsd.HasValue ? $" - {line.AmountUsd.Value:N2} USD" : "")
                + (string.IsNullOrWhiteSpace(line.Status) ? "" : $" - {line.Status}")
                + (string.IsNullOrWhiteSpace(line.Notes) ? "" : $" - {line.Notes}")));
            parts.Add($"تقسیم: {splitText}");
        }

        var normalizedNotes = NormalizeFreeText(userNotes);
        if (!string.IsNullOrWhiteSpace(normalizedNotes))
        {
            parts.Add(normalizedNotes);
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private async Task<int?> ResolveShipmentProductIdAsync(int shipmentId, Shipment shipment)
    {
        var contractProductIds = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId)
            .Join(
                _db.Contracts.AsNoTracking(),
                sc => sc.ContractId,
                c => c.Id,
                (_, c) => c.ProductId)
            .Distinct()
            .ToListAsync();

        if (contractProductIds.Count == 1)
        {
            return contractProductIds[0];
        }

        if (shipment.Contract?.ProductId > 0)
        {
            return shipment.Contract.ProductId;
        }

        var transportProductIds = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId && l.Status != InventoryTransportLegStatus.Cancelled)
            .Select(l => l.ProductId)
            .Distinct()
            .ToListAsync();

        return transportProductIds.Count == 1 ? transportProductIds[0] : null;
    }

    private async Task<decimal> GetDirectShipmentLossQuantityMtAsync(int shipmentId)
    {
        var directLosses = await GetDirectShipmentLossesAsync(shipmentId);
        return directLosses.Sum(l => l.QuantityMt);
    }

    /// <summary>Reference لِ LossEventـی که خودِ سیستم برای کسریِ رسیدِ حمل می‌سازد.</summary>
    private const string TransportReceiptLossReferencePrefix = "TRANSPORT-RECEIPT:";

    /// <summary>
    /// ضایعاتی که هنوز در هیچ سطلِ دیگری شمرده نشده‌اند.
    ///
    /// تنها LossEventـی که واقعاً تکراری است، آن است که خودِ سیستم هنگام ثبت کسریِ رسیدِ حمل
    /// می‌سازد (Stage=ReceiptShortage با Reference «TRANSPORT-RECEIPT:{id}»)؛ همان مقدار از
    /// طریق ShortageQuantityMt رسید در «کسری تخلیه» یا «کسری حمل داخلی» آمده است.
    ///
    /// پیش‌تر هر LossEventـی که به یک لِج/بارگیری/رسید/ارسال/فروش وصل بود کنار گذاشته می‌شد.
    /// نتیجه: کسری‌ای که کاربر با دکمهٔ «ثبت کسری» روی یک سفر (یا روی یک ارسال/فروش) ثبت می‌کرد
    /// در جدول تب کسری‌ها دیده می‌شد ولی در هیچ‌کدام از چهار کارت آماری شمرده نمی‌شد و
    /// «مجموع کسری و ضایعات» تکان نمی‌خورد. کسری‌ای که رسید پشتش نیست تکراری هم نیست.
    /// </summary>
    private async Task<List<DirectShipmentLossRow>> GetDirectShipmentLossesAsync(int shipmentId)
        => await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId
                && !l.IsCancelled
                && l.DifferenceQuantityMt > 0m
                && !(l.Stage == LossEventStage.ReceiptShortage
                    && l.Reference != null
                    && l.Reference.StartsWith(TransportReceiptLossReferencePrefix)))
            .Select(l => new DirectShipmentLossRow(l.ContractId, l.DifferenceQuantityMt))
            .ToListAsync();

    private static Dictionary<int, decimal> AllocateDirectLossesByContract(
        IReadOnlyList<DirectShipmentLossRow> directLosses,
        IReadOnlyDictionary<int, decimal> lossCapacityByContract)
    {
        var result = lossCapacityByContract.Keys.ToDictionary(id => id, _ => 0m);
        if (directLosses.Count == 0 || lossCapacityByContract.Count == 0)
        {
            return result;
        }

        var unassignedLoss = 0m;
        foreach (var loss in directLosses)
        {
            if (loss.ContractId.HasValue
                && result.ContainsKey(loss.ContractId.Value)
                && lossCapacityByContract.TryGetValue(loss.ContractId.Value, out var capacity))
            {
                var available = Math.Max(capacity - result[loss.ContractId.Value], 0m);
                var applied = Math.Min(loss.QuantityMt, available);
                result[loss.ContractId.Value] += applied;
                unassignedLoss += Math.Max(loss.QuantityMt - applied, 0m);
                continue;
            }

            unassignedLoss += loss.QuantityMt;
        }

        if (unassignedLoss <= 0m)
        {
            return result;
        }

        var weights = lossCapacityByContract
            .Select(kvp => new
            {
                ContractId = kvp.Key,
                Remaining = Math.Max(kvp.Value - result[kvp.Key], 0m)
            })
            .Where(x => x.Remaining > 0m)
            .ToList();

        var totalRemaining = weights.Sum(x => x.Remaining);
        if (totalRemaining <= 0m)
        {
            return result;
        }

        var lossToAllocate = Math.Min(unassignedLoss, totalRemaining);
        var allocated = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            var share = i == weights.Count - 1
                ? lossToAllocate - allocated
                : decimal.Round(lossToAllocate * weights[i].Remaining / totalRemaining, 4, MidpointRounding.AwayFromZero);
            share = Math.Min(share, weights[i].Remaining);
            result[weights[i].ContractId] += share;
            allocated += share;
        }

        return result;
    }

    private static decimal RoundMoney(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static string? ResolveSingleOrMixed(IEnumerable<string?> values, string mixedLabel)
    {
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "-")
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => mixedLabel
        };
    }

    private sealed class ShipmentFinancialRollup
    {
        public ShipmentFinancialRollup(int shipmentId)
        {
            ShipmentId = shipmentId;
        }

        public int ShipmentId { get; }
        public SalesPnlSnapshot RealisedSales { get; set; } = EmptySalesPnl;
        public decimal TotalSalesUsd { get; set; }
        public decimal SoldQuantityMt { get; set; }
        public decimal TotalPurchaseCostUsd { get; set; }
        // ریز منابع بهای خرید (قرارداد/بارگیری/مقدار/نرخ) از منبع واحد محاسبه.
        public ShipmentPurchaseCostSnapshot PurchaseCost { get; set; } = ShipmentPurchaseCostSnapshot.Empty(0);
        public decimal TotalOperationalExpensesUsd { get; set; }
        public List<ShipmentPnlTransportLegItemViewModel> TransportLegs { get; } = [];
        public List<ShipmentPnlSalesItemViewModel> Sales { get; } = [];
        public List<ShipmentPnlExpenseItemViewModel> Expenses { get; } = [];
        public List<ShipmentPnlLedgerItemViewModel> LedgerEntries { get; } = [];
    }

    private sealed record DirectShipmentLossRow(int? ContractId, decimal QuantityMt);
}
