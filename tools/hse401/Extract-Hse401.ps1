<#
.SYNOPSIS
    Крок 1 HSE401: витяг із книги (КОПІЯ, лише читання): словники, вхідні комірки, кеш значень Excel,
    ознаки "це формула" по кожній комірці, база робочих днів по рядку (183/247/порожньо) і прапорці змішаних рядків.

.DESCRIPTION
    Книга читається як zip з XML (tools/hse401/Hse401Xlsx.Lib.ps1); VBA не запускається і не читається.
    Результат: out/hse401-extract.json (великий, не комітиться) і короткий звіт у консолі.

.PARAMETER SourceCopy
    Шлях до КОПІЇ книги (скрипт сам копіює оригінал у scratch-каталог, якщо задано -Original).
.PARAMETER Original
    Оригінал (лише читається/копіюється).
#>
[CmdletBinding()]
param(
    [string]$Original = 'C:\Users\Babyc\Downloads\HSE401_v1.0.3.1_patched 2.xlsm',
    [string]$WorkDir = (Join-Path $env:TEMP 'hse401-work'),
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Xlsx.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
if (-not $OutFile) {
    $od = Join-Path $here 'out'
    if (-not (Test-Path $od)) { New-Item -ItemType Directory -Force $od | Out-Null }
    $OutFile = Join-Path $od 'hse401-extract.json'
}
if (-not (Test-Path $WorkDir)) { New-Item -ItemType Directory -Force $WorkDir | Out-Null }
$copy = Join-Path $WorkDir 'hse401-copy.xlsm'
Copy-Item -LiteralPath $Original -Destination $copy -Force

$col = { param($l) [Hse401.XlsxReader]::ColIndex($l) }
$letters = { param($i) [Hse401.XlsxReader]::ColLetters($i) }

$monthTon = 'U', 'W', 'Y', 'AC', 'AE', 'AG', 'AK', 'AM', 'AO', 'AS', 'AU', 'AW'
$monthWd = 'V', 'X', 'Z', 'AD', 'AF', 'AH', 'AL', 'AN', 'AP', 'AT', 'AV', 'AX'

$zip = Open-Hse401Zip $copy
try {
    $shared = [Hse401.XlsxReader]::ReadSharedStrings($zip)
    $hse = Get-Hse401Cells $zip 'xl/worksheets/sheet1.xml' $shared
    $dd = Get-Hse401Cells $zip 'xl/worksheets/sheet5.xml' $shared
    $cfg = Get-Hse401Cells $zip 'xl/worksheets/sheet4.xml' $shared
}
finally { $zip.Dispose() }

# ---------- словники (DropdownList, рядки 3..) ----------
function Get-DdColumn([string]$letter) {
    $ci = & $col $letter
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($c in $dd) { if ($c.Col -eq $ci -and $c.Row -ge 3 -and $null -ne $c.Value -and ([string]$c.Value).Trim().Length -gt 0) { $list.Add([string]$c.Value) } }
    return , @($list)
}
$dict = [ordered]@{
    region   = Get-DdColumn 'B'
    onoff    = Get-DdColumn 'D'
    area     = Get-DdColumn 'E'
    facility = Get-DdColumn 'F'
    activity = Get-DdColumn 'G'
    permit   = Get-DdColumn 'H'
    permitGuid = Get-DdColumn 'T'
    equip    = Get-DdColumn 'I'
    srctype  = Get-DdColumn 'J'
}
$poll = New-Object System.Collections.Generic.List[object]
$byRow = @{}
foreach ($c in $dd) { if ($c.Row -ge 3 -and $c.Col -in (& $col 'O'), (& $col 'P'), (& $col 'Q')) { if (-not $byRow.ContainsKey($c.Row)) { $byRow[$c.Row] = @{} }; $byRow[$c.Row][$c.Col] = $c.Value } }
foreach ($r in ($byRow.Keys | Sort-Object)) {
    $o = $byRow[$r][(& $col 'O')]
    if ($null -eq $o -or ([string]$o).Trim() -eq '') { continue }
    $poll.Add([ordered]@{ ddRow = $r; code = [string]$o; component = [string]$byRow[$r][(& $col 'P')]; phase = [string]$byRow[$r][(& $col 'Q')] })
}
$dict['pollutant'] = $poll.ToArray()
$daysCell = $cfg | Where-Object { $_.Ref -eq 'I5' } | Select-Object -First 1

# ---------- рядки таблиці HSE_Table: Excel-рядки 9..6436, колонки B..BL ----------
$first = 9; $last = 6436
$cIdxMax = & $col 'BL'
$rowCells = @{}
$year = $null
foreach ($c in $hse) {
    if ($c.Ref -eq 'C5') { $year = [int]$c.Value }
    if ($c.Row -ge $first -and $c.Row -le $last -and $c.Col -ge 2 -and $c.Col -le $cIdxMax) {
        if (-not $rowCells.ContainsKey($c.Row)) { $rowCells[$c.Row] = New-Object System.Collections.Generic.List[object] }
        $rowCells[$c.Row].Add($c)
    }
}
$inv = [Globalization.CultureInfo]::InvariantCulture
$daysInYear = if ($year % 400 -eq 0 -or ($year % 4 -eq 0 -and $year % 100 -ne 0)) { 366 } else { 365 }
$bases = @(183, 247, $daysInYear)

$rows = New-Object System.Collections.Generic.List[object]
$stat = @{ rows = 0; basisBlank = 0; basis183 = 0; basis247 = 0; basisOther = 0; mixed = 0; unknown = 0; cells = 0; formulaCells = 0 }
foreach ($r in ($rowCells.Keys | Sort-Object)) {
    $v = [ordered]@{}   # колонка -> значення (кеш Excel)
    $fl = New-Object System.Collections.Generic.List[string]   # колонки, що є формулами в Excel
    $isNum = @{}
    foreach ($c in $rowCells[$r]) {
        $L = & $letters $c.Col
        $stat.cells++
        if ($c.IsFormula) { $fl.Add($L); $stat.formulaCells++ }
        if ($null -ne $c.Value -and ([string]$c.Value).Length -gt 0) {
            $v[$L] = [string]$c.Value
            $isNum[$L] = ($c.Type -ne 's' -and $c.Type -ne 'str' -and $c.Type -ne 'inlineStr')
        }
    }
    # база: R*wd/tons за формульними місячними комірками з wd>0, tons>0; будь-яке число (365/366 = порожня база)
    $limit = 0.0
    if ($v.Contains('R')) { [void][double]::TryParse($v['R'], 'Float', $inv, [ref]$limit) }
    $est = New-Object System.Collections.Generic.List[double]
    for ($m = 0; $m -lt 12; $m++) {
        $tc = $monthTon[$m]; $wc = $monthWd[$m]
        if (-not ($fl -contains $tc)) { continue }
        if (-not ($v.Contains($tc) -and $v.Contains($wc))) { continue }
        $t = 0.0; $w = 0.0
        [void][double]::TryParse($v[$tc], 'Float', $inv, [ref]$t); [void][double]::TryParse($v[$wc], 'Float', $inv, [ref]$w)
        if ($t -gt 0 -and $w -gt 0 -and $limit -gt 0) { $est.Add($limit * $w / $t) }
    }
    $basis = $null; $mixed = $false; $note = ''
    if ($est.Count -eq 0) { $note = 'no-evidence' }
    else {
        $lo = ($est | Measure-Object -Minimum).Minimum; $hi = ($est | Measure-Object -Maximum).Maximum
        if (($hi - $lo) / $lo -lt 1e-6) {
            $b = [Math]::Round($lo, 6)
            if ([Math]::Abs($b - $daysInYear) / $daysInYear -lt 1e-6) { $basis = $null }   # = днів у році: база порожня
            else { $basis = $b; if ($b -eq [Math]::Round($b)) { $basis = [int]$b } }
        }
        else {
            $mixed = $true
            $grp = $est | ForEach-Object { [Math]::Round($_, 3) } | Group-Object | Sort-Object Count -Descending
            $note = 'mixed:' + (($grp | ForEach-Object { $_.Name }) -join '/')
            $b0 = [double]$grp[0].Name
            if ([Math]::Abs($b0 - $daysInYear) / $daysInYear -ge 1e-6) { $basis = $b0 }
        }
    }
    if ($note -eq 'no-evidence') { $stat.unknown++ }
    $stat.rows++
    if ($mixed) { $stat.mixed++ }
    elseif ($null -eq $basis) { $stat.basisBlank++ }
    elseif ($basis -eq 183) { $stat.basis183++ }
    elseif ($basis -eq 247) { $stat.basis247++ }
    else { $stat.basisOther++ }
    $rows.Add([ordered]@{ r = $r; v = $v; f = $fl.ToArray(); basis = $basis; mixed = $mixed; note = $note })
}

$doc = [ordered]@{
    meta = [ordered]@{ year = $year; daysInYear = $daysInYear; excelDaysCell = if ($daysCell) { $daysCell.Value } else { $null }; source = 'HSE401 xlsm (copy)'; rowCount = $rows.Count }
    dictionaries = $dict
    rows = $rows.ToArray()
}
[IO.File]::WriteAllText($OutFile, (ConvertTo-HseJson -Value $doc).ToString(), (New-Object Text.UTF8Encoding($false)))

Write-Host ("Рік {0}, днів {1}; рядків {2}, комірок {3} (формул {4})" -f $year, $daysInYear, $stat.rows, $stat.cells, $stat.formulaCells)
Write-Host ("База: порожня {0}, 183 {1}, 247 {2}, інших {5}, змішаних {3}, без свідчень {4}" -f $stat.basisBlank, $stat.basis183, $stat.basis247, $stat.mixed, $stat.unknown, $stat.basisOther)
foreach ($k in $dict.Keys) { Write-Host ("  словник {0}: {1}" -f $k, @($dict[$k]).Count) }
Write-Host "Файл: $OutFile ($([math]::Round((Get-Item $OutFile).Length / 1MB, 1)) МБ)"
