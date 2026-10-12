using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public interface IAccountingChartSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

public sealed class AccountingChartSeeder(
    ApplicationDbContext db,
    IOptions<AccountingOptions> options) : IAccountingChartSeeder
{
    // مرحله ۱۳ — MonetaryTreatment فقط برای حساب‌های استانداردی که Seeder مالکِ قطعیِ آن‌هاست
    // تعیین می‌شود. حداقلِ صریحِ پرامپ: Cash/Bank، AR و AP پولی‌اند. Advanceها و بقیهٔ حساب‌ها
    // پیش‌فرض NonMonetary — هیچ حسابی به‌طور ضمنی تسعیر نمی‌شود.
    private static readonly AccountSeed[] DefaultAccounts =
    [
        new("1100", "Cash/Bank Control", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.Monetary),
        new("1200", "Accounts Receivable", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.Monetary),
        // امانی: کاهندهٔ دریافتنی — طلبِ نسیهٔ فروشِ امانی که پولش از آنِ مالک است. شرکت تا وصول به
        // مالک بدهکار نیست، پس این طلب نه دارایی شرکت است نه بدهی‌اش؛ ۱۲۰۰ − ۱۲۱۰ = صفر.
        new("1210", "Custody Receivables for Owners (contra)", AccountType.Asset, NormalBalance.Credit, MonetaryTreatment.Monetary),
        new("1300", "Inventory", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1310", "Inventory In Transit", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1400", "Supplier Prepayment", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1410", "Employee Advance", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1500", "Operational Fixed Assets", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1590", "Accumulated Depreciation", AccountType.Asset, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2100", "Accounts Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.Monetary),
        new("2200", "Customer Advance", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2300", "Freight Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2400", "Commission Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2500", "Employee Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2510", "Accrued Expenses Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        // امانی: پولِ مالکان نزد شرکت و عوایدِ فروشِ هنوز وصول‌نشده — هر دو با هویتِ مالک روی سطر.
        new("2600", "Custody Funds Payable to Owners", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.Monetary),
        new("3100", "Current Year Profit/Loss", AccountType.Equity, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("3200", "Retained Earnings", AccountType.Equity, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        // جاریِ شرکا: یک حسابِ کنترلی، هویتِ شریک روی سطر (PartyType=Partner). مانده‌اش دوطرفه
        // است — بستانکار یعنی شراکت به شریک بدهکار است، بدهکار یعنی شریک به شراکت. طبقهٔ
        // Equity است چون هم آوردهٔ شریک و هم سهمِ سودش را نگه می‌دارد، نه یک بدهی تجاری.
        new("3300", "Partner Current Account", AccountType.Equity, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4100", "Sales Revenue", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4200", "Exchange Gain", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4300", "Asset Rental Revenue", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4400", "Internal Asset Recovery", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4500", "Custody Commission Revenue", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4510", "Custody Service Revenue", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("5100", "Cost of Goods Sold", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5200", "General Expense", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5300", "Exchange Loss", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5400", "Inventory Loss", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5500", "Depreciation Expense", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5600", "Asset Operating Expense", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5700", "Salary Expense", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary)
    ];

    private readonly AccountingOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (db.Database.IsRelational())
            DatabaseSafetyGuard.EnsureSeederAllowed(db.Database.GetDbConnection().Database);

        var companyIds = await db.Companies.AsNoTracking()
            .Where(company => !db.AccountingSettings.Any(settings => settings.CompanyId == company.Id))
            .Select(company => company.Id)
            .ToListAsync(cancellationToken);

        foreach (var companyId in companyIds)
            await SeedCompanyAsync(companyId, cancellationToken);

        await EnsurePartnerCurrentAccountAsync(cancellationToken);
        await EnsureSalaryExpenseAccountAsync(cancellationToken);
        await EnsureCustodyAccountsAsync(cancellationToken);
    }

    /// <summary>
    /// حساب‌های امانی (۲۶۰۰، ۱۲۱۰، ۴۵۰۰، ۴۵۱۰) بعد از تنظیماتِ شرکت‌های موجود اضافه شدند. همان الگوی
    /// ۵۷۰۰: فقط ارجاعِ خالی کامل می‌شود؛ حسابِ هم‌کدی که نوعش جور نیست خودکار وصل نمی‌شود.
    /// </summary>
    private async Task EnsureCustodyAccountsAsync(CancellationToken cancellationToken)
    {
        var pending = await db.AccountingSettings
            .Where(x => x.CustodyFundsPayableAccountId == null
                || x.CustodyPendingCollectionAccountId == null
                || x.CustodyCommissionRevenueAccountId == null
                || x.CustodyServiceRevenueAccountId == null)
            .ToListAsync(cancellationToken);

        foreach (var settings in pending)
        {
            settings.CustodyFundsPayableAccountId ??= await EnsureAccountAsync(settings.CompanyId, "2600", cancellationToken);
            settings.CustodyPendingCollectionAccountId ??= await EnsureAccountAsync(settings.CompanyId, "1210", cancellationToken);
            settings.CustodyCommissionRevenueAccountId ??= await EnsureAccountAsync(settings.CompanyId, "4500", cancellationToken);
            settings.CustodyServiceRevenueAccountId ??= await EnsureAccountAsync(settings.CompanyId, "4510", cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int?> EnsureAccountAsync(int companyId, string code, CancellationToken cancellationToken)
    {
        var seed = DefaultAccounts.Single(x => x.Code == code);
        var account = await db.Accounts
            .SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Code == seed.Code, cancellationToken);
        if (account is null)
        {
            account = new Account
            {
                CompanyId = companyId,
                Code = seed.Code,
                Name = seed.Name,
                AccountType = seed.AccountType,
                NormalBalance = seed.NormalBalance,
                IsControlAccount = true,
                AllowManualPosting = false,
                IsActive = true,
                MonetaryTreatment = seed.MonetaryTreatment
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);
        }

        // حسابِ موجود با همین کد فقط وقتی به امانی وصل می‌شود که نوع و سمت طبیعی‌اش همان طبقه‌بندی باشد؛
        // مثلاً «۱۲۱۰»ِ دیگری با سمت بدهکار، کاهندهٔ دریافتنیِ امانی (سمت بستانکار) نیست.
        return account.AccountType == seed.AccountType && account.NormalBalance == seed.NormalBalance ? account.Id : null;
    }

    /// <summary>
    /// حساب ۵۷۰۰ هم مثل ۳۳۰۰ بعد از ساخته‌شدنِ تنظیماتِ شرکت‌های موجود اضافه شد. همان الگو: فقط همین
    /// یک حساب و همین یک ارجاعِ خالی کامل می‌شود؛ ارجاعِ پرشده بازنویسی نمی‌شود.
    /// </summary>
    private async Task EnsureSalaryExpenseAccountAsync(CancellationToken cancellationToken)
    {
        var pending = await db.AccountingSettings
            .Where(x => x.SalaryExpenseAccountId == null)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
            return;

        var seed = DefaultAccounts.Single(x => x.Code == "5700");
        foreach (var settings in pending)
        {
            var account = await db.Accounts
                .SingleOrDefaultAsync(
                    x => x.CompanyId == settings.CompanyId && x.Code == seed.Code,
                    cancellationToken);

            if (account is null)
            {
                account = new Account
                {
                    CompanyId = settings.CompanyId,
                    Code = seed.Code,
                    Name = seed.Name,
                    AccountType = seed.AccountType,
                    NormalBalance = seed.NormalBalance,
                    IsControlAccount = true,
                    AllowManualPosting = false,
                    IsActive = true,
                    MonetaryTreatment = seed.MonetaryTreatment
                };
                db.Accounts.Add(account);
                await db.SaveChangesAsync(cancellationToken);
            }

            // حسابِ ۵۷۰۰ که کاربر پیش‌تر برای کار دیگری ساخته باشد و نوعش مصرف نباشد، خودکار
            // وصل نمی‌شود؛ تعیینش به صفحهٔ «نقشه حساب‌ها» واگذار می‌شود.
            if (account.AccountType == AccountType.Expense)
                settings.SalaryExpenseAccountId = account.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// حساب ۳۳۰۰ بعد از ساخته‌شدنِ تنظیماتِ شرکت‌های موجود به Chart اضافه شد، پس
    /// <see cref="SeedCompanyAsync"/> — که فقط شرکتِ بدونِ تنظیمات را می‌سازد — هرگز به آن‌ها
    /// نمی‌رسد. این گذر همان یک حساب و همان یک ارجاع را برایشان کامل می‌کند و بس: هیچ حساب
    /// دیگری را نمی‌سازد و هیچ ارجاعِ پرشده‌ای را بازنویسی نمی‌کند.
    /// </summary>
    private async Task EnsurePartnerCurrentAccountAsync(CancellationToken cancellationToken)
    {
        var pending = await db.AccountingSettings
            .Where(x => x.PartnerCurrentAccountId == null)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
            return;

        var seed = DefaultAccounts.Single(x => x.Code == "3300");
        foreach (var settings in pending)
        {
            var account = await db.Accounts
                .SingleOrDefaultAsync(
                    x => x.CompanyId == settings.CompanyId && x.Code == seed.Code,
                    cancellationToken);

            if (account is null)
            {
                account = new Account
                {
                    CompanyId = settings.CompanyId,
                    Code = seed.Code,
                    Name = seed.Name,
                    AccountType = seed.AccountType,
                    NormalBalance = seed.NormalBalance,
                    IsControlAccount = true,
                    AllowManualPosting = false,
                    IsActive = true,
                    MonetaryTreatment = seed.MonetaryTreatment
                };
                db.Accounts.Add(account);
                await db.SaveChangesAsync(cancellationToken);
            }

            settings.PartnerCurrentAccountId = account.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedCompanyAsync(int companyId, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational())
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (await db.AccountingSettings.AnyAsync(x => x.CompanyId == companyId, cancellationToken))
                return;

            var accountsByCode = await db.Accounts
                .Where(x => x.CompanyId == companyId)
                .ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);

            if (accountsByCode.TryGetValue("2500", out var employeePayable)
                && employeePayable.Name == "Employee/Accrued Payable"
                && !await db.JournalEntryLines.AsNoTracking()
                    .AnyAsync(x => x.AccountId == employeePayable.Id, cancellationToken))
            {
                employeePayable.Name = "Employee Payable";
            }

            foreach (var seed in DefaultAccounts)
            {
                if (accountsByCode.ContainsKey(seed.Code))
                    continue;

                var account = new Account
                {
                    CompanyId = companyId,
                    Code = seed.Code,
                    Name = seed.Name,
                    AccountType = seed.AccountType,
                    NormalBalance = seed.NormalBalance,
                    IsControlAccount = true,
                    AllowManualPosting = false,
                    IsActive = true,
                    MonetaryTreatment = seed.MonetaryTreatment
                };
                db.Accounts.Add(account);
                accountsByCode.Add(seed.Code, account);
            }

            await db.SaveChangesAsync(cancellationToken);

            db.AccountingSettings.Add(new AccountingSettings
            {
                CompanyId = companyId,
                FunctionalCurrencyCode = NormalizeCurrency(_options.DefaultFunctionalCurrencyCode),
                CashBankControlAccountId = accountsByCode["1100"].Id,
                AccountsReceivableAccountId = accountsByCode["1200"].Id,
                InventoryAccountId = accountsByCode["1300"].Id,
                InventoryInTransitAccountId = accountsByCode["1310"].Id,
                SupplierPrepaymentAccountId = accountsByCode["1400"].Id,
                EmployeeAdvanceAccountId = accountsByCode["1410"].Id,
                AccountsPayableAccountId = accountsByCode["2100"].Id,
                CustomerAdvanceAccountId = accountsByCode["2200"].Id,
                FreightPayableAccountId = accountsByCode["2300"].Id,
                CommissionPayableAccountId = accountsByCode["2400"].Id,
                EmployeePayableAccountId = accountsByCode["2500"].Id,
                AccruedExpenseAccountId = accountsByCode["2510"].Id,
                CurrentYearProfitLossAccountId = accountsByCode["3100"].Id,
                RetainedEarningsAccountId = accountsByCode["3200"].Id,
                PartnerCurrentAccountId = accountsByCode["3300"].Id,
                SalesRevenueAccountId = accountsByCode["4100"].Id,
                ExchangeGainAccountId = accountsByCode["4200"].Id,
                AssetRentalRevenueAccountId = accountsByCode["4300"].Id,
                InternalAssetRecoveryAccountId = accountsByCode["4400"].Id,
                CostOfGoodsSoldAccountId = accountsByCode["5100"].Id,
                GeneralExpenseAccountId = accountsByCode["5200"].Id,
                ExchangeLossAccountId = accountsByCode["5300"].Id,
                InventoryLossAccountId = accountsByCode["5400"].Id,
                DepreciationExpenseAccountId = accountsByCode["5500"].Id,
                AssetOperatingExpenseAccountId = accountsByCode["5600"].Id,
                SalaryExpenseAccountId = accountsByCode["5700"].Id,
                CustodyFundsPayableAccountId = accountsByCode["2600"].Id,
                CustodyPendingCollectionAccountId = accountsByCode["1210"].Id,
                CustodyCommissionRevenueAccountId = accountsByCode["4500"].Id,
                CustodyServiceRevenueAccountId = accountsByCode["4510"].Id,
                FixedAssetAccountId = accountsByCode["1500"].Id,
                AccumulatedDepreciationAccountId = accountsByCode["1590"].Id
            });

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private static string NormalizeCurrency(string value)
        => string.IsNullOrWhiteSpace(value) ? "USD" : value.Trim().ToUpperInvariant();

    private sealed record AccountSeed(
        string Code,
        string Name,
        AccountType AccountType,
        NormalBalance NormalBalance,
        MonetaryTreatment MonetaryTreatment);
}
