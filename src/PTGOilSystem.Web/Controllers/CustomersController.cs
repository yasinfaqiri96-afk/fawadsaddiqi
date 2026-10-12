using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Customers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class CustomersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;
    private readonly IPartyStatementReadService _partyStatements;

    public CustomersController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety,
        IPartyStatementReadService? partyStatements = null)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
        _partyStatements = partyStatements ?? PartyStatementReadService.CreateDefault(db);
    }

    public async Task<IActionResult> Index(string? q, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            // PTG canonical search — کلیدِ canonical به شرطِ قبلی اضافه می‌شود، جایگزینِ آن نمی‌شود:
            // هیچ نتیجه‌ای از دست نمی‌رود و «یوسف» سطرِ «يوسف» را هم پیدا می‌کند.
            // SearchKey خالی یعنی سطرِ پیش از Backfill؛ همان شرطِ قبلی هنوز آن را می‌یابد.
            var canonicalTerm = AfghanTextNormalizer.NormalizeForSearch(q);
            query = query.Where(p => (p.SearchKey != null && p.SearchKey.Contains(canonicalTerm)) || (p.Code != null && p.Code.Contains(q)) || p.Name.Contains(q) || (p.NamePersian != null && p.NamePersian.Contains(q)) || (p.ContactPerson != null && p.ContactPerson.Contains(q)));
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new CustomerIndexItemViewModel
            {
                Id = p.Id,
                Name = p.Name,
                NamePersian = p.NamePersian,
                Country = p.Country,
                ContactPerson = p.ContactPerson,
                Phone = p.Phone,
                IsActive = p.IsActive
            })
            .ToListAsync());
    }

    public async Task<IActionResult> Details(int id, int? contractId = null, string? tab = null)
    {
        var item = await BuildCustomerProfileAsync(id, contractId, tab);
        if (item == null) return NotFound();
        var statement = await _partyStatements.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, id),
            new PartyStatementFilter { ContractId = contractId, IncludeOperationalColumns = false },
            HttpContext?.RequestAborted ?? CancellationToken.None);
        ViewData["PartyStatementSummary"] = statement.Summary;
        ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        ViewBag.PaymentTransactions = item.Payments
            .Select(p => new PaymentListItemViewModel
            {
                Id = p.PaymentId,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = p.DirectionName,
                PaymentKind = p.PaymentKind,
                PaymentKindName = p.PaymentKindName,
                CashAccountName = p.CashAccount,
                CounterpartyTypeName = PaymentCounterpartyTypeLabels.ToPersian(PaymentCounterpartyType.Customer),
                CounterpartyName = item.Name,
                CustomerName = item.Name,
                ContractNumber = p.ContractNumber,
                SalesInvoiceNumber = p.InvoiceNumber,
                RelatedTo = p.InvoiceNumber ?? p.ContractNumber ?? "-",
                Description = p.Description,
                CreatedByDisplay = p.CreatedByDisplay,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                LedgerEntryId = p.LedgerEntryId
            })
            .ToList();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Customer());

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,IsActive,Notes")] Customer model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        model.CreatedAtUtc = DateTime.UtcNow;
        _db.Customers.Add(model);
        await _db.SaveChangesAsync();
        TempData["ok"] = "مشتری با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,IsActive,Notes")] Customer model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        var existing = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();
        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.Country = model.Country;
        existing.ContactPerson = model.ContactPerson;
        existing.Phone = model.Phone;
        existing.Address = model.Address;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["ok"] = "ویرایش با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var safety = await _deleteSafety.EvaluateCustomerAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Customer), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("مشتری");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("مشتری")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("مشتری");
            return RedirectToAction(nameof(Index));
        }

        var deleteDiff = AuditDiffFormatter.ForDelete(
            ("Name", item.Name),
            ("Country", item.Country),
            ("ContactPerson", item.ContactPerson));
        _db.Customers.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Customer), item.Id, AuditAction.Delete, diff: deleteDiff);
        TempData["ok"] = "مشتری حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Customer), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    private async Task<CustomerProfileViewModel?> BuildCustomerProfileAsync(int id, int? contractId, string? tab)
    {
        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        if (customer == null)
        {
            return null;
        }

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Include(c => c.Product)
            .Where(c => c.ContractType == ContractType.Sale && c.CustomerId == customer.Id)
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .ToListAsync();
        var contractIds = contracts.Select(c => c.Id).ToHashSet();

        if (contractId.HasValue && !contractIds.Contains(contractId.Value))
        {
            return null;
        }

        // Projected to a slim read-model instead of full SalesTransaction + Product +
        // Contract per row. Same filter/order; nav scalars carried for display.
        var sales = (await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => s.CustomerId == customer.Id
                    || (s.Contract != null
                        && s.Contract.ContractType == ContractType.Sale
                        && s.Contract.CustomerId == customer.Id))
                .OrderByDescending(s => s.SaleDate)
                .ThenByDescending(s => s.Id)
                .Select(s => new CustomerSaleProjection
                {
                    Id = s.Id,
                    InvoiceNumber = s.InvoiceNumber,
                    ContractId = s.ContractId,
                    ContractNumber = s.Contract != null ? s.Contract.ContractNumber : null,
                    ProductName = s.Product != null ? s.Product.Name : null,
                    SaleDate = s.SaleDate,
                    SaleStage = s.SaleStage,
                    QuantityMt = s.QuantityMt,
                    Currency = s.Currency,
                    UnitPriceInCurrency = s.UnitPriceInCurrency,
                    UnitPriceUsd = s.UnitPriceUsd,
                    TotalInCurrency = s.TotalInCurrency,
                    TotalUsd = s.TotalUsd,
                    IsCancelled = s.IsCancelled,
                    IsSalePricePending = s.IsSalePricePending
                })
                .ToListAsync())
            .DistinctBy(s => s.Id)
            .ToList();
        var saleIds = sales.Select(s => s.Id).ToHashSet();
        static string? FirstNonBlank(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

        static string? BuildTransportReference(
            LoadingTransportType? transportType,
            string? wagonNumber,
            string? truckPlate,
            string? billOfLadingNumber,
            string? rwbNo,
            int? fallbackId)
        {
            var reference = transportType == LoadingTransportType.Truck
                ? FirstNonBlank(truckPlate, wagonNumber, billOfLadingNumber, rwbNo)
                : FirstNonBlank(wagonNumber, truckPlate, billOfLadingNumber, rwbNo);

            if (string.IsNullOrWhiteSpace(reference) && fallbackId.HasValue)
            {
                reference = $"#{fallbackId.Value}";
            }

            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            return transportType switch
            {
                LoadingTransportType.Truck => $"موتر {reference}",
                LoadingTransportType.Wagon => $"واگن {reference}",
                _ => reference
            };
        }

        var truckDispatchRefs = await _db.TruckDispatches
                .AsNoTracking()
                .Where(d => d.SalesTransactionId.HasValue && saleIds.Contains(d.SalesTransactionId.Value))
                .Select(d => new
                {
                    SaleId = d.SalesTransactionId!.Value,
                    Reference = d.Truck != null ? d.Truck.PlateNumber : null
                })
                .ToListAsync();

        var transportReceiptRefs = await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => r.SalesTransactionId.HasValue
                    && saleIds.Contains(r.SalesTransactionId.Value)
                    && !r.IsCancelled)
                .Select(r => new
                {
                    SaleId = r.SalesTransactionId!.Value,
                    LegId = r.InventoryTransportLegId,
                    TransportType = r.InventoryTransportLeg != null ? (LoadingTransportType?)r.InventoryTransportLeg.TransportType : null,
                    WagonNumber = r.InventoryTransportLeg != null
                        ? r.InventoryTransportLeg.WagonNumber ?? (r.InventoryTransportLeg.Wagon != null ? r.InventoryTransportLeg.Wagon.WagonNumber : null)
                        : null,
                    TruckPlate = r.InventoryTransportLeg != null && r.InventoryTransportLeg.Truck != null
                        ? r.InventoryTransportLeg.Truck.PlateNumber
                        : null,
                    BillOfLadingNumber = r.InventoryTransportLeg != null ? r.InventoryTransportLeg.BillOfLadingNumber : null,
                    RwbNo = r.InventoryTransportLeg != null ? r.InventoryTransportLeg.RwbNo : null
                })
                .ToListAsync();

        var transportReferenceBySale = truckDispatchRefs
            .Select(row => new
            {
                row.SaleId,
                Text = BuildTransportReference(LoadingTransportType.Truck, null, row.Reference, null, null, null)
            })
            .Concat(transportReceiptRefs.Select(row => new
            {
                row.SaleId,
                Text = BuildTransportReference(
                    row.TransportType,
                    row.WagonNumber,
                    row.TruckPlate,
                    row.BillOfLadingNumber,
                    row.RwbNo,
                    row.LegId)
            }))
            .Where(row => !string.IsNullOrWhiteSpace(row.Text))
            .GroupBy(row => row.SaleId)
            .ToDictionary(
                group => group.Key,
                group => string.Join("، ", group
                    .Select(row => row.Text!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)));

        // Projected to a slim read-model instead of full LedgerEntry + Contract per
        // row. Same filter/order; Contract scalar parts carried for statement + the
        // customer-account predicate (Sale-contract / customer match).
        var ledgers = (await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.CustomerId == customer.Id
                    || (l.Contract != null
                        && l.Contract.ContractType == ContractType.Sale
                        && l.Contract.CustomerId == customer.Id)
                    || (l.SourceType == "Sale" && saleIds.Contains(l.SourceId)))
                .OrderBy(l => l.EntryDate)
                .ThenBy(l => l.Id)
                .Select(l => new CustomerLedgerProjection
                {
                    Id = l.Id,
                    EntryDate = l.EntryDate,
                    Side = l.Side,
                    AmountUsd = l.AmountUsd,
                    SourceType = l.SourceType,
                    SourceId = l.SourceId,
                    CustomerId = l.CustomerId,
                    ContractId = l.ContractId,
                    Currency = l.Currency,
                    SourceCurrencyCode = l.SourceCurrencyCode,
                    SourceAmount = l.SourceAmount,
                    AppliedFxRateToUsd = l.AppliedFxRateToUsd,
                    Reference = l.Reference,
                    Description = l.Description,
                    ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                    ContractType = l.Contract != null ? l.Contract.ContractType : (ContractType?)null,
                    ContractCustomerId = l.Contract != null ? l.Contract.CustomerId : null
                })
                .ToListAsync())
            .DistinctBy(l => l.Id)
            .ToList();

        // Projected to a slim read-model instead of full PaymentTransaction +
        // CashAccount + Contract + SalesTransaction(+Contract) per row. Same
        // filter/order; nav scalars carried for display + contract resolution.
        var payments = (await _db.PaymentTransactions
                .AsNoTracking()
                .Where(p => p.CustomerId == customer.Id
                    || (p.SalesTransaction != null && p.SalesTransaction.CustomerId == customer.Id)
                    || (p.Contract != null
                        && p.Contract.ContractType == ContractType.Sale
                        && p.Contract.CustomerId == customer.Id))
                .OrderByDescending(p => p.PaymentDate)
                .ThenByDescending(p => p.Id)
                .Select(p => new CustomerPaymentProjection
                {
                    Id = p.Id,
                    PaymentDate = p.PaymentDate,
                    Direction = p.Direction,
                    PaymentKind = p.PaymentKind,
                    Amount = p.Amount,
                    Currency = p.Currency,
                    AmountUsd = p.AmountUsd,
                    AppliedFxRateToUsd = p.AppliedFxRateToUsd,
                    Reference = p.Reference,
                    Description = p.Description,
                    LedgerEntryId = p.LedgerEntryId,
                    CreatedAtUtc = p.CreatedAtUtc,
                    CreatedByUserId = p.CreatedByUserId,
                    SalesTransactionId = p.SalesTransactionId,
                    CashAccountName = p.CashAccount != null ? p.CashAccount.Name : null,
                    ContractId = p.ContractId,
                    ContractNumber = p.Contract != null ? p.Contract.ContractNumber : null,
                    SalesContractId = p.SalesTransaction != null ? p.SalesTransaction.ContractId : null,
                    SalesInvoiceNumber = p.SalesTransaction != null ? p.SalesTransaction.InvoiceNumber : null,
                    SalesContractNumber = p.SalesTransaction != null && p.SalesTransaction.Contract != null
                        ? p.SalesTransaction.Contract.ContractNumber
                        : null
                })
                .ToListAsync())
            .DistinctBy(p => p.Id)
            .ToList();

        var userIds = payments
            .Select(p => p.CreatedByUserId)
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToList();
        var users = userIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName);

        static int? GetPaymentContractId(CustomerPaymentProjection payment)
            => payment.ContractId ?? payment.SalesContractId;

        static string? GetPaymentContractNumber(CustomerPaymentProjection payment)
            => payment.ContractNumber ?? payment.SalesContractNumber;

        var activeSales = sales.Where(s => !s.IsCancelled).ToList();
        var ledgerByContract = ledgers
            .Where(l => l.ContractId.HasValue)
            .GroupBy(l => l.ContractId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new LedgerTotals(
                    DebitUsd: g.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
                    CreditUsd: g.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd)));

        var salesByContract = activeSales
            .Where(s => s.ContractId.HasValue)
            .GroupBy(s => s.ContractId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new SalesTotals(
                    QuantityMt: g.Sum(s => s.QuantityMt),
                    ValueUsd: g.Sum(s => s.TotalUsd)));

        var receivedByContract = payments
            .Select(p => new { ContractId = GetPaymentContractId(p), p.PaymentKind, p.AmountUsd })
            .Where(p => p.ContractId.HasValue && p.PaymentKind == PaymentKind.CustomerReceipt)
            .GroupBy(p => p.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.AmountUsd));

        var paidToCustomerByContract = payments
            .Select(p => new { ContractId = GetPaymentContractId(p), p.PaymentKind, p.AmountUsd })
            .Where(p => p.ContractId.HasValue && p.PaymentKind == PaymentKind.CustomerPayment)
            .GroupBy(p => p.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.AmountUsd));

        // وصولیِ هر فروش از مرجعِ واحد (همان صفحهٔ فروش): تطبیقِ پیش‌دریافت هم شمرده می‌شود،
        // نه فقط پرداختی که مستقیم به فروش وصل شده است.
        var saleSettlements = await new CustomerReceiptApplicationService(_db)
            .GetSaleSettlementsAsync(sales.Select(s => s.Id).ToList());
        var receivedBySale = saleSettlements.ToDictionary(kv => kv.Key, kv => kv.Value.ReceivedUsd);
        var paidToCustomerBySale = saleSettlements.ToDictionary(kv => kv.Key, kv => kv.Value.RefundedUsd);

        var finalPriceByContract = contracts.ToDictionary(
            c => c.Id,
            c => ContractPricingAdapter.GetCanonicalFinalPrice(c));

        var contractSummaries = contracts
            .Select(c =>
            {
                var finalPriceUsd = finalPriceByContract[c.Id];
                salesByContract.TryGetValue(c.Id, out var salesTotal);
                ledgerByContract.TryGetValue(c.Id, out var ledger);
                receivedByContract.TryGetValue(c.Id, out var receivedUsd);
                paidToCustomerByContract.TryGetValue(c.Id, out var paidToCustomerUsd);

                return new CustomerContractSummaryViewModel
                {
                    ContractId = c.Id,
                    ContractNumber = c.ContractNumber,
                    Product = c.Product?.Name ?? "-",
                    ContractDate = c.ContractDate,
                    QuantityMt = c.QuantityMt,
                    Currency = c.Currency,
                    UnitPriceOriginal = c.UnitPriceInCurrency,
                    FinalPriceUsd = finalPriceUsd,
                    EstimatedTotalOriginal = c.UnitPriceInCurrency.HasValue && c.UnitPriceInCurrency.Value > 0m
                        ? decimal.Round(c.QuantityMt * c.UnitPriceInCurrency.Value, 4, MidpointRounding.AwayFromZero)
                        : null,
                    EstimatedTotalUsd = finalPriceUsd.HasValue && finalPriceUsd.Value > 0m
                        ? decimal.Round(c.QuantityMt * finalPriceUsd.Value, 4, MidpointRounding.AwayFromZero)
                        : null,
                    SoldQuantityMt = salesTotal?.QuantityMt ?? 0m,
                    SoldValueUsd = salesTotal?.ValueUsd ?? 0m,
                    LedgerDebitUsd = ledger?.DebitUsd ?? 0m,
                    LedgerCreditUsd = ledger?.CreditUsd ?? 0m,
                    ReceivedUsd = receivedUsd,
                    PaidToCustomerUsd = paidToCustomerUsd,
                    Status = c.Status,
                    StatusName = GetContractStatusName(c.Status),
                    StatusBadgeClass = GetContractStatusBadgeClass(c.Status)
                };
            })
            .ToList();

        var saleSummaries = sales
            .Select(s =>
            {
                receivedBySale.TryGetValue(s.Id, out var receivedUsd);
                paidToCustomerBySale.TryGetValue(s.Id, out var paidToCustomerUsd);

                return new CustomerSaleSummaryViewModel
                {
                    SaleId = s.Id,
                    InvoiceNumber = s.InvoiceNumber,
                    ContractId = s.ContractId,
                    ContractNumber = s.ContractNumber,
                    Product = s.ProductName ?? "-",
                    TransportReference = transportReferenceBySale.GetValueOrDefault(s.Id),
                    SaleDate = s.SaleDate,
                    SaleStage = s.SaleStage,
                    SaleStageName = SaleStageLabels.ToPersian(s.SaleStage),
                    QuantityMt = s.QuantityMt,
                    Currency = s.Currency,
                    UnitPriceInCurrency = s.UnitPriceInCurrency,
                    UnitPriceUsd = s.UnitPriceUsd,
                    TotalInCurrency = s.TotalInCurrency,
                    TotalUsd = s.TotalUsd,
                    ReceivedUsd = receivedUsd,
                    PaidToCustomerUsd = paidToCustomerUsd,
                    IsCancelled = s.IsCancelled,
                    IsSalePricePending = s.IsSalePricePending
                };
            })
            .ToList();

        var paymentSummaries = payments
            .Select(p => new CustomerPaymentSummaryViewModel
            {
                PaymentId = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = PaymentDirectionLabels.ToPersian(p.Direction),
                PaymentKind = p.PaymentKind,
                PaymentKindName = PaymentKindLabels.ToPersian(p.PaymentKind),
                CashAccount = p.CashAccountName ?? string.Empty,
                ContractId = GetPaymentContractId(p),
                ContractNumber = GetPaymentContractNumber(p),
                SaleId = p.SalesTransactionId,
                InvoiceNumber = p.SalesInvoiceNumber,
                Amount = p.Amount,
                Currency = p.Currency,
                AppliedFxRateToUsd = p.AppliedFxRateToUsd,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                Description = p.Description,
                LedgerEntryId = p.LedgerEntryId,
                CreatedAtUtc = p.CreatedAtUtc,
                CreatedByUserId = p.CreatedByUserId,
                CreatedByDisplay = p.CreatedByUserId.HasValue && users.TryGetValue(p.CreatedByUserId.Value, out var userName)
                    ? userName
                    : null
            })
            .ToList();

        var selectedContract = contractId.HasValue
            ? contractSummaries.First(c => c.ContractId == contractId.Value)
            : null;
        var customerAccountLedgers = ledgers
            .Where(l => IsCustomerAccountLedger(l, customer.Id, saleIds))
            .ToList();
        var statementLedgers = contractId.HasValue
            ? customerAccountLedgers.Where(l => l.ContractId == contractId.Value).ToList()
            : customerAccountLedgers;

        var ledgerDebitUsd = customerAccountLedgers.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd);
        var ledgerCreditUsd = customerAccountLedgers.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd);
        var totalReceivedUsd = paymentSummaries
            .Where(p => p.PaymentKind == PaymentKind.CustomerReceipt)
            .Sum(p => p.AmountUsd);
        var totalPaidToCustomerUsd = paymentSummaries
            .Where(p => p.PaymentKind == PaymentKind.CustomerPayment)
            .Sum(p => p.AmountUsd);

        return new CustomerProfileViewModel
        {
            CustomerId = customer.Id,
            Code = customer.Code,
            Name = customer.Name,
            NamePersian = customer.NamePersian,
            Country = customer.Country,
            ContactPerson = customer.ContactPerson,
            Phone = customer.Phone,
            Address = customer.Address,
            Notes = customer.Notes,
            IsActive = customer.IsActive,
            CreatedAtUtc = customer.CreatedAtUtc,
            SaleContractsCount = contractSummaries.Count,
            ActiveSaleContractsCount = contractSummaries.Count(c => c.Status == ContractStatus.Active),
            TotalContractQuantityMt = contractSummaries.Sum(c => c.QuantityMt),
            EstimatedContractValueUsd = contractSummaries.Sum(c => c.EstimatedTotalUsd ?? 0m),
            SoldQuantityMt = activeSales.Sum(s => s.QuantityMt),
            SoldValueUsd = activeSales.Sum(s => s.TotalUsd),
            RemainingContractQuantityMt = contractSummaries.Sum(c => c.RemainingQuantityMt),
            EstimatedRemainingContractValueUsd = contractSummaries.Sum(c => c.EstimatedRemainingUsd ?? 0m),
            LedgerDebitUsd = ledgerDebitUsd,
            LedgerCreditUsd = ledgerCreditUsd,
            TotalReceivedUsd = totalReceivedUsd,
            TotalPaidToCustomerUsd = totalPaidToCustomerUsd,
            LastSaleDate = activeSales.Count == 0 ? null : activeSales.Max(s => s.SaleDate),
            LastPaymentDate = paymentSummaries.Count == 0 ? null : paymentSummaries.Max(p => p.PaymentDate),
            SelectedContractId = contractId,
            ActiveTab = ResolveActiveTab(tab, contractId),
            SelectedContract = selectedContract,
            StatementContractOptions = contractSummaries
                .OrderBy(c => c.ContractNumber)
                .Select(c => new CustomerStatementContractFilterOptionViewModel
                {
                    ContractId = c.ContractId,
                    ContractNumber = c.ContractNumber
                })
                .ToList(),
            Contracts = contractSummaries,
            Sales = saleSummaries,
            Payments = paymentSummaries,
            StatementRows = BuildCustomerStatementRows(statementLedgers)
        };
    }

    internal static bool IsCustomerAccountLedger(LedgerEntry ledger, int customerId, ISet<int> saleIds)
        => ledger.CustomerId == customerId
            || (ledger.Contract != null
                && ledger.Contract.ContractType == ContractType.Sale
                && ledger.Contract.CustomerId == customerId)
            || (ledger.SourceType == "Sale" && saleIds.Contains(ledger.SourceId))
            || ledger.SourceType == nameof(PaymentKind.CustomerReceipt)
            || ledger.SourceType == nameof(PaymentKind.CustomerPayment);

    private static IReadOnlyList<CustomerStatementRowViewModel> BuildCustomerStatementRows(IEnumerable<LedgerEntry> ledgers)
    {
        var rows = new List<CustomerStatementRowViewModel>();
        var runningBalance = 0m;

        foreach (var ledger in ledgers
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.Id))
        {
            var debitUsd = ledger.Side == LedgerSide.Debit ? ledger.AmountUsd : (decimal?)null;
            var creditUsd = ledger.Side == LedgerSide.Credit ? ledger.AmountUsd : (decimal?)null;
            runningBalance += (creditUsd ?? 0m) - (debitUsd ?? 0m);
            var sourceAmount = ledger.SourceAmount ?? ledger.AmountUsd;

            rows.Add(new CustomerStatementRowViewModel
            {
                LedgerEntryId = ledger.Id,
                Date = ledger.EntryDate,
                Type = GetCustomerStatementTypeName(ledger.SourceType),
                Reference = ledger.Reference,
                SourceDetailsController = IsThreeWaySettlementSource(ledger.SourceType) ? "ThreeWaySettlement" : null,
                SourceDetailsAction = IsThreeWaySettlementSource(ledger.SourceType) ? "Details" : null,
                SourceDetailsRouteId = IsThreeWaySettlementSource(ledger.SourceType) ? ledger.SourceId : null,
                ContractId = ledger.ContractId,
                ContractNumber = ledger.Contract?.ContractNumber,
                Description = ledger.Description,
                Currency = ledger.SourceCurrencyCode ?? ledger.Currency,
                Debit = ledger.Side == LedgerSide.Debit ? sourceAmount : null,
                Credit = ledger.Side == LedgerSide.Credit ? sourceAmount : null,
                DebitUsd = debitUsd,
                CreditUsd = creditUsd,
                RunningBalanceUsd = runningBalance,
                FxRateUsed = ledger.AppliedFxRateToUsd
            });
        }

        return rows;
    }

    private static string GetCustomerStatementTypeName(string sourceType)
        => sourceType switch
        {
            "Sale" => "فروش",
            AssetRentLedgerFactory.LedgerSourceType => "کرایه دارایی",
            nameof(PaymentKind.CustomerReceipt) => "دریافت از مشتری",
            nameof(PaymentKind.CustomerPayment) => "پرداخت به مشتری",
            ThreeWaySettlementController.LedgerSourceType => "تسویه سه‌طرفه / حواله",
            ThreeWaySettlementController.CancellationLedgerSourceType => "برگشت تسویه سه‌طرفه",
            PartySettlementsController.LedgerSourceType => "تسویه بین طرف‌حساب‌ها",
            "ContractBalanceTransfer" => "انتقال مانده قرارداد",
            "Adjustment" => "اصلاح حساب",
            _ => sourceType
        };

    private static bool IsThreeWaySettlementSource(string sourceType)
        => string.Equals(sourceType, ThreeWaySettlementController.LedgerSourceType, StringComparison.Ordinal)
            || string.Equals(sourceType, ThreeWaySettlementController.CancellationLedgerSourceType, StringComparison.Ordinal);

    private static string ResolveActiveTab(string? tab, int? contractId)
    {
        if (contractId.HasValue)
        {
            return "account";
        }

        return (tab ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "activity" or "contracts" or "sales" => "activity",
            "account" or "payments" or "statement" => "account",
            _ => "overview"
        };
    }

    private static string GetContractStatusName(ContractStatus status)
        => status switch
        {
            ContractStatus.Draft => "پیش‌نویس",
            ContractStatus.Active => "فعال",
            ContractStatus.Closed => "بسته‌شده",
            ContractStatus.Cancelled => "لغوشده",
            _ => status.ToString()
        };

    private static string GetContractStatusBadgeClass(ContractStatus status)
        => status switch
        {
            ContractStatus.Active => "status-badge-success",
            ContractStatus.Closed => "status-badge-neutral",
            ContractStatus.Cancelled => "status-badge-danger",
            _ => "status-badge-warning"
        };

    // ---- Customer Details projection helpers ---------------------------------
    // Mirror of the entity-based helpers, operating on the slim read-models below.
    // Entity helpers stay untouched (IsCustomerAccountLedger is shared with
    // PaymentsController). Values are bit-identical.

    private static bool IsCustomerAccountLedger(CustomerLedgerProjection ledger, int customerId, ISet<int> saleIds)
        => ledger.CustomerId == customerId
            || (ledger.ContractType == ContractType.Sale && ledger.ContractCustomerId == customerId)
            || (ledger.SourceType == "Sale" && saleIds.Contains(ledger.SourceId))
            || ledger.SourceType == nameof(PaymentKind.CustomerReceipt)
            || ledger.SourceType == nameof(PaymentKind.CustomerPayment);

    private static IReadOnlyList<CustomerStatementRowViewModel> BuildCustomerStatementRows(
        IEnumerable<CustomerLedgerProjection> ledgers)
    {
        var rows = new List<CustomerStatementRowViewModel>();
        var runningBalance = 0m;

        foreach (var ledger in ledgers
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.Id))
        {
            var debitUsd = ledger.Side == LedgerSide.Debit ? ledger.AmountUsd : (decimal?)null;
            var creditUsd = ledger.Side == LedgerSide.Credit ? ledger.AmountUsd : (decimal?)null;
            runningBalance += (creditUsd ?? 0m) - (debitUsd ?? 0m);
            var sourceAmount = ledger.SourceAmount ?? ledger.AmountUsd;

            rows.Add(new CustomerStatementRowViewModel
            {
                LedgerEntryId = ledger.Id,
                Date = ledger.EntryDate,
                Type = GetCustomerStatementTypeName(ledger.SourceType),
                Reference = ledger.Reference,
                SourceDetailsController = IsThreeWaySettlementSource(ledger.SourceType) ? "ThreeWaySettlement" : null,
                SourceDetailsAction = IsThreeWaySettlementSource(ledger.SourceType) ? "Details" : null,
                SourceDetailsRouteId = IsThreeWaySettlementSource(ledger.SourceType) ? ledger.SourceId : null,
                ContractId = ledger.ContractId,
                ContractNumber = ledger.ContractNumber,
                Description = ledger.Description,
                Currency = ledger.SourceCurrencyCode ?? ledger.Currency,
                Debit = ledger.Side == LedgerSide.Debit ? sourceAmount : null,
                Credit = ledger.Side == LedgerSide.Credit ? sourceAmount : null,
                DebitUsd = debitUsd,
                CreditUsd = creditUsd,
                RunningBalanceUsd = runningBalance,
                FxRateUsed = ledger.AppliedFxRateToUsd
            });
        }

        return rows;
    }

    // Slim read-model for customer sale rows on the Details page.
    private sealed class CustomerSaleProjection
    {
        public int Id { get; init; }
        public string InvoiceNumber { get; init; } = "";
        public int? ContractId { get; init; }
        public string? ContractNumber { get; init; }
        public string? ProductName { get; init; }
        public DateTime SaleDate { get; init; }
        public SaleStage SaleStage { get; init; }
        public decimal QuantityMt { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal UnitPriceInCurrency { get; init; }
        public decimal UnitPriceUsd { get; init; }
        public decimal TotalInCurrency { get; init; }
        public decimal TotalUsd { get; init; }
        public bool IsCancelled { get; init; }
        public bool IsSalePricePending { get; init; }
    }

    // Slim read-model for customer ledger rows on the Details page.
    private sealed class CustomerLedgerProjection
    {
        public int Id { get; init; }
        public DateTime EntryDate { get; init; }
        public LedgerSide Side { get; init; }
        public decimal AmountUsd { get; init; }
        public string SourceType { get; init; } = "";
        public int SourceId { get; init; }
        public int? CustomerId { get; init; }
        public int? ContractId { get; init; }
        public string Currency { get; init; } = "USD";
        public string? SourceCurrencyCode { get; init; }
        public decimal? SourceAmount { get; init; }
        public decimal? AppliedFxRateToUsd { get; init; }
        public string? Reference { get; init; }
        public string Description { get; init; } = "";
        public string? ContractNumber { get; init; }
        public ContractType? ContractType { get; init; }
        public int? ContractCustomerId { get; init; }
    }

    // Slim read-model for customer payment rows on the Details page.
    private sealed class CustomerPaymentProjection
    {
        public int Id { get; init; }
        public DateTime PaymentDate { get; init; }
        public PaymentDirection Direction { get; init; }
        public PaymentKind PaymentKind { get; init; }
        public decimal Amount { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal AmountUsd { get; init; }
        public decimal? AppliedFxRateToUsd { get; init; }
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public int? LedgerEntryId { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public int? CreatedByUserId { get; init; }
        public int? SalesTransactionId { get; init; }
        public string? CashAccountName { get; init; }
        public int? ContractId { get; init; }
        public string? ContractNumber { get; init; }
        public int? SalesContractId { get; init; }
        public string? SalesInvoiceNumber { get; init; }
        public string? SalesContractNumber { get; init; }
    }

    private sealed record LedgerTotals(decimal DebitUsd, decimal CreditUsd);

    private sealed record SalesTotals(decimal QuantityMt, decimal ValueUsd);

    private static void Normalize(Customer model)
    {
        model.Code = string.IsNullOrWhiteSpace(model.Code) ? null : model.Code.Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Country = string.IsNullOrWhiteSpace(model.Country) ? null : model.Country.Trim();
        model.ContactPerson = string.IsNullOrWhiteSpace(model.ContactPerson) ? null : model.ContactPerson.Trim();
        model.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
        model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.Customers.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Code, c.Name, c.Country, c.ContactPerson, c.Phone, c.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }
}
