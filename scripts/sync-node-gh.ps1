<#
.SYNOPSIS
    Автоматичний цикл вузла: підтягнути стан, подивитись у чергу GitHub і
    запустити Claude Code ЛИШЕ якщо в черзі щось є.

.DESCRIPTION
    ПРИЗНАЧЕННЯ
        Драйвер циклу без нагляду. Запускається Планувальником завдань
        Windows кожні N хвилин на кожній із двох машин. Головна його робота —
        НЕ запускати Claude. Порожня черга означає вихід з кодом 0 і нуль
        витрачених токенів; модель піднімається тільки тоді, коли для цього
        вузла справді є issue або PR.

    ХТО ЗАПУСКАЄ
        Планувальник завдань Windows (Task Scheduler), від імені користувача,
        з увімкненим «Запускати, лише коли користувач увійшов у систему»:
            powershell.exe -NoProfile -ExecutionPolicy Bypass ^
                -File "D:\Own project\ECR\project\BabychPV\ECR\scripts\sync-node-gh.ps1"
        Людина може запустити руками, зокрема з `-Force`, щоб підняти цикл
        попри порожню чергу.

    ЩО ЗМІНЮЄ
        * `.sync-local/pk1-<yyyyMMdd>.log` (або `pk2-...`) — журнал, дописується;
        * `.sync-local/pk1.lock` (або `pk2.lock`) — на час роботи Claude;
        * робоче дерево — через `git fetch` / `git pull` і через сам Claude,
          який працює у власній зоні прав.

    ЧОГО СВІДОМО НЕ РОБИТЬ
        * НЕ запускає Claude на порожній черзі — це головний запобіжник витрат.
        * НЕ виконує `git push --force` — ніколи, за жодних умов.
        * НЕ передає `--no-verify` і не обходить git-хуки.
        * НЕ вживає `git switch` / `git restore` (git 2.19.1 їх не має).
        * НЕ створює і НЕ закриває issue та PR власноруч — це робить цикл
          усередині Claude.
        * НЕ передає `--dangerously-skip-permissions`: права беруться з
          `~/.claude/settings.json` і `.claude/settings.json` репозиторію.
          Див. коментар біля $UseDangerousSkipPermissions.
        * НЕ мержить і НЕ розв'язує конфлікти: побачив конфлікт — гучно
          завершується, бо кликати модель на конфліктне дерево гірше, ніж стояти.

.PARAMETER Force
    Запустити цикл навіть із порожньою чергою (діагностика, ручний прогін).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\sync-node-gh.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\sync-node-gh.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

# ── Налаштування ─────────────────────────────────────────────────────────────
$Repo = 'BabychPV/ECR'

# Скільки хвилин lock вважається живим. Старіший — залишок від аварійно
# перерваного прогону, його прибираємо.
$LockStaleMinutes = 60

# Стеля тривалості одного прогону Claude. МУСИТЬ бути менша за
# $LockStaleMinutes: інакше прогін переживе власний lock, наступний запуск
# визнає lock застарілим і підніме ДРУГИЙ Claude на тому ж дереві.
$RunTimeoutMinutes = 55

# ⛔ Свідомо $false. `--dangerously-skip-permissions` знімає ВСІ перевірки
# прав, разом із заборонами зон із `permissions.deny`, тобто гасить бар'єр,
# на якому тримається розподіл ролей ПК-1 / ПК-2. Замість цього цикл
# спирається на `--permission-mode acceptEdits` плюс allow/deny-списки.
# Якщо цикл впирається у запит прав — правильна відповідь у розширенні
# allow-списку в `.claude/settings.json`, а не в знятті бар'єра.
$UseDangerousSkipPermissions = $false

# ── Корінь репозиторію ────────────────────────────────────────────────────────
if ($PSScriptRoot) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
} else {
    $repoRoot = (Get-Location).Path
}

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
    Write-Host "ПОМИЛКА: '$repoRoot' не схоже на корінь git-репозиторію (немає '.git')." -ForegroundColor Red
    exit 1
}

Set-Location -LiteralPath $repoRoot

# ── Роль вузла ───────────────────────────────────────────────────────────────
$syncLocal = Join-Path $repoRoot '.sync-local'
$nodeFile = Join-Path $syncLocal 'NODE'

