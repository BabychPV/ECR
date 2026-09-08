<#
.SYNOPSIS
    Перевірка дій КЕРУЮЧОГО вузла (PK1). Запускає виконавець (PK2).

.DESCRIPTION
    ПРИЗНАЧЕННЯ
        Двовузлова схема асиметрична: PK1 видає директиви й мержить, тобто має
        більше влади. Ця влада досі не перевірялася нічим — і саме тому схема
        трималася на тому, що керуючий не помиляється. Скрипт закриває цю
        прогалину: PK2 машинно перевіряє, чи PK1 сам додержує протоколу.

    ХТО ЗАПУСКАЄ
        Виконуючий вузол (PK2) на КОЖНОМУ циклі, кроком 0.5 —
        `docs/sync/PROMPT-PK2-CYCLE.md`. Запускати може будь-хто, скрипт
        нічого не змінює в репозиторії.

    ⛔ ЧОМУ ЦЕЙ ФАЙЛ ПИШЕ PK1, А ЗАПУСКАЄ PK2
        `scripts/**` — зона записи PK1, і PK2 не має права цей файл змінювати.
        Це не незручність, а властивість: перевірку керуючого не пише той, кого
        вона перевіряє, — інакше вона нічого не гарантує. Натомість PK2 бачить
        її вихід і має право заблокувати роботу на знахідці.

    ЩО ЗМІНЮЄ
        * `.sync-local/acceptance/<номер>.sha` — відбиток блоку `acceptance`
          директиви, щоб виявити його зміну після взяття в роботу (правило
          заморозки, `docs/sync/40-DIRECTIVE-SCHEMA.md`). Локальний стан,
          у репозиторій не потрапляє.

    ЧОГО СВІДОМО НЕ РОБИТЬ
        * НЕ править issues, НЕ ставить міток, НЕ комітить. Знахідка — це
          звіт; що з нею робити, вирішує PK2 за своїм циклом.
        * НЕ звертається до `src/**` і взагалі до вмісту коду.
        * НЕ падає на першій знахідці: перелік мусить бути повним, інакше
          виправлять одне й повернуться за рештою.

.PARAMETER Quiet
    Друкувати лише знахідки, без таблиці «усе гаразд». Для планувальника.

.OUTPUTS
    Код виходу 0 — порушень немає. 1 — є (перелік у виводі).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\audit-pk1.ps1
#>
[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# ── Межі протоколу (docs/sync/30-GH-CONTROL-PLANE.md §3) ─────────────────────
$MaxOpenDirectives = 5
$MaxP0 = 1
$StaleReviewHours = 4   # скільки PR може чекати рев'ю, поки це не стане знахідкою

# ── Розв'язання gh ────────────────────────────────────────────────────────────
function Resolve-GhPath {
    $onPath = Get-Command 'gh' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $fallback = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw "не знайдено gh CLI ні в PATH, ні в '$fallback'. Встановити: winget install --id GitHub.cli"
}

$gh = Resolve-GhPath

if ($PSScriptRoot) { $repoRoot = Split-Path -Parent $PSScriptRoot }
else { $repoRoot = (Get-Location).Path }
Set-Location -LiteralPath $repoRoot

function Invoke-GhJson {
    param([string[]] $Arguments)
    $raw = & $gh @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') повернув $LASTEXITCODE :`n$raw"
    }
    if ([string]::IsNullOrWhiteSpace(($raw | Out-String))) { return @() }
    # @() обов'язкове: ConvertFrom-Json на одному об'єкті віддає скаляр,
    # і .Count на ньому падає під Set-StrictMode -Version 2.
    return @((($raw | Out-String) | ConvertFrom-Json))
}

# ── Накопичувач знахідок ─────────────────────────────────────────────────────
$findings = New-Object System.Collections.ArrayList

function Add-Finding {
    param(
        [ValidateSet('КРИТИЧНО', 'ВАЖЛИВО', 'УВАГА')] [string] $Severity,
        [string] $Rule,
        [string] $Where,
        [string] $What,
        [string] $Action
    )
    [void] $findings.Add([pscustomobject]@{
        Severity = $Severity; Rule = $Rule; Where = $Where
        What = $What; Action = $Action
    })
}

Write-Host ''
Write-Host '=== audit-pk1: перевірка дій керуючого вузла ===' -ForegroundColor Cyan
Write-Host ''

# ── А. Маршрутизація: рівно одна мітка needs:* на відкритому issue ───────────
# Нуль означає, що задача застрягла і жоден вузол її не побачить у своїй черзі.
# Дві означають, що обидва візьмуть її одночасно. Обидва випадки безсимптомні.

$open = @(Invoke-GhJson @('issue', 'list', '--state', 'open', '--limit', '200',
                        '--json', 'number,title,labels,updatedAt'))

