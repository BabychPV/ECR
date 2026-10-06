<#
.SYNOPSIS
    Статуси задач docs/build/WORK-QUEUE.md за git: «зведено» (коміт у
    dev/integration) і «випущено» (коміт у main). Звіт (Markdown) — у
    стандартний вивід або у -OutFile поза репозиторієм; у репозиторій
    скрипт НІЧОГО не пише.

.DESCRIPTION
    Рядок статусу в черзі оновлює людина чи сесія руками, і він відстає:
    задачу влито, а в черзі досі `todo`/`doing`. Git же знає напевно, чи є
    коміт із ID задачі в гілці. Скрипт читає ID із черги і шукає їх у темі
    й тілі комітів:

      випущено   — є коміт із ID, досяжний з -Release (типово origin/main);
      зведено    — коміти з ID є лише в -Integration поза -Release
                   (типово origin/dev/integration, тобто `main..dev/integration`);
      —          — комітів із ID немає в жодній гілці.

    «випущено (+N зведено)» — задача вже в main, а в dev/integration після
    того є ще N її комітів (хвіст, виправлення).

    Коли комітів із ID немає, а рядок черги сам називає хеш (`done 68252c60`
    у колонці «Статус» чи «Гілка / хеш»), статус береться за місцем цього
    хешу: «випущено (за хешем із черги)» / «зведено (за хешем із черги)».

    Які ID беруться з черги:
      задачі — перша клітинка рядка таблиці: AN-nn, AU-nn, CL-nn, HU-nn, DB-nn
               (з необов'язковою латинською літерою: AN-36b);
      коди знахідок — L<n>-<nn> і T<n>-<nn>, згадані будь-де в рядку задачі
               (L10-04, L10-04b, T6-01).

    Межі збігу — без букв і цифр по обидва боки, з урахуванням регістру:
    AN-10 не збігається з AN-100 чи AN-10b, L10-04 — з L10-04b, а назви
    гілок у нижньому регістрі (`an37-l708`) не рахуються. Запис на кшталт
    «AN-25/26/27» дає лише AN-25. Merge-коміти не рахуються (--no-merges):
    їхній текст — назва PR чи гілки, а не зроблена робота.

    ⚠ Неглибокий клон (shallow) бачить не всю історію: скрипт пише про це
    в звіт; повна картина — після `git fetch --unshallow`.

    Джерело — лише локальний git: ні `gh`, ні GitHub API, ні мережі скрипт
    не чіпає і `git fetch` сам не робить. Немає git або потрібної гілки —
    відмова з поясненням (код виходу 1), а не звіт із вигаданими статусами.
    Вердикти CI скрипт не показує.

    Сумісність: Windows PowerShell 5.1 і PowerShell 7+.

.PARAMETER RepoRoot
    Корінь репозиторію. За замовчуванням — батьківська тека tools/.

.PARAMETER Queue
    Файл черги відносно RepoRoot (або абсолютний шлях).

.PARAMETER Integration
    Гілка зведення: її коміти поза -Release дають «зведено».

.PARAMETER Release
    Гілка випуску: її коміти дають «випущено».

.PARAMETER IgnorePaths
    Облікові файли (шляхи від кореня, через /): коміт, що змінює ЛИШЕ їх,
    не рахується — це запис статусу, а не робота над задачею.

.PARAMETER OutFile
    Файл звіту (Markdown, UTF-8 без BOM); відносний шлях — від поточної
    теки. Шлях усередині -RepoRoot відхиляється: скрипт не пише в
    репозиторій. Без параметра звіт іде в стандартний вивід.

.EXAMPLE
    git fetch origin main dev/integration
    pwsh -File tools/status-from-git.ps1 -OutFile $env:TEMP\status-from-git.md

.EXAMPLE
    powershell -File tools\status-from-git.ps1 | Select-String 'AN-36'
#>
[CmdletBinding()]
param(
    [string] $RepoRoot,
    [string] $Queue = 'docs/build/WORK-QUEUE.md',
    [string] $Integration = 'origin/dev/integration',
    [string] $Release = 'origin/main',
    [string] $OutFile,
    [string[]] $IgnorePaths = @('docs/build/WORK-QUEUE.md', 'docs/build/TODO-REMAINING.md', 'docs/build/QUESTIONS-BUSINESS.md')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Відмова — одним рядком у stderr і кодом 1, без звіту: краще жодного звіту,
# ніж звіт, що виглядає справжнім, а побудований на неповних даних.
function Stop-Status([string] $message) {
    [Console]::Error.WriteLine("status-from-git: $message")
    exit 1
}

if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) {
    Stop-Status 'git недоступний (немає в PATH) — статусів не буде.'
}

# ⚠ `$PSScriptRoot` порожній під час обчислення умовчань у `param()` — тому тут.
if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    Stop-Status "немає теки репозиторію: $RepoRoot"
}
$RepoRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepoRoot).ProviderPath).TrimEnd('\', '/')

$queuePath = if ([IO.Path]::IsPathRooted($Queue)) { $Queue } else { Join-Path $RepoRoot $Queue }
if (-not (Test-Path -LiteralPath $queuePath -PathType Leaf)) {
    Stop-Status "немає файлу черги: $queuePath"
}

$outPath = $null
if ($OutFile) {
    # Відносний шлях — від поточної теки PowerShell, а не від теки процесу .NET.
    $outPath = [IO.Path]::GetFullPath($PSCmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile))
    $inside = $outPath.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
              $outPath.StartsWith($RepoRoot + '/', [StringComparison]::OrdinalIgnoreCase)
    if ($inside) {
        Stop-Status "-OutFile $outPath лежить у репозиторії $RepoRoot; скрипт у репозиторій не пише — вкажіть шлях поза ним (напр. `$env:TEMP)."
    }
}

