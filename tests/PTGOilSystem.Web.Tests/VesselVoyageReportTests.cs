using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «گزارش کشتی‌ها» — فقط‌خواندنی. تست‌ها می‌سنجند که یک سطر = یک سفر است (نه یک کشتی)،
/// تخصیص Shipperها و مغایرت آن درست خوانده می‌شود، کرایه از همان دسته‌بندی مشترک می‌آید،
/// فیلترها روی همهٔ نتایج اثر می‌گذارند و خروجی دقیقاً همان فیلترها را می‌گیرد.
/// </summary>
public class VesselVoyageReportTests
{
    private const int DieselProductId = 1;
    private const int GasolineProductId = 2;

    // ===================== دانه‌بندی سطر: سفر، نه کشتی =====================

    [Fact]
    public async Task One_Vessel_With_Two_Voyages_Produces_Two_Rows_And_One_Distinct_Vessel()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        AddVoyage(db, id: 2, code: "V-002", vesselId: 1, date: new DateTime(2026, 2, 5), quantityMt: 5000m);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(2, model.Totals.VoyageCount);
        Assert.Equal(1, model.Totals.VesselCount);
        Assert.Equal(9000m, model.Totals.TotalQuantityMt);
        // تازه‌ترین سفر اول می‌آید و شماره ردیف از یک شروع می‌شود.
        Assert.Equal("V-002", model.Rows[0].ShipmentCode);
        Assert.Equal(1, model.Rows[0].RowNumber);
        Assert.Equal(2, model.Rows[1].RowNumber);
    }

    // ===================== تخصیص Shipper و مغایرت =====================

    [Fact]
    public async Task Matching_Shipper_Allocation_Does_Not_Raise_The_Mismatch_Badge()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.ShipmentContracts.AddRange(
            new ShipmentContract { Id = 1, ShipmentId = 1, ContractId = 1, QuantityMt = 2500m },
            new ShipmentContract { Id = 2, ShipmentId = 1, ContractId = 2, QuantityMt = 1500m });
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Equal(4000m, row.AllocatedQuantityMt);
        Assert.Equal(0m, row.AllocationDifferenceMt);
        Assert.False(row.HasAllocationMismatch);
        Assert.Equal(2, row.ShipperLines.Count);
    }

    [Fact]
    public async Task Allocation_Below_Voyage_Quantity_Raises_A_Single_Mismatch_Badge()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.ShipmentContracts.Add(new ShipmentContract { Id = 1, ShipmentId = 1, ContractId = 1, QuantityMt = 3200m });
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.True(row.HasAllocationMismatch);
        Assert.Equal(-800m, row.AllocationDifferenceMt);
        Assert.Equal(1, model.MismatchCount);
    }

    [Fact]
    public async Task Voyage_Without_Any_Allocation_Is_Not_Reported_As_A_Mismatch()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Empty(row.ShipperLines);
        Assert.False(row.HasAllocationMismatch);
    }

    // ===================== کرایهٔ کشتی =====================

    [Fact]
    public async Task Freight_Total_And_Derived_Rate_Come_From_Vessel_Freight_Expenses_Only()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.ExpenseTransactions.AddRange(
            // کرایهٔ کشتی — تنها چیزی که این گزارش می‌شمارد
            NewExpense(id: 1, shipmentId: 1, expenseTypeId: 1, amountUsd: 100_000m, serviceProviderId: 1),
            // مصرف گمرکی — کرایه نیست
            NewExpense(id: 2, shipmentId: 1, expenseTypeId: 2, amountUsd: 50_000m, serviceProviderId: 1),
            // مصرف لغوشده — هرگز شمرده نمی‌شود
            NewExpense(id: 3, shipmentId: 1, expenseTypeId: 1, amountUsd: 999_000m, serviceProviderId: 1, isCancelled: true));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(100_000m, row.FreightTotalUsd);
        Assert.Equal(100_000m, model.Totals.TotalFreightUsd);
        Assert.Equal(25m, row.FreightRateUsdPerMt);
        Assert.Equal("کرایه کشتی", row.FreightTypeText);
        Assert.Equal("TRINTI", row.TransportCompanyText);
        Assert.Single(row.FreightLines);
    }

    [Fact]
    public async Task Receipt_Freight_Expense_Is_Excluded_So_Freight_Is_Not_Double_Counted()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.ExpenseTransactions.AddRange(
            NewExpense(id: 1, shipmentId: 1, expenseTypeId: 1, amountUsd: 100_000m, serviceProviderId: 1),
            // TRANSPORT-RECEIPT-FREIGHT — آینهٔ کرایهٔ رسید حمل، مثل صفحهٔ سود و زیان کنار گذاشته می‌شود.
            NewExpense(id: 2, shipmentId: 1, expenseTypeId: 3, amountUsd: 7_000m, serviceProviderId: 1));
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Equal(100_000m, row.FreightTotalUsd);
    }

    /// <summary>
    /// خط‌آهن، کرایهٔ مخزن و دیمرج هر سه <c>Category=Transport</c> دارند و در دسته‌بندی
    /// مشترکِ پروندهٔ محموله «کرایه» شمرده می‌شوند. این گزارش فقط کرایهٔ خودِ کشتی را
    /// می‌خواهد، پس هیچ‌کدام نباید در مبلغ و نرخ بیایند.
    /// </summary>
    [Fact]
    public async Task Rail_Tank_And_Demurrage_Are_Not_Vessel_Freight()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.ExpenseTransactions.AddRange(
            NewExpense(id: 1, shipmentId: 1, expenseTypeId: 1, amountUsd: 100_000m, serviceProviderId: 1),
            NewExpense(id: 2, shipmentId: 1, expenseTypeId: 4, amountUsd: 148_000m, serviceProviderId: 1),
            NewExpense(id: 3, shipmentId: 1, expenseTypeId: 5, amountUsd: 254_000m, serviceProviderId: 1),
            NewExpense(id: 4, shipmentId: 1, expenseTypeId: 6, amountUsd: 75_000m, serviceProviderId: 1));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(100_000m, row.FreightTotalUsd);
        Assert.Equal(100_000m, model.Totals.TotalFreightUsd);
        Assert.Equal(25m, row.FreightRateUsdPerMt);
        Assert.Single(row.FreightLines);
        Assert.Equal("کرایه کشتی", row.FreightTypeText);
    }

    [Theory]
    [InlineData("کرایه کشتی از SELANDIA LINES", true)]
    [InlineData("کرایه کشتی  از شرکت MT GROUP", true)]
    [InlineData("Vessel freight", true)]
    [InlineData("Sea Freight", true)]
    [InlineData("دیمرج کشتی", false)]
    [InlineData("دیمیرج کشتی از شرکت MT GROUP", false)]
    [InlineData("خط آهن امیر اباد ای شمتغ", false)]
    [InlineData("کرایه مخازن الینکا", false)]
    [InlineData("کرایه رسید حمل", false)]
    [InlineData("کرایه حمل", false)]
    // «ship» داخل «shipment» هست؛ نباید مصرف عمومیِ محموله را کرایهٔ کشتی بشمارد.
    [InlineData("Shipment freight handling", false)]
    [InlineData("", false)]
    public void Vessel_Freight_Classifier_Reads_The_Expense_Type_Name(string name, bool expected)
        => Assert.Equal(expected, VesselFreightClassifier.IsVesselFreight(name, null));

    [Fact]
    public async Task Voyage_Without_Freight_Has_No_Derived_Rate()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Equal(0m, row.FreightTotalUsd);
        Assert.Null(row.FreightRateUsdPerMt);
    }

    // ===================== کارت‌های دیزل و بنزین =====================

    [Fact]
    public async Task Diesel_And_Gasoline_Totals_Split_By_Allocated_Product()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.ShipmentContracts.AddRange(
            // قرارداد ۱ = دیزل، قرارداد ۲ = بنزین
            new ShipmentContract { Id = 1, ShipmentId = 1, ContractId = 1, QuantityMt = 2500m },
            new ShipmentContract { Id = 2, ShipmentId = 1, ContractId = 2, QuantityMt = 1500m });
        await db.SaveChangesAsync();

        var totals = (await BuildAsync(db)).Totals;

        Assert.Equal(2500m, totals.TotalDieselMt);
        Assert.Equal(1500m, totals.TotalGasolineMt);
    }

    [Theory]
    [InlineData("DIESEL", VesselVoyageFuelKind.Diesel)]
    [InlineData("gasoil", VesselVoyageFuelKind.Diesel)]
    [InlineData("گازوئیل", VesselVoyageFuelKind.Diesel)]
    [InlineData("Gasoline 92", VesselVoyageFuelKind.Gasoline)]
    [InlineData("petrol", VesselVoyageFuelKind.Gasoline)]
    [InlineData("پطرول", VesselVoyageFuelKind.Gasoline)]
    [InlineData("Bitumen", VesselVoyageFuelKind.Unknown)]
    [InlineData(null, VesselVoyageFuelKind.Unknown)]
    public void Fuel_Classifier_Reads_The_Product_Name(string? name, VesselVoyageFuelKind expected)
        => Assert.Equal(expected, VesselVoyageFuelClassifier.Classify(name));

    // ===================== وضعیت مشتق سفر =====================

    [Theory]
    // هیچ حملی، بدون تاریخ رسیدن ⇒ ثبت‌شده
    [InlineData(0, 0, 0, 0, false, VesselVoyageStatus.Registered)]
    // هیچ حملی، با تاریخ رسیدن ⇒ رسیده
    [InlineData(0, 0, 0, 0, true, VesselVoyageStatus.Arrived)]
    // همهٔ حمل‌ها لغو ⇒ لغوشده
    [InlineData(2, 2, 0, 0, false, VesselVoyageStatus.Cancelled)]
    // همهٔ حمل‌های فعال رسید خورده ⇒ تکمیل‌شده (حتی با یک حملِ لغوشده)
    [InlineData(3, 1, 2, 0, false, VesselVoyageStatus.Completed)]
    // حملِ بارگیری‌شده یا در مسیر ⇒ در مسیر
    [InlineData(2, 0, 1, 1, false, VesselVoyageStatus.InTransit)]
    public void Voyage_Status_Is_Derived_From_Leg_Status_And_Arrival(
        int total, int cancelled, int received, int moving, bool hasArrival, VesselVoyageStatus expected)
        => Assert.Equal(
            expected,
            ReportsController.ResolveVesselVoyageStatus(
                total == 0 ? null : (total, cancelled, received, moving),
                hasArrival ? new DateTime(2026, 3, 1) : null));

    [Fact]
    public async Task Received_Legs_Mark_The_Voyage_Completed()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            ShipmentId = 1,
            SourcePurchaseContractId = 1,
            ProductId = DieselProductId,
            SourceTerminalId = 1,
            LoadedDate = new DateTime(2026, 1, 6),
            QuantityMt = 4000m,
            Status = InventoryTransportLegStatus.Received
        });
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Equal(VesselVoyageStatus.Completed, row.Status);
    }

    // ===================== مشتری =====================

    [Fact]
    public async Task Customer_Comes_From_Active_Sales_Of_The_Voyage()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        db.SalesTransactions.AddRange(
            new SalesTransaction
            {
                Id = 1, ShipmentId = 1, CustomerId = 1, ProductId = DieselProductId,
                InvoiceNumber = "INV-1", SaleDate = new DateTime(2026, 1, 20), QuantityMt = 1000m
            },
            new SalesTransaction
            {
                Id = 2, ShipmentId = 1, CustomerId = 2, ProductId = DieselProductId,
                InvoiceNumber = "INV-2", SaleDate = new DateTime(2026, 1, 21), QuantityMt = 500m,
                IsCancelled = true
            });
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        // فروش لغوشده مشتری نمی‌سازد.
        Assert.Equal("Afghanistan", row.CustomerText);
    }

    // ===================== Consignee =====================

    [Fact]
    public async Task Consignee_Stays_Empty_Because_The_Schema_Has_No_Source_For_It()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        await db.SaveChangesAsync();

        Assert.Null(Assert.Single((await BuildAsync(db)).Rows).ConsigneeText);
    }

    // ===================== فیلترها =====================

    [Fact]
    public async Task Vessel_Filter_Narrows_Rows_And_Totals_Together()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        AddVoyage(db, id: 2, code: "V-002", vesselId: 2, date: new DateTime(2026, 2, 5), quantityMt: 5000m);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, new VesselVoyageReportFilterViewModel { VesselId = [2] });

        Assert.Equal("V-002", Assert.Single(model.Rows).ShipmentCode);
        Assert.Equal(1, model.Totals.VoyageCount);
        Assert.Equal(5000m, model.Totals.TotalQuantityMt);
    }

    [Fact]
    public async Task Date_Range_Filters_On_Departure_Date()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        AddVoyage(db, id: 2, code: "V-002", vesselId: 2, date: new DateTime(2026, 6, 5), quantityMt: 5000m);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, new VesselVoyageReportFilterViewModel
        {
            FromDate = new DateTime(2026, 5, 1),
            ToDate = new DateTime(2026, 7, 1)
        });

        Assert.Equal("V-002", Assert.Single(model.Rows).ShipmentCode);
    }

    [Fact]
    public async Task Fiscal_Year_Filter_Restricts_To_The_Owner_Company_Year_Range()
    {
        await using var db = NewDb();
        Seed(db);
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 1,
            CompanyId = 1,
            Name = "FY-2026",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31)
        });
        AddVoyage(db, id: 1, code: "V-2025", vesselId: 1, date: new DateTime(2025, 12, 20), quantityMt: 3000m);
        AddVoyage(db, id: 2, code: "V-2026", vesselId: 1, date: new DateTime(2026, 3, 20), quantityMt: 4000m);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, new VesselVoyageReportFilterViewModel { FiscalYearId = 1 });

        Assert.Equal("V-2026", Assert.Single(model.Rows).ShipmentCode);
        Assert.Equal(4000m, model.Totals.TotalQuantityMt);
    }

    [Fact]
    public async Task Fiscal_Year_Of_Another_Company_Never_Applies()
    {
        await using var db = NewDb();
        Seed(db);
        db.Companies.Add(new Company { Id = 2, Code = "OTHER", Name = "Other", IsSystemOwner = false });
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 9,
            CompanyId = 2,
            Name = "FY-OTHER",
            StartDate = new DateTime(2030, 1, 1),
            EndDate = new DateTime(2030, 12, 31)
        });
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 3, 20), quantityMt: 4000m);
        await db.SaveChangesAsync();

        // سال شرکت دیگر بی‌اثر است: بازه اعمال نمی‌شود و سفر ۲۰۲۶ سر جایش می‌ماند.
        var model = await BuildAsync(db, new VesselVoyageReportFilterViewModel { FiscalYearId = 9 });

        Assert.Single(model.Rows);
    }

    [Fact]
    public async Task Supplier_Filter_Uses_Contract_Allocations()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        AddVoyage(db, id: 2, code: "V-002", vesselId: 1, date: new DateTime(2026, 2, 5), quantityMt: 5000m);
        db.ShipmentContracts.AddRange(
            new ShipmentContract { Id = 1, ShipmentId = 1, ContractId = 1, QuantityMt = 4000m },
            new ShipmentContract { Id = 2, ShipmentId = 2, ContractId = 2, QuantityMt = 5000m });
        await db.SaveChangesAsync();

        // قرارداد ۲ متعلق به تأمین‌کنندهٔ ۲ است.
        var model = await BuildAsync(db, new VesselVoyageReportFilterViewModel { SupplierId = [2] });

        Assert.Equal("V-002", Assert.Single(model.Rows).ShipmentCode);
    }

    [Fact]
    public async Task Transport_Company_Filter_Uses_Freight_Expenses()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        AddVoyage(db, id: 2, code: "V-002", vesselId: 1, date: new DateTime(2026, 2, 5), quantityMt: 5000m);
        db.ExpenseTransactions.AddRange(
            NewExpense(id: 1, shipmentId: 1, expenseTypeId: 1, amountUsd: 10_000m, serviceProviderId: 1),
            NewExpense(id: 2, shipmentId: 2, expenseTypeId: 1, amountUsd: 20_000m, serviceProviderId: 2));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, new VesselVoyageReportFilterViewModel { ServiceProviderId = 2 });

        Assert.Equal("V-002", Assert.Single(model.Rows).ShipmentCode);
        Assert.Equal(20_000m, model.Totals.TotalFreightUsd);
    }

    // ===================== خروجی =====================

    [Theory]
    [InlineData("excel", TabularExportFormat.Excel)]
    [InlineData("pdf", TabularExportFormat.Pdf)]
    public async Task Export_Applies_The_Same_Filters_As_The_Page(string format, TabularExportFormat expected)
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        AddVoyage(db, id: 2, code: "V-002", vesselId: 2, date: new DateTime(2026, 2, 5), quantityMt: 5000m);
        await db.SaveChangesAsync();

        var result = Assert.IsType<TabularExportResult>(await NewController(db).VesselVoyagesExport(
            format,
            new VesselVoyageReportFilterViewModel { VesselId = [2] }));

        Assert.Equal(expected, result.Format);
        Assert.Equal("PTG_Vessel_Voyages", result.Document.FileNameStem);
        // فقط سفرِ کشتی فیلترشده در خروجی است — دقیقاً همان چیزی که صفحه نشان می‌دهد.
        Assert.Equal(1, result.Document.KnownRowCount);
        var exportedRow = Assert.Single(result.Document.Rows);
        Assert.Equal("V-002", exportedRow.Cells[2].Value);
        Assert.Equal(5000m, exportedRow.Cells[5].Value);
    }

    [Fact]
    public async Task Export_Columns_Follow_The_Reference_Workbook_Order()
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 4000m);
        await db.SaveChangesAsync();

        var result = Assert.IsType<TabularExportResult>(
            await NewController(db).VesselVoyagesExport("excel", new VesselVoyageReportFilterViewModel()));

        Assert.Equal(
            ["No", "Date", "Code No", "Vessel Name", "Kind - Cargo", "Quantity MT", "Consignee",
             "Loading Port", "Destination", "Customer", "Shipper", "Shipper allocation",
             "Transport company", "Vessel freight type", "Vessel freight rate USD/MT",
             "Total vessel freight USD", "Voyage status", "Notes"],
            result.Document.Columns.Select(c => c.TitleEn));
    }

    [Fact]
    public async Task Export_Is_Not_Limited_To_The_Current_Page()
    {
        await using var db = NewDb();
        Seed(db);
        for (var i = 1; i <= 60; i++)
        {
            AddVoyage(db, id: i, code: $"V-{i:000}", vesselId: 1, date: new DateTime(2026, 1, 1).AddDays(i), quantityMt: 100m);
        }
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var page = await controller.BuildVesselVoyagesForTestAsync(new VesselVoyageReportFilterViewModel(), paginate: true);
        var all = await controller.BuildVesselVoyagesForTestAsync(new VesselVoyageReportFilterViewModel(), paginate: false);

        Assert.Equal(50, page.Rows.Count);
        Assert.Equal(60, all.Rows.Count);
        // جمع‌ها همیشه روی کل نتایج فیلترشده‌اند، نه صفحهٔ جاری.
        Assert.Equal(6000m, page.Totals.TotalQuantityMt);
        Assert.Equal(page.Totals.TotalQuantityMt, all.Totals.TotalQuantityMt);
    }

    [Theory]
    [InlineData(TabularExportFormat.Excel, new byte[] { 0x50, 0x4B })]                 // xlsx = zip
    [InlineData(TabularExportFormat.Pdf, new byte[] { 0x25, 0x50, 0x44, 0x46 })]       // %PDF
    public async Task Export_Document_Renders_To_A_Real_File(TabularExportFormat format, byte[] magic)
    {
        await using var db = NewDb();
        Seed(db);
        AddVoyage(db, id: 1, code: "V-001", vesselId: 1, date: new DateTime(2026, 1, 5), quantityMt: 3905.104m);
        db.ShipmentContracts.Add(new ShipmentContract { Id = 1, ShipmentId = 1, ContractId = 1, QuantityMt = 3905.104m });
        db.ExpenseTransactions.Add(
            NewExpense(id: 1, shipmentId: 1, expenseTypeId: 1, amountUsd: 136_678.64m, serviceProviderId: 1));
        await db.SaveChangesAsync();

        var result = Assert.IsType<TabularExportResult>(
            await NewController(db).VesselVoyagesExport(
                format == TabularExportFormat.Excel ? "excel" : "pdf",
                new VesselVoyageReportFilterViewModel()));

        using var output = new MemoryStream();
        await CreateExportService().WriteAsync(result.Document, format, isEnglish: false, output, CancellationToken.None);

        Assert.True(output.Length > 0);
        Assert.Equal(magic, output.ToArray().Take(magic.Length).ToArray());
    }

    // ===================== زیرساخت تست =====================

    private static Services.Exports.TabularExportService CreateExportService()
    {
        var webRoot = FindWebRoot();
        return new Services.Exports.TabularExportService(
            Microsoft.Extensions.Options.Options.Create(new TabularExportOptions
            {
                ExcelMaxRows = 100_000,
                PdfMaxRows = 20_000,
                CompanyLogoPath = "/invoice-templates/fawad/logo.png",
                QuestPdfLicense = "Community"
            }),
            new VesselVoyageTestWebHostEnvironment
            {
                WebRootPath = webRoot,
                ContentRootPath = Directory.GetParent(webRoot)!.FullName
            });
    }

    private static string FindWebRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
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

    private sealed class VesselVoyageTestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ReportsController NewController(ApplicationDbContext db) => new(db);

    private static Task<VesselVoyageReportViewModel> BuildAsync(
        ApplicationDbContext db,
        VesselVoyageReportFilterViewModel? filter = null)
        => NewController(db).BuildVesselVoyagesForTestAsync(filter ?? new VesselVoyageReportFilterViewModel());

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "OWN", Name = "Owner", IsSystemOwner = true });
        db.Products.AddRange(
            new Product { Id = DieselProductId, Code = "PR001", Name = "DIESEL" },
            new Product { Id = GasolineProductId, Code = "PR002", Name = "Gasoline 92" });
        db.Suppliers.AddRange(
            new Supplier { Id = 1, Code = "SU001", Name = "BONEX FZCO" },
            new Supplier { Id = 2, Code = "SU002", Name = "Petrogas" });
        db.Customers.AddRange(
            new Customer { Id = 1, Code = "CU001", Name = "Afghanistan" },
            new Customer { Id = 2, Code = "CU002", Name = "Armania" });
        db.Vessels.AddRange(
            new Vessel { Id = 1, Code = "VE001", Name = "IGOR ORLOV" },
            new Vessel { Id = 2, Code = "VE002", Name = "HAJI DAVUD" });
        db.ServiceProviders.AddRange(
            new ServiceProvider { Id = 1, Code = "SP001", Name = "TRINTI" },
            new ServiceProvider { Id = 2, Code = "SP002", Name = "ENEYA" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Okream" });
        db.ExpenseTypes.AddRange(
            new ExpenseType { Id = 1, Code = "MAN-FREIGHT", Name = "کرایه کشتی", Category = "Transport" },
            new ExpenseType { Id = 2, Code = "MAN-CUSTOMS", Name = "گمرک", Category = "Customs" },
            new ExpenseType { Id = 3, Code = "TRANSPORT-RECEIPT-FREIGHT", Name = "کرایه رسید حمل", Category = "Transport" },
            // نام‌ها از دادهٔ واقعی گرفته شده‌اند: هر سهٔ اینها Category=Transport دارند و در
            // دسته‌بندی مشترکِ محموله «کرایه» شمرده می‌شوند، ولی کرایهٔ خودِ کشتی نیستند.
            new ExpenseType { Id = 4, Code = "MAN-RAIL", Name = "خط آهن امیر اباد ای شمتغ", Category = "Transport" },
            new ExpenseType { Id = 5, Code = "MAN-TANK", Name = "کرایه مخازن الینکا", Category = "Transport" },
            new ExpenseType { Id = 6, Code = "MAN-DEMURRAGE", Name = "دیمرج کشتی", Category = "Transport" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1, ContractNumber = "P-001", ContractType = ContractType.Purchase,
                CompanyId = 1, ProductId = DieselProductId, SupplierId = 1,
                ContractDate = new DateTime(2026, 1, 1), QuantityMt = 10_000m,
                PricingMethod = PricingMethod.Fixed
            },
            new Contract
            {
                Id = 2, ContractNumber = "P-002", ContractType = ContractType.Purchase,
                CompanyId = 1, ProductId = GasolineProductId, SupplierId = 2,
                ContractDate = new DateTime(2026, 1, 1), QuantityMt = 10_000m,
                PricingMethod = PricingMethod.Fixed
            });
        db.SaveChanges();
    }

    private static void AddVoyage(
        ApplicationDbContext db, int id, string code, int vesselId, DateTime date, decimal quantityMt)
        => db.Shipments.Add(new Shipment
        {
            Id = id,
            ShipmentCode = code,
            VesselId = vesselId,
            DepartureDate = date,
            QuantityMt = quantityMt
        });

    private static ExpenseTransaction NewExpense(
        int id, int shipmentId, int expenseTypeId, decimal amountUsd, int serviceProviderId, bool isCancelled = false)
        => new()
        {
            Id = id,
            ShipmentId = shipmentId,
            ExpenseTypeId = expenseTypeId,
            ServiceProviderId = serviceProviderId,
            ExpenseDate = new DateTime(2026, 1, 10),
            Amount = amountUsd,
            AmountUsd = amountUsd,
            Currency = "USD",
            IsCancelled = isCancelled
        };
}
