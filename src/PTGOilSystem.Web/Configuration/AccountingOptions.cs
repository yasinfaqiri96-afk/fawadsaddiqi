namespace PTGOilSystem.Web.Configuration;

public sealed class AccountingOptions
{
    public const string SectionName = "Accounting";

    // Stage 2 is infrastructure-only. Operational posting remains opt-in.
    public bool Enabled { get; set; }
    public string DefaultFunctionalCurrencyCode { get; set; } = "USD";
    public AccountingPilotOptions Pilots { get; set; } = new();
}

public sealed class AccountingPilotOptions
{
    public bool ContractBalanceTransfer { get; set; }
    public bool SupplierPaymentAllocation { get; set; }

    // انتقال «مانده قابل انتقال» تأمین‌کننده. هنوز Adapter ژورنال ندارد؛ روشن‌کردن این فلگ
    // عمداً ثبت انتقال را متوقف می‌کند تا سطر Legacy بدون سند ژورنال ثبت نشود.
    public bool SupplierBalanceTransfer { get; set; }

    // Stage 4 — receipts and payments. Each cash sub-module owns an independent flag so a
    // single mapping can be piloted without exposing the others.
    public bool CustomerReceipt { get; set; }
    public bool CustomerAdvance { get; set; }
    public bool SupplierPayment { get; set; }
    public bool SupplierPrepayment { get; set; }
    public bool SarrafPayment { get; set; }

    // Stage 5 — expenses, freight, commission. Expense accrues the liability; the two
    // payment flags settle it, so they are normally enabled together with Expense.
    public bool Expense { get; set; }
    public bool ExpensePayment { get; set; }
    public bool CommissionPayment { get; set; }

    // امانی — فروش به نمایندگی مالک، وصول، کمیشن، مصارف به حساب مالک و پرداخت به مالک.
    //   فروش              Dr دریافتنی مشتری             Cr طلب امانی به نفع مالک (کاهندهٔ دریافتنی)
    //   وصول نقدی         Dr صندوق/بانک                 Cr دریافتنی مشتری
    //                     Dr طلب امانی به نفع مالک      Cr وجوه امانی پرداختنی (مالک)
    //   وصول مستقیم مالک  Dr طلب امانی به نفع مالک      Cr دریافتنی مشتری
    //   کمیشن             Dr وجوه امانی پرداختنی         Cr درآمد کمیشن امانی
    //   حق خدمات          Dr وجوه امانی پرداختنی         Cr درآمد خدمات امانی
    //   مصرف به حساب مالک Dr وجوه امانی پرداختنی         Cr صندوق/بانک
    //   پرداخت به مالک    Dr وجوه امانی پرداختنی         Cr صندوق/بانک
    public bool Custody { get; set; }

    // قرض جنسی — قرض‌دادن از موجودی ملکی و قرض‌گرفتن با تعهد برگشت جنس.
    //   قرض‌دادن          Dr طلب جنسی (حسابِ قرارداد)    Cr موجودی (بهای میانگین همان ترمینال)
    //   برگشت جنس        Dr موجودی                       Cr طلب جنسی (ارزش دفتری آزادشده)
    //   قرض‌گرفتن         Dr موجودی (ارزش توافقی)         Cr بدهی جنسی (حسابِ قرارداد)
    //   برگشت به طرف      Dr بدهی جنسی                    Cr موجودی (بهای میانگین)؛ تفاوت ← حساب تفاوتِ قرارداد
    //   تسویه نقدی        صندوق ↔ طلب/بدهی جنسی؛ تفاوت ← حساب تفاوتِ قرارداد
    // دفتر کل روشن و این فلگ خاموش ⇒ سند قرض ثبت نمی‌شود (سند نصفه ساخته نمی‌شود).
    public bool CommodityLoan { get; set; }

    // Stage 6 — purchase and inventory. Purchase raises the supplier payable against goods in
    // transit; InventoryReceipt moves those goods from in-transit into inventory, so the two
    // are normally enabled together.
    public bool Purchase { get; set; }
    public bool InventoryReceipt { get; set; }

    // Stage 7 — sales and cost of goods sold. Sale posts the revenue; Cogs values what left
    // inventory. Cogs depends on InventoryReceipt having filled the valuation pool.
    public bool Sale { get; set; }
    public bool Cogs { get; set; }

    // Stage 8 — losses, shortage, sarraf. InventoryLoss writes off stock the tanks lost and,
    // like Cogs, depends on InventoryReceipt having filled the valuation pool. ShortageCharge
    // recognises what a carrier owes for what did not arrive. The two sarraf flags cover the
    // settlement flows that the Stage 4 SarrafPayment flag does not reach.
    public bool InventoryLoss { get; set; }
    public bool ShortageCharge { get; set; }
    public bool SarrafSettlement { get; set; }
    public bool ThreeWaySettlement { get; set; }

    // Inter-terminal transfers. Goods at different terminals cost different amounts to have got
    // there, so the valuation pool is keyed by terminal — which means a transfer has to carry its
    // cost from one pool to the other, or the sale at the destination values against the wrong
    // stock. Without this flag a transfer moves tonnes and no money, and Cogs at the destination
    // is unsafe; that is why enabling Cogs depends on enabling this.
    public bool InventoryTransfer { get; set; }

    // Stage 9 — operational asset rent. Only the rent a user records by hand in
    // OperationalAssetsController.CreateRent reaches this flag. The rents LoadingController
    // generates automatically are deliberately outside it: their freight/expense counterpart is
    // already posted from the loading side, so posting them here as well would double-count the
    // same money. Enabling this flag must not change that.
    public bool AssetRent { get; set; }

    // تخصیص سودِ قرارداد شراکتی به حساب جاری شرکا. به Sale و Cogs وابسته است — تا وقتی بهای
    // تمام‌شده ثبت نشده باشد، «سود دفتری» عددِ نهایی نیست و تخصیصش زودهنگام است.
    public bool PartnershipProfitAllocation { get; set; }

    // مدیریت بشری — معاش کارمند. یک فلگ برای هر سه جریان، چون پرداخت و مساعده همان بدهی و
    // طلبی را تسویه می‌کنند که ثبتِ معاش ساخته است؛ روشن‌کردنِ یکی بدون دیگری ماندهٔ «بدهی به
    // کارمند» را در دفتر کل منفی می‌کند.
    //   ثبت معاش / بونس / معاش نهایی‌شده   Dr Salary Expense        Cr Employee Payable
    //   پرداخت معاش                          Dr Employee Payable      Cr Cash/Bank
    //   مساعده                               Dr Employee Advance      Cr Cash/Bank
    //   وصول مساعده/قرضه از معاش             Dr Employee Payable      Cr Employee Advance
    public bool EmployeeSalary { get; set; }
}
