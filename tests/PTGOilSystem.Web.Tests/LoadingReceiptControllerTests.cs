using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class LoadingReceiptControllerTests
{
    [Fact]
    public async Task Create_Get_Uses_StorageTank_DisplayName_In_Receipt_Dropdown()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.StorageTanks.Local.Single().DisplayName = "مخزن کابل شماره ۱";
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        Assert.IsType<ViewResult>(await controller.Create(loadingId: 1, returnUrl: null));
        var tanks = Assert.IsType<SelectList>(controller.ViewData["StorageTanks"]);
        var option = Assert.Single(tanks.Where(item => item.Value == "1"));
        Assert.Equal("مخزن کابل شماره ۱", option.Text);
        Assert.DoesNotContain("TK-01", option.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_Get_Preserves_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance);

        var result = await controller.Create(
            loadingId: 1,
            returnUrl: "/ContractJourney/Details?contractId=1&tab=inventory");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LoadingReceiptCreateViewModel>(view.Model);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=inventory", model.ReturnUrl);
    }

    [Fact]
    public async Task Create_Post_Redirects_To_Local_ReturnUrl_When_Provided()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 55m,
            ReferenceDocument = "RCPT-RETURN",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=inventory"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=inventory", redirect.Url);
    }

    [Fact]
    public async Task Create_Post_Ignores_External_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 55m,
            ReferenceDocument = "RCPT-UNSAFE",
            ReturnUrl = "https://evil.com"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
    }

    [Fact]
    public async Task Create_Post_Blocks_When_Receipt_Exceeds_Loading_Quantity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 10,
            LoadingRegisterId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            ReceivedQuantityMt = 70m,
            ReferenceDocument = "RCPT-001"
        });
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 40m,
            ReferenceDocument = "RCPT-002"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<LoadingReceiptCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("بیشتر"));
    }

    [Fact]
    public async Task Create_Post_Blocks_Quantity_Already_Allocated_To_Loading_Origin_Transport()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 20,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = new DateTime(2026, 4, 24),
            QuantityMt = 60m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation
        {
            Id = 21,
            InventoryTransportLegId = 20,
            SourcePurchaseContractId = 1,
            SourceLoadingRegisterId = 1,
            QuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 50m,
            ReferenceDocument = "RCPT-AFTER-TRANSPORT"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.LoadingReceipts.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Rechecks_Current_Remaining_Before_Insert()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var db = new ApplicationDbContext(options))
        {
            SeedLoadingContext(db);
            await db.SaveChangesAsync();

            var firstController = new LoadingReceiptsController(
                db,
                new AuditService(db),
                NullLogger<LoadingReceiptsController>.Instance)
            {
                TempData = BuildTempData()
            };

            var firstResult = await firstController.Create(new LoadingReceiptCreateViewModel
            {
                LoadingRegisterId = 1,
                ReceiptDate = new DateTime(2026, 4, 24),
                TerminalId = 1,
                StorageTankId = 1,
                ReceivedQuantityMt = 60m,
                ReferenceDocument = "RCPT-060"
            });

            Assert.IsType<RedirectToActionResult>(firstResult);
        }

        await using var db2 = new ApplicationDbContext(options);
        var secondController = new LoadingReceiptsController(
            db2,
            new AuditService(db2),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var secondResult = await secondController.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 50m,
            ReferenceDocument = "RCPT-050"
        });

        var view = Assert.IsType<ViewResult>(secondResult);
        Assert.IsType<LoadingReceiptCreateViewModel>(view.Model);
        Assert.False(secondController.ModelState.IsValid);
        Assert.Contains(secondController.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("باقیمانده"));
        Assert.Equal(1, await db2.LoadingReceipts.CountAsync());
        Assert.Equal(1, await db2.InventoryMovements.CountAsync());
        Assert.Equal(1, await db2.LoadingReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task Create_Post_Creates_Receipt_Linked_InventoryMovement_Allocation_And_Audit()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 55m,
            ReferenceDocument = "RCPT-100",
            Notes = "Initial terminal receipt"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.Equal(1, receipt.LoadingRegisterId);
        Assert.Equal(1, receipt.TerminalId);
        Assert.Equal(1, receipt.StorageTankId);
        Assert.Equal(LoadingReceiptDestination.ToInventory, receipt.ReceiptDestination);
        Assert.Equal(55m, receipt.ReceivedQuantityMt);
        Assert.Equal("RCPT-100", receipt.ReferenceDocument);

        var movement = await db.InventoryMovements.SingleAsync();
        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal(1, movement.ProductId);
        Assert.Equal(1, movement.ContractId);
        Assert.Equal(1, movement.TerminalId);
        Assert.Equal(1, movement.StorageTankId);
        Assert.Equal(55m, movement.QuantityMt);
        Assert.Equal(receipt.Id, movement.LoadingReceiptId);
        Assert.Equal("RCPT-100", movement.ReferenceDocument);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        Assert.Equal(receipt.Id, allocation.LoadingReceiptId);
        Assert.Equal(LoadingReceiptAllocationDestination.ToInventory, allocation.Destination);
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocation.Status);
        Assert.Equal(55m, allocation.QuantityMt);
        Assert.Equal(1, allocation.SourcePurchaseContractId);
        Assert.Equal(1, allocation.TerminalId);
        Assert.Equal(1, allocation.StorageTankId);
        Assert.Equal(movement.Id, allocation.InventoryMovementId);
        Assert.Equal("RCPT-100", allocation.ReferenceDocument);
        Assert.Equal("Initial terminal receipt", allocation.Notes);

        var logs = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.Contains(logs, l => l.EntityName == nameof(LoadingReceipt) && l.Diff is not null && l.Diff.Contains("InventoryMovementId"));
        Assert.Contains(logs, l => l.EntityName == nameof(InventoryMovement) && l.Diff is not null && l.Diff.Contains("LoadingReceiptId"));
        Assert.Contains(logs, l => l.EntityName == nameof(LoadingReceiptAllocation) && l.Diff is not null && l.Diff.Contains("InventoryMovementId"));

        var detailsResult = await controller.Details(receipt.Id);
        var detailsView = Assert.IsType<ViewResult>(detailsResult);
        var detailsModel = Assert.IsType<LoadingReceiptDetailsViewModel>(detailsView.Model);
        Assert.Equal(55m, detailsModel.ReceivedQuantityMt);
        Assert.Equal(55m, detailsModel.TotalAllocatedQuantityMt);
        Assert.Equal(0m, detailsModel.AllocationDifferenceMt);

        var allocationSummary = Assert.Single(detailsModel.Allocations);
        Assert.Equal(LoadingReceiptAllocationDestination.ToInventory, allocationSummary.Destination);
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocationSummary.Status);
        Assert.Equal(55m, allocationSummary.QuantityMt);
        Assert.Equal("PUR-1", allocationSummary.SourcePurchaseContractNumber);
        Assert.Equal("Ilinka Terminal", allocationSummary.TerminalName);
        Assert.Equal("TK-01", allocationSummary.StorageTankCode);
        Assert.Equal(movement.Id, allocationSummary.InventoryMovementId);
        Assert.Equal("RCPT-100", allocationSummary.ReferenceDocument);
        Assert.Equal("Initial terminal receipt", allocationSummary.Notes);
    }

    [Fact]
    public async Task Create_Post_With_ReceiptShortage_Loss_Closes_Remaining_Quantity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();
        db.LoadingRegisters.Single().LoadedQuantityMt = 50m;
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 40m,
            ReferenceDocument = "RCPT-040",
            Loss = new StageLossCaptureInput
            {
                Enabled = true,
                Stage = LossEventStage.ReceiptShortage,
                QuantityMt = 10m,
                Reference = "LOSS-010"
            }
        });

        Assert.IsType<RedirectToActionResult>(result);

        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.Equal(40m, receipt.ReceivedQuantityMt);

        var loss = await db.LossEvents.SingleAsync();
        Assert.Equal(LossEventStage.ReceiptShortage, loss.Stage);
        Assert.Equal(1, loss.LoadingRegisterId);
        Assert.Equal(receipt.Id, loss.LoadingReceiptId);
        Assert.Equal(50m, loss.ExpectedQuantityMt);
        Assert.Equal(40m, loss.ActualQuantityMt);
        Assert.Equal(10m, loss.DifferenceQuantityMt);

        var nextCreateResult = await controller.Create(loadingId: 1);
        var redirect = Assert.IsType<RedirectToActionResult>(nextCreateResult);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Loading", redirect.ControllerName);

        var detailsResult = await controller.Details(receipt.Id);
        var detailsView = Assert.IsType<ViewResult>(detailsResult);
        var detailsModel = Assert.IsType<LoadingReceiptDetailsViewModel>(detailsView.Model);
        Assert.Equal(40m, detailsModel.TotalReceivedQuantityMt);
        Assert.Equal(0m, detailsModel.RemainingToReceiveMt);
    }

    [Fact]
    public async Task Details_Calculates_ReadOnly_Allocation_Total_And_Difference()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 11,
            LoadingRegisterId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 4, 24),
            ReceivedQuantityMt = 55m,
            ReferenceDocument = "RCPT-DIFF"
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 20,
            LoadingReceiptId = 11,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 24),
            QuantityMt = 55m,
            ReferenceDocument = "RCPT-DIFF"
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 30,
            LoadingReceiptId = 11,
            Destination = LoadingReceiptAllocationDestination.ToInventory,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 50m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            InventoryMovementId = 20,
            ReferenceDocument = "RCPT-DIFF",
            Notes = "read-only mismatch sample"
        });
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance);

        var result = await controller.Details(11);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LoadingReceiptDetailsViewModel>(view.Model);
        Assert.Equal(55m, model.ReceivedQuantityMt);
        Assert.Equal(50m, model.TotalAllocatedQuantityMt);
        Assert.Equal(5m, model.AllocationDifferenceMt);

        var allocation = Assert.Single(model.Allocations);
        Assert.Equal("PUR-1", allocation.SourcePurchaseContractNumber);
        Assert.Equal("Ilinka Terminal", allocation.TerminalName);
        Assert.Equal("TK-01", allocation.StorageTankCode);
        Assert.Equal(20, allocation.InventoryMovementId);
        Assert.Equal("RCPT-DIFF", allocation.ReferenceDocument);
        Assert.Equal("read-only mismatch sample", allocation.Notes);
    }

    [Fact]
    public async Task Create_Post_DirectDispatchToTruck_Creates_Receipt_Allocation_And_Dispatch_Without_Stock_Sale_Or_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var stock = new StockService(db);
        var stockBefore = await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, contractId: 1);

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            AllocationDestination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            ReceivedQuantityMt = 55m,
            AllocationDestinationName = "Truck loading bay",
            DestinationReference = "TRUCK-TRACE-1",
            ReferenceDocument = "RCPT-DIRECT",
            DirectTruckPlateNumber = "trk-typed-01",
            DirectDriverName = "Driver Typed",
            DirectDispatchDate = new DateTime(2026, 4, 24),
            DirectTruckTicketSerialNumber = "TKT-DIRECT-1",
            Notes = "Direct discharge from vessel to trucks"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.Equal(LoadingReceiptDestination.DirectDispatch, receipt.ReceiptDestination);
        Assert.Equal(1, receipt.LoadingRegisterId);
        Assert.Equal(1, receipt.TerminalId);
        Assert.Null(receipt.StorageTankId);
        Assert.Equal(55m, receipt.ReceivedQuantityMt);
        Assert.Equal("RCPT-DIRECT", receipt.ReferenceDocument);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        Assert.Equal(receipt.Id, allocation.LoadingReceiptId);
        Assert.Equal(LoadingReceiptAllocationDestination.DirectDispatchToTruck, allocation.Destination);
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocation.Status);
        Assert.Equal(55m, allocation.QuantityMt);
        Assert.Equal(1, allocation.SourcePurchaseContractId);
        Assert.Equal(1, allocation.TerminalId);
        Assert.Null(allocation.StorageTankId);
        Assert.Null(allocation.DestinationTerminalId);
        Assert.Null(allocation.DestinationStorageTankId);
        Assert.Null(allocation.DestinationLocationId);
        Assert.Equal("Truck loading bay", allocation.DestinationName);
        Assert.Equal("TRUCK-TRACE-1", allocation.DestinationReference);
        Assert.Null(allocation.InventoryMovementId);
        Assert.Null(allocation.TruckDispatchId);
        Assert.Null(allocation.SalesTransactionId);
        Assert.Equal("RCPT-DIRECT", allocation.ReferenceDocument);

        Assert.Equal(0, await db.InventoryMovements.CountAsync());
        var truck = await db.Trucks.SingleAsync();
        Assert.Equal("TRK-TYPED-01", truck.PlateNumber);
        Assert.True(truck.IsActive);
        var driver = await db.Drivers.SingleAsync();
        Assert.Equal("Driver Typed", driver.FullName);
        Assert.True(driver.IsActive);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(TruckDispatchMode.DirectFromReceipt, dispatch.DispatchMode);
        Assert.Equal(allocation.Id, dispatch.LoadingReceiptAllocationId);
        Assert.Equal(1, dispatch.ContractId);
        Assert.Equal(1, dispatch.ProductId);
        Assert.Equal(truck.Id, dispatch.TruckId);
        Assert.Equal(driver.Id, dispatch.DriverId);
        Assert.Equal(new DateTime(2026, 4, 24), dispatch.DispatchDate);
        Assert.Equal(DispatchStatus.Loaded, dispatch.Status);
        Assert.Equal(55m, dispatch.LoadedQuantityMt);
        Assert.Equal("TKT-DIRECT-1", dispatch.TicketSerialNumber);
        Assert.Null(dispatch.DischargedQuantityMt);
        Assert.Null(dispatch.ShortageMt);
        Assert.Equal(0, await db.SalesTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync());
        Assert.Equal(stockBefore, await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, contractId: 1));

        var logs = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(5, logs.Count);
        Assert.Contains(logs, l => l.EntityName == nameof(Truck));
        Assert.Contains(logs, l => l.EntityName == nameof(Driver));
        Assert.Contains(logs, l => l.EntityName == nameof(LoadingReceipt));
        Assert.Contains(logs, l => l.EntityName == nameof(LoadingReceiptAllocation));
        Assert.Contains(logs, l => l.EntityName == nameof(TruckDispatch));
        Assert.DoesNotContain(logs, l => l.EntityName == nameof(InventoryMovement));

        var detailsResult = await controller.Details(receipt.Id);
        var detailsView = Assert.IsType<ViewResult>(detailsResult);
        var detailsModel = Assert.IsType<LoadingReceiptDetailsViewModel>(detailsView.Model);
        Assert.Equal(LoadingReceiptDestination.DirectDispatch, detailsModel.ReceiptDestination);
        Assert.Equal(55m, detailsModel.TotalAllocatedQuantityMt);
        Assert.Equal(0m, detailsModel.AllocationDifferenceMt);
        Assert.Equal(0, detailsModel.InventoryMovementId);
        Assert.Null(detailsModel.StorageTankCode);

        var allocationSummary = Assert.Single(detailsModel.Allocations);
        Assert.Equal(LoadingReceiptAllocationDestination.DirectDispatchToTruck, allocationSummary.Destination);
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocationSummary.Status);
        Assert.Equal(55m, allocationSummary.QuantityMt);
        Assert.Equal("PUR-1", allocationSummary.SourcePurchaseContractNumber);
        Assert.Equal("Ilinka Terminal", allocationSummary.TerminalName);
        Assert.Null(allocationSummary.StorageTankCode);
        Assert.Equal("Truck loading bay", allocationSummary.DestinationName);
        Assert.Equal("TRUCK-TRACE-1", allocationSummary.DestinationReference);
        Assert.Null(allocationSummary.InventoryMovementId);
        Assert.Null(allocationSummary.TruckDispatchId);
        Assert.Equal(1, allocationSummary.DirectTruckDispatchCount);
        Assert.Equal(55m, allocationSummary.DirectTruckDispatchedQuantityMt);
        Assert.Equal(0m, allocationSummary.DirectTruckDispatchRemainingQuantityMt);
        Assert.Null(allocationSummary.SalesTransactionId);
    }

    [Fact]
    public async Task Create_Post_DirectSale_Creates_Sale_And_Ledger_Without_InventoryMovement()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.Customers.Add(new Customer { Id = 1, Name = "Direct Customer" });
        db.Locations.Add(new Location { Id = 10, Name = "Customer Yard" });
        await db.SaveChangesAsync();

        var stock = new StockService(db);
        var stockBefore = await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, contractId: 1);

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            AllocationDestination = LoadingReceiptAllocationDestination.DirectSale,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            ReceivedQuantityMt = 45m,
            DestinationLocationId = 10,
            AllocationDestinationName = "Customer Yard",
            DestinationReference = "SALE-TRACE-1",
            ReferenceDocument = "RCPT-DIRECT-SALE",
            SaleCustomerId = 1,
            SaleDate = new DateTime(2026, 4, 24),
            SaleUnitPriceInCurrency = 510m,
            SaleCurrency = "USD",
            SaleInvoiceNumber = "INV-DIRECT-001",
            SaleNotes = "Direct sale from receipt"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        Assert.Equal(LoadingReceiptAllocationDestination.DirectSale, allocation.Destination);
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocation.Status);
        Assert.Equal(10, allocation.DestinationLocationId);
        Assert.Equal("Customer Yard", allocation.DestinationName);
        Assert.Equal("SALE-TRACE-1", allocation.DestinationReference);
        Assert.Null(allocation.InventoryMovementId);
        Assert.Null(allocation.TruckDispatchId);
        Assert.NotNull(allocation.SalesTransactionId);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(allocation.SalesTransactionId, sale.Id);
        Assert.Equal(SaleStage.InTransit, sale.SaleStage);
        Assert.Null(sale.ContractId);
        Assert.Equal(1, sale.CompanyId);
        Assert.Equal(1, sale.CustomerId);
        Assert.Equal(1, sale.ProductId);
        Assert.Equal(10, sale.DestinationLocationId);
        Assert.Equal(45m, sale.QuantityMt);
        Assert.Equal("USD", sale.Currency);
        Assert.Equal(510m, sale.UnitPriceInCurrency);
        Assert.Equal(510m, sale.UnitPriceUsd);
        Assert.Equal(22950m, sale.TotalUsd);
        Assert.Equal("INV-DIRECT-001", sale.InvoiceNumber);
        Assert.Equal("Direct sale from receipt", sale.Notes);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);
        Assert.Equal("INV-DIRECT-001", ledger.Reference);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(22950m, ledger.AmountUsd);
        Assert.Equal(1, ledger.ContractId);
        Assert.Equal(1, ledger.CustomerId);

        Assert.Equal(0, await db.InventoryMovements.CountAsync());
        Assert.Equal(0, await db.TruckDispatches.CountAsync());
        Assert.Equal(stockBefore, await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, contractId: 1));
    }

    [Fact]
    public async Task Create_Post_DirectSale_With_Pending_Price_Creates_Pending_Sale_Without_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.Customers.Add(new Customer { Id = 1, Name = "Direct Customer" });
        db.Locations.Add(new Location { Id = 10, Name = "Customer Yard" });
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        // فروش مستقیم با قیمت باز: سطر دفتر ساخته نمی‌شود و ثبت رسید نباید روی آن بشکند.
        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            AllocationDestination = LoadingReceiptAllocationDestination.DirectSale,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            ReceivedQuantityMt = 45m,
            DestinationLocationId = 10,
            AllocationDestinationName = "Customer Yard",
            ReferenceDocument = "RCPT-DIRECT-SALE-PENDING",
            SaleCustomerId = 1,
            SaleDate = new DateTime(2026, 4, 24),
            SaleIsPricePending = true,
            SaleCurrency = "USD",
            SaleInvoiceNumber = "INV-DIRECT-PENDING-001"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(allocation.SalesTransactionId, sale.Id);
        Assert.True(sale.IsSalePricePending);
        Assert.Equal(45m, sale.QuantityMt);
        Assert.Equal(0m, sale.TotalUsd);
        Assert.Null(sale.AppliedFxRateToUsd);
        Assert.Equal(0, await db.LedgerEntries.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());

        var saleAudit = await db.AuditLogs.SingleAsync(a => a.EntityName == nameof(SalesTransaction) && a.EntityId == sale.Id);
        Assert.NotNull(saleAudit.Diff);
        Assert.Contains("ReceiptAllocationId", saleAudit.Diff);
    }

    [Fact]
    public async Task Create_Post_DirectSale_Rejects_When_Sale_Fields_Are_Missing()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.Locations.Add(new Location { Id = 10, Name = "Customer Yard" });
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            AllocationDestination = LoadingReceiptAllocationDestination.DirectSale,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            ReceivedQuantityMt = 45m,
            DestinationLocationId = 10,
            AllocationDestinationName = "Customer Yard",
            DestinationReference = "SALE-MISSING-FIELDS",
            ReferenceDocument = "RCPT-DIRECT-SALE-MISSING"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<LoadingReceiptCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState.Keys, key => key.Contains(nameof(LoadingReceiptCreateViewModel.SaleBuyerKey)));
        Assert.Contains(controller.ModelState.Keys, key => key.Contains(nameof(LoadingReceiptCreateViewModel.SaleUnitPriceInCurrency)));
        Assert.Contains(controller.ModelState.Keys, key => key.Contains(nameof(LoadingReceiptCreateViewModel.SaleInvoiceNumber)));
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        Assert.Equal(0, await db.LoadingReceiptAllocations.CountAsync());
        Assert.Equal(0, await db.SalesTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Create_Post_TransferToOtherTerminal_Trace_Only_Does_Not_Increase_Destination_Stock()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.Locations.Add(new Location { Id = 20, Name = "Kabul" });
        db.Terminals.Add(new Terminal { Id = 2, Code = "TERM-2", Name = "Kabul Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 2, TerminalId = 2, TankCode = "KBL-01", ProductId = 1, CapacityMt = 6000m });
        await db.SaveChangesAsync();

        var stock = new StockService(db);
        var sourceStockBefore = await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, contractId: 1);
        var destinationStockBefore = await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 2, contractId: 1);

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            AllocationDestination = LoadingReceiptAllocationDestination.TransferToOtherTerminal,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            ReceivedQuantityMt = 35m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            DestinationLocationId = 20,
            AllocationDestinationName = "Kabul Terminal",
            DestinationReference = "TRANSFER-TRACE-1",
            ReferenceDocument = "RCPT-TRANSFER"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        Assert.Equal(LoadingReceiptAllocationDestination.TransferToOtherTerminal, allocation.Destination);
        Assert.Equal(LoadingReceiptAllocationStatus.InTransit, allocation.Status);
        Assert.Equal(2, allocation.DestinationTerminalId);
        Assert.Equal(2, allocation.DestinationStorageTankId);
        Assert.Equal(20, allocation.DestinationLocationId);
        Assert.Equal("Kabul Terminal", allocation.DestinationName);
        Assert.Equal("TRANSFER-TRACE-1", allocation.DestinationReference);
        Assert.Null(allocation.InventoryMovementId);
        Assert.Null(allocation.TruckDispatchId);
        Assert.Null(allocation.SalesTransactionId);

        Assert.Equal(0, await db.InventoryMovements.CountAsync());
        Assert.Equal(sourceStockBefore, await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, contractId: 1));
        Assert.Equal(destinationStockBefore, await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 2, contractId: 1));
    }

    [Fact]
    public async Task Create_Post_Rejects_Mixed_Receipt_Destination()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.Mixed,
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 40m,
            ReferenceDocument = "RCPT-MIXED"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[nameof(LoadingReceiptCreateViewModel.ReceiptDestination)]!.Errors,
            e => e.ErrorMessage.Contains("ترکیبی"));
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Create_Post_Requires_Storage_Tank_For_Inventory_Receipt()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = null,
            ReceivedQuantityMt = 40m,
            ReferenceDocument = "RCPT-NO-TANK"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[nameof(LoadingReceiptCreateViewModel.StorageTankId)]!.Errors,
            e => e.ErrorMessage.Contains("مخزن"));
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
    }

    [Fact]
    public async Task BulkCreate_Post_Creates_Proportional_ToInventory_Receipts_For_Selected_Loadings()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 2,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 300m,
            BillOfLadingNumber = "RWB-002",
            WagonNumber = "WG-002"
        });
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.BulkCreate(new LoadingReceiptBulkCreateViewModel
        {
            ContractId = 1,
            LoadingRegisterIds = [1, 2],
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            TotalReceivedQuantityMt = 200m,
            ReferenceDocument = "BULK-001",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=receipts"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=receipts", redirect.Url);

        var receipts = await db.LoadingReceipts.OrderBy(r => r.LoadingRegisterId).ToListAsync();
        Assert.Equal(2, receipts.Count);
        Assert.Equal(50m, receipts[0].ReceivedQuantityMt);
        Assert.Equal(150m, receipts[1].ReceivedQuantityMt);
        Assert.All(receipts, r =>
        {
            Assert.Equal(LoadingReceiptDestination.ToInventory, r.ReceiptDestination);
            Assert.Equal(ReceiptLossMode.ImmediateKnownLoss, r.LossMode);
            Assert.Equal(1, r.TerminalId);
            Assert.Equal(1, r.StorageTankId);
            Assert.StartsWith("BULK-001 /", r.ReferenceDocument);
        });

        var movements = await db.InventoryMovements.OrderBy(m => m.LoadingReceiptId).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(new[] { 50m, 150m }, movements.Select(m => m.QuantityMt).ToArray());
        Assert.All(movements, m =>
        {
            Assert.Equal(MovementDirection.In, m.Direction);
            Assert.Equal(1, m.ContractId);
            Assert.Equal(1, m.ProductId);
            Assert.Equal(1, m.TerminalId);
            Assert.Equal(1, m.StorageTankId);
        });

        var allocations = await db.LoadingReceiptAllocations.OrderBy(a => a.LoadingReceiptId).ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(new[] { 50m, 150m }, allocations.Select(a => a.QuantityMt).ToArray());
        Assert.All(allocations, a =>
        {
            Assert.Equal(LoadingReceiptAllocationDestination.ToInventory, a.Destination);
            Assert.Equal(LoadingReceiptAllocationStatus.Completed, a.Status);
            Assert.Equal(1, a.SourcePurchaseContractId);
            Assert.NotNull(a.InventoryMovementId);
        });

        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync());
        Assert.Equal(0, await db.SalesTransactions.CountAsync());
        Assert.Equal(0, await db.LossEvents.CountAsync());
    }

    [Fact]
    public async Task BulkCreate_Post_ImmediateLoss_Creates_Proportional_ReceiptShortage_LossEvents()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.BulkCreate(new LoadingReceiptBulkCreateViewModel
        {
            ContractId = 1,
            LoadingRegisterIds = [1],
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            TotalReceivedQuantityMt = 90m,
            LossMode = BulkReceiptLossMode.ImmediateKnownLoss,
            TotalLossQuantityMt = 10m,
            TotalLossToleranceQuantityMt = 2m,
            ReferenceDocument = "BULK-LOSS-001",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=receipts"
        });

        Assert.IsType<RedirectResult>(result);

        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.Equal(90m, receipt.ReceivedQuantityMt);
        Assert.Equal(ReceiptLossMode.ImmediateKnownLoss, receipt.LossMode);

        var loss = await db.LossEvents.SingleAsync();
        Assert.Equal(LossEventStage.ReceiptShortage, loss.Stage);
        Assert.Equal(receipt.Id, loss.LoadingReceiptId);
        Assert.Equal(1, loss.LoadingRegisterId);
        Assert.Equal(100m, loss.ExpectedQuantityMt);
        Assert.Equal(90m, loss.ActualQuantityMt);
        Assert.Equal(10m, loss.DifferenceQuantityMt);
        Assert.Equal(2m, loss.AllowableLossMt);
        Assert.Equal(8m, loss.ChargeableLossMt);
        Assert.False(loss.AffectsInventory);
        Assert.Equal(1, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task BulkCreate_Post_DeferredLossMode_Saves_Receipts_Without_Immediate_LossEvents()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.BulkCreate(new LoadingReceiptBulkCreateViewModel
        {
            ContractId = 1,
            LoadingRegisterIds = [1],
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            TotalReceivedQuantityMt = 90m,
            LossMode = BulkReceiptLossMode.DeferredTankSettlement,
            ReferenceDocument = "BULK-DEFER-001",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=receipts"
        });

        Assert.IsType<RedirectResult>(result);

        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.Equal(90m, receipt.ReceivedQuantityMt);
        Assert.Equal(ReceiptLossMode.DeferredTankSettlement, receipt.LossMode);
        Assert.Equal(1, receipt.StorageTankId);
        Assert.Equal(0, await db.LossEvents.CountAsync());
        Assert.Equal(1, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task BulkCreate_Post_DeferredLossMode_With_ImmediateLossAmount_Is_Rejected()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.BulkCreate(new LoadingReceiptBulkCreateViewModel
        {
            ContractId = 1,
            LoadingRegisterIds = [1],
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            TotalReceivedQuantityMt = 90m,
            LossMode = BulkReceiptLossMode.DeferredTankSettlement,
            TotalLossQuantityMt = 10m,
            ReferenceDocument = "BULK-DEFER-INVALID",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=receipts"
        });

        Assert.IsType<RedirectResult>(result);
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
        Assert.Equal(0, await db.LossEvents.CountAsync());
        Assert.True(controller.TempData.ContainsKey("err"));
    }

    [Fact]
    public async Task BulkCreate_Post_Uses_Remaining_Quantity_For_Proportional_Distribution()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 2,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 140m,
            BillOfLadingNumber = "RWB-002",
            WagonNumber = "WG-002"
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 10,
            LoadingRegisterId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            ReceivedQuantityMt = 40m,
            ReferenceDocument = "PREVIOUS-001"
        });
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.BulkCreate(new LoadingReceiptBulkCreateViewModel
        {
            ContractId = 1,
            LoadingRegisterIds = [1, 2],
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            TotalReceivedQuantityMt = 100m,
            ReferenceDocument = "BULK-002",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=receipts"
        });

        Assert.IsType<RedirectResult>(result);

        var newReceipts = await db.LoadingReceipts
            .Where(r => r.ReferenceDocument != "PREVIOUS-001")
            .OrderBy(r => r.LoadingRegisterId)
            .ToListAsync();
        Assert.Equal(2, newReceipts.Count);
        Assert.Equal(30m, newReceipts[0].ReceivedQuantityMt);
        Assert.Equal(70m, newReceipts[1].ReceivedQuantityMt);
    }

    [Fact]
    public async Task BulkCreate_Post_Rejects_When_Total_Exceeds_Selected_Remaining()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.BulkCreate(new LoadingReceiptBulkCreateViewModel
        {
            ContractId = 1,
            LoadingRegisterIds = [1],
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            TotalReceivedQuantityMt = 101m,
            ReferenceDocument = "BULK-TOO-MUCH",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=receipts"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=receipts", redirect.Url);
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
        Assert.Equal(0, await db.LoadingReceiptAllocations.CountAsync());
        Assert.True(controller.TempData.ContainsKey("err"));
    }

    [Fact]
    public async Task Create_Post_Rejects_A_Receipt_Dated_Before_The_Loading()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedLoadingContext(db);
        await db.SaveChangesAsync();

        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            ReceiptDate = new DateTime(2026, 4, 22), // بارگیری 2026-04-23 است
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 55m,
            ReferenceDocument = "RCPT-EARLY"
        });

        Assert.True(controller.ModelState.ContainsKey(nameof(LoadingReceiptCreateViewModel.ReceiptDate)));
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
    }

    private static void SeedLoadingContext(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Ilinka Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-01", ProductId = 1, CapacityMt = 8000m });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 100m,
            BillOfLadingNumber = "RWB-001",
            WagonNumber = "WG-001",
            ConsigneeName = "Terminal Ilinka",
            DestinationName = "Novopolotsk"
        });
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new InMemoryTempDataProvider());

    private static IUrlHelper BuildUrlHelper()
        => new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
