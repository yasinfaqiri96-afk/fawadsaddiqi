using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Diagnostics;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Infrastructure.ModelBinding;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Middleware;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.AutoCode;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.QuickCreate;
using System.IO.Compression;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// ---- Client Profile ---------------------------------------------------------
// فقط Instanceی که ClientProfile__Name دارد فایل appsettings.{Name}.json را می‌خواند؛
// بدون آن هیچ تنظیمی اضافه نمی‌شود و رفتار سیستم کامل دست‌نخورده می‌ماند.
// Environment Variables و آرگومان‌ها دوباره افزوده می‌شوند تا بر فایل Profile اولویت داشته باشند.
var clientProfileName = builder.Configuration[$"{PTGOilSystem.Web.Configuration.ClientProfileOptions.SectionName}:Name"];
if (!string.IsNullOrWhiteSpace(clientProfileName))
{
    if (!ClientModuleProfile.IsValidProfileName(clientProfileName))
        throw new InvalidOperationException($"Invalid ClientProfile:Name '{clientProfileName}'.");
    builder.Configuration.AddJsonFile($"appsettings.{clientProfileName.Trim()}.json", optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

// Explicit per-instance CORS allowlist; an unconfigured instance keeps its existing behavior.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy("InstanceOrigins", policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
}

// ---- Database ---------------------------------------------------------------
// Connection string is read from DATABASE_URL first, then DefaultConnection.
// DATABASE_URL is converted from the URL form
// (postgres://user:pass@host:port/db) to the Npgsql key-value form.
var rawConnectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");
var hasDatabaseConnection = !string.IsNullOrWhiteSpace(rawConnectionString);

if (!hasDatabaseConnection)
{
    throw new InvalidOperationException(
        "PTG Oil System database connection is not configured. "
        + "Set DATABASE_URL or ConnectionStrings__DefaultConnection before starting the app. "
        + "The application no longer falls back to an InMemory database because that can make real PostgreSQL data look empty.");
}

builder.Services.AddScoped<MvcRequestTimingState>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddScoped<MvcQueryCountingInterceptor>();
    builder.Services.AddScoped<MvcRequestTimingFilter>();
}

builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(BuildPostgresConnectionString(rawConnectionString!), npgsql =>
    {
        npgsql.MigrationsAssembly(ApplicationDbContext.MigrationsAssemblyName);
        // عمداً کوتاه: هدف این است که کوئری کند سریع شکست بخورد و دیده شود،
        // نه اینکه با Timeout بلند پنهان بماند و اتصال را اشغال نگه دارد.
        npgsql.CommandTimeout(30);
    });
    if (builder.Environment.IsDevelopment())
    {
        options.AddInterceptors(serviceProvider.GetRequiredService<MvcQueryCountingInterceptor>());
    }

    // مشعل آفلاین: فقط در Scopeِ همگام‌سازی فعال می‌شود (توکن یک‌بارِ ویرایش/لغو/حذفِ آفلاین)؛ درخواست عادی بی‌اثر.
    options.AddInterceptors(serviceProvider.GetRequiredService<PTGOilSystem.Web.Services.OfflineSync.OfflineApplyScope>());
});
builder.Services.AddScoped<PTGOilSystem.Web.Services.OfflineSync.OfflineApplyScope>();

