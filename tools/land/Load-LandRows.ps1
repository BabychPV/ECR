<#
.SYNOPSIS
    Row loader of the FULL Land sample (all air tables of all documents and periods) into documents of a LOCAL stand.
    Rows get table-qualified keys p<doc>-<TableCode>-<seq:0000> (unique across the tables of a document, LAND-5094).
.DESCRIPTION
    Replaces the out-of-repo Load-Full.ps1. Production values are never in git: only the sample JSON files
    (LAND-PROD-<n>.json, LOCAL, outside the repository) carry them; the output of this script has counters and codes only.

    Safety (same as Load-LandInputCells.ps1):
      * -BaseUrl and -ConfirmStand are mandatory, no defaults; the API must be local.
      * No -Execute (or -DryRun) = preview: nothing is written. With -User the stand is READ (documents, tables, columns,
        existing row versions, registries); without it the plan is built from the files and the model only.
      * -Execute needs -User/-Password and an explicit -SqlConnection (local server, EcrLand* database).
      * Exit code 1 on NO_DOC, NO_TABLE (unknown prod table / table absent in the document), a mapped column missing
        in the table, rejected rows; the write stops before the first PATCH when the preview has such findings.
    Water (AF_Water_Land_07/07a) and waste (AF_Waste_Land_08) tables use template item keys and are NOT loaded here:
    they are counted as SKIPPED_WATER_WASTE (the old Load-Full.ps1 branch stays out of the repository).
.PARAMETER SourcePath       directory with <document name>.json files (periods -> AF table -> rows)
.PARAMETER DocMap           JSON {"LAND-PROD-7": 12, ...}: document name -> document id on the stand
.PARAMETER BaseUrl          local API address (mandatory)
.PARAMETER ConfirmStand     stand confirmation: address, host:port or host[/database] (mandatory)
.PARAMETER SqlConnection    explicit local connection string (needed for -Execute only)
.PARAMETER User / Password  ECR account (or ECR_USER / ECR_PASSWORD); without them a files+model preview
.PARAMETER PeriodKey        only these periods (YYYYMM, list)
.PARAMETER MaxRowsPerTable  0 (default) = NO cap; > 0 = explicit cap per table-period (counted as capped)
.PARAMETER RowKeyFormat     Table (default) = p<doc>-<TableCode>-<seq>; Legacy = p<doc>-<seq> (diagnostics only)
.PARAMETER AllowNameMismatch  do not stop on NAME_MISMATCH (document name on the stand differs from the -DocMap key)
.PARAMETER SkipUnknownTables  unknown prod tables are skipped with a WARN and counted (SKIPPED_UNKNOWN) instead of NO_TABLE (exit 1); NOT the default
.PARAMETER IncludeTables     only these template table codes (T9_04,T9_07); others are counted NOT_INCLUDED
.PARAMETER BatchRows        rows per PATCH (default 25)
.PARAMETER ModelPath        land-template.model.json (default: the repository one)
.PARAMETER Out              JSON report (counters, no values)
.PARAMETER Execute          write (after a clean preview)
.PARAMETER DryRun           explicit preview (wins over -Execute)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$DocMap,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [string]$SqlConnection,
    [string]$User,
    [securestring]$Password,
    $PeriodKey,
    [int]$MaxRowsPerTable = 0,
    [ValidateSet('Table', 'Legacy')][string]$RowKeyFormat = 'Table',
    [int]$BatchRows = 25,
    [string]$IncludeTables,
    [switch]$AllowNameMismatch,
    [switch]$SkipUnknownTables,
    [string]$ModelPath,
    [string]$Out,
    [switch]$Execute,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandStandSession.Lib.ps1')
. (Join-Path $here 'LandInputCells.Lib.ps1')
. (Join-Path $here 'LandRows.Lib.ps1')
Set-StrictMode -Off

