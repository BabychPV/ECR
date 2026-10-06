<#
.SYNOPSIS
    Набір для першого тестування: ОДИН zip, з яким тестувальник розгортає ECR
    на чистому сервері без клону репозиторію і без .NET SDK.

.DESCRIPTION
    A1-08 (приймальний прохід A1, 05.10.2026): артефакт CI `ecr-msi` містить
    лише `Ecr.msi` і `Ecr.msi.sha256`. Без ідемпотентного `migration.sql` і
    `deploy-ecr.ps1` перший користувач базу розгорнути не може: deploy-ecr.ps1
    без пакованої схеми шукає `src/Ecr.Infrastructure` і `dotnet ef`.

    Скрипт складає той самий «пакований» розклад, що й payload майстра
    (`build-installer.ps1`, Q-219), лише zip-ом, а не self-contained .exe:

      ECR-first-test-<Version>/
        Ecr.msi, Ecr.msi.sha256         ← з артефакту ecr-msi, хеш звірено
        deploy-ecr.ps1                  ← sql\ і migration.sql ПОРУЧ — пакований режим:
        migration.sql                      dotnet ef і SDK на сервері не потрібні
        sql\*.sql                          (deploy-ecr.ps1, `$isPackagedSchema`)
        verify-msi.ps1                  ← -IntegrityOnly: I1 (SHA-256) і I2 (підпис)
        QUICKSTART-FIRST-TEST.md        ← від нуля до першого входу + відомі обмеження
        docs\…                          ← install-guide, TESTER-HANDOVER, Гайд, адмін-доки,
                                           реліз-нотатки
        BUNDLE-INFO.txt, SHA256SUMS.txt ← версія/коміт і суми кожного файлу набору
      ECR-first-test-<Version>.zip.sha256 (поруч із zip, формат sha256sum)

    Перевірки — до запису zip, кожна з кодом [BUNDLE-Exx] (ASCII — його видно й
    у консолі з кодовою сторінкою ANSI):
      E01 у -MsiDir не рівно один .msi        E06 migration.sql без останньої міграції
      E02 немає <msi>.sha256                      з дерева (застарілий)
      E03 хеш MSI не збігається / не sha256sum E07 бракує SQL-скрипта, який кличе deploy
      E04 migration.sql немає й dotnet-ef теж  E08 бракує документа набору
      E05 migration.sql не ідемпотентний       E09 самоперевірка готового zip не пройшла
    Будь-яка з них — виняток і ненульовий код виходу; zip не лишається.

.PARAMETER MsiDir
    Тека з розпакованим артефактом `ecr-msi` (пошук рекурсивний: WiX кладе .msi
    у підтеку культури). Має бути рівно один `*.msi` і поруч `<ім'я>.msi.sha256`.

.PARAMETER Version
    Мітка версії в іменах (`ECR-first-test-<Version>.zip`); у CI — та сама, що
    передавалась build-msi.ps1.

.PARAMETER MigrationSql
    Готовий `dotnet ef migrations script --idempotent`. Не задано — скрипт
    генерує його сам (потрібні .NET SDK і dotnet-ef — на машині збірки, не на сервері).

.PARAMETER Commit
    Хеш коміту для BUNDLE-INFO.txt. Не задано — `git rev-parse HEAD`, якщо git є.

.PARAMETER OutDir
    Куди класти zip і .zip.sha256. Типово `artifacts\delivery` у корені репозиторію.

.EXAMPLE
    # Машина збірки: артефакт ecr-msi розпаковано в .\ecr-msi
    .\tools\make-delivery-bundle.ps1 -MsiDir .\ecr-msi -Version 0.0.512