// ---- Domain services (business rules, system rules #3-#9, #11, #13) --------
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.Configure<PTGOilSystem.Web.Configuration.ClientProfileOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.ClientProfileOptions.SectionName));
builder.Services.AddSingleton<ClientModuleProfile>();
// راهنمای شروع کار: پیش‌فرض خاموش؛ فقط نسخه‌های آزمایشی با Onboarding:Enabled روشنش می‌کنند.
builder.Services.Configure<PTGOilSystem.Web.Configuration.OnboardingOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.OnboardingOptions.SectionName));
builder.Services.AddScoped<IOnboardingService, OnboardingService>();
builder.Services.AddScoped<IStockService, StockService>();
// امانی — دفترِ جنسِ متعلق به دیگران؛ هرگز InventoryMovement یا سند مالی نمی‌سازد.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Custody.ICustodyService, PTGOilSystem.Web.Services.Custody.CustodyService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Custody.ICustodyReadService, PTGOilSystem.Web.Services.Custody.CustodyReadService>();
// تنها نقطهٔ اجرای قواعد مشترک ثبت حرکت موجودی (مقدار، قفل، نگهبان موجودی، سند).
// تراکنش را caller مالک است؛ Writer تراکنش مستقل باز نمی‌کند.
builder.Services.AddScoped<IInventoryMovementWriter, InventoryMovementWriter>();
builder.Services.AddScoped<ManualInventoryService>();
// PTG-P1-03 — تنها مسیرِ ساختنِ LedgerEntry.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Ledger.ILedgerPostingService, PTGOilSystem.Web.Services.Ledger.LedgerPostingService>();
// تک‌منبع «باقیماندهٔ حمل» و تفکیک سرنوشت مقدار (نگهداشت مقدار).
builder.Services.AddScoped<ITransportQuantityService, TransportQuantityService>();
// موتور عمومی وسیله → وسیله (هر نُه ترکیب موتر/واگن/کشتی، بدون حرکت موجودی).
builder.Services.AddScoped<ITransportChainService, TransportChainService>();
builder.Services.AddScoped<ITransportWorkflowService, TransportWorkflowService>();
// نسب‌نامهٔ چندقراردادی نتیجه‌های حمل (فروش/کسری) بدون شکستن سند واحد و دفترکل قدیمی.
builder.Services.AddScoped<ITransportSourceAllocationService, TransportSourceAllocationService>();
// منبع واحد حقیقتِ «فروش → قرارداد خرید»: فقط SalesTransactionSourceAllocations.
builder.Services.AddScoped<ISaleContractAttributionReader, SaleContractAttributionReader>();
// صفحهٔ فقط‌خواندنیِ «مشاهده فعالیت‌های دوره». هیچ منطق مالی/نوشتنی ندارد.
builder.Services.AddScoped<IPeriodActivityService, PeriodActivityService>();
// ---- Independent accounting core (Stage 2; feature flag defaults to off). ----
builder.Services.Configure<PTGOilSystem.Web.Configuration.AccountingOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.AccountingOptions.SectionName));
builder.Services.AddScoped<IFiscalCalendarService, FiscalCalendarService>();
builder.Services.AddScoped<IPeriodGuard, PeriodGuard>();
builder.Services.AddScoped<ISystemCompanyProvider, SystemCompanyProvider>();
builder.Services.AddScoped<IAccountingPostingService, AccountingPostingService>();
builder.Services.AddScoped<IAccountingChartSeeder, AccountingChartSeeder>();
builder.Services.AddScoped<IAccountingBackfillService, AccountingBackfillService>();
builder.Services.AddScoped<IPartnershipProfitAllocationAdapter, PartnershipProfitAllocationAdapter>();
builder.Services.AddScoped<IPartnerCurrentReconciliationService, PartnerCurrentReconciliationService>();
builder.Services.AddScoped<IChartOfAccountsReadService, ChartOfAccountsReadService>();
builder.Services.AddScoped<IAccountingMappingService, AccountingMappingService>();
builder.Services.AddScoped<IAccountingJournalNumberGenerator, AccountingJournalNumberGenerator>();
builder.Services.AddScoped<IContractBalanceTransferAccountingAdapter, ContractBalanceTransferAccountingAdapter>();
builder.Services.AddScoped<ICustodyAccountingAdapter, CustodyAccountingAdapter>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Custody.ICustodySaleService, PTGOilSystem.Web.Services.Custody.CustodySaleService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Custody.CustodyAccountingReconciliationService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Custody.CustodyConversionService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Custody.CustodyGlReadService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.CommodityLoans.ICommodityLoanService, PTGOilSystem.Web.Services.CommodityLoans.CommodityLoanService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.CommodityLoans.CommodityLoanReadService>();
builder.Services.AddScoped<ISupplierPaymentAllocationAccountingAdapter, SupplierPaymentAllocationAccountingAdapter>();
builder.Services.AddScoped<ICompanyOwnershipReportService, CompanyOwnershipReportService>();
// مرحله ۹ — گزارش فقط‌خواندنیِ آمادگی Cutover. هیچ Flag را روشن و هیچ Migration را اجرا نمی‌کند.
builder.Services.AddScoped<IAccountingReadinessService, AccountingReadinessService>();
// مرحله ۱۰ — صفحه‌های سال مالی. Overview فقط می‌خواند؛ Provisioning تنها مسیر نوشتنِ آن است.
builder.Services.AddScoped<IFiscalYearOverviewService, FiscalYearOverviewService>();
// انتخاب‌گرِ سال مالیِ فعالِ هدر — کانتکستِ کاری/گزارشی در کوکیِ امضاشده. هیچ منطقِ posting را تغییر نمی‌دهد.
builder.Services.AddScoped<IFiscalYearContext, FiscalYearContextService>();
builder.Services.AddScoped<IFiscalYearProvisioningService, FiscalYearProvisioningService>();
// مرحله ۱۲ — چک‌لیستِ بستنِ سال. کاملاً فقط‌خواندنی؛ هیچ Close/Lock/Migration/Posting ندارد.
builder.Services.AddScoped<IClosingChecklistService, ClosingChecklistService>();
// مرحله ۱۳ — Trial Close و تسعیرِ پایان دوره. سال را نمی‌بندد و دوره را HardLock نمی‌کند.
builder.Services.AddScoped<ITrialCloseService, TrialCloseService>();
// مرحله ۱۴ — Final Close اتمیک. بستنِ P&L به Equity، HardLock دوره‌ها و بستنِ سال در یک Transaction.
builder.Services.AddScoped<IFinalCloseService, FinalCloseService>();
// مرحله ۱۵ — بازگشاییِ کنترل‌شده. آثارِ Final Close فقط با Reversal رسمی برمی‌گردند؛ هیچ سندی حذف نمی‌شود.
builder.Services.AddScoped<IReopenFiscalYearService, ReopenFiscalYearService>();
// مرحله ۱۱ — تنها مسیر نوشتنِ وضعیت قفلِ دوره. قفل سخت از اینجا هم برگشت‌ناپذیر است.
builder.Services.AddScoped<IFiscalPeriodLockService, FiscalPeriodLockService>();
builder.Services.AddScoped<IPaymentCompanyResolver, PaymentCompanyResolver>();
builder.Services.AddScoped<IExpenseAccountingAdapter, ExpenseAccountingAdapter>();
builder.Services.AddScoped<IPaymentAccountingAdapter, PaymentAccountingAdapter>();
builder.Services.AddScoped<IViaSarrafAccountingAdapter, ViaSarrafAccountingAdapter>();
builder.Services.AddScoped<IInventoryValuationService, InventoryValuationService>();
builder.Services.AddScoped<IPurchaseAccountingAdapter, PurchaseAccountingAdapter>();
builder.Services.AddScoped<ISalesAccountingAdapter, SalesAccountingAdapter>();
builder.Services.AddScoped<IAssetRentAccountingAdapter, AssetRentAccountingAdapter>();
builder.Services.AddScoped<IEmployeeSalaryAccountingAdapter, EmployeeSalaryAccountingAdapter>();
builder.Services.AddScoped<IAssetRentPostingService, AssetRentPostingService>();
builder.Services.AddScoped<IInventoryLossAccountingAdapter, InventoryLossAccountingAdapter>();
builder.Services.AddScoped<IShortageChargeAccountingAdapter, ShortageChargeAccountingAdapter>();
builder.Services.AddScoped<ISarrafSettlementAccountingAdapter, SarrafSettlementAccountingAdapter>();
builder.Services.AddScoped<IThreeWaySettlementAccountingAdapter, ThreeWaySettlementAccountingAdapter>();
builder.Services.AddScoped<IInventoryTransferAccountingAdapter, InventoryTransferAccountingAdapter>();
// ---- Inventory Lineage (Phase 2). Feature flags + parallel reference-layer services. ----
builder.Services.Configure<PTGOilSystem.Web.Configuration.LineageOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.LineageOptions.SectionName));
builder.Services.AddScoped<IInventoryLineageWriter, InventoryLineageWriter>();
builder.Services.AddScoped<InventoryLineageBackfillService>();
builder.Services.AddScoped<InventoryLineagePnlService>();
builder.Services.AddScoped<InventoryTransportLegLoadService>();
builder.Services.AddScoped<InventoryTransportBatchService>();
// از کانتینر گرفته می‌شود تا آداپترهای حسابداری/نسب‌نامه در همهٔ مسیرهای رسید حمل
// (تخلیه، فروش مستقیم، انتقال به موتر، تسویه گروهی) یکسان وصل باشند. ساختِ دستی این سرویس
// آداپترها را null می‌گذاشت و مسیرها رفتار حسابداری متفاوت پیدا می‌کردند.
builder.Services.AddScoped<InventoryTransportReceiptService>();
builder.Services.AddScoped<IPurchaseAggregationService, PurchaseAggregationService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reporting.IProfitAndLossService,
    PTGOilSystem.Web.Services.Reporting.ProfitAndLossService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reconciliation.IReconciliationService,
    PTGOilSystem.Web.Services.Reconciliation.ReconciliationService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reporting.IPreSaleReservationService,
    PTGOilSystem.Web.Services.Reporting.PreSaleReservationService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reporting.INegativeStockAnalysisService,
    PTGOilSystem.Web.Services.Reporting.NegativeStockAnalysisService>();
builder.Services.AddScoped<ILossEventWorkflowService, LossEventWorkflowService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.LoadingReceipts.ILoadingReceiptCancellationService,
    PTGOilSystem.Web.Services.LoadingReceipts.LoadingReceiptCancellationService>();
