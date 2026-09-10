<#
.SYNOPSIS
    Один виклик: від чистого сервера до працюючої служби EcrApi.

.DESCRIPTION
    Оркеструє наявні, уже перевірені інструменти (директива №12) — НЕ
    дублює їхню логіку:
      1. Передумови       — sqlcmd/.NET на місці, БД існує, дані узгоджені.
      2. Схема            — та сама послідовність, що verify-sql-scripts.ps1
                             (01…14 за іменем через sqlcmd), БЕЗ 09-seed.sql
                             (застосунок виконує його сам при першому старті,
                             `docs/build/02-contracts.md` §14) і без
                             14-agent-jobs.sql, якщо не задано -FirstDeployment.
      3. MSI              — build-msi.ps1 (якщо -MsiPath не задано), потім
                             msiexec /qn.
      4. Конфігурація     — appsettings.Production.json у %ProgramData%\ECR\
                             config: пише лише в ПОРОЖНІЙ заповнювач, ніколи
                             не перезаписує заповнений (`Folders.wxs`:
                             NeverOverwrite; той самий принцип тут — на рівні
                             оркестратора, а не MSI).
      5. Старт служби     — лише якщо -ServiceAccount задано і служба сама
                             не піднялась.
      6. Здоров'я         — GET /health/live.

    Крок схеми виконується під `-SqlLogin`/інтегрованими обліковими даними
    ВИКОНАВЦЯ скрипта (DBA), НІКОЛИ під `-ServiceAccount`: сервісний
    обліковий запис застосунку не має DDL-прав у PROD (`D-66`,
    `docs/build/10-installer.md` §1.3) — і структурно не може отримати їх
    через цей скрипт, бо `-ServiceAccount` тут узагалі не бере участі в
    жодному виклику sqlcmd.

.PARAMETER SqlInstance
    Екземпляр SQL Server цільової (не тимчасової!) бази.

.PARAMETER SqlLogin
    Логін SQL-автентифікації для кроку схеми — елевований DBA-принципал.
    Без нього — інтегрована (`-E`), тобто обліковий запис, під яким
    запущено сам скрипт.

.PARAMETER SqlPassword
    Пароль до -SqlLogin. Ніколи не передається sqlcmd аргументом `-P`
    (видно в `Get-CimInstance Win32_Process`) — лише через змінну оточення
    дочірнього процесу `SQLCMDPASSWORD`, той самий прийом, що вже в
    `verify-sql-scripts.ps1`.

.PARAMETER Database
    Ім'я ЦІЛЬОВОЇ бази — не тимчасової, яку скрипт міг би сам створити й
    видалити (на відміну від verify-sql-scripts.ps1). База має існувати
    заздалегідь, з потрібним collation (`docs/build/10-installer.md`).

.PARAMETER ServiceAccount
    `DOMAIN\ecr-svc$` (gMSA, рекомендовано — без пароля) або `DOMAIN\user`.
    Передається в msiexec як SERVICE_ACCOUNT. Порожнє — служба
    реєструється, але свідомо НЕ стартує (`docs/build/10-installer.md` §1.4).

.PARAMETER ServicePassword
    Лише для не-gMSA облікового запису.

    ⛔ Чесно, а не мовчки: Windows Installer не має механізму прочитати
    властивість MSI зі змінної оточення (на відміну від sqlcmd), а
    `installer/Ecr.Installer/*.wxs` цей PR НЕ чіпає (директива №12,
    ЗАБОРОНЕНО). Тому це значення НЕМИНУЧЕ потрапляє в командний рядок
    процесу `msiexec` і видно через `Get-CimInstance Win32_Process` —
    `MsiHiddenProperties` (уже в `Package.wxs`) ховає його лише з `/l*v`
    логу, не зі списку процесів. Єдиний спосіб уникнути цього цілком —
    gMSA (без пароля взагалі). Скрипт про це попереджає вголос, а не
    вдає безпеку, якої тут немає.

.PARAMETER AppPort
    Порт Kestrel і правило брандмауера. За замовчуванням 5000.

