using System.Security.Claims;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Security;

public static class AppPermissions
{
    public const string ManageData = "ManageData";
    public const string ManageUsers = "ManageUsers";

    /// <summary>
    /// مرحله ۱۱ — اجازهٔ ثبتِ استثنایی در دورهٔ قفل‌نرم. عمداً از نقش استنتاج نمی‌شود (حتی Admin):
    /// «فقط با Permission مشخص» یعنی کسی که این Claim را ندارد، هر نقشی هم داشته باشد، نمی‌تواند.
    /// قفلِ سخت با این Permission هم باز نمی‌شود.
    /// </summary>
    public const string PostToSoftLockedPeriod = "PostToSoftLockedPeriod";

    /// <summary>
    /// مرحله ۱۵ — اجازهٔ بازگشاییِ کنترل‌شدهٔ سالِ بسته. مثل PostToSoftLockedPeriod عمداً از نقش
    /// استنتاج نمی‌شود؛ بدون این Claim حتی Admin هم نمی‌تواند سال را بازگشایی کند.
    /// </summary>
    public const string ReopenFiscalYear = "ReopenFiscalYear";

    /// <summary>
    /// دسترسی به صفحهٔ پشتیبان‌گیری برای کاربری که نقش Admin ندارد.
    /// «مدیریت کاربران» این دسترسی را نمی‌دهد؛ باید صریح داده شود.
    /// </summary>
    public const string ManageBackups = "ManageBackups";

    /// <summary>
    /// PTG-P1-01 — اجازهٔ ثبتِ استثنایی در دورهٔ عملیاتیِ بسته. مثل PostToSoftLockedPeriod
    /// عمداً از نقش استنتاج نمی‌شود؛ بدون این Claim حتی Admin هم نمی‌تواند در ماهِ بسته
    /// سند بزند، و هر عبور با دلیل در Audit ثبت می‌شود.
    /// </summary>
    public const string PostToClosedOperationalPeriod = "PostToClosedOperationalPeriod";

    /// <summary>
    /// PTG-P1-01 — اجازهٔ بستن و بازکردنِ دورهٔ عملیاتی. جدا از «ثبتِ استثنایی» است:
    /// کسی که می‌تواند در دورهٔ بسته سند بزند لزوماً نباید بتواند دوره را باز کند.
    /// </summary>
    public const string ManageOperationalPeriodLock = "ManageOperationalPeriodLock";

    // مدیریت بشری — اطلاعات معاش حساس است و از «ثبت و ویرایش اطلاعات» جداست. دیدنِ فهرست
    // کارمندان با کلید ناوبری «مدیریت بشری» و ثبت/ویرایش مشخصات با ManageData کنترل می‌شود؛
    // این چهار مورد فقط با اعطای صریح در نقش (یا نقش Admin) باز می‌شوند.
    public const string ViewEmployeeSalary = "HR.ViewSalary";
    public const string ManageEmployeeSalary = "HR.ManageSalary";
    public const string RunPayroll = "HR.RunPayroll";
    public const string PaySalary = "HR.PaySalary";
}

/// <summary>Permissionی که در صفحهٔ نقش‌ها قابل اعطاست.</summary>
public sealed record GrantablePermission(string Key, string Label, string LabelEn, string Hint);

public sealed record RoleNavigationItem(
    string Key,
    string Label,
    string Icon,
    string[] Controllers,
    bool IsSensitive = false);

public static class RoleNavigationKeys
{
    public const string Dashboard = "Dashboard";
    public const string Contracts = "Contracts";
    public const string Operations = "Operations";
    public const string Inventory = "Inventory";
    public const string OperationalAssets = "OperationalAssets";
    public const string Sales = "Sales";
    public const string CashAccounts = "CashAccounts";
    public const string Payments = "Payments";
    public const string Reports = "Reports";
    public const string Partners = "Partners";
    public const string HumanResources = "HumanResources";
    public const string BaseDefinitions = "BaseDefinitions";
    public const string Rates = "Rates";
    public const string Management = "Management";
}

public static class RoleAccessRules
{
    /// <summary>همهٔ صفحه‌های «مدیریت بشری» — یک کلید ناوبری، یک گروه تب.</summary>
    public static readonly string[] HumanResourcesControllers =
    [
        "HrDashboard", "Employees", "Attendance", "Leave", "Payroll", "EmployeeLoans",
        "EmploymentContracts", "HrOrganization", "HrReports", "HrSettings"
    ];

