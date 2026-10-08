<#
.SYNOPSIS
    Додає до шаблону Land поля шапки вкладки «2. Contract» (11 HeaderFieldDef) за маніфестом.

.DESCRIPTION
    Маніфест — docs/delivery/reference-data/land-contract/header-fields.json (13 позицій у
    порядку Excel; FileNumber і Version — службові, read-only з Document.BusinessKey і версії
    шаблону, HeaderFieldDef для них НЕ створюються). Lookup-поля прив'язуються до довідників
    LAND_* (спершу їх завантажує Import-ContractDictionaries.ps1).

    Порядок (план RC15, L3):
      1. знайти шаблон за кодом; взяти чернетку версії, якщо вона є, інакше клонувати
         останню ОПУБЛІКОВАНУ (або -SourceVersionId): POST /template-versions/{id}/clone;
      2. PUT /template-versions/{id}/header-fields/{code} лише для полів, що відсутні або
         відрізняються (тип, довідник, обов'язковість, порядок); підписи наявних полів
         зберігаються;
      3. POST /template-versions/{id}/publish;
      4. для документів з -MigrateDocumentId: POST /documents/{id}/migrate-version —
         спершу dryRun, застосування лише коли canApply.

    Ідемпотентність: якщо вибрана версія вже відповідає маніфесту — нічого не робить (0 змін,
    клон не створюється). Зміну ТИПУ наявного поля скрипт не робить (тип незмінний у ECR) —
    зупиняється з поясненням.

    ⛔ Опублікована версія шаблону незмінна: усі зміни йдуть у новій версії. Не запускати
    проти робочої бази замовника без рішення людини. -DryRun / -WhatIf — лише читання.

.PARAMETER TemplateCode
    Код шаблону (напр. Land).

.PARAMETER SourceVersionId
    Опублікована версія-основа (за замовчуванням остання опублікована).

.PARAMETER NewVersion
    Номер нової версії (за замовчуванням: основа з +1 до третього сегмента, 1.0.0.0 -> 1.0.1.0).

.PARAMETER NoPublish
    Залишити чернетку без публікації.

.PARAMETER MigrateDocumentId
    Документи, які перенести на нову версію (Safe) після публікації. Міграція виконується і тоді,
    коли шапка вже відповідає маніфесту (змін немає): ціль — остання опублікована версія шаблону.

.PARAMETER Async
    Просити сервер виконати застосування міграції фоновою задачею (`async: true` → 202 + jobId).
    За замовчуванням вимкнено: старіші сервери поля не знають. Відповідь 202 обробляється завжди,
    незалежно від цього прапора (опитування GET /jobs/{jobId}).

.PARAMETER MigrateTimeoutMinutes
    Скільки чекати завершення фонової міграції (за замовчуванням 60 хв).

.EXAMPLE
    .\Apply-ContractHeader.ps1 -TemplateCode Land -DryRun
    .\Apply-ContractHeader.ps1 -TemplateCode Land -NewVersion 1.0.1.0 -MigrateDocumentId 3,4
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$TemplateCode,
    [string]$BaseUrl = $(if ($env:ECR_BASE_URL) { $env:ECR_BASE_URL } else { 'http://localhost:5092' }),
    [string]$User,
    [securestring]$Password,
    [string]$DataDir,
    [int]$SourceVersionId,
    [string]$NewVersion,
    [switch]$NoPublish,
    [int[]]$MigrateDocumentId,
    [switch]$Async,
    [int]$MigrateTimeoutMinutes = 60,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $DataDir) { $DataDir = Join-Path $here '..\..\docs\delivery\reference-data\land-contract' }
. (Join-Path $here 'EcrApi.ps1')

# Підписи за аркушем «2. Contract» (EN - RU). Там, де в Excel немає російського підпису
# (Processed on, Permit Number), RU не вигадується.
$Labels = @{
    Area           = @{ en = 'Area'; ru = 'Район работ' }
    Contractor     = @{ en = 'Contractor or Company division'; ru = 'Подрядчик или Подразделение Компании' }
    Region         = @{ en = 'Region'; ru = 'Регион' }
    Location       = @{ en = 'Location/Facility'; ru = 'Местоположение/Объект' }
    OnOffshore     = @{ en = 'Onshore/Offshore'; ru = 'На суше/На море' }
    FilledBy       = @{ en = 'Filled in by, name, contact tel'; ru = 'Кем заполнено Ф.И.О., контактный телефон' }
    ContractHolder = @{ en = 'Contract holder'; ru = 'Ответственный за контракт' }
    ContractNumber = @{ en = 'Contract Number'; ru = 'Номер контракта' }
    TypeOfActivity = @{ en = 'Type of Activity'; ru = 'Вид деятельности' }
    ProcessedOn    = @{ en = 'Processed on' }
    Permit         = @{ en = 'Permit Number' }
}