if (-not $Quiet) { Write-Host "--- А. Маршрутизація ($($open.Count) відкритих issues)" }

foreach ($i in $open) {
    $names = @($i.labels | ForEach-Object { $_.name })
    $routing = @($names | Where-Object { $_ -like 'needs:*' })

    if ($routing.Count -eq 0) {
        Add-Finding 'КРИТИЧНО' 'І-роутинг' "issue #$($i.number)" `
            'немає жодної мітки needs:* — задачу не побачить жоден вузол' `
            'поставити needs:pk1 / needs:pk2 / needs:human'
    }
    elseif ($routing.Count -gt 1) {
        Add-Finding 'КРИТИЧНО' 'І-роутинг' "issue #$($i.number)" `
            "мітки needs:* одразу $($routing.Count): $($routing -join ', ') — обидва вузли візьмуть у роботу" `
            'лишити рівно одну'
    }
}

# ── Б. Межі навантаження ─────────────────────────────────────────────────────
# Більше п'яти відкритих директив означає, що виконавець працює за пріоритетом,
# якого не існує; два P0 означає, що пріоритету немає взагалі.

if (-not $Quiet) { Write-Host '--- Б. Межі навантаження' }

$forPk2 = @($open | Where-Object {
    @($_.labels | ForEach-Object { $_.name }) -contains 'needs:pk2'
})
if ($forPk2.Count -gt $MaxOpenDirectives) {
    Add-Finding 'ВАЖЛИВО' 'межа ≤5' 'черга needs:pk2' `
        "відкритих директив $($forPk2.Count), межа $MaxOpenDirectives" `
        'частину перевести в prio:P2 або закрити як CANCELLED'
}

$p0 = @($open | Where-Object {
    @($_.labels | ForEach-Object { $_.name }) -contains 'prio:P0'
})
if ($p0.Count -gt $MaxP0) {
    Add-Finding 'ВАЖЛИВО' 'межа ≤1 P0' 'черга' `
        "директив prio:P0 одразу $($p0.Count): #$(($p0 | ForEach-Object { $_.number }) -join ', #')" `
        'лишити один P0, решту знизити до P1'
}

# ── В. Валідність директив ───────────────────────────────────────────────────
# Директива без scope гарантовано впаде на scope-guard; без acceptance її
# неможливо здати; без default_on_ambiguity вона породжує цикл «питання-
# відповідь» на 25 хвилин за раз.

if (-not $Quiet) { Write-Host "--- В. Валідність директив ($($forPk2.Count) для PK2)" }

$acceptDir = Join-Path $repoRoot '.sync-local\acceptance'
if (-not (Test-Path -LiteralPath $acceptDir)) {
    [void] (New-Item -ItemType Directory -Path $acceptDir -Force)
}

foreach ($i in $forPk2) {
    $viewed = @(Invoke-GhJson @('issue', 'view', "$($i.number)", '--json', 'body'))
    if ($viewed.Count -gt 0) { $body = $viewed[0].body } else { $body = '' }
    if ($null -eq $body) { $body = '' }

    $hasScope = ($body -match '(?m)^\s*scope:\s*$') -and
                ($body -match '(?m)^\s*-\s+\S')
    if (-not $hasScope) {
        Add-Finding 'КРИТИЧНО' 'схема §1' "issue #$($i.number)" `
            'немає списку scope: — PR гарантовано впаде на scope-guard' `
            'не брати в роботу; status:blocked + needs:pk1'
    }

    if ($body -notmatch '(?m)^\s*acceptance:\s*$') {
        Add-Finding 'КРИТИЧНО' 'схема §2' "issue #$($i.number)" `
            'немає acceptance: — критерій здачі не визначений' `
            'не брати в роботу; status:blocked + needs:pk1'
    }
    elseif ($body -notmatch 'CI job') {
        Add-Finding 'ВАЖЛИВО' 'схема §2' "issue #$($i.number)" `
            'acceptance не посилається на імена CI-джобів — здачу неможливо перевірити машинно' `
            'вимагати переформулювання через build/test/scope-guard/contracts-guard/honesty-guard'
    }

    if ($body -notmatch '(?m)^\s*default_on_ambiguity:') {
        Add-Finding 'ВАЖЛИВО' 'схема §3' "issue #$($i.number)" `
            'немає default_on_ambiguity — головний запобіжник проти циклу питань відсутній' `
            'застосувати власний розумний дефолт і записати в DELTA.md; повідомити PK1'
    }

    # ── Г. Заморозка acceptance ──────────────────────────────────────────────
    # Критерій, який рухається, неможливо виконати: робота знецінюється не
    # помилкою виконавця, а зміною умов після подачі.
    $block = ''
    $inBlock = $false
    foreach ($line in ($body -split "`r?`n")) {
        if ($line -match '^\s*acceptance:\s*$') { $inBlock = $true; continue }
        if ($inBlock) {
            if ($line -match '^\s*-\s+\S') { $block += $line.Trim() + "`n"; continue }
            if ($line -match '^\s*$') { continue }
            break
        }
    }

    if ($block -ne '') {
        $sha = [BitConverter]::ToString(
            [Security.Cryptography.SHA256]::Create().ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($block))).Replace('-', '').Substring(0, 16)

        $shaFile = Join-Path $acceptDir "$($i.number).sha"
        if (Test-Path -LiteralPath $shaFile) {
            $known = (Get-Content -LiteralPath $shaFile -Raw).Trim()
            if ($known -ne $sha) {
                Add-Finding 'КРИТИЧНО' 'заморозка' "issue #$($i.number)" `
                    "блок acceptance ЗМІНИВСЯ після взяття в роботу (було $known, стало $sha)" `
                    'зупинити роботу; коментар в issue + needs:human — нова вимога має бути НОВОЮ директивою'
            }
        }
        else {
            Set-Content -LiteralPath $shaFile -Value $sha -NoNewline -Encoding ascii
        }
    }
}

