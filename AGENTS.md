# Repository Guidelines

## دامنه و معماری

- با کاربر فارسی/دری صحبت کن مگر انگلیسی بخواهد؛ پاسخ نهایی کوتاه و عملی باشد.
- برنامهٔ اصلی `src/PTGOilSystem.Web/` است: ASP.NET Core MVC/.NET 8، EF Core/PostgreSQL، Razor و Bootstrap RTL. تست‌ها در `tests/PTGOilSystem.Web.Tests/` با xUnit هستند. مسیر معمول `Controller → Service → ApplicationDbContext` است؛ منطق چندمرحله‌ای، مالی و Ledger در Service بماند. UI از `ak-*` و `docs/UI-DESIGN-SYSTEM.md` پیروی کند.
- مالکیت compilation: `Web → Migrations → Persistence` و `Web → Persistence`. migrationها در `src/PTGOilSystem.Migrations/Migrations/` هستند؛ Entity/DbContext از مسیر و namespace قبلی با linked source کامپایل می‌شوند. build روزمره فقط Web با project references؛ `Rebuild` و `BuildProjectReferences=false` ممنوع. EF همیشه با `--project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj` و پس از build تازه با `--no-build`. جزئیات: `docs/migrations-project.md`.

## کار کم‌زمینه

- با `rg` فایل/متد مرتبط را مستقیم پیدا کن. قاعده: `inspect once → batch edits → validate once`. پس از root cause، exploration را متوقف و implementation را شروع کن.
- برای کار عادی کل repo، generated files، `bin/obj` یا migrationها را نخوان. در فایل بزرگ فقط symbol/text و محدودهٔ لازم را بخوان؛ فایل بی‌تغییر را بی‌دلیل دوباره نخوان.
- پس از هر edit/build/test/status بررسی را تکرار نکن؛ batch کن. تکرار فقط پس از تغییر تازه، شکست check یا نگرانی مشخص.
- Graphify فقط با درخواست صریح، تغییر معماری/روابط مهم dependency، یا یک بار در پایان feature بزرگ. برای fix کوچک، UI، CSS/JS/View، Controller کوچک، متن یا rerun تست اجرا نشود.
- برای کار ساده subagent، research اضافی یا plan طولانی نساز. دو agent/ابزار هم‌زمان روی checkout و فایل مشترک تغییر ندهند؛ برای کار موازی worktree/checkout جدا لازم است، نه فقط branch جدا.

## مرزهای ایمنی

- بدون درخواست صریح Entity، DbContext، Migration/schema، `StockService`، `PricingService`، Ledger/P&L، Payment/CashAccount، InventoryMovement/Allocation یا محاسبات پول، وزن، نرخ و FX را تغییر نده؛ اگر لازم شد اثر را بگو و اجازه بگیر.
- موجودی فقط از `InventoryMovement` و `StockService` می‌آید. `DirectSale` حرکت جعلی نمی‌سازد؛ `DirectDispatchFromReceipt` نباید StockService را صدا بزند. `ContractJourney` فقط read-only/navigation است و stock، ledger یا payment نمی‌سازد.
- همهٔ POST formها `@Html.AntiForgeryToken()` صریح داشته باشند. فیلد backend را برای ساده‌سازی UI حذف/مخفی نکن؛ فیلد کم‌استفاده را به Advanced ببر.

## اجرا و validation

```powershell
.\scripts\run-local.ps1                  # http://localhost:5000؛ migration خاموش
.\scripts\run-local.ps1 -Watch
.\scripts\run-local.ps1 -ApplyMigrations # فقط با درخواست صریح
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --no-restore
.\scripts\dev-verify.ps1 -Web -Filter 'FullyQualifiedName~ClassName.MethodName'
.\scripts\dev-verify.ps1 -Ui -Paths src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css
.\scripts\dev-verify.ps1 -Full
dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~ClassName.MethodName"
.\scripts\test-fast.ps1
.\scripts\test-full.ps1
.\scripts\test-accounting.ps1
```

- startup به PostgreSQL واقعی نیاز دارد؛ InMemory fallback نیست. اجرای مستقیم app پیش‌فرض migration را اجرا می‌کند؛ برای local از runner استفاده کن. Accounting tests دیتابیس موقت ساخته و با `DROP DATABASE ... FORCE` حذف می‌کنند؛ فقط محیط تست.
- `npm test` عمداً fail می‌شود؛ تست واقعی .NET/scripts است. پس از restore اولیه همیشه `--no-restore`؛ restore فقط پس از تغییر dependency/project/import، نبود assets یا خطای assets. اسکریپت‌ها restore پنهان ندارند.
- CSS/JS/متن خالص: `dev-verify.ps1 -Ui -Paths ...`. Razor: یک Web build. Controller/ViewModel: تست همان کلاس؛ Service: تست همان Service؛ UI: تست ساختاری مرتبط اگر موجود. قرارداد تست را ضعیف نکن.
- `dev-verify.ps1 -Web -Filter ...` Web و پروژهٔ تست را یک بار در همان graph می‌سازد؛ تست‌های بعدی `--no-build --no-restore`. Web build تنها، تازگی DLL تست را تضمین نمی‌کند.
- full test فقط accounting/inventory/ledger/migration/cross-cutting/release. solution build فقط shared project، Entity/Migration architecture یا release؛ برای Web معمول ممنوع.
- Entity/DbContext/Migration: validation کامل‌تر و EF pending-model check با `--no-build` پس از build تازه؛ check مجوز apply migration نیست.
- Debug خارج Development برای bundle به `-p:PtgBuildBundles=true` نیاز دارد؛ Release/Publish bundle می‌سازند. جزئیات: `docs/development-performance.md`.

## قرارداد فایل

- `.editorconfig`: UTF-8، LF، final newline، بدون trailing whitespace.
