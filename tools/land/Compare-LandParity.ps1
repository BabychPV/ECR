<#
.SYNOPSIS
    Parity harness: results STORED by the legacy system  <->  results of the ECR calculation on the SAME inputs.
.DESCRIPTION
    READ-ONLY (no API call, no write). Inputs:
      -JsonDir     sample documents LAND-PROD-n.json (inputs; the sample exporter output, local, outside the repo)
      -OracleFile  stored legacy results from Export-LandParityOracle.ps1 (local, outside the repo)
      -SqlConnection  local stand database EcrLand* (REQUIRED in SQL mode, no default stand): calc.CalculationResult of the
                      CURRENT calculation run (calc.CalculationRun.Status = 'Current') of each document/period.
                      Recalculate the documents first (loader does it; or POST /api/v1/documents/{id}/recalculate).
    For every sample row the legacy results are aligned to the ECR rowKey pN-<seq:0000> through an input
    signature (Land_No, Land_Status, numeric inputs), then compared per (row, component, tons|gsec).

    Classes (per compared cell):
      MATCH            |d| <= StorageAbs (5e-13: half of the 12-decimal storage of calc.CalculationResult.Value) or rel <= TolRel
      MATCH_ROUNDING_LOSS  as MATCH, but the relative error is > 1e-9 (small values lose significant digits in storage)
      NEAR             |d| <= TolAbs but outside MATCH: precision loss that is small in absolute terms
                       (its relative error is reported: it can be large for values ~1e-8)
      MISMATCH         beyond tolerance: expression / constant / unit / rounding-of-inputs difference
      MISSING_NOT_IN_ECR | MISSING_NOT_PUBLISHED | MISSING_NO_BINDING | MISSING_BOUND_NO_RESULT
                       legacy has the result, the ECR document has none of that methodology: the methodology does not
                       exist / is not published (LP-07, LP-09) / has no active binding to a result column (LP-08) /
                       is bound but produced nothing (category rule, lookup load, REGFIELD: LP-10).
                       Offline mode (-EcrResultsFile) cannot tell them apart: MISSING_NO_METHODOLOGY.
      MISSING_ROW      the methodology works in the document, but not for this row (rule did not select it)
      MISSING_OUTPUT   the row is calculated by the methodology, but this component is not an ECR output
      ECR_ONLY         ECR output absent in the legacy store (NOx intermediates are tagged INTERMEDIATE)
      METHODOLOGY_DIFF both have the component, but different methodology codes
      AMBIGUOUS_KEY    several rows share one NAE in the legacy store (result has no Land_No): not comparable
      LEGACY_DUP_CONFLICT several stored copies of the result disagree
      LEGACY_STATUS_SKIP  explained by legacy behaviour 9: the row is not 'Running' (old engine skips it; the stored
                       result found by the NAE key belongs to another row)
      NOT_ALIGNED      sample row has no counterpart in the oracle (selection order / duplicates)
    Raw values are NOT written to OutFile unless -IncludeValues is passed; the file must be outside the repo.
.PARAMETER TolAbs   absolute tolerance for NEAR (default 1e-9)
.PARAMETER TolRel   relative tolerance for MATCH (default 1e-12)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$JsonDir,
    [string]$EcrResultsFile,
    [Parameter(Mandatory)][string]$OracleFile,
    [string]$SqlConnection,
    [string[]]$Documents = @('LAND-PROD-1', 'LAND-PROD-2', 'LAND-PROD-3', 'LAND-PROD-4'),
    [double]$TolAbs = 1e-9,
    [double]$TolRel = 1e-12,
    [double]$StorageAbs = 5e-13,
    [Parameter(Mandatory)][string]$OutFile,
    [switch]$IncludeValues
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'EcrApi.ps1')

# --- guards ---
$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($p in @($OutFile)) {
    if ([System.IO.Path]::GetFullPath($p).StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "OutFile '$p' is inside the repository. Refused."
    }
}
if ([string]::IsNullOrWhiteSpace($EcrResultsFile) -and [string]::IsNullOrWhiteSpace($SqlConnection)) { throw "-SqlConnection is required in SQL mode (no default stand): give a connection string to a local EcrLand* database, or -EcrResultsFile (offline). Refused." }
if ([string]::IsNullOrWhiteSpace($EcrResultsFile)) { $dbName = Assert-LocalSqlConnection $SqlConnection }