# ── Д. Керуючий у чужій зоні ─────────────────────────────────────────────────
# Хук це ловить локально, але лише на машині PK1. Якщо там його вимкнули —
# видно це буде тільки звідси, з історії.

if (-not $Quiet) { Write-Host '--- Д. Зона записи керуючого (останні 50 комітів)' }

$log = & git log -50 --format='%H|%s' 2>$null
foreach ($entry in @($log)) {
    if ([string]::IsNullOrWhiteSpace($entry)) { continue }
    $parts = $entry -split '\|', 2
    if ($parts.Count -lt 2) { continue }
    if ($parts[1] -notmatch '^\[PK1\]') { continue }

    $files = & git show --name-only --format='' $parts[0] 2>$null
    foreach ($f in @($files)) {
        if ([string]::IsNullOrWhiteSpace($f)) { continue }
        if ($f -match '^(src/|tests/|tools/)' -or $f -match '\.(csproj|sln)$') {
            Add-Finding 'КРИТИЧНО' 'І-2 зони' "коміт $($parts[0].Substring(0,8))" `
                "керуючий вузол змінив '$f' — це зона PK2" `
                'повідомити людину (needs:human): бар''єр на машині PK1 не працює'
        }
    }
}

# ── Е. Рев'ю, що зависло ─────────────────────────────────────────────────────
# Не порушення протоколу, а сигнал: виконавець подав і чекає, а керуючий не
# приходить. Простій виконавця — найдорожча помилка схеми.

if (-not $Quiet) { Write-Host '--- Е. Затримка рев''ю' }

$review = @($open | Where-Object {
    $n = @($_.labels | ForEach-Object { $_.name })
    ($n -contains 'status:review') -and ($n -contains 'needs:pk1')
})
foreach ($i in $review) {
    $age = (Get-Date) - [datetime]$i.updatedAt
    if ($age.TotalHours -gt $StaleReviewHours) {
        Add-Finding 'УВАГА' 'простій' "issue #$($i.number)" `
            ("чекає рев'ю {0:N1} год (межа {1})" -f $age.TotalHours, $StaleReviewHours) `
            'нагадати коментарем; не простоювати — брати наступну директиву'
    }
}

# ── Звіт ─────────────────────────────────────────────────────────────────────

Write-Host ''
if ($findings.Count -eq 0) {
    Write-Host '=== ПОРУШЕНЬ НЕМАЄ: керуючий вузол додержує протоколу ===' -ForegroundColor Green
    Write-Host ''
    exit 0
}

Write-Host "=== ЗНАЙДЕНО ПОРУШЕНЬ: $($findings.Count) ===" -ForegroundColor Red
Write-Host ''
$findings | Sort-Object @{Expression = {
    switch ($_.Severity) { 'КРИТИЧНО' { 0 } 'ВАЖЛИВО' { 1 } default { 2 } }
}} | Format-Table -AutoSize -Wrap Severity, Rule, Where, What, Action

Write-Host '  Що з цим робити (PROMPT-PK2-CYCLE.md, крок 0.5):' -ForegroundColor Yellow
Write-Host '    КРИТИЧНО -> роботу за цією директивою НЕ починати;'
Write-Host '                коментар в issue з цитатою знахідки + мітка needs:pk1'
Write-Host '                (а для порушення зон або заморозки — needs:human).'
Write-Host '    ВАЖЛИВО  -> можна працювати, але знахідку записати в'
Write-Host '                docs/sync/DELTA.md і назвати в коментарі до issue.'
Write-Host '    УВАГА    -> лише повідомити; на роботу не впливає.'
Write-Host ''
Write-Host '  ⛔ Знахідку НЕ виправляй сам: мітки й тіла директив — зона PK1.' -ForegroundColor Yellow
Write-Host ''
exit 1
