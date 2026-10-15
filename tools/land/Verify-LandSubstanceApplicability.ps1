<#
.SYNOPSIS
    Перевірка (лише читання БД): кожне написання потоку/палива з даних прод-вибірки (LAND-PROD-*) має ключ у довіднику
    застосовності методології, де легасі його приймає (ToLower().Contains).
.DESCRIPTION
    READ-ONLY: лише SELECT ... під READ UNCOMMITTED (NOLOCK), локальний SQL Server. Нічого не пише в БД і в репозиторій.
    Вивід: методологія, вид, написання потоку (довідкова назва), кількість рядків, префікс, коди, яких немає в довіднику;
    значень викидів немає. Код виходу 1, якщо є прогалини (для CI/ручного прогону на стенді).

    Порівняння - ординальне (довідник: ignoreCase=false), легасі - ToLower().Contains: тобто `Makat gas - ...` при ключі
    лише `Makat Gas - ...` - прогалина (SUBSTANCE_APPLICABLE дає FALSE, рядок без кроків). Логіка порівняння -
    Get-LandSelectorCoverageGaps (LandSubstanceApplicability.Lib.ps1; Linux-безпечний тест на синтетичних даних).
    Перевіряються методології з selector=prefix-required (C11_02, C11_10, C12_03); prefix-chain (C11_01) легасі
    приймає за будь-якого тексту - окремий звіт. ⚠ Таблиці даних не прив'язані до методологій у цьому скрипті, тому
    рядок може бути прогалиною для методології, яка до цієї таблиці не застосовується (інформаційно; -Methodology звужує).
.PARAMETER ColumnCodes
    Коди колонок селектора (cfg.ColumnDef.Code); за замовчуванням TypeOfFuel, TypeOfStreams, Streams.
.NOTES
    Не виконувався проти БД автором lane (стенд не чіпали): запит спирається на doc.CellValue/doc.TableRow/doc.TableInstance
    і cfg.ColumnDef; для Lookup-колонок значення береться з ValueString, а за його відсутності - з dic.RegistryEntry.Code.
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'EcrLandUi',
    [string[]]$ColumnCodes = @('TypeOfFuel', 'TypeOfStreams', 'Streams'),
    [string]$Methodology
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$here = $PSScriptRoot
. (Join-Path $here 'LandSubstanceApplicability.Lib.ps1')

$s = ($Server.Trim() -replace '^(tcp|np|lpc):', '') -split '[,\\]' | Select-Object -First 1
$local = @('.', '(local)', 'localhost', '127.0.0.1', '::1', '[::1]', [Environment]::MachineName.ToLowerInvariant()) -contains $s.Trim().ToLowerInvariant()
if (-not $local) { throw "SQL server '$Server' is not local. Refused." }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Bad database name '$Database'." }
foreach ($c in $ColumnCodes) { if ($c -notmatch '^[A-Za-z0-9_]+$') { throw "Bad column code '$c'." } }

$refDir = Join-Path $here '..\..\docs\delivery\reference-data\land-template'
$spec = Get-Content -LiteralPath (Join-Path $refDir 'land-substance-applicability.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$model = Get-Content -LiteralPath (Join-Path $refDir 'land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$reg = @($model.registries) | Where-Object { $_.code -eq 'LAND_SUBSTANCE_APPLICABILITY' } | Select-Object -First 1

$inList = ($ColumnCodes | ForEach-Object { "'$_'" }) -join ','
$sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT COALESCE(cv.ValueString, re.Code) AS Value, COUNT(DISTINCT CONCAT(cv.TableRowId, '|', cv.ColumnDefId)) AS Rows
FROM doc.CellValue cv WITH (NOLOCK)
JOIN cfg.ColumnDef cd WITH (NOLOCK) ON cd.Id = cv.ColumnDefId
LEFT JOIN dic.RegistryEntry re WITH (NOLOCK) ON re.Id = cv.ValueRegistryEntryId
WHERE cd.Code IN ($inList) AND cv.IsEmpty = 0 AND COALESCE(cv.ValueString, re.Code) IS NOT NULL
GROUP BY COALESCE(cv.ValueString, re.Code)
"@
$cn = New-Object System.Data.SqlClient.SqlConnection("Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Application Name=land-sa-verify")
$cn.Open()
try {
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 180
    $dt = New-Object System.Data.DataTable
    [void](New-Object System.Data.SqlClient.SqlDataAdapter($cmd)).Fill($dt)
}
finally { $cn.Close() }

$observed = @($dt.Rows | ForEach-Object { [pscustomobject]@{ Value = [string]$_.Value; Rows = [int]$_.Rows } })
$methods = $spec.methodologies
if ($Methodology) {
    $one = [pscustomobject]@{}
    $one | Add-Member -NotePropertyName $Methodology -NotePropertyValue $methods.$Methodology
    $methods = $one
}
$gaps = @(Get-LandSelectorCoverageGaps -Observed $observed -Methodologies $methods -Entries @($reg.entries))
Write-Host ("Distinct selector texts in data: {0}" -f $observed.Count)
foreach ($g in ($gaps | Sort-Object Methodology, Value)) {
    Write-Host ("GAP {0} {1} '{2}' rows={3} prefix={4} missingCodes={5}" -f $g.Methodology, $g.Kind, $g.Value, $g.Rows, $g.Prefix, ($g.MissingCodes -join ','))
}
if ($gaps.Count -gt 0) { Write-Host "FAIL: $($gaps.Count) gap(s)"; exit 1 }
Write-Host 'OK: every accepted selector text has a registry key'
