<#
.SYNOPSIS
    Завантажує довідник повітряних речовин LAND_AIR_SUBSTANCE зі старої системи (AF_PermitConstant) у стенд ECR
    і, за бажанням, додає речовини до складу версії методології (форма B константи, LP-07).

.DESCRIPTION
    ⛔ Значень прод-даних у git немає: скрипт читає їх під час запуску ЛОКАЛЬНО з локальної копії прода
    (`SELECT … FROM dbo.AF_PermitConstant WITH (NOLOCK)`) і пише в локальний стенд через REST API.
    Нічого з джерела не потрапляє в файли, журнали й вивід, крім кількостей.

    Кроки:
      1. читає джерело (лише локальний SQL Server; база підтверджується -ConfirmSource);
      2. будує план (3-4 цифри коду; провідні нулі знімаються; дублі й текстові коди пропускаються з обліком);
      3. якщо довідника немає — POST /api/v1/registries; якщо в описі немає полів — PUT definition (If-Match);
      4. записи, ЯКИХ ще немає за кодом `E<код>`, — POST /api/v1/registries/{code}/entries (display en/ru,
         значення SUBSTANCE_CODE, CHEMICAL_FORMULA, PHYSICAL_STATE_EN/RU). Наявні записи НЕ чіпаються,
         повторний запуск дає 0 змін;
      5. (-AttachMethodologyId/-AttachVersionId/-AttachCodes) PUT /api/v1/methodologies/{id}/versions/{vid}/substances:
         наявний склад + речовини з вказаними кодами (набір, порядок зберігається; повтор — 0 змін).

    Код запису довідника — `E<код>`: EcrCode вимагає літери першим символом, тому число лежить у полі
    SUBSTANCE_CODE (як у LAND_WASTE_SUBSTANCE).

    Стенд: лише локальний API (Assert-LocalBaseUrl); мутації — лише з -ConfirmStand; -DryRun лише читає.

.PARAMETER SourceDatabase
    База локальної копії прода, з якої читаємо (сервер — -SourceServer, за замовчуванням localhost).

.PARAMETER ConfirmSource
    Мусить дорівнювати -SourceDatabase (свідоме підтвердження, що це копія, а не живий прод).

.PARAMETER ConfirmStand
    Підтвердження стенда (`localhost:5094` тощо), як у решти скриптів tools/land.

.EXAMPLE
    $pw = Read-Host 'Пароль ECR' -AsSecureString
    .\Import-LandAirSubstances.ps1 -SourceDatabase AF_Copy -ConfirmSource AF_Copy -BaseUrl http://localhost:5094 -User admin -Password $pw -DryRun
    .\Import-LandAirSubstances.ps1 -SourceDatabase AF_Copy -ConfirmSource AF_Copy -BaseUrl http://localhost:5094 -User admin -Password $pw -ConfirmStand localhost:5094
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BaseUrl = $(if ($env:ECR_BASE_URL) { $env:ECR_BASE_URL } else { 'http://localhost:5092' }),
    [string]$User,
    [securestring]$Password,
    [string]$SourceServer = 'localhost',
    [string]$SourceDatabase,
    [string]$ConfirmSource,
    [string]$ConfirmStand,
    [int]$AttachMethodologyId,
    [int]$AttachVersionId,
    [string[]]$AttachCodes,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandAirSubstances.Lib.ps1')

$RegistryCode = 'LAND_AIR_SUBSTANCE'
$dry = $DryRun.IsPresent -or $WhatIfPreference
$hasCodes = $null -ne $AttachCodes -and $AttachCodes.Count -gt 0
$attach = $AttachMethodologyId -gt 0 -or $AttachVersionId -gt 0 -or $hasCodes
if ($attach -and ($AttachMethodologyId -le 0 -or $AttachVersionId -le 0 -or -not $hasCodes)) {
    throw 'Додавання до версії потребує всіх трьох: -AttachMethodologyId, -AttachVersionId, -AttachCodes.'
}

# Лише локальний стенд і лише підтверджене локальне джерело — ДО будь-якого читання чи виклику.
Assert-LocalBaseUrl $BaseUrl
Assert-LocalSourceDatabase -Server $SourceServer -Database $SourceDatabase -ConfirmSource $ConfirmSource
if (-not $dry) { Set-EcrStandConfirmation -Stand $ConfirmStand -Url $BaseUrl }

# 1. Джерело: лише SELECT … WITH (NOLOCK); значення не друкуються.
# Сервер і база вже перевірені Assert-LocalSourceDatabase (без ;/=), тож рядок складається без ризику ін'єкції.
$connectionText = "Data Source=$SourceServer;Initial Catalog=$SourceDatabase;Integrated Security=True;ApplicationIntent=ReadOnly;TrustServerCertificate=True;Application Name=Import-LandAirSubstances"

