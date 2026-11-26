<#
.SYNOPSIS
    SGF-flaring AC-1: parity of ECR sour-gas flaring results against the legacy system (DESIGN-sg-flaring §6).
    READ-ONLY on both databases: SELECT only, ApplicationIntent=ReadOnly. Writes CSV/Markdown into -OutDir (outside the repo).

.DESCRIPTION
    Legacy (EcrLegacyProd or the legacy `ECR` copy): CalcResult_FlaringsIntermittent_SG (key EFID),
    CalcResult_FlaringsContinuous_SG (key EFID + FCID = day x stream), CalcResult_FlaringThermaloxidizerDaily /
    CalcResult_FlaringThermaloxidizer (key Date + Train + Mode + Type + StreamNum, IsSour = 1). Every numeric result column is
    unpivoted to (Key, Column, Value).

    ECR: calc.CalculationResult (current runs) of the SG methodologies, joined to ext.SourceEventLink on
    (TableInstanceId, RowKey = SourceRowKey) -> SourceEventId = EFID. The ECR output code is compared with the legacy column of the
    same name (the GCV bridge keeps the legacy formula names, H8). TOX rows are keyed by the TOX_DAILY row key
    "<yyyy-MM-dd>|<Train>|<Mode>|<Type>|<GasColumn>" (contract of the TOX loader; adjust -ToxKeySql if the loader differs).

    Known differences are classified, not failed (DESIGN §6): X1 (cross-month INT event: same per-EFID values, other month),
    L6 (TOX Train 1 -> 481A instead of 481B), H3/L1 (TOX EOR composition), L7 (TOX SG days legacy skipped for missing HSE400).

.PARAMETER LegacyConnection
    Connection string of the legacy DB copy (read-only). Example: "Server=.;Database=EcrLegacyProd;Integrated Security=true;TrustServerCertificate=True"
.PARAMETER EcrConnection
    Connection string of the ECR DB (a copy or the live DB through a read-only login). Never write.
.PARAMETER From / To
    Date window (inclusive / exclusive), e.g. 2025-01-01 / 2026-01-01 (SG 2025 = 12 months; TOX only 2026, card note 25.11).
.PARAMETER Methodologies
    ECR methodology codes of the SG consumers (default FLERT_INT_SG_300, FLERT_INT_SG_30X, FLERT_CONT_SG_ON, FLERT_CONT_SG_OFF, TOX_SG).
.PARAMETER RelTolerance
    Relative tolerance for numbers (default 1e-6, DESIGN §6: 1e-9 for MW/S/sum/mass, 1e-6 for tonnes/gsec; NumericMode Legacy).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$LegacyConnection,
    [Parameter(Mandatory)] [string]$EcrConnection,
    [Parameter(Mandatory)] [datetime]$From,
    [Parameter(Mandatory)] [datetime]$To,
    [string[]]$Methodologies = @('FLERT_INT_SG_300', 'FLERT_INT_SG_30X', 'FLERT_CONT_SG_ON', 'FLERT_CONT_SG_OFF', 'TOX_SG'),
    [double]$RelTolerance = 1e-6,
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'sgf-parity'),
    [string]$ToxKeySql = "CONVERT(char(10), [Date], 23) + '|' + Train + '|' + Mode + '|' + [Type] + '|' + CAST(StreamNum AS nvarchar(20))"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Open-ReadOnly([string]$cs) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $cs
    $b['ApplicationIntent'] = 'ReadOnly'
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $c.Open()
    return $c
}

function Invoke-Select($conn, [string]$sql, [hashtable]$p) {
    if ($sql -notmatch '^\s*(SET NOCOUNT ON;\s*)?(SELECT|WITH|DECLARE)\b') { throw "read-only guard: only SELECT is allowed: $sql" }
    if ($sql -match '\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|EXEC|TRUNCATE)\b') { throw "read-only guard: forbidden statement in $sql" }
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 600
    foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue($k, $p[$k]) }
    $t = New-Object System.Data.DataTable
    (New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($t) | Out-Null
    return , $t
}

# Legacy: unpivot every numeric column (decimal/float/int/real/money) of a result table except ids into (Kind, Key, Column, Value, Month).
function Get-LegacyRows($conn, [string]$table, [string]$kind, [string]$keySql, [string]$dateColumn, [string]$filter) {
    $cols = Invoke-Select $conn @"
SELECT c.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@t) AND t.name IN ('decimal','numeric','float','real','int','bigint','money')
  AND c.name NOT IN ('ID','EFID','FCID','FCID_FG','FCID_SG','IsSour','NumCol','StreamNum','Check1','Check2','Check3','Check4','Check5')
"@ @{ '@t' = "dbo.$table" }
    if ($cols.Rows.Count -eq 0) { return @() }
    $values = ($cols.Rows | ForEach-Object { "('$($_.name)', CAST([$($_.name)] AS float))" }) -join ', '
    $sql = @"
SELECT '$kind' AS Kind, $keySql AS [Key], v.Col AS [Column], v.Val AS [Value], CONVERT(char(7), [$dateColumn], 126) AS [Month]
FROM dbo.$table WITH (NOLOCK) CROSS APPLY (VALUES $values) v(Col, Val)
WHERE [$dateColumn] >= @from AND [$dateColumn] < @to AND v.Val IS NOT NULL $filter
"@
    return (Invoke-Select $conn $sql @{ '@from' = $From; '@to' = $To }).Rows
}