builder.Services.AddScoped<ISimplePurchaseWorkflowService, SimplePurchaseWorkflowService>();
builder.Services.AddScoped<OperationLifecycleService>();
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<ICurrencyConversionService, CurrencyConversionService>();
builder.Services.AddScoped<IUnitConversionService, UnitConversionService>();
builder.Services.AddScoped<ISarrafSettlementService, SarrafSettlementService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IExpenseRuleEngine, ExpenseRuleEngine>();
builder.Services.AddScoped<IContractAmendmentService, ContractAmendmentService>();
builder.Services.AddScoped<IContractBalanceTransferService, ContractBalanceTransferService>();
builder.Services.AddScoped<ISupplierPaymentAllocationService, SupplierPaymentAllocationService>();
// موتور واحد «مانده قابل انتقال تأمین‌کننده» — منبع اصلی انتقال طلب به قرارداد.
builder.Services.AddScoped<ISupplierTransferableBalanceService, SupplierTransferableBalanceService>();
builder.Services.AddScoped<ISupplierFxSettlementService, SupplierFxSettlementService>();
builder.Services.AddScoped<ISupplierFxRecognitionService, SupplierFxRecognitionService>();
builder.Services.AddScoped<ISupplierBalanceTransferService, SupplierBalanceTransferService>();
builder.Services.AddScoped<ICustomerPaymentAllocationService, CustomerPaymentAllocationService>();
builder.Services.AddScoped<ICustomerReceiptApplicationService, CustomerReceiptApplicationService>();
builder.Services.AddScoped<IViaSarrafContractAssignmentService, ViaSarrafContractAssignmentService>();
builder.Services.AddScoped<IViaSarrafLegacyGroupingService, ViaSarrafLegacyGroupingService>();
builder.Services.AddScoped<IPaymentCorrectionService, PaymentCorrectionService>();
builder.Services.AddScoped<IEmployeeSalaryService, EmployeeSalaryService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IHrFileStorage, PTGOilSystem.Web.Services.HumanResources.HrFileStorage>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IEmployeeCompensationService, PTGOilSystem.Web.Services.HumanResources.EmployeeCompensationService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IEmploymentContractService, PTGOilSystem.Web.Services.HumanResources.EmploymentContractService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IHrCalendarService, PTGOilSystem.Web.Services.HumanResources.HrCalendarService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IAttendanceService, PTGOilSystem.Web.Services.HumanResources.AttendanceService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.ILeaveService, PTGOilSystem.Web.Services.HumanResources.LeaveService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IPayrollService, PTGOilSystem.Web.Services.HumanResources.PayrollService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IEmployeeLoanService, PTGOilSystem.Web.Services.HumanResources.EmployeeLoanService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.HumanResources.IEmployeeRecordsService, PTGOilSystem.Web.Services.HumanResources.EmployeeRecordsService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.Configure<PTGOilSystem.Web.Models.PartyStatements.PartyStatementOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Models.PartyStatements.PartyStatementOptions.SectionName));
// لایهٔ مرکزی «رسید، برد و بیلانس» — تنها مرجع تعیین جهت تجاری و محاسبهٔ بیلانس.
builder.Services.AddSingleton<PTGOilSystem.Web.Services.CompanyFlow.ICompanyFlowDirectionResolver,
    PTGOilSystem.Web.Services.CompanyFlow.CompanyFlowDirectionResolver>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.CompanyFlow.ICompanyFlowBalanceService,
    PTGOilSystem.Web.Services.CompanyFlow.CompanyFlowBalanceService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Expenses.IExpenseLedgerPoster,
    PTGOilSystem.Web.Services.Expenses.ExpenseLedgerPoster>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Expenses.IExpenseSettlementValidator,
    PTGOilSystem.Web.Services.Expenses.ExpenseSettlementValidator>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Parties.IPartyDirectory,
    PTGOilSystem.Web.Services.Parties.PartyDirectory>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.PartyStatements.IPartyStatementPolicyResolver,
    PTGOilSystem.Web.Services.PartyStatements.PartyStatementPolicyResolver>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.IPartyStatementReadService,
    PTGOilSystem.Web.Services.PartyStatements.PartyStatementReadService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.PartyStatementPageBuilder>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.IPartnershipStatementService,
    PTGOilSystem.Web.Services.PartyStatements.PartnershipStatementService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.IPartyBalanceReadService,
    PTGOilSystem.Web.Services.PartyStatements.PartyBalanceReadService>();
builder.Services.AddScoped<ILoginAttemptGuard, AuditLoginAttemptGuard>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Time.IAfghanistanBusinessClock,
    PTGOilSystem.Web.Services.Time.AfghanistanBusinessClock>();
// تقویمِ نمایش/ورود (میلادی یا هجری شمسی افغانستان). فقط لایهٔ UI؛ ذخیره‌سازی میلادیِ کانونیک می‌ماند.
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Calendars.IAppCalendarService,
    PTGOilSystem.Web.Services.Calendars.AppCalendarService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Calendars.ICalendarSettingsService,
    PTGOilSystem.Web.Services.Calendars.CalendarSettingsService>();
builder.Services.AddHostedService<PTGOilSystem.Web.Services.Calendars.CalendarSettingsInitializer>();
builder.Services.AddScoped<IAutoCodeService, AutoCodeService>();
builder.Services.AddScoped<AutoGeneratedCodeFilter>();
builder.Services.AddScoped<QuickCreateResultFilter>();
builder.Services.AddScoped<MasterDataDeleteSafetyService>();
builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();
builder.Services.AddScoped<IFormTokenGuard, FormTokenGuard>();
// PTG-P1-01 — قفل دورهٔ عملیاتی. مستقل از ماژول حسابداری (که خاموش است) و تنها مرجعِ
// «آیا در این تاریخ می‌شود سند زد؟» برای دفترِ عملیاتی.
builder.Services.AddScoped<PTGOilSystem.Web.Services.OperationalPeriod.IOperationalPeriodGuard,
    PTGOilSystem.Web.Services.OperationalPeriod.OperationalPeriodGuard>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.OperationalPeriod.BusinessRuleExceptionFilter>();
// PTG فاز ۹ — درخواستِ صریح و یک‌بارمصرفِ «ثبت استثنایی در دورهٔ بسته».
builder.Services.AddScoped<PTGOilSystem.Web.Services.OperationalPeriod.ClosedPeriodOverrideFilter>();
// PTG-P1-02 / ۱۲-F — تطبیق فقط‌خواندنیِ سلامتِ دفتر کل و دادهٔ تاریخی.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Reconciliation.ILedgerIntegrityReconciliationService,
    PTGOilSystem.Web.Services.Reconciliation.LedgerIntegrityReconciliationService>();
builder.Services.AddScoped<RoleNavigationAuthorizationFilter>();
builder.Services.AddScoped<AuthBootstrapper>();
builder.Services.AddHttpContextAccessor();

// ---- راهنمای هوشمند --------------------------------------------------------
// کلید API فقط از متغیر محیطی (GROQ_API_KEY / ANTHROPIC_API_KEY) خوانده می‌شود و
// هرگز به Frontend نمی‌رود. دستیار می‌تواند داده را فقط بخواند: هیچ ابزار نوشتنی
// ثبت نشده و هر ابزار پیش از اجرا با دسترسی ناوبری همان کاربر سنجیده می‌شود.
builder.Services.Configure<PTGOilSystem.Web.Configuration.AssistantOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.AssistantOptions.SectionName));
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.AssistantPageCatalog>();
// هر سه Provider ثبت می‌شوند؛ انتخاب واقعی با Assistant:Provider و
// Assistant:FallbackProvider انجام می‌شود.
builder.Services.AddHttpClient(PTGOilSystem.Web.Services.Assistant.GroqAssistantProvider.HttpClientName);
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.IAssistantProvider,
    PTGOilSystem.Web.Services.Assistant.GeminiAssistantProvider>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.IAssistantProvider,
    PTGOilSystem.Web.Services.Assistant.AnthropicAssistantProvider>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.IAssistantProvider,
    PTGOilSystem.Web.Services.Assistant.GroqAssistantProvider>();

// ابزارهای فقط‌خواندنی. Scoped چون به DbContext و سرویس‌های خواندنیِ Scoped تکیه دارند.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.SearchPartyTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.PartyBalanceTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.StockBalanceTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.ContractLookupTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.LoadingDetailsTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.ContractProgressTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.OpenContractsTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.PartyLedgerTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantToolRegistry,
    PTGOilSystem.Web.Services.Assistant.Tools.AssistantToolRegistry>();

// Scoped است چون ابزارهایش به DbContext وابسته‌اند؛ Singleton بودن آن باعث
// نگه‌داشتن یک DbContext در طول عمر برنامه می‌شد.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.IAssistantService,
    PTGOilSystem.Web.Services.Assistant.AssistantService>();