    public static readonly RoleNavigationItem[] NavigationItems =
    [
        new(RoleNavigationKeys.Dashboard, "داشبورد", "bi-house-fill", ["Home"]),
        new(RoleNavigationKeys.Contracts, "قراردادها", "bi-file-earmark-text-fill",
            ["Contracts", "ContractAmendments", "ContractJourney", "ContractBalanceTransfers"]),
        new(RoleNavigationKeys.Operations, "عملیات", "bi-truck-front-fill",
            ["Loading", "LoadingExcelImport", "InventoryTransportLegs", "InventoryTransportReceipts", "InventoryLineage", "Transports",
                "Shipments", "ShipmentContracts", "ShipmentPnl", "Dispatch", "TruckSettlements", "Expenses",
                "LossEvents", "LoadingReceipts", "CustomsDeclarations", "QualityInspections"]),
        new(RoleNavigationKeys.Inventory, "موجودی", "bi-box-seam-fill",
            ["Inventory", "InventoryReports", "Custody"]),
        new(RoleNavigationKeys.OperationalAssets, "دارایی‌های عملیاتی", "bi-truck-front-fill",
            ["OperationalAssets"]),
        new(RoleNavigationKeys.Sales, "فروش", "bi-cart-check-fill",
            ["Sales", "Invoices"]),
        new(RoleNavigationKeys.CashAccounts, "حساب‌ها و مالی", "bi-wallet-fill",
            ["CashAccounts", "Ledger", "Balance", "Finance", "AccountingReadiness", "ChartOfAccounts",
                "ClosingChecklist", "FinalClose", "FiscalYears", "FiscalYearContext", "PeriodActivity", "TrialClose"]),
        new(RoleNavigationKeys.Payments, "روزنامچه و حواله‌ها", "bi-credit-card-2-front-fill",
            ["Payments", "AccountStatements", "PartyStatements", "SarrafSettlements", "ThreeWaySettlement", "ViaSarrafPayments", "CustomerToCustomerPayments", "PartySettlements"]),
        new(RoleNavigationKeys.Reports, "گزارشات", "bi-clipboard-data-fill",
            ["Reports", "Reconciliation", "CustomsPermitTurnover"]),
        new(RoleNavigationKeys.Partners, "اشخاص", "bi-person-vcard-fill",
            ["Partners", "PartnershipStatement", "Companies", "Suppliers", "SupplierBalanceTransfers", "Customers", "ServiceProviders", "Sarrafs"]),
        new(RoleNavigationKeys.HumanResources, "مدیریت بشری", "bi-people-fill",
            HumanResourcesControllers),
        new(RoleNavigationKeys.BaseDefinitions, "تعاریف پایه", "bi-database-fill-gear",
            ["Products", "Units", "Currencies", "DailyFxRates", "Locations", "ExpenseTypes", "ExpenseRules",
                "Terminals", "StorageTanks", "Trucks", "Wagons", "Drivers", "Vessels", "AutoCodes"]),
        new(RoleNavigationKeys.Rates, "نرخ‌ها و قواعد", "bi-bar-chart-line-fill",
            ["PlattsRates"]),
        new(RoleNavigationKeys.Management, "مدیریت کاربران", "bi-person-fill-gear",
            ["Users", "Roles", "AuditLogs", "Backups", "BackupRestore", "Maintenance", "OperationalPeriodLocks", "SystemSettings"], IsSensitive: true)
    ];

    public static IReadOnlySet<string> BusinessNavigationKeys { get; } =
        NavigationItems
            .Where(item => item.Key != RoleNavigationKeys.Management)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> AllNavigationKeys { get; } =
        NavigationItems
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string SerializeNavigation(IEnumerable<string>? keys)
        => string.Join(",", NormalizeNavigation(keys));

