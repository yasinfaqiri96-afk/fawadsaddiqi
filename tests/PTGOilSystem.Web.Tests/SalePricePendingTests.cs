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
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// فروش با قیمت باز: بار تحویل و موجودی کم می‌شود، ولی تا تعیین قیمت هیچ سند مالی، طلب، عاید
/// یا سود قطعی ساخته نمی‌شود. تعیین قیمت همان اسناد فروش عادی را دقیقاً یک‌بار می‌سازد.
/// </summary>
public sealed class SalePricePendingTests
{
    private static readonly DateTime SaleDate = new(2026, 4, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Pending_Sale_Moves_Stock_But_Creates_No_Ledger_Or_Amount()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await BuildController(db).Create(PendingModel("INV-P1", 30m));

        Assert.IsType<RedirectToActionResult>(result);
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.True(sale.IsSalePricePending);
        Assert.Equal(0m, sale.TotalUsd);
        Assert.Null(sale.AppliedFxRateToUsd);
        Assert.Empty(await db.LedgerEntries.ToListAsync());

        var stockOut = await db.InventoryMovements.SingleAsync(m => m.Direction == MovementDirection.Out);
        Assert.Equal(30m, stockOut.QuantityMt);
        Assert.Equal(sale.Id, stockOut.SalesTransactionId);
        Assert.Equal(70m, await new StockService(db).GetFreeQuantityMtAsync(1, terminalId: 1, storageTankId: 1));

        var allocation = await db.SalesTransactionSourceAllocations.SingleAsync();
        Assert.Equal(30m, allocation.QuantityMt);
        Assert.Equal(2, allocation.SourcePurchaseContractId);
    }

    [Fact]
    public async Task Pending_Sale_Is_Blocked_When_Stock_Is_Insufficient()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await BuildController(db).Create(PendingModel("INV-P2", 130m));

