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
    [string]$TemplateCode = 'LAND_UI'
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$modelFile = $ModelPath
if (-not $modelFile) { $modelFile = Join-Path $repo 'docs\delivery\reference-data\land-template\land-template.model.json' }
. (Join-Path $here 'EcrApi.ps1')
Set-StrictMode -Off
Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password

$mdl = Get-Content -LiteralPath $modelFile -Raw -Encoding UTF8 | ConvertFrom-Json
$t = (Invoke-Ecr -Method GET -Path ('/api/v1/templates?limit=50&q=' + $TemplateCode)).Json
$tpl = @($t.items) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
$vers = @((Invoke-Ecr -Method GET -Path "/api/v1/templates/$($tpl.id)/versions?limit=100").Json.items)
$ver = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
$st = (Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($ver.id)/structure").Json
$hf = @((Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($ver.id)/header-fields").Json)

$bad = @(); $monthTotal = 0
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
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
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

if ($ExcelModelPath -and (Test-Path $ExcelModelPath)) {
    $x = Get-Content -LiteralPath $ExcelModelPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $xs = @($x.sheets).Count
    $xcols = ($x.sheets | ForEach-Object { @($_.tables).ForEach({ @($_.columns).Count }) } | Measure-Object -Sum).Sum
    Write-Host "Excel-модель: аркушів $xs; логічних колонок $xcols"
}
if ($bad.Count) { Write-Host "РОЗБІЖНОСТІ ($($bad.Count)):"; $bad | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }; exit 1 }
Write-Host 'Розбіжностей немає.'
