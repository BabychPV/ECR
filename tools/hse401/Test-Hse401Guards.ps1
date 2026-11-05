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

if ($script:failures -gt 0) { Write-Host "Падінь: $script:failures"; exit 1 }
Write-Host 'Усі перевірки зелені.'
exit 0
