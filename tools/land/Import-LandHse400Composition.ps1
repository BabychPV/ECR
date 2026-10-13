<#
.SYNOPSIS
    Завантажує склад газу HSE400 зі старої системи (dbo.CalcResult_AirGasCompositions_HSE400) у нормалізований довідник
    LAND_HSE400_COMPOSITION стенда ECR і словник потоків LAND_HSE400_STREAM (LP-10).

.DESCRIPTION
    ⛔ Значень прод-даних у git немає: скрипт читає їх під час запуску ЛОКАЛЬНО з локальної копії прода
    (`SELECT … FROM dbo.CalcResult_AirGasCompositions_HSE400 WITH (NOLOCK)`) і пише в локальний стенд через REST API.
    Нічого з джерела не потрапляє в файли, журнали й вивід, крім кількостей.

    Кроки:
      1. читає джерело (лише локальний SQL Server; база підтверджується -ConfirmSource); рядок = місяць (DateTime);
      2. будує план: колонка `{префікс}_{суфікс}` -> (рік, місяць, потік = префікс, показник = канонічний суфікс, значення);
         HMB: EMercaptan_mass <- EMercaptanV2_mass; колонки C1_C5/C6_C10/C12_C19 без `_mass` (FG_Export, FG_Internal,
         LPG_Internal) -> `…_mass` (так їх читає стара система); запасна: C6_C10_mass порожній -> C6_1_mass;
      3. (-DiscoverStreams) читає з копії прода різні тексти Land-полів типу палива/потоку
         (Land_TypeFuel, Land_TypeFuelOrStream, Land_TypeStream, Land_Streams; лише SELECT DISTINCT … WITH (NOLOCK))
         і додає до словника потоків ті, що збігаються з правилом старої системи (PrefixProvider);
      4. якщо довідника немає - POST /api/v1/registries; якщо в описі немає полів - PUT definition (If-Match) з полями й
         складеним первинним ключем; опис, що вже є й не збігається, не змінюється (відмова);
      5. записи, ЯКИХ ще немає за кодом, - POST /api/v1/registries/{code}/entries/batch (пачками). Наявні записи НЕ
         чіпаються, повторний запуск дає 0 змін.
         Виняток - лише з -RewriteAliased: записи, що походять з псевдоніма (C1_C5/C6_C10/C12_C19 -> `_mass`),
         перезаписуються upsert-ом за тим самим кодом (на стендах, завантажених ДО псевдоніма, `C6_C10_mass`
         FG_Internal лежить як запасна C6_1_mass - хибне значення; новий запис його виправляє).

    Немає запису за (рік, місяць, потік, показник) у довіднику -> аргумент методології не обчислюється й рядок не
    дає результатів (стара система: continue). Скрипт цього не змінює; він лише наповнює довідник.

    Стенд: лише локальний API (Assert-LocalBaseUrl); мутації - лише з -ConfirmStand; -DryRun лише читає.

.PARAMETER SourceDatabase
    База локальної копії прода, з якої читаємо (сервер - -SourceServer, за замовчуванням localhost).

.PARAMETER ConfirmSource
    Мусить дорівнювати -SourceDatabase (свідоме підтвердження, що це копія, а не живий прод).

.PARAMETER ConfirmStand
    Підтвердження стенда (`localhost:5094` тощо), як у решти скриптів tools/land.

.EXAMPLE
    $pw = Read-Host 'Пароль ECR' -AsSecureString
    .\Import-LandHse400Composition.ps1 -SourceDatabase ECR -ConfirmSource ECR -BaseUrl http://localhost:5094 -User admin -Password $pw -DiscoverStreams -DryRun
    .\Import-LandHse400Composition.ps1 -SourceDatabase ECR -ConfirmSource ECR -BaseUrl http://localhost:5094 -User admin -Password $pw -DiscoverStreams -ConfirmStand localhost:5094
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
    [switch]$DiscoverStreams,
    [switch]$RewriteAliased,
    [int]$BatchSize = 200,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandAirSubstances.Lib.ps1')   # Assert-LocalSourceDatabase
