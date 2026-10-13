<#
.SYNOPSIS
    FULL-set parity oracle: every result STORED by the legacy system (CalcResult_Emissions_Air_Land*) for one year,
    written to a LOCAL TSV (input of Compare-LandParityFull.ps1).
.DESCRIPTION
    READ-ONLY. Only SELECT ... WITH (NOLOCK) under READ UNCOMMITTED; no INSERT/UPDATE/DELETE/DDL, no temp tables.
    Local SQL Server only and only the legacy database (default 'ECR'). The output file MUST be outside the git
    repository (the script refuses otherwise): it holds real values and identifiers of the customer's objects and
    must never be committed, sent to a cloud or quoted in reports.
    Columns: efid month landTable nae component phase tons gsec methodology modified source.
.PARAMETER Months
    Only these months (default: all months of the year).
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'ECR',
    [int]$Year = 2026,
    [int[]]$Months = @(),
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
foreach ($mo in $Months) { if ($mo -lt 1 -or $mo -gt 12) { throw "Month $mo is out of 1..12." } }

$cs = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Application Name=land-parity-oracle-full"
function Q([string]$sql) {
    if ($sql -notmatch '^\s*SELECT\b') { throw 'Only SELECT statements are allowed.' }
    $cn = New-Object System.Data.SqlClient.SqlConnection($cs); $cn.Open()
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; ' + $sql
        $cmd.CommandTimeout = 600
        $dt = New-Object System.Data.DataTable
        [void](New-Object System.Data.SqlClient.SqlDataAdapter($cmd)).Fill($dt)
        return , $dt
    }
    finally { $cn.Close() }
}

$monthFilter = ''
if ($Months.Count -gt 0) { $monthFilter = ' AND MonthNumber IN (' + ((@($Months) | ForEach-Object { [int]$_ }) -join ',') + ')' }
$resultTables = @((Q "SELECT name FROM sys.tables WHERE name LIKE 'CalcResult[_]Emissions[_]Air[_]Land%' AND name NOT LIKE '%[_]History' ORDER BY 1").Rows | ForEach-Object { [string]$_.name })
New-Item -ItemType Directory -Force (Split-Path $outFull) | Out-Null
$inv = [Globalization.CultureInfo]::InvariantCulture
$w = New-Object System.IO.StreamWriter($outFull, $false, (New-Object System.Text.UTF8Encoding($false)))
$n = 0
try {
    foreach ($t in $resultTables) {
        $dt = Q "SELECT EFID, MonthNumber, LandTable, MPE_Number, Component, Component_Phase, Emission_Tons, Emission_Gsec, Methodology, ModifiedTime FROM dbo.[$t] WITH (NOLOCK) WHERE YEAR(StartDate) = $Year$monthFilter"
        foreach ($r in $dt.Rows) {
            $tons = ''; $gsec = ''
            if ($r.Emission_Tons -isnot [DBNull]) { $tons = ([double]$r.Emission_Tons).ToString('R', $inv) }
            if ($r.Emission_Gsec -isnot [DBNull]) { $gsec = ([double]$r.Emission_Gsec).ToString('R', $inv) }
            $f = @([string]$r.EFID, [string]$r.MonthNumber, [string]$r.LandTable, ([string]$r.MPE_Number).Replace("`t", ' '),
                [string]$r.Component, ([string]$r.Component_Phase).Replace("`t", ' '), $tons, $gsec,
                [string]$r.Methodology, ([datetime]$r.ModifiedTime).ToString('s'), $t)
            $w.WriteLine($f -join "`t"); $n++
        }
        Write-Host "$t $($dt.Rows.Count)"
    }
}
finally { $w.Close() }
Write-Host "total=$n"