$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)
# Stand guard BEFORE any file read or call.
$null = Initialize-LandStandSession -BaseUrl $BaseUrl -ConfirmStand $ConfirmStand -SqlConnection $SqlConnection -ForWrite:$exec
if ($BatchRows -lt 1 -or $BatchRows -gt 100) { throw '-BatchRows: 1..100.' }
if ($MaxRowsPerTable -lt 0) { throw '-MaxRowsPerTable: 0 (no cap) or a positive number.' }

$docIds = ConvertFrom-LandDocMap -Json $DocMap
$periodFilter = @()
if ($null -ne $PeriodKey) { $periodFilter = @(ConvertTo-DocumentIdList $PeriodKey) }
if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) { throw "-SourcePath '$SourcePath' is not a directory." }
$modelFile = $ModelPath
if (-not $modelFile) { $modelFile = Join-Path $here '..\..\docs\delivery\reference-data\land-template\land-template.model.json' }
$modelDoc = Get-Content -LiteralPath $modelFile -Raw -Encoding UTF8 | ConvertFrom-Json
$aliasByTable = Get-LandRowsModelAliasMap -Model $modelDoc

$haveCredentials = (-not [string]::IsNullOrWhiteSpace($User)) -or (-not [string]::IsNullOrWhiteSpace($env:ECR_USER))
if ($exec -and -not $haveCredentials) { throw 'Write needs -User/-Password (or ECR_USER/ECR_PASSWORD).' }
$lookupResolver = $null
$regCodeById = @{}
if ($haveCredentials) {
    Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
    $regList = Invoke-Ecr -Method GET -Path '/api/v1/registries'
    Assert-EcrOk $regList 'GET registries'
    foreach ($rg in @($regList.Json)) { $regCodeById[[int]$rg.id] = [string]$rg.code }
    $lookupCache = @{}
    $lookupResolver = {
        param($RegistryId, $Text)
        if ($null -eq $RegistryId -or -not $regCodeById.ContainsKey([int]$RegistryId)) { return $null }
        $ck = [string]$RegistryId
        if (-not $lookupCache.ContainsKey($ck)) {
            $lookupCache[$ck] = @{}
            $er = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($regCodeById[[int]$RegistryId])/entries?limit=1000&asOf=2026-06-15"
            if ($er.Status -eq 200) { $lookupCache[$ck] = New-LandLookupMap -Entries @($er.Json) }
        }
        return (Resolve-LandLookupId -Map $lookupCache[$ck] -Text ([string]$Text))
    }.GetNewClosure()
}

$results = New-Object System.Collections.ArrayList
$prepared = New-Object System.Collections.ArrayList
$nameState = @{}
$counters = @{ NO_DOC = 0; NO_TABLE = 0; NAME_MISMATCH = 0; SKIPPED_WATER_WASTE = 0; MISSING_COLUMN = 0; SKIPPED_UNKNOWN = 0; NOT_INCLUDED = 0 }
$includeList = @()
if (-not [string]::IsNullOrWhiteSpace($IncludeTables)) { $includeList = @(ConvertTo-LandStringList $IncludeTables) }
$unmappedAll = @{}

