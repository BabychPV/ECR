<#
.SYNOPSIS
    Один виклик → ОДИН файл: самодостатній `Ecr-Setup-<версія>.exe`.

.DESCRIPTION
    Директива Q-219 (людина): не "розкладати 10 файлів по теці на
    сервері", а віддати РІВНО один файл. Раніше майстер `EcrSetup.exe`
    (Q-216) все одно вимагав поруч `deploy-ecr.ps1`, `Ecr.msi`, а сам
    `deploy-ecr.ps1` (без -SkipSchema) додатково тягнув `src/Ecr.
    Infrastructure` і `dotnet-ef`/.NET SDK з дерева репозиторію —
    тобто "чистий сервер" насправді мусив мати клон репозиторію. Це і
    є той розрив, що знайшовся при відповіді на "які файли поруч".

    Цей скрипт:
      1. Збирає `Ecr.msi`     — build-msi.ps1 (як і раніше).
      2. Генерує міграцію     — та сама команда, що deploy-ecr.ps1 сам
                                 викликав би на чистому сервері, лише
                                 ТУТ, на машині збірки, де dotnet-ef і
                                 SDK вже є за визначенням.
      3. Готує payload        — Ecr.msi, deploy-ecr.ps1, migration.sql,
                                 sql\*.sql в artifacts\wizard-payload\
                                 (Ecr.Setup.csproj підхоплює це як
                                 Content — див. коментар там).
      4. Публікує EcrSetup    — self-contained, single-file, win-x64,
                                 -p:IncludeAllContentForSelfExtract=true:
                                 .NET сам вбудовує payload У СЕРЕДИНУ
                                 .exe і розпаковує його в тимчасову теку
                                 при кожному запуску — код майстра
                                 (ResolveScriptPath/TryDetectMsi) цього
                                 навіть не помічає, бо й так читає
                                 AppContext.BaseDirectory.
      5. Копіює результат     — artifacts\installer\Ecr-Setup-<версія>.exe.

    deploy-ecr.ps1, коли запущений ІЗ ЦЬОГО пакета, сам бачить sql\ і
    migration.sql поруч із собою і НЕ викликає dotnet ef — тому на
    сервері, де запускається Ecr-Setup-*.exe, не потрібні ні .NET SDK,
    ні dotnet-ef, ні клон репозиторію. Потрібен лише .NET Desktop
    Runtime? Ні — self-contained публікація не вимагає навіть цього.

.PARAMETER Version
    Версія продукту (три поля, як і в build-msi.ps1/rebuild-and-package-msi.ps1).

.EXAMPLE
    .\tools\build-installer.ps1 -Version 1.0.0

.NOTES
    Як і build-msi.ps1: Windows-машина збірки (WiX, npm, dotnet-ef —
    саме там, не на сервері). Не входить у dotnet build Ecr.sln.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ⛔ Q-217/Q-218: PowerShell перетворює запис нативної команди в stderr на
# помилку, і $ErrorActionPreference = 'Stop' зупиняє скрипт на ній
# незалежно від фактичного коду виходу. Той самий патерн, що в build-msi.ps1
# /rebuild-and-package-msi.ps1/deploy-ecr.ps1 — тимчасово послабити,
# перевірити $LASTEXITCODE явно.
function Invoke-NativeStep {
    param(
        [Parameter(Mandatory)] [string] $Description,
        [Parameter(Mandatory)] [scriptblock] $Command
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE) {
        throw "$Description завершився з кодом $LASTEXITCODE"
    }
}

$root       = Split-Path -Parent $PSScriptRoot
$buildMsi   = Join-Path $root 'tools\build-msi.ps1'
$sqlSrc     = Join-Path $root 'src\Ecr.Infrastructure\Persistence\Sql'
$infraProj  = Join-Path $root 'src\Ecr.Infrastructure'
$setupProj  = Join-Path $root 'tools\Ecr.Setup\Ecr.Setup.csproj'
$deployScript = Join-Path $root 'tools\deploy-ecr.ps1'

