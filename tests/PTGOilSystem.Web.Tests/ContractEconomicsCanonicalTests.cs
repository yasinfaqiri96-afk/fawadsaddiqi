using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سودِ قرارداد فقط از <see cref="ProfitAndLossService.BuildContractEconomicsAsync"/> می‌آید:
/// «سودِ محقق» فقط بخشِ فروخته‌شده را می‌شناسد و «سودِ چرخهٔ کامل» معیارِ جدایی است.
/// </summary>
public class ContractEconomicsCanonicalTests
{
    private const int PurchaseId = 5;
    private const int OtherPurchaseId = 6;
    private const int SaleContractId = 9;

    [Fact]
    public async Task Partial_Sale_Recognizes_Only_Sold_Share_And_Keeps_Lifecycle_Separate()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        db.SalesTransactions.Add(Sale(100, 40m, 24_000m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 40m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(40m, e.SoldQuantityMt);
        Assert.Equal(24_000m, e.RevenueUsd);
        Assert.Equal(0.4m, e.SoldShareRatio);
        Assert.Equal(20_000m, e.RealizedCostOfGoodsSoldUsd);
        Assert.Equal(400m, e.RealizedOperationalCostUsd);
        Assert.Equal(3_600m, e.RealizedNetProfitUsd);

        // چرخهٔ کامل: کلِ خرید و کلِ هزینه، چه فروخته شده باشد چه نه.
        Assert.Equal(51_000m, e.LifecycleTotalCostUsd);
        Assert.Equal(-27_000m, e.LifecycleMarginUsd);
    }

    [Fact]
    public async Task Unsold_Stock_Has_Zero_Realized_Profit_Not_A_Loss()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(0m, e.RealizedCostOfGoodsSoldUsd);
        Assert.Equal(0m, e.RealizedOperationalCostUsd);
        Assert.Equal(0m, e.RealizedNetProfitUsd);
        Assert.Equal(-51_000m, e.LifecycleMarginUsd);
    }

