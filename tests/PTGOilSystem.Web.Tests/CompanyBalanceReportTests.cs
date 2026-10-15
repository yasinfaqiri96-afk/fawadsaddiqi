using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «بیلانس کلی شرکت»: بیلانس تا «تا تاریخ» و عملکرد بین دو تاریخ دو محاسبهٔ مستقل‌اند، هر عدد از
/// مرجع موجود خود می‌آید و دارایی − تعهد همیشه دقیقاً خالص بیلانس است.
/// </summary>
public sealed class CompanyBalanceReportTests
{
    private const int PurchaseId = 5;
    private static readonly DateTime From = new(2026, 4, 1);
    private static readonly DateTime To = new(2026, 4, 30);

    [Fact]
    public async Task Company_Without_Transactions_Is_All_Zero_And_Keeps_Its_Layout()
    {
        await using var db = NewDb();

        var model = await BuildAsync(db);

        Assert.Equal(6, model.AssetLines.Count);
        Assert.Equal(5, model.LiabilityLines.Count);
        Assert.All(model.AssetLines.Concat(model.LiabilityLines), line => Assert.Equal(0m, line.Amount));
        Assert.Equal(0m, model.NetCompanyBalance);
        Assert.Equal(0m, model.NetProfitLoss);
        Assert.Equal(0, model.UncostedSaleCount);
    }

    [Fact]
    public async Task Cash_Is_Cumulative_Up_To_The_Report_Date_Only()
    {
        await using var db = NewDb();
        db.PaymentTransactions.AddRange(
            Payment(1, new DateTime(2026, 3, 15), PaymentDirection.In, 1_000m),
            Payment(2, new DateTime(2026, 4, 30), PaymentDirection.Out, 250m),
            Payment(3, new DateTime(2026, 5, 1), PaymentDirection.In, 9_999m));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(750m, model.CashAndBank);
        Assert.Equal(750m, model.TotalAssets);
        Assert.Equal(750m, model.NetCompanyBalance);
    }

    [Fact]
    public async Task Party_Balances_Are_Classified_By_The_Sign_Of_Each_Balance_Not_By_Party_Type()
    {
        await using var db = NewDb();
        ReceivablePayableRowViewModel Row(string type, int id, decimal balance)
            => new() { PartyType = type, PartyId = id, PartyName = $"{type}-{id}", OpeningBalanceUsd = balance };

        var model = await BuildAsync(db,
        [
            Row("Customer", 1, 1_200m),       // مشتری بدهکار → طلب
            Row("Customer", 2, -300m),        // مشتری طلبکار → پیش‌دریافت
            Row("Supplier", 3, -5_000m),      // بدهی به فروشنده
            Row("Supplier", 4, 400m),         // فروشندهٔ بدهکار → پیش‌پرداخت
            Row("ServiceProvider", 5, -700m), // ترانسپورت
            Row("Driver", 6, -50m),
            // ردیف شریکِ «طلبات و بدهی‌ها» موقعیت شراکت است (با تخصیص هزینه)؛ بیلانس از آن نمی‌خواند
            // و ماندهٔ واقعیِ شریک را از PartnerCompanyBalanceReader می‌گیرد (CompanyBalancePartnerTests).
            Row("Partner", 7, -2_000m),
            Row("Partner", 8, 100m),
            Row("Sarraf", 9, -80m),
            Row("Employee", 10, 30m)
        ]);

        Assert.Equal(1_200m, model.CustomerReceivables);
        Assert.Equal(300m, model.CustomerAdvances);
        Assert.Equal(5_000m, model.SupplierPayables);
        Assert.Equal(400m, model.Prepayments);
        Assert.Equal(750m, model.TransportPayables);
        Assert.Equal(0m, model.PartnerPayables);
        Assert.Equal(30m, model.OtherAssets);
        Assert.Equal(80m, model.OtherLiabilities);
        Assert.Equal(1_630m, model.TotalAssets);
        Assert.Equal(6_130m, model.TotalLiabilities);
        Assert.Equal(model.TotalAssets - model.TotalLiabilities, model.NetCompanyBalance);
        Assert.Equal(-4_500m, model.NetCompanyBalance);
    }

