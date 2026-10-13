using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ردیف شرکا در «بیلانس کلی شرکت» فقط ماندهٔ واقعیِ شریک با شرکت است: پرداخت شریک، تسویه، عاید فروشِ
/// نزد شریک و سهم مفادِ محقق. سهمِ هزینهٔ کالای فروخته‌نشده تخصیصِ بین شرکاست و شریکِ مالک دفتر
/// خودِ صاحب شرکت است؛ هیچ‌کدام طلب یا بدهی شرکت نیستند. صورت‌حساب شراکت دست‌نخورده می‌ماند.
/// </summary>
public sealed class CompanyBalancePartnerTests
{
    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime To = new(2026, 10, 8);

    [Fact]
    public async Task P001_Unfunded_5050_Partnership_Has_No_Partner_Liability()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], ownerIndex: 0);
        AddP001Expenses(db, s.ContractId);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db,
        [
            Row("Supplier", 1, -90_000m),
            Row("ServiceProvider", 1, -180m),
            Row("Driver", 3, -50m),
            Row("Driver", 4, -50m),
            // موقعیت شراکت در «طلبات و بدهی‌ها» (−۴۶٬۳۹۰ هر کدام)؛ بیلانس از آن نمی‌خواند.
            Row("Partner", s.PartnerIds[0], -46_390m),
            Row("Partner", s.PartnerIds[1], -46_390m)
        ]);

        Assert.Equal(90_000m, model.SupplierPayables);
        Assert.Equal(280m, model.TransportPayables);
        Assert.Equal(0m, model.PartnerPayables);
        Assert.Equal(0m, model.OtherAssets);
        Assert.Equal(90_000m, model.GoodsInTransit);
        Assert.Equal(-2_500m, model.CashAndBank);
        Assert.Equal(2_780m, model.PeriodExpenses);
        Assert.Equal(-2_780m, model.NetCompanyBalance);
        Assert.Equal(model.NetProfitLoss, model.NetCompanyBalance);
    }

    [Fact]
    public async Task Partnership_Statement_Still_Shows_The_Unsold_Cost_Allocation()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], ownerIndex: 0);
        AddP001Expenses(db, s.ContractId);
        await db.SaveChangesAsync();

        var service = new PartnershipStatementService(db);
        foreach (var partnerId in s.PartnerIds)
        {
            var statement = await service.BuildForPartnerAsync(partnerId);
            Assert.NotNull(statement);
            var allocation = Assert.Single(statement!.Entries, e => e.Kind == PartnershipStatementLineKind.UnsoldCostShare);
            Assert.Equal(-46_390m, allocation.EffectUsd);
            Assert.Equal(-46_390m, statement.NetPositionUsd);
        }

        var balances = await new PartnerCompanyBalanceReader(db, service).ReadAsync(To);
        Assert.All(balances, b => Assert.Equal(0m, b.BalanceUsd));
        Assert.Equal(new[] { true, false }, balances.OrderBy(b => b.PartnerId).Select(b => b.IsBookOwner).ToArray());
    }

    [Fact]
    public async Task Partial_External_Funding_Is_A_Partner_Liability_And_Owner_Funding_Is_Not()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], ownerIndex: 0);
        // شریک بیرونی از جیب خودش بخشی از سهمش را به فروشنده داده.
        db.PaymentTransactions.Add(Funding(s.ContractId, 20_000m, PaymentFundingSource.Partner, paidByPartnerId: s.PartnerIds[1]));
        // مالک دفتر از صندوق شرکتِ خودش داده: سرمایهٔ مالک، نه بدهی شرکت.
        db.PaymentTransactions.Add(Funding(s.ContractId, 10_000m, PaymentFundingSource.Company, companyId: s.CompanyId, cashAccountId: 1));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, []);

        Assert.Equal(20_000m, model.PartnerPayables);
        Assert.Equal(0m, model.OtherAssets);
        Assert.Equal(-10_000m, model.CashAndBank);
        var row = Assert.Single(model.DetailsFor(CompanyBalanceSection.PartnerPayables));
        Assert.Equal(s.PartnerIds[1], row.LinkId);
        Assert.Contains(model.NotesFa, note => note.Contains("مالک دفتر") && note.Contains("10,000.00"));
    }

    [Fact]
    public async Task Settlements_Move_The_External_Partner_Balance_And_Reversed_Ones_Do_Not()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], ownerIndex: 0);
        db.PartnerSettlements.AddRange(
            Settlement(s.ContractId, s.PartnerIds[1], s.PartnerIds[0], 5_000m),
            Settlement(s.ContractId, s.PartnerIds[1], s.PartnerIds[0], 7_000m, reversed: true));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, []);

        Assert.Equal(5_000m, model.PartnerPayables);
        Assert.Equal(0m, model.OtherAssets);
    }

    [Fact]
    public async Task Credit_Sale_Does_Not_Create_Cash_Held_By_External_Partner()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], ownerIndex: 0);
        var contract = await db.Contracts.SingleAsync(c => c.Id == s.ContractId);
        contract.SaleProceedsHolderPartnerId = s.PartnerIds[1];
        var customer = new Customer { Name = "Buyer", IsActive = true };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.SalesTransactions.Add(new SalesTransaction
        {
            CompanyId = s.CompanyId,
            ProductId = s.ProductId,
            CustomerId = customer.Id,
            InvoiceNumber = "S-1",
            SaleDate = To,
            QuantityMt = 50m,
            UnitPriceUsd = 1_000m,
            TotalUsd = 50_000m,
            Currency = "USD",
            TotalInCurrency = 50_000m,
            AppliedFxRateToUsd = 1m,
            SourcePurchaseContractId = s.ContractId
        });
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, []);

        // عاید ۵۰٬۰۰۰ نزد شریک − سهم ۵۰٪ از مفاد محقق (۵۰٬۰۰۰ − ۵۰ × ۹۰۰ = ۵٬۰۰۰) = ۴۷٬۵۰۰ طلب شرکت.
        Assert.Equal(0m, model.OtherAssets);
        Assert.Equal(2_500m, model.PartnerPayables);
    }

    [Fact]
    public async Task Multi_Partner_Contract_Counts_Only_Real_Funding_For_Any_Share_Split()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [40m, 30m, 30m], ownerIndex: 0);
        db.PaymentTransactions.Add(Funding(s.ContractId, 30_000m, PaymentFundingSource.Partner, paidByPartnerId: s.PartnerIds[1]));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, []);

        var row = Assert.Single(model.DetailsFor(CompanyBalanceSection.PartnerPayables));
        Assert.Equal(s.PartnerIds[1], row.LinkId);
        Assert.Equal(30_000m, model.PartnerPayables);
        // شریک سوم هیچ پولی نداده و سهم هزینه‌اش تخصیص است، نه طلب شرکت.
        Assert.Equal(0m, model.OtherAssets);
    }

    [Fact]
    public async Task Company_Without_Book_Owner_Shows_Every_Funding_Partner()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], ownerIndex: null);
        db.PaymentTransactions.Add(Funding(s.ContractId, 45_000m, PaymentFundingSource.Partner, paidByPartnerId: s.PartnerIds[0]));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, []);

        Assert.Equal(45_000m, model.PartnerPayables);
        Assert.Equal(0m, model.OtherAssets);
        Assert.DoesNotContain(model.NotesFa, note => note.Contains("مالک دفتر"));
    }

    [Fact]
    public async Task Partner_Events_After_The_Report_Date_Are_Not_In_The_Balance()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], ownerIndex: 0);
        db.PaymentTransactions.Add(Funding(s.ContractId, 20_000m, PaymentFundingSource.Partner,
            paidByPartnerId: s.PartnerIds[1], date: To.AddDays(1)));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db, []);

        Assert.Equal(0m, model.PartnerPayables);
    }

    [Fact]
    public void Receivables_Totals_Read_Partner_Balances_With_The_Partner_Sign()
    {
        var model = new ReceivablesPayablesReportViewModel
        {
            AsOfDate = To,
            Rows =
            [
                Row("Partner", 1, 2_000m),   // شریک طلبکار → بدهی شرکت
                Row("Partner", 2, -500m),    // شریک بدهکار → طلب شرکت
                Row("Customer", 3, 1_000m),
                Row("Supplier", 4, -3_000m)
            ]
        };

        Assert.Equal(1_500m, model.TotalReceivableUsd);
        Assert.Equal(5_000m, model.TotalPayableUsd);
        Assert.Equal(-3_500m, model.NetBalanceUsd);
        Assert.Equal(1_500m, model.StaleReceivableUsd);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_Company_Reports_Exclude_Unfunded_Allocation_And_Owner(bool threePartners)
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, threePartners ? [40m, 30m, 30m] : [50m, 50m], 0);
        AddP001Expenses(db, s.ContractId);
        await db.SaveChangesAsync();
        var controller = new PTGOilSystem.Web.Controllers.ReportsController(db);
        var filter = new ManagementReportFilterViewModel { FromDate = From, ToDate = To };
        var report = Assert.IsType<ReceivablesPayablesReportViewModel>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.ReceivablesPayables(filter)).Model);
        Assert.DoesNotContain(report.Rows, r => r.PartyType == "Partner");
        var aging = Assert.IsType<PartyAgingReportViewModel>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.PartyAging(filter)).Model);
        Assert.Equal(report.TotalReceivableUsd, aging.TotalReceivableUsd);
        Assert.Equal(report.TotalPayableUsd, aging.TotalPayableUsd);
        var overview = Assert.IsType<CompanyFinancialOverviewViewModel>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.CompanyOverview(filter)).Model);
        Assert.Equal(report.TotalReceivableUsd, overview.TotalReceivableUsd);
        Assert.Equal(report.TotalPayableUsd, overview.TotalPayableUsd);
        foreach (var format in new[] { "xlsx", "pdf" })
        {
            var export = Assert.IsType<PTGOilSystem.Web.Services.Exports.TabularExportResult>(
                await controller.PartyAgingExport(format, filter));
            Assert.Equal(aging.TotalReceivableUsd, export.Document.Totals!.Cells[2].Value);
            Assert.Equal(aging.TotalPayableUsd, export.Document.Totals.Cells[3].Value);
            Assert.Equal(aging.Rows.Count, export.Document.Rows.Count());
            Assert.Contains("آخرین حرکت", export.Document.TitleFa);
            var balancesExport = Assert.IsType<PTGOilSystem.Web.Services.Exports.TabularExportResult>(
                await controller.ReceivablesPayablesExport(format, filter));
            Assert.Equal(report.Rows.Count, balancesExport.Document.Rows.Count());
            Assert.Equal(report.Rows.Sum(r => r.PeriodMovementUsd), balancesExport.Document.Totals!.Cells[6].Value);
            Assert.DoesNotContain(balancesExport.Document.Columns, c => c.TitleFa.Contains("تعدیل ارزی"));
        }
        var profile = await new PartnershipStatementService(db).BuildForPartnerAsync(s.PartnerIds[1]);
        Assert.True(profile!.UnsoldCostShareUsd > 0m);
    }

    [Theory]
    [InlineData(0, 0, 2500)]
    [InlineData(10000, 0, -7500)]
    [InlineData(10000, 4000, -3500)]
    public async Task Customer_Cash_Custody_Counts_Only_Actual_Net_Receipts(decimal received, decimal refund, decimal expected)
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], 0);
        var sale = await AddSaleAsync(db, s, To, 50m, 50_000m);
        if (received > 0m) db.PaymentTransactions.Add(Receipt(s, sale, received, To));
        if (refund > 0m) db.PaymentTransactions.Add(Receipt(s, sale, refund, To, refund: true));
        await db.SaveChangesAsync();
        var balances = await new PartnerCompanyBalanceReader(db, new PartnershipStatementService(db)).ReadAsync(To);
        Assert.Equal(expected, balances.Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
        var profile = await new PartnershipStatementService(db).BuildForPartnerAsync(s.PartnerIds[1]);
        Assert.Equal(50_000m, profile!.ProceedsHeldUsd); // internal statement convention stays intact
    }

    [Fact]
    public async Task Company_Cash_Receipt_And_Partner_Personal_Funding_Are_Not_Customer_Custody()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], 0);
        var sale = await AddSaleAsync(db, s, To, 50m, 50_000m);
        var receipt = Receipt(s, sale, 50_000m, To);
        receipt.FundingSource = PaymentFundingSource.Company;
        receipt.CashAccountId = 1;
        db.PaymentTransactions.Add(receipt);
        db.PaymentTransactions.Add(Funding(s.ContractId, 10_000m, PaymentFundingSource.Partner, s.PartnerIds[1]));
        await db.SaveChangesAsync();
        var balances = await new PartnerCompanyBalanceReader(db, new PartnershipStatementService(db)).ReadAsync(To);
        Assert.Equal(12_500m, balances.Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
    }

    [Fact]
    public async Task Profit_Uses_Each_Sales_Margin_And_Historical_Share_Not_Last_Sale_Date()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], 0);
        var loading = await db.LoadingRegisters.ToListAsync();
        foreach (var l in loading) l.LoadingDate = From;
        foreach (var cp in await db.ContractPartners.ToListAsync())
        {
            cp.EffectiveFrom = From;
            cp.EffectiveTo = From.AddDays(4);
        }
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = s.ContractId, PartnerId = s.PartnerIds[0], SharePercent = 80m, EffectiveFrom = From.AddDays(4) },
            new ContractPartner { ContractId = s.ContractId, PartnerId = s.PartnerIds[1], SharePercent = 20m, EffectiveFrom = From.AddDays(4) });
        await AddSaleAsync(db, s, From.AddDays(1), 50m, 50_000m); // profit 5000, external share 2500
        await AddSaleAsync(db, s, To, 50m, 60_000m); // profit 15000, external share 3000
        var reader = new PartnerCompanyBalanceReader(db, new PartnershipStatementService(db));
        Assert.All(await reader.ReadAsync(From), b => Assert.Equal(0m, b.BalanceUsd));
        Assert.Equal(2_500m, (await reader.ReadAsync(From.AddDays(2))).Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
        Assert.Equal(5_500m, (await reader.ReadAsync(To)).Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
        var events = await reader.ReadEventsAsync(To);
        Assert.Contains(events, e => e.PartnerId == s.PartnerIds[1] && e.Date == From.AddDays(1) && e.EffectUsd == 2_500m);
    }

    [Fact]
    public async Task Future_Expenses_Receipts_And_Cancelled_Sales_Do_Not_Leak_Into_Historical_Report()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], 0);
        var sale = await AddSaleAsync(db, s, To, 50m, 50_000m);
        db.PaymentTransactions.Add(Receipt(s, sale, 10_000m, To.AddDays(1)));
        var type = new ExpenseType { Code = "FUTURE", Name = "Future" };
        db.ExpenseTransactions.Add(new ExpenseTransaction { ExpenseType = type, ContractId = s.ContractId,
            ExpenseDate = To.AddDays(1), Amount = 10_000m, AmountUsd = 10_000m, Currency = "USD" });
        var cancelled = await AddSaleAsync(db, s, To, 50m, 70_000m);
        cancelled.IsCancelled = true;
        await db.SaveChangesAsync();
        Assert.Equal(2_500m, (await new PartnerCompanyBalanceReader(db, new PartnershipStatementService(db)).ReadAsync(To))
            .Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
    }

    [Fact]
    public async Task Reversing_Cash_Application_Does_Not_Return_Cash_Or_Count_It_Twice()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], 0);
        var sale = await AddSaleAsync(db, s, To, 50m, 50_000m);
        var receipt = Receipt(s, sale, 10_000m, To);
        receipt.ContractId = null;
        receipt.SalesTransactionId = null;
        db.PaymentTransactions.Add(receipt);
        await db.SaveChangesAsync();
        var application = new CustomerPaymentAllocationApplication { PaymentTransactionId = receipt.Id,
            SalesTransactionId = sale.Id, AppliedAt = To, AppliedAmountUsd = 10_000m, AppliedPaymentAmount = 10_000m };
        db.CustomerPaymentAllocationApplications.Add(application);
        await db.SaveChangesAsync();
        var reader = new PartnerCompanyBalanceReader(db, new PartnershipStatementService(db));
        Assert.Equal(-7_500m, (await reader.ReadAsync(To)).Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
        application.Status = CustomerPaymentAllocationApplicationStatus.Reversed;
        application.ReversedAtUtc = To;
        await db.SaveChangesAsync();
        Assert.Equal(-7_500m, (await reader.ReadAsync(To)).Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
    }

    [Fact]
    public async Task Settlement_Reversal_Preserves_Report_Before_Reversal_Date()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], 0);
        var settlement = Settlement(s.ContractId, s.PartnerIds[1], s.PartnerIds[0], 5_000m, reversed: true);
        settlement.ReversedAtUtc = To.AddDays(1);
        db.PartnerSettlements.Add(settlement);
        await db.SaveChangesAsync();
        var reader = new PartnerCompanyBalanceReader(db, new PartnershipStatementService(db));
        Assert.Equal(5_000m, (await reader.ReadAsync(To)).Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
        Assert.Equal(0m, (await reader.ReadAsync(To.AddDays(1))).Single(b => b.PartnerId == s.PartnerIds[1]).BalanceUsd);
    }

    private static async Task<SalesTransaction> AddSaleAsync(ApplicationDbContext db, Scenario s, DateTime date, decimal quantity, decimal amount)
    {
        var customer = new Customer { Name = "Buyer", IsActive = true };
        var contract = await db.Contracts.SingleAsync(c => c.Id == s.ContractId);
        contract.SaleProceedsHolderPartnerId = s.PartnerIds[1];
        var sale = new SalesTransaction { CompanyId = s.CompanyId, ProductId = s.ProductId, Customer = customer,
            InvoiceNumber = $"S-{Guid.NewGuid():N}", SaleDate = date, QuantityMt = quantity, TotalUsd = amount,
            UnitPriceUsd = amount / quantity, Currency = "USD", SourcePurchaseContractId = s.ContractId };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }

    [Fact]
    public async Task Partial_Receipt_Moves_Claim_From_Customer_To_Partner_Exactly_Once()
    {
        await using var db = NewDb();
        var s = await SeedAsync(db, [50m, 50m], 0);
        var sale = await AddSaleAsync(db, s, To, 50m, 50_000m);
        db.LedgerEntries.Add(new LedgerEntry { EntryDate = To, Side = LedgerSide.Debit, AmountUsd = 50_000m,
            CustomerId = sale.CustomerId, SourceType = "Sale", SourceId = sale.Id });
        var receipt = Receipt(s, sale, 10_000m, To);
        var ledger = new LedgerEntry { EntryDate = To, Side = LedgerSide.Debit, AmountUsd = 10_000m,
            CustomerId = sale.CustomerId, SourceType = "CustomerReceipt", SourceId = 0 };
        db.LedgerEntries.Add(ledger);
        await db.SaveChangesAsync();
        receipt.LedgerEntryId = ledger.Id;
        db.PaymentTransactions.Add(receipt);
        await db.SaveChangesAsync();
        ledger.SourceId = receipt.Id;
        await db.SaveChangesAsync();
        var controller = new PTGOilSystem.Web.Controllers.ReportsController(db);
        var filter = new ManagementReportFilterViewModel { FromDate = From, ToDate = To };
        var report = Assert.IsType<ReceivablesPayablesReportViewModel>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.ReceivablesPayables(filter)).Model);
        Assert.Equal(40_000m, report.CustomerReceivableUsd);
        Assert.Equal(7_500m, report.Rows.Single(r => r.PartyType == "Partner").CompanyClaimUsd);
        Assert.Equal(47_500m, report.TotalReceivableUsd);
        Assert.DoesNotContain(report.Rows, r => r.PartyType == "Partner" && r.PartyId == s.PartnerIds[0]);
        var aging = Assert.IsType<PartyAgingReportViewModel>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.PartyAging(filter)).Model);
        Assert.Equal(report.TotalReceivableUsd, aging.TotalReceivableUsd);
        Assert.Equal(report.TotalPayableUsd, aging.TotalPayableUsd);
    }

    private static PaymentTransaction Receipt(Scenario s, SalesTransaction sale, decimal amount, DateTime date, bool refund = false)
        => new() { PaymentDate = date, ContractId = s.ContractId, SalesTransactionId = sale.Id, CustomerId = sale.CustomerId,
            PaidByPartnerId = s.PartnerIds[1], FundingSource = PaymentFundingSource.Partner,
            Direction = refund ? PaymentDirection.Out : PaymentDirection.In,
            PaymentKind = refund ? PaymentKind.CustomerPayment : PaymentKind.CustomerReceipt,
            Amount = amount, AmountUsd = amount, Currency = "USD", AppliedFxRateToUsd = 1m };

    // ── زیرساخت ─────────────────────────────────────────────────────────────

    private sealed record Scenario(int ContractId, int CompanyId, int ProductId, int[] PartnerIds);

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "CASH", Name = "Main Cash", Currency = "USD", AccountType = CashAccountType.Cash });
        db.SaveChanges();
        return db;
    }

    /// <summary>قرارداد خرید شراکتی ۱۰۰ تن: دو بارگیری ۵۰ تنی به ۹۰۰، بدون رسید.</summary>
    private static async Task<Scenario> SeedAsync(ApplicationDbContext db, decimal[] shares, int? ownerIndex)
    {
        var partners = shares
            .Select((_, i) => new Partner { Code = $"PA{i + 1:000}", Name = $"Partner {i + 1}", IsActive = true })
            .ToArray();
        var product = new Product { Code = "GAS", Name = "Gas" };
        var supplier = new Supplier { Name = "Supplier", IsActive = true };
        db.Partners.AddRange(partners);
        db.AddRange(product, supplier);
        await db.SaveChangesAsync();

        var company = new Company
        {
            Code = "NOVA",
            Name = "Nova",
            OwnerPartnerId = ownerIndex is { } owner ? partners[owner].Id : null
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = "P-001",
            ContractType = ContractType.Purchase,
            OwnershipType = ContractOwnershipType.Partnership,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            Currency = "USD",
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 900m,
            ContractDate = From
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        db.ContractPartners.AddRange(partners.Select((p, i) => new ContractPartner
        {
            ContractId = contract.Id,
            PartnerId = p.Id,
            SharePercent = shares[i],
            EffectiveFrom = From
        }));
        db.LoadingRegisters.AddRange(
            new LoadingRegister { ContractId = contract.Id, ProductId = product.Id, LoadingDate = To, LoadedQuantityMt = 50m, LoadingPriceUsd = 900m },
            new LoadingRegister { ContractId = contract.Id, ProductId = product.Id, LoadingDate = To, LoadedQuantityMt = 50m, LoadingPriceUsd = 900m });
        await db.SaveChangesAsync();

        return new Scenario(contract.Id, company.Id, product.Id, partners.Select(p => p.Id).ToArray());
    }

    /// <summary>مصارف P-001: کرایهٔ بارگیری ۵۰+۵۰ و حمل ۱۰۰+۸۰ (بدهی)، گمرک ۱٬۵۰۰+۱٬۰۰۰ (نقد از صندوق).</summary>
    private static void AddP001Expenses(ApplicationDbContext db, int contractId)
    {
        var type = new ExpenseType { Code = "OPS", Name = "Operations" };
        db.ExpenseTypes.Add(type);
        ExpenseTransaction Expense(decimal usd, ExpenseSettlementMode mode)
            => new()
            {
                ExpenseType = type,
                ContractId = contractId,
                ExpenseDate = To,
                Amount = usd,
                Currency = "USD",
                AmountUsd = usd,
                SettlementMode = mode,
                CashAccountId = mode == ExpenseSettlementMode.PaidImmediately ? 1 : null,
                Description = "P-001"
            };

        db.ExpenseTransactions.AddRange(
            Expense(50m, ExpenseSettlementMode.Payable),
            Expense(50m, ExpenseSettlementMode.Payable),
            Expense(100m, ExpenseSettlementMode.Payable),
            Expense(80m, ExpenseSettlementMode.Payable),
            Expense(1_500m, ExpenseSettlementMode.PaidImmediately),
            Expense(1_000m, ExpenseSettlementMode.PaidImmediately));
    }

    private static PaymentTransaction Funding(
        int contractId,
        decimal usd,
        PaymentFundingSource source,
        int? paidByPartnerId = null,
        int? companyId = null,
        int? cashAccountId = null,
        DateTime? date = null)
        => new()
        {
            PaymentDate = date ?? To,
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            ContractId = contractId,
            Amount = usd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = usd,
            FundingSource = source,
            PaidByPartnerId = paidByPartnerId,
            CompanyId = companyId,
            CashAccountId = cashAccountId,
            Description = "funding"
        };

    private static PartnerSettlement Settlement(int contractId, int fromPartnerId, int toPartnerId, decimal usd, bool reversed = false)
        => new()
        {
            SettlementDate = To,
            FromPartnerId = fromPartnerId,
            ToPartnerId = toPartnerId,
            ContractId = contractId,
            Amount = usd,
            Currency = "USD",
            AmountUsd = usd,
            IsReversed = reversed
        };

    private static ReceivablePayableRowViewModel Row(string type, int id, decimal balance)
        => new() { PartyType = type, PartyId = id, PartyName = $"{type}-{id}", OpeningBalanceUsd = balance };

    private static Task<CompanyBalanceReportViewModel> BuildAsync(
        ApplicationDbContext db,
        IReadOnlyList<ReceivablePayableRowViewModel> partyRows)
        => new CompanyBalanceReportService(db, new ProfitAndLossService(db), new StockService(db), new PricingService(db))
            .BuildAsync(new CompanyBalanceReportRequest(From, To, "USD", To), partyRows);
}
