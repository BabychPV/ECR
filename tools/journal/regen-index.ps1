<#
.SYNOPSIS
    Перебудовує docs/build/questions.md (зведення) з docs/build/questions/Q-N.md.

.DESCRIPTION
    Директива паралельного аудиту (2026-09-11): доки журнал був ОДНИМ файлом,
    кожна паралельна лінія робіт правила рядок зведення й секцію запису в
    ньому самому — і кожна лінія конфліктувала з кожною іншою на цьому самому
    файлі, гарантовано. Рішення — одна задача, один файл: кожен запис живе
    у docs/build/questions/Q-N.md, а questions.md стає ГЕНЕРОВАНИМ індексом.

    Джерело правди для кожного рядка зведення — сам файл запису:
    frontmatter (id/type/status) плюс розділи `## Зведення` (Тема) і
    `## Статус (зведення)` (Статус). Секцію `## Деталі` індекс НЕ копіює —
    вона лишається лише у файлі запису.

    Статичну частину (легенда типів, шаблон, правила заповнення) скрипт бере
    з docs/build/questions-preamble.md і просто копіює на початок — це
    людський текст, не дані, регенерувати його нема з чого.

.PARAMETER RepoRoot
    Корінь репозиторію. За замовчуванням — обчислюється від розташування
    цього скрипта (tools/journal/regen-index.ps1 → на два рівні вгору).
#>
[CmdletBinding()]
param(
    [string] $RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ⚠ `$PSScriptRoot` не заповнений під час обчислення значення ЗА
# ЗАМОВЧУВАННЯМ у самому `param()` (це рахується ДО того, як скрипт
# "офіційно" починається) — лишається порожнім рядком, і `Split-Path` падає
# на ньому. Обчислення винесено сюди, у тіло скрипта, де `$PSScriptRoot` уже
# є.
if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$questionsDir = Join-Path $RepoRoot 'docs/build/questions'
$preamblePath = Join-Path $RepoRoot 'docs/build/questions-preamble.md'
$indexPath = Join-Path $RepoRoot 'docs/build/questions.md'

if (-not (Test-Path $preamblePath)) {
    throw "Немає docs/build/questions-preamble.md — статичну частину індексу нема звідки взяти."
}

if (-not (Test-Path $questionsDir)) {
    throw "Немає docs/build/questions/ — журнал ще не розділено на файли Q-N.md."
}

# ⚠ Номер сортується ЧИСЛОМ, не рядком: "Q-10" мав би стати перед "Q-2"
# лексикографічно, а це вже не наскрізна нумерація (`Правило 3`).
function Get-QuestionNumber([string] $Id) {
    if ($Id -match '^Q-(\d+)$') { return [int]$Matches[1] }
    throw "Не вдалося розпізнати номер запису: '$Id'"
}

# ⚠ Розділ `## Заголовок` читається до НАСТУПНОГО `## ` того самого рівня —
# не до кінця файлу: `## Деталі` іде одразу після `## Статус (зведення)`, і
# без цієї межі "Тема"/"Статус" ковтали б увесь вміст деталей теж.
function Get-Section([string] $Text, [string] $Heading) {
    $pattern = "(?ms)^## $([regex]::Escape($Heading))\s*\n(.*?)(?=\n## |\z)"
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        throw "Розділ '## $Heading' не знайдено."
    }

    return $match.Groups[1].Value.Trim()
}

function Get-Frontmatter([string] $Text) {
    $match = [regex]::Match($Text, '(?ms)^---\s*\n(.*?)\n---\s*\n')
    if (-not $match.Success) {
        throw 'Немає YAML frontmatter (--- ... ---) на початку файлу.'
    }

    $result = @{}
    foreach ($line in $match.Groups[1].Value -split "`n") {
        if ($line -match '^(\w+):\s*(.*)$') {
            $result[$Matches[1]] = $Matches[2].Trim()
        }
    }

    return $result
}

$files = Get-ChildItem -Path $questionsDir -Filter 'Q-*.md' -File
if ($files.Count -eq 0) {
    throw "docs/build/questions/ порожній — індекс регенерувати нема з чого."
}

$rows = foreach ($file in $files) {
    $text = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    $front = Get-Frontmatter $text

    $id = $front['id']
    $expectedId = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($id -ne $expectedId) {
        throw "Ім'я файлу '$($file.Name)' і id у frontmatter ('$id') розходяться."
    }

    [pscustomobject]@{
        Number = Get-QuestionNumber $id
        Id     = $id
        Type   = $front['type']
        # ⚠ Перенос рядка в комірці таблиці ламає Markdown-таблицю — тема й
        # статус можуть бути багатоабзацні у файлі джерела (перенесення
        # редактором), тому пробіли/переноси стискаються в один пробіл лише
        # ТУТ, для рядка індексу; сам файл запису лишається читабельним.
        Theme  = (Get-Section $text 'Зведення') -replace '\s+', ' '
        Status = (Get-Section $text 'Статус (зведення)') -replace '\s+', ' '
    }
}

$sorted = $rows | Sort-Object Number

$table = New-Object System.Text.StringBuilder
[void]$table.AppendLine('## Зведення')
[void]$table.AppendLine()
[void]$table.AppendLine('> ⛔ Ця таблиця ГЕНЕРУЄТЬСЯ з `docs/build/questions/Q-N.md` скриптом')
[void]$table.AppendLine('> `tools/journal/regen-index.ps1` (директива паралельного аудиту,')
[void]$table.AppendLine('> 2026-09-11). НЕ редагуй рядки тут напряму — правка розійдеться з')
[void]$table.AppendLine('> файлом джерела при наступній регенерації. Редагуй сам `Q-N.md` і')
[void]$table.AppendLine('> перезапусти скрипт.')
[void]$table.AppendLine()
[void]$table.AppendLine('| ID | Тип | Тема | Статус |')
[void]$table.AppendLine('|---|---|---|---|')

foreach ($row in $sorted) {
    [void]$table.AppendLine("| $($row.Id) | $($row.Type) | $($row.Theme) | $($row.Status) |")
}

$preamble = (Get-Content -Path $preamblePath -Raw -Encoding UTF8).TrimEnd()
$content = $preamble + "`n`n" + $table.ToString().TrimEnd() + "`n"

# ⚠ UTF8 БЕЗ BOM: інакше кожен наступний git diff цього файлу показував би
# змінений перший байт навіть тоді, коли вміст не змінився.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($indexPath, $content, $utf8NoBom)

Write-Host "Перебудовано $indexPath з $($sorted.Count) записів ($($questionsDir))."
