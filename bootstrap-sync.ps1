<#
.SYNOPSIS
    Розгортає структуру каналу синхронізації двовузлового процесу (ПК-1 / ПК-2).

.DESCRIPTION
    ПРИЗНАЧЕННЯ
        Створює каркас каналу обміну між двома вузлами Claude Code:
        теки `docs/sync/`, `docs/sync/pk1/`, `docs/sync/pk2/`, журнал відхилень
        `docs/sync/DELTA.md` і локальну (некомітовану) теку `.sync-local/`.

    ХТО ЗАПУСКАЄ
        Людина, один раз на кожній машині, з КОРЕНЯ репозиторію:
            powershell -ExecutionPolicy Bypass -File bootstrap-sync.ps1
        Скрипт ІДЕМПОТЕНТНИЙ: повторний запуск безпечний.

    ЩО ЗМІНЮЄ
        * створює перелічені вище теки, якщо їх немає;
        * створює `docs/sync/DELTA.md` із заголовком, ЯКЩО файлу немає;
        * додає рядок `.sync-local/` у `.gitignore`, якщо його там немає
          (ДОПИСУЄ байти в кінець, файл не перезаписує).

    ЧОГО СВІДОМО НЕ РОБИТЬ
        * НЕ перезаписує жодного наявного файлу — ні DELTA.md, ні .gitignore,
          ні протоколів у `docs/sync/`. Наявне лишається як є, завжди.
        * НЕ створює гілок, НЕ робить `git add`, НЕ комітить і НЕ пушить.
        * НЕ пише `.sync-local/NODE` — роль вузла ставить `scripts/install-node.ps1`.
        * НЕ вмикає git-хуки і НЕ чіпає `~/.claude/settings.json` — це теж
          робота `scripts/install-node.ps1`.
        * НЕ звертається до мережі та до `gh`.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File bootstrap-sync.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# ── Корінь репозиторію ────────────────────────────────────────────────────────
# Скрипт лежить у корені, тож $PSScriptRoot і є коренем. Якщо скрипт
# запустили дот-сорсингом (тоді $PSScriptRoot порожній) — беремо поточну теку.
if ($PSScriptRoot) {
    $repoRoot = $PSScriptRoot
} else {
    $repoRoot = (Get-Location).Path
}

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
    Write-Host ''
    Write-Host "  ПОМИЛКА: '$repoRoot' не схоже на корінь git-репозиторію (немає '.git')." -ForegroundColor Red
    Write-Host '  Запускай bootstrap-sync.ps1 саме з кореня репозиторію.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '=== bootstrap-sync: канал синхронізації ПК-1 / ПК-2 ===' -ForegroundColor Cyan
Write-Host "    корінь: $repoRoot"
Write-Host ''

# Звіт: що створено, а що вже було.
$report = New-Object System.Collections.ArrayList

function Add-Report {
    param(
        [Parameter(Mandatory)][string] $Item,
        [Parameter(Mandatory)][ValidateSet('СТВОРЕНО', 'ВЖЕ БУЛО')][string] $Status,
        [string] $Note = ''
    )
    $row = New-Object psobject
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Обʼєкт'  -Value $Item
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Стан'    -Value $Status
    Add-Member -InputObject $row -MemberType NoteProperty -Name 'Примітка' -Value $Note
    [void] $report.Add($row)

    if ($Status -eq 'СТВОРЕНО') {
        Write-Host "  [+] СТВОРЕНО  $Item" -ForegroundColor Green
    } else {
        Write-Host "  [=] вже було  $Item" -ForegroundColor DarkGray
    }
}

function New-DirIfMissing {
    param([Parameter(Mandatory)][string] $Relative)

    $full = Join-Path $repoRoot $Relative
    if (Test-Path -LiteralPath $full -PathType Container) {
        Add-Report -Item "$Relative/" -Status 'ВЖЕ БУЛО'
        return
    }
    if (Test-Path -LiteralPath $full) {
        # Шлях є, але це файл — це не те, чого ми чекали. Мовчати не можна.
        throw "'$Relative' існує, але це ФАЙЛ, а не тека. Прибери його вручну і повтори запуск."
    }
    [void] (New-Item -ItemType Directory -Path $full)
    Add-Report -Item "$Relative/" -Status 'СТВОРЕНО'
}

# ── Крок 1. Теки каналу ──────────────────────────────────────────────────────
Write-Host '--- Крок 1/4: теки каналу' -ForegroundColor Yellow
New-DirIfMissing -Relative 'docs\sync'
New-DirIfMissing -Relative 'docs\sync\pk1'
New-DirIfMissing -Relative 'docs\sync\pk2'
Write-Host ''

# ── Крок 2. Журнал відхилень DELTA.md ────────────────────────────────────────
Write-Host '--- Крок 2/4: docs/sync/DELTA.md' -ForegroundColor Yellow

