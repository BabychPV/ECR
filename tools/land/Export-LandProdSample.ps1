<#
.SYNOPSIS
    Exports the REAL inputs of the legacy system (AF_Air_Land_* tables of a LOCAL copy of its database) into the
    sample files LAND-PROD-<n>.json that Load-LandRows.ps1 loads and Compare-LandParityFull.ps1 aligns by position.
.DESCRIPTION
    READ-ONLY: SELECT ... WITH (NOLOCK) only, local SQL Server only, ApplicationIntent=ReadOnly. The files carry
    production values: they are written OUTSIDE the repository (a path inside it is refused) and never committed;
    the output of this script has counters only.

    File format (the same as the earlier hand-made sample):
      {"doc":"LAND-PROD-<n>","periods":{"2026-01":{"AF_Air_Land_Stationary_Equipment":{"columns":[...],"rows":[{"_efid":"...", "<col>": <value>, ...}]}}}}
    Periods come from the legacy column Time (month of the stored local date). Empty cells are omitted; audit columns
    (EFID/ATID/ModifiedTime/ModifiedBy/Time) and the contract header (Con_*) are not exported (the loader ignores Con_*).

    Packing into documents: the rows of one legacy EFID (an event frame = one report of one facility for one month)
    always stay together; EFIDs of a month are packed first-fit into documents so that no table of a document-month
    exceeds -MaxRowsPerDocTable (template row limit MaxDynamicRows; default 900, below 1000). Document numbers are
    global per run (LAND-PROD-1..N), the same EFID set per month goes to the same document as far as capacity allows.
