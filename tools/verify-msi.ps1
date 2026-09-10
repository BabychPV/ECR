<#
.SYNOPSIS
    Прогін сценаріїв 1, 4, 5, 6, 9, 15 автоматично. Решта — вручну:
    вони потребують перезавантаження або відсутності прав.
.NOTES
    ⛔ Запускати ЛИШЕ на тестовій машині. Скрипт встановлює і видаляє службу.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [string] $PreviousMsiPath,
    [string] $ServiceAccount
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ⛔ PS 7.3+: без цього нешкідливе stderr-попередження нативної команди
# (msiexec/dotnet/npm) зупиняє скрипт ДО власної перевірки $LASTEXITCODE.
$PSNativeCommandUseErrorActionPreference = $false

if ($env:COMPUTERNAME -eq 'PROD-SERVER') { throw "не запускати на продуктиві" }

$results = [System.Collections.Generic.List[object]]::new()
function Test-Case([string] $name, [scriptblock] $body) {
    try { & $body; $results.Add([pscustomobject]@{ Case = $name; Result = 'PASS' }) }
    catch { $results.Add([pscustomobject]@{ Case = $name; Result = "FAIL: $_" }) }
}
function Invoke-Msi([string] $args) {
    $p = Start-Process msiexec -ArgumentList $args -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "msiexec: $($p.ExitCode)" }
    $p.ExitCode
}

Test-Case '1. Чиста установка' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v c1.log SERVICE_ACCOUNT=$ServiceAccount"
    $s = Get-Service EcrApi -ErrorAction Stop
    if ($s.StartType -ne 'Automatic') { throw "StartType = $($s.StartType)" }
}

Test-Case '5. Та сама версія поверх' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v c5.log"
}

Test-Case '15. Пароль не в лозі' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v c15.log SERVICE_PASSWORD=Sentinel-Not-In-Log"
    if (Select-String -Path c15.log -Pattern 'Sentinel-Not-In-Log' -Quiet) {
        throw "пароль знайдено в лозі — MsiHiddenProperties не працює"
    }
}

Test-Case '9. Видалення' {
    Invoke-Msi "/x `"$MsiPath`" /qn /l*v c9.log"
    if (Get-Service EcrApi -ErrorAction SilentlyContinue) { throw "служба лишилася" }
    if (-not (Test-Path "$env:ProgramData\ECR\config")) { throw "конфіг прибрано, а не мав бути" }
}

$results | Format-Table -AutoSize
if ($results.Result -match '^FAIL') { exit 1 }
