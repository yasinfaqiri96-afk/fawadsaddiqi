# نگاشت رابط مشعل به تصاویر مرجع

مرجع اجرایی: متن کامل بستهٔ Mashal-UI-Reference-Pack؛ هر هفت تصویر باز و بررسی شد.
دامنهٔ این تغییر، Razor/CSS/JavaScript نمایش است. Controller، Service، Entity، DbContext، migration و محاسبات تغییر نمی‌کنند.

| صفحه / کامپوننت | فایل واقعی | مرجع / variant | وضعیت و حدود شواهد |
|---|---|---|---|
| پوسته، نوار بالا، منوی ماژول‌ها | `Views/Shared/_Layout.cshtml` | R01–R04 روشن؛ R05–R06 آبی؛ دریافت مرتبط با بل R07 | variant بر اساس مسیر و همان مدل فرم؛ بدون انتخاب تم جدید |
| سبک‌های مشترک | `wwwroot/css/ptg/78-mashal-reference.css` | هر دو خانواده، مستقل | توکن، هندسه، کنترل، responsive؛ بارگذاری در Development و bundle |
| حساب کاربری و امنیت | `Views/Auth/ChangePassword.cshtml` | R01 روشن | هویت واقعی از Claims، جزئیات رمز در disclosure قابل استفاده با کیبورد؛ هیچ آپلود یا فیلد پروفایل بدون backend اضافه نشد |
| کسب‌وکار | `Views/Companies/Edit.cshtml`, `_CreateForm.cshtml` | R02 روشن | لوگوی موجود مشعل؛ فیلدهای واقعی شرکت، مالک دفتر و وضعیت حفظ شدند |
| حسابداری | `Views/ChartOfAccounts/Mapping.cshtml` | R03 روشن، تعمیم محتوای واقعی | تنظیمات mapping موجود، گروه‌بندی دو ستونه، قفل حساب دارای سند و همان POST؛ FIFO/مالیات ساختگی اضافه نشده |
| تقویم | `Views/SystemSettings/Index.cshtml` | R03 روشن، تعمیم سبک | همان ذخیرهٔ تقویم و همان توضیح اثر بر تاریخ؛ بدون تغییر دادهٔ تاریخ |
| ستون داخلی تنظیمات | `Views/Shared/_MashalSettingsNav.cshtml` | R01–R03 | گزینه‌های واقعی و گارد مجوز؛ جستجو فقط در ناوبری |
| تراکنش | `Views/Payments/Create.cshtml` | R04 روشن | تمام name/id/data-* و tokenهای ضدتکرار و CSRF حفظ شده‌اند؛ بانک/شخص دو مقصد مستقل، اطلاعات تخصصی در جزئیات بیشتر |
| دریافت مرتبط با بل | همان `Views/Payments/Create.cshtml` | R07 آبی در زیرجریان موجود | تنها وقتی `SalesTransactionId` در مدل وجود دارد؛ مدل یک تراکنش چندشخص/چندبانک را پشتیبانی نمی‌کند، دکمهٔ افزودن جعلی ندارد |
| داشبورد | `Views/Home/Index.cshtml`, `_DashboardInsights.cshtml` | R05 آبی | سه ستون با نسبت 457:305:258، داده از `RecentActivities`، `TankCapacities` و هشدارها؛ آمار، نمودار و محمولهٔ فعلی حذف نشده |
| مودال ابزار | همان Home و `wwwroot/js/mashal-reference.js` | R06 آبی | Bootstrap dialog واقعی با Escape، focus trap و بازگرداندن focus؛ شش بخش واقعی، انتخاب همه و ماندگاری محلی برای هر کاربر |
| input/select/combobox/date/textarea | 78 + کامپوننت‌های `ak-*` فعلی | R01–R04؛ ارتفاع 32px | منطق انتخاب موجودیت و date picker موجود حفظ شد؛ readonly/error/disabled قابل تشخیص |
| checkbox/radio/switch | 78 + Bootstrap فعلی | R03، تعمیم | checked و disabled از مدل واقعی، هرگز از وضعیت عکس تنظیم نمی‌شوند |
| button/badge/card/table/pagination | 78 + `Views/Shared/Components/Ak` فعلی | R04/R05، تعمیم | ستون‌های مالی، مرتب‌سازی، pagination و فرم backend حذف نشده‌اند |
| dropdown/drawer/toast/tooltip/loading/error | کنترل‌های Bootstrap و پوستهٔ فعلی | تعمیم روشن/آبی | رفتار و permission gate موجود حفظ شده؛ در تصاویر شواهد کامل از حالت‌ها نیست |
| مخازن، موجودی، حمل، حواله، خرید/فروش، صراف و گزارش‌ها | Viewهای فعلی زیر همان layout | تعمیم R01–R04 | از قالب کنترل مشترک استفاده می‌کنند؛ این صفحات «عین مرجع» نیستند |

