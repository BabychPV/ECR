<#
.SYNOPSIS
    Offline guards for tools/hse30x (no stand, no DB), same style as tools/hse401/Test-Hse401Guards.ps1.
    Exit code 0 - all green, 1 - failures.
#>
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse30xModel.Lib.ps1')

$script:failures = 0
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { Write-Host "PASS  $Name" } else { Write-Host "FAIL  $Name"; $script:failures++ }
}
function Get-ByCode($cols) { $h = @{}; foreach ($c in $cols) { $h[$c.code] = $c }; return $h }

$pairs = Get-H30ComponentColumns
$sg = Get-H30SgColumns; $sgBy = Get-ByCode $sg
$tox = Get-H30ToxColumns; $toxBy = Get-ByCode $tox
$gcv = @($pairs | ForEach-Object { "GCV_$($_.Column)" })

# ---- FLERT component list (FlertCompositionSchema.ComponentColumns) ----
Check 'FLERT: 49 component columns, 46 component codes' ($pairs.Count -eq 49 -and @($pairs | ForEach-Object { $_.Component } | Sort-Object -Unique).Count -eq 46)
Check 'FLERT: long aliases lead to the short code' ((@($pairs | Where-Object { $_.Column -ne $_.Component } | ForEach-Object { "$($_.Column)>$($_.Component)" }) -join ',') -eq 'Carbon_Monoxide>CO,Hydrogen>H2,Oxygen>O2')

# ---- Column sets ----
$sgBase = 'VARIANT', 'SourceEventId', 'Start', 'End', 'Duration', 'Value', 'Coefficient', 'Total', 'Location', 'Area', 'Unit', 'Equipment', 'TagNumber',
'FlareUnit', 'Category', 'IsAuto', 'IsOnshore', 'IsHMB', 'FlertStreamId', 'FlertCaseText', 'FlertCaseName', 'FlertCase', 'FlertNoComposition'
$toxBase = 'Date', 'Train', 'Mode', 'Type', 'StreamNumber', 'Volume', 'OperTime', 'MethodCalculation', 'SourceFile', 'SourceSheet',
'FlertCaseName', 'FlertCase', 'FlertNoComposition'
Check 'SG_FLARING: exact column set and order' ((@($sg | ForEach-Object { $_.code }) -join ',') -ceq (@($sgBase) + $gcv -join ','))
Check 'TOX_SG: exact column set and order' ((@($tox | ForEach-Object { $_.code }) -join ',') -ceq (@($toxBase) + $gcv + @('GCV_Helium', 'GCV_C2H6S') -join ','))
Check 'Enums: VARIANT / Location / Type values' ((@($sgBy['VARIANT'].values) -join ',') -eq 'INT,CONT_ON,CONT_OFF' -and (@($sgBy['Location'].values) -join ',') -eq 'ONSHORE,A_ISLAND,D_ISLAND' -and (@($toxBy['Type'].values) -join ',') -eq 'SOR,EOR')
Check 'Total = [Value] * [Coefficient], Coefficient default 1' ($sgBy['Total'].expression -ceq '[Value] * [Coefficient]' -and $sgBy['Coefficient'].defaultValue -eq '1')
$hiddenSg = 'SourceEventId', 'IsAuto', 'IsOnshore', 'IsHMB', 'FlertStreamId', 'FlertCaseText', 'FlertCaseName', 'FlertCase', 'FlertNoComposition'
Check 'SG: link/info columns and all GCV hidden, inputs visible' (@($sg | Where-Object { $_.hidden -ne (($hiddenSg -contains $_.code) -or $_.code -like 'GCV_*') }).Count -eq 0)
Check 'TOX: MethodCalculation/SourceFile/SourceSheet/link/GCV hidden' (@($tox | Where-Object { $_.hidden -ne ($_.code -in 'MethodCalculation', 'SourceFile', 'SourceSheet', 'FlertCaseName', 'FlertCase', 'FlertNoComposition' -or $_.code -like 'GCV_*') }).Count -eq 0)

