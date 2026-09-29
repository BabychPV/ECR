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
    [switch] $SkipWeb,
    # Лише публікація (Api + Worker + веб) без збірки MSI: перевірити злиття
    # двох публікацій на машині без WiX. WiX тоді не вимагається.
    [switch] $PublishOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root       = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$msiDir     = Join-Path $root 'artifacts\msi'
$wixproj    = Join-Path $root 'installer\Ecr.Installer\Ecr.Installer.wixproj'
$clientDir  = Join-Path $root 'src\Ecr.Web'

# ⛔ Q-217 (реальний прогін): PowerShell перетворює КОЖЕН запис нативної
# команди в stderr на запис у потоці помилок — і $ErrorActionPreference =
# 'Stop' зупиняє скрипт на цьому записі, незалежно від коду виходу.
# "npm warn deprecated ..." (звичайне попередження, код виходу 0) зупинив
# збірку саме так, ДО власної перевірки $LASTEXITCODE. Це не про
# $PSNativeCommandUseErrorActionPreference (та змінна за замовчуванням і
# так $false — попередня версія цього фікса міняла її на те саме
# значення, тобто не робила нічого). Єдине надійне джерело істини —
# фактичний код виходу; ця функція послаблює ErrorActionPreference РІВНО
# на час виклику й перевіряє $LASTEXITCODE явно, а не покладається на те,
# як PowerShell трактує stderr.
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

# Версія збірки .NET або $null (нативний файл, не збірка).
function Get-AssemblyVersionOrNull {
    param([Parameter(Mandatory)] [string] $Path)
    try { [System.Reflection.AssemblyName]::GetAssemblyName($Path).Version }
    catch { $null }
}

# Зливає публікацію воркера в публікацію Api, НЕ змінюючи жодного файлу Api
# (пояснення — у кроці 1a нижче). Файл, якого в Api немає, копіюється;
# побайтово однаковий — пропускається; різний — лишається версія Api, але
# лише якщо це збірка і її версія не нижча за ту, що потрібна воркеру.
# Будь-що інше (нижча версія, різний нативний файл) — зупинка з переліком:
# один із двох процесів отримав би не той файл, з яким його зібрано.
function Merge-WorkerPublish {
    param(
        [Parameter(Mandatory)] [string] $WorkerDir,
        [Parameter(Mandatory)] [string] $PublishDir
    )

    $workerRoot = (Resolve-Path $WorkerDir).Path.TrimEnd('\')
    $copied = 0
    $identical = 0
    $keptApi = [System.Collections.Generic.List[string]]::new()
    $conflicts = [System.Collections.Generic.List[string]]::new()

    foreach ($file in Get-ChildItem $workerRoot -Recurse -File) {
        $relative = $file.FullName.Substring($workerRoot.Length).TrimStart('\')
        $target = Join-Path $PublishDir $relative

        if (-not (Test-Path $target)) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item $file.FullName $target
            $copied++
            continue
        }

        if ((Get-FileHash $file.FullName -Algorithm SHA256).Hash -eq (Get-FileHash $target -Algorithm SHA256).Hash) {
            $identical++
            continue
        }

        $needed = Get-AssemblyVersionOrNull $file.FullName
        $present = Get-AssemblyVersionOrNull $target
        if ($needed -and $present -and $present -ge $needed) {
            $keptApi.Add("$relative (Api $present >= воркер $needed)")
        }
        else {
            $conflicts.Add("$relative (Api: $(if ($present) { $present } else { 'не збірка' }), воркер: $(if ($needed) { $needed } else { 'не збірка' }))")
        }
    }

    if ($conflicts.Count -gt 0) {
        throw ("Публікації Api і Worker несумісні — спільні файли різні, і версія Api нижча " +
               "або файл нативний:`n  " + ($conflicts -join "`n  ") +
               "`nВирівняй версії пакетів у Directory.Packages.props (або рантайму) і збери знову.")
    }

    Write-Host ("Воркер злито в публікацію: нових файлів $copied, однакових $identical, " +
                "спільних збірок лишено від Api $($keptApi.Count).") -ForegroundColor Green
    foreach ($line in $keptApi) { Write-Host "  $line" -ForegroundColor DarkGray }
}

# ── 0. Інструменти на місці? ──────────────────────────────────────────────
# Перевірка ПЕРЕД довгою публікацією: інакше про відсутність wix/npm
# дізнаємося через кілька хвилин, коли публікація чи збірка клієнта вже
# пройшла.
if (-not $PublishOnly) {
    $wix = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wix) {
        throw "WiX CLI не знайдено. Встановити: dotnet tool install --global wix --version 5.0.2"
    }
    Write-Host "WiX: $(wix --version)" -ForegroundColor Cyan
}

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
    Invoke-NativeStep "dotnet publish" {
        dotnet publish (Join-Path $root 'src\Ecr.Api\Ecr.Api.csproj') `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -p:PublishReadyToRun=true `
            -p:Version=$Version `
            -o $publishDir
    }

    # ── 1a. Воркер перерахунку (ФВ-9.8, D-206; служба EcrWorker, Worker.wxs) ──
    # ⛔ НЕ прямо в $publishDir: обидві публікації self-contained і несуть
    # спільні файли (рантайм, Microsoft.Extensions.*). Api бере
    # Microsoft.Extensions.* з рантайм-пакета ASP.NET Core, воркер — з NuGet
    # (CPM), і після ReadyToRun байти різні навіть за однієї версії. Друга
    # публікація поверх першої мовчки замінила б файли Api файлами воркера —
    # тобто служба EcrApi запускалася б на збірках, з якими її ніхто не
    # перевіряв. Тому воркер публікується окремо, а злиття — Merge-WorkerPublish:
    # файли Api лишаються як є, спільний файл допускається лише тоді, коли
    # версія збірки Api не нижча за потрібну воркеру.
    $workerStage = Join-Path $root 'artifacts\publish-worker'
    if (Test-Path $workerStage) { Remove-Item $workerStage -Recurse -Force }

    Invoke-NativeStep "dotnet publish Ecr.Worker" {
        dotnet publish (Join-Path $root 'src\Ecr.Worker\Ecr.Worker.csproj') `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -p:PublishReadyToRun=true `
            -p:Version=$Version `
            -o $workerStage
    }

    Merge-WorkerPublish -WorkerDir $workerStage -PublishDir $publishDir
    Remove-Item $workerStage -Recurse -Force
}