. (Join-Path $here 'LandHse400Composition.Lib.ps1')

$dry = $DryRun.IsPresent -or $WhatIfPreference
if ($BatchSize -lt 1 -or $BatchSize -gt 500) { throw '-BatchSize має бути в межах 1..500.' }

# Лише локальний стенд і лише підтверджене локальне джерело - ДО будь-якого читання чи виклику.
Assert-LocalBaseUrl $BaseUrl
Assert-LocalSourceDatabase -Server $SourceServer -Database $SourceDatabase -ConfirmSource $ConfirmSource
# -DryRun з -ConfirmStand: підтвердження потрібне лише для серверного dryRun=true перезапису псевдонімів (відкат).
if (-not $dry -or $ConfirmStand) { Set-EcrStandConfirmation -Stand $ConfirmStand -Url $BaseUrl }

# 1. Джерело: лише SELECT … WITH (NOLOCK); значення не друкуються.
# Сервер і база вже перевірені Assert-LocalSourceDatabase (без ;/=), тож рядок складається без ризику ін'єкції.
$connectionText = "Data Source=$SourceServer;Initial Catalog=$SourceDatabase;Integrated Security=True;ApplicationIntent=ReadOnly;TrustServerCertificate=True;Application Name=Import-LandHse400Composition"

$sourceRows = New-Object System.Collections.Generic.List[object]
$streamTexts = New-Object System.Collections.Generic.List[string]
$conn = New-Object System.Data.SqlClient.SqlConnection($connectionText)
try {
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 120
    $cmd.CommandText = 'SELECT * FROM dbo.CalcResult_AirGasCompositions_HSE400 WITH (NOLOCK)'
    $rd = $cmd.ExecuteReader()
    try {
        while ($rd.Read()) {
            if ($rd.IsDBNull($rd.GetOrdinal('DateTime'))) { continue }
            $when = [datetime]$rd['DateTime']
            $cols = @{}
            for ($i = 0; $i -lt $rd.FieldCount; $i++) {
                $name = $rd.GetName($i)
                if ($name -eq 'DateTime') { continue }
                $cols[$name] = if ($rd.IsDBNull($i)) { $null } else { $rd.GetValue($i) }
            }
            $sourceRows.Add([pscustomobject]@{ Year = $when.Year; Month = $when.Month; Columns = $cols })
        }
    }
    finally { $rd.Dispose() }

    if ($DiscoverStreams) {
        # Колонки Land-полів потоку в прод-таблицях розрахунку (імена - з метаданих; лише SELECT DISTINCT).
        $meta = $conn.CreateCommand()
        $meta.CommandTimeout = 120
        $meta.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME LIKE 'AF[_]Air[_]Land[_]%' AND COLUMN_NAME IN ('Land_TypeFuel', 'Land_TypeFuelOrStream', 'Land_TypeStream', 'Land_Streams')"
        $pairs = New-Object System.Collections.Generic.List[object]
        $mr = $meta.ExecuteReader()
        try { while ($mr.Read()) { $pairs.Add(@([string]$mr['TABLE_NAME'], [string]$mr['COLUMN_NAME'])) } }
        finally { $mr.Dispose() }

        foreach ($p in $pairs) {
            if ($p[0] -notmatch '^[A-Za-z0-9_]{1,128}$' -or $p[1] -notmatch '^[A-Za-z0-9_]{1,128}$') { continue }
            $q = $conn.CreateCommand()
            $q.CommandTimeout = 120
            $q.CommandText = "SELECT DISTINCT CAST([$($p[1])] AS nvarchar(255)) AS Txt FROM dbo.[$($p[0])] WITH (NOLOCK) WHERE [$($p[1])] IS NOT NULL"
            $qr = $q.ExecuteReader()
            try { while ($qr.Read()) { $streamTexts.Add([string]$qr['Txt']) } }
            finally { $qr.Dispose() }
        }
    }
}
finally { $conn.Dispose() }

