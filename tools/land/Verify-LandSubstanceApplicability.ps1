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
    приймає за будь-якого тексту - окремий звіт. GAP - лише для таблиці з АКТИВНОЮ прив'язкою методології
    (cfg.CalculationBinding.IsActive=1) і значення в колонці-аргументі методології (spec.argument; для C11_10 ще
    Land_TypeFuel); для Lookup-колонки текст - поле NAME запису довідника (REGFIELD). Решта - INFO (код виходу не змінює).
.PARAMETER ColumnCodes
    Коди колонок селектора (cfg.ColumnDef.Code); за замовчуванням TypeOfFuel, TypeOfStreams, Streams.
.PARAMETER IncludeUnbound
    Стара поведінка: читати ще TypeOfFuel/TypeOfStreams/Streams і вважати GAP будь-яку прогалину, незалежно від прив'язки.
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'EcrLandUi',
    [string[]]$ColumnCodes,
    [string]$Methodology,
    [switch]$IncludeUnbound
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$here = $PSScriptRoot
. (Join-Path $here 'LandSubstanceApplicability.Lib.ps1')

$s = ($Server.Trim() -replace '^(tcp|np|lpc):', '') -split '[,\\]' | Select-Object -First 1
$local = @('.', '(local)', 'localhost', '127.0.0.1', '::1', '[::1]', [Environment]::MachineName.ToLowerInvariant()) -contains $s.Trim().ToLowerInvariant()
if (-not $local) { throw "SQL server '$Server' is not local. Refused." }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Bad database name '$Database'." }

$refDir = Join-Path $here '..\..\docs\delivery\reference-data\land-template'
$spec = Get-Content -LiteralPath (Join-Path $refDir 'land-substance-applicability.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$model = Get-Content -LiteralPath (Join-Path $refDir 'land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$reg = @($model.registries) | Where-Object { $_.code -eq 'LAND_SUBSTANCE_APPLICABILITY' } | Select-Object -First 1

$extraArgs = @{ ECW_C11_10 = @('Land_TypeFuel') }   # C11_10 читає Land_TypeFuel АБО Land_TypeFuelOrStream
if (-not $ColumnCodes) {
    $cols = New-Object System.Collections.Generic.HashSet[string]
    foreach ($m in $spec.methodologies.PSObject.Properties) {
        if ($m.Value.selector -ne 'prefix-required') { continue }
        [void]$cols.Add([string]$m.Value.argument)
        if ($extraArgs.ContainsKey($m.Name)) { foreach ($a in $extraArgs[$m.Name]) { [void]$cols.Add($a) } }
    }
    $ColumnCodes = @($cols)
    if ($IncludeUnbound) { $ColumnCodes = @($ColumnCodes) + @('TypeOfFuel', 'TypeOfStreams', 'Streams') }
}
foreach ($c in $ColumnCodes) { if ($c -notmatch '^[A-Za-z0-9_]+$') { throw "Bad column code '$c'." } }
$inList = ($ColumnCodes | ForEach-Object { "'$_'" }) -join ','
$sqlValues = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT td.Code AS Tbl, cd.Code AS Col, COALESCE(cv.ValueString, rn.ValueString, re.Code) AS Value,
       COUNT(DISTINCT cv.TableRowId) AS Rows
FROM doc.CellValue cv WITH (NOLOCK)
JOIN cfg.ColumnDef cd WITH (NOLOCK) ON cd.Id = cv.ColumnDefId
JOIN cfg.TableDef td WITH (NOLOCK) ON td.Id = cv.TableDefId
LEFT JOIN dic.RegistryEntry re WITH (NOLOCK) ON re.Id = cv.ValueRegistryEntryId
LEFT JOIN (SELECT rv.RegistryEntryId, rv.ValueString FROM dic.RegistryValue rv WITH (NOLOCK)
           JOIN cfg.RegistryFieldDef f WITH (NOLOCK) ON f.Id = rv.RegistryFieldDefId AND f.Code = 'NAME') rn ON rn.RegistryEntryId = re.Id
WHERE cd.Code IN ($inList) AND cd.IsDeleted = 0 AND td.IsDeleted = 0 AND cv.IsEmpty = 0
  AND COALESCE(cv.ValueString, rn.ValueString, re.Code) IS NOT NULL
GROUP BY td.Code, cd.Code, COALESCE(cv.ValueString, rn.ValueString, re.Code)
"@
$sqlBind = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT DISTINCT m.Code AS Methodology, td.Code AS Tbl
FROM cfg.CalculationBinding b WITH (NOLOCK)
JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = b.MethodologyId
JOIN cfg.TableDef td WITH (NOLOCK) ON td.Id = b.TableDefId
WHERE b.IsActive = 1
"@
$cn = New-Object System.Data.SqlClient.SqlConnection("Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Application Name=land-sa-verify")
$cn.Open()
try {
    $dt = New-Object System.Data.DataTable
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sqlValues; $cmd.CommandTimeout = 180
    [void](New-Object System.Data.SqlClient.SqlDataAdapter($cmd)).Fill($dt)
    $db = New-Object System.Data.DataTable
    $cmd2 = $cn.CreateCommand(); $cmd2.CommandText = $sqlBind; $cmd2.CommandTimeout = 180
    [void](New-Object System.Data.SqlClient.SqlDataAdapter($cmd2)).Fill($db)
}
finally { $cn.Close() }

$observed = @($dt.Rows | ForEach-Object { [pscustomobject]@{ Table = [string]$_.Tbl; Column = [string]$_.Col; Value = [string]$_.Value; Rows = [int]$_.Rows } })
$bindings = @($db.Rows | ForEach-Object { [pscustomobject]@{ Methodology = [string]$_.Methodology; Table = [string]$_.Tbl } })
$methods = $spec.methodologies
if ($Methodology) {
    $one = [pscustomobject]@{}
    $one | Add-Member -NotePropertyName $Methodology -NotePropertyValue $methods.$Methodology
    $methods = $one
}
# `ForEach-Object { $_ }` розгортає випадковий вкладений масив, зовнішнє `@()` дає масив (Count 0/1/N, StrictMode-безпечно).
$all = @(Get-LandSelectorCoverageClassified -Observed $observed -Methodologies $methods -Entries @($reg.entries) -Bindings $bindings -ExtraArguments $extraArgs -IncludeUnbound:$IncludeUnbound | ForEach-Object { $_ })
$gaps = @($all | Where-Object { $_.Status -eq 'GAP' })
$info = @($all | Where-Object { $_.Status -eq 'INFO' })
Write-Host ("Distinct (table, column, text) in data: {0}; active bindings: {1}" -f $observed.Count, $bindings.Count)
foreach ($g in ($all | Sort-Object Status, Methodology, Table, Value)) {
    Write-Host ("{0} {1} tbl={2} col={3} {4} '{5}' rows={6} prefix={7} missingCodes={8} reason={9}" -f $g.Status, $g.Methodology, $g.Table, $g.Column, $g.Kind, $g.Value, $g.Rows, $g.Prefix, ($g.MissingCodes -join ','), $g.Reason)
}
Write-Host ("GAP={0} INFO={1}" -f $gaps.Count, $info.Count)
if ($gaps.Count -gt 0) { Write-Host "FAIL: $($gaps.Count) gap(s)"; exit 1 }
Write-Host 'OK: every accepted selector text in a bound table has a registry key'