    [Theory]
    [InlineData("Partner", 2_000, CompanyBalanceSection.PartnerPayables)] // مثبت = شریک طلبکار
    [InlineData("Partner", -2_000, CompanyBalanceSection.OtherAssets)]    // منفی = شریک بدهکار
    [InlineData("Customer", 2_000, CompanyBalanceSection.CustomerReceivables)]
    [InlineData("Supplier", -2_000, CompanyBalanceSection.SupplierPayables)]
    public void Classifier_Follows_Each_Party_Statement_Sign(string partyType, int balanceUsd, CompanyBalanceSection expected)
        => Assert.Equal(expected, CompanyBalanceClassifier.Classify(partyType, balanceUsd));

    [Fact]
    public async Task Receivable_And_Advance_Of_Different_Customers_Are_Not_Netted()
    {
        await using var db = NewDb();

        var model = await BuildAsync(db,
        [
            new() { PartyType = "Customer", PartyId = 1, PartyName = "A", OpeningBalanceUsd = 1_000m },
            new() { PartyType = "Customer", PartyId = 2, PartyName = "B", OpeningBalanceUsd = -1_000m }
        ]);

        Assert.Equal(1_000m, model.CustomerReceivables);
        Assert.Equal(1_000m, model.CustomerAdvances);
        Assert.Equal(2, model.DetailsFor(CompanyBalanceSection.CustomerReceivables).Count
            + model.DetailsFor(CompanyBalanceSection.CustomerAdvances).Count);
    }