$plan = ConvertTo-LandHse400CompositionPlan -Rows ([object[]]$sourceRows.ToArray())
Write-Host "Джерело: рядків (місяців) $($sourceRows.Count); значень до довідника: $(@($plan.Items).Count); пропущено: $($plan.Skipped); дублів: $($plan.Duplicates); запасна C6_1_mass: $($plan.Fallbacks); EMercaptanV2 для HMB: $($plan.Overrides); псевдонімів C*_mass: $($plan.Aliases)."
if (@($plan.Items).Count -eq 0) { throw 'У джерелі немає жодного значення складу: нічого завантажувати.' }

$staticTexts = @(
    'Fuel gas (Makat) - Топливный газ (Makat)', 'Fuel gas (HMB) - Топливный газ (МТБ)', 'Fuel gas (Export) - Топливный газ (Товарный)',
    'Fuel Gas (Internal) - Топливный газ (Внутренний)', 'LPG (HMB) - СУГ (МТБ)', 'LPG (Internal) - СУГ (Внутренний)')
$streamPlan = ConvertTo-LandHse400StreamPlan -Texts ([string[]](@($staticTexts) + @($streamTexts)))
Write-Host "Словник потоків: записів $(@($streamPlan.Items).Count); текстів без збігу з правилом: $($streamPlan.Unmatched); дублів: $($streamPlan.Duplicates)."

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
$defs = Get-LandHse400RegistryDefinitions

function Initialize-HseRegistry($spec) {
    $list = Invoke-Ecr -Method GET -Path '/api/v1/registries'
    Assert-EcrOk $list 'GET registries'
    $def = @($list.Json) | Where-Object { $_.code -eq $spec.Code } | Select-Object -First 1
    $created = $false
    if ($null -eq $def) {
        $created = $true
        if ($dry) { Write-Host "[dry-run] створив би довідник $($spec.Code)"; return [pscustomobject]@{ Definition = $null; Created = $true; FieldsCreated = $true } }
        $c = Invoke-Ecr -Method POST -Path '/api/v1/registries' -Body @{ code = $spec.Code; nameL10n = @{ en = $spec.NameEn }; isTemporal = $false }
        Assert-EcrOk $c "POST registries ($($spec.Code))"
    }

    $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($spec.Code)/definition"
    Assert-EcrOk $d "GET definition ($($spec.Code))"
    $definition = $d.Json
    $have = @($definition.fields | ForEach-Object { [string]$_.code })
    $fieldsCreated = $false
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
                codeMode = $definition.codeMode; fields = $fields; keys = $keys; rules = @(); reason = 'Land HSE400 composition: fields and key'
            }
            Assert-EcrOk $put "PUT definition ($($spec.Code))"
            $d = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($spec.Code)/definition"
            Assert-EcrOk $d "GET definition ($($spec.Code))"
            $definition = $d.Json
        }
    }
    else {
        $need = @($spec.Fields | ForEach-Object { $_.Code })
        $missing = @($need | Where-Object { $have -notcontains $_ })
        if ($missing.Count -gt 0) {
            throw "Довідник $($spec.Code) уже має поля, але без: $($missing -join ', '). Опис не змінюється автоматично. Відмова."
        }
    }
    return [pscustomobject]@{ Definition = $definition; Created = $created; FieldsCreated = $fieldsCreated }
}

function Get-HseEntryCodes([string]$code) {
    $set = @{}
    $e = Invoke-Ecr -Method GET -Path "/api/v1/registries/$code/entries?asOf=$today"
    Assert-EcrOk $e "GET entries ($code)"
    # Значення - Id запису (для зміни наявного запису: batch upsert за `id`, а не за кодом - код наявного = 409).
    foreach ($x in @($e.Json)) { $set[([string]$x.code).ToUpperInvariant()] = [long]$x.id }
    return $set
}

