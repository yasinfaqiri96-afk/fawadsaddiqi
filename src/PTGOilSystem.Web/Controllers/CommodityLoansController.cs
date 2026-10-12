using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.CommodityLoans;
using PTGOilSystem.Web.Models.Custody;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.CommodityLoans;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

// قرض جنسی — قرض‌دادن از موجودی ملکی و قرض‌گرفتن با تعهد برگشت جنس. جدا از امانی.
[Authorize]
public class CommodityLoansController(
    ApplicationDbContext db,
    ICommodityLoanService loans,
    CommodityLoanReadService reader,
    ISystemCompanyProvider company,
    IFormTokenGuard formTokens,
    IAfghanistanBusinessClock businessClock,
    IOptions<AccountingOptions> accounting) : Controller
{
    public async Task<IActionResult> Index(CommodityLoanDirection? direction = null, bool cancelled = false)
        => View(new CommodityLoanIndexViewModel
        {
            Loans = await reader.ListAsync(direction, cancelled),
            Direction = direction,
            IncludeCancelled = cancelled,
            Today = businessClock.Today
        });

    public async Task<IActionResult> Details(int id)
    {
        var detail = await reader.GetAsync(id);
        return detail is null ? NotFound() : View(detail);
    }

    public async Task<IActionResult> Report(DateTime? asOf = null)
        => View(await reader.ReportAsync(asOf ?? businessClock.Today));

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(CommodityLoanDirection direction = CommodityLoanDirection.Lend)
    {
        var today = businessClock.Today;
        return View(await FormViewAsync(new CommodityLoanForm { Direction = direction, LoanDate = today, DueDate = today.AddDays(30) }));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CommodityLoanForm form, [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!CustodyParty.TryParse(form.CounterpartyKey, out var partyType, out var partyId))
            ModelState.AddModelError(nameof(form.CounterpartyKey), "طرف قرض را انتخاب کنید.");
        if (!form.ProductId.HasValue) ModelState.AddModelError(nameof(form.ProductId), "جنس را انتخاب کنید.");
        if (form.QuantityMt is not > 0m) ModelState.AddModelError(nameof(form.QuantityMt), "مقدار را وارد کنید.");
        var location = CustodyLocationKey.Parse(form.LocationKey);
        if (location is null) ModelState.AddModelError(nameof(form.LocationKey), "محل را انتخاب کنید.");
        if (ModelState.IsValid)
        {
            try
            {
                var id = await loans.CreateAsync(new CommodityLoanInput
                {
                    Direction = form.Direction,
                    CounterpartyType = partyType,
                    CounterpartyId = partyId,
                    ProductId = form.ProductId!.Value,
                    QualitySpec = form.QualitySpec,
                    QuantityMt = form.QuantityMt!.Value,
                    LoanDate = form.LoanDate,
                    DueDate = form.DueDate,
                    ReturnObligation = form.ReturnObligation,
                    BorrowBasis = form.Direction == CommodityLoanDirection.Borrow ? form.BorrowBasis : null,
                    TerminalId = location!.TerminalId,
                    StorageTankId = location.StorageTankId,
                    ValuationPolicy = form.ValuationPolicy,
                    AgreedUnitValueUsd = form.Direction == CommodityLoanDirection.Borrow ? form.AgreedUnitValueUsd : null,
                    ValuationReference = form.ValuationReference,
                    LoanAccountId = form.LoanAccountId,
                    DifferenceAccountId = form.DifferenceAccountId,
                    QuantityTolerancePercent = form.QuantityTolerancePercent,
                    CashSettlementAllowed = form.CashSettlementAllowed,
                    CashSettlementUnitPriceUsd = form.CashSettlementUnitPriceUsd,
                    CashSettlementReference = form.CashSettlementReference,
                    AgreementReference = form.AgreementReference,
                    Notes = form.Notes,
                    FormToken = formToken
                });
                TempData["ok"] = "قرض جنسی ثبت شد.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ex is BusinessRuleException or AccountingValidationException)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
            catch (DbUpdateException ex) when (formTokens.IsDuplicate(ex))
            {
                TempData["err"] = "این قرض قبلاً ثبت شده است و دوباره ثبت نشد.";
                return RedirectToAction(nameof(Index));
            }
        }
        return View(await FormViewAsync(form));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Settle(int id, CommodityLoanSettlementKind kind = CommodityLoanSettlementKind.InKindReturn)
    {
        var detail = await reader.GetAsync(id);
        if (detail is null) return NotFound();
        if (detail.Row.OutstandingMt <= 0m)
        {
            TempData["err"] = "این قرض ماندهٔ باز ندارد.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var form = new CommodityLoanSettlementForm
        {
            LoanId = id, Kind = kind, Date = businessClock.Today,
            QuantityMt = detail.Row.OutstandingMt,
            LocationKey = CustodyLocationKey.Key(detail.Loan.TerminalId, detail.Loan.StorageTankId)
        };
        return View(await SettlementViewAsync(form, detail));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Settle(CommodityLoanSettlementForm form, [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var detail = await reader.GetAsync(form.LoanId);
        if (detail is null) return NotFound();
        if (form.QuantityMt is not > 0m) ModelState.AddModelError(nameof(form.QuantityMt), "مقدار را وارد کنید.");
        var location = CustodyLocationKey.Parse(form.LocationKey);
        if (form.Kind == CommodityLoanSettlementKind.InKindReturn && location is null)
            ModelState.AddModelError(nameof(form.LocationKey), "محل را انتخاب کنید.");
        if (ModelState.IsValid)
        {
            try
            {
                await loans.SettleAsync(new CommodityLoanSettlementInput
                {
                    LoanId = form.LoanId,
                    Kind = form.Kind,
                    SettlementDate = form.Date,
                    QuantityMt = form.QuantityMt!.Value,
                    TerminalId = form.Kind == CommodityLoanSettlementKind.InKindReturn ? location!.TerminalId : null,
                    StorageTankId = form.Kind == CommodityLoanSettlementKind.InKindReturn ? location!.StorageTankId : null,
                    CashAccountId = form.Kind == CommodityLoanSettlementKind.CashSettlement ? form.CashAccountId : null,
                    DocumentNumber = form.DocumentNumber,
                    Notes = form.Notes,
                    FormToken = formToken
                });
                TempData["ok"] = $"{CommodityLoanSettlementForm.Title(form.Kind)} ثبت شد.";
                return RedirectToAction(nameof(Details), new { id = form.LoanId });
            }
            catch (Exception ex) when (ex is BusinessRuleException or AccountingValidationException)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
            catch (DbUpdateException ex) when (formTokens.IsDuplicate(ex))
            {
                TempData["err"] = "این تسویه قبلاً ثبت شده است و دوباره ثبت نشد.";
                return RedirectToAction(nameof(Details), new { id = form.LoanId });
            }
        }
        return View(await SettlementViewAsync(form, detail));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSettlement(int id, int loanId, string? reason)
    {
        try
        {
            await loans.CancelSettlementAsync(id, reason ?? "");
            TempData["ok"] = "تسویه لغو شد و اثر موجودی، صندوق و دفتر کل آن برگشت.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or AccountingValidationException)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id = loanId });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        try
        {
            await loans.CancelLoanAsync(id, reason ?? "");
            TempData["ok"] = "قرض جنسی لغو شد و موجودی و دفتر کل برگشت.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or AccountingValidationException)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<CommodityLoanFormViewModel> FormViewAsync(CommodityLoanForm form)
    {
        var companyId = await company.FindOwnerCompanyIdAsync();
        var accounts = companyId is null ? [] : await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsActive)
            .OrderBy(a => a.Code)
            .Select(a => new { a.Id, a.Code, a.Name, a.AccountType })
            .ToListAsync();
        return new CommodityLoanFormViewModel
        {
            Form = form,
            GlEnabled = accounting.Value.Enabled,
            Counterparties = await CounterpartiesAsync(),
            Products = (await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name)
                    .Select(p => new { p.Id, Name = p.NamePersian ?? p.Name }).ToListAsync())
                .Select(p => (p.Id, p.Name)).ToList(),
            Locations = await LocationsAsync(),
            AssetAccounts = accounts.Where(a => a.AccountType == AccountType.Asset).Select(a => (a.Id, $"{a.Code} · {a.Name}")).ToList(),
            LiabilityAccounts = accounts.Where(a => a.AccountType == AccountType.Liability).Select(a => (a.Id, $"{a.Code} · {a.Name}")).ToList(),
            DifferenceAccounts = accounts.Where(a => a.AccountType is AccountType.Revenue or AccountType.Expense).Select(a => (a.Id, $"{a.Code} · {a.Name}")).ToList()
        };
    }

    private async Task<CommodityLoanSettlementViewModel> SettlementViewAsync(CommodityLoanSettlementForm form, CommodityLoanDetail detail)
        => new()
        {
            Form = form,
            Detail = detail,
            Locations = await LocationsAsync(),
            CashAccounts = (await db.CashAccounts.AsNoTracking()
                    .Where(a => a.IsActive && (a.Currency == "USD" || a.AccountType == CashAccountType.Mixed))
                    .OrderBy(a => a.Name).Select(a => new { a.Id, a.Name, a.Code }).ToListAsync())
                .Select(a => (a.Id, $"{a.Name} ({a.Code})")).ToList()
        };

    private async Task<IReadOnlyList<CommodityLoanOption>> CounterpartiesAsync()
    {
        var customers = await db.Customers.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new { x.Id, Name = x.NamePersian ?? x.Name }).ToListAsync();
        var suppliers = await db.Suppliers.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new { x.Id, Name = x.NamePersian ?? x.Name }).ToListAsync();
        var companies = await db.Companies.AsNoTracking().Where(x => x.IsActive && !x.IsSystemOwner)
            .Select(x => new { x.Id, Name = x.NamePersian ?? x.Name }).ToListAsync();
        var partners = await db.Partners.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new { x.Id, Name = x.NamePersian ?? x.Name }).ToListAsync();
        return companies.Select(x => new CommodityLoanOption(CustodyParty.Key(AccountingPartyType.Company, x.Id), "شرکت · " + x.Name))
            .Concat(customers.Select(x => new CommodityLoanOption(CustodyParty.Key(AccountingPartyType.Customer, x.Id), "مشتری · " + x.Name)))
            .Concat(suppliers.Select(x => new CommodityLoanOption(CustodyParty.Key(AccountingPartyType.Supplier, x.Id), "فروشنده · " + x.Name)))
            .Concat(partners.Select(x => new CommodityLoanOption(CustodyParty.Key(AccountingPartyType.Partner, x.Id), "شریک · " + x.Name)))
            .ToList();
    }

    private async Task<IReadOnlyList<CommodityLoanOption>> LocationsAsync()
    {
        var terminals = await db.Terminals.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name }).ToListAsync();
        var tanks = await db.StorageTanks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.TankCode)
            .Select(t => new { t.Id, t.TerminalId, Name = t.DisplayName ?? t.TankCode }).ToListAsync();
        var options = new List<CommodityLoanOption>();
        foreach (var terminal in terminals)
        {
            var own = tanks.Where(t => t.TerminalId == terminal.Id).ToList();
            if (own.Count == 0)
                options.Add(new CommodityLoanOption(CustodyLocationKey.Key(terminal.Id, null), terminal.Name));
            else
                options.AddRange(own.Select(t => new CommodityLoanOption(CustodyLocationKey.Key(terminal.Id, t.Id), $"{terminal.Name} / {t.Name}")));
        }
        return options;
    }
}
