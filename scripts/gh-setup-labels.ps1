<#
.SYNOPSIS
    Створює (або оновлює) повний набір міток GitHub, якими живе двовузловий процес.

.DESCRIPTION
    ПРИЗНАЧЕННЯ
        Керуючий шар процесу — GitHub Issues і PR, а не файли. Мітки в ньому
        не оздоблення, а СТАН: `status:*` — де річ зараз, `needs:*` — чия
        черга, `prio:*` — що першим, `type:*` — що це за робота,
        `iter:*` — до якої ітерації належить. Без цих міток скрипт циклу
        `sync-node-gh.ps1` не бачить черги і не запускає вузол.

    ХТО ЗАПУСКАЄ
        Людина, ОДИН раз на репозиторій, з будь-якої з двох машин:
            powershell -ExecutionPolicy Bypass -File scripts\gh-setup-labels.ps1

    ЩО ЗМІНЮЄ
        Тільки мітки в репозиторії GitHub (за домовчанням `BabychPV/ECR`):
        відсутні створює, наявним вирівнює колір і опис. Ідемпотентний:
        повторний запуск нічого не ламає і не дублює.

    ЧОГО СВІДОМО НЕ РОБИТЬ
        * НЕ видаляє жодної мітки — ні своєї, ні чужої, ні дефолтних
          `bug`/`enhancement`. Видалення мітки знімає її з усіх issue,
          а це втрата стану. Прибирати зайве — вручну, свідомо.
        * НЕ створює, НЕ закриває і НЕ редагує issue та PR.
        * НЕ чіпає локальний репозиторій: ні файлів, ні git config, ні хуків.
        * НЕ робить `git push` (тим паче `--force`) і НЕ комітить.
        * НЕ вмикає ніяких налаштувань репозиторію (branch protection тощо).

.PARAMETER Repo
    Репозиторій у вигляді `власник/назва`. За домовчанням `BabychPV/ECR`.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\gh-setup-labels.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\gh-setup-labels.ps1 -Repo BabychPV/ECR
#>
[CmdletBinding()]
param(
    [string] $Repo = 'BabychPV/ECR'
)

$ErrorActionPreference = 'Stop'

# ── Пошук gh ─────────────────────────────────────────────────────────────────
# На цій машині gh стоїть у 'C:\Program Files\GitHub CLI\gh.exe' і в PATH
# може не бути (зокрема під Планувальником завдань, де середовище інше).
# Порядок: PATH -> відомий абсолютний шлях -> зрозуміла відмова.
function Resolve-GhPath {
    try {
        $cmd = Get-Command -Name 'gh' -CommandType Application -ErrorAction Stop
        if ($cmd -is [array]) { $cmd = $cmd[0] }
        if ($cmd -and $cmd.Source) { return $cmd.Source }
    } catch {
        # У PATH немає — це не помилка, просто йдемо до запасного шляху.
    }

    $fallbacks = @(
        'C:\Program Files\GitHub CLI\gh.exe',
        'C:\Program Files (x86)\GitHub CLI\gh.exe'
    )
    foreach ($candidate in $fallbacks) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    throw ("gh CLI не знайдено." + [Environment]::NewLine +
           "  Шукав: у PATH (команда 'gh'), потім:" + [Environment]::NewLine +
           "    " + ($fallbacks -join ([Environment]::NewLine + "    ")) + [Environment]::NewLine +
           "  Постав GitHub CLI (https://cli.github.com/) або вкажи правильний шлях у цьому скрипті.")
}

