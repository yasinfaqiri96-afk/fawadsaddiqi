using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class CurrenciesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;

    public CurrenciesController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
    }

    public async Task<IActionResult> Index(string? q, bool? isActive, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 12);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 12;

        var query = _db.Currencies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c =>
                c.Code.Contains(term) ||
                c.Name.Contains(term) ||
                (c.NamePersian != null && c.NamePersian.Contains(term)) ||
                (c.Symbol != null && c.Symbol.Contains(term)));
        }
        if (isActive.HasValue)
            query = query.Where(c => c.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(await query
            .OrderBy(c => c.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Currencies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? NotFound() : View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? cloneFromId = null)
    {
        if (!cloneFromId.HasValue)
        {
            return View(new Currency { Code = SystemCurrency.BaseCurrencyCode, IsActive = true });
        }

        var source = await _db.Currencies
            .AsNoTracking()
            .FirstOrDefaultAsync(currency => currency.Id == cloneFromId.Value);
        if (source is null)
        {
            return NotFound();
        }

        return View(new Currency
        {
            Code = source.Code,
            Name = source.Name,
            NamePersian = source.NamePersian,
            Symbol = source.Symbol,
            IsActive = source.IsActive
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,Symbol,IsActive")] Currency model, string? returnUrl = null)
    {
        Normalize(model);
        await ValidateAsync(model, model.Id);

        if (!ModelState.IsValid)
            return View(model);

        _db.Currencies.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Currency),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Code", model.Code),
                ("Name", model.Name),
                ("NamePersian", model.NamePersian),
                ("Symbol", model.Symbol),
                ("IsActive", model.IsActive)));

        TempData["ok"] = "ارز با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Currencies.FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? NotFound() : View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,Symbol,IsActive")] Currency model)
    {
        if (id != model.Id)
            return BadRequest();

        Normalize(model);
        await ValidateAsync(model, id);

        if (!ModelState.IsValid)
            return View(model);

        var existing = await _db.Currencies.FirstOrDefaultAsync(x => x.Id == id);
        if (existing is null)
            return NotFound();

        var diff = AuditDiffFormatter.ForUpdate(
            ("Code", existing.Code, model.Code),
            ("Name", existing.Name, model.Name),
            ("NamePersian", existing.NamePersian, model.NamePersian),
            ("Symbol", existing.Symbol, model.Symbol),
            ("IsActive", existing.IsActive, model.IsActive));

        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.Symbol = model.Symbol;
        existing.IsActive = model.IsActive;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Currency), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش ارز انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Currencies.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null)
            return NotFound();

        if (string.Equals(item.Code, SystemCurrency.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            TempData["err"] = "حذف یا غیرفعال‌کردن ارز پایه سیستم مجاز نیست.";
            return RedirectToAction(nameof(Index));
        }

        var safety = await _deleteSafety.EvaluateCurrencyAsync(item.Code);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Currency), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("ارز");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("ارز")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("ارز");
            return RedirectToAction(nameof(Index));
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("Code", item.Code),
            ("Name", item.Name),
            ("Symbol", item.Symbol));
        _db.Currencies.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Currency), id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "ارز حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(Currency model, int currentId)
    {
        if (string.IsNullOrWhiteSpace(model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد ارز الزامی است.");

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "نام ارز الزامی است.");

        if (await _db.Currencies.AnyAsync(c => c.Id != currentId && c.Code == model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد ارز تکراری است.");
    }

    private static void Normalize(Currency model)
    {
        model.Code = SystemCurrency.Normalize(model.Code);
        if (string.Equals(model.Code, "RBL", StringComparison.OrdinalIgnoreCase))
        {
            model.Code = "RUB";
        }
        else if (string.Equals(model.Code, "AFG", StringComparison.OrdinalIgnoreCase))
        {
            model.Code = "AFN";
        }
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Symbol = string.IsNullOrWhiteSpace(model.Symbol) ? null : model.Symbol.Trim();
    }

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.Currencies.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Code, c.Name, c.Symbol, c.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }
}