    public static string[] NormalizeNavigation(IEnumerable<string>? keys)
    {
        var allowedKeys = AllNavigationKeys;
        var normalized = (keys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Where(key => allowedKeys.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => Array.FindIndex(NavigationItems, item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (!normalized.Contains(RoleNavigationKeys.Dashboard, StringComparer.OrdinalIgnoreCase))
        {
            normalized.Insert(0, RoleNavigationKeys.Dashboard);
        }

        return normalized.ToArray();
    }

    public static IReadOnlySet<string> ParseNavigation(string? raw)
        => NormalizeNavigation((raw ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> DefaultNavigationForRole(string? roleName)
    {
        if (string.Equals(roleName, AuthRoles.Admin, StringComparison.Ordinal))
        {
            return AllNavigationKeys;
        }

        if (string.Equals(roleName, AuthRoles.Manager, StringComparison.Ordinal)
            || string.Equals(roleName, AuthRoles.Operator, StringComparison.Ordinal)
            || string.Equals(roleName, AuthRoles.Viewer, StringComparison.Ordinal))
        {
            return BusinessNavigationKeys;
        }

        return new HashSet<string>([RoleNavigationKeys.Dashboard], StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<string> ResolveNavigationForRole(Role? role)
    {
        if (role is null)
        {
            return new HashSet<string>([RoleNavigationKeys.Dashboard], StringComparer.OrdinalIgnoreCase);
        }

        var keys = !string.IsNullOrWhiteSpace(role.AllowedNavigationItems)
            ? ParseNavigation(role.AllowedNavigationItems)
            : DefaultNavigationForRole(role.Name);

        if (RoleCanManageUsers(role))
        {
            return NormalizeNavigation(keys.Concat([RoleNavigationKeys.Management]))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return keys;
    }

    /// <summary>Permissionهای قابل اعطا در نقش، به ترتیبِ نمایش.</summary>
    public static readonly GrantablePermission[] GrantablePermissions =
    [
        new(AppPermissions.ViewEmployeeSalary, "دیدن معاش کارمندان", "View employee salary",
            "مبلغ معاش، ماندهٔ حساب، قرضه و مساعدهٔ کارمند نمایش داده می‌شود."),
        new(AppPermissions.ManageEmployeeSalary, "مدیریت معاش", "Manage salary",
            "تعیین و تغییر معاش، ثبت و لغو تراکنش معاش، قرضه و مساعده."),
        new(AppPermissions.RunPayroll, "ساخت و نهایی‌سازی معاش ماه", "Run payroll",
            "ساخت، ویرایش، نهایی‌سازی و بازگشایی معاش ماهانه."),
        new(AppPermissions.PaySalary, "پرداخت معاش", "Pay salary",
            "پرداخت معاش و مساعده از صندوق یا بانک.")
    ];

    public static string[] NormalizeGrantedPermissions(IEnumerable<string>? keys)
    {
        var known = GrantablePermissions.Select(p => p.Key).ToArray();
        return (keys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Where(key => known.Contains(key, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => Array.IndexOf(known, key))
            .ToArray();
    }

    public static string? SerializeGrantedPermissions(IEnumerable<string>? keys)
    {
        var normalized = NormalizeGrantedPermissions(keys);
        return normalized.Length == 0 ? null : string.Join(",", normalized);
    }

    /// <summary>Permissionهای اعطاشده به نقش. نقش Admin همه را دارد.</summary>
    public static string[] ResolveGrantedPermissions(Role? role)
    {
        if (role is null)
        {
            return [];
        }

        if (string.Equals(role.Name, AuthRoles.Admin, StringComparison.Ordinal))
        {
            return GrantablePermissions.Select(p => p.Key).ToArray();
        }

        return NormalizeGrantedPermissions(
            (role.GrantedPermissions ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool HasPermission(ClaimsPrincipal user, string permission)
        => IsSuperAdmin(user) || user.HasClaim(AppClaimTypes.Permission, permission);

    /// <summary>
    /// مبلغ معاش و ماندهٔ مالیِ کارمند. هر Permissionِ مالیِ منابع بشری دیدن را هم می‌دهد، چون
    /// بدونِ دیدنِ مبلغ نمی‌توان معاش را مدیریت، محاسبه یا پرداخت کرد.
    /// </summary>
    public static bool CanViewEmployeeSalary(ClaimsPrincipal user)
        => HasPermission(user, AppPermissions.ViewEmployeeSalary)
            || HasPermission(user, AppPermissions.ManageEmployeeSalary)
            || HasPermission(user, AppPermissions.RunPayroll)
            || HasPermission(user, AppPermissions.PaySalary);

    public static bool CanManageEmployeeSalary(ClaimsPrincipal user)
        => HasPermission(user, AppPermissions.ManageEmployeeSalary);

    public static bool CanRunPayroll(ClaimsPrincipal user)
        => HasPermission(user, AppPermissions.RunPayroll);

    public static bool CanPaySalary(ClaimsPrincipal user)
        => HasPermission(user, AppPermissions.PaySalary);

    public static bool RoleCanManageData(Role? role)
        => role is not null && (
            role.CanManageData
            || string.Equals(role.Name, AuthRoles.Admin, StringComparison.Ordinal)
            || string.Equals(role.Name, AuthRoles.Manager, StringComparison.Ordinal)
            || string.Equals(role.Name, AuthRoles.Operator, StringComparison.Ordinal));

    public static bool RoleCanManageUsers(Role? role)
        => role is not null && (
            role.CanManageUsers
            || string.Equals(role.Name, AuthRoles.Admin, StringComparison.Ordinal));

    public static string? NavigationKeyForController(string? controller)
    {
        if (string.IsNullOrWhiteSpace(controller))
        {
            return null;
        }

        return NavigationItems
            .FirstOrDefault(item => item.Controllers.Contains(controller, StringComparer.OrdinalIgnoreCase))
            ?.Key;
    }

    public static bool CanManageData(ClaimsPrincipal user)
        => user.IsInRole(AuthRoles.Admin)
            || user.IsInRole(AuthRoles.Manager)
            || user.IsInRole(AuthRoles.Operator)
            || user.HasClaim(AppClaimTypes.Permission, AppPermissions.ManageData);

    public static bool CanManageUsers(ClaimsPrincipal user)
        => user.IsInRole(AuthRoles.Admin)
            || user.HasClaim(AppClaimTypes.Permission, AppPermissions.ManageUsers);

    /// <summary>
    /// بالاترین سطح دسترسی سیستم. در این برنامه نقش Admin همان SuperAdmin است؛
    /// نقش‌های دیگر — حتی با اجازهٔ مدیریت کاربران — SuperAdmin نیستند.
    /// </summary>
    public static bool IsSuperAdmin(ClaimsPrincipal user)
        => user.IsInRole(AuthRoles.Admin);

    /// <summary>دسترسی به صفحهٔ پشتیبان‌گیری: SuperAdmin یا Permission صریح ManageBackups.</summary>
    public static bool CanManageBackups(ClaimsPrincipal user)
        => IsSuperAdmin(user)
            || user.HasClaim(AppClaimTypes.Permission, AppPermissions.ManageBackups);

    /// <summary>
    /// دسترسی به بازیابی. برخلاف صفحهٔ پشتیبان‌گیری، Permission جایگزین نقش نمی‌شود:
    /// بازیابی داده‌های زنده را بازنویسی می‌کند و فقط از آنِ SuperAdmin است.
    /// </summary>
    public static bool CanRestoreBackups(ClaimsPrincipal user)
        => IsSuperAdmin(user);

    /// <summary>
    /// PTG-P1-01 — بستن و بازکردنِ دورهٔ عملیاتی: SuperAdmin یا Permission صریح.
    /// همان الگوی <see cref="CanManageBackups"/>؛ چارچوب دسترسیِ تازه‌ای ساخته نشد.
    /// </summary>
    public static bool CanManageOperationalPeriodLock(ClaimsPrincipal user)
        => IsSuperAdmin(user)
            || user.HasClaim(AppClaimTypes.Permission, AppPermissions.ManageOperationalPeriodLock);

    public static IReadOnlySet<string> AllowedNavigationForUser(ClaimsPrincipal user)
    {
        if (user.IsInRole(AuthRoles.Admin))
        {
            return AllNavigationKeys;
        }

        var claimKeys = user.FindAll(AppClaimTypes.AllowedNavigation)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (claimKeys.Length > 0)
        {
            return NormalizeNavigation(claimKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return DefaultNavigationForRole(user.FindFirstValue(ClaimTypes.Role));
    }

    public static bool CanAccessNavigation(ClaimsPrincipal user, string? navigationKey)
    {
        if (string.IsNullOrWhiteSpace(navigationKey))
        {
            return true;
        }

        return AllowedNavigationForUser(user).Contains(navigationKey);
    }

    public static bool CanAccessController(ClaimsPrincipal user, string? controller)
    {
        var navigationKey = NavigationKeyForController(controller);
        return navigationKey is not null && CanAccessNavigation(user, navigationKey);
    }

    /// <summary>Client Profile + Role/Permission: ماژول مخفیِ Profile برای هیچ نقشی باز نیست.</summary>
    public static bool CanAccessController(ClaimsPrincipal user, string? controller, ClientModuleProfile? clientModules)
        => (clientModules?.IsControllerEnabled(controller) ?? true) && CanAccessController(user, controller);

    public static bool CanAccessNavigation(ClaimsPrincipal user, string? navigationKey, ClientModuleProfile? clientModules)
        => (clientModules?.IsNavigationEnabled(navigationKey) ?? true) && CanAccessNavigation(user, navigationKey);
}
