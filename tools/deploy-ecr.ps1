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
      4. Секрети служби   — рядок підключення (постійний секрет — служба
                             читає його щоразу при старті) у реєстрі служби
                             (HKLM\...\Services\EcrApi\Environment), НЕ у
                             файлі: секрети ніколи не потрапляють у
                             appsettings.json (D-11, docs/build/
                             04-environment.md §6) — і `%ProgramData%\ECR\
                             config\appsettings.Production.json` тут не
                             виняток, хоч і не файл публікації. Знайдено
                             реальним прогоном людини (Q-213): без рядка
                             підключення застосунок падає з "Рядок
                             підключення 'Ecr' не заданий" при будь-якій
                             спробі стартувати службу. Пароль
                             bootstrap-адміністратора (за потреби, перше
                             розгортання) — НЕ туди: він одноразовий (на
                             відміну від рядка підключення), тож пишеться в
                             окремий файл `%ProgramData%\ECR\config\
                             bootstrap.secret` з ACL лише на обліковий
                             запис служби — застосунок сам читає й видаляє
                             його одразу після першого старту (директива
                             №13, Q-215, `BootstrapSecretFile.cs`).
      5. Конфігурація     — appsettings.Production.json у %ProgramData%\ECR\
                             config: НЕсекретні значення (наприклад,
                             Telemetry:OtlpEndpoint), пише лише в ПОРОЖНІЙ
                             заповнювач, ніколи не перезаписує заповнений
                             (`Folders.wxs`: NeverOverwrite; той самий
                             принцип тут — на рівні оркестратора, а не MSI).
      6. Старт служби     — лише якщо -ServiceAccount задано; тоді
                             БЕЗУМОВНИЙ Restart-Service, навіть якщо MSI
                             (Q-212) уже підняв службу під час msiexec
                             кроком 3, до того, як цей скрипт устиг
                             записати секрети кроком 4 — свіжозаписане
                             оточення побачить лише новий запуск процесу,
                             не вже працюючий.
      7. Здоров'я         — GET /health/live.

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

.PARAMETER ConnectionString
    Рядок підключення до -Database. Пишеться у реєстр служби
    (`HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment`, REG_MULTI_SZ,
    змінна `ECR_ConnectionStrings__Ecr`) — САМЕ там і НІКОЛИ у
    appsettings.json-родину файлів (D-11). Без цього параметра служба
    зареєструється й навіть підніметься (Windows Installer/SCM про нього
    не знає), але сам застосунок одразу впаде з `InvalidOperationException`
    при першій спробі побудувати DI-контейнер (Q-213, знайдено реальним
    прогоном на LenovoNakuLaptop). Існуючі інші записи в Environment цієї
    служби не чіпаються — лише ECR_ConnectionStrings__Ecr додається чи
    замінюється.

    ⛔ Чесно, а не мовчки: Windows Installer не має механізму прочитати
    властивість MSI зі змінної оточення (на відміну від sqlcmd), а
    `installer/Ecr.Installer/*.wxs` цей PR НЕ чіпає (директива №12,
    ЗАБОРОНЕНО). Тому це значення НЕМИНУЧЕ потрапляє в командний рядок
    процесу `msiexec` і видно через `Get-CimInstance Win32_Process` —
    `MsiHiddenProperties` (уже в `Package.wxs`) ховає його лише з `/l*v`
    логу, не зі списку процесів. Єдиний спосіб уникнути цього цілком —
    gMSA (без пароля взагалі). Скрипт про це попереджає вголос, а не
    вдає безпеку, якої тут немає.

.PARAMETER BootstrapPassword
    Пароль для одноразового локального адміністратора `bootstrap`
    (`Ecr.Application.Security.BootstrapAdmin`). НЕ йде в реєстр служби —
    на відміну від `-ConnectionString`, цей секрет одноразовий: потрібен
    рівно одному виклику при першому старті, а не щоразу, коли служба
    піднімається. Тримати його в реєстрі назавжди означало б ще один
    секрет, який довелося б прибирати вручну. Замість цього пишеться у
    файл `%ProgramData%\ECR\config\bootstrap.secret` з ACL, обмеженим лише
    обліковим записом служби (`-ServiceAccount`, або `NT AUTHORITY\SYSTEM`
    для Local System) і `BUILTIN\Administrators` — застосунок сам читає
    цей файл і одразу видаляє його при першому старті (директива №13,
    Q-215, `BootstrapSecretFile.cs`, `StartupSequence.cs`), незалежно від
    того, чи вдалося пароль потім використати. Потрібен ЛИШЕ на першому
    розгортанні порожньої бази: застосунок сам створює користувача
    `bootstrap` з роллю `BootstrapAdministrator`, якщо жоден
    домен-адміністратор ще не існує, і сам деактивує його, щойно
    домен-користувач отримає право `Security.ManageUsers`.

    Без пароля, зазначеного тут ХОЧ РАЗ (уручну чи цим параметром),
    увійти в порожню базу нічим — Windows-автентифікація (`Negotiate`)
    працює лише для вже відомого домен-користувача з роллю в системі, а
    такого на порожній базі ще немає.