// ---- Backups (پشتیبان‌گیری کامل؛ خارج از منطق مالی/عملیاتی) -----------------
// اجرای واقعی فقط در BackupSchedulerHostedService رخ می‌دهد و BackupExecutionLock
// تک‌نمونه‌ای است تا دو بکاپ هرگز هم‌زمان نشوند.
builder.Services.Configure<PTGOilSystem.Web.Configuration.BackupOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.BackupOptions.SectionName));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Backups.IBackupExecutionLock,
    PTGOilSystem.Web.Services.Backups.BackupExecutionLock>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Backups.IBackupTriggerQueue,
    PTGOilSystem.Web.Services.Backups.BackupTriggerQueue>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IPostgresBackupTool,
    PTGOilSystem.Web.Services.Backups.PostgresBackupTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IGoogleDriveBackupClient,
    PTGOilSystem.Web.Services.Backups.GoogleDriveBackupClient>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IBackupService,
    PTGOilSystem.Web.Services.Backups.BackupService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IBackupRestoreService,
    PTGOilSystem.Web.Services.Backups.BackupRestoreService>();
builder.Services.AddHostedService<PTGOilSystem.Web.Services.Backups.BackupSchedulerHostedService>();
// ۱۲-A — پاک‌سازیِ کران‌دارِ توکن‌های Idempotency. توکنِ داخلِ پنجرهٔ تلاش دوباره حذف نمی‌شود.
builder.Services.AddHostedService<PTGOilSystem.Web.Services.ProcessedFormTokenRetentionHostedService>();

var configuredDataProtectionKeysPath = builder.Configuration["PTG_DATA_PROTECTION_KEYS_PATH"];
var shouldUseLocalDataProtectionKeys =
    !string.IsNullOrWhiteSpace(configuredDataProtectionKeysPath)
    || builder.Environment.IsDevelopment()
    || OperatingSystem.IsWindows();

if (shouldUseLocalDataProtectionKeys)
{
    var dataProtectionKeysPath = string.IsNullOrWhiteSpace(configuredDataProtectionKeysPath)
        ? Path.Combine(builder.Environment.ContentRootPath, ".aspnet-data-protection-keys")
        : configuredDataProtectionKeysPath;

    Directory.CreateDirectory(dataProtectionKeysPath);

    builder.Services
        .AddDataProtection()
        .SetApplicationName("PTGOilSystem.Web")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "text/css", "application/javascript", "text/javascript",
        "application/json", "text/html", "image/svg+xml",
        "font/woff2", "font/woff", "font/ttf", "font/otf",
        "application/font-woff2", "application/font-woff"
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Imports.ExcelImportJobCoordinator>();
builder.Services.Configure<PTGOilSystem.Web.Services.Exports.TabularExportOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Services.Exports.TabularExportOptions.SectionName));
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Exports.ITabularExportService, PTGOilSystem.Web.Services.Exports.TabularExportService>();

// ---- Rate limiting (مسیرهای سنگین) -----------------------------------------
// هدف: جلوگیری از تمام‌شدن Connection Pool وقتی کاربر روی «خروجی CSV» یا گزارش
// سنگین پی‌درپی کلیک می‌کند. تفکیک بر اساس کاربر واردشده، و در نبود آن بر اساس IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Login, httpContext =>
        RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: $"login-ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 1,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    // سهمیهٔ خروجی به تفکیک «مسیر» است، همان چیزی که پیام رد شدن هم می‌گوید. قبلاً همهٔ
    // ۴۵ اکشن خروجی برنامه (CSV/Excel/PDF) یک سطل ۵تایی مشترک برای هر کاربر داشتند؛
    // بنابراین چند خروجی معمولی در یک دقیقه سهمیه را تمام می‌کرد و کلیک بعدی — مثلاً
    // PDF صورت‌حساب تأمین‌کننده — با 429 رد می‌شد بی‌آنکه خودش پرمصرف بوده باشد.
    // کلید از الگوی مسیر ساخته می‌شود نه از URL کامل، تا id‌های مختلف سطل جدید نسازند.
    // سقف ۵ برای کار عادی کم بود: Excel و PDF یک مسیر مشترک دارند (format فقط query است)
    // و چند خروجی پشت‌سرهم از یک صفحه، کاربر را با 429 روبه‌رو می‌کرد. سقف ۶۰ در دقیقه
    // همچنان جلوی سیل درخواست و تمام‌شدن Connection Pool را می‌گیرد.
    options.AddPolicy(RateLimitPolicies.CsvExport, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{ResolveRateLimitPartitionKey(httpContext)}|{ResolveRateLimitRouteKey(httpContext)}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy(RateLimitPolicies.HeavyReport, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveRateLimitPartitionKey(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        var isLoginRequest = context.HttpContext.Request.Path.StartsWithSegments("/Auth/Login")
            || context.HttpContext.Request.Path.StartsWithSegments("/api/mobile/v1/auth/login");
        if (isLoginRequest)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store, no-cache";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            context.HttpContext.Response.Headers.XFrameOptions = "DENY";

            try
            {
                await using var scope = context.HttpContext.RequestServices.CreateAsyncScope();
                var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
                await audit.LogActivityAndSaveAsync(new AuditLogEntryInput
                {
                    Category = PTGOilSystem.Web.Models.Entities.AuditLogCategories.Authentication,
                    EntityName = "User",
                    Action = LoginAuditActions.RateLimited,
                    Module = "Auth",
                    Description = "درخواست ورود به دلیل محدودیت نرخ رد شد.",
                    HttpMethod = context.HttpContext.Request.Method,
                    RequestPath = context.HttpContext.Request.Path.Value,
                    ControllerName = "Auth",
                    ActionName = "Login",
                    StatusCode = StatusCodes.Status429TooManyRequests,
                    IsSuccess = false,
                    CorrelationId = context.HttpContext.TraceIdentifier,
                    IpAddress = context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = context.HttpContext.Request.Headers.UserAgent.ToString()
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("LoginRateLimiting");
                logger.LogWarning(ex, "Unable to persist login rate-limit audit event.");
            }
        }

        // مهلت واقعی را اعلام کن تا کاربر/مرورگر نداند «کمی» یعنی چقدر.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // API موبایل پاسخ ProblemDetails می‌گیرد؛ متن ساده فقط برای صفحات وب می‌ماند.
        if (ApiRequest.IsApi(context.HttpContext))
        {
            await ApiProblem.WriteAsync(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                ApiErrorCodes.RateLimited,
                ApiProblem.RateLimitedMessage);
            return;
        }

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            isLoginRequest
                ? "ورود موقتاً محدود شده است. لطفاً کمی بعد دوباره تلاش کنید."
                : "تعداد درخواست‌های شما برای این مسیر بیش از حد مجاز است. لطفاً کمی صبر کنید و دوباره تلاش کنید.",
            cancellationToken);
    };
});

// ---- Authentication / Authorization -----------------------------------------
// Behind Replit's reverse proxy the app receives plain HTTP; trust the
// X-Forwarded-Proto header so the framework knows the original request was
// HTTPS. This makes the Secure cookie policy below behave correctly.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Proxy IP is not fixed on Replit autoscale; clear the known-proxy allow-list.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// کوکی وب (همان تنظیمات قبلی، طرح پیش‌فرض) + JWT Bearer فقط برای /api/mobile.
// تنظیمات کامل و سیاست‌ها: Security/AuthenticationSetup.cs
builder.Services.AddPtgAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddPtgMobileApi();
// مشعل آفلاین (PWA): دریافت عملیات آفلاین و ثبت قطعی خودکار با همان اکشن‌های موجود (بدون منطق مالی موازی).
builder.Services.AddScoped<PTGOilSystem.Web.Services.OfflineSync.IOfflineSyncService, PTGOilSystem.Web.Services.OfflineSync.OfflineSyncService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.OfflineSync.IOfflineSnapshotService, PTGOilSystem.Web.Services.OfflineSync.OfflineSnapshotService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.OfflineSync.OfflineOperationApplier>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.OfflineSync.IMvcFormDispatcher, PTGOilSystem.Web.Services.OfflineSync.MvcFormDispatcher>();
// پیش‌نویس خودکار فرم‌های ثبت: فقط فیلدهای فرم را نگه می‌دارد؛ ثبت نهایی همان اکشن موجود است.
builder.Services.AddScoped<PTGOilSystem.Web.Services.FormDrafts.IFormDraftService, PTGOilSystem.Web.Services.FormDrafts.FormDraftService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.FormDrafts.FormDraftSubmitFilter>();

