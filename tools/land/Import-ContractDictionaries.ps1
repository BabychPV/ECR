<#
.SYNOPSIS
    Завантажує довідники вкладки «2. Contract» шаблону Land (LAND_*) через REST API ECR.

.DESCRIPTION
    Джерело — закомічені CSV з docs/delivery/reference-data/land-contract (коди записів
    заморожені в файлах, не обчислюються). Список довідників бере з маніфесту header-fields.json
    (поля kind=Lookup). Для кожного довідника:
      1. якщо довідника немає — POST /api/v1/registries;
      2. якщо в описі немає поля NAME — PUT /api/v1/registries/{code}/definition (If-Match);
      3. записи, ЯКИХ ще немає за кодом, — POST /api/v1/registries/{code}/entries
         (код, display en/ru, значення NAME).
    Наявні записи НЕ чіпаються: зміни адміністратора не перезаписуються, повторний запуск
    дає 0 змін.

    Чому не CSV-імпорт (entries/import): він приймає лише code і поля опису, а display
    ставить рівним коду і не знає вікон чинності (висновок S-7). Тому запис по одному.

    Права: Registry.EditDefinition + Registry.Publish (довідник, опис) і Registry.EditData
    (записи) — у штатному сіді це SystemAdministrator.

    ⛔ Не запускати проти робочої бази замовника без рішення людини. Скрипт нічого не
    видаляє. -DryRun / -WhatIf виконують лише читання.

.PARAMETER BaseUrl
    Адреса API (за замовчуванням $env:ECR_BASE_URL або http://localhost:5092).

.PARAMETER User
    Локальний користувач (або $env:ECR_USER).

.PARAMETER Password
    Пароль як SecureString (або $env:ECR_PASSWORD). У файлах і логах не зберігається.

.PARAMETER DataDir
    Каталог CSV і маніфесту (за замовчуванням docs/delivery/reference-data/land-contract).

.PARAMETER Only
    Обмежити перелік довідників кодами (напр. LAND_PERMIT).

.PARAMETER DryRun
    Лише читання: показати, що було б створено.

.EXAMPLE
    $env:ECR_USER='admin'; $env:ECR_PASSWORD='...'   # лише для поточного сеансу
    .\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092 -DryRun
    .\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BaseUrl = $(if ($env:ECR_BASE_URL) { $env:ECR_BASE_URL } else { 'http://localhost:5092' }),
    [string]$User,
    [securestring]$Password,
    [string]$DataDir,
    [string[]]$Only,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $DataDir) { $DataDir = Join-Path $here '..\..\docs\delivery\reference-data\land-contract' }
. (Join-Path $here 'EcrApi.ps1')

# Назви довідників (EN/RU) і темпоральність. Рішення S-4/план §8: LAND_PERMIT темпоральний
# (вікна дії додаються пізніше окремо; запис без вікна чинний завжди). Темпоральність
# обирається один раз при створенні, тому тут, а не пізніше.
$RegistryMeta = @{
    LAND_AREA       = @{ en = 'Land contract: Area';                 ru = 'Контракт Land: район работ';          temporal = $false }
    LAND_CONTRACTOR = @{ en = 'Land contract: Contractor';           ru = 'Контракт Land: подрядчик';            temporal = $false }
    LAND_REGION     = @{ en = 'Land contract: Region';               ru = 'Контракт Land: регион';               temporal = $false }
    LAND_LOCATION   = @{ en = 'Land contract: Location/Facility';    ru = 'Контракт Land: местоположение/объект'; temporal = $false }
    LAND_ONOFFSHORE = @{ en = 'Land contract: Onshore/Offshore';     ru = 'Контракт Land: на суше/на море';      temporal = $false }
    LAND_ACTIVITY   = @{ en = 'Land contract: Type of activity';     ru = 'Контракт Land: вид деятельности';     temporal = $false }
    LAND_PERMIT     = @{ en = 'Land contract: Permit number';        ru = 'Контракт Land: номер разрешения';     temporal = $true }
}

$dry = $DryRun.IsPresent -or $WhatIfPreference
$DataDir = (Resolve-Path -LiteralPath $DataDir).Path

