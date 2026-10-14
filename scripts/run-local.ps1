param(
    [string]$Database = "ptg_oil_system",
    [string]$DbHost = "localhost",
    [int]$DbPort = 5432,
    [string]$DbUsername = "postgres",
    [string]$DbPassword = $env:PTG_LOCAL_DB_PASSWORD,
    [string]$BootstrapAdminUsername = $(if ($env:PTG_BOOTSTRAP_ADMIN_USERNAME) { $env:PTG_BOOTSTRAP_ADMIN_USERNAME } else { "admin" }),
    [string]$BootstrapAdminFullName = $(if ($env:PTG_BOOTSTRAP_ADMIN_FULLNAME) { $env:PTG_BOOTSTRAP_ADMIN_FULLNAME } else { "System Administrator" }),
    [string]$BootstrapAdminPassword = $env:PTG_BOOTSTRAP_ADMIN_PASSWORD,
    [switch]$ApplyMigrations,
    [switch]$MigrateOnly,
    [switch]$Watch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$secretStorePath = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "PTGOilSystem\local-run-secrets.json"

function Convert-LocalSecureStringToPlainText {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.SecureString]$SecureValue
    )

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

function Read-LocalSecret {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prompt
    )

    $secureValue = Read-Host $Prompt -AsSecureString
    return Convert-LocalSecureStringToPlainText -SecureValue $secureValue
}

function Protect-LocalSecret {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $secureValue = ConvertTo-SecureString -String $Value -AsPlainText -Force
    return ConvertFrom-SecureString -SecureString $secureValue
}

function Unprotect-LocalSecret {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $secureValue = ConvertTo-SecureString -String $Value
    return Convert-LocalSecureStringToPlainText -SecureValue $secureValue
}

function Get-LocalSecretStore {
    if (-not (Test-Path -LiteralPath $secretStorePath)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $secretStorePath -Raw | ConvertFrom-Json
    }
    catch {
        Write-Warning "Stored local secrets could not be read and will be ignored."
        return $null
    }
}

function Save-LocalSecretStore {
    param(
        [AllowEmptyString()]
        [string]$DbPasswordValue,

        [AllowEmptyString()]
        [string]$BootstrapAdminPasswordValue
    )

    $storeDirectory = Split-Path -Path $secretStorePath -Parent
    if (-not (Test-Path -LiteralPath $storeDirectory)) {
        New-Item -ItemType Directory -Path $storeDirectory -Force | Out-Null
    }

    $store = [ordered]@{
        DbPassword             = $(if (-not [string]::IsNullOrWhiteSpace($DbPasswordValue)) { Protect-LocalSecret -Value $DbPasswordValue } else { $null })
        BootstrapAdminPassword = $(if (-not [string]::IsNullOrWhiteSpace($BootstrapAdminPasswordValue)) { Protect-LocalSecret -Value $BootstrapAdminPasswordValue } else { $null })
    }

    $store | ConvertTo-Json | Set-Content -LiteralPath $secretStorePath -Encoding UTF8
}

function Get-LocalEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($scope in @("Process", "User", "Machine")) {
        $value = [Environment]::GetEnvironmentVariable($Name, $scope)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $null
}

function Set-LocalRunnerEnvironment {
    # --- سرعت راه‌اندازی: حذف تله‌متری/لوگو/first-run و روشن نگه‌داشتن tiered JIT ---
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:DOTNET_TieredCompilation = "1"
    $env:DOTNET_TieredPGO = "0"
    $env:DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER = "1"

    $env:ASPNETCORE_ENVIRONMENT = $(if ($Watch) { "Development" } else { "Production" })
    $env:ASPNETCORE_URLS = "http://localhost:5000"
    $env:ConnectionStrings__DefaultConnection = "Host=$DbHost;Port=$DbPort;Username=$DbUsername;Password=$DbPassword;Database=$Database;SSL Mode=Prefer;Trust Server Certificate=true"
    # Database updates are explicit through -ApplyMigrations/-MigrateOnly.
    # Normal and watch startup must never repeat migration discovery implicitly.
    $env:PTG_AUTO_MIGRATE = "false"
    # Trusted-device (offline PWA) session key: same DPAPI-protected per-user key as run-pwa-local.ps1.
    # Without it a Production run cannot issue device sessions (/api/sync/v1/device-session returns 503).
    $jwtKeyFile = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "PTGOilSystem\pwa-local-jwt.key"
    if (-not (Test-Path -LiteralPath $jwtKeyFile)) {
        New-Item -ItemType Directory -Path (Split-Path $jwtKeyFile -Parent) -Force | Out-Null
        $keyBytes = New-Object byte[] 48
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($keyBytes)
        ConvertTo-SecureString -String ([Convert]::ToBase64String($keyBytes)) -AsPlainText -Force |
            ConvertFrom-SecureString | Set-Content -LiteralPath $jwtKeyFile -Encoding ASCII
    }
    $env:PTG_MOBILE_JWT_SIGNING_KEY = Unprotect-LocalSecret -Value (Get-Content -LiteralPath $jwtKeyFile -Raw).Trim()
    $env:PTG_BOOTSTRAP_ADMIN_USERNAME = $BootstrapAdminUsername
    $env:PTG_BOOTSTRAP_ADMIN_FULLNAME = $BootstrapAdminFullName

    if ([string]::IsNullOrWhiteSpace($BootstrapAdminPassword)) {
        Remove-Item Env:PTG_BOOTSTRAP_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    }
    else {
        $env:PTG_BOOTSTRAP_ADMIN_PASSWORD = $BootstrapAdminPassword
    }
}