// ---- MVC --------------------------------------------------------------------
builder.Services.AddControllersWithViews(options =>
{
    // فرم انتقال گروهی می‌تواند هزاران ردیف داشته باشد؛ محدودیت قدیمی ۱۰۰ ردیف حذف شده است.
    options.MaxModelBindingCollectionSize = 20_000;
    // تاریخ‌های query/form با Kind=Unspecified بایند می‌شوند و Npgsql آن‌ها را برای
    // ستون timestamptz رد می‌کند؛ این provider همه را به UTC نرمال می‌کند.
    options.ModelBinderProviders.Insert(0, new UtcDateTimeModelBinderProvider());
    // فرم‌های ثبت جدید دیگر صفرِ پیش‌فرض را در input نمی‌گذارند؛ پس فیلد عددیِ اختیاری می‌تواند
    // خالی post شود. این provider همان خالی را به صفر bind می‌کند و [Required] را دست نمی‌زند.
    options.ModelBinderProviders.Insert(1, new OptionalNumericModelBinderProvider());
    // ?page=0 قرارداد داخلیِ Export است، نه چیزی که کاربر از نوار آدرس بدهد؛ این فیلتر
    // فقط مقدار آمده از خودِ درخواست را به بازهٔ مجاز برمی‌گرداند.
    options.Filters.Add<PTGOilSystem.Web.Filters.ListPageBoundsFilter>();
    options.Filters.AddService<AutoGeneratedCodeFilter>();
    options.Filters.AddService<RoleNavigationAuthorizationFilter>();
    options.Filters.Add<OperationLifecycleReadOnlyFilter>();
    // پس از فیلترهای دسترسی: ثبت نهاییِ پیش‌نویس‌شده را یک‌بار و بدون تکرار علامت می‌زند.
    options.Filters.AddService<PTGOilSystem.Web.Services.FormDrafts.FormDraftSubmitFilter>();
    options.Filters.AddService<QuickCreateResultFilter>();
    // PTG-P1-01 / P1-05 / P3-C — قفل دوره، برخورد هم‌زمانی و خطای محدودیت دیتابیس باید
    // پیام فارسی بدهند، نه صفحهٔ «خطای سرور» با متن فنی.
    // PTG فاز ۹ — پیش از اجرای Action خوانده می‌شود تا اگر کاربرِ مجاز دلیل نوشته باشد،
    // عبورِ همین یک درخواست باز شود. بدون دسترسی یا بدون دلیل، هیچ اثری ندارد.
    options.Filters.AddService<PTGOilSystem.Web.Services.OperationalPeriod.ClosedPeriodOverrideFilter>();
    options.Filters.AddService<PTGOilSystem.Web.Services.OperationalPeriod.BusinessRuleExceptionFilter>();
    if (builder.Environment.IsDevelopment())
    {
        options.Filters.AddService<MvcRequestTimingFilter>();
    }
});

// نکته: Razor Runtime Compilation عمداً فعال نشد. کامپایلر runtime نسخهٔ زبان C# 12 را
// رعایت نمی‌کند و collection expression مثل `[]` را رد می‌کند (Invalid expression term '[').
// برای دیدن زندهٔ تغییرات .cshtml بدون restart از dotnet watch استفاده کنید (run-dev.bat).

var app = builder.Build();

