using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class InventoryTransportReceiptsControllerTests
{
    [Fact]
    public async Task Create_Get_Uses_Localized_ReceiptDestination_Options()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Create(leg.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<InventoryTransportReceiptCreateViewModel>(view.Model);
        var options = Assert.IsAssignableFrom<IEnumerable<SelectListItem>>((object)controller.ViewBag.ReceiptDestinations);
        Assert.Contains(options, option => option.Text == "ورود به موجودی");
        Assert.DoesNotContain(options, option => option.Text == InventoryTransportReceiptDestination.ToInventory.ToString());
    }

    [Fact]
    public async Task Create_Get_Preselects_DirectSale_When_Requested_From_Transport_Details()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Create(
            leg.Id,
            InventoryTransportReceiptDestination.DirectSale);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportReceiptCreateViewModel>(view.Model);
        Assert.Equal(InventoryTransportReceiptDestination.DirectSale, model.ReceiptDestination);
    }

    [Fact]
    public async Task Create_Get_Uses_Focused_Inventory_Form_When_Opened_From_Transport_Details()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Create(
            leg.Id,
            InventoryTransportReceiptDestination.ToInventory,
            focused: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportReceiptCreateViewModel>(view.Model);
        Assert.Equal(InventoryTransportReceiptDestination.ToInventory, model.ReceiptDestination);
        Assert.True(Assert.IsType<bool>(controller.ViewData["FocusedInventoryReceipt"]));
        Assert.Equal(30m, Assert.IsType<decimal>(controller.ViewData["RemainingMt"]));
    }

    [Fact]
    public async Task Create_ToInventory_Creates_InventoryMovement_In_At_Destination()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            Notes = "Destination receipt"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(leg.Id, receipt.InventoryTransportLegId);
        Assert.Equal(InventoryTransportReceiptDestination.ToInventory, receipt.ReceiptDestination);
        Assert.NotNull(receipt.InventoryMovementId);

        var movement = await db.InventoryMovements.SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal(28m, movement.QuantityMt);
        Assert.Equal(1, movement.ContractId);
        Assert.Equal(1, movement.ProductId);
        Assert.Equal(2, movement.TerminalId);
        Assert.Equal(2, movement.StorageTankId);

        var reloadedLeg = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.Equal(InventoryTransportLegStatus.Received, reloadedLeg.Status);
    }

    [Fact]
    public async Task Create_ToInventory_With_Shortage_Creates_TransportLeg_LossEvent()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        var loss = await db.LossEvents.SingleAsync();
        Assert.Equal(leg.Id, loss.TransportLegId);
        Assert.Equal(1, loss.ContractId);
        Assert.Equal(1, loss.ProductId);
        Assert.Equal(LossEventStage.ReceiptShortage, loss.Stage);
        Assert.Equal(30m, loss.ExpectedQuantityMt);
        Assert.Equal(28m, loss.ActualQuantityMt);
        Assert.Equal(2m, loss.ChargeableLossMt);
        Assert.False(loss.AffectsInventory);
        Assert.Contains("TRANSPORT-RECEIPT:", loss.Reference);
    }

    [Fact]
    public async Task Create_ToInventory_Increases_Destination_Stock()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var stock = new StockService(db);
        var before = await stock.GetFreeQuantityMtAsync(1, terminalId: 2, contractId: 1, storageTankId: 2);
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        var after = await stock.GetFreeQuantityMtAsync(1, terminalId: 2, contractId: 1, storageTankId: 2);
        Assert.Equal(0m, before);
        Assert.Equal(28m, after);
    }

    [Fact]
    public async Task Create_ToInventory_With_ServiceProvider_Freight_Posts_Profile_Ledger_And_Avoids_Pnl_Double_Count()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 1,
            Code = "RAIL-1",
            Name = "Railway Services",
            ProviderType = ServiceProviderType.RailwayService,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        leg.TransportType = LoadingTransportType.Truck;
        leg.WagonNumber = null;
        leg.BillOfLadingNumber = "TRUCK-LEG-1";
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 30m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightCostUsd = 140m,
            ServiceProviderId = 1
        });

        Assert.IsType<RedirectToActionResult>(result);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(1, receipt.ServiceProviderId);
        Assert.Equal(140m, receipt.FreightCostUsd);
        var reloadedLeg = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.True(reloadedLeg.IsFreightSettled);
        Assert.Equal(new DateTime(2026, 5, 5), reloadedLeg.FreightSettledDate);

        var expense = await db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .SingleAsync();
        Assert.Equal("TRANSPORT-RECEIPT-FREIGHT", expense.ExpenseType?.Code);
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Equal(leg.Id, expense.TransportLegId);
        Assert.Equal(140m, expense.AmountUsd);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Equal(140m, ledger.AmountUsd);

        var profile = Assert.IsType<ViewResult>(await new ServiceProvidersController(db).Details(1));
        var profileModel = Assert.IsType<PTGOilSystem.Web.Models.ServiceProviders.ServiceProviderProfileViewModel>(profile.Model);
        Assert.Equal(140m, profileModel.TotalExpensesUsd);
        var profileExpense = profileModel.Expenses.Single();
        Assert.Equal(140m, profileExpense.AmountUsd);
        Assert.Equal("#1 - Truck", profileExpense.TransportLegLabel);

        var pnl = await new InventoryTransportPnlService(db).BuildForLegsAsync([leg.Id]);
        var snapshot = pnl[leg.Id];
        Assert.Equal(0m, snapshot.ExpenseTransactionsUsd);
        Assert.Equal(140m, snapshot.ReceiptFreightExpenseUsd);
        Assert.Equal(140m, snapshot.OperationalExpensesUsd);
    }

    [Fact]
    public async Task Create_Partial_Receipt_With_Freight_Keeps_Remaining_Freight_Open()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 1,
            Code = "PARTIAL-CARRIER",
            Name = "Partial Carrier",
            ProviderType = ServiceProviderType.TransportCompany,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        leg.TransportType = LoadingTransportType.Truck;
        await db.SaveChangesAsync();

        var result = await BuildController(db).Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 10m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightCostUsd = 50m,
            ServiceProviderId = 1
        });

        Assert.IsType<RedirectToActionResult>(result);
        var reloadedLeg = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.NotEqual(InventoryTransportLegStatus.Received, reloadedLeg.Status);
        Assert.False(reloadedLeg.IsFreightSettled);
        Assert.Null(reloadedLeg.FreightSettledDate);
    }

    [Fact]
    public async Task Create_ToInventory_Does_Not_Change_PurchaseAggregation()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedPurchaseLoadingAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var aggregation = new PurchaseAggregationService(db);
        var before = await aggregation.AggregateForContractAsync(1, contractFinalPriceUsd: null);
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        var after = await aggregation.AggregateForContractAsync(1, contractFinalPriceUsd: null);
        Assert.Equal(before.TotalLoadedQuantityMt, after.TotalLoadedQuantityMt);
        Assert.Equal(before.TraceablePurchaseCostUsd, after.TraceablePurchaseCostUsd);
        Assert.Equal(10m, after.TotalLoadedQuantityMt);
        Assert.Equal(1_000m, after.TraceablePurchaseCostUsd);
    }

    [Fact]
    public async Task Create_Accepts_ReceivedQuantity_Greater_Than_Leg_Quantity_As_Surplus()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        // ترازوی مقصد بیشتر از مبدأ خوانده است؛ ثبت تخلیهٔ واقعی مسدود نمی‌شود.
        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 31m,
            ShortageQuantityMt = -1m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(31m, receipt.ReceivedQuantityMt);
        Assert.Equal(-1m, receipt.ShortageQuantityMt);
        Assert.Equal(0m, receipt.ChargeableShortageMt);

        var movement = await db.InventoryMovements
            .SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(31m, movement.QuantityMt);
    }

    /// <summary>
    /// سه سناریوی پایهٔ تخلیه، دقیقاً همان‌طور که کاربر انتظار دارد:
    /// ۱۰→۱۰ عادی، ۱۰→۱۰٫۱ مازاد، ۱۰→۹٫۹ کسری.
    /// موجودی مقصد همیشه به‌اندازهٔ تخلیهٔ واقعی زیاد می‌شود و مازاد هرگز جریمه نمی‌شود.
    /// این تست منطق را تغییر نمی‌دهد؛ فقط رفتار فعلی را قفل می‌کند.
    /// </summary>
    // decimal در attribute مجاز نیست، پس مقادیر به‌صورت رشته می‌آیند و دقیق parse می‌شوند.
    [Theory]
    [InlineData("10", "10", "0")]      // عادی
    [InlineData("10", "10.1", "-0.1")] // مازاد
    [InlineData("10", "9.9", "0.1")]   // کسری
    public async Task Unload_Records_The_Real_Quantity_For_Normal_Surplus_And_Shortage(
        string loadedText,
        string unloadedText,
        string shortageText)
    {
        var loadedMt = decimal.Parse(loadedText, System.Globalization.CultureInfo.InvariantCulture);
        var unloadedMt = decimal.Parse(unloadedText, System.Globalization.CultureInfo.InvariantCulture);
        var expectedShortageMt = decimal.Parse(shortageText, System.Globalization.CultureInfo.InvariantCulture);

        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: loadedMt);
        var stock = new StockService(db);
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = unloadedMt,
            ShortageQuantityMt = expectedShortageMt,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(unloadedMt, receipt.ReceivedQuantityMt);
        Assert.Equal(expectedShortageMt, receipt.ShortageQuantityMt);

        // مازاد (عدد منفی) هرگز به کسریِ قابل‌شارژ تبدیل نمی‌شود.
        Assert.Equal(Math.Max(0m, expectedShortageMt), receipt.ChargeableShortageMt);

        // موجودی مقصد دقیقاً به اندازهٔ تخلیهٔ واقعی زیاد می‌شود، نه مقدار بارگیری.
        var movement = await db.InventoryMovements
            .SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(unloadedMt, movement.QuantityMt);
        Assert.Equal(
            unloadedMt,
            await stock.GetFreeQuantityMtAsync(1, terminalId: 2, contractId: 1, storageTankId: 2));

        // اختلاف (چه کسری چه مازاد) به‌عنوان رویداد ثبت می‌شود تا ردیابی شود؛ ولی
        // مازاد هیچ‌وقت مبلغ قابل‌شارژ ندارد. تخلیهٔ برابر هیچ رویدادی نمی‌سازد.
        var losses = await db.LossEvents.ToListAsync();
        if (expectedShortageMt == 0m)
        {
            Assert.Empty(losses);
        }
        else
        {
            var loss = Assert.Single(losses);
            Assert.Equal(LossEventStage.ReceiptShortage, loss.Stage);
            Assert.Equal(expectedShortageMt, loss.DifferenceQuantityMt);
            Assert.Equal(expectedShortageMt > 0m ? expectedShortageMt : 0m, loss.ChargeableLossMt);
        }
    }

    [Fact]
    public async Task Create_DirectSale_Creates_Sale_And_Ledger_Without_InventoryMovement()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var beforeMovements = await db.InventoryMovements.CountAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            ShortageQuantityMt = 2m,
            SaleCustomerId = 1,
            SaleInvoiceNumber = "INV-TRANSPORT-001",
            SaleDate = new DateTime(2026, 5, 6),
            SaleCurrency = "USD",
            SaleUnitPriceInCurrency = 750m
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(InventoryTransportReceiptDestination.DirectSale, receipt.ReceiptDestination);
        Assert.Null(receipt.InventoryMovementId);
        Assert.NotNull(receipt.SalesTransactionId);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(receipt.SalesTransactionId, sale.Id);
        Assert.Null(sale.ContractId);
        Assert.Equal(1, sale.CompanyId);
        Assert.Equal(1, sale.CustomerId);
        Assert.Equal(1, sale.ProductId);
        Assert.Equal(SaleStage.InTransit, sale.SaleStage);
        Assert.Equal("INV-TRANSPORT-001", sale.InvoiceNumber);
        Assert.Equal(28m, sale.QuantityMt);
        Assert.Equal(750m, sale.UnitPriceUsd);
        Assert.Equal(21_000m, sale.TotalUsd);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(21_000m, ledger.AmountUsd);
        Assert.Equal(1, ledger.ContractId);
        Assert.Equal(1, ledger.CustomerId);

        var loss = await db.LossEvents.SingleAsync();
        Assert.Equal(leg.Id, loss.TransportLegId);
        Assert.Equal(2m, loss.ChargeableLossMt);

        var reloadedLeg = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.Equal(InventoryTransportLegStatus.Received, reloadedLeg.Status);
    }

    [Fact]
    public async Task Create_DirectSale_With_Pending_Price_Delivers_Without_Ledger()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);

        var result = await BuildController(db).Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 30m,
            SaleCustomerId = 1,
            SaleInvoiceNumber = "INV-TRANSPORT-PENDING",
            SaleDate = new DateTime(2026, 5, 6),
            SaleCurrency = "USD",
            SaleIsPricePending = true
        });

        Assert.IsType<RedirectToActionResult>(result);
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.True(sale.IsSalePricePending);
        Assert.Equal(30m, sale.QuantityMt);
        Assert.Equal(0m, sale.TotalUsd);
        Assert.Empty(await db.LedgerEntries.Where(l => l.SourceType == "Sale").ToListAsync());
        Assert.Equal(sale.Id, (await db.InventoryTransportReceipts.SingleAsync()).SalesTransactionId);
        Assert.Equal(InventoryTransportLegStatus.Received, (await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id)).Status);
    }

    [Fact]
    public async Task Create_DirectSale_Propagates_TransportLeg_Shipment_To_Sale_Ledger_And_Loss()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "KALUGA", QuantityMt = 30m });
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        leg.ShipmentId = 1;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            ShortageQuantityMt = 2m,
            SaleCustomerId = 1,
            SaleInvoiceNumber = "INV-KALUGA-001",
            SaleDate = new DateTime(2026, 5, 6),
            SaleCurrency = "USD",
            SaleUnitPriceInCurrency = 750m
        });

        var sale = await db.SalesTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync();
        var loss = await db.LossEvents.SingleAsync();

        Assert.Equal(1, sale.ShipmentId);
        Assert.Equal(1, ledger.ShipmentId);
        Assert.Equal(1, loss.ShipmentId);
    }

    [Fact]
    public async Task Create_DirectSale_Rejects_When_Sale_Fields_Are_Missing()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var beforeMovements = await db.InventoryMovements.CountAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Create_DirectDispatch_Redirects_To_Unified_Continue_Without_Writes()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var beforeMovements = await db.InventoryMovements.CountAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectDispatch,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            ShortageQuantityMt = 2m,
            DirectDispatchTruckId = 1,
            DirectDispatchDriverId = 1,
            DirectDispatchDate = new DateTime(2026, 5, 6),
            DirectDispatchLoadedQuantityMt = 28m,
            DirectDispatchDestinationLocationId = 1,
            DirectDispatchTicketSerialNumber = "TD-001"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Continue", redirect.ActionName);
        Assert.Equal("Transports", redirect.ControllerName);
        Assert.Equal(leg.Id, redirect.RouteValues!["transportLegId"]);
        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Empty(await db.TruckDispatches.ToListAsync());
        Assert.Empty(await db.LossEvents.ToListAsync());
    }

    [Fact]
    public async Task Create_DirectDispatch_Legacy_Post_Does_Not_Bypass_Unified_Quantity_Engine()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 30m);
        var beforeMovements = await db.InventoryMovements.CountAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectDispatch,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 28m,
            ShortageQuantityMt = 2m,
            DirectDispatchTruckId = 1,
            DirectDispatchDriverId = 1,
            DirectDispatchDate = new DateTime(2026, 5, 6),
            DirectDispatchLoadedQuantityMt = 20m,
            DirectDispatchDestinationLocationId = 1,
            DirectDispatchTicketSerialNumber = "TD-002"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Continue", redirect.ActionName);
        Assert.Equal("Transports", redirect.ControllerName);
        Assert.Equal(leg.Id, redirect.RouteValues!["transportLegId"]);
        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Empty(await db.TruckDispatches.ToListAsync());
        Assert.Empty(await db.LossEvents.ToListAsync());
    }

    [Fact]
    public async Task Create_Truck_DeductTrue_Computes_FinalFreight_And_Stocks_Only_Received()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 50m);
        leg.TransportType = LoadingTransportType.Truck;
        // کرایه باید طرف‌حساب داشته باشد وگرنه سرویس آن را رد می‌کند؛ اینجا موتروانِ مستقل.
        leg.DriverId = 1;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        // 50 loaded / 48 received / rate 20 / shortage rate 30 / Deduct=true ⇒ FinalFreight = 1000 − 60 = 940
        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 48m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightRateUsdPerMt = 20m,
            ShortageRateUsd = 30m,
            DeductShortageFromFreight = true
        });

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(48m, receipt.ReceivedQuantityMt);
        Assert.Equal(2m, receipt.ShortageQuantityMt);
        Assert.Equal(1000m, receipt.FreightCostUsd);
        Assert.Equal(60m, receipt.ShortageChargeUsd);
        Assert.Equal(940m, receipt.FreightPayableUsd);

        var movement = await db.InventoryMovements.SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(48m, movement.QuantityMt);
    }

    [Fact]
    public async Task Create_Truck_DeductFalse_Keeps_Full_Gross_Freight()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 50m);
        leg.TransportType = LoadingTransportType.Truck;
        leg.DriverId = 1;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 48m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightRateUsdPerMt = 20m,
            ShortageRateUsd = 30m,
            DeductShortageFromFreight = false
        });

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(1000m, receipt.FreightCostUsd);
        Assert.Null(receipt.ShortageChargeUsd);   // ShortageDeduction = 0
        Assert.Equal(1000m, receipt.FreightPayableUsd); // FinalFreight = GrossFreight
    }

    [Theory]
    [InlineData(LoadingTransportType.Truck)]
    [InlineData(LoadingTransportType.Wagon)]
    [InlineData(LoadingTransportType.Vessel)]
    public async Task Create_ToInventory_Stocks_Only_Received_For_All_Transport_Types(LoadingTransportType transportType)
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 100m);
        leg.TransportType = transportType;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 98m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(98m, receipt.ReceivedQuantityMt);
        Assert.Equal(2m, receipt.ShortageQuantityMt);

        var movement = await db.InventoryMovements.SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(98m, movement.QuantityMt); // عملیات فقط با مقدار رسیده، نه کسری

        var loss = await db.LossEvents.SingleAsync();
        Assert.Equal(2m, loss.ChargeableLossMt);
        Assert.False(loss.AffectsInventory); // کسری وارد موجودی نمی‌شود
    }

    [Fact]
    public async Task Create_With_CompanyAsset_Does_Not_Post_Freight()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-1",
            Name = "Company Truck 1",
            AssetType = OperationalAssetType.Truck,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var leg = await SeedLoadedLegAsync(db, quantityMt: 50m);
        leg.TransportType = LoadingTransportType.Truck;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 50m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightCostUsd = 800m,
            OperationalAssetId = 1
        });

        // موترِ خودِ شرکت به خودش کرایه پرداخت نمی‌کند؛ هیچ مصرف/لجرِ کرایه‌ای ساخته نمی‌شود.
        // سوخت/حق‌سفر/راه جداگانه (دستی) ثبت می‌شوند، نه از این مسیر.
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());

        // در پروفایل دارایی، عوایدِ کرایه صفر است (کرایهٔ ساختگی حذف شد).
        var profile = Assert.IsType<ViewResult>(
            await new OperationalAssetsController(db).Details(1, new DateTime(2026, 5, 1), new DateTime(2026, 5, 31)));
        var model = Assert.IsType<PTGOilSystem.Web.Models.OperationalAssets.OperationalAssetProfileViewModel>(profile.Model);
        Assert.Equal(0m, model.FreightIncomeUsd);
    }

    [Fact]
    public async Task Create_With_IndependentDriver_Posts_Freight_Credit_To_Driver()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 50m);
        leg.TransportType = LoadingTransportType.Truck;
        leg.DriverId = 1; // موترِ شخصیِ راننده — نه شرکت خدماتی، نه دارایی خودی.
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 50m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightCostUsd = 800m
        });

        // کرایه روی حساب همان راننده (نه شرکت/دارایی) می‌نشیند: مصرف + لجرِ Credit با DriverId.
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(1, expense.DriverId);
        Assert.Null(expense.ServiceProviderId);
        Assert.Null(expense.OperationalAssetId);
        Assert.Equal(800m, expense.AmountUsd);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense");
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.DriverId);
        Assert.Equal(800m, ledger.AmountUsd);
    }

    [Fact]
    public async Task Create_With_ShortageAsSeparateDebt_Keeps_Freight_Gross_And_Debits_Responsible()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 50m);
        leg.TransportType = LoadingTransportType.Truck;
        leg.DriverId = 1;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 48m,
            ShortageQuantityMt = 2m,
            AllowanceMt = 0m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightCostUsd = 800m,
            ShortageRateUsd = 500m,
            ShortageAsSeparateDebt = true
        });

        // کرایه دست‌نخورده (ناخالص) می‌ماند؛ خسارت از کرایه کم نمی‌شود.
        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(800m, receipt.FreightPayableUsd);
        Assert.Equal(1000m, receipt.ShortageChargeUsd); // 2MT × 500

        // خسارت به‌عنوان بدهیِ مستقل (Debit) روی حساب راننده ثبت می‌شود، جدا از کرایه.
        var freightLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense");
        Assert.Equal(LedgerSide.Credit, freightLedger.Side);
        Assert.Equal(800m, freightLedger.AmountUsd);

        var shortageLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "ShortageCharge");
        Assert.Equal(LedgerSide.Debit, shortageLedger.Side);
        Assert.Equal(1, shortageLedger.DriverId);
        Assert.Equal(1000m, shortageLedger.AmountUsd);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static InventoryTransportReceiptsController BuildController(ApplicationDbContext db)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = "ptg-ui-lang=fa";

        return new(
            db,
            new CurrencyConversionService(new PricingService(db)),
            NullLogger<InventoryTransportReceiptsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };
    }

    // کرایه بدون طرف‌حساب یعنی مبلغی که در P&L می‌نشیند ولی بدهیِ هیچ‌کس نمی‌شود:
    // SyncReceiptFreightExpenseAsync بدون شرکت خدماتی/دارایی/راننده هیچ مصرف و سطر دفتری
    // نمی‌سازد. اعتبارسنجی باید جلوی همان حالت را بگیرد.
    [Fact]
    public async Task Create_Rejects_Freight_When_No_Freight_Party_Is_Known()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 50m);
        leg.TransportType = LoadingTransportType.Truck;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 48m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightRateUsdPerMt = 20m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
    }

    // کرایه‌ای که قبلاً با نوع مصرفِ «کرایه حمل» ثبت شده در P&L داخل ExpenseTransactionsUsd
    // است؛ کرایهٔ دوم روی رسید همان مبلغ را از راه ReceiptFreightExpenseUsd دوباره می‌آورد.
    [Fact]
    public async Task Create_Rejects_Freight_When_The_Leg_Already_Has_A_Registered_Freight_Expense()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 50m);
        leg.TransportType = LoadingTransportType.Truck;
        leg.DriverId = 1;

        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 900,
            Code = InventoryTransportReceiptService.TransportFreightExpenseCode,
            Name = "Transport freight",
            NamePersian = "کرایه حمل",
            IsActive = true
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 900,
            ExpenseTypeId = 900,
            TransportLegId = leg.Id,
            DriverId = 1,
            ExpenseDate = new DateTime(2026, 5, 4),
            Amount = 1000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1000m,
            SettlementMode = ExpenseSettlementMode.Payable,
            CounterpartyType = AccountingPartyType.Driver,
            CounterpartyId = 1
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 48m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            FreightRateUsdPerMt = 20m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
    }

    private static async Task SeedReferenceDataAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A" });
        db.Locations.Add(new Location { Id = 1, Name = "Border Yard" });
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "SRC", Name = "Source Terminal" },
            new Terminal { Id = 2, Code = "DST", Name = "Destination Terminal" });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TerminalId = 1, TankCode = "SRC-TK", ProductId = 1, CapacityMt = 500m },
            new StorageTank { Id = 2, TerminalId = 2, TankCode = "DST-TK", ProductId = 1, CapacityMt = 500m });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        await db.SaveChangesAsync();
    }

    // ===== تخلیهٔ واقعی برابر/بیشتر/کمتر از بارگیری (واگن) =====
    // بارگیری ۱۰ تن؛ تخلیه ۱۰ = بدون اختلاف، ۱۰٫۱ = مازاد، ۹٫۹ = کسری.
    // در هر سه حالت ثبت باید بپذیرد، موجودی مقصد به اندازهٔ تخلیهٔ واقعی زیاد شود و حمل بسته شود.

    [Fact]
    public async Task Unload_Equal_To_Loaded_Quantity_Closes_Leg_Without_Variance()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 10m);
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 10m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        Assert.IsType<RedirectToActionResult>(result);
        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(10m, receipt.ReceivedQuantityMt);
        Assert.Equal(0m, receipt.ShortageQuantityMt);

        var movement = await db.InventoryMovements
            .SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal(10m, movement.QuantityMt);

        Assert.Empty(await db.LossEvents.ToListAsync());
        Assert.Equal(
            InventoryTransportLegStatus.Received,
            (await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id)).Status);
    }

    [Fact]
    public async Task Unload_More_Than_Loaded_Is_Accepted_And_Recorded_As_Positive_Variance()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 10m);
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 10.1m,
            ShortageQuantityMt = -0.1m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        Assert.IsType<RedirectToActionResult>(result);
        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(10.1m, receipt.ReceivedQuantityMt);

        // موجودی مقصد دقیقاً به اندازهٔ تخلیهٔ واقعی زیاد می‌شود.
        var movement = await db.InventoryMovements
            .SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(10.1m, movement.QuantityMt);

        // مازاد ۰٫۱ تن ثبت می‌شود و هرگز قابل شارژ نیست.
        var variance = await db.LossEvents.SingleAsync();
        Assert.Equal(LossEventStage.ReceiptShortage, variance.Stage);
        Assert.Equal(-0.1m, variance.DifferenceQuantityMt);
        Assert.Equal(0m, variance.ChargeableLossMt);
        Assert.Equal(leg.Id, variance.TransportLegId);

        // حمل بسته می‌شود.
        Assert.Equal(
            InventoryTransportLegStatus.Received,
            (await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id)).Status);
    }

    [Fact]
    public async Task Unload_Less_Than_Loaded_Is_Recorded_As_Chargeable_Shortage()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedLegAsync(db, quantityMt: 10m);
        var controller = BuildController(db);

        var result = await controller.Create(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 9.9m,
            ShortageQuantityMt = 0.1m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        Assert.IsType<RedirectToActionResult>(result);
        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(9.9m, receipt.ReceivedQuantityMt);

        var movement = await db.InventoryMovements
            .SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}");
        Assert.Equal(9.9m, movement.QuantityMt);

        var shortage = await db.LossEvents.SingleAsync();
        Assert.Equal(0.1m, shortage.DifferenceQuantityMt);
        Assert.Equal(0.1m, shortage.ChargeableLossMt);

        Assert.Equal(
            InventoryTransportLegStatus.Received,
            (await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id)).Status);
    }

    private static async Task<InventoryTransportLeg> SeedLoadedLegAsync(ApplicationDbContext db, decimal quantityMt)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 5, 2),
            QuantityMt = quantityMt,
            ReferenceDocument = "TRANSPORT-LEG:1"
        });
        var leg = new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-1",
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Loaded,
            OutboundInventoryMovementId = 1
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private static async Task SeedPurchaseLoadingAsync(ApplicationDbContext db)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        await db.SaveChangesAsync();
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