if (-not (Test-Path (Join-Path $publishDir 'Ecr.Api.exe'))) {
    throw "У публікації немає Ecr.Api.exe — перевір, чи це проєкт служби."
}
foreach ($required in 'Ecr.Worker.exe', 'Ecr.Worker.dll', 'worker.settings.json') {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "У публікації немає $required — Worker.wxs посилається на нього, MSI не збереться."
    }
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
        Invoke-NativeStep "npm ci" { & npm.cmd ci }
        Invoke-NativeStep "npm run build" { & npm.cmd run build }
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

if ($PublishOnly) {
    Write-Host ""
    Write-Host "-PublishOnly: публікація готова ($publishDir), MSI не збирається." -ForegroundColor Green
    return
}

# ── 2. Збірка MSI ─────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $msiDir | Out-Null

Invoke-NativeStep "збірка MSI" {
    dotnet build $wixproj `
        -c $Configuration `
        -p:VersionPrefix=$Version `
        -p:PublishDir=$publishDir `
        -p:OutputPath=$msiDir
}

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
    # ⚠ [void]: Execute() повертає VT_EMPTY, і без [void] функція віддавала
    # @($null, значення) — порожній UpgradeCode тоді не ловився (@($null, $null)
    # у -not дає $false). Знайдено на синтетичному MSI (verify-msi.ps1).
    [void] $v.Execute(); $r = $v.Fetch()
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
Write-Host "З воркером перерахунку (служба EcrWorker, типово вимкнена):"
Write-Host "  msiexec /i `"$($msi.Name)`" /qn /l*v install.log SERVICE_ACCOUNT=DOMAIN\ecr-svc$ WORKER_ENABLED=1"
