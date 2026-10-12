using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.DeleteSafety;

public sealed record MasterDataDeleteSafetyResult(
    bool CanDelete,
    bool ArchiveInsteadOfDelete,
    string[] UsageAreas)
{
    public string DependencySummary => string.Join("، ", UsageAreas);

    public string BuildBlockedMessage(string entityTitle)
        => $"حذف {entityTitle} ممکن نیست، چون در {DependencySummary} استفاده شده است.";

    public string BuildArchivedMessage(string entityTitle)
        => $"{entityTitle} به دلیل استفاده در {DependencySummary} حذف نشد و غیرفعال شد.";

    public static MasterDataDeleteSafetyResult Allow()
        => new(true, false, []);

    public static MasterDataDeleteSafetyResult Archive(params string[] usageAreas)
        => new(false, true, usageAreas);

    public static MasterDataDeleteSafetyResult Block(params string[] usageAreas)
        => new(false, false, usageAreas);
}

public class MasterDataDeleteSafetyService
{
    private readonly ApplicationDbContext _db;

    public MasterDataDeleteSafetyService(ApplicationDbContext db) => _db = db;

    public async Task<MasterDataDeleteSafetyResult> EvaluateProductAsync(int productId)
    {
        var usageAreas = new List<string>();

        if (await _db.CustodyLots.AnyAsync(l => l.ProductId == productId))
            usageAreas.Add("امانی");

        if (await _db.Contracts.AnyAsync(c => c.ProductId == productId))
            usageAreas.Add("قراردادها");
        if (await _db.DailyPlattsPrices.AnyAsync(p => p.ProductId == productId))
            usageAreas.Add("قیمت‌های روز پلتس");
        if (await _db.StorageTanks.AnyAsync(t => t.ProductId == productId))
            usageAreas.Add("مخازن");
        if (await _db.InventoryBatches.AnyAsync(b => b.ProductId == productId))
            usageAreas.Add("بچ‌های موجودی");
        if (await _db.InventoryMovements.AnyAsync(m => m.ProductId == productId))
            usageAreas.Add("حرکات موجودی");
        if (await _db.LoadingRegisters.AnyAsync(l => l.ProductId == productId))
            usageAreas.Add("بارگیری‌ها");
        if (await _db.TruckDispatches.AnyAsync(d => d.ProductId == productId))
            usageAreas.Add("دیسپچ‌ها");
        if (await _db.SalesTransactions.AnyAsync(s => s.ProductId == productId))
            usageAreas.Add("فروش‌ها");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateCompanyAsync(int companyId)
    {
        var usageAreas = new List<string>();

        if (await _db.CustodySupplierIdentityLinks.AnyAsync(x => x.CompanyId == companyId))
            usageAreas.Add("پیوند هویت امانی");

        if (await _db.CustodyLots.AnyAsync(l => l.OwnerPartyType == AccountingPartyType.Company && l.OwnerPartyId == companyId)
            || await _db.Terminals.AnyAsync(t => t.CustodianPartyType == AccountingPartyType.Company && t.CustodianPartyId == companyId))
            usageAreas.Add("امانی");

        if (await _db.Contracts.AnyAsync(c => c.CompanyId == companyId))
            usageAreas.Add("قراردادها");
        if (await _db.SalesTransactions.AnyAsync(s => s.CompanyId == companyId))
            usageAreas.Add("فروش‌ها");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateSupplierAsync(int supplierId)
    {
        var usageAreas = new List<string>();

        if (await _db.CustodySupplierIdentityLinks.AnyAsync(x => x.SupplierId == supplierId))
            usageAreas.Add("پیوند هویت امانی");

        if (await _db.CustodyLots.AnyAsync(l => l.OwnerPartyType == AccountingPartyType.Supplier && l.OwnerPartyId == supplierId)
            || await _db.Terminals.AnyAsync(t => t.CustodianPartyType == AccountingPartyType.Supplier && t.CustodianPartyId == supplierId))
            usageAreas.Add("امانی");

        if (await _db.Contracts.AnyAsync(c => c.SupplierId == supplierId))
            usageAreas.Add("قراردادها");
        if (await _db.LedgerEntries.AnyAsync(l => l.SupplierId == supplierId))
            usageAreas.Add("دفتر کل");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateCustomerAsync(int customerId)
    {
        var usageAreas = new List<string>();

        if (await _db.CustodySupplierIdentityLinks.AnyAsync(x => x.CustomerId == customerId))
            usageAreas.Add("پیوند هویت امانی");

        if (await _db.CustodyLots.AnyAsync(l => l.OwnerPartyType == AccountingPartyType.Customer && l.OwnerPartyId == customerId)
            || await _db.Terminals.AnyAsync(t => t.CustodianPartyType == AccountingPartyType.Customer && t.CustodianPartyId == customerId)
            || await _db.CustodySales.AnyAsync(s => s.CustomerId == customerId))
            usageAreas.Add("امانی");

        if (await _db.Contracts.AnyAsync(c => c.CustomerId == customerId))
            usageAreas.Add("قراردادها");
        if (await _db.SalesTransactions.AnyAsync(s => s.CustomerId == customerId))
            usageAreas.Add("فروش‌ها");
        if (await _db.LedgerEntries.AnyAsync(l => l.CustomerId == customerId))
            usageAreas.Add("دفتر کل");
        if (await _db.CustomerToCustomerPayments.AnyAsync(p => p.PayerCustomerId == customerId || p.PayeeCustomerId == customerId))
            usageAreas.Add("پرداخت‌های بین مشتریان");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateTerminalAsync(int terminalId)
    {
        var usageAreas = new List<string>();

        if (await _db.CustodyMovements.AnyAsync(m => m.TerminalId == terminalId))
            usageAreas.Add("امانی");

        if (await _db.StorageTanks.AnyAsync(t => t.TerminalId == terminalId))
            usageAreas.Add("مخازن");
        if (await _db.InventoryBatches.AnyAsync(b => b.TerminalId == terminalId))
            usageAreas.Add("بچ‌های موجودی");
        if (await _db.InventoryMovements.AnyAsync(m => m.TerminalId == terminalId))
            usageAreas.Add("حرکات موجودی");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateStorageTankAsync(int storageTankId)
    {
        var usageAreas = new List<string>();

        if (await _db.CustodyMovements.AnyAsync(m => m.StorageTankId == storageTankId))
            usageAreas.Add("امانی");

        if (await _db.InventoryMovements.AnyAsync(m => m.StorageTankId == storageTankId))
            usageAreas.Add("حرکات موجودی");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateLocationAsync(int locationId)
    {
        var usageAreas = new List<string>();

        if (await _db.Contracts.AnyAsync(c => c.DestinationLocationId == locationId))
            usageAreas.Add("قراردادها");
        if (await _db.LoadingRegisters.AnyAsync(l => l.OriginLocationId == locationId))
            usageAreas.Add("بارگیری‌ها");
        if (await _db.TruckDispatches.AnyAsync(d => d.DestinationLocationId == locationId))
            usageAreas.Add("دیسپچ‌ها");
        if (await _db.Shipments.AnyAsync(s => s.OriginLocationId == locationId || s.DestinationLocationId == locationId))
            usageAreas.Add("محموله‌ها");
        if (await _db.SalesTransactions.AnyAsync(s => s.DestinationLocationId == locationId))
            usageAreas.Add("فروش‌ها");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateExpenseTypeAsync(int expenseTypeId)
    {
        var usageAreas = new List<string>();

        if (await _db.ExpenseRules.AnyAsync(r => r.ExpenseTypeId == expenseTypeId))
            usageAreas.Add("قواعد هزینه");
        if (await _db.ExpenseTransactions.AnyAsync(t => t.ExpenseTypeId == expenseTypeId))
            usageAreas.Add("تراکنش‌های هزینه");
        if (await _db.CustodyFinanceEntries.AnyAsync(e => e.ExpenseTypeId == expenseTypeId))
            usageAreas.Add("اسناد مالی امانی");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateCurrencyAsync(string currencyCode)
    {
        var usageAreas = new List<string>();
        var normalizedCode = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();

        if (await _db.Contracts.AnyAsync(c => c.Currency == normalizedCode))
            usageAreas.Add("قراردادها");
        if (await _db.SalesTransactions.AnyAsync(s => s.Currency == normalizedCode))
            usageAreas.Add("فروش‌ها");
        if (await _db.ExpenseTransactions.AnyAsync(e => e.Currency == normalizedCode))
            usageAreas.Add("هزینه‌ها");
        if (await _db.PaymentTransactions.AnyAsync(p => p.Currency == normalizedCode))
            usageAreas.Add("پرداخت‌ها");
        if (await _db.CashAccounts.AnyAsync(a => a.Currency == normalizedCode))
            usageAreas.Add("حساب‌های نقد / بانک");
        if (await _db.ExpenseRules.AnyAsync(r => r.Currency == normalizedCode))
            usageAreas.Add("قواعد هزینه");
        if (await _db.DailyFxRates.AnyAsync(r => r.BaseCurrency == normalizedCode || r.QuoteCurrency == normalizedCode))
            usageAreas.Add("نرخ‌های ارز روزانه");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateUnitAsync(int unitId)
    {
        var usageAreas = new List<string>();

        if (await _db.Products.AnyAsync(p => p.UnitId == unitId))
            usageAreas.Add("کالاها");

        return BuildArchivableResult(usageAreas);
    }

    /// <summary>
    /// PTG-P2-01 — شریک فقط با «قرارداد مشارکتی» بسته نمی‌شود.
    ///
    /// پیش‌تر تنها <c>ContractPartners</c> بررسی می‌شد، پس شریکی که فقط «تأمین مالی» کرده
    /// بود <c>CanDelete=true</c> می‌گرفت، <c>Remove</c> می‌شد و کلید خارجیِ
    /// <c>Restrict</c> در دیتابیس یک <c>DbUpdateException</c>ِ مدیریت‌نشده می‌ساخت:
    /// داده سالم می‌ماند ولی کاربر خطای ۵۰۰ می‌دید. حالا هر ارجاعِ واقعیِ شریک شمرده
    /// می‌شود و پیام می‌گوید کجا استفاده شده است.
    /// </summary>
    public async Task<MasterDataDeleteSafetyResult> EvaluatePartnerAsync(int partnerId)
    {
        var usageAreas = new List<string>();

        if (await _db.CustodySupplierIdentityLinks.AnyAsync(x => x.PartnerId == partnerId))
            usageAreas.Add("پیوند هویت امانی");

        if (await _db.CustodyLots.AnyAsync(l => l.OwnerPartyType == AccountingPartyType.Partner && l.OwnerPartyId == partnerId)
            || await _db.Terminals.AnyAsync(t => t.CustodianPartyType == AccountingPartyType.Partner && t.CustodianPartyId == partnerId))
            usageAreas.Add("امانی");

        if (await _db.ContractPartners.AnyAsync(cp => cp.PartnerId == partnerId))
            usageAreas.Add("قراردادهای مشارکتی");
        if (await _db.PaymentTransactions.AnyAsync(p => p.PaidByPartnerId == partnerId))
            usageAreas.Add("پرداخت‌های تأمین‌مالی‌شده توسط شریک");
        if (await _db.PartnerSettlements.AnyAsync(s => s.FromPartnerId == partnerId || s.ToPartnerId == partnerId))
            usageAreas.Add("تسویه‌های شرکا");
        if (await _db.Contracts.AnyAsync(c => c.SaleProceedsHolderPartnerId == partnerId))
            usageAreas.Add("قراردادهایی که عاید فروششان نزد این شریک است");
        if (await _db.AssetOwnershipShares.AnyAsync(s => s.PartnerId == partnerId))
            usageAreas.Add("سهم مالکیت دارایی‌های عملیاتی");
        if (await _db.AssetRentShares.AnyAsync(s => s.PartnerId == partnerId))
            usageAreas.Add("سهم کرایه دارایی‌های عملیاتی");
        if (await _db.AssetRentTransactions.AnyAsync(t => t.ChargedToPartnerId == partnerId))
            usageAreas.Add("کرایه‌های تحمیل‌شده به این شریک");
        if (await _db.LedgerEntries.AnyAsync(l => l.PartnerId == partnerId))
            usageAreas.Add("روزنامچهٔ مالی شریک");

        return BuildArchivableResult(usageAreas);
    }

    public async Task<MasterDataDeleteSafetyResult> EvaluateUserAsync(int userId)
    {
        var usageAreas = new List<string>();

        if (await _db.FiscalYears.AnyAsync(fy => fy.OpenedByUserId == userId
                || fy.ClosedByUserId == userId
                || fy.ReopenedByUserId == userId))
            usageAreas.Add("سال‌های مالی");
        if (await _db.FiscalPeriods.AnyAsync(fp => fp.LockedByUserId == userId))
            usageAreas.Add("دوره‌های مالی");
        if (await _db.FiscalYearStatusHistories.AnyAsync(h => h.ChangedByUserId == userId))
            usageAreas.Add("تاریخچه وضعیت سال مالی");
        if (await _db.FiscalYearCloseRuns.AnyAsync(r => r.StartedByUserId == userId
                || r.CompletedByUserId == userId))
            usageAreas.Add("عملیات بستن سال مالی");
        if (await _db.JournalEntries.AnyAsync(j => j.PostedByUserId == userId))
            usageAreas.Add("اسناد حسابداری");

        return BuildArchivableResult(usageAreas);
    }

    private static MasterDataDeleteSafetyResult BuildArchivableResult(List<string> usageAreas)
        => usageAreas.Count == 0
            ? MasterDataDeleteSafetyResult.Allow()
            : MasterDataDeleteSafetyResult.Archive(usageAreas.ToArray());

    private static MasterDataDeleteSafetyResult BuildBlockingResult(List<string> usageAreas)
        => usageAreas.Count == 0
            ? MasterDataDeleteSafetyResult.Allow()
            : MasterDataDeleteSafetyResult.Block(usageAreas.ToArray());
}
