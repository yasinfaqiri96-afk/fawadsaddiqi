using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Dispatch;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class DispatchControllerTests
{
    [Fact]
    public void ValidationScriptsPartial_Does_Not_Reference_Missing_Local_Validation_Assets()
    {
        var partialPath = GetProjectFilePath("src", "PTGOilSystem.Web", "Views", "Shared", "_ValidationScriptsPartial.cshtml");
        var content = File.ReadAllText(partialPath);

        Assert.DoesNotContain("~/lib/jquery-validation/dist/jquery.validate.min.js", content);
        Assert.DoesNotContain("~/lib/jquery-validation-unobtrusive/jquery.validate.unobtrusive.min.js", content);
    }

    [Fact]
    public void Create_View_Uses_Full_Validation_Summary_At_The_Top_Of_The_Form()
    {
        var viewPath = GetProjectFilePath("src", "PTGOilSystem.Web", "Views", "Dispatch", "Create.cshtml");
        var content = File.ReadAllText(viewPath);
        var flashPartialPath = GetProjectFilePath("src", "PTGOilSystem.Web", "Views", "Shared", "_FlashAlerts.cshtml");
        var flashPartial = File.ReadAllText(flashPartialPath);

        Assert.Contains("asp-validation-summary=\"All\"", content);
        Assert.DoesNotContain("TempData[\"error\"]", content);
        Assert.Contains("TempData[\"error\"]", flashPartial);
        Assert.Contains("data-boltz-auto-dismiss", flashPartial);
    }

    [Fact]
    public void Shared_Alerts_Use_Green_For_Success_And_Red_For_Error()
    {
        var tokensPath = GetProjectFilePath("src", "PTGOilSystem.Web", "wwwroot", "css", "ptg", "01-tokens.css");
        var tokens = File.ReadAllText(tokensPath);
        var toastCssPath = GetProjectFilePath("src", "PTGOilSystem.Web", "wwwroot", "css", "ptg", "18-toasts.css");
        var toastCss = File.ReadAllText(toastCssPath);

        Assert.Contains("--success-main: #24AF22;", tokens);
        Assert.Contains("--success-rgb: 36, 175, 34;", tokens);
        Assert.Contains("--error-main: #CB0B0B;", tokens);
        Assert.Contains(".ptg-toast--success { background: var(--ptg-success", toastCss);
        Assert.Contains(".ptg-toast--error   { background: var(--ptg-danger", toastCss);
    }

    [Fact]
    public async Task Create_Post_Blocks_When_Inventory_Is_Insufficient()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01" });
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new DispatchCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            SourceTerminalId = 1,
            DispatchDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 20m
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DispatchCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("موجودی کافی"));
    }

    [Fact]
    public async Task Create_Get_Redirects_To_The_Unified_Transport_Entry()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CTR-1",
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<DispatchController>.Instance);

        var result = await controller.Create(contractId: 1, returnUrl: "/Contracts/Details/1?tab=dispatch");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Create", redirect.ActionName);
        Assert.Equal("Transports", redirect.ControllerName);
        Assert.Equal((int)TransportStartSourceKind.Inventory, redirect.RouteValues!["sourceKind"]);
    }

    [Fact]
    public async Task Create_Post_When_Unexpected_Error_Occurs_Adds_Clear_Friendly_Message()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01" });
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("Unexpected failure")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new DispatchCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            SourceTerminalId = 1,
            DispatchDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 20m
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DispatchCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[string.Empty]!.Errors,
            e => e.ErrorMessage.Contains("خطای غیرمنتظره") || e.ErrorMessage.Contains("اطلاعات واردشده"));
    }

    [Fact]
    public async Task Create_Post_Persists_Dispatch_And_Inventory_Movement_And_Audit()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dispatchDate = new DateTime(2026, 4, 23);
        Assert.Equal(DateTimeKind.Unspecified, dispatchDate.Kind);

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 500m });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01" });
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot" });
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-1"
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new DispatchCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationLocationId = 1,
            DispatchDate = dispatchDate,
            LoadedQuantityMt = 25m,
            Notes = "Night dispatch",
            ReferenceDocument = "DISP-001"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(25m, dispatch.LoadedQuantityMt);
        Assert.Equal(1, dispatch.DestinationLocationId);
        Assert.Equal(TruckDispatchMode.FromInventory, dispatch.DispatchMode);
        Assert.Null(dispatch.LoadingReceiptAllocationId);

        var outMovement = await db.InventoryMovements
            .OrderBy(m => m.Id)
            .LastAsync();

        Assert.Equal(MovementDirection.Out, outMovement.Direction);
        Assert.Equal(25m, outMovement.QuantityMt);
        Assert.Equal("TRUCK-DISPATCH:1", outMovement.ReferenceDocument);

        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal(nameof(TruckDispatch), log.EntityName);
        Assert.Equal("Insert", log.Action);
    }

    [Fact]
    public async Task Create_Post_Rechecks_Stock_After_Source_Revalidation_And_Blocks_When_Second_Check_Fails()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 500m });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true });
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        await db.SaveChangesAsync();

        var stock = new TwoStepStockService(new BusinessRuleException("STOCK_CHANGED", "موجودی کافی نیست. موجودی توسط ثبت دیگری مصرف شده است."));
        var controller = new DispatchController(
            db,
            stock,
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new DispatchCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DispatchDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 20m
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DispatchCreateViewModel>(view.Model);
        Assert.Equal(2, stock.EnsureMovementCallCount);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("ثبت دیگری") || e.ErrorMessage.Contains("موجودی کافی"));
        Assert.Empty(await db.TruckDispatches.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Details_Returns_Source_Info_From_Linked_Inventory_Movement()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 500m });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01" });
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 20m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 23),
            QuantityMt = 20m,
            ReferenceDocument = "TRUCK-DISPATCH:1"
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<DispatchController>.Instance);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DispatchDetailsViewModel>(view.Model);
        Assert.Equal("Terminal 1", model.SourceTerminalName);
        Assert.Equal("TK-1", model.SourceStorageTankCode);
    }

    [Fact]
    public async Task Create_Post_Redirects_To_ReturnUrl_When_Provided()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 500m });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true });
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot" });
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-1"
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.Create(new DispatchCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 25m,
            ReferenceDocument = "DISP-RETURN-1",
            ReturnUrl = "/Contracts/Details/1?tab=dispatch"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Contracts/Details/1?tab=dispatch", redirect.Url);
    }

    [Fact]
    public async Task TruckDispatch_Can_Link_Multiple_Direct_Dispatches_To_LoadingReceiptAllocation()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Trucks.AddRange(
            new Truck { Id = 1, PlateNumber = "TRK-01" },
            new Truck { Id = 2, PlateNumber = "TRK-02" });
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 22),
            LoadedQuantityMt = 100m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 4, 23),
            ReceivedQuantityMt = 100m
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            Status = LoadingReceiptAllocationStatus.TraceOnly,
            QuantityMt = 100m,
            SourcePurchaseContractId = 1,
            TerminalId = 1
        });
        db.TruckDispatches.AddRange(
            new TruckDispatch
            {
                ContractId = 1,
                ProductId = 1,
                TruckId = 1,
                DispatchMode = TruckDispatchMode.DirectFromReceipt,
                LoadingReceiptAllocationId = 1,
                DispatchDate = new DateTime(2026, 4, 23),
                LoadedQuantityMt = 60m
            },
            new TruckDispatch
            {
                ContractId = 1,
                ProductId = 1,
                TruckId = 2,
                DispatchMode = TruckDispatchMode.DirectFromReceipt,
                LoadingReceiptAllocationId = 1,
                DispatchDate = new DateTime(2026, 4, 23),
                LoadedQuantityMt = 40m
            });
        await db.SaveChangesAsync();

        var allocation = await db.LoadingReceiptAllocations
            .Include(a => a.DirectTruckDispatches)
            .SingleAsync();

        Assert.Equal(2, allocation.DirectTruckDispatches.Count);
        Assert.All(allocation.DirectTruckDispatches, d => Assert.Equal(TruckDispatchMode.DirectFromReceipt, d.DispatchMode));
        Assert.Equal(100m, allocation.DirectTruckDispatches.Sum(d => d.LoadedQuantityMt));
    }

    [Fact]
    public async Task CreateDirectFromReceipt_Post_Creates_Direct_Dispatch_Without_Stock_InventoryMovement_Sale_Or_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for DirectFromReceipt.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateDirectFromReceipt(new DispatchDirectFromReceiptCreateViewModel
        {
            LoadingReceiptAllocationId = 1,
            TruckId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 60m,
            TicketSerialNumber = "DIR-001",
            Notes = "Direct load to truck"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(TruckDispatchMode.DirectFromReceipt, dispatch.DispatchMode);
        Assert.Equal(1, dispatch.LoadingReceiptAllocationId);
        Assert.Equal(1, dispatch.ContractId);
        Assert.Equal(1, dispatch.ProductId);
        Assert.Equal(60m, dispatch.LoadedQuantityMt);
        Assert.Equal("DIR-001", dispatch.TicketSerialNumber);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        Assert.Equal(LoadingReceiptAllocationStatus.InTransit, allocation.Status);

        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task CreateDirectFromReceipt_Post_Creates_Typed_Truck_And_Driver_When_Not_Selected()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for DirectFromReceipt.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateDirectFromReceipt(new DispatchDirectFromReceiptCreateViewModel
        {
            LoadingReceiptAllocationId = 1,
            TruckPlateNumberInput = "trk-new-03",
            DriverNameInput = "Driver Typed",
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 25m,
            TicketSerialNumber = "DIR-TYPED"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var truck = await db.Trucks.SingleAsync(t => t.PlateNumber == "TRK-NEW-03");
        var driver = await db.Drivers.SingleAsync(d => d.FullName == "Driver Typed");
        var dispatch = await db.TruckDispatches.SingleAsync();

        Assert.Equal(truck.Id, dispatch.TruckId);
        Assert.Equal(driver.Id, dispatch.DriverId);
        Assert.Equal(25m, dispatch.LoadedQuantityMt);
        Assert.Equal("DIR-TYPED", dispatch.TicketSerialNumber);
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        var logs = await db.AuditLogs.ToListAsync();
        Assert.Contains(logs, l => l.EntityName == nameof(Truck));
        Assert.Contains(logs, l => l.EntityName == nameof(Driver));
        Assert.Contains(logs, l => l.EntityName == nameof(TruckDispatch));
    }

    [Fact]
    public async Task CreateDirectFromReceipt_Post_Allows_Multiple_Dispatches_Up_To_Allocation_Quantity_And_Completes_Allocation()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for DirectFromReceipt.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateDirectFromReceipt(new DispatchDirectFromReceiptCreateViewModel
        {
            LoadingReceiptAllocationId = 1,
            TruckId = 2,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 40m
        });

        Assert.IsType<RedirectToActionResult>(result);

        var allocation = await db.LoadingReceiptAllocations
            .Include(a => a.DirectTruckDispatches)
            .SingleAsync();

        Assert.Equal(2, allocation.DirectTruckDispatches.Count);
        Assert.Equal(100m, allocation.DirectTruckDispatches.Sum(d => d.LoadedQuantityMt));
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocation.Status);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Unload_Post_Delivers_Direct_Dispatch_And_Creates_Receipt_Inventory_Movement_And_Driver_Shortage()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 1,
            Code = "TRN-1",
            Name = "Road Carrier",
            ProviderType = ServiceProviderType.TransportCompany,
            IsActive = true
        });
        db.StorageTanks.Add(new StorageTank
        {
            Id = 1,
            TerminalId = 1,
            TankCode = "TK-01",
            ProductId = 1,
            CapacityMt = 500m,
            IsActive = true
        });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A", IsActive = true });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = 40m,
            TicketSerialNumber = "DIR-001"
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for truck unload.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Unload(1, new DispatchUnloadViewModel
        {
            TruckDispatchId = 1,
            ReceiptDate = new DateTime(2026, 4, 25),
            DestinationTerminalId = 1,
            DestinationStorageTankId = 1,
            DischargedQuantityMt = 38.5m,
            ShortageMt = 1.5m,
            AllowanceMt = 0.5m,
            FreightCostUsd = 1000m,
            ServiceProviderId = 1,
            ShortageRateUsd = 200m,
            ReceivedBy = "Tank operator",
            DocumentReference = "UNL-001",
            Notes = "Truck unloaded into tank"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(DispatchStatus.Delivered, dispatch.Status);
        Assert.Equal(38.5m, dispatch.DischargedQuantityMt);
        Assert.Equal(1.5m, dispatch.ShortageMt);
        Assert.Equal(0.5m, dispatch.AllowanceMt);
        Assert.Equal(0.5m, dispatch.ToleranceMt);
        Assert.Equal(1.0m, dispatch.ChargeableShortageMt);
        Assert.Equal(1000m, dispatch.FreightCostUsd);
        Assert.Equal(200m, dispatch.ShortageRateUsd);
        Assert.Equal(200m, dispatch.PayableUsd);
        Assert.Equal(800m, dispatch.FreightPayableUsd);
        Assert.Equal(1, dispatch.ServiceProviderId);

        var expense = await db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .SingleAsync();
        Assert.Equal("TRUCK-DISPATCH-FREIGHT", expense.ExpenseType?.Code);
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Equal(1, expense.TruckDispatchId);
        Assert.Equal(800m, expense.AmountUsd);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Equal(800m, ledger.AmountUsd);

        var receipt = await db.DeliveryReceipts.SingleAsync();
        Assert.Equal(1, receipt.TruckDispatchId);
        Assert.Equal(new DateTime(2026, 4, 25), receipt.ReceiptDate);
        Assert.Equal(38.5m, receipt.ReceivedQuantityMt);
        Assert.Equal("Tank operator", receipt.ReceivedBy);
        Assert.Equal("UNL-001", receipt.DocumentReference);

        var movement = await db.InventoryMovements.SingleAsync();
        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal(1, movement.ProductId);
        Assert.Equal(1, movement.ContractId);
        Assert.Equal(1, movement.TerminalId);
        Assert.Equal(1, movement.StorageTankId);
        Assert.Equal(new DateTime(2026, 4, 25), movement.MovementDate);
        Assert.Equal(38.5m, movement.QuantityMt);
        Assert.Equal("TRUCK-UNLOAD:1", movement.ReferenceDocument);

        var loss = await db.LossEvents.SingleAsync();
        Assert.Equal(LossEventStage.DispatchShortage, loss.Stage);
        Assert.Equal(1, loss.TruckDispatchId);
        Assert.Equal(40m, loss.ExpectedQuantityMt);
        Assert.Equal(38.5m, loss.ActualQuantityMt);
        Assert.Equal(1.5m, loss.DifferenceQuantityMt);
        Assert.Equal(0.5m, loss.AllowableLossMt);
        Assert.Equal(1.0m, loss.ChargeableLossMt);
        Assert.Equal("Driver", loss.ResponsiblePartyType);
        Assert.Equal("Driver A", loss.ResponsiblePartyName);
        Assert.False(loss.AffectsInventory);
        Assert.Null(loss.InventoryMovementId);
        Assert.False(loss.IsCancelled);
    }

    [Fact]
    public async Task Unload_Post_Records_Surplus_As_Negative_Variance_And_Never_Charges_The_Driver()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.StorageTanks.Add(new StorageTank
        {
            Id = 1,
            TerminalId = 1,
            TankCode = "TK-01",
            ProductId = 1,
            CapacityMt = 500m,
            IsActive = true
        });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A", IsActive = true });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = 10m,
            TicketSerialNumber = "DIR-002"
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for truck unload.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        // بارگیری ۱۰ تن، تخلیه ۱۰.۲ تن ⇒ ۰.۲ تن اضافه‌بار.
        var result = await controller.Unload(1, new DispatchUnloadViewModel
        {
            TruckDispatchId = 1,
            ReceiptDate = new DateTime(2026, 4, 25),
            DestinationTerminalId = 1,
            DestinationStorageTankId = 1,
            DischargedQuantityMt = 10.2m,
            ShortageMt = -0.2m,
            FreightCostUsd = 1000m,
            ShortageRateUsd = 200m,
            DocumentReference = "UNL-002"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(10.2m, dispatch.DischargedQuantityMt);
        // تفاوت با علامت واقعی ذخیره می‌شود و صفر نمی‌گردد.
        Assert.Equal(-0.2m, dispatch.ShortageMt);
        Assert.Equal(0m, dispatch.ChargeableShortageMt);
        Assert.Null(dispatch.PayableUsd);
        // اضافه‌بار از کرایه کم نمی‌شود.
        Assert.Equal(1000m, dispatch.FreightPayableUsd);

        // موجودی مقصد دقیقاً به اندازهٔ تخلیهٔ واقعی زیاد می‌شود.
        var movement = await db.InventoryMovements.SingleAsync();
        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal(10.2m, movement.QuantityMt);
        Assert.Equal("TRUCK-UNLOAD:1", movement.ReferenceDocument);

        var receipt = await db.DeliveryReceipts.SingleAsync();
        Assert.Equal(10.2m, receipt.ReceivedQuantityMt);

        // رکورد قابل ردیابی ساخته می‌شود و هرگز قابل جریمه نیست.
        var variance = await db.LossEvents.SingleAsync();
        Assert.Equal(LossEventStage.DispatchShortage, variance.Stage);
        Assert.Equal(1, variance.TruckDispatchId);
        Assert.Equal(10m, variance.ExpectedQuantityMt);
        Assert.Equal(10.2m, variance.ActualQuantityMt);
        Assert.Equal(-0.2m, variance.DifferenceQuantityMt);
        Assert.Equal(0m, variance.AllowableLossMt);
        Assert.Equal(0m, variance.ChargeableLossMt);
        Assert.False(variance.AffectsInventory);
        Assert.False(variance.IsCancelled);
    }

    [Fact]
    public async Task Unload_Post_Blocks_Editing_A_Posted_Inventory_Document()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.StorageTanks.Add(new StorageTank
        {
            Id = 1,
            TerminalId = 1,
            TankCode = "TK-01",
            ProductId = 1,
            CapacityMt = 500m,
            IsActive = true
        });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A", IsActive = true });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = 10m,
            TicketSerialNumber = "DIR-003"
        });
        await db.SaveChangesAsync();

        DispatchUnloadViewModel BuildModel(decimal dischargedMt, decimal differenceMt) => new()
        {
            TruckDispatchId = 1,
            ReceiptDate = new DateTime(2026, 4, 25),
            DestinationTerminalId = 1,
            DestinationStorageTankId = 1,
            DischargedQuantityMt = dischargedMt,
            ShortageMt = differenceMt,
            DocumentReference = "UNL-003"
        };

        DispatchController BuildController() => new(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for truck unload.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        // سند قطعی موجودی immutable است: اصلاح باید از مسیر برگشت/ثبت مجدد انجام شود، نه mutation خاموش.
        Assert.IsType<RedirectToActionResult>(await BuildController().Unload(1, BuildModel(9.8m, 0.2m)));
        var afterShortage = await db.LossEvents.SingleAsync();
        Assert.Equal(0.2m, afterShortage.DifferenceQuantityMt);
        var postedMovement = await db.InventoryMovements.SingleAsync();
        Assert.Equal(9.8m, postedMovement.QuantityMt);
        Assert.Equal("TRUCK-UNLOAD:1", postedMovement.ReferenceDocument);

        var editController = BuildController();
        Assert.IsType<ViewResult>(await editController.Unload(1, BuildModel(10.2m, -0.2m)));
        Assert.False(editController.ModelState.IsValid);
        var unchangedLoss = await db.LossEvents.SingleAsync();
        Assert.Equal(afterShortage.Id, unchangedLoss.Id);
        Assert.Equal(0.2m, unchangedLoss.DifferenceQuantityMt);
        Assert.Equal(0.2m, unchangedLoss.ChargeableLossMt);
        Assert.False(unchangedLoss.IsCancelled);
        Assert.Equal(0.2m, (await db.TruckDispatches.SingleAsync()).ShortageMt);
        var unchangedMovement = await db.InventoryMovements.SingleAsync();
        Assert.Equal(postedMovement.Id, unchangedMovement.Id);
        Assert.Equal(9.8m, unchangedMovement.QuantityMt);
    }

    [Fact]
    public async Task CreateDirectFromReceipt_Post_Rejects_OverDispatch_Without_Creating_Dispatch()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for DirectFromReceipt.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateDirectFromReceipt(new DispatchDirectFromReceiptCreateViewModel
        {
            LoadingReceiptAllocationId = 1,
            TruckId = 2,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 41m
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DispatchDirectFromReceiptCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(1, await db.TruckDispatches.CountAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Cancel_DirectFromReceipt_Does_Not_Create_Reversal_InventoryMovement()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for DirectFromReceipt cancel.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        await controller.Cancel(1);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(DispatchStatus.Cancelled, dispatch.Status);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task CreateSaleFromDirectDispatch_With_Pending_Price_Records_The_Sale_Without_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for DirectFromReceipt sale.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateSaleFromDirectDispatch(new DispatchDirectFromReceiptSaleCreateViewModel
        {
            TruckDispatchId = 1,
            CustomerId = 1,
            SaleDate = new DateTime(2026, 4, 25),
            QuantityMt = 60m,
            Currency = "USD",
            IsSalePricePending = true,
            InvoiceNumber = "DDS-PENDING"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.True(sale.IsSalePricePending);
        Assert.Equal(60m, sale.QuantityMt);
        Assert.Equal(0m, sale.TotalUsd);
        Assert.Empty(await db.LedgerEntries.Where(l => l.SourceType == "Sale").ToListAsync());
        Assert.Equal(sale.Id, (await db.TruckDispatches.SingleAsync()).SalesTransactionId);
    }

    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Creates_Optional_Sale_And_Ledger_Without_Stock_Movement()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for DirectFromReceipt sale.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateSaleFromDirectDispatch(new DispatchDirectFromReceiptSaleCreateViewModel
        {
            TruckDispatchId = 1,
            CustomerId = 1,
            SaleDate = new DateTime(2026, 4, 25),
            QuantityMt = 60m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            InvoiceNumber = "DDS-001",
            Notes = "sold from direct dispatch"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(1, dispatch.SalesTransactionId);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(SaleStage.InTransit, sale.SaleStage);
        Assert.Null(sale.ContractId);
        Assert.Equal(1, sale.CompanyId);
        Assert.Equal(1, sale.CustomerId);
        Assert.Equal(1, sale.ProductId);
        Assert.Equal(1, sale.DestinationLocationId);
        Assert.Equal(60m, sale.QuantityMt);
        Assert.Equal(500m, sale.UnitPriceUsd);
        Assert.Equal(30000m, sale.TotalUsd);
        Assert.Equal("DDS-001", sale.InvoiceNumber);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);
        Assert.Equal(1, ledger.ContractId);
        Assert.Equal(1, ledger.CustomerId);
        Assert.Equal(30000m, ledger.AmountUsd);

        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Sells_FromInventory_Dispatch_Without_Stock_Movement()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.FromInventory,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 40m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for FromInventory sale.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateSaleFromDirectDispatch(new DispatchDirectFromReceiptSaleCreateViewModel
        {
            TruckDispatchId = 1,
            CustomerId = 1,
            SaleDate = new DateTime(2026, 4, 25),
            QuantityMt = 40m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            InvoiceNumber = "FIS-001",
            Notes = "sold from inventory truck"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(1, dispatch.SalesTransactionId);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Null(sale.ContractId);
        Assert.Equal(1, sale.CompanyId);
        Assert.Equal(1, sale.CustomerId);
        Assert.Equal(40m, sale.QuantityMt);
        Assert.Equal(20000m, sale.TotalUsd);
        Assert.Equal("FIS-001", sale.InvoiceNumber);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);
        Assert.Equal(1, ledger.ContractId);
        Assert.Equal(20000m, ledger.AmountUsd);

        // موجودی هنگام بارگیری موتر کم شده؛ فروش نباید حرکت انبار جدید بسازد.
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Rejects_OverSale_Without_Creating_Sale()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for rejected DirectFromReceipt sale.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateSaleFromDirectDispatch(new DispatchDirectFromReceiptSaleCreateViewModel
        {
            TruckDispatchId = 1,
            CustomerId = 1,
            SaleDate = new DateTime(2026, 4, 25),
            QuantityMt = 61m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            InvoiceNumber = "DDS-OVER"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DispatchDirectFromReceiptSaleCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Null((await db.TruckDispatches.SingleAsync()).SalesTransactionId);
    }

    // سناریوی واقعی: ۱۰ تن بارگیری، ۱۰.۲ تن تخلیه، تمام ۱۰.۲ تن به مشتری فروخته می‌شود.
    // فروش و طلب مشتری باید روی مقدار واقعی بنشیند، نه روی وزن بارگیری.
    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Sells_The_Real_Unloaded_Quantity_Including_Surplus()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.FromInventory,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 10m,
            DischargedQuantityMt = 10.2m,
            ShortageMt = -0.2m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for surplus sale.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateSaleFromDirectDispatch(new DispatchDirectFromReceiptSaleCreateViewModel
        {
            TruckDispatchId = 1,
            CustomerId = 1,
            SaleDate = new DateTime(2026, 4, 25),
            QuantityMt = 10.2m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            InvoiceNumber = "SUR-001"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(10.2m, sale.QuantityMt);
        Assert.Equal(5100m, sale.TotalUsd);

        // طلب مشتری = عاید کاملِ ۱۰.۲ تن، نه ۱۰ تن.
        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(5100m, ledger.AmountUsd);

        // اضافه‌بار قیمت خرید نمی‌سازد و حرکت موجودی تازه‌ای هم ندارد.
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    // اضافه‌بار حداکثر تا مقدار تخلیه فروخته می‌شود؛ بیش از آن رد می‌شود و هیچ رکوردی نمی‌سازد.
    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Rejects_A_Sale_Above_The_Unloaded_Quantity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.FromInventory,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 10m,
            DischargedQuantityMt = 10.2m,
            ShortageMt = -0.2m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for rejected surplus sale.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.CreateSaleFromDirectDispatch(new DispatchDirectFromReceiptSaleCreateViewModel
        {
            TruckDispatchId = 1,
            CustomerId = 1,
            SaleDate = new DateTime(2026, 4, 25),
            QuantityMt = 10.5m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            InvoiceNumber = "SUR-OVER"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Null((await db.TruckDispatches.SingleAsync()).SalesTransactionId);
    }

    // کسری مسیر فروش را تغییر نمی‌دهد: مبنای فروش باز هم مقدار واقعیِ تخلیه‌شده است.
    [Fact]
    public async Task CreateSaleFromDirectDispatch_Get_Defaults_To_The_Real_Unloaded_Quantity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.FromInventory,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 10m,
            DischargedQuantityMt = 9.8m,
            ShortageMt = 0.2m
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called on GET.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var view = Assert.IsType<ViewResult>(await controller.CreateSaleFromDirectDispatch(1));
        var model = Assert.IsType<DispatchDirectFromReceiptSaleCreateViewModel>(view.Model);

        Assert.Equal(9.8m, model.QuantityMt);
        Assert.Equal(9.8m, model.DispatchSellableQuantityMt);
        Assert.Equal(10m, model.DispatchLoadedQuantityMt);
        Assert.Equal(0m, model.DispatchSurplusMt);
    }

    // فروش مستقیم از موتر باید Lineage واقعیِ همان موتر را روی خودِ فروش بنشاند تا عاید در
    // سود و زیان قرارداد خرید و محموله دیده شود؛ بدون این فیلدها فروش هیچ قرارداد و محموله‌ای نداشت.
    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Stamps_The_Real_Purchase_Contract_Lineage()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 10m,
            DischargedQuantityMt = 10.2m
        });
        await db.SaveChangesAsync();

        var controller = BuildDirectSaleController(db);

        Assert.IsType<RedirectToActionResult>(await controller.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 10.2m, invoiceNumber: "LIN-001")));

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Null(sale.ContractId);                    // قرارداد فروش ندارد
        Assert.Equal(1, sale.SourcePurchaseContractId);  // قرارداد خرید از Lineage واقعی
        Assert.Equal(1, sale.TruckDispatchId);           // موترِ منبع
        Assert.Null(sale.ShipmentId);                    // این مسیر محموله ندارد ⇒ حدس زده نمی‌شود
        Assert.Equal(1, (await db.TruckDispatches.SingleAsync()).SalesTransactionId);
    }

    // فروش قسمتی: ۱۰.۲ تخلیه، ۱۰.۰۵ فروش اول، ۰.۱۵ باقی‌مانده که باید قابل فروش بماند.
    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Supports_Partial_Sale_And_Then_The_Remainder()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedPartialSaleDispatchAsync(db);

        var controller = BuildDirectSaleController(db);
        Assert.IsType<RedirectToActionResult>(await controller.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 10.05m, invoiceNumber: "PART-001")));

        // باقی‌مانده روی فرم بعدی دقیقاً ۰.۱۵ است.
        var view = Assert.IsType<ViewResult>(await controller.CreateSaleFromDirectDispatch(1));
        var model = Assert.IsType<DispatchDirectFromReceiptSaleCreateViewModel>(view.Model);
        Assert.Equal(10.05m, model.DispatchAlreadySoldQuantityMt);
        Assert.Equal(0.15m, model.DispatchRemainingQuantityMt);
        Assert.Equal(0.15m, model.QuantityMt);

        Assert.IsType<RedirectToActionResult>(await controller.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 0.15m, invoiceNumber: "PART-002")));

        var sales = await db.SalesTransactions.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(2, sales.Count);
        Assert.All(sales, s => Assert.Equal(1, s.TruckDispatchId));
        Assert.All(sales, s => Assert.Equal(1, s.SourcePurchaseContractId));
        Assert.Equal(10.2m, sales.Sum(s => s.QuantityMt));

        // لینک یکتای موتر روی اولین فروش می‌ماند و فروش دوم آن را جابه‌جا نمی‌کند.
        Assert.Equal(sales[0].Id, (await db.TruckDispatches.SingleAsync()).SalesTransactionId);
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    // فروش بیشتر از باقی‌مانده باید رد شود و هیچ رکوردی نسازد.
    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Rejects_A_Sale_Above_The_Remaining_Quantity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedPartialSaleDispatchAsync(db);

        var controller = BuildDirectSaleController(db);
        Assert.IsType<RedirectToActionResult>(await controller.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 10.05m, invoiceNumber: "REM-001")));

        Assert.IsType<ViewResult>(await controller.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 0.2m, invoiceNumber: "REM-002")));
        Assert.False(controller.ModelState.IsValid);

        Assert.Single(await db.SalesTransactions.ToListAsync());
        Assert.Single(await db.LedgerEntries.ToListAsync());
    }

    // ثبت دوبارهٔ همان درخواست (کلیک دوباره) فروش تکراری نمی‌سازد: شمارهٔ فاکتور یکتاست.
    [Fact]
    public async Task CreateSaleFromDirectDispatch_Post_Does_Not_Create_A_Duplicate_Sale_On_Resubmit()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedPartialSaleDispatchAsync(db);

        var controller = BuildDirectSaleController(db);
        Assert.IsType<RedirectToActionResult>(await controller.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 10.2m, invoiceNumber: "DUP-001")));

        Assert.IsType<ViewResult>(await controller.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 10.2m, invoiceNumber: "DUP-001")));
        Assert.False(controller.ModelState.IsValid);

        Assert.Single(await db.SalesTransactions.ToListAsync());
        Assert.Single(await db.LedgerEntries.ToListAsync());
    }

    // لغو فروش موتر باید لینک یکتا را آزاد کند تا همان مقدار دوباره قابل فروش شود.
    [Fact]
    public async Task Cancelling_A_Direct_Truck_Sale_Frees_The_Dispatch_For_A_New_Sale()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        await SeedPartialSaleDispatchAsync(db);

        var dispatchController = BuildDirectSaleController(db);
        Assert.IsType<RedirectToActionResult>(await dispatchController.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 10.2m, invoiceNumber: "CAN-001")));

        var saleId = (await db.SalesTransactions.SingleAsync()).Id;
        var salesController = new SalesController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<SalesController>.Instance)
        {
            TempData = BuildTempData()
        };

        // PTG-P2-03 — ابطال فروش دیگر بدون دلیل انجام نمی‌شود.
        await salesController.Cancel(saleId, cancelReason: "آزمون آزادسازی موتر");

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Null(dispatch.SalesTransactionId);

        // باقی‌مانده دوباره کل مقدار تخلیه‌شده است و فروش تازه ثبت می‌شود.
        var view = Assert.IsType<ViewResult>(await dispatchController.CreateSaleFromDirectDispatch(1));
        var model = Assert.IsType<DispatchDirectFromReceiptSaleCreateViewModel>(view.Model);
        Assert.Equal(0m, model.DispatchAlreadySoldQuantityMt);
        Assert.Equal(10.2m, model.DispatchRemainingQuantityMt);

        Assert.IsType<RedirectToActionResult>(await dispatchController.CreateSaleFromDirectDispatch(
            BuildDirectSaleInput(quantityMt: 10.2m, invoiceNumber: "CAN-002")));

        Assert.Equal(1, await db.SalesTransactions.CountAsync(s => !s.IsCancelled));
        Assert.Equal(10.2m, await db.SalesTransactions.Where(s => !s.IsCancelled).SumAsync(s => s.QuantityMt));
    }

    private static DispatchController BuildDirectSaleController(ApplicationDbContext db)
        => new(
            db,
            new ThrowingStockService(new InvalidOperationException("StockService must not be called for a direct truck sale.")),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

    private static DispatchDirectFromReceiptSaleCreateViewModel BuildDirectSaleInput(
        decimal quantityMt,
        string invoiceNumber)
        => new()
        {
            TruckDispatchId = 1,
            CustomerId = 1,
            SaleDate = new DateTime(2026, 4, 25),
            QuantityMt = quantityMt,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            InvoiceNumber = invoiceNumber
        };

    // ۱۰ تن بارگیری، ۱۰.۲ تن تخلیه (۰.۲ اضافه‌بار) — مبنای سناریوهای فروش قسمتی.
    private static async Task SeedPartialSaleDispatchAsync(ApplicationDbContext db)
    {
        await SeedDirectDispatchAllocationAsync(db);
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 10m,
            DischargedQuantityMt = 10.2m,
            ShortageMt = -0.2m
        });
        await db.SaveChangesAsync();
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new InMemoryTempDataProvider());

    private static IUrlHelper BuildUrlHelper()
        => new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));

    private static string GetProjectFilePath(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
    }

    private static async Task SeedDirectDispatchAllocationAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.Trucks.AddRange(
            new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true },
            new Truck { Id = 2, PlateNumber = "TRK-02", IsActive = true });
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CTR-1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 22),
            LoadedQuantityMt = 100m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 4, 23),
            ReceivedQuantityMt = 100m
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            Status = LoadingReceiptAllocationStatus.TraceOnly,
            QuantityMt = 100m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            DestinationLocationId = 1
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

    private sealed class ThrowingStockService(Exception exception) : IStockService
    {
        public Task<decimal> GetFreeQuantityMtAsync(int productId, int? terminalId = null, int? contractId = null, int? inventoryBatchId = null, int? storageTankId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<decimal> GetTotalFreeQuantityMtAsync(int? terminalId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TankStockItem>> GetTankAvailabilityAsync(int productId, int contractId, DateTime? asOfUtc = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TankStockItem>>([]);

        public Task<IReadOnlyList<StockSummaryItem>> GetStockSummaryAsync(int? productId = null, int? contractId = null, int? terminalId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<StockCardItem>> GetStockCardAsync(int? productId = null, int? contractId = null, int? terminalId = null, int? storageTankId = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<StockMovementSummaryItem>> GetMovementSummaryAsync(int? productId = null, int? contractId = null, int? terminalId = null, int? storageTankId = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AcquireStockMutationLockAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureSufficientStockForMovementAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.FromException(exception);

        public Task EnsureMovementDoesNotCauseFutureNegativeStockAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureSufficientStockForSaleAsync(SalesTransaction sale, CancellationToken ct = default)
            => Task.FromException(exception);

        public Task EnsureSufficientStockForSaleAsync(SalesTransaction sale, int? sourcePurchaseContractId, CancellationToken ct = default)
            => Task.FromException(exception);
    }

    private sealed class TwoStepStockService(BusinessRuleException secondCallException) : IStockService
    {
        public int EnsureMovementCallCount { get; private set; }

        public Task<decimal> GetFreeQuantityMtAsync(int productId, int? terminalId = null, int? contractId = null, int? inventoryBatchId = null, int? storageTankId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<decimal> GetTotalFreeQuantityMtAsync(int? terminalId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TankStockItem>> GetTankAvailabilityAsync(int productId, int contractId, DateTime? asOfUtc = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TankStockItem>>([]);

        public Task<IReadOnlyList<StockSummaryItem>> GetStockSummaryAsync(int? productId = null, int? contractId = null, int? terminalId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<StockCardItem>> GetStockCardAsync(int? productId = null, int? contractId = null, int? terminalId = null, int? storageTankId = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<StockMovementSummaryItem>> GetMovementSummaryAsync(int? productId = null, int? contractId = null, int? terminalId = null, int? storageTankId = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AcquireStockMutationLockAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureSufficientStockForMovementAsync(InventoryMovement movement, CancellationToken ct = default)
        {
            EnsureMovementCallCount++;
            return EnsureMovementCallCount == 1
                ? Task.CompletedTask
                : Task.FromException(secondCallException);
        }

        public Task EnsureMovementDoesNotCauseFutureNegativeStockAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureSufficientStockForSaleAsync(SalesTransaction sale, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task EnsureSufficientStockForSaleAsync(SalesTransaction sale, int? sourcePurchaseContractId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
