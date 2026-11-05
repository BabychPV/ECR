<#
.SYNOPSIS
    Офлайн-перевірка запобіжників tools/hse401 (без стенда й бази): функції беруться з самих скриптів (AST), HTTP підмінено.
    D1 (репетиція 05.11): коли в NAE_TREE НЕМАЄ жодного чинного вузла (усі вилучені), пошук вилучених мусить їх знайти,
    а не прочитати порожню відповідь як «дерева немає». Код виходу 0 - усе зелене, 1 - є падіння.
#>
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

function Import-HseFunction([string]$File, [string[]]$Names) {
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $here $File), [ref]$null, [ref]$null)
    foreach ($n in $Names) {
        $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $n }, $true)
        if ($null -eq $fn) { throw "Функції $n у $File немає" }
        . ([scriptblock]::Create($fn.Extent.Text))
        Set-Item -Path "function:script:$n" -Value (Get-Item "function:$n").ScriptBlock
    }
}
Import-HseFunction 'Hse401Stand.Lib.ps1' @('Get-HseItems')
Import-HseFunction 'Import-Hse401Nae.ps1' @('Get-NaeEntriesAsOf', 'Find-NaeRetired')

$script:failures = 0
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { Write-Host "PASS  $Name" } else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# Підміна HTTP: сьогодні чинних немає (200 []), на минулі дати - вузли, що потім вилучені.
$script:retiredNodes = @(
    [pscustomobject]@{ id = 7514; code = 'R_ATYRAU'; display = 'Atyrau'; levelCode = 'REGION'; validTo = '2026-11-04' },
    [pscustomobject]@{ id = 7515; code = 'A_ATYRAU_ON'; display = 'Onshore'; levelCode = 'AREA'; validTo = '2026-11-04' }
)
$script:todayAsOf = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
function Get-EcrErrorCode($r) { '' }
function Invoke-Hse {
    param([string]$Method, [string]$Path)
    if ($Path -like "*asOf=$($script:todayAsOf)") { return [pscustomobject]@{ Status = 200; Json = @() } }
    return [pscustomobject]@{ Status = 200; Json = $script:retiredNodes }
}

$todayList = Get-NaeEntriesAsOf $script:todayAsOf
Check 'D1: порожня відповідь 200 - порожній масив, а не $null' ($null -ne $todayList -and $todayList.Count -eq 0)

$ret = Find-NaeRetired
Check 'D1: дерево існує, коли чинних вузлів 0' ([bool]$ret.Exists)
Check 'D1: зрізи на минулі дати виконано' ($ret.Scans -gt 0)
Check 'D1: обидва вилучені вузли знайдено' ($ret.Retired.Count -eq 2 -and $ret.Retired.ContainsKey([long]7514))
Check 'D1: чинних вузлів 0' ($ret.Active.Count -eq 0)

# 404 - дерева справді немає: перевірка вилучених не потрібна.
function Invoke-Hse { param([string]$Method, [string]$Path) [pscustomobject]@{ Status = 404; Json = $null } }
$ret404 = Find-NaeRetired
Check '404: дерева немає - Exists=false' (-not $ret404.Exists)

# ---- Модель шаблону (05.11): колонка L = H_NAE, H_MPE прибрано, назви й формати ----
. (Join-Path $here 'Hse401Model.Lib.ps1')
Import-HseFunction 'Update-Hse401Template.ps1' @('Get-HseL10nText', 'Get-HseTemplateDiff', 'Get-HseNextVersion')
$mcols = Get-Hse401Columns   # без @(): функція віддає масив через ", "
$byCode = @{}; foreach ($mc in $mcols) { $byCode[$mc.Code] = $mc }
Check 'Модель: H_MPE немає' (-not $byCode.ContainsKey('H_MPE'))
Check 'Модель: жодна колонка не посилається на H_MPE у формулі' (@($mcols | Where-Object { $_.Expr -and $_.Expr -match 'H_MPE' }).Count -eq 0)
Check 'Модель: H_NAE = колонка Excel L, Lookup на NAE_TREE level=SRC' ($byCode['H_NAE'].Xl -eq 'L' -and $byCode['H_NAE'].Type -eq 'Lookup' -and $byCode['H_NAE'].Registry -eq 'NAE_TREE' -and $byCode['H_NAE'].Filter -eq 'level=SRC')
Check 'Модель: H_NAE названо MPE number / ПДВ номер' ($byCode['H_NAE'].En -eq 'MPE number' -and $byCode['H_NAE'].Ru -eq 'ПДВ номер')
Check 'Модель: L єдина серед колонок' (@($mcols | Where-Object { $_.Xl -eq 'L' }).Count -eq 1)
Check 'Модель: H_NAE одразу після H_SRCTYPE' ([array]::IndexOf(@($mcols | ForEach-Object { $_.Code }), 'H_NAE') -eq ([array]::IndexOf(@($mcols | ForEach-Object { $_.Code }), 'H_SRCTYPE') + 1))
Check 'Модель: K = Emission Source Type' ($byCode['H_SRCTYPE'].En -ceq 'Emission Source Type')
Check 'Модель: усі H_Dnn = Work Days' (@($mcols | Where-Object { $_.Code -like 'H_D[0-9][0-9]' -and $_.En -cne 'Work Days' }).Count -eq 0)
Check 'Модель: R/S 12 знаків' ($byCode['H_LIMIT_T'].DisplayFormat -eq '0.000000000000' -and $byCode['H_LIMIT_GS'].DisplayFormat -eq '0.000000000000')
Check 'Модель: BC/BD формат 0.0000' ($byCode['H_RAW_T'].DisplayFormat -eq '0.0000' -and $byCode['H_RUN_H'].DisplayFormat -eq '0.0000')
Check 'Модель: формат показу <= 50 знаків' (@($mcols | Where-Object { $_.DisplayFormat -and $_.DisplayFormat.Length -gt 50 }).Count -eq 0)