$manifest = Get-Content -LiteralPath (Join-Path $DataDir 'header-fields.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$targets = @($manifest.fields | Where-Object { $_.kind -eq 'Lookup' })
if ($Only) { $targets = @($targets | Where-Object { $Only -contains $_.registry }) }
if ($targets.Count -eq 0) { throw 'Немає довідників для завантаження (перевірте -Only і маніфест).' }

# Читаємо й перевіряємо всі CSV ДО першого виклику API: зіпсований файл не має залишити
# частково заповнений довідник.
$plan = @()
foreach ($t in $targets) {
    if (-not $RegistryMeta.ContainsKey($t.registry)) { throw "Невідомий довідник у маніфесті: $($t.registry)" }
    $rows = Read-Utf8Csv (Join-Path $DataDir $t.csv)
    $seen = @{}
    foreach ($r in $rows) {
        if ([string]::IsNullOrWhiteSpace($r.code) -or [string]::IsNullOrWhiteSpace($r.NAME)) {
            throw "$($t.csv): порожній code або NAME."
        }
        if ($seen.ContainsKey($r.code.ToUpperInvariant())) { throw "$($t.csv): дубль коду $($r.code)." }
        $seen[$r.code.ToUpperInvariant()] = $true
    }
    $plan += [pscustomobject]@{ Registry = $t.registry; Csv = $t.csv; Rows = $rows }
}

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

$summary = @()
$failed = $false

foreach ($p in $plan) {
    $code = $p.Registry
    $meta = $RegistryMeta[$code]
    $row = [ordered]@{ Registry = $code; RegistryCreated = $false; FieldCreated = $false; Created = 0; Existing = 0; Skipped = 0 }

    # 1. Довідник.
    $list = Invoke-Ecr -Method GET -Path '/api/v1/registries'
    Assert-EcrOk $list 'GET registries'
    $def = @($list.Json) | Where-Object { $_.code -eq $code } | Select-Object -First 1

    if ($null -eq $def) {
        $row.RegistryCreated = $true
        if ($dry) { Write-Host "[dry-run] створив би довідник $code (temporal=$($meta.temporal))" }
        else {
            $c = Invoke-Ecr -Method POST -Path '/api/v1/registries' -Body @{
                code = $code; nameL10n = @{ en = $meta.en; ru = $meta.ru }; isTemporal = $meta.temporal
            }
            Assert-EcrOk $c "POST registries ($code)"
            $def = $c.Json
        }
    }

    # 2. Поле NAME.
    $definition = $null
    if ($null -ne $def) {
        $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$code/definition"
        Assert-EcrOk $d "GET definition ($code)"
        $definition = $d.Json
    }
    $hasName = $null -ne $definition -and (@($definition.fields) | Where-Object { $_.code -eq 'NAME' })
    if (-not $hasName) {
        $row.FieldCreated = $true
        if ($dry) { Write-Host "[dry-run] додав би поле NAME до $code" }
        else {
            $put = Invoke-Ecr -Method PUT -Path "/api/v1/registries/$code/definition" -Headers @{ 'If-Match' = [string]$definition.definitionVersion } -Body @{
                codeMode = $definition.codeMode
                fields   = @(@{
                        id = $null; code = 'NAME'; nameL10n = @{ values = @{ en = 'Name'; ru = 'Название' } }; dataType = 'String'
                        ordinal = 1; isRequired = $true; isKey = $true; lookupRegistryDefId = $null; unitId = $null
                    })
                keys     = @()
                rules    = @()
                reason   = 'Land contract dictionaries: field NAME'
            }
            Assert-EcrOk $put "PUT definition ($code)"
            $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$code/definition"
            Assert-EcrOk $d "GET definition ($code)"
            $definition = $d.Json
        }
    }

    # 3. Записи: лише відсутні за кодом.
    $existingCodes = @{}
    if ($null -ne $definition) {
        $e = Invoke-Ecr -Method GET -Path "/api/v1/registries/$code/entries?asOf=$today"
        Assert-EcrOk $e "GET entries ($code)"
        foreach ($x in @($e.Json)) { $existingCodes[([string]$x.code).ToUpperInvariant()] = $true }
    }

    foreach ($r in $p.Rows) {
        if ($existingCodes.ContainsKey($r.code.ToUpperInvariant())) { $row.Existing++; continue }
        if ($dry -or $null -eq $definition) { $row.Created++; continue }

        $display = @{ en = $(if ($r.display_en) { $r.display_en } else { $r.NAME }) }
        if (-not [string]::IsNullOrWhiteSpace($r.display_ru)) { $display.ru = $r.display_ru }

        $post = Invoke-Ecr -Method POST -Path "/api/v1/registries/$code/entries" -Body @{
            id = $null; registryDefId = $definition.id; code = $r.code; display = @{ values = $display }
            parentEntryId = $null; values = @{ NAME = $r.NAME }
        }
        if ($post.Status -in 200, 201) { $row.Created++ }
        elseif ($post.Status -in 409, 422) {
            # Код зайнятий записом, якого GET не показав (закритий/видалений), або правило довідника.
            # Не перезаписуємо: повідомляємо й рухаємося далі.
            $row.Skipped++
            Write-Warning "${code}/$($r.code): HTTP $($post.Status) $(Get-EcrErrorCode $post) — пропущено."
        }
        else {
            $failed = $true
            Write-Error "${code}/$($r.code): HTTP $($post.Status) $(Get-EcrErrorCode $post)" -ErrorAction Continue
            break
        }
    }
    $summary += [pscustomobject]$row
}

$summary | Format-Table -AutoSize | Out-String | Write-Host
$changes = ($summary | Measure-Object -Property Created -Sum).Sum
$changes += @($summary | Where-Object { $_.RegistryCreated -or $_.FieldCreated }).Count
Write-Host "Змін: $changes$(if ($dry) { ' (dry-run, нічого не записано)' })."
if ($failed) { exit 1 }