.PARAMETER AppPort
    Порт Kestrel і правило брандмауера. За замовчуванням 5000.

.PARAMETER ConfigValues
    Шлях до JSON-файлу з НЕсекретними значеннями appsettings.Production.json
    цього майданчика (наприклад, Telemetry:OtlpEndpoint) — НІКОЛИ рядок
    підключення чи інший секрет, для нього -ConnectionString (D-11).
    Записується ЛИШЕ якщо цільовий файл ще заповнювач (порожній об'єкт) —
    інакше крок 5 попереджає і нічого не чіпає.

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
    # Перше розгортання на чистому сервері (порожня база — потрібен bootstrap)
    $cs = Read-Host -AsSecureString -Prompt 'Рядок підключення'
    $bp = Read-Host -AsSecureString -Prompt 'Пароль bootstrap-адміністратора'
    .\tools\deploy-ecr.ps1 -SqlInstance NCATUATV12 -Database ECR `
        -ServiceAccount 'DOMAIN\ecr-svc$' -Version 1.0.0 -ConnectionString $cs `
        -BootstrapPassword $bp -ConfigValues .\uat-config.json -FirstDeployment

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
    [System.Security.SecureString] $ConnectionString,
    [System.Security.SecureString] $BootstrapPassword,
    [int] $AppPort = 5000,
    [string] $ConfigValues,
    [string] $MsiPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [switch] $SkipSchema,
    [switch] $FirstDeployment
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ⛔ PS 7.3+: без цього нешкідливе stderr-попередження нативної команди
# (sqlcmd/msiexec/dotnet/npm) зупиняє скрипт ДО власної перевірки
# $LASTEXITCODE нижче (реальний прогін — build-msi.ps1, "npm warn
# deprecated" зупинив збірку, хоча код виходу був 0).
$PSNativeCommandUseErrorActionPreference = $false

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

# ⚠ Чиста функція, без реєстру, — єдиний спосіб перевірити (D-134) саме
# логіку злиття без прав адміністратора й без реального ключа служби:
# чужі записи Environment мають лишитись незайманими, змінюється лише
# запис з іменем $Name (замінюється, якщо вже був, інакше додається).
#
# ⛔ `return ,(...)` — КОМА ОБОВ'ЯЗКОВА. Без неї PowerShell розгортає
# масив з ОДНИМ елементом назад у скаляр на виході з функції (класична
# пастка): $result.Count і далі показує 1 (ETS-властивість скаляра теж
# дорівнює 1), але $result[0] тоді індексує СИМВОЛ рядка, а не елемент
# масиву — і Set-ItemProperty -Type MultiString отримає рядок замість
# REG_MULTI_SZ-масиву. Проявляється ЛИШЕ коли в реєстрі опиняється рівно
# один запис (типово — перше встановлення без інших змінних служби), тож
# без D-134 на порожньому масиві цей конкретний випадок легко не помітити.
function Merge-ServiceEnvironmentEntry {
    param(
        [string[]] $Existing,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Value
    )

    $prefix = "$Name="
    $untouched = @($Existing | Where-Object { $_ -notlike "$prefix*" })
    return ,([string[]]($untouched + "$prefix$Value"))
}

# ⚠ Windows Installer/MSI не має механізму передати змінну оточення в
# процес служби (D-11 забороняє appsettings.json, а ServiceInstall у WiX
# не бере оточення взагалі) — реєстр служби це єдиний канал, яким сам
# Windows SCM користується для застосунків, що читають ASP.NET Core
# `AddEnvironmentVariables`. Саме злиття — в Merge-ServiceEnvironmentEntry
# вище; тут лише читання й запис реєстру.
function Set-ServiceEnvironmentVariable {
    param(
        [Parameter(Mandatory)] [string] $ServiceName,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Value
    )

    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path $keyPath)) {
        throw "Немає ${keyPath}: службу $ServiceName ще не встановлено (крок 3 мав відбутися першим)."
    }

    $prop = Get-ItemProperty -Path $keyPath -Name Environment -ErrorAction SilentlyContinue
    $existing = if ($prop) { @($prop.Environment) } else { @() }
    $updated = Merge-ServiceEnvironmentEntry -Existing $existing -Name $Name -Value $Value

    Set-ItemProperty -Path $keyPath -Name Environment -Value $updated -Type MultiString
}