$deltaPath = Join-Path $repoRoot 'docs\sync\DELTA.md'
if (Test-Path -LiteralPath $deltaPath) {
    Add-Report -Item 'docs/sync/DELTA.md' -Status 'ВЖЕ БУЛО' -Note 'не чіпаємо: файл append-only'
} else {
    # Одинарні лапки: вміст беремо буквально, жодної підстановки змінних.
    $deltaSeed = @'
# DELTA — журнал відхилень від ТЗ

Файл **тільки дописується** (append-only). Ніколи не редагуй і не видаляй
наявні записи: DELTA — це слід рішень, а не робочий чернетник. Виправлення
попереднього запису оформлюється НОВИМ записом із посиланням на старий.

**Один запис = одне відхилення.** Якщо зробив три відхилення — три записи.

`DL-NNN` — номер запису, суцільна нумерація, наступний вільний.
`D-NNN` — рішення з `docs/tz/10-decisions.md`, від якого відхилився.
Якщо рішення немає — пиши `D-—`.

`Вплив` — рівно одне зі: `LOW` | `MEDIUM` | `HIGH`.
* `LOW` — не змінює поведінки для користувача і не звужує ТЗ;
* `MEDIUM` — змінює внутрішній контракт, поведінку видно в коді або тестах;
* `HIGH` — змінює зовнішню поведінку, дані або обсяг ТЗ. Такий запис
  супроводжується міткою `needs:human` на відповідному issue.

## Формат запису

```
## DL-NNN | D-NNN | YYYY-MM-DD
Що в ТЗ:
Що зробив:
Чому:
Вплив: LOW|MEDIUM|HIGH
```

## Приклад

```
## DL-001 | D-042 | 2026-01-15
Що в ТЗ: обчислення виконує збережена процедура.
Що зробив: обчислення в застосунку, процедура лишилась тонкою обгорткою.
Чому: у процедурі неможливо покрити граничні випадки тестами.
Вплив: MEDIUM
```

---

'@
    # UTF-8 БЕЗ BOM: DELTA.md читають і хуки bash, і grep. BOM на початку
    # файлу ламає порівняння першого рядка в них.
    [System.IO.File]::WriteAllText($deltaPath, $deltaSeed, (New-Object System.Text.UTF8Encoding($false)))
    Add-Report -Item 'docs/sync/DELTA.md' -Status 'СТВОРЕНО' -Note 'заголовок + формат запису'
}
Write-Host ''

# ── Крок 3. Локальна тека .sync-local/ ───────────────────────────────────────
Write-Host '--- Крок 3/4: .sync-local/' -ForegroundColor Yellow
New-DirIfMissing -Relative '.sync-local'
Write-Host ''

# ── Крок 4. .gitignore: рядок .sync-local/ ───────────────────────────────────
Write-Host '--- Крок 4/4: .gitignore' -ForegroundColor Yellow

$gitignorePath = Join-Path $repoRoot '.gitignore'
$needle = '.sync-local/'

# Читаємо байтами і декодуємо явно як UTF-8. Get-Content у PS 5.1 без BOM
# сприймає файл як ANSI і калічить кирилицю — а в цьому .gitignore є
# українські коментарі. Тому: байти + явний UTF8, і НІКОЛИ не перезапис.
$alreadyIgnored = $false
$gitignoreExists = Test-Path -LiteralPath $gitignorePath

if ($gitignoreExists) {
    $bytes = [System.IO.File]::ReadAllBytes($gitignorePath)
    $text = (New-Object System.Text.UTF8Encoding($false)).GetString($bytes)
    foreach ($line in ($text -split "`n")) {
        $trimmed = $line.Trim().TrimEnd("`r")
        if ($trimmed -eq $needle -or $trimmed -eq '.sync-local' -or $trimmed -eq '/.sync-local/' -or $trimmed -eq '/.sync-local') {
            $alreadyIgnored = $true
            break
        }
    }
}

if ($alreadyIgnored) {
    Add-Report -Item '.gitignore -> .sync-local/' -Status 'ВЖЕ БУЛО'
} else {
    $addition = "`r`n" `
        + "# Локальний стан вузла: роль (NODE), журнали циклів, lock-файли.`r`n" `
        + "# У кожної машини свій — у репозиторій не потрапляє.`r`n" `
        + ".sync-local/`r`n"

    # AppendAllText з UTF8Encoding($false) дописує чисті UTF-8 байти без BOM.
    # Дописування (а не перезапис) безпечне і для файлу з BOM, і без нього.
    [System.IO.File]::AppendAllText($gitignorePath, $addition, (New-Object System.Text.UTF8Encoding($false)))

    if ($gitignoreExists) {
        Add-Report -Item '.gitignore -> .sync-local/' -Status 'СТВОРЕНО' -Note 'дописано в кінець файлу'
    } else {
        Add-Report -Item '.gitignore -> .sync-local/' -Status 'СТВОРЕНО' -Note '.gitignore створено заново'
    }
}
Write-Host ''

# ── Підсумок ─────────────────────────────────────────────────────────────────
$created = @($report | Where-Object { $_.'Стан' -eq 'СТВОРЕНО' })
$existed = @($report | Where-Object { $_.'Стан' -eq 'ВЖЕ БУЛО' })

Write-Host '=== ПІДСУМОК ===' -ForegroundColor Cyan
$report | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host ("  створено: {0}    вже було: {1}" -f $created.Count, $existed.Count)
Write-Host ''
Write-Host '  Наступний крок — прив''язати машину до ролі:' -ForegroundColor Yellow
Write-Host '      powershell -ExecutionPolicy Bypass -File scripts\install-node.ps1 -Node PK1'
Write-Host '      powershell -ExecutionPolicy Bypass -File scripts\install-node.ps1 -Node PK2'
Write-Host ''

exit 0
