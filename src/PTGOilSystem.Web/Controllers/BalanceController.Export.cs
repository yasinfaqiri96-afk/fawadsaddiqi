using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Balance;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class BalanceController
{
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> ContractsExport(string? format, [FromQuery] ContractsBalanceFilterViewModel? filter = null)
    {
        var model = await GetViewModelAsync<ContractsBalanceViewModel>(Contracts(filter, page: 0));
        var items = model.Items.ToList();
        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Contracts_Balance",
            TitleFa = "بیلانس قراردادها",
            TitleEn = "Contract Balances",
            KnownRowCount = items.Count,
            ForceLandscape = true,
            Columns =
            [
                new("قرارداد", "Contract", Width: 18), new("نوع", "Type", Width: 12), new("وضعیت", "Status", Width: 12),
                new("تأمین‌کننده", "Supplier", Width: 22), new("جنس", "Product", Width: 16),
                new("واحد", "Unit", Width: 12), new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
                new("محموله‌ها", "Shipments", TabularExportValueType.Integer, 12), new("فروش USD", "Sales USD", TabularExportValueType.Number, 16),
                new("مصارف USD", "Expenses USD", TabularExportValueType.Number, 16), new("اسناد دفتر", "Ledger entries", TabularExportValueType.Integer, 13),
                new("مانده USD", "Balance USD", TabularExportValueType.Number, 17)
            ],
            Rows = items.Select(item => new TabularExportRow(
            [
                TabularExportCell.Text(item.ContractNumber), TabularExportCell.Text(item.ContractTypeName), TabularExportCell.Text(item.StatusName),
                TabularExportCell.Text(item.SupplierName), TabularExportCell.Text(item.ProductName),
                TabularExportCell.Text(item.ContractUnitText), TabularExportCell.Number(item.QuantityMt), TabularExportCell.Integer(item.ShipmentCount),
                TabularExportCell.Number(item.TotalSalesUsd), TabularExportCell.Number(item.TotalExpensesUsd), TabularExportCell.Integer(item.RelatedLedgerCount),
                TabularExportCell.Number(item.BaseBalanceUsd)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(null), TabularExportCell.Integer(items.Sum(i => (long)i.ShipmentCount)),
                TabularExportCell.Number(model.FilteredTotalSalesUsd), TabularExportCell.Number(model.FilteredTotalExpensesUsd),
                TabularExportCell.Integer(items.Sum(i => (long)i.RelatedLedgerCount)),
                TabularExportCell.Number(model.FilteredBalanceUsd)
            ])
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> CustomersExport(string? format, [FromQuery] CustomersBalanceFilterViewModel? filter = null)
    {
        filter ??= new CustomersBalanceFilterViewModel();
        var model = await GetViewModelAsync<CustomersBalanceViewModel>(Customers(filter, page: 0));
        var items = model.Items.ToList();
        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Customers_Balance",
            TitleFa = "گردش حساب مشتریان",
            TitleEn = "Customer Accounts",
            KnownRowCount = items.Count,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("جستجو / Search", filter.Search)),
            Columns =
            [
                new("نام مشتری", "Customer", Width: 28),
                new("مجموع فروش USD", "Total sales USD", TabularExportValueType.Number, 18),
                new("مجموع رسید USD", "Total received USD", TabularExportValueType.Number, 18),
                new("مانده USD", "Balance USD", TabularExportValueType.Number, 18),
                new("اسناد", "Documents", TabularExportValueType.Integer, 12)
            ],
            Rows = items.Select(item => new TabularExportRow(
            [
                TabularExportCell.Text(item.Name), TabularExportCell.Number(item.TotalSalesUsd),
                TabularExportCell.Number(item.TotalReceivedUsd), TabularExportCell.Number(item.BaseBalanceUsd),
                TabularExportCell.Integer(item.RelatedLedgerCount)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Number(model.FilteredTotalSalesUsd),
                TabularExportCell.Number(model.FilteredTotalReceivedUsd), TabularExportCell.Number(model.FilteredBalanceUsd),
                TabularExportCell.Integer(items.Sum(i => (long)i.RelatedLedgerCount))
            ])
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> SuppliersExport(string? format, [FromQuery] SuppliersBalanceFilterViewModel? filter = null)
    {
        filter ??= new SuppliersBalanceFilterViewModel();
        var model = await GetViewModelAsync<SuppliersBalanceViewModel>(Suppliers(filter, page: 0));
        var items = model.Items.ToList();
        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Suppliers_Balance",
            TitleFa = "گردش حساب تأمین‌کنندگان",
            TitleEn = "Supplier Accounts",
            KnownRowCount = items.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("جستجو / Search", filter.Search)),
            Columns =
            [
                new("نام تأمین‌کننده", "Supplier", Width: 26),
                new("قراردادها", "Contracts", TabularExportValueType.Integer, 12),
                new("جمع خرید USD", "Total purchases USD", TabularExportValueType.Number, 18),
                new("جمع پرداخت USD", "Total paid USD", TabularExportValueType.Number, 18),
                new("جمع فروش USD", "Total sales USD", TabularExportValueType.Number, 18),
                new("مانده USD", "Balance USD", TabularExportValueType.Number, 18)
            ],
            Rows = items.Select(item => new TabularExportRow(
            [
                TabularExportCell.Text(item.Name), TabularExportCell.Integer(item.RelatedContractsCount),
                TabularExportCell.Number(item.TotalPurchaseUsd), TabularExportCell.Number(item.TotalPaidUsd),
                TabularExportCell.Number(item.TotalSalesUsd), TabularExportCell.Number(item.BaseBalanceUsd)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Integer(items.Sum(i => (long)i.RelatedContractsCount)),
                TabularExportCell.Number(model.FilteredTotalPurchaseUsd), TabularExportCell.Number(model.FilteredTotalPaidUsd),
                TabularExportCell.Number(model.FilteredTotalSalesUsd), TabularExportCell.Number(model.FilteredBalanceUsd)
            ])
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> ContractsCsv([FromQuery] ContractsBalanceFilterViewModel? filter = null)
    {
        var model = await GetViewModelAsync<ContractsBalanceViewModel>(Contracts(filter, page: 0));
        return CsvExportSupport.File(this, "contracts-balance.csv",
            ["Contract", "Type", "Status", "Supplier", "Product", "Unit", "QuantityMt", "ShipmentCount", "SalesUsd", "ExpensesUsd", "LedgerCount", "BaseBalanceUsd"],
            model.Items.Select(i => new[]
            {
                i.ContractNumber, i.ContractTypeName, i.StatusName, i.SupplierName, i.ProductName,
                i.ContractUnitText, CsvExportSupport.Decimal(i.QuantityMt), i.ShipmentCount.ToString(), CsvExportSupport.Decimal(i.TotalSalesUsd),
                CsvExportSupport.Decimal(i.TotalExpensesUsd), i.RelatedLedgerCount.ToString(), CsvExportSupport.Decimal(i.BaseBalanceUsd)
            }));
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> CustomersCsv([FromQuery] CustomersBalanceFilterViewModel? filter = null)
    {
        var model = await GetViewModelAsync<CustomersBalanceViewModel>(Customers(filter, page: 0));
        return CsvExportSupport.File(this, "customers-balance.csv",
            ["Customer", "SalesUsd", "ReceivedUsd", "BaseBalanceUsd", "Documents"],
            model.Items.Select(i => new[]
            {
                i.Name, CsvExportSupport.Decimal(i.TotalSalesUsd), CsvExportSupport.Decimal(i.TotalReceivedUsd),
                CsvExportSupport.Decimal(i.BaseBalanceUsd), i.RelatedLedgerCount.ToString()
            }));
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> SuppliersCsv([FromQuery] SuppliersBalanceFilterViewModel? filter = null)
    {
        var model = await GetViewModelAsync<SuppliersBalanceViewModel>(Suppliers(filter, page: 0));
        return CsvExportSupport.File(this, "suppliers-balance.csv",
            ["Supplier", "Contracts", "PurchaseUsd", "PaidUsd", "SalesUsd", "BaseBalanceUsd"],
            model.Items.Select(i => new[]
            {
                i.Name, i.RelatedContractsCount.ToString(), CsvExportSupport.Decimal(i.TotalPurchaseUsd),
                CsvExportSupport.Decimal(i.TotalPaidUsd), CsvExportSupport.Decimal(i.TotalSalesUsd),
                CsvExportSupport.Decimal(i.BaseBalanceUsd)
            }));
    }

    private static async Task<T> GetViewModelAsync<T>(Task<IActionResult> actionTask)
    {
        var result = await actionTask;
        var view = (ViewResult)result;
        return (T)view.Model!;
    }
}
