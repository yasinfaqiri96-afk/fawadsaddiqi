using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Inventory;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class InventoryControllerTests
{
    [Fact]
    public async Task Index_Uses_StorageTank_DisplayName()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Main Terminal" });
        db.StorageTanks.Add(new StorageTank
        {
            Id = 1,
            TerminalId = 1,
            TankCode = "TK-001",
            DisplayName = "مخزن مرکزی شماره ۱"
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ProductId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 6, 29),
            QuantityMt = 10m
        });
        await db.SaveChangesAsync();

        var controller = new InventoryController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<InventoryController>.Instance)
        {
            TempData = BuildTempData()
        };

        var view = Assert.IsType<ViewResult>(await controller.Index(q: null));
        var model = Assert.IsType<InventoryIndexViewModel>(view.Model);
        Assert.Equal("مخزن مرکزی شماره ۱", Assert.Single(model.Items).StorageTankCode);
    }

    [Fact]
    public async Task Create_Post_Blocks_Outgoing_Movement_When_Stock_Is_Insufficient()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Main Terminal" });
        await db.SaveChangesAsync();

        var controller = new InventoryController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<InventoryController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new InventoryMovementCreateViewModel
        {
            ProductId = 1,
            TerminalId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 23),
            QuantityMt = 10m
        }, Guid.NewGuid().ToString("N"));

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<InventoryMovementCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("موجودی کافی"));
    }

    [Fact]
    public async Task StockSummary_Uses_Service_Aligned_Inventory_Logic()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Main Terminal" });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = 100m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Adjustment,
                MovementDate = new DateTime(2026, 4, 21),
                QuantityMt = 5m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 22),
                QuantityMt = 20m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Transfer,
                MovementDate = new DateTime(2026, 4, 23),
                QuantityMt = 10m
            });
        await db.SaveChangesAsync();

        var controller = new InventoryController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<InventoryController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.StockSummary();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryStockSummaryIndexViewModel>(view.Model);
        var row = Assert.Single(model.Rows);
        Assert.Equal(75m, row.FreeQuantityMt);
    }

    [Fact]
    public async Task StockCard_Uses_Service_Aligned_Running_Balance()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Main Terminal" });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = 100m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 21),
                QuantityMt = 30m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Adjustment,
                MovementDate = new DateTime(2026, 4, 22),
                QuantityMt = 10m
            });
        await db.SaveChangesAsync();

        var controller = new InventoryController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<InventoryController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.StockCard(new InventoryStockCardFilterViewModel
        {
            ProductId = 1,
            TerminalId = 1,
            FromDate = new DateTime(2026, 4, 21),
            ToDate = new DateTime(2026, 4, 22)
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryStockCardViewModel>(view.Model);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(-30m, model.Rows[0].SignedQuantityMt);
        Assert.Equal(70m, model.Rows[0].RunningBalanceMt);
        Assert.Equal(10m, model.Rows[1].SignedQuantityMt);
        Assert.Equal(80m, model.Rows[1].RunningBalanceMt);
    }

    [Fact]
    public async Task Create_Post_Persists_Movement_And_Writes_Audit_Log()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Main Terminal" });
        await db.SaveChangesAsync();

        var controller = new InventoryController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<InventoryController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new InventoryMovementCreateViewModel
        {
            ProductId = 1,
            TerminalId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 23),
            QuantityMt = 25m,
            ReferenceDocument = "GRN-1",
            Notes = "Initial receipt"
        }, Guid.NewGuid().ToString("N"));

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var movement = await db.InventoryMovements.SingleAsync();
        Assert.Equal(25m, movement.QuantityMt);

        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal(nameof(InventoryMovement), log.EntityName);
        Assert.Equal("Insert", log.Action);
        Assert.Contains("QuantityMt=25.0000", log.Diff);
    }

    [Fact]
    public async Task StockCard_ToDate_IncludesMovementsLaterInTheSelectedDay()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options);
        var day = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Products.Add(new Product { Id = 1, Code = "DATE", Name = "تیل" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "DATE", Name = "ترمینال" });
        db.InventoryMovements.Add(new InventoryMovement { ProductId = 1, TerminalId = 1,
            Direction = MovementDirection.In, QuantityMt = 10m, MovementDate = day.AddHours(15) });
        await db.SaveChangesAsync();
        var controller = new InventoryController(db, new StockService(db), new AuditService(db),
            NullLogger<InventoryController>.Instance) { TempData = BuildTempData() };
        var view = Assert.IsType<ViewResult>(await controller.StockCard(new InventoryStockCardFilterViewModel
            { ProductId = 1, FromDate = day, ToDate = day }));
        Assert.Single(Assert.IsType<InventoryStockCardViewModel>(view.Model).Rows);
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new InMemoryTempDataProvider());

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