$sourceRows = New-Object System.Collections.Generic.List[object]
$conn = New-Object System.Data.SqlClient.SqlConnection($connectionText)
try {
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 120
    $cmd.CommandText = 'SELECT CAST(Substance_Code AS nvarchar(32)) AS Code, Substance_Name_En AS NameEn, Substance_Name_Ru AS NameRu, ' +
        'Chemical_Formula AS Formula, CAST(Substance_Physical_State_En AS nvarchar(255)) AS StateEn, ' +
        'CAST(Substance_Physical_State_Ru AS nvarchar(255)) AS StateRu FROM dbo.AF_PermitConstant WITH (NOLOCK) WHERE Substance_Code IS NOT NULL'
    $rd = $cmd.ExecuteReader()
    try {
        while ($rd.Read()) {
            $sourceRows.Add([pscustomobject]@{
                    Code    = [string]$rd['Code']
                    NameEn  = if ($rd.IsDBNull(1)) { $null } else { [string]$rd['NameEn'] }
                    NameRu  = if ($rd.IsDBNull(2)) { $null } else { [string]$rd['NameRu'] }
                    Formula = if ($rd.IsDBNull(3)) { $null } else { [string]$rd['Formula'] }
                    StateEn = if ($rd.IsDBNull(4)) { $null } else { [string]$rd['StateEn'] }
                    StateRu = if ($rd.IsDBNull(5)) { $null } else { [string]$rd['StateRu'] }
                })
        }
    }
    finally { $rd.Dispose() }
}
finally { $conn.Dispose() }

$plan = ConvertTo-LandAirSubstancePlan -Rows ([object[]]$sourceRows.ToArray())
Write-Host "Джерело: рядків $($sourceRows.Count); до довідника: $(@($plan.Items).Count); пропущено (не 3-4 цифри): $($plan.Skipped); дублів: $($plan.Duplicates); провідні нулі знято: $($plan.Normalized)."
if (@($plan.Items).Count -eq 0) { throw 'У джерелі немає жодної речовини з кодом із 3-4 цифр: нічого завантажувати.' }

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

# 2. Довідник.
$list = Invoke-Ecr -Method GET -Path '/api/v1/registries'
Assert-EcrOk $list 'GET registries'
$def = @($list.Json) | Where-Object { $_.code -eq $RegistryCode } | Select-Object -First 1
$registryCreated = $false
if ($null -eq $def) {
    $registryCreated = $true
    if ($dry) { Write-Host "[dry-run] створив би довідник $RegistryCode" }
    else {
        $c = Invoke-Ecr -Method POST -Path '/api/v1/registries' -Body @{
            code = $RegistryCode; nameL10n = @{ en = 'Land: Air pollutant substances'; ru = 'Land: загрязняющие вещества (воздух)' }; isTemporal = $false
        }
        Assert-EcrOk $c "POST registries ($RegistryCode)"
        $def = $c.Json
    }
}

# 3. Поля опису: SUBSTANCE_CODE (ключ), CHEMICAL_FORMULA, PHYSICAL_STATE_EN, PHYSICAL_STATE_RU.
$definition = $null
$fieldsCreated = $false
if ($null -ne $def) {
    $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$RegistryCode/definition"
    Assert-EcrOk $d "GET definition ($RegistryCode)"
    $definition = $d.Json
}
$existingFields = @(if ($null -ne $definition) { $definition.fields })
if ($existingFields.Count -eq 0) {
    $fieldsCreated = $true
    if ($dry) { Write-Host "[dry-run] додав би поля SUBSTANCE_CODE, CHEMICAL_FORMULA, PHYSICAL_STATE_EN, PHYSICAL_STATE_RU до $RegistryCode" }
    else {
        $specs = @(
            @{ code = 'SUBSTANCE_CODE'; en = 'Substance code'; ru = 'Код вещества'; key = $true },
            @{ code = 'CHEMICAL_FORMULA'; en = 'Chemical formula'; ru = 'Химическая формула'; key = $false },
            @{ code = 'PHYSICAL_STATE_EN'; en = 'Physical state (EN)'; ru = 'Агрегатное состояние (EN)'; key = $false },
            @{ code = 'PHYSICAL_STATE_RU'; en = 'Physical state (RU)'; ru = 'Агрегатное состояние (RU)'; key = $false }
        )
        $fieldBodies = @()
        $ordinal = 1
        foreach ($s in $specs) {
            $fieldBodies += @{
                id = $null; code = $s.code; nameL10n = @{ values = @{ en = $s.en; ru = $s.ru } }; dataType = 'String'
                ordinal = $ordinal; isRequired = $s.key; isKey = $s.key; lookupRegistryDefId = $null; unitId = $null
            }
            $ordinal++
        }
        $put = Invoke-Ecr -Method PUT -Path "/api/v1/registries/$RegistryCode/definition" -Headers @{ 'If-Match' = [string]$definition.definitionVersion } -Body @{
            codeMode = $definition.codeMode
            fields   = $fieldBodies
            keys     = @()
            rules    = @()
            reason   = 'Land air substances: fields'
        }
        Assert-EcrOk $put "PUT definition ($RegistryCode)"
        $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$RegistryCode/definition"
        Assert-EcrOk $d "GET definition ($RegistryCode)"
        $definition = $d.Json
    }
}
elseif (-not ($existingFields | Where-Object { $_.code -eq 'SUBSTANCE_CODE' })) {
    throw "Довідник $RegistryCode уже має поля, але без SUBSTANCE_CODE: опис не змінюється автоматично. Відмова."
}