# ---- FlertCase link per VARIANT ----
$intLink = "REGONE('STREAM_CASE', ROW.STREAM.LEGACY_ID = [FlertStreamId] && ROW.CASE_NAME = [FlertCaseName])"
$expected = "IF([VARIANT] = 'INT', $intLink, " +
"IF([VARIANT] = 'CONT_ON', REGONE('STREAM_CASE', ROW.STREAM.NUMBER = '416' && ROW.STREAM.IS_ONSHORE = TRUE && ROW.CASE_NAME = [FlertCaseName]), " +
"IF([VARIANT] = 'CONT_OFF', $intLink, REGONE('STREAM_CASE', FALSE))))"
Check 'SG FlertCase: INT / CONT_ON / CONT_OFF branches, unknown VARIANT -> #N/A' ($sgBy['FlertCase'].expression -ceq $expected)
$linkTest = Join-Path $here '../../tests/Ecr.Infrastructure.Tests/Jobs/FlertCaseLinkTests.cs'
if (Test-Path $linkTest) {
    $lt = [regex]::Match([IO.File]::ReadAllText($linkTest), 'Link => \$"(?<f>REGONE[^"]+)"').Groups['f'].Value -replace '\{Codes\.Case\}', 'STREAM_CASE'
    Check 'INT link = the formula FlertCaseLinkTests evaluates' ($lt -ceq $intLink)
}
Check 'TOX FlertCase: stream NUMBER + onshore + exact CASE_NAME' ($toxBy['FlertCase'].expression -ceq "REGONE('STREAM_CASE', ROW.STREAM.NUMBER = [StreamNumber] && ROW.STREAM.IS_ONSHORE = TRUE && ROW.CASE_NAME = [FlertCaseName])")
$all = @($sg) + @($tox) | Where-Object { $_.expression }
Check 'Template dialect: no CONTAINS / MONTH (methodology-only)' (@($all | Where-Object { $_.expression -match '\b(CONTAINS|MONTH)\s*\(' }).Count -eq 0)
Check 'FlertNoComposition = REGFIELD([FlertCase], NO_COMPOSITION) in both' ($sgBy['FlertNoComposition'].expression -ceq "REGFIELD([FlertCase], 'NO_COMPOSITION')" -and $toxBy['FlertNoComposition'].expression -ceq $sgBy['FlertNoComposition'].expression)

# ---- Season only on CONT_OFF ----
$season = "IF([Period].Sequence >= 4 && [Period].Sequence <= 9, 'Summer', 'Winter')"
Check 'SG FlertCaseName: season only in the CONT_OFF branch' ($sgBy['FlertCaseName'].expression -ceq "IF([VARIANT] = 'CONT_OFF', $season, [FlertCaseText])")
Check 'Season text appears in exactly one column (SG FlertCaseName)' (@($all | Where-Object { $_.expression -match "'Summer'|'Winter'" }).Count -eq 1)

# ---- GCV bridges ----
Check 'TOX: GCV_Helium and GCV_C2H6S = constant 0' ($toxBy['GCV_Helium'].expression -ceq '0' -and $toxBy['GCV_C2H6S'].expression -ceq '0')
Check 'SG: no Helium / C2H6S bridges (not FLERT columns)' (-not $sgBy.ContainsKey('GCV_Helium') -and -not $sgBy.ContainsKey('GCV_C2H6S'))
$okGcv = $true
foreach ($p in $pairs) {
    $e = "REGSUM('GAS_COMPOSITION', ROW.CASE = [FlertCase] && ROW.COMPONENT = REGFIND('COMPONENT', '$($p.Component)'), ROW.MOL_PCT)"
    if ($sgBy["GCV_$($p.Column)"].expression -cne $e -or $toxBy["GCV_$($p.Column)"].expression -cne $e) { $okGcv = $false }
}
Check 'GCV_<X> = REGSUM(GAS_COMPOSITION by case and component) in both tables' $okGcv
foreach ($a in @(@('Carbon_Monoxide', 'CO'), @('Hydrogen', 'H2'), @('Oxygen', 'O2'))) {
    Check "Alias GCV_$($a[0]) reads the short code $($a[1]) (= GCV_$($a[1]))" ($sgBy["GCV_$($a[0])"].expression -match "REGFIND\('COMPONENT', '$($a[1])'\)" -and $sgBy["GCV_$($a[0])"].expression -ceq $sgBy["GCV_$($a[1])"].expression)
}

# ---- Builder: preview only, idempotent ----
$builder = Join-Path $here 'Build-Hse30xTemplate.ps1'
$src = [IO.File]::ReadAllText($builder)
Check 'Builder: no HTTP / stand calls' ($src -notmatch 'Invoke-(RestMethod|WebRequest|Hse)|HttpClient|/api/v1')
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("hse30x-guard-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$ps = (Get-Process -Id $PID).Path
$r1 = & $ps -NoProfile -ExecutionPolicy Bypass -File $builder -OutDir $tmp
$h1 = (Get-FileHash (Join-Path $tmp 'hse30x-model.json')).Hash
$r2 = & $ps -NoProfile -ExecutionPolicy Bypass -File $builder -OutDir $tmp
$h2 = (Get-FileHash (Join-Path $tmp 'hse30x-model.json')).Hash
Check 'Builder: first run writes the model' ((@($r1) -join "`n") -match 'changes: [1-9]')
Check 'Builder: re-run = 0 changes, same bytes' ((@($r2) -join "`n") -match 'changes: 0' -and $h1 -eq $h2)
$j = [IO.File]::ReadAllText((Join-Path $tmp 'hse30x-model.json')) | ConvertFrom-Json
Check 'Model JSON: HSE30X, Monthly, two tables' ($j.templateCode -eq 'HSE30X' -and $j.periodKind -eq 'Monthly' -and (@($j.tables | ForEach-Object { $_.code }) -join ',') -eq 'SG_FLARING,TOX_SG')
Remove-Item -Recurse -Force $tmp

if ($script:failures -gt 0) { Write-Host "Failures: $script:failures"; exit 1 }
Write-Host 'All guards green.'
exit 0
