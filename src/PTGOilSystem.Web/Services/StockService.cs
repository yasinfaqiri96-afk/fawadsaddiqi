using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Services;

public class StockService : IStockService
{
    private readonly ApplicationDbContext _db;

    public StockService(ApplicationDbContext db) => _db = db;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    private static DateTime? NormalizeUtc(DateTime? value)
        => value.HasValue ? NormalizeUtc(value.Value) : null;

    private IQueryable<InventoryMovement> BuildMovementQuery(
        int? productId = null,
        int? terminalId = null,
        int? contractId = null,
        int? inventoryBatchId = null,
        int? storageTankId = null,
        DateTime? asOfUtc = null)
    {
        var query = _db.InventoryMovements.AsNoTracking().AsQueryable();
        var normalizedAsOfUtc = NormalizeUtc(asOfUtc);

        if (productId.HasValue) query = query.Where(m => m.ProductId == productId.Value);
        if (terminalId.HasValue) query = query.Where(m => m.TerminalId == terminalId.Value);
        if (contractId.HasValue)
        {
            var scopedContractId = contractId.Value;
            query = query.Where(m =>
                m.ContractId == scopedContractId
                || (m.ContractId == null
                    && m.LoadingReceipt != null
                    && m.LoadingReceipt.LoadingRegister != null
                    && m.LoadingReceipt.LoadingRegister.ContractId == scopedContractId));
        }
        if (inventoryBatchId.HasValue) query = query.Where(m => m.InventoryBatchId == inventoryBatchId.Value);
        if (storageTankId.HasValue) query = query.Where(m => m.StorageTankId == storageTankId.Value);
        if (normalizedAsOfUtc.HasValue) query = query.Where(m => m.MovementDate <= normalizedAsOfUtc.Value);

        return query;
    }

    private static decimal ToSignedQuantity(MovementDirection direction, decimal quantityMt) => direction switch
    {
        MovementDirection.In => quantityMt,
        MovementDirection.Adjustment => quantityMt,
        MovementDirection.Out => -quantityMt,
        MovementDirection.Transfer => -quantityMt,
        _ => 0m
    };

    private static Task<decimal?> SumSignedQuantityAsync(
        IQueryable<InventoryMovement> query,
        CancellationToken ct)
        => query.Quantities()
            .Select(m => (decimal?)m.SignedQuantityMt)
            .SumAsync(ct);

    private static int? ResolveMovementContractId(InventoryMovement movement)
        => movement.ContractId ?? movement.LoadingReceipt?.LoadingRegister?.ContractId;

    private static string? ResolveMovementContractNumber(InventoryMovement movement)
        => movement.Contract?.ContractNumber
            ?? movement.LoadingReceipt?.LoadingRegister?.Contract?.ContractNumber;

    public async Task<decimal> GetFreeQuantityMtAsync(
        int productId,
        int? terminalId = null,
        int? contractId = null,
        int? inventoryBatchId = null,
        int? storageTankId = null,
        DateTime? asOfUtc = null,
        CancellationToken ct = default)
    {
        var total = await SumSignedQuantityAsync(
            BuildMovementQuery(
                productId: productId,
                terminalId: terminalId,
                contractId: contractId,
                inventoryBatchId: inventoryBatchId,
                storageTankId: storageTankId,
                asOfUtc: asOfUtc),
            ct);

        return total ?? 0m;
    }

    public async Task<decimal> GetTotalFreeQuantityMtAsync(
        int? terminalId = null,
        DateTime? asOfUtc = null,
        CancellationToken ct = default)
    {
        var total = await SumSignedQuantityAsync(
            BuildMovementQuery(
                terminalId: terminalId,
                asOfUtc: asOfUtc),
            ct);

        return total ?? 0m;
    }