function Get-HseProp($obj, [string]$name) {
    # Значення властивості або $null, без винятку під Set-StrictMode.
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Add-HseEntries([string]$code, [object[]]$rows, [switch]$ServerDryRun) {
    # rows: @{ clientRowId; code; display; values } - новий запис; @{ clientRowId; id; values } - зміна наявного
    # (batch upsert за id без code/name: ECR-REG-0409 entryCodeTaken стосується лише НОВОГО рядка з кодом).
    # Повертає (додано, відхилено, змінено, без змін).
    $added = 0; $rejected = 0; $updated = 0; $unchanged = 0
    $dryText = if ($ServerDryRun) { 'true' } else { 'false' }
    for ($i = 0; $i -lt $rows.Count; $i += $BatchSize) {
        $chunk = @($rows[$i..([Math]::Min($i + $BatchSize, $rows.Count) - 1)])
        # StrictMode (його вмикає EcrApi.ps1): у «нових» рядків немає властивості Id, у «наявних» - Code/Display;
        # доступ лише через Get-HseProp, пакети нових і наявних збираються окремими запитами (змішаний вхід - два POST).
        $items = @($chunk | ForEach-Object {
                $idValue = Get-HseProp $_ 'Id'
                if ($null -ne $idValue) { @{ clientRowId = $_.ClientRowId; op = 'upsert'; id = [long]$idValue; values = $_.Values } }
                else { @{ clientRowId = $_.ClientRowId; op = 'upsert'; code = (Get-HseProp $_ 'Code'); name = (Get-HseProp $_ 'Display'); values = $_.Values } }
            })
        $r = Invoke-Ecr -Method POST -Path "/api/v1/registries/$code/entries/batch?dryRun=$dryText" -Body @{ items = $items }
        Assert-EcrOk $r "POST entries/batch ($code)"
        $updated += [int](Get-HseProp $r.Json 'updated'); $unchanged += [int](Get-HseProp $r.Json 'unchanged')
        $jsonRows = @(Get-HseProp $r.Json 'rows')
        $errRows = @($jsonRows | Where-Object { $null -ne $_ -and (Get-HseProp $_ 'status') -eq 'error' })
        if ($ServerDryRun) {
            if ($errRows.Count -eq 0) { $added += [int](Get-HseProp $r.Json 'added'); continue }
        }
        elseif (Get-HseProp $r.Json 'applied') { $added += $chunk.Count; continue }
        # Пачка відхилена повністю: рахуємо за кодами помилок, без значень.
        $bad = $errRows
        $rejected += $chunk.Count
        $codes = @($bad | ForEach-Object { @(Get-HseProp $_ 'errors') | ForEach-Object { [string](Get-HseProp $_ 'errorCode') } } | Group-Object | ForEach-Object { "$($_.Name)×$($_.Count)" })
        Write-Warning "Пачка $code [$i..]: відхилено ($($codes -join ', '))."
    }
    return [pscustomobject]@{ Added = $added; Rejected = $rejected; Updated = $updated; Unchanged = $unchanged }
}

# 2. Довідники й записи.
$stream = Initialize-HseRegistry $defs.Stream
$comp = Initialize-HseRegistry $defs.Composition
$changes = 0
foreach ($r in @($stream, $comp)) { if ($r.Created) { $changes++ }; if ($r.FieldsCreated) { $changes++ } }

$haveStream = if ($null -ne $stream.Definition) { Get-HseEntryCodes $defs.Stream.Code } else { @{} }
$newStream = @($streamPlan.Items | Where-Object { -not $haveStream.ContainsKey($_.EntryCode.ToUpperInvariant()) })
$haveComp = if ($null -ne $comp.Definition) { Get-HseEntryCodes $defs.Composition.Code } else { @{} }
$newComp = @($plan.Items | Where-Object { -not $haveComp.ContainsKey($_.EntryCode.ToUpperInvariant()) })
# -RewriteAliased: наявні записи-псевдоніми теж йдуть upsert-ом (виправлення значення, що лягло як запасна).
$rewriteComp = if ($RewriteAliased) { @($plan.Items | Where-Object { $_.Aliased -and $haveComp.ContainsKey($_.EntryCode.ToUpperInvariant()) }) } else { @() }

function New-HseCompositionValues($item) {
    # Числа йдуть JSON-числами: рядок «1,234» сервер читає за мовою користувача (неоднозначно).
    return @{ YEAR = [int]$item.Year; MONTH = [int]$item.Month; STREAM = $item.Stream; INDICATOR = $item.Indicator
        VALUE = [decimal]::Parse($item.Value, [System.Globalization.CultureInfo]::InvariantCulture) }
}

$failed = $false
$streamAdded = 0; $compAdded = 0; $rewriteUpdated = 0; $rewriteUnchanged = 0
if ($dry -or $null -eq $stream.Definition -or $null -eq $comp.Definition) {
    $streamAdded = $newStream.Count; $compAdded = $newComp.Count
    # Перезапис псевдонімів у dry-run: серверний dryRun=true (транзакція відкочується) - показує, що змінилось би.
    if ($rewriteComp.Count -gt 0 -and $null -ne $comp.Definition -and $ConfirmStand) {
        $rrows = @($rewriteComp | ForEach-Object {
                [pscustomobject]@{ ClientRowId = $_.EntryCode; Id = $haveComp[$_.EntryCode.ToUpperInvariant()]; Values = (New-HseCompositionValues $_) } })
        $d = Add-HseEntries $defs.Composition.Code $rrows -ServerDryRun
        $rewriteUpdated = $d.Updated; $rewriteUnchanged = $d.Unchanged
        if ($d.Rejected) { $failed = $true }
    }
}
else {
    $rows = @($newStream | ForEach-Object { [pscustomobject]@{ ClientRowId = $_.EntryCode; Code = $_.EntryCode; Display = $_.Display; Values = @{ TEXT = $_.Text; PREFIX = $_.Prefix } } })
    if ($rows.Count) { $s = Add-HseEntries $defs.Stream.Code $rows; $streamAdded = $s.Added; if ($s.Rejected) { $failed = $true } }
    if (-not $failed) {
        $rows = @($newComp | ForEach-Object {
                [pscustomobject]@{ ClientRowId = $_.EntryCode; Code = $_.EntryCode; Display = $_.EntryCode; Values = (New-HseCompositionValues $_) } })
        if ($rows.Count) { $c = Add-HseEntries $defs.Composition.Code $rows; $compAdded = $c.Added; if ($c.Rejected) { $failed = $true } }
    }
    # Зміна наявних записів-псевдонімів: окремий пакет, за Id запису (без code/name); повтор -> «без змін».
    if (-not $failed -and $rewriteComp.Count -gt 0) {
        $rrows = @($rewriteComp | ForEach-Object {
                [pscustomobject]@{ ClientRowId = $_.EntryCode; Id = $haveComp[$_.EntryCode.ToUpperInvariant()]; Values = (New-HseCompositionValues $_) } })
        $w = Add-HseEntries $defs.Composition.Code $rrows
        $rewriteUpdated = $w.Updated; $rewriteUnchanged = $w.Unchanged
        if ($w.Rejected) { $failed = $true }
    }
}

Write-Host "Словник $($defs.Stream.Code): додано $streamAdded, наявних $(@($streamPlan.Items).Count - $newStream.Count)."
Write-Host "Склад $($defs.Composition.Code): додано $compAdded, наявних $(@($plan.Items).Count - $newComp.Count)."
if ($RewriteAliased) {
    if ($dry -and -not $ConfirmStand) { Write-Host "Псевдоніми наявних записів: до перезапису $($rewriteComp.Count); скільки з них зміниться - покаже -DryRun з -ConfirmStand (серверний dryRun=true)." }
    else { Write-Host "Псевдоніми наявних записів: до перезапису $($rewriteComp.Count); змінено $rewriteUpdated; без змін $rewriteUnchanged$(if ($dry) { ' (серверний dryRun, відкат)' })." }
}
Write-Host "Змін: $($changes + $streamAdded + $compAdded + $rewriteUpdated)$(if ($dry) { ' (dry-run, нічого не записано)' })."
if ($failed) { exit 1 }
