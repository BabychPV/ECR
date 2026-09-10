<#
.SYNOPSIS
    Публікує застосунок і збирає MSI. Один виклик, без ручних кроків.
.EXAMPLE
    .\tools\build-msi.ps1 -Version 1.0.0
.NOTES
    Не входить у `dotnet build Ecr.sln`: WiX не має нативної збірки під
    Linux, а CI цього репозиторію (`.github/workflows/ci.yml`) виконується
    ЛИШЕ на ubuntu-latest. Цей скрипт — окрема, свідомо відокремлена точка
    входу «одна команда → .msi»; запускати на Windows-машині збірки
    (docs/build/10-installer.md, розділ про відхилення від первинного тексту).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SkipPublish,
    [switch] $SkipWeb
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ⛔ PowerShell 7.3+: $PSNativeCommandUseErrorActionPreference за
# замовчуванням $true — будь-який запис нативної команди в stderr (навіть
# звичайне попередження, не помилку) підпадає під $ErrorActionPreference і
# зупиняє скрипт ДО того, як власна перевірка $LASTEXITCODE нижче встигає
# спрацювати. Реальний прогін упав саме тут: "npm warn deprecated ..." від
# `npm ci` (не помилка, код виходу 0) зупинив збірку як "NativeCommandError".
# Вимкнено навмисно: єдине джерело істини про успіх нативного виклику в
# цьому скрипті — явний $LASTEXITCODE, як і скрізь нижче.
$PSNativeCommandUseErrorActionPreference = $false

$root       = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$msiDir     = Join-Path $root 'artifacts\msi'
$wixproj    = Join-Path $root 'installer\Ecr.Installer\Ecr.Installer.wixproj'
$clientDir  = Join-Path $root 'src\Ecr.Web'

# ── 0. Інструменти на місці? ──────────────────────────────────────────────
# Перевірка ПЕРЕД довгою публікацією: інакше про відсутність wix/npm
# дізнаємося через кілька хвилин, коли публікація чи збірка клієнта вже
# пройшла.
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    throw "WiX CLI не знайдено. Встановити: dotnet tool install --global wix --version 5.0.2"
}
Write-Host "WiX: $(wix --version)" -ForegroundColor Cyan

if (-not $SkipWeb -and -not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "npm не знайдено -- потрібен для збірки src/Ecr.Web (Q-214). Пропустити: -SkipWeb (MSI вийде без вебки)."
}

# ── 1. Публікація ─────────────────────────────────────────────────────────
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # Self-contained: рантайм усередині, залежності від системи немає
    # (docs/build/10-installer.md §1.2). ReadyToRun скорочує холодний старт
    # служби; для служби це важливо, бо перший запит після рестарту сервера
    # інакше чекає JIT.
    dotnet publish (Join-Path $root 'src\Ecr.Api\Ecr.Api.csproj') `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishReadyToRun=true `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE) { throw "dotnet publish завершився з кодом $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $publishDir 'Ecr.Api.exe'))) {
    throw "У публікації немає Ecr.Api.exe — перевір, чи це проєкт служби."
}

# ── 1b. Веб-клієнт (Q-214) ───────────────────────────────────────────────
# Кладеться в $publishDir\wwwroot, а не окремим ComponentGroup у .wxs:
# `Folders.wxs`/`AppFiles` уже забирає ВЕСЬ $(PublishDir)\** одним `Files
# Include` (коментар там від самого початку згадував "статику SPA" —
# механізм на це чекав, просто нічого туди не клалось). `Program.cs`
# (`UseStaticFiles`/`MapFallbackToFile`) читає САМЕ wwwroot поруч із
# Ecr.Api.exe за замовчуванням ASP.NET Core — жодного додаткового
# налаштування шляху не треба.
if (-not $SkipWeb) {
    Write-Host ""
    Write-Host "Збірка веб-клієнта (src/Ecr.Web)..." -ForegroundColor Cyan

    Push-Location $clientDir
    try {
        # ⛔ npm.cmd, НЕ npm(.ps1): власний npm.ps1 (Node.js для Windows)
        # звертається до $MyInvocation.Statement/.PipelineElements, яких
        # немає під Set-StrictMode -Version Latest цього скрипта —
        # "The property 'Statement' cannot be found on this object" — не
        # наша помилка, а несумісність самого npm.ps1 зі строгим режимом.
        # npm.cmd — той самий npm, інший вхідний файл, без цього коду.
        #
        # npm ci, не install: відтворюваність з package-lock.json, той самий
        # принцип, що self-contained публікація для .NET-частини.
        & npm.cmd ci
        if ($LASTEXITCODE) { throw "npm ci завершився з кодом $LASTEXITCODE" }

        & npm.cmd run build
        if ($LASTEXITCODE) { throw "npm run build завершився з кодом $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }

    $clientDist = Join-Path $clientDir 'dist'
    if (-not (Test-Path (Join-Path $clientDist 'index.html'))) {
        throw "У $clientDist немає index.html — vite build нічого не зібрав, перевір вивід вище."
    }

    $wwwroot = Join-Path $publishDir 'wwwroot'
    if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
    Copy-Item $clientDist $wwwroot -Recurse
    Write-Host "Веб-клієнт: $wwwroot" -ForegroundColor Green
}
elseif (-not (Test-Path (Join-Path $publishDir 'wwwroot\index.html'))) {
    Write-Warning "-SkipWeb: MSI збереться БЕЗ веб-клієнта (немає index.html у wwwroot)."
}

# ── 2. Збірка MSI ─────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $msiDir | Out-Null

dotnet build $wixproj `
    -c $Configuration `
    -p:VersionPrefix=$Version `
    -p:PublishDir=$publishDir `
    -p:OutputPath=$msiDir
if ($LASTEXITCODE) { throw "збірка MSI завершилася з кодом $LASTEXITCODE" }

# -Recurse: WiX кладе фінальний .msi в підтеку культури (en-US тощо), не
# прямо в $msiDir.
$msi = Get-ChildItem $msiDir -Filter '*.msi' -Recurse | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $msi) { throw "MSI не створено" }

# ── 3. Перевірка артефакту ────────────────────────────────────────────────
# Мінімальна, але саме та, що ловить типові помилки пакування:
# невірна версія (три поля!) і пропущений UpgradeCode.
$expected = "$Version"
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msi.FullName, 0)
function Get-MsiProperty([string] $name) {
    $v = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$name'")
    $v.Execute(); $r = $v.Fetch()
    if ($r) { $r.StringData(1) } else { $null }
}
$actualVersion = Get-MsiProperty 'ProductVersion'
$upgradeCode   = Get-MsiProperty 'UpgradeCode'

if ($actualVersion -ne $expected) {
    throw "ProductVersion у MSI = '$actualVersion', очікували '$expected'. " +
          "MSI порівнює версії лише за трьома полями — четверте поле не використовувати."
}
if (-not $upgradeCode) { throw "UpgradeCode відсутній: оновлення не працюватиме" }

$hash = (Get-FileHash $msi.FullName -Algorithm SHA256).Hash.ToLower()

Write-Host ""
Write-Host "MSI:      $($msi.FullName)" -ForegroundColor Green
Write-Host "Версія:   $actualVersion"
Write-Host "Розмір:   $('{0:N1}' -f ($msi.Length/1MB)) MB"
Write-Host "SHA-256:  $hash"
Write-Host ""
Write-Host "Тиха установка:"
Write-Host "  msiexec /i `"$($msi.Name)`" /qn /l*v install.log SERVICE_ACCOUNT=DOMAIN\ecr-svc$"
