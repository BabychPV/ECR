<#
.SYNOPSIS
    Порівнює побудований у ECR шаблон Land із моделлю (аркуші/таблиці/колонки/рядки/довідники/правила) і Excel-моделлю.

.DESCRIPTION
    Лише читання. Вхід: land-template.model.json (+ необов'язково template-model.json з розбору Excel), активний API.
    Друкує таблицю «аркуш: Excel -> ECR» і перелік розбіжностей; код виходу 1, якщо є розбіжності.

.EXAMPLE
    .\Verify-LandTemplate.ps1 -BaseUrl http://localhost:5094 -User land-owner -Password (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5094',
    [string]$User = 'land-owner',
    [securestring]$Password,
    [string]$ModelPath,
    [string]$ExcelModelPath,
    [string]$TemplateCode = 'LAND_UI',
    [string[]]$KnownStaleTables = @(),
    [int]$PrecisionChecksPerTable = 0
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$modelFile = $ModelPath
if (-not $modelFile) { $modelFile = Join-Path $repo 'docs\delivery\reference-data\land-template\land-template.model.json' }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandTemplateDiff.Lib.ps1')
Set-StrictMode -Off
Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password

$mdl = Get-Content -LiteralPath $modelFile -Raw -Encoding UTF8 | ConvertFrom-Json
$t = (Invoke-Ecr -Method GET -Path ('/api/v1/templates?limit=50&q=' + $TemplateCode)).Json
$tpl = @($t.items) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
$vers = @((Invoke-Ecr -Method GET -Path "/api/v1/templates/$($tpl.id)/versions?limit=100").Json.items)
$ver = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
$st = (Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($ver.id)/structure").Json
$hf = @((Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($ver.id)/header-fields").Json)

$bad = @(); $monthTotal = 0; $calcChecked = 0; $precChecked = 0; $precBad = 0
$pres = @{ checked = 0; width = 0; format = 0 }
$rows = @()
$sheetByCode = @{}
foreach ($s in @($st.sheets)) { $sheetByCode[[string]$s.code] = $s }
$tot = @{ xt = 0; et = 0; xc = 0; ec = 0; xr = 0; er = 0 }
foreach ($sh in @($mdl.sheets)) {
    $es = $sheetByCode[[string]$sh.code]
    if ($null -eq $es) { $bad += "немає аркуша $($sh.code)"; continue }
    $xt = @($sh.tables).Count; $et = @($es.tables).Count
    $xc = 0; $ec = 0; $xr = 0; $er = 0
    foreach ($tb in @($sh.tables)) {
        $etb = @($es.tables) | Where-Object { $_.code -eq $tb.code } | Select-Object -First 1
        if ($null -eq $etb) { $bad += "немає таблиці $($sh.code)/$($tb.code)"; continue }
        $mc = @($tb.columns | ForEach-Object { [string]$_.code })
        $ecs = @($etb.columns | ForEach-Object { [string]$_.code })
        $xc += $mc.Count; $ec += $ecs.Count
        $miss = @($mc | Where-Object { $ecs -notcontains $_ })
        if ($miss.Count) { $bad += "$($sh.code)/$($tb.code): немає колонок: $($miss -join ',')" }
        # LP-08: колонки результатів методологій (ecrAdded 'methodology output') мусять бути Calculated, тобто лише для читання
        foreach ($mcol in @($tb.columns | Where-Object { [string]$_.dataType -eq 'Calculated' })) {
            $ecol = @($etb.columns) | Where-Object { $_.code -eq $mcol.code } | Select-Object -First 1
            if ($null -eq $ecol) { continue }
            $calcChecked++
            if (-not ([string]$ecol.dataType -in 'Calculated', '8')) { $bad += "$($sh.code)/$($tb.code): колонка результату $($mcol.code) має тип $($ecol.dataType), а не Calculated" }
        }
        # L1 (стилі комірок): ширина й формат числа з моделі проти структури версії (styleId у DTO структури немає - див. перелік стилів нижче)
        foreach ($mcol in @($tb.columns)) {
            $wantFmt = Resolve-LandDisplayFormat $mcol; if (-not $mcol.widthPx -and -not $wantFmt) { continue }
            $ecol = @($etb.columns) | Where-Object { $_.code -eq $mcol.code } | Select-Object -First 1
            if ($null -eq $ecol) { continue }
            $pres.checked++
            if ($mcol.widthPx -and [int]$ecol.widthPx -ne [int]$mcol.widthPx) { $pres.width++ }
            if ($wantFmt -and [string]$ecol.displayFormat -ne [string]$wantFmt) { $pres.format++ }
        }
        # LP-29: Precision/Scale колонки мають збігатися з моделлю. Структура версії їх не носить, тому - GET колонки
        # (ліміт на таблицю: -PrecisionChecksPerTable, 0 = усі колонки з precision у моделі).
        $precCols = @($tb.columns | Where-Object { $_.precision })
        if ($PrecisionChecksPerTable -gt 0) { $precCols = @($precCols | Select-Object -First $PrecisionChecksPerTable) }
        foreach ($mcol in $precCols) {
            if (@($etb.columns | Where-Object { $_.code -eq $mcol.code }).Count -eq 0) { continue }
            $pc = (Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($ver.id)/tables/$($etb.id)/columns/$([string]$mcol.code)").Json
            $precChecked++
            $wantScale = if ($mcol.scale) { [int]$mcol.scale } else { $null }
            $gotScale = if ($null -ne $pc.scale) { [int]$pc.scale } else { $null }
            if ([int]$pc.precision -ne [int]$mcol.precision -or $gotScale -ne $wantScale) {
                $precBad++
                if ($precBad -le 20) { $bad += "$($sh.code)/$($tb.code): колонка $($mcol.code) Precision/Scale $($pc.precision)/$($pc.scale) vs модель $($mcol.precision)/$($mcol.scale)" }
            }
        }
        $xr += @($tb.rows).Count; $er += @($etb.rows).Count
        if (@($tb.rows).Count -ne @($etb.rows).Count) { $bad += "$($sh.code)/$($tb.code): рядків $(@($tb.rows).Count) vs $(@($etb.rows).Count)" }
        if ([string]$etb.layoutKind -ne [string]$tb.layout) { $bad += "$($sh.code)/$($tb.code): layout $($etb.layoutKind) vs $($tb.layout)" }
        # прапор IsMonthColumn не входить у DTO структури: перевіряємо вибірково першу й останню місячну колонку таблиці
        $mcols = @($tb.columns | Where-Object { $_.month -and $_.excel } | ForEach-Object { [string]$_.code })
        foreach ($mc in @($mcols | Select-Object -First 1) + @($mcols | Select-Object -Last 1)) {
            if (-not $mc) { continue }
            $cd = (Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($ver.id)/tables/$($etb.id)/columns/$mc").Json
            if (-not [bool]$cd.isMonthColumn) { $bad += "$($sh.code)/$($tb.code): колонка $mc не позначена IsMonthColumn" }
        }
        $monthTotal += $mcols.Count
    }
    $rows += [pscustomobject]@{ Sheet = $sh.excelName; XlTables = $xt; EcrTables = $et; ModelCols = $xc; EcrCols = $ec; ModelRows = $xr; EcrRows = $er }
    $tot.xt += $xt; $tot.et += $et; $tot.xc += $xc; $tot.ec += $ec; $tot.xr += $xr; $tot.er += $er
}
# D-YE-1: вираз формули колонки (Formula/Calculated) і readOnly зі структури версії проти моделі. Значень не друкуємо:
# лише таблиця: колонки. Раніше розбіжний вираз клону (T5_08/T5_09 Winter) Verify не ловив.
$fm = @(Get-LandFormulaMismatches $st $mdl)
foreach ($m in $fm) { $bad += $m }
# D-UPG-1: стеля динамічних рядків наявних таблиць проти моделі (клон зі старими T3_00/T9_04/T9_07 раніше проходив Verify
# і відхиляв рядки завантаження ECR-ROW-0409). Розбіжність - стеля нижча за модель.
$mr = @(Get-LandMaxRowsMismatches $st $mdl)
foreach ($m in $mr) { $bad += $m }
Write-Host ("Стелі динамічних рядків перевірено: таблиць нижче моделі {0}" -f $mr.Count)
Write-Host ("Формули колонок і readOnly перевірено: розбіжних таблиць {0}" -f $fm.Count)
# LP-28: таблиця є у версії, а в моделі її немає (застарілі T9a_03/06/07 клону) - розбіжність, окрім явного -KnownStaleTables.
$stale = @(Get-LandStaleTables $st $mdl $KnownStaleTables)
if ($stale.Count) { $bad += ("зайві таблиці (є у версії, немає в моделі): {0}: {1}" -f $stale.Count, (($stale | ForEach-Object { $_.Key }) -join ', ')) }
Write-Host ("Зайвих таблиць (є у версії, немає в моделі): {0}" -f $stale.Count)
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "Колонок результатів (Calculated) перевірено: $calcChecked"
if ($precBad -gt 0) { $bad += "Precision/Scale: розбіжностей з моделлю $precBad з $precChecked перевірених колонок" }
Write-Host "Колонок перевірено за Precision/Scale: $precChecked (розбіжностей $precBad)"
Write-Host ("Разом: таблиць {0}/{1}, колонок {2}/{3}, рядків Fixed {4}/{5}; місячних колонок у моделі {9}; версія {6} ({7}); полів шапки {8}" -f $tot.xt, $tot.et, $tot.xc, $tot.ec, $tot.xr, $tot.er, $ver.version, $ver.status, $hf.Count, $monthTotal)

# довідники
$regs = @((Invoke-Ecr -Method GET -Path '/api/v1/registries').Json)
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
$rn = 0
foreach ($r in @($mdl.registries)) {
    $x = $regs | Where-Object { $_.code -eq $r.code } | Select-Object -First 1
    if ($null -eq $x) { $bad += "немає довідника $($r.code)"; continue }
    $n = @((Invoke-Ecr -Method GET -Path "/api/v1/registries/$($r.code)/entries?asOf=$today").Json).Count
    $rn++
    if ($n -lt @($r.entries).Count) { $bad += "довідник $($r.code): записів $n < $(@($r.entries).Count)" }
}
Write-Host "Довідників перевірено: $rn"

# L1: стилі ролей Excel, на які посилаються колонки/шапки таблиць моделі, мусять існувати у версії; ширина/формат збігаються
$usedStyles = @{}
foreach ($sh in @($mdl.sheets)) {
    foreach ($tb in @($sh.tables)) {
        if ($tb.headerStyleRole) { $usedStyles[[string]$tb.headerStyleRole] = $true }
        foreach ($c in @($tb.columns)) { if ($c.styleRole) { $usedStyles[[string]$c.styleRole] = $true } }
    }
}
if ($usedStyles.Count -gt 0) {
    $vs = @((Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($ver.id)/styles").Json)
    $have = @($vs | ForEach-Object { [string]$_.code })
    $absent = @($usedStyles.Keys | Where-Object { $have -notcontains $_ })
    if ($absent.Count) { $bad += "стилів ролей немає у версії: $($absent -join ', ')" }
    Write-Host ("Стилів ролей: у моделі використано {0}, у версії {1}; презентація колонок перевірена {2}: розбіжностей ширини {3}, формату {4}" -f $usedStyles.Count, $have.Count, $pres.checked, $pres.width, $pres.format)
    if ($pres.width -or $pres.format) { $bad += "презентація колонок: ширина $($pres.width), формат $($pres.format) з $($pres.checked) не збігаються з моделлю" }
}

if ($ExcelModelPath -and (Test-Path $ExcelModelPath)) {
    $x = Get-Content -LiteralPath $ExcelModelPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $xs = @($x.sheets).Count
    $xcols = ($x.sheets | ForEach-Object { @($_.tables).ForEach({ @($_.columns).Count }) } | Measure-Object -Sum).Sum
    Write-Host "Excel-модель: аркушів $xs; логічних колонок $xcols"
}
if ($bad.Count) { Write-Host "РОЗБІЖНОСТІ ($($bad.Count)):"; $bad | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }; exit 1 }
Write-Host 'Розбіжностей немає.'
