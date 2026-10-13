using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class CustomersController
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, string? q, CancellationToken cancellationToken)
    {
        var query = _db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => (p.Code != null && p.Code.Contains(q)) || p.Name.Contains(q)
                || (p.NamePersian != null && p.NamePersian.Contains(q)) || (p.ContactPerson != null && p.ContactPerson.Contains(q)));

        var rows = await query.OrderBy(p => p.Name).Select(p => new
        {
            p.Id, p.Code, p.Name, p.NamePersian, p.Country, p.Phone, p.Address, p.IsActive
        }).ToListAsync(cancellationToken);

        var balances = (await _partyBalances.GetBalancesAsync(
                new ManagementReportFilterViewModel(),
                cancellationToken,
                [PartyStatementPartyType.Customer]))
            .ToDictionary(row => row.PartyId, row => row.ClosingBalanceUsd);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Customers", TitleFa = "مشتریان", TitleEn = "Customers", KnownRowCount = rows.Count,
            Filters = TabularExportSupport.FilterSummary(("جستجو / Search", q)),
            Columns =
            [
                new("کد", "Code", Width: 12), new("نام", "Name", Width: 22), new("نام فارسی", "Persian name", Width: 22),
                new("کشور", "Country", Width: 15), new("مانده حساب USD", "Balance USD", TabularExportValueType.Number, 16), new("تلفن", "Phone", Width: 16),
                new("آدرس", "Address", Width: 28, Wrap: true), new("فعال", "Active", TabularExportValueType.Boolean, 10)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.Code), TabularExportCell.Text(r.Name), TabularExportCell.Text(r.NamePersian),
                TabularExportCell.Text(r.Country), TabularExportCell.Number(balances.GetValueOrDefault(r.Id)), TabularExportCell.Text(r.Phone),
                TabularExportCell.Text(r.Address), TabularExportCell.Boolean(r.IsActive)
            ]))
        });
    }
}
