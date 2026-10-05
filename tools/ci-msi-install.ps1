<#
.SYNOPSIS
    CI-джоба `msi-install (windows)`: РЕАЛЬНА установка MSI на ефемерному
    ранері windows-latest — служби, оновлення з попередньої MSI, WORKER_ENABLED,
    запис режиму Api функціями deploy-ecr.ps1 у справжній реєстр служби.
.DESCRIPTION
    Кроки:
      P. Попередня MSI — артефакт `ecr-msi` останнього прогону з гілки
         dev/integration або main (джоба `msi (windows)` була зелена, бо саме
         вона вивантажує артефакт). Немає — з цієї ж гілки (ранній прогін,
         позначається). Немає й там або версія не нижча за поточну — сценарії
         W3/W3b пропускаються з поясненням, не падають.
      V. tools/verify-msi.ps1 без -StaticOnly: S1–S5, 1, W1, W2, [W3, W3b], 5,
         15, 9, W5 — справжній msiexec, служби EcrApi/EcrWorker.
      D1. deploy-ecr.ps1 -WhatIf (типово і -DisableWorker): план передає
         WORKER_ENABLED=1/0 і записує Mode=Database/Executor=Worker або
         Executor=InProcess. SQL не потрібен: під -WhatIf скрипт sqlcmd не
         кличе (контракт скрипта; заглушка sqlcmd, якщо його нема, це й
         перевіряє — виклик дав би код 97).
      D2. Функції кроку 5 deploy-ecr.ps1 (вирізані з файлу парсером, як у
         DeployWorkerModeTests) проти СПРАВЖНЬОГО реєстру встановленої служби:
         EcrWorker є → Environment EcrApi має Mode=Database, Executor=Worker;
         після REINSTALL WORKER_ENABLED=0 служби нема → Executor=InProcess,
         Mode=Quartz; чужі записи Environment лишаються.
      D3. (довідково, не падає) Чи переживає Environment EcrApi оновлення
         попередньої MSI поточною без повторного deploy-ecr.ps1.
    ⚠ Чого тут НЕМАЄ: старту служб (SQL на ранері немає, SERVICE_ACCOUNT
    порожній → служби зареєстровані, але не стартують, §1.4); повного прогону
    deploy-ecr.ps1 без -WhatIf (потребує SQL Server і схеми); /health.
.NOTES
    ⛔ Встановлює і видаляє службу. Лише на ефемерному ранері CI
    (GITHUB_ACTIONS=true) — інакше відмова.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiDir,
    [Parameter(Mandatory)] [string] $LogDir,
    [string] $PreviousMsiPath,
    [switch] $DiscoverPrevious
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'ci-msi-install.ps1 встановлює і видаляє службу — лише на ефемерному ранері CI (GITHUB_ACTIONS=true). Руками — tools/verify-msi.ps1 на тестовій машині.'
}

$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogDir = (Resolve-Path $LogDir).Path

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string] $case, [string] $result) {
    $results.Add([pscustomobject]@{ Case = $case; Result = $result })
    Write-Host "  [$result] $case"
}
function Test-Case([string] $name, [scriptblock] $body) {
    try { & $body | Out-Null; Add-Result $name 'PASS' }
    catch { Add-Result $name "FAIL: $_" }
}

function Get-MsiVersion([string] $path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $db = $installer.OpenDatabase($path, 0)
        $view = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductVersion'")
        [void] $view.Execute()
        $record = $view.Fetch()
        $value = $record.StringData(1)
        [void] $view.Close()
        return [version] $value
    }
    finally {
        $view = $null; $db = $null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
}

function Invoke-Msi([string] $arguments) {
    $p = Start-Process msiexec -ArgumentList $arguments -Wait -PassThru -WorkingDirectory $LogDir
    if ($p.ExitCode -notin 0, 3010) { throw "msiexec $arguments → $($p.ExitCode)" }
}

# ── Поточна MSI ──────────────────────────────────────────────────────────
$msi = Get-ChildItem $MsiDir -Filter '*.msi' -Recurse | Select-Object -First 1
if (-not $msi) { throw "MSI не знайдено в $MsiDir (артефакт ecr-msi джоби msi (windows))" }
$msiPath = $msi.FullName
$currentVersion = Get-MsiVersion $msiPath
Write-Host "Поточна MSI: $msiPath, версія $currentVersion"

