using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ShellViewStructureTests
{
    private static readonly string[] CanonicalPtgStylesheets =
    [
        "~/css/ptg/01-tokens.css",
        "~/css/ptg/02-base.css",
        "~/css/ptg/03-layout.css",
        "~/css/ptg/04-sidebar.css",
        "~/css/ptg/05-components.css",
        "~/css/ptg/06-forms.css",
        "~/css/ptg/07-tables.css",
        "~/css/ptg/08-modals.css",
        "~/css/ptg/09-pages.css",
        "~/css/ptg/10-responsive.css",
        "~/css/ptg/11-details.css",
        "~/css/ptg/12-dashboard.css",
        "~/css/ptg/13-compat.css",
    ];

    [Theory]
    [InlineData("~/css/ptg/01-tokens.css")]
    [InlineData("~/css/ptg/02-base.css")]
    [InlineData("~/css/ptg/03-layout.css")]
    [InlineData("~/css/ptg/04-sidebar.css")]
    [InlineData("~/css/ptg/05-components.css")]
    [InlineData("~/css/ptg/06-forms.css")]
    [InlineData("~/css/ptg/07-tables.css")]
    [InlineData("~/css/ptg/08-modals.css")]
    [InlineData("~/css/ptg/09-pages.css")]
    [InlineData("~/css/ptg/10-responsive.css")]
    [InlineData("~/css/ptg/11-details.css")]
    [InlineData("~/css/ptg/12-dashboard.css")]
    [InlineData("~/css/ptg/13-compat.css")]
    public void Layout_Loads_Each_Canonical_Ptg_Stylesheet_In_Order(string expectedStylesheet)
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var modalLayout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_ModalLayout.cshtml");

        Assert.Contains(expectedStylesheet, layout);
        Assert.Contains(expectedStylesheet, modalLayout);

        var previousIndex = -1;
        foreach (var stylesheet in CanonicalPtgStylesheets)
        {
            var currentIndex = layout.IndexOf(stylesheet, StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"{stylesheet} must load once and after the preceding PTG layer.");
            previousIndex = currentIndex;
        }
    }

    [Fact]
    public void Desktop_Sidebar_Collapse_Keeps_An_Icon_Rail()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tokensCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/01-tokens.css");
        var sidebarCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/04-sidebar.css")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var navigationJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/navigation.js");

        // The rail is the invariant this test guards: the collapsed sidebar must stay an
        // 88px icon rail and the expanded sidebar must be strictly wider than it. The exact
        // expanded width is a design choice (currently 224px) and is deliberately not pinned.
        Assert.Contains("--layout-sidebar-mini: 88px", tokensCss);
        var expandedSidebarPx = ReadPixelToken(tokensCss, "--layout-sidebar");
        var railPx = ReadPixelToken(tokensCss, "--layout-sidebar-mini");
        Assert.Equal(88, railPx);
        Assert.True(
            expandedSidebarPx > railPx,
            $"Expanded sidebar ({expandedSidebarPx}px) must be wider than the icon rail ({railPx}px).");
        Assert.Contains("is-sidebar-collapsed .ak-navpanel {\n        display: flex;", sidebarCss);
        Assert.Contains("is-sidebar-collapsed .ptg-nav-ic > svg {\n        inline-size: 27px;\n        block-size: 27px;", sidebarCss);
        Assert.Contains("is-sidebar-collapsed .boltz-nav-link > span", sidebarCss);
        Assert.Contains("window.matchMedia(\"(min-width: 1200px)\")", navigationJs);
        Assert.Contains("window.matchMedia(\"(min-width: 992px)\")", navigationJs);
        Assert.Contains("document.body.classList.remove(\"is-sidebar-collapsed\")", navigationJs);
        Assert.Contains("aria-label=\"@node.Label\"", layout);
        Assert.Contains("title=\"@node.Label\"", layout);
    }

    [Fact]
    public void Sidebar_Uses_Light_Chortke_Surfaces_Without_Changing_State_Colors()
    {
        var tokensCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/01-tokens.css");
        var sidebarCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/04-sidebar.css");

        // نوار کناری Deep Navy است؛ همهٔ رنگ‌ها در 01-tokens متمرکزند و 04-sidebar فقط token می‌خواند.
        Assert.Contains("--ptg-sidebar-panel: #1A467D", tokensCss);
        Assert.Contains("--ptg-sidebar-rail: #14355F", tokensCss);
        Assert.Contains("--ptg-sidebar-panel: #1E1E27", tokensCss);
        Assert.Contains("--ptg-sidebar-rail: #16161D", tokensCss);
        Assert.Contains("--ptg-sidebar-hover-bg: rgba(255, 255, 255, 0.10)", tokensCss);
        Assert.Contains("--ptg-sidebar-active-bg: rgba(255, 255, 255, 0.14)", tokensCss);
        Assert.Contains("--ptg-sidebar-danger: #FF9A9A", tokensCss);
        Assert.Contains("background: var(--ptg-sidebar-panel)", sidebarCss);
    }

    [Fact]
    public void Sidebar_Groups_Users_And_Backups_Under_Settings()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");

        Assert.Contains("if (canManageUsers)", layout);
        Assert.Contains("settingsItems.Add((\"Users\", \"Index\", T(\"کاربران\", \"Users\"), \"nav-users\"))", layout);
        Assert.Contains("if (canManageBackups)", layout);
        Assert.Contains("settingsItems.Add((\"Backups\", \"Index\", T(\"بکاپ‌گیری\", \"Backups\"), \"nav-settings\"))", layout);
        Assert.Contains("T(\"تنظیمات\", \"Settings\")", layout);
        Assert.Contains("children: settingsItems.ToArray()", layout);
        Assert.DoesNotContain("navSource.Add(NavNode(\"Users\"", layout);
        Assert.DoesNotContain("navSource.Add(NavNode(\"Backups\"", layout);
    }

    [Fact]
    public void Sidebar_Uses_Mashal_Brand_And_Settings_Starts_Collapsed()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");

        Assert.Contains("~/images/mashal-sidebar-white.webp", layout);
        Assert.Contains("alt=\"مشعل — سیستم مدیریت تجارت نفت و گاز\"", layout);
        Assert.DoesNotContain("~/images/saddiqi-sidebar-wordmark.webp", layout);
        Assert.Contains("!string.Equals(node.Controller, \"SystemSettings\", StringComparison.OrdinalIgnoreCase)", layout);
    }

    [Fact]
    public void Shared_PageFrame_Owns_Workspace_Width_Gutters_And_Form_Columns()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tokensCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/01-tokens.css");
        var pageFrameCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css");
        var akauntingCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/45-akaunting.css");
        var formCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/76-form-system.css");

        Assert.Contains("ptg-page-body ptg-page-frame", layout);
        Assert.Contains("~/css/ptg/70-page-frame.css", layout);
        Assert.True(
            layout.LastIndexOf("~/css/ptg/70-page-frame.css", StringComparison.Ordinal)
            > layout.LastIndexOf("~/css/ptg/61-finance-workspace.css", StringComparison.Ordinal));
        Assert.Contains("--layout-content-max: 1280px", tokensCss);

        // One form width for the whole system: the reference form
        // («ثبت دریافت / پرداخت») set it, and 76-form-system.css now applies it to
        // every Create/Edit page. The old narrow/wide pair is gone.
        Assert.Contains("--chk-form-width: 1080px", tokensCss);
        Assert.Contains("--ak-form-content-max: var(--chk-form-width)", tokensCss);
        Assert.DoesNotContain("--layout-form-max", tokensCss);
        Assert.DoesNotContain("--ak-form-content-wide-max", tokensCss);

        Assert.Contains("padding-inline: var(--layout-gutter-desktop)", pageFrameCss);
        Assert.Contains("max-inline-size: var(--layout-content-max)", pageFrameCss);
        Assert.Contains("--layout-content-max: 1400px", pageFrameCss);
        Assert.Contains("--layout-content-max: 1480px", pageFrameCss);
        Assert.Contains("--layout-content-max: 1560px", pageFrameCss);
        Assert.Contains("@media (min-width: 1200px) and (max-width: 1599.98px)", pageFrameCss);
        Assert.Contains("padding-inline: var(--layout-gutter-compact)", pageFrameCss);
        Assert.Contains("@media (min-width: 768px) and (max-width: 1199.98px)", pageFrameCss);
        Assert.DoesNotContain(".ptg-page > .boltz-content-body", akauntingCss);

        // The form column is owned by the form system, which loads after both
        // 50-ak-components.css and the page layers so it has the last word.
        Assert.Contains(".ak-form-page:has(> form.ak-form)", formCss);
        Assert.Contains("padding-inline: 0", formCss);
        Assert.Contains("inline-size: min(100%, var(--ak-form-content-max))", formCss);
        Assert.Contains("~/css/ptg/76-form-system.css", layout);
        Assert.True(
            layout.LastIndexOf("~/css/ptg/76-form-system.css", StringComparison.Ordinal)
            > layout.LastIndexOf("~/css/ptg/50-ak-components.css", StringComparison.Ordinal));
    }

    [Fact]
    public void Header_Removes_Settings_And_Fits_The_Account_Avatar()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var shellCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/boltz-shell.css")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("ptg-hgear", layout);
        Assert.DoesNotContain("bi bi-gear", layout);
        Assert.Contains("ptg-havatar-ring ptg-havatar-trigger", layout);
        Assert.Contains(".ptg-havatar-ring {\n    width: 2.5rem;\n    height: 2.5rem;\n    padding: 2px;", shellCss);
        Assert.Contains("object-fit: contain", shellCss);
    }

    [Fact]
    public void Header_Drops_The_Fiscal_Year_Switcher_And_Keeps_The_Component_Contract()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var component = ReadRepoFile(
            "src/PTGOilSystem.Web/Views/Shared/Components/FiscalYearSwitcher/Default.cshtml");

        // The topbar cluster no longer carries the fiscal-year switcher.
        Assert.DoesNotContain("<vc:fiscal-year-switcher>", layout);
        Assert.DoesNotContain("ptg-hfyear", layout);

        // The component itself stays intact: selection posts to the dedicated context
        // controller with an antiforgery token, and the year id is the only thing
        // submitted — the company is resolved server-side.
        Assert.Contains("asp-controller=\"FiscalYearContext\"", component);
        Assert.Contains("asp-action=\"Select\"", component);
        Assert.Contains("Html.AntiForgeryToken()", component);
        Assert.Contains("name=\"fiscalYearId\"", component);
        Assert.DoesNotContain("name=\"companyId\"", component);

        // Status badges reuse the canonical ak-status skin, not a bespoke one.
        Assert.Contains("ak-status ptg-hfyear-status", component);
    }

    [Fact]
    public void Header_Uses_Real_Language_And_Account_Controls_Without_A_Duplicate_Search()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var headerCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/74-header-controls.css");

        // The page-level fake search was removed; the two real controls remain line icons.
        Assert.DoesNotContain("ptg-hsearch-trigger", layout);
        Assert.DoesNotContain("data-global-search", layout);
        Assert.Contains("bi bi-translate ptg-hflag-ico", layout);
        Assert.Contains("bi bi-person ptg-havatar-ico", layout);
        Assert.DoesNotContain("ptgUkFlag", layout);

        // The header trigger itself carries no image — the drawer keeps its own avatar.
        var triggerStart = layout.IndexOf("ptg-havatar-ring ptg-havatar-trigger", StringComparison.Ordinal);
        var triggerEnd = layout.IndexOf("</button>", triggerStart, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", layout[triggerStart..triggerEnd]);

        // The remaining icon controls share the same size, and the avatar has no bespoke ring.
        Assert.Contains(".ptg-havatar-ring\n) .bi", headerCss.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("box-shadow: none;", headerCss);
    }

    [Fact]
    public void Canonical_Tabs_Skin_Loads_Last_And_Covers_Every_Tab_Family()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var modalLayout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_ModalLayout.cshtml");
        var tabsCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/16-system-tabs.css");
        var listsCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/15-system-lists.css");
        var tabsJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/ptg-tabs.js");
        var sectionTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_SectionTabs.cshtml");
        var shipmentTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");
        var journeyTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyDetailTabsRail.cshtml");

        Assert.True(
            layout.LastIndexOf("~/css/ptg/16-system-tabs.css", StringComparison.Ordinal)
            > layout.IndexOf("RenderSectionAsync(\"Styles\"", StringComparison.Ordinal),
            "The canonical tabs skin must load after page-specific styles.");
        Assert.True(
            modalLayout.LastIndexOf("~/css/ptg/16-system-tabs.css", StringComparison.Ordinal)
            > modalLayout.IndexOf("RenderSectionAsync(\"Styles\"", StringComparison.Ordinal),
            "Modal tabs must use the same final skin.");

        // Chortke tab tokens. These are the contract every rail renders against.
        Assert.Contains("--ptg-tabs-font-size: 16px", tabsCss);
        Assert.Contains("--ptg-tabs-text-color: #333333", tabsCss);
        Assert.Contains("--ptg-tabs-active-color: var(--primary-main)", tabsCss);
        Assert.Contains("--ptg-tabs-border-color: #E0E0E0", tabsCss);
        Assert.Contains("--ptg-tabs-item-gap: 32px", tabsCss);
        Assert.Contains("--ptg-tabs-rail-height: 52px", tabsCss);
        Assert.Contains("--ptg-tabs-indicator-height: 3px", tabsCss);
        Assert.Contains("--ptg-tabs-transition-duration: 300ms", tabsCss);
        Assert.Contains("--ptg-tabs-focus-color: rgba(16, 98, 208, .25)", tabsCss);
        Assert.Contains("border-bottom: 1px solid var(--ptg-tabs-border-color)", tabsCss);

        // Flat rail: spacing comes from tab padding, never from a gap, and the
        // rail must not wrap to a second line on narrow screens.
        Assert.Contains("gap: 0;", tabsCss);
        Assert.Contains("flex-wrap: nowrap", tabsCss);
        Assert.Contains("scrollbar-width: thin", tabsCss);
        Assert.DoesNotContain("font-weight: 750 !important", listsCss);
        Assert.Contains(".ptg-tabs-rail", tabsCss);
        Assert.Contains(".ptg-tab-item", tabsCss);
        Assert.DoesNotContain(".section-tabs", tabsCss);
        Assert.DoesNotContain(".oa-reference-tabs", tabsCss);
        Assert.DoesNotContain(".transport-profile-tabs", tabsCss);

        // 45-akaunting.css loads after the canonical skin, so it must not restyle
        // tabs — its old block beat every rail with !important. It may only refer
        // to tabs to exclude them from the global link colour, which otherwise
        // outweighs .ptg-tab-item and paints idle link-tabs in the active purple.
        var akauntingCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/45-akaunting.css");
        Assert.Contains(":not(.ptg-tab-item)", akauntingCss);
        Assert.DoesNotContain("font-size: 14px !important", akauntingCss);
        Assert.DoesNotContain("font-weight: 500 !important", akauntingCss);
        Assert.DoesNotContain(".ptg-tab-item {", akauntingCss);

        Assert.Contains("class=\"ptg-tabs-rail\"", sectionTabs);
        Assert.Contains("class=\"ptg-tab-item ", sectionTabs);
        Assert.Contains("data-section-tabs-group", sectionTabs);
        Assert.Contains("_DetailsTabs.cshtml", shipmentTabs);
        Assert.Contains("Context.Request.Query[\"tab\"]", shipmentTabs);
        Assert.DoesNotContain("data-shipment-file-tabs", shipmentTabs);
        Assert.Contains("class=\"ptg-tabs-rail\"", journeyTabs);
        Assert.DoesNotContain("cj-tabs-card", journeyTabs);
        Assert.DoesNotContain("cj-tab-pill", journeyTabs);
        Assert.DoesNotContain("cj-step-sep", journeyTabs);

        // Tabs are text-only, like the Akaunting reference: no icon markup inside
        // a rail, and no Bootstrap tab toggle anywhere in the system. (The page
        // action button next to the rail keeps its own icon — that is not a tab.)
        Assert.DoesNotContain("<i class=\"bi @tab.Icon\"", sectionTabs);
        Assert.DoesNotContain("<use href=\"#@tab.Icon\"", sectionTabs);
        Assert.DoesNotContain("<i class=\"bi @tab.Icon\"", journeyTabs);
        Assert.DoesNotContain("data-bs-toggle=\"tab\"", shipmentTabs);
        Assert.DoesNotContain("data-bs-toggle=\"tab\"", ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Journey.cshtml"));
        Assert.DoesNotContain("data-bs-toggle=\"tab\"", ReadRepoFile("src/PTGOilSystem.Web/Views/OperationalAssets/Details.cshtml"));

        // One engine drives every rail: keyboard, ARIA and overflow scrolling.
        Assert.Contains("wireHorizontalScroll", tabsJs);
        Assert.Contains("event.preventDefault();", tabsJs);
        Assert.Contains("ArrowLeft", tabsJs);
        Assert.Contains("ArrowRight", tabsJs);
        Assert.Contains("role\", \"tablist\"", tabsJs);
        Assert.Contains("role\", \"tabpanel\"", tabsJs);
        Assert.Contains("aria-controls", tabsJs);
    }

    [Fact]
    public void Finance_Assets_Remain_Available_Across_Spa_And_Back_Navigation()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var financeCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/61-finance-workspace.css");
        var invoiceDocument = ReadRepoFile("src/PTGOilSystem.Web/Views/Invoices/Document.cshtml");

        Assert.Contains("~/css/ptg/61-finance-workspace.css", layout);
        Assert.DoesNotContain("@if (isFinanceWorkspace)", layout);

        // The light/dark switch was removed from the topbar; the shell is light-only.
        Assert.DoesNotContain("data-finance-theme-toggle", layout);
        Assert.DoesNotContain("~/js/finance-theme.js", layout);

        Assert.Contains("body.is-finance-workspace", financeCss);
        Assert.Contains("data-ptg-page-asset=\"invoice-document\"", invoiceDocument);
    }

    [Fact]
    public void Dashboard_Uses_A_Scoped_Responsive_Erp_Composition()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Home/Index.cshtml");
        var insights = ReadRepoFile("src/PTGOilSystem.Web/Views/Home/_DashboardInsights.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/12-dashboard.css");
        var script = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/dashboard.js");

        // The dashboard is the `dash-*` composition: headline totals, the sales mix and
        // one trend chart. The older `dashboard-shortcut` / `dashboard-chart-*` naming and
        // the hero banner are gone and must not come back.
        Assert.Contains("ViewData[\"DashboardAssets\"] = true", view);
        Assert.DoesNotContain("dashboard-hero", view);
        Assert.DoesNotContain("dashboard-period", view);
        Assert.DoesNotContain("PTG OIL SYSTEM", view);
        Assert.DoesNotContain("داشبورد مدیریتی", view);
        Assert.DoesNotContain("تصویر روشن از عملیات", view);
        Assert.DoesNotContain("dashboard-shortcut", view);
        Assert.DoesNotContain("dashboard-kpis", view);
        Assert.DoesNotContain("dashboard-metric-card", view);
        Assert.DoesNotContain("dashboard-activity", view);
        Assert.DoesNotContain("RecentActivities", view);

        // The quick-action rail was removed from the dashboard; navigation lives in the shell.
        Assert.DoesNotContain("dash-quick", view);
        Assert.DoesNotContain("dash-quick", insights);
        Assert.DoesNotContain("دسترسی سریع", view);

        // The lower dashboard is the in-progress shipment flow on its own full-width row.
        // It reuses the shared status and empty state.
        Assert.DoesNotContain("dash-activity-card", insights);
        Assert.DoesNotContain("RecentActivities", insights);
        Assert.Contains("dash-flow-card", insights);
        Assert.DoesNotContain("dash-capacity", insights);
        Assert.DoesNotContain("dash-insight-stack", insights);
        Assert.Contains("Model.ActiveShipmentProgress.Take(3)", insights);
        Assert.Contains("class=\"ak-status @item.StatusClass\"", insights);
        Assert.Contains("<partial name=\"_EmptyState\"", insights);
        Assert.Contains("<progress", insights);
        Assert.DoesNotContain("style=", insights);
        Assert.DoesNotContain("<script", insights);

        // Exactly one trend chart, fed entirely from data-* attributes.
        Assert.Equal(1, view.Split("data-dashboard-chart", StringSplitOptions.None).Length - 1);
        Assert.Contains("data-labels=\"@chartLabels\"", view);
        Assert.Contains("data-sales=\"@chartSales\"", view);
        Assert.Contains("data-expenses=\"@chartExpenses\"", view);

        // The only inline style is the mix-legend dot, whose colour comes from the model.
        Assert.Equal(1, view.Split("style=", StringSplitOptions.None).Length - 1);
        Assert.Contains("class=\"dash-dot\" style=\"background:@seg.color\"", view);

        Assert.Contains(".dashboard-page", css);
        Assert.DoesNotContain(".dash-quick", css);
        Assert.DoesNotContain(".dash-capacity", css);
        Assert.Contains(".dash-insights", css);
        Assert.Contains(".dash-mix", css);
        Assert.Contains(".dash-trend", css);
        Assert.DoesNotContain(".dash-total", css);
        Assert.DoesNotContain(".dashboard-hero", css);
        Assert.DoesNotContain(".dashboard-period", css);
        Assert.DoesNotContain(".dashboard-metric", css);
        Assert.DoesNotContain(".dashboard-activity", css);
        Assert.Contains("max-inline-size: 100%", css);
        Assert.Contains("overflow: visible", css);
        Assert.Contains("@media (max-width: 575.98px)", css);

        Assert.Contains("ptg:page-ready", script);
        Assert.Contains("pageshow", script);
        Assert.Contains("replaceChildren", script);
        Assert.Contains("Intl.NumberFormat", script);
        Assert.Contains("function linePath(points)", script);
        Assert.Contains("dash-chart-line--", script);
        Assert.Contains("dash-chart-area--", script);
        Assert.DoesNotContain("dashboard-radial", script);
    }

    [Fact]
    public void Base_Definition_Tabs_Use_The_Shared_Statistic_Cards()
    {
        var modules = new[]
        {
            "Products",
            "Units",
            "Currencies",
            "DailyFxRates",
            "Locations",
            "ExpenseTypes",
            "ExpenseRules",
            "StorageTanks",
            "Terminals"
        };

        foreach (var module in modules)
        {
            var view = ReadRepoFile($"src/PTGOilSystem.Web/Views/{module}/Index.cshtml");
            var headerPosition = view.IndexOf("_AkPageHeader.cshtml", StringComparison.Ordinal);
            var statisticsPosition = view.IndexOf("ak-stat-grid mb-3", StringComparison.Ordinal);

            Assert.True(headerPosition >= 0, $"{module} must keep the shared page header.");
            Assert.True(statisticsPosition > headerPosition, $"{module} statistics must follow the page header.");
            Assert.Equal(4, view.Split("<vc:stat-card", StringSplitOptions.None).Length - 1);
        }
    }

    [Fact]
    public void Operations_Tabs_Use_The_Shared_Statistic_Cards_With_Avatars()
    {
        var modules = new[]
        {
            "Loading",
            "ShipmentPnl",
            "Dispatch",
            "TruckSettlements",
            "Expenses",
            "LossEvents",
            "LoadingReceipts",
            "CustomsDeclarations",
            "Sales"
        };

        foreach (var module in modules)
        {
            var view = ReadRepoFile($"src/PTGOilSystem.Web/Views/{module}/Index.cshtml");

            Assert.Contains("ak-stat-grid mb-3", view);
            Assert.Equal(4, view.Split("<vc:stat-card", StringSplitOptions.None).Length - 1);
            Assert.Equal(4, view.Split(" avatar=\"", StringSplitOptions.None).Length - 1);
        }

        var transports = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Index.cshtml");
        Assert.Contains("transport-workflow-table", transports);
        Assert.Contains("NumberDisplay.Quantity(item.QuantityMt)", transports);
        Assert.Contains("ak-stat-grid mb-3", transports);
        Assert.Equal(4, transports.Split("<vc:stat-card", StringSplitOptions.None).Length - 1);
        Assert.Equal(4, transports.Split(" avatar=\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Canonical_Index_Lists_Use_The_Shared_Module_Row_Avatar()
    {
        var tables = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/tables.js");
        var components = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var personCell = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_PersonCell.cshtml");
        var modules = new[]
        {
            "accountstatements", "auditlogs", "cashaccounts", "chartofaccounts",
            "closingchecklist", "companies", "contractamendments", "contractbalancetransfers",
            "contractjourney", "contracts", "currencies", "customers", "customsdeclarations",
            "customspermitturnover", "dailyfxrates", "dispatch", "drivers", "employees",
            "expenserules", "expenses", "expensetypes", "finalclose", "fiscalyears",
            "inventory", "inventorytransportlegs", "ledger", "loading", "loadingreceipts",
            "locations", "lossevents", "operationalassets", "partners", "payments",
            "plattsrates", "products", "roles", "sales", "sarrafs", "sarrafsettlements",
            "serviceproviders", "shipmentcontracts", "shipmentpnl", "storagetanks",
            "suppliers", "terminals", "threewaysettlement", "trialclose", "trucks",
            "trucksettlements", "units", "users", "vessels", "wagons"
        };

        Assert.Contains("initializeListRowAvatars();", tables);
        Assert.Contains("document.body.classList.contains(\"action-index\")", tables);
        Assert.Contains(".ptg-person-avatar, .ptg-list-row-avatar, .ak-empty", tables);
        Assert.Contains("ptg-list-entity-cell", tables);
        Assert.Contains("ptg-list-row-avatar", components);
        Assert.Contains("width: 28px", components);
        // رنگ از توکن primary می‌آید، نه hex هاردکد.
        Assert.Contains("background: var(--ptg-primary)", components);
        Assert.Contains("class=\"ptg-person-avatar\"", personCell);

        foreach (var module in modules)
        {
            Assert.Contains($"{module}:", tables);
        }
    }

    [Fact]
    public void Server_Paged_Tables_Are_Excluded_From_Shared_Client_Pagination()
    {
        var pagination = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Pagination.cshtml");
        var tables = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/tables.js");

        Assert.Contains("data-server-pagination=\"true\"", pagination);
        Assert.Contains("if (table.closest(\"[data-disable-client-pagination='true']\")) return;", tables);
        Assert.Contains("if (hasServerPagination(table)) return;", tables);
        Assert.Contains("table.closest(\".ak-list, [data-operations-list], .ak-list-page, .ak-form-page\")", tables);
        Assert.Contains("list.querySelector(\"[data-server-pagination='true']\")", tables);
        Assert.Contains("var pageSize = 10;", tables);

        // Tables without a server marker keep the existing client-side pager.
        Assert.Contains("if (rows.length <= pageSize) return;", tables);
        Assert.Contains("pager.className = \"ptg-client-pager\";", tables);
        Assert.Contains("initializeResponsiveTables();", tables);
        Assert.Contains("initializeClickableTableRows();", tables);
        Assert.Contains("initializeBulkSelection();", tables);
    }

    [Fact]
    public void Base_Definitions_Navigation_Opens_The_First_Shared_Tab()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_SectionTabs.cshtml");

        Assert.Contains("(\"Currencies\",   \"Index\", T(\"ارزها\"", tabs);
        Assert.Contains("SidebarItem(\"Currencies\", \"Index\", T(\"تعاریف پایه\", \"Base Definitions\")", layout);
        Assert.Contains("NavNode(\"Currencies\", \"Index\", T(\"تعاریف پایه\", \"Base Definitions\")", layout);
        Assert.DoesNotContain("SidebarItem(\"StorageTanks\", \"Index\", T(\"تعاریف پایه\", \"Base Definitions\")", layout);
        Assert.DoesNotContain("NavNode(\"StorageTanks\", \"Index\", T(\"تعاریف پایه\", \"Base Definitions\")", layout);
    }

    [Fact]
    public void Inventory_Navigation_Opens_The_Existing_Shared_Tabs()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_SectionTabs.cshtml");

        Assert.Contains("var inventoryControllers = new[] { \"Inventory\", \"Custody\", \"CommodityLoans\" };", layout);
        Assert.Contains("NavNode(\"Inventory\", \"Index\", T(\"موجودی\", \"Inventory\"), \"nav-assets\", active: inventoryControllers)", layout);
        Assert.Contains("(\"Inventory\",  \"Index\",        T(\"حرکت‌های موجودی\", \"Inventory Movements\")", tabs);
        Assert.Contains("(\"Custody\",    \"Index\",        T(\"امانی\",           \"Custody\")", tabs);
        Assert.Contains("(\"CommodityLoans\", \"Index\",    T(\"قرض تیل\",        \"Commodity loans\")", tabs);
    }

    [Fact]
    public void Inventory_Forms_Preserve_Safety_Fields_And_Loan_Labels_Use_Plain_Dari()
    {
        var form = ReadRepoFile("src/PTGOilSystem.Web/Views/Inventory/Create.cshtml");
        Assert.Contains("Html.AntiForgeryToken()", form);
        Assert.Contains("Html.FormToken()", form);
        Assert.Contains("asp-for=\"DestinationTerminalId\"", form);
        Assert.Contains("asp-for=\"DestinationStorageTankId\"", form);
        Assert.Contains("asp-for=\"NegativeAdjustment\"", form);
        Assert.Contains("asp-for=\"Reason\"", form);
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/CommodityLoans/Index.cshtml");
        Assert.Contains("تیل قرض دادیم", index);
        Assert.Contains("تیل قرض گرفتیم", index);
        Assert.DoesNotContain("قرض جنسی", index);
        var registry = ReadRepoFile("src/PTGOilSystem.Web/Services/OfflineSync/OfflineFormRegistry.cs");
        Assert.Contains("Token(\"Inventory\", \"Create\",", registry);
    }

    [Theory]
    [InlineData(".ptg-app")]
    [InlineData(".ptg-page-frame")]
    [InlineData(".ptg-sidebar")]
    [InlineData(".ak-page-header")]
    [InlineData(".ak-form-page")]
    [InlineData(".ak-form-section")]
    [InlineData(".ak-detail-toolbar")]
    [InlineData(".ptg-tabs-rail")]
    [InlineData(".ak-status")]
    [InlineData(".ak-row-menu")]
    [InlineData(".ak-form-grid")]
    [InlineData(".ak-table-wrap")]
    [InlineData(".ak-table")]
    [InlineData(".ak-list")]
    [InlineData(".ak-empty")]
    [InlineData(".ak-pager")]
    [InlineData(".ak-modal-content")]
    public void Ptg_Css_Defines_Each_Canonical_Selector_Or_Intentional_Bridge(string selector)
    {
        var css = ReadPtgCss();

        Assert.Contains(selector, css);
    }

    [Fact]
    public void Shared_List_Card_Composes_Existing_List_Nodes_And_Preserves_Hover_Actions()
    {
        var listScript = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/list-toolbar-row.js");
        var tablesScript = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/tables.js");
        var components = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var listStyles = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/15-system-lists.css");
        var operationsFooter = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_OperationsListFooter.cshtml");
        var pagedFooter = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_PagedListFooter.cshtml");

        Assert.Contains("assembleListCard(page, toolbar)", listScript);
        Assert.Contains("card.classList.add(\"ak-list-card\")", listScript);
        Assert.Contains("window.PTG.setListCardLoading", listScript);
        Assert.Contains("--ptg-list-radius: 12px", listStyles);
        Assert.Contains(".ak-list-card", listStyles);
        // چگالی ردیف دیگر در لایه لیست تکرار نمی‌شود؛ تنها منبع آن 50-ak-components.css
        // است (چرتکه: 12 + 21 + 12 = ارتفاع ردیف 45px) تا دو لایه با هم نجنگند.
        Assert.DoesNotContain("height: 54px", listStyles);
        Assert.Contains(
            "padding: 12px 10px",
            ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css"));
        Assert.Contains(".ak-list-footer", listStyles);
        Assert.Contains(".ak-list-loading-state", listStyles);
        Assert.Contains("ak-list-result-count", operationsFooter);
        Assert.Contains("ak-list-result-count", pagedFooter);

        Assert.Contains("initializeContextualRowActions();", tablesScript);
        Assert.Contains("action.source.click();", tablesScript);
        Assert.Contains(".ak-table tbody tr:hover .ak-row-actions", components);
        Assert.Contains(".ak-table tbody tr:focus-within .ak-row-actions", components);
    }

    [Fact]
    public void Shared_Ak_Tables_Keep_Headers_And_Values_On_The_Same_Column_Edge()
    {
        var components = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");

        Assert.Contains("body.boltz-rtl .ak-table > thead > tr > th.ak-col-num", components);
        Assert.Contains("body.boltz-rtl .ak-table > tbody > tr > td.ak-col-num", components);
        Assert.Contains("body.boltz-ltr .ak-table > thead > tr > th.ak-col-num", components);
        Assert.Contains("body.boltz-ltr .ak-table > tbody > tr > td.ak-col-num", components);
        Assert.Contains(".ak-table > tfoot > tr > :is(th, td).ak-col-num", components);
        Assert.Contains("td.ak-num:not(.ak-col-num)", components);
    }

    [Theory]
    [InlineData("src/PTGOilSystem.Web/Views/Home/Index.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Products/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/StorageTanks/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Customers/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Suppliers/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Employees/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Contracts/Index.cshtml", "ak-table")]
    [InlineData("src/PTGOilSystem.Web/Views/Contracts/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Loading/Details.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Dispatch/Create.cshtml", "_AkPageHeader")]
    [InlineData("src/PTGOilSystem.Web/Views/Sales/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Expenses/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Payments/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/CustomsDeclarations/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/CustomsPermitTurnover/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ContractAmendments/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ContractJourney/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Inventory/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/PlattsRates/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ShipmentContracts/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/AccountStatements/Details.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_CreateModalShell.cshtml", "ak-modal")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_EmptyState.cshtml", "ak-empty")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_Pagination.cshtml", "ak-pager")]
    public void Razor_View_Uses_The_Current_Ptg_Marker_Or_Intentional_Compat_Bridge(
        string relativePath,
        string expectedMarker)
    {
        var view = ReadRepoFile(relativePath);

        Assert.Contains(expectedMarker, view);
    }

    [Theory]
    [InlineData("~/css/ptg/37-masterdata-cards.css", "src/PTGOilSystem.Web/wwwroot/css/ptg/37-masterdata-cards.css")]
    [InlineData("~/css/design-system/core.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/core.css")]
    [InlineData("~/css/design-system/layout.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/layout.css")]
    [InlineData("~/css/design-system/components.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/components.css")]
    [InlineData("~/css/design-system/pages.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/pages.css")]
    [InlineData("~/css/view-structure.css", "src/PTGOilSystem.Web/wwwroot/css/view-structure.css")]
    public void Legacy_Stylesheet_Is_Not_An_Active_Layout_Dependency(string layoutReference, string relativePath)
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var modalLayout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_ModalLayout.cshtml");

        Assert.DoesNotContain(layoutReference, layout);
        Assert.DoesNotContain(layoutReference, modalLayout);
        Assert.False(File.Exists(GetRepoPath(relativePath)), $"Legacy stylesheet must not be restored: {relativePath}");
    }

    [Fact]
    public void Excel_Import_Scripts_Reinitialise_After_An_Spa_Page_Swap()
    {
        var spaNav = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/spa-nav.js");
        var excelImport = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/excel-import.js");
        var customsImport = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/customs-import.js");

        // The swap replaces the page DOM and announces only "ptg:page-ready". An uploader that
        // waits for any other signal is never initialised after in-app navigation, so its file
        // picker stays unbound and the start button stays disabled.
        Assert.Contains("dispatchEvent(new CustomEvent(\"ptg:page-ready\"", spaNav);
        Assert.Contains("window.addEventListener('ptg:page-ready'", excelImport);
        Assert.Contains("window.addEventListener(\"ptg:page-ready\"", customsImport);
    }

    [Fact]
    public void Contracts_List_Uses_The_Shared_Row_Selection_Contract()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Contracts/Index.cshtml");

        Assert.Contains("data-operations-list", view);
        Assert.Contains("class=\"ak-col-check\"", view);
        Assert.Contains("data-ops-select-all", view);
        Assert.Contains("data-ops-row-select", view);
        Assert.Contains("_OperationsListFooter", view);
        Assert.Contains("colspan=\"7\"", view);
        Assert.True(
            view.IndexOf("data-ops-select-all", StringComparison.Ordinal)
            < view.IndexOf("@T(\"قرارداد\", \"Contract\")", StringComparison.Ordinal));
        Assert.Contains("class=\"ak-col-grow\"", view);
        Assert.Contains(">@item.DisplayLabel</a>", view);
        Assert.DoesNotContain("<div class=\"ak-muted\" dir=\"ltr\">@item.ContractNumber</div>", view);
        // Current list operations cancel and archive through the lifecycle
        // service; restoring the former hard-delete form would discard history.
        Assert.Contains("<partial name=\"_LifecycleActionForms\"", view);
        Assert.Contains("item.Status == ContractStatus.Cancelled, currentReturnUrl", view);
        Assert.Contains("RoleAccessRules.CanManageData(User)", view);
        Assert.DoesNotContain("asp-action=\"Delete\"", view);
        var lifecycle = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_LifecycleActionForms.cshtml");
        Assert.Contains("RoleAccessRules.CanManageData(User)", lifecycle);
        Assert.Contains("asp-action=\"Cancel\" method=\"post\" data-no-spa=\"true\"", lifecycle);
        Assert.Contains("asp-action=\"DeleteCancelled\" method=\"post\" data-no-spa=\"true\"", lifecycle);
        Assert.Equal(2, lifecycle.Split("@Html.AntiForgeryToken()", StringSplitOptions.None).Length - 1);
        Assert.Contains("name=\"reason\" class=\"ak-input\" required", lifecycle);
        Assert.Contains("name=\"returnUrl\" value=\"@Model.ReturnUrl\"", lifecycle);
        Assert.Contains("data-ptg-confirm=\"true\"", lifecycle);
        Assert.Contains("class=\"dropdown-item ak-danger\"", lifecycle);
    }

    [Fact]
    public void Logout_Forms_Use_Native_Post_Without_Spa_Or_Submit_Guard_Interception()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var spaNav = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/spa-nav.js");

        Assert.Equal(2, layout.Split("asp-action=\"Logout\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, layout.Split("data-no-spa=\"true\" data-no-submit-guard", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("/logout/i.test(action)", spaNav);
    }

    [Fact]
    public void Shared_Stat_Cards_Fit_Long_Values_Without_Ellipsis()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var component = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/StatCard/Default.cshtml");
        var script = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/stat-card-fit.js");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/52-stat-card.css");

        Assert.Contains("~/js/stat-card-fit.js", layout);
        Assert.Contains("data-ak-stat-value", component);
        Assert.Contains("new ResizeObserver", script);
        Assert.Contains("value.clientWidth / value.scrollWidth", script);
        Assert.Contains("window.addEventListener(\"ptg:page-ready\"", script);
        Assert.Contains("text-overflow: clip", css);
    }

    [Fact]
    public void Confirmed_Ux_Workflows_Stay_Real_Compact_And_Lazy()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var pageHeaderCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var loadingDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");
        var loadingIndex = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Index.cshtml");
        var salesDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Details.cshtml");
        var expenseDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/Expenses/Details.cshtml");
        var inventoryOperations = ReadRepoFile("src/PTGOilSystem.Web/Views/Reports/InventoryOperations.cshtml");
        var reports = ReadRepoFile("src/PTGOilSystem.Web/Views/Reports/Index.cshtml");
        var reportsController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ReportsController.cs");
        var migration = ReadRepoFile("src/PTGOilSystem.Migrations/Migrations/20260808120000_NormalizeRblCurrencyReferenceToRub.cs");

        Assert.DoesNotContain("data-search-open", layout);
        Assert.DoesNotContain("data-global-search", layout);
        Assert.DoesNotContain("~/js/global-search.js", layout);
        Assert.DoesNotContain("onclick=\"return false;\"", layout);
        Assert.DoesNotContain("ptg-account-link-badge", layout);
        Assert.DoesNotContain("ptg-account-switch-avatar", layout);
        Assert.Contains(".ak-page-header:not(.ak-detail-header) .ak-page-title", pageHeaderCss);
        Assert.DoesNotContain("display: none !important", pageHeaderCss);

        Assert.DoesNotContain("data-loading-expense-prefetch=\"false\"", loadingDetails);
        Assert.DoesNotContain("data-receipt-remote-modal", loadingDetails);
        Assert.DoesNotContain("<partial name=\"_LoadingExpenseEditor\"", loadingDetails);
        Assert.DoesNotContain("<partial name=\"~/Views/LoadingReceipts/_ReceiptCreateForm.cshtml\"", loadingDetails);
        Assert.Contains("Model.TotalCount, 20", loadingIndex);
        Assert.Contains("salesTransactionId = Model.Id", salesDetails);
        Assert.Contains("customerId = Model.CustomerId", salesDetails);
        Assert.Contains("\"Details\", \"Ledger\"", expenseDetails);
        Assert.Contains("Url.Action(warning.Action, warning.Controller, warning.RouteValues)", inventoryOperations);
        // مرکز گزارشات فقط یک دسته‌بندی دارد و آن در کنترلر است؛ صفحه دستهٔ موازی
        // نمی‌سازد، وگرنه هر گزارش تازه می‌تواند از تب‌ها بیفتد.
        Assert.Contains("Model.Groups", reports);
        Assert.DoesNotContain("managementReportKeys", reports);
        Assert.DoesNotContain("financePartyReportKeys", reports);
        Assert.Contains("وضعیت شرکت", reportsController);
        Assert.Contains("کنترول و اسناد", reportsController);

        Assert.Contains("UPDATE \"Currencies\"", migration);
        Assert.Contains("SET \"Code\" = 'RUB'", migration);
        Assert.DoesNotContain("PaymentTransactions", migration);
        Assert.DoesNotContain("LedgerEntries", migration);
    }

    [Fact]
    public void Shared_Shell_Enforces_Accessible_Responsive_Component_Contracts()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var baseCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/02-base.css");
        var responsiveCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/10-responsive.css");
        var tabsCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/16-system-tabs.css");
        var componentsCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var typographyCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/71-typography.css");

        Assert.Contains("class=\"ptg-skip-link\"", layout);
        Assert.Contains("id=\"ptg-main-content\" tabindex=\"-1\"", layout);
        Assert.Contains(".ptg-skip-link:focus-visible", baseCss);
        Assert.Contains("\"IRANSans\", \"Vazirmatn\"", baseCss);

        Assert.Contains("--ptg-radius-card: var(--radius-card)", responsiveCss);
        Assert.DoesNotContain("--ptg-radius-card: 22px", responsiveCss);
        Assert.Contains("--ak-control-height: 44px", responsiveCss);
        Assert.Contains("@media (hover: none), (pointer: coarse)", componentsCss);
        Assert.Contains("overscroll-behavior-x: contain", componentsCss);
        Assert.Contains("min-height: 44px", componentsCss);
        Assert.Contains("font-size: 16px !important", typographyCss);
        Assert.DoesNotContain("transition: all", tabsCss);

        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/js/global-search.js"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml.css"));
    }

    [Fact]
    public void Razor_Images_Reserve_Layout_Space()
    {
        var viewsRoot = GetRepoPath("src/PTGOilSystem.Web/Views");
        var imagePattern = new System.Text.RegularExpressions.Regex(
            @"<img\b[^>]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (var viewPath in Directory.GetFiles(viewsRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            var view = File.ReadAllText(viewPath);

            foreach (System.Text.RegularExpressions.Match image in imagePattern.Matches(view))
            {
                Assert.True(
                    image.Value.Contains("width=", StringComparison.OrdinalIgnoreCase)
                    && image.Value.Contains("height=", StringComparison.OrdinalIgnoreCase),
                    $"Image in '{Path.GetRelativePath(viewsRoot, viewPath)}' must reserve width and height: {image.Value}");
            }
        }
    }

    private static string ReadPtgCss()
    {
        var ptgRoot = GetRepoPath("src/PTGOilSystem.Web/wwwroot/css/ptg");

        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(ptgRoot, "*.css")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath));

    private static bool RepoFileExists(string relativePath)
        => File.Exists(GetRepoPath(relativePath));

    /// <summary>Reads a `--token: NNNpx;` custom property out of a CSS file.</summary>
    private static int ReadPixelToken(string css, string token)
    {
        var match = System.Text.RegularExpressions.Regex.Match(css, $@"{token}:\s*(\d+)px");
        Assert.True(match.Success, $"CSS custom property '{token}' with a px value was not found.");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetRepoPath(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(sourceFilePath) ?? string.Empty,
                 })
        {
            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src", "PTGOilSystem.Web"))
                    && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                {
                    return Path.GetFullPath(Path.Combine(directory.FullName, normalizedPath));
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for ShellViewStructureTests.");
    }
}