.PARAMETER ConfigValues
    Шлях до JSON-файлу з реальними значеннями appsettings.Production.json
    цього майданчика. Записується ЛИШЕ якщо цільовий файл ще заповнювач
    (порожній об'єкт) — інакше крок 4 попереджає і нічого не чіпає.

.PARAMETER MsiPath
    Готовий Ecr.msi. Якщо не задано — скрипт сам викликає
    build-msi.ps1 -Version (тоді -Version обов'язковий).

.PARAMETER Version
    Версія для build-msi.ps1, якщо -MsiPath не задано.

.PARAMETER SkipSchema
    DBA вже накотив схему окремо (крок 2 повністю пропускається, sqlcmd
    не викликається жодного разу).

.PARAMETER FirstDeployment
    Перше розгортання на цій базі — тоді й лише тоді виконується
    14-agent-jobs.sql (завдання SQL Agent у msdb, `D-66`, одноразово).
    Без цього прапорця — оновлення, 14-agent-jobs.sql пропускається.
    Явний прапорець, а не автовизначення за станом бази: судження про
    "перше це чи ні" належить тому, хто розгортає, а не евристиці, яка
    вгадує за відсутністю таблиць.

.EXAMPLE
    # Побачити повний план, нічого не роблячи в системі
    .\tools\deploy-ecr.ps1 -SqlInstance NCATUATV12 -Database ECR `
        -ServiceAccount 'DOMAIN\ecr-svc$' -Version 1.0.0 -WhatIf

.EXAMPLE
    # Перше розгортання на чистому сервері
    .\tools\deploy-ecr.ps1 -SqlInstance NCATUATV12 -Database ECR `
        -ServiceAccount 'DOMAIN\ecr-svc$' -Version 1.0.0 `
        -ConfigValues .\uat-config.json -FirstDeployment

.NOTES
    Не переписує tools/build-msi.ps1, tools/sign-msi.ps1,
    tools/verify-msi.ps1, tools/verify-sql-scripts.ps1 — лише викликає їх
    (build-msi.ps1) або повторює перевірену послідовність (schema-крок).

    ⚠ Кожен .sql-файл — окрема sqlcmd-транзакція за замовчуванням (та сама
    поведінка, що вже в `verify-sql-scripts.ps1`/`09-commands.md` §3, не
    нова властивість цього оркестратора — файли з
    `src/Ecr.Infrastructure/Persistence/Sql/` цей PR не чіпає). Збій
    усередині файлу N лишає файли 1..N-1 застосованими повністю, N —
    можливо частково (залежно від внутрішніх операторів файлу), N+1..14 —
    не займаними взагалі. Оркестратор на цьому й зупиняється: наступний
    файл ніколи не запускається після невдалого.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SqlInstance,
    [string] $SqlLogin,
    [System.Security.SecureString] $SqlPassword,
    [Parameter(Mandatory)] [string] $Database,
    [string] $ServiceAccount,
    [System.Security.SecureString] $ServicePassword,
    [int] $AppPort = 5000,
    [string] $ConfigValues,
    [string] $MsiPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [switch] $SkipSchema,
    [switch] $FirstDeployment
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root      = Split-Path -Parent $PSScriptRoot
$sqlDir    = Join-Path $root 'src\Ecr.Infrastructure\Persistence\Sql'
$artifacts = Join-Path $root 'artifacts'
$migration = Join-Path $artifacts 'migration.sql'
$buildMsi  = Join-Path $root 'tools\build-msi.ps1'
$configPath = Join-Path $env:ProgramData 'ECR\config\appsettings.Production.json'

function Write-Step {
    param([string] $Text)
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function ConvertFrom-SecureStringPlain {
    param([System.Security.SecureString] $Secure)
    if (-not $Secure) { return $null }
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# ⚠ Окрема функція, а не вбудований код кроку 4: єдиний спосіб перевірити
# цю логіку по-справжньому (D-134), не проганяючи весь конвеєр до msiexec
# (реальний, а не заповнювач appsettings.Production.json — той самий факт
# майданчика, якого цей скрипт не вигадує).
function Test-ConfigIsPlaceholder {
    param([string] $Path)

    if (-not (Test-Path $Path)) { return $true }

    try {
        $existing = Get-Content $Path -Raw | ConvertFrom-Json -ErrorAction Stop
        return @($existing.PSObject.Properties).Count -eq 0
    }
    catch {
        # Не парситься як JSON — не наш заповнювач; не чіпаємо і не вгадуємо.
        return $false
    }
}

# ⚠ ПЕРЕДУМОВИ — до будь-якої зміни системи (дешевша відмова тут, ніж на
# кроці 3 з наполовину встановленою службою).
Write-Step "Крок 1/6: передумови"

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd не знайдено. Ним DBA виконує розгортання — без нього продовжувати нема сенсу."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK не знайдено."
}
if (-not $MsiPath -and -not $Version) {
    throw "Треба або -MsiPath (готовий Ecr.msi), або -Version (сам зберу через build-msi.ps1)."
}
if ($MsiPath -and -not (Test-Path $MsiPath)) {
    throw "MsiPath вказує на неіснуючий файл: $MsiPath"
}
if ($ServicePassword -and -not $ServiceAccount) {
    throw "-ServicePassword без -ServiceAccount — нема кому призначати пароль."
}
if ($ServicePassword) {
    Write-Warning ("SERVICE_PASSWORD потрапить у командний рядок msiexec і буде видимий " +
        "у списку процесів (Get-CimInstance Win32_Process) — MsiHiddenProperties ховає його " +
        "лише з /l*v логу, не зі списку процесів; це обмеження Windows Installer, не цього " +
        "скрипта. Уникнути цілком можна лише gMSA (без пароля).")
}
if ($ConfigValues -and -not (Test-Path $ConfigValues)) {
    throw "ConfigValues вказує на неіснуючий файл: $ConfigValues"
}

$sqlAuth = if ($SqlLogin) { @('-U', $SqlLogin) } else { @('-E') }

function Invoke-DeploySql {
    param(
        [Parameter(Mandatory)] [string] $TargetDb,
        [string] $Query,
        [string] $File
    )

    $arguments = @('-S', $SqlInstance) + $sqlAuth + @('-C', '-b', '-I', '-d', $TargetDb)
    $arguments += if ($File) { @('-i', $File) } else { @('-Q', $Query) }
    $what = if ($File) { Split-Path -Leaf $File } else { $Query }

    if ($PSCmdlet.ShouldProcess("$SqlInstance / $TargetDb", "sqlcmd -i $what")) {
        & sqlcmd @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "sqlcmd повернув $LASTEXITCODE на ${what}: файли до цього застосовані, ${what} і все після — ні."
        }
    }
}

# Перевірка з'єднання — читає, нічого не змінює, але й вона під ShouldProcess:
# контракт -WhatIf каже прямо «жодного sqlcmd», без винятків для читання.
Invoke-DeploySql -TargetDb 'master' -Query 'SELECT 1;'
Invoke-DeploySql -TargetDb 'master' -Query "IF DB_ID('$Database') IS NULL RAISERROR('database missing', 16, 1);"

try {
    if ($SqlPassword) { $env:SQLCMDPASSWORD = ConvertFrom-SecureStringPlain $SqlPassword }

    # ---------------------------------------------------------------------
    if ($SkipSchema) {
        Write-Step "Крок 2/6: схема — ПРОПУЩЕНО (-SkipSchema)"
    }
    else {
        Write-Step "Крок 2/6: схема ($Database на $SqlInstance)"

        New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

        if ($PSCmdlet.ShouldProcess($migration, 'dotnet ef migrations script --idempotent')) {
            & dotnet ef migrations script --idempotent `
                --project (Join-Path $root 'src\Ecr.Infrastructure') `
                --startup-project (Join-Path $root 'src\Ecr.Infrastructure') `
                --output $migration
            if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations script повернув $LASTEXITCODE" }
        }

        # ⛔ Та сама послідовність, що verify-sql-scripts.ps1 (docs/build/
        # 09-commands.md §3): 11 ПЕРЕД 07 (інакше aud.* лишиться на PRIMARY),
        # 06 — останнім (бере базу в ексклюзивне користування).
        # ⚠ 09-seed.sql тут НЕМАЄ НІКОЛИ — DML, застосунок виконує сам при
        # першому старті. 14-agent-jobs.sql — лише при -FirstDeployment.
        $scripts = [System.Collections.Generic.List[string]]::new()
        $scripts.AddRange([string[]](
            '01-filegroups.sql', '02-partitions.sql', '<migration>',
            '11-audit-tables.sql', '07-partition-tables.sql', '08-system-tables.sql',
            '12-archive-tables.sql', '13-cache-table.sql', '03-archive-proc.sql',
            '04-partition-maintenance.sql', '05-rpt-views.sql', '10-triggers.sql', '06-rcsi.sql'
        ))
        if ($FirstDeployment) { $scripts.Add('14-agent-jobs.sql') }

        foreach ($name in $scripts) {
            if ($name -eq '<migration>') {
                Invoke-DeploySql -TargetDb $Database -File $migration
            }
            else {
                $path = Join-Path $sqlDir $name
                if (-not (Test-Path $path)) { throw "Немає ${path}: перелік розійшовся з деревом." }
                Invoke-DeploySql -TargetDb $Database -File $path
            }
        }
    }
}
finally {
    if ($env:SQLCMDPASSWORD) { Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------
Write-Step "Крок 3/6: MSI"

if (-not $MsiPath) {
    if ($PSCmdlet.ShouldProcess('build-msi.ps1', "build-msi.ps1 -Version $Version")) {
        & $buildMsi -Version $Version
        $msiDir = Join-Path $root 'artifacts\msi'
        $built = Get-ChildItem $msiDir -Filter '*.msi' -Recurse | Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $built) { throw 'build-msi.ps1 не залишив жодного .msi у artifacts\msi.' }
        $MsiPath = $built.FullName
    }
    else {
        $MsiPath = '<буде зібрано build-msi.ps1>'
    }
}

$msiArgs       = @('/i', "`"$MsiPath`"", '/qn', '/l*v', 'ecr-install.log')
$msiArgsShown  = $msiArgs.Clone()
if ($ServiceAccount) {
    $msiArgs      += "SERVICE_ACCOUNT=$ServiceAccount"
    $msiArgsShown += "SERVICE_ACCOUNT=$ServiceAccount"
}
$msiArgs      += "APP_PORT=$AppPort"
$msiArgsShown += "APP_PORT=$AppPort"
if ($ServicePassword) {
    $msiArgs      += "SERVICE_PASSWORD=$(ConvertFrom-SecureStringPlain $ServicePassword)"
    $msiArgsShown += 'SERVICE_PASSWORD=***'   # ніколи не в плані/логу, лише в реальному виклику
}

if ($PSCmdlet.ShouldProcess($MsiPath, "msiexec $($msiArgsShown -join ' ')")) {
    $proc = Start-Process msiexec -ArgumentList $msiArgs -Wait -PassThru
    if ($proc.ExitCode -notin 0, 3010) { throw "msiexec повернув $($proc.ExitCode) — див. ecr-install.log" }
}

# ---------------------------------------------------------------------
Write-Step "Крок 4/6: конфігурація ($configPath)"

$isPlaceholder = Test-ConfigIsPlaceholder -Path $configPath

if (-not $isPlaceholder) {
    Write-Warning "$configPath уже має власні значення — НЕ перезаписую. Заповни $ConfigValues в нього вручну, якщо треба щось додати."
}
elseif (-not $ConfigValues) {
    Write-Host "Заповнювач лишається порожнім: -ConfigValues не задано. Заповнити треба вручну перед першим реальним використанням." -ForegroundColor Yellow
}
else {
    $values = Get-Content $ConfigValues -Raw | ConvertFrom-Json -ErrorAction Stop   # падає гучно, якщо не JSON
    if ($PSCmdlet.ShouldProcess($configPath, "записати значення з $ConfigValues")) {
        $values | ConvertTo-Json -Depth 20 | Set-Content -Path $configPath -Encoding UTF8
        Write-Host "Записано." -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------
Write-Step "Крок 5/6: старт служби"

if (-not $ServiceAccount) {
    Write-Host "SERVICE_ACCOUNT не задано — служба зареєстрована, але не стартує (навмисно, docs/build/10-installer.md §1.4)." -ForegroundColor Yellow
}
else {
    $svc = Get-Service -Name EcrApi -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Host 'Служба вже Running.' -ForegroundColor Green
    }
    elseif ($PSCmdlet.ShouldProcess('EcrApi', 'Start-Service')) {
        Start-Service -Name EcrApi
    }
}

# ---------------------------------------------------------------------
Write-Step "Крок 6/6: перевірка здоров'я"

$healthUrl = "http://localhost:$AppPort/health/live"
if ($PSCmdlet.ShouldProcess($healthUrl, 'GET /health/live')) {
    $ok = $false
    for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
        try {
            $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3
            $ok = $resp.StatusCode -eq 200
        }
        catch { Start-Sleep -Seconds 3 }
    }
    if (-not $ok) { throw "Служба не відповіла на $healthUrl за відведений час. Перевір Event Log (джерело ECR) і %ProgramData%\ECR\logs." }
    Write-Host "Служба відповідає на $healthUrl." -ForegroundColor Green
}

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
