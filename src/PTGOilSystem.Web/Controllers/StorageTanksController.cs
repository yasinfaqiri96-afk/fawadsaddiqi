using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.StorageTanks;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class StorageTanksController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;
    private readonly IStockService _stock;
    private readonly ILossEventWorkflowService _lossWorkflow;

    public StorageTanksController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety,
        IStockService stock,
        ILossEventWorkflowService lossWorkflow)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
        _stock = stock;
        _lossWorkflow = lossWorkflow;
    }

    private async Task PopulateLookupsAsync(StorageTank? current = null)
    {
        ViewBag.Terminals = new SelectList(await _db.Terminals.AsNoTracking().OrderBy(t => t.Code).ToListAsync(), "Id", "Name", current?.TerminalId);
        ViewBag.Products = new SelectList(await _db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync(), "Id", "Name", current?.ProductId);
    }

    public async Task<IActionResult> Index(int[]? terminalId, int[]? productId, bool? isActive, string? q, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 8);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 8;

        var query = _db.StorageTanks
            .AsNoTracking()
            .AsQueryable();
        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (terminalId is { Length: > 0 }) query = query.Where(t => terminalId.Contains(t.TerminalId));
        if (productId is { Length: > 0 }) query = query.Where(t => t.ProductId != null && productId.Contains(t.ProductId.Value));
        if (isActive.HasValue) query = query.Where(t => t.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(t =>
                t.TankCode.Contains(term)
                || (t.DisplayName != null && t.DisplayName.Contains(term))
                || (t.Terminal != null && t.Terminal.Name.Contains(term))
                || (t.Product != null && t.Product.Name.Contains(term)));
        }

        var terminals = await _db.Terminals.AsNoTracking().OrderBy(t => t.Code).ToListAsync();
        var products = await _db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync();
        var tanks = await query
            .OrderBy(t => t.Terminal!.Code)
            .ThenBy(t => t.TankCode)
            .Select(t => new
            {
                t.Id,
                t.TankCode,
                t.DisplayName,
                TerminalName = t.Terminal != null ? t.Terminal.Name : "",
                ProductName = t.Product != null ? t.Product.Name : null,
                ProductUnitOfMeasure = t.Product != null ? t.Product.UnitOfMeasure : null,
                UnitNamePersian = t.Product != null && t.Product.Unit != null ? t.Product.Unit.NamePersian : null,
                UnitSymbol = t.Product != null && t.Product.Unit != null ? t.Product.Unit.Symbol : null,
                UnitCode = t.Product != null && t.Product.Unit != null ? t.Product.Unit.Code : null,
                UnitName = t.Product != null && t.Product.Unit != null ? t.Product.Unit.Name : null,
                t.CapacityMt,
                t.IsActive
            })
            .ToListAsync();
        var singleProductId = productId is { Length: 1 } ? productId[0] : (int?)null;
        var singleTerminalId = terminalId is { Length: 1 } ? terminalId[0] : (int?)null;
        var stockCard = await _stock.GetStockCardAsync(productId: singleProductId, terminalId: singleTerminalId);
        var stockByTank = stockCard
            .Where(m => m.StorageTankId.HasValue)
            .GroupBy(m => m.StorageTankId!.Value)
            .ToDictionary(g => g.Key, g => new
            {
                QuantityMt = g.Sum(m => m.SignedQuantityMt),
                ContractCount = g
                    .Where(m => m.ContractId.HasValue && m.SignedQuantityMt != 0m)
                    .Select(m => m.ContractId!.Value)
                    .Distinct()
                    .Count()
            });

        var tankIds = tanks.Select(t => t.Id).ToArray();
        var custodyByTank = await _db.CustodyMovements.AsNoTracking()
            .Where(m => m.StorageTankId.HasValue && tankIds.Contains(m.StorageTankId.Value))
            .GroupBy(m => m.StorageTankId!.Value)
            .Select(g => new { Id = g.Key, Qty = g.Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt) })
            .ToDictionaryAsync(g => g.Id, g => g.Qty);
        var items = tanks.Select(t =>
        {
            stockByTank.TryGetValue(t.Id, out var stock);
            var currentQuantityMt = stock?.QuantityMt ?? 0m;
            return new StorageTankListItemViewModel
            {
                Id = t.Id,
                TankCode = t.TankCode,
                DisplayName = string.IsNullOrWhiteSpace(t.DisplayName) ? t.TankCode : t.DisplayName,
                TerminalName = t.TerminalName,
                ProductName = t.ProductName ?? "-",
                UnitOfMeasure = ResolveProductUnitText(t.UnitNamePersian, t.UnitSymbol, t.UnitCode, t.UnitName, t.ProductUnitOfMeasure),
                CapacityMt = t.CapacityMt,
                CurrentQuantityMt = currentQuantityMt,
                FillPercent = t.CapacityMt > 0m ? Math.Round((currentQuantityMt + custodyByTank.GetValueOrDefault(t.Id)) / t.CapacityMt * 100m, 2) : 0m,
                IsActive = t.IsActive,
                ContractCount = stock?.ContractCount ?? 0
            };
        }).ToList();

        ViewBag.Terminals = new SelectList(terminals, "Id", "Name", singleTerminalId);
        ViewBag.Products = new SelectList(products, "Id", "Name", singleProductId);
        ViewBag.CreateTerminals = new SelectList(terminals, "Id", "Name");
        ViewBag.CreateProducts = new SelectList(products, "Id", "Name");
        ViewData["terminalId"] = terminalId ?? [];

        var totalCount = items.Count;
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);
        var pagedItems = page <= 0
            ? items
            : items
                .Skip((currentPage - 1) * pageSize)
                .Take(pageSize)
                .ToList();

        return View(new StorageTankIndexViewModel
        {
            TerminalId = terminalId ?? [],
            ProductId = productId ?? [],
            IsActive = isActive,
            Query = q,
            TotalTanks = totalCount,
            ActiveTanks = items.Count(t => t.IsActive),
            TotalCapacityMt = items.Sum(t => t.CapacityMt),
            TotalCurrentQuantityMt = items.Sum(t => t.CurrentQuantityMt),
            AverageFillPercent = items.Count > 0 ? Math.Round(items.Average(t => t.FillPercent), 2) : 0m,
            Items = pagedItems,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    private static string ResolveProductUnitText(Product? product)
    {
        var unit = product?.Unit;
        return ResolveProductUnitText(
            unit?.NamePersian,
            unit?.Symbol,
            unit?.Code,
            unit?.Name,
            product?.UnitOfMeasure);
    }

    private static string ResolveProductUnitText(
        string? unitNamePersian,
        string? unitSymbol,
        string? unitCode,
        string? unitName,
        string? productUnitOfMeasure)
    {
        foreach (var candidate in new[]
        {
            unitNamePersian,
            unitSymbol,
            unitCode,
            unitName,
            productUnitOfMeasure
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return "MT";
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.StorageTanks.Include(t => t.Terminal).Include(t => t.Product).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var stockCard = await _stock.GetStockCardAsync(
            storageTankId: item.Id);

        var currentTankName = BuildTankLocation(item.Terminal?.Name, StorageTankDisplay.Build(item));
        var movementContextById = await BuildMovementContextLookupAsync(
            stockCard.Select(m => m.MovementId).Distinct().ToList(),
            currentTankName);

        decimal runningBalance = 0m;
        var movementRows = stockCard
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => m.MovementId)
            .Select(m =>
            {
                runningBalance += m.SignedQuantityMt;
                movementContextById.TryGetValue(m.MovementId, out var movementContext);
                movementContext ??= DefaultMovementContext(m.Direction, currentTankName, m.ReferenceDocument);

                return new StorageTankMovementRowViewModel
                {
                    MovementId = m.MovementId,
                    MovementDate = m.MovementDate,
                    Direction = m.Direction,
                    QuantityMt = m.QuantityMt,
                    SignedQuantityMt = m.SignedQuantityMt,
                    RunningBalanceMt = runningBalance,
                    ProductId = m.ProductId,
                    ProductCode = m.ProductCode,
                    ProductName = m.ProductName,
                    TerminalId = m.TerminalId,
                    TerminalName = string.IsNullOrWhiteSpace(m.TerminalCode)
                        ? m.TerminalName
                        : $"{m.TerminalCode} - {m.TerminalName}",
                    ContractId = m.ContractId,
                    ContractNumber = m.ContractNumber,
                    StorageTankId = m.StorageTankId,
                    ReferenceDocument = m.ReferenceDocument,
                    SourceName = movementContext.SourceName,
                    SourceTransport = movementContext.SourceTransport,
                    DestinationName = movementContext.DestinationName,
                    MovementContext = movementContext.Context,
                    Notes = m.Notes
                };
            })
            .ToList();

        var balances = movementRows
            .GroupBy(m => new { m.ProductId, m.ProductCode, m.ProductName, m.ContractId, m.ContractNumber })
            .Select(g => new StorageTankBalanceRowViewModel
            {
                ProductId = g.Key.ProductId,
                ProductCode = g.Key.ProductCode,
                ProductName = g.Key.ProductName,
                ContractId = g.Key.ContractId,
                ContractNumber = g.Key.ContractNumber,
                QuantityMt = g.Sum(m => m.SignedQuantityMt)
            })
            .Where(r => r.QuantityMt != 0m)
            .OrderBy(r => r.ProductName)
            .ThenBy(r => r.ContractNumber)
            .ToList();

        var currentQuantityMt = movementRows.Sum(m => m.SignedQuantityMt);
        var totalInQuantityMt = movementRows.Where(m => m.SignedQuantityMt > 0m).Sum(m => m.SignedQuantityMt);
        var totalOutQuantityMt = Math.Abs(movementRows.Where(m => m.SignedQuantityMt < 0m).Sum(m => m.SignedQuantityMt));
        var custodyQuantityMt = await _db.CustodyMovements.AsNoTracking().Where(m => m.StorageTankId == id)
            .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt);
        var physicalQuantityMt = currentQuantityMt + custodyQuantityMt;
        var fillPercent = item.CapacityMt > 0m ? Math.Round(physicalQuantityMt / item.CapacityMt * 100m, 2) : 0m;
        var emptyQuantityMt = Math.Max(item.CapacityMt - physicalQuantityMt, 0m);
        var lastMovementDate = movementRows.Count > 0 ? movementRows.Max(m => m.MovementDate) : (DateTime?)null;
        var recentMovements = movementRows
            .OrderByDescending(m => m.MovementDate)
            .ThenByDescending(m => m.MovementId)
            .Take(10)
            .ToList();
        var contractIds = movementRows
            .Where(m => m.ContractId.HasValue)
            .Select(m => m.ContractId!.Value)
            .Distinct()
            .ToList();
        var contractTypeById = await _db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.ContractType);
        var contractStockBreakdownRows = movementRows
            .GroupBy(m => new { m.ProductId, m.ProductCode, m.ProductName, m.ContractId, m.ContractNumber })
            .Select(g =>
            {
                var totalIn = g.Where(m => m.SignedQuantityMt > 0m).Sum(m => m.SignedQuantityMt);
                var totalOut = Math.Abs(g.Where(m => m.SignedQuantityMt < 0m).Sum(m => m.SignedQuantityMt));
                var balance = totalIn - totalOut;

                return new StorageTankContractStockBreakdownRowViewModel
                {
                    ProductId = g.Key.ProductId,
                    ProductCode = g.Key.ProductCode,
                    ProductName = g.Key.ProductName,
                    ContractId = g.Key.ContractId,
                    ContractNumber = g.Key.ContractNumber,
                    ContractType = g.Key.ContractId.HasValue && contractTypeById.TryGetValue(g.Key.ContractId.Value, out var contractType)
                        ? contractType
                        : null,
                    TotalInQuantityMt = totalIn,
                    TotalOutQuantityMt = totalOut,
                    BalanceQuantityMt = balance,
                    SharePercent = currentQuantityMt > 0m ? Math.Round(balance / currentQuantityMt * 100m, 2) : 0m
                };
            })
            .OrderByDescending(r => r.BalanceQuantityMt)
            .ThenBy(r => r.ContractNumber)
            .ThenBy(r => r.ProductName)
            .ToList();

        // ---- وضعیت تسویه/ضایعات معوق (سناریوی چند واگن → یک مخزن → ضایعه هنگام خالی‌شدن) ----
        var settlementStatus = await BuildTankSettlementStatusAsync(item.Id, contractStockBreakdownRows);

        var model = new StorageTankDetailsViewModel
        {
            Id = item.Id,
            TankCode = item.TankCode,
            DisplayName = item.DisplayName,
            TerminalId = item.TerminalId,
            TerminalName = item.Terminal?.Name ?? "",
            ProductId = item.ProductId,
            ProductName = item.Product?.Name,
            UnitOfMeasure = string.IsNullOrWhiteSpace(item.Product?.UnitOfMeasure) ? "MT" : item.Product.UnitOfMeasure.Trim(),
            CapacityMt = item.CapacityMt,
            IsActive = item.IsActive,
            Notes = item.Notes,
            CreatedAtUtc = item.CreatedAtUtc,
            CurrentQuantityMt = currentQuantityMt,
            TotalInQuantityMt = totalInQuantityMt,
            TotalOutQuantityMt = totalOutQuantityMt,
            NetMovementQuantityMt = currentQuantityMt,
            FillPercent = fillPercent,
            EmptyQuantityMt = emptyQuantityMt,
            MovementCount = movementRows.Count,
            LastMovementDate = lastMovementDate,
            Balances = balances,
            ContractStockBreakdownRows = contractStockBreakdownRows,
            RecentMovements = recentMovements,
            Movements = movementRows,
            SettlementStatus = settlementStatus
        };

        return View(model);
    }

    // وضعیت تسویهٔ نهایی و ضایعات معوق مخزن — فقط خواندنی، بدون هیچ تغییری در موجودی/منطق.
    private async Task<StorageTankSettlementStatusViewModel> BuildTankSettlementStatusAsync(
        int tankId,
        IReadOnlyList<StorageTankContractStockBreakdownRowViewModel> contractStockBreakdownRows)
    {
        // موجودی دفتری مثبت هر قرارداد در این مخزن.
        var balanceByContract = contractStockBreakdownRows
            .Where(r => r.ContractId.HasValue)
            .GroupBy(r => r.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.BalanceQuantityMt));

        var positiveContractBalances = balanceByContract.Where(kv => kv.Value > 0m).ToList();
        var settleableMt = positiveContractBalances.Sum(kv => kv.Value);
        var sourceContractCount = positiveContractBalances.Count;

        // بارگیری/واگن‌هایی که موجودی وارد این مخزن کرده‌اند.
        var feedingReceipts = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.StorageTankId == tankId
                && r.ReceiptDestination == LoadingReceiptDestination.ToInventory
                && r.LoadingRegister != null)
            .Select(r => new
            {
                r.Id,
                r.LoadingRegisterId,
                r.ReceiptDate,
                r.ReceivedQuantityMt,
                r.LossMode,
                ContractId = r.LoadingRegister!.ContractId,
                ContractNumber = r.LoadingRegister.Contract != null ? r.LoadingRegister.Contract.ContractNumber : null,
                r.LoadingRegister.TransportType,
                r.LoadingRegister.WagonNumber,
                r.LoadingRegister.RwbNo,
                r.LoadingRegister.BillOfLadingNumber,
                VesselName = r.LoadingRegister.Vessel != null ? r.LoadingRegister.Vessel.Name : null,
                TruckPlate = r.LoadingRegister.Truck != null ? r.LoadingRegister.Truck.PlateNumber : null
            })
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .ToListAsync();

        var feedingRows = feedingReceipts.Select(r =>
        {
            var transportLabel = r.TransportType switch
            {
                LoadingTransportType.Wagon => string.IsNullOrWhiteSpace(r.WagonNumber) ? r.RwbNo : r.WagonNumber,
                LoadingTransportType.Vessel => r.VesselName,
                LoadingTransportType.Truck => r.TruckPlate,
                _ => null
            };

            return new StorageTankFeedingLoadingRowViewModel
            {
                LoadingReceiptId = r.Id,
                LoadingRegisterId = r.LoadingRegisterId,
                ContractNumber = r.ContractNumber,
                TransportLabel = transportLabel,
                RwbNo = r.RwbNo,
                BillOfLadingNumber = r.BillOfLadingNumber,
                TransportType = r.TransportType,
                ReceiptDate = r.ReceiptDate,
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                LossMode = r.LossMode
            };
        }).ToList();

        var deferredReceiptCount = feedingReceipts.Count(r => r.LossMode == ReceiptLossMode.DeferredTankSettlement);
        var deferredContractIds = feedingReceipts
            .Where(r => r.LossMode == ReceiptLossMode.DeferredTankSettlement)
            .Select(r => r.ContractId)
            .Distinct()
            .ToList();
        var pendingContractIds = deferredContractIds
            .Where(cid => balanceByContract.TryGetValue(cid, out var bal) && bal > 0m)
            .ToList();
        var pendingDeferredQuantityMt = pendingContractIds.Sum(cid => balanceByContract[cid]);

        // ضایعات تسویه‌شدهٔ قبلی روی این مخزن.
        var settlementEvents = await _db.LossEvents
            .AsNoTracking()
            .Where(e => e.StorageTankId == tankId
                && e.Stage == LossEventStage.TankFinalSettlement
                && !e.IsCancelled)
            .Select(e => new { e.EventDate, e.ChargeableLossMt, e.DifferenceQuantityMt, e.LossCertainty })
            .ToListAsync();

        var settledLossQuantityMt = settlementEvents.Sum(e =>
            e.DifferenceQuantityMt > 0m ? e.DifferenceQuantityMt : Math.Max(e.ChargeableLossMt, 0m));

        return new StorageTankSettlementStatusViewModel
        {
            SettleableQuantityMt = settleableMt,
            SourceContractCount = sourceContractCount,
            DeferredReceiptCount = deferredReceiptCount,
            PendingSettlementContractCount = pendingContractIds.Count,
            PendingDeferredQuantityMt = pendingDeferredQuantityMt,
            SettlementEventCount = settlementEvents.Count,
            SettledLossQuantityMt = settledLossQuantityMt,
            LastSettlementDate = settlementEvents.Count > 0 ? settlementEvents.Max(e => e.EventDate) : null,
            HasMeasuredSettlement = settlementEvents.Any(e => e.LossCertainty == LossCertaintyLevel.Measured),
            HasEstimatedSettlement = settlementEvents.Any(e => e.LossCertainty == LossCertaintyLevel.Estimated),
            FeedingLoadings = feedingRows
        };
    }

    // ---- تسویهٔ نهایی مخزن (سناریوی ضایعات معوق) -------------------------------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> SettleFinal(int id, string? returnUrl = null)
    {
        var tank = await _db.StorageTanks
            .Include(t => t.Terminal)
            .Include(t => t.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);
        if (tank is null) return NotFound();

        var (currentMt, settleableMt, balances) = await LoadTankSettlementStateAsync(id);

        var model = new StorageTankSettlementViewModel
        {
            TankId = tank.Id,
            TankCode = StorageTankDisplay.Build(tank),
            TerminalName = tank.Terminal?.Name ?? "",
            UnitOfMeasure = string.IsNullOrWhiteSpace(tank.Product?.UnitOfMeasure) ? "MT" : tank.Product!.UnitOfMeasure.Trim(),
            CurrentQuantityMt = currentMt,
            SettleableQuantityMt = settleableMt,
            AllocationMode = TankLossAllocationMode.Proportional,
            EventDate = AfghanistanBusinessClock.SystemToday,
            ActualRemainingMt = 0m,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null,
            ContractRows = BuildSettlementRows(balances, settleableMt, 0m),
            ManualLosses = BuildManualLossInputs(balances)
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> SettleFinal(StorageTankSettlementViewModel model)
    {
        var tank = await _db.StorageTanks
            .Include(t => t.Terminal)
            .Include(t => t.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == model.TankId);
        if (tank is null) return NotFound();

        var (currentMt, settleableMt, balances) = await LoadTankSettlementStateAsync(model.TankId);
        var unit = string.IsNullOrWhiteSpace(tank.Product?.UnitOfMeasure) ? "MT" : tank.Product!.UnitOfMeasure.Trim();

        if (balances.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "این مخزن موجودی قابل تسویه‌ای از قراردادهای منبع ندارد.");
        }

        // ضایعهٔ برنامه‌ریزی‌شده برای هر قرارداد بسته به روش تقسیم محاسبه می‌شود.
        // Proportional = تقسیم نسبتی (تخمینی، رفتار قبلی)، Manual = ورود دستی (اندازه‌گیری‌شده).
        var plannedLosses = new List<(TankContractBalance Balance, decimal LossMt)>();
        decimal totalLoss;
        LossCertaintyLevel certainty;

        if (model.AllocationMode == TankLossAllocationMode.Manual)
        {
            certainty = LossCertaintyLevel.Measured;
            var manualByContract = (model.ManualLosses ?? [])
                .GroupBy(m => m.ContractId)
                .ToDictionary(g => g.Key, g => g.Last().LossMt);

            foreach (var balance in balances)
            {
                manualByContract.TryGetValue(balance.ContractId, out var lossMt);
                if (lossMt < 0m)
                {
                    ModelState.AddModelError(string.Empty, $"ضایعهٔ قرارداد {balance.ContractNumber} نمی‌تواند منفی باشد.");
                }
                else if (lossMt > balance.BalanceMt)
                {
                    ModelState.AddModelError(string.Empty, $"ضایعهٔ قرارداد {balance.ContractNumber} ({lossMt:N4} {unit}) نمی‌تواند بیشتر از موجودی دفتری ({balance.BalanceMt:N4} {unit}) باشد.");
                }

                plannedLosses.Add((balance, decimal.Round(Math.Max(lossMt, 0m), 4, MidpointRounding.AwayFromZero)));
            }

            totalLoss = plannedLosses.Sum(p => p.LossMt);
        }
        else
        {
            certainty = LossCertaintyLevel.Estimated;
            if (model.ActualRemainingMt < 0m)
            {
                ModelState.AddModelError(nameof(model.ActualRemainingMt), "مقدار باقیمانده نمی‌تواند منفی باشد.");
            }
            else if (model.ActualRemainingMt > settleableMt)
            {
                ModelState.AddModelError(nameof(model.ActualRemainingMt), $"مقدار باقیمانده نمی‌تواند بیشتر از موجودی قابل تسویه ({settleableMt:N4} {unit}) باشد.");
            }

            totalLoss = Math.Max(settleableMt - model.ActualRemainingMt, 0m);
            foreach (var balance in balances)
            {
                var remainingShare = settleableMt > 0m
                    ? decimal.Round(model.ActualRemainingMt * balance.BalanceMt / settleableMt, 4, MidpointRounding.AwayFromZero)
                    : 0m;
                var lossMt = decimal.Round(balance.BalanceMt - remainingShare, 4, MidpointRounding.AwayFromZero);
                plannedLosses.Add((balance, lossMt));
            }
        }

        if (ModelState.IsValid && totalLoss <= 0m)
        {
            ModelState.AddModelError(string.Empty, model.AllocationMode == TankLossAllocationMode.Manual
                ? "هیچ مقدار ضایعه‌ای وارد نشده است؛ حداقل برای یک قرارداد ضایعه ثبت کنید."
                : "اختلافی برای ثبت ضایعات وجود ندارد؛ مقدار باقیمانده برابر موجودی فعلی است.");
        }

        if (!ModelState.IsValid)
        {
            RehydrateSettlementModel(model, tank, unit, currentMt, settleableMt, balances);
            return View(model);
        }

        // شیپمنتِ یکتای هر قرارداد را پیدا کن تا ضایعهٔ «تسویه نهایی مخزن» به پروندهٔ محموله وصل شود
        // و در محاسبهٔ ضایعات/سود محموله دیده شود. فقط وقتی قرارداد دقیقاً به یک محموله وصل است ست
        // می‌شود؛ در غیر این صورت مثل قبل null می‌ماند (بدون حدسِ نادرست).
        var settlementContractIds = plannedLosses
            .Where(p => p.LossMt > 0m)
            .Select(p => p.Balance.ContractId)
            .Distinct()
            .ToList();
        var shipmentContractRows = settlementContractIds.Count == 0
            ? []
            : await _db.ShipmentContracts
                .AsNoTracking()
                .Where(sc => settlementContractIds.Contains(sc.ContractId))
                .Select(sc => new { sc.ContractId, sc.ShipmentId })
                .ToListAsync();
        var shipmentIdByContract = shipmentContractRows
            .GroupBy(r => r.ContractId)
            .Where(g => g.Select(x => x.ShipmentId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().ShipmentId);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        var createdCount = 0;
        try
        {
            foreach (var (balance, lossMt) in plannedLosses)
            {
                if (lossMt <= 0m)
                {
                    continue;
                }

                var submission = new LossEventSubmission
                {
                    Stage = LossEventStage.TankFinalSettlement,
                    ProductId = balance.ProductId,
                    ContractId = balance.ContractId,
                    ShipmentId = shipmentIdByContract.TryGetValue(balance.ContractId, out var settleShipmentId)
                        ? settleShipmentId
                        : null,
                    TerminalId = tank.TerminalId,
                    StorageTankId = tank.Id,
                    EventDate = model.EventDate == default ? AfghanistanBusinessClock.SystemToday : model.EventDate,
                    ExpectedQuantityMt = balance.BalanceMt,
                    ActualQuantityMt = balance.BalanceMt - lossMt,
                    ToleranceQuantityMt = 0m,
                    AffectsInventory = true,
                    LossCertainty = certainty,
                    Reference = $"TANK-SETTLE:{tank.Id}",
                    Notes = BuildSettlementNotes(tank.TankCode, balance.ContractNumber, model.Notes)
                };

                await _lossWorkflow.ValidateAsync(submission, (_, error) => ModelState.AddModelError(string.Empty, error));
                if (!ModelState.IsValid)
                {
                    if (transaction is not null) await transaction.RollbackAsync();
                    RehydrateSettlementModel(model, tank, unit, currentMt, settleableMt, balances);
                    return View(model);
                }

                await _lossWorkflow.CreateAsync(submission);
                createdCount++;
            }

            if (transaction is not null) await transaction.CommitAsync();
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            ModelState.AddModelError(string.Empty, ex.Message);
            RehydrateSettlementModel(model, tank, unit, currentMt, settleableMt, balances);
            return View(model);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        var methodLabel = model.AllocationMode == TankLossAllocationMode.Manual
            ? "اندازه‌گیری‌شده (ورود دستی)"
            : "تخمینی (تقسیم نسبتی)";
        TempData["ok"] = $"تسویهٔ نهایی مخزن ثبت شد. {createdCount} رویداد ضایعات [{methodLabel}] برای قراردادهای منبع ساخته شد (مجموع {totalLoss:N4} {unit}).";
        return RedirectToAction(nameof(Details), new { id = tank.Id });
    }

    private static List<StorageTankSettlementManualLossInput> BuildManualLossInputs(
        IReadOnlyList<TankContractBalance> balances)
        => balances.Select(b => new StorageTankSettlementManualLossInput
        {
            ContractId = b.ContractId,
            ProductId = b.ProductId,
            // پیش‌فرض = موجودی دفتری همان قرارداد (یعنی فرض «همهٔ این قرارداد ضایعه شده»).
            // کاربر این مقدار را با عدد واقعی اندازه‌گیری‌شده اصلاح می‌کند.
            LossMt = b.BalanceMt
        }).ToList();

    private async Task<(decimal CurrentMt, decimal SettleableMt, List<TankContractBalance> Balances)> LoadTankSettlementStateAsync(int tankId)
    {
        var stockCard = await _stock.GetStockCardAsync(storageTankId: tankId);
        var currentMt = stockCard.Sum(m => m.SignedQuantityMt);
        var balances = stockCard
            .Where(m => m.ContractId.HasValue)
            .GroupBy(m => new { m.ProductId, m.ProductName, ContractId = m.ContractId!.Value, m.ContractNumber })
            .Select(g => new TankContractBalance(
                g.Key.ProductId,
                g.Key.ProductName,
                g.Key.ContractId,
                g.Key.ContractNumber,
                g.Sum(m => m.SignedQuantityMt)))
            .Where(b => b.BalanceMt > 0m)
            .OrderByDescending(b => b.BalanceMt)
            .ThenBy(b => b.ContractNumber)
            .ToList();
        var settleableMt = balances.Sum(b => b.BalanceMt);
        return (currentMt, settleableMt, balances);
    }

    private static List<StorageTankSettlementContractRowViewModel> BuildSettlementRows(
        IReadOnlyList<TankContractBalance> balances,
        decimal settleableMt,
        decimal actualRemainingMt)
        => balances.Select(b =>
        {
            var remainingShare = settleableMt > 0m
                ? decimal.Round(actualRemainingMt * b.BalanceMt / settleableMt, 4, MidpointRounding.AwayFromZero)
                : 0m;
            var projectedLoss = decimal.Round(b.BalanceMt - remainingShare, 4, MidpointRounding.AwayFromZero);
            return new StorageTankSettlementContractRowViewModel
            {
                ProductId = b.ProductId,
                ProductName = b.ProductName,
                ContractId = b.ContractId,
                ContractNumber = b.ContractNumber,
                BookBalanceMt = b.BalanceMt,
                SharePercent = settleableMt > 0m ? Math.Round(b.BalanceMt / settleableMt * 100m, 2) : 0m,
                ProjectedLossMt = projectedLoss > 0m ? projectedLoss : 0m
            };
        }).ToList();

    private static void RehydrateSettlementModel(
        StorageTankSettlementViewModel model,
        StorageTank tank,
        string unit,
        decimal currentMt,
        decimal settleableMt,
        IReadOnlyList<TankContractBalance> balances)
    {
        model.TankCode = StorageTankDisplay.Build(tank);
        model.TerminalName = tank.Terminal?.Name ?? "";
        model.UnitOfMeasure = unit;
        model.CurrentQuantityMt = currentMt;
        model.SettleableQuantityMt = settleableMt;
        model.ContractRows = BuildSettlementRows(balances, settleableMt, model.ActualRemainingMt);
        // اگر ورودی دستی خالی بود (مثلاً خطای حالت نسبتی)، ردیف‌ها را بازسازی کن تا UI دستی خالی نماند.
        if (model.ManualLosses is null || model.ManualLosses.Count == 0)
        {
            model.ManualLosses = BuildManualLossInputs(balances);
        }
    }

    private static string BuildSettlementNotes(string tankCode, string? contractNumber, string? notes)
    {
        var prefix = $"تسویه نهایی مخزن {tankCode}"
            + (string.IsNullOrWhiteSpace(contractNumber) ? "" : $" | قرارداد {contractNumber}");
        var combined = string.IsNullOrWhiteSpace(notes) ? prefix : $"{prefix} | {notes.Trim()}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }

    private sealed record TankContractBalance(
        int ProductId,
        string ProductName,
        int ContractId,
        string? ContractNumber,
        decimal BalanceMt);

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        await PopulateLookupsAsync();
        return View(new StorageTank { IsActive = true });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,TerminalId,TankCode,DisplayName,ProductId,CapacityMt,IsActive,Notes")] StorageTank model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) { await PopulateLookupsAsync(model); return View(model); }
        if (await _db.StorageTanks.AnyAsync(t => t.TerminalId == model.TerminalId && t.TankCode == model.TankCode))
        {
            ModelState.AddModelError(nameof(model.TankCode), "این کد مخزن در ترمینال انتخابی قبلاً ثبت شده است.");
            await PopulateLookupsAsync(model);
            return View(model);
        }
        model.CreatedAtUtc = DateTime.UtcNow;
        _db.StorageTanks.Add(model);
        await _db.SaveChangesAsync();
        TempData["ok"] = "مخزن با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.StorageTanks.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        await PopulateLookupsAsync(item);
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,TerminalId,TankCode,DisplayName,ProductId,CapacityMt,IsActive,Notes")] StorageTank model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) { await PopulateLookupsAsync(model); return View(model); }
        var existing = await _db.StorageTanks.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();
        if ((existing.TerminalId != model.TerminalId || existing.TankCode != model.TankCode)
            && await _db.StorageTanks.AnyAsync(t => t.TerminalId == model.TerminalId && t.TankCode == model.TankCode))
        {
            ModelState.AddModelError(nameof(model.TankCode), "این کد مخزن در ترمینال انتخابی قبلاً ثبت شده است.");
            await PopulateLookupsAsync(model);
            return View(model);
        }
        existing.TerminalId = model.TerminalId;
        existing.TankCode = model.TankCode;
        existing.DisplayName = model.DisplayName;
        existing.ProductId = model.ProductId;
        existing.CapacityMt = model.CapacityMt;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["ok"] = "ویرایش با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.StorageTanks.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var safety = await _deleteSafety.EvaluateStorageTankAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(StorageTank), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("مخزن");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("مخزن")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("مخزن");
            return RedirectToAction(nameof(Index));
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("TankCode", item.TankCode),
            ("TerminalId", item.TerminalId),
            ("ProductId", item.ProductId),
            ("CapacityMt", item.CapacityMt));
        _db.StorageTanks.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(StorageTank), item.Id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "مخزن حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private static void Normalize(StorageTank model)
    {
        model.TankCode = (model.TankCode ?? string.Empty).Trim().ToUpperInvariant();
        model.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private async Task<Dictionary<int, MovementContext>> BuildMovementContextLookupAsync(
        IReadOnlyCollection<int> movementIds,
        string currentTankName)
    {
        if (movementIds.Count == 0)
        {
            return [];
        }

        var movements = await _db.InventoryMovements
            .AsNoTracking()
            .Include(m => m.LoadingReceipt)
                .ThenInclude(r => r!.LoadingRegister)
                    .ThenInclude(l => l!.OriginLocation)
            .Include(m => m.LoadingReceipt)
                .ThenInclude(r => r!.LoadingRegister)
                    .ThenInclude(l => l!.Vessel)
            .Include(m => m.LoadingReceipt)
                .ThenInclude(r => r!.LoadingRegister)
                    .ThenInclude(l => l!.Truck)
            .Include(m => m.SalesTransaction)
                .ThenInclude(s => s!.Customer)
            .Include(m => m.SalesTransaction)
                .ThenInclude(s => s!.DestinationLocation)
            .Where(m => movementIds.Contains(m.Id))
            .ToListAsync();

        var dispatchIds = movements.Select(m => ParseReferenceId(m.ReferenceDocument, "TRUCK-DISPATCH:")).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var unloadIds = movements.Select(m => ParseReferenceId(m.ReferenceDocument, "TRUCK-UNLOAD:")).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        dispatchIds.AddRange(unloadIds);
        dispatchIds = dispatchIds.Distinct().ToList();

        var transportLegIds = movements.Select(m => ParseReferenceId(m.ReferenceDocument, "TRANSPORT-LEG:")).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var transportReceiptIds = movements.Select(m => ParseReferenceId(m.ReferenceDocument, "TRANSPORT-RECEIPT:")).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        var dispatchById = dispatchIds.Count == 0
            ? new Dictionary<int, TruckDispatch>()
            : await _db.TruckDispatches
                .AsNoTracking()
                .Include(d => d.Truck)
                .Include(d => d.DestinationLocation)
                .Where(d => dispatchIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id);

        var transportLegById = transportLegIds.Count == 0
            ? new Dictionary<int, InventoryTransportLeg>()
            : await _db.InventoryTransportLegs
                .AsNoTracking()
                .Include(l => l.SourceTerminal)
                .Include(l => l.SourceStorageTank)
                .Include(l => l.DestinationTerminal)
                .Include(l => l.DestinationStorageTank)
                .Include(l => l.DestinationLocation)
                .Include(l => l.Truck)
                .Include(l => l.Wagon)
                .Include(l => l.Vessel)
                .Where(l => transportLegIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id);

        var transportReceiptById = transportReceiptIds.Count == 0
            ? new Dictionary<int, InventoryTransportReceipt>()
            : await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Include(r => r.DestinationTerminal)
                .Include(r => r.DestinationStorageTank)
                .Include(r => r.InventoryTransportLeg)
                    .ThenInclude(l => l!.SourceTerminal)
                .Include(r => r.InventoryTransportLeg)
                    .ThenInclude(l => l!.SourceStorageTank)
                .Include(r => r.InventoryTransportLeg)
                    .ThenInclude(l => l!.Truck)
                .Include(r => r.InventoryTransportLeg)
                    .ThenInclude(l => l!.Wagon)
                .Include(r => r.InventoryTransportLeg)
                    .ThenInclude(l => l!.Vessel)
                .Where(r => transportReceiptIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id);

        return movements.ToDictionary(
            m => m.Id,
            m => ResolveMovementContext(m, currentTankName, dispatchById, transportLegById, transportReceiptById));
    }

    private static MovementContext ResolveMovementContext(
        InventoryMovement movement,
        string currentTankName,
        IReadOnlyDictionary<int, TruckDispatch> dispatchById,
        IReadOnlyDictionary<int, InventoryTransportLeg> transportLegById,
        IReadOnlyDictionary<int, InventoryTransportReceipt> transportReceiptById)
    {
        if (movement.LoadingReceipt?.LoadingRegister is { } loading)
        {
            var source = FirstNonEmpty(
                loading.OriginLocation?.Name,
                loading.RouteDescription,
                loading.DestinationName,
                loading.ConsigneeName,
                loading.BillOfLadingNumber,
                loading.RwbNo,
                loading.WagonNumber,
                $"Loading #{loading.Id}");

            return new MovementContext(
                source,
                currentTankName,
                $"رسید بارگیری #{movement.LoadingReceipt.Id}",
                BuildTransportSummary(
                    loading.TransportType,
                    loading.Vessel?.Name,
                    loading.Truck?.PlateNumber,
                    FirstNonEmptyOrNull(loading.WagonNumber, loading.RwbNo)));
        }

        if (movement.SalesTransaction is { } sale)
        {
            var destination = FirstNonEmpty(
                sale.DestinationLocation?.Name,
                sale.Customer?.Name,
                sale.InvoiceNumber);

            return new MovementContext(
                currentTankName,
                destination,
                $"فروش / فاکتور {sale.InvoiceNumber}");
        }

        if (ParseReferenceId(movement.ReferenceDocument, "TRUCK-DISPATCH:") is { } dispatchId
            && dispatchById.TryGetValue(dispatchId, out var dispatch))
        {
            var destination = FirstNonEmpty(
                dispatch.DestinationLocation?.Name,
                dispatch.Truck?.PlateNumber,
                $"Dispatch #{dispatch.Id}");

            return new MovementContext(
                currentTankName,
                destination,
                $"دیسپچ موتر #{dispatch.Id}");
        }

        if (ParseReferenceId(movement.ReferenceDocument, "TRUCK-UNLOAD:") is { } unloadId
            && dispatchById.TryGetValue(unloadId, out var unloadDispatch))
        {
            var source = FirstNonEmpty(
                unloadDispatch.DestinationLocation?.Name,
                unloadDispatch.Truck?.PlateNumber,
                $"Dispatch #{unloadDispatch.Id}");

            return new MovementContext(
                source,
                currentTankName,
                $"رسید برگشتی دیسپچ #{unloadDispatch.Id}",
                BuildTransportSummary(
                    LoadingTransportType.Truck,
                    vesselName: null,
                    truckPlateNumber: unloadDispatch.Truck?.PlateNumber,
                    wagonNumber: null));
        }

        if (ParseReferenceId(movement.ReferenceDocument, "TRANSPORT-LEG:") is { } legId
            && transportLegById.TryGetValue(legId, out var leg))
        {
            return new MovementContext(
                BuildTankLocation(leg.SourceTerminal?.Name, StorageTankDisplay.BuildOptional(leg.SourceStorageTank)),
                ResolveTransportLegDestination(leg),
                $"انتقال موجودی #{leg.Id}",
                BuildTransportLegSummary(leg));
        }

        if (ParseReferenceId(movement.ReferenceDocument, "TRANSPORT-RECEIPT:") is { } receiptId
            && transportReceiptById.TryGetValue(receiptId, out var receipt))
        {
            var source = receipt.InventoryTransportLeg is null
                ? $"Transport receipt #{receipt.Id}"
                : BuildTankLocation(receipt.InventoryTransportLeg.SourceTerminal?.Name, StorageTankDisplay.BuildOptional(receipt.InventoryTransportLeg.SourceStorageTank));

            return new MovementContext(
                source,
                BuildTankLocation(receipt.DestinationTerminal?.Name, StorageTankDisplay.BuildOptional(receipt.DestinationStorageTank)),
                $"رسید انتقال #{receipt.Id}",
                receipt.InventoryTransportLeg is null ? null : BuildTransportLegSummary(receipt.InventoryTransportLeg));
        }

        if ((movement.ReferenceDocument ?? string.Empty).StartsWith("LOSS-", StringComparison.OrdinalIgnoreCase))
        {
            return new MovementContext(
                currentTankName,
                "کسری / ضایعات",
                "ثبت کسری");
        }

        return DefaultMovementContext(movement.Direction, currentTankName, movement.ReferenceDocument);
    }

    private static MovementContext DefaultMovementContext(MovementDirection direction, string currentTankName, string? reference)
        => direction == MovementDirection.In
            ? new MovementContext(FirstNonEmpty(reference, "ثبت ورودی"), currentTankName, FirstNonEmpty(reference, "حرکت موجودی"))
            : new MovementContext(currentTankName, FirstNonEmpty(reference, "ثبت خروجی"), FirstNonEmpty(reference, "حرکت موجودی"));

    private static string ResolveTransportLegDestination(InventoryTransportLeg leg)
        => FirstNonEmpty(
            BuildTankLocation(leg.DestinationTerminal?.Name, StorageTankDisplay.BuildOptional(leg.DestinationStorageTank)),
            leg.DestinationLocation?.Name,
            leg.RouteDescription,
            $"Transport leg #{leg.Id}");

    private static int? ParseReferenceId(string? reference, string prefix)
    {
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var idText = reference[prefix.Length..].Split([' ', '/', '|', '-'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(idText, out var id) ? id : null;
    }

    private static string BuildTankLocation(string? terminalName, string? tankCode)
        => FirstNonEmpty(
            !string.IsNullOrWhiteSpace(terminalName) && !string.IsNullOrWhiteSpace(tankCode)
                ? $"{terminalName} / {tankCode}"
                : null,
            terminalName,
            tankCode,
            "-");

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "-";

    private static string? FirstNonEmptyOrNull(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    // شماره وسیلهٔ یک پایهٔ انتقال موجودی (واگن ثبت‌شده یا شماره دستی واگن/RWB).
    private static string? BuildTransportLegSummary(InventoryTransportLeg leg)
        => BuildTransportSummary(
            leg.TransportType,
            leg.Vessel?.Name,
            leg.Truck?.PlateNumber,
            FirstNonEmptyOrNull(leg.Wagon?.WagonNumber, leg.WagonNumber, leg.RwbNo));

    // شماره وسیله و نوع آن («واگن: 1234») برای نمایش در ستون «از کجا».
    private static string? BuildTransportSummary(
        LoadingTransportType transportType,
        string? vesselName,
        string? truckPlateNumber,
        string? wagonNumber)
        => transportType switch
        {
            LoadingTransportType.Vessel when !string.IsNullOrWhiteSpace(vesselName) => $"کشتی: {vesselName.Trim()}",
            LoadingTransportType.Wagon when !string.IsNullOrWhiteSpace(wagonNumber) => $"واگن: {wagonNumber.Trim()}",
            LoadingTransportType.Truck when !string.IsNullOrWhiteSpace(truckPlateNumber) => $"موتر: {truckPlateNumber.Trim()}",
            _ when !string.IsNullOrWhiteSpace(vesselName) => $"کشتی: {vesselName.Trim()}",
            _ when !string.IsNullOrWhiteSpace(wagonNumber) => $"واگن: {wagonNumber.Trim()}",
            _ when !string.IsNullOrWhiteSpace(truckPlateNumber) => $"موتر: {truckPlateNumber.Trim()}",
            _ => null
        };

    private sealed record MovementContext(
        string SourceName,
        string DestinationName,
        string Context,
        string? SourceTransport = null);
}