foreach ($docName in @($docIds.Keys | Sort-Object)) {
    $file = Join-Path $SourcePath "$docName.json"
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "No '$docName.json' in -SourcePath." }
    if ($docName -notmatch '(\d+)$') { throw "Document name '$docName' has no trailing number: the row key prefix cannot be built." }
    $docNumber = [int]$Matches[1]
    $docId = [int]$docIds[$docName]
    $parsed = ConvertFrom-LandLargeJson -Text ([IO.File]::ReadAllText((Resolve-Path -LiteralPath $file).ProviderPath))
    $periodsNode = $parsed['periods']
    if ($null -eq $periodsNode) { throw "'$docName.json' has no periods node." }

    if ($haveCredentials -and -not $nameState.ContainsKey($docName)) {
        $nameState[$docName] = Get-LandDocumentNameState -DocumentId $docId -ExpectedName $docName
    }
    $docBlocked = Get-LandRowsDocBlock -NameState $(if ($nameState.ContainsKey($docName)) { $nameState[$docName] } else { '' }) -AllowNameMismatch:$AllowNameMismatch

    foreach ($pk in @($periodsNode.Keys | Sort-Object)) {
        $periodNumber = [int]($pk.Replace('-', ''))
        $monthNo = [int]$pk.Substring(5, 2)
        if ($periodFilter.Count -gt 0 -and $periodFilter -notcontains $periodNumber) { continue }
        $tablesInfo = $null
        if ($haveCredentials -and -not $docBlocked) {
            $tabs = Invoke-LandEcrRetry -HttpMethod GET -ApiPath "/api/v1/documents/$docId/tables?periodKey=$periodNumber"
            if ($tabs.Status -eq 200) { $tablesInfo = @($tabs.Json) } else { $docBlocked = 'NO_DOC' }
        }
        foreach ($prodTable in @($periodsNode[$pk].Keys | Sort-Object)) {
            $row = [ordered]@{ doc = $docName; period = $periodNumber; prodTable = $prodTable; table = ''; prodRows = 0; rows = 0; cells = 0
                capped = 0; unmapped = ''; missingColumns = ''; sourceWithoutColumn = ''; lookupMiss = 0; lookupUnresolved = 0; dupCells = 0; mixedText = 0; mixedNumber = 0; type3InMixed = 0
                skipped = ''; rowsOk = 0; rowsRejected = 0; codes = ''; rejectReasons = '' }
            $tableKey = Get-LandRowsTableKey -ProdTable $prodTable
            $srcRows = @($periodsNode[$pk][$prodTable]['rows'])
            $row.prodRows = $srcRows.Count
            if ($docBlocked) { $row.skipped = $docBlocked; $counters[$docBlocked]++; [void]$results.Add([pscustomobject]$row); continue }
            if (-not $tableKey -and $SkipUnknownTables) { $row.skipped = 'SKIPPED_UNKNOWN'; $counters.SKIPPED_UNKNOWN++; [void]$results.Add([pscustomobject]$row); continue }
            if ($tableKey -and $includeList.Count -gt 0 -and $includeList -notcontains ($tableKey -split '\|')[1]) { $row.skipped = 'NOT_INCLUDED'; $counters.NOT_INCLUDED++; [void]$results.Add([pscustomobject]$row); continue }
            if (-not $tableKey) { $row.skipped = 'NO_TABLE'; $row.codes = 'UNKNOWN_PROD_TABLE;'; $counters.NO_TABLE++; [void]$results.Add([pscustomobject]$row); continue }
            if (Test-LandRowsSpecialTable -TableKey $tableKey) { $row.skipped = 'SKIPPED_WATER_WASTE'; $counters.SKIPPED_WATER_WASTE++; [void]$results.Add([pscustomobject]$row); continue }
            $tableCode = ($tableKey -split '\|')[1]
            $sheetCode = ($tableKey -split '\|')[0]
            $row.table = $tableKey
            $columnMap = $null; $tableInstanceId = 0; $versions = @{}
            if ($haveCredentials) {
                $info = @($tablesInfo) | Where-Object { $_.sheetCode -eq $sheetCode -and $_.tableCode -eq $tableCode } | Select-Object -First 1
                if (-not $info) { $row.skipped = 'NO_TABLE'; $counters.NO_TABLE++; [void]$results.Add([pscustomobject]$row); continue }
                $tableInstanceId = [int]$info.tableInstanceId
                $cur = Invoke-LandEcrRetry -HttpMethod GET -ApiPath "/api/v1/documents/$docId/tables/${tableInstanceId}?periodKey=$periodNumber"
                if ($cur.Status -ne 200) { $row.skipped = 'NO_TABLE'; $row.codes = "HTTP$($cur.Status);"; $counters.NO_TABLE++; [void]$results.Add([pscustomobject]$row); continue }
                $columnMap = ConvertTo-LandRowsStandColumnMap -StandColumns $cur.Json.columns
                foreach ($er in @($cur.Json.rows)) { $versions[[string]$er.rowKey] = [string]$er.rowVersion }
            }
            else {
                $columnMap = Get-LandRowsModelColumns -Model $modelDoc -TableKey $tableKey
                if ($null -eq $columnMap) { $row.skipped = 'NO_TABLE'; $row.codes = 'NOT_IN_MODEL;'; $counters.NO_TABLE++; [void]$results.Add([pscustomobject]$row); continue }
            }
            $tableAliases = $null
            if ($aliasByTable.ContainsKey($tableKey)) { $tableAliases = $aliasByTable[$tableKey] }
            $tp = New-LandRowsTablePlan -DocNumber $docNumber -TableCode $tableCode -MonthNo $monthNo -SourceRows $srcRows -ColumnMap $columnMap `
                -TableAliases $tableAliases -LookupResolver $lookupResolver -KeyFormat $RowKeyFormat -RowLimit $MaxRowsPerTable
            foreach ($pr in $tp.Rows) { if ($versions.ContainsKey($pr.rowKey)) { $pr.baseVersion = $versions[$pr.rowKey] } }
            $row.rows = $tp.Rows.Count; $row.cells = $tp.Cells; $row.capped = $tp.Capped
            $row.lookupMiss = $tp.LookupMiss; $row.lookupUnresolved = $tp.LookupUnresolved; $row.dupCells = $tp.DupCells; $row.mixedText = $tp.MixedText; $row.mixedNumber = $tp.MixedNumber; $row.type3InMixed = $tp.Type3InMixed
            $row.unmapped = ($tp.Unmapped -join ',')
            $row.missingColumns = ($tp.MissingTargets -join ',')
            $row.sourceWithoutColumn = ((Get-LandRowsSourceWithoutColumn -ColumnMap $columnMap -FilledColumns @($tp.Rows | ForEach-Object { $_.cells | ForEach-Object { $_.columnCode } })) -join ',')
            if ($tp.MissingTargets.Count -gt 0) { $counters.MISSING_COLUMN += $tp.MissingTargets.Count }
            foreach ($u in $tp.Unmapped) { $unmappedAll["$tableCode/$u"] = 1 }
            [void]$prepared.Add([pscustomobject]@{ Key = "$docName|$periodNumber|$tableCode"; DocId = $docId; TableInstanceId = $tableInstanceId; PeriodNumber = $periodNumber; Patch = $tp.Rows; Result = $row })
            [void]$results.Add([pscustomobject]$row)
        }
    }
}

# ===== Preview summary: counters and codes only =====
foreach ($s in $results) {
    if ($s.skipped) { Write-Host ('{0}/{1}/{2} skipped={3} {4}' -f $s.doc, $s.period, $s.prodTable, $s.skipped, $s.codes); continue }
    Write-Host ('{0}/{1}/{2} -> {3}: prodRows={4} rows={5} cells={6} capped={7} unmapped=[{8}] missingColumns=[{9}] sourceWithoutColumn=[{10}] lookupMiss={11} lookupUnresolved={12}' -f `
            $s.doc, $s.period, $s.prodTable, $s.table, $s.prodRows, $s.rows, $s.cells, $s.capped, $s.unmapped, $s.missingColumns, $s.sourceWithoutColumn, $s.lookupMiss, $s.lookupUnresolved)
}
$totRows = [int](($results | Measure-Object rows -Sum).Sum)
$totCells = [int](($results | Measure-Object cells -Sum).Sum)
$totCapped = [int](($results | Measure-Object capped -Sum).Sum)
$totMiss = [int](($results | Measure-Object lookupMiss -Sum).Sum)
$totMixedText = [int](($results | Measure-Object mixedText -Sum).Sum)
$totMixedNumber = [int](($results | Measure-Object mixedNumber -Sum).Sum)
$totType3 = [int](($results | Measure-Object type3InMixed -Sum).Sum)
if ($totType3 -gt 0) { Write-Warning "NAE type3 of C11_10 in T9_04/T9_07: $totType3 rows - OperatingTime is a String column, type3 reads it as a number (D-423)." }
$modeText = if ($haveCredentials) { 'stand read' } else { 'files+model only (no -User: NO_DOC / lookups NOT checked)' }
Write-Host "PREVIEW ($modeText): table-periods $($results.Count), rows $totRows, cells $totCells, capped $totCapped, NO_DOC $($counters.NO_DOC), NO_TABLE $($counters.NO_TABLE), NAME_MISMATCH $($counters.NAME_MISMATCH), MISSING_COLUMN $($counters.MISSING_COLUMN), SKIPPED_WATER_WASTE $($counters.SKIPPED_WATER_WASTE), SKIPPED_UNKNOWN $($counters.SKIPPED_UNKNOWN), NOT_INCLUDED $($counters.NOT_INCLUDED), lookupMiss $totMiss, operatingTimeText $totMixedText, operatingTimeNumber $totMixedNumber, naeType3InMixed $totType3, rowKeyFormat $RowKeyFormat, maxRowsPerTable $MaxRowsPerTable."
if ($counters.SKIPPED_UNKNOWN -gt 0) { Write-Host "WARN -SkipUnknownTables: $($counters.SKIPPED_UNKNOWN) table-periods of unknown prod tables were NOT loaded." }
if ($unmappedAll.Count -gt 0) { Write-Host ('SOURCE WITHOUT TARGET (table/column, no values): ' + ((@($unmappedAll.Keys) | Sort-Object) -join ', ')) }