$payload      = Join-Path $root 'artifacts\wizard-payload'
$msiDir       = Join-Path $root 'artifacts\msi'
$setupPublish = Join-Path $root 'artifacts\setup-publish'
$outDir       = Join-Path $root 'artifacts\installer'

if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue) -and
    -not (dotnet tool list --global 2>$null | Select-String 'dotnet-ef')) {
    throw "dotnet-ef не знайдено (global tool). Встановити: dotnet tool install --global dotnet-ef --version <версія Microsoft.EntityFrameworkCore.Design з Directory.Packages.props>"
}

# ── 1. Ecr.msi ────────────────────────────────────────────────────────────
Write-Host "== Крок 1/4: Ecr.msi ($Version) ==" -ForegroundColor Cyan
& $buildMsi -Version $Version -Configuration $Configuration

$msi = Get-ChildItem $msiDir -Filter '*.msi' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $msi) { throw "build-msi.ps1 не залишив жодного .msi у $msiDir" }

# ── 2. Payload: Ecr.msi + deploy-ecr.ps1 + migration.sql + sql\*.sql ──────
Write-Host ""
Write-Host "== Крок 2/4: payload майстра ($payload) ==" -ForegroundColor Cyan

if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'sql') | Out-Null

Copy-Item $msi.FullName (Join-Path $payload 'Ecr.msi') -Force
Copy-Item $deployScript (Join-Path $payload 'deploy-ecr.ps1') -Force
Copy-Item (Join-Path $sqlSrc '*.sql') (Join-Path $payload 'sql') -Force

# ⛔ Та сама команда, яку deploy-ecr.ps1 сам викликав би на кроці 2/7,
# якби -SkipSchema не було задано — лише тут, на машині збірки, де
# dotnet-ef і SDK вже стоять за визначенням (build-msi.ps1 вимагає їх
# для публікації Ecr.Api). Мета Q-219: сервер, де запускається готовий
# .exe, не повинен мати НІ ТОГО, НІ ТОГО.
Invoke-NativeStep "dotnet ef migrations script" {
    dotnet ef migrations script --idempotent `
        --project $infraProj `
        --startup-project $infraProj `
        --output (Join-Path $payload 'migration.sql')
}

if (-not (Test-Path (Join-Path $payload 'migration.sql'))) {
    throw "dotnet ef не залишив migration.sql у payload"
}

$sqlCount = (Get-ChildItem (Join-Path $payload 'sql') -Filter '*.sql').Count
Write-Host "Payload: Ecr.msi, deploy-ecr.ps1, migration.sql, $sqlCount sql-файлів" -ForegroundColor Green

# ── 3. Публікація EcrSetup.exe: self-contained, single-file, payload вбудовано ──
Write-Host ""
Write-Host "== Крок 3/4: публікація EcrSetup.exe (self-contained, single-file) ==" -ForegroundColor Cyan

if (Test-Path $setupPublish) { Remove-Item $setupPublish -Recurse -Force }

Invoke-NativeStep "dotnet publish (Ecr.Setup)" {
    dotnet publish $setupProj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:Version=$Version `
        -o $setupPublish
}

$exe = Join-Path $setupPublish 'EcrSetup.exe'
if (-not (Test-Path $exe)) { throw "Публікація не залишила EcrSetup.exe у $setupPublish" }

# ── 4. Фінальний артефакт ──────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$final = Join-Path $outDir "Ecr-Setup-$Version.exe"
Copy-Item $exe $final -Force

$hash = (Get-FileHash $final -Algorithm SHA256).Hash.ToLower()
$sizeMb = '{0:N1}' -f ((Get-Item $final).Length / 1MB)

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
Write-Host "Інсталятор: $final"
Write-Host "Розмір:     $sizeMb MB"
Write-Host "SHA-256:    $hash"
Write-Host ""
Write-Host "На сервер копіюється РІВНО цей один файл. Жодного .NET SDK, Node," -ForegroundColor Cyan
Write-Host "dotnet-ef чи клону репозиторію на сервері не потрібно." -ForegroundColor Cyan