# Update-Hse401Template: різниця. Живий знімок = стара модель (H_MPE, старі назви, без форматів).
function New-LiveCol($code, $ord, $en, $ru, $fmt, $hidden) { [pscustomobject]@{ code = $code; ordinal = $ord; headerL10n = [pscustomobject]@{ values = [pscustomobject]@{ en = $en; ru = $ru; kk = 'k' } }; displayFormat = $fmt; isHidden = $hidden } }
$live = @(); $o = 0
foreach ($mc in $mcols) {
    $o++
    $en = $mc.En; $ru = $mc.Ru; $fmt = $mc.DisplayFormat
    if ($mc.Code -eq 'H_NAE') { $en = 'Emission source (NAE)'; $ru = 'Источник эмиссии (NAE)' }
    if ($mc.Code -eq 'H_SRCTYPE') { $en = 'Emission source type' }
    if ($mc.Code -like 'H_D[0-9][0-9]') { $en = 'Work days' }
    if ($mc.Code -in 'H_LIMIT_T', 'H_LIMIT_GS', 'H_RAW_T', 'H_RUN_H') { $fmt = $null }
    $live += New-LiveCol $mc.Code $o $en $ru $fmt $false
    if ($mc.Code -eq 'H_NAE') { $o++; $live += New-LiveCol 'H_MPE' $o 'MPE number' 'ПДВ номер' $null $false }
}
$d = @(Get-HseTemplateDiff -LiveColumns $live -ModelColumns $mcols)
Check 'Diff: видалення H_MPE' (@($d | Where-Object { $_.Kind -eq 'RemoveColumn' -and $_.Code -eq 'H_MPE' }).Count -eq 1)
Check 'Diff: 4 формати' (@($d | Where-Object { $_.Kind -eq 'DisplayFormat' }).Count -eq 4)
Check 'Diff: H_NAE перейменовано (en і ru)' (@($d | Where-Object { $_.Kind -eq 'Retitle' -and $_.Code -eq 'H_NAE' }).Count -eq 2)
Check 'Diff: 15 назв (12 Work Days + K + H_NAE en/ru)' (@($d | Where-Object { $_.Kind -eq 'Retitle' }).Count -eq 15)
Check 'Diff: порядок не змінився (видалення не є перестановкою)' (@($d | Where-Object { $_.Kind -eq 'Reorder' }).Count -eq 0)
$dh = @(Get-HseTemplateDiff -LiveColumns $live -ModelColumns $mcols -HideInsteadOfDelete)
Check 'Diff: -HideInsteadOfDelete -> HideColumn' (@($dh | Where-Object { $_.Kind -eq 'HideColumn' }).Count -eq 1 -and @($dh | Where-Object { $_.Kind -eq 'RemoveColumn' }).Count -eq 0)
# ідемпотентність: живий знімок = модель (без H_MPE) -> порожньо
$live2 = @(); $o = 0
foreach ($mc in $mcols) { $o++; $live2 += New-LiveCol $mc.Code $o $mc.En $mc.Ru $mc.DisplayFormat $false }
Check 'Diff: повторний запуск порожній' (@(Get-HseTemplateDiff -LiveColumns $live2 -ModelColumns $mcols).Count -eq 0)
Check 'Diff: колонка моделі відсутня у версії -> MissingColumn' (@(Get-HseTemplateDiff -LiveColumns ($live2 | Select-Object -Skip 1) -ModelColumns $mcols | Where-Object { $_.Kind -eq 'MissingColumn' }).Count -eq 1)
Check 'Версія: 1.0.0.2 -> 1.0.0.3, наявну пропускає' ((Get-HseNextVersion '1.0.0.2' @()) -eq '1.0.0.3' -and (Get-HseNextVersion '1.0.0.2' @('1.0.0.3')) -eq '1.0.0.4')

if ($script:failures -gt 0) { Write-Host "Падінь: $script:failures"; exit 1 }
Write-Host 'Усі перевірки зелені.'
exit 0