# ── Виклик gh з надійним кодом виходу ────────────────────────────────────────
# Як і з git: у PS 5.1 stderr native-команди приходить ErrorRecord'ами і під
# 'Stop' стає термінальною помилкою навіть при коді виходу 0. Аргументи —
# ОДНИМ масивом, бо PowerShell перехопив би `--` і будь-який `-x`.
function Invoke-Gh {
    param([Parameter(Mandatory)][string[]] $GhArgs)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & $script:ghPath @GhArgs 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }

    $lines = @()
    foreach ($item in @($raw)) {
        if ($null -ne $item) { $lines += [string]$item }
    }

    $result = New-Object psobject
    Add-Member -InputObject $result -MemberType NoteProperty -Name 'ExitCode' -Value $code
    Add-Member -InputObject $result -MemberType NoteProperty -Name 'Lines'    -Value $lines
    Add-Member -InputObject $result -MemberType NoteProperty -Name 'Text'     -Value ($lines -join [Environment]::NewLine)
    return $result
}

Write-Host ''
Write-Host '=== gh-setup-labels: мітки керуючого шару ===' -ForegroundColor Cyan

try {
    $script:ghPath = Resolve-GhPath
} catch {
    Write-Host ''
    Write-Host "  ПОМИЛКА: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
Write-Host "    gh:   $script:ghPath"
Write-Host "    репо: $Repo"

$ver = Invoke-Gh @('--version')
if ($ver.ExitCode -ne 0) {
    Write-Host ''
    Write-Host "  ПОМИЛКА: '$script:ghPath --version' повернув $($ver.ExitCode):" -ForegroundColor Red
    Write-Host $ver.Text
    exit 1
}
Write-Host "    версія: $(@($ver.Lines)[0])"

# Авторизація. Без неї кожен виклик упаде поодинці й діагностика розпливеться.
$auth = Invoke-Gh @('auth', 'status')
if ($auth.ExitCode -ne 0) {
    Write-Host ''
    Write-Host '  ПОМИЛКА: gh не авторизований (gh auth status повернув ' -ForegroundColor Red -NoNewline
    Write-Host "$($auth.ExitCode)):" -ForegroundColor Red
    Write-Host $auth.Text
    Write-Host ''
    Write-Host '  Авторизуйся вручну і повтори запуск:' -ForegroundColor Yellow
    Write-Host '      gh auth login'
    exit 1
}
Write-Host '    авторизація: є' -ForegroundColor Green

# ── Перелік міток процесу ────────────────────────────────────────────────────
# Кольори підібрані так, щоб група читалась оком: status — за «температурою»
# (синій -> жовтий -> фіолетовий -> червоний -> зелений), prio — червоний
# градієнт, iter — бліді відтінки, щоб не сперечались зі станом.
function New-Label {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Color,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][string] $Group
    )
    $o = New-Object psobject
    Add-Member -InputObject $o -MemberType NoteProperty -Name 'Name'        -Value $Name
    Add-Member -InputObject $o -MemberType NoteProperty -Name 'Color'       -Value $Color
    Add-Member -InputObject $o -MemberType NoteProperty -Name 'Description' -Value $Description
    Add-Member -InputObject $o -MemberType NoteProperty -Name 'Group'       -Value $Group
    return $o
}