function Invoke-LocalEfDatabaseUpdate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $migrationsProject = Join-Path (Split-Path (Split-Path $ProjectPath -Parent) -Parent) "PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj"
    $commandOutput = & dotnet ef database update --project $migrationsProject --startup-project $ProjectPath 2>&1
    $exitCode = $LASTEXITCODE

    if ($null -ne $commandOutput) {
        $commandOutput | ForEach-Object { Write-Host $_ }
    }

    return [pscustomobject]@{
        ExitCode   = $exitCode
        OutputText = ($commandOutput | Out-String)
    }
}

function Test-LocalBuildRequired {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectDirectory,

        [Parameter(Mandatory = $true)]
        [string]$OutputDllPath
    )

    if (-not (Test-Path -LiteralPath $OutputDllPath)) {
        return $true
    }

    # The runtime output must include both new assemblies, including on a
    # checkout with old Web output from before the project split.
    foreach ($assemblyName in @('PTGOilSystem.Persistence.dll', 'PTGOilSystem.Migrations.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $OutputDllPath -Parent) $assemblyName))) {
            return $true
        }
    }

    $outputTime = (Get-Item -LiteralPath $OutputDllPath).LastWriteTimeUtc
    $sourceDirectories = @($ProjectDirectory,
        (Join-Path (Split-Path $ProjectDirectory -Parent) 'PTGOilSystem.Persistence'),
        (Join-Path (Split-Path $ProjectDirectory -Parent) 'PTGOilSystem.Migrations'))
    $buildInputs = Get-ChildItem -LiteralPath $sourceDirectories -Recurse -File |
        Where-Object {
            $isSourceFile = $_.Extension -in @('.cs', '.cshtml', '.csproj', '.props', '.targets')
            $isGeneratedFile = $_.FullName -match '[\\/](bin|obj)[\\/]'
            $isSourceFile -and -not $isGeneratedFile
        }

    foreach ($inputFile in $buildInputs) {
        if ($inputFile.LastWriteTimeUtc -gt $outputTime) {
            return $true
        }
    }

    return $false
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src/PTGOilSystem.Web/PTGOilSystem.Web.csproj"
$projectDirectory = Split-Path $projectPath -Parent
$outputDllPath = Join-Path $projectDirectory "bin/Debug/net8.0/PTGOilSystem.Web.dll"
$dbPasswordSource = "parameter"

if ($MigrateOnly) {
    $ApplyMigrations = $true
}

$storedSecrets = Get-LocalSecretStore

if ([string]::IsNullOrWhiteSpace($DbPassword)) {
    $DbPassword = Get-LocalEnvironmentValue -Name "PTG_LOCAL_DB_PASSWORD"
    if (-not [string]::IsNullOrWhiteSpace($DbPassword)) {
        $dbPasswordSource = "environment"
    }
}

if ([string]::IsNullOrWhiteSpace($DbPassword) -and $null -ne $storedSecrets -and -not [string]::IsNullOrWhiteSpace($storedSecrets.DbPassword)) {
    $DbPassword = Unprotect-LocalSecret -Value $storedSecrets.DbPassword
    $dbPasswordSource = "cache"
}

if ([string]::IsNullOrWhiteSpace($DbPassword)) {
    $DbPassword = Read-LocalSecret -Prompt "PostgreSQL password for $DbUsername@$DbHost"
    $dbPasswordSource = "prompt"
}