# ── P. Попередня MSI ─────────────────────────────────────────────────────
<#
    L10-10 (AUDIT-2026-10-03 §1J): вибір артефакту `ecr-msi` для оновлення.
    ⛔ Ім'я гілки — не доказ походження: PR з форку з гілкою `main` чи
    `dev/integration` дає прогін у ЦЬОМУ репозиторії з тим самим head_branch, і
    його MSI ставилася б на ранер як «попередній реліз». Тому беремо лише
    прогони, чий head-репозиторій — цей (`workflow_run.head_repository_id`
    дорівнює `GITHUB_REPOSITORY_ID`). Немає id — нічого не беремо (не вгадуємо).
    Порядок: найновіший dev/integration|main; інакше — найновіший тієї ж гілки.
#>
function Select-PreviousMsiArtifact {
    param([object[]] $Artifacts, [string] $RepositoryId, [string] $RunId, [string] $OwnBranch)
    if (-not $RepositoryId) { return $null }
    $all = @($Artifacts |
        Where-Object { $_ -and -not $_.expired -and $_.workflow_run -and
            "$($_.workflow_run.id)" -ne "$RunId" -and
            "$($_.workflow_run.head_repository_id)" -eq "$RepositoryId" } |
        Sort-Object created_at -Descending)
    $pick = $all | Where-Object { $_.workflow_run.head_branch -in 'dev/integration', 'main' } | Select-Object -First 1
    if (-not $pick -and $OwnBranch) {
        $pick = $all | Where-Object { $_.workflow_run.head_branch -eq $OwnBranch } | Select-Object -First 1
    }
    return $pick
}

function Find-PreviousMsi {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Write-Host '  gh на ранері немає — попередню MSI не шукаю.'; return $null }
    if (-not $env:GH_TOKEN) { Write-Host '  GH_TOKEN не задано — попередню MSI не шукаю.'; return $null }
    $repo = $env:GITHUB_REPOSITORY

    $raw = & gh api "repos/$repo/actions/artifacts?name=ecr-msi&per_page=100"
    if ($LASTEXITCODE) { Write-Host "  gh api artifacts → код $LASTEXITCODE — попередню MSI не шукаю."; return $null }
    $own = if ($env:GITHUB_HEAD_REF) { $env:GITHUB_HEAD_REF } else { $env:GITHUB_REF_NAME }
    $pick = Select-PreviousMsiArtifact -Artifacts (($raw -join "`n") | ConvertFrom-Json).artifacts `
        -RepositoryId $env:GITHUB_REPOSITORY_ID -RunId $env:GITHUB_RUN_ID -OwnBranch $own
    if (-not $pick) { Write-Host '  Артефакту ecr-msi з іншого прогону цього репозиторію немає (dev/integration, main, ця гілка) — W3/W3b пропущено.'; return $null }
    $label = if ($pick.workflow_run.head_branch -in 'dev/integration', 'main') { "гілка $($pick.workflow_run.head_branch)" }
        else { "⚠ НЕ реліз dev/integration/main: ранній прогін тієї самої гілки $own (dev/integration і main артефакту ecr-msi ще не мають)" }

    $runId = $pick.workflow_run.id
    # Поза $LogDir: той вивантажується при падінні, а MSI там зайва.
    $dest = Join-Path ([System.IO.Path]::GetTempPath()) "ecr-msi-previous-$runId"
    & gh run download $runId -R $repo -n ecr-msi -D $dest
    if ($LASTEXITCODE) { Write-Host "  gh run download $runId → код $LASTEXITCODE — W3/W3b пропущено."; return $null }
    $file = Get-ChildItem $dest -Filter '*.msi' -Recurse | Select-Object -First 1
    if (-not $file) { Write-Host "  В артефакті прогону $runId немає .msi — W3/W3b пропущено."; return $null }
    Write-Host "  Попередня MSI: прогін $runId ($label, $($pick.workflow_run.head_sha)), https://github.com/$repo/actions/runs/$runId"
    return $file.FullName
}

Write-Host '── P. Попередня MSI'
if (-not $PreviousMsiPath -and $DiscoverPrevious) { $PreviousMsiPath = Find-PreviousMsi }
if ($PreviousMsiPath) {
    $previousVersion = Get-MsiVersion $PreviousMsiPath
    Write-Host "  Версія попередньої: $previousVersion"
    if ($previousVersion -ge $currentVersion) {
        # MajorUpgrade блокує пониження, а однакова версія — не оновлення.
        Write-Host "  Попередня ($previousVersion) не нижча за поточну ($currentVersion) — це не оновлення; W3/W3b пропущено."
        $PreviousMsiPath = $null
    }
}
if (-not $PreviousMsiPath) { Add-Result 'P. Оновлення з попередньої MSI (W3/W3b, D3)' 'SKIP: попередньої MSI немає або вона не нижча — див. лог вище' }

# ── V. verify-msi.ps1 без -StaticOnly ───────────────────────────────────
Write-Host '── V. verify-msi.ps1 (повний режим)'
$verifyArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'verify-msi.ps1'), '-MsiPath', $msiPath, '-LogDir', $LogDir)
if ($PreviousMsiPath) { $verifyArgs += @('-PreviousMsiPath', $PreviousMsiPath) }
& pwsh @verifyArgs
Add-Result 'V. verify-msi.ps1 (S1–S5, 1, W1, W2, W3/W3b за наявності попередньої, 5, 15, 9, W5)' $(if ($LASTEXITCODE) { "FAIL: код $LASTEXITCODE (таблиця вище)" } else { 'PASS' })

# Після verify-msi продукт знято (9, W5). Якщо ні — прибрати, щоб D-кроки
# стартували з чистої машини.
if (Get-Service EcrApi -ErrorAction SilentlyContinue) { Invoke-Msi "/x `"$msiPath`" /qn /l*v cleanup-v.log" }

