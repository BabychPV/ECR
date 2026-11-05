<#
.SYNOPSIS
    HSE401 від початку до кінця. За замовчуванням (Scratch) - на ПУСТОМУ scratch-стенді (EcrHse401Scratch, :5180): облікові записи -> довідники ->
    NAE -> шаблон -> проєкт/документ -> завантаження -> очікування перерахунку -> порівняння з Excel. Кроки по черзі, час кожного - у out/hse401-timings.json.
    -Target Live: той самий ланцюг на живому стенді (без кроку Accounts; вхід за -CredentialFile), див. README, розділ "Живий режим".
    -PreviewOnly: лише читання - план (що буде створено/заповнено) у консоль і out/hse401-preview.json.

.PARAMETER Extract  перевитягти книгу (повільно, ~2 хв); інакше береться out/hse401-extract.json.
.PARAMETER From     Accounts | Registries | Nae | Template | Project | Load | Compare - з якого кроку.
.PARAMETER Target   Scratch (за замовчуванням) | Live.
.PARAMETER BaseUrl / Database / ConfirmStand / CredentialFile   лише для Live (ConfirmStand = Database; файл поза git-worktree).
.PARAMETER AcceptNaePreview  Live: прийняти помилки/колізії кодів у NAE preview (вилучені вузли це НЕ обходить).
#>
[CmdletBinding()]
param([switch]$Extract, [ValidateSet('Accounts', 'Registries', 'Nae', 'Template', 'Project', 'Load', 'Compare')][string]$From = 'Accounts',
    [string]$BootstrapPassword = 'Hse401-Scratch-2026!',
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
# однакові параметри цілі для всіх дочірніх скриптів (кожен ставить ціль сам і перевіряє її)
$common = @{ Target = $Target }
if ($BaseUrl) { $common.BaseUrl = $BaseUrl }
if ($Database) { $common.Database = $Database }
if ($ConfirmStand) { $common.ConfirmStand = $ConfirmStand }
if ($CredentialFile) { $common.CredentialFile = $CredentialFile }
if ($PreviewOnly) { $common.PreviewOnly = $true }
if ($AcceptNaePreview) { $common.AcceptNaePreview = $true }
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
if ($PreviewOnly) { Reset-Hse401Plan }
if ($Extract -or -not (Test-Path (Join-Path $out 'hse401-extract.json'))) { & (Join-Path $here 'Extract-Hse401.ps1') }
Step 'Accounts' {
    if (Test-Hse401Live) { Write-HseLog 'Live: крок Accounts пропущено (без bootstrap, ролей і користувачів).' }
    elseif ($PreviewOnly) { Write-HseLog '[preview] крок Accounts пропущено.' }
    else { Initialize-Hse401Accounts -BootstrapPassword $BootstrapPassword }
}
Step 'Registries' { & (Join-Path $here 'Build-Hse401Registries.ps1') @common }
Step 'Nae' { & (Join-Path $here 'Import-Hse401Nae.ps1') @common }
Step 'Template' { & (Join-Path $here 'Build-Hse401Template.ps1') -Stage Template @common }
Step 'Project' { & (Join-Path $here 'Build-Hse401Template.ps1') -Stage Project @common }
Step 'Load' {
    $since = (Get-Date).ToUniversalTime().AddSeconds(-2)
    & (Join-Path $here 'Load-Hse401Document.ps1') @common
    if ($PreviewOnly) { return }
    $state = Read-HseJson (Get-Hse401StateFile)
    Assert-Hse401StateMatches $state
    Connect-Hse
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    $w = Wait-HseDocJobs -DocumentId ([int]$state['documentId']) -Since $since
    Write-HseLog ("перерахунок: задач {0}, невдалих {1}; кінець через {2} c після останнього PATCH; {3}" -f $w.Jobs, $w.Failed, [math]::Round($sw2.Elapsed.TotalSeconds, 1), ($w.Messages -join ' | '))
    $timings['RecalcWaitAfterLoadSec'] = [math]::Round($sw2.Elapsed.TotalSeconds, 1)
    $timings['RecalcSpanFromFirstJobSec'] = [math]::Round(($w.LastUpdated - $w.FirstCreated).TotalSeconds, 1)
}
Step 'Compare' { & (Join-Path $here 'Compare-Hse401.ps1') @common }
if ($PreviewOnly) { Write-Hse401PlanSummary; return }
[IO.File]::WriteAllText((Join-Path $out 'hse401-timings.json'), (ConvertTo-HseJson -Value $timings).ToString(), (New-Object Text.UTF8Encoding($false)))