# ECR methodology -> legacy input table (the ECR result row key carries no table).
$MethodologyTable = [ordered]@{
    '^ECW_C05_' = 'AF_Air_Land_Stationary_Equipment'
    '^ECW_C06_01' = 'AF_Air_Land_Mobile_Equipment'
    '^ECW_C07_07' = 'AF_Air_Land_Other_Equipment_5_08'
    '^ECW_C07_16' = 'AF_Air_Land_Other_Equipment_5_22'
    '^ECW_C07_08' = 'AF_Air_Land_Other_Equipment_5_09'
    '^ECW_C07_21' = 'AF_Air_Land_Other_Equipment_5_27'
}
function Get-TableOfMethodology([string]$code) {
    foreach ($k in $MethodologyTable.Keys) { if ($code -match $k) { return $MethodologyTable[$k] } }
    return $null
}

function Sql-Rows([string]$sql, [hashtable]$p = @{}) {
    $cn = New-Object System.Data.SqlClient.SqlConnection($SqlConnection); $cn.Open()
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; $sql"
        foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue("@$k", $p[$k]) }
        $dt = New-Object System.Data.DataTable
        [void](New-Object System.Data.SqlClient.SqlDataAdapter($cmd)).Fill($dt)
        return , $dt
    }
    finally { $cn.Close() }
}

function Get-MismatchClass([double]$old, [double]$ecr) {
    # Direction/ratio only: the class never carries the values themselves.
    if ($ecr -eq 0) { return 'MISMATCH_ECR_ZERO' }
    if ($old -eq 0) { return 'MISMATCH_LEGACY_ZERO' }
    $q = $ecr / $old
    foreach ($f in 1000.0, 3.6, 3600.0, 100.0, 10.0, 1e6) {
        if ([math]::Abs($q / $f - 1) -lt 1e-4) { return "MISMATCH_RATIO_x$f" }
        if ([math]::Abs($q * $f - 1) -lt 1e-4) { return "MISMATCH_RATIO_div$f" }
    }
    return 'MISMATCH'
}
function Format-Sig($v) {
    if ($null -eq $v) { return '' }
    if ($v -is [string]) { return $v.Trim() }
    try { return ([double]$v).ToString('0.#########', [Globalization.CultureInfo]::InvariantCulture) } catch { return [string]$v }
}