# 4. Записи: лише відсутні за кодом.
$byCode = @{}
if ($null -ne $definition) {
    $e = Invoke-Ecr -Method GET -Path "/api/v1/registries/$RegistryCode/entries?asOf=$today"
    Assert-EcrOk $e "GET entries ($RegistryCode)"
    foreach ($x in @($e.Json)) { $byCode[([string]$x.code).ToUpperInvariant()] = $x }
}

$created = 0
$existing = 0
$skippedEntries = 0
$failed = $false
foreach ($it in $plan.Items) {
    if ($byCode.ContainsKey($it.EntryCode.ToUpperInvariant())) { $existing++; continue }
    if ($dry -or $null -eq $definition) { $created++; continue }

    $display = @{ en = $it.DisplayEn }
    if (-not [string]::IsNullOrWhiteSpace($it.DisplayRu)) { $display.ru = $it.DisplayRu }
    $values = @{ SUBSTANCE_CODE = $it.Code }
    if ($it.Formula) { $values.CHEMICAL_FORMULA = $it.Formula }
    if ($it.StateEn) { $values.PHYSICAL_STATE_EN = $it.StateEn }
    if ($it.StateRu) { $values.PHYSICAL_STATE_RU = $it.StateRu }

    $post = Invoke-Ecr -Method POST -Path "/api/v1/registries/$RegistryCode/entries" -Body @{
        id = $null; registryDefId = $definition.id; code = $it.EntryCode; display = @{ values = $display }
        parentEntryId = $null; values = $values
    }
    if ($post.Status -in 200, 201) { $created++; $byCode[$it.EntryCode.ToUpperInvariant()] = $post.Json }
    elseif ($post.Status -in 409, 422) {
        $skippedEntries++
        Write-Warning "$($it.EntryCode): HTTP $($post.Status) $(Get-EcrErrorCode $post) — пропущено."
    }
    else {
        $failed = $true
        Write-Error "$($it.EntryCode): HTTP $($post.Status) $(Get-EcrErrorCode $post)" -ErrorAction Continue
        break
    }
}

$changes = $created + $(if ($registryCreated) { 1 } else { 0 }) + $(if ($fieldsCreated) { 1 } else { 0 })

# 5. Склад речовин версії (необов'язково): наявні + вказані коди.
$attached = 0
if ($attach -and -not $failed) {
    $subUrl = "/api/v1/methodologies/$AttachMethodologyId/versions/$AttachVersionId/substances"
    $cur = Invoke-Ecr -Method GET -Path $subUrl
    Assert-EcrOk $cur 'GET substances'
    $ids = New-Object System.Collections.Generic.List[long]
    foreach ($i in @($cur.Json.items)) { $ids.Add([long]$i.substanceEntryId) }

    foreach ($raw in $AttachCodes) {
        $c = ([string]$raw).Trim().TrimStart('0')
        $hit = $byCode["E$c".ToUpperInvariant()]
        if ($null -eq $hit -and $dry) { Write-Host "[dry-run] речовина '$raw' з'явиться після імпорту."; continue }
        if ($null -eq $hit) { throw "Код речовини '$raw' відсутній у довіднику $RegistryCode (після імпорту). Відмова." }
        $entryId = [long]$hit.id
        if (-not $ids.Contains($entryId)) { $ids.Add($entryId); $attached++ }
    }

    if ($attached -eq 0) { Write-Host 'Склад версії: усі вказані речовини вже є — 0 змін.' }
    elseif ($dry) { Write-Host "[dry-run] додав би до складу версії речовин: $attached" }
    else {
        $putSub = Invoke-Ecr -Method PUT -Path $subUrl -Body @{ substanceEntryIds = @($ids) }
        Assert-EcrOk $putSub 'PUT substances'
        Write-Host "Склад версії: додано речовин $attached, усього $($ids.Count)."
    }
}

Write-Host "Довідник ${RegistryCode}: створено записів $created, наявних $existing, пропущено $skippedEntries$(if ($registryCreated) { '; довідник створено' })$(if ($fieldsCreated) { '; поля створено' })."
Write-Host "Змін: $($changes + $attached)$(if ($dry) { ' (dry-run, нічого не записано)' })."
if ($failed) { exit 1 }
