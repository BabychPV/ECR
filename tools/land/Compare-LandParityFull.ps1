<#
.SYNOPSIS
    FULL-set parity: results STORED by the legacy system <-> ECR results on the same inputs, for all documents,
    all periods and several legacy EFIDs (the repeat full parity after a new template version).
.DESCRIPTION
    READ-ONLY (no API call, no write to a database). Inputs (all LOCAL, outside the repository):
      -JsonDir     full-set sample files LAND-PROD-<n>.json: {doc, periods: {"2026-01": {<AF_Air_Land_*>: {columns, rows}}}},
                   rows carry _efid and the input columns (the same files are the loader input)
      -OracleTsv   legacy stored results from Export-LandParityOracleFull.ps1 (holds values: never commit)
      -SqlConnection  local stand database EcrLand* (Current calculation runs); or -StateFile (offline JSON, tests)
    Rows are aligned BY POSITION: sample row i of a table <-> ECR rowKey p<doc>-<i:0000>. The methodology -> table map
    is taken from cfg.CalculationBinding; -MethodologyTable adds entries for methodologies that have no binding yet
    (C08 / C11 / C12). Classes and tolerances are those of Compare-LandParity.ps1 (MATCH, MATCH_ROUNDING_LOSS, NEAR,
    MISMATCH_*, MISSING_*, MISSING_ROW, MISSING_OUTPUT, ECR_ONLY*, METHODOLOGY_DIFF, LEGACY_DUP_CONFLICT,
    LEGACY_STATUS_SKIP, AMBIGUOUS_KEY) plus:
      ROW_NOT_LOADED_CAPPED   the legacy row exists, the ECR document has no such row, and the table holds
                              MaxDynamicRows rows (template row limit; LP-01)
      ROW_NOT_LOADED_NO_CELLS the legacy row exists, the ECR document has no such row, the limit is not reached
      (every MISSING_* / ROW_NOT_LOADED_* class gets the suffix _ZERO when the legacy value is 0)
    -AggregateBy NaePhase: an ambiguous key (the legacy result has no Land_No; several Land_No share one NAE + phase)
    is compared as a SUM over the group's ECR rows, one cell per (group, methodology, output). Aggregate classes carry
    the prefix AGG_ (AGG_MATCH, AGG_MATCH_ROUNDING_LOSS, AGG_NEAR, AGG_MISMATCH*, AGG_MISSING_*, AGG_ROW_NOT_LOADED_*,
    AGG_ECR_ONLY*); a sum that does not match while some group rows are not loaded is INCOMPLETE_ROW_NOT_LOADED_*.
    The storage tolerance is multiplied by the number of summed rows.
    Output has NO values: identifiers, classes and relative errors only (<OutPrefix>.summary.json, <OutPrefix>.cells.tsv).
.PARAMETER Months            only these months of every document (default: all); "3" or "3,4" or "3;4"
.PARAMETER GroupBy           '<TableCode>=<Col>[+<Col>]' (e.g. T11_02=Land_TypeFuel) or '<Col>[+<Col>]' for all tables:
                             columns forming the NAE group key instead of the auto-detected phase column
                             (the first column is also the legacy Component_Phase filter). Needed for C12_01.
.PARAMETER MethodologyTable  '<regex>=<TableCode>[+<TableCode>]', e.g. '^ECW_C12_01$=T11_02+T11a_01'; used when the
                             methodology has no active binding. Several tables: the result is attributed to the
                             tables whose row exists (multiTableResults in the summary counts unresolved ones).
.PARAMETER AggregateBy       None (default) | NaePhase
.PARAMETER ValidateOnly      parse and print the resolved parameters (JSON), do nothing else
#>
[CmdletBinding()]
param(
    [string]$JsonDir,
    [string]$OracleTsv,
    [string]$OutPrefix,
    [string]$SqlConnection,
    [string]$StateFile,
    [string[]]$Documents = @(),
    [int]$MinDocument = 1,
    [int]$MaxDocs = 0,
    [string[]]$Months = @(),
    [string[]]$GroupBy = @(),
    [string[]]$MethodologyTable = @(),
    [ValidateSet('None', 'NaePhase')][string]$AggregateBy = 'None',
    [double]$TolAbs = 1e-9,
    [double]$TolRel = 1e-12,
    [double]$StorageAbs = 5e-13,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'EcrApi.ps1')
. (Join-Path $PSScriptRoot 'LandParity.Lib.ps1')

$groupByMap = ConvertTo-LandParityGroupByMap $GroupBy
$methTableMap = ConvertTo-LandParityMethodologyTableMap $MethodologyTable
# -File passes "3,4" as ONE string (an [int[]] parameter would read it as 34): split by hand.
[int[]]$MonthList = @(@($Months) | ForEach-Object { ([string]$_) -split '[,; ]+' } | Where-Object { $_ -ne '' } | ForEach-Object {
        $m = 0
        if (-not [int]::TryParse($_, [ref]$m) -or $m -lt 1 -or $m -gt 12) { throw "Month '$_' is out of 1..12." }
        $m })
