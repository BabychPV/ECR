<#
.SYNOPSIS
    Статичні перевірки складу MSI (без установки) і прогін сценаріїв 1, 5,
    9, 15 автоматично (Q-222: коментар раніше називав 4 і 6, яких тут
    ніколи не було), плюс сценарії воркера W1/W2 (ФВ-9.8, D-206). Решта —
    вручну: вони потребують перезавантаження або відсутності прав.
.PARAMETER StaticOnly
    Лише статичні перевірки таблиць MSI: нічого не встановлює, безпечно на
    будь-якій машині (зокрема на машині збірки).
.NOTES
    ⛔ Без -StaticOnly запускати ЛИШЕ на тестовій машині. Скрипт встановлює і
    видаляє службу.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [string] $PreviousMsiPath,
    [string] $ServiceAccount,
    [switch] $StaticOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$MsiPath = (Resolve-Path $MsiPath).Path

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

# ── Статичні перевірки: таблиці MSI, без установки ────────────────────────
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($MsiPath, 0)   # 0 = лише читання

# Рядки запиту як масиви рядків (стовпці з 1). Порожній результат — порожній масив.
function Get-MsiRows([string] $query, [int] $columns) {
    # ⚠ [void] обов'язковий: COM-методи Execute/Close повертають VT_EMPTY, і
    # PowerShell видає його у вихід функції як $null — рядків ставало більше
    # на два (перевірено на синтетичному MSI).
    $view = $db.OpenView($query)
    [void] $view.Execute()
    $rows = @()
    while ($record = $view.Fetch()) {
        $rows += , @(1..$columns | ForEach-Object { $record.StringData($_) })
    }
    [void] $view.Close()
    return , $rows
}

# FileName у таблиці File — «КОРОТКЕ|Довге» або лише ім'я.
function Find-MsiFile([string] $longName) {
    $all = Get-MsiRows 'SELECT `File`, `Component_`, `FileName` FROM `File`' 3
    return , @($all | Where-Object { ($_[2] -split '\|')[-1] -eq $longName })
}

Test-Case 'S1. Ecr.Worker.exe і worker.settings.json у MSI' {
    foreach ($name in 'Ecr.Api.exe', 'Ecr.Worker.exe', 'Ecr.Worker.dll', 'worker.settings.json') {
        $found = Find-MsiFile $name
        if ($found.Count -ne 1) { throw "$name у таблиці File: $($found.Count) рядків, очікували 1" }
    }
}

# I2-2 (2026-09-30): типово 1 — служба є на свіжій установці й на оновленні без
# властивостей; інакше оновлення мовчки знімало б виконавця перерахунку.
Test-Case 'S2. WORKER_ENABLED типово 1' {
    $rows = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'WORKER_ENABLED'" 1
    if ($rows.Count -ne 1 -or $rows[0][0] -ne '1') { throw "WORKER_ENABLED = '$($rows | ForEach-Object { $_[0] })', очікували '1'" }
    $secure = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'SecureCustomProperties'" 1
    if ($secure.Count -ne 1 -or ($secure[0][0] -split ';') -notcontains 'WORKER_ENABLED') {
        throw 'WORKER_ENABLED не в SecureCustomProperties — з /qn під UAC значення не дійде до серверної частини'
    }
}

Test-Case 'S3. Служба EcrWorker умовна, --supervisor, бінарник — Ecr.Worker.exe' {
    $svc = Get-MsiRows "SELECT ``Component_``, ``Arguments``, ``StartName`` FROM ``ServiceInstall`` WHERE ``Name`` = 'EcrWorker'" 3
    if ($svc.Count -ne 1) { throw "ServiceInstall EcrWorker: $($svc.Count) рядків" }
    $component = $svc[0][0]
    if ($svc[0][1] -ne '--supervisor') { throw "Arguments = '$($svc[0][1])'" }
    if ($svc[0][2] -ne '[SERVICE_ACCOUNT]') { throw "StartName = '$($svc[0][2])', очікували той самий обліковий запис, що EcrApi" }

    $comp = Get-MsiRows "SELECT ``Condition``, ``Attributes``, ``KeyPath`` FROM ``Component`` WHERE ``Component`` = '$component'" 3
    if ($comp[0][0] -notmatch 'WORKER_ENABLED\s*=\s*"1"') { throw "умова компонента '$($comp[0][0])' — служба ставилася б завжди" }
    if (([int] $comp[0][1] -band 0x40) -eq 0) { throw 'компонент не Transitive — REINSTALL з WORKER_ENABLED=0/1 не перемикатиме службу' }

    # Бінарник служби = ключовий файл компонента.
    $exe = Find-MsiFile 'Ecr.Worker.exe'
    if ($comp[0][2] -ne $exe[0][0]) { throw "KeyPath компонента '$($comp[0][2])' — не Ecr.Worker.exe ('$($exe[0][0])')" }
}