// ---- بررسی پیکربندی دستیار ---------------------------------------------------
// فقط گزارش می‌کند و هرگز برنامه را متوقف نمی‌کند: نبودن کلید دستیار نباید جلوی
// بالا آمدن سامانه را بگیرد. هیچ مقدار کلیدی Log نمی‌شود، فقط نام متغیر محیطی.
PTGOilSystem.Web.Services.Assistant.AssistantStartupValidator.Validate(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Assistant"));

// ---- Auto-migrate database --------------------------------------------------
// Migrations run on startup by default to preserve existing server behaviour.
// Set PTG_AUTO_MIGRATE=false (or ConnectionStrings/Database:AutoMigrate=false)
// to disable — the desktop wrapper sets this so it never migrates implicitly.
var autoMigrate = builder.Configuration.GetValue("PTG_AUTO_MIGRATE", true)
    && builder.Configuration.GetValue("Database:AutoMigrate", true);
if (autoMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    DatabaseSafetyGuard.EnsureMigrationAllowed(db.Database.GetDbConnection().Database);
    db.Database.Migrate();
}

// ---- Accounting backfill (CLI) ----------------------------------------------
// `dotnet run -- --accounting-backfill [--dry-run]` replays the purchase, inventory-receipt,
// sale and COGS postings for data entered while the accounting module was switched off, then
// exits without serving. It creates no operational data; every artifact comes from the same
// production adapter the create-time path uses, so a second run posts nothing new.
if (args.Contains("--accounting-backfill", StringComparer.OrdinalIgnoreCase))
{
    var exitCode = await RunAccountingBackfillAsync(app, args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
    return exitCode;
}

// ---- Loading price repair (CLI) ---------------------------------------------
// `dotnet run -- --repair-loading-prices [--dry-run]` تعمیرِ یک‌بارهٔ دادهٔ قدیمی است: بارگیری‌های
// خریدِ بدون قیمت معتبر که قراردادشان نرخ نهاییِ قطعی دارد، با همان نرخ تکمیل می‌شوند. هیچ منطق
// قیمت‌گذاری تازه‌ای اینجا نیست؛ کار را همان مسیر موجودِ «اصلاح قیمت» قرارداد
// (ContractsController.RepricePurchaseLoadings → SyncPurchaseLoadingPricesAsync) انجام می‌دهد، پس
// قفل روبل، دفتر تأمین‌کننده و اسناد حسابداری هم مثل فشردن همان دکمه هماهنگ می‌شوند. بارگیریِ
// قیمت‌دار و قراردادِ بدون نرخ قطعی دست‌نخورده می‌مانند.
if (args.Contains("--repair-loading-prices", StringComparer.OrdinalIgnoreCase))
{
    var exitCode = await RunLoadingPriceRepairAsync(app, args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
    return exitCode;
}

// ---- Loading freight party repair (CLI) -------------------------------------
// `dotnet run -- --repair-loading-freight --contract-id=N --provider-name="..." [--dry-run]` تعمیرِ
// یک‌بارهٔ دادهٔ قدیمی است: بارگیری‌های همان یک قرارداد که کرایهٔ «بدوش خریدار» بدون طرف‌حساب
// داشتند، به شرکت خدماتی (یافتن با نام دقیق، یا ساخت با نوع شرکت ترانسپورتی) وصل می‌شوند و سند
// مصرف کرایه با همان مسیر ویرایش بارگیری (LoadingController) ساخته می‌شود؛ پس بدهی طرف‌حساب و سند
// حسابداری مثل ذخیرهٔ فرم ثبت می‌شوند. بارگیریِ کامل دست نمی‌خورد و اجرای دوباره بی‌اثر است.
if (args.Contains("--repair-loading-freight", StringComparer.OrdinalIgnoreCase))
{
    var exitCode = await RunLoadingFreightPartyRepairAsync(
        app,
        int.TryParse(CliArgValue(args, "--contract-id"), out var repairContractId) ? repairContractId : 0,
        CliArgValue(args, "--provider-name"),
        args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
    return exitCode;
}

// ---- Customs cash currency repair (CLI) -------------------------------------
// `dotnet run -- --repair-customs-cash-currency [--dry-run]` تعمیرِ یک‌بارهٔ دادهٔ قدیمی است: مصرفِ گمرکِ
// «نقد پرداخت شد» که پیش از این همیشه به USD ثبت می‌شد ولی صندوقش ارز دیگری دارد، با همان
// CustomsDeclarationExpenseSync (مسیر ذخیرهٔ اظهارنامه) به ارزِ صندوق بازنویسی می‌شود. همان سند و همان
// سطر دفتر کل درجا به‌روز می‌شوند (سند تازه ساخته نمی‌شود) و معادل دالری تغییر نمی‌کند؛ گروهی که
// معادل افغانی/نرخ ندارد یا جمع دالری‌اش با سند فرق دارد دست نمی‌خورد. اجرای دوباره بی‌اثر است.
if (args.Contains("--repair-customs-cash-currency", StringComparer.OrdinalIgnoreCase))
{
    var exitCode = await RunCustomsCashCurrencyRepairAsync(app, args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
    return exitCode;
}

// `dotnet run -- --partner-reconciliation` prints, per partnership contract and partner, what the
// partnership statement says, what the ledger's partner current account holds, and the named
// reason for any difference. Read-only.
if (args.Contains("--partner-reconciliation", StringComparer.OrdinalIgnoreCase))
{
    await using var reconciliationScope = app.Services.CreateAsyncScope();
    var reconciliation = reconciliationScope.ServiceProvider
        .GetRequiredService<IPartnerCurrentReconciliationService>();
    var report = await reconciliation.BuildAsync();

    Console.WriteLine("== Partner current reconciliation ==");
    foreach (var row in report.Rows)
    {
        Console.WriteLine(
            $"{row.ContractNumber} / {row.PartnerName}: statement={row.StatementNetPositionUsd:N2}, "
            + $"ledger={row.LedgerPartnerCurrentUsd:N2}, companyFunded={row.CompanyFundedContributionUsd:N2}, "
            + $"partnerFunded={row.PartnerFundedContributionUsd:N2}, proceedsHeld={row.SaleProceedsHeldUsd:N2}, "
            + $"profitShare={row.ProfitShareUsd:N2}, unsoldCostShare={row.UnsoldCostShareUsd:N2}, "
            + $"difference={row.DifferenceUsd:N2} [{row.DifferenceReason ?? "none"}]");
    }

    Console.WriteLine($"Fully reconciled: {report.IsFullyReconciled}");
    return report.IsFullyReconciled ? 0 : 1;
}

// `dotnet run -- --partnership-profit-discrepancies` lists, per partnership contract, the profit
// already appropriated in the ledger against the contract's current realized profit. Read-only:
// nothing is posted, reversed or rewritten. A changed contract needs a human decision (reverse the
// old allocation, then run --accounting-backfill, which is idempotent and supports --dry-run).
if (args.Contains("--partnership-profit-discrepancies", StringComparer.OrdinalIgnoreCase))
{
    await using var discrepancyScope = app.Services.CreateAsyncScope();
    var allocation = discrepancyScope.ServiceProvider
        .GetRequiredService<IPartnershipProfitAllocationAdapter>();
    var rows = await allocation.FindDiscrepanciesAsync();

    Console.WriteLine("== Partnership profit allocation vs realized contract profit ==");
    foreach (var row in rows)
    {
        Console.WriteLine(
            $"{row.ContractNumber}: posted={(row.PostedProfitUsd?.ToString("N2") ?? "none")}, "
            + $"realized={row.CanonicalProfitUsd:N2}, difference={row.DifferenceUsd:N2} [{row.Status}]");
    }

    var changed = rows.Count(x => x.Status == PartnershipProfitAllocationDiscrepancy.ProfitChanged);
    Console.WriteLine($"Changed since allocation: {changed}");
    return changed == 0 ? 0 : 1;
}

await SeedAuthenticationAsync(app);

// Must run first so downstream middleware sees the real scheme/host from the proxy.
app.UseForwardedHeaders();

// HTML responses get Vary: X-PTG-SPA (and no-store for SPA-stripped renders) so
// the browser HTTP cache never serves a stripped page to a normal navigation.
app.UseMiddleware<SpaCacheHeadersMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
// Note: HTTPS termination is handled by Replit's reverse proxy.
// UseHsts / UseHttpsRedirection are intentionally omitted to avoid
// redirect loops when the app receives plain HTTP from the proxy.
app.UseResponseCompression();

// Health-check probe — must respond 200 before auth middleware runs
// so that Replit's autoscale startup probe succeeds.
app.MapGet("/health", () => Results.Ok("healthy")).AllowAnonymous();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var path = context.Context.Request.Path;

        if (path.StartsWithSegments("/css")
            || path.StartsWithSegments("/js")
            || path.StartsWithSegments("/vendor")
            || path.StartsWithSegments("/images")
            || path.StartsWithSegments("/img")
            || path.StartsWithSegments("/assets")
            || path.StartsWithSegments("/favicon.ico"))
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
        else if (path.StartsWithSegments("/uploads"))
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=86400";
        }
    }
});

// فقط /api: خطا و پاسخ خالیِ 401/403/404 به ProblemDetails؛ صفحات وب دست نمی‌خورند.
app.UseMiddleware<ApiErrorMiddleware>();
app.UseRouting();
if (allowedOrigins.Length > 0)
{
    app.UseCors("InstanceOrigins");
}
app.UseAuthentication();
// پس از UseAuthentication تا تفکیک بر اساس کاربر واردشده انجام شود، نه فقط IP.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevAutoSignInMiddleware>();
}

app.UseAuthorization();
app.UseMiddleware<ActivityLogMiddleware>();
// آماده‌سازی پس‌زمینهٔ کار بدون سرور: ETag/304 برای صفحات بدون تغییر (فقط درخواست‌های X-PTG-Prepare).
app.UseMiddleware<OfflinePrepareMiddleware>();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
return 0;

// ---- Helpers ----------------------------------------------------------------
static async Task<int> RunAccountingBackfillAsync(WebApplication app, bool dryRun)
{
    using var scope = app.Services.CreateScope();
    var backfill = scope.ServiceProvider.GetRequiredService<IAccountingBackfillService>();
    var report = await backfill.RunAsync(dryRun);

    Console.WriteLine(dryRun ? "== Accounting backfill - DRY RUN ==" : "== Accounting backfill ==");
    Console.WriteLine($"Chart of accounts seeded: {report.ChartSeeded}");
    foreach (var step in report.Steps)
    {
        Console.WriteLine(
            $"{step.Name}: candidates={step.Candidates}, "
            + $"posted={step.Posted}, missing journal={step.MissingJournal}, "
            + $"needs review={step.NeedsReview}, cancelled={step.Cancelled}, "
            + $"skipped existing={step.SkippedExisting}, skipped other={step.SkippedOther}"
            + (step.Reasons.Count > 0 ? $" [{string.Join(", ", step.Reasons)}]" : string.Empty));
    }
    foreach (var error in report.Errors)
        Console.WriteLine($"ERROR: {error}");
    Console.WriteLine($"Errors: {report.Errors.Count}");

    return report.Succeeded ? 0 : 1;
}

