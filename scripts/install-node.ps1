<#
.SYNOPSIS
    Прив'язує ЦЮ машину до ролі вузла (PK1 або PK2). Зональний бар'єр
    знято 2026-09-08 — цей скрипт більше НЕ обмежує шляхи запису.

.DESCRIPTION
    ПРИЗНАЧЕННЯ
        Один запуск позначає машину керуючим вузлом (PK1, Opus) або
        виконавцем (PK2, Sonnet) і перевіряє на живому git, що запис
        УСПІШНО проходить (зональних заборон більше немає — людина
        прибрала їх прямим рішенням, бо вони ставали черговою
        "ASK і чекай" зупинкою при кожній новій межі каталогу).

    ХТО ЗАПУСКАЄ
        Людина, один раз на машині, після `bootstrap-sync.ps1`:
            powershell -ExecutionPolicy Bypass -File scripts\install-node.ps1 -Node PK1
            powershell -ExecutionPolicy Bypass -File scripts\install-node.ps1 -Node PK2

    ЩО ЗМІНЮЄ
        * `.sync-local/NODE` — рівно ім'я ролі, без переносу рядка;
        * `git config core.hooksPath .githooks` (локально, лише цей репозиторій);
        * `%USERPROFILE%\.claude\settings.json` — ЗЛИВАЄ (merge) ключ `model`
          і прибирає застарілі зональні `permissions.deny`, якщо вони лишились
          від прогону до 2026-09-08; перед записом робить резервну копію
          `.bak-<мітка часу>`;
        * тимчасово створює і прибирає пробний файл (`docs/__probe.tmp`).

    ЧОГО СВІДОМО НЕ РОБИТЬ
        * НЕ створює і НЕ редагує самі хуки — лише перевіряє їх наявність,
          права запуску і переноси рядків. Хуки — вміст репозиторію.
        * НЕ пушить нічого. Пробний коміт відкручується `git reset --soft`
          одразу після перевірки, лишаючи гілку такою, якою вона була.
        * НЕ виконує `git switch` / `git restore` — тут git 2.19.1, цих
          команд не існує. Тільки `git checkout` і `git reset HEAD -- <файл>`.
        * НЕ чіпає глобальний `git config` і НЕ ставить `core.autocrlf`.
        * НЕ ставить `--no-verify`.
        * НЕ звертається до мережі, до `gh` і до GitHub.

.PARAMETER Node
    Роль цієї машини: `PK1` (керуючий) або `PK2` (виконавець).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install-node.ps1 -Node PK2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('PK1', 'PK2')]
    [string] $Node
)

$ErrorActionPreference = 'Stop'

# ── Корінь репозиторію ────────────────────────────────────────────────────────
if ($PSScriptRoot) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
} else {
    $repoRoot = (Get-Location).Path
}

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
    Write-Host ''
    Write-Host "  ПОМИЛКА: '$repoRoot' не схоже на корінь git-репозиторію (немає '.git')." -ForegroundColor Red
    exit 1
}

Set-Location -LiteralPath $repoRoot

$script:warnings = New-Object System.Collections.ArrayList
$script:stepNo = 0
$totalSteps = 6

function Write-Step {
    param([Parameter(Mandatory)][string] $Text)
    $script:stepNo++
    Write-Host ''
    Write-Host ("--- Крок {0}/{1}: {2}" -f $script:stepNo, $totalSteps, $Text) -ForegroundColor Yellow
}

function Write-Ok {
    param([Parameter(Mandatory)][string] $Text)
    Write-Host "    [OK]   $Text" -ForegroundColor Green
}

function Write-Warn {
    param([Parameter(Mandatory)][string] $Text)
    Write-Host "    [УВАГА] $Text" -ForegroundColor Magenta
    [void] $script:warnings.Add($Text)
}

function Write-Fail {
    param([Parameter(Mandatory)][string] $Text)
    Write-Host "    [ЗБІЙ] $Text" -ForegroundColor Red
}

