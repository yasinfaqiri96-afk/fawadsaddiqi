using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public class DashboardService : IDashboardService
{
    private const decimal LowStockThresholdMt = 10m;
    private const int EndingSoonDays = 30;
    private const int RecentDispatchDays = 7;
    private const int AlertLimit = 5;
    private const int MarketWeekSpan = 10;
    private const int DashboardRowLimit = 8;
    // کارت‌های بینش داشبورد: حداکثر سه ردیف (قرارداد/مخزن) و چهار ردیف فعالیت.
    private const int InsightRowLimit = 3;
    // مقیاس رنگ نوار پیشرفت مطابق تصویر مرجع داشبورد: هرچه درصد بالاتر، وضعیت بهتر.
    private const decimal ProgressWarningPercent = 50m;
    private const decimal ProgressCriticalPercent = 20m;

    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAfghanistanBusinessClock _businessClock;

    public DashboardService(
        ApplicationDbContext db,
        IHttpContextAccessor httpContextAccessor,
        IAfghanistanBusinessClock? businessClock = null)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
    }

    public async Task<DashboardViewModel> BuildDashboardAsync(CancellationToken ct = default)
    {
        var todayUtc = _businessClock.Today;
        var currentWeekStart = StartOfWeek(todayUtc, DayOfWeek.Monday);
        var previousWeekStart = currentWeekStart.AddDays(-7);
        var nextWeekStart = currentWeekStart.AddDays(7);

        var vm = new DashboardViewModel();

        await PopulateCountsAndTotalsAsync(vm, todayUtc, currentWeekStart, previousWeekStart, nextWeekStart, ct);
        await PopulateInventoryAsync(vm, ct);
        await PopulateAlertsAsync(vm, todayUtc, ct);
        await PopulateMarketSeriesAsync(vm, todayUtc, ct);
        await PopulateRecentActivitiesAsync(vm, ct);
        await PopulateOrderPanelsAsync(vm, ct);
        await PopulateOperationalStatsAsync(vm, todayUtc, ct);
        await PopulateActiveShipmentProgressAsync(vm, ct);

        return vm;
    }

    // Light read-only operational stats for the dashboard cards/lists.
    // All queries AsNoTracking, no heavy Include. Uncertain stats are left at 0
    // and rendered as "needs review" by the view.
    private async Task PopulateOperationalStatsAsync(DashboardViewModel vm, DateTime todayUtc, CancellationToken ct)
    {
        var tomorrowUtc = todayUtc.AddDays(1);
        var monthStartUtc = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        if (_db.Database.IsRelational())
        {
            await PopulateOperationalStatsWithSingleRoundTripAsync(vm, todayUtc, tomorrowUtc, monthStartUtc, ct);
        }
        else
        {
            await PopulateOperationalStatsWithLinqAsync(vm, todayUtc, tomorrowUtc, monthStartUtc, ct);
        }

        // همان تجمیع قبلی؛ فقط کلید مخزن هم نگه داشته می‌شود تا کارت «ظرفیت مخازن»
        // بدون کوئری اضافه روی موجودی ساخته شود. معناشناسی LowStockTankCount تغییر نکرده.
        var tankStocks = await _db.InventoryMovements.AsNoTracking()
            .Where(m => m.StorageTankId != null)
            .GroupBy(m => m.StorageTankId)
            .Select(g => new
            {
                StorageTankId = g.Key,
                StockMt = g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m)
            })
            .ToListAsync(ct);
        vm.LowStockTankCount = tankStocks.Count(s => s.StockMt <= LowStockThresholdMt);

        var stockByTankId = tankStocks
            .Where(s => s.StorageTankId.HasValue)
            .GroupBy(s => s.StorageTankId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.StockMt));
        await PopulateTankCapacitiesAsync(vm, stockByTankId, ct);
    }

    // کارت «ظرفیت مخازن»: مخازن فعالِ دارای ظرفیت، مرتب بر اساس درصد اشغال.
    private async Task PopulateTankCapacitiesAsync(
        DashboardViewModel vm,
        IReadOnlyDictionary<int, decimal> stockByTankId,
        CancellationToken ct)
    {
        var tanks = await _db.StorageTanks.AsNoTracking()
            .Where(t => t.IsActive && t.CapacityMt > 0m && t.Terminal!.CustodianPartyType == null)
            .Select(t => new
            {
                t.Id,
                t.TankCode,
                t.DisplayName,
                t.CapacityMt,
                CustodyMt = _db.CustodyMovements.Where(m => m.StorageTankId == t.Id)
                    .Sum(m => (decimal?)(m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt)) ?? 0m,
                ProductName = t.Product == null ? "" : t.Product.Name
            })
            .ToListAsync(ct);

        var tankRows = tanks
            .Select(t =>
            {
                var stock = (stockByTankId.TryGetValue(t.Id, out var value) ? value : 0m) + t.CustodyMt;
                var percent = Math.Clamp(stock / t.CapacityMt * 100m, 0m, 100m);

                return new DashboardTankCapacityViewModel
                {
                    TankId = t.Id,
                    TankName = string.IsNullOrWhiteSpace(t.DisplayName) ? t.TankCode : t.DisplayName!,
                    ProductName = t.ProductName,
                    StockMt = stock,
                    CapacityMt = t.CapacityMt,
                    FillPercent = percent,
                    ProgressTone = ResolveProgressTone(percent)
                };
            })
            .ToList();

        vm.TotalTankStockMt = tankRows.Sum(t => t.StockMt);
        vm.TotalTankCapacityMt = tankRows.Sum(t => t.CapacityMt);
        vm.TankCapacities = tankRows
            .OrderByDescending(t => t.FillPercent)
            .ThenBy(t => t.TankName, StringComparer.Ordinal)
            .Take(InsightRowLimit)
            .ToList();
    }

    // کارت «محموله‌های در جریان»: محمولهٔ ثبت‌شده + مقدار حمل‌شدهٔ آن از روی تخصیص‌های حمل.
    // همهٔ جمع‌ها به‌صورت زیرکوئری همبسته در یک رفت‌وبرگشت اجرا می‌شوند (بدون N+1).
    private async Task PopulateActiveShipmentProgressAsync(DashboardViewModel vm, CancellationToken ct)
    {
        var rows = await _db.Shipments.AsNoTracking()
            .Where(s => s.QuantityMt > 0m || s.ShipmentContracts.Any(sc => sc.QuantityMt > 0m))
            .OrderByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.ShipmentCode,
                s.QuantityMt,
                // مقدار اعلام‌شده اگر صفر باشد، از جمع تخصیص قراردادها بازسازی می‌شود
                // (همان قاعدهٔ ResolveOriginalShipmentQuantity در صفحهٔ سود و زیان محموله).
                AllocatedMt = s.ShipmentContracts.Sum(sc => (decimal?)sc.QuantityMt) ?? 0m,
                VesselName = s.Vessel == null ? null : s.Vessel.Name,
                ProductName = s.Contract == null || s.Contract.Product == null ? "" : s.Contract.Product.Name,
                // «حمل از موجودی» = فقط legهای واقعیِ وسیله (واگن/موتر) که بارگیری‌شان ثبت شده؛
                // legهای منبعِ خودِ محموله (Unspecified) و کشتی حمل خروجی نیستند.
                MovedMt = _db.InventoryTransportLegs
                    .Where(l => l.ShipmentId == s.Id
                        && l.Status != InventoryTransportLegStatus.Draft
                        && l.Status != InventoryTransportLegStatus.Cancelled
                        && l.OutboundInventoryMovementId != null
                        && (l.TransportType == LoadingTransportType.Wagon
                            || l.TransportType == LoadingTransportType.Truck))
                    .Sum(l => (decimal?)l.QuantityMt),
                InTransitLegCount = _db.InventoryTransportLegs
                    .Count(l => l.ShipmentId == s.Id && l.Status == InventoryTransportLegStatus.InTransit),
                LoadedLegCount = _db.InventoryTransportLegs
                    .Count(l => l.ShipmentId == s.Id && l.Status == InventoryTransportLegStatus.Loaded),
                ReceivedLegCount = _db.InventoryTransportLegs
                    .Count(l => l.ShipmentId == s.Id && l.Status == InventoryTransportLegStatus.Received)
            })
            .Take(InsightRowLimit)
            .ToListAsync(ct);

        vm.ActiveShipmentProgress = rows
            .Select(row =>
            {
                var total = row.QuantityMt > 0m ? row.QuantityMt : row.AllocatedMt;
                var moved = row.MovedMt ?? 0m;
                var percent = total <= 0m ? 0m : Math.Clamp(moved / total * 100m, 0m, 100m);
                var hasVessel = !string.IsNullOrWhiteSpace(row.VesselName);
                var tone = ResolveProgressTone(percent);

                // برچسب از وضعیت واقعی حمل می‌آید؛ حملِ InTransit یعنی «در مسیر» و
                // حملِ Loaded یعنی بار رسیده و در نوبت تخلیه است.
                var statusText = row.InTransitLegCount > 0
                    ? Text("در مسیر", "In transit")
                    : row.LoadedLegCount > 0
                        ? Text("در حال تخلیه", "Discharging")
                        : row.ReceivedLegCount > 0
                            ? Text("تحویل شده", "Received")
                            : Text("در انتظار حمل", "Awaiting transport");

                return new DashboardShipmentProgressViewModel
                {
                    ShipmentId = row.Id,
                    ShipmentCode = row.ShipmentCode,
                    SubtitleText = hasVessel
                        ? Text($"کشتی {row.VesselName}", $"Vessel {row.VesselName}")
                        : row.ProductName,
                    IconClass = hasVessel ? "bi-water" : "bi-box-seam",
                    AvatarPath = hasVessel
                        ? "/images/stat-cards/ref-blue/ops-loading.webp"
                        : "/images/stat-cards/ref-blue/ops-transport.webp",
                    LoadedMt = moved,
                    TotalMt = total,
                    ProgressPercent = percent,
                    StatusText = statusText,
                    // رنگ برچسب دقیقاً هم‌رنگِ نوار پیشرفت است (مطابق تصویر مرجع).
                    StatusClass = tone switch
                    {
                        "is-critical" => "is-danger",
                        "is-warning" => "is-pending",
                        _ => "is-active"
                    },
                    ProgressTone = tone
                };
            })
            .ToList();
    }

    // نسخه یک رفت‌وبرگشت: همان ۱۸ شمارش/جمع مسیر LINQ پایین، در یک SELECT.
    // معناشناسی هر ستون باید دقیقاً با کوئری LINQ متناظر یکسان بماند
    // (اثبات در DashboardServicePostgresTests روی PostgreSQL واقعی).
    private async Task PopulateOperationalStatsWithSingleRoundTripAsync(
        DashboardViewModel vm,
        DateTime todayUtc,
        DateTime tomorrowUtc,
        DateTime monthStartUtc,
        CancellationToken ct)
    {
        var row = await _db.Database.SqlQuery<DashboardOperationalStatsRow>($"""
            SELECT
                (SELECT COUNT(*)::int FROM "InventoryTransportLegs" WHERE "Status" = {(int)InventoryTransportLegStatus.InTransit}) AS "ShipmentsInTransitCount",
                (SELECT COALESCE(SUM("TotalUsd"), 0) FROM "SalesTransactions" WHERE NOT "IsCancelled" AND "SaleDate" >= {todayUtc} AND "SaleDate" < {tomorrowUtc}) AS "TodaySalesUsd",
                (SELECT COUNT(*)::int FROM "SalesTransactions" WHERE NOT "IsCancelled" AND "SaleDate" >= {todayUtc} AND "SaleDate" < {tomorrowUtc}) AS "TodaySalesCount",
                (SELECT COUNT(*)::int FROM "Sarrafs" WHERE "IsActive") AS "ActiveSarrafCount",
                (SELECT COUNT(*)::int FROM "LoadingRegisters" WHERE "LoadingDate" >= {todayUtc} AND "LoadingDate" < {tomorrowUtc}) AS "TodayLoadingCount",
                (SELECT COUNT(*)::int FROM "TruckDispatches" d WHERE d."Status" <> {(int)DispatchStatus.Cancelled} AND d."DispatchDate" >= {todayUtc} AND d."DispatchDate" < {tomorrowUtc} AND NOT EXISTS (SELECT 1 FROM "InventoryTransportLegAllocations" a WHERE a."SourceTransportReceiptId" = d."InventoryTransportReceiptId")) AS "TodayDispatchCount",
                (SELECT COUNT(*)::int FROM "LoadingRegisters" l WHERE NOT EXISTS (SELECT 1 FROM "LoadingReceipts" r WHERE r."LoadingRegisterId" = l."Id" AND NOT r."IsCancelled")) AS "LoadingsWithoutReceiptCount",
                (SELECT COUNT(*)::int FROM "LoadingReceipts" r WHERE NOT r."IsCancelled" AND NOT EXISTS (SELECT 1 FROM "LoadingReceiptAllocations" a WHERE a."LoadingReceiptId" = r."Id")) AS "ReceiptsWithoutAllocationCount",
                (SELECT COUNT(*)::int FROM "LoadingRegisters" l WHERE NOT EXISTS (SELECT 1 FROM "CustomsDeclarations" c WHERE c."LoadingRegisterId" = l."Id")) AS "LoadingsWithoutCustomsCount",
                (SELECT COUNT(*)::int FROM "SalesTransactions" s WHERE NOT "IsCancelled" AND NOT EXISTS (SELECT 1 FROM "PaymentTransactions" p WHERE p."SalesTransactionId" = s."Id") AND NOT EXISTS (SELECT 1 FROM "CustomerPaymentAllocationApplications" a WHERE a."SalesTransactionId" = s."Id" AND a."Status" = {(int)CustomerPaymentAllocationApplicationStatus.Active})) AS "SalesWithoutPaymentCount",
                (SELECT COUNT(*)::int FROM "Contracts" WHERE "Status" = {(int)ContractStatus.Active} AND "UnitPriceUsd" IS NULL AND "ManualFinalPriceUsd" IS NULL AND "PlattsManualPriceUsd" IS NULL) AS "ContractsWithoutFinalPriceCount",
                (SELECT COUNT(*)::int FROM "LossEvents" WHERE NOT "IsCancelled") AS "ShortageCount",
                (SELECT COUNT(*)::int FROM "LossEvents" WHERE NOT "IsCancelled" AND "ChargeableLossMt" > 0) AS "ExcessShortageCount",
                (SELECT COUNT(*)::int FROM "SarrafSettlements" WHERE "DifferenceType" <> {(int)SarrafSettlementDifferenceType.None}) AS "SarrafRateDiffCount"
            """).SingleAsync(ct);

        vm.ShipmentsInTransitCount = row.ShipmentsInTransitCount;
        vm.TodaySalesUsd = row.TodaySalesUsd;
        vm.TodaySalesCount = row.TodaySalesCount;
        vm.ActiveSarrafCount = row.ActiveSarrafCount;
        vm.TodayLoadingCount = row.TodayLoadingCount;
        vm.TodayDispatchCount = row.TodayDispatchCount;
        vm.LoadingsWithoutReceiptCount = row.LoadingsWithoutReceiptCount;
        vm.ReceiptsWithoutAllocationCount = row.ReceiptsWithoutAllocationCount;
        vm.LoadingsWithoutCustomsCount = row.LoadingsWithoutCustomsCount;
        vm.SalesWithoutPaymentCount = row.SalesWithoutPaymentCount;
        vm.ContractsWithoutFinalPriceCount = row.ContractsWithoutFinalPriceCount;
        vm.ShortageCount = row.ShortageCount;
        vm.ExcessShortageCount = row.ExcessShortageCount;
        vm.SarrafRateDiffCount = row.SarrafRateDiffCount;
    }

    private async Task PopulateOperationalStatsWithLinqAsync(
        DashboardViewModel vm,
        DateTime todayUtc,
        DateTime tomorrowUtc,
        DateTime monthStartUtc,
        CancellationToken ct)
    {
        vm.ShipmentsInTransitCount = await _db.InventoryTransportLegs.AsNoTracking()
            .CountAsync(l => l.Status == InventoryTransportLegStatus.InTransit, ct);

        vm.TodaySalesUsd = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.SaleDate >= todayUtc && s.SaleDate < tomorrowUtc)
            .SumAsync(s => (decimal?)s.TotalUsd, ct) ?? 0m;
        vm.TodaySalesCount = await _db.SalesTransactions.AsNoTracking()
            .CountAsync(s => !s.IsCancelled && s.SaleDate >= todayUtc && s.SaleDate < tomorrowUtc, ct);

        vm.ActiveSarrafCount = await _db.Sarrafs.AsNoTracking().CountAsync(s => s.IsActive, ct);

        vm.TodayLoadingCount = await _db.LoadingRegisters.AsNoTracking()
            .CountAsync(l => l.LoadingDate >= todayUtc && l.LoadingDate < tomorrowUtc, ct);
        // رکورد سازگاریِ ادامهٔ حمل ارسال مستقل نیست؛ شمردنش آمار ارسال را متورم می‌کند.
        var continuedReceiptIds = TransportChainProjection.ContinuedTransferReceiptIds(_db);
        vm.TodayDispatchCount = await _db.TruckDispatches.AsNoTracking()
            .CountAsync(d => d.Status != DispatchStatus.Cancelled && d.DispatchDate >= todayUtc && d.DispatchDate < tomorrowUtc
                && !(d.InventoryTransportReceiptId != null && continuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)), ct);

        vm.LoadingsWithoutReceiptCount = await _db.LoadingRegisters.AsNoTracking()
            .CountAsync(l => !_db.LoadingReceipts.Any(r => r.LoadingRegisterId == l.Id && !r.IsCancelled), ct);
        vm.ReceiptsWithoutAllocationCount = await _db.LoadingReceipts.AsNoTracking()
            .CountAsync(r => !r.IsCancelled && !_db.LoadingReceiptAllocations.Any(a => a.LoadingReceiptId == r.Id), ct);
        vm.LoadingsWithoutCustomsCount = await _db.LoadingRegisters.AsNoTracking()
            .CountAsync(l => !_db.CustomsDeclarations.Any(c => c.LoadingRegisterId == l.Id), ct);
        vm.SalesWithoutPaymentCount = await _db.SalesTransactions.AsNoTracking()
            .CountAsync(s => !s.IsCancelled
                && !_db.PaymentTransactions.Any(p => p.SalesTransactionId == s.Id)
                && !_db.CustomerPaymentAllocationApplications.Any(a =>
                    a.SalesTransactionId == s.Id && a.Status == CustomerPaymentAllocationApplicationStatus.Active), ct);

        vm.ContractsWithoutFinalPriceCount = await _db.Contracts.AsNoTracking()
            .CountAsync(c => c.Status == ContractStatus.Active
                && c.UnitPriceUsd == null
                && c.ManualFinalPriceUsd == null
                && c.PlattsManualPriceUsd == null, ct);

        vm.ShortageCount = await _db.LossEvents.AsNoTracking().CountAsync(e => !e.IsCancelled, ct);
        vm.ExcessShortageCount = await _db.LossEvents.AsNoTracking()
            .CountAsync(e => !e.IsCancelled && e.ChargeableLossMt > 0m, ct);

        vm.SarrafRateDiffCount = await _db.SarrafSettlements.AsNoTracking()
            .CountAsync(x => x.DifferenceType != SarrafSettlementDifferenceType.None, ct);
    }

    private async Task PopulateCountsAndTotalsAsync(
        DashboardViewModel vm,
        DateTime todayUtc,
        DateTime currentWeekStart,
        DateTime previousWeekStart,
        DateTime nextWeekStart,
        CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            await PopulateCountsAndTotalsWithSingleRoundTripAsync(
                vm,
                todayUtc,
                currentWeekStart,
                previousWeekStart,
                nextWeekStart,
                ct);
            return;
        }

        var recentDispatchFromUtc = todayUtc.AddDays(-RecentDispatchDays);

        vm.ProductCount = await _db.Products.AsNoTracking().CountAsync(ct);
        vm.ActiveContractCount = await _db.Contracts.AsNoTracking().CountAsync(c => c.Status == ContractStatus.Active, ct);
        vm.TotalContractCount = await _db.Contracts.AsNoTracking().CountAsync(ct);
        vm.LoadingCount = await _db.LoadingRegisters.AsNoTracking().CountAsync(ct);
        vm.LoadingReceiptCount = await _db.LoadingReceipts.AsNoTracking().CountAsync(r => !r.IsCancelled, ct);
        vm.SalesCount = await _db.SalesTransactions.AsNoTracking().CountAsync(s => !s.IsCancelled, ct);
        vm.ShipmentCount = await _db.Shipments.AsNoTracking().CountAsync(ct);
        var recentContinuedReceiptIds = TransportChainProjection.ContinuedTransferReceiptIds(_db);
        vm.RecentDispatchCount = await _db.TruckDispatches.AsNoTracking()
            .CountAsync(d => d.Status != DispatchStatus.Cancelled && d.DispatchDate >= recentDispatchFromUtc
                && !(d.InventoryTransportReceiptId != null && recentContinuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)), ct);

        vm.TotalSalesUsd = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled)
            .SumAsync(s => (decimal?)s.TotalUsd, ct) ?? 0m;
        vm.TotalExpensesUsd = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled)
            .SumAsync(e => (decimal?)e.AmountUsd, ct) ?? 0m;
        vm.PurchaseReserveUsd = await _db.LoadingRegisters.AsNoTracking()
            .Where(l => l.LoadingPriceUsd.HasValue && l.LoadingPriceUsd.Value > 0m)
            .SumAsync(l => (decimal?)(l.LoadedQuantityMt * l.LoadingPriceUsd!.Value), ct) ?? 0m;

        var currentSalesUsd = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.SaleDate >= currentWeekStart && s.SaleDate < nextWeekStart)
            .SumAsync(s => (decimal?)s.TotalUsd, ct) ?? 0m;
        var previousSalesUsd = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.SaleDate >= previousWeekStart && s.SaleDate < currentWeekStart)
            .SumAsync(s => (decimal?)s.TotalUsd, ct) ?? 0m;
        var currentExpensesUsd = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled && e.ExpenseDate >= currentWeekStart && e.ExpenseDate < nextWeekStart)
            .SumAsync(e => (decimal?)e.AmountUsd, ct) ?? 0m;
        var previousExpensesUsd = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled && e.ExpenseDate >= previousWeekStart && e.ExpenseDate < currentWeekStart)
            .SumAsync(e => (decimal?)e.AmountUsd, ct) ?? 0m;

        var currentLoadingReceipts = await _db.LoadingReceipts.AsNoTracking()
            .CountAsync(r => !r.IsCancelled && r.ReceiptDate >= currentWeekStart && r.ReceiptDate < nextWeekStart, ct);
        var previousLoadingReceipts = await _db.LoadingReceipts.AsNoTracking()
            .CountAsync(r => !r.IsCancelled && r.ReceiptDate >= previousWeekStart && r.ReceiptDate < currentWeekStart, ct);
        var currentLedgerEntries = await _db.LedgerEntries.AsNoTracking()
            .CountAsync(l => l.EntryDate >= currentWeekStart && l.EntryDate < nextWeekStart, ct);
        var previousLedgerEntries = await _db.LedgerEntries.AsNoTracking()
            .CountAsync(l => l.EntryDate >= previousWeekStart && l.EntryDate < currentWeekStart, ct);

        vm.TotalSalesWeekChangePercent = CalculatePercentChange(currentSalesUsd, previousSalesUsd);
        vm.TotalExpensesWeekChangePercent = CalculatePercentChange(currentExpensesUsd, previousExpensesUsd);
        vm.LoadingReceiptsWeekChangePercent = CalculatePercentChange(currentLoadingReceipts, previousLoadingReceipts);
        vm.LedgerEntriesWeekChangePercent = CalculatePercentChange(currentLedgerEntries, previousLedgerEntries);
    }

    private async Task PopulateCountsAndTotalsWithSingleRoundTripAsync(
        DashboardViewModel vm,
        DateTime todayUtc,
        DateTime currentWeekStart,
        DateTime previousWeekStart,
        DateTime nextWeekStart,
        CancellationToken ct)
    {
        var recentDispatchFromUtc = todayUtc.AddDays(-RecentDispatchDays);
        var row = await _db.Database.SqlQuery<DashboardCountsAndTotalsRow>($"""
            SELECT
                (SELECT COUNT(*)::int FROM "Products") AS "ProductCount",
                (SELECT COUNT(*)::int FROM "Contracts" WHERE "Status" = {(int)ContractStatus.Active}) AS "ActiveContractCount",
                (SELECT COUNT(*)::int FROM "Contracts") AS "TotalContractCount",
                (SELECT COUNT(*)::int FROM "LoadingRegisters") AS "LoadingCount",
                (SELECT COUNT(*)::int FROM "LoadingReceipts" WHERE NOT "IsCancelled") AS "LoadingReceiptCount",
                (SELECT COUNT(*)::int FROM "SalesTransactions" WHERE NOT "IsCancelled") AS "SalesCount",
                (SELECT COUNT(*)::int FROM "Shipments") AS "ShipmentCount",
                (SELECT COUNT(*)::int FROM "TruckDispatches" d WHERE d."Status" <> {(int)DispatchStatus.Cancelled} AND d."DispatchDate" >= {recentDispatchFromUtc} AND NOT EXISTS (SELECT 1 FROM "InventoryTransportLegAllocations" a WHERE a."SourceTransportReceiptId" = d."InventoryTransportReceiptId")) AS "RecentDispatchCount",
                (SELECT COALESCE(SUM("TotalUsd"), 0) FROM "SalesTransactions" WHERE NOT "IsCancelled") AS "TotalSalesUsd",
                (SELECT COALESCE(SUM("AmountUsd"), 0) FROM "ExpenseTransactions" WHERE NOT "IsCancelled") AS "TotalExpensesUsd",
                (SELECT COALESCE(SUM("LoadedQuantityMt" * "LoadingPriceUsd"), 0) FROM "LoadingRegisters" WHERE "LoadingPriceUsd" IS NOT NULL AND "LoadingPriceUsd" > 0) AS "PurchaseReserveUsd",
                (SELECT COALESCE(SUM("TotalUsd"), 0) FROM "SalesTransactions" WHERE NOT "IsCancelled" AND "SaleDate" >= {currentWeekStart} AND "SaleDate" < {nextWeekStart}) AS "CurrentSalesUsd",
                (SELECT COALESCE(SUM("TotalUsd"), 0) FROM "SalesTransactions" WHERE NOT "IsCancelled" AND "SaleDate" >= {previousWeekStart} AND "SaleDate" < {currentWeekStart}) AS "PreviousSalesUsd",
                (SELECT COALESCE(SUM("AmountUsd"), 0) FROM "ExpenseTransactions" WHERE NOT "IsCancelled" AND "ExpenseDate" >= {currentWeekStart} AND "ExpenseDate" < {nextWeekStart}) AS "CurrentExpensesUsd",
                (SELECT COALESCE(SUM("AmountUsd"), 0) FROM "ExpenseTransactions" WHERE NOT "IsCancelled" AND "ExpenseDate" >= {previousWeekStart} AND "ExpenseDate" < {currentWeekStart}) AS "PreviousExpensesUsd",
                (SELECT COUNT(*)::int FROM "LoadingReceipts" WHERE NOT "IsCancelled" AND "ReceiptDate" >= {currentWeekStart} AND "ReceiptDate" < {nextWeekStart}) AS "CurrentLoadingReceipts",
                (SELECT COUNT(*)::int FROM "LoadingReceipts" WHERE NOT "IsCancelled" AND "ReceiptDate" >= {previousWeekStart} AND "ReceiptDate" < {currentWeekStart}) AS "PreviousLoadingReceipts",
                (SELECT COUNT(*)::int FROM "LedgerEntries" WHERE "EntryDate" >= {currentWeekStart} AND "EntryDate" < {nextWeekStart}) AS "CurrentLedgerEntries",
                (SELECT COUNT(*)::int FROM "LedgerEntries" WHERE "EntryDate" >= {previousWeekStart} AND "EntryDate" < {currentWeekStart}) AS "PreviousLedgerEntries"
            """).SingleAsync(ct);

        vm.ProductCount = row.ProductCount;
        vm.ActiveContractCount = row.ActiveContractCount;
        vm.TotalContractCount = row.TotalContractCount;
        vm.LoadingCount = row.LoadingCount;
        vm.LoadingReceiptCount = row.LoadingReceiptCount;
        vm.SalesCount = row.SalesCount;
        vm.ShipmentCount = row.ShipmentCount;
        vm.RecentDispatchCount = row.RecentDispatchCount;
        vm.TotalSalesUsd = row.TotalSalesUsd;
        vm.TotalExpensesUsd = row.TotalExpensesUsd;
        vm.PurchaseReserveUsd = row.PurchaseReserveUsd;
        vm.TotalSalesWeekChangePercent = CalculatePercentChange(row.CurrentSalesUsd, row.PreviousSalesUsd);
        vm.TotalExpensesWeekChangePercent = CalculatePercentChange(row.CurrentExpensesUsd, row.PreviousExpensesUsd);
        vm.LoadingReceiptsWeekChangePercent = CalculatePercentChange(row.CurrentLoadingReceipts, row.PreviousLoadingReceipts);
        vm.LedgerEntriesWeekChangePercent = CalculatePercentChange(row.CurrentLedgerEntries, row.PreviousLedgerEntries);
    }

    private async Task PopulateInventoryAsync(DashboardViewModel vm, CancellationToken ct)
    {
        var stockRows = await _db.InventoryMovements.AsNoTracking()
            .GroupBy(m => new { m.ProductId, m.TerminalId, m.ContractId })
            .Select(g => new StockAggregateRow
            {
                ProductId = g.Key.ProductId,
                TerminalId = g.Key.TerminalId,
                ContractId = g.Key.ContractId,
                FreeQuantityMt = g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m),
                LastMovementDate = g.Max(m => m.MovementDate),
                MovementCount = g.Count()
            })
            .ToListAsync(ct);

        vm.TerminalStockMt = stockRows.Sum(r => r.FreeQuantityMt);

        var productIds = stockRows.Select(r => r.ProductId).Distinct().ToArray();
        var terminalIds = stockRows.Select(r => r.TerminalId).Distinct().ToArray();
        var contractIds = stockRows.Where(r => r.ContractId.HasValue).Select(r => r.ContractId!.Value).Distinct().ToArray();

        // نام جنس/ترمینال/قرارداد فقط برای متن هشدارها لازم است؛ هر سه در یک رفت‌وبرگشت
        // (UNION ALL) خوانده می‌شوند، نه سه query جدا. همان ردیف‌ها و همان مقادیر.
        var lookupRows = await _db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { Kind = 1, p.Id, Code = (string?)p.Code, Name = (string?)p.Name })
            .Concat(_db.Terminals.AsNoTracking()
                .Where(t => terminalIds.Contains(t.Id))
                .Select(t => new { Kind = 2, t.Id, Code = (string?)t.Code, Name = (string?)t.Name }))
            .Concat(_db.Contracts.AsNoTracking()
                .Where(c => contractIds.Contains(c.Id))
                .Select(c => new { Kind = 3, c.Id, Code = (string?)c.ContractNumber, Name = (string?)null }))
            .ToListAsync(ct);

        var products = lookupRows
            .Where(row => row.Kind == 1)
            .ToDictionary(row => row.Id, row => new { row.Code, row.Name });
        var terminals = lookupRows
            .Where(row => row.Kind == 2)
            .ToDictionary(row => row.Id, row => new { row.Code, row.Name });
        var contracts = lookupRows
            .Where(row => row.Kind == 3)
            .ToDictionary(row => row.Id, row => new { ContractNumber = row.Code });

        vm.LowStockAlerts = stockRows
            .Where(r => r.FreeQuantityMt <= LowStockThresholdMt)
            .OrderBy(r => r.FreeQuantityMt)
            .ThenBy(r => products.TryGetValue(r.ProductId, out var product) ? product.Code : r.ProductId.ToString(CultureInfo.InvariantCulture))
            .Take(AlertLimit)
            .Select(r =>
            {
                products.TryGetValue(r.ProductId, out var product);
                terminals.TryGetValue(r.TerminalId, out var terminal);
                var reference = r.ContractId.HasValue && contracts.TryGetValue(r.ContractId.Value, out var contract)
                    ? contract.ContractNumber
                    : null;

                return new DashboardAlertViewModel
                {
                    Title = Text("موجودی کم", "Low stock"),
                    Message = Text(
                        $"موجودی آزاد {FormatProduct(product?.Code, product?.Name)} در {FormatTerminal(terminal?.Code, terminal?.Name, r.TerminalId)} برابر {r.FreeQuantityMt:N4} MT است.",
                        $"Free stock for {FormatProduct(product?.Code, product?.Name)} at {FormatTerminal(terminal?.Code, terminal?.Name, r.TerminalId)} is {r.FreeQuantityMt:N4} MT."),
                    Severity = r.FreeQuantityMt < 0m ? "danger" : "warning",
                    Reference = reference
                };
            })
            .ToList();
    }

    private async Task PopulateAlertsAsync(DashboardViewModel vm, DateTime todayUtc, CancellationToken ct)
    {
        var endingSoonUntilUtc = todayUtc.AddDays(EndingSoonDays);

        vm.ContractsEndingSoonAlerts = await BuildContractsEndingSoonAlertsAsync(todayUtc, endingSoonUntilUtc, ct);

        // هر دو هشدار محموله از یک رفت‌وبرگشت می‌آیند؛ هر طرف Top-N خودش را در SQL می‌گیرد،
        // پس مجموعه و ترتیب هر فهرست دقیقاً مثل دو query قبلی است.
        var shipmentAlertRows = await _db.Shipments.AsNoTracking()
            .Where(s => !_db.SalesTransactions.Any(t => t.ShipmentId == s.Id && !t.IsCancelled))
            .OrderByDescending(s => s.Id)
            .Select(s => new { Kind = 1, s.Id, s.ShipmentCode, s.QuantityMt })
            .Take(AlertLimit)
            .Concat(_db.Shipments.AsNoTracking()
                .Where(s => !_db.ExpenseTransactions.Any(e => e.ShipmentId == s.Id && !e.IsCancelled))
                .OrderByDescending(s => s.Id)
                .Select(s => new { Kind = 2, s.Id, s.ShipmentCode, s.QuantityMt })
                .Take(AlertLimit))
            .ToListAsync(ct);

        vm.ShipmentsWithoutSalesAlerts = shipmentAlertRows
            .Where(row => row.Kind == 1)
            .OrderByDescending(row => row.Id)
            .Select(row => new DashboardAlertViewModel
            {
                Title = Text("محموله بدون فروش", "Shipment without sale"),
                Message = Text(
                    $"برای محموله {row.ShipmentCode} با مقدار {row.QuantityMt:N4} MT هیچ فروش فعالی ثبت نشده است.",
                    $"Shipment {row.ShipmentCode} with {row.QuantityMt:N4} MT has no active sale transaction."),
                Severity = "warning",
                Reference = row.ShipmentCode
            })
            .ToList();

        vm.ShipmentsWithoutExpensesAlerts = shipmentAlertRows
            .Where(row => row.Kind == 2)
            .OrderByDescending(row => row.Id)
            .Select(row => new DashboardAlertViewModel
            {
                Title = Text("محموله بدون هزینه", "Shipment without expense"),
                Message = Text(
                    $"برای محموله {row.ShipmentCode} با مقدار {row.QuantityMt:N4} MT هیچ هزینه فعالی ثبت نشده است.",
                    $"Shipment {row.ShipmentCode} with {row.QuantityMt:N4} MT has no active expense transaction."),
                Severity = "warning",
                Reference = row.ShipmentCode
            })
            .ToList();
    }

    private async Task PopulateMarketSeriesAsync(DashboardViewModel vm, DateTime todayUtc, CancellationToken ct)
    {
        // این دو MaxAsync عمداً جدا مانده‌اند. هرکدام مستقیماً روی partial index
        // (SaleDate/ExpenseDate با شرط «لغونشده») می‌نشیند و در حد میکروثانیه جواب می‌دهد؛
        // یکی‌کردنشان با UNION ALL همان index را از کار می‌اندازد و روی ۶۰٬۰۰۰ ردیف
        // اندازه‌گیری‌شده ۲٫۵ms را به ۹۵ms می‌برد. یک رفت‌وبرگشت کمتر ارزش آن را ندارد.
        var latestSaleDate = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled)
            .Select(s => (DateTime?)s.SaleDate)
            .MaxAsync(ct);
        var latestExpenseDate = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled)
            .Select(e => (DateTime?)e.ExpenseDate)
            .MaxAsync(ct);

        var anchorDate = new[] { latestSaleDate, latestExpenseDate }
            .Where(value => value.HasValue)
            .Select(value => value!.Value.Date)
            .DefaultIfEmpty(todayUtc)
            .Max();
        var anchorWeekStart = StartOfWeek(anchorDate, DayOfWeek.Monday);
        var firstWeekStart = anchorWeekStart.AddDays(-(MarketWeekSpan - 1) * 7);
        var lastWeekEnd = anchorWeekStart.AddDays(7);
        var weekStarts = Enumerable.Range(0, MarketWeekSpan)
            .Select(index => firstWeekStart.AddDays(index * 7))
            .ToList();

        // هر دو سری روزانه در یک رفت‌وبرگشت جمع‌بندی می‌شوند؛ قبلاً دو GroupBy جدا بود.
        // گروه‌بندی همچنان در PostgreSQL انجام می‌شود و مقدار هر روز عوض نمی‌شود.
        var seriesRows = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.SaleDate >= firstWeekStart && s.SaleDate < lastWeekEnd)
            .Select(s => new { Kind = 1, Date = s.SaleDate.Date, Amount = (decimal?)s.TotalUsd })
            .Concat(_db.ExpenseTransactions.AsNoTracking()
                .Where(e => !e.IsCancelled && e.ExpenseDate >= firstWeekStart && e.ExpenseDate < lastWeekEnd)
                .Select(e => new { Kind = 2, Date = e.ExpenseDate.Date, Amount = (decimal?)e.AmountUsd }))
            .GroupBy(row => new { row.Kind, row.Date })
            .Select(g => new
            {
                g.Key.Kind,
                g.Key.Date,
                Total = g.Sum(row => row.Amount) ?? 0m
            })
            .ToListAsync(ct);

        var salesRows = seriesRows
            .Where(row => row.Kind == 1)
            .Select(row => new { row.Date, TotalUsd = row.Total })
            .ToList();
        var expenseRows = seriesRows
            .Where(row => row.Kind == 2)
            .Select(row => new { row.Date, AmountUsd = row.Total })
            .ToList();

        vm.MarketLabels = weekStarts
            .Select(weekStart => weekStart.ToCalendarString("MM/dd", CultureInfo.InvariantCulture))
            .ToList();
        vm.MarketSalesSeries = weekStarts
            .Select(weekStart => salesRows
                .Where(s => s.Date >= weekStart && s.Date < weekStart.AddDays(7))
                .Sum(s => s.TotalUsd))
            .ToList();
        vm.MarketExpenseSeries = weekStarts
            .Select(weekStart => expenseRows
                .Where(e => e.Date >= weekStart && e.Date < weekStart.AddDays(7))
                .Sum(e => e.AmountUsd))
            .ToList();
        vm.MarketSubtitle = Text(
            "روند درآمد و هزینه بر اساس تراکنش های ثبت شده",
            "Revenue and expense movement from posted transactions");
        vm.MarketRangeLabel = Text(
            $"{firstWeekStart:MM/dd} تا {anchorWeekStart:MM/dd}",
            $"{firstWeekStart:MMM dd} - {anchorWeekStart:MMM dd}");
    }

    private async Task PopulateRecentActivitiesAsync(DashboardViewModel vm, CancellationToken ct)
    {
        var sales = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.SaleDate,
                s.QuantityMt,
                s.TotalUsd,
                ProductCode = s.Product == null ? "" : s.Product.Code
            })
            .Take(DashboardRowLimit)
            .ToListAsync(ct);

        var loadings = await _db.LoadingRegisters.AsNoTracking()
            .OrderByDescending(l => l.LoadingDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.BillOfLadingNumber,
                l.RwbNo,
                l.LoadingDate,
                l.LoadedQuantityMt,
                ProductCode = l.Product == null ? "" : l.Product.Code
            })
            .Take(DashboardRowLimit)
            .ToListAsync(ct);

        var activityContinuedReceiptIds = TransportChainProjection.ContinuedTransferReceiptIds(_db);
        var dispatches = await _db.TruckDispatches.AsNoTracking()
            .Where(d => d.Status != DispatchStatus.Cancelled
                && !(d.InventoryTransportReceiptId != null && activityContinuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)))
            .OrderByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id,
                d.DispatchDate,
                d.LoadedQuantityMt,
                d.Status,
                ProductCode = d.Product == null ? "" : d.Product.Code
            })
            .Take(DashboardRowLimit)
            .ToListAsync(ct);

        var expenses = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.ExpenseDate,
                e.AmountUsd,
                ExpenseCode = e.ExpenseType == null ? "" : e.ExpenseType.Code
            })
            .Take(DashboardRowLimit)
            .ToListAsync(ct);

        vm.RecentActivities = sales
            .Select(s => new ActivitySeed(
                s.SaleDate,
                new DashboardActivityViewModel
                {
                    DirectionClass = "is-sell",
                    DirectionIcon = "bi-arrow-up-left",
                    ProductIcon = "bi-fuel-pump-fill",
                    Name = $"{Text("فروش", "Sale")} {FirstNonEmpty(s.InvoiceNumber, Text($"#{s.Id}", $"INV-{s.Id}"))} {DisplayCode(s.ProductCode)}".Trim(),
                    TimeText = FormatActivityTime(s.SaleDate),
                    Amount = $"-{s.QuantityMt:N2} MT",
                    AmountClass = "is-negative",
                    Status = Text("تکمیل شده", "Completed"),
                    StatusClass = "status-badge-success"
                }))
            .Concat(loadings.Select(l =>
            {
                var reference = FirstNonEmpty(l.BillOfLadingNumber, l.RwbNo, Text($"#{l.Id}", $"Loading #{l.Id}"));
                return new ActivitySeed(
                    l.LoadingDate,
                    new DashboardActivityViewModel
                    {
                        DirectionClass = "is-buy",
                        DirectionIcon = "bi-arrow-down-right",
                        ProductIcon = "bi-droplet-fill",
                        Name = $"{Text("بارگیری", "Loading")} {reference} {DisplayCode(l.ProductCode)}".Trim(),
                        TimeText = FormatActivityTime(l.LoadingDate),
                        Amount = $"+{l.LoadedQuantityMt:N2} MT",
                        AmountClass = "is-positive",
                        Status = Text("تکمیل شده", "Completed"),
                        StatusClass = "status-badge-success"
                    });
            }))
            .Concat(dispatches.Select(d => new ActivitySeed(
                d.DispatchDate,
                new DashboardActivityViewModel
                {
                    DirectionClass = "is-sell",
                    DirectionIcon = "bi-arrow-up-left",
                    ProductIcon = "bi-truck-front-fill",
                    Name = $"{Text("ارسال", "Dispatch")} #{d.Id} {DisplayCode(d.ProductCode)}".Trim(),
                    TimeText = FormatActivityTime(d.DispatchDate),
                    Amount = $"-{d.LoadedQuantityMt:N2} MT",
                    AmountClass = "is-negative",
                    Status = FormatDispatchStatus(d.Status),
                    StatusClass = "status-badge-neutral"
                })))
            .Concat(expenses.Select(e => new ActivitySeed(
                e.ExpenseDate,
                new DashboardActivityViewModel
                {
                    DirectionClass = "is-sell",
                    DirectionIcon = "bi-arrow-up-left",
                    ProductIcon = "bi-clipboard2-pulse-fill",
                    Name = $"{Text("هزینه", "Expense")} {FirstNonEmpty(e.ExpenseCode, Text($"#{e.Id}", $"EXP-{e.Id}"))}".Trim(),
                    TimeText = FormatActivityTime(e.ExpenseDate),
                    Amount = $"-${e.AmountUsd:N0}",
                    AmountClass = "is-negative",
                    Status = Text("ثبت شده", "Posted"),
                    StatusClass = "status-badge-neutral"
                })))
            .OrderByDescending(row => row.Date)
            .ThenBy(row => row.Item.Name)
            .Take(DashboardRowLimit)
            .Select(row => row.Item)
            .ToList();
    }

    private async Task PopulateOrderPanelsAsync(DashboardViewModel vm, CancellationToken ct)
    {
        var outboundRows = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.UnitPriceUsd,
                s.QuantityMt,
                s.TotalUsd
            })
            .Take(DashboardRowLimit)
            .ToListAsync(ct);

        var inboundRows = await _db.LoadingRegisters.AsNoTracking()
            .OrderByDescending(l => l.LoadingDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.BillOfLadingNumber,
                l.RwbNo,
                l.LoadedQuantityMt,
                l.LoadingPriceUsd,
                ContractUnitPriceUsd = l.Contract == null ? null : l.Contract.UnitPriceUsd
            })
            .Take(DashboardRowLimit)
            .ToListAsync(ct);

        vm.OutboundOrderPanel = new DashboardOrderPanelViewModel
        {
            Title = Text("فروش های اخیر", "Recent Sales"),
            TokenLabel = Text("فروش", "Sales"),
            TokenIcon = "bi bi-droplet-fill",
            TokenClass = "",
            Rows = outboundRows
                .Select(row => new DashboardOrderRowViewModel
                {
                    Reference = FirstNonEmpty(row.InvoiceNumber, Text($"#{row.Id}", $"INV-{row.Id}")),
                    Price = row.UnitPriceUsd.ToString("N2", CultureInfo.InvariantCulture),
                    Amount = row.QuantityMt.ToString("N2", CultureInfo.InvariantCulture),
                    Total = $"${row.TotalUsd:N2}"
                })
                .ToList()
        };

        vm.InboundOrderPanel = new DashboardOrderPanelViewModel
        {
            Title = Text("بارگیری های اخیر", "Recent Loading"),
            TokenLabel = Text("بارگیری", "Loading"),
            TokenIcon = "bi bi-coin",
            TokenClass = "is-orange",
            Rows = inboundRows
                .Select(row =>
                {
                    var price = row.LoadingPriceUsd ?? row.ContractUnitPriceUsd ?? 0m;
                    return new DashboardOrderRowViewModel
                    {
                        Reference = FirstNonEmpty(row.BillOfLadingNumber, row.RwbNo, Text($"#{row.Id}", $"Loading #{row.Id}")),
                        Price = price.ToString("N2", CultureInfo.InvariantCulture),
                        Amount = row.LoadedQuantityMt.ToString("N2", CultureInfo.InvariantCulture),
                        Total = $"${price * row.LoadedQuantityMt:N2}"
                    };
                })
                .ToList()
        };
    }

    private async Task<List<DashboardAlertViewModel>> BuildContractsEndingSoonAlertsAsync(
        DateTime todayUtc,
        DateTime endingSoonUntilUtc,
        CancellationToken ct)
    {
        var contracts = await _db.Contracts.AsNoTracking()
            .Where(c => c.Status == ContractStatus.Active
                && c.EndDate.HasValue
                && c.EndDate >= todayUtc
                && c.EndDate <= endingSoonUntilUtc)
            .OrderBy(c => c.EndDate)
            .Select(c => new { c.ContractNumber, c.EndDate })
            .Take(AlertLimit)
            .ToListAsync(ct);

        return contracts
            .Select(c =>
            {
                var daysLeft = c.EndDate!.Value.Date.Subtract(todayUtc).Days;
                return new DashboardAlertViewModel
                {
                    Title = Text("قرارداد رو به پایان", "Contract ending soon"),
                    Message = Text(
                        $"قرارداد {c.ContractNumber} در تاریخ {c.EndDate.ToCalendarString("yyyy/MM/dd")} پایان می یابد ({daysLeft:N0} روز مانده).",
                        $"Contract {c.ContractNumber} ends on {c.EndDate.ToCalendarString("yyyy/MM/dd")} ({daysLeft:N0} days left)."),
                    Severity = daysLeft <= 7 ? "danger" : "warning",
                    Reference = c.ContractNumber
                };
            })
            .ToList();
    }

    private string Text(string fa, string en)
        => UiText.T(_httpContextAccessor.HttpContext, fa, en);

    private static decimal CalculatePercentChange(decimal current, decimal previous)
    {
        if (previous == 0m)
        {
            return current == 0m ? 0m : 100m;
        }

        return (current - previous) / Math.Abs(previous) * 100m;
    }

    private static DateTime StartOfWeek(DateTime value, DayOfWeek startOfWeek)
    {
        var diff = (7 + (value.DayOfWeek - startOfWeek)) % 7;
        return value.AddDays(-diff).Date;
    }

    private string FormatProduct(string? code, string? name)
        => string.IsNullOrWhiteSpace(code)
            ? string.IsNullOrWhiteSpace(name) ? Text("کالا", "product") : name
            : string.IsNullOrWhiteSpace(name) ? code : $"{code} - {name}";

    private string FormatTerminal(string? code, string? name, int terminalId)
        => string.IsNullOrWhiteSpace(code)
            ? string.IsNullOrWhiteSpace(name) ? Text($"ترمینال {terminalId}", $"terminal #{terminalId}") : name
            : string.IsNullOrWhiteSpace(name) ? code : $"{code} - {name}";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string DisplayCode(string? code)
        => string.IsNullOrWhiteSpace(code) ? "" : $"({code})";

    private string FormatActivityTime(DateTime value)
    {
        var elapsed = DateTime.UtcNow - value.ToUniversalTime();
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalDays >= 1
            ? Text($"{(int)elapsed.TotalDays} روز قبل", $"{(int)elapsed.TotalDays}d ago")
            : elapsed.TotalHours >= 1
                ? Text($"{(int)elapsed.TotalHours} ساعت قبل", $"{(int)elapsed.TotalHours}h ago")
                : elapsed.TotalMinutes >= 1
                    ? Text($"{(int)elapsed.TotalMinutes} دقیقه قبل", $"{(int)elapsed.TotalMinutes}m ago")
                    : Text("همین حالا", "Just now");
    }

    private static string ResolveProgressTone(decimal percent)
        => percent < ProgressCriticalPercent
            ? "is-critical"
            : percent < ProgressWarningPercent
                ? "is-warning"
                : "is-normal";

    private string FormatDispatchStatus(DispatchStatus status)
        => status switch
        {
            DispatchStatus.Loaded => Text("بارگیری شده", "Loaded"),
            DispatchStatus.InTransit => Text("در مسیر", "In Transit"),
            DispatchStatus.Delivered => Text("تحویل شده", "Delivered"),
            DispatchStatus.Cancelled => Text("لغو شده", "Cancelled"),
            _ => status.ToString()
        };

    private sealed class StockAggregateRow
    {
        public int ProductId { get; set; }
        public int TerminalId { get; set; }
        public int? ContractId { get; set; }
        public decimal FreeQuantityMt { get; set; }
        public DateTime LastMovementDate { get; set; }
        public int MovementCount { get; set; }
    }

    private sealed record ActivitySeed(DateTime Date, DashboardActivityViewModel Item);

    public sealed class DashboardOperationalStatsRow
    {
        public int ShipmentsInTransitCount { get; set; }
        public decimal TodaySalesUsd { get; set; }
        public int TodaySalesCount { get; set; }
        public int ActiveSarrafCount { get; set; }
        public int TodayLoadingCount { get; set; }
        public int TodayDispatchCount { get; set; }
        public int LoadingsWithoutReceiptCount { get; set; }
        public int ReceiptsWithoutAllocationCount { get; set; }
        public int LoadingsWithoutCustomsCount { get; set; }
        public int SalesWithoutPaymentCount { get; set; }
        public int ContractsWithoutFinalPriceCount { get; set; }
        public int ShortageCount { get; set; }
        public int ExcessShortageCount { get; set; }
        public int SarrafRateDiffCount { get; set; }
    }

    public sealed class DashboardCountsAndTotalsRow
    {
        public int ProductCount { get; set; }
        public int ActiveContractCount { get; set; }
        public int TotalContractCount { get; set; }
        public int LoadingCount { get; set; }
        public int LoadingReceiptCount { get; set; }
        public int SalesCount { get; set; }
        public int ShipmentCount { get; set; }
        public int RecentDispatchCount { get; set; }
        public decimal TotalSalesUsd { get; set; }
        public decimal TotalExpensesUsd { get; set; }
        public decimal PurchaseReserveUsd { get; set; }
        public decimal CurrentSalesUsd { get; set; }
        public decimal PreviousSalesUsd { get; set; }
        public decimal CurrentExpensesUsd { get; set; }
        public decimal PreviousExpensesUsd { get; set; }
        public int CurrentLoadingReceipts { get; set; }
        public int PreviousLoadingReceipts { get; set; }
        public int CurrentLedgerEntries { get; set; }
        public int PreviousLedgerEntries { get; set; }
    }
}