static async Task<int> RunLoadingPriceRepairAsync(WebApplication app, bool dryRun)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    // «بدون قیمت معتبر» با همان تعریف IsPricePending و همان فیلتر SyncPurchaseLoadingPricesAsync.
    var pricelessByContract = await db.LoadingRegisters
        .AsNoTracking()
        .Where(l => l.LoadingPriceUsd == null || l.LoadingPriceUsd <= 0m)
        .GroupBy(l => l.ContractId)
        .Select(g => new { ContractId = g.Key, Count = g.Count() })
        .ToListAsync();

    var contractIds = pricelessByContract.Select(row => row.ContractId).ToList();
    var purchaseContracts = contractIds.Count == 0
        ? []
        : await db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .ToListAsync();

    // «نرخ نهاییِ قطعی» با همان تعریف سیستم؛ اینجا چیزی محاسبه نمی‌شود.
    var repairableContracts = purchaseContracts
        .Where(c => ContractPricingAdapter.GetCanonicalFinalPrice(c).HasValue)
        .OrderBy(c => c.ContractNumber, StringComparer.Ordinal)
        .ToList();
    var repairableContractIds = repairableContracts.Select(c => c.Id).ToHashSet();

    var pendingByContractId = pricelessByContract.ToDictionary(row => row.ContractId, row => row.Count);
    var totalPending = pendingByContractId.Values.Sum();
    var repairablePending = pendingByContractId
        .Where(row => repairableContractIds.Contains(row.Key))
        .Sum(row => row.Value);

    Console.WriteLine(dryRun ? "== Loading price repair - DRY RUN ==" : "== Loading price repair ==");
    Console.WriteLine("-- Before --");
    Console.WriteLine($"Loadings without a valid price: {totalPending}");
    Console.WriteLine($"  repairable (contract has a final price): {repairablePending} in {repairableContracts.Count} contract(s)");
    Console.WriteLine($"  left pending (contract has no final price): {totalPending - repairablePending}");

    if (repairableContracts.Count == 0)
    {
        Console.WriteLine("Nothing to repair.");
        return 0;
    }

    foreach (var contract in repairableContracts)
    {
        var finalPriceUsd = ContractPricingAdapter.GetCanonicalFinalPrice(contract)!.Value;
        Console.WriteLine(
            $"{contract.ContractNumber}: {pendingByContractId[contract.Id]} priceless loading(s) "
            + $"{(dryRun ? "would take" : "take")} {finalPriceUsd:N4} USD/MT");
    }

    if (dryRun)
    {
        Console.WriteLine("Dry run - no change was written.");
        return 0;
    }

    var repaired = 0;
    var errors = 0;
    foreach (var contract in repairableContracts)
    {
        // همان اکشنی که کاربر در برنامه می‌زند: قیمت‌های موجود را دست نمی‌زند و فقط
        // بارگیری بدون قیمت را تکمیل می‌کند، سپس دفتر و حسابداری را هماهنگ می‌کند.
        await using var contractScope = app.Services.CreateAsyncScope();
        var controller = ActivatorUtilities.CreateInstance<ContractsController>(contractScope.ServiceProvider);
        controller.TempData = new TempDataDictionary(new DefaultHttpContext(), new MaintenanceTempDataProvider());

        try
        {
            await controller.RepricePurchaseLoadings(contract.Id);
            repaired += pendingByContractId[contract.Id];
        }
        catch (Exception ex)
        {
            errors++;
            Console.WriteLine($"ERROR {contract.ContractNumber}: {ex.Message}");
        }
    }

    await using var verifyScope = app.Services.CreateAsyncScope();
    var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var remaining = await verifyDb.LoadingRegisters
        .AsNoTracking()
        .CountAsync(l => l.LoadingPriceUsd == null || l.LoadingPriceUsd <= 0m);

    Console.WriteLine("-- After --");
    Console.WriteLine($"Repaired loadings: {repaired}");
    Console.WriteLine($"Loadings still without a price: {remaining}");
    Console.WriteLine($"Errors: {errors}");

    return errors == 0 ? 0 : 1;
}

static string? CliArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            return args[i][(name.Length + 1)..].Trim();
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1].Trim();
    }

    return null;
}