$null = New-Item -ItemType Directory -Force -Path $OutDir
$legacy = Open-ReadOnly $LegacyConnection
$ecr = Open-ReadOnly $EcrConnection
try {
    $legacyRows = @()
    $legacyRows += Get-LegacyRows $legacy 'CalcResult_FlaringsIntermittent_SG' 'INT' "CAST(EFID AS nvarchar(100))" 'DateTime' ''
    $legacyRows += Get-LegacyRows $legacy 'CalcResult_FlaringsContinuous_SG' 'CONT' "CAST(EFID AS nvarchar(100)) + '|' + CAST(FCID AS nvarchar(20))" 'DateTime' ''
    $legacyRows += Get-LegacyRows $legacy 'CalcResult_FlaringThermaloxidizerDaily' 'TOX' $ToxKeySql 'Date' 'AND IsSour = 1'

    $codes = ($Methodologies | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ','
    $ecrRows = (Invoke-Select $ecr @"
SELECT CASE WHEN m.Code LIKE 'TOX%' THEN 'TOX' WHEN m.Code LIKE '%CONT%' THEN 'CONT' ELSE 'INT' END AS Kind,
       CASE WHEN m.Code LIKE 'TOX%' THEN r.SourceRowKey ELSE COALESCE(l.SourceEventId, r.SourceRowKey) END AS [Key],
       r.OutputCode AS [Column], CAST(r.Value AS float) AS [Value], r.PeriodKey, m.Code AS Methodology
FROM calc.CalculationResult r
JOIN calc.CalculationRun run ON run.Id = r.CalculationRunId AND run.Status = N'Current'
JOIN calc.MethodologyVersion mv ON mv.Id = r.MethodologyVersionId
JOIN calc.Methodology m ON m.Id = mv.MethodologyId
LEFT JOIN ext.SourceEventLink l ON l.TableInstanceId = r.TableInstanceId AND l.RowKey = r.SourceRowKey
WHERE m.Code IN ($codes) AND r.PeriodKey >= @fromKey AND r.PeriodKey < @toKey
"@ @{ '@fromKey' = [int]$From.ToString('yyyyMM') ; '@toKey' = [int]$To.ToString('yyyyMM') + 1 }).Rows

    $ecrByKey = @{}
    foreach ($r in $ecrRows) { $ecrByKey["$($r.Kind)|$($r.Key)|$($r.Column)"] = $r }
    $out = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    foreach ($l in $legacyRows) {
        $k = "$($l.Kind)|$($l.Key)|$($l.Column)"
        $seen[$k] = $true
        $e = $ecrByKey[$k]
        $status = if ($null -eq $e) { 'missingInEcr' } else {
            $a = [double]$l.Value; $b = [double]$e.Value
            $den = [Math]::Max([Math]::Abs($a), [Math]::Abs($b))
            if ($den -eq 0 -or [Math]::Abs($a - $b) / $den -le $RelTolerance) { 'equal' } else { 'different' }
        }
        $known = ''
        if ($status -ne 'equal' -and $l.Kind -eq 'TOX' -and $l.Key -match '\|Train 1\|' -and $l.Key -match '\|481') { $known = 'L6' }
        if ($status -ne 'equal' -and $l.Kind -eq 'TOX' -and $l.Key -match '\|EOR\|') { $known = 'H3/L1' }
        $out.Add([pscustomobject]@{ Kind = $l.Kind; Key = $l.Key; Column = $l.Column; Legacy = $l.Value; Ecr = $(if ($e) { $e.Value } else { $null }); Status = $status; Known = $known; Month = $l.Month })
    }
    foreach ($r in $ecrRows) {
        $k = "$($r.Kind)|$($r.Key)|$($r.Column)"
        if (-not $seen.ContainsKey($k)) {
            $out.Add([pscustomobject]@{ Kind = $r.Kind; Key = $r.Key; Column = $r.Column; Legacy = $null; Ecr = $r.Value; Status = 'missingInLegacy'; Known = $(if ($r.Kind -eq 'TOX') { 'L7?' } else { '' }); Month = '' })
        }
    }

    $csv = Join-Path $OutDir 'sgf-parity.csv'
    $out | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $csv
    $summary = $out | Group-Object Kind, Status, Known | Sort-Object Name | ForEach-Object { "| $($_.Name -replace ', ', ' | ') | $($_.Count) |" }
    $md = @("# SGF parity $($From.ToString('yyyy-MM-dd'))..$($To.ToString('yyyy-MM-dd'))", '', '| Kind | Status | Known | Rows |', '|---|---|---|---|') + $summary
    [IO.File]::WriteAllLines((Join-Path $OutDir 'sgf-parity.md'), $md, (New-Object Text.UTF8Encoding($false)))
    $md | ForEach-Object { Write-Host $_ }
    $bad = @($out | Where-Object { $_.Status -ne 'equal' -and -not $_.Known }).Count
    Write-Host "Unexplained differences: $bad (details: $csv)"
    if ($bad -gt 0) { exit 2 }
}
finally {
    $legacy.Dispose(); $ecr.Dispose()
}