if (-not (Test-Path -LiteralPath $nodeFile -PathType Leaf)) {
    Write-Host ''
    Write-Host "ПОМИЛКА: немає '$nodeFile' — машина не прив'язана до ролі вузла." -ForegroundColor Red
    Write-Host 'Спершу виконай (один раз на машині):' -ForegroundColor Yellow
    Write-Host '    powershell -ExecutionPolicy Bypass -File bootstrap-sync.ps1'
    Write-Host '    powershell -ExecutionPolicy Bypass -File scripts\install-node.ps1 -Node PK1   # або PK2'
    exit 1
}

$nodeRaw = (New-Object System.Text.UTF8Encoding($false)).GetString(
    [System.IO.File]::ReadAllBytes($nodeFile))
$node = $nodeRaw.Trim().Trim([char]0xFEFF).ToUpper()

if ($node -ne 'PK1' -and $node -ne 'PK2') {
    Write-Host ''
    Write-Host "ПОМИЛКА: '$nodeFile' містить '$node', а очікується рівно 'PK1' або 'PK2'." -ForegroundColor Red
    Write-Host 'Перезапиши файл через scripts\install-node.ps1 -Node PK1|PK2' -ForegroundColor Yellow
    exit 1
}

$nodeLower = $node.ToLower()

# ── Журнал ───────────────────────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $syncLocal -PathType Container)) {
    [void] (New-Item -ItemType Directory -Path $syncLocal)
}

$logPath = Join-Path $syncLocal ("{0}-{1}.log" -f $nodeLower, (Get-Date -Format 'yyyyMMdd'))
$script:logEncoding = New-Object System.Text.UTF8Encoding($false)

function Write-Log {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Message,
        [ValidateSet('INFO', 'WARN', 'ERROR', 'OK')][string] $Level = 'INFO'
    )

    $line = '{0} [{1}] [{2}] {3}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $node, $Level, $Message

    # Журнал важливіший за красу виводу, але падати через зайнятий файл
    # не будемо: пишемо в stdout завжди, у файл — з кількома спробами.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $script:logEncoding)
            break
        } catch {
            if ($attempt -eq 3) {
                Write-Host "  (не вдалося дописати в журнал '$logPath': $($_.Exception.Message))" -ForegroundColor Magenta
            } else {
                Start-Sleep -Milliseconds 200
            }
        }
    }

    if ($Level -eq 'ERROR') {
        Write-Host $line -ForegroundColor Red
    } elseif ($Level -eq 'WARN') {
        Write-Host $line -ForegroundColor Magenta
    } elseif ($Level -eq 'OK') {
        Write-Host $line -ForegroundColor Green
    } else {
        Write-Host $line
    }
}

function Write-LogBlock {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text, [string] $Prefix = '    | ')
    if ($null -eq $Text -or $Text.Trim() -eq '') { return }
    foreach ($line in ($Text -split "`r?`n")) {
        Write-Log ($Prefix + $line)
    }
}

# ── Виклик зовнішніх команд ──────────────────────────────────────────────────
# У PS 5.1 stderr native-команди приходить ErrorRecord'ами і під 'Stop' стає
# термінальною помилкою навіть при коді виходу 0. Аргументи передаємо ОДНИМ
# масивом: PowerShell перехопив би `--` і будь-який `-x` як власний параметр.
function Invoke-Exe {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $ExeArgs
    )

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & $FilePath @ExeArgs 2>&1
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

# ── Пошук git ────────────────────────────────────────────────────────────────
# Під Планувальником завдань PATH інший, ніж у консолі: на 'git' у PATH
# розраховувати не можна.
function Resolve-GitPath {
    try {
        $cmd = Get-Command -Name 'git' -CommandType Application -ErrorAction Stop
        if ($cmd -is [array]) { $cmd = $cmd[0] }
        if ($cmd -and $cmd.Source) { return $cmd.Source }
    } catch {
        # У PATH немає — не помилка, йдемо далі.
    }

    $fallbacks = @(
        'C:\Program Files\Git\cmd\git.exe',
        'C:\Program Files (x86)\Git\cmd\git.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe')
    )
    foreach ($candidate in $fallbacks) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    return $null
}

$script:gitPath = Resolve-GitPath
if ($null -eq $script:gitPath) {
    Write-Log 'git не знайдено ні в PATH, ні за шляхом C:\Program Files\Git\cmd\git.exe' -Level 'ERROR'
    exit 1
}