    [Fact]
    public async Task Received_Goods_Leave_Transit_So_Stock_And_Transit_Are_Never_Counted_Twice()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.LoadingRegisters.Add(Loading(1, 100m, 500m));
        db.LoadingReceipts.Add(Receipt(1, 60m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 60m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(30_000m, model.InventoryValue);
        Assert.Equal(20_000m, model.GoodsInTransit);
        // کل بار یک بار شمرده شده: ۱۰۰ تن × ۵۰۰.
        Assert.Equal(50_000m, model.InventoryValue + model.GoodsInTransit);
    }

    [Fact]
    public async Task Receipt_After_The_Report_Date_Keeps_The_Load_In_Transit_On_That_Date()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.LoadingRegisters.Add(Loading(1, 100m, 500m));
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 5, 3)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 5, 3)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(0m, model.InventoryValue);
        Assert.Equal(50_000m, model.GoodsInTransit);
    }

    [Fact]
    public async Task Cancelled_Loading_And_Cancelled_Receipt_Do_Not_Enter_The_Balance()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        var cancelledLoading = Loading(1, 40m, 500m);
        cancelledLoading.IsCancelled = true;
        db.LoadingRegisters.AddRange(cancelledLoading, Loading(2, 10m, 500m));
        var cancelledReceipt = Receipt(2, 10m, new DateTime(2026, 4, 5));
        cancelledReceipt.IsCancelled = true;
        db.LoadingReceipts.Add(cancelledReceipt);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        // بارگیری لغوشده هیچ اثری ندارد؛ رسید لغوشده بار را از مسیر خارج نمی‌کند.
        Assert.Equal(5_000m, model.GoodsInTransit);
    }

    [Fact]
    public async Task Stock_Without_A_Purchase_Price_Is_Reported_But_Not_Valued()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.InventoryMovements.Add(StockIn(1, 25m, new DateTime(2026, 4, 10), contractId: null));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(0m, model.InventoryValue);
        var row = Assert.Single(model.DetailsFor(CompanyBalanceSection.InventoryValue));
        Assert.False(row.IsValued);
        Assert.Equal(25m, row.QuantityMt);
        Assert.Contains(model.NotesFa, note => note.Contains("قیمت خرید"));
    }

    [Fact]
    public async Task Period_Profit_Uses_Contract_Cost_And_Only_Period_Transactions()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.LoadingRegisters.Add(Loading(1, 100m, 500m));
        db.SalesTransactions.AddRange(
            Sale(100, 10m, 6_000m, new DateTime(2026, 4, 20)),
            Sale(101, 5m, 9_999m, new DateTime(2026, 5, 2)));
        db.InventoryMovements.AddRange(
            StockOut(1, saleId: 100, 10m, new DateTime(2026, 4, 20)),
            StockOut(2, saleId: 101, 5m, new DateTime(2026, 5, 2)));
        db.ExpenseTransactions.AddRange(
            Expense(1, 300m, new DateTime(2026, 4, 12)),
            Expense(2, 777m, new DateTime(2026, 3, 31)),
            Expense(3, 555m, new DateTime(2026, 4, 13), cancelled: true));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(6_000m, model.SalesRevenue);
        Assert.Equal(5_000m, model.CostOfSales);
        Assert.Equal(1_000m, model.GrossProfit);
        Assert.Equal(300m, model.PeriodExpenses);
        Assert.Equal(700m, model.NetProfitLoss);
        Assert.Equal(CompanyBalanceReportText.NetProfit, CompanyBalanceReportText.NetResultLabel(model.NetProfitLoss));
        Assert.Equal(model.PeriodExpenses, model.DetailsFor(CompanyBalanceSection.PeriodExpenses).Sum(r => r.Amount));
    }

    [Fact]
    public async Task Loss_Is_Labelled_As_Loss()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.ExpenseTransactions.Add(Expense(1, 1_250m, new DateTime(2026, 4, 12)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(-1_250m, model.NetProfitLoss);
        Assert.Equal(CompanyBalanceReportText.NetLoss, CompanyBalanceReportText.NetResultLabel(model.NetProfitLoss));
    }

    [Fact]
    public async Task Profit_Does_Not_Change_The_Balance_And_Balance_Equation_Holds()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.ExpenseTransactions.Add(Expense(1, 1_000m, new DateTime(2026, 4, 12)));
        db.PaymentTransactions.Add(Payment(1, new DateTime(2026, 4, 1), PaymentDirection.In, 2_000m));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        // مصرف ثبت‌شده بدون حرکت صندوق، دارایی را کم نمی‌کند؛ فقط در عملکرد دوره دیده می‌شود.
        Assert.Equal(2_000m, model.NetCompanyBalance);
        Assert.Equal(-1_000m, model.NetProfitLoss);
    }

    [Fact]
    public async Task Report_Currency_Uses_The_System_Daily_Rate_In_Either_Direction()
    {
        await using var db = NewDb();
        db.PaymentTransactions.Add(Payment(1, new DateTime(2026, 4, 1), PaymentDirection.In, 100m));
        db.DailyFxRates.Add(new DailyFxRate { Id = 1, BaseCurrency = "USD", QuoteCurrency = "AFN", RateDate = new DateTime(2026, 4, 28), Rate = 70m });
        db.DailyFxRates.Add(new DailyFxRate { Id = 2, BaseCurrency = "EUR", QuoteCurrency = "USD", RateDate = new DateTime(2026, 4, 28), Rate = 1.25m });
        await db.SaveChangesAsync();

        var afn = await BuildAsync(db, currency: "AFN");
        var eur = await BuildAsync(db, currency: "EUR");

        Assert.Equal(7_000m, afn.CashAndBank);
        Assert.Equal(100m, afn.TotalAssetsUsd);
        Assert.Equal(afn.TotalAssets - afn.TotalLiabilities, afn.NetCompanyBalance);
        Assert.Equal(80m, eur.CashAndBank);
    }

    [Fact]
    public async Task Missing_Rate_Falls_Back_To_Base_Currency_With_A_Message()
    {
        await using var db = NewDb();
        db.Currencies.AddRange(
            new Currency { Id = 1, Code = "USD", Name = "Dollar" },
            new Currency { Id = 2, Code = "AFN", Name = "Afghani" });
        db.PaymentTransactions.Add(Payment(1, new DateTime(2026, 4, 1), PaymentDirection.In, 100m));
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var view = Assert.IsType<ViewResult>(await controller.CompanyBalance(new CompanyBalanceReportFilterViewModel
        {
            FromDate = From,
            ToDate = To,
            Currency = "AFN"
        }));
        var model = Assert.IsType<CompanyBalanceReportViewModel>(view.Model);

        Assert.Equal("USD", model.CurrencyCode);
        Assert.False(string.IsNullOrWhiteSpace(model.CurrencyErrorFa));
        Assert.Equal(100m, model.CashAndBank);
        Assert.Equal(2, model.Currencies.Count);
    }

    [Fact]
    public async Task Details_Page_Lists_The_Rows_Behind_A_Line()
    {
        await using var db = NewDb();
        db.PaymentTransactions.Add(Payment(1, new DateTime(2026, 4, 1), PaymentDirection.In, 100m));
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await Controller(db).CompanyBalanceDetails(
            new CompanyBalanceReportFilterViewModel { FromDate = From, ToDate = To },
            CompanyBalanceSection.CashAndBank));
        var model = Assert.IsType<CompanyBalanceDetailsViewModel>(view.Model);

        var row = Assert.Single(model.Rows);
        Assert.Equal("Main Cash", row.Name);
        Assert.Equal(100m, row.Amount);
        Assert.IsType<NotFoundResult>(await Controller(db).CompanyBalanceDetails(null, null));
    }

    [Fact]
    public async Task Pdf_Is_Generated_From_The_Same_Model()
    {
        await using var db = NewDb();
        db.PaymentTransactions.Add(Payment(1, new DateTime(2026, 4, 1), PaymentDirection.In, 287_200m));
        await db.SaveChangesAsync();
        var model = await BuildAsync(db);

        await using var stream = new MemoryStream();
        await CreateExportService().WriteCompanyBalancePdfAsync(model, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 1_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Number_Format_Has_Thousands_Separator_Two_Decimals_And_A_Minus_Sign()
    {
        Assert.Equal("287,200.00", CompanyBalanceReportText.Amount(287_200m));
        Assert.Equal("-12,450.50", CompanyBalanceReportText.Amount(-12_450.5m));
        Assert.Equal("287,200.00 USD", CompanyBalanceReportText.Money(287_200m, "USD"));
    }

    // ── مشکل ۱: بهای واحد تاریخی بدون نگاه به آینده ────────────────────────

    [Fact]
    public async Task Historical_Average_Cost_Ignores_Loadings_After_The_Report_Date()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.LoadingRegisters.AddRange(
            Loading(1, 100m, 500m),                                   // 2026-04-02
            Loading(2, 100m, 700m, new DateTime(2026, 5, 10)));     // بعد از 2026-04-30
        db.LoadingReceipts.Add(Receipt(1, 60m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 60m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var april = await BuildAsync(db);
        var may = await BuildAsync(db, from: new DateTime(2026, 5, 1), to: new DateTime(2026, 5, 31));

        // تا 2026-04-30 فقط بارگیری ۵۰۰ دالری وجود داشت.
        Assert.Equal(30_000m, april.InventoryValue);
        Assert.Equal(20_000m, april.GoodsInTransit);
        // بعد از بارگیری دوم میانگین ۶۰۰ است، برای موجودی و بار در راه هر دو.
        Assert.Equal(36_000m, may.InventoryValue);
        Assert.Equal(84_000m, may.GoodsInTransit);
    }

    [Fact]
    public async Task Cancelled_Loading_Does_Not_Move_The_Average_Cost()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        var cancelled = Loading(2, 100m, 900m);
        cancelled.IsCancelled = true;
        db.LoadingRegisters.AddRange(Loading(1, 100m, 500m), cancelled);
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(50_000m, model.InventoryValue);
        Assert.Equal(0m, model.GoodsInTransit);
    }

    // ── مشکل ۲: هزینهٔ مستقیم بارگیری ───────────────────────────────────────

    [Fact]
    public async Task Scenario_A_Undocumented_Freight_On_Loading_Is_Part_Of_Inventory_Cost()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        var loading = Loading(1, 100m, 100m);
        loading.TransportExpenseUsd = 1_000m;
        db.LoadingRegisters.Add(loading);
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(11_000m, model.InventoryValue);
        Assert.Equal(0m, model.PeriodExpenses);
    }

    [Fact]
    public async Task Scenario_B_Freight_With_An_Expense_Document_Is_Counted_Once_As_Period_Expense()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        var loading = Loading(1, 100m, 100m);
        loading.TransportExpenseUsd = 1_000m;                     // آینهٔ همان سند
        db.LoadingRegisters.Add(loading);
        var document = Expense(1, 1_000m, new DateTime(2026, 4, 2));
        document.LoadingRegisterId = 1;
        db.ExpenseTransactions.Add(document);
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(10_000m, model.InventoryValue);
        Assert.Equal(1_000m, model.PeriodExpenses);
        Assert.Equal(11_000m, model.InventoryValue + model.PeriodExpenses);
    }

    [Fact]
    public async Task Scenario_B_Driver_Freight_Mirrored_Into_A_Line_Based_Loading_Is_Not_Capitalized_Again()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 2, Code = "LOAD-TRANSPORT", Name = "Loading Transport Freight" });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A" });
        var loading = Loading(1, 100m, 100m);
        loading.TransportExpenseUsd = 1_200m;                     // ۲۰۰ ردیف بی‌طرف + ۱۰۰۰ آینهٔ سند راننده
        db.LoadingRegisters.Add(loading);
        db.LoadingExpenseLines.Add(new LoadingExpenseLine
        {
            Id = 1,
            LoadingRegisterId = 1,
            ExpenseTypeId = 2,
            AmountUsd = 200m,
            PartyType = LoadingExpensePartyType.None
        });
        var driverDocument = Expense(1, 1_000m, new DateTime(2026, 4, 2));
        driverDocument.ExpenseTypeId = 2;
        driverDocument.LoadingRegisterId = 1;
        driverDocument.DriverId = 1;
        db.ExpenseTransactions.Add(driverDocument);
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(10_200m, model.InventoryValue);
        Assert.Equal(1_000m, model.PeriodExpenses);
    }

    [Fact]
    public async Task Scenario_C_Storage_Other_And_Admin_Costs_Never_Enter_Inventory()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        var loading = Loading(1, 100m, 100m);
        loading.WarehouseExpenseUsd = 200m;
        loading.OtherExpenseUsd = 100m;
        db.LoadingRegisters.Add(loading);
        db.ExpenseTransactions.Add(Expense(1, 500m, new DateTime(2026, 4, 15)));   // اداری
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(10_000m, model.InventoryValue);
        Assert.Equal(800m, model.PeriodExpenses);
        Assert.Equal(model.PeriodExpenses, model.DetailsFor(CompanyBalanceSection.PeriodExpenses).Sum(r => r.Amount));
    }

    [Fact]
    public async Task Scenario_D_Direct_Freight_Splits_Between_Cost_Of_Sales_And_Remaining_Stock_Once()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        var loading = Loading(1, 100m, 100m);
        loading.TransportExpenseUsd = 1_000m;
        db.LoadingRegisters.Add(loading);
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        db.SalesTransactions.Add(Sale(100, 40m, 6_000m, new DateTime(2026, 4, 20)));
        db.InventoryMovements.Add(StockOut(1, saleId: 100, 40m, new DateTime(2026, 4, 20)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(6_600m, model.InventoryValue);
        Assert.Equal(4_400m, model.CostOfSales);
        Assert.Equal(11_000m, model.InventoryValue + model.CostOfSales);
        Assert.Equal(0m, model.PeriodExpenses);
    }

    [Fact]
    public async Task Scenario_E_Freight_Recorded_After_The_Report_Date_Does_Not_Leak_Into_The_Past()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.LoadingRegisters.Add(Loading(1, 100m, 100m));
        var later = Loading(2, 10m, 100m, new DateTime(2026, 5, 5));
        later.TransportExpenseUsd = 1_000m;
        db.LoadingRegisters.Add(later);
        var laterDocument = Expense(1, 700m, new DateTime(2026, 5, 6));
        laterDocument.LoadingRegisterId = 1;
        db.ExpenseTransactions.Add(laterDocument);
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(10_000m, model.InventoryValue);
        Assert.Equal(0m, model.GoodsInTransit);
        Assert.Equal(0m, model.PeriodExpenses);
    }

    [Fact]
    public async Task Freight_Borne_By_The_Seller_Is_Not_A_Company_Cost()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        var loading = Loading(1, 100m, 100m);
        loading.TransportExpenseUsd = 1_000m;
        loading.FreightCostResponsibility = CostResponsibility.Seller;
        db.LoadingRegisters.Add(loading);
        db.LoadingReceipts.Add(Receipt(1, 100m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 100m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(10_000m, model.InventoryValue);
    }

    // ── مشکل ۳: بار در راه ─────────────────────────────────────────────────

    [Fact]
    public async Task Partial_Receipt_Keeps_Only_The_Remaining_Quantity_In_Transit()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.LoadingRegisters.Add(Loading(1, 100m, 500m));
        db.LoadingReceipts.Add(Receipt(1, 70m, new DateTime(2026, 4, 10)));
        db.InventoryMovements.Add(StockIn(1, 70m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        var transit = Assert.Single(model.DetailsFor(CompanyBalanceSection.GoodsInTransit));
        Assert.Equal(30m, transit.QuantityMt);
        Assert.Contains("رسید جزئی", transit.Secondary);
        var stock = Assert.Single(model.DetailsFor(CompanyBalanceSection.InventoryValue));
        Assert.Equal(70m, stock.QuantityMt);
        Assert.Equal(50_000m, model.InventoryValue + model.GoodsInTransit);
    }

    [Fact]
    public async Task Old_Loadings_Without_Any_Receipt_Are_Flagged_But_Still_Counted()
    {
        await using var db = NewDb();
        SeedPurchase(db);
        db.LoadingRegisters.AddRange(
            Loading(1, 10m, 500m, new DateTime(2026, 1, 10)),   // ۱۱۰ روز، بدون رسید ⇒ هشدار
            Loading(2, 10m, 500m, new DateTime(2026, 4, 20)),   // تازه ⇒ هشدار ندارد
            Loading(3, 10m, 500m, new DateTime(2026, 1, 10)));  // قدیمی ولی رسید جزئی دارد ⇒ هشدار ندارد
        db.LoadingReceipts.Add(Receipt(3, 4m, new DateTime(2026, 2, 1)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(1, model.LongTransitLoadCount);
        Assert.Equal(10m, model.LongTransitQuantityMt);
        Assert.Equal(5_000m, model.LongTransitAmount);
        Assert.Equal(13_000m, model.GoodsInTransit);
        Assert.Contains(model.NotesFa, note => note.Contains("بدون رسید ثبت‌شده"));
        Assert.Contains(model.DetailsFor(CompanyBalanceSection.GoodsInTransit), row => row.Secondary!.Contains("بدون رسید"));
    }

    // ── زیرساخت ─────────────────────────────────────────────────────────────

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "CASH", Name = "Main Cash", Currency = "USD", AccountType = CashAccountType.Cash });
        db.SaveChanges();
        return db;
    }

    private static Task<CompanyBalanceReportViewModel> BuildAsync(
        ApplicationDbContext db,
        IReadOnlyList<ReceivablePayableRowViewModel>? partyRows = null,
        string currency = "USD",
        DateTime? from = null,
        DateTime? to = null)
        => new CompanyBalanceReportService(db, new ProfitAndLossService(db), new StockService(db), new PricingService(db))
            .BuildAsync(
                new CompanyBalanceReportRequest(from ?? From, to ?? To, currency, new DateTime(2026, 5, 1, 10, 24, 0)),
                partyRows ?? []);

    private static ReportsController Controller(ApplicationDbContext db)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static void SeedPurchase(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TANK-A" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "GEN", Name = "General" });
        db.Contracts.Add(new Contract
        {
            Id = PurchaseId,
            ContractNumber = "P-005",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 3, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
    }

    private static PaymentTransaction Payment(int id, DateTime date, PaymentDirection direction, decimal usd)
        => new() { Id = id, PaymentDate = date, Direction = direction, CashAccountId = 1, Amount = usd, AmountUsd = usd, Currency = "USD" };

    private static LoadingRegister Loading(int id, decimal quantityMt, decimal priceUsd, DateTime? loadingDate = null)
        => new()
        {
            Id = id,
            ContractId = PurchaseId,
            ProductId = 1,
            LoadingDate = loadingDate ?? new DateTime(2026, 4, 2),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd
        };

    private static LoadingReceipt Receipt(int loadingId, decimal quantityMt, DateTime date)
        => new() { Id = loadingId, LoadingRegisterId = loadingId, TerminalId = 1, StorageTankId = 1, ReceiptDate = date, ReceivedQuantityMt = quantityMt };

    private static InventoryMovement StockIn(int id, decimal quantityMt, DateTime date, int? contractId = PurchaseId)
        => new()
        {
            Id = id,
            ProductId = 1,
            ContractId = contractId,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = date,
            QuantityMt = quantityMt
        };

    private static InventoryMovement StockOut(int id, int saleId, decimal quantityMt, DateTime date)
        => new()
        {
            Id = id + 1000,
            ProductId = 1,
            ContractId = PurchaseId,
            TerminalId = 1,
            StorageTankId = 1,
            SalesTransactionId = saleId,
            Direction = MovementDirection.Out,
            MovementDate = date,
            QuantityMt = quantityMt
        };

    private static SalesTransaction Sale(int id, decimal quantityMt, decimal totalUsd, DateTime date)
        => new()
        {
            Id = id,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = $"INV-{id}",
            SaleDate = date,
            QuantityMt = quantityMt,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalUsd = totalUsd,
            TotalInCurrency = totalUsd,
            Currency = "USD"
        };

    private static ExpenseTransaction Expense(int id, decimal amountUsd, DateTime date, bool cancelled = false)
        => new()
        {
            Id = id,
            ExpenseTypeId = 1,
            ExpenseDate = date,
            Amount = amountUsd,
            AmountUsd = amountUsd,
            Currency = "USD",
            IsCancelled = cancelled
        };

    private static TabularExportService CreateExportService()
    {
        var webRoot = FindWebRoot();
        var environment = new TestWebHostEnvironment
        {
            WebRootPath = webRoot,
            ContentRootPath = Directory.GetParent(webRoot)!.FullName
        };
        var options = Options.Create(new TabularExportOptions
        {
            ExcelMaxRows = 100,
            PdfMaxRows = 100,
            CompanyLogoPath = "/invoice-templates/fawad/logo.png",
            QuestPdfLicense = "Community"
        });
        return new TabularExportService(options, environment);
    }

    private static string FindWebRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "PTGOilSystem.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }
}
