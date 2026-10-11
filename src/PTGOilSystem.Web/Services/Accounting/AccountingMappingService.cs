using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Services.Accounting;

/// <summary>
/// یک نقشِ حسابداری در <see cref="AccountingSettings"/>: کدام سرفصل برای کدام بخش عملیاتی سند می‌خورد.
/// کاربر عادی فقط مشتری، تأمین‌کننده و صندوق را انتخاب می‌کند؛ Adapterها حساب را از همین نقش‌ها و
/// هویتِ طرف/صندوق را از بُعدِ سطر سند (PartyType/PartyId و CashAccountId) برمی‌دارند.
/// </summary>
public sealed record AccountingMappingRole(
    string Key,
    string Section,
    string SectionEn,
    string Label,
    string LabelEn,
    string Usage,
    string UsageEn,
    AccountType ExpectedType,
    Func<AccountingSettings, int?> Get,
    Action<AccountingSettings, int> Set);

public static class AccountingMappingCatalog
{
    private const string Cash = "صندوق و بانک";
    private const string Parties = "طرف حساب‌ها";
    private const string Sales = "فروش";
    private const string Inventory = "موجودی";
    private const string InTransit = "کالای در راه";
    private const string Cogs = "بهای تمام‌شده";
    private const string Expenses = "مصارف";
    private const string Fx = "تسعیر ارز";
    private const string Equity = "سرمایه و بستن سال";
    private const string Assets = "دارایی‌های عملیاتی";

