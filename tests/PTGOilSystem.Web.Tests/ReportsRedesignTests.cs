using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Balance;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// بازطراحی گزارش‌ها: ستون‌های تازه از منبع درست پر می‌شوند، هیچ پولی دو بار شمرده نمی‌شود،
/// جمع کارت‌ها روی همهٔ نتایج فیلترشده است و خروجی همان ستون‌های صفحه را دارد.
/// </summary>
public sealed class ReportsRedesignTests
{
    private static readonly DateTime Day = new(2026, 5, 10);

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Customers.AddRange(new Customer { Id = 1, Name = "Customer A" }, new Customer { Id = 2, Name = "Customer B" });
        db.Suppliers.AddRange(new Supplier { Id = 1, Name = "Supplier A" }, new Supplier { Id = 2, Name = "Supplier B" });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf" });
        db.ExpenseTypes.AddRange(
            new ExpenseType { Id = 1, Code = "TRUCK", Name = "Trucking" },
            new ExpenseType { Id = 2, Code = "FXD", Name = "FX difference", Category = "FxDifference" });
        db.Contracts.Add(new Contract
        {
            Id = 10, ContractName = "Purchase A", ContractNumber = "P-10", ContractType = ContractType.Purchase,
            Status = ContractStatus.Active, CompanyId = 1, ProductId = 1, SupplierId = 1,
            ContractDate = Day, QuantityMt = 200m, PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 500m
        });
        return db;
    }

    // ── ۳) مفاد قراردادها ──────────────────────────────────────────────

    [Fact]
    public void Contract_Pnl_Row_Splits_Purchase_Price_From_Expenses_And_Hides_Unfinal_Profit()
    {
        var priced = new ContractPnlRowViewModel
        {
            ContractQuantityMt = 100m, ContractUnitPriceUsd = 500m,
            PurchaseValueUsd = 40_000m, TotalCostUsd = 43_500m, TotalRevenueUsd = 50_000m, GrossMarginUsd = 6_500m
        };
        var pending = new ContractPnlRowViewModel
        {
            PurchaseValueUsd = 10_000m, TotalCostUsd = 11_000m, TotalRevenueUsd = 30_000m, GrossMarginUsd = 19_000m,
            PendingLoadingCount = 1, PendingLoadedMt = 40m
        };
        var unsold = new ContractPnlRowViewModel { PurchaseValueUsd = 5_000m, TotalCostUsd = 5_200m, GrossMarginUsd = -5_200m };

        Assert.Equal(50_000m, priced.ContractValueUsd);
        Assert.Equal(3_500m, priced.ExpensesUsd);
        Assert.Equal(priced.TotalCostUsd, priced.PurchaseValueUsd + priced.ExpensesUsd);
        Assert.Equal(6_500m, priced.ContractProfitUsd);
        // قیمت خرید یک بار نامعلوم است: برآورد به‌عنوان مفاد قطعی نشان داده نمی‌شود.
        Assert.Null(pending.ContractProfitUsd);
        Assert.Null(unsold.ContractProfitUsd);
        Assert.Null(unsold.ContractValueUsd);

        var report = new ContractPnlReportViewModel { PurchaseRows = [priced, pending, unsold] };
        Assert.Equal(50_000m, report.TotalContractValueUsd);
        Assert.Equal(1_000m + 3_500m + 200m, report.TotalContractExpensesUsd);
        Assert.Equal(80_000m, report.TotalDirectSaleRevenueUsd);
        Assert.Equal(6_500m, report.TotalContractProfitUsd);
        Assert.Equal(1, report.PendingPriceProfitCount);
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public async Task Contract_Pnl_Export_Has_New_Columns_And_Purchase_Rows_Only(string format)
    {
        await using var db = NewDb();
        await db.SaveChangesAsync();
        var controller = new ReportsController(db);
        var export = Assert.IsType<TabularExportResult>(await controller.ContractPnlExport(format));
        var titles = export.Document.Columns.Select(c => c.TitleFa).ToList();

        Assert.Contains("قیمت خرید USD", titles);
        Assert.Contains("جمع مصارف USD", titles);
        Assert.Contains("مفاد قرارداد USD", titles);
        Assert.DoesNotContain(titles, t => t.Contains("حاشیه") || t.Contains("فیصدی") || t.Contains("محقق"));
        Assert.Single(export.Document.Rows);
        Assert.Contains(export.Document.Filters, f => f.LabelFa.StartsWith("مجموع مبلغ قراردادها"));
    }

    // ── ۱ و ۲) طلبات و سن حساب ─────────────────────────────────────────

    [Fact]
    public async Task Party_Aging_Export_Drops_Idle_Days_And_Bucket_Columns()
    {
        await using var db = NewDb();
        await db.SaveChangesAsync();
        var export = Assert.IsType<TabularExportResult>(await new ReportsController(db).PartyAgingExport("xlsx"));
        var titles = export.Document.Columns.Select(c => c.TitleFa).ToList();

        Assert.DoesNotContain("روز بدون حرکت", titles);
        Assert.DoesNotContain("دسته", titles);
        Assert.Equal(titles.Count, export.Document.Totals!.Cells.Count);
    }

    // ── ۵ و ۶) پول واقعیِ دریافت/پرداخت ──────────────────────────────

    [Fact]
    public async Task Customer_Net_Receipts_Count_Cash_Hawala_And_ThreeWay_Once()
    {
        await using var db = NewDb();
        db.PaymentTransactions.AddRange(
            new PaymentTransaction { Id = 1, PaymentDate = Day, Direction = PaymentDirection.In, PaymentKind = PaymentKind.CustomerReceipt, CustomerId = 1, Amount = 1_000m, AmountUsd = 1_000m },
            new PaymentTransaction { Id = 2, PaymentDate = Day, Direction = PaymentDirection.Out, PaymentKind = PaymentKind.CustomerPayment, CustomerId = 1, Amount = 100m, AmountUsd = 100m },
            // پرداختِ دیگری که مشتری ندارد شمرده نمی‌شود.
            new PaymentTransaction { Id = 3, PaymentDate = Day, Direction = PaymentDirection.Out, PaymentKind = PaymentKind.SupplierPayment, SupplierId = 1, Amount = 700m, AmountUsd = 700m });
        db.SarrafSettlements.AddRange(
            new SarrafSettlement { Id = 1, SettlementDate = Day, Direction = SarrafSettlementDirection.In, CounterpartyType = SarrafSettlementCounterpartyType.Customer, CustomerId = 1, SarrafId = 1, Status = SarrafSettlementStatus.Posted, SupplierAcceptedAmountUsd = 300m },
            new SarrafSettlement { Id = 2, SettlementDate = Day, Direction = SarrafSettlementDirection.In, CounterpartyType = SarrafSettlementCounterpartyType.Customer, CustomerId = 1, SarrafId = 1, Status = SarrafSettlementStatus.Cancelled, SupplierAcceptedAmountUsd = 999m });
        db.ThreeWaySettlements.Add(new ThreeWaySettlement { Id = 1, SettlementDate = Day, CustomerId = 1, SupplierId = 1, Status = ThreeWaySettlementStatus.Posted, CustomerPaidUsd = 200m, SupplierAcceptedUsd = 200m });
        await db.SaveChangesAsync();

        var reader = new PartyCashSettlementReader(db);
        var customers = await reader.GetCustomerNetReceiptsAsync([1, 2], null, null);
        var suppliers = await reader.GetSupplierNetPaymentsAsync([1], null, null);

        Assert.Equal(1_000m - 100m + 300m + 200m, customers[1]);
        Assert.False(customers.ContainsKey(2));
        // تسویهٔ سه‌جانبه پرداخت به تأمین‌کننده هم است (مشتری مستقیم پرداخته).
        Assert.Equal(700m + 200m, suppliers[1]);
    }

    [Fact]
    public async Task Supplier_Sales_Count_Only_Sales_Where_Supplier_Is_The_Buyer()
    {
        await using var db = NewDb();
        db.SalesTransactions.AddRange(
            // نفتِ قرارداد خرید همین تأمین‌کننده که به مشتری دیگر فروخته شده — فروش به تأمین‌کننده نیست.
            new SalesTransaction { Id = 1, InvoiceNumber = "S-1", SaleDate = Day, ContractId = 10, CustomerId = 2, CompanyId = 1, ProductId = 1, QuantityMt = 10m, TotalUsd = 9_000m },
            new SalesTransaction { Id = 2, InvoiceNumber = "S-2", SaleDate = Day, SupplierId = 1, CompanyId = 1, ProductId = 1, QuantityMt = 2m, TotalUsd = 1_500m },
            new SalesTransaction { Id = 3, InvoiceNumber = "S-3", SaleDate = Day, SupplierId = 1, CompanyId = 1, ProductId = 1, QuantityMt = 2m, TotalUsd = 800m, IsCancelled = true });
        db.PaymentTransactions.Add(new PaymentTransaction { Id = 1, PaymentDate = Day, Direction = PaymentDirection.Out, PaymentKind = PaymentKind.SupplierPayment, SupplierId = 1, Amount = 4_000m, AmountUsd = 4_000m });
        await db.SaveChangesAsync();

        var model = Assert.IsType<SuppliersBalanceViewModel>(Assert.IsType<ViewResult>(
            await new BalanceController(db).Suppliers(new SuppliersBalanceFilterViewModel { SupplierId = [1] })).Model);
        var item = Assert.Single(model.Items);

        Assert.Equal(1_500m, item.TotalSalesUsd);
        Assert.Equal(4_000m, item.TotalPaidUsd);
        Assert.Equal(1, item.RelatedContractsCount);
        Assert.Equal(model.FilteredTotalPaidUsd, item.TotalPaidUsd);

        var export = Assert.IsType<TabularExportResult>(await new BalanceController(db).SuppliersExport("xlsx", new SuppliersBalanceFilterViewModel { SupplierId = [1] }));
        Assert.Equal(
            new[] { "نام تأمین‌کننده", "قراردادها", "جمع خرید USD", "جمع پرداخت USD", "جمع فروش USD", "مانده USD" },
            export.Document.Columns.Select(c => c.TitleFa).ToArray());
    }

    [Fact]
    public async Task Customer_Accounts_Show_Received_Separately_From_Sales()
    {
        await using var db = NewDb();
        db.SalesTransactions.Add(new SalesTransaction { Id = 1, InvoiceNumber = "S-1", SaleDate = Day, CustomerId = 1, CompanyId = 1, ProductId = 1, QuantityMt = 10m, TotalUsd = 5_000m });
        db.PaymentTransactions.Add(new PaymentTransaction { Id = 1, PaymentDate = Day, Direction = PaymentDirection.In, PaymentKind = PaymentKind.CustomerReceipt, SalesTransactionId = 1, Amount = 2_000m, AmountUsd = 2_000m });
        await db.SaveChangesAsync();

        var model = Assert.IsType<CustomersBalanceViewModel>(Assert.IsType<ViewResult>(
            await new BalanceController(db).Customers(new CustomersBalanceFilterViewModel { CustomerId = [1] })).Model);
        var item = Assert.Single(model.Items);

        Assert.Equal(5_000m, item.TotalSalesUsd);
        Assert.Equal(2_000m, item.TotalReceivedUsd);

        var export = Assert.IsType<TabularExportResult>(await new BalanceController(db).CustomersExport("pdf", new CustomersBalanceFilterViewModel { CustomerId = [1] }));
        Assert.Equal(
            new[] { "نام مشتری", "مجموع فروش USD", "مجموع رسید USD", "مانده USD", "اسناد" },
            export.Document.Columns.Select(c => c.TitleFa).ToArray());
        Assert.Equal(2_000m, export.Document.Totals!.Cells[2].Value);
    }

    [Fact]
    public async Task Contract_Balance_Export_Has_No_Customer_Column()
    {
        await using var db = NewDb();
        await db.SaveChangesAsync();
        var export = Assert.IsType<TabularExportResult>(await new BalanceController(db).ContractsExport("xlsx"));
        Assert.DoesNotContain(export.Document.Columns, c => c.TitleFa == "مشتری");
        Assert.Equal(export.Document.Columns.Count, export.Document.Totals!.Cells.Count);
    }

    // ── ۸) گزارش مصارف ─────────────────────────────────────────────────

    [Fact]
    public async Task Expense_Report_Counts_Each_Expense_Once_And_Totals_Cover_All_Pages()
    {
        await using var db = NewDb();
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction { Id = 1, ExpenseTypeId = 1, ExpenseDate = Day, Amount = 100m, AmountUsd = 100m, SettlementMode = ExpenseSettlementMode.PaidImmediately },
            new ExpenseTransaction { Id = 2, ExpenseTypeId = 1, ExpenseDate = Day.AddDays(1), ContractId = 10, Amount = 20_000m, Currency = "AFN", AppliedFxRateToUsd = 0.01m, AmountUsd = 200m, SettlementMode = ExpenseSettlementMode.Payable },
            new ExpenseTransaction { Id = 3, ExpenseTypeId = 1, ExpenseDate = Day, Amount = 999m, AmountUsd = 999m, IsCancelled = true },
            new ExpenseTransaction { Id = 4, ExpenseTypeId = 2, ExpenseDate = Day, Amount = 50m, AmountUsd = 50m });
        db.PaymentTransactions.Add(new PaymentTransaction { Id = 1, PaymentDate = Day, Direction = PaymentDirection.Out, PaymentKind = PaymentKind.ExpensePayment, ExpenseTransactionId = 2, Amount = 50m, AmountUsd = 50m });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var model = Assert.IsType<ExpenseReportViewModel>(Assert.IsType<ViewResult>(
            await controller.ExpenseReport(new ExpenseReportFilterViewModel(), page: 1, perPage: 1)).Model);

        Assert.Single(model.Rows);
        Assert.Equal(2, model.TotalCount);
        Assert.Equal(300m, model.TotalAmountUsd);
        Assert.Equal(150m, model.TotalPaidUsd);
        Assert.Equal(150m, model.TotalUnpaidUsd);
        Assert.Equal(2, model.PageCount);

        var contractOnly = Assert.IsType<ExpenseReportViewModel>(Assert.IsType<ViewResult>(
            await controller.ExpenseReport(new ExpenseReportFilterViewModel { ContractId = 10 })).Model);
        var row = Assert.Single(contractOnly.Rows);
        Assert.Equal(ExpenseReportPaymentStatus.PartlyPaid, row.PaymentStatus);
        Assert.Equal(ExpenseReportSource.Contract, row.Source);
        Assert.True(row.HasOriginalCurrency);

        var export = Assert.IsType<TabularExportResult>(await controller.ExpenseReportExport("xlsx", new ExpenseReportFilterViewModel()));
        Assert.Equal(2, export.Document.Rows.Count());
        Assert.Equal(300m, export.Document.Totals!.Cells[6].Value);
    }

    // ── ۹) گزارش کسری‌ها ───────────────────────────────────────────────

    [Fact]
    public async Task Loss_Report_Values_Only_Priced_Losses_And_Never_Nets_Surplus()
    {
        await using var db = NewDb();
        db.LoadingRegisters.AddRange(
            new LoadingRegister { Id = 1, ContractId = 10, ProductId = 1, LoadingDate = Day, LoadedQuantityMt = 50m, LoadingPriceUsd = 500m },
            new LoadingRegister { Id = 2, ContractId = 10, ProductId = 1, LoadingDate = Day, LoadedQuantityMt = 50m });
        db.LossEvents.AddRange(
            new LossEvent { Id = 1, Stage = LossEventStage.TransitLoss, ProductId = 1, LoadingRegisterId = 1, EventDate = Day, DifferenceQuantityMt = 2m, ChargeableLossMt = 1m },
            new LossEvent { Id = 2, Stage = LossEventStage.ReceiptShortage, ProductId = 1, EventDate = Day, DifferenceQuantityMt = 3m },
            new LossEvent { Id = 3, Stage = LossEventStage.DispatchShortage, ProductId = 1, EventDate = Day, DifferenceQuantityMt = -1m },
            new LossEvent { Id = 4, Stage = LossEventStage.TransitLoss, ProductId = 1, EventDate = Day, DifferenceQuantityMt = 7m, IsCancelled = true });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var model = Assert.IsType<LossReportViewModel>(Assert.IsType<ViewResult>(
            await controller.LossReport(new LossReportFilterViewModel())).Model);

        Assert.Equal(2, model.TotalCount);
        Assert.Equal(5m, model.TotalLossMt);
        Assert.Equal(1m, model.TotalChargeableMt);
        Assert.Equal(1_000m, model.TotalValuedUsd);
        Assert.Equal(1, model.UnpricedCount);
        Assert.Null(model.Rows.Single(r => r.LossEventId == 2).ValueUsd);

        var chargeable = Assert.IsType<LossReportViewModel>(Assert.IsType<ViewResult>(
            await controller.LossReport(new LossReportFilterViewModel { Kind = LossReportKind.ChargeableOnly })).Model);
        Assert.Equal(1, Assert.Single(chargeable.Rows).LossEventId);

        var export = Assert.IsType<TabularExportResult>(await controller.LossReportExport("pdf", new LossReportFilterViewModel()));
        Assert.Equal(model.TotalValuedUsd, export.Document.Totals!.Cells[7].Value);
        Assert.Null(export.Document.Rows.Single(r => (string?)r.Cells[1].Value == "کسری رسید و تخلیه").Cells[7].Value);
    }

    // ── ۱۱) برند خروجی‌ها ─────────────────────────────────────────────

    [Fact]
    public void Export_Branding_Defaults_To_Mashal_Only()
    {
        var options = new TabularExportOptions();
        Assert.Equal("مشعل | نرم‌افزار تخصصی نفت و گاز", options.OrganizationName);
        Assert.Equal("MASHAL | Oil & Gas Management Software", options.OrganizationNameEn);
        Assert.Equal("/images/mashal-logo-blue.webp", options.CompanyLogoPath);
        Assert.True(string.IsNullOrEmpty(options.CompanyEmail));
        Assert.DoesNotContain("صدیقی", options.CompanyNameFa);
        Assert.DoesNotContain("Saddiqi", new PTGOilSystem.Web.Models.PartyStatements.PartyStatementOptions().CompanyName);
    }
}
