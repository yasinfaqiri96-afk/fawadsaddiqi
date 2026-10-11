# ادامهٔ توسعهٔ امانی توسط Claude

این فایل وضعیت واقعی ادامهٔ کار Claude را ثبت می‌کند. مرجع کلی و تاریخچه: [custody-completion-progress.md](custody-completion-progress.md).

## مبنا

- شاخهٔ Work: `feature/custody-final-completion`؛ آخرین Commit معتبر هنگام شروع: `a16ffe1f1d0d6cfd0c3d2e3cc737d65dbffd3e61` (پس از آن چیزی Push نشده بود).
- شاخهٔ Claude: `feature/custody-claude-continuation`، ساخته‌شده از همان `a16ffe1`. روی main و شاخهٔ Work هیچ Push نشده است.
- محیط: .NET SDK 8.0.425 و PostgreSQL 16.15 محلیِ آزمایشی (`127.0.0.1`)؛ هیچ دیتابیس تولیدی، بکاپ واقعی یا رمز تولیدی استفاده نشد.
- **فروش از چند بار** در GitHub موجود نبود (کدِ Work ذخیره نشده بود)؛ از نو و روی مسیرهای موجود پیاده شد.

## نتیجهٔ مبنا (پیش از هر تغییر)

- Build: ۰ خطا، ۱۴ هشدار موجود.
- تست‌های Custody: **88 Passed / 0 Failed / 0 Skipped**.
- Suite کامل `a16ffe1`: **4160 Passed / 18 Failed / 0 Skipped / 4178**؛ هر ۱۸ شکست `PartnerSettlementImportTests` به‌علت نبود `Payment.xlsx` (جزئیات: [payment-source-fixture.md](payment-source-fixture.md)). فایل جعلی ساخته نشد و تستی حذف یا Skip نشد.

## مرحلهٔ ۱ — تثبیت تبدیل/هویت و فروش چندباره، برگشت قسمی و مصرف نسیه

| قابلیت | وضعیت | توضیح کوتاه |
|---|---|---|
| تبدیل امانی به خرید (کامل/قسمی، مقدار فزیکی، بهای تمام‌شده، بدهی، ژورنال یک‌بار) | Complete and Tested | تست‌های قبلی Work دوباره موفق‌اند. |
| لغو تبدیل پس از برگشتِ کامل اسناد وابسته | Complete and Tested | دو تست تازه: پس از برگشتِ حرکت خروج ملکی و پس از برگشتِ انتقال مانده، لغو مجاز و اتمیک است. |
| مسیرهای جایگزینِ ویرایش | Complete and Tested (backend)، Needs Verification (UI) | اصلاح قیمت قرارداد (`EditPricing`)، اصلاح قیمت بارگیری (`EditPrice`) و تغییر مقدار/قیمت در اصلاح بارگیری برای قرارداد تبدیل مسدود شد؛ اصلاح فقط با لغو و ثبت دوبارهٔ تبدیل. |
| هویت مالک/فروشنده | Complete and Tested | پیوند هم‌زمان روی هر دو هویت قفل می‌شود؛ یک فروشنده به دو مشتری (یا دو شریک) متفاوت پیوند نمی‌خورد. تطبیق براساس نام ندارد. |
| حساب مالیِ شریکِ پیوندشده | Complete and Tested | تیلِ شریکی که مدیر با سند معتبر به نقش فروشندهٔ موجودِ همان شخص پیوند داده، از همان نقش فروشنده فروخته و تسویه می‌شود؛ شخص تازه، حساب «سهم شراکت» یا LedgerEntry شریک ساخته نمی‌شود. شرکتِ دیگر (Company) همچنان فقط نگهداری دارد. |
| Audit در دیتابیس | Complete and Tested | تست‌ها وجود سابقه را با اتصال جداگانه به PostgreSQL می‌سنجند (تبدیل، پیوند، فروش چندباره، برگشت). |
| فروش امانی از چند بار | Complete and Tested | یک سند فروش، یک ژورنال، یک کمیشن روی کل؛ سهم هر بار یک حرکت جدا با OperationKey مشترک؛ قفل همهٔ بارها به ترتیب شناسه؛ سقف مسدودی هر بار؛ فقط همان مالک و همان جنس؛ لغو همهٔ سهم‌ها را یک‌جا برمی‌گرداند؛ هم‌زمانی و توکن تکراری تست شد. |
| برگشت قسمی فروش | Complete and Tested | سند `CustodySaleReturn`؛ تیل به همان بار و محلِ فروش برمی‌گردد (ظرفیت/اختلاط کنترل می‌شود)؛ طلب مشتری و «در انتظار وصول» مالک کم می‌شود؛ کمیشن با همان روش روی مقدار باقی دوباره سنجیده می‌شود (کمیشن ثابت تا برگشتِ کامل باقی است)؛ کمیشنِ «هنگام فروش» معکوس و مفاد شرکت اصلاح می‌شود؛ آخرین برگشتِ کامل باقیماندهٔ دقیق مبلغ و کمیشن را می‌گیرد؛ لغو از آخر به اول؛ لغوِ فروشِ دارای برگشت بسته است. |
| برگشتِ پولِ وصول‌شده به مشتری | Blocked by Business Decision | برگشت فقط تا مبلغ وصول‌نشده مجاز است. استرداد پول وصول‌شده (از صندوق شرکت یا از حساب مالک، و سهم کمیشن) قاعدهٔ قراردادی لازم دارد؛ حدس زده نشد. |
| مصرف نسیهٔ خدمات‌دهنده به حساب مالک و پرداخت قسمی | Complete and Tested | مالک بدهکار، شرکت به خدمات‌دهنده بدهکار (حساب پرداختنی از «نوع مصرف» — همان نگاشت مصارف عادی؛ نوع بدون حساب رد می‌شود). پرداخت کامل/قسمی از صندوق، بدون کم‌شدن دوبارهٔ حساب مالک؛ اضافه‌پرداخت و پرداخت هم‌زمان رد می‌شود؛ لغو مصرفِ دارای پرداخت بسته است. هیچ ExpenseTransaction (مصرف P&L شرکت) ساخته نمی‌شود. |
| حق خدمات شرکت، مصرف نقدی به حساب مالک، لغو امن | Complete and Tested | از قبل موجود؛ دوباره تست شد. |
| تطبیق تاریخی و راپور GL | Complete and Tested | برگشت‌ها و مصارف نسیه در «سند فاقد ژورنال»، Preview/Apply تطبیق و راپور GL امانی شمرده می‌شوند؛ سطرهای خدمات‌دهنده در مانده مالک قاطی نمی‌شوند. |

