<#
.SYNOPSIS
    Parity oracle: reads the results STORED by the legacy system (CalcResult_Emissions_*) for the same
    (document, month, table) selection that Export-ProdLandSample2.ps1 used, and writes them to a LOCAL file.
.DESCRIPTION
    READ-ONLY. Only SELECT ... WITH (NOLOCK) under READ UNCOMMITTED; no INSERT/UPDATE/DELETE/DDL, no temp tables.
    Local SQL Server only (localhost, ., (local), 127.0.0.1, ::1, this machine's name) and only the legacy
    database (default 'ECR'). The output file MUST be outside the git repository (the script refuses otherwise):
    it holds real values and identifiers of the customer's objects and must never be committed, sent to a
    cloud or quoted in reports (only identifiers, counts, classes and relative errors may be reported).

    For each table of the sample the script re-runs the selection of the sample exporter (one legacy EFID per
    table and month; the same filters, TOP cap and ORDER BY Land_No) and emits, per input row (seq = 1-based
    position, the same as the rowKey suffix pNNN-<seq:0000> that Load-ProdLandSample.ps1 used):
      - an alignment signature (Land_No, Land_Status, numeric input columns), because ORDER BY Land_No
        does not fix the order of ties;
      - the stored results: Component, Emission_Tons, Emission_Gsec, Methodology, ModifiedTime,
        matched by (EFID, MonthNumber, MPE_Number = Land_NAE, LandTable). Several rows with one NAE inside the
        selection are flagged ambiguous (the legacy result has no Land_No).
.PARAMETER Months
    Months of the sample documents in document order (LAND-PROD-1..n).
.PARAMETER OutFile
    Local JSON path outside the repository.
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'ECR',
    [int[]]$Months = @(1, 2, 5, 7),
    [int]$Year = 2026,
    [int]$StationaryCap = 150,
    [int]$RowCap = 200,
    [Parameter(Mandatory)][string]$OutFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# --- guards: local server, legacy DB only, output outside the repo ---
$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$outFull = [System.IO.Path]::GetFullPath($OutFile)
if ($outFull.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutFile '$OutFile' is inside the repository: real production values must not be written there."
}
$s = ($Server.Trim() -replace '^(tcp|np|lpc):', '') -split '[,\\]' | Select-Object -First 1
$local = @('.', '(local)', 'localhost', '127.0.0.1', '::1', '[::1]', [Environment]::MachineName.ToLowerInvariant()) -contains $s.Trim().ToLowerInvariant()
if (-not $local) { throw "SQL server '$Server' is not local. Refused." }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Bad database name '$Database'." }

$cs = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Application Name=land-parity-oracle"
function Q([string]$sql) {
    if ($sql -notmatch '^\s*SELECT\b') { throw 'Only SELECT statements are allowed.' }
    $cn = New-Object System.Data.SqlClient.SqlConnection($cs); $cn.Open()
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; ' + $sql
        $cmd.CommandTimeout = 180
        $dt = New-Object System.Data.DataTable
        [void](New-Object System.Data.SqlClient.SqlDataAdapter($cmd)).Fill($dt)
        return , $dt
    }
    finally { $cn.Close() }
}

$resultTables = @((Q "SELECT name FROM sys.tables WHERE name LIKE 'CalcResult[_]Emissions[_]%' AND name NOT LIKE '%[_]History' ORDER BY 1").Rows | ForEach-Object { [string]$_.name })
$inputTables = @((Q "SELECT t.name FROM sys.tables t JOIN sys.dm_db_partition_stats p ON p.object_id=t.object_id AND p.index_id IN (0,1) WHERE (t.name LIKE 'AF[_]Air[_]Land%') AND t.name NOT LIKE '%History' GROUP BY t.name HAVING SUM(p.row_count)>0 ORDER BY 1").Rows | ForEach-Object { [string]$_.name })

