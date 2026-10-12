using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ContractJourneyController : Controller
{
    private static readonly string[] PaymentLedgerSourceTypes = Enum.GetNames<PaymentKind>();

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IPricingService _pricing;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IPartyBalanceReadService _partyBalances;
    private readonly IProfitAndLossService _profitAndLoss;

    public ContractJourneyController(
        ApplicationDbContext db,
        IStockService stock,
        IPricingService? pricing = null,
        IPurchaseAggregationService? purchaseAggregation = null,
        IProfitAndLossService? profitAndLoss = null,
        IPartyBalanceReadService? partyBalances = null)
    {
        _db = db;
        _stock = stock;
        _pricing = pricing ?? new PricingService(db);
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _profitAndLoss = profitAndLoss ?? new ProfitAndLossService(db);
        _partyBalances = partyBalances ?? PartyBalanceReadService.CreateDefault(db);
    }

    /// <summary>
    /// خلاصهٔ سودِ پروندهٔ قرارداد فقط از ProfitAndLossService.BuildContractEconomicsAsync؛ همان
    /// اعدادِ گزارش مفاد قراردادها و صورت‌حساب شراکت.
    /// </summary>
    private async Task<ContractJourneyMiniPnlViewModel> BuildMiniPnlAsync(
        int contractId,
        RealisedPnlViewModel? realised,
        string note)
    {
        var economics = (await _profitAndLoss.BuildContractEconomicsAsync(
                [contractId],
                HttpContext?.RequestAborted ?? CancellationToken.None))
            .GetValueOrDefault(contractId) ?? new ContractEconomicsSnapshot { ContractId = contractId };
        return new ContractJourneyMiniPnlViewModel
        {
            Realised = realised ?? RealisedPnlViewModel.Empty,
            TraceableSalesRevenueUsd = economics.RevenueUsd,
            SoldQuantityMt = economics.SoldQuantityMt,
            TraceablePurchaseCostUsd = economics.PurchaseValueUsd,
            PricedPurchaseQuantityMt = economics.PricedLoadedMt,
            PendingPurchaseQuantityMt = economics.PendingLoadedMt,
            WeightedAveragePurchasePriceUsd = economics.WeightedAveragePurchasePriceUsd,
            TraceableExpensesUsd = economics.OperationalCostBaseUsd,
            RecordedExpensesUsd = economics.RecordedExpenseCostUsd,
            LossCostUsd = economics.LossCostUsd + economics.ShipmentLossCostUsd,
            UnsoldCostUsd = economics.UnsoldCostUsd,
            Confidence = economics.Confidence,
            CostOfGoodsSoldUsd = economics.RealizedCostOfGoodsSoldUsd,
            SoldShareRatio = economics.SoldShareRatio,
            ExpensesForSoldUsd = economics.RealizedOperationalCostUsd,
            GrossMarginUsd = economics.RealizedGrossMarginUsd,
            RealizedFxNetUsd = economics.RealizedFxNetUsd,
            RealizedNetProfitUsd = economics.RealizedNetProfitUsd,
            LifecycleMarginUsd = economics.LifecycleMarginUsd,
            Note = note
        };
    }

    /// <summary>
    /// ماندهٔ قرارداد فقط از موتورِ رسمی (جمعِ ماندهٔ طرف‌حساب‌های قرارداد) — همان عددِ گزارش
    /// «مانده قراردادها» و بستن قرارداد. مثبت = طلب شرکت، منفی = بدهی شرکت.
    /// </summary>
    private async Task<decimal> ReadContractBalanceUsdAsync(int contractId)
        => (await _partyBalances.GetContractBalancesAsync([contractId], ct: HttpContext?.RequestAborted ?? CancellationToken.None))
            .TryGetValue(contractId, out var balance)
                ? balance.NetBalanceUsd
                : 0m;

    private async Task PopulateContractsAsync(int? selectedContractId = null)
    {
        ViewBag.Contracts = new SelectList(
            ContractUiText.ToLookupOptions(
                await _db.Contracts
                    .AsNoTracking()
                    .Include(c => c.Product)
                    .Include(c => c.Unit)
                    .OrderByDescending(c => c.ContractDate)
                    .ThenBy(c => c.ContractNumber)
                    .ToListAsync()),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedContractId);
    }

    public async Task<IActionResult> Index(int? contractId = null, string? tab = null)
    {
        if (contractId.HasValue)
        {
            return RedirectToAction(nameof(Details), new { contractId = contractId.Value });
        }

        var activeTab = ContractJourneyTabs.Index.Normalize(tab);
        await PopulateContractsAsync();

        return View(new ContractJourneyIndexViewModel
        {
            ActiveTab = activeTab,
            Items = await BuildIndexItemsAsync()
        });
    }

    /// <summary>
    /// خروجی Excel/PDF فهرست قراردادها — دقیقاً همان ردیف‌های صفحهٔ Index، بدون هیچ محاسبهٔ جدید.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format)
    {
        var items = await BuildIndexItemsAsync();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Contract_Journey",
            TitleFa = "مسیر قرارداد",
            TitleEn = "Contract Journey",
            KnownRowCount = items.Count,
            Columns =
            [
                new("قرارداد", "Contract", Width: 18), new("نوع", "Type", Width: 12),
                new("جنس", "Product", Width: 18), new("طرف قرارداد", "Counterparty", Width: 22),
                new("واحد", "Unit", Width: 10),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
                new("تاریخ قرارداد", "Contract date", TabularExportValueType.Date, 14),
                new("وضعیت", "Status", Width: 14)
            ],
            Rows = items.Select(item => new TabularExportRow(
            [
                TabularExportCell.Text(item.ContractNumber), TabularExportCell.Text(item.ContractTypeName),
                TabularExportCell.Text(item.ProductName), TabularExportCell.Text(item.PartnerName),
                TabularExportCell.Text(item.ContractUnitText), TabularExportCell.Number(item.QuantityMt),
                TabularExportCell.Date(item.ContractDate), TabularExportCell.Text(item.StatusName)
            ]))
        });
    }

    private async Task<List<ContractJourneyIndexItemViewModel>> BuildIndexItemsAsync()
        => await _db.Contracts
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Include(c => c.Supplier)
            .Include(c => c.Customer)
            .AsNoTracking()
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Select(c => new ContractJourneyIndexItemViewModel
            {
                ContractId = c.Id,
                ContractName = c.ContractName,
                ContractNumber = c.ContractNumber,
                ContractTypeName = ToContractTypeName(c.ContractType),
                ContractTypeBadgeClass = c.ContractType == ContractType.Purchase
                    ? "status-badge status-badge-info"
                    : "status-badge status-badge-warning",
                ProductName = c.Product != null ? c.Product.Name : string.Empty,
                ContractUnitText = c.Unit != null
                    ? c.Unit.Symbol ?? c.Unit.Code ?? c.Unit.NamePersian ?? c.Unit.Name ?? "—"
                    : "—",
                PartnerName = c.ContractType == ContractType.Purchase
                    ? c.Supplier != null ? c.Supplier.Name : "—"
                    : c.Customer != null ? c.Customer.Name : "—",
                QuantityMt = c.QuantityMt,
                ContractDate = c.ContractDate,
                StatusName = ToContractStatusName(c.Status)
            })
            .ToListAsync();

    public async Task<IActionResult> Details(int contractId, string? tab = null, bool lockContract = false)
    {
        var activeTab = ContractJourneyTabs.Details.Normalize(tab);
        var isTabFragmentRequest = string.Equals(
            ControllerContext.HttpContext?.Request.Headers["X-PTG-Contract-Tab"].ToString(),
            "1",
            StringComparison.Ordinal);

        var contract = await _db.Contracts
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Include(c => c.Company)
            .Include(c => c.Supplier)
            .Include(c => c.Customer)
            .Include(c => c.ParentContract)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contractId);

        if (contract is null)
        {
            return NotFound();
        }

        // قرارداد بسته فقط قابل مشاهده است؛ نمای جزئیات از این برای منوی عملیات و نوار اطلاع استفاده می‌کند.
        ViewData["ContractIsClosed"] = contract.Status == ContractStatus.Closed;

        var needsSummaryContext =activeTab == ContractJourneyTabs.Details.Summary || !isTabFragmentRequest;
        var (hasMixedLoadingPrices, loadingsValueUsd) = needsSummaryContext
            ? await LoadLoadingPriceSpreadAsync(_db, contract.Id)
            : (false, 0m);
        var subContractItems = needsSummaryContext
            ? await LoadSubContractSummariesAsync(_db, contract.Id)
            : [];
        var partnerShareItems = needsSummaryContext && contract.OwnershipType == ContractOwnershipType.Partnership
            ? await LoadContractPartnerSharesAsync(_db, contract.Id)
            : [];

        var pricingResult = await _pricing.CalculateContractPriceAsync(contract);
        var journeyReturnUrl = $"/ContractJourney/Details?contractId={contract.Id}&tab={activeTab}&lockContract={lockContract.ToString().ToLowerInvariant()}";
        var editPricingUrl = $"/Contracts/EditPricing/{contract.Id}?returnUrl={Uri.EscapeDataString(journeyReturnUrl)}";

        var baseModel = new ContractJourneyDetailsViewModel
        {
            ContractId = contract.Id,
            ContractName = contract.ContractName,
            ContractNumber = contract.ContractNumber,
            ContractTypeName = ToContractTypeName(contract.ContractType),
            ContractTypeBadgeClass = contract.ContractType == ContractType.Purchase
                ? "status-badge status-badge-info"
                : "status-badge status-badge-warning",
            CompanyName = contract.Company?.Name ?? string.Empty,
            ProductName = contract.Product?.Name ?? string.Empty,
            ContractUnitText = contract.Unit?.Symbol ?? contract.Unit?.Code ?? contract.Unit?.NamePersian ?? contract.Unit?.Name ?? "—",
            SupplierName = contract.Supplier?.Name,
            CustomerName = contract.Customer?.Name,
            ContractQuantityMt = contract.QuantityMt,
            HasMixedLoadingPrices = hasMixedLoadingPrices,
            LoadingsValueUsd = loadingsValueUsd,
            ParentContractId = contract.ParentContractId,
            ParentContractNumber = contract.ParentContract?.DisplayLabel,
            SubContractItems = subContractItems,
            IsPartnershipContract = contract.OwnershipType == ContractOwnershipType.Partnership,
            PartnerShareItems = partnerShareItems,
            Currency = contract.Currency,
            PriceDisplay = ToResolvedPriceDisplay(contract, pricingResult),
            PricingMethodName = ToPricingMethodName(contract),
            PricingStatusName = ContractPricingAdapter.GetPricingStatusLabel(contract),
            RubSettlementSummary = BuildRubSettlementSummary(contract, Array.Empty<LoadingRegister>(), pricingResult.FinalUnitPrice),
            EditPricingUrl = editPricingUrl,
            PricingFormulaText = pricingResult.FormulaText,
            PricingFinalUnitPriceUsd = pricingResult.FinalUnitPrice,
            PricingNeedsReview = pricingResult.NeedsReview,
            PricingReason = pricingResult.Reason,
            PricingFallbackApplied = pricingResult.FallbackApplied,
            PricingFormulaNote = contract.PricingFormulaNote,
            StatusName = ToContractStatusName(contract.Status),
            StatusBadgeClass = ToContractStatusBadgeClass(contract.Status),
            ContractDate = contract.ContractDate,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            Notes = contract.Notes,
            IsPurchaseContract = contract.ContractType == ContractType.Purchase
        };

        if (contract.ContractType != ContractType.Purchase)
        {
            return View(await BuildSaleContractDetailsAsync(contract, baseModel, activeTab, lockContract));
        }

        if (activeTab == ContractJourneyTabs.Details.Summary && ControllerContext.HttpContext is not null)
        {
            return View(await BuildPurchaseInitialSummaryDetailsAsync(contract, baseModel, pricingResult.FinalUnitPrice, lockContract));
        }

        // تب «رسید بارگیری» فقط بارگیری‌ها، رسیدها و کسری رسید را نشان می‌دهد. مسیر کامل
        // پایین (فروش، دیسپچ، مصارف، پرداخت، دفتر کل، P&L) هیچ‌کدام در این تب رندر نمی‌شوند،
        // پس مثل تب خلاصه یک مسیر سبک جدا دارد: ۵ کوئری به‌جای ~۴۰.
        if (activeTab == ContractJourneyTabs.Details.Receipts)
        {
            return View(await BuildPurchaseReceiptsDetailsAsync(contract, baseModel, pricingResult, lockContract));
        }

        if (isTabFragmentRequest && activeTab == ContractJourneyTabs.Details.Inventory)
        {
            return View(await BuildPurchaseInventoryDetailsAsync(contract.Id, baseModel, lockContract));
        }

        if (isTabFragmentRequest && activeTab == ContractJourneyTabs.Details.Dispatch)
        {
            return View(await BuildPurchaseDispatchDetailsAsync(contract.Id, baseModel, lockContract));
        }

        var notesForReview = new List<string>();
        var contractFinalPriceUsd = pricingResult.FinalUnitPrice;
        var needsInventoryTransportRowDetails = activeTab == ContractJourneyTabs.Details.InventoryTransport;
        var needsShipmentScenarios = activeTab == ContractJourneyTabs.Details.Sales;

        var loadingRegisters = await _db.LoadingRegisters
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .AsNoTracking()
            .Where(l => l.ContractId == contractId && !l.IsArchived && !l.IsCancelled)
            .OrderBy(l => l.LoadingDate)
            .ThenBy(l => l.Id)
            .ToListAsync();
        var loadingIds = loadingRegisters.Select(l => l.Id).ToList();
        var loadingById = loadingRegisters.ToDictionary(l => l.Id);
        var loadingIdsWithOfficialExpenses = await LoadLoadingIdsWithOfficialExpensesAsync(loadingIds);
        var loadingIdsWithExpenseLines = await LoadLoadingIdsWithExpenseLinesAsync(loadingIds);
        var mirroredDriverExpenses = await _purchaseAggregation.LoadMirroredDriverExpenseAmountsAsync(loadingIds);

        // Single source of truth for purchase quantity / cost / averages.
        // Computed eagerly so every downstream aggregation (KPIs, mini
        // P&L, timeline, journey items) reads from the same snapshot and
        // future filtering (e.g. excluding InventoryTransportLeg rows)
        // happens in exactly one place: the service.
        var purchaseAgg = _purchaseAggregation.AggregateForLoadedRegisters(
            contract.Id,
            loadingRegisters,
            contractFinalPriceUsd,
            loadingIdsWithOfficialExpenses,
            loadingIdsWithExpenseLines,
            mirroredDriverExpenses);
        var rubSettlementSummary = BuildRubSettlementSummary(contract, loadingRegisters, contractFinalPriceUsd);

        decimal? ResolveEffectiveLoadingPriceUsd(LoadingRegister loading)
            => HasValidLoadingPrice(loading.LoadingPriceUsd)
                ? loading.LoadingPriceUsd
                : contractFinalPriceUsd;

        var productName = contract.Product?.Name ?? string.Empty;
        var receiptQuantityByLoadingId = loadingIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.LoadingReceipts
                .AsNoTracking()
                .Where(r => loadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled)
                .GroupBy(r => r.LoadingRegisterId)
                .Select(g => new { LoadingRegisterId = g.Key, TotalReceived = g.Sum(r => r.ReceivedQuantityMt) })
                .ToDictionaryAsync(g => g.LoadingRegisterId, g => g.TotalReceived);

        var loadingItems = BuildLoadingItems(
            loadingRegisters,
            productName,
            receiptQuantityByLoadingId,
            pricingResult.BasePlattsPrice,
            pricingResult.PremiumDiscountUsd,
            ResolveEffectiveLoadingPriceUsd);
        var loadingItemById = loadingItems.ToDictionary(item => item.Id);
        var totalLoadedQuantityMt = purchaseAgg.TotalLoadedQuantityMt;

        if (isTabFragmentRequest && activeTab == ContractJourneyTabs.Details.Loadings)
        {
            return View(BuildPurchaseTabPayload(
                baseModel,
                activeTab,
                lockContract,
                rubSettlementSummary: rubSettlementSummary,
                loadingItems: loadingItems));
        }

        var inventoryTransportLegs = await _db.InventoryTransportLegs
            .Include(l => l.SourceTerminal)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Include(l => l.DestinationLocation)
            // برای نمایش «نمبر وسیله» در سفرهای موتری.
            .Include(l => l.Truck)
            .AsNoTracking()
            .Where(l => l.SourcePurchaseContractId == contractId
                || l.Allocations.Any(a => a.SourcePurchaseContractId == contractId))
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToListAsync();
        var inventoryTransportLegIds = inventoryTransportLegs.Select(l => l.Id).ToList();
        var inventoryTransportLegById = inventoryTransportLegs.ToDictionary(l => l.Id);

        var inventoryTransportReceipts = new List<InventoryTransportReceipt>();
        if (inventoryTransportLegIds.Count > 0)
        {
            var inventoryTransportReceiptQuery = _db.InventoryTransportReceipts
                .AsNoTracking()
                .Include(r => r.DestinationTerminal)
                .Include(r => r.DestinationStorageTank)
                .Include(r => r.SalesTransaction)
                .Where(r => inventoryTransportLegIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled);

            if (needsInventoryTransportRowDetails)
            {
                inventoryTransportReceiptQuery = inventoryTransportReceiptQuery
                    .Include(r => r.DirectTruckDispatches)
                    .AsSplitQuery();
            }

            inventoryTransportReceipts = await inventoryTransportReceiptQuery
                .OrderBy(r => r.ReceiptDate)
                .ThenBy(r => r.Id)
                .ToListAsync();
        }
        var inventoryTransportReceiptByLegId = inventoryTransportReceipts
            .GroupBy(r => r.InventoryTransportLegId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Id).First());
        var inventoryTransportReceiptById = inventoryTransportReceipts
            .ToDictionary(r => r.Id);
        var inventoryTransportExpenseRows = !needsInventoryTransportRowDetails || inventoryTransportLegIds.Count == 0
            ? []
            : await _db.ExpenseTransactions
                .AsNoTracking()
                .Include(e => e.ExpenseType)
                .Where(e => e.TransportLegId.HasValue && inventoryTransportLegIds.Contains(e.TransportLegId.Value) && !e.IsCancelled)
                .OrderByDescending(e => e.ExpenseDate)
                .ThenByDescending(e => e.Id)
                .Select(e => new
                {
                    LegId = e.TransportLegId!.Value,
                    Item = new ContractJourneyTransportExpenseDetailViewModel
                    {
                        Id = e.Id,
                        ExpenseDate = e.ExpenseDate,
                        ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.Name : "",
                        AmountUsd = e.AmountUsd,
                        Description = e.Description
                    }
                })
                .ToListAsync();
        var inventoryTransportExpensesByLegId = inventoryTransportExpenseRows
            .GroupBy(e => e.LegId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Item).ToList());
        var inventoryTransportCustomsRows = !needsInventoryTransportRowDetails || inventoryTransportLegIds.Count == 0
            ? []
            : await _db.CustomsDeclarations
                .AsNoTracking()
                .Where(cd => cd.TransportLegId.HasValue && inventoryTransportLegIds.Contains(cd.TransportLegId.Value))
                .OrderByDescending(cd => cd.DeclarationDate)
                .ThenByDescending(cd => cd.Id)
                .Select(cd => new
                {
                    LegId = cd.TransportLegId!.Value,
                    Item = new ContractJourneyTransportCustomsDetailViewModel
                    {
                        Id = cd.Id,
                        DeclarationDate = cd.DeclarationDate,
                        WagonOrTruckNumber = cd.WagonOrTruckNumber,
                        DeclarationReference = cd.DeclarationReference,
                        ConsignmentWeightMt = cd.ConsignmentWeightMt,
                        TotalAfn = cd.TotalAfn,
                        TotalUsd = cd.TotalUsd
                    }
                })
                .ToListAsync();
        var inventoryTransportCustomsByLegId = inventoryTransportCustomsRows
            .GroupBy(cd => cd.LegId)
            .ToDictionary(g => g.Key, g => g.Select(cd => cd.Item).ToList());
        var inventoryTransportLossRows = !needsInventoryTransportRowDetails || inventoryTransportLegIds.Count == 0
            ? []
            : await _db.LossEvents
                .AsNoTracking()
                .Where(le => le.TransportLegId.HasValue && inventoryTransportLegIds.Contains(le.TransportLegId.Value) && !le.IsCancelled)
                .OrderByDescending(le => le.EventDate)
                .ThenByDescending(le => le.Id)
                .Select(le => new
                {
                    LegId = le.TransportLegId!.Value,
                    Item = new ContractJourneyTransportLossDetailViewModel
                    {
                        Id = le.Id,
                        EventDate = le.EventDate,
                        StageName = le.Stage.ToString(),
                        ExpectedQuantityMt = le.ExpectedQuantityMt,
                        ActualQuantityMt = le.ActualQuantityMt,
                        DifferenceQuantityMt = le.DifferenceQuantityMt,
                        ChargeableLossMt = le.ChargeableLossMt,
                        Reference = le.Reference
                    }
                })
                .ToListAsync();
        var inventoryTransportLossesByLegId = inventoryTransportLossRows
            .GroupBy(le => le.LegId)
            .ToDictionary(g => g.Key, g => g.Select(le => le.Item).ToList());
        var inventoryTransportPnlByLegId = !needsInventoryTransportRowDetails || inventoryTransportLegIds.Count == 0
            ? new Dictionary<int, InventoryTransportPnlSnapshot>()
            : (await new InventoryTransportPnlService(_db).BuildForLegsAsync(inventoryTransportLegIds))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var inventoryTransportLegItems = inventoryTransportLegs
            .Select(l =>
            {
                inventoryTransportReceiptByLegId.TryGetValue(l.Id, out var receipt);
                inventoryTransportExpensesByLegId.TryGetValue(l.Id, out var legExpenses);
                inventoryTransportCustomsByLegId.TryGetValue(l.Id, out var legCustoms);
                inventoryTransportLossesByLegId.TryGetValue(l.Id, out var legLosses);
                inventoryTransportPnlByLegId.TryGetValue(l.Id, out var legPnl);
                return new ContractJourneyTransportLegItemViewModel
                {
                    Id = l.Id,
                    LoadedDate = l.LoadedDate,
                    ReceivedDate = receipt?.ReceiptDate,
                    TransportTypeName = ToLoadingTransportTypeName(l.TransportType),
                    WagonNumber = l.WagonNumber,
                    VehicleNumber = BuildVehicleNumber(l),
                    RwbNo = l.RwbNo,
                    SourceTerminalName = l.SourceTerminal?.Name ?? string.Empty,
                    SourceTankCode = StorageTankDisplay.BuildOptional(l.SourceStorageTank),
                    DestinationTerminalName = l.DestinationTerminal?.Name,
                    DestinationTankCode = StorageTankDisplay.BuildOptional(l.DestinationStorageTank),
                    QuantityMt = l.QuantityMt,
                    ReceivedQuantityMt = receipt?.ReceivedQuantityMt ?? 0m,
                    ShortageQuantityMt = receipt?.ShortageQuantityMt ?? 0m,
                    StatusName = l.Status.ToString(),
                    OutboundInventoryMovementId = l.OutboundInventoryMovementId,
                    DestinationReceiptId = receipt?.Id,
                    PurchaseUnitCostUsd = legPnl?.PurchaseUnitCostUsd,
                    PurchaseCostUsd = legPnl?.PurchaseCostUsd ?? 0m,
                    OperationalExpensesUsd = legPnl?.OperationalExpensesUsd ?? 0m,
                    SalesUsd = legPnl?.SalesUsd ?? 0m,
                    SoldQuantityMt = legPnl?.SoldQuantityMt ?? 0m,
                    TotalCostUsd = legPnl?.TotalCostUsd ?? 0m,
                    GrossMarginUsd = legPnl?.GrossMarginUsd ?? 0m,
                    UnsoldQuantityMt = legPnl?.UnsoldQuantityMt ?? l.QuantityMt,
                    PnlTraceNote = legPnl?.SalesTraceNote ?? "No traceable sale",
                    DestinationReceipt = receipt is null
                        ? null
                        : new ContractJourneyTransportReceiptDetailViewModel
                        {
                            Id = receipt.Id,
                            ReceiptDate = receipt.ReceiptDate,
                            ReceiptDestination = receipt.ReceiptDestination,
                            ReceivedQuantityMt = receipt.ReceivedQuantityMt,
                            ShortageQuantityMt = receipt.ShortageQuantityMt,
                            AllowanceMt = receipt.AllowanceMt,
                            ChargeableShortageMt = receipt.ChargeableShortageMt,
                            FreightRateUsdPerMt = receipt.FreightRateUsdPerMt,
                            FreightCostUsd = receipt.FreightCostUsd,
                            ShortageRateUsd = receipt.ShortageRateUsd,
                            ShortageChargeUsd = receipt.ShortageChargeUsd,
                            FreightPayableUsd = receipt.FreightPayableUsd,
                            DestinationTerminalName = receipt.DestinationTerminal?.Name,
                            DestinationTankCode = StorageTankDisplay.BuildOptional(receipt.DestinationStorageTank),
                            InventoryMovementId = receipt.InventoryMovementId,
                            SalesTransactionId = receipt.SalesTransactionId,
                            SaleInvoiceNumber = receipt.SalesTransaction?.InvoiceNumber,
                            DirectTruckDispatchCount = receipt.DirectTruckDispatches.Count(d => d.Status != DispatchStatus.Cancelled),
                            DirectTruckDispatchedQuantityMt = receipt.DirectTruckDispatches
                                .Where(d => d.Status != DispatchStatus.Cancelled)
                                .Sum(d => d.LoadedQuantityMt),
                            FirstDirectTruckDispatchId = receipt.DirectTruckDispatches
                                .Where(d => d.Status != DispatchStatus.Cancelled)
                                .OrderBy(d => d.Id)
                                .Select(d => (int?)d.Id)
                                .FirstOrDefault()
                        },
                    Expenses = legExpenses ?? [],
                    CustomsDeclarations = legCustoms ?? [],
                    Losses = legLosses ?? []
                };
            })
            .ToList();

        if (isTabFragmentRequest && activeTab == ContractJourneyTabs.Details.InventoryTransport)
        {
            return View(BuildPurchaseTabPayload(
                baseModel,
                activeTab,
                lockContract,
                inventoryTransportLegItems: inventoryTransportLegItems));
        }

        var loadingReceipts = loadingIds.Count == 0
            ? new List<LoadingReceipt>()
            : await _db.LoadingReceipts
                .Include(r => r.Terminal)
                .Include(r => r.StorageTank)
                .Include(r => r.InventoryMovement)
                .AsNoTracking()
                .Where(r => loadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled)
                .OrderBy(r => r.LoadingRegisterId)
                .ThenBy(r => r.ReceiptDate)
                .ThenBy(r => r.Id)
                .ToListAsync();
        var receiptIds = loadingReceipts.Select(r => r.Id).ToList();
        var receiptAllocations = receiptIds.Count == 0
            ? new List<LoadingReceiptAllocation>()
            : await _db.LoadingReceiptAllocations
                .Include(a => a.LoadingReceipt)
                .Include(a => a.SourcePurchaseContract)
                .Include(a => a.Terminal)
                .Include(a => a.StorageTank)
                .Include(a => a.DestinationTerminal)
                .Include(a => a.DestinationStorageTank)
                .Include(a => a.DestinationLocation)
                .Include(a => a.SalesTransaction)
                .Include(a => a.DirectTruckDispatches)
                .AsSplitQuery()
                .AsNoTracking()
                .Where(a => receiptIds.Contains(a.LoadingReceiptId))
                .OrderBy(a => a.LoadingReceipt!.ReceiptDate)
                .ThenBy(a => a.Id)
                .ToListAsync();

        var receiptItems = new List<ContractJourneyReceiptItemViewModel>();
        foreach (var receiptGroup in loadingReceipts.GroupBy(r => r.LoadingRegisterId))
        {
            var loadedQuantityMt = loadingById.TryGetValue(receiptGroup.Key, out var loading)
                ? loading.LoadedQuantityMt
                : 0m;
            decimal cumulativeReceiptQuantityMt = 0m;

            foreach (var receipt in receiptGroup.OrderBy(r => r.ReceiptDate).ThenBy(r => r.Id))
            {
                cumulativeReceiptQuantityMt += receipt.ReceivedQuantityMt;
                receiptItems.Add(new ContractJourneyReceiptItemViewModel
                {
                    Id = receipt.Id,
                    LoadingRegisterId = receipt.LoadingRegisterId,
                    LoadingVehicleNumber = BuildVehicleNumber(loading),
                    ReceiptDate = receipt.ReceiptDate,
                    ReceivedQuantityMt = receipt.ReceivedQuantityMt,
                    TerminalName = receipt.Terminal?.Name ?? string.Empty,
                    StorageTankCode = StorageTankDisplay.BuildOptional(receipt.StorageTank),
                    InventoryMovementId = receipt.InventoryMovement?.Id,
                    RemainingLoadingMt = Math.Max(loadedQuantityMt - cumulativeReceiptQuantityMt, 0m),
                    ActualArrivedQuantityMt = receipt.ActualArrivedQuantityMt,
                    DifferenceQuantityMt = receipt.ActualArrivedQuantityMt.HasValue
                        ? receipt.ActualArrivedQuantityMt.Value - receipt.ReceivedQuantityMt
                        : null,
                    ReferenceDocument = receipt.ReferenceDocument ?? receipt.InventoryMovement?.ReferenceDocument
                });
            }
        }
        var receiptAllocationItems = receiptAllocations
            .Select(a =>
            {
                var activeDirectDispatches = a.DirectTruckDispatches
                    .Where(d => d.Status != DispatchStatus.Cancelled)
                    .ToList();
                var directDispatchedQuantityMt = activeDirectDispatches.Sum(d => d.LoadedQuantityMt);
                var linkedDispatchId = a.TruckDispatchId
                    ?? activeDirectDispatches
                        .OrderBy(d => d.Id)
                        .Select(d => (int?)d.Id)
                        .FirstOrDefault();
                var hasQuantityMismatch =
                    (a.SalesTransaction is not null && a.SalesTransaction.QuantityMt != a.QuantityMt)
                    || (directDispatchedQuantityMt > 0m
                        && a.Status == LoadingReceiptAllocationStatus.Completed
                        && directDispatchedQuantityMt != a.QuantityMt);

                return new ContractJourneyReceiptAllocationItemViewModel
                {
                    Id = a.Id,
                    LoadingReceiptId = a.LoadingReceiptId,
                    ReceiptDate = a.LoadingReceipt?.ReceiptDate ?? DateTime.MinValue,
                    DestinationName = ToLoadingReceiptAllocationDestinationName(a.Destination),
                    StatusName = ToLoadingReceiptAllocationStatusName(a.Status),
                    QuantityMt = a.QuantityMt,
                    SourcePurchaseContractNumber = a.SourcePurchaseContract?.DisplayLabel ?? contract.DisplayLabel,
                    TerminalName = a.Terminal?.Name,
                    StorageTankCode = StorageTankDisplay.BuildOptional(a.StorageTank),
                    DestinationTerminalName = a.DestinationTerminal?.Name,
                    DestinationStorageTankCode = StorageTankDisplay.BuildOptional(a.DestinationStorageTank),
                    DestinationLocationName = a.DestinationLocation?.Name
                        ?? a.DestinationName
                        ?? a.DestinationReference,
                    TruckDispatchId = linkedDispatchId,
                    SalesTransactionId = a.SalesTransactionId,
                    InventoryMovementId = a.InventoryMovementId,
                    HasQuantityMismatch = hasQuantityMismatch
                };
            })
            .OrderByDescending(a => a.ReceiptDate)
            .ThenByDescending(a => a.Id)
            .ToList();
        var totalReceivedQuantityMt = loadingReceipts.Sum(r => r.ReceivedQuantityMt);
        var receiptLoadingById = loadingReceipts.ToDictionary(r => r.Id, r => r.LoadingRegisterId);
        var receiptShortageRowsByLoadingId = await _db.LossEvents
            .AsNoTracking()
            .Where(e => (e.LoadingRegisterId.HasValue && loadingIds.Contains(e.LoadingRegisterId.Value)
                    || e.LoadingReceiptId.HasValue && receiptIds.Contains(e.LoadingReceiptId.Value))
                && e.Stage == LossEventStage.ReceiptShortage
                && !e.IsCancelled)
            .Select(e => new
            {
                e.LoadingRegisterId,
                e.LoadingReceiptId,
                e.DifferenceQuantityMt,
                e.ChargeableLossMt
            })
            .ToListAsync();
        var receiptShortageQuantityByLoadingId = receiptShortageRowsByLoadingId
            .Select(e => new
            {
                LoadingRegisterId = e.LoadingRegisterId
                    ?? (e.LoadingReceiptId.HasValue && receiptLoadingById.TryGetValue(e.LoadingReceiptId.Value, out var receiptLoadingId)
                        ? receiptLoadingId
                        : (int?)null),
                e.DifferenceQuantityMt,
                e.ChargeableLossMt
            })
            .Where(e => e.LoadingRegisterId.HasValue)
            .GroupBy(e => e.LoadingRegisterId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(e => e.DifferenceQuantityMt > 0m ? e.DifferenceQuantityMt : Math.Max(e.ChargeableLossMt, 0m)));
        var bulkReceiptCandidates = BuildBulkReceiptCandidates(
            loadingRegisters,
            loadingItemById,
            receiptQuantityByLoadingId,
            receiptShortageQuantityByLoadingId);
        var unreceiptedLoadingCount = loadingRegisters.Count(l => !receiptQuantityByLoadingId.ContainsKey(l.Id));
        var receiptDifferenceQuantityMt = loadingReceipts
            .Where(r => r.ActualArrivedQuantityMt.HasValue)
            .Sum(r => r.ActualArrivedQuantityMt!.Value - r.ReceivedQuantityMt);
        var loadingDocumentReferences = BuildLoadingDocumentReferences(loadingRegisters);

        var hasLoadingIds = loadingIds.Count > 0;
        var hasInventoryTransportLegIds = inventoryTransportLegIds.Count > 0;
        var customsDeclarationEntities = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c =>
                (hasLoadingIds && c.LoadingRegisterId.HasValue && loadingIds.Contains(c.LoadingRegisterId.Value))
                || (hasInventoryTransportLegIds && c.TransportLegId.HasValue && inventoryTransportLegIds.Contains(c.TransportLegId.Value)))
            .ToListAsync();
        var customsDeclarations = customsDeclarationEntities
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();
        var customsDeclarationTotalUsd = customsDeclarations.Sum(c => c.TotalUsd);

        var directShipments = await _db.Shipments
            .Include(s => s.Vessel)
            .Include(s => s.OriginLocation)
            .Include(s => s.DestinationLocation)
            .AsNoTracking()
            .Where(s => s.ContractId == contractId)
            .OrderBy(s => s.DepartureDate)
            .ThenBy(s => s.Id)
            .ToListAsync();
        // A purchase contract can sit inside a multi-contract (inventory / موجودی)
        // shipment through the ShipmentContracts join table, where Shipment.ContractId
        // points at a different contract or none. Union those shipment ids (plus this
        // contract's transport-leg shipments) so shipment-scoped payments/ledger/
        // expenses are attributed to this contract too — mirrors the summary and
        // scenario builders. Quantity/visible-shipment display stays on directShipments.
        var contractShipmentIds = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ContractId == contractId)
            .Select(sc => sc.ShipmentId)
            .ToListAsync();
        var shipmentIds = directShipments.Select(s => s.Id)
            .Concat(contractShipmentIds)
            .Concat(inventoryTransportLegs
                .Where(l => l.ShipmentId.HasValue)
                .Select(l => l.ShipmentId!.Value))
            .Distinct()
            .ToList();
        var shipmentQuantityMt = directShipments.Sum(s => s.QuantityMt);

        var inventoryMovements = await _db.InventoryMovements
            .Include(m => m.Terminal)
            .Include(m => m.StorageTank)
            .AsNoTracking()
            .Where(m => m.ContractId == contractId)
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => m.Id)
            .ToListAsync();
        var movementIds = inventoryMovements.Select(m => m.Id).ToList();
        var saleIds = inventoryMovements
            .Where(m => m.Direction == MovementDirection.Out && m.SalesTransactionId.HasValue)
            .Select(m => m.SalesTransactionId!.Value)
            .Distinct()
            .ToList();
        var salesById = saleIds.Count == 0
            ? new Dictionary<int, SalesTransaction>()
            : await _db.SalesTransactions
                .Include(s => s.Contract)
                .Include(s => s.Customer)
                .AsNoTracking()
                .Where(s => saleIds.Contains(s.Id) && !s.IsCancelled)
                .ToDictionaryAsync(s => s.Id);

        var inventoryMovementItems = new List<ContractJourneyInventoryMovementItemViewModel>();
        decimal runningBalanceMt = 0m;
        foreach (var movement in inventoryMovements)
        {
            var signedQuantityMt = ToSignedQuantity(movement.Direction, movement.QuantityMt);
            runningBalanceMt += signedQuantityMt;
            inventoryMovementItems.Add(new ContractJourneyInventoryMovementItemViewModel
            {
                Id = movement.Id,
                MovementDate = movement.MovementDate,
                DirectionName = ToMovementDirectionName(movement.Direction),
                QuantityMt = movement.QuantityMt,
                SignedQuantityMt = signedQuantityMt,
                RunningBalanceMt = runningBalanceMt,
                TerminalName = movement.Terminal?.Name ?? string.Empty,
                StorageTankCode = StorageTankDisplay.BuildOptional(movement.StorageTank),
                ReferenceDocument = movement.ReferenceDocument,
                SourceLabel = movement.SalesTransactionId.HasValue && salesById.TryGetValue(movement.SalesTransactionId.Value, out var sale)
                    ? sale.InvoiceNumber
                    : movement.ReferenceDocument,
                SalesTransactionId = movement.SalesTransactionId
            });
        }
        var inventoryInQuantityMt = inventoryMovements
            .Where(m => m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment)
            .Sum(m => m.QuantityMt);
        var inventoryOutQuantityMt = inventoryMovements
            .Where(m => m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer)
            .Sum(m => m.QuantityMt);

        var saleItemsFromInventory = inventoryMovements
            .Where(m => m.Direction == MovementDirection.Out && m.SalesTransactionId.HasValue)
            .GroupBy(m => m.SalesTransactionId!.Value)
            .Where(g => salesById.ContainsKey(g.Key))
            .Select(g =>
            {
                var sale = salesById[g.Key];
                var quantityMt = g.Sum(m => m.QuantityMt);
                return new ContractJourneySaleItemViewModel
                {
                    SalesTransactionId = sale.Id,
                    IsSalePricePending = sale.IsSalePricePending,
                    ShipmentId = sale.ShipmentId,
                    InvoiceNumber = sale.InvoiceNumber,
                    CustomerName = sale.Customer?.Name ?? string.Empty,
                    SaleDate = sale.SaleDate,
                    QuantityMt = quantityMt,
                    UnitPriceUsd = sale.UnitPriceUsd,
                    AmountUsd = decimal.Round(sale.UnitPriceUsd * quantityMt, 4, MidpointRounding.AwayFromZero),
                    SalesContractDisplay = sale.Contract?.DisplayLabel ?? SalesContractText.WithoutSalesContract,
                    StockSourceTypeName = sale.StockSourceType.HasValue ? ToStockSourceTypeName(sale.StockSourceType.Value) : null,
                    SaleStageName = SaleStageLabels.ToPersian(sale.SaleStage),
                    HasInventoryMovementTrace = true,
                    SourcePurchaseContractNumber = contract.DisplayLabel,
                    TraceKind = "مستقیم از خروج موجودی و شناسه فروش"
                };
            })
            .Where(s => salesById[s.SalesTransactionId].SaleStage != SaleStage.PreSale)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.SalesTransactionId)
            .ToList();
        var inventorySaleIdSet = saleItemsFromInventory
            .Select(s => s.SalesTransactionId)
            .ToHashSet();
        var directSaleAllocations = await _db.LoadingReceiptAllocations
            .Include(a => a.SalesTransaction)
                .ThenInclude(s => s!.Customer)
            .Include(a => a.SalesTransaction)
                .ThenInclude(s => s!.Contract)
            .Include(a => a.SourcePurchaseContract)
            .AsNoTracking()
            .Where(a =>
                a.Destination == LoadingReceiptAllocationDestination.DirectSale
                && a.SourcePurchaseContractId == contractId
                && a.SalesTransactionId.HasValue)
            .OrderByDescending(a => a.SalesTransaction!.SaleDate)
            .ThenByDescending(a => a.SalesTransactionId)
            .ToListAsync();
        var directSaleItems = directSaleAllocations
            .Where(a => a.SalesTransaction is not null
                && !a.SalesTransaction.IsCancelled
                && a.SalesTransaction.SaleStage != SaleStage.PreSale
                && !inventorySaleIdSet.Contains(a.SalesTransactionId!.Value))
            .Select(a =>
            {
                var sale = a.SalesTransaction!;
                var hasQuantityMismatch = a.QuantityMt != sale.QuantityMt;
                return new ContractJourneySaleItemViewModel
                {
                    SalesTransactionId = sale.Id,
                    IsSalePricePending = sale.IsSalePricePending,
                    ShipmentId = sale.ShipmentId,
                    InvoiceNumber = sale.InvoiceNumber,
                    CustomerName = sale.Customer?.Name ?? string.Empty,
                    SaleDate = sale.SaleDate,
                    QuantityMt = sale.QuantityMt,
                    UnitPriceUsd = sale.UnitPriceUsd,
                    AmountUsd = sale.TotalUsd,
                    SalesContractDisplay = sale.Contract?.DisplayLabel ?? SalesContractText.WithoutSalesContract,
                    StockSourceTypeName = sale.StockSourceType.HasValue ? ToStockSourceTypeName(sale.StockSourceType.Value) : null,
                    SaleStageName = SaleStageLabels.ToPersian(sale.SaleStage),
                    HasInventoryMovementTrace = false,
                    SourcePurchaseContractNumber = a.SourcePurchaseContract?.DisplayLabel ?? contract.DisplayLabel,
                    LoadingReceiptAllocationId = a.Id,
                    AllocationQuantityMt = a.QuantityMt,
                    HasQuantityMismatch = hasQuantityMismatch,
                    TraceKind = "Direct Sale from Receipt Allocation"
                };
            })
            .ToList();
        var directSaleIdSet = directSaleItems
            .Select(s => s.SalesTransactionId)
            .ToHashSet();
        var directInventoryTransportSaleIds = inventoryTransportReceipts
            .Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale && r.SalesTransactionId.HasValue)
            .Select(r => r.SalesTransactionId!.Value)
            .Distinct()
            .ToList();
        var directInventoryTransportSalesById = directInventoryTransportSaleIds.Count == 0
            ? new Dictionary<int, SalesTransaction>()
            : await _db.SalesTransactions
                .Include(s => s.Customer)
                .Include(s => s.Contract)
                .AsNoTracking()
                .Where(s => directInventoryTransportSaleIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);
        var directInventoryTransportSourceRows = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => directInventoryTransportSaleIds.Contains(a.SalesTransactionId))
            .Select(a => new
            {
                a.SalesTransactionId,
                a.SourcePurchaseContractId,
                a.TransportLegId,
                a.QuantityMt,
                a.AmountUsd
            })
            .ToListAsync();
        var directInventoryTransportSourceBySaleId = directInventoryTransportSourceRows
            .Where(a => a.SourcePurchaseContractId == contractId)
            .GroupBy(a => a.SalesTransactionId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    QuantityMt = g.Sum(a => a.QuantityMt),
                    AmountUsd = g.Sum(a => a.AmountUsd),
                    TransportLegId = g.Select(a => a.TransportLegId).FirstOrDefault(id => id.HasValue)
                });
        var salesWithPersistedSourceAllocations = directInventoryTransportSourceRows
            .Select(a => a.SalesTransactionId)
            .ToHashSet();
        var directInventoryTransportSaleItems = inventoryTransportReceipts
            .Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale
                && r.SalesTransactionId.HasValue
                && directInventoryTransportSalesById.ContainsKey(r.SalesTransactionId.Value)
                && inventoryTransportLegById.ContainsKey(r.InventoryTransportLegId)
                && (!salesWithPersistedSourceAllocations.Contains(r.SalesTransactionId.Value)
                    || directInventoryTransportSourceBySaleId.ContainsKey(r.SalesTransactionId.Value))
                && !inventorySaleIdSet.Contains(r.SalesTransactionId.Value)
                && !directSaleIdSet.Contains(r.SalesTransactionId.Value))
            .Select(r =>
            {
                var sale = directInventoryTransportSalesById[r.SalesTransactionId!.Value];
                var leg = inventoryTransportLegById[r.InventoryTransportLegId];
                directInventoryTransportSourceBySaleId.TryGetValue(sale.Id, out var sourceShare);
                var quantityMt = sourceShare?.QuantityMt ?? sale.QuantityMt;
                var amountUsd = sourceShare?.AmountUsd ?? sale.TotalUsd;
                var sourceLegId = sourceShare?.TransportLegId ?? leg.Id;
                var hasQuantityMismatch = !salesWithPersistedSourceAllocations.Contains(sale.Id)
                    && r.ReceivedQuantityMt != sale.QuantityMt;
                return new ContractJourneySaleItemViewModel
                {
                    SalesTransactionId = sale.Id,
                    IsSalePricePending = sale.IsSalePricePending,
                    ShipmentId = sale.ShipmentId,
                    InventoryTransportLegId = sourceLegId,
                    InventoryTransportReceiptId = r.Id,
                    InvoiceNumber = sale.InvoiceNumber,
                    CustomerName = sale.Customer?.Name ?? string.Empty,
                    SaleDate = sale.SaleDate,
                    QuantityMt = quantityMt,
                    UnitPriceUsd = sale.UnitPriceUsd,
                    AmountUsd = amountUsd,
                    SalesContractDisplay = sale.Contract?.DisplayLabel ?? SalesContractText.WithoutSalesContract,
                    StockSourceTypeName = sale.StockSourceType.HasValue ? ToStockSourceTypeName(sale.StockSourceType.Value) : null,
                    SaleStageName = SaleStageLabels.ToPersian(sale.SaleStage),
                    HasInventoryMovementTrace = false,
                    SourcePurchaseContractNumber = contract.DisplayLabel,
                    InventoryTransportReference = BuildTransportLegReference(leg),
                    AllocationQuantityMt = quantityMt,
                    HasQuantityMismatch = hasQuantityMismatch,
                    TraceKind = sourceShare is null
                        ? "Direct Sale from Inventory Transport Receipt"
                        : "Direct Sale from Transport Source Allocation"
                };
            })
            .Where(s => !directInventoryTransportSalesById[s.SalesTransactionId].IsCancelled
                && directInventoryTransportSalesById[s.SalesTransactionId].SaleStage != SaleStage.PreSale)
            .ToList();
        var directInventoryTransportSaleIdSet = directInventoryTransportSaleItems
            .Select(s => s.SalesTransactionId)
            .ToHashSet();
        // فروش مستقیم از موتر: هر دو مسیرِ لینک خوانده می‌شود — SalesTransaction.TruckDispatchId
        // (که فروش قسمتی و فروشِ باقی‌مانده را هم پوشش می‌دهد) و TruckDispatch.SalesTransactionId
        // (رکوردهای قدیمی). حالت دیسپچ (DirectFromReceipt یا FromInventory) فیلتر نمی‌شود، چون
        // فروشِ موترِ بارگیری‌شده از مخزن هم InventoryMovementِ فروش ندارد و بدون این مسیر گم می‌شد.
        var contractDispatchIds = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.ContractId == contractId)
            .Select(d => d.Id)
            .ToListAsync();
        var directDispatchSaleDispatches = contractDispatchIds.Count == 0
            ? []
            : await _db.TruckDispatches
                .Include(d => d.SalesTransaction)
                    .ThenInclude(s => s!.Customer)
                .Include(d => d.SalesTransaction)
                    .ThenInclude(s => s!.Contract)
                .Include(d => d.LoadingReceiptAllocation)
                    .ThenInclude(a => a!.SourcePurchaseContract)
                .AsNoTracking()
                .Where(d => contractDispatchIds.Contains(d.Id)
                    && (d.SalesTransactionId.HasValue
                        || _db.SalesTransactions.Any(s => s.TruckDispatchId == d.Id && !s.IsCancelled)))
                .ToListAsync();
        var dispatchSalesByDispatchId = contractDispatchIds.Count == 0
            ? new Dictionary<int, List<SalesTransaction>>()
            : (await _db.SalesTransactions
                .Include(s => s.Customer)
                .Include(s => s.Contract)
                .AsNoTracking()
                .Where(s => s.TruckDispatchId.HasValue
                    && contractDispatchIds.Contains(s.TruckDispatchId.Value))
                .ToListAsync())
                .GroupBy(s => s.TruckDispatchId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
        var directDispatchSaleItems = directDispatchSaleDispatches
            .SelectMany(d =>
            {
                var sales = new List<SalesTransaction>();
                if (dispatchSalesByDispatchId.TryGetValue(d.Id, out var linked))
                {
                    sales.AddRange(linked);
                }

                if (d.SalesTransaction is not null && sales.All(s => s.Id != d.SalesTransaction.Id))
                {
                    sales.Add(d.SalesTransaction);
                }

                return sales.Select(sale => (Dispatch: d, Sale: sale));
            })
            .Where(x => !x.Sale.IsCancelled
                && x.Sale.SaleStage != SaleStage.PreSale
                && !inventorySaleIdSet.Contains(x.Sale.Id)
                && !directSaleIdSet.Contains(x.Sale.Id)
                && !directInventoryTransportSaleIdSet.Contains(x.Sale.Id))
            .GroupBy(x => x.Sale.Id)
            .Select(g => g.First())
            .OrderByDescending(x => x.Sale.SaleDate)
            .ThenByDescending(x => x.Sale.Id)
            .Select(x =>
            {
                var d = x.Dispatch;
                var sale = x.Sale;
                // مبنای مقایسه مقدار واقعیِ تخلیه‌شده است، نه وزن بارگیری: فروشِ اضافه‌بار و فروش
                // قسمتی هر دو عادی‌اند و نباید علامت مغایرت بگیرند؛ فقط فروشِ بیش از مقدارِ
                // تخلیه‌شده واقعاً مغایرت است.
                var hasQuantityMismatch = sale.QuantityMt > (d.DischargedQuantityMt ?? d.LoadedQuantityMt);
                return new ContractJourneySaleItemViewModel
                {
                    SalesTransactionId = sale.Id,
                    IsSalePricePending = sale.IsSalePricePending,
                    ShipmentId = sale.ShipmentId,
                    TruckDispatchId = d.Id,
                    InvoiceNumber = sale.InvoiceNumber,
                    CustomerName = sale.Customer?.Name ?? string.Empty,
                    SaleDate = sale.SaleDate,
                    QuantityMt = sale.QuantityMt,
                    UnitPriceUsd = sale.UnitPriceUsd,
                    AmountUsd = sale.TotalUsd,
                    SalesContractDisplay = sale.Contract?.DisplayLabel ?? SalesContractText.WithoutSalesContract,
                    StockSourceTypeName = sale.StockSourceType.HasValue ? ToStockSourceTypeName(sale.StockSourceType.Value) : null,
                    SaleStageName = SaleStageLabels.ToPersian(sale.SaleStage),
                    HasInventoryMovementTrace = false,
                    SourcePurchaseContractNumber = d.LoadingReceiptAllocation?.SourcePurchaseContract?.DisplayLabel ?? contract.DisplayLabel,
                    LoadingReceiptAllocationId = d.LoadingReceiptAllocationId,
                    AllocationQuantityMt = d.LoadingReceiptAllocation?.QuantityMt,
                    HasQuantityMismatch = hasQuantityMismatch,
                    TraceKind = "Sale from Direct Truck Dispatch"
                };
            })
            .ToList();
        var saleItems = saleItemsFromInventory
            .Concat(directSaleItems)
            .Concat(directInventoryTransportSaleItems)
            .Concat(directDispatchSaleItems)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.SalesTransactionId)
            .ToList();
        var directSaleQuantityMismatchWarning = directSaleItems.Concat(directInventoryTransportSaleItems).Any(s => s.HasQuantityMismatch)
            ? "DirectSale quantity mismatch: one or more receipt allocations differ from their linked SalesTransaction quantity."
            : null;
        if (directSaleQuantityMismatchWarning is not null)
        {
            notesForReview.Add(directSaleQuantityMismatchWarning);
        }
        var soldQuantityMt = saleItems.Sum(s => s.QuantityMt);
        var shipmentScenarios = needsShipmentScenarios
            ? await BuildPurchaseShipmentScenariosAsync(
                contractId,
                contractFinalPriceUsd,
                inventoryTransportLegs,
                inventoryTransportReceipts)
            : [];

        var preSaleSales = await _db.SalesTransactions
            .Include(s => s.Customer)
            .Include(s => s.Contract)
            .AsNoTracking()
            .Where(s => s.ContractId == contractId && s.SaleStage == SaleStage.PreSale)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();
        var preSaleItems = preSaleSales
            .Select(s => new ContractJourneyPreSaleItemViewModel
            {
                SalesTransactionId = s.Id,
                InvoiceNumber = s.InvoiceNumber,
                CustomerName = s.Customer?.Name ?? string.Empty,
                SaleDate = s.SaleDate,
                QuantityMt = s.QuantityMt,
                SalesContractDisplay = s.Contract?.DisplayLabel ?? SalesContractText.WithoutSalesContract,
                TraceKind = "مستقیم از قرارداد فروش"
            })
            .ToList();

        if (isTabFragmentRequest && activeTab == ContractJourneyTabs.Details.Sales)
        {
            return View(BuildPurchaseTabPayload(
                baseModel,
                activeTab,
                lockContract,
                salesItems: saleItems,
                preSaleItems: preSaleItems,
                shipmentScenarios: shipmentScenarios));
        }

        var directDispatches = await _db.TruckDispatches
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.DestinationLocation)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.LoadingReceipt)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.DestinationLocation)
            .AsNoTracking()
            .Where(d => d.ContractId == contractId)
            .OrderBy(d => d.DispatchDate)
            .ThenBy(d => d.Id)
            .ToListAsync();
        var directDispatchAllocationIds = directDispatches
            .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.LoadingReceiptAllocationId.HasValue)
            .Select(d => d.LoadingReceiptAllocationId!.Value)
            .Distinct()
            .ToList();
        var directDispatchAllocationQuantityRows = directDispatchAllocationIds.Count == 0
            ? []
            : await _db.TruckDispatches
                .AsNoTracking()
                .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt
                    && d.Status != DispatchStatus.Cancelled
                    && d.LoadingReceiptAllocationId.HasValue
                    && directDispatchAllocationIds.Contains(d.LoadingReceiptAllocationId.Value))
                .Select(d => new { AllocationId = d.LoadingReceiptAllocationId!.Value, d.LoadedQuantityMt })
                .ToListAsync();
        var directDispatchQuantityByAllocationId = directDispatchAllocationQuantityRows
            .GroupBy(d => d.AllocationId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.LoadedQuantityMt));
        var dispatchIds = directDispatches.Select(d => d.Id).ToList();
        var dispatchReferenceMap = dispatchIds.ToDictionary(id => $"TRUCK-DISPATCH:{id}", id => id);
        var dispatchReferences = dispatchReferenceMap.Keys.ToList();
        var dispatchMovements = dispatchIds.Count == 0
            ? new List<InventoryMovement>()
            : await _db.InventoryMovements
                .Include(m => m.Terminal)
                .Include(m => m.StorageTank)
                .AsNoTracking()
                .Where(m => m.ContractId == contractId && m.ReferenceDocument != null && dispatchReferences.Contains(m.ReferenceDocument))
                .ToListAsync();
        var dispatchMovementByDispatchId = dispatchMovements
            .Select(m => new { DispatchId = dispatchReferenceMap.GetValueOrDefault(m.ReferenceDocument ?? string.Empty), Movement = m })
            .Where(x => x.DispatchId > 0)
            .GroupBy(x => x.DispatchId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Movement.Id).First().Movement);

        var dispatchItems = directDispatches
            .Select(d =>
            {
                dispatchMovementByDispatchId.TryGetValue(d.Id, out var movement);
                var allocation = d.LoadingReceiptAllocation;
                decimal? allocationTotalDirectDispatchedQuantityMt = allocation is null
                    ? null
                    : directDispatchQuantityByAllocationId.GetValueOrDefault(allocation.Id);
                decimal? allocationRemainingQuantityMt = allocation is null || !allocationTotalDirectDispatchedQuantityMt.HasValue
                    ? null
                    : allocation.QuantityMt - allocationTotalDirectDispatchedQuantityMt.Value;
                return new ContractJourneyDispatchItemViewModel
                {
                    Id = d.Id,
                    DispatchMode = d.DispatchMode,
                    LoadingReceiptAllocationId = d.LoadingReceiptAllocationId,
                    LoadingReceiptId = allocation?.LoadingReceiptId,
                    DispatchDate = d.DispatchDate,
                    TruckPlateNumber = d.Truck?.PlateNumber ?? string.Empty,
                    DriverName = d.Driver?.FullName,
                    DestinationName = d.DestinationLocation?.Name
                        ?? allocation?.DestinationName
                        ?? allocation?.DestinationLocation?.Name
                        ?? allocation?.DestinationReference,
                    StatusName = d.Status.ToString(),
                    LoadedQuantityMt = d.LoadedQuantityMt,
                    DischargedQuantityMt = d.DischargedQuantityMt,
                    AllocationQuantityMt = allocation?.QuantityMt,
                    AllocationTotalDirectDispatchedQuantityMt = allocationTotalDirectDispatchedQuantityMt,
                    AllocationRemainingQuantityMt = allocationRemainingQuantityMt,
                    SourceTerminalName = movement?.Terminal?.Name,
                    SourceStorageTankCode = StorageTankDisplay.BuildOptional(movement?.StorageTank),
                    FreightCostUsd = d.FreightCostUsd,
                    ReferenceDocument = movement?.ReferenceDocument,
                    TraceKind = d.DispatchMode == TruckDispatchMode.DirectFromReceipt
                        ? "Direct Truck Dispatch from Receipt Allocation"
                        : movement is null
                        ? "مستقیم از قرارداد"
                        : "مستقیم از قرارداد و سند موجودی"
                };
            })
            .OrderByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .ToList();
        var dispatchedQuantityMt = directDispatches.Sum(d => d.LoadedQuantityMt);
        var dispatchFreightCostUsd = directDispatches.Sum(d => d.FreightCostUsd ?? 0m);
        var dispatchWithoutInventoryTraceCount = dispatchItems.Count(d =>
            d.DispatchMode != TruckDispatchMode.DirectFromReceipt
            && string.IsNullOrWhiteSpace(d.ReferenceDocument));

        if (isTabFragmentRequest && activeTab == ContractJourneyTabs.Details.Dispatch)
        {
            return View(BuildPurchaseTabPayload(
                baseModel,
                activeTab,
                lockContract,
                dispatchItems: dispatchItems));
        }

        IQueryable<ExpenseTransaction> ExpenseQuery() => _db.ExpenseTransactions
            .AsNoTracking()
            .Include(e => e.ExpenseType)
            .Include(e => e.Shipment)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.TransportLeg);

        var hasShipmentIds = shipmentIds.Count > 0;
        var hasDispatchIds = dispatchIds.Count > 0;
        var expenseEntities = await ExpenseQuery()
            .Where(e => e.ContractId == contractId
                || (hasShipmentIds && e.ShipmentId.HasValue && shipmentIds.Contains(e.ShipmentId.Value)
                    && (e.ContractId == null || e.ContractId == contractId))
                || (hasDispatchIds && e.TruckDispatchId.HasValue && dispatchIds.Contains(e.TruckDispatchId.Value))
                || (hasInventoryTransportLegIds && e.TransportLegId.HasValue && inventoryTransportLegIds.Contains(e.TransportLegId.Value))
                || (hasLoadingIds && e.LoadingRegisterId.HasValue && loadingIds.Contains(e.LoadingRegisterId.Value)))
            .ToListAsync();

        var expenses = expenseEntities
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToList();
        var expenseIdSet = expenses.Select(e => e.Id).ToHashSet();
        var shipmentIdSet = shipmentIds.ToHashSet();
        var dispatchIdSet = dispatchIds.ToHashSet();

        static string ExpenseSearchText(ExpenseTransaction expense)
            => string.Join(' ',
                expense.ExpenseType?.Code,
                expense.ExpenseType?.Name,
                expense.ExpenseType?.NamePersian,
                expense.Description);

        static bool ContainsAnyTerm(string text, params string[] terms)
            => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

        static bool IsStorageRentExpense(ExpenseTransaction expense)
        {
            var text = ExpenseSearchText(expense);
            return ContainsAnyTerm(text,
                "storage",
                "warehouse",
                "tank rent",
                "مخزن",
                "مخازن",
                "ذخیره",
                "انبار");
        }

        static bool IsTransportFreightExpense(ExpenseTransaction expense)
        {
            if (ExpenseClassification.IsWagonRentExpense(expense))
            {
                return true;
            }

            var text = ExpenseSearchText(expense);
            return ContainsAnyTerm(text,
                "freight",
                "transport",
                "truck",
                "vessel freight",
                "shipping",
                "demurrage",
                "حمل",
                "ترانسپورت",
                "کرایه موتر",
                "کرایه کشتی",
                "دیمیرج");
        }

        var expenseStorageRentUsd = expenses
            .Where(e => !e.IsCancelled && IsStorageRentExpense(e))
            .Sum(e => e.AmountUsd);
        var expenseTransportFreightUsd = expenses
            .Where(e => !e.IsCancelled && IsTransportFreightExpense(e))
            .Sum(e => e.AmountUsd);
        var hasOfficialWagonRentExpense = expenses.Any(e =>
            !e.IsCancelled && ExpenseClassification.IsWagonRentExpense(e));
        // مصرفِ لغوشده پول نیست، و مصرفِ ساخته‌شده از اظهارنامهٔ گمرکی در همین ردیف‌ها
        // به‌شکل inventoryTransportCustomsTotalByLegId جدا شمرده می‌شود؛ اگر اینجا هم بیاید
        // ستون «مصرف حمل» و ستون «گمرک» یک پول را دو بار روی همان فروش می‌نشانند.
        var inventoryTransportExpenseGroups = expenses
            .Where(e => !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && e.TransportLegId.HasValue
                && inventoryTransportLegById.ContainsKey(e.TransportLegId.Value))
            .GroupBy(e => e.TransportLegId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var inventoryTransportExpenseAllocations = inventoryTransportLegs
            .Select(l =>
            {
                var legExpenses = inventoryTransportExpenseGroups.TryGetValue(l.Id, out var groupedExpenses)
                    ? groupedExpenses
                    : [];
                var expenseTotalUsd = legExpenses.Sum(e => e.AmountUsd);

                return new ContractJourneyTransportLegExpenseAllocationViewModel
                {
                    TransportLegId = l.Id,
                    LoadedDate = l.LoadedDate,
                    TransportTypeName = ToLoadingTransportTypeName(l.TransportType),
                    TransportReference = BuildTransportLegReference(l),
                    RwbNo = l.RwbNo,
                    SourceLocation = BuildTankLocation(l.SourceTerminal?.Name, StorageTankDisplay.BuildOptional(l.SourceStorageTank)),
                    DestinationLocation = BuildTransportLegDestination(l),
                    QuantityMt = l.QuantityMt,
                    ExpenseCount = legExpenses.Count,
                    ExpenseTotalUsd = expenseTotalUsd,
                    ExpensePerMtUsd = l.QuantityMt > 0m ? expenseTotalUsd / l.QuantityMt : null
                };
            })
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.TransportLegId)
            .ToList();

        // مصرفِ لغوشده پول نیست: بعد از لغو مصرف گروهی/تکی نباید در تب مصارف قرارداد
        // بماند (جمع‌های همین صفحه هم آن را نمی‌شمارند).
        var expenseItems = expenses
            .Where(e => !e.IsCancelled)
            .Select(e =>
            {
                var transportLeg = e.TransportLegId.HasValue
                    && inventoryTransportLegById.TryGetValue(e.TransportLegId.Value, out var matchedLeg)
                        ? matchedLeg
                        : null;

                return new ContractJourneyExpenseItemViewModel
                {
                    ExpenseTransactionId = e.Id,
                    ExpenseTypeName = e.ExpenseType?.NamePersian ?? e.ExpenseType?.Name ?? string.Empty,
                    ExpenseDate = e.ExpenseDate,
                    AmountUsd = e.AmountUsd,
                    ShipmentCode = e.Shipment?.ShipmentCode,
                    TransportLegId = transportLeg?.Id,
                    TransportLegLabel = transportLeg is null ? null : BuildTransportLegReference(transportLeg),
                    TransportLegQuantityMt = transportLeg?.QuantityMt,
                    TransportLegExpensePerMtUsd = transportLeg is not null && transportLeg.QuantityMt > 0m
                        ? e.AmountUsd / transportLeg.QuantityMt
                        : null,
                    DispatchLabel = e.TruckDispatch is null ? null : $"#{e.TruckDispatch.Id} - {e.TruckDispatch.Truck?.PlateNumber ?? "بدون پلاک"}",
                    VehicleNumber = NullIfEmpty(
                        BuildVehicleNumber(e.TruckDispatch),
                        BuildVehicleNumber(transportLeg)),
                    Description = e.Description,
                    TraceKind = ResolveExpenseTraceKind(e, contractId, shipmentIdSet, dispatchIdSet, inventoryTransportLegIds)
                };
            })
            .ToList();
        // کرایهٔ داخلیِ ترانسپورت شخصی شرکت برای بارگیری‌های همین قرارداد: AssetRentTransaction
        // بدون Ledger. فقط برای گزارش مدیریتی نمایش داده می‌شود و به ExpenseItems/جمع مصارف اضافه
        // نمی‌شود، چون همان مبلغ از قبل در فیلد درون‌خطیِ کرایهٔ بارگیری شمرده شده است.
        var internalTransportCostItems = hasLoadingIds
            ? await _db.AssetRentTransactions
                .AsNoTracking()
                .Where(r => !r.IsCancelled
                    && r.LoadingRegisterId.HasValue
                    && loadingIds.Contains(r.LoadingRegisterId.Value))
                .OrderByDescending(r => r.RentDate)
                .ThenByDescending(r => r.Id)
                .Select(r => new ContractJourneyInternalTransportCostItemViewModel
                {
                    AssetRentTransactionId = r.Id,
                    LoadingRegisterId = r.LoadingRegisterId,
                    RentDate = r.RentDate,
                    AssetName = r.OperationalAsset != null ? r.OperationalAsset.Name : string.Empty,
                    QuantityMt = r.QuantityMt,
                    RateUsdPerMt = r.Rate,
                    AmountUsd = r.AmountUsd
                })
                .ToListAsync()
            : [];
        var inventoryTransportExpenseTotalByLegId = inventoryTransportExpenseGroups
            .ToDictionary(g => g.Key, g => g.Value.Sum(e => e.AmountUsd));
        var inventoryTransportCustomsTotalByLegId = customsDeclarations
            .Where(c => c.TransportLegId.HasValue && inventoryTransportLegById.ContainsKey(c.TransportLegId.Value))
            .GroupBy(c => c.TransportLegId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.TotalUsd));
        var shipmentSaleCostAllocations = BuildShipmentSaleCostAllocations(saleItems, shipmentScenarios);

        static bool SameQuantity(decimal left, decimal right)
            => decimal.Abs(left - right) <= 0.0001m;

        ContractJourneySaleItemViewModel EnrichSaleCost(ContractJourneySaleItemViewModel item)
        {
            decimal? purchaseUnitCostUsd = purchaseAgg.WeightedAveragePurchasePriceUsd;
            decimal? purchaseCostUsd = purchaseUnitCostUsd.HasValue
                ? decimal.Round(item.QuantityMt * purchaseUnitCostUsd.Value, 4, MidpointRounding.AwayFromZero)
                : null;
            decimal? transportLegExpenseCostUsd = null;
            decimal? transportLegCustomsCostUsd = null;
            string? costAllocationNote = item.CostAllocationNote;

            if (!item.InventoryTransportLegId.HasValue
                && item.ShipmentId.HasValue
                && shipmentSaleCostAllocations.TryGetValue(item.SalesTransactionId, out var shipmentCostAllocation))
            {
                if (shipmentCostAllocation.PurchaseCostPerMtUsd.HasValue)
                {
                    purchaseUnitCostUsd = shipmentCostAllocation.PurchaseCostPerMtUsd.Value;
                    purchaseCostUsd = decimal.Round(
                        item.QuantityMt * shipmentCostAllocation.PurchaseCostPerMtUsd.Value,
                        4,
                        MidpointRounding.AwayFromZero);
                }

                transportLegExpenseCostUsd = shipmentCostAllocation.OperationalExpensePerMtUsd > 0m
                    ? decimal.Round(
                        item.QuantityMt * shipmentCostAllocation.OperationalExpensePerMtUsd,
                        4,
                        MidpointRounding.AwayFromZero)
                    : null;
                transportLegCustomsCostUsd = shipmentCostAllocation.CustomsPerMtUsd > 0m
                    ? decimal.Round(
                        item.QuantityMt * shipmentCostAllocation.CustomsPerMtUsd,
                        4,
                        MidpointRounding.AwayFromZero)
                    : null;
                costAllocationNote = "Shipment landed cost allocation";
            }

            if (item.InventoryTransportLegId.HasValue
                && inventoryTransportLegById.TryGetValue(item.InventoryTransportLegId.Value, out var leg)
                && leg.QuantityMt > 0m)
            {
                var shouldChargeFullTransportLeg =
                    item.InventoryTransportReceiptId.HasValue
                    && inventoryTransportReceiptById.TryGetValue(item.InventoryTransportReceiptId.Value, out var receipt)
                    && receipt.ShortageQuantityMt > 0m
                    && SameQuantity(receipt.ReceivedQuantityMt, item.QuantityMt)
                    && SameQuantity(receipt.ReceivedQuantityMt + receipt.ShortageQuantityMt, leg.QuantityMt);

                var ratio = shouldChargeFullTransportLeg
                    ? 1m
                    : item.QuantityMt / leg.QuantityMt;

                if (shouldChargeFullTransportLeg && purchaseUnitCostUsd.HasValue)
                {
                    purchaseCostUsd = decimal.Round(
                        leg.QuantityMt * purchaseUnitCostUsd.Value,
                        4,
                        MidpointRounding.AwayFromZero);
                }

                if (inventoryTransportExpenseTotalByLegId.TryGetValue(leg.Id, out var legExpenseTotalUsd))
                {
                    transportLegExpenseCostUsd = decimal.Round(legExpenseTotalUsd * ratio, 4, MidpointRounding.AwayFromZero);
                }

                if (inventoryTransportCustomsTotalByLegId.TryGetValue(leg.Id, out var legCustomsTotalUsd))
                {
                    transportLegCustomsCostUsd = decimal.Round(legCustomsTotalUsd * ratio, 4, MidpointRounding.AwayFromZero);
                }
            }

            return new ContractJourneySaleItemViewModel
            {
                SalesTransactionId = item.SalesTransactionId,
                IsSalePricePending = item.IsSalePricePending,
                ShipmentId = item.ShipmentId,
                TruckDispatchId = item.TruckDispatchId,
                InventoryTransportLegId = item.InventoryTransportLegId,
                InventoryTransportReceiptId = item.InventoryTransportReceiptId,
                InvoiceNumber = item.InvoiceNumber,
                CustomerName = item.CustomerName,
                SaleDate = item.SaleDate,
                QuantityMt = item.QuantityMt,
                UnitPriceUsd = item.UnitPriceUsd,
                AmountUsd = item.AmountUsd,
                SalesContractDisplay = item.SalesContractDisplay,
                StockSourceTypeName = item.StockSourceTypeName,
                SaleStageName = item.SaleStageName,
                HasInventoryMovementTrace = item.HasInventoryMovementTrace,
                SourcePurchaseContractNumber = item.SourcePurchaseContractNumber,
                InventoryTransportReference = item.InventoryTransportReference,
                LoadingReceiptAllocationId = item.LoadingReceiptAllocationId,
                AllocationQuantityMt = item.AllocationQuantityMt,
                PurchaseUnitCostUsd = purchaseUnitCostUsd,
                PurchaseCostUsd = purchaseCostUsd,
                TransportLegExpenseCostUsd = transportLegExpenseCostUsd,
                TransportLegCustomsCostUsd = transportLegCustomsCostUsd,
                CostAllocationNote = costAllocationNote,
                HasQuantityMismatch = item.HasQuantityMismatch,
                TraceKind = item.TraceKind
            };
        }

        saleItems = saleItems
            .Select(EnrichSaleCost)
            .ToList();
        var saleIdSet = saleItems.Select(s => s.SalesTransactionId).ToHashSet();
        // مصرفِ لغوشده پول نیست و در هیچ جمعی نمی‌آید.
        var totalExpensesUsd = expenses.Where(e => !e.IsCancelled).Sum(e => e.AmountUsd);
        var expenseBreakdowns = expenseItems
            .GroupBy(e => string.IsNullOrWhiteSpace(e.ExpenseTypeName) ? "نامشخص" : e.ExpenseTypeName)
            .OrderByDescending(g => g.Sum(e => e.AmountUsd))
            .Select(g => new ContractJourneyExpenseBreakdownViewModel
            {
                ExpenseTypeName = g.Key,
                Count = g.Count(),
                AmountUsd = g.Sum(e => e.AmountUsd)
            })
            .ToList();

        var hasReceiptIds = receiptIds.Count > 0;
        var hasMovementIds = movementIds.Count > 0;
        var lossEntities = await _db.LossEvents
            .AsNoTracking()
            .Where(e => e.ContractId == contractId
                || e.SourceAllocations.Any(a => a.SourcePurchaseContractId == contractId)
                || (hasInventoryTransportLegIds && e.TransportLegId.HasValue && inventoryTransportLegIds.Contains(e.TransportLegId.Value))
                || (hasDispatchIds && e.TruckDispatchId.HasValue && dispatchIds.Contains(e.TruckDispatchId.Value))
                || (hasLoadingIds && e.LoadingRegisterId.HasValue && loadingIds.Contains(e.LoadingRegisterId.Value))
                || (hasReceiptIds && e.LoadingReceiptId.HasValue && receiptIds.Contains(e.LoadingReceiptId.Value))
                || (hasMovementIds && e.InventoryMovementId.HasValue && movementIds.Contains(e.InventoryMovementId.Value)))
            .ToListAsync();
        var lossEntityIds = lossEntities.Select(e => e.Id).ToList();
        var lossIdsWithSourceAllocations = await _db.LossEventSourceAllocations
            .AsNoTracking()
            .Where(a => lossEntityIds.Contains(a.LossEventId))
            .Select(a => new { a.LossEventId, a.SourcePurchaseContractId, a.QuantityMt })
            .ToListAsync();
        var lossSourceQuantityById = lossIdsWithSourceAllocations
            .Where(a => a.SourcePurchaseContractId == contractId)
            .GroupBy(a => a.LossEventId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.QuantityMt));
        var allocatedLossIds = lossIdsWithSourceAllocations.Select(a => a.LossEventId).ToHashSet();
        var losses = lossEntities
            .Where(e => !allocatedLossIds.Contains(e.Id) || lossSourceQuantityById.ContainsKey(e.Id))
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .OrderByDescending(e => e.EventDate)
            .ThenByDescending(e => e.Id)
            .ToList();
        var activeLosses = losses
            .Where(e => !e.IsCancelled)
            .ToList();
        decimal DisplayLossEventQuantityMt(LossEvent loss)
            => lossSourceQuantityById.TryGetValue(loss.Id, out var sourceQuantityMt)
                ? sourceQuantityMt
                : loss.DifferenceQuantityMt > 0m
                ? loss.DifferenceQuantityMt
                : Math.Max(loss.ChargeableLossMt, 0m);
        static decimal DisplayLossItemQuantityMt(ContractJourneyLossItemViewModel loss)
            => loss.DifferenceQuantityMt > 0m
                ? loss.DifferenceQuantityMt
                : Math.Max(loss.ChargeableLossMt, 0m);

        static decimal DisplayDispatchShortageMt(TruckDispatch dispatch)
        {
            if (dispatch.Status == DispatchStatus.Cancelled)
            {
                return 0m;
            }

            if (dispatch.ShortageMt.HasValue)
            {
                return Math.Max(dispatch.ShortageMt.Value, 0m);
            }

            return dispatch.DischargedQuantityMt.HasValue
                ? Math.Max(dispatch.LoadedQuantityMt - dispatch.DischargedQuantityMt.Value, 0m)
                : 0m;
        }

        var loadingDifferenceLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.LoadingDifference)
            .Sum(DisplayLossEventQuantityMt);
        var receiptShortageLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.ReceiptShortage)
            .Sum(DisplayLossEventQuantityMt);
        var recordedDispatchLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.DispatchShortage)
            .Sum(DisplayLossEventQuantityMt);
        var inventoryTransportLossMt = activeLosses
            .Where(e => e.TransportLegId.HasValue || e.Stage == LossEventStage.TransitLoss)
            .Sum(DisplayLossEventQuantityMt);
        // ضایعات مخزن هم شامل کسریِ طبیعی است و هم ضایعهٔ «تسویه نهایی مخزن» (سناریوی Deferred)
        // که هنگام تخلیهٔ کامل مخزن معلوم می‌شود؛ هر دو روی همان مخزن ثبت می‌شوند.
        var tankLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.TankNaturalLoss
                || e.Stage == LossEventStage.TankFinalSettlement)
            .Sum(DisplayLossEventQuantityMt);
        var salesLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.SalesDifference)
            .Sum(DisplayLossEventQuantityMt);

        var receiptById = loadingReceipts.ToDictionary(r => r.Id);
        var movementById = inventoryMovements.ToDictionary(m => m.Id);
        var dispatchById = directDispatches.ToDictionary(d => d.Id);
        var recordedDispatchLossIds = activeLosses
            .Where(e => e.Stage == LossEventStage.DispatchShortage && e.TruckDispatchId.HasValue)
            .Select(e => e.TruckDispatchId!.Value)
            .ToHashSet();

        // نمبر وسیلهٔ کسری از همان مرحله‌ای می‌آید که کسری روی آن ثبت شده؛
        // اگر کسری مستقیم به وسیله وصل نباشد، از رسید/حرکت به بارگیری آن می‌رسیم.
        string? ResolveLossVehicleNumber(LossEvent loss)
        {
            if (loss.TruckDispatchId.HasValue && dispatchById.TryGetValue(loss.TruckDispatchId.Value, out var lossDispatch))
            {
                return BuildVehicleNumber(lossDispatch);
            }

            if (loss.TransportLegId.HasValue && inventoryTransportLegById.TryGetValue(loss.TransportLegId.Value, out var lossLeg))
            {
                return BuildVehicleNumber(lossLeg);
            }

            var loadingRegisterId = loss.LoadingRegisterId;
            if (!loadingRegisterId.HasValue
                && loss.LoadingReceiptId.HasValue
                && receiptById.TryGetValue(loss.LoadingReceiptId.Value, out var lossReceipt))
            {
                loadingRegisterId = lossReceipt.LoadingRegisterId;
            }

            if (!loadingRegisterId.HasValue
                && loss.InventoryMovementId.HasValue
                && movementById.TryGetValue(loss.InventoryMovementId.Value, out var lossMovement)
                && lossMovement.LoadingReceiptId.HasValue
                && receiptById.TryGetValue(lossMovement.LoadingReceiptId.Value, out lossReceipt))
            {
                loadingRegisterId = lossReceipt.LoadingRegisterId;
            }

            return loadingRegisterId.HasValue && loadingById.TryGetValue(loadingRegisterId.Value, out var lossLoading)
                ? BuildVehicleNumber(lossLoading)
                : null;
        }

        var recordedLossItems = activeLosses
            .Select(e =>
            {
                var displayedQuantityMt = DisplayLossEventQuantityMt(e);
                var originalQuantityMt = e.DifferenceQuantityMt > 0m
                    ? e.DifferenceQuantityMt
                    : Math.Max(e.ChargeableLossMt, 0m);
                var ratio = originalQuantityMt > 0m
                    ? Math.Min(displayedQuantityMt / originalQuantityMt, 1m)
                    : 1m;
                return new ContractJourneyLossItemViewModel
                {
                    LossEventId = e.Id,
                    StageName = ToLossStageName(e.Stage),
                    EventDate = e.EventDate,
                    ExpectedQuantityMt = e.ExpectedQuantityMt,
                    ActualQuantityMt = e.ActualQuantityMt,
                    DifferenceQuantityMt = displayedQuantityMt,
                    ToleranceQuantityMt = decimal.Round(e.ToleranceQuantityMt * ratio, 4, MidpointRounding.AwayFromZero),
                    AllowableLossMt = decimal.Round(e.AllowableLossMt * ratio, 4, MidpointRounding.AwayFromZero),
                    ChargeableLossMt = decimal.Round(e.ChargeableLossMt * ratio, 4, MidpointRounding.AwayFromZero),
                    RelatedMovementId = e.InventoryMovementId,
                    VehicleNumber = ResolveLossVehicleNumber(e),
                    TraceKind = lossSourceQuantityById.ContainsKey(e.Id)
                        ? "Transport source allocation"
                        : ResolveLossTraceKind(e, contractId, shipmentIdSet, dispatchIdSet, loadingIds, receiptIds, movementIds, inventoryTransportLegIds)
                };
            })
            .ToList();
        var derivedDispatchLossItems = directDispatches
            .Where(d => !recordedDispatchLossIds.Contains(d.Id))
            .Select(d => new { Dispatch = d, DifferenceQuantityMt = DisplayDispatchShortageMt(d) })
            .Where(x => x.DifferenceQuantityMt > 0m)
            .Select(x =>
            {
                var toleranceQuantityMt = Math.Max(x.Dispatch.AllowanceMt ?? x.Dispatch.ToleranceMt ?? 0m, 0m);
                return new ContractJourneyLossItemViewModel
                {
                    LossEventId = 0,
                    StageName = ToLossStageName(LossEventStage.DispatchShortage),
                    EventDate = x.Dispatch.DispatchDate,
                    ExpectedQuantityMt = x.Dispatch.LoadedQuantityMt,
                    ActualQuantityMt = x.Dispatch.DischargedQuantityMt ?? Math.Max(x.Dispatch.LoadedQuantityMt - x.DifferenceQuantityMt, 0m),
                    DifferenceQuantityMt = x.DifferenceQuantityMt,
                    ToleranceQuantityMt = toleranceQuantityMt,
                    AllowableLossMt = Math.Min(x.DifferenceQuantityMt, toleranceQuantityMt),
                    ChargeableLossMt = Math.Max(x.Dispatch.ChargeableShortageMt ?? 0m, 0m),
                    VehicleNumber = BuildVehicleNumber(x.Dispatch),
                    TraceKind = $"TruckDispatch #{x.Dispatch.Id}"
                };
            })
            .ToList();
        var lossItems = recordedLossItems
            .Concat(derivedDispatchLossItems)
            .OrderByDescending(e => e.EventDate)
            .ThenByDescending(e => e.LossEventId)
            .ToList();
        var dispatchShortageLossMt = recordedDispatchLossMt + derivedDispatchLossItems.Sum(DisplayLossItemQuantityMt);
        var totalLossQuantityMt = lossItems.Sum(DisplayLossItemQuantityMt);

        if (isTabFragmentRequest && activeTab == ContractJourneyTabs.Details.Costs)
        {
            return View(BuildPurchaseTabPayload(
                baseModel,
                activeTab,
                lockContract,
                expenseItems: expenseItems,
                internalTransportCostItems: internalTransportCostItems,
                lossItems: lossItems,
                inventoryTransportExpenseAllocations: inventoryTransportExpenseAllocations,
                loadingDifferenceLossMt: loadingDifferenceLossMt,
                receiptShortageLossMt: receiptShortageLossMt,
                dispatchShortageLossMt: dispatchShortageLossMt,
                inventoryTransportLossMt: inventoryTransportLossMt));
        }

        var hasSaleIds = saleIdSet.Count > 0;
        var hasExpenseIds = expenseIdSet.Count > 0;
        var paymentEntities = await _db.PaymentTransactions
            .Include(p => p.CashAccount)
            .Include(p => p.PaidByPartner)
            .Include(p => p.LedgerEntry)
            .AsNoTracking()
            .Where(p => p.ContractId == contractId
                || (hasSaleIds && p.SalesTransactionId.HasValue && saleIdSet.Contains(p.SalesTransactionId.Value))
                || (hasExpenseIds && p.ExpenseTransactionId.HasValue && expenseIdSet.Contains(p.ExpenseTransactionId.Value))
                || (hasShipmentIds && p.ShipmentId.HasValue && shipmentIds.Contains(p.ShipmentId.Value))
                || (hasDispatchIds && p.TruckDispatchId.HasValue && dispatchIds.Contains(p.TruckDispatchId.Value)))
            .ToListAsync();
        var payments = paymentEntities
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToList();
        var paymentIds = payments.Select(p => p.Id).ToHashSet();

        var paymentItems = payments
            .Select(p => new ContractJourneyPaymentItemViewModel
            {
                PaymentTransactionId = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = ToPaymentDirectionName(p.Direction),
                PaymentKind = p.PaymentKind,
                PaymentKindName = ToPaymentKindName(p.PaymentKind),
                CashAccountName = p.CashAccount?.Name ?? string.Empty,
                FundingSource = p.FundingSource,
                PaidByPartnerName = p.PaidByPartner?.Name,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                LedgerEntryId = p.LedgerEntryId,
                TraceKind = ResolvePaymentTraceKind(p, contractId, saleIdSet, expenseIdSet, shipmentIdSet, dispatchIdSet)
            })
            .ToList();
        var sarrafSettlementItems = await BuildSarrafSettlementItemsAsync(contractId);
        var totalPaymentsUsd = payments.Sum(p => p.AmountUsd);
        var paymentInTotalUsd = payments.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd);
        var paymentOutTotalUsd = payments.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd);

        var hasPaymentIds = paymentIds.Count > 0;
        var ledgerEntities = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId == contractId
                || (hasSaleIds && l.SourceType == "Sale" && saleIdSet.Contains(l.SourceId))
                || (hasExpenseIds && l.SourceType == "Expense" && expenseIdSet.Contains(l.SourceId))
                || (hasPaymentIds && PaymentLedgerSourceTypes.Contains(l.SourceType) && paymentIds.Contains(l.SourceId))
                || (hasShipmentIds && l.ShipmentId.HasValue && shipmentIds.Contains(l.ShipmentId.Value)))
            .ToListAsync();
        var ledgers = ledgerEntities
            .GroupBy(l => l.Id)
            .Select(g => g.First())
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .ToList();

        var ledgerItems = ledgers
            .Select(l => new ContractJourneyLedgerItemViewModel
            {
                LedgerEntryId = l.Id,
                EntryDate = l.EntryDate,
                SideName = UiText.LedgerSideName(HttpContext, l.Side),
                AmountUsd = l.AmountUsd,
                SourceType = l.SourceType,
                SourceId = l.SourceId,
                Reference = l.Reference,
                Description = l.Description,
                TraceKind = ResolveLedgerTraceKind(l, contractId, saleIdSet, expenseIdSet, paymentIds, shipmentIdSet)
            })
            .ToList();
        var debitTotalUsd = ledgers.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd);
        var creditTotalUsd = ledgers.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd);
        var relatedBalanceUsd = await ReadContractBalanceUsdAsync(contractId);

        var stockSummary = await _stock.GetStockSummaryAsync(contractId: contractId);
        var currentStockQuantityMt = stockSummary.Sum(s => s.FreeQuantityMt);
        var hasNegativeStockWarning = currentStockQuantityMt < 0m || inventoryMovementItems.Any(m => m.RunningBalanceMt < 0m);

        var preSaleSectionState = preSaleItems.Any()
            ? new ContractJourneySectionStateViewModel()
            : new ContractJourneySectionStateViewModel
            {
                BadgeText = "نیاز به بررسی",
                BadgeClass = "needs-review",
                Message = "اتصال قطعی پیش‌فروش به این قرارداد نیاز به بررسی دارد.",
                IsNeedsReview = true
            };
        if (preSaleSectionState.IsNeedsReview)
        {
            notesForReview.Add(preSaleSectionState.Message!);
        }

        var dispatchSectionState = dispatchItems.Any()
            ? new ContractJourneySectionStateViewModel()
            : new ContractJourneySectionStateViewModel
            {
                BadgeText = "نیاز به بررسی",
                BadgeClass = "needs-review",
                Message = "اتصال قطعی دیسپچ به این قرارداد نیاز به بررسی دارد.",
                IsNeedsReview = true
            };
        if (dispatchSectionState.IsNeedsReview)
        {
            notesForReview.Add(dispatchSectionState.Message!);
        }

        var kpis = new ContractJourneyKpiSummaryViewModel
        {
            ContractQuantityMt = contract.QuantityMt,
            LoadedQuantityMt = totalLoadedQuantityMt,
            ReceivedQuantityMt = totalReceivedQuantityMt,
            DispatchedQuantityMt = dispatchedQuantityMt,
            SoldQuantityMt = soldQuantityMt,
            PreSaleQuantityMt = preSaleItems.Any() ? preSaleItems.Sum(p => p.QuantityMt) : null,
            CurrentStockQuantityMt = currentStockQuantityMt,
            LossQuantityMt = totalLossQuantityMt,
            TotalExpensesUsd = totalExpensesUsd,
            TotalPaymentsUsd = totalPaymentsUsd,
            RelatedBalanceUsd = relatedBalanceUsd,
            PreSaleNeedsReview = preSaleSectionState.IsNeedsReview,
            PreSaleNote = preSaleSectionState.Message
        };

        var quantityFlowItems = BuildQuantityFlowItems(
            contract.QuantityMt,
            totalLoadedQuantityMt,
            totalReceivedQuantityMt,
            dispatchedQuantityMt,
            soldQuantityMt,
            totalLossQuantityMt,
            currentStockQuantityMt,
            Math.Max(contract.QuantityMt - totalLoadedQuantityMt, 0m),
            Math.Max(totalLoadedQuantityMt - totalReceivedQuantityMt - receiptShortageLossMt, 0m));

        // The purchase aggregation snapshot was computed up-front (see the
        // top of this method). All downstream consumers below read from
        // the same snapshot so the numbers stay consistent and the future
        // InventoryTransportLeg filter can live in exactly one place.
        var loadingTransportExpenseUsd = purchaseAgg.LoadingTransportExpenseUsd;
        var loadingWarehouseExpenseUsd = purchaseAgg.LoadingWarehouseExpenseUsd;
        var loadingOtherExpenseUsd = purchaseAgg.LoadingOtherExpenseUsd;
        var loadingRailwayExpenseUsd = hasOfficialWagonRentExpense
            ? 0m
            : purchaseAgg.LoadingRailwayExpenseUsd;
        var loadingOperationalExpenseUsd =
            loadingTransportExpenseUsd +
            loadingWarehouseExpenseUsd +
            loadingOtherExpenseUsd +
            loadingRailwayExpenseUsd;
        var contractTransportExpenseUsd = loadingTransportExpenseUsd + expenseTransportFreightUsd;
        var contractStorageRentExpenseUsd = loadingWarehouseExpenseUsd + expenseStorageRentUsd;
        var pendingPurchaseQuantityMt = purchaseAgg.PendingPurchaseQuantityMt;
        // سود محقق‌شدهٔ بخش مالی پرونده قرارداد فقط از ProfitAndLossService می‌آید.
        // نگاشت فروش‌های قابل‌ردیابیِ این قرارداد (saleItems) کار همین کنترلر است،
        // اما درآمد و COGS آن‌ها بازمحاسبه نمی‌شود؛ فقط سهمِ همین قرارداد از هر فروش شمرده می‌شود.
        var realisedContractPnl = await _profitAndLoss.BuildForPurchaseContractSalesAsync(
            contract.Id,
            saleItems.Select(s => s.SalesTransactionId).ToList());
        var miniPnl = await BuildMiniPnlAsync(
            contractId,
            realisedContractPnl.ToViewModel(),
            "سود محقق فقط بر پایهٔ فروشِ انجام‌شده است (بهای کالای فروخته‌شده و سهمِ فروخته‌شدهٔ هزینه‌ها).");

        var kpiCards = BuildKpiCards(kpis);
        var timelineSteps = BuildTimelineSteps(
            contract.QuantityMt,
            totalLoadedQuantityMt,
            totalReceivedQuantityMt,
            dispatchItems.Count,
            dispatchedQuantityMt,
            directShipments.Count,
            shipmentQuantityMt,
            saleItems.Count,
            soldQuantityMt,
            totalExpensesUsd,
            payments.Count,
            totalPaymentsUsd,
            currentStockQuantityMt,
            miniPnl.GrossMarginUsd,
            dispatchSectionState,
            preSaleSectionState,
            loadingRegisters.Any(l => l.VesselId.HasValue || !string.IsNullOrWhiteSpace(l.BillOfLadingNumber)));

        var activityItems = BuildActivityItems(
            loadingItems,
            receiptItems,
            dispatchItems,
            saleItems,
            expenseItems,
            paymentItems,
            ledgerItems);

        var ledgerSummary = new ContractJourneyLedgerSummaryViewModel
        {
            DebitTotalUsd = debitTotalUsd,
            CreditTotalUsd = creditTotalUsd,
            BalanceUsd = relatedBalanceUsd,
            SourceTypeCounts = ledgers
                .GroupBy(l => l.SourceType)
                .OrderBy(g => g.Key)
                .Select(g => new ContractJourneySourceCountViewModel
                {
                    SourceType = g.Key,
                    Count = g.Count()
                })
                .ToList(),
            ReferenceList = ledgers
                .Select(l => l.Reference)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct()
                .Take(8)
                .Cast<string>()
                .ToList()
        };

        var salesCount = saleItems.Count + preSaleItems.Count;
        var nextAction = BuildPurchaseNextAction(
            contract.Id,
            loadingRegisters,
            totalLoadedQuantityMt,
            totalReceivedQuantityMt,
            currentStockQuantityMt,
            dispatchItems.Count,
            salesCount,
            expenseItems.Count,
            paymentItems.Count);
        var warnings = BuildWarnings(
            contract,
            baseModel.PricingNeedsReview,
            baseModel.PricingFallbackApplied,
            totalLoadedQuantityMt,
            totalReceivedQuantityMt,
            loadingItems.Count,
            receiptItems.Count,
            dispatchItems.Count,
            salesCount,
            expenseItems.Count,
            paymentItems.Count,
            isSaleContract: false,
            unreceiptedLoadingCount,
            hasNegativeStockWarning,
            salesWithoutTraceCount: 0).ToList();
        if (pendingPurchaseQuantityMt > 0m)
        {
            warnings.Add("برخی بارگیری‌ها نرخ ندارند؛ P&L نهایی نیست.");
        }

        if (directSaleQuantityMismatchWarning is not null)
        {
            warnings.Add(directSaleQuantityMismatchWarning);
        }

        var visibleShipmentCount = directShipments
            .Select(s => s.Id)
            .Concat(shipmentScenarios.Select(s => s.ShipmentId))
            .Distinct()
            .Count();
        var visibleShipmentQuantityMt = shipmentScenarios.Count > 0
            ? shipmentScenarios.Sum(s => s.AllocatedQuantityMt)
            : shipmentQuantityMt;

        var pendingTankSettlementQuantityMt = await GetPendingTankSettlementQuantityMtAsync(baseModel.ContractId);

        return View(new ContractJourneyDetailsViewModel
        {
            ActiveTab = activeTab,
            LockContract = lockContract,
            ContractId = baseModel.ContractId,
            ContractName = baseModel.ContractName,
            ContractNumber = baseModel.ContractNumber,
            ContractTypeName = baseModel.ContractTypeName,
            ContractTypeBadgeClass = baseModel.ContractTypeBadgeClass,
            CompanyName = baseModel.CompanyName,
            ProductName = baseModel.ProductName,
            ContractUnitText = baseModel.ContractUnitText,
            SupplierName = baseModel.SupplierName,
            CustomerName = baseModel.CustomerName,
            ContractQuantityMt = baseModel.ContractQuantityMt,
            HasMixedLoadingPrices = baseModel.HasMixedLoadingPrices,
            LoadingsValueUsd = baseModel.LoadingsValueUsd,
            ParentContractId = baseModel.ParentContractId,
            ParentContractNumber = baseModel.ParentContractNumber,
            SubContractItems = baseModel.SubContractItems,
            IsPartnershipContract = baseModel.IsPartnershipContract,
            PartnerShareItems = baseModel.PartnerShareItems,
            Currency = baseModel.Currency,
            PriceDisplay = baseModel.PriceDisplay,
            PricingMethodName = baseModel.PricingMethodName,
            PricingStatusName = baseModel.PricingStatusName,
            RubSettlementSummary = rubSettlementSummary,
            EditPricingUrl = baseModel.EditPricingUrl,
            PricingFormulaText = baseModel.PricingFormulaText,
            PricingFinalUnitPriceUsd = baseModel.PricingFinalUnitPriceUsd,
            PricingNeedsReview = baseModel.PricingNeedsReview,
            PricingReason = baseModel.PricingReason,
            PricingFallbackApplied = baseModel.PricingFallbackApplied,
            PricingFormulaNote = baseModel.PricingFormulaNote,
            StatusName = baseModel.StatusName,
            StatusBadgeClass = baseModel.StatusBadgeClass,
            ContractDate = baseModel.ContractDate,
            StartDate = baseModel.StartDate,
            EndDate = baseModel.EndDate,
            Notes = baseModel.Notes,
            IsPurchaseContract = baseModel.IsPurchaseContract,
            ShipmentCount = visibleShipmentCount,
            ShipmentQuantityMt = visibleShipmentQuantityMt,
            LoadingDocumentReferences = loadingDocumentReferences,
            UnreceiptedLoadingCount = unreceiptedLoadingCount,
            ReceiptDifferenceQuantityMt = receiptDifferenceQuantityMt,
            LoadingDifferenceLossMt = loadingDifferenceLossMt,
            ReceiptShortageLossMt = receiptShortageLossMt,
            DispatchShortageLossMt = dispatchShortageLossMt,
            InventoryTransportLossMt = inventoryTransportLossMt,
            TankLossMt = tankLossMt,
            SalesLossMt = salesLossMt,
            PendingTankSettlementQuantityMt = pendingTankSettlementQuantityMt,
            InventoryInQuantityMt = inventoryInQuantityMt,
            InventoryOutQuantityMt = inventoryOutQuantityMt,
            HasNegativeStockWarning = hasNegativeStockWarning,
            SalesFromInventoryCount = saleItems.Count(s => s.HasInventoryMovementTrace),
            SalesWithoutTraceCount = 0,
            DispatchFreightCostUsd = dispatchFreightCostUsd,
            DispatchWithoutInventoryTraceCount = dispatchWithoutInventoryTraceCount,
            LoadingOperationalExpenseUsd = loadingOperationalExpenseUsd,
            LoadingRailwayExpenseUsd = loadingRailwayExpenseUsd,
            LoadingWarehouseExpenseUsd = loadingWarehouseExpenseUsd,
            ContractTransportExpenseUsd = contractTransportExpenseUsd,
            ContractStorageRentExpenseUsd = contractStorageRentExpenseUsd,
            PendingLoadingPriceCount = purchaseAgg.PendingLoadingCount,
            PendingLoadingPriceQuantityMt = pendingPurchaseQuantityMt,
            CustomsDeclarationCount = customsDeclarations.Count,
            CustomsDeclarationTotalUsd = customsDeclarationTotalUsd,
            PaymentInTotalUsd = paymentInTotalUsd,
            PaymentOutTotalUsd = paymentOutTotalUsd,
            Kpis = kpis,
            KpiCards = kpiCards,
            TimelineSteps = timelineSteps,
            QuantityFlowItems = quantityFlowItems,
            ExpenseBreakdowns = expenseBreakdowns,
            ShipmentScenarios = shipmentScenarios,
            InventoryTransportExpenseAllocations = inventoryTransportExpenseAllocations,
            ActivityItems = activityItems,
            DispatchSectionState = dispatchSectionState,
            PreSaleSectionState = preSaleSectionState,
            LoadingItems = loadingItems,
            ReceiptItems = receiptItems.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Id).ToList(),
            BulkReceiptCandidates = bulkReceiptCandidates,
            ReceiptAllocationItems = receiptAllocationItems,
            InventoryMovementItems = inventoryMovementItems.OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id).ToList(),
            InventoryTransportLegItems = inventoryTransportLegItems.OrderByDescending(l => l.LoadedDate).ThenByDescending(l => l.Id).ToList(),
            DispatchItems = dispatchItems,
            SalesItems = saleItems,
            PreSaleItems = preSaleItems,
            ExpenseItems = expenseItems,
            InternalTransportCostItems = internalTransportCostItems,
            LossItems = lossItems,
            PaymentItems = paymentItems,
            SarrafSettlementItems = sarrafSettlementItems,
            LedgerSummary = ledgerSummary,
            LedgerItems = ledgerItems,
            MiniPnl = miniPnl,
            NotesForReview = notesForReview,
            Warnings = warnings,
            NextRecommendedActionTitle = nextAction.Title,
            NextRecommendedActionDescription = nextAction.Description,
            NextRecommendedActionUrl = nextAction.Url,
            NextRecommendedActionCssClass = nextAction.CssClass
        });
    }

    private static ContractJourneyDetailsViewModel BuildPurchaseTabPayload(
        ContractJourneyDetailsViewModel source,
        string activeTab,
        bool lockContract,
        ContractJourneyRubSettlementSummaryViewModel? rubSettlementSummary = null,
        IReadOnlyList<ContractJourneyLoadingItemViewModel>? loadingItems = null,
        IReadOnlyList<ContractJourneyInventoryMovementItemViewModel>? inventoryMovementItems = null,
        IReadOnlyList<ContractJourneyTransportLegItemViewModel>? inventoryTransportLegItems = null,
        IReadOnlyList<ContractJourneyDispatchItemViewModel>? dispatchItems = null,
        IReadOnlyList<ContractJourneySaleItemViewModel>? salesItems = null,
        IReadOnlyList<ContractJourneyPreSaleItemViewModel>? preSaleItems = null,
        IReadOnlyList<ContractJourneyShipmentScenarioViewModel>? shipmentScenarios = null,
        IReadOnlyList<ContractJourneyExpenseItemViewModel>? expenseItems = null,
        IReadOnlyList<ContractJourneyLossItemViewModel>? lossItems = null,
        IReadOnlyList<ContractJourneyInternalTransportCostItemViewModel>? internalTransportCostItems = null,
        IReadOnlyList<ContractJourneyTransportLegExpenseAllocationViewModel>? inventoryTransportExpenseAllocations = null,
        decimal loadingDifferenceLossMt = 0m,
        decimal receiptShortageLossMt = 0m,
        decimal dispatchShortageLossMt = 0m,
        decimal inventoryTransportLossMt = 0m)
        => new()
        {
            ActiveTab = activeTab,
            LockContract = lockContract,
            ContractId = source.ContractId,
            ContractName = source.ContractName,
            ContractNumber = source.ContractNumber,
            ContractTypeName = source.ContractTypeName,
            ContractTypeBadgeClass = source.ContractTypeBadgeClass,
            CompanyName = source.CompanyName,
            ProductName = source.ProductName,
            ContractUnitText = source.ContractUnitText,
            SupplierName = source.SupplierName,
            CustomerName = source.CustomerName,
            ContractQuantityMt = source.ContractQuantityMt,
            HasMixedLoadingPrices = source.HasMixedLoadingPrices,
            LoadingsValueUsd = source.LoadingsValueUsd,
            ParentContractId = source.ParentContractId,
            ParentContractNumber = source.ParentContractNumber,
            Currency = source.Currency,
            PriceDisplay = source.PriceDisplay,
            PricingMethodName = source.PricingMethodName,
            PricingStatusName = source.PricingStatusName,
            RubSettlementSummary = rubSettlementSummary ?? source.RubSettlementSummary,
            EditPricingUrl = source.EditPricingUrl,
            PricingFormulaText = source.PricingFormulaText,
            PricingFinalUnitPriceUsd = source.PricingFinalUnitPriceUsd,
            PricingNeedsReview = source.PricingNeedsReview,
            PricingReason = source.PricingReason,
            PricingFallbackApplied = source.PricingFallbackApplied,
            PricingFormulaNote = source.PricingFormulaNote,
            StatusName = source.StatusName,
            StatusBadgeClass = source.StatusBadgeClass,
            ContractDate = source.ContractDate,
            StartDate = source.StartDate,
            EndDate = source.EndDate,
            Notes = source.Notes,
            IsPurchaseContract = source.IsPurchaseContract,
            LoadingItems = loadingItems ?? [],
            InventoryMovementItems = inventoryMovementItems ?? [],
            InventoryTransportLegItems = inventoryTransportLegItems ?? [],
            DispatchItems = dispatchItems ?? [],
            SalesItems = salesItems ?? [],
            PreSaleItems = preSaleItems ?? [],
            ShipmentScenarios = shipmentScenarios ?? [],
            ExpenseItems = expenseItems ?? [],
            InternalTransportCostItems = internalTransportCostItems ?? [],
            LossItems = lossItems ?? [],
            InventoryTransportExpenseAllocations = inventoryTransportExpenseAllocations ?? [],
            LoadingDifferenceLossMt = loadingDifferenceLossMt,
            ReceiptShortageLossMt = receiptShortageLossMt,
            DispatchShortageLossMt = dispatchShortageLossMt,
            InventoryTransportLossMt = inventoryTransportLossMt
        };

    private async Task<ContractJourneyDetailsViewModel> BuildPurchaseInventoryDetailsAsync(
        int contractId,
        ContractJourneyDetailsViewModel baseModel,
        bool lockContract)
    {
        var movements = await _db.InventoryMovements
            .Include(m => m.Terminal)
            .Include(m => m.StorageTank)
            .AsNoTracking()
            .Where(m => m.ContractId == contractId)
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => m.Id)
            .ToListAsync();
        var saleIds = movements
            .Where(m => m.SalesTransactionId.HasValue)
            .Select(m => m.SalesTransactionId!.Value)
            .Distinct()
            .ToList();
        var invoiceBySaleId = saleIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => saleIds.Contains(s.Id))
                .Select(s => new { s.Id, s.InvoiceNumber })
                .ToDictionaryAsync(s => s.Id, s => s.InvoiceNumber);

        decimal runningBalanceMt = 0m;
        var items = movements.Select(movement =>
        {
            var signedQuantityMt = ToSignedQuantity(movement.Direction, movement.QuantityMt);
            runningBalanceMt += signedQuantityMt;
            return new ContractJourneyInventoryMovementItemViewModel
            {
                Id = movement.Id,
                MovementDate = movement.MovementDate,
                DirectionName = ToMovementDirectionName(movement.Direction),
                QuantityMt = movement.QuantityMt,
                SignedQuantityMt = signedQuantityMt,
                RunningBalanceMt = runningBalanceMt,
                TerminalName = movement.Terminal?.Name ?? string.Empty,
                StorageTankCode = StorageTankDisplay.BuildOptional(movement.StorageTank),
                ReferenceDocument = movement.ReferenceDocument,
                SourceLabel = movement.SalesTransactionId.HasValue
                    && invoiceBySaleId.TryGetValue(movement.SalesTransactionId.Value, out var invoice)
                        ? invoice
                        : movement.ReferenceDocument,
                SalesTransactionId = movement.SalesTransactionId
            };
        }).ToList();

        return BuildPurchaseTabPayload(
            baseModel,
            ContractJourneyTabs.Details.Inventory,
            lockContract,
            inventoryMovementItems: items);
    }

    private async Task<ContractJourneyDetailsViewModel> BuildPurchaseDispatchDetailsAsync(
        int contractId,
        ContractJourneyDetailsViewModel baseModel,
        bool lockContract)
    {
        var dispatches = await _db.TruckDispatches
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.DestinationLocation)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.LoadingReceipt)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.DestinationLocation)
            .AsNoTracking()
            .Where(d => d.ContractId == contractId)
            .OrderBy(d => d.DispatchDate)
            .ThenBy(d => d.Id)
            .ToListAsync();
        var allocationIds = dispatches
            .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.LoadingReceiptAllocationId.HasValue)
            .Select(d => d.LoadingReceiptAllocationId!.Value)
            .Distinct()
            .ToList();
        var allocationQuantityRows = allocationIds.Count == 0
            ? []
            : await _db.TruckDispatches
                .AsNoTracking()
                .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt
                    && d.Status != DispatchStatus.Cancelled
                    && d.LoadingReceiptAllocationId.HasValue
                    && allocationIds.Contains(d.LoadingReceiptAllocationId.Value))
                .Select(d => new { AllocationId = d.LoadingReceiptAllocationId!.Value, d.LoadedQuantityMt })
                .ToListAsync();
        var quantityByAllocationId = allocationQuantityRows
            .GroupBy(d => d.AllocationId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.LoadedQuantityMt));

        var referenceMap = dispatches.ToDictionary(d => $"TRUCK-DISPATCH:{d.Id}", d => d.Id);
        var references = referenceMap.Keys.ToList();
        var movements = references.Count == 0
            ? []
            : await _db.InventoryMovements
                .Include(m => m.Terminal)
                .Include(m => m.StorageTank)
                .AsNoTracking()
                .Where(m => m.ContractId == contractId
                    && m.ReferenceDocument != null
                    && references.Contains(m.ReferenceDocument))
                .ToListAsync();
        var movementByDispatchId = movements
            .Select(m => new { DispatchId = referenceMap.GetValueOrDefault(m.ReferenceDocument ?? string.Empty), Movement = m })
            .Where(x => x.DispatchId > 0)
            .GroupBy(x => x.DispatchId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Movement.Id).First().Movement);

        var items = dispatches.Select(dispatch =>
        {
            movementByDispatchId.TryGetValue(dispatch.Id, out var movement);
            var allocation = dispatch.LoadingReceiptAllocation;
            decimal? dispatchedForAllocation = allocation is null
                ? null
                : quantityByAllocationId.GetValueOrDefault(allocation.Id);
            return new ContractJourneyDispatchItemViewModel
            {
                Id = dispatch.Id,
                DispatchMode = dispatch.DispatchMode,
                LoadingReceiptAllocationId = dispatch.LoadingReceiptAllocationId,
                LoadingReceiptId = allocation?.LoadingReceiptId,
                DispatchDate = dispatch.DispatchDate,
                TruckPlateNumber = dispatch.Truck?.PlateNumber ?? string.Empty,
                DriverName = dispatch.Driver?.FullName,
                DestinationName = dispatch.DestinationLocation?.Name
                    ?? allocation?.DestinationName
                    ?? allocation?.DestinationLocation?.Name
                    ?? allocation?.DestinationReference,
                StatusName = dispatch.Status.ToString(),
                LoadedQuantityMt = dispatch.LoadedQuantityMt,
                DischargedQuantityMt = dispatch.DischargedQuantityMt,
                AllocationQuantityMt = allocation?.QuantityMt,
                AllocationTotalDirectDispatchedQuantityMt = dispatchedForAllocation,
                AllocationRemainingQuantityMt = allocation is null || !dispatchedForAllocation.HasValue
                    ? null
                    : allocation.QuantityMt - dispatchedForAllocation.Value,
                SourceTerminalName = movement?.Terminal?.Name,
                SourceStorageTankCode = StorageTankDisplay.BuildOptional(movement?.StorageTank),
                FreightCostUsd = dispatch.FreightCostUsd,
                ReferenceDocument = movement?.ReferenceDocument,
                TraceKind = dispatch.DispatchMode == TruckDispatchMode.DirectFromReceipt
                    ? "Direct Truck Dispatch from Receipt Allocation"
                    : movement is null ? "مستقیم از قرارداد" : "مستقیم از قرارداد و سند موجودی"
            };
        }).OrderByDescending(d => d.DispatchDate).ThenByDescending(d => d.Id).ToList();

        return BuildPurchaseTabPayload(
            baseModel,
            ContractJourneyTabs.Details.Dispatch,
            lockContract,
            dispatchItems: items);
    }

    // مجموع موجودی دفتری مخزن‌هایی که رسید «ضایعات بعداً از تسویه مخزن» این قرارداد
    // در آن‌ها هنوز موجودی مثبت دارد — یعنی ضایعهٔ نهایی هنوز مشخص نشده (وضعیت موقت).
    // فقط خواندنی است و هیچ stock/ledger نمی‌سازد.
    // قیمت هر بارگیری مستقل ذخیره می‌شود. اینجا فقط دو چیز خوانده می‌شود: آیا قیمت‌ها متفاوت‌اند،
    // و ارزش واقعی بارگیری‌ها = Sum(LoadedQuantityMt × LoadingPriceUsd).
    public static async Task<(bool HasMixedPrices, decimal LoadingsValueUsd)> LoadLoadingPriceSpreadAsync(
        ApplicationDbContext db,
        int contractId)
    {
        var pricedGroups = await db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == contractId && l.LoadingPriceUsd != null && l.LoadingPriceUsd > 0m)
            .GroupBy(l => l.LoadingPriceUsd!.Value)
            .Select(g => new { UnitPriceUsd = g.Key, QuantityMt = g.Sum(l => l.LoadedQuantityMt) })
            .ToListAsync();

        var valueUsd = pricedGroups.Sum(g => decimal.Round(g.QuantityMt * g.UnitPriceUsd, 4, MidpointRounding.AwayFromZero));
        return (pricedGroups.Count > 1, valueUsd);
    }

    // گزارش تجمیعی زیرقراردادها برای قرارداد اصلی. فقط خواندنی است؛ پرداخت، دفتر کل و مصارف
    // روی همان زیرقراردادی می‌مانند که در آن ثبت شده‌اند.
    public static async Task<IReadOnlyList<ContractJourneySubContractItemViewModel>> LoadSubContractSummariesAsync(
        ApplicationDbContext db,
        int contractId)
    {
        var children = await db.Contracts
            .AsNoTracking()
            .Where(c => c.ParentContractId == contractId)
            .OrderBy(c => c.ContractNumber)
            .Select(c => new { c.Id, c.ContractName, c.ContractNumber, c.QuantityMt, c.Status })
            .ToListAsync();

        if (children.Count == 0)
        {
            return [];
        }

        var childIds = children.Select(c => c.Id).ToList();
        var loadingTotals = await db.LoadingRegisters
            .AsNoTracking()
            .Where(l => childIds.Contains(l.ContractId))
            .GroupBy(l => l.ContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                LoadedQuantityMt = g.Sum(l => l.LoadedQuantityMt),
                LoadingsValueUsd = g.Sum(l => l.LoadedQuantityMt * (l.LoadingPriceUsd ?? 0m))
            })
            .ToDictionaryAsync(g => g.ContractId);

        return children
            .Select(child =>
            {
                var loaded = loadingTotals.TryGetValue(child.Id, out var totals) ? totals.LoadedQuantityMt : 0m;
                var value = loadingTotals.TryGetValue(child.Id, out var valueTotals) ? valueTotals.LoadingsValueUsd : 0m;
                return new ContractJourneySubContractItemViewModel
                {
                    ContractId = child.Id,
                    ContractName = child.ContractName,
                    ContractNumber = child.ContractNumber,
                    StatusName = ToContractStatusName(child.Status),
                    QuantityMt = child.QuantityMt,
                    LoadedQuantityMt = loaded,
                    RemainingQuantityMt = Math.Max(child.QuantityMt - loaded, 0m),
                    LoadingsValueUsd = decimal.Round(value, 4, MidpointRounding.AwayFromZero)
                };
            })
            .ToList();
    }

    // سهم شرکای قرارداد شراکتی. فقط خواندنی برای نمایش در تب خلاصه؛ هیچ محاسبهٔ مالی
    // یا ثبت دفترکل بر اساس آن انجام نمی‌شود.
    public static async Task<IReadOnlyList<ContractJourneyPartnerShareItemViewModel>> LoadContractPartnerSharesAsync(
        ApplicationDbContext db,
        int contractId)
    {
        // // PTG-P0-03 — سهم تاریخ‌دار شد؛ برای نمایش فقط آخرین بازهٔ هر شریک دیده می‌شود.
        var rows = await db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.ContractId == contractId)
            .Select(cp => new
            {
                cp.PartnerId,
                PartnerCode = cp.Partner != null ? cp.Partner.Code : string.Empty,
                PartnerName = cp.Partner != null ? cp.Partner.Name : string.Empty,
                cp.SharePercent,
                cp.EffectiveFrom
            })
            .ToListAsync();

        return rows
            .GroupBy(x => x.PartnerId)
            .Select(g => g.OrderByDescending(x => x.EffectiveFrom).First())
            .OrderByDescending(x => x.SharePercent)
            .ThenBy(x => x.PartnerName)
            .Select(x => new ContractJourneyPartnerShareItemViewModel
            {
                PartnerId = x.PartnerId,
                PartnerCode = x.PartnerCode,
                PartnerName = x.PartnerName,
                SharePercent = x.SharePercent
            })
            .ToList();
    }

    private async Task<decimal> GetPendingTankSettlementQuantityMtAsync(int contractId)
    {
        var deferredTankIds = await _db.LoadingReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.LossMode == ReceiptLossMode.DeferredTankSettlement
                && r.ReceiptDestination == LoadingReceiptDestination.ToInventory
                && r.StorageTankId != null
                && r.LoadingRegister != null
                && r.LoadingRegister.ContractId == contractId)
            .Select(r => r.StorageTankId!.Value)
            .Distinct()
            .ToListAsync();

        if (deferredTankIds.Count == 0)
        {
            return 0m;
        }

        var balanceByTank = (await _db.InventoryMovements.AsNoTracking()
            .Where(m => m.StorageTankId != null
                && deferredTankIds.Contains(m.StorageTankId!.Value)
                && (m.ContractId == contractId
                    || (m.ContractId == null
                        && m.LoadingReceipt != null
                        && m.LoadingReceipt.LoadingRegister != null
                        && m.LoadingReceipt.LoadingRegister.ContractId == contractId)))
            .Select(m => new { m.StorageTankId, m.Direction, m.QuantityMt })
            .ToListAsync())
            .GroupBy(m => m.StorageTankId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m));

        return balanceByTank.Values.Where(b => b > 0m).Sum();
    }

    private static List<ContractJourneyLoadingItemViewModel> BuildLoadingItems(
        IReadOnlyList<LoadingRegister> loadingRegisters,
        string productName,
        IReadOnlyDictionary<int, decimal> receiptQuantityByLoadingId,
        decimal? basePlattsPrice,
        decimal? premiumDiscountUsd,
        Func<LoadingRegister, decimal?> resolveEffectiveLoadingPriceUsd)
        => loadingRegisters
            .Select(l => new ContractJourneyLoadingItemViewModel
            {
                Id = l.Id,
                LoadingDate = l.LoadingDate,
                ProductName = productName,
                BillOfLadingNumber = l.BillOfLadingNumber,
                RwbNo = l.RwbNo,
                TransportTypeName = ToLoadingTransportTypeName(l.TransportType),
                TransportTypeLabel = ToLoadingTransportTypeName(l.TransportType),
                DocumentSummary = BuildLoadingDocumentSummary(l),
                WagonNumber = l.WagonNumber,
                RouteDescription = l.RouteDescription,
                LogisticsCompanyName = l.LogisticsCompanyName,
                LoadedQuantityMt = l.LoadedQuantityMt,
                TotalReceivedQuantityMt = receiptQuantityByLoadingId.TryGetValue(l.Id, out var receivedQty) ? receivedQty : 0m,
                PlattsUsd = l.PlattsUsd ?? basePlattsPrice,
                PremiumDiscountUsd = premiumDiscountUsd,
                LoadingPriceUsd = resolveEffectiveLoadingPriceUsd(l),
                SettlementCurrencyCode = l.SettlementCurrencyCode,
                RubRateStatus = l.RubRateStatus,
                RubPerUsdRate = l.RubPerUsdRate,
                RubRateDate = l.RubRateDate,
                RubRateSource = l.RubRateSource,
                AmountUsdAtRubLock = l.AmountUsdAtRubLock,
                AmountRubAtRubLock = l.AmountRubAtRubLock,
                SettlementUnitPriceRub = l.SettlementUnitPriceRub,
                SettlementValueRub = l.SettlementValueRub,
                TransportExpenseUsd = l.TransportExpenseUsd,
                WarehouseExpenseUsd = l.WarehouseExpenseUsd,
                OtherExpenseUsd = l.OtherExpenseUsd,
                RailwayExpenseUsd = l.RailwayExpenseUsd,
                ConsigneeName = l.ConsigneeName,
                DestinationName = l.DestinationName,
                Notes = l.Notes,
                VehicleSummary = BuildVehicleSummary(l.Vessel?.Name, l.Truck?.PlateNumber),
                VehicleNumber = BuildVehicleNumber(l)
            })
            .ToList();

    private static List<ContractJourneyBulkReceiptCandidateViewModel> BuildBulkReceiptCandidates(
        IReadOnlyList<LoadingRegister> loadingRegisters,
        IReadOnlyDictionary<int, ContractJourneyLoadingItemViewModel> loadingItemById,
        IReadOnlyDictionary<int, decimal> receiptQuantityByLoadingId,
        IReadOnlyDictionary<int, decimal> receiptShortageQuantityByLoadingId)
        => loadingRegisters
            .Select(l =>
            {
                var alreadyReceivedQuantityMt = receiptQuantityByLoadingId.GetValueOrDefault(l.Id);
                var receiptShortageQuantityMt = receiptShortageQuantityByLoadingId.GetValueOrDefault(l.Id);
                var loadingItem = loadingItemById[l.Id];
                return new ContractJourneyBulkReceiptCandidateViewModel
                {
                    LoadingRegisterId = l.Id,
                    LoadingDate = l.LoadingDate,
                    ProductName = loadingItem.ProductName,
                    TransportTypeLabel = loadingItem.TransportTypeLabel,
                    VehicleNumber = loadingItem.VehicleNumber,
                    BillOfLadingNumber = l.BillOfLadingNumber,
                    RwbNo = l.RwbNo,
                    WagonNumber = l.WagonNumber,
                    LoadedQuantityMt = l.LoadedQuantityMt,
                    LoadingPriceUsd = loadingItem.LoadingPriceUsd,
                    LoadingValueUsd = loadingItem.LoadingValueUsd,
                    AlreadyReceivedQuantityMt = alreadyReceivedQuantityMt,
                    RemainingQuantityMt = Math.Max(l.LoadedQuantityMt - alreadyReceivedQuantityMt - receiptShortageQuantityMt, 0m),
                    ConsigneeName = l.ConsigneeName,
                    DestinationName = l.DestinationName
                };
            })
            .Where(l => l.RemainingQuantityMt > 0m)
            .OrderBy(l => l.LoadingDate)
            .ThenBy(l => l.LoadingRegisterId)
            .ToList();

    // مسیر سبک تب «رسید بارگیری». فقط داده‌ای که همین تب رندر می‌کند خوانده می‌شود:
    // بارگیری‌ها، رسیدهای بارگیری، رویدادهای کسری رسید و گزینه‌های ترمینال/مخزن فرم رسید
    // گروهی. محاسبات فروش، دیسپچ، مصارف، پرداخت، دفتر کل و P&L در این تب نمایش داده
    // نمی‌شوند و اجرا نمی‌شوند — منطق مالی/موجودی تغییری نکرده، فقط خوانده نمی‌شود.
    private async Task<ContractJourneyDetailsViewModel> BuildPurchaseReceiptsDetailsAsync(
        Contract contract,
        ContractJourneyDetailsViewModel baseModel,
        ContractPriceResult pricingResult,
        bool lockContract)
    {
        var contractId = contract.Id;
        var contractFinalPriceUsd = pricingResult.FinalUnitPrice;

        var loadingRegisters = await _db.LoadingRegisters
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            .OrderBy(l => l.LoadingDate)
            .ThenBy(l => l.Id)
            .ToListAsync();
        var loadingIds = loadingRegisters.Select(l => l.Id).ToList();
        var loadingById = loadingRegisters.ToDictionary(l => l.Id);
        var rubSettlementSummary = BuildRubSettlementSummary(contract, loadingRegisters, contractFinalPriceUsd);

        decimal? ResolveEffectiveLoadingPriceUsd(LoadingRegister loading)
            => HasValidLoadingPrice(loading.LoadingPriceUsd)
                ? loading.LoadingPriceUsd
                : contractFinalPriceUsd;

        var loadingReceipts = loadingIds.Count == 0
            ? new List<LoadingReceipt>()
            : await _db.LoadingReceipts
                .Include(r => r.Terminal)
                .Include(r => r.StorageTank)
                .Include(r => r.InventoryMovement)
                .AsNoTracking()
                .Where(r => loadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled)
                .OrderBy(r => r.LoadingRegisterId)
                .ThenBy(r => r.ReceiptDate)
                .ThenBy(r => r.Id)
                .ToListAsync();
        var receiptIds = loadingReceipts.Select(r => r.Id).ToList();
        var receiptLoadingById = loadingReceipts.ToDictionary(r => r.Id, r => r.LoadingRegisterId);
        // همان فیلترِ مسیر کامل (`!IsCancelled`) — پس جمع رسیدها یکسان می‌ماند.
        var receiptQuantityByLoadingId = loadingReceipts
            .GroupBy(r => r.LoadingRegisterId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.ReceivedQuantityMt));

        var loadingItems = BuildLoadingItems(
            loadingRegisters,
            contract.Product?.Name ?? string.Empty,
            receiptQuantityByLoadingId,
            pricingResult.BasePlattsPrice,
            pricingResult.PremiumDiscountUsd,
            ResolveEffectiveLoadingPriceUsd);
        var loadingItemById = loadingItems.ToDictionary(item => item.Id);

        var receiptItems = new List<ContractJourneyReceiptItemViewModel>();
        foreach (var receiptGroup in loadingReceipts.GroupBy(r => r.LoadingRegisterId))
        {
            var loadedQuantityMt = loadingById.TryGetValue(receiptGroup.Key, out var loading)
                ? loading.LoadedQuantityMt
                : 0m;
            decimal cumulativeReceiptQuantityMt = 0m;

            foreach (var receipt in receiptGroup.OrderBy(r => r.ReceiptDate).ThenBy(r => r.Id))
            {
                cumulativeReceiptQuantityMt += receipt.ReceivedQuantityMt;
                receiptItems.Add(new ContractJourneyReceiptItemViewModel
                {
                    Id = receipt.Id,
                    LoadingRegisterId = receipt.LoadingRegisterId,
                    LoadingVehicleNumber = BuildVehicleNumber(loading),
                    ReceiptDate = receipt.ReceiptDate,
                    ReceivedQuantityMt = receipt.ReceivedQuantityMt,
                    TerminalName = receipt.Terminal?.Name ?? string.Empty,
                    StorageTankCode = StorageTankDisplay.BuildOptional(receipt.StorageTank),
                    InventoryMovementId = receipt.InventoryMovement?.Id,
                    RemainingLoadingMt = Math.Max(loadedQuantityMt - cumulativeReceiptQuantityMt, 0m),
                    ActualArrivedQuantityMt = receipt.ActualArrivedQuantityMt,
                    DifferenceQuantityMt = receipt.ActualArrivedQuantityMt.HasValue
                        ? receipt.ActualArrivedQuantityMt.Value - receipt.ReceivedQuantityMt
                        : null,
                    ReferenceDocument = receipt.ReferenceDocument ?? receipt.InventoryMovement?.ReferenceDocument
                });
            }
        }

        // دامنهٔ رویدادهای کسری رسید دقیقاً همان دامنهٔ مسیر کامل است (قرارداد، بارگیری،
        // رسید، دیسپچ، سفر حمل و سند موجودیِ همین قرارداد) — فقط با EXISTS در یک کوئری.
        var hasLoadingIds = loadingIds.Count > 0;
        var hasReceiptIds = receiptIds.Count > 0;
        var receiptShortageRows = await _db.LossEvents
            .AsNoTracking()
            .Where(e => e.Stage == LossEventStage.ReceiptShortage
                && !e.IsCancelled
                && (e.ContractId == contractId
                    || (hasLoadingIds && e.LoadingRegisterId.HasValue && loadingIds.Contains(e.LoadingRegisterId.Value))
                    || (hasReceiptIds && e.LoadingReceiptId.HasValue && receiptIds.Contains(e.LoadingReceiptId.Value))
                    || (e.TruckDispatchId.HasValue
                        && _db.TruckDispatches.Any(d => d.Id == e.TruckDispatchId.Value && d.ContractId == contractId))
                    || (e.TransportLegId.HasValue
                        && _db.InventoryTransportLegs.Any(l => l.Id == e.TransportLegId.Value && l.SourcePurchaseContractId == contractId))
                    || (e.InventoryMovementId.HasValue
                        && _db.InventoryMovements.Any(m => m.Id == e.InventoryMovementId.Value && m.ContractId == contractId))))
            .Select(e => new
            {
                e.Id,
                e.LoadingRegisterId,
                e.LoadingReceiptId,
                e.DifferenceQuantityMt,
                e.ChargeableLossMt
            })
            .ToListAsync();
        var receiptShortageLossRows = receiptShortageRows
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToList();
        static decimal DisplayShortageQuantityMt(decimal differenceQuantityMt, decimal chargeableLossMt)
            => differenceQuantityMt > 0m ? differenceQuantityMt : Math.Max(chargeableLossMt, 0m);
        var receiptShortageLossMt = receiptShortageLossRows
            .Sum(e => DisplayShortageQuantityMt(e.DifferenceQuantityMt, e.ChargeableLossMt));
        var receiptShortageQuantityByLoadingId = receiptShortageLossRows
            .Select(e => new
            {
                LoadingRegisterId = e.LoadingRegisterId
                    ?? (e.LoadingReceiptId.HasValue && receiptLoadingById.TryGetValue(e.LoadingReceiptId.Value, out var receiptLoadingId)
                        ? receiptLoadingId
                        : (int?)null),
                e.DifferenceQuantityMt,
                e.ChargeableLossMt
            })
            .Where(e => e.LoadingRegisterId.HasValue)
            .GroupBy(e => e.LoadingRegisterId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(e => DisplayShortageQuantityMt(e.DifferenceQuantityMt, e.ChargeableLossMt)));

        var bulkReceiptCandidates = BuildBulkReceiptCandidates(
            loadingRegisters,
            loadingItemById,
            receiptQuantityByLoadingId,
            receiptShortageQuantityByLoadingId);

        ViewBag.BulkReceiptTerminals = new SelectList(
            await _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(),
            "Id",
            "Name");
        ViewBag.BulkReceiptStorageTanks = (await StorageTankDisplay.LoadOptionsAsync(
                _db.StorageTanks.AsNoTracking().OrderBy(t => t.DisplayName ?? t.TankCode)))
            .Select(t => new ContractJourneyBulkReceiptStorageTankOptionViewModel
            {
                Id = t.Id,
                TerminalId = t.TerminalId,
                DisplayName = t.Display
            })
            .ToList();

        return new ContractJourneyDetailsViewModel
        {
            ActiveTab = ContractJourneyTabs.Details.Receipts,
            LockContract = lockContract,
            ContractId = baseModel.ContractId,
            ContractName = baseModel.ContractName,
            ContractNumber = baseModel.ContractNumber,
            ContractTypeName = baseModel.ContractTypeName,
            ContractTypeBadgeClass = baseModel.ContractTypeBadgeClass,
            CompanyName = baseModel.CompanyName,
            ProductName = baseModel.ProductName,
            ContractUnitText = baseModel.ContractUnitText,
            SupplierName = baseModel.SupplierName,
            CustomerName = baseModel.CustomerName,
            ContractQuantityMt = baseModel.ContractQuantityMt,
            HasMixedLoadingPrices = baseModel.HasMixedLoadingPrices,
            LoadingsValueUsd = baseModel.LoadingsValueUsd,
            ParentContractId = baseModel.ParentContractId,
            ParentContractNumber = baseModel.ParentContractNumber,
            SubContractItems = baseModel.SubContractItems,
            IsPartnershipContract = baseModel.IsPartnershipContract,
            PartnerShareItems = baseModel.PartnerShareItems,
            Currency = baseModel.Currency,
            PriceDisplay = baseModel.PriceDisplay,
            PricingMethodName = baseModel.PricingMethodName,
            PricingStatusName = baseModel.PricingStatusName,
            RubSettlementSummary = rubSettlementSummary,
            EditPricingUrl = baseModel.EditPricingUrl,
            PricingFormulaText = baseModel.PricingFormulaText,
            PricingFinalUnitPriceUsd = baseModel.PricingFinalUnitPriceUsd,
            PricingNeedsReview = baseModel.PricingNeedsReview,
            PricingReason = baseModel.PricingReason,
            PricingFallbackApplied = baseModel.PricingFallbackApplied,
            PricingFormulaNote = baseModel.PricingFormulaNote,
            StatusName = baseModel.StatusName,
            StatusBadgeClass = baseModel.StatusBadgeClass,
            ContractDate = baseModel.ContractDate,
            StartDate = baseModel.StartDate,
            EndDate = baseModel.EndDate,
            Notes = baseModel.Notes,
            IsPurchaseContract = baseModel.IsPurchaseContract,
            UnreceiptedLoadingCount = loadingRegisters.Count(l => !receiptQuantityByLoadingId.ContainsKey(l.Id)),
            ReceiptDifferenceQuantityMt = loadingReceipts
                .Where(r => r.ActualArrivedQuantityMt.HasValue)
                .Sum(r => r.ActualArrivedQuantityMt!.Value - r.ReceivedQuantityMt),
            ReceiptShortageLossMt = receiptShortageLossMt,
            LoadingDocumentReferences = BuildLoadingDocumentReferences(loadingRegisters),
            LoadingItems = loadingItems,
            ReceiptItems = receiptItems
                .OrderByDescending(r => r.ReceiptDate)
                .ThenByDescending(r => r.Id)
                .ToList(),
            BulkReceiptCandidates = bulkReceiptCandidates
        };
    }

    private async Task<ContractJourneyDetailsViewModel> BuildPurchaseInitialSummaryDetailsAsync(
        Contract contract,
        ContractJourneyDetailsViewModel baseModel,
        decimal? contractFinalPriceUsd,
        bool lockContract)
    {
        var contractId = contract.Id;

        var loadingRegisters = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            .OrderBy(l => l.LoadingDate)
            .ThenBy(l => l.Id)
            .Select(l => new LoadingRegister
            {
                Id = l.Id,
                ContractId = l.ContractId,
                LoadingDate = l.LoadingDate,
                LoadedQuantityMt = l.LoadedQuantityMt,
                BillOfLadingNumber = l.BillOfLadingNumber,
                RwbNo = l.RwbNo,
                WagonNumber = l.WagonNumber,
                RouteDescription = l.RouteDescription,
                ConsigneeName = l.ConsigneeName,
                DestinationName = l.DestinationName,
                TransportType = l.TransportType,
                LoadingPriceUsd = l.LoadingPriceUsd,
                SettlementCurrencyCode = l.SettlementCurrencyCode,
                RubRateStatus = l.RubRateStatus,
                RubPerUsdRate = l.RubPerUsdRate,
                RubRateDate = l.RubRateDate,
                RubRateSource = l.RubRateSource,
                AmountUsdAtRubLock = l.AmountUsdAtRubLock,
                AmountRubAtRubLock = l.AmountRubAtRubLock,
                TransportExpenseUsd = l.TransportExpenseUsd,
                WarehouseExpenseUsd = l.WarehouseExpenseUsd,
                OtherExpenseUsd = l.OtherExpenseUsd,
                RailwayExpenseUsd = l.RailwayExpenseUsd
            })
            .ToListAsync();
        var loadingIds = loadingRegisters.Select(l => l.Id).ToList();
        var loadingById = loadingRegisters.ToDictionary(l => l.Id);
        var loadingIdsWithOfficialExpenses = await LoadLoadingIdsWithOfficialExpensesAsync(loadingIds);
        var loadingIdsWithExpenseLines = await LoadLoadingIdsWithExpenseLinesAsync(loadingIds);
        var mirroredDriverExpenses = await _purchaseAggregation.LoadMirroredDriverExpenseAmountsAsync(loadingIds);
        var purchaseAgg = _purchaseAggregation.AggregateForLoadedRegisters(
            contractId,
            loadingRegisters,
            contractFinalPriceUsd,
            loadingIdsWithOfficialExpenses,
            loadingIdsWithExpenseLines,
            mirroredDriverExpenses);
        var rubSettlementSummary = BuildRubSettlementSummary(contract, loadingRegisters, contractFinalPriceUsd);

        var receiptRows = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => loadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled)
            .Select(r => new
            {
                r.Id,
                r.LoadingRegisterId,
                r.ReceiptDate,
                r.ReceivedQuantityMt,
                r.ActualArrivedQuantityMt,
                TerminalName = r.Terminal != null ? r.Terminal.Name : string.Empty,
                StorageTankCode = r.StorageTank == null
                    ? null
                    : r.StorageTank.DisplayName == null || r.StorageTank.DisplayName == ""
                        ? r.StorageTank.TankCode
                        : r.StorageTank.DisplayName
            })
            .ToListAsync();
        var receiptIds = receiptRows.Select(r => r.Id).ToList();
        var receiptLoadingById = receiptRows.ToDictionary(r => r.Id, r => r.LoadingRegisterId);
        var totalReceivedQuantityMt = receiptRows.Sum(r => r.ReceivedQuantityMt);
        var receiptQuantityByLoadingId = receiptRows
            .GroupBy(r => r.LoadingRegisterId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.ReceivedQuantityMt));
        var unreceiptedLoadingCount = 0;
        var receiptDifferenceQuantityMt = receiptRows
            .Where(r => r.ActualArrivedQuantityMt.HasValue)
            .Sum(r => r.ActualArrivedQuantityMt!.Value - r.ReceivedQuantityMt);

        var inventoryTransportLegRows = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.SourcePurchaseContractId == contractId)
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.ShipmentId,
                l.TransportType,
                l.LoadedDate,
                l.QuantityMt,
                l.Status
            })
            .ToListAsync();
        var inventoryTransportLegIds = inventoryTransportLegRows.Select(l => l.Id).ToList();
        var inventoryTransportReceiptRows = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => inventoryTransportLegIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => new
            {
                r.Id,
                r.InventoryTransportLegId,
                r.ReceiptDate,
                r.ReceivedQuantityMt,
                r.ShortageQuantityMt,
                r.ReceiptDestination,
                r.SalesTransactionId
            })
            .ToListAsync();
        var latestInventoryTransportReceiptByLegId = inventoryTransportReceiptRows
            .GroupBy(r => r.InventoryTransportLegId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Id).First());
        var inventoryTransportQuantityMt = inventoryTransportLegRows.Sum(l => l.QuantityMt);
        var inventoryTransportReceivedMt = latestInventoryTransportReceiptByLegId.Values.Sum(r => r.ReceivedQuantityMt);
        var inventoryTransportShortageMt = latestInventoryTransportReceiptByLegId.Values.Sum(r => r.ShortageQuantityMt);
        var inventoryTransportInTransitMt = inventoryTransportLegRows.Sum(l =>
        {
            if (l.Status == InventoryTransportLegStatus.Received || l.Status == InventoryTransportLegStatus.Cancelled)
            {
                return 0m;
            }

            latestInventoryTransportReceiptByLegId.TryGetValue(l.Id, out var receipt);
            return Math.Max(l.QuantityMt - (receipt?.ReceivedQuantityMt ?? 0m) - (receipt?.ShortageQuantityMt ?? 0m), 0m);
        });

        var directShipmentRows = await _db.Shipments
            .AsNoTracking()
            .Where(s => s.ContractId == contractId)
            .Select(s => new { s.Id, s.QuantityMt })
            .ToListAsync();
        var shipmentContractRows = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ContractId == contractId)
            .Select(sc => new { sc.ShipmentId, QuantityMt = sc.QuantityMt ?? 0m })
            .ToListAsync();
        var shipmentIds = directShipmentRows
            .Select(s => s.Id)
            .Concat(shipmentContractRows.Select(s => s.ShipmentId))
            .Concat(inventoryTransportLegRows.Where(l => l.ShipmentId.HasValue).Select(l => l.ShipmentId!.Value))
            .Distinct()
            .ToList();
        var visibleShipmentQuantityMt = shipmentContractRows.Count > 0
            ? shipmentContractRows.Sum(s => s.QuantityMt)
            : directShipmentRows.Sum(s => s.QuantityMt);

        var inventoryMovementRows = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ContractId == contractId)
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.MovementDate,
                m.Direction,
                m.QuantityMt,
                m.LoadingReceiptId,
                m.SalesTransactionId,
                m.ReferenceDocument,
                TerminalName = m.Terminal != null ? m.Terminal.Name : string.Empty,
                StorageTankCode = m.StorageTank == null
                    ? null
                    : m.StorageTank.DisplayName == null || m.StorageTank.DisplayName == ""
                        ? m.StorageTank.TankCode
                        : m.StorageTank.DisplayName
            })
            .ToListAsync();
        var movementIds = inventoryMovementRows.Select(m => m.Id).ToList();
        var inventorySaleIds = inventoryMovementRows
            .Where(m => m.Direction == MovementDirection.Out && m.SalesTransactionId.HasValue)
            .Select(m => m.SalesTransactionId!.Value)
            .Distinct()
            .ToList();
        decimal runningBalanceMt = 0m;
        var hasNegativeMovementBalance = false;
        foreach (var movement in inventoryMovementRows)
        {
            runningBalanceMt += ToSignedQuantity(movement.Direction, movement.QuantityMt);
            hasNegativeMovementBalance = hasNegativeMovementBalance || runningBalanceMt < 0m;
        }
        var inventoryInQuantityMt = inventoryMovementRows
            .Where(m => m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment)
            .Sum(m => m.QuantityMt);
        var inventoryOutQuantityMt = inventoryMovementRows
            .Where(m => m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer)
            .Sum(m => m.QuantityMt);

        var directDispatchRows = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.ContractId == contractId)
            .Select(d => new
            {
                d.Id,
                d.DispatchMode,
                d.LoadingReceiptAllocationId,
                LoadingReceiptId = d.LoadingReceiptAllocation != null
                    ? (int?)d.LoadingReceiptAllocation.LoadingReceiptId
                    : null,
                d.SalesTransactionId,
                d.DispatchDate,
                d.Status,
                d.LoadedQuantityMt,
                d.DischargedQuantityMt,
                d.AllowanceMt,
                d.ToleranceMt,
                d.ShortageMt,
                d.ChargeableShortageMt,
                d.FreightCostUsd
            })
            .ToListAsync();
        var dispatchIds = directDispatchRows.Select(d => d.Id).ToList();
        var dispatchQuantityMt = directDispatchRows.Sum(d => d.LoadedQuantityMt);
        var dispatchFreightCostUsd = directDispatchRows.Sum(d => d.FreightCostUsd ?? 0m);
        var dispatchReferenceMap = dispatchIds.ToDictionary(id => $"TRUCK-DISPATCH:{id}", id => id);
        var dispatchReferences = dispatchReferenceMap.Keys.ToList();
        var tracedDispatchIds = dispatchIds.Count == 0
            ? new HashSet<int>()
            : (await _db.InventoryMovements
                .AsNoTracking()
                .Where(m => m.ContractId == contractId
                    && m.ReferenceDocument != null
                    && dispatchReferences.Contains(m.ReferenceDocument))
                .Select(m => m.ReferenceDocument!)
                .ToListAsync())
                .Select(reference => dispatchReferenceMap.GetValueOrDefault(reference))
                .Where(id => id > 0)
                .ToHashSet();
        var dispatchWithoutInventoryTraceCount = directDispatchRows.Count(d =>
            d.DispatchMode != TruckDispatchMode.DirectFromReceipt
            && !tracedDispatchIds.Contains(d.Id));

        var inventorySaleRows = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ContractId == contractId
                && m.Direction == MovementDirection.Out
                && m.SalesTransactionId.HasValue)
            .Join(
                _db.SalesTransactions
                    .AsNoTracking()
                    .Where(s => !s.IsCancelled && s.SaleStage != SaleStage.PreSale),
                m => m.SalesTransactionId!.Value,
                s => s.Id,
                (m, s) => new
                {
                    SalesTransactionId = s.Id,
                    CustomerName = s.Customer != null ? s.Customer.Name : string.Empty,
                    SaleDate = s.SaleDate,
                    QuantityMt = m.QuantityMt,
                    AmountUsd = decimal.Round(m.QuantityMt * s.UnitPriceUsd, 4, MidpointRounding.AwayFromZero),
                    HasQuantityMismatch = false
                })
            .ToListAsync();
        var inventorySaleIdSet = inventorySaleRows.Select(s => s.SalesTransactionId).ToHashSet();
        var directSaleRows = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectSale
                && a.SourcePurchaseContractId == contractId
                && a.SalesTransactionId.HasValue
                && a.SalesTransaction != null
                && !a.SalesTransaction.IsCancelled
                && a.SalesTransaction.SaleStage != SaleStage.PreSale
                && !inventorySaleIdSet.Contains(a.SalesTransactionId.Value))
            .Select(a => new
            {
                SalesTransactionId = a.SalesTransactionId!.Value,
                CustomerName = a.SalesTransaction!.Customer != null ? a.SalesTransaction.Customer.Name : string.Empty,
                SaleDate = a.SalesTransaction.SaleDate,
                QuantityMt = a.SalesTransaction.QuantityMt,
                AmountUsd = a.SalesTransaction.TotalUsd,
                HasQuantityMismatch = a.QuantityMt != a.SalesTransaction.QuantityMt
            })
            .ToListAsync();
        var directSaleIdSet = directSaleRows.Select(s => s.SalesTransactionId).ToHashSet();
        var directInventoryTransportSaleIds = inventoryTransportReceiptRows
            .Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale && r.SalesTransactionId.HasValue)
            .Select(r => r.SalesTransactionId!.Value)
            .Distinct()
            .ToList();
        var directInventoryTransportSaleRows = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => directInventoryTransportSaleIds.Contains(s.Id)
                && !s.IsCancelled
                && s.SaleStage != SaleStage.PreSale
                && !inventorySaleIdSet.Contains(s.Id)
                && !directSaleIdSet.Contains(s.Id))
            .Select(s => new
            {
                SalesTransactionId = s.Id,
                CustomerName = s.Customer != null ? s.Customer.Name : string.Empty,
                SaleDate = s.SaleDate,
                QuantityMt = s.QuantityMt,
                AmountUsd = s.TotalUsd,
                HasQuantityMismatch = false
            })
            .ToListAsync();
        var directInventoryTransportSaleIdSet = directInventoryTransportSaleRows.Select(s => s.SalesTransactionId).ToHashSet();
        var directDispatchSaleRows = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt
                && d.ContractId == contractId
                && d.SalesTransactionId.HasValue
                && d.SalesTransaction != null
                && !d.SalesTransaction.IsCancelled
                && d.SalesTransaction.SaleStage != SaleStage.PreSale
                && !inventorySaleIdSet.Contains(d.SalesTransactionId.Value)
                && !directSaleIdSet.Contains(d.SalesTransactionId.Value)
                && !directInventoryTransportSaleIdSet.Contains(d.SalesTransactionId.Value))
            .Select(d => new
            {
                SalesTransactionId = d.SalesTransactionId!.Value,
                CustomerName = d.SalesTransaction!.Customer != null ? d.SalesTransaction.Customer.Name : string.Empty,
                SaleDate = d.SalesTransaction.SaleDate,
                QuantityMt = d.SalesTransaction.QuantityMt,
                AmountUsd = d.SalesTransaction.TotalUsd,
                HasQuantityMismatch = d.LoadedQuantityMt != d.SalesTransaction.QuantityMt
            })
            .ToListAsync();
        var saleRows = inventorySaleRows
            .Concat(directSaleRows)
            .Concat(directInventoryTransportSaleRows)
            .Concat(directDispatchSaleRows)
            .ToList();
        var saleIdSet = saleRows.Select(s => s.SalesTransactionId).ToHashSet();
        var directSaleQuantityMismatchWarning = saleRows.Any(s => s.HasQuantityMismatch)
            ? "DirectSale quantity mismatch: one or more receipt allocations differ from their linked SalesTransaction quantity."
            : null;

        // محموله چند-قراردادی (موجودی): فروش‌هایی که در سطح محموله ثبت شده‌اند و از طریق
        // حمل موجودیِ همین قرارداد سهم می‌گیرند، در saleRows بالا دیده نمی‌شوند (چون فقط با
        // ShipmentId وصل‌اند، نه ContractId/حرکت موجودی). همان سرویسی که پرونده محموله برای
        // سهم هر قرارداد استفاده می‌کند را روی حمل‌های این قرارداد اجرا می‌کنیم و فقط فروش‌های
        // هنوز-نشمرده را (بر پایهٔ SaleId) اضافه می‌کنیم تا دوباره‌شماری نشود. سهم هر قرارداد
        // دقیقاً برابر پرونده محموله می‌شود.
        var transportLegPnl = inventoryTransportLegIds.Count == 0
            ? (IReadOnlyDictionary<int, InventoryTransportPnlSnapshot>)new Dictionary<int, InventoryTransportPnlSnapshot>()
            : await new InventoryTransportPnlService(_db).BuildForLegsAsync(inventoryTransportLegIds);
        var shipmentAllocatedSaleRows = transportLegPnl.Values
            .SelectMany(p => p.Sales)
            .Where(s => !saleIdSet.Contains(s.SaleId))
            .GroupBy(s => s.SaleId)
            .Select(g => new
            {
                QuantityMt = g.Sum(x => x.QuantityMt),
                AmountUsd = g.Sum(x => x.AmountUsd)
            })
            .ToList();
        var shipmentAllocatedSaleQuantityMt = shipmentAllocatedSaleRows.Sum(s => s.QuantityMt);
        var shipmentAllocatedSaleTotalUsd = shipmentAllocatedSaleRows.Sum(s => s.AmountUsd);

        // مصرف و کسورادِ سطح‌محموله که «بدون تگ» ثبت شده‌اند (ShipmentId دارند ولی نه
        // TransportLegId و نه ContractId) بین قراردادهای همان محموله تقسیم می‌شوند. این‌ها
        // هرگز در expenseRows/lossRows پایین نمی‌آیند (آن‌ها ContractId/leg می‌خواهند) پس
        // افزودن سهمِ این قرارداد هیچ همپوشانی/دوباره‌شماری ندارد؛ مصرف/کسورادِ تگ‌دارِ همین
        // قرارداد جای دیگر شمرده شده. کلید = مجموعهٔ کاملِ محموله‌های این قرارداد (shipmentIds
        // که ShipmentContracts و حمل‌ها را هم شامل است) تا قراردادِ بدون حمل هم دیده شود.
        // وزنِ هر قرارداد = مقدارِ ثبت‌شدهٔ همان قرارداد در پرونده محموله (ShipmentContracts.QuantityMt)
        // و در نبودِ آن، مقدارِ حملِ همان قرارداد.
        var thisContractLegQtyByShipment = inventoryTransportLegRows
            .Where(l => l.ShipmentId.HasValue && l.QuantityMt > 0m)
            .GroupBy(l => l.ShipmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityMt));
        var thisContractAllocByShipment = shipmentContractRows
            .GroupBy(s => s.ShipmentId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.QuantityMt));
        var allContractAllocByShipment = shipmentIds.Count == 0
            ? new Dictionary<int, decimal>()
            : (await _db.ShipmentContracts
                .AsNoTracking()
                .Where(sc => shipmentIds.Contains(sc.ShipmentId) && sc.QuantityMt.HasValue && sc.QuantityMt.Value > 0m)
                .Select(sc => new { sc.ShipmentId, QuantityMt = sc.QuantityMt!.Value })
                .ToListAsync())
                .GroupBy(x => x.ShipmentId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));
        var allLegsQtyByShipment = shipmentIds.Count == 0
            ? new Dictionary<int, decimal>()
            : (await _db.InventoryTransportLegs
                .AsNoTracking()
                .Where(l => l.ShipmentId.HasValue && shipmentIds.Contains(l.ShipmentId.Value) && l.QuantityMt > 0m)
                .Select(l => new { ShipmentId = l.ShipmentId!.Value, l.QuantityMt })
                .ToListAsync())
                .GroupBy(x => x.ShipmentId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));
        var untaggedShipmentExpenseByShipment = shipmentIds.Count == 0
            ? new Dictionary<int, decimal>()
            : (await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => !e.IsCancelled
                    && e.ShipmentId.HasValue && shipmentIds.Contains(e.ShipmentId.Value)
                    && !e.TransportLegId.HasValue
                    && !e.ContractId.HasValue)
                .Select(e => new { ShipmentId = e.ShipmentId!.Value, e.AmountUsd })
                .ToListAsync())
                .GroupBy(x => x.ShipmentId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.AmountUsd));
        var untaggedShipmentLossByShipment = shipmentIds.Count == 0
            ? new Dictionary<int, decimal>()
            : (await _db.LossEvents
                .AsNoTracking()
                .Where(e => !e.IsCancelled
                    && e.ShipmentId.HasValue && shipmentIds.Contains(e.ShipmentId.Value)
                    && !e.TransportLegId.HasValue
                    && !e.LoadingRegisterId.HasValue
                    && !e.LoadingReceiptId.HasValue
                    && !e.TruckDispatchId.HasValue
                    && !e.SalesTransactionId.HasValue
                    && !e.ContractId.HasValue
                    && e.DifferenceQuantityMt > 0m)
                .Select(e => new { ShipmentId = e.ShipmentId!.Value, e.DifferenceQuantityMt })
                .ToListAsync())
                .GroupBy(x => x.ShipmentId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.DifferenceQuantityMt));
        var directShipmentIdSet = directShipmentRows.Select(s => s.Id).ToHashSet();
        decimal shipmentSharedExpenseUsd = 0m;
        decimal shipmentSharedLossMt = 0m;
        foreach (var sid in shipmentIds)
        {
            // وزنِ این قرارداد: مقدار ثبت‌شده در محموله، سپس مقدار حمل.
            var thisWeight = thisContractAllocByShipment.GetValueOrDefault(sid);
            if (thisWeight <= 0m)
            {
                thisWeight = thisContractLegQtyByShipment.GetValueOrDefault(sid);
            }

            // وزنِ کلِ محموله: مجموع مقادیر ثبت‌شدهٔ همهٔ قراردادها، سپس مجموع حمل‌ها.
            var allWeight = allContractAllocByShipment.GetValueOrDefault(sid);
            if (allWeight <= 0m)
            {
                allWeight = allLegsQtyByShipment.GetValueOrDefault(sid);
            }
            // محمولهٔ تک‌قراردادیِ قدیمی (فقط Shipment.ContractId): این قرارداد کلِ محموله است.
            if (allWeight <= 0m && directShipmentIdSet.Contains(sid))
            {
                thisWeight = 1m;
                allWeight = 1m;
            }
            if (allWeight <= 0m || thisWeight <= 0m)
            {
                continue;
            }

            var ratio = Math.Min(thisWeight / allWeight, 1m);
            shipmentSharedExpenseUsd += untaggedShipmentExpenseByShipment.GetValueOrDefault(sid) * ratio;
            shipmentSharedLossMt += untaggedShipmentLossByShipment.GetValueOrDefault(sid) * ratio;
        }
        shipmentSharedExpenseUsd = decimal.Round(shipmentSharedExpenseUsd, 4, MidpointRounding.AwayFromZero);
        shipmentSharedLossMt = decimal.Round(shipmentSharedLossMt, 4, MidpointRounding.AwayFromZero);

        var preSaleRows = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.ContractId == contractId && s.SaleStage == SaleStage.PreSale)
            .Select(s => new
            {
                s.Id,
                CustomerName = s.Customer != null ? s.Customer.Name : string.Empty,
                s.QuantityMt
            })
            .ToListAsync();

        var expenseRows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ContractId == contractId
                || (e.TruckDispatchId.HasValue && dispatchIds.Contains(e.TruckDispatchId.Value))
                || (e.TransportLegId.HasValue && inventoryTransportLegIds.Contains(e.TransportLegId.Value))
                || (e.LoadingRegisterId.HasValue && loadingIds.Contains(e.LoadingRegisterId.Value)))
            .Select(e => new
            {
                e.Id,
                e.AmountUsd,
                e.LoadingRegisterId,
                e.TransportLegId,
                e.IsCancelled,
                e.CustomsDeclarationId,
                ExpenseTypeCode = e.ExpenseType != null ? e.ExpenseType.Code : null,
                ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.Name : null,
                ExpenseTypeNamePersian = e.ExpenseType != null ? e.ExpenseType.NamePersian : null,
                e.Description
            })
            .ToListAsync();
        var expenseIdSet = expenseRows.Select(e => e.Id).ToHashSet();
        // مصرفِ لغوشده پول نیست و در هیچ جمعی نمی‌آید.
        var totalExpensesUsd = expenseRows.Where(e => !e.IsCancelled).Sum(e => e.AmountUsd) + shipmentSharedExpenseUsd;
        var inventoryTransportExpenseTotalUsd = expenseRows
            .Where(e => !e.IsCancelled
                && e.TransportLegId.HasValue
                && inventoryTransportLegIds.Contains(e.TransportLegId.Value))
            .Sum(e => e.AmountUsd);

        static string ExpenseText(string? code, string? name, string? namePersian, string? description)
            => string.Join(' ', code, name, namePersian, description);

        static bool ContainsAnyTerm(string text, params string[] terms)
            => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

        static bool IsStorageRentExpense(string text)
            => ContainsAnyTerm(text,
                "storage",
                "warehouse",
                "tank rent",
                "مخزن",
                "مخازن",
                "ذخیره",
                "انبار");

        static bool IsTransportFreightExpense(string text)
            => ContainsAnyTerm(text,
                "freight",
                "transport",
                "truck",
                "vessel freight",
                "shipping",
                "demurrage",
                "حمل",
                "ترانسپورت",
                "کرایه موتر",
                "کرایه کشتی",
                "دیمیرج");

        var expenseStorageRentUsd = expenseRows
            .Where(e => !e.IsCancelled
                && IsStorageRentExpense(ExpenseText(e.ExpenseTypeCode, e.ExpenseTypeName, e.ExpenseTypeNamePersian, e.Description)))
            .Sum(e => e.AmountUsd);
        var expenseTransportFreightUsd = expenseRows
            .Where(e => !e.IsCancelled
                && (ExpenseClassification.IsWagonRent(e.ExpenseTypeCode, e.ExpenseTypeName, e.ExpenseTypeNamePersian, e.Description)
                    || IsTransportFreightExpense(ExpenseText(e.ExpenseTypeCode, e.ExpenseTypeName, e.ExpenseTypeNamePersian, e.Description))))
            .Sum(e => e.AmountUsd);
        var hasOfficialWagonRentExpense = expenseRows.Any(e =>
            !e.IsCancelled && ExpenseClassification.IsWagonRent(e.ExpenseTypeCode, e.ExpenseTypeName, e.ExpenseTypeNamePersian, e.Description));

        var customsRows = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => (c.LoadingRegisterId.HasValue && loadingIds.Contains(c.LoadingRegisterId.Value))
                || (c.TransportLegId.HasValue && inventoryTransportLegIds.Contains(c.TransportLegId.Value)))
            .Select(c => new { c.Id, c.TotalUsd })
            .ToListAsync();
        var customsDeclarations = customsRows;

        var lossRows = await _db.LossEvents
            .AsNoTracking()
            .Where(e => e.ContractId == contractId
                || (e.TransportLegId.HasValue && inventoryTransportLegIds.Contains(e.TransportLegId.Value))
                || (e.TruckDispatchId.HasValue && dispatchIds.Contains(e.TruckDispatchId.Value))
                || (e.LoadingRegisterId.HasValue && loadingIds.Contains(e.LoadingRegisterId.Value))
                || (e.LoadingReceiptId.HasValue && receiptIds.Contains(e.LoadingReceiptId.Value))
                || (e.InventoryMovementId.HasValue && movementIds.Contains(e.InventoryMovementId.Value)))
            .Select(e => new
            {
                e.Id,
                e.Stage,
                e.EventDate,
                e.LoadingRegisterId,
                e.LoadingReceiptId,
                e.TransportLegId,
                e.TruckDispatchId,
                e.InventoryMovementId,
                e.DifferenceQuantityMt,
                e.ChargeableLossMt,
                e.IsCancelled
            })
            .ToListAsync();
        var activeLosses = lossRows
            .Where(e => !e.IsCancelled)
            .ToList();
        static decimal DisplayLossQuantity(decimal differenceQuantityMt, decimal chargeableLossMt)
            => differenceQuantityMt > 0m ? differenceQuantityMt : Math.Max(chargeableLossMt, 0m);

        var loadingDifferenceLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.LoadingDifference)
            .Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt));
        var receiptShortageLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.ReceiptShortage)
            .Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt));
        var receiptShortageQuantityByLoadingId = activeLosses
            .Where(e => e.Stage == LossEventStage.ReceiptShortage)
            .Select(e => new
            {
                LoadingRegisterId = e.LoadingRegisterId
                    ?? (e.LoadingReceiptId.HasValue && receiptLoadingById.TryGetValue(e.LoadingReceiptId.Value, out var receiptLoadingId)
                        ? receiptLoadingId
                        : (int?)null),
                e.DifferenceQuantityMt,
                e.ChargeableLossMt
            })
            .Where(e => e.LoadingRegisterId.HasValue)
            .GroupBy(e => e.LoadingRegisterId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt)));
        unreceiptedLoadingCount = loadingRegisters.Count(l => !receiptQuantityByLoadingId.ContainsKey(l.Id));
        var recordedDispatchLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.DispatchShortage)
            .Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt));
        var inventoryTransportLossMt = activeLosses
            .Where(e => e.TransportLegId.HasValue || e.Stage == LossEventStage.TransitLoss)
            .Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt));
        // مثل تب اصلی: ضایعات مخزن هم کسری طبیعی است و هم تسویه نهایی مخزن.
        var tankLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.TankNaturalLoss
                || e.Stage == LossEventStage.TankFinalSettlement)
            .Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt));
        var salesLossMt = activeLosses
            .Where(e => e.Stage == LossEventStage.SalesDifference)
            .Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt));
        var recordedDispatchLossIds = activeLosses
            .Where(e => e.Stage == LossEventStage.DispatchShortage && e.TruckDispatchId.HasValue)
            .Select(e => e.TruckDispatchId!.Value)
            .ToHashSet();
        decimal DisplayDispatchShortage(
            DispatchStatus status,
            decimal loadedQuantityMt,
            decimal? dischargedQuantityMt,
            decimal? shortageMt)
        {
            if (status == DispatchStatus.Cancelled)
            {
                return 0m;
            }

            if (shortageMt.HasValue)
            {
                return Math.Max(shortageMt.Value, 0m);
            }

            return dischargedQuantityMt.HasValue
                ? Math.Max(loadedQuantityMt - dischargedQuantityMt.Value, 0m)
                : 0m;
        }

        var derivedDispatchLossMt = directDispatchRows
            .Where(d => !recordedDispatchLossIds.Contains(d.Id))
            .Sum(d => DisplayDispatchShortage(d.Status, d.LoadedQuantityMt, d.DischargedQuantityMt, d.ShortageMt));
        var dispatchShortageLossMt = recordedDispatchLossMt + derivedDispatchLossMt;
        // کسریِ حملِ موجودیِ این قرارداد (ShortageQuantityMt رسیدِ حمل) در پرونده محموله همان
        // «کسری هر قرارداد» است، ولی LossEvent نیست پس در activeLosses بالا دیده نمی‌شود و کارت
        // ضایعات ۰ می‌ماند. اینجا اضافه‌اش می‌کنیم؛ برای جلوگیری از دوباره‌شماری، ضرر ثبت‌شدهٔ
        // leg-دار را کم می‌کنیم (همان منطق MAX پرونده محموله بین کسری رسید و ضرر ثبت‌شده).
        var legRecordedLossMt = activeLosses
            .Where(e => e.TransportLegId.HasValue && inventoryTransportLegIds.Contains(e.TransportLegId.Value))
            .Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt));
        var transportShortageLossMt = Math.Max(inventoryTransportShortageMt - legRecordedLossMt, 0m);
        var totalLossQuantityMt = activeLosses.Sum(e => DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt))
            + derivedDispatchLossMt
            + shipmentSharedLossMt
            + transportShortageLossMt;

        var paymentRows = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.ContractId == contractId
                || (p.SalesTransactionId.HasValue && saleIdSet.Contains(p.SalesTransactionId.Value))
                || (p.ExpenseTransactionId.HasValue && expenseIdSet.Contains(p.ExpenseTransactionId.Value))
                || (p.ShipmentId.HasValue && shipmentIds.Contains(p.ShipmentId.Value))
                || (p.TruckDispatchId.HasValue && dispatchIds.Contains(p.TruckDispatchId.Value)))
            .Select(p => new
            {
                p.Id,
                p.Direction,
                p.PaymentKind,
                p.AmountUsd
            })
            .ToListAsync();
        var paymentIds = paymentRows.Select(p => p.Id).ToHashSet();
        var paymentTotalUsd = paymentRows.Sum(p => p.AmountUsd);
        var paymentInTotalUsd = paymentRows.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd);
        var paymentOutTotalUsd = paymentRows.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd);
        // payload خلاصه فهرست PaymentItems/SarrafSettlementItems را پر نمی‌کند؛ رقم «پرداخت‌شده»
        // کارت چرخه قرارداد باید همین‌جا محاسبه شود، هم‌فرمول با تب پرداخت‌ها.
        var supplierSarrafSettledUsd = (await _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.ContractId == contractId && s.Status == SarrafSettlementStatus.Posted)
            .Select(s => new
            {
                s.DifferenceTreatment,
                s.RequestedAmountUsd,
                s.SupplierAcceptedAmountUsd
            })
            .ToListAsync())
            .Sum(s => s.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
                ? s.RequestedAmountUsd
                : s.SupplierAcceptedAmountUsd);
        var supplierPaidNetUsd = paymentRows
                .Where(p => p.PaymentKind == PaymentKind.SupplierPayment && p.Direction == PaymentDirection.Out)
                .Sum(p => p.AmountUsd)
            - paymentRows
                .Where(p => p.PaymentKind == PaymentKind.SupplierReceipt && p.Direction == PaymentDirection.In)
                .Sum(p => p.AmountUsd)
            + supplierSarrafSettledUsd;

        var ledgerRows = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId == contractId
                || (l.SourceType == "Sale" && saleIdSet.Contains(l.SourceId))
                || (l.SourceType == "Expense" && expenseIdSet.Contains(l.SourceId))
                || (PaymentLedgerSourceTypes.Contains(l.SourceType) && paymentIds.Contains(l.SourceId))
                || (l.ShipmentId.HasValue && shipmentIds.Contains(l.ShipmentId.Value)))
            .Select(l => new
            {
                l.Id,
                l.Side,
                l.AmountUsd,
                l.SourceType,
                l.Reference
            })
            .ToListAsync();
        var ledgers = ledgerRows;
        var debitTotalUsd = ledgers.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd);
        var creditTotalUsd = ledgers.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd);
        var relatedBalanceUsd = await ReadContractBalanceUsdAsync(contractId);

        var stockSummary = await _stock.GetStockSummaryAsync(contractId: contractId);
        var currentStockQuantityMt = stockSummary.Sum(s => s.FreeQuantityMt);
        var hasNegativeStockWarning = currentStockQuantityMt < 0m || hasNegativeMovementBalance;

        var saleQuantityMt = saleRows.Sum(s => s.QuantityMt) + shipmentAllocatedSaleQuantityMt;
        var saleTotalUsd = saleRows.Sum(s => s.AmountUsd) + shipmentAllocatedSaleTotalUsd;
        var preSaleQuantityMt = preSaleRows.Sum(s => s.QuantityMt);
        var loadingOperationalExpenseUsd =
            purchaseAgg.LoadingTransportExpenseUsd +
            purchaseAgg.LoadingWarehouseExpenseUsd +
            purchaseAgg.LoadingOtherExpenseUsd +
            (hasOfficialWagonRentExpense ? 0m : purchaseAgg.LoadingRailwayExpenseUsd);
        var contractTransportExpenseUsd = purchaseAgg.LoadingTransportExpenseUsd + expenseTransportFreightUsd;
        var contractStorageRentExpenseUsd = purchaseAgg.LoadingWarehouseExpenseUsd + expenseStorageRentUsd;

        var preSaleSectionState = preSaleRows.Any()
            ? new ContractJourneySectionStateViewModel()
            : new ContractJourneySectionStateViewModel
            {
                BadgeText = "نیاز به بررسی",
                BadgeClass = "needs-review",
                Message = "اتصال قطعی پیش‌فروش به این قرارداد نیاز به بررسی دارد.",
                IsNeedsReview = true
            };
        var dispatchSectionState = directDispatchRows.Any()
            ? new ContractJourneySectionStateViewModel()
            : new ContractJourneySectionStateViewModel
            {
                BadgeText = "نیاز به بررسی",
                BadgeClass = "needs-review",
                Message = "اتصال قطعی دیسپچ به این قرارداد نیاز به بررسی دارد.",
                IsNeedsReview = true
            };
        var notesForReview = new List<string>();
        if (preSaleSectionState.IsNeedsReview && preSaleSectionState.Message is not null)
        {
            notesForReview.Add(preSaleSectionState.Message);
        }
        if (dispatchSectionState.IsNeedsReview && dispatchSectionState.Message is not null)
        {
            notesForReview.Add(dispatchSectionState.Message);
        }

        var kpis = new ContractJourneyKpiSummaryViewModel
        {
            ContractQuantityMt = contract.QuantityMt,
            LoadedQuantityMt = purchaseAgg.TotalLoadedQuantityMt,
            ReceivedQuantityMt = totalReceivedQuantityMt,
            DispatchedQuantityMt = dispatchQuantityMt,
            SoldQuantityMt = saleQuantityMt,
            PreSaleQuantityMt = preSaleRows.Any() ? preSaleQuantityMt : null,
            CurrentStockQuantityMt = currentStockQuantityMt,
            LossQuantityMt = totalLossQuantityMt,
            TotalExpensesUsd = totalExpensesUsd,
            TotalPaymentsUsd = paymentTotalUsd,
            RelatedBalanceUsd = relatedBalanceUsd,
            PreSaleNeedsReview = preSaleSectionState.IsNeedsReview,
            PreSaleNote = preSaleSectionState.Message
        };

        var miniPnl = await BuildMiniPnlAsync(
            contract.Id,
            realised: null,
            "سود محقق فقط بر پایهٔ فروشِ انجام‌شده است (بهای کالای فروخته‌شده و سهمِ فروخته‌شدهٔ هزینه‌ها).");
        var nextAction = BuildPurchaseNextAction(
            contract.Id,
            loadingRegisters,
            purchaseAgg.TotalLoadedQuantityMt,
            totalReceivedQuantityMt,
            currentStockQuantityMt,
            directDispatchRows.Count(),
            saleRows.Count() + preSaleRows.Count(),
            expenseRows.Count(),
            paymentRows.Count());
        var warnings = BuildWarnings(
            contract,
            baseModel.PricingNeedsReview,
            baseModel.PricingFallbackApplied,
            purchaseAgg.TotalLoadedQuantityMt,
            totalReceivedQuantityMt,
            loadingRegisters.Count,
            receiptRows.Count(),
            directDispatchRows.Count(),
            saleRows.Count() + preSaleRows.Count(),
            expenseRows.Count(),
            paymentRows.Count(),
            isSaleContract: false,
            unreceiptedLoadingCount,
            hasNegativeStockWarning,
            salesWithoutTraceCount: 0).ToList();
        if (purchaseAgg.PendingPurchaseQuantityMt > 0m)
        {
            warnings.Add("برخی بارگیری‌ها نرخ ندارند؛ P&L نهایی نیست.");
        }
        if (directSaleQuantityMismatchWarning is not null)
        {
            warnings.Add(directSaleQuantityMismatchWarning);
        }

        var storageOverviewItems = receiptRows
            .GroupBy(item => new { item.TerminalName, item.StorageTankCode })
            .Select(group => new ContractJourneyStorageOverviewItemViewModel
            {
                Label = string.IsNullOrWhiteSpace(group.Key.StorageTankCode)
                    ? group.Key.TerminalName
                    : $"{group.Key.StorageTankCode} - {group.Key.TerminalName}",
                QuantityMt = group.Sum(item => item.ReceivedQuantityMt)
            })
            .OrderByDescending(item => item.QuantityMt)
            .Take(3)
            .ToList();
        if (!storageOverviewItems.Any())
        {
            storageOverviewItems = new List<ContractJourneyStorageOverviewItemViewModel>
            {
                new()
                {
                    Label = "موجودی فعلی",
                    QuantityMt = currentStockQuantityMt
                }
            };
        }

        var transportOverviewItems = loadingRegisters
            .GroupBy(item => ToLoadingTransportTypeName(item.TransportType))
            .Select(group => new ContractJourneyTransportOverviewItemViewModel
            {
                Label = group.Key,
                QuantityMt = group.Sum(item => item.LoadedQuantityMt),
                Note = $"{group.Count():N0} رکورد"
            })
            .OrderByDescending(item => item.QuantityMt)
            .Take(3)
            .ToList();
        if (!transportOverviewItems.Any())
        {
            transportOverviewItems = inventoryTransportLegRows
                .GroupBy(item => ToLoadingTransportTypeName(item.TransportType))
                .Select(group => new ContractJourneyTransportOverviewItemViewModel
                {
                    Label = group.Key,
                    QuantityMt = group.Sum(item => item.QuantityMt),
                    Note = $"{group.Count():N0} سند"
                })
                .OrderByDescending(item => item.QuantityMt)
                .Take(3)
                .ToList();
        }
        if (!transportOverviewItems.Any())
        {
            transportOverviewItems.Add(new ContractJourneyTransportOverviewItemViewModel
            {
                Label = "حمل ثبت‌شده",
                QuantityMt = dispatchQuantityMt + inventoryTransportQuantityMt,
                Note = $"{directDispatchRows.Count + inventoryTransportLegRows.Count:N0} عملیات"
            });
        }

        var salesOverviewItems = saleRows
            .GroupBy(item => string.IsNullOrWhiteSpace(item.CustomerName) ? "-" : item.CustomerName)
            .Select(group => new ContractJourneySalesOverviewItemViewModel
            {
                Label = group.Key,
                QuantityMt = group.Sum(item => item.QuantityMt),
                AmountUsd = group.Sum(item => item.AmountUsd)
            })
            .OrderByDescending(item => item.AmountUsd)
            .Take(3)
            .ToList();
        if (!salesOverviewItems.Any() && preSaleRows.Any())
        {
            salesOverviewItems = preSaleRows
                .GroupBy(item => string.IsNullOrWhiteSpace(item.CustomerName) ? "-" : item.CustomerName)
                .Select(group => new ContractJourneySalesOverviewItemViewModel
                {
                    Label = group.Key,
                    QuantityMt = group.Sum(item => item.QuantityMt),
                    AmountUsd = 0m
                })
                .OrderByDescending(item => item.QuantityMt)
                .Take(3)
                .ToList();
        }
        var activityItems = loadingRegisters.Select(l => new ContractJourneyActivityItemViewModel
            {
                Date = l.LoadingDate,
                Title = "Loading",
                Description = $"{l.LoadedQuantityMt:N4} MT" + (string.IsNullOrWhiteSpace(l.BillOfLadingNumber) ? string.Empty : $" | {l.BillOfLadingNumber}"),
                Icon = "bi bi-train-freight-front",
                ToneClass = "journey-activity-info"
            })
            .Concat(receiptRows.Select(r => new ContractJourneyActivityItemViewModel
            {
                Date = r.ReceiptDate,
                Title = "Receipt",
                Description = $"{r.ReceivedQuantityMt:N4} MT | {r.TerminalName}",
                Icon = "bi bi-box-arrow-in-down",
                ToneClass = "journey-activity-success"
            }))
            .Concat(inventoryTransportLegRows.Select(l => new ContractJourneyActivityItemViewModel
            {
                Date = l.LoadedDate,
                Title = "Inventory Transport",
                Description = $"{l.QuantityMt:N4} MT | {l.Status}",
                Icon = "bi bi-truck-front",
                ToneClass = "journey-activity-info"
            }))
            .Concat(directDispatchRows.Select(d => new ContractJourneyActivityItemViewModel
            {
                Date = d.DispatchDate,
                Title = "Dispatch",
                Description = $"{d.LoadedQuantityMt:N4} MT | {d.Status}",
                Icon = "bi bi-truck",
                ToneClass = "journey-activity-info"
            }))
            .Concat(saleRows.Select(s => new ContractJourneyActivityItemViewModel
            {
                Date = s.SaleDate,
                Title = "Sale",
                Description = $"{s.QuantityMt:N4} MT | {s.AmountUsd:N2} USD",
                Icon = "bi bi-receipt",
                ToneClass = "journey-activity-success"
            }))
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.Title)
            .Take(12)
            .ToList();

        var summaryMetrics = new ContractJourneySummaryMetricsViewModel
        {
            HasValues = true,
            LoadingCount = loadingRegisters.Count(),
            ReceiptCount = receiptRows.Count(),
            InventoryMovementCount = inventoryMovementRows.Count(),
            InventoryTransportLegCount = inventoryTransportLegRows.Count(),
            DispatchCount = directDispatchRows.Count(),
            SaleCount = saleRows.Count(),
            PreSaleCount = preSaleRows.Count(),
            ExpenseCount = expenseRows.Count(),
            LossCount = activeLosses.Count + directDispatchRows.Count(d => !recordedDispatchLossIds.Contains(d.Id)
                && DisplayDispatchShortage(d.Status, d.LoadedQuantityMt, d.DischargedQuantityMt, d.ShortageMt) > 0m),
            PaymentCount = paymentRows.Count(),
            LedgerCount = ledgers.Count(),
            LoadingQuantityMt = purchaseAgg.TotalLoadedQuantityMt,
            LoadingValueUsd = purchaseAgg.TraceablePurchaseCostUsd,
            ReceiptQuantityMt = totalReceivedQuantityMt,
            ReceiptTankCount = receiptRows
                .Select(item => $"{item.TerminalName}|{item.StorageTankCode}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            InventoryTransportQuantityMt = inventoryTransportQuantityMt,
            InventoryTransportReceivedMt = inventoryTransportReceivedMt,
            InventoryTransportShortageMt = inventoryTransportShortageMt,
            InventoryTransportInTransitMt = inventoryTransportInTransitMt,
            DispatchQuantityMt = dispatchQuantityMt,
            SaleQuantityMt = saleQuantityMt,
            PreSaleQuantityMt = preSaleQuantityMt,
            SaleTotalUsd = saleTotalUsd,
            ExpenseTotalUsd = totalExpensesUsd,
            InventoryTransportExpenseTotalUsd = inventoryTransportExpenseTotalUsd,
            PaymentTotalUsd = paymentTotalUsd,
            SupplierPaidNetUsd = supplierPaidNetUsd,
            LossQuantityMt = totalLossQuantityMt,
            ChargeableLossQuantityMt = activeLosses.Sum(e =>
                Math.Min(Math.Max(e.ChargeableLossMt, 0m), DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt))),
            AllowedLossQuantityMt = activeLosses.Sum(e =>
                Math.Max(DisplayLossQuantity(e.DifferenceQuantityMt, e.ChargeableLossMt) - Math.Max(e.ChargeableLossMt, 0m), 0m)),
            StorageOverviewItems = storageOverviewItems,
            TransportOverviewItems = transportOverviewItems,
            SalesOverviewItems = salesOverviewItems
        };

        var pendingTankSettlementQuantityMt = await GetPendingTankSettlementQuantityMtAsync(contract.Id);
        // بارِ هنوز در بارگیری: همان قاعدهٔ «بار در مسیر از مبدأ» (بی‌محدودیتِ تاریخ).
        var originRemainingQuantityMt = await new GoodsInTransitQuantityReader(_db)
            .ReadOriginRemainingForContractAsync(contract.Id, DateTime.UtcNow.Date.AddYears(100));
        // کرایهٔ رسیدهای حمل: ناخالص، کسرِ کسری از راننده (جبران؛ یک بار) و خالصِ قابل پرداخت.
        var transportFreightRows = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled && inventoryTransportLegIds.Contains(r.InventoryTransportLegId))
            .Select(r => new
            {
                GrossUsd = r.FreightCostUsd ?? 0m,
                DeductionUsd = r.ShortageChargeUsd ?? 0m,
                PayableUsd = r.FreightPayableUsd ?? 0m,
                DeductedMt = (r.ShortageChargeUsd ?? 0m) > 0m ? r.ChargeableShortageMt ?? 0m : 0m
            })
            .ToListAsync();

        return new ContractJourneyDetailsViewModel
        {
            ActiveTab = ContractJourneyTabs.Details.Summary,
            LockContract = lockContract,
            IsInitialSummaryPayload = true,
            ContractId = baseModel.ContractId,
            ContractName = baseModel.ContractName,
            ContractNumber = baseModel.ContractNumber,
            ContractTypeName = baseModel.ContractTypeName,
            ContractTypeBadgeClass = baseModel.ContractTypeBadgeClass,
            CompanyName = baseModel.CompanyName,
            ProductName = baseModel.ProductName,
            ContractUnitText = baseModel.ContractUnitText,
            SupplierName = baseModel.SupplierName,
            CustomerName = baseModel.CustomerName,
            ContractQuantityMt = baseModel.ContractQuantityMt,
            HasMixedLoadingPrices = baseModel.HasMixedLoadingPrices,
            LoadingsValueUsd = baseModel.LoadingsValueUsd,
            ParentContractId = baseModel.ParentContractId,
            ParentContractNumber = baseModel.ParentContractNumber,
            SubContractItems = baseModel.SubContractItems,
            IsPartnershipContract = baseModel.IsPartnershipContract,
            PartnerShareItems = baseModel.PartnerShareItems,
            Currency = baseModel.Currency,
            PriceDisplay = baseModel.PriceDisplay,
            PricingMethodName = baseModel.PricingMethodName,
            PricingStatusName = baseModel.PricingStatusName,
            RubSettlementSummary = rubSettlementSummary,
            EditPricingUrl = baseModel.EditPricingUrl,
            PricingFormulaText = baseModel.PricingFormulaText,
            PricingFinalUnitPriceUsd = baseModel.PricingFinalUnitPriceUsd,
            PricingNeedsReview = baseModel.PricingNeedsReview,
            PricingReason = baseModel.PricingReason,
            PricingFallbackApplied = baseModel.PricingFallbackApplied,
            PricingFormulaNote = baseModel.PricingFormulaNote,
            StatusName = baseModel.StatusName,
            StatusBadgeClass = baseModel.StatusBadgeClass,
            ContractDate = baseModel.ContractDate,
            StartDate = baseModel.StartDate,
            EndDate = baseModel.EndDate,
            Notes = baseModel.Notes,
            IsPurchaseContract = baseModel.IsPurchaseContract,
            ShipmentCount = shipmentIds.Count,
            ShipmentQuantityMt = visibleShipmentQuantityMt,
            LoadingDocumentReferences = BuildLoadingDocumentReferences(loadingRegisters),
            UnreceiptedLoadingCount = unreceiptedLoadingCount,
            ReceiptDifferenceQuantityMt = receiptDifferenceQuantityMt,
            LoadingDifferenceLossMt = loadingDifferenceLossMt,
            ReceiptShortageLossMt = receiptShortageLossMt,
            DispatchShortageLossMt = dispatchShortageLossMt,
            InventoryTransportLossMt = inventoryTransportLossMt,
            TankLossMt = tankLossMt,
            SalesLossMt = salesLossMt,
            PendingTankSettlementQuantityMt = pendingTankSettlementQuantityMt,
            OriginRemainingQuantityMt = originRemainingQuantityMt,
            TransportFreightGrossUsd = transportFreightRows.Sum(r => r.GrossUsd),
            TransportShortageDeductionUsd = transportFreightRows.Sum(r => r.DeductionUsd),
            TransportFreightPayableUsd = transportFreightRows.Sum(r => r.PayableUsd),
            TransportDeductedShortageMt = transportFreightRows.Sum(r => r.DeductedMt),
            InventoryInQuantityMt = inventoryInQuantityMt,
            InventoryOutQuantityMt = inventoryOutQuantityMt,
            HasNegativeStockWarning = hasNegativeStockWarning,
            SalesFromInventoryCount = inventorySaleRows.Count(),
            SalesWithoutTraceCount = 0,
            DispatchFreightCostUsd = dispatchFreightCostUsd,
            DispatchWithoutInventoryTraceCount = dispatchWithoutInventoryTraceCount,
            LoadingOperationalExpenseUsd = loadingOperationalExpenseUsd,
            LoadingRailwayExpenseUsd = hasOfficialWagonRentExpense ? 0m : purchaseAgg.LoadingRailwayExpenseUsd,
            LoadingWarehouseExpenseUsd = purchaseAgg.LoadingWarehouseExpenseUsd,
            ContractTransportExpenseUsd = contractTransportExpenseUsd,
            ContractStorageRentExpenseUsd = contractStorageRentExpenseUsd,
            PendingLoadingPriceCount = purchaseAgg.PendingLoadingCount,
            PendingLoadingPriceQuantityMt = purchaseAgg.PendingPurchaseQuantityMt,
            CustomsDeclarationCount = customsDeclarations.Count,
            CustomsDeclarationTotalUsd = customsDeclarations.Sum(c => c.TotalUsd),
            PaymentInTotalUsd = paymentInTotalUsd,
            PaymentOutTotalUsd = paymentOutTotalUsd,
            Kpis = kpis,
            KpiCards = BuildKpiCards(kpis),
            TimelineSteps = BuildTimelineSteps(
                contract.QuantityMt,
                purchaseAgg.TotalLoadedQuantityMt,
                totalReceivedQuantityMt,
                directDispatchRows.Count(),
                dispatchQuantityMt,
                shipmentIds.Count,
                visibleShipmentQuantityMt,
                saleRows.Count(),
                saleQuantityMt,
                totalExpensesUsd,
                paymentRows.Count(),
                paymentTotalUsd,
                currentStockQuantityMt,
                miniPnl.GrossMarginUsd,
                dispatchSectionState,
                preSaleSectionState,
                loadingRegisters.Any(l => !string.IsNullOrWhiteSpace(l.BillOfLadingNumber))),
            QuantityFlowItems = BuildQuantityFlowItems(
                contract.QuantityMt,
                purchaseAgg.TotalLoadedQuantityMt,
                totalReceivedQuantityMt,
                dispatchQuantityMt,
                saleQuantityMt,
                totalLossQuantityMt,
                currentStockQuantityMt,
                Math.Max(contract.QuantityMt - purchaseAgg.TotalLoadedQuantityMt, 0m),
                Math.Max(purchaseAgg.TotalLoadedQuantityMt - totalReceivedQuantityMt - receiptShortageLossMt, 0m)),
            ActivityItems = activityItems,
            DispatchSectionState = dispatchSectionState,
            PreSaleSectionState = preSaleSectionState,
            LedgerSummary = new ContractJourneyLedgerSummaryViewModel
            {
                DebitTotalUsd = debitTotalUsd,
                CreditTotalUsd = creditTotalUsd,
                BalanceUsd = relatedBalanceUsd,
                SourceTypeCounts = ledgers
                    .GroupBy(l => l.SourceType)
                    .OrderBy(g => g.Key)
                    .Select(g => new ContractJourneySourceCountViewModel
                    {
                        SourceType = g.Key,
                        Count = g.Count()
                    })
                    .ToList(),
                ReferenceList = ledgers
                    .Select(l => l.Reference)
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct()
                    .Take(8)
                    .Cast<string>()
                    .ToList()
            },
            MiniPnl = miniPnl,
            NotesForReview = notesForReview,
            Warnings = warnings,
            SummaryMetrics = summaryMetrics,
            NextRecommendedActionTitle = nextAction.Title,
            NextRecommendedActionDescription = nextAction.Description,
            NextRecommendedActionUrl = nextAction.Url,
            NextRecommendedActionCssClass = nextAction.CssClass
        };
    }

    private async Task<IReadOnlyList<ContractJourneyShipmentScenarioViewModel>> BuildPurchaseShipmentScenariosAsync(
        int contractId,
        decimal? contractFinalPriceUsd,
        IReadOnlyList<InventoryTransportLeg> inventoryTransportLegs,
        IReadOnlyList<InventoryTransportReceipt> inventoryTransportReceipts)
    {
        var contractShipmentRows = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ContractId == contractId)
            .Select(sc => new
            {
                sc.ShipmentId,
                QuantityMt = sc.QuantityMt ?? 0m
            })
            .ToListAsync();

        var shipmentIds = contractShipmentRows
            .Select(sc => sc.ShipmentId)
            .Concat(inventoryTransportLegs
                .Where(l => l.ShipmentId.HasValue)
                .Select(l => l.ShipmentId!.Value))
            .Distinct()
            .ToList();

        if (shipmentIds.Count == 0)
        {
            return [];
        }

        var shipments = await _db.Shipments
            .Include(s => s.Vessel)
            .Include(s => s.OriginLocation)
            .Include(s => s.DestinationLocation)
            .AsNoTracking()
            .Where(s => shipmentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);

        var allShipmentContractRows = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => shipmentIds.Contains(sc.ShipmentId))
            .Select(sc => new
            {
                sc.ShipmentId,
                QuantityMt = sc.QuantityMt ?? 0m
            })
            .ToListAsync();

        var contractAllocationByShipmentId = contractShipmentRows
            .GroupBy(sc => sc.ShipmentId)
            .ToDictionary(g => g.Key, g => g.Sum(sc => sc.QuantityMt));
        var totalAllocationByShipmentId = allShipmentContractRows
            .GroupBy(sc => sc.ShipmentId)
            .ToDictionary(g => g.Key, g => g.Sum(sc => sc.QuantityMt));
        var legsByShipmentId = inventoryTransportLegs
            .Where(l => l.ShipmentId.HasValue && shipmentIds.Contains(l.ShipmentId.Value))
            .GroupBy(l => l.ShipmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var receiptByLegId = inventoryTransportReceipts
            .GroupBy(r => r.InventoryTransportLegId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Id).First());
        var scenarioLegIds = legsByShipmentId
            .SelectMany(g => g.Value)
            .Select(l => l.Id)
            .Distinct()
            .ToList();

        var inventorySaleRows = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ContractId == contractId
                && m.Direction == MovementDirection.Out
                && m.SalesTransactionId.HasValue)
            .Join(
                _db.SalesTransactions
                    .AsNoTracking()
                    .Where(s => !s.IsCancelled && s.ShipmentId.HasValue),
                m => m.SalesTransactionId!.Value,
                s => s.Id,
                (m, s) => new
                {
                    ShipmentId = s.ShipmentId!.Value,
                    SalesTransactionId = s.Id,
                    QuantityMt = m.QuantityMt,
                    AmountUsd = decimal.Round(m.QuantityMt * s.UnitPriceUsd, 4, MidpointRounding.AwayFromZero)
                })
            .Where(s => shipmentIds.Contains(s.ShipmentId))
            .ToListAsync();

        var inventorySaleIds = inventorySaleRows
            .Select(s => s.SalesTransactionId)
            .ToHashSet();
        var directReceiptSaleRows = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.SalesTransactionId.HasValue
                && scenarioLegIds.Contains(r.InventoryTransportLegId))
            .Join(
                _db.SalesTransactions
                    .AsNoTracking()
                    .Where(s => !s.IsCancelled && s.ShipmentId.HasValue),
                r => r.SalesTransactionId!.Value,
                s => s.Id,
                (r, s) => new
                {
                    ShipmentId = s.ShipmentId!.Value,
                    SalesTransactionId = s.Id,
                    QuantityMt = r.ReceivedQuantityMt,
                    AmountUsd = decimal.Round(r.ReceivedQuantityMt * s.UnitPriceUsd, 4, MidpointRounding.AwayFromZero)
                })
            .Where(s => shipmentIds.Contains(s.ShipmentId) && !inventorySaleIds.Contains(s.SalesTransactionId))
            .ToListAsync();

        var saleAggregates = inventorySaleRows
            .Concat(directReceiptSaleRows)
            .GroupBy(s => s.ShipmentId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Count = g.Select(s => s.SalesTransactionId).Distinct().Count(),
                    QuantityMt = g.Sum(s => s.QuantityMt),
                    AmountUsd = g.Sum(s => s.AmountUsd)
                });

        // مصرفِ ساخته‌شده از اظهارنامهٔ گمرکی کنار می‌رود، چون همان مبلغ پایین‌تر در
        // CustomsUsd شمرده می‌شود و TotalOperationalCostUsd هر دو را جمع می‌کند.
        var expenseRows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && ((e.ShipmentId.HasValue && shipmentIds.Contains(e.ShipmentId.Value))
                    || (e.TransportLegId.HasValue && scenarioLegIds.Contains(e.TransportLegId.Value))))
            .Select(e => new
            {
                e.Id,
                e.ShipmentId,
                e.ContractId,
                e.TransportLegId,
                e.AmountUsd
            })
            .ToListAsync();

        var customsRows = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => c.TransportLegId.HasValue && scenarioLegIds.Contains(c.TransportLegId.Value))
            .Select(c => new
            {
                c.Id,
                c.TransportLegId,
                c.TotalUsd
            })
            .ToListAsync();

        var purchaseUnitCostUsd = contractFinalPriceUsd.GetValueOrDefault();
        var scenarios = new List<ContractJourneyShipmentScenarioViewModel>();

        foreach (var shipmentId in shipmentIds)
        {
            shipments.TryGetValue(shipmentId, out var shipment);
            var legs = legsByShipmentId.TryGetValue(shipmentId, out var shipmentLegs)
                ? shipmentLegs
                : [];
            var shipmentLegIds = legs.Select(l => l.Id).ToHashSet();
            var receiptedLegs = legs
                .Where(l => receiptByLegId.ContainsKey(l.Id))
                .ToList();
            var distributionLegs = receiptedLegs
                .Where(l => l.TransportType != LoadingTransportType.Vessel)
                .ToList();
            if (distributionLegs.Count == 0)
            {
                distributionLegs = receiptedLegs;
            }

            contractAllocationByShipmentId.TryGetValue(shipmentId, out var allocatedQuantityMt);
            if (allocatedQuantityMt <= 0m)
            {
                allocatedQuantityMt = distributionLegs.Count > 0
                    ? distributionLegs.Sum(l => l.QuantityMt)
                    : legs.Sum(l => l.QuantityMt);
            }

            totalAllocationByShipmentId.TryGetValue(shipmentId, out var totalShipmentAllocationMt);
            var allocationShare = totalShipmentAllocationMt > 0m
                ? allocatedQuantityMt / totalShipmentAllocationMt
                : 0m;

            saleAggregates.TryGetValue(shipmentId, out var saleAggregate);
            var specificExpenseRows = expenseRows
                .Where(e => (e.TransportLegId.HasValue && shipmentLegIds.Contains(e.TransportLegId.Value))
                    || (e.ShipmentId == shipmentId && e.ContractId == contractId))
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .ToList();
            var sharedExpenseRows = expenseRows
                .Where(e => e.ShipmentId == shipmentId
                    && !e.ContractId.HasValue
                    && !e.TransportLegId.HasValue)
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .ToList();
            var shipmentCustomsRows = customsRows
                .Where(c => c.TransportLegId.HasValue && shipmentLegIds.Contains(c.TransportLegId.Value))
                .ToList();

            scenarios.Add(new ContractJourneyShipmentScenarioViewModel
            {
                ShipmentId = shipmentId,
                ShipmentCode = shipment?.ShipmentCode ?? $"#{shipmentId}",
                VesselName = shipment?.Vessel?.Name,
                DepartureDate = shipment?.DepartureDate,
                ArrivalDate = shipment?.ArrivalDate,
                OriginName = shipment?.OriginLocation?.NamePersian ?? shipment?.OriginLocation?.Name,
                DestinationName = shipment?.DestinationLocation?.NamePersian ?? shipment?.DestinationLocation?.Name,
                AllocatedQuantityMt = allocatedQuantityMt,
                PurchaseUnitCostUsd = purchaseUnitCostUsd,
                PurchaseCostUsd = decimal.Round(allocatedQuantityMt * purchaseUnitCostUsd, 4, MidpointRounding.AwayFromZero),
                TransportLoadedQuantityMt = distributionLegs.Sum(l => l.QuantityMt),
                ReceivedQuantityMt = distributionLegs
                    .Where(l => receiptByLegId.ContainsKey(l.Id))
                    .Sum(l => receiptByLegId[l.Id].ReceivedQuantityMt),
                SoldQuantityMt = saleAggregate?.QuantityMt ?? 0m,
                SalesUsd = saleAggregate?.AmountUsd ?? 0m,
                ExpenseTransactionsUsd = decimal.Round(specificExpenseRows.Sum(e => e.AmountUsd), 4, MidpointRounding.AwayFromZero),
                SharedExpenseTransactionsUsd = decimal.Round(sharedExpenseRows.Sum(e => e.AmountUsd) * allocationShare, 4, MidpointRounding.AwayFromZero),
                CustomsUsd = decimal.Round(shipmentCustomsRows.Sum(c => c.TotalUsd), 4, MidpointRounding.AwayFromZero),
                TransportLegCount = distributionLegs.Count,
                SalesCount = saleAggregate?.Count ?? 0,
                ExpenseCount = specificExpenseRows.Count + sharedExpenseRows.Count,
                CustomsCount = shipmentCustomsRows.Count
            });
        }

        return scenarios
            .OrderByDescending(s => s.DepartureDate ?? s.ArrivalDate ?? DateTime.MinValue)
            .ThenByDescending(s => s.ShipmentId)
            .ToList();
    }

    private static IReadOnlyDictionary<int, ShipmentSaleCostAllocation> BuildShipmentSaleCostAllocations(
        IReadOnlyCollection<ContractJourneySaleItemViewModel> saleItems,
        IReadOnlyCollection<ContractJourneyShipmentScenarioViewModel> shipmentScenarios)
    {
        if (saleItems.Count == 0 || shipmentScenarios.Count == 0)
        {
            return new Dictionary<int, ShipmentSaleCostAllocation>();
        }

        static bool IsEligibleShipmentInventorySale(ContractJourneySaleItemViewModel item)
            => item.HasInventoryMovementTrace
               && item.ShipmentId.HasValue
               && !item.InventoryTransportLegId.HasValue
               && item.QuantityMt > 0m;

        var eligibleQuantityByShipmentId = saleItems
            .Where(IsEligibleShipmentInventorySale)
            .GroupBy(s => s.ShipmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.QuantityMt));
        if (eligibleQuantityByShipmentId.Count == 0)
        {
            return new Dictionary<int, ShipmentSaleCostAllocation>();
        }

        var scenarioByShipmentId = shipmentScenarios
            .GroupBy(s => s.ShipmentId)
            .ToDictionary(g => g.Key, g => g.First());

        var allocations = new Dictionary<int, ShipmentSaleCostAllocation>();
        foreach (var item in saleItems.Where(IsEligibleShipmentInventorySale))
        {
            var shipmentId = item.ShipmentId!.Value;
            if (!scenarioByShipmentId.TryGetValue(shipmentId, out var scenario))
            {
                continue;
            }

            var soldQuantityMt = scenario.SoldQuantityMt > 0m
                ? scenario.SoldQuantityMt
                : eligibleQuantityByShipmentId.GetValueOrDefault(shipmentId);
            if (soldQuantityMt <= 0m)
            {
                continue;
            }

            var purchaseCostPerMtUsd = scenario.PurchaseCostUsd > 0m
                ? scenario.PurchaseCostUsd / soldQuantityMt
                : (decimal?)null;
            var operationalExpensePerMtUsd =
                (scenario.ExpenseTransactionsUsd + scenario.SharedExpenseTransactionsUsd) / soldQuantityMt;
            var customsPerMtUsd = scenario.CustomsUsd / soldQuantityMt;

            if (!purchaseCostPerMtUsd.HasValue
                && operationalExpensePerMtUsd <= 0m
                && customsPerMtUsd <= 0m)
            {
                continue;
            }

            allocations[item.SalesTransactionId] = new ShipmentSaleCostAllocation(
                purchaseCostPerMtUsd,
                operationalExpensePerMtUsd,
                customsPerMtUsd);
        }

        return allocations;
    }

    private sealed record ShipmentSaleCostAllocation(
        decimal? PurchaseCostPerMtUsd,
        decimal OperationalExpensePerMtUsd,
        decimal CustomsPerMtUsd);

    private sealed record RecommendedAction(string Title, string Description, string Url, string CssClass);

    private async Task<ContractJourneyDetailsViewModel> BuildSaleContractDetailsAsync(
        Contract contract,
        ContractJourneyDetailsViewModel baseModel,
        string activeTab,
        bool lockContract)
    {
        var sales = await _db.SalesTransactions
            .Include(s => s.Customer)
            .AsNoTracking()
            .Where(s => s.ContractId == contract.Id && !s.IsCancelled)
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();
        var saleIdSet = sales.Select(s => s.Id).ToHashSet();
        var saleInventoryMovements = saleIdSet.Count == 0
            ? new List<InventoryMovement>()
            : await _db.InventoryMovements
                .Include(m => m.Contract)
                .AsNoTracking()
                .Where(m => m.Direction == MovementDirection.Out &&
                            m.SalesTransactionId.HasValue &&
                            saleIdSet.Contains(m.SalesTransactionId.Value))
                .ToListAsync();
        var saleMovementBySaleId = saleInventoryMovements
            .GroupBy(m => m.SalesTransactionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).First());

        var saleItems = sales
            .Select(s => new ContractJourneySaleItemViewModel
            {
                SalesTransactionId = s.Id,
                IsSalePricePending = s.IsSalePricePending,
                ShipmentId = s.ShipmentId,
                InvoiceNumber = s.InvoiceNumber,
                CustomerName = s.Customer?.Name ?? baseModel.CustomerName ?? string.Empty,
                SaleDate = s.SaleDate,
                QuantityMt = s.QuantityMt,
                UnitPriceUsd = s.UnitPriceUsd,
                AmountUsd = s.TotalUsd,
                SalesContractDisplay = contract.DisplayLabel,
                StockSourceTypeName = s.StockSourceType.HasValue ? ToStockSourceTypeName(s.StockSourceType.Value) : null,
                SaleStageName = SaleStageLabels.ToPersian(s.SaleStage),
                HasInventoryMovementTrace = saleMovementBySaleId.ContainsKey(s.Id),
                SourcePurchaseContractNumber = saleMovementBySaleId.ContainsKey(s.Id)
                    ? saleMovementBySaleId[s.Id].Contract?.DisplayLabel
                    : null,
                TraceKind = "مستقیم از قرارداد فروش"
            })
            .ToList();

        var salesWithoutTraceCount = saleItems.Count(s => !s.HasInventoryMovementTrace);

        var expenses = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .AsNoTracking()
            .Where(e => e.ContractId == contract.Id && !e.IsCancelled)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToListAsync();
        var expenseIdSet = expenses.Select(e => e.Id).ToHashSet();
        var expenseItems = expenses
            .Select(e => new ContractJourneyExpenseItemViewModel
            {
                ExpenseTransactionId = e.Id,
                ExpenseTypeName = e.ExpenseType?.NamePersian ?? e.ExpenseType?.Name ?? string.Empty,
                ExpenseDate = e.ExpenseDate,
                AmountUsd = e.AmountUsd,
                Description = e.Description,
                TraceKind = "مستقیم از قرارداد فروش"
            })
            .ToList();

        var paymentEntities = new List<PaymentTransaction>();
        paymentEntities.AddRange(await _db.PaymentTransactions
            .Include(p => p.CashAccount)
            .Include(p => p.PaidByPartner)
            .Include(p => p.LedgerEntry)
            .AsNoTracking()
            .Where(p => p.ContractId == contract.Id)
            .ToListAsync());
        if (saleIdSet.Count > 0)
        {
            paymentEntities.AddRange(await _db.PaymentTransactions
                .Include(p => p.CashAccount)
                .Include(p => p.PaidByPartner)
                .Include(p => p.LedgerEntry)
                .AsNoTracking()
                .Where(p => p.SalesTransactionId.HasValue && saleIdSet.Contains(p.SalesTransactionId.Value))
                .ToListAsync());
        }

        var payments = paymentEntities
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToList();
        var paymentIds = payments.Select(p => p.Id).ToHashSet();
        var paymentItems = payments
            .Select(p => new ContractJourneyPaymentItemViewModel
            {
                PaymentTransactionId = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = ToPaymentDirectionName(p.Direction),
                PaymentKind = p.PaymentKind,
                PaymentKindName = ToPaymentKindName(p.PaymentKind),
                CashAccountName = p.CashAccount?.Name ?? string.Empty,
                FundingSource = p.FundingSource,
                PaidByPartnerName = p.PaidByPartner?.Name,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                LedgerEntryId = p.LedgerEntryId,
                TraceKind = ResolvePaymentTraceKind(p, contract.Id, saleIdSet, expenseIdSet, new HashSet<int>(), new HashSet<int>())
            })
            .ToList();
        var sarrafSettlementItems = await BuildSarrafSettlementItemsAsync(contract.Id);

        var ledgerEntities = new List<LedgerEntry>();
        ledgerEntities.AddRange(await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId == contract.Id)
            .ToListAsync());
        if (saleIdSet.Count > 0)
        {
            ledgerEntities.AddRange(await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Sale" && saleIdSet.Contains(l.SourceId))
                .ToListAsync());
        }
        if (paymentIds.Count > 0)
        {
            ledgerEntities.AddRange(await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => PaymentLedgerSourceTypes.Contains(l.SourceType) && paymentIds.Contains(l.SourceId))
                .ToListAsync());
        }

        var ledgers = ledgerEntities
            .GroupBy(l => l.Id)
            .Select(g => g.First())
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .ToList();
        var ledgerItems = ledgers
            .Select(l => new ContractJourneyLedgerItemViewModel
            {
                LedgerEntryId = l.Id,
                EntryDate = l.EntryDate,
                SideName = UiText.LedgerSideName(HttpContext, l.Side),
                AmountUsd = l.AmountUsd,
                SourceType = l.SourceType,
                SourceId = l.SourceId,
                Reference = l.Reference,
                Description = l.Description,
                TraceKind = ResolveLedgerTraceKind(l, contract.Id, saleIdSet, expenseIdSet, paymentIds, new HashSet<int>())
            })
            .ToList();
        var debitTotalUsd = ledgers.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd);
        var creditTotalUsd = ledgers.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd);
        var relatedBalanceUsd = await ReadContractBalanceUsdAsync(contract.Id);
        var totalSalesUsd = saleItems.Sum(s => s.AmountUsd);
        var totalExpensesUsd = expenseItems.Sum(e => e.AmountUsd);
        var totalPaymentsUsd = paymentItems.Sum(p => p.AmountUsd);
        var paymentInTotalUsd = payments.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd);
        var paymentOutTotalUsd = payments.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd);
        var expenseBreakdowns = expenseItems
            .GroupBy(e => string.IsNullOrWhiteSpace(e.ExpenseTypeName) ? "نامشخص" : e.ExpenseTypeName)
            .OrderByDescending(g => g.Sum(e => e.AmountUsd))
            .Select(g => new ContractJourneyExpenseBreakdownViewModel
            {
                ExpenseTypeName = g.Key,
                Count = g.Count(),
                AmountUsd = g.Sum(e => e.AmountUsd)
            })
            .ToList();
        var activityItems = BuildActivityItems(
            [],
            [],
            [],
            saleItems,
            expenseItems,
            paymentItems,
            ledgerItems);

        var kpis = new ContractJourneyKpiSummaryViewModel
        {
            ContractQuantityMt = contract.QuantityMt,
            SoldQuantityMt = saleItems.Sum(s => s.QuantityMt),
            TotalExpensesUsd = totalExpensesUsd,
            TotalPaymentsUsd = totalPaymentsUsd,
            RelatedBalanceUsd = relatedBalanceUsd
        };
        var miniPnl = await BuildMiniPnlAsync(
            contract.Id,
            realised: null,
            "سود قرارداد فروش: عاید − بهای انبارِ کالای فروخته‌شده − مصارفِ قرارداد ± اثرِ ارزی.");
        var ledgerSummary = new ContractJourneyLedgerSummaryViewModel
        {
            DebitTotalUsd = debitTotalUsd,
            CreditTotalUsd = creditTotalUsd,
            BalanceUsd = relatedBalanceUsd,
            SourceTypeCounts = ledgers
                .GroupBy(l => l.SourceType)
                .OrderBy(g => g.Key)
                .Select(g => new ContractJourneySourceCountViewModel
                {
                    SourceType = g.Key,
                    Count = g.Count()
                })
                .ToList(),
            ReferenceList = ledgers
                .Select(l => l.Reference)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct()
                .Take(8)
                .Cast<string>()
                .ToList()
        };
        var nextAction = BuildSaleNextAction(contract.Id, saleItems.Count, paymentItems.Count);
        var warnings = BuildWarnings(
            contract,
            baseModel.PricingNeedsReview,
            baseModel.PricingFallbackApplied,
            loadedQuantityMt: 0m,
            receivedQuantityMt: 0m,
            loadingCount: 0,
            receiptCount: 0,
            dispatchCount: 0,
            salesCount: saleItems.Count,
            expenseCount: expenseItems.Count,
            paymentCount: paymentItems.Count,
            isSaleContract: true,
            salesWithoutTraceCount: salesWithoutTraceCount);

        return new ContractJourneyDetailsViewModel
        {
            ActiveTab = activeTab,
            LockContract = lockContract,
            ContractId = baseModel.ContractId,
            ContractName = baseModel.ContractName,
            ContractNumber = baseModel.ContractNumber,
            ContractTypeName = baseModel.ContractTypeName,
            ContractTypeBadgeClass = baseModel.ContractTypeBadgeClass,
            CompanyName = baseModel.CompanyName,
            ProductName = baseModel.ProductName,
            ContractUnitText = baseModel.ContractUnitText,
            SupplierName = baseModel.SupplierName,
            CustomerName = baseModel.CustomerName,
            ContractQuantityMt = baseModel.ContractQuantityMt,
            HasMixedLoadingPrices = baseModel.HasMixedLoadingPrices,
            LoadingsValueUsd = baseModel.LoadingsValueUsd,
            ParentContractId = baseModel.ParentContractId,
            ParentContractNumber = baseModel.ParentContractNumber,
            SubContractItems = baseModel.SubContractItems,
            IsPartnershipContract = baseModel.IsPartnershipContract,
            PartnerShareItems = baseModel.PartnerShareItems,
            Currency = baseModel.Currency,
            PriceDisplay = baseModel.PriceDisplay,
            PricingMethodName = baseModel.PricingMethodName,
            PricingStatusName = baseModel.PricingStatusName,
            EditPricingUrl = baseModel.EditPricingUrl,
            PricingFormulaText = baseModel.PricingFormulaText,
            PricingFinalUnitPriceUsd = baseModel.PricingFinalUnitPriceUsd,
            PricingNeedsReview = baseModel.PricingNeedsReview,
            PricingReason = baseModel.PricingReason,
            PricingFallbackApplied = baseModel.PricingFallbackApplied,
            PricingFormulaNote = baseModel.PricingFormulaNote,
            StatusName = baseModel.StatusName,
            StatusBadgeClass = baseModel.StatusBadgeClass,
            ContractDate = baseModel.ContractDate,
            StartDate = baseModel.StartDate,
            EndDate = baseModel.EndDate,
            Notes = baseModel.Notes,
            IsPurchaseContract = false,
            UnsupportedMessage = "مرکز عملیات قرارداد فروش فعلاً خلاصه فروش، پرداخت و Ledger را نشان می‌دهد؛ جریان کامل موجودی برای قرارداد خرید کامل‌تر است.",
            SalesFromInventoryCount = saleItems.Count(s => s.HasInventoryMovementTrace),
            SalesWithoutTraceCount = salesWithoutTraceCount,
            PaymentInTotalUsd = paymentInTotalUsd,
            PaymentOutTotalUsd = paymentOutTotalUsd,
            Kpis = kpis,
            KpiCards = BuildKpiCards(kpis),
            ExpenseBreakdowns = expenseBreakdowns,
            ActivityItems = activityItems,
            SalesItems = saleItems,
            ExpenseItems = expenseItems,
            PaymentItems = paymentItems,
            SarrafSettlementItems = sarrafSettlementItems,
            LedgerSummary = ledgerSummary,
            LedgerItems = ledgerItems,
            MiniPnl = miniPnl,
            Warnings = warnings,
            NextRecommendedActionTitle = nextAction.Title,
            NextRecommendedActionDescription = nextAction.Description,
            NextRecommendedActionUrl = nextAction.Url,
            NextRecommendedActionCssClass = nextAction.CssClass
        };
    }

    private async Task<IReadOnlyList<ContractJourneySarrafSettlementItemViewModel>> BuildSarrafSettlementItemsAsync(int contractId)
    {
        var settlements = await _db.SarrafSettlements
            .AsNoTracking()
            .Include(s => s.Sarraf)
            .Include(s => s.Supplier)
            .Where(s => s.ContractId == contractId)
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        var settlementIds = settlements.Select(s => s.Id).ToList();
        var supplierReductionRubBySettlementId = settlementIds.Count == 0
            ? new Dictionary<int, decimal?>()
            : (await _db.LedgerEntries
                .AsNoTracking()
                // فقط اثرِ جاریِ هر تسویه: ثبتِ منسوخ‌شده و سطرِ برگشت از جمعِ روبلیِ قرارداد کنار می‌روند.
                .WhereEffectiveSarrafSettlementLegs(_db)
                .Where(l => settlementIds.Contains(l.SourceId)
                    && (l.SourceType == SarrafSettlementService.SupplierLedgerSourceType
                        || l.SourceType == SarrafSettlementService.CancelSourceType
                        || l.SourceType == SarrafSettlementService.EditReversalSourceType)
                    && l.SourceAmount.HasValue
                    && (l.SourceCurrencyCode == "RUB"
                        || (l.SourceCurrencyCode == null && l.Currency == "RUB")))
                .Select(l => new
                {
                    l.SourceId,
                    l.Side,
                    l.SourceAmount
                })
                .ToListAsync())
            .GroupBy(l => l.SourceId)
            .ToDictionary(
                g => g.Key,
                g => (decimal?)g.Sum(l => l.Side == LedgerSide.Debit
                    ? l.SourceAmount!.Value
                    : -l.SourceAmount!.Value));

        return settlements
            .Select(s => new ContractJourneySarrafSettlementItemViewModel
            {
                SarrafSettlementId = s.Id,
                SettlementDate = s.SettlementDate,
                SarrafName = s.Sarraf?.Name ?? string.Empty,
                SupplierName = s.Supplier?.Name,
                ReferenceNumber = s.ReferenceNumber,
                RequestedAmountUsd = s.RequestedAmountUsd,
                SarrafChargedAmountUsd = s.SarrafChargedAmountUsd,
                SupplierAcceptedAmountUsd = s.SupplierAcceptedAmountUsd,
                SupplierReductionAmountUsd = SarrafSupplierReductionAmountUsd(s),
                SupplierReductionAmountRub = supplierReductionRubBySettlementId.TryGetValue(s.Id, out var supplierReductionRub)
                    ? supplierReductionRub
                    : null,
                DifferenceAmountUsd = s.DifferenceAmountUsd,
                DifferenceType = s.DifferenceType,
                DifferenceTreatment = s.DifferenceTreatment,
                Status = s.Status,
                LedgerEntryId = s.LedgerEntryId,
                ExchangeDifferenceLedgerEntryId = s.ExchangeDifferenceLedgerEntryId
            })
            .ToList();
    }

    private static decimal SarrafSupplierReductionAmountUsd(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedAmountUsd
            : settlement.SupplierAcceptedAmountUsd;

    private static RecommendedAction BuildPurchaseNextAction(
        int contractId,
        IReadOnlyList<LoadingRegister> loadingRegisters,
        decimal loadedQuantityMt,
        decimal receivedQuantityMt,
        decimal currentStockQuantityMt,
        int dispatchCount,
        int salesCount,
        int expenseCount,
        int paymentCount)
    {
        if (loadingRegisters.Count == 0)
        {
            return new RecommendedAction(
                "ثبت بارگیری",
                "برای این قرارداد هنوز بارگیری ثبت نشده است. ثبت واقعی از فرم Loading انجام می‌شود.",
                AppendReturnUrl($"/Loading/Create?contractId={contractId}&lockContract=true", JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Loadings)),
                "btn btn-primary");
        }

        if (loadedQuantityMt > receivedQuantityMt)
        {
            var singleLoading = loadingRegisters.Count == 1 ? loadingRegisters[0] : null;
            var url = singleLoading is null
                ? JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Loadings)
                : AppendReturnUrl(
                    $"/LoadingReceipts/Create?loadingId={singleLoading.Id}",
                    JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Receipts));

            return new RecommendedAction(
                "ثبت رسید بارگیری",
                singleLoading is null
                    ? "چند بارگیری باز وجود دارد. از جدول بارگیری، ردیف مناسب را برای ثبت رسید انتخاب کنید."
                    : "برای بارگیری ثبت‌شده هنوز رسید کامل ثبت نشده است.",
                url,
                "btn btn-primary");
        }

        if (receivedQuantityMt > 0m && currentStockQuantityMt <= 0m && dispatchCount == 0 && salesCount == 0)
        {
            return new RecommendedAction(
                "بررسی موجودی یا ثبت Dispatch / فروش",
                "رسید/تخلیه ثبت شده اما موجودی آزاد یا خروج مرتبط هنوز واضح نیست. ابتدا Stock Card را بررسی کنید و سپس خروج را از فرم Dispatch یا Sales ثبت کنید.",
                JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Inventory),
                "btn btn-primary");
        }

        if (receivedQuantityMt > 0m && currentStockQuantityMt > 0m && dispatchCount == 0 && salesCount == 0)
        {
            return new RecommendedAction(
                "ثبت Dispatch یا فروش",
                "رسید و موجودی وجود دارد؛ خروج بعدی باید از فرم Dispatch یا Sales ثبت شود.",
                AppendReturnUrl($"/Dispatch/Create?contractId={contractId}", JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Dispatch)),
                "btn btn-primary");
        }

        if ((dispatchCount > 0 || salesCount > 0) && expenseCount == 0)
        {
            return new RecommendedAction(
                "ثبت هزینه‌ها",
                "عملیات خروج یا فروش ثبت شده اما هزینه‌ای برای این قرارداد دیده نمی‌شود.",
                AppendReturnUrl($"/Expenses/Create?contractId={contractId}", JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Costs)),
                "btn btn-outline-primary");
        }

        if ((salesCount > 0 || expenseCount > 0) && paymentCount == 0)
        {
            return new RecommendedAction(
                "ثبت پرداخت/دریافت",
                "فروش یا هزینه ثبت شده اما پرداخت یا دریافت مرتبط دیده نمی‌شود.",
                AppendReturnUrl($"/Payments/Create?contractId={contractId}", JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Finance)),
                "btn btn-outline-primary");
        }

        return new RecommendedAction(
            "بررسی Ledger و سود/زیان",
            "داده‌های اصلی ثبت شده‌اند. Ledger و گزارش سود/زیان را برای کنترل نهایی بررسی کنید.",
            $"/Ledger?ContractId={contractId}",
            "btn btn-outline-secondary");
    }

    private static RecommendedAction BuildSaleNextAction(int contractId, int salesCount, int paymentCount)
    {
        if (salesCount == 0)
        {
            return new RecommendedAction(
                "ثبت فروش",
                "برای این قرارداد فروش هنوز SalesTransaction ثبت نشده است.",
                AppendReturnUrl($"/Sales/Create?contractId={contractId}", JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Sales)),
                "btn btn-primary");
        }

        if (paymentCount == 0)
        {
            return new RecommendedAction(
                "ثبت دریافت از مشتری",
                "فروش ثبت شده اما دریافت مرتبط دیده نمی‌شود.",
                AppendReturnUrl($"/Payments/Create?contractId={contractId}", JourneyReturnUrl(contractId, ContractJourneyTabs.Details.Finance)),
                "btn btn-outline-primary");
        }

        return new RecommendedAction(
            "بررسی Ledger / Balance / P&L",
            "فروش و دریافت دیده می‌شود؛ کنترل نهایی از Ledger و گزارش‌ها انجام شود.",
            $"/Ledger?ContractId={contractId}",
            "btn btn-outline-secondary");
    }

    private static IReadOnlyList<string> BuildWarnings(
        Contract contract,
        bool pricingNeedsReview,
        bool pricingFallbackApplied,
        decimal loadedQuantityMt,
        decimal receivedQuantityMt,
        int loadingCount,
        int receiptCount,
        int dispatchCount,
        int salesCount,
        int expenseCount,
        int paymentCount,
        bool isSaleContract,
        int unreceiptedLoadingCount = 0,
        bool hasNegativeStockWarning = false,
        int salesWithoutTraceCount = 0)
    {
        var warnings = new List<string>();

        if (pricingNeedsReview)
        {
            warnings.Add("قیمت نهایی قرارداد هنوز تکمیل نشده است؛ عملیات مالی جدید ممکن است نیاز به قیمت دستی داشته باشد.");
        }

        if (pricingFallbackApplied)
        {
            warnings.Add("برای قیمت‌گذاری از نرخ جایگزین استفاده شده است.");
        }

        if (loadedQuantityMt > contract.QuantityMt)
        {
            warnings.Add("مقدار بارگیری از مقدار قرارداد بیشتر شده است.");
        }
        else if (contract.QuantityMt > 0m && loadedQuantityMt >= contract.QuantityMt * 0.9m)
        {
            warnings.Add("مقدار بارگیری به حد قرارداد نزدیک است.");
        }

        if (!isSaleContract && loadingCount == 0)
        {
            warnings.Add("قرارداد خرید هنوز بارگیری ثبت‌شده ندارد.");
        }

        if (loadingCount > 0 && receiptCount == 0)
        {
            warnings.Add("بارگیری ثبت شده اما رسید بارگیری ندارد.");
        }

        if (unreceiptedLoadingCount > 0)
        {
            warnings.Add($"{unreceiptedLoadingCount:N0} بارگیری هنوز receipt/تخلیه کامل ندارد.");
        }

        if (receiptCount > 0 && salesCount == 0 && dispatchCount == 0)
        {
            warnings.Add("رسید ثبت شده اما فروش یا Dispatch مرتبط دیده نمی‌شود.");
        }

        if (hasNegativeStockWarning)
        {
            warnings.Add("موجودی آزاد یا مانده حرکتی منفی/مشکوک به نظر می‌رسد و باید در Stock Card بررسی شود.");
        }

        if (salesCount > 0 && paymentCount == 0)
        {
            warnings.Add("فروش ثبت شده اما payment مرتبط دیده نمی‌شود.");
        }

        if (expenseCount > 0 && paymentCount == 0)
        {
            warnings.Add("هزینه ثبت شده اما payment مرتبط دیده نمی‌شود.");
        }

        if (salesWithoutTraceCount > 0)
        {
            warnings.Add($"{salesWithoutTraceCount:N0} فروش trace/ردیابی کامل منبع موجودی از مسیر InventoryMovement ندارد.");
        }

        if (expenseCount == 0)
        {
            warnings.Add("هزینه‌ای برای این قرارداد ثبت نشده است.");
        }

        if (isSaleContract)
        {
            warnings.Add("برای قرارداد فروش، ContractJourney فعلی خلاصه محدودتری نسبت به قرارداد خرید دارد.");
        }

        warnings.Add("سود/زیان فعلی خلاصه عملیاتی است و باید با Ledger/Reports نهایی بررسی شود.");

        return warnings;
    }

    private static string JourneyReturnUrl(int contractId, string tab)
        => $"/ContractJourney/Details?contractId={contractId}&tab={Uri.EscapeDataString(tab)}";

    private static string AppendReturnUrl(string url, string returnUrl)
        => $"{url}{(url.Contains('?') ? "&" : "?")}returnUrl={Uri.EscapeDataString(returnUrl)}";

    private static IReadOnlyList<ContractJourneyKpiCardViewModel> BuildKpiCards(ContractJourneyKpiSummaryViewModel kpis)
        => new List<ContractJourneyKpiCardViewModel>
        {
            new() { Icon = "bi bi-file-earmark-text", Title = "مقدار قرارداد", Value = $"{kpis.ContractQuantityMt:N4} MT", ToneClass = "journey-kpi-primary" },
            new() { Icon = "bi bi-box-arrow-in-down", Title = "بارگیری", Value = $"{kpis.LoadedQuantityMt:N4} MT", ToneClass = "journey-kpi-info" },
            new() { Icon = "bi bi-building-check", Title = "رسید", Value = $"{kpis.ReceivedQuantityMt:N4} MT", ToneClass = "journey-kpi-success" },
            new() { Icon = "bi bi-truck", Title = "دیسپچ", Value = $"{kpis.DispatchedQuantityMt:N4} MT", ToneClass = "journey-kpi-info" },
            new() { Icon = "bi bi-receipt", Title = "فروش", Value = $"{kpis.SoldQuantityMt:N4} MT", ToneClass = "journey-kpi-success" },
            new() { Icon = "bi bi-journal-text", Title = "پیش‌فروش", Value = kpis.PreSaleQuantityMt.HasValue ? $"{kpis.PreSaleQuantityMt.Value:N4} MT" : "نیاز به بررسی", Subtitle = kpis.PreSaleNote, ToneClass = kpis.PreSaleNeedsReview ? "journey-kpi-warning" : "journey-kpi-info" },
            new() { Icon = "bi bi-box-seam", Title = "موجودی", Value = $"{kpis.CurrentStockQuantityMt:N4} MT", ToneClass = "journey-kpi-primary" },
            new() { Icon = "bi bi-exclamation-triangle", Title = "کسری", Value = $"{kpis.LossQuantityMt:N4} MT", ToneClass = "journey-kpi-danger" },
            new() { Icon = "bi bi-wallet2", Title = "هزینه", Value = $"{kpis.TotalExpensesUsd:N2} USD", ToneClass = "journey-kpi-warning" },
            new() { Icon = "bi bi-cash-stack", Title = "پرداخت", Value = $"{kpis.TotalPaymentsUsd:N2} USD", ToneClass = "journey-kpi-info" },
            new() { Icon = "bi bi-calculator", Title = "ماندهٔ قرارداد", Value = $"{kpis.RelatedBalanceUsd:N2} USD", ToneClass = "journey-kpi-primary" }
        };

    private static IReadOnlyList<ContractJourneyTimelineStepViewModel> BuildTimelineSteps(
        decimal contractQuantityMt,
        decimal loadedQuantityMt,
        decimal receivedQuantityMt,
        int dispatchCount,
        decimal dispatchedQuantityMt,
        int shipmentCount,
        decimal shipmentQuantityMt,
        int salesCount,
        decimal soldQuantityMt,
        decimal totalExpensesUsd,
        int paymentsCount,
        decimal totalPaymentsUsd,
        decimal currentStockQuantityMt,
        decimal grossMarginUsd,
        ContractJourneySectionStateViewModel dispatchState,
        ContractJourneySectionStateViewModel preSaleState,
        bool hasLoadingVesselData)
        => new List<ContractJourneyTimelineStepViewModel>
        {
            new() { Icon = "bi bi-file-earmark-text", Title = "قرارداد", Value = $"{contractQuantityMt:N4} MT", Description = "مبنای گزارش", BadgeText = "قطعی", BadgeClass = "status-badge status-badge-success" },
            new() { Icon = "bi bi-box-arrow-in-down", Title = "بارگیری", Value = $"{loadedQuantityMt:N4} MT", Description = loadedQuantityMt > 0m ? "ثبت مستقیم روی قرارداد" : "بدون بارگیری ثبت‌شده", BadgeText = "قطعی", BadgeClass = "status-badge status-badge-success" },
            new() { Icon = "bi bi-water", Title = "حمل دریایی", Value = shipmentCount > 0 ? $"{shipmentCount:N0} مورد" : hasLoadingVesselData ? "اطلاعات محدود" : "ثبت نشده", Description = shipmentCount > 0 ? $"{shipmentQuantityMt:N4} MT" : hasLoadingVesselData ? "اطلاعات از بارگیری موجود است." : "داده مستقیم ثبت نشده است.", BadgeText = shipmentCount > 0 || hasLoadingVesselData ? "قطعی" : "خالی", BadgeClass = shipmentCount > 0 || hasLoadingVesselData ? "status-badge status-badge-info" : "status-badge status-badge-neutral" },
            new() { Icon = "bi bi-truck", Title = "دیسپچ", Value = dispatchCount > 0 ? $"{dispatchedQuantityMt:N4} MT" : "نیاز به بررسی", Description = dispatchCount > 0 ? $"{dispatchCount:N0} مورد" : dispatchState.Message, BadgeText = dispatchState.BadgeText, BadgeClass = dispatchState.BadgeClass },
            new() { Icon = "bi bi-wallet2", Title = "هزینه", Value = $"{totalExpensesUsd:N2} USD", Description = "هزینه‌های قابل ردیابی", BadgeText = "قطعی", BadgeClass = "status-badge status-badge-warning" },
            new() { Icon = "bi bi-building-check", Title = "رسید", Value = $"{receivedQuantityMt:N4} MT", Description = "رسیدهای متصل به بارگیری", BadgeText = "قطعی", BadgeClass = "status-badge status-badge-success" },
            new() { Icon = "bi bi-box-seam", Title = "موجودی", Value = $"{currentStockQuantityMt:N4} MT", Description = "مانده فعلی قرارداد", BadgeText = "قطعی", BadgeClass = "status-badge status-badge-info" },
            new() { Icon = "bi bi-receipt", Title = "فروش", Value = salesCount > 0 ? $"{soldQuantityMt:N4} MT" : "0 MT", Description = salesCount > 0 ? $"{salesCount:N0} فروش ثبت‌شده" : "فروشی ثبت نشده است.", BadgeText = "قطعی", BadgeClass = "status-badge status-badge-success" },
            new() { Icon = "bi bi-journal-text", Title = "پیش‌فروش", Value = preSaleState.IsNeedsReview ? "نیاز به بررسی" : "ثبت شده", Description = preSaleState.Message ?? "پیش‌فروش ثبت شده است.", BadgeText = preSaleState.BadgeText, BadgeClass = preSaleState.BadgeClass },
            new() { Icon = "bi bi-cash-stack", Title = "پرداخت", Value = paymentsCount > 0 ? $"{totalPaymentsUsd:N2} USD" : "0 USD", Description = paymentsCount > 0 ? $"{paymentsCount:N0} تراکنش" : "پرداختی ثبت نشده است.", BadgeText = "قطعی", BadgeClass = "status-badge status-badge-info" },
            new() { Icon = "bi bi-graph-up-arrow", Title = "سود و زیان", Value = $"{grossMarginUsd:N2} USD", Description = "بر پایه فروش و هزینه ثبت‌شده", BadgeText = "محافظه‌کارانه", BadgeClass = "status-badge status-badge-neutral" }
        };

    private static IReadOnlyList<ContractJourneyQuantityFlowItemViewModel> BuildQuantityFlowItems(
        decimal contractQuantityMt,
        decimal loadedQuantityMt,
        decimal receivedQuantityMt,
        decimal dispatchedQuantityMt,
        decimal soldQuantityMt,
        decimal lossQuantityMt,
        decimal currentStockQuantityMt,
        decimal remainingToLoadMt,
        decimal remainingToReceiveMt)
        => new List<ContractJourneyQuantityFlowItemViewModel>
        {
            BuildQuantityFlowItem("مقدار قرارداد", contractQuantityMt, contractQuantityMt, "مقدار ثبت‌شده روی قرارداد.", "journey-progress-primary"),
            BuildQuantityFlowItem("بارگیری", loadedQuantityMt, contractQuantityMt, "بارگیری‌های مستقیم قرارداد.", "journey-progress-info"),
            BuildQuantityFlowItem("رسید", receivedQuantityMt, contractQuantityMt, "رسیدهای ثبت‌شده.", "journey-progress-success"),
            BuildQuantityFlowItem("فروش", soldQuantityMt, contractQuantityMt, "فروش‌های کاهنده موجودی.", "journey-progress-success"),
            BuildQuantityFlowItem("دیسپچ", dispatchedQuantityMt, contractQuantityMt, "دیسپچ‌های مستقیم قرارداد.", "journey-progress-info"),
            BuildQuantityFlowItem("کسری", lossQuantityMt, contractQuantityMt, "کسری‌های قابل ثبت.", "journey-progress-danger"),
            BuildQuantityFlowItem("موجودی", currentStockQuantityMt, contractQuantityMt, "مانده فعلی قرارداد.", "journey-progress-primary"),
            BuildQuantityFlowItem("باقی‌مانده بارگیری", remainingToLoadMt, contractQuantityMt, "بخش بارگیری‌نشده.", "journey-progress-neutral"),
            BuildQuantityFlowItem("باقی‌مانده رسید", remainingToReceiveMt, contractQuantityMt, "بخش رسیدنشده.", "journey-progress-warning")
        };

    private static ContractJourneyQuantityFlowItemViewModel BuildQuantityFlowItem(
        string label,
        decimal quantityMt,
        decimal contractQuantityMt,
        string helpText,
        string toneClass)
    {
        var percentage = contractQuantityMt <= 0m
            ? 0
            : (int)Math.Clamp(Math.Round((quantityMt / contractQuantityMt) * 100m, MidpointRounding.AwayFromZero), 0m, 100m);

        return new ContractJourneyQuantityFlowItemViewModel
        {
            Label = label,
            QuantityMt = quantityMt,
            DisplayValue = quantityMt.ToString("N4") + " MT",
            HelpText = helpText,
            ToneClass = toneClass,
            Percentage = percentage
        };
    }

    private static IReadOnlyList<string> BuildLoadingDocumentReferences(IEnumerable<LoadingRegister> loadingRegisters)
        => loadingRegisters
            .SelectMany(l => new[]
            {
                string.IsNullOrWhiteSpace(l.RwbNo) ? null : l.RwbNo,
                string.IsNullOrWhiteSpace(l.BillOfLadingNumber) ? null : l.BillOfLadingNumber,
                string.IsNullOrWhiteSpace(l.WagonNumber) ? null : l.WagonNumber,
                string.IsNullOrWhiteSpace(l.RouteDescription) ? null : l.RouteDescription
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .Take(10)
            .Cast<string>()
            .ToList();

    private static string? BuildLoadingDocumentSummary(LoadingRegister loadingRegister)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(loadingRegister.RwbNo))
        {
            parts.Add("RWB " + loadingRegister.RwbNo);
        }

        if (!string.IsNullOrWhiteSpace(loadingRegister.BillOfLadingNumber))
        {
            parts.Add("B/L " + loadingRegister.BillOfLadingNumber);
        }

        if (!string.IsNullOrWhiteSpace(loadingRegister.WagonNumber))
        {
            parts.Add("Wagon/CMR " + loadingRegister.WagonNumber);
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    // «نمبر وسیله»: پلاک موتر، شمارهٔ واگن یا شمارهٔ بارنامه — هرکدام که روی همان رکورد ثبت شده.
    // فقط نمایش است؛ هیچ عددی از این مسیر در محاسبه وارد نمی‌شود.
    private static string? BuildVehicleNumber(LoadingRegister? loading)
        => loading is null
            ? null
            : NullIfEmpty(loading.Truck?.PlateNumber, loading.WagonNumber, loading.RwbNo, loading.BillOfLadingNumber);

    private static string? BuildVehicleNumber(InventoryTransportLeg? leg)
        => leg is null
            ? null
            : NullIfEmpty(leg.Truck?.PlateNumber, leg.WagonNumber, leg.RwbNo, leg.BillOfLadingNumber);

    private static string? BuildVehicleNumber(TruckDispatch? dispatch)
        => dispatch is null ? null : NullIfEmpty(dispatch.Truck?.PlateNumber);

    private static string? NullIfEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildTransportLegReference(InventoryTransportLeg leg)
        => FirstNonEmpty(
            leg.WagonNumber,
            leg.BillOfLadingNumber,
            leg.RwbNo,
            $"#{leg.Id}");

    private static string BuildTransportLegDestination(InventoryTransportLeg leg)
        => FirstNonEmpty(
            BuildTankLocation(leg.DestinationTerminal?.Name, StorageTankDisplay.BuildOptional(leg.DestinationStorageTank)),
            leg.DestinationLocation?.Name,
            leg.RouteDescription,
            "-");

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

    private static IReadOnlyList<ContractJourneyActivityItemViewModel> BuildActivityItems(
        IReadOnlyList<ContractJourneyLoadingItemViewModel> loadingItems,
        IReadOnlyList<ContractJourneyReceiptItemViewModel> receiptItems,
        IReadOnlyList<ContractJourneyDispatchItemViewModel> dispatchItems,
        IReadOnlyList<ContractJourneySaleItemViewModel> saleItems,
        IReadOnlyList<ContractJourneyExpenseItemViewModel> expenseItems,
        IReadOnlyList<ContractJourneyPaymentItemViewModel> paymentItems,
        IReadOnlyList<ContractJourneyLedgerItemViewModel> ledgerItems)
    {
        var items = new List<ContractJourneyActivityItemViewModel>();

        items.AddRange(loadingItems.Select(l => new ContractJourneyActivityItemViewModel
        {
            Date = l.LoadingDate,
            Title = "بارگیری",
            Description = $"{l.LoadedQuantityMt:N4} MT" + (string.IsNullOrWhiteSpace(l.DocumentSummary) ? string.Empty : " | " + l.DocumentSummary),
            Icon = "bi bi-train-freight-front",
            ToneClass = "journey-activity-info"
        }));
        items.AddRange(receiptItems.Select(r => new ContractJourneyActivityItemViewModel
        {
            Date = r.ReceiptDate,
            Title = "رسید / تخلیه",
            Description = $"{r.ReceivedQuantityMt:N4} MT | {r.TerminalName}",
            Icon = "bi bi-box-arrow-in-down",
            ToneClass = "journey-activity-success"
        }));
        items.AddRange(dispatchItems.Select(d => new ContractJourneyActivityItemViewModel
        {
            Date = d.DispatchDate,
            Title = "Dispatch",
            Description = $"{d.LoadedQuantityMt:N4} MT | {d.TruckPlateNumber}",
            Icon = "bi bi-truck",
            ToneClass = "journey-activity-info"
        }));
        items.AddRange(saleItems.Select(s => new ContractJourneyActivityItemViewModel
        {
            Date = s.SaleDate,
            Title = "فروش",
            Description = $"{s.QuantityMt:N4} MT | {s.AmountUsd:N2} USD | {s.InvoiceNumber}",
            Icon = "bi bi-receipt",
            ToneClass = "journey-activity-success"
        }));
        items.AddRange(expenseItems.Select(e => new ContractJourneyActivityItemViewModel
        {
            Date = e.ExpenseDate,
            Title = "هزینه",
            Description = $"{e.AmountUsd:N2} USD | {e.ExpenseTypeName}",
            Icon = "bi bi-wallet2",
            ToneClass = "journey-activity-warning"
        }));
        items.AddRange(paymentItems.Select(p => new ContractJourneyActivityItemViewModel
        {
            Date = p.PaymentDate,
            Title = p.DirectionName,
            Description = $"{p.AmountUsd:N2} USD | {p.PaymentKindName}",
            Icon = "bi bi-cash-stack",
            ToneClass = "journey-activity-primary"
        }));
        items.AddRange(ledgerItems.Select(l => new ContractJourneyActivityItemViewModel
        {
            Date = l.EntryDate,
            Title = "Ledger",
            Description = $"{l.SideName} | {l.AmountUsd:N2} USD | {l.SourceType}",
            Icon = "bi bi-journal-richtext",
            ToneClass = "journey-activity-neutral"
        }));

        return items
            .OrderByDescending(i => i.Date)
            .ThenBy(i => i.Title)
            .Take(12)
            .ToList();
    }

    private static string ResolveExpenseTraceKind(
        ExpenseTransaction expense,
        int contractId,
        HashSet<int> shipmentIds,
        HashSet<int> dispatchIds,
        IReadOnlyCollection<int> inventoryTransportLegIds)
    {
        if (expense.TransportLegId.HasValue && inventoryTransportLegIds.Contains(expense.TransportLegId.Value))
        {
            return "از مسیر انتقال از موجودی";
        }

        if (expense.ContractId == contractId)
        {
            return "مستقیم از قرارداد";
        }

        if (expense.ShipmentId.HasValue && shipmentIds.Contains(expense.ShipmentId.Value))
        {
            return expense.ContractId.HasValue && expense.ContractId != contractId
                ? "نیاز به بررسی / محموله با قرارداد متفاوت"
                : "از مسیر محموله";
        }

        if (expense.TruckDispatchId.HasValue && dispatchIds.Contains(expense.TruckDispatchId.Value))
        {
            return expense.ContractId.HasValue && expense.ContractId != contractId
                ? "نیاز به بررسی / دیسپچ با قرارداد متفاوت"
                : "از مسیر دیسپچ";
        }

        return "نیاز به بررسی";
    }

    private static string ResolveLossTraceKind(
        LossEvent lossEvent,
        int contractId,
        HashSet<int> shipmentIds,
        HashSet<int> dispatchIds,
        IReadOnlyCollection<int> loadingIds,
        IReadOnlyCollection<int> receiptIds,
        IReadOnlyCollection<int> movementIds,
        IReadOnlyCollection<int> inventoryTransportLegIds)
    {
        if (lossEvent.ContractId == contractId)
        {
            return "مستقیم از قرارداد";
        }

        if (lossEvent.InventoryMovementId.HasValue && movementIds.Contains(lossEvent.InventoryMovementId.Value))
        {
            return "از مسیر سند موجودی";
        }

        if (lossEvent.TransportLegId.HasValue && inventoryTransportLegIds.Contains(lossEvent.TransportLegId.Value))
        {
            return "از مسیر انتقال از موجودی";
        }

        if (lossEvent.LoadingRegisterId.HasValue && loadingIds.Contains(lossEvent.LoadingRegisterId.Value))
        {
            return "از مسیر بارگیری";
        }

        if (lossEvent.LoadingReceiptId.HasValue && receiptIds.Contains(lossEvent.LoadingReceiptId.Value))
        {
            return "از مسیر رسید";
        }

        if (lossEvent.TruckDispatchId.HasValue && dispatchIds.Contains(lossEvent.TruckDispatchId.Value))
        {
            return "از مسیر دیسپچ";
        }

        if (lossEvent.ShipmentId.HasValue && shipmentIds.Contains(lossEvent.ShipmentId.Value))
        {
            return "از مسیر محموله";
        }

        return "نیاز به بررسی";
    }

    private static string ResolvePaymentTraceKind(
        PaymentTransaction payment,
        int contractId,
        HashSet<int> saleIds,
        HashSet<int> expenseIds,
        HashSet<int> shipmentIds,
        HashSet<int> dispatchIds)
    {
        if (payment.ContractId == contractId)
        {
            return "مستقیم از قرارداد";
        }

        if (payment.SalesTransactionId.HasValue && saleIds.Contains(payment.SalesTransactionId.Value))
        {
            return "از مسیر فروش";
        }

        if (payment.ExpenseTransactionId.HasValue && expenseIds.Contains(payment.ExpenseTransactionId.Value))
        {
            return "از مسیر هزینه";
        }

        if (payment.ShipmentId.HasValue && shipmentIds.Contains(payment.ShipmentId.Value))
        {
            return "از مسیر محموله";
        }

        if (payment.TruckDispatchId.HasValue && dispatchIds.Contains(payment.TruckDispatchId.Value))
        {
            return "از مسیر دیسپچ";
        }

        return "نیاز به بررسی";
    }

    private static string ResolveLedgerTraceKind(
        LedgerEntry ledgerEntry,
        int contractId,
        HashSet<int> saleIds,
        HashSet<int> expenseIds,
        HashSet<int> paymentIds,
        HashSet<int> shipmentIds)
    {
        if (ledgerEntry.ContractId == contractId)
        {
            return "مستقیم از قرارداد";
        }

        if (ledgerEntry.SourceType == "Sale" && saleIds.Contains(ledgerEntry.SourceId))
        {
            return "از مسیر فروش";
        }

        if (ledgerEntry.SourceType == "Expense" && expenseIds.Contains(ledgerEntry.SourceId))
        {
            return "از مسیر هزینه";
        }

        if (PaymentLedgerSourceTypes.Contains(ledgerEntry.SourceType) && paymentIds.Contains(ledgerEntry.SourceId))
        {
            return "از مسیر پرداخت";
        }

        if (ledgerEntry.ShipmentId.HasValue && shipmentIds.Contains(ledgerEntry.ShipmentId.Value))
        {
            return "از مسیر محموله";
        }

        return "نیاز به بررسی";
    }

    private static decimal ToSignedQuantity(MovementDirection direction, decimal quantityMt) => direction switch
    {
        MovementDirection.In => quantityMt,
        MovementDirection.Adjustment => quantityMt,
        MovementDirection.Out => -quantityMt,
        MovementDirection.Transfer => -quantityMt,
        _ => 0m
    };

    private static bool HasValidLoadingPrice(decimal? loadingPriceUsd)
        => loadingPriceUsd.HasValue && loadingPriceUsd.Value > 0m;

    private static ContractJourneyRubSettlementSummaryViewModel BuildRubSettlementSummary(
        Contract contract,
        IEnumerable<LoadingRegister> loadingRegisters,
        decimal? contractFinalPriceUsd)
    {
        var settlementCurrency = SystemCurrency.Normalize(contract.SettlementCurrencyCode);
        var loadings = loadingRegisters.ToList();

        // ارزش روبلی بارگیری‌ها از منبع مشترکِ LoadingRubValuation — همان منبعی که پروفایل
        // تأمین‌کننده استفاده می‌کند. پیش از early return محاسبه می‌شود، چون رقم روبلیِ فایل
        // بارگیری به SettlementCurrencyCode قرارداد وابسته نیست.
        var loadingRubTotal = LoadingRubValuation.AggregateForContract(
            loadings.Select(ToLoadingRubFacts),
            contractFinalPriceUsd,
            LoadingRubValuation.IsRubCurrency(contract.Currency) ? contract.UnitPriceInCurrency : null,
            loadings.Sum(l => l.LoadedQuantityMt),
            loadings.Sum(l => CurrentLoadingUsd(l) ?? 0m),
            LoadingRubValuation.ContractRubPerUsdRate(
                contract.Currency,
                contract.ContractRubPerUsdRate,
                contract.AppliedFxRateToUsd));

        if (!string.Equals(settlementCurrency, "RUB", StringComparison.OrdinalIgnoreCase))
        {
            return new ContractJourneyRubSettlementSummaryViewModel
            {
                SettlementCurrencyCode = settlementCurrency,
                RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
                LoadingValueRub = loadingRubTotal.AmountRub,
                LoadingValueRubIsEstimated = loadingRubTotal.IsEstimated
            };
        }

        var locked = loadings
            .Where(l => l.RubRateStatus == RubSettlementRateStatus.Locked
                && l.AmountUsdAtRubLock.HasValue
                && l.AmountRubAtRubLock.HasValue)
            .ToList();
        var pending = loadings
            .Where(l => l.RubRateStatus != RubSettlementRateStatus.Locked
                || !l.AmountUsdAtRubLock.HasValue
                || !l.AmountRubAtRubLock.HasValue)
            .ToList();

        decimal? CurrentLoadingUsd(LoadingRegister loading)
        {
            var price = HasValidLoadingPrice(loading.LoadingPriceUsd)
                ? loading.LoadingPriceUsd
                : contractFinalPriceUsd;

            return HasValidLoadingPrice(price)
                ? Math.Round(loading.LoadedQuantityMt * price!.Value, 4, MidpointRounding.AwayFromZero)
                : null;
        }

        return new ContractJourneyRubSettlementSummaryViewModel
        {
            SettlementCurrencyCode = settlementCurrency,
            RubRatePolicy = contract.RubRatePolicy,
            ContractRubPerUsdRate = contract.ContractRubPerUsdRate,
            ContractRubRateDate = contract.ContractRubRateDate,
            ContractRubRateSource = contract.ContractRubRateSource,
            // نرخ روبل قفل می‌ماند، اما مبلغ با ارزش زندهٔ دالری بازمحاسبه می‌شود (دنبال‌کردن تغییر قیمت قرارداد).
            LockedAmountUsd = locked.Sum(l => CurrentLoadingUsd(l) ?? 0m),
            LockedAmountRub = locked.Sum(l => Math.Round((CurrentLoadingUsd(l) ?? 0m) * (l.RubPerUsdRate ?? 0m), 2, MidpointRounding.AwayFromZero)),
            PendingAmountUsd = pending.Sum(l => CurrentLoadingUsd(l) ?? 0m),
            PendingQuantityMt = pending.Sum(l => l.LoadedQuantityMt),
            LockedLoadingCount = locked.Count,
            PendingRateLoadingCount = pending.Count,
            LoadingValueRub = loadingRubTotal.AmountRub,
            LoadingValueRubIsEstimated = loadingRubTotal.IsEstimated
        };
    }

    private static LoadingRubFacts ToLoadingRubFacts(LoadingRegister loading)
        => new(
            loading.LoadedQuantityMt,
            loading.LoadingPriceUsd,
            loading.RubRateStatus,
            loading.RubPerUsdRate,
            loading.AmountRubAtRubLock,
            loading.SettlementUnitPriceRub,
            loading.SettlementValueRub);

    private static string ToMovementDirectionName(MovementDirection direction) => direction switch
    {
        MovementDirection.In => "ورود",
        MovementDirection.Out => "خروج",
        MovementDirection.Transfer => "انتقال",
        MovementDirection.Adjustment => "تعدیل",
        _ => direction.ToString()
    };

    private static string ToLoadingReceiptAllocationDestinationName(LoadingReceiptAllocationDestination destination)
        => destination switch
        {
            LoadingReceiptAllocationDestination.ToInventory => "ورود به موجودی",
            LoadingReceiptAllocationDestination.DirectSale => "فروش مستقیم",
            LoadingReceiptAllocationDestination.DirectDispatchToTruck => "دیسپچ مستقیم به موتر",
            LoadingReceiptAllocationDestination.TransferToOtherTerminal => "انتقال به ترمینال دیگر",
            _ => destination.ToString()
        };

    private static string ToLoadingReceiptAllocationStatusName(LoadingReceiptAllocationStatus status)
        => status switch
        {
            LoadingReceiptAllocationStatus.TraceOnly => "در حال ردیابی",
            LoadingReceiptAllocationStatus.InTransit => "در مسیر",
            LoadingReceiptAllocationStatus.Completed => "تکمیل‌شده",
            LoadingReceiptAllocationStatus.Cancelled => "لغوشده",
            _ => status.ToString()
        };

    private static string ToContractTypeName(ContractType contractType) => contractType switch
    {
        ContractType.Purchase => "خرید",
        ContractType.Sale => "فروش",
        _ => contractType.ToString()
    };

    private static string ToContractStatusName(ContractStatus status) => status switch
    {
        ContractStatus.Draft => "پیش‌نویس",
        ContractStatus.Active => "فعال",
        ContractStatus.Closed => "بسته‌شده",
        ContractStatus.Cancelled => "لغوشده",
        _ => status.ToString()
    };

    private static string ToContractStatusBadgeClass(ContractStatus status) => status switch
    {
        ContractStatus.Draft => "status-badge status-badge-neutral",
        ContractStatus.Active => "status-badge status-badge-success",
        ContractStatus.Closed => "status-badge status-badge-dark",
        ContractStatus.Cancelled => "status-badge status-badge-danger",
        _ => "status-badge status-badge-neutral"
    };

    private async Task<HashSet<int>> LoadLoadingIdsWithOfficialExpensesAsync(IReadOnlyCollection<int> loadingIds)
    {
        if (loadingIds.Count == 0)
        {
            return [];
        }

        // مصرفِ ساخته‌شده از اظهارنامهٔ گمرکی هم LoadingRegisterId می‌گیرد، ولی آینهٔ فیلدهای
        // درون‌خطیِ حمل/گدام/سایر/خط‌آهن نیست؛ اگر اینجا شمرده شود آن مصارف واقعی حذف
        // می‌شوند و مصرف کمتر از واقع می‌آید. رجوع: PurchaseAggregationService.
        var ids = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && e.LoadingRegisterId.HasValue
                && loadingIds.Contains(e.LoadingRegisterId.Value))
            .Select(e => e.LoadingRegisterId!.Value)
            .Distinct()
            .ToListAsync();

        return ids.ToHashSet();
    }

    private async Task<HashSet<int>> LoadLoadingIdsWithExpenseLinesAsync(IReadOnlyCollection<int> loadingIds)
    {
        if (loadingIds.Count == 0)
        {
            return [];
        }

        var ids = await _db.LoadingExpenseLines
            .AsNoTracking()
            .Where(l => loadingIds.Contains(l.LoadingRegisterId))
            .Select(l => l.LoadingRegisterId)
            .Distinct()
            .ToListAsync();

        return ids.ToHashSet();
    }

    private static string ToPricingMethodName(Contract contract)
        => ContractPricingAdapter.GetPricingDisplayLabel(contract);

    private static string ToResolvedPriceDisplay(Contract contract, ContractPriceResult pricingResult)
        => pricingResult.FinalUnitPrice.HasValue
            ? $"{pricingResult.FinalUnitPrice.Value:N2} USD/MT"
            : ToPriceDisplay(contract);

    private static string ToPriceDisplay(Contract contract)
        => ContractPricingAdapter.FormatPrice(ContractPricingAdapter.GetCanonicalFinalPrice(contract));

    private static string ToLossStageName(LossEventStage stage) => stage switch
    {
        LossEventStage.LoadingDifference => "اختلاف بارگیری",
        LossEventStage.TransitLoss => "ضایعات مسیر",
        LossEventStage.ReceiptShortage => "کسری در رسید",
        LossEventStage.TankNaturalLoss => "افت طبیعی مخزن",
        LossEventStage.DispatchShortage => "کسری دیسپچ",
        LossEventStage.CustomsLoss => "کسری گمرکی",
        LossEventStage.SalesDifference => "اختلاف فروش",
        LossEventStage.ManualAdjustment => "تعدیل دستی",
        _ => stage.ToString()
    };

    private static string ToPaymentDirectionName(PaymentDirection direction) => direction switch
    {
        PaymentDirection.In => "دریافت",
        PaymentDirection.Out => "پرداخت",
        _ => direction.ToString()
    };

    private static string ToPaymentKindName(PaymentKind paymentKind) => paymentKind switch
    {
        PaymentKind.CustomerReceipt => "دریافت از مشتری",
        PaymentKind.SupplierPayment => "پرداخت به تأمین‌کننده",
        PaymentKind.SupplierReceipt => "دریافت از تأمین‌کننده",
        PaymentKind.CustomerPayment => "پرداخت به مشتری",
        PaymentKind.ExpensePayment => "پرداخت هزینه",
        PaymentKind.TruckPayment => "پرداخت موتر",
        PaymentKind.ManualPayment => "پرداخت دستی",
        PaymentKind.ManualReceipt => "دریافت دستی",
        PaymentKind.EmployeeSalaryPayment => "پرداخت معاش کارمند",
        PaymentKind.EmployeeSalaryAdvance => "برداشت معاش کارمند",
        PaymentKind.EmployeeReturn => "برگشت از کارمند",
        _ => paymentKind.ToString()
    };

    private static string ToLoadingTransportTypeName(LoadingTransportType transportType) => transportType switch
    {
        LoadingTransportType.Vessel => "کشتی",
        LoadingTransportType.Wagon => "واگون",
        LoadingTransportType.Truck => "موتر",
        _ => "نامشخص"
    };

    private static string ToStockSourceTypeName(StockSourceType stockSourceType) => stockSourceType switch
    {
        StockSourceType.Wagon => "واگون",
        StockSourceType.Stock => "Stock",
        StockSourceType.Tank => "تانک",
        _ => stockSourceType.ToString()
    };

    private static string? BuildVehicleSummary(string? vesselName, string? truckPlateNumber)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(vesselName))
        {
            parts.Add("کشتی: " + vesselName);
        }

        if (!string.IsNullOrWhiteSpace(truckPlateNumber))
        {
            parts.Add("موتر: " + truckPlateNumber);
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }
}
