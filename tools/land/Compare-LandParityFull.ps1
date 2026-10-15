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
    Rows are aligned BY POSITION: sample row i of a table <-> ECR rowKey p<doc>-<TableCode>-<i:0000> (table-qualified;
    the legacy p<doc>-<i:0000> is read when the document has no such rows; several candidate tables + different values
    -> AMBIGUOUS_ECR_KEY). The methodology -> table map
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
    LEGACY_ROW_SUM: the legacy engine writes ONE record per source row (modified = the row's ModifiedTime, not a run), so
    the legacy value of a group is the SUM of ALL stored copies of the key (not `latest`). Every key group with more than
    one row (also one Land_No twice) is aggregated. A copy count different from the number of Running rows of the group
    is AGG_LEGACY_COPY_COUNT_DIFF (group `other`, values not compared). With an explicit -GroupBy a phase absent in the
    oracle is NOT replaced by all the NAE rows: the group has no legacy (only ECR_NON_RUNNING is reported).
    Output has NO values: identifiers, classes and relative errors only (<OutPrefix>.summary.json, <OutPrefix>.cells.tsv).
.PARAMETER Months            only these months of every document (default: all); "3" or "3,4" or "3;4"
.PARAMETER GroupBy           '<TableCode>=<Col>[+<Col>]' (e.g. T11_02=Land_TypeFuel) or '<Col>[+<Col>]' for all tables:
                             columns forming the NAE group key instead of the auto-detected phase column
                             (the first column is also the legacy Component_Phase filter). Needed for C12_01.
.PARAMETER MethodologyTable  '<regex>=<TableCode>[+<TableCode>]', e.g. '^ECW_C12_01$=T11_02+T11a_01'; used when the
                             methodology has no active binding. Several tables: the result is attributed to the
                             tables whose row exists (multiTableResults in the summary counts unresolved ones).
.PARAMETER TypeFoldMethodology  regex of methodologies (besides the built-in ^ECW_C12_02$ and ^ECW_C11_13$) whose outputs ECW_EC_<tons|gsec>_<code>_typeN
                             are folded to the legacy key ECW_EC_<tons|gsec>_<code> (the legacy code writes ONE branch). Both
                             branches present for one key -> BRANCH_BOTH_PRESENT (values are not compared). Code 415 type1 is
                             never folded (the legacy code has no type1 for 415): ECR_ONLY.
                             Outputs ECW_EC_<tons|gsec>_XXX are always renamed by the substance code of the result
                             (SUBSTANCE_CODE of the register entry; C12_03 and the like).
.PARAMETER NoBranchSelector  disable the branch selector (ECW_C11_01 / C11_02 / C11_11_02 / C11_11_03 / C12_01): the legacy code
                             computes ONE branch per (row, output) chosen by NAE/stream, ECR emits all of them. By default only
                             the legacy-selected branch is compared; the other branches are ECR_ONLY_NOT_SELECTED (summary:
                             branchSelectedCells / notSelectedCells). With the switch the branches are folded and several
                             present -> BRANCH_BOTH_PRESENT.
.PARAMETER RelThreshold      relative error up to which a NEAR/MISMATCH cell counts as matched in the report buckets
                             (default 1e-9); the report: matched / withinThreshold / mismatch / ecrOnly / oracleOnly / other
                             per methodology and per substance (summary.json buckets+bySubstance, <OutPrefix>.buckets.tsv).
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
    # (-StrictGroupBy: a GroupBy column missing in a table => exit != 0 after the report is written; default only warns.)
    [string[]]$MethodologyTable = @(),
    [string[]]$TypeFoldMethodology = @(),
    [double]$RelThreshold = 1e-9,
    [ValidateSet('None', 'NaePhase')][string]$AggregateBy = 'None',
    [double]$TolAbs = 1e-9,
    [double]$TolRel = 1e-12,
    [double]$StorageAbs = 5e-13,
    [switch]$NoBranchSelector,
    [switch]$StrictGroupBy,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'EcrApi.ps1')
. (Join-Path $PSScriptRoot 'LandParity.Lib.ps1')

$groupByMap = ConvertTo-LandParityGroupByMap $GroupBy
$methTableMap = ConvertTo-LandParityMethodologyTableMap $MethodologyTable
[string[]]$typeFoldList = @(@($TypeFoldMethodology) | ForEach-Object { ([string]$_) -split ';' } | Where-Object { $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })
foreach ($rx in $typeFoldList) {
    try { [void][regex]::new($rx) } catch { throw "TypeFoldMethodology '$rx': bad regex ($($_.Exception.Message))." }
}
if ($RelThreshold -lt 0) { throw "-RelThreshold must be >= 0." }
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
        typeFold = @(@(Get-LandParityTypeFoldDefault) + @($typeFoldList))
        relThreshold = $RelThreshold
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
$state.TypeFold = @(@($state.TypeFold) + @($typeFoldList))
if ($NoBranchSelector) { $state.BranchSelector = @() }
if ([string]::IsNullOrWhiteSpace($StateFile)) { Import-LandParityStateSql $SqlConnection $state }
else { Import-LandParityStateFile $StateFile $state }
Write-Host "ecr results: $($state.ResultCount); loaded rows: $($state.Loaded.Count)"
$oracle = Import-LandParityOracleTsv $OracleTsv
Write-Host "oracle keys: $($oracle.Count)"