$out = [ordered]@{ formatVersion = 1; year = $Year; documents = [ordered]@{}; summary = [ordered]@{} }
$n = 0
foreach ($m in $Months) {
    $n++
    $docKey = "LAND-PROD-$n"
    $doc = [ordered]@{ month = $m; tables = [ordered]@{} }
    foreach ($t in $inputTables) {
        $isStat = ($t -eq 'AF_Air_Land_Stationary_Equipment')
        $order = 'COUNT(*) DESC'
        if ($isStat) { $order = 'SUM(CASE WHEN TRY_CAST(Land_RunningHoursMonthly AS float)>0 THEN 1 ELSE 0 END) DESC' }
        $pick = (Q "SELECT TOP 1 EFID FROM dbo.[$t] WITH (NOLOCK) WHERE YEAR([Time])=$Year AND MONTH([Time])=$m GROUP BY EFID ORDER BY $order, EFID").Rows
        if ($pick.Count -eq 0) { continue }
        $efid = [string]$pick[0].EFID
        $cols = @((Q "SELECT COLUMN_NAME n, DATA_TYPE d FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='$t' ORDER BY ORDINAL_POSITION").Rows)
        $names = @($cols | ForEach-Object { [string]$_.n })
        $numeric = @($cols | Where-Object { $_.d -in 'float', 'decimal', 'numeric', 'int', 'bigint', 'smallint', 'real' } |
                ForEach-Object { [string]$_.n } | Where-Object { $_ -notmatch '^(EFID|ATID|ID|Time|ModifiedTime|Land_Reserv.*|Land_.*Reserv\d*|Land_PermitId|Land_ObjectId|Land_SubstanceId|Land_Flag\d|Land_Reserved\d|Land_StringReportType)$' })
        $phaseCols = @($names | Where-Object { $_ -match '^Land_Type(Fuel|FuelOrStream|StorMater|StorMaterial|StoredMaterial|BurnFuel|PumpedLiquid)$' })
        $sel = @('Land_No', 'Land_NAE', 'Land_Status') | Where-Object { $names -contains $_ }
        $sel = @($sel + $phaseCols)
        $sel = @($sel + $numeric | Select-Object -Unique)
        if ($sel.Count -eq 0) { continue }
        $selSql = ($sel | ForEach-Object { "[$_]" }) -join ', '
        $where = "EFID = '$efid'"
        $cap = $RowCap
        if ($isStat) {
            $where += ' AND (TRY_CAST(Land_RunningHoursMonthly AS float)>0 OR TRY_CAST(Land_Measure_FuelSubstanceConsumptionOrPumpedVolume AS float)>0)'
            $cap = $StationaryCap
        }
        $ord = if ($names -contains 'Land_No') { 'Land_No' } else { '(SELECT NULL)' }
        $rows = @((Q "SELECT TOP ($cap) $selSql FROM dbo.[$t] WITH (NOLOCK) WHERE $where ORDER BY $ord").Rows)
        if ($rows.Count -eq 0) { continue }

        # stored results of this EFID/month for this input table (all CalcResult_Emissions_* tables)
        $byNae = @{}
        foreach ($rt in $resultTables) {
            $rc = @((Q "SELECT COLUMN_NAME n FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='$rt'").Rows | ForEach-Object { [string]$_.n })
            if (-not ($rc -contains 'LandTable' -and $rc -contains 'MPE_Number' -and $rc -contains 'Component')) { continue }
            $res = @((Q "SELECT MPE_Number, Component, Component_Phase, Emission_Tons, Emission_Gsec, Methodology, ModifiedTime FROM dbo.[$rt] WITH (NOLOCK) WHERE EFID='$efid' AND MonthNumber=$m AND LandTable='$t'").Rows)
            foreach ($r in $res) {
                $k = [string]$r.MPE_Number
                if (-not $byNae.ContainsKey($k)) { $byNae[$k] = New-Object System.Collections.ArrayList }
                [void]$byNae[$k].Add([ordered]@{
                        component = [string]$r.Component
                        phase = [string]$r.Component_Phase
                        tons = $(if ($r.Emission_Tons -is [DBNull]) { $null } else { [double]$r.Emission_Tons })
                        gsec = $(if ($r.Emission_Gsec -is [DBNull]) { $null } else { [double]$r.Emission_Gsec })
                        methodology = [string]$r.Methodology
                        modified = ([datetime]$r.ModifiedTime).ToString('s')
                        source = $rt
                    })
            }
        }
        # A NAE is ambiguous only when DIFFERENT Land_No share it: the legacy store keeps every input row twice
        # (stub copy + re-run copy, one EFID) and both copies carry the same Land_No.
        $naeCount = @{}
        if ($names -contains 'Land_NAE') {
            $naeNos = @{}
            foreach ($r in $rows) {
                $k = [string]$r.Land_NAE + '|' + $(if ($phaseCols.Count -gt 0) { [string]$r[$phaseCols[0]] } else { '' })
                if (-not $naeNos.ContainsKey($k)) { $naeNos[$k] = @{} }
                $ln = $(if ($names -contains 'Land_No') { [string]$r.Land_No } else { [string]$r.GetHashCode() })
                $naeNos[$k][$ln] = 1
            }
            foreach ($k in $naeNos.Keys) { $naeCount[$k] = $naeNos[$k].Count }
        }

        $list = New-Object System.Collections.ArrayList
        $seq = 0
        foreach ($r in $rows) {
            $seq++
            $inputs = [ordered]@{}
            foreach ($c in $sel) {
                if ($c -eq 'Land_NAE') { continue }
                $v = $r[$c]
                if ($v -is [DBNull]) { $v = $null } elseif ($v -is [decimal]) { $v = [double]$v }
                $inputs[$c] = $v
            }
            $nae = $(if ($names -contains 'Land_NAE') { [string]$r.Land_NAE } else { '' })
            $ph = $(if ($phaseCols.Count -gt 0) { [string]$r[$phaseCols[0]] } else { '' })
            $rs = @()
            $amb = $false
            if ($nae -ne '' -and $byNae.ContainsKey($nae)) {
                $rs = @($byNae[$nae])
                if ($ph -ne '') {
                    $narrow = @($rs | Where-Object { $_.phase.Trim() -eq $ph.Trim() })
                    if ($narrow.Count -gt 0) { $rs = $narrow }
                }
                if ($naeCount[$nae + '|' + $ph] -gt 1) { $amb = $true }
            }
            [void]$list.Add([ordered]@{ seq = $seq; naeAmbiguous = $amb; inputs = $inputs; results = $rs })
        }
        $doc.tables[$t] = [ordered]@{ rows = $list }
    }
    $out.documents[$docKey] = $doc
}
$json = $out | ConvertTo-Json -Depth 9 -Compress
New-Item -ItemType Directory -Force (Split-Path $outFull) | Out-Null
[System.IO.File]::WriteAllText($outFull, $json, (New-Object System.Text.UTF8Encoding($false)))
foreach ($k in $out.documents.Keys) {
    $d = $out.documents[$k]
    $rowsTot = 0; $withRes = 0
    foreach ($tv in $d.tables.Values) { foreach ($r in $tv.rows) { $rowsTot++; if (@($r.results).Count -gt 0) { $withRes++ } } }
    Write-Host ("{0}: tables={1} rows={2} rowsWithStoredResults={3}" -f $k, $d.tables.Count, $rowsTot, $withRes)
}
