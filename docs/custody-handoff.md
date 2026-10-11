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
