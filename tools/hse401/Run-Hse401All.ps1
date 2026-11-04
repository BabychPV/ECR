<#
.SYNOPSIS
    HSE401 від початку до кінця на ПУСТОМУ scratch-стенді (EcrHse401Scratch, :5180): облікові записи -> довідники -> NAE -> шаблон ->
    проєкт/документ -> завантаження -> очікування перерахунку -> порівняння з Excel. Кроки по черзі, час кожного - у out/hse401-timings.json.

.PARAMETER Extract  перевитягти книгу (повільно, ~2 хв); інакше береться out/hse401-extract.json.
.PARAMETER From     Accounts | Registries | Nae | Template | Project | Load | Compare - з якого кроку.
#>
[CmdletBinding()]
param([switch]$Extract, [ValidateSet('Accounts', 'Registries', 'Nae', 'Template', 'Project', 'Load', 'Compare')][string]$From = 'Accounts',
    [string]$BootstrapPassword = 'Hse401-Scratch-2026!')
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
$out = Get-Hse401OutDir
$order = 'Accounts', 'Registries', 'Nae', 'Template', 'Project', 'Load', 'Compare'
$timings = [ordered]@{}
function Step($name, [scriptblock]$body) {
    if ($order.IndexOf($name) -lt $order.IndexOf($From)) { return }
    Write-HseLog "=== $name ==="
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $body
    $timings[$name] = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Write-HseLog ("=== {0}: {1} c ===" -f $name, $timings[$name])
}
if ($Extract -or -not (Test-Path (Join-Path $out 'hse401-extract.json'))) { & (Join-Path $here 'Extract-Hse401.ps1') }
Step 'Accounts' { Initialize-Hse401Accounts -BootstrapPassword $BootstrapPassword }
Step 'Registries' { & (Join-Path $here 'Build-Hse401Registries.ps1') }
Step 'Nae' { & (Join-Path $here 'Import-Hse401Nae.ps1') }
Step 'Template' { & (Join-Path $here 'Build-Hse401Template.ps1') -Stage Template }
Step 'Project' { & (Join-Path $here 'Build-Hse401Template.ps1') -Stage Project }
Step 'Load' {
    $since = (Get-Date).ToUniversalTime().AddSeconds(-2)
    & (Join-Path $here 'Load-Hse401Document.ps1')
    $state = Read-HseJson (Join-Path $out 'hse401-state.json')
    Connect-Hse
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    $w = Wait-HseDocJobs -DocumentId ([int]$state['documentId']) -Since $since
    Write-HseLog ("перерахунок: задач {0}, невдалих {1}; кінець через {2} c після останнього PATCH; {3}" -f $w.Jobs, $w.Failed, [math]::Round($sw2.Elapsed.TotalSeconds, 1), ($w.Messages -join ' | '))
    $timings['RecalcWaitAfterLoadSec'] = [math]::Round($sw2.Elapsed.TotalSeconds, 1)
    $timings['RecalcSpanFromFirstJobSec'] = [math]::Round(($w.LastUpdated - $w.FirstCreated).TotalSeconds, 1)
}
Step 'Compare' { & (Join-Path $here 'Compare-Hse401.ps1') }
[IO.File]::WriteAllText((Join-Path $out 'hse401-timings.json'), (ConvertTo-HseJson -Value $timings).ToString(), (New-Object Text.UTF8Encoding($false)))
