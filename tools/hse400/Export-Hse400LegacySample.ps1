# tools/hse400/Export-Hse400LegacySample.ps1
# Вивантажує РЕАЛЬНІ входи й збережені результати HSE400 зі старої системи (копія прода, лише читання):
#   AF_AirGasCompositions_HSE400 (ввід, один рядок = один місяць) і CalcResult_AirGasCompositions_HSE400 (результат) за -Year.
# Підключення з наміром ReadOnly, лише SELECT ... WITH (NOLOCK). Вихід - JSON { year; months = { "<n>" = { inputs; results } } }
# із числами рядками (інваріантна культура, без втрати точності decimal).
# ⛔ Значення - дані замовника: -OutFile МАЄ лежати поза git-worktree (скрипт відмовляє інакше). Нічого не пише в базу.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutFile,
    [int]$Year = 2026,
    [string]$SqlConnection = 'Server=localhost;Database=EcrLegacyProd;Integrated Security=true;TrustServerCertificate=true;ApplicationIntent=ReadOnly'
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Hse400Stand.Lib.ps1')   # Test-Hse400InsideGit

$full = [IO.Path]::GetFullPath($OutFile)
if (Test-Hse400InsideGit $full) { throw 'OutFile лежить усередині git-worktree: дані замовника не мають бути поруч із репозиторієм. Відмова.' }
if ($SqlConnection -notmatch 'ApplicationIntent=ReadOnly') { $SqlConnection += ';ApplicationIntent=ReadOnly' }

function Read-Table([System.Data.SqlClient.SqlConnection]$c, [string]$sql) {
    $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
    $r = $cmd.ExecuteReader()
    $rows = New-Object System.Collections.Generic.List[object]
    while ($r.Read()) {
        $h = [ordered]@{}
        for ($i = 0; $i -lt $r.FieldCount; $i++) { $h[$r.GetName($i)] = if ($r.IsDBNull($i)) { $null } else { $r.GetValue($i) } }
        $rows.Add($h)
    }
    $r.Close()
    return $rows.ToArray()
}
function ConvertTo-Inv($v) {
    if ($null -eq $v) { return $null }
    if ($v -is [decimal]) { return $v.ToString([Globalization.CultureInfo]::InvariantCulture) }
    if ($v -is [double]) { return $v.ToString('R', [Globalization.CultureInfo]::InvariantCulture) }
    return $null
}

$conn = New-Object System.Data.SqlClient.SqlConnection $SqlConnection
$conn.Open()
try {
    $af = Read-Table $conn "SELECT * FROM dbo.AF_AirGasCompositions_HSE400 WITH (NOLOCK) ORDER BY [Time]"
    $cr = Read-Table $conn "SELECT * FROM dbo.CalcResult_AirGasCompositions_HSE400 WITH (NOLOCK) ORDER BY [DateTime]"
}
finally { $conn.Close() }

$skipIn = @('EFID', 'ModifiedTime', 'Time'); $skipOut = @('ID', 'ModifiedTime', 'EFID', 'DateTime')
$months = [ordered]@{}
foreach ($row in $af) {
    $t = [datetime]$row['Time']; if ($t.Year -ne $Year) { continue }
    $n = [string]$t.Month
    if ($months.Contains($n)) { throw "Два рядки вводу за місяць $n (EFID не унікальні за місяцем): вибірку не можна однозначно перенести." }
    $in = [ordered]@{}
    foreach ($k in $row.Keys) { if ($skipIn -notcontains $k) { $v = ConvertTo-Inv $row[$k]; if ($null -ne $v) { $in[$k] = $v } } }
    $months[$n] = [ordered]@{ efid = [string]$row['EFID']; inputs = $in; results = [ordered]@{} }
}
$matched = 0
foreach ($row in $cr) {
    $t = [datetime]$row['DateTime']; if ($t.Year -ne $Year) { continue }
    $n = [string]$t.Month
    if (-not $months.Contains($n) -or $months[$n].efid -ne [string]$row['EFID']) { continue }
    foreach ($k in $row.Keys) { if ($skipOut -notcontains $k) { $v = ConvertTo-Inv $row[$k]; if ($null -ne $v) { $months[$n].results[$k] = $v } } }
    $matched++
}
$dir = Split-Path -Parent $full
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$json = [ordered]@{ year = $Year; months = $months } | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($full, $json, (New-Object Text.UTF8Encoding($false)))
$res = ($months.Values | ForEach-Object { $_.results.Count } | Measure-Object -Sum).Sum
Write-Host ("Вивантажено: місяців {0}, рядків результату з парою за EFID {1}; вхідних значень {2}, результатних {3}." -f $months.Count, $matched, (($months.Values | ForEach-Object { $_.inputs.Count } | Measure-Object -Sum).Sum), $res)