        Assert.IsType<ViewResult>(result);
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
    }

    [Fact]
    public async Task SetPrice_Posts_The_Sale_Ledger_Once_And_Reprices_Source_Shares()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P3", 30m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();

        var result = await BuildController(db).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var priced = await db.SalesTransactions.AsNoTracking().SingleAsync();
        Assert.False(priced.IsSalePricePending);
        Assert.Equal(15000m, priced.TotalUsd);
        Assert.Equal(500m, priced.UnitPriceUsd);
        Assert.Equal(1m, priced.AppliedFxRateToUsd);
        Assert.NotNull(priced.SalePriceFixedAtUtc);
        Assert.Equal(sale.SaleDate, priced.SaleDate);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);
        Assert.Equal(15000m, ledger.AmountUsd);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(sale.SaleDate.Date, ledger.EntryDate.Date);
        Assert.Equal(15000m, (await db.SalesTransactionSourceAllocations.SingleAsync()).AmountUsd);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.Diff != null && a.Diff.Contains("IsSalePricePending"));

        // قطعی‌سازی دوباره (دوبار کلیک / کاربر دوم) چیزی نمی‌سازد.
        var second = await BuildController(db).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = priced.Version,
            Currency = "USD",
            UnitPriceInCurrency = 900m
        });
        Assert.IsType<RedirectToActionResult>(second);
        Assert.Single(await db.LedgerEntries.ToListAsync());
        Assert.Equal(15000m, (await db.SalesTransactions.AsNoTracking().SingleAsync()).TotalUsd);
    }

    [Fact]
    public async Task SetPrice_With_A_Stale_Version_Changes_Nothing()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P4", 10m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();

        // کاربر دیگری در این فاصله یادداشت را تغییر داده و نسخه جلو رفته است.
        await using (var other = new ApplicationDbContext(options))
        {
            var tracked = await other.SalesTransactions.SingleAsync();
            tracked.Notes = "changed by another user";
            await other.SaveChangesAsync();
        }

        await using var fresh = new ApplicationDbContext(options);
        var result = await BuildController(fresh).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });

        Assert.IsType<RedirectToActionResult>(result);
        await using var check = new ApplicationDbContext(options);
        Assert.True((await check.SalesTransactions.SingleAsync()).IsSalePricePending);
        Assert.Empty(await check.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task SetPrice_For_A_Delivery_In_A_Closed_Period_Posts_In_The_Open_Period()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P5", 10m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();
        db.OperationalPeriodLocks.Add(new OperationalPeriodLock { LockedThroughDate = SaleDate.AddDays(5), IsActive = true });
        await db.SaveChangesAsync();

        await using var fresh = new ApplicationDbContext(options);
        var result = await BuildController(fresh).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });

        // دورهٔ تحویل بسته است: سند فروش در دورهٔ باز (امروز) ثبت می‌شود و دورهٔ بسته دست نمی‌خورد.
        Assert.IsType<RedirectToActionResult>(result);
        await using var check = new ApplicationDbContext(options);
        var priced = await check.SalesTransactions.SingleAsync();
        Assert.False(priced.IsSalePricePending);
        Assert.Equal(SaleDate, priced.SaleDate);
        var ledger = await check.LedgerEntries.SingleAsync();
        Assert.True(ledger.EntryDate.Date > SaleDate.AddDays(5));
        Assert.Equal(ledger.EntryDate.Date, priced.SalePriceAccountingDate!.Value.Date);
        Assert.Contains("قیمت‌گذاری در دورهٔ باز", ledger.Description);
    }

    [Fact]
    public async Task The_Closed_Period_Still_Protects_Every_Other_Change_Of_A_Pending_Sale()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P5B", 10m));
        db.OperationalPeriodLocks.Add(new OperationalPeriodLock { LockedThroughDate = SaleDate.AddDays(5), IsActive = true });
        await db.SaveChangesAsync();

        await using var fresh = new ApplicationDbContext(options);
        var sale = await fresh.SalesTransactions.SingleAsync();
        sale.QuantityMt = 5m;
        await Assert.ThrowsAsync<PTGOilSystem.Web.Services.OperationalPeriod.OperationalPeriodLockedException>(() => fresh.SaveChangesAsync());

        await using var other = new ApplicationDbContext(options);
        var cancelled = await other.SalesTransactions.SingleAsync();
        cancelled.IsCancelled = true;
        await Assert.ThrowsAsync<PTGOilSystem.Web.Services.OperationalPeriod.OperationalPeriodLockedException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task SetPrice_On_An_Open_Period_Uses_The_Delivery_Date()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P5C", 10m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();

        await BuildController(db).SetPrice(sale.Id, new SalePriceFixViewModel { Version = sale.Version, Currency = "USD", UnitPriceInCurrency = 500m });

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(SaleDate.Date, ledger.EntryDate.Date);
        Assert.Equal(SaleDate.Date, (await db.SalesTransactions.AsNoTracking().SingleAsync()).SalePriceAccountingDate!.Value.Date);
        Assert.DoesNotContain("قیمت‌گذاری در دورهٔ باز", ledger.Description);
    }

    [Fact]
    public void Loading_Direct_Sale_Draft_Builds_A_Pending_Sale_Without_Amount_Or_Rate()
    {
        var loading = new LoadingRegister
        {
            Id = 7,
            ContractId = 2,
            ProductId = 1,
            Contract = new Contract { Id = 2, CompanyId = 1, ContractType = ContractType.Purchase }
        };
        var line = new PTGOilSystem.Web.Models.Loading.LoadingReceiptAllocationLineInput
        {
            QuantityMt = 12m,
            SaleDate = SaleDate,
            SaleCurrency = "AFN",
            SaleIsPricePending = true,
            SaleCustomerId = 1,
            SaleInvoiceNumber = "INV-LD"
        };

        var draft = PTGOilSystem.Web.Services.LoadingReceipts.LoadingDirectSaleDraftService.Build(
            line, loading, SalePendingPricing.NeutralConversion(line.SaleCurrency, SaleDate));

        Assert.True(draft.Sale.IsSalePricePending);
        Assert.Equal("AFN", draft.Sale.Currency);
        Assert.Equal(0m, draft.Sale.TotalUsd);
        Assert.Null(draft.Sale.AppliedFxRateToUsd);
        Assert.False(SalePendingPricing.ShouldPostLedger(draft.Sale));
        Assert.Equal(12m, draft.Sale.QuantityMt);
    }

    [Fact]
    public void Export_Leaves_Pending_Amounts_Empty_Instead_Of_Zero()
    {
        var pending = new SalesListItemViewModel { IsSalePricePending = true, QuantityMt = 5m };
        var final = new SalesListItemViewModel { UnitPriceInCurrency = 400m, TotalInCurrency = 2000m, TotalUsd = 2000m };

        Assert.Null(pending.ReportUnitPriceInCurrency);
        Assert.Null(pending.ReportTotalUsd);
        Assert.Equal(SalePricingText.PendingSalePrice, pending.PriceStatusName);
        Assert.Equal(2000m, final.ReportTotalUsd);
    }

    [Fact]
    public async Task Cancelling_A_Pending_Sale_Returns_Stock_Without_Any_Ledger_Row()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P6", 30m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();

        var result = await BuildController(db).Cancel(sale.Id, "مشتری بار را پس داد", version: sale.Version);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True((await db.SalesTransactions.AsNoTracking().SingleAsync()).IsCancelled);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Equal(100m, await new StockService(db).GetFreeQuantityMtAsync(1, terminalId: 1, storageTankId: 1));

        var priceAfterCancel = await BuildController(db).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });
        Assert.IsType<RedirectToActionResult>(priceAfterCancel);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Quantity_Edit_Of_A_Pending_Sale_Reposts_Stock_Only()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P7", 30m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();

        var result = await BuildController(db).Edit(sale.Id, new SalesCreateViewModel
        {
            Version = sale.Version,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.TerminalStock,
            SaleDate = sale.SaleDate,
            InvoiceNumber = sale.InvoiceNumber,
            Currency = "USD",
            IsSalePricePending = true,
            QuantityMt = 20m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var edited = await db.SalesTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(20m, edited.QuantityMt);
        Assert.True(edited.IsSalePricePending);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Equal(80m, await new StockService(db).GetFreeQuantityMtAsync(1, terminalId: 1, storageTankId: 1));
    }

    [Fact]
    public async Task Advance_Receipt_On_A_Pending_Sale_Settles_After_The_Price_Is_Set()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P8", 30m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = SaleDate,
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CustomerId = 1,
                SalesTransactionId = sale.Id,
                Amount = 5000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 5000m
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = SaleDate,
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CustomerId = 1,
                Amount = 1000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 1000m
            });
        await db.SaveChangesAsync();

        var pendingDetails = await DetailsAsync(db, sale.Id);
        Assert.Equal(SalePricingState.AwaitingSalePrice, pendingDetails.PricingState);
        Assert.Equal(5000m, pendingDetails.ReceivedUsd);
        Assert.Equal(0m, pendingDetails.ReceivableBalanceUsd);
        Assert.Equal(0m, pendingDetails.OverpaymentUsd);

        var applications = new CustomerReceiptApplicationService(db);
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => applications.ApplyAsync(
            new CustomerReceiptApplyRequest(2, [new CustomerReceiptApplicationLine(sale.Id, 500m)], "tester")));
        Assert.Equal("CUSTOMER_APPLICATION_SALE_PRICE_PENDING", refused.Code);
        Assert.Empty(await applications.GetOpenSalesAsync(1));

        await BuildController(db).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });

        var pricedDetails = await DetailsAsync(db, sale.Id);
        Assert.Equal(SalePricingState.Final, pricedDetails.PricingState);
        Assert.Equal(10000m, pricedDetails.ReceivableBalanceUsd);
    }

    [Fact]
    public async Task Purchase_Price_Status_Comes_From_The_Source_Contract()
    {
        await using var db = NewDb();
        Seed(db, purchasePriceKnown: false);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P9", 30m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();

        var both = await DetailsAsync(db, sale.Id);
        Assert.Equal(SalePricingState.AwaitingBothPrices, both.PricingState);
        Assert.Equal(2, Assert.Single(both.PendingPurchaseContracts).ContractId);

        await BuildController(db).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });
        Assert.Equal(SalePricingState.AwaitingPurchasePrice, (await DetailsAsync(db, sale.Id)).PricingState);

        var contract = await db.Contracts.SingleAsync(c => c.Id == 2);
        contract.ManualFinalPriceUsd = 430m;
        await db.SaveChangesAsync();
        Assert.Equal(SalePricingState.Final, (await DetailsAsync(db, sale.Id)).PricingState);
    }

    [Fact]
    public async Task Profit_Engine_Never_Counts_A_Pending_Sale_As_Revenue()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P10", 30m));
        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync();

        var pnl = new ProfitAndLossService(db);
        var pending = await pnl.BuildForSalesAsync([sale.Id]);
        Assert.Equal(0m, pending.RevenueUsd);
        Assert.Equal(0m, pending.CostOfGoodsSoldUsd);
        Assert.Equal(1, pending.UncostedSaleCount);
        Assert.Equal(PnlConfidence.NeedsReview, pending.Confidence);

        var attribution = await new SaleContractAttributionReader(db).LoadPurchaseContractSalesAsync([2]);
        Assert.False(attribution.SalesByContract.TryGetValue(2, out var pendingShares) && pendingShares.Count > 0);

        await BuildController(db).SetPrice(sale.Id, new SalePriceFixViewModel
        {
            Version = sale.Version,
            Currency = "USD",
            UnitPriceInCurrency = 500m
        });

        var priced = await new ProfitAndLossService(db).BuildForSalesAsync([sale.Id]);
        Assert.Equal(15000m, priced.RevenueUsd);
        var pricedShares = (await new SaleContractAttributionReader(db).LoadPurchaseContractSalesAsync([2])).SalesByContract[2];
        Assert.Equal(15000m, Assert.Single(pricedShares).AmountUsd);
    }

    [Fact]
    public async Task Index_Filters_Pending_Sales_And_Keeps_Them_Out_Of_The_Sales_Total()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await BuildController(db).Create(PendingModel("INV-P11", 10m));
        var priced = PendingModel("INV-F11", 10m);
        priced.IsSalePricePending = false;
        priced.UnitPriceInCurrency = 400m;
        await BuildController(db).Create(priced);

        var controller = BuildController(db);
        var all = Assert.IsType<SalesIndexViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model);
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(4000m, (decimal)controller.ViewBag.SumTotalUsd);
        Assert.Equal(1, (int)controller.ViewBag.PendingPriceCount);

        var pendingOnly = Assert.IsType<SalesIndexViewModel>(Assert.IsType<ViewResult>(
            await BuildController(db).Index(new SalesIndexFilterViewModel { PriceStatus = "pending" })).Model);
        Assert.True(Assert.Single(pendingOnly.Items).IsSalePricePending);
    }

    [Fact]
    public async Task Final_Price_Sale_Still_Posts_Its_Ledger_Immediately()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var model = PendingModel("INV-F12", 10m);
        model.IsSalePricePending = false;
        model.UnitPriceInCurrency = 400m;

        await BuildController(db).Create(model);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.False(sale.IsSalePricePending);
        Assert.Equal(4000m, (await db.LedgerEntries.SingleAsync()).AmountUsd);
    }

    private static SalesCreateViewModel PendingModel(string invoice, decimal quantity) => new()
    {
        SaleStage = SaleStage.TerminalStock,
        CompanyId = 1,
        CustomerId = 1,
        ProductId = 1,
        SourceTerminalId = 1,
        SourceStorageTankId = 1,
        SaleDate = SaleDate,
        QuantityMt = quantity,
        Currency = "USD",
        IsSalePricePending = true,
        InvoiceNumber = invoice
    };

    private static void Seed(ApplicationDbContext db, bool purchasePriceKnown = true)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Herat Market" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-01", ProductId = 1, CapacityMt = 1000m });
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier 2" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-002",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 2,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 1000m,
            PricingMethod = purchasePriceKnown ? PricingMethod.Fixed : PricingMethod.FormulaPlatts,
            UnitPriceUsd = purchasePriceKnown ? 430m : null
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-1"
        });
    }

    private static async Task<SalesDetailsViewModel> DetailsAsync(ApplicationDbContext db, int saleId)
        => Assert.IsType<SalesDetailsViewModel>(Assert.IsType<ViewResult>(await BuildController(db).Details(saleId)).Model);

    private static DbContextOptions<ApplicationDbContext> NewOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"sale-price-pending-{Guid.NewGuid():N}")
            .Options;

    private static ApplicationDbContext NewDb() => new(NewOptions());

    private static SalesController BuildController(ApplicationDbContext db)
        => new(db, new StockService(db), new AuditService(db), NullLogger<SalesController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            Url = new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()))
        };

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
