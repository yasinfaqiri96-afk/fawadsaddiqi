using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.OperationalPeriod;

/// <summary>
/// فهرستِ «چه چیزی سند مالی/عملیاتی است و تاریخِ تجاری‌اش کدام ستون است».
///
/// PTG-P1-01 گفت مسیرهای ثبت پراکنده‌اند؛ اگر هر کنترلر خودش تاریخ را چک کند، همیشه یکی
/// از قلم می‌افتد. این فهرست تنها جای تعریف دامنهٔ قفل است و <c>ApplicationDbContext</c>
/// موقع ذخیره از همین می‌پرسد، پس هیچ مسیری — حتی سرویس یا ایمپورت — دور نمی‌زند.
///
/// عمداً فقط اسنادی اینجا هستند که موجودی یا مانده‌ای می‌سازند. دادهٔ پایه (کالا، ترمینال،
/// نرخ روز، کاربر) تاریخِ دوره ندارد و قفل به آن کاری ندارد.
/// </summary>
public static partial class OperationalPeriodScope
{
    /// <summary>
    /// تاریخِ تجاریِ این موجودیت، یا null اگر اصلاً در دامنهٔ قفل نباشد.
    /// فقط تاریخِ اصلیِ سند خوانده می‌شود، نه تاریخ‌های کمکی مثل نرخ ارز.
    /// </summary>
    public static DateTime? BusinessDateOf(object entity) => entity switch
    {
        SalesTransaction x => x.SaleDate,
        ExpenseTransaction x => x.ExpenseDate,
        PaymentTransaction x => x.PaymentDate,
        // تراکنش معاش مستقیم وارد مانده کارمند می‌شود و هیچ LedgerEntry نمی‌سازد، پس
        // بدون این سطر تنها سندِ مالی‌ای بود که می‌شد در ماهِ بسته ثبتش کرد.
        EmployeeSalaryTransaction x => x.TransactionDate,
        PartnerSettlement x => x.SettlementDate,
        SupplierBalanceTransfer x => x.TransferDate,
        ContractBalanceTransfer x => x.TransferDate,
        SarrafSettlement x => x.SettlementDate,
        LoadingRegister x => x.LoadingDate,
        LoadingReceipt x => x.ReceiptDate,
        TruckDispatch x => x.DispatchDate,
        LossEvent x => x.EventDate,
        InventoryMovement x => x.MovementDate,
        CustodyMovement x => x.MovementDate,
        CustodySale x => x.SaleDate,
        CustodyFinanceEntry x => x.EntryDate,
        CustodyConversion x => x.ConversionDate,
        CustodySaleReturn x => x.ReturnDate,
        CommodityLoan x => x.LoanDate,
        CommodityLoanSettlement x => x.SettlementDate,
        LedgerEntry x => x.EntryDate,
        _ => null,
    };

    /// <summary>نامِ فارسیِ سند برای پیام خطا. کاربر باید بداند کدام سند رد شد.</summary>
    public static string DescribeKind(object entity) => entity switch
    {
        SalesTransaction => "سند فروش",
        ExpenseTransaction => "سند مصرف",
        PaymentTransaction => "سند پرداخت",
        EmployeeSalaryTransaction => "سند معاش",
        PartnerSettlement => "تسویه شریک",
        SupplierBalanceTransfer => "انتقال مانده تأمین‌کننده",
        ContractBalanceTransfer => "انتقال مانده قرارداد",
        SarrafSettlement => "تسویه صرافی",
        LoadingRegister => "سند بارگیری",
        LoadingReceipt => "رسید بارگیری",
        TruckDispatch => "ارسال موتر",
        LossEvent => "سند کسری",
        InventoryMovement => "حرکت موجودی",
        CustodyMovement => "حرکت امانی",
        CustodySale => "فروش امانی",
        CustodyFinanceEntry => "سند مالی امانی",
        CustodyConversion => "تبدیل امانی به خرید",
        CustodySaleReturn => "برگشت فروش امانی",
        CommodityLoan => "قرض جنسی",
        CommodityLoanSettlement => "تسویه قرض جنسی",
        LedgerEntry => "ردیف دفتر کل",
        _ => "سند",
    };
}
