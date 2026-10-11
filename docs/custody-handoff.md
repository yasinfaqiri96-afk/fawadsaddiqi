# Handoff توسعه امانی

مبنا، وضعیت هر مرحله و نتایج واقعی در [custody-completion-progress.md](custody-completion-progress.md) ثبت می‌شود.

- شاخه: `feature/custody-final-completion`؛ مبنا `dd8c0fc`.
- محیط توسعه: .NET SDK در `/workspace/.dotnet`، PostgreSQL تست در container `mashal-custody-postgres`، فقط localhost.
- Build: `dotnet build tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj --no-restore`؛ سپس تست با `--no-build --no-restore`.
- PostgreSQL fixtureها دیتابیس موقت با پیشوند محافظت‌شده می‌سازند و حذف می‌کنند. به Production وصل نکنید.
- ابتدا AGENTS.md را بخوانید. ادامه از Commitهای همان شاخه، بدون Reset/Force push.
- اسناد امانی نباید دارایی یا درآمد فروش ملکی را زیاد کنند. خرید قطعی باید خروج امانی و ورود ملکی یک‌بار و اتمیک داشته باشد، بدون تهاتر خودکار.
- داده تاریخی، FX، چگالی، هویت طرف‌ها و شروط قرض براساس اطلاعات حدسی ساخته نشوند.
- این فایل سند اتمام نیست؛ موارد باقی‌مانده در Progress صریح نگهداری می‌شوند.
- کد بررسی‌شده: `0d661c3` و `8c7dda5`؛ هر دو Push شده‌اند. [PR پیش‌نویس #3](https://github.com/yasinfaqiri96-afk/oil/pull/3) بدون Merge برای بررسی موجود است.
- نتیجه نهایی این اجرا: Build موفق؛ 128 تست هدفمند موفق؛ Suite کامل 4130 موفق، 18 ناموفق، صفر Skipped. همه شکست‌ها نبود `Payment.xlsx` در `PartnerSettlementImportTests` است. فایل مرجع مجاز را فقط در مسیر مورد انتظار fixture فراهم کنید و Suite را دوباره اجرا کنید؛ فایل واقعی/خصوصی را Commit یا برای سبزکردن تست جعل نکنید.
- EF Pending-model و ارتقای بکاپ ساختگی پیش از امانی موفق شدند؛ Migration تازه وجود ندارد. E2E مرورگر و تکمیل سایر قابلیت‌ها هنوز تأیید نشده‌اند.
- ادامه پس از fb536f4: تبدیل خرید USD/MT و پیوند با فروشنده موجود اضافه شده؛ Migration جدید و حدود تأیید در [custody-conversion-testing.md](custody-conversion-testing.md). ادامه بعدی از head همین شاخه باشد؛ ارقام Suite کامل قدیمی مربوط به قبل این Migration هستند.
- ورود لیتر مخزن/رسید، با چگالی و دمای مستند و بدون بازنویسی داده قبلی، اضافه شده است؛ [custody-measurement-testing.md](custody-measurement-testing.md). سایر عملیات و بارگیری اولیه در راه هنوز MT هستند.