    public async Task<IReadOnlyList<TankStockItem>> GetTankAvailabilityAsync(
        int productId,
        int contractId,
        DateTime? asOfUtc = null,
        CancellationToken ct = default)
    {
        var rows = await BuildMovementQuery(
                productId: productId,
                contractId: contractId,
                asOfUtc: asOfUtc)
            .Where(m => m.StorageTankId != null)
            .Select(m => new
            {
                StorageTankId = m.StorageTankId!.Value,
                m.Direction,
                m.QuantityMt
            })
            .GroupBy(m => m.StorageTankId)
            .Select(g => new
            {
                StorageTankId = g.Key,
                FreeQuantityMt = g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m)
            })
            .Where(g => g.FreeQuantityMt > 0m)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return [];
        }

        var tankIds = rows.Select(r => r.StorageTankId).ToArray();
        var tanks = await _db.StorageTanks.AsNoTracking()
            .Where(t => tankIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.TankCode,
                t.TerminalId,
                TerminalName = t.Terminal != null ? t.Terminal.Name : ""
            })
            .ToDictionaryAsync(t => t.Id, ct);

        return rows
            .Select(r =>
            {
                tanks.TryGetValue(r.StorageTankId, out var tank);
                return new TankStockItem(
                    r.StorageTankId,
                    tank?.TankCode ?? $"#{r.StorageTankId}",
                    tank?.TerminalId ?? 0,
                    tank?.TerminalName ?? "",
                    r.FreeQuantityMt);
            })
            .OrderBy(r => r.TankCode)
            .ToList();
    }

    public async Task<IReadOnlyList<StockSummaryItem>> GetStockSummaryAsync(
        int? productId = null,
        int? contractId = null,
        int? terminalId = null,
        DateTime? asOfUtc = null,
        CancellationToken ct = default)
    {
        var rows = await BuildMovementQuery(
                productId: productId,
                terminalId: terminalId,
                contractId: contractId,
                asOfUtc: asOfUtc)
            .Quantities()
            .GroupBy(m => new { m.ProductId, m.TerminalId, m.ContractId })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.TerminalId,
                g.Key.ContractId,
                FreeQuantityMt = g.Sum(m => m.SignedQuantityMt),
                LastMovementDate = g.Max(m => m.MovementDate),
                MovementCount = g.Count()
            })
            .ToListAsync(ct);

        var productIds = rows.Select(r => r.ProductId).Distinct().ToArray();
        var terminalIds = rows.Select(r => r.TerminalId).Distinct().ToArray();
        var contractIds = rows.Where(r => r.ContractId.HasValue).Select(r => r.ContractId!.Value).Distinct().ToArray();

        var products = await _db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Code, p.Name })
            .ToDictionaryAsync(p => p.Id, ct);
        var terminals = await _db.Terminals.AsNoTracking()
            .Where(t => terminalIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code, t.Name })
            .ToDictionaryAsync(t => t.Id, ct);
        var contracts = await _db.Contracts.AsNoTracking()
            .Where(c => contractIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContractNumber })
            .ToDictionaryAsync(c => c.Id, ct);

        return rows
            .Select(r =>
            {
                products.TryGetValue(r.ProductId, out var product);
                terminals.TryGetValue(r.TerminalId, out var terminal);
                var contractNumber = r.ContractId.HasValue && contracts.TryGetValue(r.ContractId.Value, out var contract)
                    ? contract.ContractNumber
                    : null;

                return new StockSummaryItem(
                    r.ProductId,
                    product?.Code ?? "",
                    product?.Name ?? "",
                    r.TerminalId,
                    terminal?.Code ?? "",
                    terminal?.Name ?? "",
                    r.ContractId,
                    contractNumber,
                    r.FreeQuantityMt,
                    r.LastMovementDate,
                    r.MovementCount);
            })
            .OrderBy(r => r.ProductCode)
            .ThenBy(r => r.TerminalCode)
            .ThenBy(r => r.ContractNumber)
            .ToList();
    }

    public async Task<IReadOnlyList<StockCardItem>> GetStockCardAsync(
        int? productId = null,
        int? contractId = null,
        int? terminalId = null,
        int? storageTankId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var normalizedFromUtc = NormalizeUtc(fromUtc);
        var normalizedToUtc = NormalizeUtc(toUtc);

        var movements = await BuildMovementQuery(
                productId: productId,
                terminalId: terminalId,
                contractId: contractId,
                storageTankId: storageTankId,
                asOfUtc: normalizedToUtc)
            .Include(m => m.Product)
            .Include(m => m.Terminal)
            .Include(m => m.Contract)
            .Include(m => m.StorageTank)
            .Include(m => m.LoadingReceipt)
                .ThenInclude(r => r!.LoadingRegister)
                    .ThenInclude(l => l!.Contract)
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var rows = new List<StockCardItem>();

        foreach (var scope in movements.GroupBy(m => new
                 {
                     m.ProductId,
                     ProductCode = m.Product?.Code ?? "",
                     ProductName = m.Product?.Name ?? "",
                     m.TerminalId,
                     TerminalCode = m.Terminal?.Code ?? "",
                     TerminalName = m.Terminal?.Name ?? "",
                     ContractId = ResolveMovementContractId(m),
                     ContractNumber = ResolveMovementContractNumber(m),
                     m.StorageTankId,
                     StorageTankCode = m.StorageTank?.TankCode
                 })
                 .OrderBy(g => g.Key.ProductCode)
                 .ThenBy(g => g.Key.TerminalCode)
                 .ThenBy(g => g.Key.ContractNumber))
        {
            decimal runningBalance = 0m;

            foreach (var movement in scope.OrderBy(m => m.MovementDate).ThenBy(m => m.Id))
            {
                var signedQuantity = ToSignedQuantity(movement.Direction, movement.QuantityMt);
                runningBalance += signedQuantity;

                if (normalizedFromUtc.HasValue && movement.MovementDate < normalizedFromUtc.Value)
                {
                    continue;
                }

                rows.Add(new StockCardItem(
                    movement.Id,
                    movement.MovementDate,
                    movement.Direction,
                    movement.QuantityMt,
                    signedQuantity,
                    runningBalance,
                    scope.Key.ProductId,
                    scope.Key.ProductCode,
                    scope.Key.ProductName,
                    scope.Key.TerminalId,
                    scope.Key.TerminalCode,
                    scope.Key.TerminalName,
                    scope.Key.ContractId,
                    scope.Key.ContractNumber,
                    scope.Key.StorageTankId,
                    scope.Key.StorageTankCode,
                    movement.ReferenceDocument,
                    movement.Notes));
            }
        }

        return rows
            .OrderBy(r => r.MovementDate)
            .ThenBy(r => r.MovementId)
            .ToList();
    }

    public async Task<IReadOnlyList<StockMovementSummaryItem>> GetMovementSummaryAsync(
        int? productId = null,
        int? contractId = null,
        int? terminalId = null,
        int? storageTankId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var from = NormalizeUtc(fromUtc);
        var query = BuildMovementQuery(
            productId: productId,
            contractId: contractId,
            terminalId: terminalId,
            storageTankId: storageTankId,
            asOfUtc: toUtc);

        return await query
            .Select(m => new
            {
                ProductName = m.Product != null ? m.Product.Name : "",
                TerminalName = m.Terminal != null ? m.Terminal.Name : "",
                StorageTankCode = m.StorageTank != null ? m.StorageTank.TankCode : null,
                ContractId = m.ContractId
                    ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                        : null),
                m.MovementDate,
                m.Direction,
                m.QuantityMt
            })
            .GroupBy(m => new { m.ProductName, m.TerminalName, m.StorageTankCode, m.ContractId })
            .Select(g => new StockMovementSummaryItem(
                g.Key.ProductName,
                g.Key.TerminalName,
                g.Key.StorageTankCode,
                g.Key.ContractId,
                from.HasValue
                    ? g.Where(m => m.MovementDate < from.Value).Sum(m =>
                        m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                            ? m.QuantityMt
                            : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                                ? -m.QuantityMt
                                : 0m)
                    : 0m,
                g.Where(m => (!from.HasValue || m.MovementDate >= from.Value)
                        && m.Direction == MovementDirection.In)
                    .Sum(m => m.QuantityMt),
                g.Where(m => (!from.HasValue || m.MovementDate >= from.Value)
                        && m.Direction == MovementDirection.Out)
                    .Sum(m => m.QuantityMt),
                g.Where(m => (!from.HasValue || m.MovementDate >= from.Value)
                        && m.Direction == MovementDirection.Adjustment)
                    .Sum(m => m.QuantityMt),
                g.Where(m => (!from.HasValue || m.MovementDate >= from.Value)
                        && m.Direction == MovementDirection.Transfer)
                    .Sum(m => m.QuantityMt),
                g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m),
                g.Count(m => !from.HasValue || m.MovementDate >= from.Value),
                g.Where(m => !from.HasValue || m.MovementDate >= from.Value)
                    .Max(m => (DateTime?)m.MovementDate)))
            .ToListAsync(ct);
    }

    public async Task AcquireStockMutationLockAsync(
        InventoryMovement movement,
        CancellationToken ct = default)
    {
        if (movement is null) throw new ArgumentNullException(nameof(movement));

        // Only relational PostgreSQL supports the row/advisory locks used here.
        // Tests (in-memory/sqlite) and other providers run without the lock.
        if (!_db.Database.IsRelational()
            || !string.Equals(
                _db.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return;
        }

        if (movement.StorageTankId.HasValue)
        {
            // Row lock on the source tank — mirrors the proven Sales/Dispatch path.
            // Harmless no-op when the caller already holds this row lock.
            var tankId = movement.StorageTankId.Value;
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"SELECT ""Id"" FROM ""StorageTanks"" WHERE ""Id"" = {tankId} FOR UPDATE",
                ct);
            return;
        }

        // No single tank row to lock (contract/product-scoped movement): serialize on
        // the product with a transaction-scoped advisory lock (auto-released at commit).
        var productId = movement.ProductId;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(9271, {productId})",
            ct);
    }

    public async Task EnsureSufficientStockForMovementAsync(
        InventoryMovement movement,
        CancellationToken ct = default)
    {
        if (movement is null) throw new ArgumentNullException(nameof(movement));

        if (movement.Direction != MovementDirection.Out
            && movement.Direction != MovementDirection.Transfer)
        {
            return;
        }

        if (movement.QuantityMt <= 0m)
        {
            throw new BusinessRuleException(
                "STOCK_QTY_NON_POSITIVE",
                "مقدار حرکت موجودی باید بزرگ‌تر از صفر باشد.");
        }

        var available = await GetFreeQuantityMtAsync(
            movement.ProductId,
            terminalId: movement.TerminalId,
            contractId: movement.ContractId,
            inventoryBatchId: movement.InventoryBatchId,
            storageTankId: movement.StorageTankId,
            asOfUtc: movement.MovementDate,
            ct: ct);

        if (available < movement.QuantityMt)
        {
            throw new BusinessRuleException(
                "STOCK_INSUFFICIENT",
                $"موجودی کافی نیست. موجودی فعلی: {available:N4} MT، درخواست: {movement.QuantityMt:N4} MT.");
        }
    }

    /// <summary>
    /// PTG-P0-02 — یک سندِ خروجی نباید موجودیِ پایانیِ همان scope را منفی کند،
    /// حتی اگر در تاریخِ خودش موجودی کافی بوده باشد.
    ///
    /// چرا لازم است: <see cref="EnsureSufficientStockForMovementAsync"/> موجودی را «در تاریخ
    /// همان سند» می‌سنجد. یک فروشِ عقب‌تاریخ می‌تواند از آن عبور کند و در عوض
    /// موجودیِ امروزِ همان مخزن را منفی کند، چون فروش‌های بعدی از موجودی‌ای
    /// برداشته‌اند که حالا دیگر وجود ندارد. ثبتِ عقب‌تاریخ ممنوع نیست؛ فقط
    /// نباید بی‌صدا موجودی را منفی کند.
    ///
    /// معیار دقیقاً «ماندهٔ پایانی» است، نه تک‌تکِ نقطه‌های میانی. دلیلش عملیاتی است:
    /// در عمل، سندِ رسید اغلب بعد از بارگیریِ موتر/واگن به دفتر می‌رسد، پس خط زمانی
    /// می‌تواند یک گودالِ گذرای منفی داشته باشد که خودش ترمیم می‌شود
    /// (مسیر <c>InventoryTransportLegLoadService</c> عمداً موجودیِ جاری را مبنا می‌گیرد).
    /// آنچه هرگز مجاز نیست، ماندنِ موجودی در منفی است — همان چیزی که COGS و سود را خراب می‌کند.
    ///
    /// کارایی: کلِ خط زمانی خوانده نمی‌شود. مانده تا پیش از تاریخ سند با یک SUM
    /// گرفته می‌شود و فقط سطرهای «از آن تاریخ به بعد» پیموده می‌شوند — که برای ثبتِ
    /// امروز (حالت رایج) تقریباً هیچ سطری نیست.
    /// </summary>
    public Task EnsureMovementDoesNotCauseFutureNegativeStockAsync(
        InventoryMovement movement,
        CancellationToken ct = default)
        => CheckMovementTimelineAsync(movement, false, ct);

    public Task EnsureMovementTimelineIsNonNegativeAsync(InventoryMovement movement, CancellationToken ct = default)
        => CheckMovementTimelineAsync(movement, true, ct);

    private async Task CheckMovementTimelineAsync(InventoryMovement movement, bool everyPoint, CancellationToken ct)
    {
        if (movement is null) throw new ArgumentNullException(nameof(movement));

        // Only Out/Transfer can drive a balance below zero. In/Adjustment
        // (positive) cannot reduce stock, so this check is a no-op for them.
        if (movement.Direction != MovementDirection.Out
            && movement.Direction != MovementDirection.Transfer)
        {
            return;
        }

        if (movement.QuantityMt <= 0m)
        {
            throw new BusinessRuleException(
                "STOCK_QTY_NON_POSITIVE",
                "مقدار حرکت موجودی باید بزرگ‌تر از صفر باشد.");
        }

        var movementDate = NormalizeUtc(movement.MovementDate);

        // خط زمانیِ همان scope، بدون پیش‌تصویرِ خودِ سند وقتی در حال ویرایش است.
        var scopeQuery = BuildMovementQuery(
                productId: movement.ProductId,
                terminalId: movement.TerminalId,
                contractId: movement.ContractId,
                inventoryBatchId: movement.InventoryBatchId,
                storageTankId: movement.StorageTankId)
            .Where(m => m.Id != movement.Id);

        var balanceBefore = await SumSignedQuantityAsync(
            scopeQuery.Where(m => everyPoint ? m.MovementDate <= movementDate : m.MovementDate < movementDate),
            ct) ?? 0m;

        var laterRows = await scopeQuery
            .Where(m => everyPoint ? m.MovementDate > movementDate : m.MovementDate >= movementDate)
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => m.Id)
            .Select(m => new { m.MovementDate, m.Direction, m.QuantityMt })
            .ToListAsync(ct);

        // Manual inserts get a new Id: existing rows at the same instant precede
        // them, exactly as the stock card orders MovementDate then Id. Legacy
        // operational documents retain their historical simulation order.
        var running = balanceBefore + ToSignedQuantity(movement.Direction, movement.QuantityMt);
        var lowest = running;
        DateTime? firstNegativeDate = running < 0m ? movementDate : null;

        foreach (var row in laterRows)
        {
            running += ToSignedQuantity(row.Direction, row.QuantityMt);
            if (running < lowest)
            {
                lowest = running;
            }

            if (running < 0m && firstNegativeDate is null)
            {
                firstNegativeDate = row.MovementDate;
            }
        }

        // running اکنون ماندهٔ پایانیِ scope است. گودالِ گذرای میانی که
        // خودش ترمیم می‌شود مجاز است؛ ماندنِ موجودی در منفی نه.
        if (everyPoint ? lowest >= 0m : running >= 0m)
        {
            return;
        }

        var scope = await DescribeStockScopeAsync(movement, ct);
        throw new BusinessRuleException(
            "STOCK_FUTURE_NEGATIVE",
            $"این ثبت انجام نشد: خروج {movement.QuantityMt:N4} MT به تاریخ {movementDate.ToCalendarString("yyyy-MM-dd")} " +
            $"باعث می‌شود موجودی {scope} از تاریخ {firstNegativeDate.ToCalendarString("yyyy-MM-dd")} منفی شود " +
            $"(کمترین موجودی پیش‌بینی‌شده: {lowest:N4} MT، ماندهٔ پایانی: {running:N4} MT). " +
            "اگر این سند عقب‌تاریخ است، اول اسناد بعدیِ همان مخزن را اصلاح کنید، " +
            "یا تاریخ و مقدار این سند را تصحیح کنید.");
    }

    /// <summary>
    /// شرحِ خوانا از scope موجودی، فقط زمانی که قرار است خطا داده شود.
    /// </summary>
    private async Task<string> DescribeStockScopeAsync(InventoryMovement movement, CancellationToken ct)
    {
        var parts = new List<string>();

        var productName = await _db.Products
            .AsNoTracking()
            .Where(p => p.Id == movement.ProductId)
            .Select(p => p.NamePersian ?? p.Name)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(productName))
        {
            parts.Add($"کالای «{productName}»");
        }

        if (movement.StorageTankId.HasValue)
        {
            var tankCode = await _db.StorageTanks
                .AsNoTracking()
                .Where(t => t.Id == movement.StorageTankId.Value)
                .Select(t => t.DisplayName ?? t.TankCode)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(tankCode))
            {
                parts.Add($"مخزن «{tankCode}»");
            }
        }

        var terminalName = await _db.Terminals
            .AsNoTracking()
            .Where(t => t.Id == movement.TerminalId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(terminalName))
        {
            parts.Add($"ترمینال «{terminalName}»");
        }

        if (movement.ContractId.HasValue)
        {
            var contractNumber = await _db.Contracts
                .AsNoTracking()
                .Where(c => c.Id == movement.ContractId.Value)
                .Select(c => c.ContractNumber)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(contractNumber))
            {
                parts.Add($"قرارداد {contractNumber}");
            }
        }

        return parts.Count == 0 ? "این مخزن" : string.Join(" / ", parts);
    }

    [Obsolete("Use EnsureSufficientStockForSaleAsync(sale, sourcePurchaseContractId, ct) — sale.ContractId is the Sales contract and is NOT a valid stock filter.", error: true)]
    public Task EnsureSufficientStockForSaleAsync(
        SalesTransaction sale,
        CancellationToken ct = default)
        => throw new InvalidOperationException(
            "EnsureSufficientStockForSaleAsync(sale) is deprecated. " +
            "Pass the explicit sourcePurchaseContractId — sale.ContractId is the Sales contract " +
            "and cannot be used as a stock filter.");

    public async Task EnsureSufficientStockForSaleAsync(
        SalesTransaction sale,
        int? sourcePurchaseContractId,
        CancellationToken ct = default)
    {
        if (sale is null) throw new ArgumentNullException(nameof(sale));

        if (sale.QuantityMt <= 0m)
        {
            throw new BusinessRuleException(
                "SALE_QTY_NON_POSITIVE",
                "مقدار فروش باید بزرگ‌تر از صفر باشد.");
        }

        var available = await GetFreeQuantityMtAsync(
            sale.ProductId,
            terminalId: null,
            contractId: sourcePurchaseContractId,
            asOfUtc: sale.SaleDate,
            ct: ct);

        if (available < sale.QuantityMt)
        {
            var scope = sourcePurchaseContractId.HasValue
                ? $"قرارداد خرید #{sourcePurchaseContractId.Value}"
                : "محصول";
            throw new BusinessRuleException(
                "SALE_INSUFFICIENT_STOCK",
                $"فروش رد شد. موجودی آزاد {scope} برابر {available:N4} MT است و کمتر از مقدار درخواستی ({sale.QuantityMt:N4} MT).");
        }
    }
}
