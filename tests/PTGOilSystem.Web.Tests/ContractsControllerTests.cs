using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Ledger;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ContractsControllerTests
{
    [Theory]
    [InlineData("دیزل جنوری ۲۰۲۶", "CNT-2026-014", "دیزل جنوری ۲۰۲۶ — CNT-2026-014")]
    [InlineData("", "CNT-2026-014", "CNT-2026-014")]
    [InlineData("CNT-2026-014", "CNT-2026-014", "CNT-2026-014")]
    public void Contract_DisplayLabel_Uses_Name_And_Number_With_Legacy_Fallback(
        string contractName,
        string contractNumber,
        string expected)
    {
        var contract = new Contract { ContractName = contractName, ContractNumber = contractNumber };

        Assert.Equal(expected, contract.DisplayLabel);
        Assert.Equal(expected, ContractUiText.FormatDisplayLabel(contractName, contractNumber));
    }

    [Fact]
    public void ContractFormViewModel_ContractName_Is_Optional()
    {
        var model = new ContractFormViewModel { ContractName = null };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);

        Assert.DoesNotContain(results, result => result.MemberNames.Contains(nameof(ContractFormViewModel.ContractName)));
    }

    [Fact]
    public async Task Index_Search_Finds_Contract_By_Name()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Contracts.Single().ContractName = "دیزل جنوری ۲۰۲۶";
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index("جنوری", null, null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractIndexViewModel>(view.Model);
        Assert.Single(model.Items);
        Assert.Equal("دیزل جنوری ۲۰۲۶ — PUR-001", model.Items[0].DisplayLabel);
    }

    [Fact]
    public async Task Create_Persists_Normalized_ContractName()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.ContractName = "  دیزل جنوری ۲۰۲۶  ";

        var result = await controller.Create(model);

        Assert.IsType<RedirectToActionResult>(result);
        var saved = await db.Contracts.SingleAsync(c => c.Id != 1);
        Assert.Equal("دیزل جنوری ۲۰۲۶", saved.ContractName);
        Assert.Equal($"دیزل جنوری ۲۰۲۶ — {saved.ContractNumber}", saved.DisplayLabel);
    }

    [Fact]
    public async Task Details_Redirects_To_Locked_ContractJourney_Summary()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        var controller = BuildController(db);

        var result = await controller.Details(1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ContractJourneyController.Details), redirect.ActionName);
        Assert.Equal("ContractJourney", redirect.ControllerName);
        Assert.Equal(1, redirect.RouteValues?["contractId"]);
        Assert.Equal(ContractJourneyTabs.Details.Summary, redirect.RouteValues?["tab"]);
        Assert.Equal(true, redirect.RouteValues?["lockContract"]);
    }

    [Theory]
    [InlineData("loading", ContractJourneyTabs.Details.Loadings)]
    [InlineData("operations", ContractJourneyTabs.Details.Loadings)]
    [InlineData("loadings", ContractJourneyTabs.Details.Loadings)]
    [InlineData("receipts", ContractJourneyTabs.Details.Receipts)]
    [InlineData("loading-expenses", ContractJourneyTabs.Details.Costs)]
    [InlineData("dispatch", ContractJourneyTabs.Details.Dispatch)]
    [InlineData("sales", ContractJourneyTabs.Details.Sales)]
    [InlineData("expenses", ContractJourneyTabs.Details.Costs)]
    [InlineData("shipment-pnl", ContractJourneyTabs.Details.Ledger)]
    [InlineData("dashboard", ContractJourneyTabs.Details.Summary)]
    [InlineData("unknown", ContractJourneyTabs.Details.Summary)]
    public async Task Details_Maps_Legacy_Contract_Tabs_To_ContractJourney(string? legacyTab, string expectedJourneyTab)
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        var controller = BuildController(db);

        var result = await controller.Details(1, legacyTab);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(expectedJourneyTab, redirect.RouteValues?["tab"]);
        Assert.Equal(true, redirect.RouteValues?["lockContract"]);
    }

    [Fact]
    public async Task Create_Fixed_NonUsd_Converts_UnitPrice_And_Persists_Partners()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.AddRange(
            new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", IsActive = true });
        db.Units.Add(new Unit { Id = 2, Code = "BBL", Name = "Barrel", Symbol = "bbl", IsActive = true });
        db.DailyFxRates.Add(new DailyFxRate
        {
            Id = 1,
            BaseCurrency = "EUR",
            QuoteCurrency = "USD",
            RateDate = new DateTime(2026, 4, 23),
            Rate = 1.2m
        });
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "P1", Name = "Partner 1", IsActive = true },
            new Partner { Id = 2, Code = "P2", Name = "Partner 2", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-EUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Draft,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 2,
            SupplierId = 1,
            OwnershipType = ContractOwnershipType.Partnership,
            ContractDate = new DateTime(2026, 4, 23),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 100m,
            Currency = "EUR",
            UnitPriceInCurrency = 100m,
            PartnerShares =
            [
                new ContractPartnerShareInput { PartnerId = 1, SharePercent = 60m },
                new ContractPartnerShareInput { PartnerId = 2, SharePercent = 40m }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var contract = await db.Contracts
            .Include(c => c.ContractPartners)
            .SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal("EUR", contract.Currency);
        Assert.Equal(2, contract.UnitId);
        Assert.Equal(100m, contract.UnitPriceInCurrency);
        Assert.Equal(1.2m, contract.AppliedFxRateToUsd);
        Assert.Equal(120m, contract.UnitPriceUsd);
        Assert.Equal(ContractOwnershipType.Partnership, contract.OwnershipType);
        Assert.Equal(2, contract.ContractPartners.Count);
    }

    [Fact]
    public async Task Create_Returns_View_When_Partner_Shares_Do_Not_Total_100()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "P1", Name = "Partner 1", IsActive = true },
            new Partner { Id = 2, Code = "P2", Name = "Partner 2", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-SHARE-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Draft,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            OwnershipType = ContractOwnershipType.Partnership,
            ContractDate = new DateTime(2026, 4, 23),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 100m,
            Currency = "USD",
            UnitPriceInCurrency = 450m,
            PartnerShares =
            [
                new ContractPartnerShareInput { PartnerId = 1, SharePercent = 60m },
                new ContractPartnerShareInput { PartnerId = 2, SharePercent = 30m }
            ]
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractFormViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal("P-001", model.ContractNumber);
        Assert.Single(await db.Contracts.Where(c => c.ContractNumber == "PUR-001").ToListAsync());
    }

    [Fact]
    public async Task Create_Personal_Ignores_Posted_Partner_Shares()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "P1", Name = "Partner 1", IsActive = true },
            new Partner { Id = 2, Code = "P2", Name = "Partner 2", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-PERSONAL-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Draft,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = new DateTime(2026, 4, 23),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 100m,
            Currency = "USD",
            UnitPriceInCurrency = 450m,
            PartnerShares =
            [
                new ContractPartnerShareInput { PartnerId = 1, SharePercent = 60m },
                new ContractPartnerShareInput { PartnerId = 2, SharePercent = 40m }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var contract = await db.Contracts
            .Include(c => c.ContractPartners)
            .SingleAsync(c => c.ContractNumber == "P-001");

        Assert.Equal(ContractOwnershipType.Personal, contract.OwnershipType);
        Assert.Empty(contract.ContractPartners);
    }

    [Fact]
    public async Task Create_NotApplicableRubPolicy_Clears_Tampered_RubFields()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.SettlementCurrencyCode = "RUB";
        model.RubRatePolicy = RubSettlementRatePolicy.NotApplicable;
        model.ContractRubPerUsdRate = 92m;
        model.ContractRubRateDate = new DateTime(2026, 5, 1);
        model.ContractRubRateSource = "Tampered";

        var result = await controller.Create(model);

        Assert.IsType<RedirectToActionResult>(result);
        var contract = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal("USD", contract.SettlementCurrencyCode);
        Assert.Equal(RubSettlementRatePolicy.NotApplicable, contract.RubRatePolicy);
        Assert.Null(contract.ContractRubPerUsdRate);
        Assert.Null(contract.ContractRubRateDate);
        Assert.Null(contract.ContractRubRateSource);
    }

    [Fact]
    public async Task Create_FixedRubPolicy_Requires_Positive_RubRate()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        model.ContractRubPerUsdRate = null;

        var result = await controller.Create(model);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.ContractRubPerUsdRate)));
        Assert.False(await db.Contracts.AnyAsync(c => c.ContractNumber == "P-001"));
    }

    [Fact]
    public async Task Create_FixedRubPolicy_Persists_RubRate_And_Sets_RubSettlement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        model.ContractRubPerUsdRate = 92m;

        var result = await controller.Create(model);

        Assert.IsType<RedirectToActionResult>(result);
        var contract = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal("RUB", contract.SettlementCurrencyCode);
        Assert.Equal(RubSettlementRatePolicy.FixedContractRate, contract.RubRatePolicy);
        Assert.Equal(92m, contract.ContractRubPerUsdRate);
    }

    [Theory]
    [InlineData(RubSettlementRatePolicy.PerLoadingRate)]
    [InlineData(RubSettlementRatePolicy.RateLater)]
    public async Task Create_RubPolicyWithoutContractRate_Allows_Blank_RubRate(RubSettlementRatePolicy policy)
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.RubRatePolicy = policy;
        model.ContractRubPerUsdRate = null;

        var result = await controller.Create(model);

        Assert.IsType<RedirectToActionResult>(result);
        var contract = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal("RUB", contract.SettlementCurrencyCode);
        Assert.Equal(policy, contract.RubRatePolicy);
        Assert.Null(contract.ContractRubPerUsdRate);
    }

    [Theory]
    [InlineData("AED", 3.6725)]
    [InlineData("EUR", 0.92)]
    public async Task Create_FixedSettlement_NonRubCurrency_Persists_And_Keeps_Usd_Pricing(string settlementCode, double rate)
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Currencies.Add(new Currency { Id = 2, Code = settlementCode, Name = settlementCode, IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.SettlementCurrencyCode = settlementCode;
        model.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        model.ContractRubPerUsdRate = (decimal)rate;

        var result = await controller.Create(model);

        Assert.IsType<RedirectToActionResult>(result);
        var contract = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal(settlementCode, contract.SettlementCurrencyCode);
        Assert.Equal(RubSettlementRatePolicy.FixedContractRate, contract.RubRatePolicy);
        Assert.Equal((decimal)rate, contract.ContractRubPerUsdRate);
        // ارز پایه/حسابداری همچنان USD: ارز قیمت و ارزش دالری قرارداد تغییر نکرده است.
        Assert.Equal("USD", contract.Currency);
        Assert.Equal(450m, contract.UnitPriceUsd);
    }

    [Fact]
    public async Task Create_RubSettlement_Still_Persists_As_Rub()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Currencies.Add(new Currency { Id = 2, Code = "RUB", Name = "Russian Ruble", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.SettlementCurrencyCode = "RUB";
        model.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        model.ContractRubPerUsdRate = 92m;

        var result = await controller.Create(model);

        Assert.IsType<RedirectToActionResult>(result);
        var contract = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal("RUB", contract.SettlementCurrencyCode);
        Assert.Equal(92m, contract.ContractRubPerUsdRate);
    }

    [Fact]
    public async Task Create_FixedSettlement_InactiveCurrency_Is_Rejected()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.SettlementCurrencyCode = "AED"; // not in master data
        model.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        model.ContractRubPerUsdRate = 3.6725m;

        var result = await controller.Create(model);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.SettlementCurrencyCode)));
        Assert.False(await db.Contracts.AnyAsync(c => c.ContractNumber == "P-001"));
    }

    [Fact]
    public async Task Create_FixedSettlement_NonRub_MissingRate_Is_Rejected()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Currencies.Add(new Currency { Id = 2, Code = "AED", Name = "AED", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.SettlementCurrencyCode = "AED";
        model.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        model.ContractRubPerUsdRate = null;

        var result = await controller.Create(model);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.ContractRubPerUsdRate)));
        Assert.False(await db.Contracts.AnyAsync(c => c.ContractNumber == "P-001"));
    }

    [Theory]
    [InlineData(RubSettlementRatePolicy.PerLoadingRate)]
    [InlineData(RubSettlementRatePolicy.RateLater)]
    public async Task Create_NonRubSettlement_PerLoadingOrLater_Is_Rejected(RubSettlementRatePolicy policy)
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Currencies.Add(new Currency { Id = 2, Code = "AED", Name = "AED", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = NewBaseCreateModel();
        model.SettlementCurrencyCode = "AED";
        model.RubRatePolicy = policy; // per-loading / later only wired for RUB in this phase

        var result = await controller.Create(model);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.RubRatePolicy)));
        Assert.False(await db.Contracts.AnyAsync(c => c.ContractNumber == "P-001"));
    }

    [Fact]
    public async Task Edit_Personal_Removes_Existing_Partner_Shares()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "P1", Name = "Partner 1", IsActive = true },
            new Partner { Id = 2, Code = "P2", Name = "Partner 2", IsActive = true });
        db.ContractPartners.AddRange(
            new ContractPartner { Id = 1, ContractId = 1, PartnerId = 1, SharePercent = 60m },
            new ContractPartner { Id = 2, ContractId = 1, PartnerId = 2, SharePercent = 40m });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Edit(1, new ContractFormViewModel
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = new DateTime(2026, 4, 23),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 100m,
            Currency = "USD",
            UnitPriceInCurrency = 450m,
            PartnerShares =
            [
                new ContractPartnerShareInput { PartnerId = 1, SharePercent = 60m },
                new ContractPartnerShareInput { PartnerId = 2, SharePercent = 40m }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var contract = await db.Contracts
            .Include(c => c.ContractPartners)
            .SingleAsync(c => c.Id == 1);

        Assert.Equal(ContractOwnershipType.Personal, contract.OwnershipType);
        Assert.Empty(contract.ContractPartners);
    }

    [Fact]
    public async Task Edit_Updates_Core_Contract_Fields()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Units.Add(new Unit { Id = 2, Code = "BBL", Name = "Barrel", Symbol = "bbl", IsActive = true });
        db.Companies.Add(new Company { Id = 2, Code = "ALT", Name = "Alternate Company", IsActive = true });
        db.Products.Add(new Product { Id = 2, Code = "LPG", Name = "LPG", UnitId = 2, UnitOfMeasure = "BBL", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier B", IsActive = true });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Edit(1, new ContractFormViewModel
        {
            Id = 1,
            ContractNumber = "pur-updated-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 2,
            ProductId = 2,
            UnitId = 2,
            SupplierId = 2,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = new DateTime(2026, 5, 10),
            StartDate = new DateTime(2026, 5, 11),
            EndDate = new DateTime(2026, 12, 31),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 125m,
            Currency = "USD",
            UnitPriceInCurrency = 475m
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var contract = await db.Contracts.SingleAsync(c => c.Id == 1);
        Assert.Equal("PUR-UPDATED-001", contract.ContractNumber);
        Assert.Equal(2, contract.CompanyId);
        Assert.Equal(2, contract.ProductId);
        Assert.Equal(2, contract.UnitId);
        Assert.Equal(2, contract.SupplierId);
        Assert.Equal(new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc), contract.ContractDate);
        Assert.Equal(new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc), contract.StartDate);
    }

    [Fact]
    public async Task Edit_Post_Syncs_Loading_Prices_When_Final_Price_Changes()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 20m,
            LoadingPriceUsd = null
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Edit(1, new ContractFormViewModel
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = new DateTime(2026, 4, 23),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 100m,
            Currency = "USD",
            ManualFinalPriceUsd = 700m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 10);
        Assert.Equal(700m, loading.LoadingPriceUsd);
    }

    [Fact]
    public async Task Edit_Post_Does_Not_Reprice_Or_Relock_Finalized_Loading()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 500m;
        contract.SettlementCurrencyCode = "RUB";
        contract.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        contract.ContractRubPerUsdRate = 90m;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 12,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 6),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "RUB",
            RubPerUsdRate = 90m,
            RubRateStatus = RubSettlementRateStatus.Locked,
            AmountUsdAtRubLock = 5_000m,
            AmountRubAtRubLock = 450_000m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // قاعدهٔ #9: ویرایش عمومی قرارداد هم مسیر عمومی است — نرخ جدید نباید بارگیریِ قطعی‌شده را عوض کند.
        var result = await controller.Edit(1, new ContractFormViewModel
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = new DateTime(2026, 4, 23),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 100m,
            Currency = "USD",
            ManualFinalPriceUsd = 600m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.FixedContractRate,
            ContractRubPerUsdRate = 90m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 12);
        Assert.Equal(500m, loading.LoadingPriceUsd);
        Assert.Equal(5_000m, loading.AmountUsdAtRubLock);
        Assert.Equal(450_000m, loading.AmountRubAtRubLock);
        Assert.Equal(90m, loading.RubPerUsdRate);
    }

    [Fact]
    public async Task Delete_Allows_Active_Contract_Without_Dependencies()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var controller = BuildController(db);

        var result = await controller.Delete(1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.False(await db.Contracts.AnyAsync(c => c.Id == 1));
    }

    [Fact]
    public async Task Delete_Blocks_Contract_With_Loading_Dependency()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 20m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Delete(1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.True(await db.Contracts.AnyAsync(c => c.Id == 1));
    }

    [Fact]
    public async Task Create_ManualFinalPrice_Contract_Persists_Price_And_Note()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-002",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 200m,
            Currency = "USD",
            PricingMethod = PricingMethod.ManualFinalPrice,
            ManualFinalPriceUsd = 585m,
            PricingFormulaNote = "توافق شخصی"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var saved = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal(PricingMethod.ManualFinalPrice, saved.PricingMethod);
        Assert.Equal(585m, saved.ManualFinalPriceUsd);
        Assert.Equal("توافق شخصی", saved.PricingFormulaNote);
    }

    [Fact]
    public async Task Create_ManualPlatts_WithoutBasePrice_SavesAsNeedsReview()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-003",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 150m,
            Currency = "USD",
            PricingMethod = PricingMethod.FormulaPlatts,
            BenchmarkCode = "ULSD",
            PlattsPeriodType = PlattsPeriodType.Manual,
            PlattsManualPriceUsd = null,
            PremiumDiscountUsd = 5m,
            PricingFormulaNote = "Manual Platt's pending"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var saved = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal(PricingMethod.FormulaPlatts, saved.PricingMethod);
        Assert.Equal(PlattsPeriodType.Manual, saved.PlattsPeriodType);
        Assert.Null(saved.PlattsManualPriceUsd);
    }

    [Fact]
    public async Task Create_DailyPlatts_Persists_BasisDate()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var basisDate = new DateTime(2026, 5, 15);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-PLATTS-DAY",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 150m,
            Currency = "USD",
            PricingMethod = PricingMethod.FormulaPlatts,
            PlattsPeriodType = PlattsPeriodType.Daily,
            PlattsBasisDate = basisDate,
            PlattsBasisMonth = new DateTime(2026, 5, 1),
            PremiumDiscountUsd = 5m
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var saved = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal(PricingMethod.FormulaPlatts, saved.PricingMethod);
        Assert.Equal(PlattsPeriodType.Daily, saved.PlattsPeriodType);
        Assert.Equal(DateTimeKind.Utc, saved.PlattsBasisDate?.Kind);
        Assert.Equal(new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc), saved.PlattsBasisDate);
        Assert.Null(saved.PlattsBasisMonth);
    }

    [Fact]
    public async Task Create_MonthlyPlatts_Persists_BasisMonth()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-PLATTS-MONTH",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 150m,
            Currency = "USD",
            PricingMethod = PricingMethod.FormulaPlatts,
            PlattsPeriodType = PlattsPeriodType.Monthly,
            PlattsBasisDate = new DateTime(2026, 5, 15),
            PlattsBasisMonth = new DateTime(2026, 5, 20),
            PremiumDiscountUsd = 5m
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var saved = await db.Contracts.SingleAsync(c => c.ContractNumber == "P-001");
        Assert.Equal(PricingMethod.FormulaPlatts, saved.PricingMethod);
        Assert.Equal(PlattsPeriodType.Monthly, saved.PlattsPeriodType);
        Assert.Null(saved.PlattsBasisDate);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), saved.PlattsBasisMonth);
    }

    [Fact]
    public async Task Create_DailyPlatts_WithoutBasisDate_ReturnsView()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractNumber = "PUR-PLATTS-NO-DATE",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 150m,
            Currency = "USD",
            PricingMethod = PricingMethod.FormulaPlatts,
            PlattsPeriodType = PlattsPeriodType.Daily,
            PremiumDiscountUsd = 5m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.PlattsBasisDate)));
        Assert.False(await db.Contracts.AnyAsync(c => c.ContractNumber == "P-001"));
    }

    [Fact]
    public async Task EditPricing_Get_ReturnsViewWithPricingFields()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Contracts.Single().PricingMethod = PricingMethod.FormulaPlatts;
        db.Contracts.Single().BenchmarkCode = "ULSD";
        db.Contracts.Single().PlattsPeriodType = PlattsPeriodType.Manual;
        db.Contracts.Single().PlattsManualPriceUsd = null;
        db.Contracts.Single().PremiumDiscountUsd = 5m;
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<EditPricingViewModel>(view.Model);
        Assert.Equal(1, model.Id);
        Assert.Equal("PUR-001", model.ContractNumber);
        Assert.Equal(PricingMethod.FormulaPlatts, model.PricingMethod);
        Assert.Equal(UiPricingType.Platts, model.UiPricingType);
        Assert.Equal(PlattsUiMode.ManualDescriptive, model.PlattsUiMode);
        Assert.Equal(PlattsPeriodType.Manual, model.PlattsPeriodType);
        Assert.Null(model.PlattsManualPriceUsd);
        Assert.Equal(5m, model.PremiumDiscountUsd);
    }

    [Fact]
    public async Task EditPricing_Post_SavesPlattsBasePrice_And_Redirects()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Contracts.Single().PricingMethod = PricingMethod.FormulaPlatts;
        db.Contracts.Single().BenchmarkCode = "ULSD";
        db.Contracts.Single().PlattsPeriodType = PlattsPeriodType.Manual;
        db.Contracts.Single().PlattsManualPriceUsd = null;
        db.Contracts.Single().PremiumDiscountUsd = 5m;
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Platts,
            PlattsUiMode = PlattsUiMode.ManualDescriptive,
            FinalPriceUsdPerMt = 620m,
            PremiumDiscountUsd = 5m,
            PricingNote = "قیمت توافقی مه ۲۰۲۶"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var saved = await db.Contracts.SingleAsync(c => c.Id == 1);
        Assert.Null(saved.PlattsManualPriceUsd);
        Assert.Equal(620m, saved.ManualFinalPriceUsd);
        Assert.Equal(5m, saved.PremiumDiscountUsd);
        Assert.Equal("قیمت توافقی مه ۲۰۲۶", saved.PricingFormulaNote);
    }

    [Fact]
    public async Task EditPricing_Post_Finalizes_Only_Pending_Loadings_And_Keeps_Finalized()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.FormulaPlatts;
        contract.BenchmarkCode = "ULSD";
        contract.PlattsPeriodType = PlattsPeriodType.Monthly;
        contract.ManualFinalPriceUsd = null;
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 10,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 23),
                LoadedQuantityMt = 40m,
                LoadingPriceUsd = null
            },
            new LoadingRegister
            {
                Id = 11,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 24),
                LoadedQuantityMt = 35m,
                LoadingPriceUsd = 510m
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Platts,
            PlattsUiMode = PlattsUiMode.MonthlyAverage,
            FinalPriceUsdPerMt = 620m,
            PricingNote = "نرخ نهایی ماهانه"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var prices = await db.LoadingRegisters
            .Where(l => l.ContractId == 1)
            .OrderBy(l => l.Id)
            .Select(l => l.LoadingPriceUsd)
            .ToListAsync();

        // قاعدهٔ #9: فقط بارگیریِ در انتظار قیمت (10) قطعی می‌شود؛ بارگیریِ از پیش قطعی‌شده (11) دست‌نخورده می‌ماند.
        Assert.Equal([620m, 510m], prices);
    }

    [Fact]
    public async Task EditPricing_Post_FixedUsd_Finalizes_Pending_Loading_Price()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = null;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 20,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 12m,
            LoadingPriceUsd = null,
            SettlementCurrencyCode = "USD"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Agreed,
            FinalPriceUsdPerMt = 450m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 20);
        Assert.Equal(450m, loading.LoadingPriceUsd);
        Assert.Equal(RubSettlementRateStatus.NotRequired, loading.RubRateStatus);
        Assert.Null(loading.AmountRubAtRubLock);
    }

    [Fact]
    public async Task EditPricing_Post_RubFixedRate_Locks_Rub_From_Contract_Rate()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = null;
        contract.SettlementCurrencyCode = "RUB";
        contract.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        contract.ContractRubPerUsdRate = 90m;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 21,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = null,
            SettlementCurrencyCode = "RUB",
            RubRateStatus = RubSettlementRateStatus.Pending
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Agreed,
            FinalPriceUsdPerMt = 500m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 21);
        Assert.Equal(500m, loading.LoadingPriceUsd);
        Assert.Equal(RubSettlementRateStatus.Locked, loading.RubRateStatus);
        Assert.Equal(90m, loading.RubPerUsdRate);
        Assert.Equal(5_000m, loading.AmountUsdAtRubLock);
        Assert.Equal(450_000m, loading.AmountRubAtRubLock);
    }

    [Fact]
    public async Task EditPricing_Post_RubPerLoadingRate_Uses_Each_Loadings_Saved_Rate()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = null;
        contract.SettlementCurrencyCode = "RUB";
        contract.RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate;
        contract.ContractRubPerUsdRate = 90m; // نباید روی بارگیری‌ها اعمال شود
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 22,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 3),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = null,
                SettlementCurrencyCode = "RUB",
                RubPerUsdRate = 85m,
                RubRateStatus = RubSettlementRateStatus.Pending
            },
            new LoadingRegister
            {
                Id = 23,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 4),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = null,
                SettlementCurrencyCode = "RUB",
                RubPerUsdRate = null, // بدون نرخ ذخیره‌شده → در انتظار می‌ماند
                RubRateStatus = RubSettlementRateStatus.Pending
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Agreed,
            FinalPriceUsdPerMt = 500m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var withRate = await db.LoadingRegisters.SingleAsync(l => l.Id == 22);
        Assert.Equal(500m, withRate.LoadingPriceUsd);
        Assert.Equal(RubSettlementRateStatus.Locked, withRate.RubRateStatus);
        Assert.Equal(85m, withRate.RubPerUsdRate);
        Assert.Equal(425_000m, withRate.AmountRubAtRubLock); // 10*500*85، نه نرخ قرارداد 90

        var withoutRate = await db.LoadingRegisters.SingleAsync(l => l.Id == 23);
        Assert.Equal(500m, withoutRate.LoadingPriceUsd);
        Assert.Equal(RubSettlementRateStatus.Pending, withoutRate.RubRateStatus);
        Assert.Null(withoutRate.AmountRubAtRubLock);
    }

    [Fact]
    public async Task EditPricing_Post_NoFinalPrice_Leaves_Loading_Pending()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = null;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 24,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 5),
            LoadedQuantityMt = 8m,
            LoadingPriceUsd = null,
            SettlementCurrencyCode = "USD"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Agreed,
            FinalPriceUsdPerMt = null
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 24);
        Assert.Null(loading.LoadingPriceUsd);
    }

    [Fact]
    public async Task EditPricing_Post_Does_Not_Reprice_Or_Relock_Finalized_Loading()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 500m;
        contract.SettlementCurrencyCode = "RUB";
        contract.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        contract.ContractRubPerUsdRate = 90m;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 25,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 6),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "RUB",
            RubPerUsdRate = 90m,
            RubRateStatus = RubSettlementRateStatus.Locked,
            AmountUsdAtRubLock = 5_000m,
            AmountRubAtRubLock = 450_000m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // ویرایش دوباره با نرخ نهایی متفاوت نباید بارگیریِ قطعی‌شده را عوض کند (قاعدهٔ #9 + جلوگیری از پست تکراری).
        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Agreed,
            FinalPriceUsdPerMt = 600m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 25);
        Assert.Equal(500m, loading.LoadingPriceUsd);
        Assert.Equal(450_000m, loading.AmountRubAtRubLock);
    }

    [Fact]
    public async Task EditPricing_Post_ManualFinalPrice_AllowsPendingPrice()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        db.Contracts.Single().PricingMethod = PricingMethod.ManualFinalPrice;
        db.Contracts.Single().ManualFinalPriceUsd = 500m;
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            PricingMethod = PricingMethod.ManualFinalPrice,
            ManualFinalPriceUsd = null
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
    }

    // قیمت هر بارگیری مستقل است (از فایل اکسل خودش می‌آید)، پس «اصلاح قیمت» بارگیریِ
    // قیمت‌دار را دست نمی‌زند و نرخ روبلِ قفل‌شده‌اش هم بازقفل نمی‌شود.
    [Fact]
    public async Task RepricePurchaseLoadings_Keeps_Existing_Loading_Price_And_Rub_Lock()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 600m;
        contract.SettlementCurrencyCode = "RUB";
        contract.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        contract.ContractRubPerUsdRate = 90m;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 30,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 7),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "RUB",
            RubPerUsdRate = 90m,
            RubRateStatus = RubSettlementRateStatus.Locked,
            AmountUsdAtRubLock = 5_000m,
            AmountRubAtRubLock = 450_000m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.RepricePurchaseLoadings(1);

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 30);
        Assert.Equal(500m, loading.LoadingPriceUsd);
        Assert.Equal(RubSettlementRateStatus.Locked, loading.RubRateStatus);
        Assert.Equal(5_000m, loading.AmountUsdAtRubLock);
        Assert.Equal(450_000m, loading.AmountRubAtRubLock); // 10*500*90 — دست‌نخورده
    }

    [Fact]
    public async Task EditPricing_Post_Keeps_Legacy_Ledger_Row_Of_Finalized_Loading_Untouched()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        SeedRubFinalizedLoadingWithLegacyLedger(db, loadingId: 26, ledgerEntryId: 500);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // نرخ قرارداد از ۵۰۰ به ۶۰۰ می‌رود؛ سطر Legacy دفتر قدیمی نباید بی‌صدا عوض شود.
        var result = await controller.EditPricing(1, new EditPricingViewModel
        {
            Id = 1,
            UiPricingType = UiPricingType.Agreed,
            FinalPriceUsdPerMt = 600m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var entry = await db.LedgerEntries.SingleAsync(l => l.Id == 500);
        Assert.Equal(5_000m, entry.AmountUsd);
        Assert.Equal(450_000m, entry.SourceAmount);
    }

    // «اصلاح قیمت» فقط بارگیریِ بدون قیمت را با نرخ فعلی قرارداد تکمیل می‌کند؛
    // بارگیریِ قیمت‌دار و سطر Legacy دفترش دست‌نخورده می‌مانند.
    [Fact]
    public async Task RepricePurchaseLoadings_Only_Fills_Loadings_Without_A_Price()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        SeedRubFinalizedLoadingWithLegacyLedger(db, loadingId: 27, ledgerEntryId: 501);
        db.Contracts.Single().ManualFinalPriceUsd = 600m;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 28,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 7),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = null
        });
        await db.SaveChangesAsync();

        var result = await BuildController(db).RepricePurchaseLoadings(1);

        Assert.IsType<RedirectToActionResult>(result);
        var pricedLoading = await db.LoadingRegisters.SingleAsync(l => l.Id == 27);
        Assert.Equal(500m, pricedLoading.LoadingPriceUsd);
        Assert.Equal(5_000m, pricedLoading.AmountUsdAtRubLock);
        Assert.Equal(450_000m, pricedLoading.AmountRubAtRubLock);

        var pendingLoading = await db.LoadingRegisters.SingleAsync(l => l.Id == 28);
        Assert.Equal(600m, pendingLoading.LoadingPriceUsd);

        var entry = await db.LedgerEntries.SingleAsync(l => l.Id == 501);
        Assert.Equal(5_000m, entry.AmountUsd);
        Assert.Equal(450_000m, entry.SourceAmount);
    }

    // بارگیریِ روبلیِ قطعی‌شده (۱۰ MT × ۵۰۰ USD × ۹۰ RUB) به‌همراه سطر Legacy متناظرش.
    private static void SeedRubFinalizedLoadingWithLegacyLedger(ApplicationDbContext db, int loadingId, int ledgerEntryId)
    {
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 500m;
        contract.SettlementCurrencyCode = "RUB";
        contract.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
        contract.ContractRubPerUsdRate = 90m;

        var loading = new LoadingRegister
        {
            Id = loadingId,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 6),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "RUB",
            RubPerUsdRate = 90m,
            RubRateStatus = RubSettlementRateStatus.Locked,
            AmountUsdAtRubLock = 5_000m,
            AmountRubAtRubLock = 450_000m
        };
        db.LoadingRegisters.Add(loading);

        // PTG-P1-03 — سازنده حالا «درخواستِ ثبت» می‌دهد؛ خودِ سطر از مسیر متمرکز ساخته
        // می‌شود. اینجا فقط دادهٔ آزمون کاشته می‌شود، پس همان سرویس مستقیم صدا زده می‌شود.
        var entry = new LedgerPostingService(db).Post(SupplierLoadingLedger.Create(loading, contract));
        entry.Id = ledgerEntryId;
    }

    [Fact]
    public async Task RepricePurchaseLoadings_Without_Final_Price_Leaves_Loading_Unchanged()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractContext(db);
        var contract = db.Contracts.Single();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = null;
        contract.UnitPriceUsd = null;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 31,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 8),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "USD"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.RepricePurchaseLoadings(1);

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync(l => l.Id == 31);
        Assert.Equal(500m, loading.LoadingPriceUsd);
    }

    // ————————————————— مالک دفتر در قرارداد شراکتی —————————————————

    private static async Task<ContractsController> SeedBookOwnerCaseAsync(ApplicationDbContext db, int? ownerPartnerId)
    {
        SeedContractContext(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "P1", Name = "Partner 1", IsActive = true },
            new Partner { Id = 2, Code = "P2", Name = "Partner 2", IsActive = true },
            new Partner { Id = 3, Code = "P3", Name = "Outsider", IsActive = true });
        await db.SaveChangesAsync();
        (await db.Companies.SingleAsync(c => c.Id == 1)).OwnerPartnerId = ownerPartnerId;
        await db.SaveChangesAsync();
        return BuildController(db);
    }

    private static ContractFormViewModel NewPartnershipModel()
    {
        var model = NewBaseCreateModel();
        model.OwnershipType = ContractOwnershipType.Partnership;
        model.PartnerShares =
        [
            new ContractPartnerShareInput { PartnerId = 1, SharePercent = 60m },
            new ContractPartnerShareInput { PartnerId = 2, SharePercent = 40m }
        ];
        return model;
    }

    [Fact]
    public async Task Create_Partnership_WhoseCompanyHasNoBookOwner_IsSavedWithAShortWarning()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await SeedBookOwnerCaseAsync(db, ownerPartnerId: null);

        var result = await controller.Create(NewPartnershipModel());

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Single(await db.Contracts.Where(c => c.OwnershipType == ContractOwnershipType.Partnership).ToListAsync());
        Assert.Equal(ContractsController.BookOwnerMissingWarning, controller.TempData["Toast.Warning"]);
    }

    [Fact]
    public async Task Create_Partnership_WhoseBookOwnerIsNotAPartner_IsSavedWithAShortWarning()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await SeedBookOwnerCaseAsync(db, ownerPartnerId: 3);

        var result = await controller.Create(NewPartnershipModel());

        Assert.IsType<RedirectToActionResult>(result);
        var warning = Assert.IsType<string>(controller.TempData["Toast.Warning"]);
        Assert.Contains("Outsider", warning);
    }

    [Fact]
    public async Task Create_Partnership_WithTheBookOwnerAmongThePartners_HasNoWarning()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await SeedBookOwnerCaseAsync(db, ownerPartnerId: 1);

        var result = await controller.Create(NewPartnershipModel());

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null(controller.TempData["Toast.Warning"]);
    }

    [Fact]
    public async Task Create_Personal_NeverWarnsAboutABookOwner()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await SeedBookOwnerCaseAsync(db, ownerPartnerId: null);

        var result = await controller.Create(NewBaseCreateModel());

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null(controller.TempData["Toast.Warning"]);
    }

    [Fact]
    public async Task Edit_Partnership_RemovingTheBookOwnerFromThePartners_IsSavedWithAShortWarning()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await SeedBookOwnerCaseAsync(db, ownerPartnerId: 1);
        Assert.IsType<RedirectToActionResult>(await controller.Create(NewPartnershipModel()));
        var created = await db.Contracts.SingleAsync(c => c.OwnershipType == ContractOwnershipType.Partnership);

        var editor = BuildController(db);
        var model = NewPartnershipModel();
        model.Id = created.Id;
        model.ContractNumber = created.ContractNumber;
        model.Version = created.Version;
        model.PartnerShares =
        [
            new ContractPartnerShareInput { PartnerId = 2, SharePercent = 60m },
            new ContractPartnerShareInput { PartnerId = 3, SharePercent = 40m }
        ];

        var result = await editor.Edit(created.Id, model);

        Assert.IsType<RedirectToActionResult>(result);
        var warning = Assert.IsType<string>(editor.TempData["Toast.Warning"]);
        Assert.Contains("Partner 1", warning);
    }

    [Fact]
    public async Task ContractForm_KnowsEachCompanysBookOwner_ForThePartnerBadge()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await SeedBookOwnerCaseAsync(db, ownerPartnerId: 2);

        Assert.IsType<ViewResult>(await controller.Create());

        var owners = Assert.IsAssignableFrom<IReadOnlyDictionary<int, int>>(controller.ViewBag.CompanyBookOwners);
        Assert.Equal(2, owners[1]);
    }

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static ContractsController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static ContractFormViewModel NewBaseCreateModel() => new()
    {
        ContractName = "قرارداد آزمایشی",
        ContractType = ContractType.Purchase,
        Status = ContractStatus.Draft,
        CompanyId = 1,
        ProductId = 1,
        UnitId = 1,
        SupplierId = 1,
        OwnershipType = ContractOwnershipType.Personal,
        ContractDate = new DateTime(2026, 4, 23),
        PricingMethod = PricingMethod.Fixed,
        QuantityMt = 100m,
        Currency = "USD",
        UnitPriceInCurrency = 450m,
        RubRatePolicy = RubSettlementRatePolicy.NotApplicable
    };

    private static void SeedContractContext(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, UnitOfMeasure = "MT", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractName = "قرارداد خرید آزمایشی",
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            Currency = "USD",
            UnitPriceInCurrency = 450m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 450m
        });
        db.SaveChanges();
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
