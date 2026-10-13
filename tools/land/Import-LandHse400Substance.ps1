<#
.SYNOPSIS
    Заводить у стенді ECR довідник LAND_HSE400_SUBSTANCE: код речовини -> показник складу HSE400
    (`{хімічна назва}_mass`), за яким фрагмент CompositionMass знаходить колонку складу (LP-10).

.DESCRIPTION
    Прод-даних тут немає й читати їх не потрібно: відповідність береться з моделі Land
    (`docs/delivery/reference-data/land-template/land-template.model.json`, довідник LAND_HSE400_SUBSTANCE), яку
    генерує `model-gen/hse400_bridges.py` зі словника старого бекенду PollutionCodeService.GetMassName
    (15 кодів). Скрипт потрібен стендам, побудованим ДО появи довідника; свіжий стенд отримує його етапом
    `Build-LandTemplate.ps1 -Stage Registries`.

    Кроки:
      1. читає модель; будує план (код запису `C<код>`; ключ SUBSTANCE_CODE; показник INDICATOR);
      2. якщо довідника немає - POST /api/v1/registries; якщо в описі немає полів - PUT definition (If-Match) із
         полями й первинним ключем; опис, що вже є й не збігається, не змінюється (відмова);
      3. записи, ЯКИХ ще немає за кодом, - POST /api/v1/registries/{code}/entries/batch. Наявні НЕ чіпаються,
         повторний запуск дає 0 змін.

    Стенд: лише локальний API (Assert-LocalBaseUrl); мутації - лише з -ConfirmStand; -DryRun лише читає.

.EXAMPLE
    $pw = Read-Host 'Пароль ECR' -AsSecureString
    .\Import-LandHse400Substance.ps1 -BaseUrl http://localhost:5094 -User admin -Password $pw -DryRun
    .\Import-LandHse400Substance.ps1 -BaseUrl http://localhost:5094 -User admin -Password $pw -ConfirmStand localhost:5094
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BaseUrl = $(if ($env:ECR_BASE_URL) { $env:ECR_BASE_URL } else { 'http://localhost:5092' }),
    [string]$User,
    [securestring]$Password,
    [string]$ConfirmStand,
    [string]$ModelPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandHse400Composition.Lib.ps1')

$dry = $DryRun.IsPresent -or $WhatIfPreference
$modelFile = $ModelPath
if (-not $modelFile) { $modelFile = Join-Path $here '..\..\docs\delivery\reference-data\land-template\land-template.model.json' }

# Лише локальний стенд - ДО будь-якого виклику.
Assert-LocalBaseUrl $BaseUrl
if (-not $dry) { Set-EcrStandConfirmation -Stand $ConfirmStand -Url $BaseUrl }

$mdl = Get-Content -LiteralPath $modelFile -Raw -Encoding UTF8 | ConvertFrom-Json
$registry = @($mdl.registries) | Where-Object { $_.code -eq 'LAND_HSE400_SUBSTANCE' } | Select-Object -First 1
if ($null -eq $registry) { throw "У моделі $modelFile немає довідника LAND_HSE400_SUBSTANCE (перегенеруйте модель: model-gen/run-model-gen.ps1)." }
$plan = ConvertTo-LandHse400SubstancePlan -Registry $registry
Write-Host "Модель: кодів речовин $(@($plan.Items).Count); пропущено: $($plan.Skipped)."
if (@($plan.Items).Count -eq 0) { throw 'У моделі немає жодного коду речовини: нічого завантажувати.' }

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
$spec = (Get-LandHse400RegistryDefinitions).Substance

