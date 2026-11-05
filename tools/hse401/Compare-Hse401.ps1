<#
.SYNOPSIS
    Крок 7 HSE401: порівняння результату ECR із кешем Excel. Пусто == 0; відносний допуск 1e-9 (абсолютний пол 1e-15 -> клас tolerance);
    класи розбіжностей: match, expected-literal (у Excel комірка була літералом), VLOOKUP-3219 (наближений VLOOKUP/код поза діапазоном),
    phase-vocabulary (літерал фази інакшого словника), mixed-basis-row, tolerance, REAL.

.OUTPUTS
    out/hse401-compare-real.csv (усі REAL), out/hse401-compare-classes.csv (кількості клас x колонка), консольна таблиця.
#>
[CmdletBinding()]
param([string]$OutPrefix = 'hse401-compare',
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Xlsx.Lib.ps1')
. (Join-Path $here 'Hse401Model.Lib.ps1')
Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
$out = Get-Hse401OutDir
if (Test-Hse401Preview) { Write-HseLog '[preview] порівняння пропущено: документ ще не завантажено (порівнювати є з чим лише після Load).'; return }
$stateFile = Get-Hse401StateFile
$state = Read-HseJson $stateFile
Assert-Hse401StateMatches $state
Connect-Hse
$inv = [Globalization.CultureInfo]::InvariantCulture

$data = Read-HseJson (Join-Path $out 'hse401-extract.json')
$rows = $data['rows']
$cols = Get-Hse401Columns
$cmp = @($cols | Where-Object { $_.Xl -and ($_.Kind -eq 'Formula' -or $_.Code -in 'H_LIMIT_T', 'H_LIMIT_GS', 'H_RAW_T', 'H_RUN_H' -or $_.Code -like 'H_D*') })

# словник для класифікації VLOOKUP
$pol = @{}
$dd = $data['dictionaries']['pollutant']
foreach ($p in $dd) { $pol[[string]$p['code']] = $p }
$lastCode = [string]$dd[$dd.Count - 1]['code']   # поза діапазоном O3:Q86 (VLOOKUP не бачить)

$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Invoke-Hse GET "/api/v1/documents/$($state['documentId'])/tables/$($state['tableInstanceId'])?periodKey=$($state['periodKey'])"
if ($r.Status -ne 200) { throw "slice -> $($r.Status)" }
$sliceSec = $sw.Elapsed.TotalSeconds
$slice = $r.Json
Write-HseLog ("зріз: {0} рядків, {1} КБ, {2} c (разом із розбором JSON)" -f @($slice.rows).Count, [int]($r.Text.Length / 1KB), [math]::Round($sw.Elapsed.TotalSeconds, 1))
$byKey = @{}
foreach ($sr in $slice.rows) { $byKey[[string]$sr.rowKey] = $sr.cells }

$classes = @{}      # "клас|колонка" -> n
$real = New-Object System.Collections.Generic.List[object]
$examples = @{}     # клас -> перші 3 приклади
function Add-Class($cls, $code, $example) {
    $k = "$cls|$code"; if ($classes.ContainsKey($k)) { $classes[$k]++ } else { $classes[$k] = 1 }
    if (-not $examples.ContainsKey($cls)) { $examples[$cls] = New-Object System.Collections.Generic.List[string] }
    if ($examples[$cls].Count -lt 3) { $examples[$cls].Add($example) }
}
$nCells = 0
for ($i = 1; $i -le $rows.Count; $i++) {
    $row = $rows[$i - 1]; $v = $row['v']; $fl = $row['f']
    $cells = $byKey[('r{0:0000}' -f $i)]
    $pcode = if ($v.ContainsKey('O')) { ([string]$v['O']).Trim() } else { '' }
    foreach ($c in $cmp) {
        $nCells++
        $xl = $c.Xl
        $excel = if ($v.ContainsKey($xl)) { [string]$v[$xl] } else { $null }
        $isF = ($fl -contains $xl)
        $ecr = $null
        if ($null -ne $cells -and $cells.PSObject.Properties[$c.Code]) { $ecr = [string]$cells.($c.Code) }
        $isText = ($c.Type -eq 'String')
        $match = $false; $abs = 0.0
        if ($isText) {
            $e = if ($null -eq $excel) { '' } else { $excel.Trim() }; $a = if ($null -eq $ecr) { '' } else { $ecr.Trim() }
            $match = ($e -ceq $a)
        }
        else {
            $e = 0.0; $a = 0.0
            if ($null -ne $excel -and $excel -ne '') { [void][double]::TryParse($excel, [Globalization.NumberStyles]::Float, $inv, [ref]$e) }
            if ($null -ne $ecr -and $ecr -ne '') { [void][double]::TryParse($ecr, [Globalization.NumberStyles]::Float, $inv, [ref]$a) }
            $abs = [Math]::Abs($a - $e)
            $den = [Math]::Max([Math]::Abs($a), [Math]::Abs($e))
            $match = ($abs -eq 0) -or ($den -gt 0 -and ($abs / $den) -le 1e-9)
        }
        if ($match) { Add-Class 'match' $c.Code ''; continue }
        $ex = "row $($row['r']) $($c.Code) excel=[$excel] ecr=[$ecr]"
        if (-not $isText -and $abs -le 1e-13) { Add-Class 'tolerance' $c.Code $ex; continue }
        if ($row['mixed']) { Add-Class 'mixed-basis-row' $c.Code $ex; continue }
        if (-not $isText -and $c.Code -like 'H_RQ*') {
            # Excel рахував сировину/години за днями в році (Configuration!I5), хоча місячні тонни - за жорстким базисом рядка
            $sumWd = 0.0
            $q = [int]$c.Code.Substring(4, 1)
            foreach ($mo in (3 * $q - 2), (3 * $q - 1), (3 * $q)) { $wk = $script:HseMonthWd[$mo - 1]; if ($v.ContainsKey($wk)) { $sumWd += [double]::Parse($v[$wk], $inv) } }
            $rawSrc = if ($c.Code -like '*_T') { 'BC' } else { 'BD' }
            $raw = 0.0; if ($v.ContainsKey($rawSrc)) { $raw = [double]::Parse($v[$rawSrc], $inv) }
            $alt = $raw / [double]$data['meta']['daysInYear'] * $sumWd
            if ([Math]::Abs($alt - $e) -le 1e-9 * [Math]::Max([Math]::Abs($alt), [Math]::Abs($e)) + 1e-13) { Add-Class 'mixed-basis-row' $c.Code $ex; continue }
        }
        if (-not $isText -and $c.Code -like 'H_Q?_GS' -and $isF) {
            # Excel: =S (g/s) незалежно від робочих днів кварталу; правило ECR (за аналізом): S лише якщо днів > 0
            $lim = 0.0; if ($v.ContainsKey('S')) { $lim = [double]::Parse($v['S'], $inv) }
            if ([Math]::Abs($e - $lim) -le 1e-9 * [Math]::Abs($lim) -and $a -eq 0) { Add-Class 'gs-rule' $c.Code $ex; continue }
        }        if ($isText) {
            $dict = $pol[$pcode]
            if ($c.Code -in 'H_COMPONENT', 'H_PHASE') {
                if (-not $isF) {
                    if ($c.Code -eq 'H_PHASE') { Add-Class 'phase-vocabulary' $c.Code $ex } else { Add-Class 'expected-literal' $c.Code $ex }
                    continue
                }
                # формула VLOOKUP наближено: код поза діапазоном (останній рядок словника) або сусідній
                $want = if ($null -ne $dict) { if ($c.Code -eq 'H_COMPONENT') { [string]$dict['component'] } else { [string]$dict['phase'] } } else { $null }
                if ($pcode -eq $lastCode -or ($null -ne $want -and $ecr -ceq $want)) { Add-Class 'VLOOKUP-3219' $c.Code $ex; continue }
            }
            if (-not $isF) { Add-Class 'expected-literal' $c.Code $ex; continue }
        }
        else {
            if (-not $isF) { Add-Class 'expected-literal' $c.Code $ex; continue }
        }
        Add-Class 'REAL' $c.Code $ex
        $real.Add([pscustomobject]@{ excelRow = $row['r']; rowKey = ('r{0:0000}' -f $i); column = $c.Code; excelCol = $xl; excel = $excel; ecr = $ecr; basis = $row['basis']; code = $pcode })
    }
}
Write-HseLog ("Порівняно {0} комірок за {1} колонками, {2} c" -f $nCells, $cmp.Count, [math]::Round($sw.Elapsed.TotalSeconds, 1))

$byClass = @{}
foreach ($k in $classes.Keys) { $cls = $k.Split('|')[0]; if (-not $byClass.ContainsKey($cls)) { $byClass[$cls] = 0 }; $byClass[$cls] += $classes[$k] }
Write-Host ''
Write-Host 'Клас                   Комірок'
foreach ($k in ($byClass.Keys | Sort-Object)) { Write-Host ("{0,-22} {1,9}" -f $k, $byClass[$k]) }
$csvRows = foreach ($k in ($classes.Keys | Sort-Object)) { $p = $k.Split('|'); [pscustomobject]@{ class = $p[0]; column = $p[1]; count = $classes[$k] } }
$csvRows | Export-Csv -LiteralPath (Join-Path $out "$OutPrefix-classes.csv") -NoTypeInformation -Encoding UTF8
$real | Export-Csv -LiteralPath (Join-Path $out "$OutPrefix-real.csv") -NoTypeInformation -Encoding UTF8
Write-HseLog ("REAL: {0} комірок -> {1}" -f $real.Count, (Join-Path $out "$OutPrefix-real.csv"))
foreach ($cls in ($examples.Keys | Where-Object { $_ -ne 'match' } | Sort-Object)) { foreach ($e in $examples[$cls]) { Write-Host ("  [{0}] {1}" -f $cls, $e) } }
if ($real.Count -gt 0) {
    Write-Host 'REAL по колонках:'
    $real | Group-Object column | Sort-Object Count -Descending | ForEach-Object { Write-Host ("  {0} x{1}" -f $_.Name, $_.Count) }
    Write-Host 'REAL приклади:'
    $real | Select-Object -First 10 | ForEach-Object { Write-Host ("  Excel row {0} {1}({2}): excel=[{3}] ecr=[{4}] basis={5} code={6}" -f $_.excelRow, $_.column, $_.excelCol, $_.excel, $_.ecr, $_.basis, $_.code) }
}
$state['sliceSeconds'] = [math]::Round($sliceSec, 1); $state['sliceKB'] = [int]($r.Text.Length / 1KB)
$state['standKey'] = Get-Hse401StandKey
[IO.File]::WriteAllText($stateFile, (ConvertTo-HseJson -Value $state).ToString(), (New-Object Text.UTF8Encoding($false)))