    /// <summary>همهٔ ارجاع‌های AccountingSettings به Account، به ترتیبِ نمایش.</summary>
    public static readonly IReadOnlyList<AccountingMappingRole> Roles =
    [
        new(nameof(AccountingSettings.CashBankControlAccountId), Cash, "Cash & bank",
            "کنترل صندوق و بانک", "Cash/bank control",
            "هر دریافت و پرداخت نقدی. صندوق/بانکِ انتخاب‌شده روی سطر سند (CashAccountId) می‌نشیند؛ برای هر صندوق حساب جدا لازم نیست.",
            "Every cash receipt and payment. The chosen till/bank rides on the line (CashAccountId); no account per till is needed.",
            AccountType.Asset, s => s.CashBankControlAccountId, (s, id) => s.CashBankControlAccountId = id),

        new(nameof(AccountingSettings.AccountsReceivableAccountId), Parties, "Parties",
            "حساب‌های دریافتنی مشتری", "Accounts receivable",
            "فروش نسیه و دریافت از مشتری. مشتری روی سطر سند (PartyType=Customer) ثبت می‌شود.",
            "Credit sales and customer receipts. The customer rides on the line (PartyType=Customer).",
            AccountType.Asset, s => s.AccountsReceivableAccountId, (s, id) => s.AccountsReceivableAccountId = id),
        new(nameof(AccountingSettings.CustomerAdvanceAccountId), Parties, "Parties",
            "پیش‌دریافت مشتری", "Customer advance",
            "دریافتِ علامت‌خورده به‌عنوان پیش‌دریافت و تهاترِ آن هنگام فروش.",
            "Receipts marked as advance and their offset on sale.",
            AccountType.Liability, s => s.CustomerAdvanceAccountId, (s, id) => s.CustomerAdvanceAccountId = id),
        new(nameof(AccountingSettings.AccountsPayableAccountId), Parties, "Parties",
            "حساب‌های پرداختنی تأمین‌کننده", "Accounts payable",
            "خرید از تأمین‌کننده، پرداخت به تأمین‌کننده و صراف، و مصارفِ با نوع بدهی «حساب‌های پرداختنی».",
            "Supplier purchases, supplier and sarraf payments, and expenses whose payable kind is Accounts payable.",
            AccountType.Liability, s => s.AccountsPayableAccountId, (s, id) => s.AccountsPayableAccountId = id),
        new(nameof(AccountingSettings.SupplierPrepaymentAccountId), Parties, "Parties",
            "پیش‌پرداخت به تأمین‌کننده", "Supplier prepayment",
            "پرداختِ علامت‌خورده به‌عنوان پیش‌پرداخت به تأمین‌کننده.",
            "Supplier payments marked as advance.",
            AccountType.Asset, s => s.SupplierPrepaymentAccountId, (s, id) => s.SupplierPrepaymentAccountId = id),
        new(nameof(AccountingSettings.FreightPayableAccountId), Parties, "Parties",
            "کرایهٔ پرداختنی (حمل‌کننده)", "Freight payable",
            "مصارف حمل، پرداخت به حمل‌کننده و جریمهٔ کسریِ حمل.",
            "Freight expenses, carrier payments and transport shortage charges.",
            AccountType.Liability, s => s.FreightPayableAccountId, (s, id) => s.FreightPayableAccountId = id),
        new(nameof(AccountingSettings.CommissionPayableAccountId), Parties, "Parties",
            "کمیسیون پرداختنی", "Commission payable",
            "مصارفِ با نوع بدهی «کمیسیون پرداختنی» و پرداختِ آن‌ها.",
            "Expenses whose payable kind is Commission payable, and their payment.",
            AccountType.Liability, s => s.CommissionPayableAccountId, (s, id) => s.CommissionPayableAccountId = id),
        new(nameof(AccountingSettings.EmployeeAdvanceAccountId), Parties, "Parties",
            "مساعدهٔ کارمند", "Employee advance",
            "مساعده و پیش‌پرداخت به کارمند.",
            "Advances paid to employees.",
            AccountType.Asset, s => s.EmployeeAdvanceAccountId, (s, id) => s.EmployeeAdvanceAccountId = id),
        new(nameof(AccountingSettings.EmployeePayableAccountId), Parties, "Parties",
            "بدهی به کارمند", "Employee payable",
            "معاش و بدهیِ پرداختنی به کارمند.",
            "Salaries and other amounts owed to employees.",
            AccountType.Liability, s => s.EmployeePayableAccountId, (s, id) => s.EmployeePayableAccountId = id),
        new(nameof(AccountingSettings.PartnerCurrentAccountId), Parties, "Parties",
            "جاری شرکا", "Partner current account",
            "پرداخت یا دریافتی که شریک به‌جای صندوق شرکت انجام داده، و تخصیص سود. شریک روی سطر سند ثبت می‌شود.",
            "Payments or receipts a partner handled instead of the company till, and profit allocation. The partner rides on the line.",
            AccountType.Equity, s => s.PartnerCurrentAccountId, (s, id) => s.PartnerCurrentAccountId = id),

        new(nameof(AccountingSettings.CustodyFundsPayableAccountId), Parties, "Parties",
            "وجوه امانی پرداختنی به مالکان", "Custody funds payable to owners",
            "پولِ وصول‌شدهٔ فروش امانی که از آنِ مالک است، منهای کمیشن، مصارف و پرداخت‌ها. مالک روی سطر سند.",
            "Collected custody sale proceeds owed to the owner, less commission, charges and payouts. The owner rides on the line.",
            AccountType.Liability, s => s.CustodyFundsPayableAccountId, (s, id) => s.CustodyFundsPayableAccountId = id),
        new(nameof(AccountingSettings.CustodyPendingCollectionAccountId), Parties, "Parties",
            "طلب امانی به نفع مالکان (کاهندهٔ دریافتنی)", "Custody receivables for owners (contra)",
            "طلبِ نسیهٔ فروش امانی که پولش از آنِ مالک است؛ تا وصول نه دارایی شرکت است نه بدهی‌اش.",
            "Uncollected custody sales belonging to the owner; neither a company asset nor a liability until collected.",
            AccountType.Asset, s => s.CustodyPendingCollectionAccountId, (s, id) => s.CustodyPendingCollectionAccountId = id),

        new(nameof(AccountingSettings.SalesRevenueAccountId), Sales, "Sales",
            "درآمد فروش", "Sales revenue",
            "بستانکارِ سند فروش (در برابر دریافتنی یا پیش‌دریافت مشتری).",
            "Credit side of every sale journal (against receivable or customer advance).",
            AccountType.Revenue, s => s.SalesRevenueAccountId, (s, id) => s.SalesRevenueAccountId = id),
        new(nameof(AccountingSettings.CustodyCommissionRevenueAccountId), Sales, "Sales",
            "درآمد کمیشن امانی", "Custody commission revenue",
            "کمیشنِ شرکت از فروش به نمایندگی مالک.",
            "Company commission on sales made on the owner's behalf.",
            AccountType.Revenue, s => s.CustodyCommissionRevenueAccountId, (s, id) => s.CustodyCommissionRevenueAccountId = id),
        new(nameof(AccountingSettings.CustodyServiceRevenueAccountId), Sales, "Sales",
            "درآمد خدمات امانی", "Custody service revenue",
            "حق مخزن، بارگیری و خدماتِ دیگرِ شرکت به مالکِ امانی.",
            "Storage, loading and other company services charged to custody owners.",
            AccountType.Revenue, s => s.CustodyServiceRevenueAccountId, (s, id) => s.CustodyServiceRevenueAccountId = id),

        new(nameof(AccountingSettings.InventoryAccountId), Inventory, "Inventory",
            "موجودی کالا", "Inventory",
            "ورود کالا به ترمینال/مخزن و خروجِ آن با فروش، ضایعات یا انتقال.",
            "Goods received into terminals/tanks and relieved by sale, loss or transfer.",
            AccountType.Asset, s => s.InventoryAccountId, (s, id) => s.InventoryAccountId = id),
        new(nameof(AccountingSettings.InventoryLossAccountId), Inventory, "Inventory",
            "کسری و ضایعات موجودی", "Inventory loss",
            "کسری رسید، ضایعات و کسریِ حملِ بین‌ترمینالی.",
            "Receipt shortage, losses and inter-terminal transport shortage.",
            AccountType.Expense, s => s.InventoryLossAccountId, (s, id) => s.InventoryLossAccountId = id),

        new(nameof(AccountingSettings.InventoryInTransitAccountId), InTransit, "Goods in transit",
            "کالای در راه", "Inventory in transit",
            "خریدِ بارگیری‌شده تا رسیدن به ترمینال، و حملِ بین‌ترمینالی تا رسید.",
            "Loaded purchases until terminal receipt, and inter-terminal transfers until received.",
            AccountType.Asset, s => s.InventoryInTransitAccountId, (s, id) => s.InventoryInTransitAccountId = id),

        new(nameof(AccountingSettings.CostOfGoodsSoldAccountId), Cogs, "Cost of goods sold",
            "بهای تمام‌شدهٔ کالای فروش‌رفته", "Cost of goods sold",
            "بدهکارِ سند بهای تمام‌شده هنگام فروش، به میانگین موزونِ موجودی.",
            "Debit side of the cost journal on each sale, at weighted average cost.",
            AccountType.Expense, s => s.CostOfGoodsSoldAccountId, (s, id) => s.CostOfGoodsSoldAccountId = id),

        new(nameof(AccountingSettings.GeneralExpenseAccountId), Expenses, "Expenses",
            "مصارف عمومی", "General expense",
            "بدهکارِ سند هر مصرفِ ثبت‌شده.",
            "Debit side of every recorded expense.",
            AccountType.Expense, s => s.GeneralExpenseAccountId, (s, id) => s.GeneralExpenseAccountId = id),
        new(nameof(AccountingSettings.SalaryExpenseAccountId), Expenses, "Expenses",
            "مصرف معاش", "Salary expense",
            "بدهکارِ سندِ ثبت معاش و معاش نهایی‌شدهٔ ماه (در برابر «بدهی به کارمند»).",
            "Debit side of salary accruals and finalized payroll (against Employee payable).",
            AccountType.Expense, s => s.SalaryExpenseAccountId, (s, id) => s.SalaryExpenseAccountId = id),
        new(nameof(AccountingSettings.AccruedExpenseAccountId), Expenses, "Expenses",
            "مصارف معوق", "Accrued expenses",
            "مصارفِ با نوع بدهی «مصارف معوق» و پرداختِ آن‌ها.",
            "Expenses whose payable kind is Accrued expense, and their payment.",
            AccountType.Liability, s => s.AccruedExpenseAccountId, (s, id) => s.AccruedExpenseAccountId = id),

        new(nameof(AccountingSettings.ExchangeGainAccountId), Fx, "FX",
            "سود تسعیر ارز", "Exchange gain",
            "اختلافِ مثبتِ نرخ در تسویه و تسعیرِ پایان دوره.",
            "Favourable rate differences on settlement and period-end revaluation.",
            AccountType.Revenue, s => s.ExchangeGainAccountId, (s, id) => s.ExchangeGainAccountId = id),
        new(nameof(AccountingSettings.ExchangeLossAccountId), Fx, "FX",
            "زیان تسعیر ارز", "Exchange loss",
            "اختلافِ منفیِ نرخ در تسویه و تسعیرِ پایان دوره.",
            "Adverse rate differences on settlement and period-end revaluation.",
            AccountType.Expense, s => s.ExchangeLossAccountId, (s, id) => s.ExchangeLossAccountId = id),

        new(nameof(AccountingSettings.CurrentYearProfitLossAccountId), Equity, "Equity & year close",
            "سود و زیان سال جاری", "Current year profit/loss",
            "بستنِ حساب‌های درآمد و مصرف در بستن سال مالی.",
            "Receives revenue and expense balances at fiscal year close.",
            AccountType.Equity, s => s.CurrentYearProfitLossAccountId, (s, id) => s.CurrentYearProfitLossAccountId = id),
        new(nameof(AccountingSettings.RetainedEarningsAccountId), Equity, "Equity & year close",
            "سود انباشته", "Retained earnings",
            "انتقالِ سود و زیانِ سال پس از بستن.",
            "Receives the year's profit/loss after closing.",
            AccountType.Equity, s => s.RetainedEarningsAccountId, (s, id) => s.RetainedEarningsAccountId = id),

        new(nameof(AccountingSettings.FixedAssetAccountId), Assets, "Operational assets",
            "دارایی ثابت عملیاتی", "Operational fixed assets",
            "بهای دارایی‌های عملیاتی (موتر، واگن، مخزن).",
            "Cost of operational assets (trucks, wagons, tanks).",
            AccountType.Asset, s => s.FixedAssetAccountId, (s, id) => s.FixedAssetAccountId = id),
        new(nameof(AccountingSettings.AccumulatedDepreciationAccountId), Assets, "Operational assets",
            "استهلاک انباشته", "Accumulated depreciation",
            "کاهندهٔ دارایی ثابت (مانده بستانکار).",
            "Contra fixed-asset account (credit balance).",
            AccountType.Asset, s => s.AccumulatedDepreciationAccountId, (s, id) => s.AccumulatedDepreciationAccountId = id),
        new(nameof(AccountingSettings.DepreciationExpenseAccountId), Assets, "Operational assets",
            "مصرف استهلاک", "Depreciation expense",
            "استهلاکِ دوره‌ای دارایی‌های عملیاتی.",
            "Periodic depreciation of operational assets.",
            AccountType.Expense, s => s.DepreciationExpenseAccountId, (s, id) => s.DepreciationExpenseAccountId = id),
        new(nameof(AccountingSettings.AssetRentalRevenueAccountId), Assets, "Operational assets",
            "درآمد کرایهٔ دارایی", "Asset rental revenue",
            "کرایهٔ دارایی به طرف بیرونی.",
            "Renting an asset out to a third party.",
            AccountType.Revenue, s => s.AssetRentalRevenueAccountId, (s, id) => s.AssetRentalRevenueAccountId = id),
        new(nameof(AccountingSettings.InternalAssetRecoveryAccountId), Assets, "Operational assets",
            "بازیافت داخلی دارایی", "Internal asset recovery",
            "کرایهٔ داخلیِ دارایی که روی قرارداد/محموله شارژ می‌شود.",
            "Internal asset charge allocated to a contract or shipment.",
            AccountType.Revenue, s => s.InternalAssetRecoveryAccountId, (s, id) => s.InternalAssetRecoveryAccountId = id),
        new(nameof(AccountingSettings.AssetOperatingExpenseAccountId), Assets, "Operational assets",
            "مصرف عملیاتی دارایی", "Asset operating expense",
            "مصرفِ استفادهٔ داخلی از دارایی در قرارداد/محموله.",
            "Cost of internal asset usage on a contract or shipment.",
            AccountType.Expense, s => s.AssetOperatingExpenseAccountId, (s, id) => s.AssetOperatingExpenseAccountId = id)
    ];