function Invoke-Git {
    param([Parameter(Mandatory)][string[]] $GitArgs)
    return Invoke-Exe -FilePath $script:gitPath -ExeArgs $GitArgs
}

# ── Пошук gh ─────────────────────────────────────────────────────────────────
# Під Планувальником завдань PATH інший, ніж у консолі, тому на PATH не
# розраховуємо: PATH -> відомий абсолютний шлях -> зрозуміла відмова.
function Resolve-GhPath {
    try {
        $cmd = Get-Command -Name 'gh' -CommandType Application -ErrorAction Stop
        if ($cmd -is [array]) { $cmd = $cmd[0] }
        if ($cmd -and $cmd.Source) { return $cmd.Source }
    } catch {
        # У PATH немає — не помилка, йдемо далі.
    }

    $fallbacks = @(
        'C:\Program Files\GitHub CLI\gh.exe',
        'C:\Program Files (x86)\GitHub CLI\gh.exe'
    )
    foreach ($candidate in $fallbacks) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

# ── Пошук claude ─────────────────────────────────────────────────────────────
function Resolve-ClaudePath {
    try {
        $cmd = Get-Command -Name 'claude' -CommandType Application -ErrorAction Stop
        if ($cmd -is [array]) { $cmd = $cmd[0] }
        if ($cmd -and $cmd.Source) { return $cmd.Source }
    } catch {
        # У PATH немає — не помилка, йдемо далі.
    }

    $fallbacks = @(
        (Join-Path $env:APPDATA 'npm\claude.cmd'),
        (Join-Path $env:USERPROFILE '.local\bin\claude.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\claude\claude.exe'),
        (Join-Path $env:PROGRAMFILES 'nodejs\claude.cmd')
    )
    foreach ($candidate in $fallbacks) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    return $null
}

# ═════════════════════════════════════════════════════════════════════════════
Write-Log '──────────────────────────────────────────────────────────────'
Write-Log ("старт прогону: вузол $node, корінь '$repoRoot', Force=" + [bool]$Force)

# ── Крок 1. Захист від накладання прогонів ───────────────────────────────────
$lockPath = Join-Path $syncLocal ("{0}.lock" -f $nodeLower)

if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
    $lockAgeMin = ([datetime]::Now - (Get-Item -LiteralPath $lockPath).LastWriteTime).TotalMinutes

    if ($lockAgeMin -lt $LockStaleMinutes) {
        Write-Log ("lock '$lockPath' живий ({0:N1} хв, стеля {1} хв) — попередній прогін ще працює, виходжу без запуску" -f `
            $lockAgeMin, $LockStaleMinutes) -Level 'WARN'
        exit 0
    }

    Write-Log ("lock '$lockPath' застарілий ({0:N1} хв > {1} хв) — залишок аварійного прогону, прибираю" -f `
        $lockAgeMin, $LockStaleMinutes) -Level 'WARN'
    try {
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction Stop
    } catch {
        Write-Log "не вдалося прибрати застарілий lock: $($_.Exception.Message)" -Level 'ERROR'
        exit 1
    }
}

# ── Крок 2. Синхронізація з origin ───────────────────────────────────────────
Write-Log 'git fetch origin --quiet'
$fetch = Invoke-Git @('fetch', 'origin', '--quiet')
if ($fetch.ExitCode -ne 0) {
    # Мережа буває. Це попередження, а не привід стояти: локальна черга
    # могла лишитись з попереднього прогону.
    Write-Log "git fetch повернув $($fetch.ExitCode) — працюю на наявному локальному стані" -Level 'WARN'
    Write-LogBlock $fetch.Text
} else {
    Write-Log 'fetch: ок' -Level 'OK'
}

Write-Log 'git pull --quiet'
$pull = Invoke-Git @('pull', '--quiet')
if ($pull.ExitCode -ne 0) {
    Write-Log "git pull повернув $($pull.ExitCode)" -Level 'WARN'
    Write-LogBlock $pull.Text
} else {
    Write-Log 'pull: ок' -Level 'OK'
}

# Конфлікт — інша річ, ніж збій мережі. Кликати модель на дерево з
# незакритим мержем не можна: вона побачить чужі маркери як свій код.
$unmerged = Invoke-Git @('ls-files', '--unmerged')
$mergeHead = Join-Path $repoRoot '.git\MERGE_HEAD'
if (($unmerged.ExitCode -eq 0 -and $unmerged.Text.Trim() -ne '') -or (Test-Path -LiteralPath $mergeHead)) {
    Write-Log 'у дереві НЕЗАВЕРШЕНИЙ МЕРЖ або конфліктні файли — цикл не запускається' -Level 'ERROR'
    Write-LogBlock $unmerged.Text
    Write-Log 'розв''яжи конфлікт руками (git 2.19.1: git checkout --ours|--theirs -- <файл>, git add, git commit)' -Level 'ERROR'
    exit 1
}

$branch = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
if ($branch.ExitCode -eq 0) {
    Write-Log ("поточна гілка: " + $branch.Text.Trim())
}

# ── Крок 3. Чи є взагалі робота ──────────────────────────────────────────────
$ghPath = Resolve-GhPath
if ($null -eq $ghPath) {
    Write-Log 'gh CLI не знайдено ні в PATH, ні за шляхом C:\Program Files\GitHub CLI\gh.exe' -Level 'ERROR'
    Write-Log 'без gh неможливо прочитати чергу; запускати модель наосліп не буду' -Level 'ERROR'
    exit 1
}
Write-Log "gh: $ghPath"

function Get-OpenIssueCount {
    param([Parameter(Mandatory)][string] $Label)

    $res = Invoke-Exe -FilePath $ghPath -ExeArgs @(
        'issue', 'list',
        '--repo', $Repo,
        '--state', 'open',
        '--label', $Label,
        '--limit', '100',
        '--json', 'number,title')

    $out = New-Object psobject
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Ok'      -Value $false
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Total'   -Value 0
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Numbers' -Value @()
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Text'    -Value $res.Text

    if ($res.ExitCode -ne 0) { return $out }

    if ($res.Text.Trim() -eq '') {
        $out.Ok = $true
        return $out
    }

    try {
        $parsed = $res.Text | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $out.Text = "не вдалося розібрати відповідь gh як JSON: $($_.Exception.Message)"
        return $out
    }

    $items = @($parsed)
    $nums = @()
    foreach ($item in $items) {
        if ($item -and $item.number) { $nums += ('#' + [string]$item.number) }
    }
    $out.Ok = $true
    $out.Total = $items.Count
    $out.Numbers = $nums
    return $out
}

function Get-PrsAwaitingReview {
    $res = Invoke-Exe -FilePath $ghPath -ExeArgs @(
        'pr', 'list',
        '--repo', $Repo,
        '--state', 'open',
        '--limit', '100',
        '--json', 'number,title,isDraft,labels,reviewDecision')

    $out = New-Object psobject
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Ok'      -Value $false
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Total'   -Value 0
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Numbers' -Value @()
    Add-Member -InputObject $out -MemberType NoteProperty -Name 'Text'    -Value $res.Text

    if ($res.ExitCode -ne 0) { return $out }

    if ($res.Text.Trim() -eq '') {
        $out.Ok = $true
        return $out
    }

    try {
        $parsed = $res.Text | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $out.Text = "не вдалося розібрати відповідь gh як JSON: $($_.Exception.Message)"
        return $out
    }

    $nums = @()
    foreach ($pr in @($parsed)) {
        if (-not $pr) { continue }

        # Чернетка — ще не робота для рев'ю.
        $isDraft = $false
        if ($null -ne $pr.isDraft) { $isDraft = [bool]$pr.isDraft }
        if ($isDraft) { continue }

        $names = @()
        foreach ($lb in @($pr.labels)) {
            if ($lb -and $lb.name) { $names += [string]$lb.name }
        }

        $decision = ''
        if ($null -ne $pr.reviewDecision) { $decision = [string]$pr.reviewDecision }

        $wanted = $false
        if ($names -contains 'status:review') { $wanted = $true }
        if ($names -contains 'needs:pk1') { $wanted = $true }
        if ($decision -eq 'REVIEW_REQUIRED') { $wanted = $true }

        if ($wanted) { $nums += ('#' + [string]$pr.number) }
    }

    $out.Ok = $true
    $out.Total = $nums.Count
    $out.Numbers = $nums
    return $out
}

$queueTotal = 0
$queueReadFailed = $false

if ($node -eq 'PK1') {
    $issues = Get-OpenIssueCount -Label 'needs:pk1'
    if (-not $issues.Ok) {
        Write-Log "не вдалося прочитати issue з міткою needs:pk1" -Level 'ERROR'
        Write-LogBlock $issues.Text
        $queueReadFailed = $true
    } else {
        Write-Log ("open issues [needs:pk1]: {0} {1}" -f $issues.Total, (@($issues.Numbers) -join ' '))
        $queueTotal += $issues.Total
    }

    $prs = Get-PrsAwaitingReview
    if (-not $prs.Ok) {
        Write-Log 'не вдалося прочитати перелік PR' -Level 'ERROR'
        Write-LogBlock $prs.Text
        $queueReadFailed = $true
    } else {
        Write-Log ("open PR на рев'ю [status:review | needs:pk1 | REVIEW_REQUIRED]: {0} {1}" -f `
            $prs.Total, (@($prs.Numbers) -join ' '))
        $queueTotal += $prs.Total
    }
} else {
    $issues = Get-OpenIssueCount -Label 'needs:pk2'
    if (-not $issues.Ok) {
        Write-Log 'не вдалося прочитати issue з міткою needs:pk2' -Level 'ERROR'
        Write-LogBlock $issues.Text
        $queueReadFailed = $true
    } else {
        Write-Log ("open issues [needs:pk2]: {0} {1}" -f $issues.Total, (@($issues.Numbers) -join ' '))
        $queueTotal += $issues.Total
    }
}

if ($queueReadFailed -and -not $Force) {
    Write-Log 'чергу прочитати не вдалося — модель наосліп не запускаю (для примусу: -Force)' -Level 'ERROR'
    exit 1
}
if ($queueReadFailed -and $Force) {
    Write-Log 'чергу прочитати не вдалося, але заданий -Force — продовжую' -Level 'WARN'
}

# ── Крок 4. Головний запобіжник витрат ───────────────────────────────────────
if ($queueTotal -eq 0 -and -not $Force) {
    Write-Log 'черга порожня, вихід' -Level 'OK'
    exit 0
}

if ($queueTotal -eq 0 -and $Force) {
    Write-Log 'черга порожня, але заданий -Force — запускаю цикл' -Level 'WARN'
} else {
    Write-Log ("у черзі позицій: $queueTotal — запускаю цикл") -Level 'OK'
}

# ── Крок 5. Запуск Claude Code ───────────────────────────────────────────────
$claudePath = Resolve-ClaudePath
if ($null -eq $claudePath) {
    Write-Log 'claude CLI не знайдено ні в PATH, ні за типовими шляхами встановлення' -Level 'ERROR'
    Write-Log 'перевір: where claude   /   npm ls -g @anthropic-ai/claude-code' -Level 'ERROR'
    exit 1
}
Write-Log "claude: $claudePath"

$outFile = Join-Path $syncLocal ("{0}.stdout.tmp" -f $nodeLower)
$errFile = Join-Path $syncLocal ("{0}.stderr.tmp" -f $nodeLower)
$inFile = Join-Path $syncLocal ("{0}.stdin.tmp" -f $nodeLower)

$claudeExit = -1

try {
    # lock ставимо ВПРИТУЛ до запуску: усе, що вище, дешеве і швидке.
    Set-Content -LiteralPath $lockPath -Value ("{0} pid={1} {2}" -f $node, $PID, (Get-Date -Format o)) -Encoding ascii
    Write-Log "lock поставлено: $lockPath"

    # Порожній stdin: під Планувальником консолі немає, і якщо процес
    # спробує щось прочитати з введення — він зависне до таймаута.
    Set-Content -LiteralPath $inFile -Value '' -NoNewline -Encoding ascii

    $argString = '-p "/cycle" --permission-mode acceptEdits'
    if ($UseDangerousSkipPermissions) {
        $argString += ' --dangerously-skip-permissions'
    }

    # CreateProcess не вміє запускати .cmd/.bat напряму, а перенаправлення
    # потоків вимикає ShellExecute. Тому пакетний файл кличемо через cmd /c.
    $ext = [System.IO.Path]::GetExtension($claudePath).ToLower()
    if ($ext -eq '.cmd' -or $ext -eq '.bat') {
        $exeToRun = $env:ComSpec
        $exeArgs = '/c ""' + $claudePath + '" ' + $argString + '"'
    } else {
        $exeToRun = $claudePath
        $exeArgs = $argString
    }

    Write-Log "запуск: $exeToRun $exeArgs"
    $startedAt = Get-Date

    $proc = Start-Process -FilePath $exeToRun `
        -ArgumentList $exeArgs `
        -WorkingDirectory $repoRoot `
        -NoNewWindow `
        -PassThru `
        -RedirectStandardOutput $outFile `
        -RedirectStandardError $errFile `
        -RedirectStandardInput $inFile

    $timeoutMs = $RunTimeoutMinutes * 60 * 1000
    if (-not $proc.WaitForExit($timeoutMs)) {
        Write-Log "прогін перевищив стелю $RunTimeoutMinutes хв — знімаю процес (pid $($proc.Id))" -Level 'ERROR'
        try {
            $proc.Kill()
            $proc.WaitForExit(30000) | Out-Null
        } catch {
            Write-Log "не вдалося зняти процес: $($_.Exception.Message)" -Level 'ERROR'
        }
        $claudeExit = -2
    } else {
        $claudeExit = $proc.ExitCode
    }

    $elapsed = ([datetime]::Now - $startedAt).TotalMinutes

    # Вивід моделі — у той самий журнал, щоб розбір прогону був в одному файлі.
    Write-Log '─── stdout claude ───'
    if (Test-Path -LiteralPath $outFile) {
        $stdoutText = ''
        try {
            $stdoutText = (New-Object System.Text.UTF8Encoding($false)).GetString(
                [System.IO.File]::ReadAllBytes($outFile))
        } catch {
            Write-Log "не вдалося прочитати '$outFile': $($_.Exception.Message)" -Level 'WARN'
        }
        if ($stdoutText.Trim() -eq '') {
            Write-Log '    (порожньо)'
        } else {
            Write-LogBlock $stdoutText
        }
    } else {
        Write-Log '    (файл stdout не створено)' -Level 'WARN'
    }

    Write-Log '─── stderr claude ───'
    if (Test-Path -LiteralPath $errFile) {
        $stderrText = ''
        try {
            $stderrText = (New-Object System.Text.UTF8Encoding($false)).GetString(
                [System.IO.File]::ReadAllBytes($errFile))
        } catch {
            Write-Log "не вдалося прочитати '$errFile': $($_.Exception.Message)" -Level 'WARN'
        }
        if ($stderrText.Trim() -eq '') {
            Write-Log '    (порожньо)'
        } else {
            Write-LogBlock $stderrText
        }
    } else {
        Write-Log '    (файл stderr не створено)' -Level 'WARN'
    }

    Write-Log '─────────────────────'

    if ($claudeExit -eq 0) {
        Write-Log ("цикл завершився успішно: код 0, тривалість {0:N1} хв" -f $elapsed) -Level 'OK'
    } elseif ($claudeExit -eq -2) {
        Write-Log ("цикл ЗНЯТО за таймаутом: тривалість {0:N1} хв" -f $elapsed) -Level 'ERROR'
    } else {
        Write-Log ("цикл завершився з кодом $claudeExit, тривалість {0:N1} хв" -f $elapsed) -Level 'ERROR'
    }
} finally {
    # lock знімається завжди: і на успіху, і на збої, і на Ctrl+C, і на
    # винятку. Забутий lock тихо зупиняє вузол на цілу годину.
    if (Test-Path -LiteralPath $lockPath) {
        try {
            Remove-Item -LiteralPath $lockPath -Force -ErrorAction Stop
            Write-Log "lock знято: $lockPath"
        } catch {
            Write-Log "НЕ ВДАЛОСЯ зняти lock '$lockPath': $($_.Exception.Message)" -Level 'ERROR'
            Write-Log 'прибери файл руками, інакше наступний прогін мовчки пропуститься' -Level 'ERROR'
        }
    }

    foreach ($tmp in @($outFile, $errFile, $inFile)) {
        if (Test-Path -LiteralPath $tmp) {
            try { Remove-Item -LiteralPath $tmp -Force -ErrorAction Stop } catch { }
        }
    }
}

Write-Log ("кінець прогону: вузол $node, код claude $claudeExit")
Write-Log '──────────────────────────────────────────────────────────────'

if ($claudeExit -eq 0) { exit 0 }
exit 1
