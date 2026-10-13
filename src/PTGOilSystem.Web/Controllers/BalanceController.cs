using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Balance;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class BalanceController : Controller
{
    private const int IndexPageSize = 20;
    private readonly ApplicationDbContext _db;
    private readonly IPartyBalanceReadService _partyBalances;
    private readonly IProfitAndLossService _profitAndLoss;
    private readonly PartyCashSettlementReader _cashSettlements;

    public BalanceController(
        ApplicationDbContext db,
        IPartyBalanceReadService? partyBalances = null,
        IProfitAndLossService? profitAndLoss = null)
    {
        _db = db;
        // ارزش خرید قرارداد فقط از همان موتور مالیِ «مفاد قراردادها» خوانده می‌شود.
        _profitAndLoss = profitAndLoss ?? new ProfitAndLossService(
            db, new PurchaseAggregationService(db), new SaleContractAttributionReader(db));
        _cashSettlements = new PartyCashSettlementReader(db);
        _partyBalances = partyBalances ?? new PartyBalanceReadService(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            new PartyDirectory(db));
    }

    public IActionResult Index()
    {
        return RedirectToAction(nameof(Contracts));
    }

    private async Task PopulateContractsLookupsAsync(ContractsBalanceFilterViewModel filter)
    {
        ViewBag.ContractTypes = new SelectList(
            Enum.GetValues<ContractType>()
                .Select(value => new { Value = value, Text = GetContractTypeName(value) })
                .ToList(),
            "Value",
            "Text",
            filter.ContractType.Only());

        ViewBag.ContractStatuses = new SelectList(
            Enum.GetValues<ContractStatus>()
                .Select(value => new { Value = value, Text = GetContractStatusName(value) })
                .ToList(),
            "Value",
            "Text",
            filter.Status.Only());

        ViewBag.Products = new SelectList(
            await _db.Products
                .AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.ProductId.Only());

        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.CustomerId.Only());

        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.SupplierId.Only());
    }

    private async Task PopulateCustomersLookupsAsync(CustomersBalanceFilterViewModel filter)
    {
        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.CustomerId.Only());

        ViewBag.Countries = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .Where(c => c.Country != null && c.Country != string.Empty)
                .Select(c => c.Country!)
                .Distinct()
                .OrderBy(country => country)
                .ToListAsync(),
            filter.Country.OnlyText());
    }

    private async Task PopulateSuppliersLookupsAsync(SuppliersBalanceFilterViewModel filter)
    {
        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.SupplierId.Only());

        ViewBag.Countries = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .Where(s => s.Country != null && s.Country != string.Empty)
                .Select(s => s.Country!)
                .Distinct()
                .OrderBy(country => country)
                .ToListAsync(),
            filter.Country.OnlyText());
    }

    private static IQueryable<Contract> ApplyContractsFilter(
        IQueryable<Contract> query,
        ContractsBalanceFilterViewModel filter)
    {
        if (filter.FromDate.HasValue)
        {
            query = query.Where(c => c.ContractDate >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(c => c.ContractDate <= filter.ToDate.Value);
        }

        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (filter.ContractType.Length > 0)
        {
            var contractTypes = filter.ContractType;
            query = query.Where(c => contractTypes.Contains(c.ContractType));
        }

        if (filter.Status.Length > 0)
        {
            var statuses = filter.Status;
            query = query.Where(c => statuses.Contains(c.Status));
        }

        if (filter.ProductId.Length > 0)
        {
            var productIds = filter.ProductId;
            query = query.Where(c => productIds.Contains(c.ProductId));
        }

        if (filter.CustomerId.Length > 0)
        {
            var customerIds = filter.CustomerId;
            query = query.Where(c => c.CustomerId != null && customerIds.Contains(c.CustomerId.Value));
        }

        if (filter.SupplierId.Length > 0)
        {
            var supplierIds = filter.SupplierId;
            query = query.Where(c => c.SupplierId != null && supplierIds.Contains(c.SupplierId.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(c =>
                c.ContractName.Contains(search)
                || c.ContractNumber.Contains(search)
                || (c.Customer != null && c.Customer.Name.Contains(search))
                || (c.Supplier != null && c.Supplier.Name.Contains(search))
                || (c.Product != null && c.Product.Name.Contains(search)));
        }

        return query;
    }

    private static IQueryable<Customer> ApplyCustomersFilter(
        IQueryable<Customer> query,
        CustomersBalanceFilterViewModel filter)
    {
        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (filter.CustomerId.Length > 0)
        {
            var customerIds = filter.CustomerId;
            query = query.Where(c => customerIds.Contains(c.Id));
        }

        if (filter.Country.Length > 0)
        {
            var countries = filter.Country.Select(value => value.Trim()).ToArray();
            query = query.Where(c => c.Country != null && countries.Contains(c.Country));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(c =>
                c.Name.Contains(search)
                || (c.Country != null && c.Country.Contains(search))
                || (c.ContactPerson != null && c.ContactPerson.Contains(search))
                || (c.Code != null && c.Code.Contains(search)));
        }

        return query;
    }

    private static IQueryable<Supplier> ApplySuppliersFilter(
        IQueryable<Supplier> query,
        SuppliersBalanceFilterViewModel filter)
    {
        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (filter.SupplierId.Length > 0)
        {
            var supplierIds = filter.SupplierId;
            query = query.Where(s => supplierIds.Contains(s.Id));
        }

        if (filter.Country.Length > 0)
        {
            var countries = filter.Country.Select(value => value.Trim()).ToArray();
            query = query.Where(s => s.Country != null && countries.Contains(s.Country));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(s =>
                s.Name.Contains(search)
                || (s.Country != null && s.Country.Contains(search))
                || (s.ContactPerson != null && s.ContactPerson.Contains(search))
                || (s.Code != null && s.Code.Contains(search)));
        }

        return query;
    }

    public async Task<IActionResult> Contracts([FromQuery] ContractsBalanceFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new ContractsBalanceFilterViewModel();
        await PopulateContractsLookupsAsync(filter);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var contractQuery = ApplyContractsFilter(_db.Contracts.AsNoTracking(), filter);
        var totalCount = await contractQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var orderedContracts = contractQuery
            .OrderByDescending(c => c.ContractDate)
            .ThenByDescending(c => c.Id);

        var contracts = await (page <= 0
                ? orderedContracts
                : orderedContracts
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                c.ContractType,
                c.Status,
                CustomerName = c.Customer != null ? c.Customer.Name : null,
                SupplierName = c.Supplier != null ? c.Supplier.Name : null,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null,
                c.QuantityMt
            })
            .ToListAsync();

        // ردیف‌ها همان صفحهٔ جاری‌اند، ولی جمع‌های کارت‌های بالا روی همهٔ نتایجِ فیلتر است؛
        // پس تجمیع‌ها برای همهٔ شناسه‌های فیلترشده ساخته می‌شوند (در دیتابیس، یک query برای هر جمع).
        var contractIds = await contractQuery.Select(contract => contract.Id).ToListAsync();

        var salesQuery = _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled && s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value));
        if (filter.FromDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate <= filter.ToDate.Value);
        var salesSummary = contractIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await salesQuery
            .GroupBy(s => s.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                TotalUsd = g.Sum(x => x.TotalUsd)
            })
            .ToDictionaryAsync(x => x.ContractId, x => x.TotalUsd);

        var allocatedSalesQuery = _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => contractIds.Contains(a.SourcePurchaseContractId)
                && a.SalesTransaction != null
                && !a.SalesTransaction.IsCancelled);
        if (filter.FromDate.HasValue)
        {
            allocatedSalesQuery = allocatedSalesQuery.Where(a => a.SalesTransaction!.SaleDate >= filter.FromDate.Value);
        }
        if (filter.ToDate.HasValue)
        {
            allocatedSalesQuery = allocatedSalesQuery.Where(a => a.SalesTransaction!.SaleDate <= filter.ToDate.Value);
        }
        var allocatedSalesSummary = contractIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await allocatedSalesQuery
                .GroupBy(a => a.SourcePurchaseContractId)
                .Select(g => new { ContractId = g.Key, TotalUsd = g.Sum(a => a.AmountUsd) })
                .ToDictionaryAsync(x => x.ContractId, x => x.TotalUsd);
        foreach (var allocatedSale in allocatedSalesSummary)
        {
            salesSummary[allocatedSale.Key] = salesSummary.GetValueOrDefault(allocatedSale.Key) + allocatedSale.Value;
        }

        // مصرفِ لغوشده پول نیست؛ اگر شمرده شود ستون «مصارف» و مانده اشتباه می‌شود.
        var expensesQuery = _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled && e.ContractId.HasValue && contractIds.Contains(e.ContractId.Value));
        if (filter.FromDate.HasValue) expensesQuery = expensesQuery.Where(e => e.ExpenseDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) expensesQuery = expensesQuery.Where(e => e.ExpenseDate <= filter.ToDate.Value);
        var expensesSummary = contractIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await expensesQuery
            .GroupBy(e => e.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                TotalUsd = g.Sum(x => x.AmountUsd)
            })
            .ToDictionaryAsync(x => x.ContractId, x => x.TotalUsd);

        var ledgerQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId.HasValue && contractIds.Contains(l.ContractId.Value));
        if (filter.FromDate.HasValue) ledgerQuery = ledgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) ledgerQuery = ledgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value);
        var ledgerCounts = contractIds.Count == 0
            ? new Dictionary<int, int>()
            : await ledgerQuery
            .GroupBy(l => l.ContractId!.Value)
            .Select(g => new { ContractId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ContractId, x => x.Count);

        // ماندهٔ قرارداد فقط از موتورِ رسمی (جمعِ ماندهٔ طرف‌حساب‌های همان قرارداد، تا پایانِ بازه):
        // همان عددِ پروندهٔ قرارداد و بستن قرارداد. مثبت = طلب شرکت، منفی = بدهی شرکت.
        var contractBalances = await _partyBalances.GetContractBalancesAsync(
            contractIds,
            filter.ToDate,
            HttpContext?.RequestAborted ?? CancellationToken.None);

        var shipmentSummary = contractIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.Shipments
            .AsNoTracking()
            .Where(s => s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value))
            .GroupBy(s => s.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.ContractId, x => x.Count);

        var items = contracts.Select(contract =>
        {
            salesSummary.TryGetValue(contract.Id, out var salesTotal);
            expensesSummary.TryGetValue(contract.Id, out var expensesTotal);
            ledgerCounts.TryGetValue(contract.Id, out var ledgerCount);
            shipmentSummary.TryGetValue(contract.Id, out var shipmentCount);

            return new ContractBalanceListItemViewModel
            {
                Id = contract.Id,
                ContractNumber = contract.ContractNumber,
                ContractTypeName = GetContractTypeName(contract.ContractType),
                StatusName = GetContractStatusName(contract.Status),
                CustomerName = contract.CustomerName,
                SupplierName = contract.SupplierName,
                ProductName = contract.ProductName,
                ContractUnitText = ContractUiText.ResolveUnitText(
                    contract.UnitSymbol,
                    contract.UnitCode,
                    contract.UnitNamePersian,
                    contract.UnitName),
                QuantityMt = contract.QuantityMt,
                ShipmentCount = shipmentCount,
                TotalSalesUsd = salesTotal,
                TotalExpensesUsd = expensesTotal,
                RelatedLedgerCount = ledgerCount,
                BaseBalanceUsd = contractBalances.TryGetValue(contract.Id, out var balance) ? balance.NetBalanceUsd : 0m
            };
        }).ToList();

        return View(new ContractsBalanceViewModel
        {
            FilteredTotalSalesUsd = salesSummary.Values.Sum(),
            FilteredTotalExpensesUsd = expensesSummary.Values.Sum(),
            FilteredBalanceUsd = contractBalances.Values.Sum(balance => balance.NetBalanceUsd),
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> ContractDetails(int id)
    {
        var exists = await _db.Contracts
            .AsNoTracking()
            .AnyAsync(c => c.Id == id);
        if (!exists)
        {
            return NotFound();
        }
        return RedirectToAction(
            "Details",
            "ContractJourney",
            new { contractId = id, tab = ContractJourneyTabs.Details.Finance });
    }

    public async Task<IActionResult> Customers([FromQuery] CustomersBalanceFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new CustomersBalanceFilterViewModel();
        await PopulateCustomersLookupsAsync(filter);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var customerQuery = ApplyCustomersFilter(_db.Customers.AsNoTracking(), filter);
        var totalCount = await customerQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var orderedCustomers = customerQuery.OrderBy(c => c.Name);

        var customers = await (page <= 0
                ? orderedCustomers
                : orderedCustomers
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Country
            })
            .ToListAsync();

        // ردیف‌ها همان صفحهٔ جاری‌اند، ولی جمع‌های کارت‌های بالا روی همهٔ نتایجِ فیلتر است؛
        // پس تجمیع‌ها برای همهٔ شناسه‌های فیلترشده ساخته می‌شوند (در دیتابیس، یک query برای هر جمع).
        var customerIds = await customerQuery.Select(customer => customer.Id).ToListAsync();
        // فقط ماندهٔ مشتری لازم است. ردیف صراف/کارمند/شریک همین‌جا هم دور ریخته می‌شد،
        // پس خواندنشان از دیتابیس کارِ بی‌حاصل بود؛ حالا اصلاً خوانده نمی‌شوند. عددِ
        // ماندهٔ مشتری‌ها ذره‌ای تغییر نمی‌کند، چون هیچ‌کدام از آن منابع ردیفِ مشتری نمی‌سازند.
        var officialCustomerBalances = (await _partyBalances.GetBalancesAsync(
                new ManagementReportFilterViewModel
                {
                    FromDate = filter.FromDate,
                    ToDate = filter.ToDate
                },
                HttpContext?.RequestAborted ?? CancellationToken.None,
                partyTypes: [PartyStatementPartyType.Customer]))
            .Where(row => row.PartyType == PartyStatementPartyType.Customer
                && customerIds.Contains(row.PartyId))
            .ToDictionary(row => row.PartyId);

        var filteredContractsQuery = _db.Contracts
            .AsNoTracking()
            .Where(c => c.CustomerId.HasValue && customerIds.Contains(c.CustomerId.Value));
        if (filter.FromDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var contractSummary = customerIds.Count == 0
            ? new Dictionary<int, CountAggregate>()
            : await filteredContractsQuery
                .GroupBy(c => c.CustomerId!.Value)
                .Select(g => new
                {
                    CustomerId = g.Key,
                    Summary = new CountAggregate(
                        g.Count(),
                        g.Count(c => c.Status == ContractStatus.Active),
                        g.Count(c => c.Status == ContractStatus.Closed))
                })
                .ToDictionaryAsync(x => x.CustomerId, x => x.Summary);

        var salesQuery = _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled && s.CustomerId.HasValue && customerIds.Contains(s.CustomerId.Value));
        if (filter.FromDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate <= filter.ToDate.Value);
        var salesSummary = customerIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await salesQuery
            .GroupBy(s => s.CustomerId!.Value)
            .Select(g => new
            {
                CustomerId = g.Key,
                TotalUsd = g.Sum(x => x.TotalUsd)
            })
            .ToDictionaryAsync(x => x.CustomerId, x => x.TotalUsd);

        var expensesSummary = customerIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await (
                from e in _db.ExpenseTransactions.AsNoTracking()
                join c in filteredContractsQuery on e.ContractId equals c.Id
                where !e.IsCancelled
                    && (!filter.FromDate.HasValue || e.ExpenseDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || e.ExpenseDate <= filter.ToDate.Value)
                group e by c.CustomerId!.Value into g
                select new
                {
                    CustomerId = g.Key,
                    TotalUsd = g.Sum(x => x.AmountUsd)
                })
            .ToDictionaryAsync(x => x.CustomerId, x => x.TotalUsd);

        // پول واقعیِ دریافت‌شده، مستقل از ارزش فروش (نقد، بانک، حوالهٔ صراف و تسویهٔ سه‌جانبه).
        var receivedSummary = await _cashSettlements.GetCustomerNetReceiptsAsync(
            customerIds, filter.FromDate, filter.ToDate, HttpContext?.RequestAborted ?? CancellationToken.None);

        var directLedgerQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.CustomerId.HasValue && customerIds.Contains(l.CustomerId.Value));
        if (filter.FromDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value);
        var directLedgerSummary = customerIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await directLedgerQuery
            .GroupBy(l => l.CustomerId!.Value)
            .Select(g => new
            {
                CustomerId = g.Key,
                Summary = new LedgerAggregate(g.Count())
            })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Summary);

        var contractLedgerSummary = customerIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await (
                from l in _db.LedgerEntries.AsNoTracking()
                join c in filteredContractsQuery on l.ContractId equals c.Id
                where !l.CustomerId.HasValue
                    && (!filter.FromDate.HasValue || l.EntryDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || l.EntryDate <= filter.ToDate.Value)
                group l by c.CustomerId!.Value into g
                select new
                {
                    CustomerId = g.Key,
                    Summary = new LedgerAggregate(g.Count())
                })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Summary);

        var items = customers.Select(customer =>
        {
            contractSummary.TryGetValue(customer.Id, out var contracts);
            salesSummary.TryGetValue(customer.Id, out var salesTotal);
            expensesSummary.TryGetValue(customer.Id, out var expensesTotal);
            directLedgerSummary.TryGetValue(customer.Id, out var directLedger);
            contractLedgerSummary.TryGetValue(customer.Id, out var contractLedger);


            return new CustomerBalanceListItemViewModel
            {
                Id = customer.Id,
                Name = customer.Name,
                Country = customer.Country,
                RelatedContractsCount = contracts?.Count ?? 0,
                ActiveContractsCount = contracts?.ActiveCount ?? 0,
                ClosedContractsCount = contracts?.ClosedCount ?? 0,
                TotalSalesUsd = salesTotal,
                TotalExpensesUsd = expensesTotal,
                TotalReceivedUsd = receivedSummary.GetValueOrDefault(customer.Id),
                RelatedLedgerCount = (directLedger?.Count ?? 0) + (contractLedger?.Count ?? 0),
                // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته).
                BaseBalanceUsd = officialCustomerBalances.TryGetValue(customer.Id, out var officialBalance)
                    ? officialBalance.ClosingBalanceUsd
                    : 0m
            };
        }).ToList();

        return View(new CustomersBalanceViewModel
        {
            FilteredTotalReceivedUsd = receivedSummary.Values.Sum(),
            FilteredTotalSalesUsd = salesSummary.Values.Sum(),
            FilteredTotalExpensesUsd = expensesSummary.Values.Sum(),
            FilteredBalanceUsd = officialCustomerBalances.Values.Sum(row => row.ClosingBalanceUsd),
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> CustomerDetails(int id)
    {
        var exists = await _db.Customers
            .AsNoTracking()
            .AnyAsync(c => c.Id == id);
        if (!exists)
        {
            return NotFound();
        }
        return RedirectToAction("Details", "Customers", new { id });
    }

    public async Task<IActionResult> Suppliers([FromQuery] SuppliersBalanceFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new SuppliersBalanceFilterViewModel();
        await PopulateSuppliersLookupsAsync(filter);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var supplierQuery = ApplySuppliersFilter(_db.Suppliers.AsNoTracking(), filter);
        var totalCount = await supplierQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var orderedSuppliers = supplierQuery.OrderBy(s => s.Name);

        var suppliers = await (page <= 0
                ? orderedSuppliers
                : orderedSuppliers
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Country
            })
            .ToListAsync();

        // ردیف‌ها همان صفحهٔ جاری‌اند، ولی جمع‌های کارت‌های بالا روی همهٔ نتایجِ فیلتر است؛
        // پس تجمیع‌ها برای همهٔ شناسه‌های فیلترشده ساخته می‌شوند (در دیتابیس، یک query برای هر جمع).
        var supplierIds = await supplierQuery.Select(supplier => supplier.Id).ToListAsync();
        // قرینهٔ صفحهٔ مشتری‌ها: فقط ردیف تأمین‌کننده خوانده می‌شود.
        var officialSupplierBalances = (await _partyBalances.GetBalancesAsync(
                new ManagementReportFilterViewModel
                {
                    FromDate = filter.FromDate,
                    ToDate = filter.ToDate
                },
                HttpContext?.RequestAborted ?? CancellationToken.None,
                partyTypes: [PartyStatementPartyType.Supplier]))
            .Where(row => row.PartyType == PartyStatementPartyType.Supplier
                && supplierIds.Contains(row.PartyId))
            .ToDictionary(row => row.PartyId);

        var filteredContractsQuery = _db.Contracts
            .AsNoTracking()
            .Where(c => c.SupplierId.HasValue && supplierIds.Contains(c.SupplierId.Value));
        if (filter.FromDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var contractSummary = supplierIds.Count == 0
            ? new Dictionary<int, CountAggregate>()
            : await filteredContractsQuery
                .GroupBy(c => c.SupplierId!.Value)
                .Select(g => new
                {
                    SupplierId = g.Key,
                    Summary = new CountAggregate(
                        g.Count(),
                        g.Count(c => c.Status == ContractStatus.Active),
                        g.Count(c => c.Status == ContractStatus.Closed))
                })
                .ToDictionaryAsync(x => x.SupplierId, x => x.Summary);

        // «جمع فروش» فقط فروشی است که خودِ تأمین‌کننده خریدار آن است. فروشِ نفتِ قرارداد خرید او
        // به یک مشتری دیگر فروش به تأمین‌کننده نیست و این‌جا شمرده نمی‌شود.
        var supplierSalesQuery = _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled && s.SupplierId.HasValue && supplierIds.Contains(s.SupplierId.Value));
        if (filter.FromDate.HasValue) supplierSalesQuery = supplierSalesQuery.Where(s => s.SaleDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) supplierSalesQuery = supplierSalesQuery.Where(s => s.SaleDate <= filter.ToDate.Value);
        var salesSummary = supplierIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await supplierSalesQuery
                .GroupBy(s => s.SupplierId!.Value)
                .Select(g => new { SupplierId = g.Key, TotalUsd = g.Sum(x => x.TotalUsd) })
                .ToDictionaryAsync(x => x.SupplierId, x => x.TotalUsd);

        // «جمع خرید» = ارزش خرید واقعیِ بارهای قیمت‌دارِ قراردادهای خرید همین تأمین‌کننده، از همان
        // موتور مالیِ «مفاد قراردادها». مبلغ قراردادِ اجرا‌نشده خرید قطعی حساب نمی‌شود.
        var purchaseContracts = await filteredContractsQuery
                .Where(c => c.ContractType == ContractType.Purchase)
                .Select(c => new { c.Id, SupplierId = c.SupplierId!.Value })
                .ToListAsync();
        var purchaseEconomics = purchaseContracts.Count == 0
            ? new Dictionary<int, ContractEconomicsSnapshot>()
            : await _profitAndLoss.BuildContractEconomicsAsync(
                purchaseContracts.Select(c => c.Id).ToList(),
                HttpContext?.RequestAborted ?? CancellationToken.None);
        var purchaseSummary = purchaseContracts
            .GroupBy(c => c.SupplierId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(c => purchaseEconomics.GetValueOrDefault(c.Id)?.PurchaseValueUsd ?? 0m));

        // «جمع پرداخت» = پول واقعیِ پرداخت‌شده (نقد، بانک، حوالهٔ صراف و تسویهٔ سه‌جانبه).
        var paidSummary = await _cashSettlements.GetSupplierNetPaymentsAsync(
            supplierIds, filter.FromDate, filter.ToDate, HttpContext?.RequestAborted ?? CancellationToken.None);

        var expensesSummary = supplierIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await (
                from e in _db.ExpenseTransactions.AsNoTracking()
                join c in filteredContractsQuery on e.ContractId equals c.Id
                where !e.IsCancelled
                    && (!filter.FromDate.HasValue || e.ExpenseDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || e.ExpenseDate <= filter.ToDate.Value)
                group e by c.SupplierId!.Value into g
                select new
                {
                    SupplierId = g.Key,
                    TotalUsd = g.Sum(x => x.AmountUsd)
                })
            .ToDictionaryAsync(x => x.SupplierId, x => x.TotalUsd);

        var directLedgerQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SupplierId.HasValue && supplierIds.Contains(l.SupplierId.Value));
        if (filter.FromDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value);
        var directLedgerSummary = supplierIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await directLedgerQuery
            .GroupBy(l => l.SupplierId!.Value)
            .Select(g => new
            {
                SupplierId = g.Key,
                Summary = new LedgerAggregate(g.Count())
            })
            .ToDictionaryAsync(x => x.SupplierId, x => x.Summary);

        var contractLedgerSummary = supplierIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await (
                from l in _db.LedgerEntries.AsNoTracking()
                join c in filteredContractsQuery on l.ContractId equals c.Id
                where !l.SupplierId.HasValue
                    && (!filter.FromDate.HasValue || l.EntryDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || l.EntryDate <= filter.ToDate.Value)
                group l by c.SupplierId!.Value into g
                select new
                {
                    SupplierId = g.Key,
                    Summary = new LedgerAggregate(g.Count())
                })
            .ToDictionaryAsync(x => x.SupplierId, x => x.Summary);

        var items = suppliers.Select(supplier =>
        {
            contractSummary.TryGetValue(supplier.Id, out var contracts);
            salesSummary.TryGetValue(supplier.Id, out var salesTotal);
            expensesSummary.TryGetValue(supplier.Id, out var expensesTotal);
            directLedgerSummary.TryGetValue(supplier.Id, out var directLedger);
            contractLedgerSummary.TryGetValue(supplier.Id, out var contractLedger);


            return new SupplierBalanceListItemViewModel
            {
                Id = supplier.Id,
                Name = supplier.Name,
                Country = supplier.Country,
                RelatedContractsCount = contracts?.Count ?? 0,
                ActiveContractsCount = contracts?.ActiveCount ?? 0,
                ClosedContractsCount = contracts?.ClosedCount ?? 0,
                TotalSalesUsd = salesTotal,
                TotalExpensesUsd = expensesTotal,
                TotalPurchaseUsd = purchaseSummary.GetValueOrDefault(supplier.Id),
                TotalPaidUsd = paidSummary.GetValueOrDefault(supplier.Id),
                RelatedLedgerCount = (directLedger?.Count ?? 0) + (contractLedger?.Count ?? 0),
                // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته) = پرداخت‌ها − بار.
                BaseBalanceUsd = officialSupplierBalances.TryGetValue(supplier.Id, out var officialBalance)
                    ? officialBalance.ClosingBalanceUsd
                    : 0m
            };
        }).ToList();

        return View(new SuppliersBalanceViewModel
        {
            FilteredTotalPurchaseUsd = purchaseSummary.Values.Sum(),
            FilteredTotalPaidUsd = paidSummary.Values.Sum(),
            FilteredTotalSalesUsd = salesSummary.Values.Sum(),
            FilteredTotalExpensesUsd = expensesSummary.Values.Sum(),
            FilteredBalanceUsd = officialSupplierBalances.Values.Sum(row => row.ClosingBalanceUsd),
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> SupplierDetails(int id)
    {
        var exists = await _db.Suppliers
            .AsNoTracking()
            .AnyAsync(s => s.Id == id);
        if (!exists)
        {
            return NotFound();
        }
        return RedirectToAction("Details", "Suppliers", new { id });
    }


    private sealed record CountAggregate(int Count, int ActiveCount, int ClosedCount);

    /// <summary>فقط تعدادِ اسناد؛ ماندهٔ طرف‌حساب از موتورِ رسمی می‌آید، نه از جمعِ خامِ Debit/Credit.</summary>
    private sealed record LedgerAggregate(int Count);

    private static string GetContractTypeName(ContractType contractType)
        => contractType == ContractType.Purchase ? "خرید" : "فروش";

    private static string GetContractStatusName(ContractStatus status)
        => status switch
        {
            ContractStatus.Draft => "پیش‌نویس",
            ContractStatus.Active => "فعال",
            ContractStatus.Closed => "بسته",
            ContractStatus.Cancelled => "لغو‌شده",
            _ => status.ToString()
        };
}