.NOTES
    Працює в Windows PowerShell 5.1 і PowerShell 7 (у т. ч. Linux): zip пишеться
    через System.IO.Compression з іменами записів через `/`, а не Compress-Archive
    (у 5.1 він пише `\`, і такі записи інші розпаковувачі не розуміють).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiDir,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.\-]*$')] [string] $Version,
    [string] $MigrationSql,
    [string] $Commit,
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root     = Split-Path -Parent $PSScriptRoot
$sqlSrc   = Join-Path (Join-Path (Join-Path (Join-Path $root 'src') 'Ecr.Infrastructure') 'Persistence') 'Sql'
$migSrc   = Join-Path (Join-Path (Join-Path (Join-Path $root 'src') 'Ecr.Infrastructure') 'Persistence') 'Migrations'
$infraProj = Join-Path (Join-Path $root 'src') 'Ecr.Infrastructure'
$deploy   = Join-Path $PSScriptRoot 'deploy-ecr.ps1'
$verify   = Join-Path $PSScriptRoot 'verify-msi.ps1'
if (-not $OutDir) { $OutDir = Join-Path (Join-Path $root 'artifacts') 'delivery' }

function Stop-Bundle([string] $Code, [string] $Message) {
    throw "[BUNDLE-$Code] $Message"
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Документи набору: шлях у репозиторії → шлях у наборі. Порядок — порядок читання.
$docs = [ordered]@{
    'docs/delivery/QUICKSTART-FIRST-TEST.md' = 'QUICKSTART-FIRST-TEST.md'
    'docs/build/11-install-guide.md'         = 'docs/install-guide.md'
    'docs/build/TESTER-HANDOVER.md'          = 'docs/TESTER-HANDOVER.md'
    'docs/build/TESTER-GUIDE.md'             = 'docs/TESTER-GUIDE.md'
    'docs/build/TESTER-SCENARIOS-2026-10-01.md' = 'docs/TESTER-SCENARIOS.md'
    'docs/build/MSI-SIGNING-OPTIONS.md'      = 'docs/MSI-SIGNING-OPTIONS.md'
    'docs/admin/admin-guide.md'              = 'docs/admin/admin-guide.md'
    'docs/admin/https-certificate.md'        = 'docs/admin/https-certificate.md'
    'docs/admin/operations-runbook.md'       = 'docs/admin/operations-runbook.md'
    'docs/admin/notifications-runbook.md'    = 'docs/admin/notifications-runbook.md'
}

Write-Host "== Набір для першого тестування $Version ==" -ForegroundColor Cyan

# ── 1. MSI і його хеш ─────────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $MsiDir -PathType Container)) {
    Stop-Bundle 'E01' "Теки -MsiDir немає: $MsiDir (розпакуйте туди артефакт CI ecr-msi)."
}
$msis = @(Get-ChildItem -LiteralPath $MsiDir -Filter '*.msi' -Recurse -File)
if ($msis.Count -ne 1) {
    Stop-Bundle 'E01' ("У $MsiDir має бути рівно один .msi, знайдено $($msis.Count)" +
        $(if ($msis.Count) { ': ' + (($msis | ForEach-Object FullName) -join ', ') } else { '' }) + '.')
}
$msi = $msis[0]
$shaFile = "$($msi.FullName).sha256"
if (-not (Test-Path -LiteralPath $shaFile -PathType Leaf)) {
    Stop-Bundle 'E02' "Немає $($msi.Name).sha256 поруч із MSI ($shaFile): без еталона verify-msi.ps1 -IntegrityOnly на сервері впаде."
}
$shaLine = @(Get-Content -LiteralPath $shaFile | Where-Object { $_.Trim() }) | Select-Object -First 1
if (-not $shaLine -or $shaLine.Trim() -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
    Stop-Bundle 'E03' "$($msi.Name).sha256 не у форматі sha256sum ('<64 hex>  $($msi.Name)'): '$shaLine'."
}
$expected = $Matches[1].ToLowerInvariant()
$named    = $Matches[2].Trim()
if ($named -ne $msi.Name) {
    Stop-Bundle 'E03' "$($msi.Name).sha256 описує інший файл: '$named'."
}
$actual = Get-Sha256 $msi.FullName
if ($actual -ne $expected) {
    Stop-Bundle 'E03' "SHA-256 MSI не збігається з $($msi.Name).sha256: файл $actual, еталон $expected. Артефакт пошкоджено або підмінено."
}
Write-Host "MSI: $($msi.Name) ($('{0:N1}' -f ($msi.Length / 1MB)) MB), SHA-256 $actual — збігається з еталоном." -ForegroundColor Green

# ── 2. migration.sql: ідемпотентний і не застарілий ──────────────────────
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ecr-bundle-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    if (-not $MigrationSql) {
        $haveEf = [bool] (Get-Command dotnet-ef -ErrorAction SilentlyContinue)
        if (-not $haveEf -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            $haveEf = [bool] (& dotnet tool list --global 2>$null | Select-String -SimpleMatch 'dotnet-ef')
        }
        if (-not $haveEf) {
            Stop-Bundle 'E04' ("Не задано -MigrationSql, а dotnet-ef недоступний. Або передайте готовий " +
                "'dotnet ef migrations script --idempotent', або встановіть: dotnet tool install --global dotnet-ef " +
                "--version <версія Microsoft.EntityFrameworkCore.Design з Directory.Packages.props>.")
        }
        $MigrationSql = Join-Path $work 'migration.sql'
        Write-Host "Генерую migration.sql (dotnet ef migrations script --idempotent)…"
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & dotnet ef migrations script --idempotent --project $infraProj --startup-project $infraProj --output $MigrationSql
        }
        finally { $ErrorActionPreference = $previous }
        if ($LASTEXITCODE -or -not (Test-Path -LiteralPath $MigrationSql)) {
            Stop-Bundle 'E04' ("dotnet ef migrations script завершився з кодом $LASTEXITCODE і не залишив migration.sql " +
                "(у свіжому чекауті спершу dotnet restore Ecr.sln — інакше NETSDK1004).")
        }
    }
    elseif (-not (Test-Path -LiteralPath $MigrationSql -PathType Leaf)) {
        Stop-Bundle 'E04' "Файлу -MigrationSql немає: $MigrationSql."
    }

    $migText = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $MigrationSql).Path)
    # --idempotent обгортає КОЖНУ міграцію в цю перевірку; без неї повторний прогін на
    # наявній базі падав би на першому ж CREATE TABLE.
    $guard = "IF NOT EXISTS\s*\(\s*SELECT \* FROM (\[\w+\]\.)?\[__EFMigrationsHistory\]\s+WHERE \[MigrationId\] = N'"
    if ($migText -notmatch $guard) {
        Stop-Bundle 'E05' ("migration.sql не ідемпотентний (немає перевірок IF NOT EXISTS … [__EFMigrationsHistory]): " +
            "потрібен 'dotnet ef migrations script --idempotent'.")
    }
    $migrationIds = @(Get-ChildItem -LiteralPath $migSrc -Filter '*.cs' -File |
        Where-Object { $_.Name -match '^\d{14}_[^.]+\.cs$' } |
        ForEach-Object { $_.BaseName } | Sort-Object)
    if ($migrationIds.Count -eq 0) {
        Stop-Bundle 'E06' "У $migSrc не знайдено жодної міграції — скрипт запущено не з дерева репозиторію?"
    }
    $latest = $migrationIds[-1]
    if (-not $migText.Contains("N'$latest'")) {
        Stop-Bundle 'E06' ("migration.sql застарілий: у ньому немає останньої міграції дерева '$latest'. " +
            "Згенеруйте його з того самого коміту, що й MSI.")
    }
    Write-Host "migration.sql: ідемпотентний, останній крок $latest ($($migrationIds.Count) міграцій у дереві)." -ForegroundColor Green

    # ── 3. SQL-скрипти: усе, що кличе deploy-ecr.ps1, має бути в наборі ──
    $sqlFiles = @(Get-ChildItem -LiteralPath $sqlSrc -Filter '*.sql' -File | Sort-Object Name)
    $deployText = [System.IO.File]::ReadAllText($deploy)
    $called = @([regex]::Matches($deployText, "'(\d\d-[A-Za-z0-9-]+\.sql)'") | ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique)
    if ($called.Count -lt 10) {
        Stop-Bundle 'E07' "У deploy-ecr.ps1 знайдено лише $($called.Count) імен SQL-скриптів — перелік кроку 2 змінив форму, перевірка дивиться не туди."
    }
    $missing = @($called | Where-Object { $name = $_; -not ($sqlFiles | Where-Object Name -eq $name) })
    if ($missing.Count) {
        Stop-Bundle 'E07' "deploy-ecr.ps1 виконує SQL-скрипти, яких немає в $sqlSrc`: $($missing -join ', ')."
    }

    # ── 4. Документи ────────────────────────────────────────────────────
    $missingDocs = @($docs.Keys | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) })
    $notes = @(Get-ChildItem -LiteralPath (Join-Path (Join-Path $root 'docs') 'release-notes') -Filter '*.md' -File -ErrorAction SilentlyContinue |
        Sort-Object Name)
    if ($missingDocs.Count) { Stop-Bundle 'E08' "Бракує документів набору: $($missingDocs -join ', ')." }
    if ($notes.Count -eq 0) { Stop-Bundle 'E08' 'Бракує реліз-нотаток: docs/release-notes/*.md порожній.' }

    # ── 5. Склад набору: шлях у zip → джерело ───────────────────────────
    $top = "ECR-first-test-$Version"
    $entries = [ordered]@{}
    $entries['Ecr.msi']        = $msi.FullName
    $entries['Ecr.msi.sha256'] = $null   # переписується нижче під ім'я Ecr.msi
    $entries['deploy-ecr.ps1'] = $deploy
    $entries['verify-msi.ps1'] = $verify
    $entries['migration.sql']  = (Resolve-Path -LiteralPath $MigrationSql).Path
    foreach ($f in $sqlFiles) { $entries["sql/$($f.Name)"] = $f.FullName }
    foreach ($k in $docs.Keys) { $entries[$docs[$k]] = Join-Path $root $k }
    foreach ($n in $notes) { $entries["docs/release-notes/$($n.Name)"] = $n.FullName }

    # Ecr.msi.sha256 у наборі завжди описує 'Ecr.msi' — під цим іменем MSI і лежить
    # (deploy-ecr.ps1 -MsiPath .\Ecr.msi, verify-msi.ps1 шукає <MsiPath>.sha256).
    $shaOut = Join-Path $work 'Ecr.msi.sha256'
    [System.IO.File]::WriteAllText($shaOut, "$actual  Ecr.msi`n", [System.Text.Encoding]::ASCII)
    $entries['Ecr.msi.sha256'] = $shaOut

    if (-not $Commit -and (Get-Command git -ErrorAction SilentlyContinue)) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { $Commit = (& git -C $root rev-parse HEAD 2>$null | Select-Object -First 1) } finally { $ErrorActionPreference = $previous }
    }
    if (-not $Commit) { $Commit = 'unknown' }

    $info = Join-Path $work 'BUNDLE-INFO.txt'
    $infoLines = @(
        "bundle=$top"
        "version=$Version"
        "commit=$Commit"
        "created_utc=$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
        "msi_sha256=$actual"
        "latest_migration=$latest"
        "sql_scripts=$($sqlFiles.Count)"
        'start=QUICKSTART-FIRST-TEST.md'
    )
    [System.IO.File]::WriteAllText($info, (($infoLines -join "`n") + "`n"), [System.Text.Encoding]::ASCII)
    $entries['BUNDLE-INFO.txt'] = $info

    # SHA256SUMS.txt — по всіх файлах набору, крім себе; перевірка на сервері:
    # Get-FileHash, або `sha256sum -c SHA256SUMS.txt` у теці набору.
    $sums = foreach ($name in ($entries.Keys | Sort-Object)) { "$(Get-Sha256 $entries[$name])  $name" }
    $sumsFile = Join-Path $work 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllText($sumsFile, (($sums -join "`n") + "`n"), [System.Text.Encoding]::ASCII)
    $entries['SHA256SUMS.txt'] = $sumsFile

    # ── 6. Zip ───────────────────────────────────────────────────────────
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $OutDir = (Resolve-Path -LiteralPath $OutDir).Path
    $zipPath = Join-Path $OutDir "$top.zip"
    $zipSha  = "$zipPath.sha256"
    foreach ($p in @($zipPath, $zipSha)) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $ok = $false
    try {
        $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $entries.Keys) {
                [void] [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip, $entries[$name], "$top/$name", [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $zip.Dispose() }

        # ── 7. Самоперевірка: той самий склад і ті самі суми після запису ──
        $check = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $names = @($check.Entries | ForEach-Object FullName)
            $want  = @($entries.Keys | ForEach-Object { "$top/$_" })
            $diff  = @(Compare-Object ($want | Sort-Object) ($names | Sort-Object))
            if ($diff.Count) { Stop-Bundle 'E09' "Склад zip не збігається з планом: $(($diff | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join ', ')." }
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                foreach ($e in $check.Entries) {
                    $rel = $e.FullName.Substring($top.Length + 1)
                    $s = $e.Open()
                    try { $h = -join ($sha.ComputeHash($s) | ForEach-Object { $_.ToString('x2') }) } finally { $s.Dispose() }
                    if ($h -ne (Get-Sha256 $entries[$rel])) { Stop-Bundle 'E09' "Запис $rel у zip пошкоджено (SHA-256 не збігається з джерелом)." }
                }
            }
            finally { $sha.Dispose() }
        }
        finally { $check.Dispose() }
        $ok = $true
    }
    finally {
        if (-not $ok -and (Test-Path -LiteralPath $zipPath)) { Remove-Item -LiteralPath $zipPath -Force }
    }

    $zipHash = Get-Sha256 $zipPath
    [System.IO.File]::WriteAllText($zipSha, "$zipHash  $top.zip`n", [System.Text.Encoding]::ASCII)

    Write-Host ""
    Write-Host "Готово." -ForegroundColor Green
    Write-Host "Набір:    $zipPath ($('{0:N1}' -f ((Get-Item -LiteralPath $zipPath).Length / 1MB)) MB, $($entries.Count) файлів)"
    Write-Host "SHA-256:  $zipHash ($top.zip.sha256)"
    Write-Host "Початок:  $top/QUICKSTART-FIRST-TEST.md"
    Write-Output "BUNDLE_ZIP=$zipPath"
    Write-Output "BUNDLE_SHA256=$zipHash"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
