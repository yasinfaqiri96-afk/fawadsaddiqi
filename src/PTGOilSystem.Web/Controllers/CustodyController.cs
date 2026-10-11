using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Custody;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Custody;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

// امانی — تیلِ دیگران نزد ما، تیلِ ما نزد دیگران و موجودی فزیکی مخازن.
// هیچ اکشنی اینجا خرید، فروش، Ledger، Journal یا InventoryMovement نمی‌سازد.
[Authorize]
public class CustodyController(
    ApplicationDbContext db,
    ICustodyService custody,
    ICustodyReadService reader,
    IFormTokenGuard formTokens,
    IAfghanistanBusinessClock businessClock,
    ICustodySaleService sales,
    CustodyAccountingReconciliationService reconciliation) : Controller
{
    public async Task<IActionResult> Index(string tab = "others", string? owner = null, int? productId = null)
    {
        tab = tab is "ours" or "tanks" ? tab : "others";
        CustodyLotFilter filter = new(ProductId: productId);
        if (CustodyParty.TryParse(owner, out var ownerType, out var ownerId))
            filter = filter with { OwnerPartyType = ownerType, OwnerPartyId = ownerId };

        var model = new CustodyIndexViewModel
        {
            Tab = tab,
            OwnerKey = owner,
            ProductId = productId,
            Lots = await reader.GetLotSummariesAsync(filter),
            AtThirdParties = tab == "ours" ? await reader.GetCompanyStockAtThirdPartiesAsync() : [],
            Physical = tab == "tanks" ? await reader.GetPhysicalPositionsAsync() : [],
            Owners = await reader.GetOwnerOptionsAsync(),
            Products = await ProductsAsync()
        };
        return View(model);
    }

    public async Task<IActionResult> Details(int id)
    {
        var detail = await reader.GetLotDetailAsync(id);
        if (detail is null) return NotFound();
        return View(detail);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Receive(int? lotId = null, bool inTransit = false)
    {
        var form = new CustodyReceiveForm { LotId = lotId, InTransitOnly = inTransit && lotId is null, Date = businessClock.Today };
        CustodyLotSummary? transitLot = null;
        if (lotId.HasValue)
        {
            transitLot = (await reader.GetLotDetailAsync(lotId.Value))?.Summary;
            if (transitLot is null) return NotFound();
            form.QuantityMt = transitLot.LoadedQuantityMt is { } loaded && loaded > transitLot.ReceivedMt
                ? loaded - transitLot.ReceivedMt
                : null;
        }
        return View(await BuildReceiveViewModelAsync(form, transitLot));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(
        CustodyReceiveForm form,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        CustodyLotSummary? transitLot = form.LotId.HasValue ? (await reader.GetLotDetailAsync(form.LotId.Value))?.Summary : null;
        CustodyOwner? owner = null;
        if (!form.LotId.HasValue)
        {
            if (!CustodyParty.TryParse(form.OwnerKey, out var ownerType, out var ownerId))
                ModelState.AddModelError(nameof(form.OwnerKey), "مالک را انتخاب کنید.");
            else
                owner = new CustodyOwner(ownerType, ownerId);
            if (!form.ProductId.HasValue)
                ModelState.AddModelError(nameof(form.ProductId), "جنس را انتخاب کنید.");
        }

        var location = CustodyLocationKey.Parse(form.LocationKey);
        if (form.InTransitOnly && !form.LotId.HasValue)
        {
            if (form.LoadedQuantityMt is not > 0m)
                ModelState.AddModelError(nameof(form.LoadedQuantityMt), "مقدار بارگیری را وارد کنید.");
        }
        else
        {
            if (location is null)
                ModelState.AddModelError(nameof(form.LocationKey), "مخزن را انتخاب کنید.");
            if (form.QuantityMt is not > 0m)
                ModelState.AddModelError(nameof(form.QuantityMt), "مقدار رسید را وارد کنید.");
        }

        if (!ModelState.IsValid)
            return View(await BuildReceiveViewModelAsync(form, transitLot));

        try
        {
            int lotId;
            if (form.InTransitOnly && !form.LotId.HasValue)
            {
                lotId = await custody.RegisterInTransitAsync(new CustodyInTransitInput
                {
                    Owner = owner!,
                    ProductId = form.ProductId!.Value,
                    LoadedDate = form.LoadedDate ?? form.Date,
                    LoadedQuantityMt = form.LoadedQuantityMt!.Value,
                    OriginDescription = form.OriginDescription,
                    TransportReference = form.TransportReference,
                    DocumentNumber = form.DocumentNumber,
                    Notes = form.Notes,
                    FormToken = formToken
                });
                TempData["ok"] = "بارِ در راه ثبت شد. هنگام رسیدن، رسید آن را ثبت کنید.";
            }
            else
            {
                lotId = await custody.ReceiveAsync(new CustodyReceiptInput
                {
                    LotId = form.LotId,
                    Owner = owner,
                    ProductId = form.ProductId,
                    Location = location!,
                    ReceiptDate = form.Date,
                    QuantityMt = form.QuantityMt!.Value,
                    LoadedDate = form.LoadedDate,
                    LoadedQuantityMt = form.LoadedQuantityMt,
                    OriginDescription = form.OriginDescription,
                    TransportReference = form.TransportReference,
                    DocumentNumber = form.DocumentNumber,
                    Notes = form.Notes,
                    FormToken = formToken
                });
                TempData["ok"] = "ورود امانی ثبت شد. خرید یا بدهی برای شرکت ساخته نشد.";
            }
            return RedirectToAction(nameof(Details), new { id = lotId });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (DbUpdateException ex) when (formTokens.IsDuplicate(ex))
        {
            TempData["err"] = "این ورود قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }

        return View(await BuildReceiveViewModelAsync(form, transitLot));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Operation(int id, CustodyOperation op)
    {
        var lot = (await reader.GetLotDetailAsync(id))?.Summary;
        if (lot is null) return NotFound();
        if (lot.Status != CustodyLotStatus.Active)
        {
            TempData["err"] = "این بار هنوز رسید نشده یا لغو شده است.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var form = new CustodyOperationForm { LotId = id, Operation = op, Date = businessClock.Today };
        var positions = await reader.GetPositionsAsync(id);
        if (positions.Count == 1)
            form.LocationKey = CustodyLocationKey.Key(positions[0].TerminalId, positions[0].StorageTankId);
        return View(await BuildOperationViewModelAsync(form, lot));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Operation(
        CustodyOperationForm form,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var lot = (await reader.GetLotDetailAsync(form.LotId))?.Summary;
        if (lot is null) return NotFound();

        var location = CustodyLocationKey.Parse(form.LocationKey);
        if (form.QuantityMt is not > 0m)
            ModelState.AddModelError(nameof(form.QuantityMt), "مقدار را وارد کنید.");
        if (form.Operation != CustodyOperation.Hold && location is null)
            ModelState.AddModelError(nameof(form.LocationKey), "محل را انتخاب کنید.");
        var toLocation = CustodyLocationKey.Parse(form.ToLocationKey);
        if (form.Operation == CustodyOperation.Transfer && toLocation is null)
            ModelState.AddModelError(nameof(form.ToLocationKey), "مخزن مقصد را انتخاب کنید.");
        CustodyOwner? newOwner = null;
        if (form.Operation == CustodyOperation.Ownership)
        {
            if (CustodyParty.TryParse(form.NewOwnerKey, out var type, out var ownerId))
                newOwner = new CustodyOwner(type, ownerId);
            else
                ModelState.AddModelError(nameof(form.NewOwnerKey), "مالک جدید را انتخاب کنید.");
        }
        if (form.Operation == CustodyOperation.Deliver && string.IsNullOrWhiteSpace(form.CounterpartyName))
            ModelState.AddModelError(nameof(form.CounterpartyName), "گیرنده را بنویسید.");
        if (form.Operation == CustodyOperation.Hold && string.IsNullOrWhiteSpace(form.Reason))
            ModelState.AddModelError(nameof(form.Reason), "دلیل مسدودیت را بنویسید.");

        if (!ModelState.IsValid)
            return View(await BuildOperationViewModelAsync(form, lot));

        var qty = form.QuantityMt!.Value;
        try
        {
            var redirectLotId = form.LotId;
            switch (form.Operation)
            {
                case CustodyOperation.Return:
                case CustodyOperation.Deliver:
                    await custody.ReleaseAsync(new CustodyReleaseInput
                    {
                        LotId = form.LotId,
                        Kind = form.Operation == CustodyOperation.Return ? CustodyMovementKind.ReturnToOwner : CustodyMovementKind.DeliveryToThirdParty,
                        Location = location!,
                        MovementDate = form.Date,
                        QuantityMt = qty,
                        CounterpartyName = form.CounterpartyName,
                        VehicleReference = form.VehicleReference,
                        DocumentNumber = form.DocumentNumber,
                        Notes = form.Notes,
                        FormToken = formToken
                    });
                    break;
                case CustodyOperation.Transfer:
                    await custody.TransferLocationAsync(new CustodyLocationTransferInput
                    {
                        LotId = form.LotId, From = location!, To = toLocation!, MovementDate = form.Date, QuantityMt = qty,
                        DocumentNumber = form.DocumentNumber, Notes = form.Notes, FormToken = formToken
                    });
                    break;
                case CustodyOperation.Ownership:
                    redirectLotId = await custody.TransferOwnershipAsync(new CustodyOwnershipTransferInput
                    {
                        LotId = form.LotId, NewOwner = newOwner!, Location = location!, MovementDate = form.Date, QuantityMt = qty,
                        DocumentNumber = form.DocumentNumber, Notes = form.Notes, FormToken = formToken
                    });
                    break;
                case CustodyOperation.Loss:
                case CustodyOperation.Surplus:
                    await custody.AdjustAsync(new CustodyAdjustmentInput
                    {
                        LotId = form.LotId,
                        Kind = form.Operation == CustodyOperation.Loss ? CustodyMovementKind.Loss : CustodyMovementKind.Surplus,
                        Location = location!, MovementDate = form.Date, QuantityMt = qty,
                        Responsibility = form.Responsibility,
                        DocumentNumber = form.DocumentNumber, Notes = form.Notes, FormToken = formToken
                    });
                    break;
                case CustodyOperation.Hold:
                    await custody.PlaceHoldAsync(form.LotId, qty, form.Reason!, formToken);
                    break;
                default:
                    return BadRequest();
            }

            TempData["ok"] = $"{CustodyOperationForm.Title(form.Operation)} ثبت شد.";
            return RedirectToAction(nameof(Details), new { id = redirectLotId });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (DbUpdateException ex) when (formTokens.IsDuplicate(ex))
        {
            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Details), new { id = form.LotId });
        }

        return View(await BuildOperationViewModelAsync(form, lot));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reverse(int id, Guid operationKey, string? reason)
    {
        try
        {
            await custody.ReverseOperationAsync(operationKey, reason ?? "");
            TempData["ok"] = "عملیات لغو شد و مقدار به حالت قبل برگشت.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReleaseHold(int id, int holdId, string? note)
    {
        try
        {
            await custody.ReleaseHoldAsync(holdId, note);
            TempData["ok"] = "مسدودیت رفع شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TransitResponsibility(int id, CustodyLossResponsibility responsibility)
    {
        if (!Enum.IsDefined(responsibility)) return BadRequest();
        await custody.SetTransitLossResponsibilityAsync(id, responsibility);
        TempData["ok"] = "مسئول اختلاف ثبت شد.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------------------------------------------------------------- فروش امانی و حساب مالک

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Sale(int id)
    {
        var lot = (await reader.GetLotDetailAsync(id))?.Summary;
        if (lot is null) return NotFound();
        if (lot.Status != CustodyLotStatus.Active)
        {
            TempData["err"] = "این بار هنوز رسید نشده یا لغو شده است.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (!CustodySaleService.FinancialOwnerTypes.Contains(lot.OwnerPartyType))
        {
            TempData["err"] = "فروش امانی فعلاً فقط برای مالکی است که مشتری یا فروشنده ثبت شده است.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var form = new CustodySaleForm { LotId = id, Date = businessClock.Today };
        var positions = await reader.GetPositionsAsync(id);
        if (positions.Count == 1)
            form.LocationKey = CustodyLocationKey.Key(positions[0].TerminalId, positions[0].StorageTankId);
        return View(await BuildSaleViewModelAsync(form, lot));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Sale(
        CustodySaleForm form,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var lot = (await reader.GetLotDetailAsync(form.LotId))?.Summary;
        if (lot is null) return NotFound();

        var location = CustodyLocationKey.Parse(form.LocationKey);
        if (location is null) ModelState.AddModelError(nameof(form.LocationKey), "محل را انتخاب کنید.");
        if (!form.CustomerId.HasValue) ModelState.AddModelError(nameof(form.CustomerId), "مشتری را انتخاب کنید.");
        if (form.QuantityMt is not > 0m) ModelState.AddModelError(nameof(form.QuantityMt), "مقدار را وارد کنید.");
        if (form.UnitPriceUsd is not > 0m) ModelState.AddModelError(nameof(form.UnitPriceUsd), "قیمت فی تُن را وارد کنید.");
        if (form.CommissionMethod != CustodyCommissionMethod.None && form.CommissionRate is not >= 0m)
            ModelState.AddModelError(nameof(form.CommissionRate), "مقدار کمیشن را وارد کنید.");
        if (form.Settlement == CustodySaleSettlement.CashToCompany && !form.CashAccountId.HasValue)
            ModelState.AddModelError(nameof(form.CashAccountId), "صندوق یا بانک را انتخاب کنید.");
        if (!ModelState.IsValid)
            return View(await BuildSaleViewModelAsync(form, lot));

        try
        {
            var saleId = await sales.CreateSaleAsync(new CustodySaleInput
            {
                LotId = form.LotId,
                Location = location!,
                CustomerId = form.CustomerId!.Value,
                SaleDate = form.Date,
                QuantityMt = form.QuantityMt!.Value,
                UnitPriceUsd = form.UnitPriceUsd!.Value,
                CommissionMethod = form.CommissionMethod,
                CommissionRate = form.CommissionRate ?? 0m,
                CommissionRecognition = form.CommissionRecognition,
                Settlement = form.Settlement,
                CashAccountId = form.CashAccountId,
                DocumentNumber = form.DocumentNumber,
                Notes = form.Notes,
                FormToken = formToken
            });
            TempData["ok"] = "فروش امانی ثبت شد. مبلغ فروش از آنِ مالک است؛ فقط کمیشن درآمد شرکت است.";
            return RedirectToAction(nameof(Owner), new { key = CustodyParty.Key(lot.OwnerPartyType, lot.OwnerPartyId), saleId });
        }
        catch (Exception ex) when (ex is BusinessRuleException or PTGOilSystem.Web.Services.Accounting.AccountingValidationException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (DbUpdateException ex) when (formTokens.IsDuplicate(ex))
        {
            TempData["err"] = "این فروش قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Details), new { id = form.LotId });
        }

        return View(await BuildSaleViewModelAsync(form, lot));
    }

    public async Task<IActionResult> Owner(string key)
    {
        if (!CustodyParty.TryParse(key, out var type, out var partyId)) return NotFound();
        var account = await reader.GetOwnerAccountAsync(new CustodyOwner(type, partyId));
        ViewBag.Lots = await reader.GetLotSummariesAsync(new CustodyLotFilter(type, partyId));
        return View(account);
    }

    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Reconcile(string key)
    {
        if (!CustodyParty.TryParse(key, out var type, out var partyId)) return NotFound();
        ViewBag.OwnerKey = key;
        ViewBag.OwnerName = await reader.GetPartyNameAsync(type, partyId);
        return View(await reconciliation.PreviewAsync(new CustodyOwner(type, partyId)));
    }

    [Authorize(Policy = AuthPolicies.AdminOnly)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconcile(string key, string fingerprint, bool confirmed, string reason)
    {
        if (!CustodyParty.TryParse(key, out var type, out var partyId)) return NotFound();
        try
        {
            var count = await reconciliation.ApplyAsync(new CustodyOwner(type, partyId), fingerprint, confirmed, reason ?? "");
            TempData["ok"] = $"تطبیق امانی انجام شد؛ {count} سند دفتر کل ثبت شد.";
            return RedirectToAction(nameof(Owner), new { key });
        }
        catch (Exception ex) when (ex is BusinessRuleException or PTGOilSystem.Web.Services.Accounting.AccountingValidationException)
        {
            TempData["err"] = ex is BusinessRuleException ? ex.Message : "تطبیق انجام نشد؛ دوره و حساب‌های دفتر کل را بررسی کنید.";
            return RedirectToAction(nameof(Reconcile), new { key });
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Finance(string key, CustodyFinanceKind kind, int? saleId = null)
    {
        if (!CustodyParty.TryParse(key, out var type, out var partyId) || !Enum.IsDefined(kind)) return NotFound();
        var account = await reader.GetOwnerAccountAsync(new CustodyOwner(type, partyId));
        if (!account.SupportsFinance)
        {
            TempData["err"] = "حساب مالی امانی فعلاً فقط برای مالکی است که مشتری یا فروشنده ثبت شده است.";
            return RedirectToAction(nameof(Owner), new { key });
        }

        var form = new CustodyFinanceForm { Kind = kind, OwnerKey = key, SaleId = saleId, Date = businessClock.Today };
        if (saleId.HasValue && account.Sales.FirstOrDefault(s => s.Id == saleId) is { } sale
            && kind is CustodyFinanceKind.Collection or CustodyFinanceKind.DirectToOwner)
        {
            form.AmountUsd = sale.OutstandingUsd;
        }
        else if (kind == CustodyFinanceKind.OwnerPayout && account.FundsBalanceUsd > 0m)
        {
            form.AmountUsd = account.FundsBalanceUsd;
        }
        return View(await BuildFinanceViewModelAsync(form, account));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Finance(
        CustodyFinanceForm form,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!CustodyParty.TryParse(form.OwnerKey, out var type, out var partyId) || !Enum.IsDefined(form.Kind)) return NotFound();
        var owner = new CustodyOwner(type, partyId);
        var collects = form.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.DirectToOwner;
        if (collects && !form.SaleId.HasValue) ModelState.AddModelError(nameof(form.SaleId), "فروش را انتخاب کنید.");
        if (form.AmountUsd is not > 0m) ModelState.AddModelError(nameof(form.AmountUsd), "مبلغ را وارد کنید.");
        if (form.Kind is CustodyFinanceKind.Collection or CustodyFinanceKind.OwnerPayout or CustodyFinanceKind.ReimbursableExpense
            && !form.CashAccountId.HasValue)
            ModelState.AddModelError(nameof(form.CashAccountId), "صندوق یا بانک را انتخاب کنید.");

        if (ModelState.IsValid)
        {
            try
            {
                await sales.RecordFinanceAsync(new CustodyFinanceInput
                {
                    Kind = form.Kind,
                    SaleId = form.SaleId,
                    Owner = owner,
                    EntryDate = form.Date,
                    AmountUsd = form.AmountUsd!.Value,
                    CashAccountId = form.CashAccountId,
                    ExpenseTypeId = form.ExpenseTypeId,
                    PayeeName = form.PayeeName,
                    DocumentNumber = form.DocumentNumber,
                    Description = form.Description,
                    FormToken = formToken
                });
                TempData["ok"] = $"{CustodyFinanceForm.Title(form.Kind)} ثبت شد.";
                return RedirectToAction(nameof(Owner), new { key = form.OwnerKey });
            }
            catch (Exception ex) when (ex is BusinessRuleException or PTGOilSystem.Web.Services.Accounting.AccountingValidationException)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
            catch (DbUpdateException ex) when (formTokens.IsDuplicate(ex))
            {
                TempData["err"] = "این سند قبلاً ثبت شده است و دوباره ثبت نشد.";
                return RedirectToAction(nameof(Owner), new { key = form.OwnerKey });
            }
        }

        return View(await BuildFinanceViewModelAsync(form, await reader.GetOwnerAccountAsync(owner)));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSale(int id, string key, string? reason)
    {
        try
        {
            await sales.CancelSaleAsync(id, reason ?? "");
            TempData["ok"] = "فروش امانی لغو شد؛ تیل به موجودی مالک برگشت و اسناد مالی آن معکوس شد.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or PTGOilSystem.Web.Services.Accounting.AccountingValidationException)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Owner), new { key });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelFinance(int id, string key, string? reason)
    {
        try
        {
            await sales.CancelFinanceAsync(id, reason ?? "");
            TempData["ok"] = "سند لغو شد و اثر آن معکوس شد.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or PTGOilSystem.Web.Services.Accounting.AccountingValidationException)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Owner), new { key });
    }

    private async Task<CustodySaleViewModel> BuildSaleViewModelAsync(CustodySaleForm form, CustodyLotSummary lot)
    {
        var positions = await reader.GetPositionsAsync(lot.LotId);
        return new CustodySaleViewModel
        {
            Form = form,
            Lot = lot,
            SourceLocations = positions
                .Select(p => new CustodyLocationOption(
                    CustodyLocationKey.Key(p.TerminalId, p.StorageTankId),
                    $"{(p.TankCode is null ? p.TerminalName : $"{p.TerminalName} / {p.TankCode}")} — {NumberDisplay.Quantity(p.QuantityMt, "MT")}"))
                .ToList(),
            Customers = (await db.Customers.AsNoTracking()
                    .Where(c => c.IsActive && !(lot.OwnerPartyType == AccountingPartyType.Customer && c.Id == lot.OwnerPartyId))
                    .OrderBy(c => c.Name)
                    .Select(c => new { c.Id, Name = c.NamePersian ?? c.Name })
                    .ToListAsync())
                .Select(c => (c.Id, c.Name)).ToList(),
            CashAccounts = await UsdCashAccountsAsync()
        };
    }

    private async Task<CustodyFinanceViewModel> BuildFinanceViewModelAsync(CustodyFinanceForm form, CustodyOwnerAccount account)
        => new()
        {
            Form = form,
            Account = account,
            OpenSales = account.Sales.Where(s => s.OutstandingUsd > 0m).ToList(),
            CashAccounts = await UsdCashAccountsAsync(),
            ExpenseTypes = (await db.ExpenseTypes.AsNoTracking()
                    .Where(t => t.IsActive)
                    .OrderBy(t => t.Name)
                    .Select(t => new { t.Id, Name = t.NamePersian ?? t.Name })
                    .ToListAsync())
                .Select(t => (t.Id, t.Name)).ToList()
        };

    /// <summary>سند امانی فعلاً دالری است؛ فقط صندوق/بانکِ دالری یا مختلط.</summary>
    private async Task<IReadOnlyList<(int Id, string Name)>> UsdCashAccountsAsync()
        => (await db.CashAccounts.AsNoTracking()
                .Where(a => a.IsActive && (a.Currency == "USD" || a.AccountType == CashAccountType.Mixed))
                .OrderBy(a => a.Name)
                .Select(a => new { a.Id, a.Name, a.Code })
                .ToListAsync())
            .Select(a => (a.Id, $"{a.Name} ({a.Code})")).ToList();

    private async Task<CustodyReceiveViewModel> BuildReceiveViewModelAsync(CustodyReceiveForm form, CustodyLotSummary? transitLot)
        => new()
        {
            Form = form,
            TransitLot = transitLot,
            Owners = await reader.GetOwnerOptionsAsync(),
            Products = await ProductsAsync(),
            Locations = await OwnLocationsAsync(transitLot?.ProductId)
        };

    private async Task<CustodyOperationViewModel> BuildOperationViewModelAsync(CustodyOperationForm form, CustodyLotSummary lot)
    {
        var positions = await reader.GetPositionsAsync(lot.LotId);
        return new CustodyOperationViewModel
        {
            Form = form,
            Lot = lot,
            SourceLocations = positions
                .Select(p => new CustodyLocationOption(
                    CustodyLocationKey.Key(p.TerminalId, p.StorageTankId),
                    $"{(p.TankCode is null ? p.TerminalName : $"{p.TerminalName} / {p.TankCode}")} — {NumberDisplay.Quantity(p.QuantityMt, "MT")}"))
                .ToList(),
            TargetLocations = form.Operation is CustodyOperation.Transfer or CustodyOperation.Surplus
                ? await OwnLocationsAsync(lot.ProductId)
                : [],
            Owners = form.Operation == CustodyOperation.Ownership
                ? (await reader.GetOwnerOptionsAsync())
                    .Where(o => !(o.PartyType == lot.OwnerPartyType && o.PartyId == lot.OwnerPartyId))
                    .ToList()
                : []
        };
    }

    /// <summary>فقط تأسیساتِ خودِ ما؛ مخزنی که برای جنسِ دیگری تعریف شده نشان داده نمی‌شود.</summary>
    private async Task<IReadOnlyList<CustodyLocationOption>> OwnLocationsAsync(int? productId)
    {
        var terminals = await db.Terminals.AsNoTracking()
            .Where(t => t.IsActive && t.CustodianPartyType == null)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync();
        var tanks = await db.StorageTanks.AsNoTracking()
            .Where(t => t.IsActive && (productId == null || t.ProductId == null || t.ProductId == productId))
            .OrderBy(t => t.TankCode)
            .Select(t => new { t.Id, t.TerminalId, t.TankCode, t.DisplayName })
            .ToListAsync();

        var options = new List<CustodyLocationOption>();
        foreach (var terminal in terminals)
        {
            var terminalTanks = tanks.Where(t => t.TerminalId == terminal.Id).ToList();
            if (terminalTanks.Count == 0)
            {
                options.Add(new CustodyLocationOption(CustodyLocationKey.Key(terminal.Id, null), terminal.Name));
                continue;
            }
            options.AddRange(terminalTanks.Select(t => new CustodyLocationOption(
                CustodyLocationKey.Key(terminal.Id, t.Id),
                $"{terminal.Name} / {t.DisplayName ?? t.TankCode}")));
        }
        return options;
    }

    private async Task<IReadOnlyList<(int Id, string Name)>> ProductsAsync()
        => (await db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, Name = p.NamePersian ?? p.Name })
                .ToListAsync())
            .Select(p => (p.Id, p.Name))
            .ToList();
}