.PARAMETER SourceDatabase   database of the LOCAL copy of the legacy system (default EcrLegacyProd)
.PARAMETER Server           local SQL Server (default localhost)
.PARAMETER OutDir           directory for LAND-PROD-<n>.json (must be OUTSIDE the repository)
.PARAMETER Tables           AF table names to export (default: every AF_Air_Land_* table that is not *_History)
.PARAMETER MaxRowsPerDocTable  capacity of one table of one document-month (default 900)
.PARAMETER Year             only this year of Time (default 2026)
.PARAMETER Summary          optional JSON file with the counters (documents, rows per table), no values
#>
[CmdletBinding()]
param(
    [string]$SourceDatabase = 'EcrLegacyProd',
    [string]$Server = 'localhost',
    [Parameter(Mandatory)][string]$OutDir,
    [string[]]$Tables = @(),
    [int]$MaxRowsPerDocTable = 900,
    [int]$Year = 2026,
    [string]$Summary
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($p in @($OutDir, $Summary)) {
    if ($p -and [IO.Path]::GetFullPath($p).StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Path '$p' is inside the repository. Refused." }
}
$localNames = @('localhost', '.', '(local)', '127.0.0.1', '::1', [Environment]::MachineName)
if ($localNames -notcontains $Server) { throw "Server '$Server' is not local. Refused." }
if ($SourceDatabase -match '^EcrLand') { throw "Source '$SourceDatabase' looks like an ECR stand, not a legacy copy." }
if ($MaxRowsPerDocTable -lt 1) { throw '-MaxRowsPerDocTable must be positive.' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$cs = "Server=$Server;Database=$SourceDatabase;Integrated Security=true;TrustServerCertificate=true;ApplicationIntent=ReadOnly"
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()
function Invoke-Sql([string]$Text) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $Text; $cmd.CommandTimeout = 600
    return $cmd
}
if ($Tables.Count -eq 0) {
    $rd = (Invoke-Sql "SELECT name FROM sys.tables WHERE name LIKE 'AF[_]Air[_]Land[_]%' AND name NOT LIKE '%[_]History' ORDER BY name").ExecuteReader()
    $list = New-Object System.Collections.ArrayList
    while ($rd.Read()) { [void]$list.Add([string]$rd.GetString(0)) }
    $rd.Close(); $Tables = @($list)
}
foreach ($t in $Tables) { if ($t -notmatch '^AF_Air_Land_[A-Za-z0-9_]+$') { throw "Table name '$t' is not an AF_Air_Land_* table." } }
$skip = @('EFID', 'ATID', 'ModifiedTime', 'ModifiedBy', 'Time')
$inv = [Globalization.CultureInfo]::InvariantCulture

# table -> month -> efid -> rows (list of ordered dictionaries); rows already ordered by EFID, ID
$data = @{}
$counters = [ordered]@{}
foreach ($t in $Tables) {
    $q = "SELECT * FROM dbo.[$t] WITH (NOLOCK) WHERE Time >= '$Year-01-01' AND Time < '$($Year + 1)-01-01' ORDER BY EFID, Time"
    $r = (Invoke-Sql $q).ExecuteReader()
    $colNames = @(0..($r.FieldCount - 1) | ForEach-Object { $r.GetName($_) })
    $n = 0
    while ($r.Read()) {
        $row = [ordered]@{ _efid = ([string]$r['EFID']).ToLowerInvariant() }
        for ($i = 0; $i -lt $colNames.Count; $i++) {
            $c = $colNames[$i]
            if ($skip -contains $c -or $c -like 'Con_*' -or $r.IsDBNull($i)) { continue }
            $v = $r.GetValue($i)
            if ($v -is [string]) { if ($v.Trim().Length -eq 0) { continue }; $row[$c] = $v }
            elseif ($v -is [decimal] -or $v -is [double] -or $v -is [single]) { $row[$c] = ([decimal]$v).ToString($inv) }
            elseif ($v -is [datetime]) { $row[$c] = $v.ToString('s', $inv) }
            elseif ($v -is [bool]) { $row[$c] = [string]$v }
            else { $row[$c] = $v }
        }
        $month = ([datetime]$r['Time']).ToString('yyyy-MM', $inv)
        if (-not $data.ContainsKey($t)) { $data[$t] = @{ } }
        if (-not $data[$t].ContainsKey($month)) { $data[$t][$month] = New-Object System.Collections.Specialized.OrderedDictionary }
        $efid = $row._efid
        if (-not $data[$t][$month].Contains($efid)) { $data[$t][$month][$efid] = New-Object System.Collections.ArrayList }
        [void]$data[$t][$month][$efid].Add($row)
        $n++
    }
    $r.Close()
    $counters[$t] = $n
}
$conn.Close()

# packing: per month, EFIDs first-fit into documents
$months = @($data.Values | ForEach-Object { $_.Keys } | Sort-Object -Unique)
$docOf = @{}        # month|efid -> doc number
$load = @{}         # doc|month|table -> rows
$maxDoc = 0
foreach ($m in $months) {
    $efids = @($data.Keys | ForEach-Object { if ($data[$_].ContainsKey($m)) { $data[$_][$m].Keys } } | Sort-Object -Unique)
    foreach ($e in $efids) {
        $need = @{}
        foreach ($t in $Tables) { if ($data.ContainsKey($t) -and $data[$t].ContainsKey($m) -and $data[$t][$m].Contains($e)) { $need[$t] = $data[$t][$m][$e].Count } }
        $placed = 0
        for ($d = 1; $d -le ($maxDoc + 1); $d++) {
            $fits = $true
            foreach ($t in $need.Keys) { if (([int]$load["$d|$m|$t"] + $need[$t]) -gt $MaxRowsPerDocTable -and [int]$load["$d|$m|$t"] -gt 0) { $fits = $false } }
            if ($fits) { $placed = $d; break }
        }
        if ($placed -eq 0) { $placed = $maxDoc + 1 }
        if ($placed -gt $maxDoc) { $maxDoc = $placed }
        $docOf["$m|$e"] = $placed
        foreach ($t in $need.Keys) { $load["$placed|$m|$t"] = [int]$load["$placed|$m|$t"] + $need[$t] }
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$docStats = @()
for ($d = 1; $d -le $maxDoc; $d++) {
    $periods = [ordered]@{}
    $rowsInDoc = 0
    foreach ($m in $months) {
        $pm = [ordered]@{}
        foreach ($t in $Tables) {
            if (-not ($data.ContainsKey($t) -and $data[$t].ContainsKey($m))) { continue }
            $rows = New-Object System.Collections.ArrayList
            $cols = @{}
            foreach ($e in $data[$t][$m].Keys) {
                if ($docOf["$m|$e"] -ne $d) { continue }
                foreach ($row in $data[$t][$m][$e]) { [void]$rows.Add($row); foreach ($k in $row.Keys) { if ($k -ne '_efid') { $cols[$k] = 1 } } }
            }
            if ($rows.Count -eq 0) { continue }
            $pm[$t] = [ordered]@{ columns = @($cols.Keys | Sort-Object); rows = @($rows) }
            $rowsInDoc += $rows.Count
        }
        if ($pm.Count -gt 0) { $periods[$m] = $pm }
    }
    $doc = [ordered]@{ doc = "LAND-PROD-$d"; periods = $periods }
    $file = Join-Path $OutDir "LAND-PROD-$d.json"
    [IO.File]::WriteAllText($file, ($doc | ConvertTo-Json -Depth 8 -Compress), $utf8)
    $docStats += [pscustomobject]@{ doc = "LAND-PROD-$d"; periods = $periods.Count; rows = $rowsInDoc }
    Write-Host ("LAND-PROD-{0}: periods {1}, rows {2}" -f $d, $periods.Count, $rowsInDoc)
}
Write-Host ("Documents: {0}; rows per table: {1}" -f $maxDoc, (($counters.Keys | Where-Object { $counters[$_] -gt 0 } | ForEach-Object { "$_=$($counters[$_])" }) -join ', '))
if ($Summary) {
    [IO.File]::WriteAllText($Summary, ([ordered]@{ documents = $docStats; tables = $counters } | ConvertTo-Json -Depth 5), $utf8)
}