$DocList = @(@($Documents) | ForEach-Object { ([string]$_) -split '[,; ]+' } | Where-Object { $_ -ne '' })

if ($ValidateOnly) {
    [ordered]@{
        aggregateBy = $AggregateBy
        months = @($MonthList)
        groupBy = @($groupByMap.Keys | Sort-Object | ForEach-Object { [ordered]@{ table = $_; columns = @($groupByMap[$_]) } })
        methodologyTable = @($methTableMap | ForEach-Object { [ordered]@{ regex = $_.Regex; tables = @($_.Tables) } })
    } | ConvertTo-Json -Depth 5 -Compress | Write-Host
    return
}

foreach ($req in 'JsonDir', 'OracleTsv', 'OutPrefix') {
    if ([string]::IsNullOrWhiteSpace((Get-Variable $req -ValueOnly))) { throw "-$req is required." }
}

# --- guards: outputs outside the repository ---
$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($p in @($OutPrefix, $OracleTsv)) {
    if ([System.IO.Path]::GetFullPath($p).StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$p' is inside the repository. Refused."
    }
}
if ([string]::IsNullOrWhiteSpace($StateFile) -and [string]::IsNullOrWhiteSpace($SqlConnection)) { throw "-SqlConnection is required in SQL mode (no default stand): give a connection string to a local EcrLand* database, or -StateFile (offline). Refused." }
if ([string]::IsNullOrWhiteSpace($StateFile)) { [void](Assert-LocalSqlConnection $SqlConnection) }

# --- documents ---
$files = @(Get-ChildItem -LiteralPath $JsonDir -Filter 'LAND-PROD-*.json' |
        ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Num = [int]($_.BaseName -replace '^LAND-PROD-', ''); Name = $_.BaseName } } |
        Where-Object { $_.Num -ge $MinDocument } | Sort-Object Num)
if ($DocList.Count -gt 0) { $files = @($files | Where-Object { $DocList -contains $_.Name }) }
if ($MaxDocs -gt 0) { $files = @($files | Select-Object -First $MaxDocs) }
if ($files.Count -eq 0) { throw "No LAND-PROD-<n>.json documents selected in '$JsonDir'." }

# --- state ---
$state = New-LandParityState
$state.MethodologyTable = $methTableMap
if ([string]::IsNullOrWhiteSpace($StateFile)) { Import-LandParityStateSql $SqlConnection $state }
else { Import-LandParityStateFile $StateFile $state }
Write-Host "ecr results: $($state.ResultCount); loaded rows: $($state.Loaded.Count)"
$oracle = Import-LandParityOracleTsv $OracleTsv
Write-Host "oracle keys: $($oracle.Count)"

$acc = New-LandParityAccumulator
$opt = @{ Months = @($MonthList); StorageAbs = $StorageAbs; TolRel = $TolRel; TolAbs = $TolAbs; AggregateBy = $AggregateBy; GroupBy = $groupByMap }
Invoke-LandParityCompare -DocumentFile @($files | ForEach-Object { $_.Path }) -StateObject $state -Oracle $oracle -Accumulator $acc -Option $opt -Progress {
    param($i, $n) if ($i % 10 -eq 0) { Write-Host "docs $i/$n" }
}

$summary = Get-LandParitySummary $acc $state
$result = [ordered]@{
    formatVersion = 2
    tolerance = [ordered]@{ abs = $TolAbs; rel = $TolRel; storageAbs = $StorageAbs }
    aggregateBy = $AggregateBy
    months = @($MonthList)
    documents = [int]$files.Count
    aggregateGroups = [int]$acc.AggregateGroups
    totalCells = $summary.totalCells
    classes = $summary.classes
    byMethodology = $summary.byMethodology
    multiTableResults = $summary.multiTableResults
    groups = $summary.groups
}
$outJson = [System.IO.Path]::GetFullPath("$OutPrefix.summary.json")
$outTsv = [System.IO.Path]::GetFullPath("$OutPrefix.cells.tsv")
New-Item -ItemType Directory -Force (Split-Path $outJson) | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($outJson, ($result | ConvertTo-Json -Depth 8 -Compress), $utf8)
$head = "doc`tperiod`ttable`trowKey`trows`tefid`tmethodology`toutput`tclass`trel"
[System.IO.File]::WriteAllLines($outTsv, [string[]](@($head) + $acc.Detail.ToArray()), $utf8)
Write-Host 'classes:'
$summary.classes | ForEach-Object { Write-Host ("  {0,-40} {1}" -f $_.class, $_.cells) }
Write-Host "out: $outJson / $outTsv (non-MATCH cells: $($acc.Detail.Count))"
