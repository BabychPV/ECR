<#
.SYNOPSIS
    Точково завантажує вхідні комірки Land з JSON повного набору в документи стенда: вид TypeFuel
    (Land_TypeFuel і ReleaseOnColdVentYesNo_M<мм>) або вид Gtu (GTU-входи T10_03/T11_03).

.DESCRIPTION
    Повний завантажувач їх не заповнював (колонки з'явилися після побудови стенда). Інструмент не створює документів
    і шапок: для кожного документа з -DocMap (ім'я -> id) і періоду читає таблицю, бере rowVersion рядків і шле
    PATCH /api/v1/documents/{id}/cells (origin UserEdit, confirmed) пачками. Значення довідників (паливо, Так/Ні)
    зіставляються із записами довідника колонки за текстом; не знайдене - lookupMiss, не вгадується.

    За замовчуванням (і з -DryRun) НІЧОГО не пише: читає файли -SourcePath і (з обліковим записом) таблиці стенда -
    перевіряє документи, таблиці й колонки та друкує лічильники. Запис - тільки з -Execute, явним -SqlConnection
    (локальний сервер, база EcrLand*) і -ConfirmStand `<адреса>/<база>`; перед записом виконується ВЕСЬ
    передперегляд: NO_DOC / NO_TABLE / відсутня колонка зупиняють запис до першого PATCH (відсутні колонки - лише з
    -AllowMissingColumns: тоді пишуться комірки наявних колонок, решта лічиться окремо).

    Код виходу 1: NO_DOC, NO_TABLE, ROW_MISSING, відсутня колонка (без -AllowMissingColumns), lookupMiss, відхилений
    рядок. Лише локальний API. У вивід не йде значень комірок: лише коди таблиць/колонок і кількості.

.PARAMETER Kind            TypeFuel | Gtu.
.PARAMETER SourcePath      Локальний каталог із JSON повного набору: <ім'я документа>.json (periods -> таблиці AF -> rows).
                           Дані прода лишаються поза репозиторієм; шлях - лише параметром.
.PARAMETER DocMap          JSON {"<ім'я документа>": <id>, ...} (рядок або шлях до файла): які документи й під якими id.
.PARAMETER BaseUrl         Адреса API стенда (обов'язково, без дефолту).
.PARAMETER ConfirmStand    Підтвердження стенда (обов'язково, без дефолту): адреса API, host:port або host; для запису -
                           `<адреса>/<база>` (наприклад localhost:5094/EcrLandUi).
.PARAMETER SqlConnection   Явний рядок підключення стенда (лише для назви бази в підтвердженні; дефолту немає).
.PARAMETER User            Обліковий запис (або $env:ECR_USER).
.PARAMETER Password        Пароль SecureString (або $env:ECR_PASSWORD).
.PARAMETER PeriodKey       Обмеження періодів Рік*100+Місяць (202601 або 202601,202602); без нього - усі з файла.
.PARAMETER Out             Файл звіту JSON (за бажання; без значень комірок).
.PARAMETER BatchRows       Рядків в одному PATCH (за замовчуванням 25).
.PARAMETER ModelPath        Модель Land (land-template.model.json); за замовчуванням з репозиторію. З неї береться перелік GTU-колонок
                           ПО-ТАБЛИЧНО (T10_03: Month + GTU1..4 = 21; T11_03: Month + GTU1..5 = 26).
.PARAMETER AllowNameMismatch  Не зупиняти на NAME_MISMATCH (ім'я документа на стенді інше, ніж ключ у -DocMap).
.PARAMETER AllowMissingColumns  Писати наявні колонки, хоч частини колонок плану на стенді немає (за замовчуванням - зупинка).
.PARAMETER Execute         Виконати запис (без нього - лише передперегляд).
.PARAMETER DryRun          Явно лише передперегляд (перемагає -Execute).

.EXAMPLE
    # Передперегляд (без запису):
    $pw = Read-Host 'Пароль ECR' -AsSecureString
    .\Load-LandInputCells.ps1 -Kind TypeFuel -SourcePath <каталог JSON> -DocMap '{"<імʼя1>":<id1>,"<імʼя2>":<id2>}' `
        -BaseUrl http://localhost:5094 -ConfirmStand localhost:5094 -User land-owner -Password $pw
    # Запис:
    .\Load-LandInputCells.ps1 -Kind TypeFuel -SourcePath <каталог JSON> -DocMap '{"<імʼя1>":<id1>}' -BaseUrl http://localhost:5094 `
        -User land-owner -Password $pw -SqlConnection 'Server=localhost;Database=EcrLandUi;Integrated Security=true' `
        -ConfirmStand localhost:5094/EcrLandUi -Execute
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('TypeFuel', 'Gtu')][string]$Kind,
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$DocMap,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [string]$SqlConnection,
    [string]$User,
    [securestring]$Password,
    $PeriodKey,
    [string]$Out,
    [int]$BatchRows = 25,
    [string]$ModelPath,
    [switch]$AllowMissingColumns,
    [switch]$AllowNameMismatch,
    [switch]$Execute,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandInputCells.Lib.ps1')
. (Join-Path $here 'LandStandSession.Lib.ps1')
Set-StrictMode -Off

$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)
# Охорона стенда - ДО читання файлів і будь-якого виклику.
$null = Initialize-LandStandSession -BaseUrl $BaseUrl -ConfirmStand $ConfirmStand -SqlConnection $SqlConnection -ForWrite:$exec
if ($BatchRows -lt 1 -or $BatchRows -gt 100) { throw '-BatchRows: 1..100.' }

$docIds = ConvertFrom-LandDocMap -Json $DocMap
$periods = @()
if ($null -ne $PeriodKey) { $periods = @(ConvertTo-DocumentIdList $PeriodKey) }
if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) { throw "Каталог -SourcePath '$SourcePath' не існує." }

$modelColumns = $null
$orphans = @{}
if ($Kind -eq 'Gtu') {
    $modelFile = $ModelPath
    if (-not $modelFile) { $modelFile = Join-Path $here '..\..\docs\delivery\reference-data\land-template\land-template.model.json' }
    $modelDoc = Get-Content -LiteralPath $modelFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $modelColumns = Get-LandModelTableColumns -Model $modelDoc -TableCodes @((Get-LandInputCellKind -Name 'Gtu').SheetOf.Keys)
}

$plan = [ordered]@{}
foreach ($docName in @($docIds.Keys)) {
    $file = Join-Path $SourcePath "$docName.json"
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "У каталозі -SourcePath немає файла '$docName.json'." }
    $parsed = ConvertFrom-LandLargeJson -Text ([IO.File]::ReadAllText((Resolve-Path -LiteralPath $file).ProviderPath))
    $part = New-LandInputCellPlan -KindName $Kind -DocumentName $docName -Document $parsed -Periods $periods -ModelColumns $modelColumns -SourceWithoutTarget $orphans
    foreach ($k in $part.Keys) { $plan[$k] = $part[$k] }
}

$summary = @(Get-LandInputCellPlanSummary -Plan $plan)
$sumRows = [int](($summary | Measure-Object Rows -Sum).Sum)
$sumCells = [int](($summary | Measure-Object Cells -Sum).Sum)
Write-Host "PLAN ($Kind): table-periods $($summary.Count), rows $sumRows, cells $sumCells (TypeFuel $(($summary | Measure-Object TypeFuel -Sum).Sum), ColdVent $(($summary | Measure-Object ColdVent -Sum).Sum), Gtu $(($summary | Measure-Object Gtu -Sum).Sum))."
if ($orphans.Count -gt 0) {
    Write-Host ("WARN source-without-target-column (table/column=cells, no values; the model table has no such column, cells are not loaded): " +
        ((@($orphans.Keys | Sort-Object) | ForEach-Object { "$_=$($orphans[$_])" }) -join ', '))
}
if ($summary.Count -eq 0) { throw 'План порожній: у файлах немає потрібних таблиць/значень.' }

$haveCredentials = (-not [string]::IsNullOrWhiteSpace($User)) -or (-not [string]::IsNullOrWhiteSpace($env:ECR_USER))
if (-not $haveCredentials) {
    if ($exec) { throw 'Запис потребує -User/-Password (або ECR_USER/ECR_PASSWORD).' }
    $summary | Sort-Object Key | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host 'PLAN ONLY (files; no -User, so the stand was not read: NO_DOC/NO_TABLE/missing columns NOT checked).'
    return
}

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
$def = Get-LandInputCellKind -Name $Kind

# Довідники: id -> код і ліниві карти записів на (реєстр, дата).
$regList = Invoke-Ecr -Method GET -Path '/api/v1/registries'
Assert-EcrOk $regList 'GET registries'
$regCodeById = @{}
$regIdByCode = @{}
foreach ($rg in @($regList.Json)) { $regCodeById[[int]$rg.id] = [string]$rg.code; $regIdByCode[[string]$rg.code] = [int]$rg.id }
$lookupCache = @{}
function Get-LookupMap([int]$RegistryId, [string]$AsOfDate) {
    $ck = "$RegistryId|$AsOfDate"
    if (-not $lookupCache.ContainsKey($ck)) {
        $code = $regCodeById[$RegistryId]
        $lookupCache[$ck] = @{}
        if ($code) {
            $er = Invoke-Ecr -Method GET -Path "/api/v1/registries/$code/entries?limit=1000&asOf=$AsOfDate"
            if ($er.Status -eq 200) { $lookupCache[$ck] = New-LandLookupMap -Entries @($er.Json) }
        }
    }
    return $lookupCache[$ck]
}

# ===== Передперегляд (читання): документ -> таблиця -> колонки -> рядки; нічого не пишеться. =====
$prepared = [ordered]@{}
$nameState = @{}
$results = New-Object System.Collections.ArrayList
$missingColumnNames = @{}
foreach ($key in @($plan.Keys | Sort-Object)) {
    $docName, $periodText, $tableCode = $key -split '\|'
    $periodNumber = [int]$periodText
    $st = [ordered]@{ key = $key; rows = @($plan[$key]).Count; cellsSent = 0; rowsOk = 0; rowsRejected = 0; lookupMiss = 0
        missingColumnCells = 0; missingColumns = ''; codes = ''; skipped = '' }
    $docId = [int]$docIds[$docName]
    if (-not $nameState.ContainsKey($docName)) { $nameState[$docName] = Get-LandDocumentNameState -DocumentId $docId -ExpectedName $docName }
    if ($nameState[$docName] -eq 'NO_DOC') { $st.skipped = 'NO_DOC'; [void]$results.Add([pscustomobject]$st); continue }
    if ($nameState[$docName] -eq 'NAME_MISMATCH' -and -not $AllowNameMismatch) { $st.skipped = 'NAME_MISMATCH'; [void]$results.Add([pscustomobject]$st); continue }
    $tabs = Invoke-LandEcrRetry -HttpMethod GET -ApiPath "/api/v1/documents/$docId/tables?periodKey=$periodNumber"
    if ($tabs.Status -ne 200) { $st.skipped = 'NO_DOC'; $st.codes = "HTTP$($tabs.Status);"; [void]$results.Add([pscustomobject]$st); continue }
    $tableInfo = @($tabs.Json) | Where-Object { $_.sheetCode -eq $def.SheetOf[$tableCode] -and $_.tableCode -eq $tableCode } | Select-Object -First 1
    if (-not $tableInfo) { $st.skipped = 'NO_TABLE'; [void]$results.Add([pscustomobject]$st); continue }
    $tableInstanceId = [int]$tableInfo.tableInstanceId
    $cur = Invoke-LandEcrRetry -HttpMethod GET -ApiPath "/api/v1/documents/$docId/tables/${tableInstanceId}?periodKey=$periodNumber"
    if ($cur.Status -ne 200) { $st.skipped = 'NO_TABLE'; $st.codes = "HTTP$($cur.Status);"; [void]$results.Add([pscustomobject]$st); continue }
    $columns = @{}
    foreach ($c in @($cur.Json.columns)) { $columns[[string]$c.code] = $c }
    $versions = @{}
    foreach ($er in @($cur.Json.rows)) { $versions[[string]$er.rowKey] = [string]$er.rowVersion }
    $asOf = '{0}-{1}-15' -f $periodText.Substring(0, 4), $periodText.Substring(4, 2)

    $patch = New-Object System.Collections.ArrayList
    $absent = @{}
    foreach ($r in $plan[$key]) {
        if (-not $versions.ContainsKey($r.RowKey)) { $st.rowsRejected++; $st.codes += 'ROW_MISSING;'; continue }
        $cells = New-Object System.Collections.ArrayList
        foreach ($c in $r.Cells) {
            $colDef = $columns[$c.Col]
            if ($null -eq $colDef) { $st.missingColumnCells++; $absent[$c.Col] = $true; $missingColumnNames["$tableCode/$($c.Col)"] = $true; continue }
            if ($c.Kind -eq 'Gtu') {
                if ($c.Col -eq $def.TextColumn) { [void]$cells.Add(@{ columnCode = $c.Col; value = [string]$c.Text }); continue }
                $num = ConvertTo-LandInputNumber -Text $c.Text
                if ($null -eq $num) { $st.lookupMiss++ } else { [void]$cells.Add(@{ columnCode = $c.Col; value = $num }) }
                continue
            }
            $regId = 0
            if ($colDef.PSObject.Properties.Match('lookupRegistryDefId').Count -gt 0 -and $colDef.lookupRegistryDefId) { $regId = [int]$colDef.lookupRegistryDefId }
            else {
                $fallback = if ($c.Kind -eq 'TypeFuel') { $def.TypeFuelRegistry } else { $def.ColdVentRegistry }
                if ($regIdByCode.ContainsKey($fallback)) { $regId = [int]$regIdByCode[$fallback] }
            }
            $entryId = $null
            if ($regId -gt 0) { $entryId = Resolve-LandLookupId -Map (Get-LookupMap -RegistryId $regId -AsOfDate $asOf) -Text $c.Text }
            if ($null -eq $entryId) { $st.lookupMiss++; continue }
            [void]$cells.Add(@{ columnCode = $c.Col; value = $entryId })
        }
        if ($cells.Count -gt 0) { [void]$patch.Add(@{ rowKey = $r.RowKey; baseVersion = $versions[$r.RowKey]; cells = $cells }) }
    }
    $st.missingColumns = (@($absent.Keys | Sort-Object) -join ',')
    $prepared[$key] = [pscustomobject]@{ DocId = $docId; TableInstanceId = $tableInstanceId; PeriodNumber = $periodNumber; Patch = $patch }
    [void]$results.Add([pscustomobject]$st)
}

# ===== Підсумок передперегляду (без значень) =====
$noDoc = @($results | Where-Object { $_.skipped -eq 'NO_DOC' }).Count
$noTable = @($results | Where-Object { $_.skipped -eq 'NO_TABLE' }).Count
$nameMismatch = @($results | Where-Object { $_.skipped -eq 'NAME_MISMATCH' }).Count
$rowMissing = @($results | Where-Object { $_.codes -like '*ROW_MISSING*' }).Count
$missingCells = [int](($results | Measure-Object missingColumnCells -Sum).Sum)
$lookupMisses = [int](($results | Measure-Object lookupMiss -Sum).Sum)
$readyCells = [int](($prepared.Values | ForEach-Object { @($_.Patch | ForEach-Object { $_.cells.Count }) } | Measure-Object -Sum).Sum)
foreach ($s in $results) {
    Write-Host ('{0} rows={1} skipped={2} missingColumnCells={3} [{4}] lookupMiss={5} {6}' -f $s.key, $s.rows, $(if ($s.skipped) { $s.skipped } else { '-' }), $s.missingColumnCells, $s.missingColumns, $s.lookupMiss, $s.codes)
}
Write-Host "PREVIEW: ready cells $readyCells; NO_DOC $noDoc; NO_TABLE $noTable; NAME_MISMATCH $nameMismatch; ROW_MISSING $rowMissing; skipped-missing-column cells $missingCells; lookupMiss-or-not-number $lookupMisses."
if ($missingColumnNames.Count -gt 0) {
    Write-Host ("MISSING COLUMNS (table/column, no values; compare with the model bridges): " + ((@($missingColumnNames.Keys) | Sort-Object) -join ', '))
}
$blocked = ($noDoc + $noTable + $nameMismatch + $rowMissing) -gt 0 -or ($missingCells -gt 0 -and -not $AllowMissingColumns) -or $lookupMisses -gt 0

if ($exec) {
    if ($blocked) {
        Write-Host 'WRITE STOPPED before the first PATCH: the preview has findings (see above).'
    }
    else {
        $utcStart = (Get-Date).ToUniversalTime()
        foreach ($st in $results) {
            if (-not $prepared.Contains($st.key)) { continue }
            $job = $prepared[$st.key]
            for ($i = 0; $i -lt $job.Patch.Count; $i += $BatchRows) {
                $slice = @($job.Patch[$i..([Math]::Min($i + $BatchRows, $job.Patch.Count) - 1)])
                $body = @{ tableInstanceId = $job.TableInstanceId; periodKey = $job.PeriodNumber; origin = 'UserEdit'; confirmed = $true; rows = $slice }
                $pr = Invoke-LandEcrRetry -HttpMethod PATCH -ApiPath "/api/v1/documents/$($job.DocId)/cells" -RequestBody $body
                if ($pr.Status -eq 200) {
                    $st.rowsOk += $slice.Count
                    $st.cellsSent += [int](@($slice | ForEach-Object { $_.cells.Count }) | Measure-Object -Sum).Sum
                }
                else { $st.rowsRejected += $slice.Count; $st.codes += ("HTTP$($pr.Status) " + (Get-EcrErrorCode $pr) + ';') }
            }
            Write-Host ('{0} ok={1} rej={2} cells={3} {4}' -f $st.key, $st.rowsOk, $st.rowsRejected, $st.cellsSent, $st.codes)
        }
        Write-Host "WRITE_START_UTC=$($utcStart.ToString('o')) WRITE_END_UTC=$((Get-Date).ToUniversalTime().ToString('o'))"
        Write-Host "TOTAL rowsOk=$([int](($results | Measure-Object rowsOk -Sum).Sum)) rejected=$([int](($results | Measure-Object rowsRejected -Sum).Sum)) cells=$([int](($results | Measure-Object cellsSent -Sum).Sum))"
    }
}
else {
    Write-Host 'PLAN (no writes). To write: -Execute -SqlConnection <local, EcrLand*> -ConfirmStand <address>/<database>.'
}
if ($Out) { $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Out -Encoding UTF8 }
$rejectedRows = [int](($results | Measure-Object rowsRejected -Sum).Sum)
if ($blocked -or $rejectedRows -gt 0) { exit 1 }