    public static AccountingMappingRole? Find(string? key)
        => Roles.FirstOrDefault(role => string.Equals(role.Key, key, StringComparison.Ordinal));
}

public sealed record AccountingMappingUpdateResult(bool Succeeded, string Message);

public interface IAccountingMappingService
{
    Task<AccountingMappingPageViewModel> BuildAsync(CancellationToken cancellationToken = default);

    Task<AccountingMappingUpdateResult> UpdateAsync(
        string? roleKey,
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// نمایش و تغییرِ Mappingِ سرفصل‌ها به بخش‌های عملیاتی، روی همان <see cref="AccountingSettings"/>
/// که همهٔ Adapterها می‌خوانند — هیچ جدول یا منطقِ Posting تازه‌ای ساخته نمی‌شود.
///
/// قواعدِ ایمنی:
/// - نقشی که حسابِ فعلی‌اش سند دارد قفل است؛ جابه‌جایی، ماندهٔ قبلی را از گزارش‌ها و تهاترها
///   (مثلاً پیش‌دریافت مشتری) جدا می‌کند.
/// - حسابِ جدید باید متعلق به شرکت مالک، فعال و از نوعِ مورد انتظارِ نقش باشد.
/// - یک حساب فقط یک نقش می‌گیرد؛ Adapterها حساب‌های تکراری را ناقص می‌دانند و Skip می‌کنند.
/// </summary>
public sealed class AccountingMappingService(
    ApplicationDbContext db,
    ISystemCompanyProvider systemCompany,
    IAuditService audit) : IAccountingMappingService
{
    public async Task<AccountingMappingPageViewModel> BuildAsync(CancellationToken cancellationToken = default)
    {
        var ownerCompanyId = await systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (ownerCompanyId is null)
            return new AccountingMappingPageViewModel(null, false, null, []);

        var settings = await db.AccountingSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == ownerCompanyId.Value, cancellationToken);
        if (settings is null)
            return new AccountingMappingPageViewModel(ownerCompanyId, false, null, []);

        var mapped = AccountingMappingCatalog.Roles
            .Select(role => (Role: role, AccountId: role.Get(settings)))
            .ToList();
        var mappedIds = mapped
            .Where(x => x.AccountId.HasValue)
            .Select(x => x.AccountId!.Value)
            .ToList();

        var ownerAccounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == ownerCompanyId.Value || mappedIds.Contains(a.Id))
            .OrderBy(a => a.Code)
            .Select(a => new { a.Id, a.CompanyId, a.Code, a.Name, a.AccountType, a.IsActive })
            .ToListAsync(cancellationToken);
        var accountsById = ownerAccounts.ToDictionary(a => a.Id);

        var postedIds = (await db.JournalEntryLines.AsNoTracking()
                .Where(l => mappedIds.Contains(l.AccountId))
                .Select(l => l.AccountId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var useCount = mappedIds
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = mapped.Select(item =>
        {
            var role = item.Role;
            var current = item.AccountId is int id ? accountsById.GetValueOrDefault(id) : null;

            var issue = item.AccountId is null ? AccountingMappingIssue.NotMapped
                : current is null ? AccountingMappingIssue.Missing
                : current.CompanyId != ownerCompanyId.Value ? AccountingMappingIssue.WrongCompany
                : !current.IsActive ? AccountingMappingIssue.Inactive
                : current.AccountType != role.ExpectedType ? AccountingMappingIssue.WrongType
                : useCount[current.Id] > 1 ? AccountingMappingIssue.Duplicate
                : AccountingMappingIssue.None;

            // گزینه‌ها: حسابِ فعالِ همان شرکت و همان نوع، که نقشِ دیگری آن را نگرفته است.
            var options = ownerAccounts
                .Where(a => a.CompanyId == ownerCompanyId.Value
                    && a.IsActive
                    && a.AccountType == role.ExpectedType
                    && (a.Id == item.AccountId || !useCount.ContainsKey(a.Id)))
                .Select(a => new AccountingMappingOption(a.Id, a.Code, a.Name))
                .ToList();

            return new AccountingMappingRowViewModel(
                role.Key,
                role.Section,
                role.SectionEn,
                role.Label,
                role.LabelEn,
                role.Usage,
                role.UsageEn,
                role.ExpectedType,
                item.AccountId,
                current?.Code,
                current?.Name,
                issue,
                IsLocked: item.AccountId is int postedId && postedIds.Contains(postedId),
                options);
        }).ToList();

        return new AccountingMappingPageViewModel(
            ownerCompanyId,
            SettingsExist: true,
            settings.FunctionalCurrencyCode,
            rows);
    }

    public async Task<AccountingMappingUpdateResult> UpdateAsync(
        string? roleKey,
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var role = AccountingMappingCatalog.Find(roleKey);
        if (role is null)
            return Fail("نقش حساب نامعتبر است.");

        var ownerCompanyId = await systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (ownerCompanyId is null)
            return Fail("شرکت مالک سیستم تعیین نشده است.");

        var settings = await db.AccountingSettings
            .SingleOrDefaultAsync(x => x.CompanyId == ownerCompanyId.Value, cancellationToken);
        if (settings is null)
            return Fail("تنظیمات حسابداری برای شرکت مالک هنوز ساخته نشده است.");

        var currentId = role.Get(settings);
        if (currentId == accountId)
            return new AccountingMappingUpdateResult(true, "تغییری ثبت نشد؛ همین حساب از قبل انتخاب شده است.");

        if (currentId is int lockedId
            && await db.JournalEntryLines.AsNoTracking().AnyAsync(l => l.AccountId == lockedId, cancellationToken))
        {
            return Fail($"«{role.Label}» قفل است: حساب فعلی سند ثبت‌شده دارد و تغییر آن ماندهٔ قبلی را از گزارش‌ها و تهاترها جدا می‌کند.");
        }

        var target = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == ownerCompanyId.Value, cancellationToken);
        if (target is null)
            return Fail("حساب انتخاب‌شده متعلق به شرکت مالک نیست.");
        if (!target.IsActive)
            return Fail("حساب انتخاب‌شده غیرفعال است.");
        if (target.AccountType != role.ExpectedType)
            return Fail($"نوع حساب برای «{role.Label}» باید «{AccountTypeLabel(role.ExpectedType)}» باشد.");

        var otherRole = AccountingMappingCatalog.Roles
            .FirstOrDefault(r => r.Key != role.Key && r.Get(settings) == accountId);
        if (otherRole is not null)
            return Fail($"این حساب قبلاً برای «{otherRole.Label}» انتخاب شده است؛ هر حساب فقط یک نقش می‌گیرد.");

        role.Set(settings, accountId);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAndSaveAsync(
            nameof(AccountingSettings),
            settings.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate((role.Key, currentId, accountId)),
            ct: cancellationToken);

        return new AccountingMappingUpdateResult(
            true,
            $"«{role.Label}» به حساب {target.Code} - {target.Name} وصل شد. اسناد بعدی روی همین حساب ثبت می‌شوند.");
    }

    private static AccountingMappingUpdateResult Fail(string message) => new(false, message);

    private static string AccountTypeLabel(AccountType type) => type switch
    {
        AccountType.Asset => "دارایی",
        AccountType.Liability => "بدهی",
        AccountType.Equity => "سرمایه",
        AccountType.Revenue => "درآمد",
        AccountType.Expense => "مصرف",
        _ => type.ToString()
    };
}