$blocked = ($counters.NO_DOC + $counters.NO_TABLE + $counters.MISSING_COLUMN + $counters.NAME_MISMATCH) -gt 0

if ($exec) {
    if ($blocked) { Write-Host 'WRITE STOPPED before the first PATCH: the preview has findings (see above).' }
    else {
        foreach ($job in $prepared) {
            if ($job.Patch.Count -eq 0) { continue }
            for ($i = 0; $i -lt $job.Patch.Count; $i += $BatchRows) {
                $slice = @($job.Patch[$i..([Math]::Min($i + $BatchRows, $job.Patch.Count) - 1)])
                $body = @{ tableInstanceId = $job.TableInstanceId; periodKey = $job.PeriodNumber; origin = 'UserEdit'; confirmed = $true; rows = $slice }
                $pr = Invoke-LandEcrRetry -HttpMethod PATCH -ApiPath "/api/v1/documents/$($job.DocId)/cells" -RequestBody $body
                if ($pr.Status -eq 200) { $job.Result.rowsOk += $slice.Count }
                else { $job.Result.rowsRejected += $slice.Count; $job.Result.codes += ("HTTP$($pr.Status) " + (Get-EcrErrorCode $pr) + ';') }
                if ($pr.Status -eq 422) {
                    $rr = Get-LandRowsRejectReasons -Response $pr
                    if ($rr) { $job.Result.rejectReasons += $rr + ';'; Write-Host ('rejected: ' + $rr) }
                }
            }
            Write-Host ('{0} ok={1} rej={2} {3}' -f $job.Key, $job.Result.rowsOk, $job.Result.rowsRejected, $job.Result.codes)
        }
        Write-Host "TOTAL rowsOk=$([int](($results | Measure-Object rowsOk -Sum).Sum)) rejected=$([int](($results | Measure-Object rowsRejected -Sum).Sum))"
    }
}
else { Write-Host 'PLAN (no writes). To write: -Execute -User ... -SqlConnection <local, EcrLand*> -ConfirmStand <address>/<database>.' }
if ($Out) { $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Out -Encoding UTF8 }
$rejectedTotal = [int](($results | Measure-Object rowsRejected -Sum).Sum)
if ($blocked -or $rejectedTotal -gt 0) { exit 1 }