# ── Виклик git з надійним кодом виходу ───────────────────────────────────────
# У PS 5.1 native-команда пише в stderr ErrorRecord'ами: під
# $ErrorActionPreference='Stop' це стає термінальною помилкою навіть при
# коді виходу 0. Тому на час виклику знижуємо preference до 'Continue',
# зливаємо потоки і дивимося РІВНО на $LASTEXITCODE.
#
# ⛔ Аргументи передаємо ОДНИМ масивом, а не через ValueFromRemainingArguments:
# PowerShell перехоплює `--` як власний признак кінця параметрів, а `-f`
# сприймає за ім'я параметра. Тобто `Invoke-Git add -f -- файл` втратив би
# і `-f`, і `--`. Тому виклик завжди виглядає так:
#     Invoke-Git @('add', '-f', '--', $file)
function Invoke-Git {
    param([Parameter(Mandatory)][string[]] $GitArgs)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & git @GitArgs 2>&1
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
Write-Host "=== install-node: прив'язка машини до ролі $Node ===" -ForegroundColor Cyan
Write-Host "    корінь:   $repoRoot"
if ($Node -eq 'PK1') {
    Write-Host '    роль:     PK1 — керуючий вузол (Opus). Зональних обмежень немає.'
} else {
    Write-Host '    роль:     PK2 — виконавець (Sonnet). Зональних обмежень немає.'
}

# ── Крок 1. .sync-local/NODE ─────────────────────────────────────────────────
Write-Step ".sync-local/NODE = $Node"

$syncLocal = Join-Path $repoRoot '.sync-local'
if (-not (Test-Path -LiteralPath $syncLocal -PathType Container)) {
    [void] (New-Item -ItemType Directory -Path $syncLocal)
    Write-Ok 'створено теку .sync-local/'
}

$nodeFile = Join-Path $syncLocal 'NODE'
# -NoNewline + ascii: у файлі має бути РІВНО 'PK1' або 'PK2', 3 байти.
# Будь-який зайвий байт (CRLF, BOM) ламає порівняння рядка в хуках bash.
Set-Content -LiteralPath $nodeFile -Value $Node -NoNewline -Encoding ascii

$nodeBytes = [System.IO.File]::ReadAllBytes($nodeFile)
if ($nodeBytes.Length -ne 3) {
    Write-Fail "у .sync-local/NODE $($nodeBytes.Length) байт замість 3 — перевір вручну."
    exit 1
}
Write-Ok "записано '$Node' ($($nodeBytes.Length) байт, без переносу рядка): $nodeFile"

# ── Крок 2. core.hooksPath ───────────────────────────────────────────────────
Write-Step 'git config core.hooksPath .githooks'

$cfg = Invoke-Git @('config', 'core.hooksPath', '.githooks')
if ($cfg.ExitCode -ne 0) {
    Write-Fail "git config повернув $($cfg.ExitCode):"
    Write-Host $cfg.Text
    exit 1
}

$check = Invoke-Git @('config', '--get', 'core.hooksPath')
if ($check.ExitCode -ne 0 -or ($check.Text.Trim()) -ne '.githooks') {
    Write-Fail "core.hooksPath не встановився: прочитано '$($check.Text.Trim())'."
    exit 1
}
Write-Ok "core.hooksPath = .githooks (локально, тільки цей репозиторій)"

# ── Крок 3. Наявність хуків ──────────────────────────────────────────────────
Write-Step 'наявність хуків .githooks/'

$hookNames = @('pre-commit', 'commit-msg', 'pre-push')
$hooksDir = Join-Path $repoRoot '.githooks'
$presentHooks = New-Object System.Collections.ArrayList

if (-not (Test-Path -LiteralPath $hooksDir -PathType Container)) {
    Write-Warn "теки .githooks/ НЕМАЄ ЗОВСІМ. Бар'єр зон не працює: коміт у будь-яку теку пройде."
} else {
    foreach ($hookName in $hookNames) {
        $hookPath = Join-Path $hooksDir $hookName
        if (Test-Path -LiteralPath $hookPath -PathType Leaf) {
            $size = (Get-Item -LiteralPath $hookPath).Length
            if ($size -eq 0) {
                Write-Warn ".githooks/$hookName існує, але ПОРОЖНІЙ (0 байт) — він нічого не перевіряє."
            } else {
                Write-Ok ".githooks/$hookName — є ($size байт)"
                [void] $presentHooks.Add($hookPath)
            }
        } else {
            Write-Warn ".githooks/$hookName ВІДСУТНІЙ. Відповідна перевірка не виконуватиметься."
        }
    }
}

# ── Крок 4. Переноси рядків у хуках (LF, не CRLF) ────────────────────────────
Write-Step 'переноси рядків у хуках (мусить бути LF)'

if ($presentHooks.Count -eq 0) {
    Write-Warn 'перевіряти нічого: жодного непорожнього хука не знайдено.'
} else {
    $crlfFound = New-Object System.Collections.ArrayList
    foreach ($hookPath in $presentHooks) {
        $bytes = [System.IO.File]::ReadAllBytes($hookPath)
        $hasCr = $false
        foreach ($b in $bytes) {
            if ($b -eq 0x0D) { $hasCr = $true; break }
        }
        $hookName = Split-Path -Leaf $hookPath
        if ($hasCr) {
            [void] $crlfFound.Add($hookName)
            Write-Fail ".githooks/$hookName містить байт 0x0D (CR) — це CRLF."
        } else {
            Write-Ok ".githooks/$hookName — чистий LF"
        }
    }

    if ($crlfFound.Count -gt 0) {
        Write-Host ''
        Write-Host "    ⛔ Хуки з CRLF: $($crlfFound -join ', ')" -ForegroundColor Red
        Write-Host '    bash упаде на них із помилкою виду:' -ForegroundColor Red
        Write-Host "        bash: .githooks/pre-commit: /bin/sh^M: bad interpreter: No such file or directory" -ForegroundColor Red
        Write-Host '    Це НЕ помилка вмісту хука — це переноси рядків Windows у shell-скрипті.' -ForegroundColor Red
        Write-Host ''
        Write-Host '    Як лікувати (одноразово, у корені репозиторію):' -ForegroundColor Yellow
        Write-Host '      1) додай/перевір у .gitattributes рядок:'
        Write-Host '             .githooks/** text eol=lf'
        Write-Host '      2) перевихопи файли з індексу (git 2.19.1, без git restore):'
        Write-Host '             git rm --cached -r .githooks'
        Write-Host '             git reset HEAD -- .githooks'
        Write-Host '             git checkout -- .githooks'
        Write-Host '      3) повтори запуск install-node.ps1'
        Write-Warn "хуки з CRLF ($($crlfFound -join ', ')) — бар'єр працюватиме непередбачувано."
    }
}

# ── Крок 5. ~/.claude/settings.json (merge, без затирання) ───────────────────
Write-Step '~/.claude/settings.json (злиття, наявні ключі не затираються)'

# PS 5.1 не має ConvertFrom-Json -AsHashtable, тому конвертуємо руками.
function ConvertTo-HashtableDeep {
    param($Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [bool] -or $Value -is [int] -or $Value -is [long] -or `
        $Value -is [double] -or $Value -is [decimal] -or $Value -is [datetime]) {
        return $Value
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $out = @{}
        foreach ($key in @($Value.Keys)) {
            $out[[string]$key] = ConvertTo-HashtableDeep $Value[$key]
        }
        return $out
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $list = New-Object System.Collections.ArrayList
        foreach ($item in $Value) {
            [void] $list.Add((ConvertTo-HashtableDeep $item))
        }
        # Кома перед виразом: інакше PowerShell розгорне масив у скаляр.
        return , $list.ToArray()
    }

    $props = $null
    if ($null -ne $Value.PSObject) { $props = $Value.PSObject.Properties }
    if ($null -ne $props) {
        $out = @{}
        foreach ($p in $props) {
            $out[$p.Name] = ConvertTo-HashtableDeep $p.Value
        }
        return $out
    }

    return $Value
}

# Об'єднання списку рядків без дублікатів, з ЗБЕРЕЖЕННЯМ наявних елементів.
function Merge-StringList {
    # ⛔ `-Obsolete` існує тому, що злиття, яке лише ДОДАЄ, робить розширення
    # прав неможливим: правило, записане минулим прогоном, лежить у файлі
    # користувача вічно. Саме так `Edit(docs/**)` пережив видачу прав на
    # `docs/build/**` і продовжував блокувати виконавця.
    param($Existing, [string[]] $Additional, [string[]] $Obsolete = @())

    $list = New-Object System.Collections.ArrayList
    foreach ($item in @($Existing)) {
        if ($null -ne $item -and ([string]$item).Trim() -ne '') {
            $s = [string]$item
            if ($Obsolete -contains $s) { continue }
            if (-not $list.Contains($s)) { [void] $list.Add($s) }
        }
    }
    foreach ($item in $Additional) {
        if (-not $list.Contains($item)) { [void] $list.Add($item) }
    }
    return , $list.ToArray()
}

$claudeDir = Join-Path $env:USERPROFILE '.claude'
$settingsPath = Join-Path $claudeDir 'settings.json'

if (-not (Test-Path -LiteralPath $claudeDir -PathType Container)) {
    [void] (New-Item -ItemType Directory -Path $claudeDir)
    Write-Ok "створено теку $claudeDir"
}

$settings = @{}
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    # Явний UTF-8: у файлі можуть бути неASCII-шляхи, а Get-Content у PS 5.1
    # без BOM читає як ANSI і калічить їх.
    $rawJson = (New-Object System.Text.UTF8Encoding($false)).GetString(
        [System.IO.File]::ReadAllBytes($settingsPath))
    $rawJson = $rawJson.TrimStart([char]0xFEFF)

    if ($rawJson.Trim() -eq '') {
        Write-Warn "$settingsPath порожній — вважаємо його за {}."
        $settings = @{}
    } else {
        try {
            $parsed = $rawJson | ConvertFrom-Json -ErrorAction Stop
        } catch {
            Write-Fail "$settingsPath — НЕ валідний JSON: $($_.Exception.Message)"
            Write-Host '    Скрипт свідомо НЕ перезаписує зламаний файл, щоб не втратити налаштування.' -ForegroundColor Red
            Write-Host '    Виправ JSON вручну (або перейменуй файл) і повтори запуск.' -ForegroundColor Red
            exit 1
        }
        $settings = ConvertTo-HashtableDeep $parsed
        if ($null -eq $settings -or -not ($settings -is [System.Collections.IDictionary])) {
            Write-Fail "$settingsPath містить не об'єкт JSON, а щось інше. Виправ вручну."
            exit 1
        }
    }

    # Резервна копія — до будь-якого запису.
    $backupPath = "$settingsPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $settingsPath -Destination $backupPath
    Write-Ok "резервна копія: $backupPath"
    Write-Ok ("прочитано наявних ключів: {0} ({1})" -f $settings.Keys.Count, (@($settings.Keys) -join ', '))
} else {
    Write-Ok "$settingsPath не існував — створюємо новий"
}

# Модель за роллю.
if ($Node -eq 'PK1') { $wantModel = 'opus' } else { $wantModel = 'sonnet' }

$oldModel = $null
if ($settings.ContainsKey('model')) { $oldModel = [string]$settings['model'] }
$settings['model'] = $wantModel
if ($null -ne $oldModel -and $oldModel -ne $wantModel) {
    Write-Ok "model: '$oldModel' -> '$wantModel'"
} else {
    Write-Ok "model = '$wantModel'"
}

# ⛔ ЗОНАЛЬНІ ЗАБОРОНИ ЗНЯТО ЦІЛКОМ (2026-09-08, пряме рішення людини).
#
# До цього рядка тут стояло ~90 рядків deny/allow-списків окремо для
# PK1 і PK2 — версія, що йшла за версією: спершу `docs/**` цілком,
# потім вузькі винятки для `docs/build/**`, потім те саме для
# `contracts/openapi.snapshot.json`. Кожен виняток розв'язував рівно
# одну зупинку і відкривав наступну — той самий клас затору в іншому
# місці. Людина попросила прибрати механізм цілком, а не латати його
# втретє.
#
# Обидва вузли тепер без обмежень інструмента на шляхи: PK2 пише де
# потрібно для роботи (код, тести, docs, contracts, scripts), PK1 не
# обмежений формально, хоч і не пише код за роллю.
#
# ⛔ Це НЕ стосується шести гейтів CI (build, test, honesty-guard,
# server, client, a11y) і заборони прямого пушу в main
# (.githooks/pre-push) — вони лишаються критерієм приймання коду,
# а не зональним бар'єром, і жодна настанова їх не знімає.
$denyList = @()
$allowList = @()

# Правила МИНУЛИХ прогонів, які треба фізично ПРИБРАТИ з файла
# користувача — інакше зняття зони тут нічого не змінить: інструмент
# і далі відмовлятиме за правилом, записаним попереднім прогоном.
$obsoleteDeny = @(
    'Edit(src/**)', 'Write(src/**)',
    'Edit(tests/**)', 'Write(tests/**)',
    'Edit(tools/**)', 'Write(tools/**)',
    'Edit(docs/**)', 'Write(docs/**)',
    'Edit(docs/build/**)', 'Write(docs/build/**)',
    'Edit(docs/sync/**)', 'Write(docs/sync/**)',
    'Edit(docs/tz/**)', 'Write(docs/tz/**)',
    'Edit(docs/architecture/**)', 'Write(docs/architecture/**)',
    'Edit(docs/reference/**)', 'Write(docs/reference/**)',
    'Edit(docs/CHECKSUMS.txt)', 'Write(docs/CHECKSUMS.txt)',
    'Edit(CLAUDE.md)', 'Write(CLAUDE.md)',
    'Edit(.githooks/**)', 'Write(.githooks/**)',
    'Edit(.github/**)', 'Write(.github/**)',
    'Edit(.claude/**)', 'Write(.claude/**)',
    'Edit(scripts/**)', 'Write(scripts/**)',
    'Bash(gh pr merge:*)', 'Bash(gh pr review:*)'
)

if (-not $settings.ContainsKey('permissions') -or -not ($settings['permissions'] -is [System.Collections.IDictionary])) {
    $settings['permissions'] = @{}
}
$perm = $settings['permissions']

$existingDeny = $null
if ($perm.ContainsKey('deny')) { $existingDeny = $perm['deny'] }
$denyBefore = @(@($existingDeny) | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
$perm['deny'] = Merge-StringList -Existing $existingDeny -Additional $denyList -Obsolete $obsoleteDeny
Write-Ok ("permissions.deny: {0} правил" -f @($perm['deny']).Count)
foreach ($rule in @($perm['deny'])) { Write-Host "             deny  $rule" -ForegroundColor DarkGray }

$removed = @($denyBefore | Where-Object { @($perm['deny']) -notcontains $_ })
foreach ($rule in $removed) { Write-Host "             ЗНЯТО deny  $rule" -ForegroundColor Yellow }

$allowBefore = @()
if ($perm.ContainsKey('allow')) {
    $allowBefore = @(@($perm['allow']) | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
}
if ($allowList.Count -gt 0) {
    $perm['allow'] = Merge-StringList -Existing $perm['allow'] -Additional $allowList
    Write-Ok ("permissions.allow: {0} правил" -f @($perm['allow']).Count)
    foreach ($rule in @($perm['allow'])) { Write-Host "             allow $rule" -ForegroundColor DarkGray }
}

# ⛔ Права Claude Code читаються ОДИН РАЗ на старті сесії. Отже цей скрипт,
# запущений усередині вже живої сесії, міняє файл на диску — і не міняє
# нічого в тому процесі, який його запустив. Без цього попередження зміна
# прав виглядає як «права видано, а інструмент відмовляє», і діагностика
# йде не туди: саме це й сталося 2026-09-08 на вузлі PK2.
$rulesChanged = ($removed.Count -gt 0) -or
    (@($perm['deny']).Count -ne $denyBefore.Count) -or
    (($allowList.Count -gt 0) -and (@($perm['allow']).Count -ne $allowBefore.Count))
if ($rulesChanged) {
    Write-Host ''
    Write-Host '  ⛔ ПРАВИЛА ПРАВ ЗМІНИЛИСЯ. Перезапусти сесію Claude Code.' -ForegroundColor Yellow
    Write-Host '     Права читаються на старті сесії; цей процес їх не перечитає.' -ForegroundColor Yellow
    Write-Host '     Без перезапуску інструмент відмовлятиме попри новий дозвіл.' -ForegroundColor Yellow
}

$json = $settings | ConvertTo-Json -Depth 10
# UTF-8 БЕЗ BOM: JSON.parse у Node спотикається на BOM, а settings.json
# читає саме Claude Code. Тому не Set-Content -Encoding UTF8 (він додає BOM),
# а явний UTF8Encoding($false).
[System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Ok "записано $settingsPath (UTF-8 без BOM)"

# ── Крок 6. Дим-тест: коміт БУДЬ-ДЕ мусить ПРОЙТИ (зонального бар'єра нема) ──
# 2026-09-08: цей крок раніше очікував ВІДМОВУ (бар'єр зон працює). Зональний
# бар'єр знято прямим рішенням людини, тож тепер тест очікує протилежне —
# ПІДТВЕРДЖУЄ відсутність бар'єра, а не мовчки лишає застарілу перевірку,
# яка з часом почала б рапортувати "FAIL" на кожному прогоні.
Write-Step "дим-тест: пробний коміт має ПРОЙТИ (зональний бар'єр знято)"

$probeRel = 'docs/__probe.tmp'
$probeFull = Join-Path $repoRoot ($probeRel -replace '/', '\')
$probeDir = Split-Path -Parent $probeFull

# Передумова A: індекс мусить бути ЧИСТИЙ. Інакше `git commit` затягне в
# коміт чужі застейджені зміни — цього ми робити не маємо права.
$staged = Invoke-Git @('diff', '--cached', '--name-only')
if ($staged.ExitCode -ne 0) {
    Write-Fail "не вдалося прочитати індекс (git diff --cached повернув $($staged.ExitCode)):"
    Write-Host $staged.Text
    exit 1
}
$stagedFiles = @($staged.Lines | Where-Object { $_.Trim() -ne '' })
if ($stagedFiles.Count -gt 0) {
    Write-Fail "в індексі вже є $($stagedFiles.Count) файл(ів) — дим-тест НЕ запускається:"
    foreach ($f in $stagedFiles) { Write-Host "        $f" -ForegroundColor Red }
    Write-Host '    Пробний коміт затягнув би їх із собою. Прибери їх з індексу' -ForegroundColor Red
    Write-Host '    (`git reset HEAD -- <файл>`; тут git 2.19.1, `git restore` немає) і повтори.' -ForegroundColor Red
    exit 1
}

# Передумова B: без user.name/user.email `git commit` упаде САМ, і ми
# зарахували б це за успішну роботу бар'єра. Хибний PASS гірший за збій.
$uname = Invoke-Git @('config', '--get', 'user.name')
$uemail = Invoke-Git @('config', '--get', 'user.email')
if ($uname.ExitCode -ne 0 -or $uname.Text.Trim() -eq '' -or $uemail.ExitCode -ne 0 -or $uemail.Text.Trim() -eq '') {
    Write-Fail 'не задані git user.name / user.email.'
    Write-Host '    Пробний коміт упав би через це, а не через хук — тест був би брехливий.' -ForegroundColor Red
    Write-Host '    Задай їх і повтори:' -ForegroundColor Yellow
    Write-Host '        git config user.name  "..."'
    Write-Host '        git config user.email "..."'
    exit 1
}

if (-not (Test-Path -LiteralPath $probeDir -PathType Container)) {
    [void] (New-Item -ItemType Directory -Path $probeDir -Force)
}

$barrierOk = $false
$commitLeaked = $false

try {
    Set-Content -LiteralPath $probeFull -Value "probe $Node $(Get-Date -Format o)" -Encoding ascii
    Write-Host "    створено пробний файл: $probeRel" -ForegroundColor DarkGray

    # -f: щоб .gitignore не з'їв файл тихо і не зробив тест безпредметним.
    $add = Invoke-Git @('add', '-f', '--', $probeRel)
    if ($add.ExitCode -ne 0) {
        Write-Fail "git add -- $probeRel повернув $($add.ExitCode):"
        Write-Host $add.Text
        exit 1
    }

    # Переконуємось, що файл СПРАВДІ в індексі: інакше `git commit` не мав би
    # що комітити, упав би сам, і ми зарахували б хибний PASS.
    $stagedNow = Invoke-Git @('diff', '--cached', '--name-only')
    $isStaged = $false
    foreach ($line in $stagedNow.Lines) {
        if ($line.Trim() -eq $probeRel) { $isStaged = $true; break }
    }
    if (-not $isStaged) {
        Write-Fail "'$probeRel' не потрапив в індекс — дим-тест неможливий."
        Write-Host $stagedNow.Text
        exit 1
    }
    Write-Host "    у індексі: $probeRel" -ForegroundColor DarkGray

    $msg = "[$Node][CHORE] probe"
    Write-Host "    пробуємо: git commit -m ""$msg""  (ОЧІКУЄМО УСПІХ)" -ForegroundColor DarkGray
    $commit = Invoke-Git @('commit', '-m', $msg)

    Write-Host ''
    Write-Host '    --- вивід git commit ---' -ForegroundColor DarkGray
    if ($commit.Text.Trim() -ne '') {
        foreach ($line in $commit.Lines) { Write-Host "    $line" -ForegroundColor DarkGray }
    } else {
        Write-Host '    (порожній вивід)' -ForegroundColor DarkGray
    }
    Write-Host "    --- код виходу: $($commit.ExitCode) ---" -ForegroundColor DarkGray
    Write-Host ''

    if ($commit.ExitCode -eq 0) {
        $barrierOk = $true
        $commitLeaked = $true
        Write-Host '    ############################################################' -ForegroundColor Green
        Write-Host "    #  PASS: коміт пройшов без бар'єра, як і задумано          #" -ForegroundColor Green
        Write-Host '    ############################################################' -ForegroundColor Green
    } else {
        Write-Host '    ############################################################' -ForegroundColor Red
        Write-Host "    #  FAIL: щось і досі блокує коміт — див. вивід вище        #" -ForegroundColor Red
        Write-Host '    ############################################################' -ForegroundColor Red
    }
} finally {
    # Прибирання виконується завжди — і на PASS, і на FAIL, і на винятку.
    # Тепер PASS теж лишає коміт (він мав пройти) — відкручуємо його так само,
    # це лише пробний файл, не робота, яку варто лишати в історії.
    if ($commitLeaked) {
        Write-Host ''
        Write-Host '    відкручуємо пробний коміт...' -ForegroundColor Yellow
        $undo = Invoke-Git @('reset', '--soft', 'HEAD~1')
        if ($undo.ExitCode -ne 0) {
            Write-Fail "git reset --soft HEAD~1 повернув $($undo.ExitCode):"
            Write-Host $undo.Text
            Write-Host "    ⛔ ВІДКРУТИ КОМІТ ВРУЧНУ: git reset --soft HEAD~1" -ForegroundColor Red
        } else {
            Write-Host '    коміт відкручено (зміни лишились в індексі)' -ForegroundColor Yellow
        }
    }

    # git 2.19.1: знімаємо з індексу через `git reset HEAD --`, бо
    # `git restore --staged` у цій версії не існує.
    $unstage = Invoke-Git @('reset', 'HEAD', '--', $probeRel)
    if ($unstage.ExitCode -ne 0) {
        Write-Warn "git reset HEAD -- $probeRel повернув $($unstage.ExitCode). Перевір індекс вручну: git status"
    } else {
        Write-Host "    знято з індексу: $probeRel" -ForegroundColor DarkGray
    }

    if (Test-Path -LiteralPath $probeFull) {
        Remove-Item -LiteralPath $probeFull -Force
        Write-Host "    видалено пробний файл: $probeRel" -ForegroundColor DarkGray
    }

    $leftovers = Invoke-Git @('status', '--porcelain', '--', $probeRel)
    if ($leftovers.ExitCode -eq 0 -and $leftovers.Text.Trim() -ne '') {
        Write-Warn "після прибирання git status ще бачить '$probeRel': $($leftovers.Text.Trim())"
    }
}

if (-not $barrierOk) {
    Write-Host ''
    Write-Host '=== КРИТИЧНИЙ ЗБІЙ УСТАНОВКИ ===' -ForegroundColor Red
    Write-Host '  Пробний коміт НЕ пройшов, хоч зональний бар''єр знято — щось інше' -ForegroundColor Red
    Write-Host '  блокує запис (не .githooks/pre-commit, він тепер `exit 0` завжди).' -ForegroundColor Red
    Write-Host ''
    Write-Host '  Що перевірити:' -ForegroundColor Yellow
    Write-Host '    1) git config --get core.hooksPath  -> мусить бути .githooks'
    Write-Host '    2) .githooks/pre-commit             -> існує, `exit 0`, не 0 байт'
    Write-Host '    3) переноси рядків у хуках          -> LF, не CRLF (див. Крок 4)'
    Write-Host '    4) .githooks/commit-msg             -> так само `exit 0`'
    Write-Host ''
    exit 1
}

# ── Підсумок ─────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== ПІДСУМОК УСТАНОВКИ ===' -ForegroundColor Cyan
Write-Host "  роль вузла .......... $Node"
Write-Host "  модель .............. $wantModel"
Write-Host "  .sync-local/NODE .... $nodeFile"
Write-Host "  core.hooksPath ...... .githooks"
Write-Host "  налаштування ........ $settingsPath"
Write-Host "  заборон (deny) ...... $(@($perm['deny']).Count) (застарілі зональні прибрані, якщо були)"
if ($barrierOk) {
    Write-Host "  дим-тест ............ PASS (пробний коміт у $probeRel пройшов, зональних заборон нема)" -ForegroundColor Green
}

if ($script:warnings.Count -gt 0) {
    Write-Host ''
    Write-Host "  ПОПЕРЕДЖЕННЯ ($($script:warnings.Count)):" -ForegroundColor Magenta
    foreach ($w in $script:warnings) { Write-Host "    * $w" -ForegroundColor Magenta }
    Write-Host ''
    Write-Host '  Установка завершилась, але з попередженнями — прочитай їх.' -ForegroundColor Magenta
} else {
    Write-Host ''
    Write-Host '  Попереджень немає. Вузол готовий.' -ForegroundColor Green
}

Write-Host ''
Write-Host '  Наступні кроки:' -ForegroundColor Yellow
Write-Host '    * мітки GitHub (один раз на репозиторій, з будь-якої машини):'
Write-Host '    * автоматичний цикл (Планувальник завдань Windows):'
Write-Host ''

exit 0