### Migration تازه

`20261011231526_AddCustodySaleReturnsAndProviderCharges` — کاملاً افزایشی: جدول `CustodySaleReturns` و دو ستون nullable (`ServiceProviderId`, `SettlesFinanceEntryId`) در `CustodyFinanceEntries` با FK محدودکننده. هیچ داده‌ای backfill یا بازنویسی نمی‌شود.

- EF Pending-model پس از Build تازه: `No changes have been made to the model since the last migration.`
- دیتابیس آزمایشی تا Migration قبلی ساخته شد، دادهٔ کاملاً ساختگی (بار ۱۰۰ تُن، فروش ۳۰ تُن ۱۵٬۰۰۰ دالر، وصول ۶٬۰۰۰) ثبت و بکاپ گرفته شد؛ ارتقا روی همان دیتابیس و روی نسخهٔ بازیابی‌شده از بکاپ موفق بود و حرکات، فروش، اسناد مالی و بارها بدون تغییر ماندند. Down روی نسخهٔ بازیابی‌شده هم موفق و داده بدون تغییر بود.

### نتایج تست این مرحله

- Build: ۰ خطا، ۱۴ هشدار موجود.
- تست‌های Custody: **107 Passed / 0 Failed / 0 Skipped** (۸۸ قبلی + ۱۹ تازه).
- تست‌های مرتبط (PartyStatement، ProfitAndLoss، ExpenseAccountingAdapter، LoadingController، ContractsController، ShellViewStructure، Migration، SimplePurchase، OperationalPeriod، InventoryMovementWriter): **385 Passed / 0 Failed**.
- اجرای نخست: ۱۷ تست تازه به‌دلیل تاریخ آیندهٔ داده‌های تست (PeriodGuard) و یک تست به‌دلیل تاریخ فروش دوباره قبل از برگشت رد شدند؛ تاریخ‌ها اصلاح شدند. یک تست قبلی ترتیب خطا (`CUSTODY_INSUFFICIENT` پیش از `CUSTODY_HELD`) را نشان داد؛ ترتیب کنترل خروج تکی در مسیر چندباره حفظ شد. هیچ assertion ضعیف نشد.

## فایل‌های تغییرکرده (مرحلهٔ ۱)

سرویس‌ها: `CustodyService`، `CustodySaleService`، `CustodyConversionService`، `CustodyReadService`، `CustodyGlReadService`، `CustodyAccountingReconciliationService`، `CustodyAccountingAdapter`، `ExpenseAccountingAdapter` (فقط استخراج نگاشت حساب پرداختنی بدون تغییر رفتار)، `PartyStatementReadService`، `ProfitAndLossService.CompanyPeriod`، `OperationalPeriodScope`.
Controllerها: `CustodyController`، `ContractsController` (گارد قیمت)، `LoadingController` (گارد مقدار/قیمت).
Viewها: `Custody/Sale`، `Custody/SaleReturn` (تازه)، `Custody/Finance`، `Custody/Owner`، `Custody/Details`.
مدل/EF: `Custody.cs`، `CustodyModelConfiguration`، `ApplicationDbContext`، Migration بالا.
تست: `CustodyContinuationPostgresTests` (تازه)، `CustodyConversionPostgresTests`.

## گام بعدی

مرحلهٔ واحدها (لیتر برای بارگیری در راه، انتقال، فروش و تحویل امانی با همان قاعدهٔ معتبر)، سپس راپورها، E2E مرورگر، Suite کامل و PR. چندارزی و قرض جنسی وابسته به تصمیم‌های قراردادی‌اند (پایین).