Test-Case 'S4. Старт EcrWorker — лише за WORKER_ENABLED=1 і SERVICE_ACCOUNT' {
    # Event: 0x1 = Start при установці.
    $ctl = Get-MsiRows "SELECT ``Component_``, ``Event`` FROM ``ServiceControl`` WHERE ``Name`` = 'EcrWorker'" 2
    $starts = @($ctl | Where-Object { ([int] $_[1] -band 0x1) -ne 0 })
    if ($starts.Count -ne 1) { throw "ServiceControl зі стартом EcrWorker: $($starts.Count)" }
    $cond = (Get-MsiRows "SELECT ``Condition`` FROM ``Component`` WHERE ``Component`` = '$($starts[0][0])'" 1)[0][0]
    if ($cond -notmatch 'WORKER_ENABLED' -or $cond -notmatch 'SERVICE_ACCOUNT') { throw "умова старту '$cond'" }
}

Test-Case 'S5. EcrApi — безумовна, як і раніше' {
    $svc = Get-MsiRows "SELECT ``Component_`` FROM ``ServiceInstall`` WHERE ``Name`` = 'EcrApi'" 1
    if ($svc.Count -ne 1) { throw "ServiceInstall EcrApi: $($svc.Count) рядків" }
    $cond = (Get-MsiRows "SELECT ``Condition`` FROM ``Component`` WHERE ``Component`` = '$($svc[0][0])'" 1)[0][0]
    if ($cond) { throw "компонент EcrApi отримав умову '$cond'" }
}

$db = $null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null

if ($StaticOnly) {
    $results | Format-Table -AutoSize
    if ($results.Result -match '^FAIL') { exit 1 }
    return
}

# ── Сценарії з установкою (лише тестова машина) ───────────────────────────
if ($env:COMPUTERNAME -eq 'PROD-SERVER') { throw "не запускати на продуктиві" }

Test-Case '1. Чиста установка' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v c1.log SERVICE_ACCOUNT=$ServiceAccount"
    $s = Get-Service EcrApi -ErrorAction Stop
    if ($s.StartType -ne 'Automatic') { throw "StartType = $($s.StartType)" }
    # I2-2: без WORKER_ENABLED служба воркера є — типове значення 1.
    $w = Get-CimInstance Win32_Service -Filter "Name='EcrWorker'"
    if (-not $w) { throw 'EcrWorker не зареєстровано без WORKER_ENABLED (типове 1)' }
    if ($w.PathName -notmatch 'Ecr\.Worker\.exe"?\s+--supervisor') { throw "PathName = $($w.PathName)" }
    $api = Get-CimInstance Win32_Service -Filter "Name='EcrApi'"
    if ($w.StartName -ne $api.StartName) { throw "обліковий запис EcrWorker '$($w.StartName)' ≠ EcrApi '$($api.StartName)'" }
}

Test-Case 'W1. WORKER_ENABLED=0 прибирає службу, EcrApi лишається (REINSTALL, транзитивний компонент)' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw1.log REINSTALL=ALL REINSTALLMODE=vomus WORKER_ENABLED=0 SERVICE_ACCOUNT=$ServiceAccount"
    if (Get-Service EcrWorker -ErrorAction SilentlyContinue) { throw 'EcrWorker лишився' }
    Get-Service EcrApi -ErrorAction Stop | Out-Null
}

Test-Case 'W2. Той самий MSI, WORKER_ENABLED=1 повертає службу' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw2.log REINSTALL=ALL REINSTALLMODE=vomus WORKER_ENABLED=1 SERVICE_ACCOUNT=$ServiceAccount"
    if (-not (Get-Service EcrWorker -ErrorAction SilentlyContinue)) { throw 'EcrWorker не зареєстровано' }
}

# I2-2: оновлення з попередньої версії БЕЗ властивостей — служба воркера є
# (попередня версія могла ставити без неї: тоді це саме той випадок, де
# Executor = Worker лишився б без виконавця, якби типове було 0).
if ($PreviousMsiPath) {
    Test-Case 'W3. Оновлення з попередньої версії без WORKER_ENABLED — служба є' {
        Invoke-Msi "/x `"$MsiPath`" /qn /l*v cw3x.log"
        Invoke-Msi "/i `"$((Resolve-Path $PreviousMsiPath).Path)`" /qn /l*v cw3a.log WORKER_ENABLED=0 SERVICE_ACCOUNT=$ServiceAccount"
        Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw3b.log SERVICE_ACCOUNT=$ServiceAccount"
        if (-not (Get-Service EcrWorker -ErrorAction SilentlyContinue)) { throw 'після оновлення EcrWorker немає' }
    }
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
    if (Get-Service EcrWorker -ErrorAction SilentlyContinue) { throw "служба EcrWorker лишилася" }
    if (-not (Test-Path "$env:ProgramData\ECR\config")) { throw "конфіг прибрано, а не мав бути" }
}

$results | Format-Table -AutoSize
if ($results.Result -match '^FAIL') { exit 1 }