# State of the methodologies in the stand: published version? active binding to a result column?
$methState = $null
if ([string]::IsNullOrWhiteSpace($EcrResultsFile)) {
    $methState = @{}
    foreach ($r in (Sql-Rows @"
SELECT m.Code,
  CASE WHEN EXISTS (SELECT 1 FROM calc.MethodologyVersion v WHERE v.MethodologyId = m.Id AND v.Status = 1) THEN 1 ELSE 0 END AS Pub,
  CASE WHEN EXISTS (SELECT 1 FROM cfg.CalculationBinding b WHERE b.MethodologyId = m.Id AND b.IsActive = 1) THEN 1 ELSE 0 END AS Bound
FROM calc.Methodology m
"@).Rows) { $methState[[string]$r.Code] = @([int]$r.Pub, [int]$r.Bound) }
}
function Get-MissingClass([string]$meth) {
    if ($null -eq $methState) { return 'MISSING_NO_METHODOLOGY' }
    if (-not $methState.ContainsKey($meth)) { return 'MISSING_NOT_IN_ECR' }
    if ($methState[$meth][0] -eq 0) { return 'MISSING_NOT_PUBLISHED' }          # LP-07 / LP-09
    if ($methState[$meth][1] -eq 0) { return 'MISSING_NO_BINDING' }              # LP-08: no result column / binding
    return 'MISSING_BOUND_NO_RESULT'                                               # rule / loader / category rule (LP-10)
}

$oracle = Get-Content -LiteralPath $OracleFile -Raw -Encoding UTF8 | ConvertFrom-Json
$cells = New-Object System.Collections.Generic.List[object]   # non-MATCH detail
$agg = @{}                                                     # key: doc|methodology|output|class
$docSummary = @()

function Add-Class($doc, $table, $rowKey, $meth, $out, $class, $abs, $rel, $old, $ecr) {
    $k = "$meth|$out|$class"
    if (-not $agg.ContainsKey($k)) { $agg[$k] = [ordered]@{ methodology = $meth; output = $out; class = $class; count = 0; maxRel = 0.0; maxAbs = 0.0; docs = @{}; tables = @{}; sample = $null } }
    $a = $agg[$k]; $a.count++
    if ($rel -gt $a.maxRel) { $a.maxRel = $rel }
    if ($abs -gt $a.maxAbs) { $a.maxAbs = $abs }
    $a.docs[$doc] = 1; $a.tables[$table] = 1
    if ($null -eq $a.sample -and $class -ne 'MATCH' -and $class -ne 'MATCH_ROUNDING_LOSS') { $a.sample = "$doc/$table/$rowKey" }
    if ($class -ne 'MATCH' -and $class -ne 'MATCH_ROUNDING_LOSS') {
        $o = [ordered]@{ doc = $doc; table = $table; rowKey = $rowKey; methodology = $meth; output = $out; class = $class; abs = $abs; rel = $rel }
        if ($IncludeValues) { $o.old = $old; $o.ecr = $ecr }
        $cells.Add([pscustomobject]$o)
    }
}

foreach ($docName in $Documents) {
    $n = [int]($docName -replace '^LAND-PROD-', '')
    $file = Join-Path $JsonDir "$docName.json"
    $sample = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
    $period = [int]$sample.year * 100 + [int]$sample.month
    $od = $oracle.documents.$docName
    if ($null -eq $od) { Write-Warning "$docName is not in the oracle"; continue }

    $docId = 0
    if (-not [string]::IsNullOrWhiteSpace($EcrResultsFile)) {
        # Offline mode (tests, archived runs): [{document, methodology, rowKey, outputCode, value}]
        $all = @(Get-Content -LiteralPath $EcrResultsFile -Raw -Encoding UTF8 | ConvertFrom-Json)
        $dt = New-Object System.Data.DataTable
        foreach ($c in 'SourceRowKey', 'OutputCode', 'Value', 'Methodology') { [void]$dt.Columns.Add($c) }
        foreach ($e in ($all | Where-Object { $_.document -eq $docName })) { [void]$dt.Rows.Add($e.rowKey, $e.outputCode, [string]$e.value, $e.methodology) }
        $res = , $dt
    }
    else {
    $docRow = Sql-Rows "SELECT Id FROM doc.Document WHERE JSON_VALUE(NameL10n,'$.en') = @n" @{ n = $docName }
    if ($docRow.Rows.Count -eq 0) { Write-Warning "$docName is not in the stand database"; continue }
    $docId = [long]$docRow.Rows[0].Id
    $res = Sql-Rows @"
SELECT cr.SourceRowKey, cr.OutputCode, cr.Value, m.Code AS Methodology
FROM calc.CalculationResult cr
JOIN calc.CalculationRun r ON r.Id = cr.CalculationRunId AND r.Status = 'Current'
JOIN calc.MethodologyVersion mv ON mv.Id = cr.MethodologyVersionId
JOIN calc.Methodology m ON m.Id = mv.MethodologyId
WHERE cr.DocumentId = @d AND cr.PeriodKey = @p
"@ @{ d = $docId; p = $period }
    }

    # ECR: table -> rowKey -> "meth|output" -> value ; plus methodology set
    $ecr = @{}; $ecrMeth = @{}; $ecrRowAny = @{}
    foreach ($r in $res.Rows) {
        $m = [string]$r.Methodology
        $tbl = Get-TableOfMethodology $m
        if ($null -eq $tbl) { $tbl = '(unmapped)' }
        $ecrMeth["$tbl|$m"] = 1
        $rk = [string]$r.SourceRowKey
        if (-not $ecr.ContainsKey($tbl)) { $ecr[$tbl] = @{} }
        if (-not $ecr[$tbl].ContainsKey($rk)) { $ecr[$tbl][$rk] = @{} }
        $ecr[$tbl][$rk]["$m|$([string]$r.OutputCode)"] = [double]::Parse([string]$r.Value, [Globalization.CultureInfo]::InvariantCulture)
        $ecrRowAny["$tbl|$rk"] = 1
    }

    $cnt = [ordered]@{ doc = $docName; period = $period; docId = $docId; rowsSample = 0; rowsAligned = 0; rowsNotAligned = 0; rowsWithLegacyResults = 0; ecrResults = $res.Rows.Count }
    foreach ($tp in $sample.tables.PSObject.Properties) {
        $table = $tp.Name
        $ot = $od.tables.PSObject.Properties[$table]
        if ($null -eq $ot) { continue }
        # alignment: signature -> queue of oracle rows
        $queue = @{}
        foreach ($orow in @($ot.Value.rows)) {
            $parts = @()
            foreach ($pp in $orow.inputs.PSObject.Properties) { $parts += ($pp.Name + '=' + (Format-Sig $pp.Value)) }
            $sig = ($parts | Sort-Object) -join ';'
            if (-not $queue.ContainsKey($sig)) { $queue[$sig] = New-Object System.Collections.Queue }
            $queue[$sig].Enqueue($orow)
        }
        $oracleNames = @(@($ot.Value.rows)[0].inputs.PSObject.Properties.Name)
        $seq = 0
        foreach ($srow in @($tp.Value.rows)) {
            $seq++
            $cnt.rowsSample++
            $parts = @()
            foreach ($nm in $oracleNames) {
                $pv = $srow.PSObject.Properties[$nm]
                $parts += ($nm + '=' + $(if ($pv) { Format-Sig $pv.Value } else { '' }))
            }
            $sig = ($parts | Sort-Object) -join ';'
            $rowKey = 'p{0}-{1:0000}' -f $n, $seq
            if (-not $queue.ContainsKey($sig) -or $queue[$sig].Count -eq 0) {
                $cnt.rowsNotAligned++
                Add-Class $docName $table $rowKey '(row)' '(row)' 'NOT_ALIGNED' 0 0 $null $null
                continue
            }
            $orow = $queue[$sig].Dequeue()
            $cnt.rowsAligned++
            if (@($orow.results).Count -gt 0) { $cnt.rowsWithLegacyResults++ }

            # legacy: latest copy per (methodology, component)
            $legacy = @{}
            foreach ($lr in @($orow.results)) {
                $k = "$($lr.methodology)|$($lr.component)"
                if (-not $legacy.ContainsKey($k)) { $legacy[$k] = New-Object System.Collections.ArrayList }
                [void]$legacy[$k].Add($lr)
            }
            $ecrRow = $null
            if ($ecr.ContainsKey($table) -and $ecr[$table].ContainsKey($rowKey)) { $ecrRow = $ecr[$table][$rowKey] }
            $seenEcr = @{}
            foreach ($k in $legacy.Keys) {
                $meth, $comp = $k -split '\|', 2
                $copies = @($legacy[$k] | Sort-Object { $_.modified })
                $latest = $copies[-1]
                foreach ($metric in 'tons', 'gsec') {
                    $out = "ECW_EC_${metric}_$comp"
                    $oldV = $latest.$metric
                    $outLabel = "${metric}_$comp"
                    if ($orow.naeAmbiguous) { Add-Class $docName $table $rowKey $meth $outLabel 'AMBIGUOUS_KEY' 0 0 $oldV $null; continue }
                    if ($copies.Count -gt 1) {
                        $vals = @($copies | ForEach-Object { $_.$metric } | Where-Object { $null -ne $_ } | Select-Object -Unique)
                        if ($vals.Count -gt 1) { Add-Class $docName $table $rowKey $meth $outLabel 'LEGACY_DUP_CONFLICT' 0 0 $oldV $null; continue }
                    }
                    if ($null -eq $oldV) { continue }
                    $ek = "$meth|$out"
                    $ecrV = $null; $isDiff = $false
                    if ($null -ne $ecrRow -and $ecrRow.ContainsKey($ek)) { $ecrV = $ecrRow[$ek]; $seenEcr[$ek] = 1 }
                    elseif ($null -ne $ecrRow) {
                        # same component under another methodology of the row?
                        foreach ($ck in $ecrRow.Keys) {
                            if ($ck.EndsWith("|$out", [System.StringComparison]::Ordinal)) {
                                Add-Class $docName $table $rowKey $meth $outLabel 'METHODOLOGY_DIFF' 0 0 $oldV $ecrRow[$ck]
                                $seenEcr[$ck] = 1; $isDiff = $true; break
                            }
                        }
                    }
                    if ($isDiff) { continue }
                    if ($null -eq $ecrV) {
                        $tableMeth = $ecrMeth.ContainsKey("$table|$meth")
                        $class = if (-not $tableMeth) { Get-MissingClass $meth } elseif (-not $ecrRowAny.ContainsKey("$table|$rowKey")) { 'MISSING_ROW' } else { 'MISSING_OUTPUT' }
                        # Legacy behaviour 9 (R_Land_RuleMatcher): a row whose Land_Status is not 'Running' is skipped by the old
                        # engine; results found for it by the NAE key belong to another row (key artefact), not a defect.
                        $stProp = $srow.PSObject.Properties['Land_Status']
                        if ($class -in 'MISSING_ROW', 'MISSING_OUTPUT' -and $null -ne $stProp -and $null -ne $stProp.Value -and [string]$stProp.Value -ne 'Running') { $class = 'LEGACY_STATUS_SKIP' }
                        if ($oldV -eq 0) { $class = $class + '_ZERO' }
                        Add-Class $docName $table $rowKey $meth $outLabel $class 0 0 $oldV $null
                        continue
                    }
                    $abs = [math]::Abs([double]$oldV - [double]$ecrV)
                    $den = [math]::Max([math]::Abs([double]$oldV), [math]::Abs([double]$ecrV))
                    $rel = if ($den -eq 0) { 0.0 } else { $abs / $den }
                    $class = if ($abs -le $StorageAbs -or $rel -le $TolRel) { $(if ($rel -gt 1e-9) { 'MATCH_ROUNDING_LOSS' } else { 'MATCH' }) } elseif ($abs -le $TolAbs) { 'NEAR' } else { Get-MismatchClass ([double]$oldV) ([double]$ecrV) }
                    Add-Class $docName $table $rowKey $meth $outLabel $class $abs $rel $oldV $ecrV
                }
            }
            if ($null -ne $ecrRow) {
                foreach ($ck in $ecrRow.Keys) {
                    if ($seenEcr.ContainsKey($ck)) { continue }
                    $meth, $out = $ck -split '\|', 2
                    $label = ($out -replace '^ECW_EC_', '')
                    $class = if ($label -match '_NOx$') { 'ECR_ONLY_INTERMEDIATE' } elseif ($ecrRow[$ck] -eq 0) { 'ECR_ONLY_ZERO' } else { 'ECR_ONLY' }
                    # D-425: рядок із колонкою статусу, де статус не Running (чи порожній), легасі не рахує: ECR його порахував -
                    # регресія фільтра «лише Running», а не «ECR порахував там, де легасі не було».
                    $nrProp = $srow.PSObject.Properties['Land_Status']
                    if ($class -like 'ECR_ONLY*' -and $null -ne $nrProp -and ($null -eq $nrProp.Value -or [string]$nrProp.Value -ne 'Running')) { $class = 'ECR_NON_RUNNING' }
                    if ($orow.naeAmbiguous) { $class = 'AMBIGUOUS_KEY' }
                    Add-Class $docName $table $rowKey $meth $label $class 0 0 $null $ecrRow[$ck]
                }
            }
        }
    }
    $docSummary += [pscustomobject]$cnt
}

# --- output ---
$groups = @($agg.Values | ForEach-Object {
        [pscustomobject]@{ methodology = $_.methodology; output = $_.output; class = $_.class; count = $_.count
            maxRel = $_.maxRel; maxAbs = $_.maxAbs; docs = (@($_.docs.Keys | Sort-Object) -join ','); tables = (@($_.tables.Keys | Sort-Object) -join ','); sample = $_.sample }
    } | Sort-Object class, methodology, output)
$byClass = $groups | Group-Object class | ForEach-Object { [pscustomobject]@{ class = $_.Name; cells = [int]($_.Group | Measure-Object count -Sum).Sum } } | Sort-Object cells -Descending
$result = [ordered]@{
    formatVersion = 1
    tolerance = [ordered]@{ abs = $TolAbs; rel = $TolRel; storageAbs = $StorageAbs }
    documents = $docSummary
    classes = $byClass
    groups = $groups
    cells = $cells
}
New-Item -ItemType Directory -Force (Split-Path ([System.IO.Path]::GetFullPath($OutFile))) | Out-Null
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutFile), ($result | ConvertTo-Json -Depth 6 -Compress), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "documents:"
$docSummary | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host 'classes:'
$byClass | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "OutFile: $OutFile (non-MATCH cells: $($cells.Count))"
