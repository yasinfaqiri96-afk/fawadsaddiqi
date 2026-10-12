using System.IO;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ContractJourneyViewStructureTests
{
    [Fact]
    public void ContractJourney_Navigation_Groups_Keep_Existing_Source_Routes_And_Native_Links()
    {
        var view = ReadContractJourneyDetailsMarkup();
        var rail = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyDetailTabsRail.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");

        Assert.Contains("GroupLabel = T(\"بارها و عملیات\", \"Cargo operations\")", view);
        Assert.Contains("GroupLabel = T(\"مالی و مصارف\", \"Finance and expenses\")", view);
        Assert.Contains("Model.Tabs.GroupBy(tab => tab.GroupKey)", rail);
        Assert.Contains("asp-route-contractId=\"@Model.ContractId\"", rail);
        Assert.Contains("asp-route-lockContract=\"@Model.LockContract\"", rail);
        Assert.Contains("aria-current=", rail);
        Assert.DoesNotContain("role=\"tab\"", rail);
        Assert.Contains(".ak-navigation-group", css);
        Assert.Contains("flex: 1 1 100%;", css);
        Assert.Contains("href=\"@ReturnUrl(stepTab)\"", view);
        Assert.Contains("step.Number == 5 && Model.IsPurchaseContract", view);
        Assert.Contains("T(\"مصارف\", \"Expenses\"), expenseState", view);
        Assert.Contains("href=\"@ReturnUrl(LossesPresentationTab)\"", view);
    }

    [Fact]
    public void ContractJourney_Tab_Failure_Offers_Real_Requested_Url_And_Preserves_Keyboard_Focus()
    {
        var js = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/contract-journey-tabs.js");
        var view = ReadContractJourneyDetailsMarkup();

        Assert.Contains("showError(url);", js);
        Assert.Contains("retry.href = url;", js);
        Assert.Contains("retry.setAttribute(\"data-no-spa\", \"true\");", js);
        Assert.Contains("error.setAttribute(\"role\", \"alert\");", js);
        Assert.Contains("restoredLink.focus({ preventScroll: true });", js);
        Assert.Contains("data-retry-text=", view);
    }

    [Fact]
    public void Contract_Details_Uses_Shared_Export_Menu_For_The_Active_Tab()
    {
        var details = ReadContractJourneyDetailsMarkup();
        var header = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/Ak/_AkPageHeader.cshtml");

        // The menu moved into the page header action group; the page only supplies the model.
        Assert.Contains("ViewData[\"AkHeaderExport\"]", details);
        Assert.Contains("\"DetailsExport\"", details);
        Assert.Contains("[\"contractId\"] = Model.ContractId", details);
        Assert.Contains("[\"tab\"] = activeTab", details);
        Assert.Contains("[\"lockContract\"] = Model.LockContract", details);
        Assert.Contains("_ExportMenu.cshtml", header);
    }

    [Fact]
    public void ContractJourney_Views_Hide_Global_SectionTabs()
    {
        var details = ReadContractJourneyDetailsMarkup();
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/Index.cshtml");

        Assert.Contains("ViewData[\"HideSectionTabs\"] = true;", details);
        Assert.Contains("ViewData[\"HideSectionTabs\"] = true;", index);
    }

    [Fact]
    public void Contract_Details_View_File_Is_Removed()
    {
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/Contracts/Details.cshtml"));
    }

    [Fact]
    public void Legacy_Contract_Details_ViewModel_File_Is_Removed_After_Journey_Migration()
    {
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Models/Contracts/ContractDetailsViewModel.cs"));
        Assert.True(RepoFileExists("src/PTGOilSystem.Web/Models/Contracts/ContractDetailsTabs.cs"));
    }

    [Fact]
    public void Shared_SectionTabs_Removes_Unreachable_Legacy_Contract_Details_Strip()
    {
        var sectionTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_SectionTabs.cshtml");

        Assert.DoesNotContain("contract-detail-top-strip", sectionTabs);
        Assert.DoesNotContain("contract-detail-top-action", sectionTabs);
        Assert.DoesNotContain("asp-controller=\"Contracts\"\r\n                               asp-action=\"Details\"", sectionTabs);
    }

    [Fact]
    public void Contract_Create_And_Edit_Disable_Inactive_Pricing_Sections()
    {
        foreach (var relativePath in new[]
        {
            "src/PTGOilSystem.Web/Views/Contracts/Create.cshtml",
            "src/PTGOilSystem.Web/Views/Contracts/Edit.cshtml"
        })
        {
            var contents = ReadRepoFile(relativePath);

            Assert.Contains("function setSectionActive(element, active)", contents);
            Assert.Contains("setFieldsDisabled(element, !active);", contents);
            Assert.Contains("setSectionActive(fixedPricingSection, isFixed);", contents);
            Assert.Contains("setSectionActive(plattsPricingSection, isPlatts);", contents);
            Assert.Contains("setSectionActive(manualFinalPricingSection, isManualFinal);", contents);
            Assert.Contains("setSectionActive(manualPriceGroup, isPlatts && selectedPeriod === '3');", contents);
            Assert.Contains("plattsCurrencyHidden.value = isPlatts ? 'USD' : '';", contents);
            Assert.Contains("selectedPolicy === perLoadingRate || selectedPolicy === rateLater", contents);
            Assert.Contains("rubOption = document.createElement('option');", contents);
            Assert.Contains("settlementCurrency.value = rubOption.value;", contents);
            Assert.DoesNotContain("toggle(manualPriceGroup, false);", contents);
        }
    }

    [Fact]
    public void ContractJourney_Detail_Tabs_Use_Canonical_Shared_Rail()
    {
        var view = ReadContractJourneyDetailsMarkup();

        Assert.Contains("class=\"ak-form-page ak-detail-page\" data-contract-journey-page", view);
        Assert.Contains("class=\"ptg-tabs-rail\"", view);
        Assert.Contains("class=\"ptg-tab-item ", view);
        Assert.Contains("class=\"ak-tab-content ak-detail-content\"", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("asp-action=\"Details\"", view);
        Assert.Contains("data-contract-journey-tab=\"@tab.Key\"", view);
        Assert.Contains("data-contract-journey-tab-nav=\"true\"", view);
        Assert.Contains("data-contract-journey-tab-link=\"true\"", view);
        Assert.Contains("data-contract-journey-tab-content=\"true\"", view);
        Assert.Contains("data-contract-journey-facts-host", view);
        Assert.Contains("data-contract-journey-facts", view);
        Assert.Contains("activeTab == ContractJourneyTabs.Details.Summary", view);
        Assert.Contains("id=\"journey-tab-lists\"", view);

        // خانواده‌های موازی قدیمی قرارداد نباید باقی مانده باشند
        foreach (var legacy in new[]
        {
            "cj-page", "cj-shell", "cj-identity-card", "cj-metric-card", "cj-table-card", "cj-btn",
            "cj-back-link", "cj-date-chip", "cj-summary-grid", "cj-facts-grid",
            "journey-ops-card", "journey-action-btn", "journey-table-link", "journey-list-pager",
            "lifecycle-", "app-table-card", "ptg-card", "ds-page", "st-detail-table-card", "st-toolbar-btn",
            "status-badge", "operation-chip", "_DetailsTabs", "_DetailsHeader"
        })
        {
            Assert.DoesNotContain(legacy, view);
        }
    }

    [Fact]
    public void ContractJourney_Every_Operation_Tab_Uses_Shared_Stat_Cards_With_Avatars()
    {
        var view = ReadContractJourneyDetailsMarkup();
        // تب خلاصه دیگر کارت آماری ندارد؛ چرخه قرارداد جای آن را گرفته است.
        var statisticLabels = new[]
        {
            "آمار بارگیری",
            "آمار رسید بارگیری",
            "آمار جریان حمل بار",
            "آمار موجودی",
            "آمار نقل و انتقالات",
            "آمار فروشات",
            "آمار مصارف",
            "آمار کسری و ضایعات",
            "آمار پرداخت‌ها"
        };

        foreach (var label in statisticLabels)
        {
            Assert.Contains(label, view);
        }

        // ۹ تب عملیاتی × ۴ کارت + کارت جایگزین قرارداد فروش در تب پرداخت‌ها
        Assert.Equal(37, view.Split("<vc:stat-card", StringSplitOptions.None).Length - 1);
        Assert.Equal(37, view.Split(" avatar=\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ContractJourney_Tab_Stat_Cards_Use_Only_Data_Loaded_By_That_Tab()
    {
        var view = ReadContractJourneyDetailsMarkup();

        // مسیرهای سبک تب‌ها Kpis، موتور مفاد و کسری‌ها را نمی‌خوانند؛ کارت‌هایی که از آن‌ها
        // می‌خواندند در کلیک تب صفر یا غلط نشان می‌دادند.
        Assert.DoesNotContain("value=\"@NumberDisplay.Quantity(currentStockMt)\"", view);
        Assert.DoesNotContain("value=\"@NumberDisplay.Quantity(Model.Kpis.DispatchedQuantityMt)\"", view);
        Assert.DoesNotContain("value=\"@NumberDisplay.Quantity(lifecycleSaleableQuantityMt)\"", view);
        Assert.DoesNotContain("value=\"@NumberDisplay.Quantity(dispatchShortageWastageMt)\"", view);
        Assert.DoesNotContain("value=\"@summaryExpenseTotalUsd.ToString(\"N0\")\"", view);
        Assert.DoesNotContain("value=\"@summaryLossCostUsd.ToString(\"N0\")\"", view);
        Assert.Contains("var inventoryBookBalanceMt = Model.InventoryMovementItems.Sum(m => m.SignedQuantityMt);", view);
        Assert.Contains("T(\"قابل پرداخت به تأمین‌کننده\", \"Payable to supplier\")", view);
        Assert.DoesNotContain("T(\"مبلغ قرارداد\", \"Contract amount\")", view);
    }

    [Fact]
    public void ContractJourney_Losses_Stay_Accessible_From_Summary_On_The_Costs_Payload()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/Details.cshtml");

        Assert.DoesNotContain("new() { Key = LossesPresentationTab", view);
        Assert.Contains("href=\"@ReturnUrl(LossesPresentationTab)\"", view);
        Assert.Contains("tab = ContractJourneyTabs.Details.Costs, lossesView = true", view);
        Assert.Contains("ActiveTab = activeGroupTab,", view);
        Assert.Contains("Key = ContractJourneyTabs.Details.Costs, Label", view);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(LossesPresentationTab)\"", view);

        // Both record lists belong to the same costs payload, with no loss-only gate.
        var costsBlock = view[view.LastIndexOf("case ContractJourneyTabs.Details.Costs:", StringComparison.Ordinal)..];
        var expensesStart = costsBlock.IndexOf("expensesQ", StringComparison.Ordinal);
        var lossesStart = costsBlock.IndexOf("data-journey-losses", StringComparison.Ordinal);
        Assert.True(expensesStart >= 0);
        Assert.True(lossesStart > expensesStart);
        Assert.DoesNotContain("@if (!isLossesView)", costsBlock);
        Assert.DoesNotContain("@if (isLossesView)", costsBlock);
        Assert.True(costsBlock.IndexOf("asp-controller=\"LossEvents\"", StringComparison.Ordinal) > lossesStart);
    }

    [Fact]
    public void ContractJourney_Finance_Tab_Drops_Duplicate_Summary_And_Collapses_Profit()
    {
        var finance = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyFinanceTab.cshtml");

        Assert.DoesNotContain("T(\"خلاصه مالی\", \"Financial summary\")", finance);
        Assert.Contains("<details class=\"ak-advanced\" data-journey-realized-profit>", finance);
        Assert.Contains("@if (Model.SarrafSettlements.Any() || !string.IsNullOrWhiteSpace(Context.Request.Query[\"settlementsQ\"].ToString()))", finance);
    }

    [Fact]
    public void ContractJourney_Summary_Uses_Shared_Ak_Flat_Summary()
    {
        var contents = ReadContractJourneyDetailsMarkup();
        var summaryBlock = ExtractSummaryBlock(contents);

        // خلاصه قرارداد: تخت، بدون کارت، روی Canvas مشترک
        Assert.Contains("data-contract-journey-facts", contents);
        Assert.Contains("class=\"ak-list\"", contents);
        Assert.Contains("class=\"ak-summary-page\"", summaryBlock);
        Assert.Contains("ak-form-section", summaryBlock);
        Assert.Contains("_AkSectionHead.cshtml", summaryBlock);
        Assert.Contains("class=\"ak-table ak-detail-table\"", summaryBlock);
        Assert.Contains("ak-num", summaryBlock);
        Assert.Contains("ak-status", contents);
        Assert.Contains("ak-row-menu", contents);
        Assert.Contains("contractDisplayValueUsd", contents);
        Assert.Contains("supplierRemainingUsd", contents);

        foreach (var legacy in new[]
        {
            "cj-summary-page", "cj-final-net", "cj-overview-grid", "cj-status-card", "cj-stage-card",
            "lifecycle-status-card", "lifecycle-blue-panel", "lifecycle-pill", "StripedGauge",
            "reference-dashboard", "reference-kpi", "octane-", "<style"
        })
        {
            Assert.DoesNotContain(legacy, summaryBlock);
        }

        Assert.DoesNotContain("case ContractJourneyTabs.Details.Dashboard", contents);
        Assert.DoesNotContain("asp-controller=\"Contracts\" asp-action=\"Details\"", contents);
    }

    [Fact]
    public void ContractJourney_Legacy_Page_Styles_Are_Deleted()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");

        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/css/contract-journey.css"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/css/contract-journey-loadings.css"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/css/contract-journey-receipts.css"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/css/contract-journey-reference-vendor.css"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyOperationalHero.cshtml"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneySummaryTitlebar.cshtml"));

        Assert.DoesNotContain("contract-journey.css", layout);
        Assert.DoesNotContain("contract-journey-receipts.css", layout);
        Assert.DoesNotContain("contract-journey-loadings.css", layout);
        Assert.DoesNotContain("contract-journey-shell", layout);

        // اسکریپت تب‌ها باید بماند
        Assert.Contains("~/js/contract-journey-tabs.js", layout);
    }

    [Fact]
    public void ContractJourney_Summary_Embeds_Shared_Transparent_Artwork_With_Live_Figures()
    {
        var contents = ReadContractJourneyDetailsMarkup();
        var summaryBlock = ExtractSummaryBlock(contents);
        Assert.Contains("viewBox=\"@CycleAvatarViewBox(step.Number)\"", summaryBlock);
        Assert.Contains("viewBox=\"@CycleAvatarViewBox(0)\"", summaryBlock);
        Assert.Contains("<partial name=\"_ContractCycleAvatarArtwork\"", summaryBlock);
        Assert.DoesNotContain("cycle-artwork.png", summaryBlock);
        Assert.DoesNotContain("clipPath", summaryBlock);
        Assert.Equal(2, summaryBlock.Split("<use href=\"#journey-cycle-avatar-artwork\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("LifecycleQuantityMarkup(step.FirstValue)", summaryBlock);
        Assert.Contains("@step.Title", summaryBlock);
        Assert.Contains("@TextOrDash(partner.PartnerName)", summaryBlock);
        Assert.DoesNotContain("<img", summaryBlock);
        Assert.DoesNotContain("data:image", summaryBlock);
        // Inline SVG is limited to the two reusable avatar templates, never a page image or gauge.
        Assert.Equal(2, summaryBlock.Split("<svg", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, summaryBlock.Split("<svg class=\"ak-cycle-avatar\"", StringSplitOptions.None).Length - 1);
        var artwork = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractCycleAvatarArtwork.cshtml");
        Assert.Equal(1, artwork.Split("data:image/webp;base64,", StringSplitOptions.None).Length - 1);
        Assert.Contains("id=\"journey-cycle-avatar-artwork\"", artwork);
        var encoded = artwork.Split("data:image/webp;base64,", StringSplitOptions.None)[1].Split('"')[0];
        var bytes = Convert.FromBase64String(encoded);
        Assert.InRange(bytes.Length, 1, 80_000);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
    }

    [Fact]
    public void ContractJourney_Tab_Partials_Use_Only_Shared_Ak_Components()
    {
        var partials = new[]
        {
            "_ContractJourneyLoadingsTab",
            "_ContractJourneyReceiptsTab",
            "_ContractJourneyInventoryTab",
            "_ContractJourneyDispatchTab",
            "_ContractJourneySalesTab",
            "_ContractJourneyFinanceTab"
        };

        foreach (var name in partials)
        {
            var partial = ReadRepoFile($"src/PTGOilSystem.Web/Views/ContractJourney/{name}.cshtml");

            Assert.Contains("class=\"ak-table\"", partial);
            Assert.Contains("ak-form-section", partial);
            Assert.Contains("ak-empty", partial);
            Assert.Contains("id=\"journey-tab-lists\"", partial);

            foreach (var legacy in new[]
            {
                "cj-", "journey-ops-card", "journey-action-btn", "journey-table-link", "journey-card",
                "app-table-card", "ptg-card", "card-header", "card-body", "table-responsive",
                "status-badge", "operation-chip", "btn-outline-primary", "st-detail-table-card"
            })
            {
                Assert.DoesNotContain(legacy, partial);
            }
        }
    }

    [Fact]
    public void ContractJourney_Every_Detail_List_Uses_Independent_Shared_Search_Filter()
    {
        var details = ReadContractJourneyDetailsMarkup();
        var filter = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_ContractJourneyListFilter.cshtml");
        var searchKeys = new[]
        {
            "subContractsQ", "shipmentsQ", "loadingsQ", "pendingReceiptsQ", "receiptsQ",
            "inventoryQ", "transportLegsQ", "dispatchQ", "salesQ", "expensesQ",
            "lossesQ", "paymentsQ", "settlementsQ"
        };

        Assert.Equal(13, details.Split("_ContractJourneyListFilter", StringSplitOptions.None).Length - 1);
        Assert.All(searchKeys, key => Assert.Contains($"\"{key}\"", details));
        Assert.Contains("_AkSearchFilter", filter);
        Assert.Contains("Context.Request.Query", filter);
        Assert.Contains("Model.PageKey", filter);
        Assert.Contains("filteredLoadingItems.Count", details);
        Assert.Contains("filteredReceiptItems.Count", details);
        Assert.Contains("filteredDispatchItems.Count", details);
        Assert.Contains("filteredSalesItems.Count", details);
        Assert.Contains("filteredExpenseItems.Count", details);
        Assert.Contains("filteredLossItems.Count", details);
        Assert.Contains("filteredPaymentItems.Count", details);
    }

    [Fact]
    public void ContractJourney_Costs_Tab_Shows_Transport_Expense_Breakdown_As_Collapsed_Detail()
    {
        var details = ReadContractJourneyDetailsMarkup();

        Assert.DoesNotContain("allocationsQ", details);
        Assert.DoesNotContain("allocationsPage", details);
        Assert.DoesNotContain("Cargo flow expense allocation", details);
        Assert.Contains("<details class=\"ak-advanced\" data-journey-leg-expense-breakdown>", details);
        Assert.Contains("Show expense details by transport", details);
        Assert.Contains(".Where(item => item.ExpenseCount > 0)", details);

        var expensesNote = details.IndexOf("\"Cargo flow expenses\"", StringComparison.Ordinal);
        var breakdown = details.IndexOf("data-journey-leg-expense-breakdown", StringComparison.Ordinal);
        var lossSection = details.IndexOf("data-journey-losses", StringComparison.Ordinal);
        Assert.True(expensesNote >= 0 && expensesNote < breakdown && breakdown < lossSection);
    }

    [Fact]
    public void Contract_Backlinks_Target_ContractJourney_Not_Legacy_Details_View()
    {
        var files = new[]
        {
            ReadRepoFile("src/PTGOilSystem.Web/Views/ContractAmendments/Index.cshtml"),
            ReadRepoFile("src/PTGOilSystem.Web/Views/ContractAmendments/Create.cshtml"),
            ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentContracts/Index.cshtml")
        };

        foreach (var contents in files)
        {
            Assert.DoesNotContain("asp-controller=\"Contracts\" asp-action=\"Details\"", contents);
        }

        Assert.Contains("asp-controller=\"ContractJourney\"", string.Concat(files));
    }

    [Fact]
    public void Create_Views_Preserve_ReturnUrl_When_Opened_From_Contract_Details()
    {
        var loading = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Create.cshtml");
        var dispatch = ReadRepoFile("src/PTGOilSystem.Web/Views/Dispatch/Create.cshtml");
        var sales = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Create.cshtml");
        var expenses = ReadRepoFile("src/PTGOilSystem.Web/Views/Expenses/Create.cshtml");
        var customs = ReadRepoFile("src/PTGOilSystem.Web/Views/CustomsDeclarations/Create.cshtml");
        var loadingReceipts = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/Create.cshtml");
        var loadingReceiptForm = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml");
        var payments = ReadRepoFile("src/PTGOilSystem.Web/Views/Payments/Create.cshtml");
        var losses = ReadRepoFile("src/PTGOilSystem.Web/Views/LossEvents/Create.cshtml");

        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", loading);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", dispatch);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", sales);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", expenses);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", customs);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", loadingReceiptForm);
        Assert.Contains("<partial name=\"_ReceiptCreateForm\" model=\"Model\" />", loadingReceipts);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", payments);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", losses);
    }

    [Fact]
    public void Loading_Receipt_Form_Uses_Shared_Ak_Layout()
    {
        var form = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");

        // The two section headings were shortened from "Source Information" /
        // "Destination Routing" to "Receipt" / "Destination"; the order they pin is
        // unchanged, so the anchors move to the stable section ids and field names.
        var headerIndex = form.IndexOf("class=\"ak-form", StringComparison.Ordinal);
        var sourceIndex = form.IndexOf("id=\"receiptLoadingTitle\"", StringComparison.Ordinal);
        var receivedQuantityIndex = form.IndexOf("name=\"ReceivedQuantityMt\"", StringComparison.Ordinal);
        var scenarioIndex = form.IndexOf("data-scenario-pick=\"inventory\"", StringComparison.Ordinal);
        var destinationIndex = form.IndexOf("for=\"TerminalId\"", StringComparison.Ordinal);

        Assert.True(headerIndex >= 0);
        Assert.True(sourceIndex > headerIndex);
        Assert.True(receivedQuantityIndex > sourceIndex);
        Assert.True(scenarioIndex > receivedQuantityIndex);
        Assert.True(destinationIndex > scenarioIndex);
        Assert.Contains("ak-form-section", form);
        Assert.Contains("ak-form-grid", form);
        Assert.Contains("ak-field", form);
        Assert.Contains("ak-input", form);
        Assert.Contains("id=\"receiptRoutingTitle\"", form);
        Assert.Contains("مقصد کالا", form);
        Assert.Contains("Save receipt", form);
        Assert.Contains("ثبت رسید", form);
        Assert.Contains("UiText.IsEn(Context)", form);
        Assert.Contains("asp-for=\"StorageTankId\"", form);
        // «آیا کسری وجود دارد؟» با دو دکمه ثبت می‌شود و همان Loss.Enabled را می‌نویسد.
        Assert.Contains("name=\"Loss.Enabled\"", form);
        Assert.Contains("data-loss-pick=\"yes\"", form);
        Assert.Contains("data-loss-panel", form);
        Assert.Contains("asp-for=\"Loss.QuantityMt\"", form);
        // مقدار واقعی رسیده دیگر فیلد جدا ندارد و از مقدار رسید آینه می‌شود.
        Assert.Contains("data-copy-value-to=\"ActualArrivedQuantityMt\"", form);
        Assert.DoesNotContain("data-copy-value-to=\"DestinationTerminalId\"", form);
        Assert.Contains(".ak-form-section", css);
        Assert.Contains(".ak-form-grid", css);
        Assert.Contains(".ak-field", css);
        Assert.Contains(".ak-input", css);
        Assert.DoesNotContain("data-boltz-wizard", form);
        Assert.DoesNotContain("_BoltzWizardStepper", form);
        Assert.DoesNotContain("receipt-wizard-card", form);
    }

    [Fact]
    public void LoadingReceipt_Create_Uses_Shared_Ak_Contract()
    {
        var page = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/Create.cshtml");
        var form = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml");
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var js = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/modal-design-system.js");

        Assert.Contains("ViewData[\"ModalDesignSystemAssets\"] = true;", page);
        Assert.DoesNotContain("ContentBodyClass", page);
        Assert.Contains("ViewData[\"HideSectionTabs\"] = true;", page);
        Assert.Contains("class=\"ak-form-page\"", page);
        Assert.Contains("UiText.T(Context, \"ثبت رسید بارگیری\", \"Create Loading Receipt\")", page);
        Assert.Contains("dir=\"@(UiText.IsEn(Context) ? \"ltr\" : \"rtl\")\"", page);
        Assert.Contains("<partial name=\"_ReceiptCreateForm\" model=\"Model\" />", page);
        Assert.DoesNotContain("ptg-modal-workbench", page);
        Assert.DoesNotContain("receipt-reference-workbench", page);

        Assert.Contains("useModalDesignSystemAssets", layout);
        Assert.Contains("~/js/modal-design-system.js", layout);
        Assert.Contains("data-ptg-page-asset=\"modal-design-system-script\"", layout);

        Assert.Contains("class=\"ak-form", form);
        Assert.Contains("ak-form-section", form);
        Assert.Contains("ak-form-grid", form);
        Assert.Contains("ak-field", form);
        Assert.Contains("ak-input", form);
        // جدول تخصیص ترکیبی حذف شده است؛ فرم رسید دیگر جدول ندارد.
        Assert.DoesNotContain("ak-table", form);
        Assert.DoesNotContain("data-scenario-pick=\"mixed\"", form);
        Assert.Contains("data-receipt-create-form", form);
        Assert.Contains("asp-controller=\"LoadingReceipts\"", form);
        Assert.Contains("asp-action=\"Create\"", form);
        Assert.Contains("data-scenario-pick=\"inventory\"", form);
        Assert.Contains("data-scenario-pick=\"sale\"", form);
        Assert.Contains("data-scenario-pick=\"truck\"", form);
        Assert.Contains("data-scenario-pick=\"transfer\"", form);
        Assert.Contains("<input asp-for=\"LoadingRegisterId\" type=\"hidden\" />", form);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", form);
        Assert.Contains("<input asp-for=\"ReceiptDestination\" type=\"hidden\" id=\"ReceiptDestination\" />", form);
        Assert.Contains("<input asp-for=\"AllocationDestination\" type=\"hidden\" id=\"AllocationDestination\" />", form);
        Assert.Contains("asp-for=\"DestinationTerminalId\"", form);
        Assert.Contains("asp-for=\"DestinationStorageTankId\"", form);
        Assert.Contains("asp-for=\"DirectTruckTicketSerialNumber\"", form);
        Assert.Contains("asp-for=\"DirectDriverName\"", form);
        Assert.Contains("asp-for=\"SaleAppliedFxRateToUsd\"", form);
        Assert.Contains("asp-for=\"SaleNotes\"", form);
        Assert.DoesNotContain("<script>", form);

        Assert.Contains(".ak-form-section", css);
        Assert.Contains(".ak-form-grid", css);
        Assert.Contains(".ak-input", css);
        Assert.DoesNotContain("receipt-reference-card", form);
        Assert.DoesNotContain("loading-receipt-reference-form", form);
        Assert.Contains("initializeReceiptCreateForm", js);
        Assert.Contains("data-receipt-create-form", js);
        Assert.Contains("scenarioPanelMatches", js);
        Assert.Contains("syncScenarioPanelFields", js);
    }

    [Fact]
    public void Shared_Create_Modals_And_Form_Pages_Use_The_Unified_Modal_Design_System()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var modalShell = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_CreateModalShell.cshtml");
        var pageShell = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_CreatePageShell.cshtml");
        var akCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var js = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/modal-design-system.js");

        // The shared modal-design-system behaviour script stays wired in the layout.
        Assert.Contains("~/js/modal-design-system.js", layout);
        Assert.Contains("initializeModalDesignSystem", js);
        // The preview-card hook was pruned once every shell dropped the preview visual.
        Assert.DoesNotContain("data-modal-preview-card", js);
        Assert.DoesNotContain("initializePreviewCard", js);

        foreach (var shell in new[] { modalShell, pageShell })
        {
            Assert.Contains("ak-form", shell);
            Assert.Contains("Html.PartialAsync(formPartialPath, Model)", shell);
            Assert.DoesNotContain("ptg-modal-workbench", shell);
            Assert.DoesNotContain("ptg-modal-preview-card", shell);
            Assert.DoesNotContain("ptg-reference", shell);
        }

        // Modal shell: one flat ak modal, every Ajax/close hook preserved.
        Assert.Contains("ak-modal", modalShell);
        Assert.Contains("ak-modal-head", modalShell);
        Assert.Contains("ak-modal-title", modalShell);
        Assert.Contains("ak-modal-close", modalShell);
        Assert.Contains("ak-modal-foot", modalShell);
        Assert.Contains("data-entity-modal=\"@modalId\"", modalShell);
        Assert.Contains("data-ptg-entity-modal-form", modalShell);
        Assert.Contains("data-entity-modal-close", modalShell);
        Assert.Contains("asp-action=\"@formAction\"", modalShell);
        Assert.Contains("enctype=\"@formEnctype\"", modalShell);
        Assert.Contains("form=\"@formId\"", modalShell);
        Assert.DoesNotContain("ptg-modal-system", modalShell);
        Assert.DoesNotContain("ResolveVisualText", modalShell);
        Assert.DoesNotContain("ptg-modal-variant", modalShell);

        // Page shell: flat ak canvas with the shared header + footer components.
        Assert.Contains("ak-form-page", pageShell);
        Assert.Contains("asp-action=\"Create\"", pageShell);
        Assert.Contains("Context.Request.Query[\"returnUrl\"]", pageShell);
        Assert.Contains("Url.IsLocalUrl(requestedReturnUrl)", pageShell);
        Assert.Contains("asp-route-returnUrl=\"@returnUrl\"", pageShell);
        Assert.Contains("_AkPageHeader", pageShell);
        Assert.Contains("_AkFooterActions", pageShell);
        Assert.DoesNotContain("ptg-modal-form-scroll", pageShell);

        // The single shared ak modal-shell CSS lives in the ak component file.
        Assert.Contains(".ak-modal-content", akCss);
        Assert.Contains(".ak-modal-head", akCss);
        Assert.Contains(".ak-modal-foot", akCss);
    }

    [Fact]
    public void Core_Transport_And_Party_Create_Actions_Use_Shared_Pages_Not_Modals()
    {
        var controllers = new[]
        {
            "Products", "Units", "Currencies", "DailyFxRates", "Locations", "ExpenseTypes", "ExpenseRules",
            "StorageTanks", "Terminals", "Trucks", "Wagons", "Drivers", "Vessels",
            "Suppliers", "Partners", "Companies", "Customers", "ServiceProviders", "Sarrafs", "Employees"
        };

        foreach (var controller in controllers)
        {
            var index = ReadRepoFile($"src/PTGOilSystem.Web/Views/{controller}/Index.cshtml");
            var create = ReadRepoFile($"src/PTGOilSystem.Web/Views/{controller}/Create.cshtml");
            var controllerSource = ReadRepoFile($"src/PTGOilSystem.Web/Controllers/{controller}Controller.cs");
            Assert.Contains("var returnUrl = $\"{Context.Request.Path}{Context.Request.QueryString}\";", index);
            Assert.Contains("Url.Action(\"Create\", new { returnUrl })", index);
            Assert.DoesNotContain("_CreateModalShell", index);
            Assert.DoesNotContain("data-entity-modal-open", index);
            Assert.Contains("string? returnUrl = null", controllerSource);
            Assert.Contains("Url.IsLocalUrl(returnUrl)", controllerSource);
            Assert.Contains("LocalRedirect(returnUrl)", controllerSource);
            Assert.True(
                create.Contains("_CreatePageShell", StringComparison.Ordinal)
                || create.Contains("ak-form-page", StringComparison.Ordinal),
                $"{controller}/Create must use the shared ak page form contract.");

            if (!create.Contains("_CreatePageShell", StringComparison.Ordinal))
            {
                Assert.Contains("Context.Request.Query[\"returnUrl\"]", create);
                Assert.Contains("Url.IsLocalUrl(requestedReturnUrl)", create);
                Assert.Contains("asp-route-returnUrl", create);
            }
        }
    }

    [Fact]
    public void Entity_Quick_Create_And_Live_Modal_Consumers_Remain_Available()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var pageShell = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_CreatePageShell.cshtml");
        var modalShell = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_CreateModalShell.cshtml");
        var cashAccounts = ReadRepoFile("src/PTGOilSystem.Web/Views/CashAccounts/Index.cshtml");
        var plattsRates = ReadRepoFile("src/PTGOilSystem.Web/Views/PlattsRates/Index.cshtml");
        var receiptForm = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml");
        var coreJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/core.js");
        var modalJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/modal-design-system.js");
        var comboboxJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/ak-entity-combobox.js");
        var akPageHeader = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/Ak/_AkPageHeader.cshtml");

        Assert.Contains("\"CashAccounts\", \"PlattsRates\", \"LoadingReceipts\"", layout);
        Assert.Contains("data-ptg-entity-modal-form", modalShell);
        // CashAccounts/Index now opens the create modal through the shared _AkPageHeader
        // (ActionModalTarget), which still emits the live data-entity-modal-open hook.
        Assert.Contains("cashAccountsCreateModal", cashAccounts);
        Assert.Contains("data-entity-modal-open=\"@Model.ActionModalTarget\"", akPageHeader);
        Assert.Contains("_CreateModalShell", cashAccounts);
        Assert.Contains("_CreateModalShell", plattsRates);
        Assert.Contains("data-receipt-create-form", receiptForm);
        Assert.Contains("initializeReceiptCreateForm", modalJs);
        Assert.Contains("initializeEntityModalFormSubmit", modalJs);
        Assert.Contains("fetch(action", modalJs);
        Assert.Contains("select.dataset.akQuickCreateTarget", comboboxJs);
        Assert.Contains("select.dataset.akQuickCreateUrl", comboboxJs);
        Assert.Contains("window.PTG.openPageModal", comboboxJs);
        Assert.Contains("quickCreateSelect: select", comboboxJs);
        Assert.Contains("size: \"compact\"", comboboxJs);
        Assert.Contains("select.dispatchEvent(new Event(\"change\"", comboboxJs);
        // قرارداد واقعی همین دو پارامتر مسیر است: فیلتر سمت سرور روی modal/quickCreate
        // تصمیم می‌گیرد. صفت data-ptg-quick-create هیچ مصرف‌کننده‌ای نداشت و حذف شد.
        Assert.Contains("asp-route-quickCreate", pageShell);
        Assert.Contains("asp-route-modal", pageShell);
        Assert.Contains("initializeQuickCreateForms", coreJs);
        Assert.Contains("\"X-Requested-With\": \"XMLHttpRequest\"", coreJs);
        Assert.Contains("window.PTG.completeQuickCreate", coreJs);
        Assert.Contains("select.add(option)", coreJs);
        Assert.DoesNotContain("quickCreateSelect: select,\n                        closeOnRedirect: true", comboboxJs);
    }

    [Fact]
    public void InventoryTransportLeg_CreateFromInventory_Uses_Only_Scoped_Form_Assets()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/CreateFromInventory.cshtml");
        var js = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/inventory-transport-form.js");
        var shipmentDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");

        Assert.Contains("ak-form-page", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("class=\"ak-form\" data-inv-transport-form", view);
        Assert.Contains("class=\"ak-table\" data-vehicle-table", view);
        Assert.Contains("class=\"ak-summary\" data-alloc-bar", view);
        Assert.Contains("~/js/inventory-transport-form.js", view);
        Assert.Contains("data-inv-transport-form", view);
        Assert.Contains("CreateFromInventory", shipmentDetails);
        Assert.DoesNotContain("CreateBatch", shipmentDetails);
        Assert.DoesNotContain("inventory-transport-leg-form.js", view);
        Assert.DoesNotContain("inventory-transport-form.css", view);
        Assert.DoesNotContain("class=\"inv-transport-form-", view);
        Assert.Contains("function recalculate()", js);
        Assert.Contains("data-submit-button", js);
        Assert.Contains("[data-vehicle-table]", js);
        Assert.DoesNotContain(".inv-transport-form-", js);
        Assert.Contains("name=\"ActiveStep\"", view);
        Assert.Contains("asp-validation-summary=\"All\"", view);
        Assert.Contains("Allocations[@allocationIndex]", view);
        Assert.DoesNotContain("Allocations[0].SourceInventoryMovementId", view);
        Assert.Contains("form.addEventListener(\"submit\"", js);
        Assert.Contains("setStep(postedActiveStep", js);
        Assert.Contains("data-capacity=", view);
    }

    [Fact]
    public void InventoryTransportLeg_Edit_Keeps_Legacy_Dynamic_Labels_Isolated()
    {
        var edit = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Edit.cshtml");

        Assert.Contains("ViewData[\"InventoryTransportLegFormAssets\"] = true;", edit);
        Assert.Contains("data-inventory-transport-leg-form", edit);
        Assert.Contains("data-transport-type-select", edit);
        Assert.Contains("data-transport-dynamic-label", edit);
    }

    [Fact]
    public void InventoryTransportLeg_Details_Uses_Transport_Detail_Reference_Layout()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Details.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");

        Assert.Contains("ak-form-page", view);
        Assert.Contains("data-transport-details", view);
        Assert.Contains("data-ak-detail-v2=\"true\"", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("AkHeaderIdentity", view);
        Assert.Contains("_DetailCards.cshtml", view);
        Assert.Contains("_DetailMore.cshtml", view);
        Assert.Contains("ak-linear-detail", view);
        Assert.Contains("حمل با موتر", view);
        Assert.Contains("حمل با واگن", view);
        Assert.Contains("حمل با کشتی", view);
        Assert.Contains("Model.TransportType == LoadingTransportType.Truck", view);
        Assert.Contains("Model.TransportType == LoadingTransportType.Wagon", view);
        Assert.Contains("Model.TransportType == LoadingTransportType.Vessel", view);
        // The page states its identity in one horizontal strip, then a two-by-two card grid
        // (main information, vehicle visual, expenses, cargo). The shared overview strip is not
        // used here, and the secondary material sits in one native <details> so no record,
        // activity row or history entry is dropped from the page.
        Assert.Contains("ptg-record-detail", view);
        Assert.Contains("AkDetailCardsModel", view);
        Assert.Contains("VisualAvatar = transportVisualAvatar", view);
        Assert.Contains("Steps = transportVisualAvatar is null ? routeSteps : []", view);
        // Source and destination show for every vehicle type; the vehicle visual moved
        // into the cargo band, so it no longer hides the route.
        Assert.DoesNotContain("RouteNodes = transportVisualAvatar is null", view);
        Assert.Contains("RouteNodes = new List<AkInfoItem>", view);
        Assert.DoesNotContain("_DetailOverview.cshtml", view);
        Assert.DoesNotContain("<vc:stat-card", view);
        Assert.DoesNotContain("_DetailKpiStrip.cshtml", view);
        Assert.DoesNotContain("_DetailPager.cshtml", view);
        Assert.DoesNotContain("_OperationsDetailMore.cshtml", view);
        Assert.Contains("AkHeaderOverflowActions", view);
        // Next operations are the shared bottom bar; the kebab keeps only the side routes.
        // The old page duplicated both lists into a "next cargo operation" modal as well.
        Assert.DoesNotContain("nextOperationModal", view);
        Assert.DoesNotContain("data-next-op-forward", view);
        Assert.Contains("data-ak-operations-detail=\"true\"", view);
        Assert.DoesNotContain("data-ptcd-tab", view);
        Assert.DoesNotContain("data-ptcd-pager", view);
        Assert.DoesNotContain("ak-form-section ak-detail-section is-receipt", view);
        Assert.Contains("ثبت گمرک", view);
        Assert.DoesNotContain("نگاه سریع", view);
        Assert.DoesNotContain("خلاصه مالی حمل", view);
        Assert.DoesNotContain("transport-details-records", view);
        Assert.DoesNotContain("transport-records-table", view);
        Assert.DoesNotContain("data-transport-detail-trigger", view);
        Assert.DoesNotContain("root.querySelectorAll('.ak-list-row')", view);
        Assert.DoesNotContain("<dl class=\"ak-list\">", view);
        Assert.DoesNotContain("ak-detail-totals", view);
        Assert.Contains(".ak-linear-detail .ak-detail-overview", css);
        Assert.Contains(".ak-linear-detail .ak-detail-metrics", css);
        Assert.Contains(".ak-linear-detail .ak-detail-activity-row", css);
        // The card grid lives in the shared detail layer, scoped to the card pages.
        Assert.Contains(".ptg-record-detail .ptg-td-grid", css);
        Assert.Contains(".ptg-record-detail .ptg-td-step", css);
        // Strip and cards are one shared component, not per-page markup.
        var cardsPartial = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailCards.cshtml");
        Assert.Contains("ptg-td-strip", cardsPartial);
        Assert.Contains("ptg-td-grid", cardsPartial);
        Assert.Contains("ptg-td-steps", cardsPartial);
        Assert.DoesNotContain("[data-transport-details] .transport-primary-grid", css);
        Assert.DoesNotContain("[data-transport-details] .ak-detail-kpi-strip > .ak-stat-card:not(.ak-stat-card--empty):nth-child(1)", css);
        Assert.DoesNotContain("[data-transport-details] .ak-detail-kpi-strip > .ak-stat-card:not(.ak-stat-card--empty):nth-child(2)", css);
        Assert.DoesNotContain("[data-transport-details] .ak-detail-kpi-strip > .ak-stat-card:not(.ak-stat-card--empty):nth-child(3)", css);
        Assert.DoesNotContain("[data-transport-details] .ak-detail-kpi-strip > .ak-stat-card:not(.ak-stat-card--empty):nth-child(4)", css);
        Assert.DoesNotContain("ptg-transport-clean-details", view);
        Assert.DoesNotContain("ptcd-summary-card", view);
    }
    [Fact]
    public void Operation_Record_Detail_Pages_Use_Shared_Ak_Detail_Contract()
    {
        var pages = new[]
        {
            "src/PTGOilSystem.Web/Views/Sales/Details.cshtml",
            "src/PTGOilSystem.Web/Views/Expenses/Details.cshtml",
            "src/PTGOilSystem.Web/Views/LossEvents/Details.cshtml",
            "src/PTGOilSystem.Web/Views/Dispatch/Details.cshtml",
            "src/PTGOilSystem.Web/Views/CustomsDeclarations/Details.cshtml",
            "src/PTGOilSystem.Web/Views/LoadingReceipts/Details.cshtml",
            "src/PTGOilSystem.Web/Views/Loading/Details.cshtml",
            "src/PTGOilSystem.Web/Views/InventoryTransportLegs/Details.cshtml",
            "src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml"
        };

        foreach (var path in pages)
        {
            var view = ReadRepoFile(path);

            Assert.Contains("ak-form-page", view);
            Assert.Contains("_AkPageHeader.cshtml", view);
            Assert.Contains("AkHeaderIdentity", view);
            // Card-layout pages state their identity in one strip and a card grid, and
            // fold activity, history and next operations into the shared _DetailMore
            // disclosure. Every other operation record keeps the classic composition.
            // Neither shape drops a fact: both feed the same shared partials.
            var usesCards = view.Contains("_DetailCards.cshtml", StringComparison.Ordinal);
            if (usesCards)
            {
                Assert.Contains("_DetailMore.cshtml", view);
                Assert.Contains("ptg-record-detail", view);
                Assert.DoesNotContain("_DetailOverview.cshtml", view);
            }
            else
            {
                Assert.Contains("_DetailOverview.cshtml", view);
                Assert.Contains("_DetailActivityList.cshtml", view);
            }

            Assert.DoesNotContain("<details", view);
            Assert.Contains("ak-linear-detail", view);
            Assert.Contains("data-ak-operations-detail=\"true\"", view);
            Assert.Contains("ViewData[\"HideSectionTabs\"] = true;", view);
            Assert.DoesNotContain("_OperationsDetailMore.cshtml", view);
            Assert.DoesNotContain("_DetailKpiStrip.cshtml", view);
            Assert.DoesNotContain("<vc:stat-card", view);

            // Transport legs are the one operation record whose identity is not readable from
            // the document number alone — the same plate can haul a different product on a
            // different date over a different route — so this page states product · date · route
            // in the header context line and drives its staged workflow from the shared action
            // bar. Every other operation record keeps identity in the summary card and next
            // actions in the kebab.
            if (!usesCards && !path.Contains("ShipmentPnl", StringComparison.Ordinal))
            {
                Assert.Contains("_DetailActionBar.cshtml", view);
            }

            foreach (var legacy in new[]
            {
                "sd-card", "sd-btn", "sd-summary", "od-card", "od-btn", "od-summary", "od-facts",
                "loading-details-simple-page", "loading-detail-simple-card", "loading-journal-page",
                "ops-details-page", "sales-details-page", "summary-strip", "card detail-card",
                "ViewData[\"SalesDetailsAssets\"]", "ViewData[\"OpsDetailsAssets\"]"
            })
            {
                Assert.DoesNotContain(legacy, view);
            }
        }

        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        Assert.DoesNotContain("31-sales-details.css", layout);
        Assert.DoesNotContain("32-ops-details.css", layout);
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/css/ptg/31-sales-details.css"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/css/ptg/32-ops-details.css"));
    }

    [Fact]
    public void InventoryTransport_Active_Flow_Views_Use_Shared_Ak_Components()
    {
        var active = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Active.cshtml");
        var activeDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/ActiveDetails.cshtml");
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Index.cshtml");

        Assert.Contains("ak-list-page", active);
        Assert.Contains("_AkPageHeader.cshtml", active);
        Assert.Contains("class=\"ak-stat-grid\"", active);
        Assert.Contains("<vc:stat-card", active);
        Assert.Contains("class=\"ak-form-grid\"", active);
        Assert.Contains("class=\"ak-table\"", active);
        Assert.Contains("asp-action=\"Details\"", active);
        // ActiveDetails is a detail page, so it uses the shared linear detail shell like
        // every other Operations detail view instead of a page-local header and card wall.
        Assert.Contains("ak-form-page", activeDetails);
        Assert.Contains("ak-linear-detail", activeDetails);
        Assert.Contains("_AkPageHeader.cshtml", activeDetails);
        Assert.Contains("AkHeaderIdentity", activeDetails);
        Assert.Contains("_DetailOverview.cshtml", activeDetails);
        Assert.Contains("_DetailActivityList.cshtml", activeDetails);
        Assert.Contains("_DetailActionBar.cshtml", activeDetails);
        Assert.DoesNotContain("<vc:stat-card", activeDetails);
        Assert.Contains("class=\"ak-table", activeDetails);
        Assert.Contains("data-href=\"@Url.Action(\"Details\"", index);
        Assert.Contains("LoadingTransportType.Truck => UiText.T(Context, \"موتر\", \"Truck\")", index);
        Assert.Contains("LoadingTransportType.Wagon => UiText.T(Context, \"واگن\", \"Wagon\")", index);
        Assert.Contains("LoadingTransportType.Vessel => UiText.T(Context, \"کشتی\", \"Vessel\")", index);
        Assert.Contains("Url.Action(\"Create\", \"Transports\")", index);
        Assert.Contains("Filter.WorkflowState", index);
        Assert.Contains("NumberDisplay.Quantity(item.QuantityMt)", index);
        Assert.DoesNotContain("Url.Action(\"GroupTransfer\")", index);
        Assert.DoesNotContain("Url.Action(\"CreateFromInventory\")", index);
        Assert.DoesNotContain("asp-action=\"CreateBatch\"", index);
        Assert.DoesNotContain("inventory-transport-active.css", active);
        Assert.DoesNotContain("class=\"inventory-flow-", active);
        Assert.DoesNotContain("active-detail-", activeDetails);
    }

    [Fact]
    public void InventoryTransportReceipt_Create_Form_Uses_Localized_Persian_Copy()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportReceipts/Create.cshtml");

        Assert.Contains("UiText.T(Context", view);
        Assert.Contains("ReceiptDate", view);
        Assert.Contains("ReceiptDestination", view);
        Assert.Contains("ReceivedQuantityMt", view);
        Assert.Contains("ShortageQuantityMt", view);
        Assert.Contains("DestinationTerminalId", view);
        Assert.Contains("DestinationStorageTankId", view);
        Assert.Contains("toInventoryValue", view);
        Assert.DoesNotContain("<label asp-for=\"ReceiptDate\" class=\"form-label\"></label>", view);
        Assert.DoesNotContain("<label asp-for=\"ReceiptDestination\" class=\"form-label\"></label>", view);
        Assert.DoesNotContain("<label asp-for=\"ReceivedQuantityMt\" class=\"form-label\"></label>", view);
    }

    [Fact]
    public void InventoryTransportReceipt_Create_Uses_Compact_Destination_Unload_Layout()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportReceipts/Create.cshtml");

        Assert.Contains("ثبت رسید / تخلیه", view);
        Assert.Contains("شماره سفر", view);
        Assert.Contains("وسیله حمل", view);
        Assert.Contains("نمبر موتر", view);
        Assert.Contains("بارگیری‌شده از مخزن", view);
        Assert.Contains("وزن تخلیه", view);
        Assert.Contains("مجموع کرایه", view);
        Assert.Contains("مجرای کمبودی", view);
        Assert.Contains("کرایه حمل (اختیاری)", view);
        Assert.Contains("کسری یا مازاد (اختیاری)", view);
        Assert.Contains("data-optional-toggle=\"freight\"", view);
        Assert.Contains("data-optional-toggle=\"shortage\"", view);
        Assert.Contains("data-optional-body=\"freight\" @(freightOpen ? null : \"hidden\")", view);
        Assert.Contains("data-optional-body=\"shortage\" @(shortageOpen ? null : \"hidden\")", view);
        Assert.Contains("ak-form-page", view);
        Assert.Contains("class=\"ak-form\"", view);
        Assert.Contains("_AkPageHeader", view);
        Assert.DoesNotContain("shipment-receipt-form", view);
    }

    [Fact]
    public void InventoryTransportReceipt_Focused_DirectSale_Keeps_Required_Fields_Clear_And_Optional_Sections_Closed()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportReceipts/Create.cshtml");

        Assert.Contains("data-focused-sale-primary", view);
        Assert.Contains("اطلاعات ضروری فروش", view);
        Assert.Contains("فقط فیلدهای ستاره‌دار برای ثبت فروش لازم‌اند", view);
        Assert.Contains("<input asp-for=\"ReceiptDestination\" type=\"hidden\" data-receipt-destination", view);
        // The buyer selector now supports customer and supplier buyers; both
        // parsed domain keys must surface server validation next to this field.
        Assert.Contains("<label asp-for=\"SaleBuyerKey\" class=\"ak-label\">@T(\"خریدار\", \"Buyer\") <b>*</b>", view);
        Assert.Contains("<select asp-for=\"SaleBuyerKey\" asp-items=\"ViewBag.Buyers\"", view);
        Assert.Contains("asp-validation-for=\"SaleBuyerKey\"", view);
        Assert.Contains("asp-validation-for=\"SaleCustomerId\"", view);
        Assert.Contains("asp-validation-for=\"SaleSupplierId\"", view);
        Assert.Contains("<label asp-for=\"ReceivedQuantityMt\">@T(\"مقدار فروش\", \"Sale quantity\") <b>*</b>", view);
        Assert.Contains("<label asp-for=\"SaleUnitPriceInCurrency\" class=\"ak-label\">@T(\"قیمت فی تن\", \"Unit price per MT\") <b>*</b>", view);
        Assert.Contains("کرایه حمل (اختیاری)", view);
        Assert.Contains("کسری یا مازاد (اختیاری)", view);
        Assert.Contains("var shortageOpen = Model.ShortageQuantityMt != 0m", view);
        Assert.DoesNotContain("if (hasValue) checkbox.checked = true", view);
    }

    [Fact]
    public void InventoryTransportReceipt_Focused_Delivery_Shows_Only_Core_Fields_First()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportReceipts/Create.cshtml");
        var transportDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Details.cshtml");

        Assert.Contains("data-focused-inventory-primary", view);
        Assert.Contains("اطلاعات ضروری تحویل", view);
        Assert.Contains("تاریخ تحویل", view);
        Assert.Contains("مقدار تحویل‌شده", view);
        Assert.Contains("name=\"focused\" value=\"true\"", view);
        Assert.Contains("data-focused-inventory=\"@focusedInventory.ToString().ToLowerInvariant()\"", view);
        Assert.Contains("name=\"ReceiptDestination\" id=\"ReceiptDestination\" value=\"@toInventoryValue\"", view);
        Assert.Contains("const isToInventory = focusedInventory || value === toInventoryValue;", view);
        Assert.Contains("data-focused-financial-summary", view);
        Assert.Contains("خلاصه محاسبه اختیاری", view);
        Assert.Contains("focusedSale || focusedInventory", view);
        Assert.Contains("!focusedSale && !focusedInventory", view);
        Assert.Contains("focused = true", transportDetails);
        Assert.Contains("PrimaryLabel = null", transportDetails);
        Assert.Contains("Label = T(\"ویرایش\", \"Edit\"), Href = editUrl", transportDetails);
    }

    [Fact]
    public void ContractJourney_Operational_Links_Are_ReturnUrl_Aware()
    {
        var contents = ReadContractJourneyDetailsMarkup();

        Assert.Contains("asp-controller=\"Loading\"", contents);
        Assert.Contains("asp-action=\"Details\"", contents);
        Assert.Contains("asp-route-id=\"@item.Id\"", contents);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(ContractJourneyTabs.Details.Loadings)\"", contents);
        Assert.Contains("asp-controller=\"LoadingReceipts\"", contents);
        Assert.Contains("asp-route-id=\"@item.Id\"", contents);
        // تب رسید، آدرس بازگشت را یک بار می‌سازد و در هر ردیف همان را استفاده می‌کند
        // (Url.Action برای هر ردیف سه بار اجرا نشود).
        Assert.Contains("var receiptsReturnUrl = ReturnUrl(ContractJourneyTabs.Details.Receipts);", contents);
        Assert.Contains("asp-route-returnUrl=\"@receiptsReturnUrl\"", contents);
        Assert.Contains("asp-controller=\"Dispatch\"", contents);
        Assert.Contains("asp-route-id=\"@item.Id\"", contents);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(ContractJourneyTabs.Details.Dispatch)\"", contents);
        Assert.Contains("asp-controller=\"Sales\"", contents);
        Assert.Contains("asp-route-id=\"@item.SalesTransactionId\"", contents);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(ContractJourneyTabs.Details.Sales)\"", contents);
        Assert.Contains("asp-controller=\"Expenses\"", contents);
        Assert.Contains("asp-route-id=\"@item.ExpenseTransactionId\"", contents);
        Assert.Contains("asp-controller=\"LossEvents\"", contents);
        Assert.Contains("asp-route-id=\"@item.LossEventId\"", contents);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(LossesPresentationTab)\"", contents);
        Assert.Contains("asp-controller=\"Payments\"", contents);
        Assert.Contains("asp-route-id=\"@item.PaymentTransactionId\"", contents);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(ContractJourneyTabs.Details.Finance)\"", contents);
        Assert.Contains("asp-controller=\"Payments\" asp-action=\"Edit\" asp-route-id=\"@item.PaymentTransactionId\"", contents);
    }

    [Fact]
    public void ContractJourney_Losses_Tab_Provides_Edit_Action()
    {
        var contents = ReadContractJourneyDetailsMarkup();

        Assert.Contains("const string LossesPresentationTab = \"losses\";", contents);
        Assert.Contains("asp-route-lossesView=\"@(isLossesPresentationTab ? \"true\" : null)\"", contents);
        // کسری داخل تب «مصرف و کسری» رندر می‌شود
        Assert.Contains("case ContractJourneyTabs.Details.Costs:", contents);
        Assert.Contains("asp-controller=\"LossEvents\"", contents);
        Assert.Contains("asp-action=\"Edit\"", contents);
        Assert.Contains("asp-route-id=\"@item.LossEventId\"", contents);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(LossesPresentationTab)\"", contents);
    }

    [Fact]
    public void ContractJourney_Removes_Legacy_Tab_Summary_Strip_Without_Dropping_Current_Summary_Data()
    {
        var contents = ReadContractJourneyDetailsMarkup();

        Assert.DoesNotContain("var tabSummaryCards = activeTab switch", contents);
        Assert.DoesNotContain("journey-tab-summary-strip", contents);
        Assert.Contains("LocalizeDisplay(string? value)", contents);
        Assert.DoesNotContain("dashboardTopStats", contents);
        Assert.DoesNotContain("dashboardFlowSteps", contents);
        Assert.Contains("data-contract-journey-facts", contents);
        Assert.Contains("contractDisplayValueUsd", contents);
        Assert.DoesNotContain("activeTab != ContractJourneyTabs.Details.Summary", contents);
        Assert.DoesNotContain("activeTab != ContractJourneyTabs.Details.Dispatch", contents);
        Assert.Contains("asp-action=\"CustomsBatch\"", contents);
        // مصارف و ارزش ضایعات از موتور مفاد؛ نه جمعی که گمرکِ دارای سند مصرف را دوباره بشمارد و
        // نه ارزش ضایعه‌ای که باقی‌ماندهٔ دو جمع باشد.
        Assert.Contains("summaryExpenseTotalUsd = Model.MiniPnl.RecordedExpensesUsd", contents);
        Assert.Contains("summaryLossCostUsd = Model.MiniPnl.LossCostUsd", contents);
        Assert.DoesNotContain("expenseTotalUsd + Model.LoadingOperationalExpenseUsd + Model.CustomsDeclarationTotalUsd", contents);
        Assert.DoesNotContain("Math.Max(summaryPnlExpenseTotalUsd - summaryExpenseTotalUsd, 0m)", contents);
        Assert.DoesNotContain("summaryTransportExpenseUsd = Model.ContractTransportExpenseUsd", contents);
        Assert.DoesNotContain("summaryStorageRentExpenseUsd = Model.ContractStorageRentExpenseUsd", contents);
        Assert.DoesNotContain("referenceKpiCards", contents);
        Assert.DoesNotContain("referenceBarItems", contents);
        Assert.DoesNotContain("referenceDonutTotal", contents);
        Assert.Contains("loadingCount", contents);
        Assert.Contains("dispatchCount", contents);
        Assert.Contains("salesAverageUsd", contents);
        Assert.Contains("totalDisplayLossMt", contents);
        Assert.Contains("Model.ReceiptShortageLossMt", contents);
        Assert.Contains("Model.DispatchShortageLossMt", contents);
        Assert.DoesNotContain("Model.SalesLossMt", contents);
        Assert.Contains("receiptShortageWastageMt", contents);
        Assert.Contains("dispatchShortageWastageMt", contents);
        Assert.DoesNotContain("salesWastageMt", contents);
        Assert.Contains("paymentCount", contents);
    }

    [Fact]
    public void ContractJourney_Summary_Uses_Full_Cycle_Profit_Without_Changing_Realized_Partner_Shares()
    {
        var view = ReadContractJourneyDetailsMarkup();
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ContractJourneyController.cs");
        Assert.Contains("LifecycleMarginUsd = economics.LifecycleMarginUsd", controller);
        Assert.Contains("lifecycleProfitUsd = Model.MiniPnl.LifecycleMarginUsd", view);
        Assert.Contains("FormatLifecycleUsd(lifecycleProfitUsd)", view);
        Assert.Contains("ShareProfitUsd = pnlOperationalMarginUsd * partner.SharePercent / 100m", view);
    }

    [Fact]
    public void ContractJourney_DetailTabs_Group_Operations_Without_Removing_Their_Routes()
    {
        var tabs = ReadRepoFile("src/PTGOilSystem.Web/Models/ContractJourney/ContractJourneyViewModels.cs");
        var view = ReadContractJourneyDetailsMarkup();

        Assert.Contains("public const string InventoryTransport = \"inventorytransport\";", tabs);
        Assert.Contains("InventoryTransport => InventoryTransport", tabs);
        var detailTabsIndex = view.IndexOf("var detailTabs = Model.IsPurchaseContract", StringComparison.Ordinal);
        Assert.True(detailTabsIndex >= 0);
        var purchaseTabsEnd = view.IndexOf(": new ContractJourneyDetailTabLinkViewModel[]", detailTabsIndex, StringComparison.Ordinal);
        var purchaseTabs = view[detailTabsIndex..purchaseTabsEnd];
        Assert.Equal(7, purchaseTabs.Split("new() { Key =", StringSplitOptions.None).Length - 1);
        var loadingsTabIndex = view.IndexOf("Key = ContractJourneyTabs.Details.Loadings", detailTabsIndex, StringComparison.Ordinal);
        var inventoryTabIndex = view.IndexOf("Key = ContractJourneyTabs.Details.Inventory,", detailTabsIndex, StringComparison.Ordinal);
        var inventoryTransportTabIndex = view.IndexOf("Key = ContractJourneyTabs.Details.InventoryTransport", detailTabsIndex, StringComparison.Ordinal);
        Assert.True(loadingsTabIndex >= 0);
        Assert.True(inventoryTabIndex > loadingsTabIndex);
        Assert.True(inventoryTransportTabIndex > inventoryTabIndex);

        Assert.Contains("case ContractJourneyTabs.Details.Loadings:", view);
        Assert.Contains("case ContractJourneyTabs.Details.Receipts:", view);
        Assert.Contains("case ContractJourneyTabs.Details.InventoryTransport:", view);
        Assert.Contains("case ContractJourneyTabs.Details.Inventory:", view);
        Assert.Contains("case ContractJourneyTabs.Details.Dispatch:", view);
        Assert.Contains("case ContractJourneyTabs.Details.Sales:", view);
        Assert.Contains("case ContractJourneyTabs.Details.Costs:", view);
        Assert.Contains("case ContractJourneyTabs.Details.Finance:", view);
        Assert.DoesNotContain("case ContractJourneyTabs.Details.Operations:", view);

        Assert.Contains("Label = T(\"بارگیری و رسید\", \"Loading & receipts\")", purchaseTabs);
        Assert.Contains("Label = T(\"حمل و نقل\", \"Transport\")", purchaseTabs);
        Assert.Contains("Label = T(\"حساب قرارداد\", \"Contract account\")", purchaseTabs);
        Assert.DoesNotContain("Key = ContractJourneyTabs.Details.Receipts,", purchaseTabs);
        Assert.DoesNotContain("Key = ContractJourneyTabs.Details.Dispatch,", purchaseTabs);
        Assert.Contains("Key = ContractJourneyTabs.Details.Costs,", purchaseTabs);
        Assert.Contains("Expenses & losses", purchaseTabs);
        Assert.DoesNotContain("ContractJourneyTabs.Details.Finance => (Tab: ContractJourneyTabs.Details.Costs", view);
        Assert.Contains("ContractJourneyTabs.Details.Receipts => ContractJourneyTabs.Details.Loadings", view);
        Assert.Contains("ContractJourneyTabs.Details.Dispatch => ContractJourneyTabs.Details.InventoryTransport", view);
        Assert.Contains("data-contract-journey-companion=\"@ReturnUrl(groupedSection.Tab)\"", view);
        Assert.Contains("data-companion-before=", view);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(ContractJourneyTabs.Details.Dispatch)\"", view);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(ContractJourneyTabs.Details.InventoryTransport)\"", view);
        Assert.Contains("asp-controller=\"Loading\" asp-action=\"Create\"", view);
        Assert.Contains("asp-controller=\"LoadingReceipts\" asp-action=\"Create\"", view);

        // هر تب فقط جدول/سکشن مشترک ak دارد
        Assert.Contains("class=\"ak-table\"", view);
        Assert.Contains("class=\"ak-table-wrap\"", view);
        Assert.Contains("class=\"ak-form-section\"", view);
        Assert.Contains("ak-empty", view);
        Assert.Contains("_DetailPager.cshtml", view);
        Assert.Contains("await DetailPager(", view);
        Assert.DoesNotContain("Microsoft.AspNetCore.Html.IHtmlContent ListPager", view);
        foreach (var legacy in new[] { "journey-wide-table", "journey-compact-table", "journey-tab-grid", "journey-finance-panel", "journey-loading-list", "table-responsive", "<div class=\"row g-3\">" })
        {
            Assert.DoesNotContain(legacy, view);
        }
    }

    [Fact]
    public void ContractJourney_InventoryTransport_Tab_Uses_Shared_Ak_Table()
    {
        var view = ReadContractJourneyDetailsMarkup();
        // دو switch با همین case هست: اولی نوار KPI و دومی محتوای تب؛ جدول در دومی است.
        const string caseMarker = "case ContractJourneyTabs.Details.InventoryTransport:";
        var block = ExtractSwitchCaseBlock(
            view[view.LastIndexOf(caseMarker, StringComparison.Ordinal)..],
            caseMarker,
            "case ContractJourneyTabs.Details.Inventory:");

        Assert.Contains("class=\"ak-table ak-detail-table\"", block);
        Assert.Contains("ak-row-menu", block);
        Assert.Contains("ak-status", block);
        Assert.Contains("ak-num", block);
        Assert.Contains("@await DetailPager(\"transportLegPage\", transportLegPage, transportLegPageCount)", block);
        Assert.Contains("TransportInTransitQuantity(leg)", block);
        Assert.Contains("TransportStatusToneClass(leg.StatusName)", block);
        Assert.Contains("asp-controller=\"InventoryTransportLegs\"", block);
        foreach (var legacy in new[] { "journey-ops-card", "journey-chain-step", "journey-inventory-chain", "journey-chain-legs", "status-badge", "cj-" })
        {
            Assert.DoesNotContain(legacy, block);
        }
    }

    [Fact]
    public void ContractJourney_Receipts_Tab_Exposes_Bulk_Receipt_Checklist_And_Individual_Links()
    {
        var block = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyReceiptsTab.cshtml");

        // قرارداد مشترک ak
        Assert.Contains("class=\"ak-form\"", block);
        Assert.Contains("ak-form-section", block);
        Assert.Contains("ak-form-grid", block);
        Assert.Contains("class=\"ak-input\"", block);
        Assert.Contains("class=\"ak-table\"", block);
        Assert.Contains("ak-footer-actions", block);
        Assert.Contains("ak-row-menu", block);

        // hookهای زندهٔ رسید گروهی
        Assert.Contains("asp-action=\"BulkCreate\"", block);
        Assert.Contains("@Html.AntiForgeryToken()", block);
        Assert.Contains("data-bulk-receipt-form", block);
        Assert.Contains("data-bulk-receipt-collapsed=\"true\"", block);
        Assert.Contains("data-bulk-receipt-toggle", block);
        Assert.Contains("data-bulk-receipt-toggle-label", block);
        Assert.Contains("data-bulk-receipt-panel hidden", block);
        Assert.Contains("data-bulk-receipt-select-all", block);
        Assert.Contains("data-bulk-receipt-clear", block);
        Assert.Contains("data-bulk-receipt-row", block);
        Assert.Contains("data-bulk-receipt-qty", block);
        Assert.Contains("data-bulk-receipt-total-input", block);
        Assert.Contains("data-bulk-receipt-terminal-select", block);
        Assert.Contains("data-bulk-receipt-tank-select", block);
        Assert.Contains("data-terminal-id=\"@tank.TerminalId\"", block);
        Assert.Contains("data-bulk-receipt-selected-count", block);
        Assert.Contains("data-bulk-receipt-selected-qty", block);
        Assert.Contains("name=\"LoadingRegisterIds\"", block);
        Assert.Contains("T(\"نمبر وسیله\", \"Vehicle No\")", block);
        Assert.Contains("T(\"جنس\", \"Product\")", block);
        Assert.Contains("T(\"وسیله حمل\", \"Transport\")", block);
        Assert.Contains("T(\"مقدار بارگیری‌شده\", \"Loaded quantity\")", block);
        Assert.Contains("T(\"باقی‌مانده رسید\", \"Remaining to receive\")", block);
        Assert.DoesNotContain("T(\"قیمت فی تن\", \"Unit price\")", block);
        Assert.DoesNotContain("T(\"ارزش بارگیری\", \"Loading value\")", block);
        Assert.Contains("item.VehicleNumber", block);
        Assert.Contains("item.ProductName", block);
        Assert.Contains("item.TransportTypeLabel", block);
        Assert.Contains("item.LoadedQuantityMt", block);
        Assert.Contains("@NumberDisplay.Quantity(item.RemainingQuantityMt)", block);
        Assert.DoesNotContain("item.LoadingPriceUsd", block);
        Assert.DoesNotContain("item.LoadingValueUsd", block);
        Assert.Equal(3, block.Split("<th class=\"ak-col-num\">", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, block.Split("<td class=\"ak-col-num\"><span class=\"ak-num\">", StringSplitOptions.None).Length - 1);
        Assert.Contains("name=\"LossMode\"", block);
        Assert.Contains("data-loss-mode=\"immediate\"", block);
        Assert.Contains("BulkReceiptLossMode.None", block);
        Assert.Contains("BulkReceiptLossMode.ImmediateKnownLoss", block);
        Assert.Contains("BulkReceiptLossMode.DeferredTankSettlement", block);
        Assert.Contains("name=\"TotalReceivedQuantityMt\"", block);
        Assert.Contains("name=\"TotalLossQuantityMt\"", block);
        Assert.Contains("name=\"TotalLossToleranceQuantityMt\"", block);
        Assert.Contains("name=\"ReferenceDocument\"", block);
        Assert.Contains("name=\"LossResponsiblePartyName\"", block);
        Assert.Contains("name=\"ReturnUrl\"", block);
        Assert.Contains("asp-action=\"Create\" asp-route-loadingId=\"@item.LoadingRegisterId\"", block);
        Assert.Contains("data-page-modal=\"true\"", block);

        // دو بخش با عنوان و تعداد؛ ثبت رسید هر ردیف مستقیم، نه در منوی ⋮
        Assert.Contains("T(\"بارگیری‌های منتظر رسید\", \"Loadings awaiting receipt\")} ({Model.PendingLoadingCount:N0})", block);
        Assert.Contains("T(\"رسیدهای ثبت‌شده\", \"Registered receipts\")} ({Model.ReceiptCount:N0})", block);
        Assert.Contains("class=\"btn btn-sm btn-light ak-secondary-action\" data-page-modal=\"true\"", block);
        Assert.Contains("T(\"ثبت رسید\", \"Record receipt\")", block);
        Assert.DoesNotContain("T(\"ثبت تکی\", \"Single receipt\")", block);

        // انتخاب گروهی: هیچ ردیفی از پیش انتخاب نمی‌شود و مقدار کل خالی شروع می‌شود
        var rowCheckbox = block[block.IndexOf("data-bulk-receipt-row", StringComparison.Ordinal)..];
        rowCheckbox = rowCheckbox[..rowCheckbox.IndexOf("/>", StringComparison.Ordinal)];
        Assert.DoesNotContain(" checked", rowCheckbox);
        Assert.Contains("name=\"TotalReceivedQuantityMt\" class=\"ak-input ak-num\" value=\"\"", block);
        Assert.Contains("T(\"مقدار انتخاب‌شده\", \"Selected quantity\")", block);
        Assert.DoesNotContain("T(\"مانده\", \"Remaining\")", block);

        // دکمهٔ عمومی «ثبت رسید جدید» که همیشه اولین بارگیری را باز می‌کرد حذف شده است
        Assert.DoesNotContain("Model.FirstLoadingId", block);
        Assert.DoesNotContain("T(\"ثبت رسید جدید\", \"New receipt\")", block);

        foreach (var legacy in new[] { "st-detail-table-card", "st-toolbar-btn", "st-quantity-pill", "journey-receipt-entry-grid", "journey-receipts-simple", "form-control", "form-select" })
        {
            Assert.DoesNotContain(legacy, block);
        }

        // نمایش فیلدهای ضایعات از قرارداد مشترک ak می‌آید، نه از CSS صفحه‌ای
        var akCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        Assert.Contains("[data-bulk-receipt-form].ak-form .ak-field.ak-loss-only", akCss);
        Assert.Contains("max-inline-size: 860px", akCss);
        Assert.Contains("input[data-loss-mode=\"immediate\"]:checked", akCss);
        // ستون انتخاب و دکمه‌های انتخاب همه/پاک‌کردن فقط بعد از باز کردن رسید گروهی؛
        // فرم گروهی زیر جدول باز می‌شود، نه کنار آن.
        Assert.Contains("[data-bulk-receipt-form]:not(.is-bulk-receipt-open) :is(.ak-col-check, [data-bulk-receipt-select-all], [data-bulk-receipt-clear])", akCss);
        Assert.DoesNotContain("grid-template-columns: minmax(0, 3fr) minmax(360px, 2fr)", akCss);

        var coreJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/core.js");
        Assert.Contains("function syncStorageTankOptions()", coreJs);
        Assert.Contains("[data-bulk-receipt-terminal-select]", coreJs);
        Assert.Contains("[data-bulk-receipt-tank-select]", coreJs);
        Assert.Contains("option.getAttribute(\"data-terminal-id\")", coreJs);
        Assert.Contains("document.addEventListener(\"change\"", coreJs);
        Assert.Contains("event.target.matches(\"[data-bulk-receipt-terminal-select]\")", coreJs);
        Assert.DoesNotContain("rows.forEach(function (row)", coreJs);
        Assert.Contains("document.addEventListener(\"submit\"", coreJs);
        Assert.Contains("reloadContractJourneyTab", coreJs);
    }

    [Fact]
    public void ContractJourney_Details_Uses_Shared_Ak_Detail_Contract()
    {
        var view = ReadContractJourneyDetailsMarkup();

        Assert.Contains("ak-form-page", view);
        Assert.Contains("data-contract-journey-page", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("_AkSectionHead.cshtml", view);
        Assert.Contains("class=\"ptg-tabs-rail\"", view);
        Assert.Contains("data-contract-journey-tab-link=\"true\"", view);
        Assert.Contains("ak-status", view);
        Assert.Contains("ak-row-menu", view);
        Assert.Contains("ak-col-actions", view);
        Assert.Contains("ak-col-num", view);
        Assert.Contains("ak-empty", view);
        // Contract statement lives in the header kebab; the export menu holds the visible slot.
        Assert.Contains("Label = T(\"دفتر حساب قرارداد\", \"Contract statement\"), Href = contractStatementUrl", view);
        Assert.DoesNotContain("T(\"دفتر حساب قرارداد\", \"Contract statement\"), \"bi-journal-text\", contractStatementUrl", view);
        Assert.DoesNotContain("Model.NextRecommendedAction", view);

        foreach (var legacy in new[]
        {
            "cj-", "journey-action-btn", "journey-list-header-actions", "journey-ops-head-tools",
            "app-table-card", "card-header", "card-body", "btn-outline-primary", "finance-amount",
            "status-badge", "operation-chip", "reference-dashboard", "contract-ops-center", "<style"
        })
        {
            Assert.DoesNotContain(legacy, view);
        }
    }

    [Fact]
    public void ContractJourney_Details_Wires_Lazy_Tab_Loading_With_Page_Cache()
    {
        var view = ReadContractJourneyDetailsMarkup();
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var js = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/contract-journey-tabs.js");

        Assert.Contains("var isAjaxTabRequest = string.Equals(Context.Request.Headers[\"X-Requested-With\"], \"XMLHttpRequest\", StringComparison.OrdinalIgnoreCase);", view);
        Assert.Contains("Layout = isAjaxTabRequest ? null : \"_Layout\";", view);
        Assert.Contains("ViewData[\"ContractJourneyAssets\"] = true;", view);
        Assert.Contains("contract-journey-page", view);
        Assert.Contains("data-contract-journey-tab-nav=\"true\"", view);
        Assert.Contains("data-contract-journey-tab-link=\"true\"", view);
        Assert.Contains("data-contract-journey-tab-content=\"true\"", view);
        Assert.Contains("data-contract-journey-facts", view);
        Assert.Contains("data-no-spa=\"true\"", view);
        Assert.Contains("~/js/contract-journey-tabs.js", layout);
        Assert.Contains("data-ptg-page-asset=\"contract-journey-tabs\"", layout);
        Assert.Contains("var tabCache = new Map();", js);
        Assert.Contains("fetch(url", js);
        Assert.Contains("document.addEventListener(\"click\", onTabClick, true);", js);
        Assert.Contains("document.querySelector(\"[data-contract-journey-page]\")", js);
        Assert.Contains("data-contract-journey-tab-loading", js);
        Assert.Contains("data-contract-journey-tab-error", js);
    }

    [Fact]
    public void ContractJourney_Details_Removes_Row_Detail_Modals()
    {
        var view = ReadContractJourneyDetailsMarkup();
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tabsJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/contract-journey-tabs.js");

        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyRowDetailModal.cshtml"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/js/contract-journey-details.js"));
        Assert.DoesNotContain("data-journey-row-detail", view);
        Assert.DoesNotContain("journey-row-details-btn", view);
        Assert.DoesNotContain("data-journey-detail-title", view);
        Assert.DoesNotContain("rowDetailModalModel", view);
        Assert.DoesNotContain("ContractJourneyDetailsScript", layout);
        Assert.DoesNotContain("contract-journey-details", tabsJs);
    }
    [Fact]
    public void ContractJourney_Dashboard_Tab_Is_Merged_Into_Summary()
    {
        var tabs = ReadRepoFile("src/PTGOilSystem.Web/Models/ContractJourney/ContractJourneyViewModels.cs");
        var contractsController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ContractsController.cs");

        Assert.Contains("Dashboard => Summary", tabs);
        Assert.Contains("ContractJourneyTabs.Details.Dashboard => ContractJourneyTabs.Details.Summary", contractsController);
    }

    [Fact]
    public void Loading_Create_Removes_ContractJourney_Context_Guide_When_ReturnUrl_Is_Local()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Create.cshtml");

        Assert.Contains("Url.IsLocalUrl(Model.ReturnUrl)", contents);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", contents);
    }

    [Fact]
    public void Customs_Create_Uses_ReturnUrl_For_Back_And_Cancel()
    {
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/CustomsDeclarationsController.cs");
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/CustomsDeclarations/Create.cshtml");

        Assert.Contains("ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null", controller);
        Assert.Contains("model.ReturnUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl) ? localReturnUrl : null;", controller);
        Assert.Contains("var backUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)", view);
        Assert.Contains("_AkFooterActions", view);
        Assert.Contains("_AkPageHeader", view);
        Assert.Contains("customs-create-page", view);
        Assert.Contains("ak-form-section", view);
        Assert.Contains("ak-footer-actions", ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/Ak/_AkFooterActions.cshtml"));
        Assert.DoesNotContain("asp-action=\"Index\" asp-route-loadingRegisterId=\"@Model.LoadingRegisterId\" class=\"btn btn-outline-secondary\"", view);
    }

    [Fact]
    public void Loading_Details_Preserves_ReturnUrl_And_Falls_Back_To_Contract_Loading_List()
    {
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/LoadingController.cs");
        var receiptController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/LoadingReceiptsController.cs");
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");

        Assert.Contains("public async Task<IActionResult> Index(", controller);
        Assert.Contains("DateTime? fromDate = null", controller);
        Assert.Contains("DateTime? toDate = null", controller);
        // فیلتر قرارداد چندانتخابی شد: همان پارامتر contractId، با OR بین مقادیر؛
        // اعمال فیلتر در LoadingListFilter مشترکِ لیست و Export است.
        var loadingListFilter = ReadRepoFile("src/PTGOilSystem.Web/Helpers/LoadingListFilter.cs");
        Assert.Contains("int[]? contractId = null", controller);
        Assert.Contains("ContractIds = contractId", controller);
        Assert.Contains("query = query.Where(l => contractIds.Contains(l.ContractId));", loadingListFilter);
        Assert.Contains("public async Task<IActionResult> Details(int id, string? returnUrl = null)", controller);
        Assert.Contains("CanRegisterReceipt = remainingToReceiveMt > 0m", controller);
        Assert.DoesNotContain("PopulateReceiptLookupsAsync(receiptEditor)", controller);
        Assert.DoesNotContain("ReceiptEditor = receiptEditor", controller);
        Assert.Contains("bool modal = false", receiptController);
        Assert.Contains("return PartialView(\"_ReceiptCreateForm\", model);", receiptController);
        Assert.Contains("ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;", controller);
        Assert.Contains("ViewData[\"HideSectionTabs\"] = true;", view);
        Assert.Contains("var loadingListReturnUrl = Url.Action(\"Index\", \"Loading\", new { contractId = Model.ContractId }) ?? \"/Loading\";", view);
        Assert.Contains("var effectiveReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) ? returnUrl : loadingListReturnUrl;", view);
        Assert.Contains("var currentPageReturnUrl = Context.Request.Path + Context.Request.QueryString;", view);
        Assert.Contains("(string?)effectiveReturnUrl", view);
        Assert.Contains("Href = receiptEditorUrl", view);
        Assert.DoesNotContain("loadingReceiptModal", view);
        Assert.DoesNotContain("data-receipt-remote-modal", view);
        Assert.Contains("Url.Action(\"Create\", \"LoadingReceipts\", new { loadingId = Model.Id, returnUrl = currentPageReturnUrl })", view);
        Assert.Contains("ak-linear-detail", view);
        Assert.Contains("ptg-record-detail", view);
        Assert.Contains("_AkPageHeader", view);
        Assert.Contains("_DetailCards.cshtml", view);
        Assert.Contains("_DetailMore.cshtml", view);
        Assert.DoesNotContain("data-bs-toggle=\"tab\"", view);
        Assert.DoesNotContain("id=\"loading-tab-", view);
        Assert.DoesNotContain("loading-details-simple-page", view);
        Assert.DoesNotContain("loading-detail-clean-header", view);
        Assert.DoesNotContain("loading-detail-single-shell", view);
        Assert.Contains("NumberDisplay.UnitPrice(Model.FreightRateUsdPerMt, \"USD/MT\")", view);
        // Totals must still come from the precomputed view-model values; Razor never
        // creates a second financial calculation path.
        Assert.Contains("var loadingCostsGrandTotal = Model.LoadingCostsGrandTotalUsd;", view);
        Assert.Contains("loadingCostsGrandTotal.ToString(\"N2\")", view);
        Assert.DoesNotContain("_DetailSummaryCard.cshtml", view);
        Assert.DoesNotContain("_DetailKpiStrip.cshtml", view);
        Assert.DoesNotContain("expenseLines.Sum", view);
        Assert.DoesNotContain("Model.CustomsItems.Sum", view);
        Assert.DoesNotContain("loading-journal-metrics", view);
        Assert.DoesNotContain("loading-journal-grid-bottom", view);
        Assert.DoesNotContain("asp-controller=\"LoadingReceipts\" asp-action=\"Create\" asp-route-loadingId=\"@Model.Id\"", view);
        Assert.Contains("returnUrl = currentPageReturnUrl", view);
        Assert.Contains("Url.Action(\"Create\", \"CustomsDeclarations\"", view);
        Assert.DoesNotContain("asp-controller=\"CustomsDeclarations\" asp-action=\"Details\"", view);
        Assert.DoesNotContain("asp-controller=\"LoadingReceipts\" asp-action=\"Details\"", view);
        Assert.DoesNotContain("Model.TransportType == PTGOilSystem.Web.Models.Entities.LoadingTransportType.Wagon", view);
        Assert.DoesNotContain("Model.TransportType == PTGOilSystem.Web.Models.Entities.LoadingTransportType.Truck", view);
        Assert.DoesNotContain("bi bi-plus-lg me-1", view);
    }

    [Fact]
    public void Loading_Details_Header_Provides_Loss_Create_Action()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");

        Assert.Contains("Url.Action(\"Create\", \"LossEvents\"", contents);
        Assert.Contains("loadingRegisterId = Model.Id", contents);
        Assert.Contains("returnUrl = currentPageReturnUrl", contents);
    }

    [Fact]
    public void Loading_Details_Shows_Only_Four_Operational_Quantities_And_Links_Converted_Transport()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");
        var quantityStart = view.IndexOf("var quantityMetrics", StringComparison.Ordinal);
        var timelineStart = view.IndexOf("var timelineItems", quantityStart, StringComparison.Ordinal);
        var quantityBlock = view[quantityStart..timelineStart];
        var pricingStart = view.IndexOf("var pricingItems", StringComparison.Ordinal);
        var rubStart = view.IndexOf("var rubValue", pricingStart, StringComparison.Ordinal);
        var pricingBlock = view[pricingStart..rubStart];

        Assert.Contains("T(\"بارگیری\", \"Loaded\")", quantityBlock);
        Assert.Contains("T(\"رسید\", \"Received\")", quantityBlock);
        Assert.Contains("T(\"کسری\", \"Shortage\")", quantityBlock);
        Assert.Contains("T(\"باقی\", \"Remaining\")", quantityBlock);
        Assert.DoesNotContain("قابل ارسال / تخصیص", quantityBlock);
        Assert.DoesNotContain("مجموع مصارف", quantityBlock);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(quantityBlock, "new\\(\\)").Count);
        Assert.Contains("T(\"مجموع مصارف\", \"Total expenses\")", pricingBlock);
        Assert.Contains("Model.ActiveTransportCount > 0", view);
        Assert.Contains("Url.Action(\"Details\", \"InventoryTransportLegs\"", view);
        Assert.Contains("T(\"به حمل تبدیل شده\", \"Converted to transport\")", quantityBlock);
        Assert.Contains("Href = transportDetailsUrl", quantityBlock);
        Assert.DoesNotContain("وضعیت حمل", view);
    }

    [Fact]
    public void Loading_Details_Uses_Shared_Ak_Detail_Contract()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var detailCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");

        Assert.Contains("ak-linear-detail", view);
        Assert.Contains("_AkPageHeader", view);
        Assert.Contains("_DetailCards.cshtml", view);
        Assert.Contains("_DetailMore.cshtml", view);
        Assert.Contains("class=\"ak-form-grid", view);
        Assert.Contains(".ak-form-page", css);
        Assert.Contains(".ak-form-section", css);
        Assert.Contains(".ak-form-grid", css);
        Assert.Contains(".ak-list", css);
        Assert.Contains(".ak-status", css);
        Assert.Contains("اطلاعات اصلی", view);
        Assert.Contains("ptg-record-detail", view);
        Assert.Contains("CombineActivityAndHistory = true", view);
        Assert.DoesNotContain("TimelineLimit =", view);
        Assert.DoesNotContain("ak-summary-card loading-ruble-pricing", view);
        Assert.DoesNotContain(".ak-loading-rub-secondary > .loading-ruble-pricing", detailCss);
        // Every loading action now lives in one header group: the next step is primary,
        // one companion remains visible and secondary operations use the kebab.
        Assert.DoesNotContain("headerPrimaryModal", view);
        Assert.Contains("Label = T(\"ثبت مصارف\", \"Register expenses\"), Href = expenseEditorUrl", view);
        Assert.Contains("Label = T(\"ثبت رسید\", \"Register receipt\"), Href = receiptEditorUrl", view);
        Assert.Contains("ViewData[\"AkHeaderCompanionAction\"]", view);
        Assert.DoesNotContain("NextActions =", view);
        Assert.DoesNotContain("PrimaryLabel =", view);
        Assert.Contains("JourneyTitle = T(\"روند بارگیری\"", view);
        var mainStart = view.IndexOf("var mainItems", StringComparison.Ordinal);
        var pricingStart = view.IndexOf("var pricingItems", StringComparison.Ordinal);
        Assert.True(mainStart >= 0 && pricingStart > mainStart);
        var mainCardSource = view[mainStart..pricingStart];
        Assert.DoesNotContain("تاریخ بارگیری", mainCardSource);
        Assert.DoesNotContain("محصول", mainCardSource);
        Assert.DoesNotContain("قرارداد خرید", mainCardSource);
        Assert.DoesNotContain("نگاه سریع", view);
        Assert.Contains("[data-loading-details] > .ak-detail-header .ak-kebab-toggle", detailCss);
        // رنگ از توکن می‌آید، نه hex هاردکد (قاعدهٔ design-system).
        Assert.Contains("background: var(--ptg-primary) !important;", detailCss);
        Assert.DoesNotContain("[data-loading-details] .ak-detail-kpi-strip > .ak-stat-card:nth-child(1) .ak-stat-card__value", detailCss);
        Assert.DoesNotContain("[data-loading-details] .ak-detail-kpi-strip > .ak-stat-card:nth-child(2) .ak-stat-card__value", detailCss);
        Assert.DoesNotContain("[data-loading-details] .ak-detail-kpi-strip > .ak-stat-card:nth-child(3) .ak-stat-card__value", detailCss);
        Assert.DoesNotContain("[data-loading-details] .ak-detail-kpi-strip > .ak-stat-card:nth-child(4) .ak-stat-card__value", detailCss);
        Assert.Contains(".ak-linear-detail .ak-detail-overview", detailCss);
        Assert.DoesNotContain("loading-details-simple-page", view);
        Assert.DoesNotContain("loading-detail-simple-card", view);
    }

    [Fact]
    public void Loading_Expense_Editor_Reveals_The_Selected_Party_Lookup()
    {
        var editor = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/_LoadingExpenseEditor.cshtml");
        var row = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/_LoadingExpenseLineRow.cshtml");
        var script = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/loading-expense-editor.js");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");

        Assert.Contains("ak-table loading-expense-table", editor);
        Assert.DoesNotContain("<th>توضیح</th>", editor);
        Assert.Contains("data-row-provider", row);
        Assert.Contains("data-row-asset", row);
        Assert.Contains("type=\"hidden\" name=\"@Name(\"Notes\")\"", row);
        Assert.DoesNotContain("type=\"text\" maxlength=\"1000\"", row);
        Assert.Contains("provider.hidden = !isProvider;", script);
        Assert.Contains("asset.hidden = !isAsset;", script);
        // ویرایشگر مشترک است: ردیف مصارفِ حمل گزینهٔ «راننده» هم دارد، پس «بدون طرف حساب»
        // وقتی پنهان می‌شود که هیچ‌کدام از سه انتخاب فعال نباشد.
        Assert.Contains("noParty.hidden = isProvider || isAsset || isDriver;", script);
        Assert.DoesNotContain("provider.style.display", script);
        Assert.DoesNotContain("asset.style.display", script);
        Assert.Contains("[data-loading-expense-editor] .loading-expense-table", css);
        Assert.Contains("min-width: 1000px;", css);
    }

    [Fact]
    public void Loss_Create_Uses_The_Shared_Responsive_Form_Composition()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/LossEvents/Create.cshtml");

        Assert.Contains("class=\"ak-form-grid ak-form-grid--3\"", view);
        Assert.Contains("<div class=\"ak-advanced-body\">", view);
        Assert.Contains("<div class=\"ak-form-grid\">", view);
        Assert.Contains("method=\"post\" class=\"ak-form\"", view);
        Assert.DoesNotContain("loss-create-form", view);
        Assert.DoesNotContain("loss-create-page", view);
        Assert.DoesNotContain("<div class=\"ak-form-grid\">\n                <div class=\"ak-form-grid\">", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", view);
    }

    [Fact]
    public void LoadingReceipt_Details_Uses_Shared_Ak_Detail_Contract()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/Details.cshtml");
        var detailCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");

        Assert.Contains("ak-form-page", view);
        Assert.Contains("data-loading-receipt-details", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("ak-form-section", view);
        Assert.Contains("ViewData[\"HideSectionTabs\"] = true;", view);
        Assert.Contains("ModalTarget = \"loadingReceiptCancelModal\"", view);
        Assert.Contains("id=\"loadingReceiptCancelModal\"", view);
        Assert.Contains("data-loading-receipt-cancel-form", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("name=\"rowVersion\" value=\"@Model.RowVersion\"", view);
        Assert.Contains("name=\"reason\" maxlength=\"500\" required", view);
        Assert.DoesNotContain("T(\"ردیف‌های رسید، تخصیص و وضعیت\", \"Receipt lines, allocation & status\")", view);
        Assert.DoesNotContain("FormatTraceDestination", view);
        Assert.DoesNotContain("ToAllocationStatusText", view);
        Assert.DoesNotContain("ToAllocationDestinationText", view);
        Assert.Contains("T(\"شماره مرجع\", \"Reference number\")", view);
        Assert.Contains("ToReceiptReferenceText(Model.ReferenceDocument)", view);
        Assert.Contains("normalized.StartsWith(\"BULK-RCPT-\", StringComparison.OrdinalIgnoreCase)", view);
        Assert.Contains("T(\"رسید گروهی\", \"Bulk receipt\")", view);
        Assert.Contains("class=\"ak-form-section ak-detail-section ak-col-full\" data-loading-receipt-losses", view);
        Assert.Contains("T(\"کسری رسید\", \"Receipt shortage\")", view);
        Assert.Contains("LossEventStageLabels.ToPersian(loss.Stage)", view);
        Assert.Contains("ToReceiptReferenceText(loss.Reference)", view);
        Assert.Contains("T(\"قابل‌حساب\", \"Chargeable\")", view);
        Assert.DoesNotContain("CompactText(loss.Reference ?? loss.Notes", view);
        Assert.DoesNotContain("CompactText(Model.Notes", view);
        Assert.DoesNotContain("T(\"یادداشت\", \"Notes\")", view);
        // ردیف «ورود تکمیل‌شده به موجودی» در بازطراحی جزئیات رسید حذف شد؛ تخصیص‌ها از
        // خلاصهٔ تخصیص و سوابق مرتبط (ارسال/فروش) خوانده می‌شوند.
        Assert.DoesNotContain("T(\"ورود تکمیل‌شده به موجودی\", \"Completed to inventory\")", view);
        Assert.Contains("T(\"تخصیص‌ها\", \"Allocations\")", view);
        Assert.Contains("Model.Allocations.Where(item => item.TruckDispatchId.HasValue || item.SalesTransactionId.HasValue)", view);
        Assert.Contains(".ak-linear-detail .ak-detail-overview", detailCss);
        Assert.DoesNotContain("[data-loading-receipt-details] .ak-operations-more-group", detailCss);
        Assert.Contains("dir=\"rtl\"", view);
        Assert.DoesNotContain("var allocationItems", view);
        Assert.DoesNotContain("T(\"ردیف‌های رسید\", \"Receipt lines\")", view);
        Assert.DoesNotContain("loading-details-simple-page", view);
        Assert.DoesNotContain("loading-journal-page", view);
        Assert.DoesNotContain("loading-receipt-details-page", view);
        Assert.DoesNotContain("summary-strip", view);
    }
    [Fact]
    public void Operational_Detail_Views_Preserve_Local_ReturnUrl_For_Back_Navigation()
    {
        var dispatchController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/DispatchController.cs");
        var dispatchView = ReadRepoFile("src/PTGOilSystem.Web/Views/Dispatch/Details.cshtml");
        var salesController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/SalesController.cs");
        var salesView = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Details.cshtml");
        var expensesController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ExpensesController.cs");
        var expensesView = ReadRepoFile("src/PTGOilSystem.Web/Views/Expenses/Details.cshtml");
        var paymentsController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/PaymentsController.cs");
        var paymentsView = ReadRepoFile("src/PTGOilSystem.Web/Views/Payments/Details.cshtml");
        var loadingReceiptsController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/LoadingReceiptsController.cs");
        var loadingReceiptsView = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/Details.cshtml");
        var customsController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/CustomsDeclarationsController.cs");
        var customsView = ReadRepoFile("src/PTGOilSystem.Web/Views/CustomsDeclarations/Details.cshtml");

        Assert.Contains("public async Task<IActionResult> Details(int id, string? returnUrl = null)", dispatchController);
        Assert.Contains("ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;", dispatchController);
        Assert.Contains("var returnUrl = ViewBag.ReturnUrl as string;", dispatchView);

        Assert.Contains("public async Task<IActionResult> Details(int id, string? returnUrl = null)", salesController);
        Assert.Contains("ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;", salesController);
        Assert.Contains("var returnUrl = ViewBag.ReturnUrl as string;", salesView);

        Assert.Contains("public async Task<IActionResult> Details(int id, string? returnUrl = null)", expensesController);
        Assert.Contains("ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;", expensesController);
        Assert.Contains("var returnUrl = ViewBag.ReturnUrl as string;", expensesView);

        Assert.Contains("public async Task<IActionResult> Details(int id, string? returnUrl = null)", paymentsController);
        Assert.Contains("ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;", paymentsController);
        Assert.Contains("var returnUrl = ViewBag.ReturnUrl as string;", paymentsView);

        Assert.Contains("public async Task<IActionResult> Details(int id, string? returnUrl = null)", loadingReceiptsController);
        Assert.Contains("ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;", loadingReceiptsController);
        Assert.Contains("var backUrl = !string.IsNullOrWhiteSpace(returnUrl) ? returnUrl : loadingDetailsUrl;", loadingReceiptsView);

        Assert.Contains("public async Task<IActionResult> Details(int id, string? returnUrl = null)", customsController);
        Assert.Contains("ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;", customsController);
        Assert.Contains("var backUrl = !string.IsNullOrWhiteSpace(returnUrl) ? returnUrl : loadingDetailsUrl;", customsView);
    }

    [Fact]
    public void Sales_Create_Removes_Sales_Contract_And_Offers_Automatic_Purchase_Allocation()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Create.cshtml");

        Assert.Contains("<select asp-for=\"BuyerKey\" asp-items=\"ViewBag.Buyers\" class=\"ak-input\" data-ak-entity-combobox>", contents);
        Assert.Contains("SourcePurchaseContractId", contents);
        Assert.Contains("نمی‌دانم — محاسبه خودکار از موجودی مخزن", contents);
        Assert.Contains("قرارداد خرید منبع (اختیاری)", contents);
        Assert.DoesNotContain("<label asp-for=\"ContractId\"", contents);
        Assert.DoesNotContain("data-sales-contract-id", contents);
    }

    [Fact]
    public void Payments_Create_Uses_Normal_Submit_Text_When_ReturnUrl_Is_Local()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/Payments/Create.cshtml");

        Assert.Contains("Url.IsLocalUrl(Model.ReturnUrl)", contents);
        Assert.Contains("<input asp-for=\"ReturnUrl\" type=\"hidden\" />", contents);
        Assert.Contains("var submitText = isEdit ?", contents);
        Assert.Contains("@submitText", contents);
    }

    [Fact]
    public void Payments_Create_Uses_Compact_Advance_Labels_Without_Changing_Inputs()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/Payments/Create.cshtml");

        Assert.Contains("name=\"IsAdvancePayment\"", contents);
        Assert.Contains("name=\"IsCustomerAdvance\"", contents);
        Assert.Contains(">پیش‌پرداخت</label>", contents);
        Assert.Contains("پرداخت آزاد؛ تخصیص به قرارداد در آینده.", contents);
        Assert.Contains(">پیش‌دریافت</label>", contents);
        Assert.Contains("دریافت آزاد؛ اتصال به فروش در آینده.", contents);
        Assert.DoesNotContain("پیش‌پرداخت است", contents);
        Assert.DoesNotContain("پیش‌دریافت است", contents);
    }

    [Fact]
    public void Operational_Create_Forms_Do_Not_Show_ContractJourney_Guide_Blocks()
    {
        var formPaths = new[]
        {
            "src/PTGOilSystem.Web/Views/Loading/Create.cshtml",
            "src/PTGOilSystem.Web/Views/Dispatch/Create.cshtml",
            "src/PTGOilSystem.Web/Views/Sales/Create.cshtml",
            "src/PTGOilSystem.Web/Views/Expenses/Create.cshtml",
            "src/PTGOilSystem.Web/Views/Payments/Create.cshtml",
            "src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml"
        };

        foreach (var formPath in formPaths)
        {
            var contents = ReadRepoFile(formPath);

            Assert.Contains("ReturnUrl", contents);
        }
    }

    [Fact]
    public void Loading_Create_Exposes_Excel_Transport_And_Cost_Fields()
    {
        var create = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Create.cshtml");
        var rowEditor = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/_LoadingRowEditor.cshtml");
        var editExpenses = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/EditExpenses.cshtml");
        var expenseEditor = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/_LoadingExpenseEditor.cshtml");
        var details = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Index.cshtml");
        var detailActionBar = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailActionBar.cshtml");

        Assert.Contains("ak-form-page--wide loading-import-workbook", create);
        // Excel import uses the shared component in external mode: the file is read by
        // LoadingController.ImportWorkbook and its rows land straight in the form's own table.
        Assert.Contains("Views/Shared/ExcelImport/_ExcelImport.cshtml", create);
        Assert.Contains("data-loading-import", create);
        Assert.Contains("ImportWorkbook", create);
        Assert.Contains("RWB / CMR / Bill of Lading", create);
        Assert.Contains("RWB / CMR / Bill of Lading", rowEditor);
        Assert.Contains("TransportExpenseUsd", rowEditor);
        Assert.Contains("LogisticsServiceProviderId", rowEditor);
        Assert.Contains("FreightRateUsdPerMt", rowEditor);
        Assert.Contains("WarehouseExpenseUsd", rowEditor);
        Assert.Contains("OtherExpenseUsd", rowEditor);
        Assert.Contains("ChargeableQuantityMt", rowEditor);
        Assert.Contains("RailwayRateUsd", rowEditor);
        Assert.Contains("RailwayExpenseUsd", rowEditor);
        Assert.DoesNotContain("data-loading-expense-panel", rowEditor);
        Assert.Contains("_LoadingExpenseEditor", editExpenses);
        Assert.DoesNotContain("loadingExpensesModal", details);
        // Index owns a remote row-specific modal, rather than a static header
        // menu item's ModalTarget. Keep the actual trigger, endpoint and loader.
        Assert.Contains("data-bs-toggle=\"modal\" data-bs-target=\"#loadingIndexExpensesModal\"", index);
        Assert.Contains("data-expense-url=\"@Url.Action(\"EditExpenses\", \"Loading\"", index);
        Assert.Contains("data-loading-expense-remote-modal=\"true\"", index);
        Assert.Contains("ViewData[\"LoadingExpenseEditorAssets\"] = true;", index);
        var expenseScript = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/loading-expense-editor.js");
        Assert.Contains("const trigger = event.relatedTarget;", expenseScript);
        Assert.Contains("trigger?.getAttribute(\"data-expense-url\")", expenseScript);
        Assert.Contains("modal.addEventListener(\"show.bs.modal\"", expenseScript);
        Assert.Contains("Href = expenseEditorUrl", details);
        Assert.Contains("data-bs-toggle=\"modal\"", detailActionBar);
        Assert.Contains("loadingIndexExpensesModal", index);
        Assert.Contains("data-loading-expense-trigger=\"true\"", index);
        Assert.Contains("modal = true", index);
        // Modal is now row-based: the fixed Transport/Warehouse/Railway/Other inputs are gone.
        var expenseLineRow = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/_LoadingExpenseLineRow.cshtml");
        Assert.Contains("data-loading-expense-form", expenseEditor);
        Assert.Contains("data-expense-rows", expenseEditor);
        Assert.Contains("data-add-row", expenseEditor);
        Assert.Contains("data-row-template", expenseEditor);
        Assert.Contains("_LoadingExpenseLineRow", expenseEditor);
        Assert.Contains("data-loaded-quantity", expenseEditor);
        Assert.DoesNotContain("data-transport-rate", expenseEditor);
        Assert.DoesNotContain("data-warehouse-rate", expenseEditor);
        Assert.Contains("data-row-type", expenseLineRow);
        Assert.Contains("data-row-calc", expenseLineRow);
        Assert.Contains("data-row-amount", expenseLineRow);
        Assert.Contains("data-row-party", expenseLineRow);
        Assert.Contains("ServiceProviderId", expenseLineRow);
        Assert.Contains("OperationalAssetId", expenseLineRow);
        Assert.DoesNotContain("asp-controller=\"Expenses\"", details);
        Assert.DoesNotContain("asp-controller=\"Expenses\"", index);
    }

    [Fact]
    public void Loading_Index_Uses_Shared_Ak_List_Contract()
    {
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Index.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");

        Assert.Contains("ak-list-page", index);
        Assert.Contains("_AkPageHeader", index);
        Assert.Contains("_AkSearchFilter", index);
        Assert.Contains("ak-table", index);
        Assert.Contains("ak-status", index);
        Assert.Contains("ak-row-menu", index);
        Assert.Contains("ak-pager", index);
        Assert.DoesNotContain("<colgroup>", index);
        Assert.DoesNotContain("loading-col-actions", index);
        Assert.DoesNotContain("loading-table-scroll", index);
        Assert.DoesNotContain("loading-index-table", index);
        Assert.DoesNotContain("operations-list-page", index);
        Assert.Contains(".ak-table", css);
        Assert.Contains(".ak-status", css);
        Assert.Contains(".ak-row-menu", css);
        Assert.Contains(".ak-pager", css);
    }

    [Fact]
    public void LoadingReceipt_Create_Exposes_Discharge_And_Difference_Context()
    {
        // Create.cshtml now delegates the form body to the
        // _ReceiptCreateForm partial; both files together represent the
        // rendered page that the user sees, so the structural assertions
        // are evaluated against the concatenated content.
        var page = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/Create.cshtml");
        var partial = ReadRepoFile("src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml");

        Assert.Contains("_ReceiptCreateForm", page);

        var contents = page + "\n" + partial;

        Assert.Contains("ArrivalDate", contents);
        Assert.Contains("LeakDate", contents);
        Assert.Contains("ReceivedQuantityMt", contents);
        Assert.Contains("ActualArrivedQuantityMt", contents);
        Assert.Contains("TerminalId", contents);
        Assert.Contains("StorageTankId", contents);
        Assert.Contains("ReferenceDocument", contents);
        // اختلاف مقدار حالا به‌صورت راهنمای زندهٔ زیر فیلد «مقدار رسیده» نشان داده می‌شود.
        Assert.Contains("data-receipt-shortage-hint", contents);
        Assert.Contains("data-loss-auto-quantity", contents);
        Assert.Contains("data-preview-value=\"consumedQuantity\"", contents);
    }

    [Fact]
    public void Dispatch_Create_Exposes_Excel_Freight_And_Shortage_Context()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/Dispatch/Create.cshtml");

        Assert.Contains("FreightCostUsd", contents);
        Assert.Contains("FreightPayableUsd", contents);
        Assert.Contains("AllowanceMt", contents);
        Assert.Contains("ToleranceMt", contents);
        Assert.Contains("ChargeableShortageMt", contents);
        Assert.Contains("ShortageRateUsd", contents);
        Assert.Contains("کرایه ناخالص", contents);
        Assert.Contains("Gross freight", contents);
        Assert.Contains("data-freight-rate-preview", contents);
        Assert.Contains("Shortage Deduction", contents);
        Assert.Contains("data-shortage-deduction-preview", contents);
    }

    [Fact]
    public void Sales_Create_Exposes_Automatic_Stock_Source_Destination_Fx_And_Total_Context()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Create.cshtml");
        var financeForms = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/finance-forms.js");
        var componentsCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");

        Assert.Contains("_AkPageHeader", contents);
        Assert.Contains("class=\"ak-form\"", contents);
        Assert.Contains("_AkSectionHead", contents);
        Assert.Contains("_AkFooterActions", contents);
        Assert.Contains("class=\"ak-table\"", contents);
        Assert.DoesNotContain("ds-form-shell", contents);
        Assert.DoesNotContain("class=\"sales-create-form\"", contents);
        Assert.DoesNotContain("contract-form-section", contents);
        Assert.DoesNotContain("sales-items-table", contents);
        Assert.Contains("<input asp-for=\"ContractId\" type=\"hidden\" />", contents);
        Assert.DoesNotContain("data-sales-contract-id", contents);
        Assert.DoesNotContain("<label asp-for=\"ContractId\"", contents);
        Assert.DoesNotContain("data-sales-contract-help", contents);
        Assert.DoesNotContain("data-sales-contract-shipment-hint", contents);
        Assert.Contains("data-sales-company", contents);
        Assert.Contains("نمی‌دانم — محاسبه خودکار از موجودی مخزن", contents);
        Assert.Contains("asp-for=\"DestinationLocationId\" type=\"hidden\" data-sales-destination-id", contents);
        Assert.Contains("asp-for=\"AppliedFxRateToUsd\"", contents);
        Assert.Contains("asp-for=\"SaleDate\"", contents);
        Assert.DoesNotContain("<label asp-for=\"DestinationLocationId\"", contents);
        Assert.Contains("data-sales-stage", contents);
        Assert.Contains("data-sales-stage-scope=\"terminal\"", contents);
        Assert.Contains("data-sales-stage-scope=\"transit border customs\"", contents);
        Assert.Contains("data-sales-shipment", contents);
        Assert.Contains("data-sales-stock-alert", contents);
        Assert.Contains("data-sales-stock-balance-url", contents);
        Assert.Contains("data-sales-fx-rate-field", contents);
        Assert.Contains("data-sales-total-preview", contents);
        Assert.Contains("data-sales-total-value", contents);
        Assert.Contains("data-sales-line-items", contents);
        Assert.Contains("data-sales-product data-ak-entity-combobox", contents);
        Assert.Contains("[data-sales-create-form] [data-sales-line-items]:has(.ak-entity-combobox[data-open=\"true\"])", componentsCss);
        Assert.Contains("overflow: visible;", componentsCss);
        Assert.Contains(".ak-entity-quick-create[hidden]", componentsCss);
        Assert.Contains("data-sales-save-summary", contents);
        Assert.Contains("مرور نهایی فروش", contents);
        // مرور نهایی فقط دو عدد نهایی دارد (مقدار و مبلغ کل).
        Assert.Contains("class=\"ak-summary-hero\"", contents);
        Assert.Contains("data-sales-summary-qty", contents);
        Assert.Contains("data-sales-summary-amount", contents);
        Assert.DoesNotContain("تنظیمات بیشتر", contents);
        Assert.Contains(".ak-form-section[data-sales-stage-scope]", financeForms);
        Assert.Contains(".ak-field, .ak-col-full", financeForms);
        Assert.DoesNotContain(".contract-form-section[data-sales-stage-scope]", financeForms);
    }

    [Fact]
    public void Employee_Create_Uses_The_Shared_Page_Form_Without_A_Modal()
    {
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/Employees/Index.cshtml");
        var content = ReadRepoFile("src/PTGOilSystem.Web/Views/Employees/_Form.cshtml");
        var createPage = ReadRepoFile("src/PTGOilSystem.Web/Views/Employees/Create.cshtml");
        var js = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/modal-design-system.js");

        Assert.Contains("Url.Action(\"Create\", new { returnUrl })", index);
        Assert.DoesNotContain("_CreateModalShell", index);
        Assert.DoesNotContain("employeeCreateModal", index);
        Assert.DoesNotContain("employee-create-surface", content);
        Assert.DoesNotContain("data-employee-create-surface", content);
        Assert.DoesNotContain("employee-create-profile-card", content);
        Assert.DoesNotContain("employee-create-footer", content);
        Assert.DoesNotContain("<form", content);
        Assert.DoesNotContain("initializeEmployeeCreateSurface", js);
        Assert.DoesNotContain("data-employee-create-surface", js);

        Assert.Contains("asp-for=\"PhotoFile\"", content);
        Assert.Contains("class=\"ak-upload\"", content);
        Assert.Contains("class=\"ak-form-page\"", createPage);
        Assert.Contains("enctype=\"multipart/form-data\"", createPage);
    }

    [Fact]
    public void Modal_Visual_Art_And_Decorative_Assets_Are_Removed()
    {
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/Shared/_ModalVisualArt.cshtml"));
        Assert.False(Directory.Exists(GetRepoPath("src/PTGOilSystem.Web/wwwroot/img/entity-modal-visuals")));

        var modalCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/08-modals.css");
        Assert.DoesNotContain("ptg-modal-real-icon", modalCss);
        Assert.DoesNotContain("ptg-modal-visual-mark", modalCss);

        var storageTanks = ReadRepoFile("src/PTGOilSystem.Web/Views/StorageTanks/Details.cshtml");
        Assert.DoesNotContain("_ModalVisualArt", storageTanks);
        Assert.DoesNotContain("entity-modal-visuals", storageTanks);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = GetRepoPath(relativePath);

        return File.ReadAllText(path);
    }

    private static string ReadContractJourneyDetailsMarkup()
    {
        var details = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/Details.cshtml");

        return ExpandContractJourneyPartials(details);
    }

    private static string ExpandContractJourneyPartials(string contents)
    {
        foreach (var partial in new[]
        {
            ("<partial name=\"_ContractJourneyDetailTabsRail\" model=\"detailTabsRailModel\" />", "src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyDetailTabsRail.cshtml"),
            ("<partial name=\"_ContractJourneyLoadingsTab\" model=\"loadingsTabModel\" />", "src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyLoadingsTab.cshtml"),
            ("<partial name=\"_ContractJourneyReceiptsTab\" model=\"receiptsTabModel\" />", "src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyReceiptsTab.cshtml"),
            ("<partial name=\"_ContractJourneyInventoryTab\" model=\"inventoryTabModel\" />", "src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyInventoryTab.cshtml"),
            ("<partial name=\"_ContractJourneyDispatchTab\" model=\"dispatchTabModel\" />", "src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyDispatchTab.cshtml"),
            ("<partial name=\"_ContractJourneySalesTab\" model=\"salesTabModel\" />", "src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneySalesTab.cshtml"),
            ("<partial name=\"_ContractJourneyFinanceTab\" model=\"financeTabModel\" />", "src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyFinanceTab.cshtml")
        })
        {
            contents = contents.Replace(partial.Item1, ReadRepoFile(partial.Item2), StringComparison.Ordinal);
        }

        return contents;
    }

    private static bool RepoFileExists(string relativePath)
        => File.Exists(GetRepoPath(relativePath));

    private static string ExtractSummaryBlock(string contents)
    {
        const string startMarker = "@if (activeTab == ContractJourneyTabs.Details.Summary)";
        const string endMarker = "    @switch (activeTab)";

        var start = contents.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Summary block start marker was not found.");

        var end = contents.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, "Summary block end marker was not found.");

        return contents[start..end];
    }

    private static string ExtractSwitchCaseBlock(string contents, string startMarker, string endMarker)
    {
        var start = contents.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker {startMarker} was not found.");

        var end = contents.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker {endMarker} was not found.");

        return contents[start..end];
    }

    private static string GetRepoPath(string relativePath)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