$labels = @(
    # ── Стан: де річ зараз ──
    (New-Label -Group 'status' -Name 'status:open'        -Color '1D76DB' -Description 'Заведено, до роботи ще не бралися'),
    (New-Label -Group 'status' -Name 'status:in-progress' -Color 'FBCA04' -Description 'У роботі на вузлі-виконавці'),
    (New-Label -Group 'status' -Name 'status:review'      -Color '5319E7' -Description 'Готово, чекає на рев''ю керуючого вузла'),
    (New-Label -Group 'status' -Name 'status:blocked'     -Color 'B60205' -Description 'Заблоковано: бракує рішення, доступу або залежності'),
    (New-Label -Group 'status' -Name 'status:done'        -Color '0E8A16' -Description 'Прийнято і закрито'),
    (New-Label -Group 'status' -Name 'status:escalated'   -Color 'D93F0B' -Description 'Передано людині: вузли самі не вирішують'),
    (New-Label -Group 'status' -Name 'status:cancelled'   -Color 'CFD3D7' -Description 'Скасовано: робота більше не потрібна'),

    # ── Маршрутизація: чия зараз черга ──
    (New-Label -Group 'routing' -Name 'needs:pk1'   -Color '006B75' -Description 'Черга ПК-1 (керуючий, Opus): директива, відповідь, рев''ю'),
    (New-Label -Group 'routing' -Name 'needs:pk2'   -Color '0052CC' -Description 'Черга ПК-2 (виконавець, Sonnet): код, тести, інструменти'),
    (New-Label -Group 'routing' -Name 'needs:human' -Color 'E99695' -Description 'Черга людини: права, зовнішні рішення, аномалії'),

    # ── Пріоритет: що першим ──
    (New-Label -Group 'prio' -Name 'prio:P0' -Color 'B60205' -Description 'P0: блокує все інше, беруть негайно'),
    (New-Label -Group 'prio' -Name 'prio:P1' -Color 'D93F0B' -Description 'P1: у поточній ітерації обов''язково'),
    (New-Label -Group 'prio' -Name 'prio:P2' -Color 'FEF2C0' -Description 'P2: можна відкласти на наступну ітерацію'),

    # ── Тип роботи ──
    (New-Label -Group 'type' -Name 'type:implement'   -Color '0E8A16' -Description 'Нова функціональність за ТЗ'),
    (New-Label -Group 'type' -Name 'type:fix'         -Color 'D73A4A' -Description 'Виправлення дефекту наявної поведінки'),
    (New-Label -Group 'type' -Name 'type:investigate' -Color 'A2EEEF' -Description 'Розслідування: спершу з''ясувати, потім вирішувати'),
    (New-Label -Group 'type' -Name 'type:contract'    -Color '5319E7' -Description 'Зміна контракту: API, схема даних, формат обміну'),
    (New-Label -Group 'type' -Name 'type:chore'       -Color 'CFD3D7' -Description 'Обслуговування: складання, скрипти, залежності'),

    # ── Ітерація ──
    (New-Label -Group 'iter' -Name 'iter:1' -Color 'BFDADC' -Description 'Ітерація 1'),
    (New-Label -Group 'iter' -Name 'iter:2' -Color 'C5DEF5' -Description 'Ітерація 2'),
    (New-Label -Group 'iter' -Name 'iter:3' -Color 'D4C5F9' -Description 'Ітерація 3')
)

Write-Host ''
Write-Host "--- Мітки до опрацювання: $($labels.Count)" -ForegroundColor Yellow

# ── Які мітки вже є ──────────────────────────────────────────────────────────
# Знаючи наявні, можемо чесно розрізнити СТВОРЕНО і ОНОВЛЕНО у звіті.
# Якщо `--json` не підтримується (давній gh) — переходимо на `create --force`.
$existing = @()
$knowExisting = $false

$list = Invoke-Gh @('label', 'list', '--repo', $Repo, '--limit', '300', '--json', 'name')
if ($list.ExitCode -eq 0 -and $list.Text.Trim() -ne '') {
    try {
        $parsed = $list.Text | ConvertFrom-Json -ErrorAction Stop
        foreach ($item in @($parsed)) {
            if ($item -and $item.name) { $existing += [string]$item.name }
        }
        $knowExisting = $true
        Write-Host "    у репозиторії вже є міток: $($existing.Count)" -ForegroundColor DarkGray
    } catch {
        Write-Host "    не вдалося розібрати перелік міток як JSON — працюємо через 'create --force'" -ForegroundColor Magenta
    }
} else {
    Write-Host "    'gh label list --json' недоступний (код $($list.ExitCode)) — працюємо через 'create --force'" -ForegroundColor Magenta
}

# ── Створення / оновлення ────────────────────────────────────────────────────
$report = New-Object System.Collections.ArrayList
$failed = 0