# ── D1. deploy-ecr.ps1 -WhatIf ──────────────────────────────────────────
Write-Host '── D1. deploy-ecr.ps1 -WhatIf'
$deploy = Join-Path $root 'tools\deploy-ecr.ps1'
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    $stub = Join-Path $LogDir 'sqlcmd-stub'
    New-Item -ItemType Directory -Force -Path $stub | Out-Null
    Set-Content -Path (Join-Path $stub 'sqlcmd.cmd') -Encoding ascii -Value "@echo sqlcmd stub: -WhatIf must not call sqlcmd 1>&2`r`n@exit /b 97"
    $env:PATH = "$stub;$env:PATH"
    Write-Host '  sqlcmd на ранері немає — заглушка з кодом 97 (лише щоб пройти передумову кроку 1; -WhatIf не має її викликати).'
}
$cert = New-SelfSignedCertificate -Subject 'CN=ecr-ci-dataprotection' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable
try {
    $cases = @(
        # D14-08: транспорт обирається ЯВНО — без -HttpsThumbprint/-BehindHttpsProxy/-AllowHttp скрипт зупиняється,
        # тож кожен випадок його задає (CI-стенд — -AllowHttp; D1c — HTTPS зі справжнім сертифікатом ранера).
        @{ Name = 'D1a. -WhatIf типово: WORKER_ENABLED=1, Mode=Database, Executor=Worker'; Extra = @('-AllowHttp')
           Must = @('WORKER_ENABLED=1', 'ECR_Jobs__Queue__Mode=Database', 'ECR_Jobs__Recalculation__Executor=Worker', 'ASPNETCORE_URLS = http://+:5000')
           MustNot = @('WORKER_ENABLED=0', 'ECR_Jobs__Recalculation__Executor=InProcess') },
        @{ Name = 'D1b. -WhatIf -DisableWorker: WORKER_ENABLED=0, Executor=InProcess'; Extra = @('-DisableWorker', '-AllowHttp')
           Must = @('WORKER_ENABLED=0', 'ECR_Jobs__Recalculation__Executor=InProcess')
           MustNot = @('WORKER_ENABLED=1', 'ECR_Jobs__Recalculation__Executor=Worker') },
        @{ Name = 'D1c. -WhatIf -HttpsThumbprint: ASPNETCORE_URLS=https, RequireHttps=true'; Extra = @('-HttpsThumbprint', $cert.Thumbprint)
           Must = @('ASPNETCORE_URLS = https://+:5000', 'ECR_Auth__RequireHttps = true')
           MustNot = @('ASPNETCORE_URLS = http://', 'ECR_Auth__RequireHttps = false') }
    )
    $i = 0
    foreach ($case in $cases) {
        $i++
        Test-Case $case.Name {
            $log = Join-Path $LogDir "deploy-whatif-$i.log"
            $extra = [string[]] $case.Extra
            $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            try {
                $out = & pwsh -NoProfile -File $deploy -SqlInstance 'ci-no-sql' -Database 'EcrCi' -MsiPath $msiPath `
                    -DataProtectionThumbprint $cert.Thumbprint -WhatIf @extra 2>&1 | Out-String
                $code = $LASTEXITCODE
            }
            finally { $ErrorActionPreference = $eap }
            Set-Content -Path $log -Value $out -Encoding utf8
            if ($code) { throw "deploy-ecr.ps1 -WhatIf → код $code (див. $log)" }
            foreach ($s in $case.Must) { if (-not $out.Contains($s)) { throw "у плані немає '$s' (див. $log)" } }
            foreach ($s in $case.MustNot) { if ($out.Contains($s)) { throw "у плані є '$s' (див. $log)" } }
            if (Get-Service EcrApi -ErrorAction SilentlyContinue) { throw '-WhatIf встановив службу EcrApi' }
        }
    }
}
finally { Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue }

# ── D2. Функції кроку 5 deploy-ecr.ps1 проти справжнього реєстру ──────────
Write-Host '── D2. Режим Api (функції deploy-ecr.ps1) у реєстрі встановленої служби'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($deploy, [ref] $tokens, [ref] $errors)
if ($errors.Count) { throw "deploy-ecr.ps1 не парситься: $($errors[0].Message)" }
foreach ($name in 'Resolve-JobExecutionConfig', 'Merge-ServiceEnvironmentEntry', 'Set-ServiceEnvironmentVariable',
        'Remove-ServiceEnvironmentEntry', 'Remove-ServiceEnvironmentVariable', 'Get-ConfiguredValue') {
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if (-not $fn) { throw "у deploy-ecr.ps1 немає функції $name — перевірку D2 треба оновити" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

$apiKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi'
$configPath = Join-Path $env:ProgramData 'ECR\config\appsettings.Production.json'
function Get-ApiEnvironment {
    $prop = Get-ItemProperty -Path $apiKey -Name Environment -ErrorAction SilentlyContinue
    if (-not $prop) { return , @() }
    return , @($prop.Environment)
}
# ⚠ Копія ЦИКЛУ кроку 5 deploy-ecr.ps1 (рядки після «Режим перерахунку EcrApi»):
# сам цикл — шість рядків, рішення і запис — справжні функції скрипта. Основний
# потік скрипта без -WhatIf тут не запускається: він вимагає SQL Server.
function Invoke-DeployJobModeStep {
    $workerEnabled = [bool] (Get-Service -Name EcrWorker -ErrorAction SilentlyContinue)
    $decision = Resolve-JobExecutionConfig -WorkerEnabled $workerEnabled `
        -FileMode (Get-ConfiguredValue -Path $configPath -Keys 'Jobs', 'Queue', 'Mode') `
        -FileExecutor (Get-ConfiguredValue -Path $configPath -Keys 'Jobs', 'Recalculation', 'Executor')
    foreach ($name in $decision.Set.Keys) { Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name $name -Value $decision.Set[$name] }
    foreach ($name in $decision.Remove) { Remove-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name $name }
}
function Assert-Environment([string[]] $must, [string[]] $mustNot) {
    $environment = Get-ApiEnvironment
    Write-Host "    Environment EcrApi: $($environment -join ' | ')"
    foreach ($s in $must) { if ($environment -notcontains $s) { throw "у Environment EcrApi немає '$s' (є: $($environment -join ', '))" } }
    foreach ($s in $mustNot) { if ($environment -contains $s) { throw "у Environment EcrApi є '$s'" } }
}

try {
    Test-Case 'D2a. Служба EcrWorker є (установка типово) → Environment EcrApi: Mode=Database, Executor=Worker' {
        Invoke-Msi "/i `"$msiPath`" /qn /l*v d2a.log"
        if (-not (Get-Service EcrWorker -ErrorAction SilentlyContinue)) { throw 'EcrWorker немає після установки типово' }
        # Чужий запис, як його пише крок 4 скрипта, — має пережити крок 5.
        Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name 'ASPNETCORE_URLS' -Value 'http://+:5000'
        Invoke-DeployJobModeStep
        Assert-Environment @('ECR_Jobs__Queue__Mode=Database', 'ECR_Jobs__Recalculation__Executor=Worker', 'ASPNETCORE_URLS=http://+:5000') `
            @('ECR_Jobs__Recalculation__Executor=InProcess')
    }

    Test-Case 'D2b. REINSTALL WORKER_ENABLED=0 → служби нема → Executor=InProcess, Mode=Quartz' {
        Invoke-Msi "/i `"$msiPath`" /qn /l*v d2b.log REINSTALL=ALL REINSTALLMODE=vomus WORKER_ENABLED=0"
        if (Get-Service EcrWorker -ErrorAction SilentlyContinue) { throw 'EcrWorker лишився після WORKER_ENABLED=0' }
        Invoke-DeployJobModeStep
        Assert-Environment @('ECR_Jobs__Recalculation__Executor=InProcess', 'ECR_Jobs__Queue__Mode=Quartz', 'ASPNETCORE_URLS=http://+:5000') `
            @('ECR_Jobs__Recalculation__Executor=Worker', 'ECR_Jobs__Queue__Mode=Database')
    }
}
finally {
    if (Get-Service EcrApi -ErrorAction SilentlyContinue) { Invoke-Msi "/x `"$msiPath`" /qn /l*v d2x.log" }
}

# ── D4. Set-BootstrapSecretFile (L10-02): власник і ACL до запису пароля ──
Write-Host '── D4. Файл bootstrap-пароля: власник Administrators, захищений ACL'
$fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Set-BootstrapSecretFile' }, $true)
if (-not $fn) { throw 'у deploy-ecr.ps1 немає функції Set-BootstrapSecretFile — перевірку D4 треба оновити' }
. ([scriptblock]::Create($fn.Extent.Text))
Test-Case 'D4. bootstrap.secret: підкладений файл замінено, власник BA, ACL лише служба (Read,Delete) і BA' {
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) "ecr-d4-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $folder | Out-Null
    try {
        Set-Content -Path (Join-Path $folder 'bootstrap.secret') -Value 'planted' -NoNewline
        Set-BootstrapSecretFile -ConfigFolder $folder -Password 'D4-Sentinel' -Principal 'NT AUTHORITY\SYSTEM'
        $path = Join-Path $folder 'bootstrap.secret'
        if ((Get-Content -LiteralPath $path -Raw) -ne 'D4-Sentinel') { throw 'у файлі не той пароль' }
        $acl = Get-Acl -LiteralPath $path
        $owner = ([System.Security.Principal.NTAccount] $acl.Owner).Translate([System.Security.Principal.SecurityIdentifier]).Value
        if ($owner -ne 'S-1-5-32-544') { throw "власник $($acl.Owner), очікували BUILTIN\Administrators" }
        if (-not $acl.AreAccessRulesProtected) { throw 'ACL успадковується' }
        $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        $foreign = @($rules | Where-Object { $_.IdentityReference.Value -notin 'S-1-5-18', 'S-1-5-32-544' })
        if ($foreign) { throw "зайві ACE: $(($foreign | ForEach-Object { $_.IdentityReference.Value }) -join ', ')" }
    }
    finally { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }
}

# ── D3. (довідково) Environment EcrApi після оновлення без deploy-ecr.ps1 ──
if ($PreviousMsiPath) {
    Write-Host '── D3. (довідково) Environment EcrApi після оновлення попередньої MSI поточною'
    try {
        Invoke-Msi "/i `"$PreviousMsiPath`" /qn /l*v d3a.log"
        Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name 'ECR_CI_Marker' -Value 'survives'
        Invoke-Msi "/i `"$msiPath`" /qn /l*v d3b.log"
        $survived = (Get-ApiEnvironment) -contains 'ECR_CI_Marker=survives'
        Add-Result 'D3. (довідково) Environment EcrApi пережив оновлення MSI без повторного deploy-ecr.ps1' $(if ($survived) { 'INFO: так' } else { 'INFO: НІ — після оновлення MSI треба повторити deploy-ecr.ps1 (рядок підключення, режим Api)' })
    }
    catch { Add-Result 'D3. (довідково) Environment після оновлення' "INFO: не виміряно — $_" }
    finally {
        if (Get-Service EcrApi -ErrorAction SilentlyContinue) { Invoke-Msi "/x `"$msiPath`" /qn /l*v d3x.log" }
    }
}

Write-Host ''
$results | Format-Table -AutoSize -Wrap | Out-String -Width 400 | Write-Host
if ($results.Result -match '^FAIL') { exit 1 }