# ⚠ Windows PowerShell 5.1 читає вивід git у кодовій сторінці консолі —
# кирилиця в темах комітів зіпсувалась би. Повертається у finally.
$previousEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# Виклик git: stdout — рядками, stderr — окремо, код виходу — як є.
# ⚠ Windows PowerShell 5.1 за перенаправленого stderr загортає кожен його рядок
# в ErrorRecord, і з $ErrorActionPreference='Stop' будь-яке попередження git
# (`warning: refname … is ambiguous`) обірвало б скрипт. Тому тут — Continue і
# 2>&1 з розбором за типом.
function Invoke-GitRaw([string[]] $arguments) {
    $ErrorActionPreference = 'Continue'
    $all = @(& git -C $RepoRoot -c i18n.logOutputEncoding=UTF-8 -c core.quotepath=false @arguments 2>&1)
    $code = $LASTEXITCODE
    return [pscustomobject]@{
        Code = $code
        Out = @($all | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
        Err = (@($all | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join ' ').Trim()
    }
}

function Invoke-Git([string[]] $arguments) {
    $r = Invoke-GitRaw $arguments
    if ($r.Code -ne 0) {
        Stop-Status "git $($arguments[0]) повернув $($r.Code): $($r.Err)"
    }
    return $r.Out
}

function Get-Tip([string] $ref) {
    $r = Invoke-GitRaw @('rev-parse', '--verify', '--quiet', "$ref^{commit}")
    if ($r.Code -ne 0 -or -not $r.Out.Count) {
        Stop-Status "гілки '$ref' немає в $RepoRoot (потрібен git fetch?) — статусів не буде."
    }
    return $r.Out[0].Trim()
}

# Один ID — латинські букви коду, дефіс, номер, необов'язкова літера суфікса.
# Межі — без букв і цифр будь-якої абетки (у т. ч. кирилиці) по обидва боки.
$idPattern = '(?<![\p{L}\p{Nd}])(?:AN|AU|CL|HU|DB|L\d+|T\d+)-\d+[a-z]?(?![\p{L}\p{Nd}])'
$findingPattern = '(?<![\p{L}\p{Nd}])[LT]\d+-\d+[a-z]?(?![\p{L}\p{Nd}])'

# ---- черга ----------------------------------------------------------------

function Read-Queue([string] $path) {
    $tasks = [ordered]@{}
    $findings = [ordered]@{}
    $section = ''
    $statusColumn = -1
    $hashColumn = -1
    foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
        if ($line -match '^##\s+(.+)$') {
            $section = $Matches[1].Trim()
            $statusColumn = -1
            $hashColumn = -1
            continue
        }
        if (-not $line.StartsWith('|')) { continue }
        $cells = @($line.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() })
        if ($cells[0] -match '^:?-{3,}') { continue }
        $first = ($cells[0] -replace '\*', '').Trim()
        if ($first -match '^(№|ID)$') {
            $statusColumn = [array]::IndexOf($cells, 'Статус')
            $hashColumn = [array]::IndexOf($cells, 'Гілка / хеш')
            continue
        }
        if ($first -notmatch '^((?:AN|AU|CL|HU|DB)-\d+[a-z]?)(?![\p{L}\p{Nd}])') { continue }
        $id = $Matches[1]
        if (-not $tasks.Contains($id)) {
            $status = if ($statusColumn -ge 0 -and $statusColumn -lt $cells.Count) { $cells[$statusColumn] } else { '' }
            $tasks[$id] = [pscustomobject]@{ Id = $id; Section = $section; QueueStatus = $status
                Hashes = [Collections.Generic.List[string]]::new() }
        }
        # Хеші, які черга сама називає (`done 68252c60`), — запасний доказ для
        # комітів, у тексті яких ID задачі немає.
        foreach ($column in @($statusColumn, $hashColumn)) {
            if ($column -lt 0 -or $column -ge $cells.Count) { continue }
            foreach ($m in [regex]::Matches($cells[$column], '(?<![0-9a-zA-Z])[0-9a-f]{7,40}(?![0-9a-zA-Z])')) {
                if (-not $tasks[$id].Hashes.Contains($m.Value)) { $tasks[$id].Hashes.Add($m.Value) }
            }
        }
        foreach ($m in [regex]::Matches($line, $findingPattern)) {
            if (-not $findings.Contains($m.Value)) {
                $findings[$m.Value] = [Collections.Generic.List[string]]::new()
            }
            if (-not $findings[$m.Value].Contains($id)) { $findings[$m.Value].Add($id) }
        }
    }
    return [pscustomobject]@{ Tasks = $tasks; Findings = $findings }
}