$acc = New-LandParityAccumulator -RelThreshold $RelThreshold
$opt = @{ Months = @($MonthList); StorageAbs = $StorageAbs; TolRel = $TolRel; TolAbs = $TolAbs; AggregateBy = $AggregateBy; GroupBy = $groupByMap }
Invoke-LandParityCompare -DocumentFile @($files | ForEach-Object { $_.Path }) -StateObject $state -Oracle $oracle -Accumulator $acc -Option $opt -Progress {
    param($i, $n) if ($i % 10 -eq 0) { Write-Host "docs $i/$n" }
}

$summary = Get-LandParitySummary $acc $state
$result = [ordered]@{
    formatVersion = 5
    tolerance = [ordered]@{ abs = $TolAbs; rel = $TolRel; storageAbs = $StorageAbs }
    aggregateBy = $AggregateBy
    months = @($MonthList)
    documents = [int]$files.Count
    aggregateGroups = [int]$acc.AggregateGroups
    totalCells = $summary.totalCells
    ecrNonRunningCells = $summary.ecrNonRunningCells
    branchSelectedCells = $summary.branchSelectedCells
    notSelectedCells = $summary.notSelectedCells
    classes = $summary.classes
    byMethodology = $summary.byMethodology
    multiTableResults = $summary.multiTableResults
    ambiguousEcrKeys = $summary.ambiguousEcrKeys
    rowKeyFormats = $summary.rowKeyFormats
    resultsWithTableInstance = $summary.resultsWithTableInstance
    resultsWithoutTableInstance = $summary.resultsWithoutTableInstance
    missingBindingInactiveCells = $summary.missingBindingInactiveCells
    groups = $summary.groups
    relThreshold = $summary.relThreshold
    buckets = $summary.buckets
    bySubstance = $summary.bySubstance
    outputsWithoutSubstance = $summary.outputsWithoutSubstance
    foreignSubstanceIgnored = $summary.foreignSubstanceIgnored
    warnings = @($summary.warnings)
}
$outJson = [System.IO.Path]::GetFullPath("$OutPrefix.summary.json")
$outTsv = [System.IO.Path]::GetFullPath("$OutPrefix.cells.tsv")
New-Item -ItemType Directory -Force (Split-Path $outJson) | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($outJson, ($result | ConvertTo-Json -Depth 8 -Compress), $utf8)
$outBuckets = [System.IO.Path]::GetFullPath("$OutPrefix.buckets.tsv")
$inv = [Globalization.CultureInfo]::InvariantCulture
$bucketLines = @("methodology`tsubstance`tmatched`twithinThreshold`tmismatch`tecrOnly`toracleOnly`texplainedSkip`tother`tzero`trelP95`trelMax") + @(
    $summary.bySubstance | ForEach-Object {
        $p95 = ''; if ($null -ne $_.relP95) { $p95 = ([double]$_.relP95).ToString('R', $inv) }
        $mx = ''; if ($null -ne $_.relMax) { $mx = ([double]$_.relMax).ToString('R', $inv) }
        (@($_.methodology, $_.substance, $_.matched, $_.withinThreshold, $_.mismatch, $_.ecrOnly, $_.oracleOnly, $_.explainedSkip, $_.other, $_.zero, $p95, $mx) -join "`t")
    })
[System.IO.File]::WriteAllLines($outBuckets, [string[]]$bucketLines, $utf8)
$head = "doc`tperiod`ttable`trowKey`trows`tefid`tmethodology`toutput`tclass`trel"
[System.IO.File]::WriteAllLines($outTsv, [string[]](@($head) + $acc.Detail.ToArray()), $utf8)
Write-Host 'classes:'
$summary.classes | ForEach-Object { Write-Host ("  {0,-40} {1}" -f $_.class, $_.cells) }
Write-Host ("buckets (threshold {0}): methodology matched(within) mismatch ecrOnly oracleOnly explainedSkip other" -f $RelThreshold)
$summary.buckets | ForEach-Object {
    Write-Host ("  {0,-16} {1}({2}) {3} {4} {5} {6} {7}" -f $_.methodology, $_.matched, $_.withinThreshold, $_.mismatch, $_.ecrOnly, $_.oracleOnly, $_.explainedSkip, $_.other)
}
Write-Host "out: $outJson / $outTsv / $outBuckets (non-MATCH cells: $($acc.Detail.Count))"
if (@($summary.warnings).Count -gt 0) {
    Write-Host ("warnings: {0}" -f @($summary.warnings).Count)
    if ($StrictGroupBy) { throw "-StrictGroupBy: $(@($summary.warnings).Count) GroupBy warning(s); see summary.warnings." }
}
