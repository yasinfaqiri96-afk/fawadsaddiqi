using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
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
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class LoadingControllerTests
{
    [Fact]
    public async Task Export_Includes_Driver_Name_And_Phone()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db);
        var loading = await db.LoadingRegisters.SingleAsync();
        loading.DriverName = "Ahmad Driver";
        loading.DriverPhone = "0700000000";
        await db.SaveChangesAsync();

        var controller = NewLoadingController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var export = Assert.IsType<TabularExportResult>(await controller.Export("excel"));
        Assert.Contains(export.Document.Columns, column => column.TitleEn == "Driver name");
        Assert.Contains(export.Document.Columns, column => column.TitleEn == "Driver phone");
        var row = Assert.Single(export.Document.Rows);
        Assert.Contains(row.Cells, cell => Equals(cell.Value, "Ahmad Driver"));
        Assert.Contains(row.Cells, cell => Equals(cell.Value, "0700000000"));
    }

    [Fact]
    public async Task Index_Defaults_To_Ten_Ignores_Legacy_Cookie_And_Honors_Explicit_Page_Size()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        var defaultController = NewLoadingController(db);
        defaultController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var defaultResult = Assert.IsType<ViewResult>(await defaultController.Index());
        Assert.Equal(10, defaultController.ViewData["PageSize"]);
        Assert.Equal(10, defaultController.ViewData["DefaultPageSize"]);
        Assert.IsType<LoadingIndexViewModel>(defaultResult.Model);

        var savedController = NewLoadingController(db);
        var savedContext = new DefaultHttpContext();
        savedContext.Request.Headers.Cookie = "ptg-loading-page-size=50";
        savedController.ControllerContext = new ControllerContext { HttpContext = savedContext };
        await savedController.Index();
        Assert.Equal(10, savedController.ViewData["PageSize"]);

        var explicitController = NewLoadingController(db);
        explicitController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        await explicitController.Index(perPage: 50);
        Assert.Equal(50, explicitController.ViewData["PageSize"]);
    }

    // امپورت دیگر View برنمی‌گرداند؛ سطرهای آمادهٔ فرم را به‌صورت JSON می‌دهد.
    private static LoadingCreateViewModel ImportedModel(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<LoadingImportResponse>(json.Value);
        Assert.True(payload.Success, string.Join(" | ", payload.GlobalErrors));

        return new LoadingCreateViewModel
        {
            TransportType = (LoadingTransportType)payload.TransportType,
            OriginLocationId = payload.OriginLocationId,
            ProductId = payload.ProductId ?? 0,
            LoadingDate = DateTime.TryParse(
                payload.LoadingDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var loadingDate)
                ? loadingDate
                : default,
            Rows = payload.Rows.Select(row => row.Row).ToList()
        };
    }

    [Fact]
    public void Loading_Create_View_Renders_Shared_Ak_Table_Header()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml"));

        var viewContent = File.ReadAllText(viewPath);

        Assert.Contains("class=\"ak-table", viewContent);
        Assert.Contains("data-loading-table-head", viewContent);
        Assert.Contains("data-loading-column-reference", viewContent);
        Assert.Contains("data-loading-column-transport", viewContent);
    }

    [Fact]
    public void Loading_Create_View_Shows_Save_Progress_Only_After_Client_Validation()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml"));

        var viewContent = File.ReadAllText(viewPath);
        var formSubmitStart = viewContent.IndexOf(
            "loadingForm?.addEventListener('submit'",
            StringComparison.Ordinal);
        var documentSubmitStart = viewContent.IndexOf(
            "document.addEventListener('submit'",
            formSubmitStart,
            StringComparison.Ordinal);
        var formSubmitScript = viewContent[formSubmitStart..documentSubmitStart];

        Assert.DoesNotContain("showOperationStatus(", formSubmitScript);
        Assert.Contains("if (event.defaultPrevented)", viewContent[documentSubmitStart..]);
        Assert.Contains("invalid-form.validate", viewContent);
        Assert.Contains("resetLoadingSubmissionUi();", viewContent);
    }

    [Fact]
    public void Loading_Create_View_Autofills_Contract_Price_Only_When_Field_Is_Autofillable()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml"));
        var rowViewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "_LoadingRowEditor.cshtml"));

        var viewContent = File.ReadAllText(viewPath);
        var rowViewContent = File.ReadAllText(rowViewPath);

        Assert.DoesNotContain("هر گروه نرخ‌دار را در یک سطر جدا ثبت کنید", viewContent);
        Assert.DoesNotContain("۵ واگون با نرخ ۲۰۰ و ۵ واگون با نرخ ۳۰۰", viewContent);
        Assert.Contains("canAutofill(priceInput)", viewContent);
        Assert.Contains("priceInput.value = formatInputNumber(suggestedLoadingPrice)", viewContent);
        Assert.Contains("setAutofilled(priceInput, true)", viewContent);
        Assert.DoesNotContain("data-row-premium-display", rowViewContent);
    }

    [Fact]
    public void Loading_Row_Editor_Uses_Shared_Ak_Table_Row()
    {
        var partialPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "_LoadingRowEditor.cshtml"));

        var viewContent = File.ReadAllText(partialPath);

        Assert.Contains("<tr data-loading-row", viewContent);
        Assert.Contains("data-loading-date-picker", viewContent);
        Assert.DoesNotContain("loading-row-topbar", viewContent);
        Assert.DoesNotContain("loading-row-grid", viewContent);
        Assert.DoesNotContain("loading-row-track", viewContent);
    }

    [Fact]
    public void Loading_Row_Editor_Uses_Excel_Style_Loading_Sheet_Fields()
    {
        var partialPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "_LoadingRowEditor.cshtml"));

        var viewContent = File.ReadAllText(partialPath);

        Assert.Contains(".BillOfLadingNumber", viewContent);
        Assert.Contains(".LoadedQuantityMt", viewContent);
        Assert.Contains(".PlattsUsd", viewContent);
        Assert.Contains(".LoadingPriceUsd", viewContent);
        Assert.Contains(".ConsigneeName", viewContent);
        Assert.Contains(".LogisticsCompanyName", viewContent);
        Assert.Contains(".LogisticsServiceProviderId", viewContent);
        Assert.Contains(".FreightRateUsdPerMt", viewContent);
        Assert.Contains(".DestinationName", viewContent);
        Assert.Contains(".TransportExpenseUsd", viewContent);
        Assert.Contains(".WarehouseExpenseUsd", viewContent);
        Assert.Contains(".OtherExpenseUsd", viewContent);
        Assert.Contains(".ChargeableQuantityMt", viewContent);
        Assert.Contains(".RailwayRateUsd", viewContent);
        Assert.Contains(".RailwayExpenseUsd", viewContent);
        Assert.Contains(".RouteDescription", viewContent);
        Assert.Contains("data-row-logistics-provider", viewContent);
        Assert.Contains("data-row-freight-rate", viewContent);
        Assert.Contains("data-row-freight-expense", viewContent);
        Assert.Contains("data-row-warehouse-expense", viewContent);
        Assert.Contains("data-row-railway-expense", viewContent);
        Assert.DoesNotContain("data-loading-expense-panel", viewContent);
    }

    [Fact]
    public void Loading_Create_Wagon_Editor_Exposes_Railway_Fields_And_Summary()
    {
        var createViewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml"));
        var rowViewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "_LoadingRowEditor.cshtml"));

        var createView = File.ReadAllText(createViewPath);
        var rowView = File.ReadAllText(rowViewPath);
        var componentsCssPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "wwwroot", "css", "ptg", "50-ak-components.css"));
        var componentsCss = File.ReadAllText(componentsCssPath);

        Assert.Contains("data-loading-wagon-freight-column", createView);
        Assert.Contains("data-loading-wagon-summary", createView);
        Assert.Contains("data-loading-wagon-count", createView);
        Assert.Contains("data-loading-wagon-chargeable", createView);
        Assert.Contains("data-loading-wagon-expense", createView);
        Assert.Contains("enteredChargeableQuantity", createView);
        Assert.Contains("const total = effectiveQuantity * rate", createView);
        Assert.Contains("name=\"@($\"{prefix}.ChargeableQuantityMt\")\"", rowView);
        Assert.Contains("name=\"@($\"{prefix}.LogisticsCompanyName\")\"", rowView);
        Assert.Contains("aria-label=\"شرکت ترانسپورتی\"", rowView);
        Assert.Contains("data-row-railway-expense-text", rowView);
        Assert.Contains("min-width: 2660px", componentsCss);
        Assert.Contains("grid-template-columns: minmax(126px, .85fr) minmax(164px, 1.15fr)", componentsCss);
        Assert.Contains("th:nth-child(21)", componentsCss);
        Assert.Contains("white-space: nowrap", componentsCss);
    }

    [Fact]
    public void Loading_EditExpenses_Actions_Require_ManageData_Policy()
    {
        var actions = typeof(LoadingController)
            .GetMethods()
            .Where(method => method.Name == nameof(LoadingController.EditExpenses)
                             && method.DeclaringType == typeof(LoadingController))
            .ToList();

        Assert.Equal(2, actions.Count);
        Assert.All(actions, action =>
        {
            var policies = action
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .Select(attribute => attribute.Policy);

            Assert.Contains(AuthPolicies.ManageData, policies);
        });
    }

    [Fact]
    public void Loading_Create_View_Uses_Shared_Ak_Form_Contract()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml"));

        var viewContent = File.ReadAllText(viewPath);

        Assert.Contains("class=\"ak-form-page", viewContent);
        Assert.Contains("class=\"ak-form\"", viewContent);
        Assert.Contains("_AkPageHeader", viewContent);
        Assert.Contains("_AkSectionHead", viewContent);
        Assert.Contains("class=\"ak-form-grid", viewContent);
        Assert.Contains("class=\"ak-table", viewContent);
        Assert.Contains("data-loading-create-form", viewContent);
        Assert.Contains("data-loading-contract-toggle", viewContent);
        Assert.Contains("data-loading-contract-menu", viewContent);
        Assert.Contains("data-loading-product-summary", viewContent);
        Assert.Contains("data-contract-product-id", viewContent);
        Assert.DoesNotContain("<select asp-for=\"ProductId\"", viewContent);
        Assert.Contains("id=\"loadingImportedRowsJson\"", viewContent);
        Assert.Contains("id=\"loadingImportedRowsData\"", viewContent);
        Assert.Contains("data-loading-operation-status", viewContent);
        Assert.Contains("window.loadingVirtualRowsBeforeSubmit", viewContent);
        Assert.Contains("host.replaceChildren(fragment)", viewContent);
        Assert.DoesNotContain("loading-reference-card", viewContent);
        Assert.DoesNotContain("loading-one-page-form", viewContent);
        Assert.DoesNotContain("_OperationsReferenceHeader", viewContent);
        Assert.DoesNotContain("loading-grid-total", viewContent);
        Assert.DoesNotContain("ds-form-shell", viewContent);
        Assert.DoesNotContain("_BoltzWizardStepper", viewContent);
        Assert.DoesNotContain("data-boltz-wizard", viewContent);
    }

    [Fact]
    public void Loading_Row_Editor_Uses_Dependent_Logistics_Type_And_Item_Fields()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "_LoadingRowEditor.cshtml"));

        var viewContent = File.ReadAllText(viewPath);

        Assert.Contains("data-row-logistics-editor", viewContent);
        Assert.Contains("data-row-logistics-type", viewContent);
        Assert.Contains("data-row-logistics-display", viewContent);
        Assert.Contains("data-loading-logistics-pair", viewContent);
        Assert.Contains("data-loading-logistics-item-field", viewContent);
        Assert.Contains("value=\"owned\"", viewContent);
        Assert.Contains("value=\"free\"", viewContent);
        Assert.Contains("data-row-logistics-provider", viewContent);
        Assert.Contains("data-row-operational-asset", viewContent);
        Assert.Contains("LogisticsServiceProviderId", viewContent);
        Assert.Contains("OperationalAssetId", viewContent);
        Assert.DoesNotContain("<datalist", viewContent);
        Assert.DoesNotContain("data-row-logistics-mode", viewContent);
        Assert.DoesNotContain("data-row-logistics-panel", viewContent);
    }

    [Fact]
    public void Loading_Create_View_Uses_Live_Summary_Without_Old_Copy_Banners()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml"));

        var viewContent = File.ReadAllText(viewPath);

        // خلاصه از کامپوننت مشترک کارت آماری ساخته می‌شود و مقدارهایش زنده به‌روز می‌شود.
        Assert.Contains("ak-stat-grid", viewContent);
        Assert.Contains("<vc:stat-card", viewContent);
        Assert.Contains("lic-stat-quantity", viewContent);
        Assert.Contains("lic-stat-value", viewContent);
        Assert.Contains("lic-stat-expense", viewContent);
        Assert.Contains("updateSummaryTotals", viewContent);
        Assert.DoesNotContain("loading-actions-copy", viewContent);
    }

    [Fact]
    public void Loading_Create_View_Uses_Readable_Persian_Actions_Without_Mojibake()
    {
        var createViewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml"));
        var rowViewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "_LoadingRowEditor.cshtml"));

        var createView = File.ReadAllText(createViewPath);
        var rowView = File.ReadAllText(rowViewPath);

        Assert.Contains("امپورت از اکسل", createView);
        Assert.Contains("افزودن سطر", createView);
        Assert.Contains("حذف سطر", rowView);
        Assert.DoesNotContain("Ø§Ù¾Ù„ÙˆØ¯", createView);
        Assert.DoesNotContain("Ø­Ø°Ù", rowView);
    }

    [Fact]
    public void Loading_Row_Models_Expose_Platts_Field()
    {
        var rowProperty = typeof(LoadingCreateRowViewModel).GetProperty("PlattsUsd");
        var entityProperty = typeof(LoadingRegister).GetProperty("PlattsUsd");
        var listProperty = typeof(LoadingListItemViewModel).GetProperty("PlattsUsd");
        var detailsProperty = typeof(LoadingDetailsViewModel).GetProperty("PlattsUsd");

        Assert.NotNull(rowProperty);
        Assert.Equal(typeof(decimal?), rowProperty!.PropertyType);
        Assert.NotNull(entityProperty);
        Assert.Equal(typeof(decimal?), entityProperty!.PropertyType);
        Assert.NotNull(listProperty);
        Assert.Equal(typeof(decimal?), listProperty!.PropertyType);
        Assert.NotNull(detailsProperty);
        Assert.Equal(typeof(decimal?), detailsProperty!.PropertyType);
    }

    [Fact]
    public void LoadingCreateViewModel_Allows_Selected_Transport_Type()
    {
        var model = new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedQuantityMt = 1m
        };

        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            model,
            new ValidationContext(model),
            validationResults,
            validateAllProperties: true);

        Assert.True(isValid);
        Assert.DoesNotContain(validationResults, result => result.ErrorMessage == "انتخاب نوع حمل و نقل الزامی است.");
    }

    [Fact]
    public void LoadingCreateViewModel_Does_Not_Require_Legacy_TopLevel_Quantity_When_Row_Quantity_Exists()
    {
        var model = new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 4, 29),
                    WagonNumber = "WGN-001",
                    LoadedQuantityMt = 12.5m
                }
            ]
        };

        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            model,
            new ValidationContext(model),
            validationResults,
            validateAllProperties: true);

        Assert.True(isValid);
        Assert.DoesNotContain(validationResults, result => result.MemberNames.Contains(nameof(LoadingCreateViewModel.LoadedQuantityMt)));
    }

    [Fact]
    public async Task Create_Get_Preselects_Contract_And_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance);

        var result = await controller.Create(contractId: 1, returnUrl: "/Contracts/Details/1?tab=loading");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LoadingCreateViewModel>(view.Model);
        Assert.Equal(1, model.ContractId);
        Assert.Equal("/Contracts/Details/1?tab=loading", model.ReturnUrl);
    }

    [Fact]
    public async Task SuggestedPricing_Returns_Fixed_Rate_And_Platts_Reference()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "FIX-1",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 23),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 450m,
                SettlementCurrencyCode = "RUB",
                RubRatePolicy = RubSettlementRatePolicy.FixedContractRate,
                ContractRubPerUsdRate = 80m,
                ContractRubRateDate = new DateTime(2026, 4, 23),
                ContractRubRateSource = "Contract"
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PLATTS-1",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 23),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.FormulaPlatts,
                BenchmarkCode = "LPG",
                PlattsPeriodType = PlattsPeriodType.Manual,
                PlattsManualPriceUsd = 638.06m,
                PremiumDiscountUsd = -170m
            });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance);

        var fixedJson = Assert.IsType<JsonResult>(await controller.SuggestedPricing(1));
        Assert.False((bool)GetJsonProperty(fixedJson.Value!, "isFormulaPlatts")!);
        Assert.Equal(450m, GetJsonProperty(fixedJson.Value!, "finalUnitPrice"));
        Assert.Equal("RUB", GetJsonProperty(fixedJson.Value!, "settlementCurrencyCode"));
        Assert.Equal("FixedContractRate", GetJsonProperty(fixedJson.Value!, "rubRatePolicy"));
        Assert.Equal(80m, GetJsonProperty(fixedJson.Value!, "rubPerUsdRate"));
        Assert.Equal("Contract", GetJsonProperty(fixedJson.Value!, "rubRateSource"));

        var plattsJson = Assert.IsType<JsonResult>(await controller.SuggestedPricing(2));
        Assert.True((bool)GetJsonProperty(plattsJson.Value!, "isFormulaPlatts")!);
        Assert.Equal(638.06m, GetJsonProperty(plattsJson.Value!, "plattsReferencePrice"));
        Assert.Equal(-170m, GetJsonProperty(plattsJson.Value!, "premiumDiscountUsd"));
    }

    [Fact]
    public async Task Create_Post_Redirects_To_ReturnUrl_When_Provided()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "BNK", Kind = "Origin" });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 80m,
            BillOfLadingNumber = "RWB-001",
            WagonNumber = "WGN-44",
            ConsigneeName = "Terminal Ilinka",
            DestinationName = "Trusovo",
            ReturnUrl = "/Contracts/Details/1?tab=loading"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Contracts/Details/1?tab=loading", redirect.Url);
    }

    [Fact]
    public async Task Create_Post_Ignores_External_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "BNK", Kind = "Origin" });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 80m,
            BillOfLadingNumber = "RWB-UNSAFE",
            WagonNumber = "WGN-45",
            ConsigneeName = "Terminal Ilinka",
            DestinationName = "Trusovo",
            ReturnUrl = "https://evil.com"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
    }

    [Fact]
    public async Task Create_Post_Derives_Product_From_Selected_Contract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.AddRange(
            new Product { Id = 1, Code = "GO", Name = "Gas Oil" },
            new Product { Id = 2, Code = "MOG", Name = "Gasoline" });
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
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 2,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 50m,
            BillOfLadingNumber = "RWB-001",
            WagonNumber = "WGN-001"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.True(controller.ModelState.IsValid);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(1, loading.ProductId);
    }

    [Fact]
    public async Task Create_Post_Persists_Loading_And_Audit()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
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
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "BNK", Kind = "Origin" });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 80m,
            BillOfLadingNumber = "RWB-001",
            WagonNumber = "WGN-44",
            ConsigneeName = "Terminal Ilinka",
            DestinationName = "Trusovo",
            Notes = "April loading"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(1, loading.ContractId);
        Assert.Equal(1, loading.ProductId);
        Assert.Equal(1, loading.OriginLocationId);
        Assert.Equal(LoadingTransportType.Wagon, loading.TransportType);
        Assert.Null(loading.TruckId);
        Assert.Equal(80m, loading.LoadedQuantityMt);
        Assert.Equal("RWB-001", loading.BillOfLadingNumber);
        Assert.Equal("WGN-44", loading.WagonNumber);
        Assert.Equal("Terminal Ilinka", loading.ConsigneeName);
        Assert.Equal("Trusovo", loading.DestinationName);

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(nameof(LoadingRegister), audit.EntityName);
        Assert.Equal("Insert", audit.Action);
        Assert.Contains("WagonNumber", audit.Diff);
        Assert.Contains("ConsigneeName", audit.Diff);
        Assert.Contains("DestinationName", audit.Diff);
    }

    [Fact]
    public async Task Create_Post_Blocks_When_Loading_Exceeds_Contract_Remaining_Quantity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 4, 22),
            LoadedQuantityMt = 75m,
            WagonNumber = "WGN-001"
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 4, 23),
                    WagonNumber = "WGN-002",
                    LoadedQuantityMt = 30m
                }
            ]
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<LoadingCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[nameof(LoadingCreateViewModel.Rows)]!.Errors, e => e.ErrorMessage.Contains("باقیمانده قرارداد"));
        Assert.Equal(1, await db.LoadingRegisters.CountAsync());
    }

    [Fact]
    public async Task Create_Post_WithMultipleSelectedContracts_Persists_RowContractIds()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-A",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 23),
                QuantityMt = 100m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-B",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 24),
                QuantityMt = 100m
            });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            SelectedContractIds = [1, 2],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    ContractId = 1,
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-001",
                    BillOfLadingNumber = "RWB-001",
                    LoadedQuantityMt = 30m
                },
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_1",
                    ContractId = 2,
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-001",
                    BillOfLadingNumber = "RWB-001",
                    LoadedQuantityMt = 40m
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var loadings = await db.LoadingRegisters.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, loadings.Count);
        Assert.Equal(1, loadings[0].ContractId);
        Assert.Equal(2, loadings[1].ContractId);
        Assert.All(loadings, l => Assert.Equal("WGN-001", l.WagonNumber));
        Assert.All(loadings, l => Assert.Equal("RWB-001", l.BillOfLadingNumber));
    }

    [Fact]
    public async Task Create_Post_WithLockedContract_Ignores_OtherSelectedContracts()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-A",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 23),
                QuantityMt = 100m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-B",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 24),
                QuantityMt = 100m
            });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            LockContract = true,
            SelectedContractIds = [1, 2],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    ContractId = 1,
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-001",
                    BillOfLadingNumber = "RWB-001",
                    LoadedQuantityMt = 30m
                },
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_1",
                    ContractId = 2,
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-002",
                    BillOfLadingNumber = "RWB-002",
                    LoadedQuantityMt = 40m
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var loadings = await db.LoadingRegisters.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, loadings.Count);
        Assert.All(loadings, l => Assert.Equal(1, l.ContractId));
    }

    [Fact]
    public async Task Create_Post_WithMultipleSelectedContracts_Rejects_DifferentProducts()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.AddRange(
            new Product { Id = 1, Code = "LPG", Name = "LPG" },
            new Product { Id = 2, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-A",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 23),
                QuantityMt = 100m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-B",
                ContractType = ContractType.Purchase,
                ProductId = 2,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 24),
                QuantityMt = 100m
            });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            SelectedContractIds = [1, 2],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    ContractId = 1,
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-001",
                    LoadedQuantityMt = 30m
                }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(0, await db.LoadingRegisters.CountAsync());
    }

    [Fact]
    public async Task Create_Post_WithMultipleSelectedContracts_Rejects_MissingRowContract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-A",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 23),
                QuantityMt = 100m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-B",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 24),
                QuantityMt = 100m
            });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            SelectedContractIds = [1, 2],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-001",
                    LoadedQuantityMt = 30m
                }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.NotNull(controller.ModelState["Rows[row_0].ContractId"]);
        Assert.Equal(0, await db.LoadingRegisters.CountAsync());
    }

    [Fact]
    public async Task Create_Post_WithMultipleSelectedContracts_Checks_RemainingQuantity_PerContract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-A",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 23),
                QuantityMt = 50m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-B",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                CompanyId = 1,
                ContractDate = new DateTime(2026, 4, 24),
                QuantityMt = 100m
            });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 4, 24),
            WagonNumber = "OLD-001",
            LoadedQuantityMt = 30m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            SelectedContractIds = [1, 2],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    ContractId = 1,
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-001",
                    LoadedQuantityMt = 25m
                },
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_1",
                    ContractId = 2,
                    LoadingDate = new DateTime(2026, 4, 25),
                    WagonNumber = "WGN-002",
                    LoadedQuantityMt = 80m
                }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[nameof(LoadingCreateViewModel.Rows)]!.Errors, e => e.ErrorMessage.Contains("PUR-A"));
        Assert.Equal(1, await db.LoadingRegisters.CountAsync());
    }

    // قرارداد نرخ نهایی قطعی (ManualFinalPriceUsd) دارد و کاربر قیمت سطر را وارد نکرده است:
    // همان نرخ قرارداد روی بارگیری ذخیره می‌شود تا بارگیری «در انتظار نرخ» نماند. حالتِ
    // «قرارداد بدون نرخ قطعی» را Create_Post_Allows_Formula_Contract_Blank_Row_Pricing_When_Platts_Is_Unavailable می‌پوشاند.
    [Fact]
    public async Task Create_Post_Completes_Row_Price_From_The_Contract_Final_Rate()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-LPG-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 1000m,
            PricingMethod = PricingMethod.FormulaPlatts,
            BenchmarkCode = "LPG",
            PlattsPeriodType = PlattsPeriodType.Manual,
            PlattsManualPriceUsd = 638.06m,
            PremiumDiscountUsd = -170m,
            ManualFinalPriceUsd = 468.06m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 4, 23),
                    WagonNumber = "67-50825769",
                    LoadedQuantityMt = 35.88m,
                    ConsigneeName = "Favad Coltd",
                    LogisticsCompanyName = "Oxus",
                    DestinationName = "Akina"
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.PlattsUsd);
        Assert.Equal(468.06m, loading.LoadingPriceUsd);
    }

    [Fact]
    public async Task Create_Post_Allows_Formula_Contract_Blank_Row_Pricing_When_Platts_Is_Unavailable()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-LPG-2",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.FormulaPlatts,
            BenchmarkCode = "LPG",
            PlattsPeriodType = PlattsPeriodType.Manual
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 4, 23),
                    WagonNumber = "67-50825769",
                    LoadedQuantityMt = 35.88m
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.True(controller.ModelState.IsValid);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.PlattsUsd);
        Assert.Null(loading.LoadingPriceUsd);
    }

    [Fact]
    public async Task ImportWorkbook_PopulatesRows_FromExcelSheet_AndPreservesWorkbookPricing()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-LPG-3",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 1000m,
            PricingMethod = PricingMethod.FormulaPlatts,
            BenchmarkCode = "LPG",
            PlattsPeriodType = PlattsPeriodType.Manual,
            PlattsManualPriceUsd = 638.06m,
            PremiumDiscountUsd = -170m,
            ManualFinalPriceUsd = 468.06m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildLoadingWorkbookBytes())
        });

        var model = ImportedModel(result);

        Assert.Equal(LoadingTransportType.Wagon, model.TransportType);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(new DateTime(2022, 11, 25), model.Rows[0].LoadingDate);
        Assert.Equal("74207656", model.Rows[0].BillOfLadingNumber);
        Assert.Equal("67-50825769", model.Rows[0].WagonNumber);
        Assert.Equal(35.88m, model.Rows[0].LoadedQuantityMt);
        Assert.Null(model.Rows[0].PlattsUsd);
        Assert.Equal(468.06m, model.Rows[0].LoadingPriceUsd);
        Assert.Equal("Favad Coltd", model.Rows[0].ConsigneeName);
        Assert.Equal("Oxus", model.Rows[0].LogisticsCompanyName);
        Assert.Equal("Akina", model.Rows[0].DestinationName);
        Assert.Equal(40m, model.Rows[0].ChargeableQuantityMt);
        Assert.Equal(3m, model.Rows[0].RailwayRateUsd);
        Assert.Equal(3m, model.Rows[0].FreightRateUsdPerMt);
        Assert.Equal(120m, model.Rows[0].RailwayExpenseUsd);
        Assert.Equal(638.06m, model.Rows[1].PlattsUsd);
        Assert.Equal(468.06m, model.Rows[1].LoadingPriceUsd);
        // فایل بدون ستون روبلی است؛ رفتار قبلی نباید بشکند و ارقام روبلی باید null بمانند.
        Assert.Null(model.Rows[0].SettlementUnitPriceRub);
        Assert.Null(model.Rows[0].SettlementValueRub);
        Assert.Null(model.Rows[1].SettlementUnitPriceRub);
        Assert.Null(model.Rows[1].SettlementValueRub);
    }

    // سطر ناقص حذف نمی‌شود؛ وارد فرم می‌شود و خطایش با شمارهٔ واقعی سطر اکسل، نام شیت و
    // نام ستون واقعی برمی‌گردد تا زیر همان فیلد نشان داده شود.
    [Fact]
    public async Task ImportWorkbook_ReportsFieldIssue_WithRealExcelRowAndColumn()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-LPG-ISSUE",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 1000m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildWagonWorkbookWithMissingQuantityBytes())
        });

        var payload = Assert.IsType<LoadingImportResponse>(Assert.IsType<JsonResult>(result).Value);

        Assert.True(payload.Success);
        Assert.Equal(2, payload.TotalRows);
        Assert.Equal(1, payload.ImportedRows);
        Assert.Equal(1, payload.InvalidRows);
        Assert.Empty(payload.Rows[0].Issues);
        Assert.Equal(5, payload.Rows[0].ExcelRowNumber);

        var invalid = payload.Rows[1];
        Assert.Equal(6, invalid.ExcelRowNumber);
        Assert.Equal("January", invalid.SheetName);
        var issue = Assert.Single(invalid.Issues);
        Assert.Equal("loadedQuantityMt", issue.Field);
        Assert.Equal("Loaded quantity (MT)", issue.Column);
        Assert.Contains("مقدار بارگیری", issue.Message);
    }

    // فایل بدون ستون‌های ضروری هیچ سطری وارد نمی‌کند و پیام باید ستون و شیت را نام ببرد.
    [Fact]
    public async Task ImportWorkbook_Rejects_Workbook_With_A_Clear_Message()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ImportWorkbookFile = BuildWorkbookFile(BuildUnknownStructureWorkbookBytes())
        });

        var payload = Assert.IsType<LoadingImportResponse>(Assert.IsType<JsonResult>(result).Value);

        Assert.False(payload.Success);
        Assert.Empty(payload.Rows);
        Assert.NotEmpty(payload.GlobalErrors);
        Assert.Contains("قابل امپورت نیست", payload.GlobalErrors[0]);
    }

    [Fact]
    public async Task ImportWorkbook_ReadsRubPriceAndValue_FromSolvexStyleWorkbook()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-SOLVEX-3",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 3, 1),
            QuantityMt = 10000m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildSolvexRubWorkbookBytes())
        });

        var model = ImportedModel(result);

        Assert.Equal(LoadingTransportType.Wagon, model.TransportType);
        Assert.Single(model.Rows);
        Assert.Equal(9782.4m, model.Rows[0].LoadedQuantityMt);
        Assert.Equal(931.39m, model.Rows[0].LoadingPriceUsd);
        Assert.Equal(41464.11m, model.Rows[0].SettlementUnitPriceRub);
        Assert.Equal(405618509.664m, model.Rows[0].SettlementValueRub);
    }

    [Fact]
    public async Task ImportWorkbook_UsesConsistentSheetUsdPrice_WhenFirstRubRowFormulaIsBroken()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-SOLVEX-RUB",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 3, 1),
            QuantityMt = 10000m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildSolvexRubWorkbookBytes(includeBrokenLeadingUsdPrice: true))
        });

        var payload = Assert.IsType<LoadingImportResponse>(Assert.IsType<JsonResult>(result).Value);
        var firstRow = payload.Rows[0].Row;

        Assert.True(payload.Success);
        Assert.Equal(2, payload.TotalRows);
        Assert.Empty(payload.Rows[0].Issues);
        Assert.Equal(931.39m, firstRow.LoadingPriceUsd);
        Assert.Equal(41464.11m, firstRow.SettlementUnitPriceRub);
        Assert.Equal(44.518526m, firstRow.RubPerUsdRate);
        Assert.Equal(RubSettlementRateStatus.Locked, firstRow.RubRateStatus);
        Assert.Equal("Loading file", firstRow.RubRateSource);
    }

    [Fact]
    public async Task ImportWorkbook_ComputesTotalRub_WhenOnlyRubRatePresent()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-SOLVEX-4",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 3, 1),
            QuantityMt = 10000m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildSolvexRubRateOnlyWorkbookBytes())
        });

        var model = ImportedModel(result);

        Assert.Single(model.Rows);
        Assert.Equal(41464.11m, model.Rows[0].SettlementUnitPriceRub);
        Assert.Equal(
            decimal.Round(9782.4m * 41464.11m, 4, MidpointRounding.AwayFromZero),
            model.Rows[0].SettlementValueRub);
    }

    [Fact]
    public async Task ImportWorkbook_AcceptsGeroyRossiStyleWagonHeaders()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "PETROL", Name = "Petrol" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-GEROY-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 3, 1),
            QuantityMt = 1000m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildGeroyRossiStyleWorkbookBytes())
        });

        var model = ImportedModel(result);

        Assert.True(controller.ModelState.IsValid);
        Assert.Equal(LoadingTransportType.Wagon, model.TransportType);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(new DateTime(2026, 12, 3), model.Rows[0].LoadingDate);
        Assert.Equal("259801", model.Rows[0].BillOfLadingNumber);
        Assert.Equal("5460455", model.Rows[0].WagonNumber);
        Assert.Equal(53.4m, model.Rows[0].LoadedQuantityMt);
        Assert.Equal("Fawad coltd", model.Rows[0].ConsigneeName);
        Assert.Equal("AmirAbad - Rozanak", model.Rows[0].DestinationName);
    }

    [Fact]
    public async Task ImportWorkbook_PopulatesTruckRows_FromMultiSheetWorkbook_AndUsesFreightPayable()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-GAS-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 2, 1),
            QuantityMt = 5000m,
            PricingMethod = PricingMethod.FormulaPlatts,
            BenchmarkCode = "MOGAS",
            PlattsPeriodType = PlattsPeriodType.Manual,
            PlattsManualPriceUsd = 638.06m,
            PremiumDiscountUsd = -170m,
            ManualFinalPriceUsd = 468.06m
        });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "Turkmenistan, Okarem", Kind = "Origin" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "BS5144LB/LB2886TR" });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildTruckLoadingWorkbookBytes())
        });

        var model = ImportedModel(result);

        Assert.Equal(LoadingTransportType.Truck, model.TransportType);
        Assert.Equal(1, model.OriginLocationId);
        Assert.Equal(new DateTime(2026, 2, 9), model.LoadingDate);
        Assert.Equal(2, model.Rows.Count);

        var firstRow = model.Rows[0];
        Assert.Equal(new DateTime(2026, 2, 3), firstRow.LoadingDate);
        Assert.Equal("1384893", firstRow.BillOfLadingNumber);
        Assert.Equal(1, firstRow.TruckId);
        Assert.Equal("BS5144LB/LB2886TR", firstRow.ImportedTransportReference);
        Assert.Equal(28.64m, firstRow.LoadedQuantityMt);
        Assert.Equal("Herat, Afghanistan", firstRow.DestinationName);
        Assert.Equal("Soha Safa Petroleum Co", firstRow.ConsigneeName);
        Assert.Equal("Turkmenistan, Okarem -> Herat, Afghanistan", firstRow.RouteDescription);
        Assert.Equal(1426.4m, firstRow.TransportExpenseUsd);
        Assert.Null(firstRow.PlattsUsd);
        // فایل ستون قیمت ندارد، پس سطر نرخ نهایی قطعی قرارداد را می‌گیرد.
        Assert.Equal(468.06m, firstRow.LoadingPriceUsd);

        var secondRow = model.Rows[1];
        Assert.Null(secondRow.TruckId);
        Assert.Equal("ED7199LB/LB5069TR", secondRow.ImportedTransportReference);
        Assert.Equal(1676.5m, secondRow.TransportExpenseUsd);
    }

    [Fact]
    public async Task Create_Post_Saves_Truck_Workbook_After_Import_When_Freight_Has_No_Provider()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-GAS-IMPORT",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 2, 1),
            QuantityMt = 5000m
        });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "Turkmenistan, Okarem", Kind = "Origin" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "BS5144LB/LB2886TR" });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var importResult = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildTruckLoadingWorkbookBytes())
        });

        // مرورگر مقادیر خود فرم را نگه می‌دارد و فقط سطرها از فایل می‌آیند.
        var importedModel = ImportedModel(importResult);
        importedModel.ContractId = 1;
        importedModel.ProductId = 1;
        importedModel.RecordFreight = true;

        var saveResult = await controller.Create(importedModel);

        var redirect = Assert.IsType<RedirectToActionResult>(saveResult);
        Assert.Equal("Index", redirect.ActionName);
        Assert.True(controller.ModelState.IsValid);

        var loadings = await db.LoadingRegisters.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, loadings.Count);
        Assert.All(loadings, loading =>
        {
            Assert.Null(loading.LogisticsServiceProviderId);
            Assert.True(loading.FreightRateUsdPerMt > 0m);
            Assert.True(loading.TransportExpenseUsd > 0m);
        });
        Assert.Equal(2, await db.Trucks.CountAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_CreatesMissingTruck_FromImportedTransportReference()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-GAS-2",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 2, 1),
            QuantityMt = 5000m,
            PricingMethod = PricingMethod.FormulaPlatts,
            BenchmarkCode = "MOGAS",
            PlattsPeriodType = PlattsPeriodType.Manual,
            PlattsManualPriceUsd = 638.06m,
            PremiumDiscountUsd = -170m,
            ManualFinalPriceUsd = 468.06m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 2, 3),
                    BillOfLadingNumber = "1384893",
                    ImportedTransportReference = "BS5144LB/LB2886TR",
                    LoadedQuantityMt = 28.64m,
                    DestinationName = "Herat, Afghanistan",
                    ConsigneeName = "Soha Safa Petroleum Co"
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var truck = await db.Trucks.SingleAsync();
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal("BS5144LB/LB2886TR", truck.PlateNumber);
        Assert.Equal(LoadingTransportType.Truck, loading.TransportType);
        Assert.Equal(truck.Id, loading.TruckId);
        Assert.Null(loading.PlattsUsd);
        Assert.Equal(468.06m, loading.LoadingPriceUsd);
    }

    [Fact]
    public async Task Create_Post_Allows_Imported_Freight_Without_Linked_ServiceProvider()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-GAS-FREIGHT",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 2, 1),
            QuantityMt = 5000m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 2, 3),
                    BillOfLadingNumber = "1384893",
                    ImportedTransportReference = "BS5144LB/LB2886TR",
                    LoadedQuantityMt = 28.64m,
                    FreightRateUsdPerMt = 55m,
                    TransportExpenseUsd = 1426.4m
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.True(controller.ModelState.IsValid);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.LogisticsServiceProviderId);
        Assert.Null(loading.LogisticsCompanyName);
        Assert.Equal(55m, loading.FreightRateUsdPerMt);
        Assert.Equal(1575.2m, loading.TransportExpenseUsd);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_RecordFreightFalse_Clears_Imported_Freight_Before_Save()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-GAS-NO-FREIGHT",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 2, 1),
            QuantityMt = 5000m
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = false,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_0",
                    LoadingDate = new DateTime(2026, 2, 3),
                    BillOfLadingNumber = "1384893",
                    ImportedTransportReference = "BS5144LB/LB2886TR",
                    LoadedQuantityMt = 28.64m,
                    LogisticsCompanyName = "Imported carrier",
                    FreightRateUsdPerMt = 55m,
                    TransportExpenseUsd = 1426.4m
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.True(controller.ModelState.IsValid);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.LogisticsServiceProviderId);
        Assert.Null(loading.LogisticsCompanyName);
        Assert.Null(loading.FreightRateUsdPerMt);
        Assert.Null(loading.TransportExpenseUsd);
        Assert.Null(loading.RailwayExpenseUsd);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task EditPrice_Post_Updates_Only_Selected_Loading_Price_And_Audits()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-LPG",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.FormulaPlatts
        });
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 23),
                LoadedQuantityMt = 50m,
                WagonNumber = "WGN-001",
                LoadingPriceUsd = null
            },
            new LoadingRegister
            {
                Id = 2,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 24),
                LoadedQuantityMt = 50m,
                WagonNumber = "WGN-002",
                LoadingPriceUsd = 300m
            });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.EditPrice(1, new LoadingPriceEditViewModel
        {
            Id = 1,
            LoadingPriceUsd = 200m,
            PricingNote = "Platt's day loading confirmed"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal(1, redirect.RouteValues!["id"]);

        var first = await db.LoadingRegisters.SingleAsync(l => l.Id == 1);
        var second = await db.LoadingRegisters.SingleAsync(l => l.Id == 2);
        Assert.Equal(200m, first.LoadingPriceUsd);
        Assert.Contains("Platt's day loading confirmed", first.Notes);
        Assert.Equal(300m, second.LoadingPriceUsd);
        Assert.Contains(await db.AuditLogs.ToListAsync(), log =>
            log.EntityName == nameof(LoadingRegister)
            && log.EntityId == 1
            && log.Action == AuditAction.Update.ToString()
            && log.Diff is not null
            && log.Diff.Contains("LoadingPriceUsd"));
    }

    [Fact]
    public async Task Details_Returns_Enriched_Workbook_Level_Fields()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
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
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 61.5m,
            BillOfLadingNumber = "RWB-100",
            WagonNumber = "73149734",
            PlattsUsd = 638.06m,
            ConsigneeName = "Terminal Ilinka",
            DestinationName = "Trusovo"
        });
        db.Terminals.Add(new Terminal { Id = 2, Code = "ILK", Name = "Ilinka Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 3, TerminalId = 2, TankCode = "TK-01", ProductId = 1, CapacityMt = 5000m });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 21,
            LoadingRegisterId = 10,
            TerminalId = 2,
            StorageTankId = 3,
            ReceiptDate = new DateTime(2026, 4, 24),
            ReceivedQuantityMt = 40m,
            ReferenceDocument = "RCPT-100"
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 31,
            LoadingReceiptId = 21,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 2,
            StorageTankId = 3,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 24),
            QuantityMt = 40m,
            ReferenceDocument = "RCPT-100"
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 41,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                LoadedDate = new DateTime(2026, 4, 24),
                QuantityMt = 10m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 42,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                LoadedDate = new DateTime(2026, 4, 24),
                QuantityMt = 5m,
                Status = InventoryTransportLegStatus.Cancelled
            });
        db.InventoryTransportLegAllocations.AddRange(
            new InventoryTransportLegAllocation
            {
                InventoryTransportLegId = 41,
                SourcePurchaseContractId = 1,
                SourceLoadingRegisterId = 10,
                QuantityMt = 10m
            },
            new InventoryTransportLegAllocation
            {
                InventoryTransportLegId = 42,
                SourcePurchaseContractId = 1,
                SourceLoadingRegisterId = 10,
                QuantityMt = 5m
            });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Details(10);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LoadingDetailsViewModel>(view.Model);
        Assert.Equal("RWB-100", model.BillOfLadingNumber);
        Assert.Equal("73149734", model.WagonNumber);
        Assert.Equal(638.06m, model.PlattsUsd);
        Assert.Equal("Terminal Ilinka", model.ConsigneeName);
        Assert.Equal("Trusovo", model.DestinationName);
        Assert.Equal(40m, model.TotalReceivedQuantityMt);
        Assert.Equal(11.5m, model.RemainingToReceiveMt);
        Assert.Equal(1, model.ActiveTransportCount);
        Assert.Equal(41, model.FirstActiveTransportLegId);
        var receipt = Assert.Single(model.ReceiptItems);
        Assert.Equal(21, receipt.Id);
        Assert.Equal("Ilinka Terminal", receipt.TerminalName);
        Assert.Equal("TK-01", receipt.StorageTankCode);
        Assert.Equal(31, receipt.InventoryMovementId);
    }

    [Fact]
    public async Task Create_Post_Creates_One_Loading_Register_Per_Row_For_Selected_Transport_Type()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
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
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "BNK", Kind = "Origin" });
        db.Trucks.AddRange(
            new Truck { Id = 1, PlateNumber = "TRK-01" },
            new Truck { Id = 2, PlateNumber = "TRK-02" });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Truck,
            Notes = "April batch",
            RecordFreight = true,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    LoadingDate = new DateTime(2026, 4, 23),
                    TruckId = 1,
                    BillOfLadingNumber = "CMR-001",
                    RouteDescription = "BNK -> Trusovo",
                    LoadedQuantityMt = 32.5m,
                    PlattsUsd = 638.06m,
                    LoadingPriceUsd = 570m,
                    TransportExpenseUsd = 45m,
                    LogisticsCompanyName = "Axis Logistics",
                    ConsigneeName = "Terminal Ilinka",
                    DestinationName = "Trusovo"
                },
                new LoadingCreateRowViewModel
                {
                    LoadingDate = new DateTime(2026, 4, 24),
                    TruckId = 2,
                    BillOfLadingNumber = "CMR-002",
                    RouteDescription = "BNK -> Ilinka",
                    LoadedQuantityMt = 31m,
                    PlattsUsd = 641.12m,
                    LoadingPriceUsd = 572m,
                    TransportExpenseUsd = 44m,
                    LogisticsCompanyName = "Axis Logistics",
                    ConsigneeName = "Depot B",
                    DestinationName = "Ilinka"
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var loadings = await db.LoadingRegisters.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, loadings.Count);

        Assert.All(loadings, loading => Assert.Equal(LoadingTransportType.Truck, loading.TransportType));
        Assert.Equal(1, loadings[0].TruckId);
        Assert.Null(loadings[0].VesselId);
        Assert.Null(loadings[0].WagonNumber);
        Assert.Equal("BNK -> Trusovo", loadings[0].RouteDescription);
        Assert.Equal("Axis Logistics", loadings[0].LogisticsCompanyName);
        Assert.Equal(638.06m, loadings[0].PlattsUsd);
        Assert.Equal(570m, loadings[0].LoadingPriceUsd);
        Assert.Equal(45m, loadings[0].TransportExpenseUsd);
        Assert.Equal("April batch", loadings[0].Notes);

        Assert.Equal(2, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_Post_Compact_Import_Persists_All_234_Loadings()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-234",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 1_000m
        });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location
        {
            Id = 1,
            Name = "BNK",
            Kind = "Origin"
        });
        await db.SaveChangesAsync();

        var rows = Enumerable.Range(1, 234)
            .Select(index => new LoadingCreateRowViewModel
            {
                RowKey = $"xls_{index}",
                LoadingDate = new DateTime(2026, 4, 23),
                WagonNumber = $"WGN-{index:000}",
                BillOfLadingNumber = $"RWB-{index:000}",
                LoadedQuantityMt = 1m,
                LoadingPriceUsd = 500m
            })
            .ToList();
        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            ImportedRowsJson = JsonSerializer.Serialize(
                rows,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal(234, await db.LoadingRegisters.CountAsync());
        Assert.Equal(234, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_Post_Uses_ServiceProvider_And_FreightRate_For_Truck_Loading()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 1,
            Code = "LOG-1",
            Name = "Axis Logistics",
            ProviderType = ServiceProviderType.TransportCompany,
            IsActive = true
        });
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
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            FreightCostResponsibility = CostResponsibility.Buyer,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    LoadingDate = new DateTime(2026, 4, 23),
                    TruckId = 1,
                    BillOfLadingNumber = "CMR-001",
                    LoadedQuantityMt = 32.5m,
                    LogisticsServiceProviderId = 1,
                    FreightRateUsdPerMt = 2.5m
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(1, loading.LogisticsServiceProviderId);
        Assert.Equal("Axis Logistics", loading.LogisticsCompanyName);
        Assert.Equal(2.5m, loading.FreightRateUsdPerMt);
        Assert.Equal(81.25m, loading.TransportExpenseUsd);
        Assert.Null(loading.RailwayExpenseUsd);
        Assert.Null(loading.RailwayRateUsd);

        var expense = await db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .SingleAsync();
        Assert.Equal(1, expense.LoadingRegisterId);
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Equal(1, expense.ContractId);
        Assert.Equal(81.25m, expense.AmountUsd);
        Assert.Equal("LOAD-TRANSPORT", expense.ExpenseType?.Code);
        Assert.False(expense.IsCancelled);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Equal(81.25m, ledger.AmountUsd);

        var providerProfile = Assert.IsType<ViewResult>(await new ServiceProvidersController(db).Details(1));
        var profileModel = Assert.IsType<PTGOilSystem.Web.Models.ServiceProviders.ServiceProviderProfileViewModel>(providerProfile.Model);
        Assert.Equal(81.25m, profileModel.TotalExpensesUsd);
        Assert.Equal(-81.25m, profileModel.LedgerBalanceUsd);
    }

    [Fact]
    public async Task Create_Post_Uses_OperationalAsset_And_FreightRate_For_Internal_Truck_Rent()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true });
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-1",
            Name = "Company Truck 1",
            AssetType = OperationalAssetType.Truck,
            OwnershipMode = OperationalAssetOwnershipMode.FullyCompanyOwned,
            DefaultInternalRateUsd = 3m,
            IsActive = true
        });
        db.AssetOwnershipShares.Add(new AssetOwnershipShare
        {
            Id = 1,
            OperationalAssetId = 1,
            OwnerType = AssetOwnerType.Company,
            CompanyId = 1,
            SharePercent = 100m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    LoadingDate = new DateTime(2026, 4, 23),
                    TruckId = 1,
                    BillOfLadingNumber = "CMR-OWN-001",
                    LoadedQuantityMt = 32.5m,
                    OperationalAssetId = 1,
                    FreightRateUsdPerMt = 3m
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.LogisticsServiceProviderId);
        Assert.Equal("Company Truck 1", loading.LogisticsCompanyName);
        Assert.Equal(3m, loading.FreightRateUsdPerMt);
        Assert.Equal(97.5m, loading.TransportExpenseUsd);

        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());

        var rent = await db.AssetRentTransactions
            .Include(r => r.RentShares)
            .SingleAsync();
        Assert.Equal(1, rent.OperationalAssetId);
        Assert.Equal(loading.Id, rent.LoadingRegisterId);
        Assert.Equal(AssetRentUsageType.InternalCompanyUse, rent.UsageType);
        Assert.Equal(AssetRentChargedToType.PurchaseContract, rent.ChargedToType);
        Assert.Equal(1, rent.ChargedToContractId);
        Assert.Equal(32.5m, rent.QuantityMt);
        Assert.Equal(3m, rent.Rate);
        Assert.Equal(97.5m, rent.AmountUsd);

        var share = Assert.Single(rent.RentShares);
        Assert.Equal(AssetOwnerType.Company, share.OwnerType);
        Assert.Equal(1, share.CompanyId);
        Assert.Equal(97.5m, share.ShareAmountUsd);
    }

    [Theory]
    [InlineData(null, "35.88", "107.64")]
    [InlineData("40", "40", "120")]
    public async Task Create_Post_Uses_Chargeable_Quantity_Or_Loaded_Fallback_For_Wagon_Railway_Cost(
        string? chargeableQuantityText,
        string expectedChargeableQuantityText,
        string expectedRailwayExpenseText)
    {
        var chargeableQuantity = string.IsNullOrWhiteSpace(chargeableQuantityText)
            ? (decimal?)null
            : decimal.Parse(chargeableQuantityText, CultureInfo.InvariantCulture);
        var expectedChargeableQuantity = decimal.Parse(expectedChargeableQuantityText, CultureInfo.InvariantCulture);
        var expectedRailwayExpense = decimal.Parse(expectedRailwayExpenseText, CultureInfo.InvariantCulture);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 1,
            Code = "RAIL-1",
            Name = "Railway Services",
            ProviderType = ServiceProviderType.RailwayService,
            IsActive = true
        });
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
        await db.SaveChangesAsync();

        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            RecordFreight = true,
            FreightCostResponsibility = CostResponsibility.Buyer,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    LoadingDate = new DateTime(2026, 4, 23),
                    WagonNumber = "67-50825769",
                    LoadedQuantityMt = 35.88m,
                    ChargeableQuantityMt = chargeableQuantity,
                    LogisticsServiceProviderId = 1,
                    FreightRateUsdPerMt = 3m
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(1, loading.LogisticsServiceProviderId);
        Assert.Equal("Railway Services", loading.LogisticsCompanyName);
        Assert.Equal(3m, loading.FreightRateUsdPerMt);
        Assert.Equal(expectedChargeableQuantity, loading.ChargeableQuantityMt);
        Assert.Equal(3m, loading.RailwayRateUsd);
        Assert.Equal(expectedRailwayExpense, loading.RailwayExpenseUsd);
        Assert.Null(loading.TransportExpenseUsd);

        var expense = await db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .SingleAsync();
        Assert.Equal(1, expense.LoadingRegisterId);
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Equal(1, expense.ContractId);
        Assert.Equal(expectedRailwayExpense, expense.AmountUsd);
        Assert.Equal("LOAD-WAGON-RENT", expense.ExpenseType?.Code);
        Assert.False(expense.IsCancelled);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Equal(expectedRailwayExpense, ledger.AmountUsd);

        var providerProfile = Assert.IsType<ViewResult>(await new ServiceProvidersController(db).Details(1));
        var profileModel = Assert.IsType<PTGOilSystem.Web.Models.ServiceProviders.ServiceProviderProfileViewModel>(providerProfile.Model);
        Assert.Equal(expectedRailwayExpense, profileModel.TotalExpensesUsd);
        Assert.Equal(-expectedRailwayExpense, profileModel.LedgerBalanceUsd);
    }

    [Fact]
    public async Task Create_Post_FixedContractRubRate_Snapshots_And_Locks_Loading()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedRubLoadingScenario(db, RubSettlementRatePolicy.FixedContractRate, 80m);
        var controller = NewLoadingController(db);

        var result = await controller.Create(RubCreateModel(new LoadingCreateRowViewModel
        {
            RowKey = "r1",
            ContractId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.RateLater,
            WagonNumber = "W-1"
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal("RUB", loading.SettlementCurrencyCode);
        Assert.Equal(RubSettlementRateStatus.Locked, loading.RubRateStatus);
        Assert.Equal(80m, loading.RubPerUsdRate);
        Assert.Equal(1000m, loading.AmountUsdAtRubLock);
        Assert.Equal(80000m, loading.AmountRubAtRubLock);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Loading");
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1000m, ledger.AmountUsd);
        Assert.Equal(80000m, ledger.SourceAmount);
        Assert.Equal("RUB", ledger.SourceCurrencyCode);
        Assert.Equal(1, ledger.SupplierId);
        Assert.Equal(1, ledger.ContractId);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_PerLoadingRubRate_Uses_Row_Rates()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedRubLoadingScenario(db, RubSettlementRatePolicy.PerLoadingRate);
        var controller = NewLoadingController(db);

        var result = await controller.Create(RubCreateModel(
            new LoadingCreateRowViewModel
            {
                RowKey = "r1",
                ContractId = 1,
                LoadingDate = new DateTime(2026, 5, 2),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = 100m,
                SettlementCurrencyCode = "RUB",
                RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate,
                RubPerUsdRate = 80m,
                WagonNumber = "W-1"
            },
            new LoadingCreateRowViewModel
            {
                RowKey = "r2",
                ContractId = 1,
                LoadingDate = new DateTime(2026, 5, 3),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = 100m,
                SettlementCurrencyCode = "RUB",
                RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate,
                RubPerUsdRate = 82.5m,
                WagonNumber = "W-2"
            }));

        Assert.IsType<RedirectToActionResult>(result);
        var loadings = await db.LoadingRegisters.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, loadings.Count);
        Assert.Equal(80m, loadings[0].RubPerUsdRate);
        Assert.Equal(80000m, loadings[0].AmountRubAtRubLock);
        Assert.Equal(82.5m, loadings[1].RubPerUsdRate);
        Assert.Equal(82500m, loadings[1].AmountRubAtRubLock);

        var ledgers = await db.LedgerEntries
            .Where(l => l.SourceType == "Loading")
            .OrderBy(l => l.SourceId)
            .ToListAsync();
        Assert.Equal(2, ledgers.Count);
        Assert.Equal(new[] { 80000m, 82500m }, ledgers.Select(l => l.SourceAmount!.Value).ToArray());
        Assert.All(ledgers, ledger =>
        {
            Assert.Equal(LedgerSide.Credit, ledger.Side);
            Assert.Equal(1000m, ledger.AmountUsd);
            Assert.Equal("RUB", ledger.SourceCurrencyCode);
            Assert.Equal(1, ledger.SupplierId);
            Assert.Equal(1, ledger.ContractId);
        });
    }

    [Fact]
    public async Task Create_Post_PerLoadingRubRate_Requires_Row_Rate()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedRubLoadingScenario(db, RubSettlementRatePolicy.PerLoadingRate);
        var controller = NewLoadingController(db);

        var result = await controller.Create(RubCreateModel(new LoadingCreateRowViewModel
        {
            RowKey = "r1",
            ContractId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            WagonNumber = "W-1"
        }));

        Assert.IsType<ViewResult>(result);
        Assert.Contains(
            controller.ModelState["Rows[r1].RubPerUsdRate"]!.Errors,
            error => error.ErrorMessage.Contains("نرخ RUB همین بارگیری"));
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_PerLoadingRubRate_Derives_Rate_From_File_Rub_Figures()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedRubLoadingScenario(db, RubSettlementRatePolicy.PerLoadingRate);
        var controller = NewLoadingController(db);

        // فایل اکسل فقط ارقام روبلی دارد (مجموع روبل)، بدون نرخ صریح روبل/دالر.
        // نرخ باید از «مجموع روبل ÷ ارزش دالری» مشتق شود: 80000 ÷ (10×100) = 80.
        var result = await controller.Create(RubCreateModel(new LoadingCreateRowViewModel
        {
            RowKey = "r1",
            ContractId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate,
            SettlementValueRub = 80000m,
            WagonNumber = "W-1"
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = Assert.Single(await db.LoadingRegisters.ToListAsync());
        Assert.Equal(RubSettlementRateStatus.Locked, loading.RubRateStatus);
        Assert.Equal(80m, loading.RubPerUsdRate);
        Assert.Equal(1000m, loading.AmountUsdAtRubLock);
        Assert.Equal(80000m, loading.AmountRubAtRubLock);
        Assert.Equal("Loading file", loading.RubRateSource);

        var ledger = Assert.Single(await db.LedgerEntries.Where(l => l.SourceType == "Loading").ToListAsync());
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1000m, ledger.AmountUsd);
        Assert.Equal(80000m, ledger.SourceAmount!.Value);
        Assert.Equal("RUB", ledger.SourceCurrencyCode);
    }

    [Fact]
    public async Task Create_Post_RateLater_Persists_Pending_Without_Zero_Rub()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedRubLoadingScenario(db, RubSettlementRatePolicy.RateLater);
        var controller = NewLoadingController(db);

        var result = await controller.Create(RubCreateModel(new LoadingCreateRowViewModel
        {
            RowKey = "r1",
            ContractId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            WagonNumber = "W-1"
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(RubSettlementRateStatus.Pending, loading.RubRateStatus);
        Assert.Null(loading.AmountUsdAtRubLock);
        Assert.Null(loading.AmountRubAtRubLock);

        var createView = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml")));
        Assert.Contains("lic-stat-rub", createView);
        Assert.Contains("در انتظار نرخ", createView);
        Assert.DoesNotContain("₽ 0.00", createView);
    }

    [Fact]
    public async Task SetRubleRate_Post_Locks_Pending_Loading_And_Posts_Supplier_Debt()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedRubLoadingScenario(db, RubSettlementRatePolicy.RateLater);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 5m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRateStatus = RubSettlementRateStatus.Pending
        });
        await db.SaveChangesAsync();
        var controller = NewLoadingController(db);

        var result = await controller.SetRubleRate(1, new LoadingRubleRateEditViewModel
        {
            Id = 1,
            RubPerUsdRate = 81m,
            RubRateDate = new DateTime(2026, 5, 4),
            RubRateSource = "Bank"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(RubSettlementRateStatus.Locked, loading.RubRateStatus);
        Assert.Equal(81m, loading.RubPerUsdRate);
        Assert.Equal(500m, loading.AmountUsdAtRubLock);
        Assert.Equal(40500m, loading.AmountRubAtRubLock);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Loading");
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(500m, ledger.AmountUsd);
        Assert.Equal(40500m, ledger.SourceAmount);
        Assert.Equal("RUB", ledger.SourceCurrencyCode);
        Assert.Equal(1, ledger.SupplierId);
        Assert.Equal(1, ledger.ContractId);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task SetRubleRate_Post_Does_Not_Overwrite_Locked_Loading()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedRubLoadingScenario(db, RubSettlementRatePolicy.PerLoadingRate);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 5m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRateStatus = RubSettlementRateStatus.Locked,
            RubPerUsdRate = 80m,
            AmountUsdAtRubLock = 500m,
            AmountRubAtRubLock = 40000m
        });
        await db.SaveChangesAsync();
        var controller = NewLoadingController(db);

        var result = await controller.SetRubleRate(1, new LoadingRubleRateEditViewModel
        {
            Id = 1,
            RubPerUsdRate = 82.5m,
            RubRateDate = new DateTime(2026, 5, 4),
            RubRateSource = "Bank"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(RubSettlementRateStatus.Locked, loading.RubRateStatus);
        Assert.Equal(80m, loading.RubPerUsdRate);
        Assert.Equal(500m, loading.AmountUsdAtRubLock);
        Assert.Equal(40000m, loading.AmountRubAtRubLock);
    }

    private static void SeedRubLoadingScenario(
        ApplicationDbContext db,
        RubSettlementRatePolicy policy,
        decimal? fixedRate = null)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "BNK", Kind = "Origin" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = policy,
            ContractRubPerUsdRate = fixedRate,
            ContractRubRateDate = fixedRate.HasValue ? new DateTime(2026, 5, 1) : null,
            ContractRubRateSource = fixedRate.HasValue ? "Contract" : null
        });
        db.SaveChanges();
    }

    private static LoadingCreateViewModel RubCreateModel(params LoadingCreateRowViewModel[] rows)
        => new()
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = rows.First().LoadingDate,
            LoadedQuantityMt = rows.Sum(r => r.LoadedQuantityMt),
            LockContract = true,
            Rows = rows.ToList()
        };

    private static void SeedPurchaseLoadingScenario(
        ApplicationDbContext db,
        bool withServiceProvider = false,
        bool withOwnedAsset = false)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 1,
            Code = "LOAD-TRANSPORT",
            Name = "Loading Transport Freight",
            NamePersian = "کرایه حمل بارگیری",
            Category = "Transport",
            IsActive = true
        });

        if (withServiceProvider)
        {
            db.ServiceProviders.Add(new ServiceProvider
            {
                Id = 1,
                Code = "LOG-1",
                Name = "Axis Logistics",
                ProviderType = ServiceProviderType.TransportCompany,
                IsActive = true
            });
        }

        if (withOwnedAsset)
        {
            db.OperationalAssets.Add(new OperationalAsset
            {
                Id = 1,
                AssetCode = "TRK-OWN-1",
                Name = "Company Truck 1",
                AssetType = OperationalAssetType.Truck,
                OwnershipMode = OperationalAssetOwnershipMode.FullyCompanyOwned,
                DefaultInternalRateUsd = 3m,
                IsActive = true
            });
            db.AssetOwnershipShares.Add(new AssetOwnershipShare
            {
                Id = 1,
                OperationalAssetId = 1,
                OwnerType = AssetOwnerType.Company,
                CompanyId = 1,
                SharePercent = 100m,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        }

        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadingDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 32.5m
        });
        db.SaveChanges();
    }

    private static LoadingController NewLoadingController(
        ApplicationDbContext db,
        IAssetRentPostingService? rentPosting = null)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance, rentPosting: rentPosting)
        {
            TempData = BuildTempData()
        };

    private static LoadingExpenseEditViewModel ExpenseModelWithLines(params LoadingExpenseLineInputModel[] lines)
        => new() { Id = 1, Lines = lines.ToList() };

    [Fact]
    public async Task EditExpenses_Post_FixedAmount_None_Saves_Line_And_Mirrors_Legacy_Field()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db);
        var controller = NewLoadingController(db);

        var result = await controller.EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = 50m,
            PartyType = LoadingExpensePartyType.None
        }));

        Assert.IsType<RedirectToActionResult>(result);

        var line = await db.LoadingExpenseLines.SingleAsync();
        Assert.Equal(50m, line.AmountUsd);
        Assert.Equal(LoadingExpensePartyType.None, line.PartyType);
        Assert.Null(line.ExpenseTransactionId);
        Assert.Null(line.AssetRentTransactionId);

        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.AssetRentTransactions.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        // Legacy column is kept and mirrored from the "None" line (Transport bucket).
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(50m, loading.TransportExpenseUsd);
    }

    [Fact]
    public async Task EditExpenses_Post_PerMetricTon_None_Computes_Amount()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db);
        var controller = NewLoadingController(db);

        var result = await controller.EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.PerMetricTon,
            QuantityMt = 10m,
            UnitRateUsd = 2.5m,
            AmountUsd = 0m,
            PartyType = LoadingExpensePartyType.None
        }));

        Assert.IsType<RedirectToActionResult>(result);

        var line = await db.LoadingExpenseLines.SingleAsync();
        Assert.Equal(LoadingExpenseCalculationMode.PerMetricTon, line.CalculationMode);
        Assert.Equal(10m, line.QuantityMt);
        Assert.Equal(2.5m, line.UnitRateUsd);
        Assert.Equal(25m, line.AmountUsd);
    }

    [Fact]
    public async Task EditExpenses_Post_Modal_Success_Returns_Json_Redirect()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db);
        var controller = NewLoadingController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.EditExpenses(1, new LoadingExpenseEditViewModel
        {
            Id = 1,
            ReturnUrl = "/Loading/Details/1",
            Lines =
            [
                new LoadingExpenseLineInputModel
                {
                    ExpenseTypeId = 1,
                    CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
                    AmountUsd = 30m,
                    PartyType = LoadingExpensePartyType.None
                }
            ]
        }, modal: true);

        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)(GetJsonProperty(json.Value!, "success") ?? false));
        Assert.Equal("/Loading/Details/1", GetJsonProperty(json.Value!, "redirectUrl"));
    }

    [Fact]
    public async Task EditExpenses_Post_Modal_Invalid_Returns_Partial_View()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db);
        var controller = NewLoadingController(db);

        var result = await controller.EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = -5m,
            PartyType = LoadingExpensePartyType.None
        }), modal: true);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_LoadingExpenseEditor", partial.ViewName);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task EditExpenses_Post_ServiceProvider_Line_Creates_Linked_Expense_And_Credit_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db, withServiceProvider: true);
        var controller = NewLoadingController(db);

        var result = await controller.EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = 81.25m,
            PartyType = LoadingExpensePartyType.ServiceProvider,
            ServiceProviderId = 1
        }));

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(1, loading.LogisticsServiceProviderId);
        Assert.Equal("Axis Logistics", loading.LogisticsCompanyName);
        // Service-provider amounts are NOT mirrored into legacy inline fields.
        Assert.Null(loading.TransportExpenseUsd);

        var expense = await db.ExpenseTransactions.Include(e => e.ExpenseType).SingleAsync();
        Assert.Equal(1, expense.LoadingRegisterId);
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Equal(1, expense.ContractId);
        Assert.Equal(81.25m, expense.AmountUsd);
        Assert.Equal("LOAD-TRANSPORT", expense.ExpenseType?.Code);
        Assert.False(expense.IsCancelled);

        var line = await db.LoadingExpenseLines.SingleAsync();
        Assert.Equal(expense.Id, line.ExpenseTransactionId);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Equal(81.25m, ledger.AmountUsd);

        Assert.Empty(await db.InventoryMovements.ToListAsync());

        var providerProfile = Assert.IsType<ViewResult>(await new ServiceProvidersController(db).Details(1));
        var profileModel = Assert.IsType<PTGOilSystem.Web.Models.ServiceProviders.ServiceProviderProfileViewModel>(providerProfile.Model);
        Assert.Equal(81.25m, profileModel.TotalExpensesUsd);
        Assert.Equal(-81.25m, profileModel.LedgerBalanceUsd);
    }

    [Fact]
    public async Task EditExpenses_Post_OperationalAsset_Line_Creates_Internal_Rent_No_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db, withOwnedAsset: true);
        var controller = NewLoadingController(db);

        var result = await controller.EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = 97.5m,
            PartyType = LoadingExpensePartyType.OperationalAsset,
            OperationalAssetId = 1
        }));

        Assert.IsType<RedirectToActionResult>(result);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.LogisticsServiceProviderId);
        Assert.Equal("Company Truck 1", loading.LogisticsCompanyName);
        Assert.Null(loading.TransportExpenseUsd);

        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        var rent = await db.AssetRentTransactions.Include(r => r.RentShares).SingleAsync();
        Assert.Equal(1, rent.OperationalAssetId);
        Assert.Equal(1, rent.LoadingRegisterId);
        Assert.Equal(AssetRentUsageType.InternalCompanyUse, rent.UsageType);
        Assert.Equal(AssetRentChargedToType.PurchaseContract, rent.ChargedToType);
        Assert.Equal(1, rent.ChargedToContractId);
        Assert.Equal(97.5m, rent.AmountUsd);
        Assert.False(rent.IsPostedToLedger);
        Assert.False(rent.IsCancelled);

        var line = await db.LoadingExpenseLines.SingleAsync();
        Assert.Equal(rent.Id, line.AssetRentTransactionId);

        var share = Assert.Single(rent.RentShares);
        Assert.Equal(AssetOwnerType.Company, share.OwnerType);
        Assert.Equal(100m, share.SharePercent);
        Assert.Equal(97.5m, share.ShareAmountUsd);
    }

    [Fact]
    public async Task EditExpenses_Post_OperationalAsset_Line_Invokes_Internal_Accounting_Path()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db, withOwnedAsset: true);
        var posting = new RecordingAssetRentPostingService();

        await NewLoadingController(db, posting).EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = 97.5m,
            PartyType = LoadingExpensePartyType.OperationalAsset,
            OperationalAssetId = 1
        }));

        Assert.Equal(1, posting.PostCalls);
        Assert.Equal(AssetRentUsageType.InternalCompanyUse, posting.LastRent?.UsageType);
        Assert.Equal(1m, posting.LastConversion?.AppliedRateToBase);
    }

    [Fact]
    public async Task EditExpenses_Post_ServiceProvider_Line_Ignores_Operational_Asset()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db, withServiceProvider: true, withOwnedAsset: true);
        var controller = NewLoadingController(db);

        var result = await controller.EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = 40m,
            PartyType = LoadingExpensePartyType.ServiceProvider,
            ServiceProviderId = 1,
            OperationalAssetId = 1 // must be ignored: a line settles with exactly one counterparty
        }));

        Assert.IsType<RedirectToActionResult>(result);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(40m, expense.AmountUsd);
        Assert.Empty(await db.AssetRentTransactions.ToListAsync());

        var line = await db.LoadingExpenseLines.SingleAsync();
        Assert.Equal(LoadingExpensePartyType.ServiceProvider, line.PartyType);
        Assert.Null(line.OperationalAssetId);
    }

    [Fact]
    public async Task EditExpenses_Post_Rejects_Negative_Amount()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db);
        var controller = NewLoadingController(db);

        var result = await controller.EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = -5m,
            PartyType = LoadingExpensePartyType.None
        }));

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.LoadingExpenseLines.ToListAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task EditExpenses_Post_Removing_Line_Cancels_Linked_Expense()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedPurchaseLoadingScenario(db, withServiceProvider: true);

        var firstResult = await NewLoadingController(db).EditExpenses(1, ExpenseModelWithLines(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = 40m,
            PartyType = LoadingExpensePartyType.ServiceProvider,
            ServiceProviderId = 1
        }));
        Assert.IsType<RedirectToActionResult>(firstResult);
        var createdExpense = await db.ExpenseTransactions.SingleAsync();
        Assert.False(createdExpense.IsCancelled);

        // Re-post with no lines → the previously created line is removed and its expense cancelled.
        var secondResult = await NewLoadingController(db).EditExpenses(1, new LoadingExpenseEditViewModel { Id = 1, Lines = [] });
        Assert.IsType<RedirectToActionResult>(secondResult);

        Assert.Empty(await db.LoadingExpenseLines.ToListAsync());
        var cancelledExpense = await db.ExpenseTransactions.SingleAsync();
        Assert.True(cancelledExpense.IsCancelled);
        // A reversal ledger entry was posted (original credit + reversal debit).
        Assert.Equal(2, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense" && l.SourceId == cancelledExpense.Id));
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new InMemoryTempDataProvider());

    private static IUrlHelper BuildUrlHelper()
        => new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));

    private static object? GetJsonProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property!.GetValue(value);
    }

    private static IFormFile BuildWorkbookFile(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "ImportWorkbookFile", "loading.xlsx")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
    }

    private static byte[] BuildLoadingWorkbookBytes()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                BuildTextRow(1, ("D", "Contract 1000 MT LPG To Akina Number b-034321")),
                BuildTextRow(4,
                    ("A", "No"),
                    ("B", "date"),
                    ("C", "RWB NO"),
                    ("D", "Wagon No"),
                    ("E", "Loaded quantity (MT)"),
                    ("F", "platts"),
                    ("G", "Loading price"),
                    ("H", "Loading amount"),
                    ("I", "Consignee"),
                    ("J", "Transportation company"),
                    ("K", "Destination")),
                BuildMixedRow(5,
                    ("A", 1m),
                    ("B", "11/25/2022"),
                    ("C", "74207656"),
                    ("D", "67-50825769"),
                    ("E", 35.88m),
                    ("G", 468.06m),
                    ("H", 16794m),
                    ("I", "Favad Coltd"),
                    ("J", "Oxus"),
                    ("K", "Akina")),
                BuildMixedRow(6,
                    ("A", 2m),
                    ("B", "11/25/2022"),
                    ("C", "74207657"),
                    ("D", "67-57877854"),
                    ("E", 35.08m),
                    ("F", 638.06m),
                    ("G", 468.06m),
                    ("H", 16420m),
                    ("I", "Favad Coltd"),
                    ("J", "Oxus"),
                    ("K", "Akina")));

            worksheetPart.Worksheet = new Worksheet(sheetData);

            var railwayWorksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            railwayWorksheetPart.Worksheet = new Worksheet(new SheetData(
                BuildTextRow(1,
                    ("A", "RWB No"),
                    ("B", "Wagon No"),
                    ("C", "Chargeable quantity"),
                    ("D", "Railway rate"),
                    ("E", "Railway expense")),
                BuildMixedRow(2,
                    ("A", "74207656"),
                    ("B", "67-50825769"),
                    ("C", 40m),
                    ("D", 3m),
                    ("E", 120m)),
                BuildMixedRow(3,
                    ("A", "74207657"),
                    ("B", "67-57877854"),
                    ("C", 36m),
                    ("D", 4m),
                    ("E", 144m))));

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "loading"
            });
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(railwayWorksheetPart),
                SheetId = 2,
                Name = "railway"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static byte[] BuildUnknownStructureWorkbookBytes()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                BuildTextRow(1, ("A", "Something"), ("B", "Else")),
                BuildTextRow(2, ("A", "value"), ("B", "other")));

            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "January"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    // شیت واگن با یک ردیف بدون «مقدار بارگیری» تا مسیر خطای سطری آزمایش شود.
    private static byte[] BuildWagonWorkbookWithMissingQuantityBytes()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                BuildTextRow(4,
                    ("A", "No"),
                    ("B", "date"),
                    ("C", "RWB NO"),
                    ("D", "Wagon No"),
                    ("E", "Loaded quantity (MT)"),
                    ("G", "Loading price")),
                BuildMixedRow(5,
                    ("A", 1m),
                    ("B", "11/25/2022"),
                    ("C", "74207656"),
                    ("D", "67-50825769"),
                    ("E", 35.88m),
                    ("G", 468.06m)),
                BuildMixedRow(6,
                    ("A", 2m),
                    ("B", "11/25/2022"),
                    ("C", "74207657"),
                    ("D", "67-57877854"),
                    ("G", 468.06m)));

            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "January"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static byte[] BuildSolvexRubWorkbookBytes(bool includeBrokenLeadingUsdPrice = false)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                BuildTextRow(1, ("D", "BNK-SOLVEX March")),
                BuildTextRow(4,
                    ("A", "No"),
                    ("B", "date"),
                    ("C", "RWB NO"),
                    ("D", "Wagon No"),
                    ("E", "Loaded quantity (MT)"),
                    ("F", "Loading price"),
                    ("G", "Rub Price"),
                    ("H", "Total RUB")));

            if (includeBrokenLeadingUsdPrice)
            {
                sheetData.Append(BuildMixedRow(5,
                    ("A", 1m),
                    ("B", "3/15/2026"),
                    ("C", "74300000"),
                    ("D", "67-50000000"),
                    ("E", 53.35m),
                    ("F", "#REF!"),
                    ("G", 41464.11m),
                    ("H", 2212110.2685m)));
            }

            var validRowIndex = includeBrokenLeadingUsdPrice ? 6U : 5U;
            sheetData.Append(BuildMixedRow(validRowIndex,
                    ("A", 1m),
                    ("B", "3/15/2026"),
                    ("C", "74300001"),
                    ("D", "67-50000001"),
                    ("E", 9782.4m),
                    ("F", 931.39m),
                    ("G", 41464.11m),
                    ("H", 405618509.664m)));

            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "loading"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static byte[] BuildSolvexRubRateOnlyWorkbookBytes()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                BuildTextRow(1, ("D", "BNK-SOLVEX rate only")),
                BuildTextRow(4,
                    ("A", "No"),
                    ("B", "date"),
                    ("C", "RWB NO"),
                    ("D", "Wagon No"),
                    ("E", "Loaded quantity (MT)"),
                    ("F", "Loading price"),
                    ("G", "RUB/MT")),
                BuildMixedRow(5,
                    ("A", 1m),
                    ("B", "3/15/2026"),
                    ("C", "74300002"),
                    ("D", "67-50000002"),
                    ("E", 9782.4m),
                    ("F", 931.39m),
                    ("G", 41464.11m)));

            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "loading"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static byte[] BuildTruckLoadingWorkbookBytes()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            var balanceWorksheet = workbookPart.AddNewPart<WorksheetPart>();
            balanceWorksheet.Worksheet = new Worksheet(new SheetData(
                BuildTextRow(1, ("A", "Balance"))));
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(balanceWorksheet),
                SheetId = 1,
                Name = "بیلانس"
            });

            var loadingReportWorksheet = workbookPart.AddNewPart<WorksheetPart>();
            loadingReportWorksheet.Worksheet = new Worksheet(new SheetData(
                BuildTextRow(1, ("A", "SOGDIANA")),
                BuildMixedRow(2, ("A", "Date of report"), ("C", (decimal)new DateTime(2026, 2, 9).ToOADate())),
                BuildTextRow(3, ("A", "Vessel"), ("C", "\"Volga\"")),
                BuildTextRow(4, ("A", "Location"), ("C", "Turkmenistan, Okarem")),
                BuildTextRow(5, ("A", "Product"), ("C", "AI-92-K5")),
                BuildTextRow(13,
                    ("A", "No"),
                    ("B", "Date"),
                    ("C", "CMR"),
                    ("D", "Trucks"),
                    ("E", "Loaded quantity (MT)"),
                    ("F", "Belong to"),
                    ("G", "Consignee"),
                    ("H", "Transshipment/Rental ($)"),
                    ("I", "Total"),
                    ("K", "Rent of Stock"),
                    ("L", "Total")),
                BuildMixedRow(15,
                    ("A", 1m),
                    ("B", (decimal)new DateTime(2026, 2, 3).ToOADate()),
                    ("C", 1384893m),
                    ("D", "BS5144LB/LB2886TR"),
                    ("E", 28.64m),
                    ("F", "Herat, Afghanistan"),
                    ("G", "Soha Safa Petroleum Co"),
                    ("H", 55m),
                    ("I", 1575.2m),
                    ("K", 25m),
                    ("L", 716m)),
                BuildMixedRow(16,
                    ("A", 2m),
                    ("B", (decimal)new DateTime(2026, 2, 3).ToOADate()),
                    ("C", 1384894m),
                    ("D", "ED7199LB/LB5069TR"),
                    ("E", 30.82m),
                    ("F", "Herat, Afghanistan"),
                    ("G", "Soha Safa Petroleum Co"),
                    ("H", 55m),
                    ("I", 1695.1m),
                    ("K", 25m),
                    ("L", 770.5m)),
                BuildMixedRow(17,
                    ("E", 59.46m),
                    ("I", 3270.3m),
                    ("L", 1486.5m))));
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(loadingReportWorksheet),
                SheetId = 2,
                Name = "Loading Report"
            });

            var truckRentWorksheet = workbookPart.AddNewPart<WorksheetPart>();
            truckRentWorksheet.Worksheet = new Worksheet(new SheetData(
                BuildTextRow(1, ("A", "لیست کرایه موترها")),
                BuildTextRow(2,
                    ("A", "№"),
                    ("B", "تاریخ"),
                    ("C", "سیمیر"),
                    ("D", "نمبر موتر"),
                    ("E", "وزن سیمیر"),
                    ("F", "مقصد"),
                    ("G", "فی تن کرایه"),
                    ("H", "مجموع"),
                    ("I", "وزن تخلیه"),
                    ("J", "کمبودی"),
                    ("K", "حواکت"),
                    ("L", "مجرای کمبودی"),
                    ("M", "کمبود قابل مجرا"),
                    ("N", "فی تن"),
                    ("P", "کرایه پرداختی")),
                BuildMixedRow(3,
                    ("A", 1m),
                    ("B", (decimal)new DateTime(2026, 2, 3).ToOADate()),
                    ("C", 1384893m),
                    ("D", "BS5144LB/LB2886TR"),
                    ("E", 28.64m),
                    ("F", "Herat, Afghanistan"),
                    ("G", 55m),
                    ("H", 1575.2m),
                    ("I", 28.38m),
                    ("J", 0.26m),
                    ("K", 0.1m),
                    ("L", 0.16m),
                    ("M", 930m),
                    ("P", 1426.4m)),
                BuildMixedRow(4,
                    ("A", 2m),
                    ("B", (decimal)new DateTime(2026, 2, 3).ToOADate()),
                    ("C", 1384894m),
                    ("D", "ED7199LB/LB5069TR"),
                    ("E", 30.82m),
                    ("F", "Herat, Afghanistan"),
                    ("G", 55m),
                    ("H", 1695.1m),
                    ("I", 30.7m),
                    ("J", 0.12m),
                    ("K", 0.1m),
                    ("L", 0.02m),
                    ("M", 930m),
                    ("P", 1676.5m))));
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(truckRentWorksheet),
                SheetId = 3,
                Name = "کرایه موتر ها"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static byte[] BuildGeroyRossiStyleWorkbookBytes()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData(
                BuildTextRow(1),
                BuildTextRow(2,
                    ("A", "NO"),
                    ("B", "Date Loading"),
                    ("C", "CMR"),
                    ("D", "Wagon  - No"),
                    ("E", "Weight"),
                    ("F", "Consignee"),
                    ("G", "Distination")),
                BuildMixedRow(3,
                    ("A", 1m),
                    ("B", "2026-12-03"),
                    ("C", "259801"),
                    ("D", "5460455"),
                    ("E", 53.4m),
                    ("F", "Fawad coltd"),
                    ("G", "AmirAbad - Rozanak")),
                BuildMixedRow(4,
                    ("A", 2m),
                    ("B", "2026-12-03"),
                    ("C", "259802"),
                    ("D", "5461824"),
                    ("E", 53.5m),
                    ("F", "Fawad coltd"),
                    ("G", "AmirAbad - Rozanak"))));

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "بارگیری از امیرآباد به روزنک"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Row BuildTextRow(uint rowIndex, params (string Column, string Value)[] values)
    {
        var row = new Row { RowIndex = rowIndex };
        foreach (var value in values)
        {
            row.Append(new Cell
            {
                CellReference = $"{value.Column}{rowIndex}",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value.Value))
            });
        }

        return row;
    }

    private static Row BuildMixedRow(uint rowIndex, params (string Column, object Value)[] values)
    {
        var row = new Row { RowIndex = rowIndex };
        foreach (var value in values)
        {
            row.Append(value.Value switch
            {
                decimal number => new Cell
                {
                    CellReference = $"{value.Column}{rowIndex}",
                    CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture))
                },
                string text => new Cell
                {
                    CellReference = $"{value.Column}{rowIndex}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(text))
                },
                _ => new Cell
                {
                    CellReference = $"{value.Column}{rowIndex}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(value.Value.ToString() ?? string.Empty))
                }
            });
        }

        return row;
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }

    private sealed class RecordingAssetRentPostingService : IAssetRentPostingService
    {
        public int PostCalls { get; private set; }
        public AssetRentTransaction? LastRent { get; private set; }
        public CurrencyConversionResult? LastConversion { get; private set; }

        public Task<AssetRentPostingResult> PostAsync(
            AssetRentTransaction rent,
            CurrencyConversionResult conversion,
            string? assetCode,
            CancellationToken cancellationToken = default)
        {
            PostCalls++;
            LastRent = rent;
            LastConversion = conversion;
            return Task.FromResult(new AssetRentPostingResult(null, AssetRentPostingPolicy.SkipSystemGenerated));
        }

        public Task<AssetRentReversalResult> ReverseAsync(
            AssetRentTransaction rent,
            DateTime reversalDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AssetRentReversalResult(null, AssetRentPostingService.SkipNoFinancialPosting));
    }

}