static async Task<int> RunLoadingFreightPartyRepairAsync(WebApplication app, int contractId, string? providerName, bool dryRun)
{
    if (contractId <= 0 || string.IsNullOrWhiteSpace(providerName))
    {
        Console.WriteLine("Usage: --repair-loading-freight --contract-id=N --provider-name=\"...\" [--dry-run]");
        return 2;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contractId);
    if (contract is null)
    {
        Console.WriteLine($"Contract {contractId} not found.");
        return 2;
    }

    Console.WriteLine(dryRun ? "== Loading freight party repair - DRY RUN ==" : "== Loading freight party repair ==");
    Console.WriteLine($"Contract: {contract.Id} {contract.ContractNumber} (CompanyId {contract.CompanyId})");

    var providers = await db.ServiceProviders.Where(p => p.Name == providerName).ToListAsync();
    if (providers.Count > 1)
    {
        Console.WriteLine($"ERROR: {providers.Count} service providers named '{providerName}'.");
        return 1;
    }

    var provider = providers.SingleOrDefault();
    if (provider is null && !dryRun)
    {
        provider = new PTGOilSystem.Web.Models.Entities.ServiceProvider
        {
            Name = providerName,
            ProviderType = ServiceProviderType.TransportCompany,
            IsActive = true
        };
        db.ServiceProviders.Add(provider);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IAuditService>().LogAndSaveAsync(
            nameof(PTGOilSystem.Web.Models.Entities.ServiceProvider),
            provider.Id,
            AuditAction.Insert,
            diff: PTGOilSystem.Web.Services.Audit.AuditDiffFormatter.ForCreate(
                ("Name", provider.Name),
                ("ProviderType", provider.ProviderType),
                ("Source", "Repair.LegacyFreightParty")));
        Console.WriteLine($"Created service provider {provider.Id} '{provider.Name}'.");
    }
    else
    {
        Console.WriteLine(provider is null
            ? $"Service provider '{providerName}' would be created."
            : $"Service provider: {provider.Id} '{provider.Name}'.");
    }

    var controller = ActivatorUtilities.CreateInstance<LoadingController>(scope.ServiceProvider);
    controller.TempData = new TempDataDictionary(new DefaultHttpContext(), new MaintenanceTempDataProvider());
    var repaired = await controller.RepairLegacyFreightPartyAsync(
        contractId,
        provider ?? new PTGOilSystem.Web.Models.Entities.ServiceProvider { Name = providerName },
        dryRun);

    Console.WriteLine($"Incomplete loadings {(dryRun ? "to repair" : "repaired")}: {repaired.Count}, freight {repaired.Sum(r => r.FreightUsd):N2} USD");
    foreach (var (loadingId, freightUsd) in repaired)
        Console.WriteLine($"  loading {loadingId}: {freightUsd:N2}");

    if (!dryRun && provider is not null)
    {
        var verifyDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        verifyDb.ChangeTracker.Clear();
        var expenses = await verifyDb.ExpenseTransactions.AsNoTracking()
            .Where(e => e.ContractId == contractId && e.ServiceProviderId == provider.Id && !e.IsCancelled)
            .Select(e => e.AmountUsd)
            .ToListAsync();
        var owed = await verifyDb.LedgerEntries.AsNoTracking()
            .Where(l => l.ServiceProviderId == provider.Id)
            .SumAsync(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
        Console.WriteLine("-- After --");
        Console.WriteLine($"Active freight expenses for provider on contract: {expenses.Count}, {expenses.Sum():N2} USD");
        Console.WriteLine($"Provider ledger balance (credit - debit): {owed:N2} USD");
    }

    return 0;
}

static async Task<int> RunCustomsCashCurrencyRepairAsync(WebApplication app, bool dryRun)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
    var expenseAccounting = scope.ServiceProvider.GetService<IExpenseAccountingAdapter>();

    var mismatched = (await db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled
                && e.CustomsDeclarationId != null
                && e.CustomsComponentGroup != null
                && e.SettlementMode == ExpenseSettlementMode.PaidImmediately
                && e.CashAccountId != null)
            .Join(
                db.CashAccounts.AsNoTracking(),
                e => e.CashAccountId,
                a => (int?)a.Id,
                (e, a) => new
                {
                    e.Id,
                    DeclarationId = e.CustomsDeclarationId!.Value,
                    Group = e.CustomsComponentGroup!.Value,
                    e.Amount,
                    e.Currency,
                    e.AppliedFxRateToUsd,
                    e.AmountUsd,
                    CashAccountId = a.Id,
                    CashCurrency = a.Currency
                })
            .ToListAsync())
        .Where(x => SystemCurrency.Normalize(x.Currency) != SystemCurrency.Normalize(x.CashCurrency))
        .OrderBy(x => x.DeclarationId)
        .ThenBy(x => x.Id)
        .ToList();

    Console.WriteLine(dryRun ? "== Customs cash currency repair - DRY RUN ==" : "== Customs cash currency repair ==");
    Console.WriteLine($"Paid customs expenses whose currency differs from their cash account: {mismatched.Count}");

    var repaired = 0;
    var skipped = 0;
    foreach (var declarationRows in mismatched.GroupBy(x => x.DeclarationId))
    {
        var declaration = await db.CustomsDeclarations
            .Include(d => d.Items)
            .SingleAsync(d => d.Id == declarationRows.Key);
        var (dutyUsd, serviceUsd) = PTGOilSystem.Web.Services.Customs.CustomsDeclarationExpenseSync.SplitUsd(declaration.Items);

        var ready = true;
        foreach (var row in declarationRows)
        {
            var groupUsd = row.Group == PTGOilSystem.Web.Services.Customs.CustomsComponentGroup.GovernmentDuty ? dutyUsd : serviceUsd;
            var target = PTGOilSystem.Web.Services.Customs.CustomsDeclarationExpenseSync.ResolveCashAmount(
                row.CashCurrency,
                row.AmountUsd,
                PTGOilSystem.Web.Services.Customs.CustomsDeclarationExpenseSync.GroupAfn(declaration.Items, row.Group));
            var reason = groupUsd != row.AmountUsd
                ? $"SKIP: declaration USD {groupUsd:N4} differs from expense USD {row.AmountUsd:N4}"
                : target is null
                    ? "SKIP: AFN amount or rate missing for this group"
                    : $"-> {target.Amount:N4} {target.Currency} @ {target.RateToUsd} (USD {row.AmountUsd:N4} unchanged)";
            ready &= groupUsd == row.AmountUsd && target is not null;
            Console.WriteLine(
                $"  declaration {row.DeclarationId} expense {row.Id} {row.Group}: {row.Amount:N4} {row.Currency} on cash account {row.CashAccountId} ({row.CashCurrency}) {reason}");
        }

        if (!ready)
        {
            skipped += declarationRows.Count();
            continue;
        }

        if (dryRun)
        {
            repaired += declarationRows.Count();
            continue;
        }

        var activeBefore = await db.ExpenseTransactions.CountAsync(e => e.CustomsDeclarationId == declaration.Id && !e.IsCancelled);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await PTGOilSystem.Web.Services.Customs.CustomsDeclarationExpenseSync.SyncAsync(db, declaration, expenseAccounting);

        var expenseIds = declarationRows.Select(r => r.Id).ToList();
        var after = await db.ExpenseTransactions.AsNoTracking()
            .Where(e => expenseIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);
        var activeAfter = await db.ExpenseTransactions.CountAsync(e => e.CustomsDeclarationId == declaration.Id && !e.IsCancelled);
        if (activeAfter != activeBefore || declarationRows.Any(r => after[r.Id].IsCancelled || after[r.Id].AmountUsd != r.AmountUsd))
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"ERROR: declaration {declaration.Id} changed more than the cash currency; rolled back.");
            return 1;
        }

        foreach (var row in declarationRows)
        {
            var expense = after[row.Id];
            await audit.LogAndSaveAsync(
                nameof(ExpenseTransaction),
                expense.Id,
                AuditAction.Update,
                diff: PTGOilSystem.Web.Services.Audit.AuditDiffFormatter.ForUpdate(
                    ("Amount", row.Amount, expense.Amount),
                    ("Currency", row.Currency, expense.Currency),
                    ("AppliedFxRateToUsd", row.AppliedFxRateToUsd, expense.AppliedFxRateToUsd),
                    ("Source", null, "Repair.CustomsCashCurrency")));
        }

        await transaction.CommitAsync();
        repaired += declarationRows.Count();
    }

    Console.WriteLine($"Expenses {(dryRun ? "to repair" : "repaired")}: {repaired}, skipped: {skipped}");
    return 0;
}

// کلید تفکیک محدودسازی نرخ: کاربر واردشده، و در نبود آن IP مبدأ.
static string ResolveRateLimitPartitionKey(HttpContext httpContext)
    => httpContext.User?.Identity?.IsAuthenticated == true
        ? $"user:{httpContext.User.Identity.Name}"
        : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

// الگوی مسیر (نه URL) تا هر اکشن خروجی سطل خودش را داشته باشد و تعداد سطل‌ها با
// id‌های مختلف رشد نکند.
static string ResolveRateLimitRouteKey(HttpContext httpContext)
    => (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
        ?? httpContext.Request.Path.Value
        ?? "unknown";

static async Task SeedAuthenticationAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var bootstrapper = scope.ServiceProvider.GetRequiredService<AuthBootstrapper>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    await bootstrapper.EnsureSeedDataAsync(new BootstrapAdminOptions
    {
        Username = configuration["PTG_BOOTSTRAP_ADMIN_USERNAME"] ?? "admin",
        FullName = configuration["PTG_BOOTSTRAP_ADMIN_FULLNAME"] ?? "System Administrator",
        Password = configuration["PTG_BOOTSTRAP_ADMIN_PASSWORD"]
    });
}

static string BuildPostgresConnectionString(string raw)
{
    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var database = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 5432;
        return $"Host={uri.Host};Port={port};Username={username};Password={password};Database={database};{DbTlsSettings()}";
    }
    return raw;
}

// TLS hardening is opt-in via PTG_DB_STRICT_TLS=true so the default Replit/Postgres
// connection (self-signed cert) keeps working unless strict TLS is explicitly enabled.
static string DbTlsSettings()
{
    var strict = string.Equals(
        Environment.GetEnvironmentVariable("PTG_DB_STRICT_TLS"),
        "true",
        StringComparison.OrdinalIgnoreCase);
    return strict
        ? "SSL Mode=Require;Trust Server Certificate=false"
        : "SSL Mode=Prefer;Trust Server Certificate=true";
}

// نگهدارندهٔ بی‌اثر TempData برای اجرای اکشن قرارداد از خط فرمان (بیرون از چرخهٔ درخواست).
sealed class MaintenanceTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object?> LoadTempData(HttpContext context)
        => new Dictionary<string, object?>();

    public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
    {
    }
}
