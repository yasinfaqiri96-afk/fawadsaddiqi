<#
.SYNOPSIS
    مشعل آفلاین (PWA) را روی همین کمپیوتر برای نصب و آزمایش اجرا می‌کند — روی یک کپیِ جدای دیتابیس.

.DESCRIPTION
    - دیتابیس اصلی (ptg_oil_system) فقط با pg_dump خوانده می‌شود و هرگز تغییر نمی‌کند.
    - کپی آزمایشی: ptg_oil_accounting_test_pwa_local (پیشوند محافظت‌شدهٔ تست). Migrationها (از جمله
      AddOfflineSyncInbox) فقط روی همین کپی اجرا می‌شوند.
    - کلید امضای JWT نشست دستگاه یک‌بار ساخته و با DPAPI (فقط همین کاربر ویندوز) در
      %LOCALAPPDATA%\PTGOilSystem\pwa-local-jwt.key نگه داشته می‌شود تا «دستگاه مورداعتماد» پس از
      Restart سرور هم بدون ورود دوباره همگام شود.
    - آدرس: http://localhost:5400 (localhost برای Service Worker و نصب PWA «امن» حساب می‌شود).

.PARAMETER ResetData
    کپی آزمایشی را حذف و دوباره از دیتابیس اصلی می‌سازد (عملیات آزمایشیِ قبلی پاک می‌شود).

.PARAMETER Lan
    روی همهٔ کارت‌های شبکه گوش می‌دهد (برای دسترسی از موبایل در همان شبکه). بدون HTTPS،
    موبایل Service Worker را فعال نمی‌کند؛ راهنمای HTTPS در docs/offline-pwa.md آمده است.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\run-pwa-local.ps1
#>
param(
    [int]$Port = 5400,
    [switch]$ResetData,
    [switch]$Lan,
    [string]$SourceDatabase = "ptg_oil_system",
    [string]$PgBin = "C:\Program Files\PostgreSQL\18\bin",
    [string]$DbHost = "localhost",
    [int]$DbPort = 5432,
    [string]$DbUsername = "postgres"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$TestPrefix = "ptg_oil_accounting_test_"
$Database = "${TestPrefix}pwa_local"
$psql = Join-Path $PgBin "psql.exe"
$pgDump = Join-Path $PgBin "pg_dump.exe"
$pgRestore = Join-Path $PgBin "pg_restore.exe"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$webDir = Join-Path $repoRoot "src\PTGOilSystem.Web"
$dll = Join-Path $webDir "bin\Debug\net8.0\PTGOilSystem.Web.dll"

if (-not $Database.StartsWith($TestPrefix)) { throw "Refusing to use a non-test database." }
if ($Database -eq $SourceDatabase) { throw "Source and test database must differ." }

function Invoke-Psql([string]$Db, [string]$Sql) {
    $result = & $psql -h $DbHost -p $DbPort -U $DbUsername -w -d $Db -v ON_ERROR_STOP=1 -At -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $Sql" }
    return $result
}

# ---- کپی آزمایشی دیتابیس -------------------------------------------------------
$exists = (Invoke-Psql "postgres" "SELECT 1 FROM pg_database WHERE datname = '$Database'") -eq "1"
if ($ResetData -and $exists) {
    Write-Host "Dropping test copy $Database ..."
    Invoke-Psql "postgres" "DROP DATABASE IF EXISTS `"$Database`" WITH (FORCE)" | Out-Null
    $exists = $false
}

if (-not $exists) {
    $dumpFile = Join-Path ([IO.Path]::GetTempPath()) "mashal-pwa-local-source.dump"
    Write-Host "Reading $SourceDatabase (read-only pg_dump) ..."
    $env:PGOPTIONS = "-c default_transaction_read_only=on"
    & $pgDump -h $DbHost -p $DbPort -U $DbUsername -w -Fc -f $dumpFile $SourceDatabase
    $dumpExit = $LASTEXITCODE
    Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
    if ($dumpExit -ne 0) { throw "pg_dump failed." }

    Write-Host "Creating test copy $Database ..."
    Invoke-Psql "postgres" "CREATE DATABASE `"$Database`"" | Out-Null
    & $pgRestore -h $DbHost -p $DbPort -U $DbUsername -w -d $Database --no-owner --no-privileges $dumpFile
    if ($LASTEXITCODE -ne 0) { Write-Warning "pg_restore reported warnings (exit $LASTEXITCODE); continuing." }
    Remove-Item -LiteralPath $dumpFile -Force -ErrorAction SilentlyContinue
}

# ---- کلید پایدار JWT برای نشست دستگاه (DPAPI، فقط همین کاربر ویندوز) ----------
$keyDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "PTGOilSystem"
$keyFile = Join-Path $keyDir "pwa-local-jwt.key"
if (-not (Test-Path -LiteralPath $keyFile)) {
    New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
    $bytes = New-Object byte[] 48
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $plain = [Convert]::ToBase64String($bytes)
    ConvertTo-SecureString -String $plain -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $keyFile -Encoding ASCII
}
$secure = Get-Content -LiteralPath $keyFile | ConvertTo-SecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $env:PTG_MOBILE_JWT_SIGNING_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }

# ---- Build (فقط اگر خروجی نیست) ------------------------------------------------
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Host "Building Web ..."
    & dotnet build (Join-Path $webDir "PTGOilSystem.Web.csproj") --no-restore -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

$bind = if ($Lan) { "http://0.0.0.0:$Port" } else { "http://localhost:$Port" }
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $bind
$env:ConnectionStrings__DefaultConnection = "Host=$DbHost;Port=$DbPort;Username=$DbUsername;Database=$Database"
Remove-Item Env:DATABASE_URL -ErrorAction SilentlyContinue
$env:PTG_AUTO_MIGRATE = "true"          # فقط همین کپی آزمایشی
$env:PTG_DEV_AUTO_SIGNIN = "false"
$env:PTG_UI_PICK = "0"
$env:Backup__SchedulerEnabled = "false"

Write-Host ""
Write-Host "Mashal PWA (test copy: $Database)"
Write-Host "Open:     http://localhost:$Port"
Write-Host "Offline:  http://localhost:$Port/offline/index.html"
Write-Host "Stop:     close this window (Ctrl+C)"
Write-Host ""
Push-Location $webDir
try { & dotnet exec $dll --contentRoot $webDir }
finally { Pop-Location }