foreach ($label in $labels) {
    $alreadyThere = $false
    if ($knowExisting) {
        foreach ($name in $existing) {
            if ($name -eq $label.Name) { $alreadyThere = $true; break }
        }
    }

    $action = ''
    $note = ''

    if ($alreadyThere) {
        # Мітка є — вирівнюємо колір і опис, не торкаючись прив'язок до issue.
        $res = Invoke-Gh @('label', 'edit', $label.Name,
            '--repo', $Repo,
            '--color', $label.Color,
            '--description', $label.Description)
        if ($res.ExitCode -eq 0) {
            $action = 'ОНОВЛЕНО'
        } else {
            $action = 'ЗБІЙ'
            $note = $res.Text.Trim()
            $failed++
        }
    } else {
        # Мітки немає (або ми не знаємо) — створюємо з --force, який
        # перезаписує наявну замість падіння з 'already exists'.
        $res = Invoke-Gh @('label', 'create', $label.Name,
            '--repo', $Repo,
            '--color', $label.Color,
            '--description', $label.Description,
            '--force')

        if ($res.ExitCode -eq 0) {
            if ($knowExisting) { $action = 'СТВОРЕНО' } else { $action = 'СТВОРЕНО/ОНОВЛЕНО' }
        } elseif ($res.Text -match 'unknown flag|--force') {
            # Давній gh без --force: створюємо без нього, а на 'already exists'
            # переходимо на edit.
            $res2 = Invoke-Gh @('label', 'create', $label.Name,
                '--repo', $Repo,
                '--color', $label.Color,
                '--description', $label.Description)
            if ($res2.ExitCode -eq 0) {
                $action = 'СТВОРЕНО'
            } else {
                $res3 = Invoke-Gh @('label', 'edit', $label.Name,
                    '--repo', $Repo,
                    '--color', $label.Color,
                    '--description', $label.Description)
                if ($res3.ExitCode -eq 0) {
                    $action = 'ОНОВЛЕНО'
                } else {
                    $action = 'ЗБІЙ'
                    $note = $res3.Text.Trim()
                    $failed++
                }
            }
        } else {
            $action = 'ЗБІЙ'
            $note = $res.Text.Trim()
            $failed++
        }
    }

    $row = New-Object psobject
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Група'    -Value $label.Group
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Мітка'    -Value $label.Name
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Колір'    -Value $label.Color
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Дія'      -Value $action
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Примітка' -Value $note
    [void] $report.Add($row)

    if ($action -eq 'ЗБІЙ') {
        Write-Host ("  [ЗБІЙ] {0,-20} {1}" -f $label.Name, $note) -ForegroundColor Red
    } else {
        Write-Host ("  [ok]   {0,-20} #{1}  {2}" -f $label.Name, $label.Color, $action) -ForegroundColor Green
    }
}

# ── Звіт ─────────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== ЗВІТ ===' -ForegroundColor Cyan
$report | Format-Table -Property 'Група', 'Мітка', 'Колір', 'Дія', 'Примітка' -AutoSize |
    Out-String -Width 220 | Write-Host

$created = @($report | Where-Object { $_.'Дія' -like 'СТВОРЕНО*' }).Count
$updated = @($report | Where-Object { $_.'Дія' -eq 'ОНОВЛЕНО' }).Count

Write-Host ("  усього: {0}    створено: {1}    оновлено: {2}    збоїв: {3}" -f `
    $labels.Count, $created, $updated, $failed)

if ($failed -gt 0) {
    Write-Host ''
    Write-Host "  ⛔ $failed мітк(и) не опрацьовано. Найчастіші причини:" -ForegroundColor Red
    Write-Host "     * немає права запису в '$Repo' (gh auth status -> перевір scope 'repo');" -ForegroundColor Red
    Write-Host '     * помилка в назві репозиторію (параметр -Repo);' -ForegroundColor Red
    Write-Host '     * немає мережі або GitHub відповідає 5xx.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Host ''
Write-Host '  Усі мітки процесу на місці.' -ForegroundColor Green
Write-Host ''

exit 0