$dry = $DryRun.IsPresent -or $WhatIfPreference
$DataDir = (Resolve-Path -LiteralPath $DataDir).Path
$manifest = Get-Content -LiteralPath (Join-Path $DataDir 'header-fields.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$wanted = @($manifest.fields | Where-Object { $_.kind -ne 'Service' })
foreach ($w in $wanted) { if (-not $Labels.ContainsKey($w.key)) { throw "Немає підпису для поля $($w.key)." } }

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password

# --- шаблон і версії ---------------------------------------------------------------------
$t = Invoke-Ecr -Method GET -Path ('/api/v1/templates?limit=50&q=' + [uri]::EscapeDataString($TemplateCode))
Assert-EcrOk $t 'GET templates'
$tpl = @($t.Json.items) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
if ($null -eq $tpl) { throw "Шаблон «$TemplateCode» не знайдено." }

$v = Invoke-Ecr -Method GET -Path "/api/v1/templates/$($tpl.id)/versions?limit=100"
Assert-EcrOk $v 'GET template versions'
$versions = @($v.Json.items)
$draft = $versions | Where-Object { $_.status -eq 'Draft' } | Sort-Object id -Descending | Select-Object -First 1
$source = $null
if ($SourceVersionId) { $source = $versions | Where-Object { $_.id -eq $SourceVersionId } | Select-Object -First 1 }
else { $source = $versions | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1 }
if ($null -eq $draft -and $null -eq $source) { throw 'Немає ні чернетки, ні опублікованої версії для основи.' }

# База порівняння: чернетка, якщо є; інакше основа.
$baseVersion = if ($null -ne $draft) { $draft } else { $source }

# --- довідники ---------------------------------------------------------------------------
$r = Invoke-Ecr -Method GET -Path '/api/v1/registries'
Assert-EcrOk $r 'GET registries'
$registryIds = @{}
foreach ($x in @($r.Json)) { $registryIds[[string]$x.code] = [int]$x.id }

$desired = @()
foreach ($w in $wanted) {
    $lookupId = $null
    if ($w.kind -eq 'Lookup') {
        if (-not $registryIds.ContainsKey($w.registry)) {
            throw "Довідника $($w.registry) немає: спершу виконайте Import-ContractDictionaries.ps1."
        }
        $lookupId = $registryIds[$w.registry]
    }
    $req = $false
    if ($w.PSObject.Properties.Match('isRequired').Count -gt 0) { $req = [bool]$w.isRequired }
    $desired += [pscustomobject]@{
        Code = [string]$w.key; DataType = [string]$w.kind; Lookup = $lookupId; Required = $req; Ordinal = [int]$w.position
    }
}

# --- що відрізняється --------------------------------------------------------------------
$cur = Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($baseVersion.id)/header-fields"
Assert-EcrOk $cur 'GET header-fields'
$current = @{}
foreach ($c in @($cur.Json)) { $current[[string]$c.code] = $c }

$todo = @()
foreach ($d in $desired) {
    if (-not $current.ContainsKey($d.Code)) { $todo += $d; continue }
    $c = $current[$d.Code]
    if ([string]$c.dataType -ne $d.DataType) {
        throw "Поле $($d.Code): тип $($c.dataType) у версії $($baseVersion.version), потрібен $($d.DataType); тип незмінний — вирішує людина."
    }
    $curLookup = if ($null -ne $c.lookupRegistryDefId) { [int]$c.lookupRegistryDefId } else { $null }
    if ($curLookup -ne $d.Lookup -or [bool]$c.isRequired -ne $d.Required -or [int]$c.ordinal -ne $d.Ordinal) { $todo += $d }
}

# --- міграція документів (функція; викликається і коли змін шапки немає) ---------------------
function Invoke-DocumentMigration {
    param([int]$TargetVersionId)
    foreach ($docId in @($MigrateDocumentId)) {
        if (-not $docId) { continue }
        $path = "/api/v1/documents/$docId/migrate-version"
        # dry-run завжди синхронний.
        $preview = Invoke-Ecr -Method POST -Path $path -Body @{ targetVersionId = $TargetVersionId; mode = 'Safe'; dryRun = $true }
        Assert-EcrOk $preview "migrate-version dry-run (документ $docId)"
        if (-not $preview.Json.canApply) {
            Write-Warning "Документ ${docId}: міграція заблокована (refusals: $(@($preview.Json.refusals).Count)); не застосовано."
            continue
        }
        if ($dry) {
            Write-Host "[dry-run] документ ${docId}: міграцію на версію $TargetVersionId можна застосувати (canApply). Нічого не записано."
            continue
        }
        $body = @{ targetVersionId = $TargetVersionId; mode = 'Safe'; dryRun = $false }
        if ($Async) { $body.async = $true }
        $apply = Invoke-Ecr -Method POST -Path $path -Body $body
        Assert-EcrOk $apply "migrate-version (документ $docId)" -Allowed @(200, 202)
        if ($apply.Status -eq 202) {
            Write-Host "Документ ${docId}: міграція прийнята у фон (задача $($apply.Json.jobId)); чекаємо завершення."
            $job = Wait-EcrJob -JobId ([string]$apply.Json.jobId) -TimeoutMinutes $MigrateTimeoutMinutes
            Write-Host "Документ ${docId}: міграцію завершено ($($job.state)). $($job.message)"
        }
        else {
            Write-Host "Документ ${docId}: перенесено значень $($apply.Json.transferredValues), втрачено $($apply.Json.lostValues)."
        }
    }
}

$hasMigration = @($MigrateDocumentId | Where-Object { $_ }).Count -gt 0

if ($todo.Count -eq 0 -and $null -eq $draft) {
    Write-Host "Версія $($baseVersion.version) (id $($baseVersion.id)) уже відповідає маніфесту: 0 змін."
    if ($hasMigration) {
        # Шапка вже на потрібній (опублікованій) версії: міграція документів усе одно виконується.
        Invoke-DocumentMigration -TargetVersionId ([int]$baseVersion.id)
    }
    return
}

if ($todo.Count -eq 0) {
    # Чернетка вже містить усі поля (напр. попередній запуск не дійшов до публікації): лишилось опублікувати.
    Write-Host "Чернетка $($draft.version) (id $($draft.id)) уже містить усі поля: 0 змін, лишилась публікація."
}
else {
    Write-Host ("До зміни ({0}): {1}" -f $todo.Count, (($todo | ForEach-Object { $_.Code }) -join ', '))
}
if ($dry) {
    $how = if ($null -ne $draft) { "у чернетці $($draft.version)" } else { "у клоні версії $($source.version)" }
    Write-Host "[dry-run] було б застосовано $how, публікація: $(-not $NoPublish.IsPresent). Нічого не записано."
    return
}

# --- чернетка ----------------------------------------------------------------------------
$targetId = 0
if ($null -ne $draft) { $targetId = [int]$draft.id }
else {
    if ([string]::IsNullOrWhiteSpace($NewVersion)) {
        $parts = ([string]$source.version).Split('.')
        if ($parts.Count -lt 3) { throw "Не вдалося обчислити номер нової версії з «$($source.version)»: задайте -NewVersion." }
        $parts[2] = [string]([int]$parts[2] + 1)
        $NewVersion = $parts -join '.'
    }
    $cl = Invoke-Ecr -Method POST -Path "/api/v1/template-versions/$($source.id)/clone" -Body @{ newVersion = $NewVersion }
    if (Test-EcrTemplateUnsuitable $cl) { Stop-EcrTemplateUnsuitable -TemplateCode $TemplateCode }
    Assert-EcrOk $cl "clone $($source.id) -> $NewVersion"
    $targetId = [int]$cl.Json.versionId
    Write-Host "Клон: версія $NewVersion (id $targetId)."
}

foreach ($d in $todo) {
    $label = $null
    if ($current.ContainsKey($d.Code) -and $null -ne $current[$d.Code].labelL10n -and $null -ne $current[$d.Code].labelL10n.values) {
        # Зберігаємо наявні підписи (правки адміністратора).
        $label = @{}
        foreach ($p in $current[$d.Code].labelL10n.values.PSObject.Properties) { $label[$p.Name] = [string]$p.Value }
    }
    if ($null -eq $label -or $label.Count -eq 0) { $label = @{}; foreach ($k in $Labels[$d.Code].Keys) { $label[$k] = $Labels[$d.Code][$k] } }

    $put = Invoke-Ecr -Method PUT -Path "/api/v1/template-versions/$targetId/header-fields/$($d.Code)" -Body @{
        dataType = $d.DataType; isRequired = $d.Required; labelL10n = $label
        lookupRegistryDefId = $d.Lookup; ordinal = $d.Ordinal
    }
    Assert-EcrOk $put "PUT header-fields/$($d.Code)"
    Write-Host "  поле $($d.Code): ok"
}

if ($NoPublish) { Write-Host 'Чернетку залишено без публікації (-NoPublish).'; return }

$pub = Invoke-Ecr -Method POST -Path "/api/v1/template-versions/$targetId/publish" -Body @{ reason = 'Land: шапка вкладки 2. Contract (RC15)' }
if (Test-EcrTemplateUnsuitable $pub) {
    Stop-EcrTemplateUnsuitable -TemplateCode $TemplateCode -Detail "Чернетка (версія id $targetId) лишилась непублікованою. "
}
Assert-EcrOk $pub "publish $targetId"
Write-Host "Опубліковано версію id $targetId."

# --- міграція документів -----------------------------------------------------------------
Invoke-DocumentMigration -TargetVersionId $targetId