if ([string]::IsNullOrWhiteSpace($BootstrapAdminPassword)) {
    $BootstrapAdminPassword = Get-LocalEnvironmentValue -Name "PTG_BOOTSTRAP_ADMIN_PASSWORD"
}

if ([string]::IsNullOrWhiteSpace($BootstrapAdminPassword) -and $null -ne $storedSecrets -and $null -ne $storedSecrets.BootstrapAdminPassword) {
    $BootstrapAdminPassword = Unprotect-LocalSecret -Value $storedSecrets.BootstrapAdminPassword
}

Save-LocalSecretStore -DbPasswordValue $DbPassword -BootstrapAdminPasswordValue $BootstrapAdminPassword

Set-LocalRunnerEnvironment

Write-Host "PTG Oil System local run"
Write-Host "Environment: $env:ASPNETCORE_ENVIRONMENT"
Write-Host "Database: $DbHost`:$DbPort/$Database"
Write-Host "Database user: $DbUsername"
Write-Host "Bootstrap admin username: $BootstrapAdminUsername"
Write-Host "Project: $projectPath"

Push-Location $repoRoot
try {
    # restore فقط وقتی lock موجود نیست؛ بعد از آن --no-restore راه‌اندازی را سریع می‌کند
    $projectDirectories = @($projectDirectory,
        (Join-Path (Split-Path $projectDirectory -Parent) 'PTGOilSystem.Persistence'),
        (Join-Path (Split-Path $projectDirectory -Parent) 'PTGOilSystem.Migrations'))
    $missingAssets = @($projectDirectories | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $_ 'obj/project.assets.json'))
    })
    if ($missingAssets.Count -gt 0) {
        Write-Host "First run: restoring NuGet packages once..."
        & dotnet restore $projectPath
    }

    if ($ApplyMigrations) {
        Write-Host "Applying EF Core migrations..."
        $migrationResult = Invoke-LocalEfDatabaseUpdate -ProjectPath $projectPath
        if ($migrationResult.ExitCode -ne 0 -and $migrationResult.OutputText -match "password authentication failed|No password has been provided") {
            Write-Warning "The stored PostgreSQL password was rejected. Enter the current password once to refresh the local cache."
            $DbPassword = Read-LocalSecret -Prompt "PostgreSQL password for $DbUsername@$DbHost"
            $dbPasswordSource = "prompt"
            Save-LocalSecretStore -DbPasswordValue $DbPassword -BootstrapAdminPasswordValue $BootstrapAdminPassword
            Set-LocalRunnerEnvironment
            Write-Host "Retrying EF Core migrations with updated local password..."
            $migrationResult = Invoke-LocalEfDatabaseUpdate -ProjectPath $projectPath
        }

        if ($migrationResult.ExitCode -ne 0) {
            throw "dotnet ef database update failed with exit code $($migrationResult.ExitCode)."
        }

        if ($MigrateOnly) {
            Write-Host "[OK] Database connection and migrations are working."
            exit 0
        }
    }

    if ($Watch) {
        $env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = "1"
        Write-Host "Starting application in WATCH mode (auto reload on file changes)..."
        Write-Host "URL: $env:ASPNETCORE_URLS  -  Press Ctrl+C to stop."
        # --no-restore: از restore تکراری NuGet در هر راه‌اندازی صرف‌نظر کن (وقتی dependency عوض نشده)
        & dotnet watch --project $projectPath --non-interactive run --no-restore
    }
    else {
        if (Test-LocalBuildRequired -ProjectDirectory $projectDirectory -OutputDllPath $outputDllPath) {
            Write-Host "Application sources changed; running one safe incremental build..."
            & dotnet build $projectPath --no-restore -p:UseAppHost=false -p:UseSharedCompilation=false -p:DebugType=none -p:DebugSymbols=false -m:1
            if ($LASTEXITCODE -ne 0) {
                throw "Web project build failed with exit code $LASTEXITCODE."
            }
        }
        else {
            Write-Host "Application build is current; skipping compilation."
        }

        if (-not (Test-Path -LiteralPath $outputDllPath)) {
            throw "Web application output was not found: $outputDllPath"
        }

        Write-Host "Starting application without an implicit rebuild..."
        Write-Host "URL: $env:ASPNETCORE_URLS  -  Press Ctrl+C to stop."
        Push-Location $projectDirectory
        try {
            & dotnet $outputDllPath
        }
        finally {
            Pop-Location
        }
    }
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