# ---- git ------------------------------------------------------------------

# ID → список комітів (новіші першими, як у git log).
function Read-Commits([string] $range) {
    $index = @{}
    # %x1e — перед комітом, %x1f — між полями: у тілі комітів їх не буває.
    # Після останнього поля --name-only дописує змінені файли.
    $raw = (Invoke-Git @('log', '--no-merges', '--name-only', '--format=%x1e%h%x1f%cs%x1f%s%x1f%b%x1f', $range)) -join "`n"
    foreach ($record in $raw.Split([char]0x1e)) {
        $fields = $record.Split([char]0x1f)
        if ($fields.Count -lt 5) { continue }
        $files = @($fields[4].Split("`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        # Коміт, що лише переписує облікові файли (черга, беклог), — запис про
        # статус, а не робота: інакше «[DOCS] WORK-QUEUE: статуси AN-*» робив би
        # «випущеними» всі перелічені задачі.
        if ($files.Count -and -not @($files | Where-Object { $IgnorePaths -notcontains $_ }).Count) { continue }
        $commit = [pscustomobject]@{ Hash = $fields[0]; Date = $fields[1]; Subject = $fields[2] }
        $seen = @{}
        foreach ($m in [regex]::Matches($fields[2] + "`n" + $fields[3], $idPattern)) {
            if ($seen.ContainsKey($m.Value)) { continue }
            $seen[$m.Value] = $true
            if (-not $index.ContainsKey($m.Value)) {
                $index[$m.Value] = [Collections.Generic.List[object]]::new()
            }
            $index[$m.Value].Add($commit)
        }
    }
    return $index
}

function Get-GitStatus([string] $id, [hashtable] $released, [hashtable] $integrated) {
    $r = @(if ($released.ContainsKey($id)) { $released[$id] })
    $i = @(if ($integrated.ContainsKey($id)) { $integrated[$id] })
    $status = if ($r.Count -and $i.Count) { "випущено (+$($i.Count) зведено)" }
              elseif ($r.Count) { 'випущено' }
              elseif ($i.Count) { 'зведено' }
              else { '—' }
    # Останній коміт: спершу зведений (новіший за випуск), інакше з main.
    $last = if ($i.Count) { $i[0] } elseif ($r.Count) { $r[0] } else { $null }
    return [pscustomobject]@{
        Status = $status
        Count = $r.Count + $i.Count
        Last = $last
    }
}

# Де лежить коміт, названий у черзі: 'випущено', 'зведено' або $null (немає / лише в lane).
function Get-HashPlace([string] $hash) {
    $r = Invoke-GitRaw @('rev-parse', '--verify', '--quiet', "$hash^{commit}")
    if ($r.Code -ne 0 -or -not $r.Out.Count) { return $null }
    $sha = $r.Out[0].Trim()
    if ((Invoke-GitRaw @('merge-base', '--is-ancestor', $sha, $releaseTip)).Code -eq 0) { return 'випущено' }
    if ((Invoke-GitRaw @('merge-base', '--is-ancestor', $sha, $integrationTip)).Code -eq 0) { return 'зведено' }
    return $null
}

# Статус лише за хешами з черги: найкращий із них (випущено > зведено).
function Get-HashStatus($task) {
    $places = @($task.Hashes | ForEach-Object { Get-HashPlace $_ })
    if ($places -contains 'випущено') { return 'випущено' }
    if ($places -contains 'зведено') { return 'зведено' }
    return $null
}

function Format-Cell([string] $text, [int] $max = 0) {
    $t = ($text -replace '\|', '\|').Trim()
    if ($max -gt 0 -and $t.Length -gt $max) { $t = $t.Substring(0, $max - 1) + '…' }
    return $t
}

function Format-Last($git) {
    if (-not $git.Last) { return '' }
    return "``$($git.Last.Hash)`` $($git.Last.Date) $(Format-Cell $git.Last.Subject 70)"
}

try {
    $integrationTip = Get-Tip $Integration
    $releaseTip = Get-Tip $Release
    $shallow = "$(Invoke-Git @('rev-parse', '--is-shallow-repository'))".Trim() -eq 'true'

    $queueData = Read-Queue $queuePath
    $released = Read-Commits $releaseTip
    $integrated = Read-Commits "$releaseTip..$integrationTip"

    # ⚠ Без [IO.Path]::GetRelativePath: його немає в .NET Framework (Windows PowerShell 5.1).
    $relQueue = $queuePath
    if ($relQueue.StartsWith($RepoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $relQueue = $relQueue.Substring($RepoRoot.Length).TrimStart('\', '/')
    }
    $relQueue = $relQueue -replace '\\', '/'
    $counts = [ordered]@{ 'випущено' = 0; 'зведено' = 0; '—' = 0 }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Статуси задач черги з git')
    $lines.Add('')
    $lines.Add("Згенеровано ``tools/status-from-git.ps1`` — не редагувати руками; черга ``$relQueue`` цим файлом не змінюється.")
    $lines.Add('')
    $lines.Add("- **випущено** — коміт з ID досяжний з ``$Release`` @ ``$($releaseTip.Substring(0, 8))``;")
    $lines.Add("- **зведено** — коміти з ID є лише в ``$Integration`` @ ``$($integrationTip.Substring(0, 8))`` поза ``$Release``;")
    $lines.Add('- **(за хешем із черги)** — комітів з ID немає, але хеш, названий у рядку черги (`Статус`, `Гілка / хеш`), лежить у відповідній гілці;')
    $lines.Add('- **—** — ні комітів з ID, ні хешів черги в цих гілках. Шукається тема й тіло комітів; merge-коміти й коміти, що змінюють лише облікові файли (' + (($IgnorePaths | ForEach-Object { "``$_``" }) -join ', ') + '), не враховуються.')
    if ($shallow) {
        $lines.Add('')
        $lines.Add('⚠ Клон неглибокий (shallow): частина історії не видна, «—» може бути хибним. Потрібен `git fetch --unshallow`.')
    }

    $taskRows = [Collections.Generic.List[string]]::new()
    foreach ($task in $queueData.Tasks.Values) {
        $git = Get-GitStatus $task.Id $released $integrated
        if ($git.Status -eq '—') {
            $byHash = Get-HashStatus $task
            if ($byHash) { $git.Status = "$byHash (за хешем із черги)" }
        }
        $counts[($git.Status -replace ' \(.*$', '')]++
        $taskRows.Add("| $($task.Id) | $($git.Status) | $($git.Count) | $(Format-Last $git) | $(Format-Cell $task.QueueStatus 60) | $(Format-Cell $task.Section 40) |")
    }

    $lines.Add('')
    $lines.Add("Задач у черзі: $($queueData.Tasks.Count) — випущено $($counts['випущено']), зведено $($counts['зведено']), без комітів $($counts['—']).")
    $lines.Add('')
    $lines.Add('## Задачі')
    $lines.Add('')
    $lines.Add('| ID | Статус з git | Комітів | Останній коміт | Статус у черзі | Розділ черги |')
    $lines.Add('|---|---|---|---|---|---|')
    foreach ($row in $taskRows) { $lines.Add($row) }

    $lines.Add('')
    $lines.Add('## Коди знахідок, згадані в черзі')
    $lines.Add('')
    $lines.Add('| Код | Задачі черги | Статус з git | Комітів | Останній коміт |')
    $lines.Add('|---|---|---|---|---|')
    foreach ($code in $queueData.Findings.Keys) {
        $git = Get-GitStatus $code $released $integrated
        $lines.Add("| $code | $($queueData.Findings[$code] -join ', ') | $($git.Status) | $($git.Count) | $(Format-Last $git) |")
    }

    $report = ($lines -join "`n") + "`n"
    if (-not $outPath) {
        # Один рядок у конвеєр: у консолі — текст, далі — Set-Content / Select-String.
        Write-Output $report
    }
    else {
        $dir = Split-Path -Parent $outPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir | Out-Null
        }
        [IO.File]::WriteAllText($outPath, $report, [Text.UTF8Encoding]::new($false))
        Write-Host "status-from-git: задач $($queueData.Tasks.Count) (випущено $($counts['випущено']), зведено $($counts['зведено']), без комітів $($counts['—'])), кодів знахідок $($queueData.Findings.Count) → $outPath"
    }
}
finally {
    [Console]::OutputEncoding = $previousEncoding
}