    [Fact]
    public async Task Expense_And_Loss_Linked_Only_Through_Loading_Belong_To_That_Purchase_Contract()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        // بدون ContractId؛ فقط بارگیری. مصرفِ رسمیِ بارگیری فیلدهای درون‌خطی را کنار می‌زند، پس
        // اگر اینجا شمرده نشود هیچ‌جا شمرده نمی‌شود.
        db.ExpenseTransactions.Add(Expense(1, 700m, loadingRegisterId: 1));
        db.LossEvents.Add(Loss(1, chargeableMt: 2m, loadingRegisterId: 1));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(700m, e.GeneralExpenseCostUsd);
        Assert.Equal(1_000m, e.LossCostUsd);
        Assert.Equal(0, e.UnvaluedLossCount);
    }

    [Theory]
    [InlineData(CostResponsibility.Seller, 0, 1_000)]   // فروشندهٔ قرارداد خرید = طرفِ بیرونی
    [InlineData(CostResponsibility.Buyer, 1_000, 0)]    // خریدارِ قرارداد خرید = خودِ شرکت
    [InlineData(CostResponsibility.Shared, 1_000, 0)]   // سهم ثبت نشده → بدونِ تغییر (شرکت)
    [InlineData(CostResponsibility.Unspecified, 1_000, 0)]
    public async Task Cost_Responsibility_On_Purchase_Contract(CostResponsibility responsibility, int companyBorne, int externalBorne)
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        var expense = Expense(1, 1_000m, contractId: PurchaseId);
        expense.CostResponsibility = responsibility;
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(companyBorne, e.GeneralExpenseCostUsd);
        Assert.Equal(externalBorne, e.ExternalPartyBorneExpenseUsd);
    }

    [Theory]
    [InlineData(CostResponsibility.Buyer, 0, 1_000)]    // خریدارِ قرارداد فروش = مشتری
    [InlineData(CostResponsibility.Seller, 1_000, 0)]   // فروشندهٔ قرارداد فروش = خودِ شرکت
    [InlineData(CostResponsibility.Shared, 1_000, 0)]
    public async Task Cost_Responsibility_On_Sale_Contract_And_Company_Pnl_Agree(
        CostResponsibility responsibility,
        int companyBorne,
        int externalBorne)
    {
        await using var db = NewDb();
        Seed(db);
        var expense = Expense(1, 1_000m, contractId: SaleContractId);
        expense.CostResponsibility = responsibility;
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var e = (await service.BuildContractEconomicsAsync([SaleContractId]))[SaleContractId];
        var company = await service.BuildCompanyAsync(new ManagementReportFilterViewModel
        {
            FromDate = new DateTime(2026, 1, 1),
            ToDate = new DateTime(2026, 12, 31)
        });

        Assert.Equal(companyBorne, e.GeneralExpenseCostUsd);
        Assert.Equal(externalBorne, e.ExternalPartyBorneExpenseUsd);
        Assert.Equal(companyBorne, company.OperatingExpenseUsd);
    }

    [Fact]
    public async Task Policy_Resolves_Buyer_And_Seller_Relative_To_Contract_Role()
    {
        Assert.Equal(CostBearer.ExternalParty, CostResponsibilityPolicy.Resolve(CostResponsibility.Buyer, ContractType.Sale));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(CostResponsibility.Buyer, ContractType.Purchase));
        Assert.Equal(CostBearer.ExternalParty, CostResponsibilityPolicy.Resolve(CostResponsibility.Seller, ContractType.Purchase));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(CostResponsibility.Seller, ContractType.Sale));
        Assert.Equal(CostBearer.SharedUndetermined, CostResponsibilityPolicy.Resolve(CostResponsibility.Shared, ContractType.Sale));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(null, null));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(CostResponsibility.Buyer, null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Unproven_Sale_Owner_Does_Not_Depend_On_Which_Contracts_Are_Requested()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.AddRange(Loading(1, PurchaseId, 100m, 500m), Loading(2, OtherPurchaseId, 100m, 500m));
        var sale = Sale(100, 10m, 6_000m);
        sale.SourcePurchaseContractId = OtherPurchaseId; // مسیرِ آخر
        db.SalesTransactions.Add(sale);
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 10m)); // مسیرِ مقدم
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var alone = (await service.BuildContractEconomicsAsync([OtherPurchaseId]))[OtherPurchaseId];
        var both = await service.BuildContractEconomicsAsync([PurchaseId, OtherPurchaseId]);

        Assert.Empty(alone.Sales);
        Assert.Empty(both[OtherPurchaseId].Sales);
        Assert.Equal(6_000m, both[PurchaseId].RevenueUsd);
        Assert.Equal(6_000m, both.Values.Sum(x => x.RevenueUsd));
    }

    [Fact]
    public async Task Untagged_Shipment_Expense_Is_Shared_By_Recorded_Contract_Quantity()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.AddRange(Loading(1, PurchaseId, 60m, 500m), Loading(2, OtherPurchaseId, 40m, 500m));
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "SH-1", QuantityMt = 100m });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { Id = 1, ShipmentId = 1, ContractId = PurchaseId, QuantityMt = 60m },
            new ShipmentContract { Id = 2, ShipmentId = 1, ContractId = OtherPurchaseId, QuantityMt = 40m });
        var shared = Expense(1, 1_000m);
        shared.ShipmentId = 1;
        db.ExpenseTransactions.Add(shared);
        await db.SaveChangesAsync();

        var economics = await new ProfitAndLossService(db).BuildContractEconomicsAsync([PurchaseId, OtherPurchaseId]);

        Assert.Equal(600m, economics[PurchaseId].SharedShipmentExpenseUsd);
        Assert.Equal(400m, economics[OtherPurchaseId].SharedShipmentExpenseUsd);
        Assert.Equal(600m, economics[PurchaseId].OperationalCostBaseUsd);
    }

    [Fact]
    public async Task Transport_Shortage_Without_Recorded_Loss_Is_Valued_At_Weighted_Average()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = PurchaseId,
            ProductId = 1,
            LoadedDate = new DateTime(2026, 4, 5),
            QuantityMt = 30m,
            Status = InventoryTransportLegStatus.Received
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 1,
            InventoryTransportLegId = 1,
            ReceiptDate = new DateTime(2026, 4, 8),
            ReceivedQuantityMt = 29m,
            ShortageQuantityMt = 1m
        });
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(500m, e.ShipmentLossCostUsd);
    }

    [Fact]
    public async Task Direct_Sale_From_A_Transport_Leg_Carries_Only_That_Legs_Specific_Costs()
    {
        await using var db = NewDb();
        Seed(db);
        SeedTwoLegScenario(db);
        // فروش مستقیمِ کلِ حملِ ۴۰ تنی، بدون ورود به مخزن.
        db.SalesTransactions.Add(Sale(100, 40m, 60_000m));
        db.SalesTransactionSourceAllocations.Add(new SalesTransactionSourceAllocation
        {
            Id = 1,
            SalesTransactionId = 100,
            TransportLegId = 2,
            SourcePurchaseContractId = PurchaseId,
            QuantityMt = 40m,
            AmountUsd = 60_000m
        });
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(40m, e.SoldQuantityMt);
        Assert.Equal(36_000m, e.RealizedCostOfGoodsSoldUsd);
        // عمومی (کرایهٔ بارگیری ۱۰۰) × ۴۰٪ = ۴۰؛ حملِ ۴۰ تنی کامل: ۸۰ + گمرک ۱٬۰۰۰؛
        // هیچ سهمی از کرایه، گمرک، مصرف و ضایعهٔ حملِ ۵۰ تنی.
        Assert.Equal(1_120m, e.RealizedOperationalCostUsd);
        Assert.Equal(22_880m, e.RealizedNetProfitUsd);
        // مبنای کامل دست‌نخورده: هر سند یک بار.
        Assert.Equal(100m + 100m + 1_700m + 80m + 1_500m + 1_000m + 720m, e.OperationalCostBaseUsd);
        // بهای دفتری = خرید + مصارفِ پولی؛ ارزشِ ضایعه (۷۲۰) دوباره روی خرید نمی‌آید.
        Assert.Equal(4_480m, e.RecordedExpenseCostUsd);
        Assert.Equal(94_480m, e.LifecycleTotalCostUsd);
        Assert.Equal(57_360m, e.UnsoldCostUsd);
    }

    [Fact]
    public async Task Sale_From_Tank_Carries_The_Costs_Of_The_Leg_That_Filled_The_Tank()
    {
        await using var db = NewDb();
        Seed(db);
        SeedTwoLegScenario(db);
        // حملِ ۵۰ تنی ۴۹ تن به مخزن داد؛ همان ۴۹ تن از مخزن فروخته شد. حملِ ۴۰ تنی هنوز در مسیر است.
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 1,
            InventoryTransportLegId = 1,
            ReceiptDate = new DateTime(2026, 4, 8),
            ReceivedQuantityMt = 49m,
            ShortageQuantityMt = 1m,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 10,
            ProductId = 1,
            ContractId = PurchaseId,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 8),
            QuantityMt = 49m
        });
        db.SalesTransactions.Add(Sale(100, 49m, 73_500m));
        db.InventoryMovements.Add(StockOut(11, PurchaseId, saleId: 100, quantityMt: 49m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        // حملِ ۵۰ تنی کامل (۱۰۰ + ۱٬۷۰۰ + ۱٬۵۰۰ + ۷۲۰) + عمومی ۱۰۰ × ۴۹٪؛ هیچ سهمی از حملِ ۴۰ تنیِ در مسیر.
        Assert.Equal(4_020m + 49m, e.RealizedOperationalCostUsd);
    }

    [Fact]
    public async Task Sale_From_Tank_Keeps_The_Sold_Share_Rule_For_Leg_Costs()
    {
        await using var db = NewDb();
        Seed(db);
        SeedTwoLegScenario(db);
        db.SalesTransactions.Add(Sale(100, 40m, 60_000m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 40m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(0.4m, e.SoldShareRatio);
        Assert.Equal(decimal.Round(e.OperationalCostBaseUsd * 0.4m, 2), e.RealizedOperationalCostUsd);
    }

    [Fact]
    public async Task Leg_Continued_From_Terminal_Stock_Carries_The_First_Legs_Costs_To_Its_Sales()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        // حملِ ۱ تا ترمینال (رسید به موجودی)، حملِ ۲ از حرکتِ همان رسید؛ نیمِ حملِ ۲ مستقیم فروخته شد.
        db.InventoryTransportLegs.AddRange(ChainLeg(1, 100m), ChainLeg(2, 100m));
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 10,
            ProductId = 1,
            ContractId = PurchaseId,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 8),
            QuantityMt = 100m
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 1,
            InventoryTransportLegId = 1,
            ReceiptDate = new DateTime(2026, 4, 8),
            ReceivedQuantityMt = 100m,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            InventoryMovementId = 10
        });
        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation
        {
            Id = 1,
            InventoryTransportLegId = 2,
            SourcePurchaseContractId = PurchaseId,
            SourceInventoryMovementId = 10,
            QuantityMt = 100m
        });
        db.ExpenseTransactions.AddRange(ChainLegExpense(1, 1_000m, legId: 1), ChainLegExpense(2, 500m, legId: 2));
        db.SalesTransactions.Add(Sale(100, 50m, 35_000m));
        db.SalesTransactionSourceAllocations.Add(new SalesTransactionSourceAllocation
        {
            Id = 1,
            SalesTransactionId = 100,
            TransportLegId = 2,
            SourcePurchaseContractId = PurchaseId,
            QuantityMt = 50m,
            AmountUsd = 35_000m
        });
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.False(e.IsFullyDisposed);
        // حملِ ۲: ۵۰۰ × ۵۰٪؛ حملِ ۱ هم به همان ۵۰ تنِ فروخته‌شده رسیده: ۱٬۰۰۰ × ۵۰٪.
        Assert.Equal(750m, e.RealizedOperationalCostUsd);
        Assert.Equal(25_750m, e.UnsoldCostUsd);

        static InventoryTransportLeg ChainLeg(int id, decimal quantityMt) => new()
        {
            Id = id,
            SourcePurchaseContractId = PurchaseId,
            ProductId = 1,
            LoadedDate = new DateTime(2026, 4, 5),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Received
        };

        static ExpenseTransaction ChainLegExpense(int id, decimal amountUsd, int legId)
        {
            var expense = Expense(id, amountUsd, contractId: PurchaseId);
            expense.TransportLegId = legId;
            return expense;
        }
    }

    [Theory]
    [InlineData(2, 2)] // ضایعهٔ قابلِ شارژ
    [InlineData(2, 0)] // ضایعه در حدِ مجاز
    public async Task Fully_Sold_Or_Lost_Contract_Has_No_Unsold_Cost_And_Realizes_All_Costs(
        int differenceMt, int chargeableMt)
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        var loss = Loss(1, chargeableMt, loadingRegisterId: 1);
        loss.DifferenceQuantityMt = differenceMt;
        db.LossEvents.Add(loss);
        db.SalesTransactions.Add(Sale(100, 98m, 68_600m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 98m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.True(e.IsFullyDisposed);
        Assert.Equal(1m, e.SoldShareRatio);
        Assert.Equal(0m, e.UnsoldCostUsd);
        Assert.Equal(e.PurchaseValueUsd + e.RecordedExpenseCostUsd,
            e.RealizedCostOfGoodsSoldUsd + e.RealizedOperationalCostUsd);
        // همهٔ هزینه به فروش رسیده: سودِ محقق = فروش − خرید − مصارف.
        Assert.Equal(68_600m - 50_000m - 1_000m, e.RealizedNetProfitUsd);
        Assert.Equal(e.LifecycleMarginUsd, e.RealizedNetProfitUsd);
    }

    /// <summary>
    /// سناریوی P-001: دو بارگیریِ ۵۰ تنی × ۹۰۰؛ حملِ ۵۰ تنی (۱ تن ضایعه، ۰٫۸ قابل‌جبران، کرایهٔ خالص
    /// ۱٬۷۰۰، گمرک ۱٬۵۰۰، مصرف ۱۰۰) و حملِ ۴۰ تنی (گمرک ۱٬۰۰۰، مصرف ۸۰).
    /// </summary>
    private static void SeedTwoLegScenario(ApplicationDbContext db)
    {
        db.LoadingRegisters.AddRange(Loading(1, PurchaseId, 50m, 900m), Loading(2, PurchaseId, 50m, 900m));
        db.ExpenseTransactions.AddRange(
            Expense(1, 50m, contractId: PurchaseId, loadingRegisterId: 1),
            Expense(2, 50m, contractId: PurchaseId, loadingRegisterId: 2),
            LegExpense(3, 100m, legId: 1),
            LegExpense(4, 1_700m, legId: 1),
            LegExpense(5, 80m, legId: 2));
        db.InventoryTransportLegs.AddRange(
            Leg(1, 50m),
            Leg(2, 40m));
        db.CustomsDeclarations.AddRange(
            new CustomsDeclaration { Id = 1, TransportLegId = 1, DeclarationDate = new DateTime(2026, 4, 6), TotalUsd = 1_500m },
            new CustomsDeclaration { Id = 2, TransportLegId = 2, DeclarationDate = new DateTime(2026, 4, 6), TotalUsd = 1_000m });
        db.LossEvents.Add(new LossEvent
        {
            Id = 1,
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = PurchaseId,
            TransportLegId = 1,
            EventDate = new DateTime(2026, 4, 8),
            ExpectedQuantityMt = 50m,
            ActualQuantityMt = 49m,
            DifferenceQuantityMt = 1m,
            AllowableLossMt = 0.2m,
            ChargeableLossMt = 0.8m
        });

        static InventoryTransportLeg Leg(int id, decimal quantityMt) => new()
        {
            Id = id,
            SourcePurchaseContractId = PurchaseId,
            ProductId = 1,
            LoadedDate = new DateTime(2026, 4, 5),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Received
        };

        static ExpenseTransaction LegExpense(int id, decimal amountUsd, int legId)
        {
            var expense = Expense(id, amountUsd, contractId: PurchaseId);
            expense.TransportLegId = legId;
            return expense;
        }
    }

    [Fact]
    public async Task Contract_Pnl_Report_Shows_The_Canonical_Realized_And_Lifecycle_Values()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        db.SalesTransactions.Add(Sale(100, 40m, 24_000m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 40m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);
        var view = Assert.IsType<ViewResult>(
            await new ReportsController(db).ContractPnl(new ManagementReportFilterViewModel { ContractId = PurchaseId }));
        var row = Assert.Single(Assert.IsType<ContractPnlReportViewModel>(view.Model).PurchaseRows);

        Assert.Equal(e.RealizedNetProfitUsd, row.RealizedNetProfitUsd);
        Assert.Equal(e.LifecycleMarginUsd, row.GrossMarginUsd);
        Assert.Equal(e.LifecycleTotalCostUsd, row.TotalCostUsd);
        Assert.NotEqual(row.RealizedNetProfitUsd, row.GrossMarginUsd);
    }

    [Fact]
    public async Task Partnership_Profit_Is_The_Canonical_Realized_Profit_And_Unsold_Cost_Is_Shared_Separately()
    {
        await using var db = NewDb();
        Seed(db);
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "PA-1", Name = "Partner A", IsActive = true },
            new Partner { Id = 2, Code = "PA-2", Name = "Partner B", IsActive = true });
        var contract = await db.Contracts.FindAsync(PurchaseId);
        contract!.OwnershipType = ContractOwnershipType.Partnership;
        contract.SaleProceedsHolderPartnerId = 2;
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = PurchaseId, PartnerId = 1, SharePercent = 50m },
            new ContractPartner { ContractId = PurchaseId, PartnerId = 2, SharePercent = 50m });
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        db.SalesTransactions.Add(Sale(100, 40m, 24_000m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 40m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);
        var statement = await new PTGOilSystem.Web.Services.PartyStatements.PartnershipStatementService(db)
            .BuildForContractAsync(PurchaseId);

        Assert.NotNull(statement);
        Assert.Equal(e.RevenueUsd, statement!.SalesUsd);
        Assert.Equal(e.RealizedNetProfitUsd, statement.BookProfitUsd);
        Assert.Equal(3_600m, statement.BookProfitUsd);
        Assert.Equal(e.RealizedNetProfitUsd, statement.Partners.Sum(p => p.ProfitShareUsd));

        // کلِ هزینه ۵۱٬۰۰۰، فروخته‌شده ۲۰٬۴۰۰ → ۳۰٬۶۰۰ هزینهٔ فروخته‌نشده، نصف‌نصف.
        Assert.Equal(30_600m, statement.UnrealizedCostCarriedUsd);
        Assert.All(statement.Partners, p => Assert.Equal(15_300m, p.UnsoldCostShareUsd));
        // برابرسازیِ هزینه بین شرکا همان مفادِ کامل است: ۱۸۰۰ − ۱۵۳۰۰ = −۱۳۵۰۰ هر شریک.
        Assert.All(statement.Partners, p => Assert.Equal(
            decimal.Round(e.LifecycleMarginUsd / 2m, 2),
            p.ProfitShareUsd - p.UnsoldCostShareUsd));
    }

    // ---- helpers ----

    private static async Task<ContractEconomicsSnapshot> EconomicsAsync(ApplicationDbContext db, int contractId)
        => (await new ProfitAndLossService(db).BuildContractEconomicsAsync([contractId]))[contractId];

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TANK-A" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "GEN", Name = "General" });
        db.Contracts.AddRange(
            Contract(PurchaseId, "P-005", ContractType.Purchase),
            Contract(OtherPurchaseId, "P-006", ContractType.Purchase),
            Contract(SaleContractId, "S-009", ContractType.Sale));
    }

    private static Contract Contract(int id, string number, ContractType type)
        => new()
        {
            Id = id,
            ContractNumber = number,
            ContractType = type,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = type == ContractType.Purchase ? 1 : null,
            CustomerId = type == ContractType.Sale ? 1 : null,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        };

    private static LoadingRegister Loading(int id, int contractId, decimal quantityMt, decimal priceUsd)
        => new()
        {
            Id = id,
            ContractId = contractId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 2),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd
        };

    private static ExpenseTransaction Expense(int id, decimal amountUsd, int? contractId = null, int? loadingRegisterId = null)
        => new()
        {
            Id = id,
            ExpenseTypeId = 1,
            ExpenseDate = new DateTime(2026, 4, 10),
            Amount = amountUsd,
            AmountUsd = amountUsd,
            Currency = "USD",
            ContractId = contractId,
            LoadingRegisterId = loadingRegisterId
        };

    private static LossEvent Loss(int id, decimal chargeableMt, int? loadingRegisterId = null)
        => new()
        {
            Id = id,
            Stage = LossEventStage.LoadingDifference,
            ProductId = 1,
            EventDate = new DateTime(2026, 4, 3),
            DifferenceQuantityMt = chargeableMt,
            ChargeableLossMt = chargeableMt,
            LoadingRegisterId = loadingRegisterId
        };

    private static SalesTransaction Sale(int id, decimal quantityMt, decimal totalUsd)
        => new()
        {
            Id = id,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = $"INV-{id}",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = quantityMt,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalUsd = totalUsd,
            TotalInCurrency = totalUsd,
            Currency = "USD"
        };

    private static InventoryMovement StockOut(int id, int contractId, int saleId, decimal quantityMt)
        => new()
        {
            Id = id,
            ProductId = 1,
            ContractId = contractId,
            TerminalId = 1,
            StorageTankId = 1,
            SalesTransactionId = saleId,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 22),
            QuantityMt = quantityMt
        };
}