## تفاوت‌های عمدی و محدودیت‌ها

- عنوان‌ها، واحدهای USD/AFN/MT، نام شرکت/مشتری، تاریخ و محتوای نفت‌وگاز از مشعل می‌آیند؛ دادهٔ ریالی یا اشخاص مرجع کپی نشده است.
- لوگوی آبی و سفید موجود مشعل به‌جای چرتکه استفاده می‌شود؛ ظاهر تجاری کاربر در داده‌ها دست‌نخورده است.
- فونت پروژه IRANSans است. خانواده و فایل اصلی فونت از تصاویر قطعی نیست. آواتار اصلی کارمند هدست‌دار در بسته asset مستقل ندارد؛ تصویر فعلی پروژه حفظ شده و معادل دقیق مرجع ادعا نمی‌شود.
- تنظیمات محصول با مرجع یکسان نیست؛ حسابداری mapping، دریافت تک‌تراکنش و نمودارهای نفت‌وگاز حفظ شده‌اند. این تفاوت‌ها در diff پنهان نمی‌شوند.
- تصویر JPEG کم‌وضوح و cropشده معیار هندسی تقریبی است؛ PNGها معیار مستقیم 1:1 برای پوسته و مودال‌اند.

کالیبراسیون و نواحی annotation در [calibration.json](ui-reference/calibration.json) ثبت شده‌اند.
شواهد اجرا و مقایسه در `artifacts/ui-reference/`؛ نتیجهٔ بررسی در `design-qa.md` ثبت می‌شود.

## بررسی نهایی

- Web build: موفق؛ ۱۲ هشدار موجود در baseline، بدون خطا.
- ۱۸۲ آزمون موجود مرتبط با پوسته، پرداخت، تخصیص دریافت، احراز هویت و mapping حساب‌ها: موفق.
- بررسی مرورگر Chromium با DPR=1 و zoom=100% در 1366×768، 1440×900 و 390×844، به‌علاوهٔ viewportهای کالیبرهٔ هر مرجع انجام شد.
- ۱۴ بررسی رفتاری شامل Escape، focus trap، بازگشت focus، انتخاب/لغو/ذخیرهٔ ابزار، disclosure رمز، دو روش پرداخت، ارسال صحیح فیلدها در زیرجریان بل و ناوبری SPA موفق شد.
- هر ۸۸ `asp-for` و هر ۱۰۷ data-hook فرم Payments نسبت به HEAD حفظ شده است. فایل backend تغییر نکرد.
- حساب‌ها، فروش نمونه و کاربران برای مرورگر فقط در دیتابیس مجزای `mashal_ui_test` ساخته شدند. migration اجرا نشد و هیچ اتصال به دیتابیس اصلی وجود ندارد.
- در دریافتِ مرتبط با بل، کنترل‌های اصلی بدون تغییر name/value/validation بازچینی می‌شوند؛ فیلد نوع طرف حساب در جزئیات همان fieldset می‌ماند تا در حالت صراف، ارسال‌نشدن فیلدهای نقد/بانک حفظ شود.
- برای ثبت حالت «فرم جدید»، اعلان ادامهٔ پیش‌نویس از طریق دکمهٔ واقعی خودش بسته می‌شود؛ پیش‌نویس حذف یا پنهان‌سازی CSS نمی‌شود.
- Chromium آزمایشی، گواهی self-signed محلی را می‌پذیرد؛ تنظیم TLS برنامه تغییر نکرده است.

فرمان‌های بازتولید (در محیط دارای .NET، Python Playwright و برنامهٔ درحال اجرا):

```sh
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --no-restore
node --check src/PTGOilSystem.Web/wwwroot/js/mashal-reference.js
python scripts/ui-reference/capture.py --base-url https://127.0.0.1:5001 --storage-state /path/to/local-auth.json --company-id 1 --sale-id 1
python scripts/ui-reference/compare.py --references /path/to/ui-references
dotnet msbuild src/PTGOilSystem.Web/PTGOilSystem.Web.csproj -target:BuildCssBundles -p:Configuration=Debug
```

فایل storage-state حاوی نشست ورود است؛ در working tree و بستهٔ تحویل قرار نمی‌گیرد.