# 1. Довідник й опис.
$list = Invoke-Ecr -Method GET -Path '/api/v1/registries'
Assert-EcrOk $list 'GET registries'
$def = @($list.Json) | Where-Object { $_.code -eq $spec.Code } | Select-Object -First 1
$created = $false
$fieldsCreated = $false
$definition = $null
if ($null -eq $def) {
    $created = $true
    if ($dry) { Write-Host "[dry-run] створив би довідник $($spec.Code)" }
    else {
        $c = Invoke-Ecr -Method POST -Path '/api/v1/registries' -Body @{ code = $spec.Code; nameL10n = @{ en = $spec.NameEn }; isTemporal = $false }
        Assert-EcrOk $c "POST registries ($($spec.Code))"
    }
}
if (-not ($dry -and $created)) {
    $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($spec.Code)/definition"
    Assert-EcrOk $d "GET definition ($($spec.Code))"
    $definition = $d.Json
    $have = @($definition.fields | ForEach-Object { [string]$_.code })
    if ($have.Count -eq 0) {
        $fieldsCreated = $true
        if ($dry) { Write-Host "[dry-run] додав би поля й ключ до $($spec.Code)" }
        else {
            $fields = @(); $o = 1
            foreach ($f in $spec.Fields) {
                $fields += @{ id = $null; code = $f.Code; nameL10n = @{ values = @{ en = $f.Code } }; dataType = $f.DataType; ordinal = $o++
                    isRequired = $f.IsKey; isKey = $f.IsKey; lookupRegistryDefId = $null; unitId = $null }
            }
            $keys = @()
            foreach ($k in $spec.Keys) {
                $keys += @{ id = $null; code = $k.Code; nameL10n = @{ values = @{ en = $k.Code } }; fieldCodes = @($k.Fields)
                    isPrimary = $k.IsPrimary; ignoreCase = $k.IgnoreCase; isActive = $true }
            }
            $put = Invoke-Ecr -Method PUT -Path "/api/v1/registries/$($spec.Code)/definition" -Headers @{ 'If-Match' = [string]$definition.definitionVersion } -Body @{
                codeMode = $definition.codeMode; fields = $fields; keys = $keys; rules = @(); reason = 'Land HSE400 substance map: fields and key'
            }
            Assert-EcrOk $put "PUT definition ($($spec.Code))"
            $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($spec.Code)/definition"
            Assert-EcrOk $d "GET definition ($($spec.Code))"
            $definition = $d.Json
        }
    }
    else {
        $missing = @(@($spec.Fields | ForEach-Object { $_.Code }) | Where-Object { $have -notcontains $_ })
        if ($missing.Count -gt 0) {
            throw "Довідник $($spec.Code) уже має поля, але без: $($missing -join ', '). Опис не змінюється автоматично. Відмова."
        }
    }
}

# 2. Записи: лише відсутні за кодом.
$haveCodes = @{}
if ($null -ne $definition) {
    $e = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($spec.Code)/entries?asOf=$today"
    Assert-EcrOk $e "GET entries ($($spec.Code))"
    foreach ($x in @($e.Json)) { $haveCodes[([string]$x.code).ToUpperInvariant()] = $true }
}
$new = @($plan.Items | Where-Object { -not $haveCodes.ContainsKey($_.EntryCode.ToUpperInvariant()) })
$added = 0
$failed = $false
if ($dry -or $null -eq $definition) { $added = $new.Count }
elseif ($new.Count -gt 0) {
    $items = @($new | ForEach-Object {
            @{ clientRowId = $_.EntryCode; op = 'upsert'; code = $_.EntryCode; name = $_.Display
                values = @{ SUBSTANCE_CODE = $_.Code; INDICATOR = $_.Indicator } } })
    $r = Invoke-Ecr -Method POST -Path "/api/v1/registries/$($spec.Code)/entries/batch?dryRun=false" -Body @{ items = $items }
    Assert-EcrOk $r "POST entries/batch ($($spec.Code))"
    if ($r.Json.applied) { $added = $new.Count }
    else {
        $failed = $true
        $bad = @($r.Json.rows | Where-Object { $_.status -eq 'error' })
        $codes = @($bad | ForEach-Object { @($_.errors) | ForEach-Object { [string]$_.errorCode } } | Group-Object | ForEach-Object { "$($_.Name)x$($_.Count)" })
        Write-Warning "Пачка $($spec.Code): відхилено ($($codes -join ', '))."
    }
}

$changes = $added + $(if ($created) { 1 } else { 0 }) + $(if ($fieldsCreated) { 1 } else { 0 })
Write-Host "Довідник $($spec.Code): додано $added, наявних $(@($plan.Items).Count - $new.Count)$(if ($created) { '; довідник створено' })$(if ($fieldsCreated) { '; поля створено' })."
Write-Host "Змін: $changes$(if ($dry) { ' (dry-run, нічого не записано)' })."
if ($failed) { exit 1 }