# ⚠ Директива №13 (Q-215): bootstrap-пароль — ОДНОРАЗОВИЙ, на відміну від
# рядка підключення. У реєстрі служби він лишався б назавжди. Файл з ACL на
# -ServiceAccount; застосунок сам читає й видаляє його при старті
# (BootstrapSecretFile.cs, окрема зміна на боці .NET).
function Set-BootstrapSecretFile {
    param(
        [Parameter(Mandatory)] [string] $ConfigFolder,
        [Parameter(Mandatory)] [string] $Password,
        [Parameter(Mandatory)] [string] $Principal   # DOMAIN\ecr-svc$, DOMAIN\user, чи NT AUTHORITY\SYSTEM
    )

    $path = Join-Path $ConfigFolder 'bootstrap.secret'
    Set-Content -Path $path -Value $Password -Encoding UTF8 -NoNewline

    $acl = Get-Acl -Path $path
    $acl.SetAccessRuleProtection($true, $false)   # прибрати успадкування — не звичайний файл конфігу
    $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

    $account = New-Object System.Security.Principal.NTAccount($Principal)
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        $account, 'Read,Delete', 'Allow'))
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        'BUILTIN\Administrators', 'FullControl', 'Allow'))

    Set-Acl -Path $path -AclObject $acl
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
Write-Step "Крок 1/7: передумови"

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
if (-not $ConnectionString) {
    Write-Warning ("-ConnectionString не задано — застосунок впаде з InvalidOperationException " +
        "(D-11) при першій спробі стартувати, поки ECR_ConnectionStrings__Ecr не буде додано " +
        "вручну в HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment.")
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
        Write-Step "Крок 2/7: схема — ПРОПУЩЕНО (-SkipSchema)"
    }
    else {
        Write-Step "Крок 2/7: схема ($Database на $SqlInstance)"

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
Write-Step "Крок 3/7: MSI"

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
Write-Step "Крок 4/7: секрети служби (реєстр EcrApi\Environment)"

if (-not $ConnectionString) {
    Write-Host ("ECR_ConnectionStrings__Ecr не записано (-ConnectionString не задано) — " +
        "служба впаде при старті, поки значення не буде додано вручну.") -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment',
        'записати ECR_ConnectionStrings__Ecr')) {
    Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name 'ECR_ConnectionStrings__Ecr' `
        -Value (ConvertFrom-SecureStringPlain $ConnectionString)
    Write-Host "Рядок підключення записано." -ForegroundColor Green
}

if ($BootstrapPassword) {
    # ⚠ Local System — окремий випадок: обліковий запис комп'ютера немає
    # сенсу писати як ACL-принципал так само, як доменний, бо служба під
    # ним працює як NT AUTHORITY\SYSTEM.
    $principal = if ($ServiceAccount) { $ServiceAccount } else { 'NT AUTHORITY\SYSTEM' }

    if ($PSCmdlet.ShouldProcess((Join-Path (Split-Path $configPath -Parent) 'bootstrap.secret'),
            'записати одноразовий файл bootstrap-пароля')) {
        Set-BootstrapSecretFile -ConfigFolder (Split-Path $configPath -Parent) `
            -Password (ConvertFrom-SecureStringPlain $BootstrapPassword) -Principal $principal
        Write-Host "Одноразовий файл пароля записано — застосунок прибере його сам після першого старту." -ForegroundColor Green
    }
}
else {
    Write-Host ("-BootstrapPassword не задано — якщо база порожня і жоден " +
        "домен-адміністратор ще не існує, увійти в застосунок після першого розгортання " +
        "нічим (bootstrap-користувача не буде створено).") -ForegroundColor Yellow
}

# ---------------------------------------------------------------------
Write-Step "Крок 5/7: конфігурація ($configPath)"

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
Write-Step "Крок 6/7: старт служби"

if (-not $ServiceAccount) {
    Write-Host "SERVICE_ACCOUNT не задано — служба зареєстрована, але не стартує (навмисно, docs/build/10-installer.md §1.4)." -ForegroundColor Yellow
}
else {
    # ⛔ БЕЗУМОВНИЙ перезапуск, не "старт, якщо не Running": MSI (Q-212)
    # стартує службу ПІД ЧАС msiexec, ДО того, як цей скрипт встиг записати
    # секрети кроком 4 — щойно записане оточення побачить лише СВІЖИЙ запуск
    # процесу, не вже працюючий.
    $svc = Get-Service -Name EcrApi -ErrorAction SilentlyContinue
    if ($svc -and $PSCmdlet.ShouldProcess('EcrApi', 'Restart-Service')) {
        Restart-Service -Name EcrApi -Force
    }
    elseif (-not $svc -and $PSCmdlet.ShouldProcess('EcrApi', 'Start-Service')) {
        Start-Service -Name EcrApi
    }
}

# ---------------------------------------------------------------------
Write-Step "Крок 7/7: перевірка здоров'я"

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
