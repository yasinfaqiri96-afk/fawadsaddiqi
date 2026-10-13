using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class FinancialReportsViewStructureTests
{
    [Fact]
    public void CashFlow_Keeps_Filter_Export_Reconciliation_And_Trend_Contracts()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Reports/CashFlow.cshtml");

        Assert.Contains("new(\"CashAccountId\"", view);
        Assert.Contains("new(\"CompanyId\"", view);
        Assert.Contains("new(\"PaymentKind\"", view);
        Assert.Contains("new PTGOilSystem.Web.Services.Exports.ExportMenuModel(\"CashFlowExport\")", view);
        Assert.Contains("Model.OpeningBalanceUsd", view);
        Assert.Contains("Model.ClosingBalanceUsd", view);
        Assert.Contains("Model.PeriodRows", view);
        Assert.Contains("Model.ScopeWarning", view);
        Assert.Contains("Model.PartnerFundedCount", view);
        Assert.Contains("@row.Currency", view);
    }

    [Fact]
    public void CompanyOverview_Keeps_Receivables_And_Payables_In_One_Responsive_Row()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Reports/CompanyOverview.cshtml");

        Assert.Contains("<div class=\"row g-4 company-overview-balances\">", view);
        Assert.Equal(2, view.Split("class=\"col-12 col-lg-6\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Model.ActionNeededCount", view);
        Assert.DoesNotContain("Model.TopContracts", view);
        Assert.DoesNotContain("finance-page-lead", view);
        Assert.DoesNotContain("class=\"ak-note\"", view);
    }

    [Fact]
    public void ContractPnl_Renders_One_Row_Per_Contract_And_Links_Cost_Details()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Reports/ContractPnl.cshtml");

        // هر قرارداد فقط یک ردیف دارد؛ ریز مصارف در صفحهٔ خود قرارداد دیده می‌شود.
        Assert.DoesNotContain("<details class=\"ak-advanced m-0\">", view);
        Assert.DoesNotContain("ak-row-muted", view);
        Assert.Contains("asp-controller=\"ContractJourney\"", view);
        // کارت‌ها باید همان دامنه‌ای را جمع بزنند که جدول نشان می‌دهد، نه قراردادهای فروش.
        Assert.Contains("Model.TotalDirectSaleRevenueUsd", view);
        Assert.Contains("Model.TotalContractValueUsd", view);
        Assert.Contains("Model.TotalContractExpensesUsd", view);
        Assert.Contains("Model.TotalContractProfitUsd", view);
        // قیمت خرید و مصارف جدا؛ فیصدی و سود محقق از جدول برداشته شده‌اند.
        Assert.Contains("row.PurchaseValueUsd", view);
        Assert.Contains("row.ExpensesUsd", view);
        Assert.DoesNotContain("row.RealizedNetProfitUsd", view);
        Assert.DoesNotContain("row.MarginPercent", view);
        Assert.DoesNotContain("Model.TotalSalesRevenueUsd", view);
        Assert.DoesNotContain("Model.SaleRows", view);
        Assert.DoesNotContain("قراردادهای فروش", view);
        Assert.DoesNotContain("class=\"ak-note\"", view);
    }

    [Fact]
    public void ReportHub_Hides_ContractJourney_Report_Without_Changing_Its_Route()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Reports/Index.cshtml");

        Assert.Contains("string.Equals(c.Controller, \"ContractJourney\"", view);
        Assert.Contains("string.Equals(c.Action, \"Index\"", view);
        Assert.DoesNotContain("[\"ContractJourney|Index\"]", view);
    }

    private static string ReadRepoFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "PTGOilSystem.Web")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
