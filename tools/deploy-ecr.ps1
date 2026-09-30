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
                             Logging:File:Directory), пише лише в ПОРОЖНІЙ
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
      7. Здоров'я         — GET /health/live, потім GET /health/ready до
                             `Healthy`/`Degraded` (не довше -ReadyTimeoutSeconds).
                             Перевірки, що не Healthy, друкуються. Unhealthy
                             лише через `sources` (зовнішнє джерело PI/SQL) —
                             попередження, не провал; будь-яка інша Unhealthy
                             після тайм-ауту — провал розгортання.

    ФВ-9.8 / D-206 (P2) додали до кроків:
      1. редакція й версія SQL Server (SERVERPROPERTY): друкується; нижче
         2016 SP1 — зупинка; Express — зупинка без -AllowExpress;
      3. WORKER_ENABLED=1|0 у msiexec (служба EcrWorker): з I2-2 ТИПОВО 1,
         крім SQL Server Express і -DisableWorker (Resolve-WorkerDeployment);
      4. рядок підключення — ще й у Services\EcrWorker\Environment;
      5. Database:EditionMode — визначене значення в Environment служб, якщо
         оператор не задав його явно (-EditionMode, файл майданчика, Environment);
         режим перерахунку Api (Jobs:Queue:Mode, Jobs:Recalculation:Executor) —
         у Environment EcrApi за ФАКТОМ служби: Database + Worker лише разом із
         EcrWorker, без неї — InProcess (Resolve-JobExecutionConfig);
      6. перезапуск EcrWorker і перевірка, що він не впав одразу.

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
    видалити (на відміну від verify-sql-scripts.ps1). Без `-CreateDatabaseIfMissing`
    база має існувати заздалегідь, з потрібним collation
    (`docs/build/10-installer.md`); з прапорцем — скрипт створить її сам,
    тим самим collation (`Latin1_General_100_CI_AS_SC`, `02a-db-schema.md` §1.0).

.PARAMETER CreateDatabaseIfMissing
    Директива людини (2026-09-11): якщо `-Database` не існує на цільовому
    інстансі — створити її самому (`CREATE DATABASE ... COLLATE
    Latin1_General_100_CI_AS_SC`), а не вимагати, щоб адміністратор БД
    зробив це заздалегідь. Використовує ТІ САМІ облікові дані, під якими й
    так виконується крок схеми (`-SqlLogin`/інтегровані), — не нові права:
    той, хто запускає цей скрипт (чи майстер, що його хостить), має мати
    право `CREATE DATABASE` на інстансі. Не змінює `D-66`/`10-installer.md`
    §1.3: обліковий запис ЗАСТОСУНКУ (`-ServiceAccount`) і далі без
    DDL-прав, MSI і далі не торкається бази — це стосується лише
    ОРКЕСТРАТОРА, керованого людиною з доступом до БД. Без прапорця —
    попередня поведінка: відсутня база зупиняє скрипт з поясненням.

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

.PARAMETER DataProtectionThumbprint
    ⛔ S11 (аудит безпеки, 2026-09-29): ОБОВ'ЯЗКОВИЙ. Відбиток сертифіката з
    закритим ключем у `Cert:\LocalMachine\My`, яким застосунок шифрує ключі
    кільця DataProtection у `sec.DataProtectionKey`. Служба працює в
    середовищі Production, а там без сертифіката застосунок НЕ СТАРТУЄ: ключі
    у відкритому вигляді дали б кожному, хто читає базу чи її бекап, підробити
    cookie сеансу будь-якого користувача. Перевіряється на кроці 1 — ДО
    встановлення служби: сертифікат є в `Cert:\LocalMachine\My` і має закритий
    ключ (`HasPrivateKey`). Пишеться в реєстр служби змінною
    `ECR_Auth__DataProtection__CertificateThumbprint` тим самим каналом, що й
    рядок підключення (крок 4).

    ⚠ Вузлів за балансувальником кілька (D-32) — сертифікат ОДИН і той самий
    на всіх (експорт/імпорт PFX), інакше вузли не розшифрують ключі один
    одного. Обліковому запису служби (`-ServiceAccount`) потрібне право
    читання закритого ключа (certlm.msc → Усі завдання → Керування закритими
    ключами) — цього скрипт не надає. Згоди на незахищене кільце для
    одноразових стендів цей скрипт не ставить ніколи (сторож в
    Ecr.Architecture.Tests).

.PARAMETER HttpsThumbprint
    ⛔ D14-08/R-01: ТРАНСПОРТ — рівно один із трьох параметрів (`-HttpsThumbprint`,
    `-BehindHttpsProxy`, `-AllowHttp`); жодного з них — зупинка з поясненням, а не мовчазний HTTP.
    Відбиток сертифіката HTTPS у `Cert:\LocalMachine\My` (його видає PKI замовника, скрипт його не
    генерує). Перевіряється на кроці 1 — ДО встановлення: сертифікат є, має закритий ключ (`HasPrivateKey`),
    чинний за датами (спливає менш ніж за 30 днів — попередження). Пишеться в Environment служби EcrApi:
    `ASPNETCORE_URLS=https://+:<AppPort>` і `ECR_Transport__Https__CertificateThumbprint` — застосунок сам
    завантажує сертифікат зі сховища за відбитком (`HttpsTransport`, Program.cs) і віддає його Kestrel;
    `ECR_Auth__RequireHttps=true` (cookie сеансу Secure). HTTPS слухає на `-AppPort` — цей самий порт MSI
    відкриває в брандмауері (APP_PORT); для звичного 443 задай `-AppPort 443`. Обліковому запису служби
    (`-ServiceAccount`) потрібне право читання закритого ключа (certlm.msc → Усі завдання → Керування закритими
    ключами) — скрипт його не надає. Відбиток у журнал не друкується (лише суб'єкт і строк дії). Крок 7
    перевіряє, що Kestrel віддає САМЕ цей сертифікат. Параметр потрібен на КОЖНОМУ оновленні, як і решта
    параметрів Environment.

.PARAMETER HttpRedirectPort
    Лише з `-HttpsThumbprint`. Додатковий http-порт, що перенаправляє (308) на HTTPS-порт `-AppPort`:
    `ASPNETCORE_URLS=https://+:<AppPort>;http://+:<HttpRedirectPort>` і `ECR_Transport__Https__Port=<AppPort>`.
    ⚠ MSI відкриває в брандмауері лише APP_PORT — правило для цього порту адміністратор додає сам
    (`New-NetFirewallRule`, install-guide §HTTPS); скрипт про це попереджає. Без параметра — лише HTTPS-порт.

.PARAMETER BehindHttpsProxy
    TLS завершується ПЕРЕД застосунком (зворотний проксі / IIS ARR / балансувальник): Kestrel слухає http на
    `-AppPort`, `ECR_Auth__RequireHttps=true` лишається (cookie Secure — браузер говорить із проксі по HTTPS,
    тож вхід працює). ⚠ Застосунок не довіряє `X-Forwarded-*` (UseForwardedHeaders не вмикається): HSTS і
    перенаправлення http→https — на проксі; Windows-автентифікація (Negotiate) за проксі зазвичай не працює. Порт
    Kestrel варто закрити брандмауером для всіх, крім проксі.

.PARAMETER AllowHttp
    Лише для СТЕНДА: HTTP без HTTPS. `ECR_Auth__RequireHttps=false` — cookie сеансу НЕ Secure, пароль і сеанс
    ідуть відкритим текстом. Скрипт пише попередження в журнал розгортання, застосунок — попередження на
    старті й жовтий `transport` на `/health/ready` та `/admin/health`. На майданчику замовника не
    використовувати.

.PARAMETER AppPort
    Порт Kestrel і правило брандмауера (HTTPS-порт із `-HttpsThumbprint`, http-порт в інших режимах).
    За замовчуванням 5000.

.PARAMETER ConfigValues
    Шлях до JSON-файлу з НЕсекретними значеннями appsettings.Production.json
    цього майданчика (наприклад, Logging:File:Directory; ⚠ не
    Telemetry:OtlpEndpoint — експорту OTLP у цій версії немає) — НІКОЛИ рядок
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

.PARAMETER ReadyTimeoutSeconds
    Скільки секунд кроку 7 чекати, поки /health/ready стане Healthy або
    Degraded (після того, як /health/live уже відповів). За замовчуванням 180.

.PARAMETER EnableWorker
    Службу EcrWorker — наглядач пулу воркерів перерахунку (ФВ-9.8, D-206;
    `installer/Ecr.Installer/Worker.wxs`) — з I2-2 скрипт ставить і так,
    ТИПОВО. Прапорець лишився для сумісності (майстер `Ecr-Setup` передає його,
    якщо служба вже є) і має значення лише на SQL Server Express: там без нього
    воркера не буде. Секрети — той самий рядок підключення, у
    `HKLM:\SYSTEM\CurrentControlSet\Services\EcrWorker\Environment`; обліковий
    запис — той самий -ServiceAccount. Разом із службою в Environment EcrApi
    пишеться Jobs:Queue:Mode = Database і Jobs:Recalculation:Executor = Worker.

.PARAMETER DisableWorker
    Не ставити службу EcrWorker: у msiexec іде WORKER_ENABLED=0 (наявну службу
    MSI прибере — крок 3 попереджає), а EcrApi отримує
    Jobs:Recalculation:Executor = InProcess — перерахунок у процесі Api, як до
    I2-2. Стан командного рядка = бажаний стан: MSI властивість не пам'ятає,
    тож для вимкненого воркера прапорець потрібен на КОЖНОМУ оновленні.

.PARAMETER EditionMode
    Явний `Database:EditionMode` (Auto | Standard | Enterprise) — пишеться в
    Environment служб як ECR_Database__EditionMode і перекриває все.
    Без параметра скрипт визначає редакцію SQL Server сам (крок 1) і пише
    визначене значення, ЛИШЕ якщо оператор не задав його раніше: ні в
    `%ProgramData%\ECR\config\appsettings.Production.json` (Database:EditionMode),
    ні в Environment служби EcrApi. Змінна оточення перекриває файл, тому
    запис поверх явного значення у файлі тихо його скасував би — цього скрипт
    не робить ніколи.

.PARAMETER AllowExpress
    Дозволити SQL Server Express — лише для dev/стенда. Без прапорця Express
    зупиняє розгортання на кроці 1: SQL Server Agent там немає (регламентні
    завдання 14-agent-jobs.sql не створюються), межа — 10 ГБ на базу.

.EXAMPLE
    # Побачити повний план, нічого не роблячи в системі
    .\tools\deploy-ecr.ps1 -SqlInstance NCATUATV12 -Database ECR `
        -ServiceAccount 'DOMAIN\ecr-svc$' -Version 1.0.0 `
        -DataProtectionThumbprint '<відбиток з Cert:\LocalMachine\My>' `
        -HttpsThumbprint '<відбиток сертифіката HTTPS>' -AppPort 443 -WhatIf

.EXAMPLE
    # Перше розгортання на чистому сервері (порожня база — потрібен bootstrap)
    $cs = Read-Host -AsSecureString -Prompt 'Рядок підключення'
    $bp = Read-Host -AsSecureString -Prompt 'Пароль bootstrap-адміністратора'
    .\tools\deploy-ecr.ps1 -SqlInstance NCATUATV12 -Database ECR `
        -ServiceAccount 'DOMAIN\ecr-svc$' -Version 1.0.0 -ConnectionString $cs `
        -DataProtectionThumbprint '<відбиток з Cert:\LocalMachine\My>' `
        -HttpsThumbprint '<відбиток сертифіката HTTPS>' -AppPort 443 `
        -BootstrapPassword $bp -ConfigValues .\uat-config.json -FirstDeployment

.EXAMPLE
    # Стенд без сертифіката (явно і з попередженням): HTTP, cookie не Secure
    .\tools\deploy-ecr.ps1 -SqlInstance localhost -Database ECR -MsiPath .\Ecr.msi `
        -DataProtectionThumbprint '<відбиток>' -AllowHttp -AllowExpress

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
    [string] $DataProtectionThumbprint,
    [string] $HttpsThumbprint,
    [ValidateRange(0, 65535)] [int] $HttpRedirectPort = 0,
    [switch] $BehindHttpsProxy,
    [switch] $AllowHttp,
    [int] $AppPort = 5000,
    [string] $ConfigValues,
    [string] $MsiPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [switch] $SkipSchema,
    [switch] $FirstDeployment,
    [switch] $CreateDatabaseIfMissing,
    [ValidateRange(10, 3600)] [int] $ReadyTimeoutSeconds = 180,
    [switch] $EnableWorker,
    [switch] $DisableWorker,
    [ValidateSet('Auto', 'Standard', 'Enterprise')] [string] $EditionMode,
    [switch] $AllowExpress
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ⛔ Q-217 (реальний прогін): PowerShell перетворює КОЖЕН запис нативної
# команди в stderr на запис у потоці помилок, і $ErrorActionPreference =
# 'Stop' зупиняє скрипт на цьому записі незалежно від коду виходу —
# "npm warn deprecated ..." зупинило build-msi.ps1 саме так. Не про
# $PSNativeCommandUseErrorActionPreference (за замовчуванням і так
# $false — попередня версія цього фікса міняла її на те саме значення).
# Єдине надійне джерело істини — фактичний код виходу.
function Invoke-NativeStep {
    param(
        [Parameter(Mandatory)] [string] $Description,
        [Parameter(Mandatory)] [scriptblock] $Command
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE) {
        throw "$Description завершився з кодом $LASTEXITCODE"
    }
}

$root      = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$buildMsi  = Join-Path $root 'tools\build-msi.ps1'
$configPath = Join-Path $env:ProgramData 'ECR\config\appsettings.Production.json'

# ⛔ Q-219: два джерела схеми. `tools/build-installer.ps1` кладе готові
# `sql\` і `migration.sql` ПОРУЧ із цим самим файлом — так улаштований
# самодостатній `Ecr-Setup-*.exe` (payload розпаковується в один каталог
# із deploy-ecr.ps1, `IncludeAllContentForSelfExtract`). Якщо їх нема —
# це прогін із дерева репозиторію (розробка/CI/`-MsiPath` напряму), і
# джерело — `src/Ecr.Infrastructure`, як і раніше; тоді потрібні
# `dotnet-ef` і .NET SDK на цій самій машині. Пакований варіант — єдиний,
# що годиться для дійсно чистого сервера (жодного SDK, жодного клону
# репозиторію) — саме це й було метою Q-219.
$packagedSqlDir     = Join-Path $PSScriptRoot 'sql'
$packagedMigration  = Join-Path $PSScriptRoot 'migration.sql'
$isPackagedSchema   = (Test-Path $packagedSqlDir) -and (Test-Path $packagedMigration)

$sqlDir    = if ($isPackagedSchema) { $packagedSqlDir } else { Join-Path $root 'src\Ecr.Infrastructure\Persistence\Sql' }
$migration = if ($isPackagedSchema) { $packagedMigration } else { Join-Path $artifacts 'migration.sql' }

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

# ⚠ Чиста функція (D-134): рішення про редакцію SQL Server без мережі —
# перевіряється на готових значеннях SERVERPROPERTY, а не лише на живому
# інстансі (Standard/Express/2014 на машині розробника немає).
# Вхід — EngineEdition, ProductVersion, Edition (рядок). Вихід:
#   Name  — людська назва (Standard / Enterprise / Developer / Evaluation / Express),
#   Mode  — значення Database:EditionMode для запису, або $null (не записувати),
#   Stop  — причина зупинки розгортання, або $null,
#   Note  — пояснення вибору для друку.
#
# ⛔ Developer і Evaluation мають EngineEdition = 3, як Enterprise (04-environment.md
# §6), але відрізняються рядком Edition. Для них — Standard: це стенди, а
# ліцензія продуктиву, під яку їх наближають, невідома; Standard — базова
# редакція, бюджет має витримуватися на ній (D-103). Enterprise пишеться лише
# за справжнього «Enterprise Edition».
function Resolve-SqlEdition {
    param(
        [Parameter(Mandatory)] [int] $EngineEdition,
        [Parameter(Mandatory)] [string] $ProductVersion,
        [string] $Edition = '',
        [switch] $AllowExpress
    )

    $parts = $ProductVersion.Split('.')
    $major = 0
    $build = 0
    [void] [int]::TryParse($parts[0], [ref] $major)
    if ($parts.Count -gt 2) { [void] [int]::TryParse($parts[2], [ref] $build) }

    $name = switch ($EngineEdition) {
        2 { 'Standard' }
        3 {
            if ($Edition -match 'Developer') { 'Developer' }
            elseif ($Edition -match 'Evaluation') { 'Evaluation' }
            else { 'Enterprise' }
        }
        4 { 'Express' }
        default { "EngineEdition $EngineEdition" }
    }

    $result = [pscustomobject]@{ Name = $name; Major = $major; Mode = $null; Stop = $null; Note = '' }

    # ⛔ Підлога — 2016 SP1 (13.0.4001): до SP1 партиціонування, columnstore і
    # компресія лише в Enterprise, OPENJSON і CREATE OR ALTER — з 2016 SP1.
    if ($major -lt 13 -or ($major -eq 13 -and $build -lt 4001)) {
        $result.Stop = "SQL Server $ProductVersion ($name) нижче мінімальної версії 2016 SP1 (13.0.4001): " +
            "модель архівації (партиціонування, columnstore, компресія) і OPENJSON там недоступні."
        return $result
    }

    switch ($name) {
        'Standard'   { $result.Mode = 'Standard' }
        'Enterprise' { $result.Mode = 'Enterprise' }
        'Developer'  { $result.Mode = 'Standard'; $result.Note = 'Developer = Enterprise за можливостями, але це стенд: режим Standard (D-103).' }
        'Evaluation' { $result.Mode = 'Standard'; $result.Note = 'Evaluation = Enterprise на 180 днів: режим Standard, щоб не залежати від тимчасової ліцензії.' }
        'Express' {
            if ($AllowExpress) {
                $result.Mode = 'Standard'
                $result.Note = 'Express дозволено -AllowExpress (dev): завдань SQL Agent не буде, межа 10 ГБ на базу.'
            }
            else {
                $result.Stop = "SQL Server Express непридатний для розгортання: немає SQL Server Agent " +
                    "(регламентні завдання 14-agent-jobs.sql не створюються), межа 10 ГБ на базу. " +
                    "Для dev-стенда — явний дозвіл -AllowExpress."
            }
        }
        default {
            $result.Note = "Невідома редакція ($name) — Database:EditionMode не записується, застосунок визначить сам (Auto)."
        }
    }

    return $result
}

# ⚠ Чиста функція (D-134): чи писати ECR_Database__EditionMode і яке значення.
# Порядок пріоритету — явне завжди сильніше за визначене:
#   1. -EditionMode              → писати його;
#   2. значення у файлі майданчика → НЕ писати (змінна оточення перекрила б
#      файл, ProgramDataConfiguration.cs);
#   3. значення вже в Environment → НЕ писати (оператор чи попереднє розгортання);
#   4. визначена редакція         → писати її;
#   5. інакше                     → не писати (застосунок — Auto).
function Resolve-EditionModeWrite {
    param(
        [string] $Explicit,
        [string] $Detected,
        [string] $FileValue,
        [string] $EnvironmentValue
    )

    if ($Explicit) { return [pscustomobject]@{ Write = $true; Value = $Explicit; Reason = "явно, -EditionMode $Explicit" } }
    if ($FileValue) { return [pscustomobject]@{ Write = $false; Value = $FileValue; Reason = "задано у appsettings.Production.json ($FileValue) — не перезаписую" } }
    if ($EnvironmentValue) { return [pscustomobject]@{ Write = $false; Value = $EnvironmentValue; Reason = "уже в Environment служби ($EnvironmentValue) — не перезаписую" } }
    if ($Detected) { return [pscustomobject]@{ Write = $true; Value = $Detected; Reason = "визначено за редакцією SQL Server" } }
    return [pscustomobject]@{ Write = $false; Value = $null; Reason = 'редакцію не визначено — застосунок визначить сам (Auto)' }
}

# Database:EditionMode з файлу майданчика або $null. Файл, що не парситься, —
# теж $null: крок 5 уже попередив про нього, вгадувати не беремося.
function Get-ConfiguredEditionMode {
    param([string] $Path)

    if (-not (Test-Path $Path)) { return $null }
    try { $json = Get-Content $Path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { return $null }
    if (-not $json -or -not $json.PSObject.Properties['Database']) { return $null }
    $database = $json.Database
    if (-not $database -or -not $database.PSObject.Properties['EditionMode']) { return $null }
    $value = [string] $database.EditionMode
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value
}

# Значення змінної з Environment служби або $null (служби ще немає — теж $null).
function Get-ServiceEnvironmentValue {
    param(
        [Parameter(Mandatory)] [string] $ServiceName,
        [Parameter(Mandatory)] [string] $Name
    )

    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $prop = Get-ItemProperty -Path $keyPath -Name Environment -ErrorAction SilentlyContinue
    if (-not $prop) { return $null }
    $entry = @($prop.Environment) | Where-Object { $_ -like "$Name=*" } | Select-Object -First 1
    if (-not $entry) { return $null }
    return $entry.Substring($Name.Length + 1)
}

# ⚠ Чиста функція (як Merge-ServiceEnvironmentEntry): прибрати запис $Name,
# чужі — незаймані. Та сама кома в `return ,(...)` і з тієї ж причини.
function Remove-ServiceEnvironmentEntry {
    param(
        [string[]] $Existing,
        [Parameter(Mandatory)] [string] $Name
    )

    return ,([string[]] @($Existing | Where-Object { $_ -notlike "$Name=*" }))
}

function Remove-ServiceEnvironmentVariable {
    param(
        [Parameter(Mandatory)] [string] $ServiceName,
        [Parameter(Mandatory)] [string] $Name
    )

    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $prop = Get-ItemProperty -Path $keyPath -Name Environment -ErrorAction SilentlyContinue
    if (-not $prop) { return }
    $updated = Remove-ServiceEnvironmentEntry -Existing @($prop.Environment) -Name $Name
    if ($updated.Count -eq 0) { Remove-ItemProperty -Path $keyPath -Name Environment }
    else { Set-ItemProperty -Path $keyPath -Name Environment -Value $updated -Type MultiString }
}

# Значення вкладеного ключа з файлу майданчика (наприклад, Jobs → Queue → Mode)
# або $null: немає файлу, не парситься, немає ключа, порожнє значення.
function Get-ConfiguredValue {
    param(
        [string] $Path,
        [Parameter(Mandatory)] [string[]] $Keys
    )

    if (-not $Path -or -not (Test-Path $Path)) { return $null }
    try { $node = Get-Content $Path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { return $null }
    foreach ($key in $Keys) {
        if ($null -eq $node -or -not $node.PSObject.Properties[$key]) { return $null }
        $node = $node.$key
    }
    $value = [string] $node
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value
}

# ⛔ I2-2 (чиста функція): ставити службу EcrWorker чи ні. Типово — так
# (замір I2-2: ізоляція лейну default, межа пам'яті процесу, Api 162 МБ проти
# 367). Винятки:
#   -DisableWorker          → ні;
#   -EnableWorker           → так, навіть на Express (явний вибір для стенда);
#   SQL Server Express      → ні: dev-стенд (-AllowExpress), перерахунок у
#                             процесі Api, як до I2-2;
#   редакція невідома (-WhatIf, SERVERPROPERTY не виконувався) → так, з приміткою.
function Resolve-WorkerDeployment {
    param(
        [switch] $EnableWorker,
        [switch] $DisableWorker,
        [string] $EditionName
    )

    if ($EnableWorker -and $DisableWorker) {
        throw '-EnableWorker і -DisableWorker разом — оберіть одне.'
    }
    if ($DisableWorker) {
        return [pscustomobject]@{ Enabled = $false; Reason = 'вимкнено явно (-DisableWorker)' }
    }
    if ($EnableWorker) {
        return [pscustomobject]@{ Enabled = $true; Reason = 'явно (-EnableWorker)' }
    }
    if ($EditionName -eq 'Express') {
        return [pscustomobject]@{ Enabled = $false
            Reason = 'SQL Server Express — dev-стенд: перерахунок у процесі Api (увімкнути — -EnableWorker)' }
    }
    if (-not $EditionName) {
        return [pscustomobject]@{ Enabled = $true
            Reason = 'типово; редакцію SQL Server не визначено (-WhatIf) — на Express воркера не буде' }
    }
    return [pscustomobject]@{ Enabled = $true; Reason = 'типово (I2-2)' }
}

# ⛔ I2-2 (чиста функція): режим перерахунку Api визначається ФАКТОМ виконавця,
# а не лише конфігом. Api з Executor = Worker лейн перерахунку НЕ бере
# (JobLaneMap.ApiLanes), тож Worker без служби = перерахунок без виконавця.
# Тому значення пишуться в Environment EcrApi (перекриває файл майданчика й
# appsettings.json) щоразу:
#   служба є  → Mode = Database, Executor = Worker (Worker без Database нічого
#               не дає: у режимі Quartz перерахунок однаково йде в Api);
#   служби нема → Executor = InProcess завжди; Mode = Quartz (варіант B заміру
#               I2-2), АЛЕ якщо файл майданчика задає Mode сам — запис Mode з
#               Environment прибирається, щоб рішення файлу діяло.
# Значення у файлі, що суперечать факту служби, не мовчки перекриваються — попередження.
function Resolve-JobExecutionConfig {
    param(
        [Parameter(Mandatory)] [bool] $WorkerEnabled,
        [string] $FileMode,
        [string] $FileExecutor
    )

    $modeName = 'ECR_Jobs__Queue__Mode'
    $executorName = 'ECR_Jobs__Recalculation__Executor'
    $set = [ordered]@{}
    $remove = @()
    $warnings = @()

    if ($WorkerEnabled) {
        $set[$modeName] = 'Database'
        $set[$executorName] = 'Worker'
        if ($FileMode -and $FileMode -ne 'Database') {
            $warnings += "appsettings.Production.json задає Jobs:Queue:Mode = $FileMode, але служба EcrWorker є: Environment EcrApi перекриває його на Database (вимкнути воркер — -DisableWorker)."
        }
        if ($FileExecutor -and $FileExecutor -ne 'Worker') {
            $warnings += "appsettings.Production.json задає Jobs:Recalculation:Executor = $FileExecutor, але служба EcrWorker є: Environment EcrApi перекриває його на Worker (вимкнути воркер — -DisableWorker)."
        }
    }
    else {
        $set[$executorName] = 'InProcess'
        if ($FileExecutor -and $FileExecutor -ne 'InProcess') {
            $warnings += "appsettings.Production.json задає Jobs:Recalculation:Executor = $FileExecutor, а служби EcrWorker немає: перерахунок лишився б без виконавця — Environment EcrApi перекриває його на InProcess."
        }
        if ($FileMode) { $remove += $modeName }
        else { $set[$modeName] = 'Quartz' }
    }

    return [pscustomobject]@{ Set = $set; Remove = [string[]] $remove; Warnings = [string[]] $warnings }
}

# ⛔ Чиста функція: чи потрібен .NET SDK цьому запуску. Його кличуть у двох місцях —
# `build-msi.ps1` (коли -MsiPath не задано) і `dotnet ef migrations script` (крок 2,
# коли схема не з пакета й немає -SkipSchema). Більше ніде: пакований запуск із
# готовим MSI обходиться без SDK (install-guide §2.1).
function Get-DotnetRequirement {
    param(
        [Parameter(Mandatory)] [bool] $HasMsiPath,
        [Parameter(Mandatory)] [bool] $SkipSchema,
        [Parameter(Mandatory)] [bool] $IsPackagedSchema
    )

    $reasons = @()
    if (-not $HasMsiPath) { $reasons += 'build-msi.ps1 (-MsiPath не задано)' }
    if (-not $SkipSchema -and -not $IsPackagedSchema) {
        $reasons += 'dotnet ef migrations script (схема не з пакета, без -SkipSchema)'
    }

    return [pscustomobject]@{ Required = ($reasons.Count -gt 0); Reasons = [string[]] $reasons }
}

# ⛔ S11: відбиток у тому вигляді, в якому його шукає застосунок
# (`AuthenticationSetup.FindCertificate`): без пробілів і нерозривних пробілів —
# з вікна сертифіката Windows його копіюють групами по два символи.
function ConvertTo-NormalizedThumbprint {
    param([string] $Thumbprint)
    if (-not $Thumbprint) { return '' }
    return (-join ($Thumbprint.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) })).ToUpperInvariant()
}

# ⚠ Чиста функція (як Get-ReadinessVerdict): рішення кроку 1 про сертифікат
# Data Protection перевіряється на готовому об'єкті, без сховища сертифікатів.
# Вхід — нормалізований відбиток і знайдений сертифікат ($null — не знайдено).
# Вихід — текст причини відмови або $null, якщо все гаразд.
function Get-DataProtectionCertificateProblem {
    param(
        [string] $Thumbprint,
        $Certificate
    )

    if (-not $Thumbprint) {
        return ("-DataProtectionThumbprint не задано. Служба працює в Production, а там без сертифіката " +
            "застосунок не стартує (S11): ключі DataProtection у sec.DataProtectionKey у відкритому вигляді " +
            "дали б кожному, хто читає базу чи бекап, підробити сеанс будь-якого користувача. Постав сертифікат " +
            "із закритим ключем у Cert:\LocalMachine\My (ОДИН на всі вузли) і передай його відбиток.")
    }
    if (-not $Certificate) {
        return "Сертифіката з відбитком $Thumbprint немає в Cert:\LocalMachine\My. Імпортуй PFX (із закритим ключем) у сховище машини."
    }
    if (-not $Certificate.HasPrivateKey) {
        return ("Сертифікат $Thumbprint у Cert:\LocalMachine\My без закритого ключа (HasPrivateKey = False): " +
            "ним можна зашифрувати ключі кільця, але не розшифрувати — жоден сеанс не відкриється. Імпортуй PFX із закритим ключем.")
    }
    return $null
}

# ⛔ D14-08/R-01 (чиста функція): сертифікат HTTPS за відбитком. Провайдер
# ін'єктується (на живому скрипті — Cert:\LocalMachine\My, у тесті — фікстури),
# тож рішення перевіряється без сховища Windows. Вхід — нормалізований відбиток
# (ConvertTo-NormalizedThumbprint), провайдер `{ param($thumbprint) … }` (порожньо —
# не знайдено) і поточний час. Вихід — Code ('Ok' або код відмови), Problem (текст
# відмови або $null), Warning (текст або $null), Certificate. Відбиток у тексти НЕ
# потрапляє: журнал розгортання його зайвий раз не друкує.
function Get-HttpsCertificateProblem {
    param(
        [string] $Thumbprint,
        [Parameter(Mandatory)] [scriptblock] $Provider,
        [Parameter(Mandatory)] [datetime] $Now
    )

    function New-CertificateVerdict([string] $Code, $Problem, $Warning, $Certificate) {
        return [pscustomobject]@{ Code = $Code; Problem = $Problem; Warning = $Warning; Certificate = $Certificate }
    }

    if ($Thumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
        return New-CertificateVerdict 'BadFormat' ("-HttpsThumbprint: очікується відбиток із 40 шістнадцяткових символів " +
            "(SHA-1, як у вікні сертифіката Windows або Get-ChildItem Cert:\LocalMachine\My).") $null $null
    }

    $certificate = & $Provider $Thumbprint
    if (-not $certificate) {
        return New-CertificateVerdict 'NotFound' ("Сертифіката HTTPS із заданим відбитком немає в Cert:\LocalMachine\My. " +
            "Імпортуй PFX (із закритим ключем) у сховище МАШИНИ (не поточного користувача).") $null $null
    }
    if (-not $certificate.HasPrivateKey) {
        return New-CertificateVerdict 'NoPrivateKey' ("Сертифікат HTTPS у Cert:\LocalMachine\My без закритого ключа (HasPrivateKey = False): " +
            "TLS ним не підняти. Імпортуй PFX із закритим ключем.") $null $null
    }
    if ($certificate.NotAfter -lt $Now) {
        return New-CertificateVerdict 'Expired' ("Сертифікат HTTPS прострочений (діяв до $($certificate.NotAfter.ToString('yyyy-MM-dd'))): " +
            "браузери відмовляться відкривати застосунок. Постав чинний сертифікат.") $null $null
    }
    if ($certificate.NotBefore -gt $Now) {
        return New-CertificateVerdict 'NotYetValid' ("Сертифікат HTTPS ще не чинний (діє з $($certificate.NotBefore.ToString('yyyy-MM-dd'))). " +
            "Перевір годинник сервера або постав чинний сертифікат.") $null $null
    }

    $warning = $null
    if (($certificate.NotAfter - $Now).TotalDays -lt 30) {
        $warning = "Сертифікат HTTPS спливає менш ніж за 30 днів ($($certificate.NotAfter.ToString('yyyy-MM-dd'))): заздалегідь плануй заміну й повтор deploy-ecr.ps1 з новим -HttpsThumbprint."
    }
    return New-CertificateVerdict 'Ok' $null $warning $certificate
}

# ⛔ D14-08/R-01 (чиста функція): що писати в Environment служби EcrApi для обраного
# транспорту. Обрано має бути РІВНО ОДИН режим — жодного HTTP «за замовчуванням»:
#   Https — `-HttpsThumbprint`: https://+:AppPort (+ http-порт перенаправлення), RequireHttps=true;
#   Proxy — `-BehindHttpsProxy`: http, RequireHttps=true (TLS завершує проксі, cookie Secure);
#   Http  — `-AllowHttp`: http, RequireHttps=false + попередження (лише стенд).
# Вхід — нормалізований відбиток ($null/'' — не задано). Вихід — Mode, Set (ім'я → значення),
# Remove (імена, що мають зникнути з Environment — лишки попереднього режиму), Warnings,
# ProbeScheme, Code/Problem (відмова: режиму не обрано, обрано кілька, некоректні порти).
# `ECR_Auth__RequireHttps` пишеться ЗАВЖДИ явно: стан командного рядка = бажаний стан, тож
# `false` від попереднього -AllowHttp не переживе перерозгортання з HTTPS.
function Resolve-TransportConfig {
    param(
        [string] $HttpsThumbprint,
        [switch] $BehindHttpsProxy,
        [switch] $AllowHttp,
        [Parameter(Mandatory)] [int] $AppPort,
        [int] $HttpRedirectPort = 0
    )

    function New-TransportVerdict([string] $Mode, $Set, [string[]] $Remove, [string[]] $Warnings, [string] $Scheme, [string] $Code, $Problem) {
        return [pscustomobject]@{ Mode = $Mode; Set = $Set; Remove = [string[]] $Remove; Warnings = [string[]] $Warnings
            ProbeScheme = $Scheme; Code = $Code; Problem = $Problem }
    }

    $chosen = 0
    if ($HttpsThumbprint) { $chosen++ }
    if ($BehindHttpsProxy) { $chosen++ }
    if ($AllowHttp) { $chosen++ }

    if ($chosen -eq 0) {
        return New-TransportVerdict '' ([ordered]@{}) @() @() '' 'NoTransport' ("Транспорт не обрано. Задай РІВНО ОДИН: " +
            "-HttpsThumbprint <відбиток сертифіката в Cert:\LocalMachine\My> (HTTPS, рекомендовано), " +
            "-BehindHttpsProxy (TLS завершується на проксі перед застосунком) або -AllowHttp (лише стенд: cookie сеансу " +
            "не Secure). Без цього вхід з інших машин не працюватиме: cookie Secure по HTTP не відсилається. " +
            "Параметр потрібен і на кожному оновленні. Деталі — docs/build/11-install-guide.md, розділ «HTTPS».")
    }
    if ($chosen -gt 1) {
        return New-TransportVerdict '' ([ordered]@{}) @() @() '' 'Ambiguous' ("-HttpsThumbprint, -BehindHttpsProxy і -AllowHttp взаємовиключні — обери одне.")
    }
    if ($AppPort -lt 1 -or $AppPort -gt 65535) {
        return New-TransportVerdict '' ([ordered]@{}) @() @() '' 'BadPort' "-AppPort $AppPort поза 1..65535."
    }
    if ($HttpRedirectPort -gt 0 -and -not $HttpsThumbprint) {
        return New-TransportVerdict '' ([ordered]@{}) @() @() '' 'RedirectWithoutHttps' "-HttpRedirectPort має сенс лише з -HttpsThumbprint: без HTTPS перенаправляти нікуди."
    }
    if ($HttpRedirectPort -gt 0 -and $HttpRedirectPort -eq $AppPort) {
        return New-TransportVerdict '' ([ordered]@{}) @() @() '' 'RedirectPortEqualsAppPort' "-HttpRedirectPort збігається з -AppPort ($AppPort): два протоколи на одному порту неможливі."
    }

    $urlsName = 'ASPNETCORE_URLS'
    $thumbName = 'ECR_Transport__Https__CertificateThumbprint'
    $portName = 'ECR_Transport__Https__Port'
    $requireName = 'ECR_Auth__RequireHttps'
    $set = [ordered]@{}
    $warnings = @()

    if ($HttpsThumbprint) {
        $urls = "https://+:$AppPort"
        if ($HttpRedirectPort -gt 0) {
            $urls += ";http://+:$HttpRedirectPort"
            $set[$portName] = [string] $AppPort
            $warnings += ("MSI відкриває в брандмауері лише порт ${AppPort}: для http-порту перенаправлення $HttpRedirectPort правило " +
                "додай сам (New-NetFirewallRule -DisplayName 'ECR redirect' -Direction Inbound -Protocol TCP -LocalPort $HttpRedirectPort -Action Allow).")
        }
        $set[$urlsName] = $urls
        $set[$thumbName] = $HttpsThumbprint
        $set[$requireName] = 'true'
        # ⚠ Не `$remove = if … { @() }`: порожній масив із if розгортається в $null.
        $remove = @()
        if ($HttpRedirectPort -le 0) { $remove = @($portName) }
        return New-TransportVerdict 'Https' $set $remove $warnings 'https' 'Ok' $null
    }

    $set[$urlsName] = "http://+:$AppPort"
    $remove = @($thumbName, $portName)

    if ($BehindHttpsProxy) {
        $set[$requireName] = 'true'
        $warnings += ("-BehindHttpsProxy: TLS має завершуватись на проксі; Kestrel слухає http на всіх інтерфейсах порту $AppPort — " +
            "закрий його брандмауером для всіх, крім проксі. Застосунок не довіряє X-Forwarded-*: HSTS і перенаправлення http→https — " +
            "на проксі. Без проксі вхід не працюватиме (cookie Secure).")
        return New-TransportVerdict 'Proxy' $set $remove $warnings 'http' 'Ok' $null
    }

    $set[$requireName] = 'false'
    $warnings += ("-AllowHttp: служба працює по HTTP, cookie сеансу НЕ Secure (Auth:RequireHttps = false) — пароль і сеанс ідуть " +
        "відкритим текстом. Це режим СТЕНДА; на майданчику замовника постав сертифікат і використай -HttpsThumbprint. " +
        "Застосунок про це скаже на старті й жовтою перевіркою transport на /health/ready.")
    return New-TransportVerdict 'Http' $set $remove $warnings 'http' 'Ok' $null
}

# ⛔ D14-08/R-01: мережева половина кроку 7 для режиму Https. Invoke-WebRequest тут не годиться:
# сертифікат виписано на ім'я сервера, а зонд іде на localhost, — PowerShell 5.1 не вміє «пропустити
# перевірку» без глобального ServicePointManager (чіпати його в процесі майстра не можна), а
# scriptblock-колбек HttpClient виконується на потоці без runspace. Тому — SslStream зі
# синхронним колбеком і ПРИШПИЛЕННЯМ: приймається лише сертифікат із заданим відбитком, тобто зонд
# заодно доводить, що Kestrel віддає саме той сертифікат, який задав адміністратор. HTTP/1.0 — щоб
# відповідь не була chunked (тіло — усе після порожнього рядка). Повертає ту саму форму, що
# Invoke-ReadyProbe, плюс CertificateMatches ($null — рукостискання не відбулося).
function Invoke-PinnedHttpsProbe {
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Thumbprint,
        [int] $TimeoutSeconds = 10
    )

    $result = [pscustomobject]@{ StatusCode = 0; Body = $null; CertificateMatches = $null }
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ssl = $null
    try {
        $tcp.ReceiveTimeout = $TimeoutSeconds * 1000
        $tcp.SendTimeout = $TimeoutSeconds * 1000
        $tcp.Connect('127.0.0.1', $Port)

        $accept = [System.Net.Security.RemoteCertificateValidationCallback] { param($sender, $certificate, $chain, $errors) $true }
        $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false, $accept)
        $ssl.AuthenticateAsClient('localhost', (New-Object System.Security.Cryptography.X509Certificates.X509CertificateCollection),
            [System.Security.Authentication.SslProtocols]::Tls12, $false)

        $served = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        $result.CertificateMatches = ($served.Thumbprint -eq $Thumbprint.ToUpperInvariant())
        if (-not $result.CertificateMatches) { return $result }

        $request = [System.Text.Encoding]::ASCII.GetBytes("GET $Path HTTP/1.0`r`nHost: localhost`r`nConnection: close`r`n`r`n")
        $ssl.Write($request, 0, $request.Length)
        $ssl.Flush()

        $buffer = New-Object System.IO.MemoryStream
        $ssl.CopyTo($buffer)
        $text = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray())
        $headerEnd = $text.IndexOf("`r`n`r`n")
        $lineEnd = $text.IndexOf("`r`n")
        if ($headerEnd -ge 0 -and $lineEnd -ge 0) {
            $result.StatusCode = [int] (($text.Substring(0, $lineEnd) -split ' ')[1])
            $result.Body = $text.Substring($headerEnd + 4)
        }
    }
    catch { $result.StatusCode = 0 }
    finally {
        if ($ssl) { $ssl.Dispose() }
        $tcp.Dispose()
    }
    return $result
}

# ⚠ ПЕРЕДУМОВИ — до будь-якої зміни системи (дешевша відмова тут, ніж на
# кроці 3 з наполовину встановленою службою).
Write-Step "Крок 1/7: передумови"

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd не знайдено. Ним DBA виконує розгортання — без нього продовжувати нема сенсу."
}
# ⛔ .NET SDK потрібен лише там, де його реально кличуть (Get-DotnetRequirement):
# на чистому сервері з пакованою схемою і готовим MSI його немає й бути не мусить
# (install-guide §2.1, Q-219) — безумовна вимога тут зупиняла б саме цей сценарій.
$dotnetNeed = Get-DotnetRequirement -HasMsiPath ([bool] $MsiPath) -SkipSchema ([bool] $SkipSchema) `
    -IsPackagedSchema ([bool] $isPackagedSchema)
if ($dotnetNeed.Required -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw (".NET SDK не знайдено, а він потрібен для: $($dotnetNeed.Reasons -join '; '). " +
        "На чистому сервері передай готовий -MsiPath і запускай із пакета майстра (sql\ і migration.sql поруч зі скриптом) " +
        "або -SkipSchema, якщо схему вже накотив DBA.")
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
if ($EnableWorker -and $DisableWorker) {
    throw '-EnableWorker і -DisableWorker разом — оберіть одне.'
}
if (-not $ConnectionString) {
    Write-Warning ("-ConnectionString не задано — застосунок впаде з InvalidOperationException " +
        "(D-11) при першій спробі стартувати, поки ECR_ConnectionStrings__Ecr не буде додано " +
        "вручну в HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment.")
}

# ⛔ S11: сертифікат Data Protection — ДО схеми й MSI. Служба без нього в
# Production не стартує, і дізнатися про це на кроці 6 означало б уже
# встановлену, але мертву службу. Лише читання сховища — тому й під -WhatIf.
$DataProtectionThumbprint = ConvertTo-NormalizedThumbprint $DataProtectionThumbprint
$dataProtectionCertificate = $null
if ($DataProtectionThumbprint) {
    $certPath = "Cert:\LocalMachine\My\$DataProtectionThumbprint"
    if (Test-Path $certPath) { $dataProtectionCertificate = Get-Item $certPath }
}
$certificateProblem = Get-DataProtectionCertificateProblem -Thumbprint $DataProtectionThumbprint `
    -Certificate $dataProtectionCertificate
if ($certificateProblem) { throw $certificateProblem }
Write-Host ("Сертифікат Data Protection: $DataProtectionThumbprint ($($dataProtectionCertificate.Subject)), " +
    "закритий ключ є. Обліковому запису служби потрібне право читання закритого ключа.") -ForegroundColor Green

# ⛔ D14-08/R-01: транспорт — ТАКОЖ до схеми й MSI. Без вибору (HTTPS, проксі чи явний
# -AllowHttp) зупинка: мовчки поставлений HTTP дав би службу, у яку не можна увійти з
# жодної іншої машини (cookie Secure по HTTP не відсилається). Сертифікат HTTPS —
# наявність, закритий ключ, строк; лише читання сховища, тому й під -WhatIf.
$HttpsThumbprint = ConvertTo-NormalizedThumbprint $HttpsThumbprint
$transport = Resolve-TransportConfig -HttpsThumbprint $HttpsThumbprint -BehindHttpsProxy:$BehindHttpsProxy `
    -AllowHttp:$AllowHttp -AppPort $AppPort -HttpRedirectPort $HttpRedirectPort
if ($transport.Problem) { throw $transport.Problem }

if ($transport.Mode -eq 'Https') {
    $httpsCheck = Get-HttpsCertificateProblem -Thumbprint $HttpsThumbprint -Now (Get-Date) -Provider {
        param($thumbprint)
        $path = "Cert:\LocalMachine\My\$thumbprint"
        if (Test-Path $path) { Get-Item $path }
    }
    if ($httpsCheck.Problem) { throw $httpsCheck.Problem }
    if ($httpsCheck.Warning) { Write-Warning $httpsCheck.Warning }
    Write-Host ("Сертифікат HTTPS: $($httpsCheck.Certificate.Subject), діє до " +
        "$($httpsCheck.Certificate.NotAfter.ToString('yyyy-MM-dd')), закритий ключ є. " +
        "Обліковому запису служби потрібне право читання закритого ключа.") -ForegroundColor Green
}
Write-Host "Транспорт: $($transport.Mode) (ASPNETCORE_URLS = $($transport.Set['ASPNETCORE_URLS']))."
foreach ($warning in $transport.Warnings) { Write-Warning $warning }

$sqlAuth = if ($SqlLogin) { @('-U', $SqlLogin) } else { @('-E') }

# ⚠ Чиста функція (як Merge-ServiceEnvironmentEntry): без мережі, щоб рішення
# кроку 7 перевірялося на готових відповідях (D-134), а не лише на живому стенді.
# Вхід — код відповіді /health/ready і тіло (HealthReportDto: status, checks[]).
# Вихід — Outcome: 'Ready' (Healthy/Degraded), 'Warning' (Unhealthy ЛИШЕ через
# `sources`), 'Wait' (ще не готово — або відповіді немає, або Unhealthy інша);
# Status — загальний статус; Details — рядки про перевірки, що не Healthy.
#
# ⚠ `sources` — не провал розгортання: це зовнішній PI/SQL, недоступний з
# причин поза цим сервером, і ручне введення без нього працює (аудит, п. 7).
function Get-ReadinessVerdict {
    param(
        [int] $StatusCode,
        [string] $Body
    )

    $details = @()
    if ($StatusCode -eq 0 -or [string]::IsNullOrWhiteSpace($Body)) {
        return [pscustomobject]@{ Outcome = 'Wait'; Status = 'немає відповіді'; Details = $details }
    }

    try { $report = $Body | ConvertFrom-Json -ErrorAction Stop }
    catch { return [pscustomobject]@{ Outcome = 'Wait'; Status = "HTTP $StatusCode, тіло не JSON"; Details = $details } }

    # ⚠ Set-StrictMode Latest: звернення до відсутньої властивості — виняток.
    $status = if ($report.PSObject.Properties['status']) { [string] $report.status } else { '' }
    $checks = @()
    if ($report.PSObject.Properties['checks'] -and $report.checks) { $checks = @($report.checks) }

    foreach ($check in $checks) {
        if ([string] $check.status -ne 'Healthy') {
            $details += "$($check.name): $($check.status) — $($check.description)"
        }
    }

    $outcome = switch ($status) {
        'Healthy'  { 'Ready' }
        'Degraded' { 'Ready' }
        'Unhealthy' {
            $failing = @($checks | Where-Object { [string] $_.status -eq 'Unhealthy' } | ForEach-Object { [string] $_.name })
            if ($failing.Count -gt 0 -and @($failing | Where-Object { $_ -ne 'sources' }).Count -eq 0) { 'Warning' } else { 'Wait' }
        }
        default { 'Wait' }
    }

    return [pscustomobject]@{ Outcome = $outcome; Status = $status; Details = $details }
}

# Мережева половина кроку 7: код і тіло /health/ready, зокрема при 503 —
# Invoke-WebRequest на 503 кидає виняток, а звіт перевірок лежить саме в тілі.
function Invoke-ReadyProbe {
    param([Parameter(Mandatory)] [string] $Url)

    try {
        $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 10
        return [pscustomobject]@{ StatusCode = [int] $resp.StatusCode; Body = [string] $resp.Content }
    }
    catch {
        $code = 0
        $body = $null
        $webResponse = $null
        if ($_.Exception.PSObject.Properties['Response']) { $webResponse = $_.Exception.Response }
        if ($webResponse) {
            $code = [int] $webResponse.StatusCode
            # ⚠ Windows PowerShell 5.1: ErrorDetails.Message на 503 порожній
            # (перевірено живим HttpListener), тіло є лише в потоці відповіді —
            # і читати його треба явно як UTF-8, бо описи перевірок кириличні.
            if ($webResponse -is [System.Net.HttpWebResponse]) {
                try {
                    $stream = $webResponse.GetResponseStream()
                    if ($stream.CanSeek) { $stream.Position = 0 }
                    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
                    try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
                }
                catch { $body = $null }
            }
            if ([string]::IsNullOrWhiteSpace($body) -and $_.ErrorDetails -and $_.ErrorDetails.Message) {
                $body = $_.ErrorDetails.Message                     # PowerShell 7
            }
        }
        return [pscustomobject]@{ StatusCode = $code; Body = $body }
    }
}

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
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & sqlcmd @arguments
        }
        finally {
            $ErrorActionPreference = $previousEap
        }
        if ($LASTEXITCODE -ne 0) {
            throw "sqlcmd повернув $LASTEXITCODE на ${what}: файли до цього застосовані, ${what} і все після — ні."
        }
    }
}

# Запит із результатом (рядки виводу sqlcmd без заголовків, стовпці через '|').
# Під ShouldProcess, як і Invoke-DeploySql: -WhatIf — жодного sqlcmd. $null,
# якщо не виконувався.
function Invoke-DeployQuery {
    param(
        [Parameter(Mandatory)] [string] $TargetDb,
        [Parameter(Mandatory)] [string] $Query
    )

    $arguments = @('-S', $SqlInstance) + $sqlAuth + @('-C', '-b', '-I', '-h', '-1', '-W', '-s', '|', '-d', $TargetDb, '-Q', $Query)
    if (-not $PSCmdlet.ShouldProcess("$SqlInstance / $TargetDb", "sqlcmd -Q $Query")) { return $null }

    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & sqlcmd @arguments
    }
    finally {
        $ErrorActionPreference = $previousEap
    }
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd повернув $LASTEXITCODE на запиті: $Query`n$($output -join "`n")"
    }
    return , @($output | Where-Object { $_ -and $_.Trim() })
}

$detectedEdition = $null

try {
    # ⛔ Q-232: пароль виставляється ПЕРЕД першим-ліпшим викликом sqlcmd,
    # не після. Перевірка з'єднання нижче так само потребує автентифікації,
    # коли задано -SqlLogin, — раніше пароль з'являвся аж на кроці схеми, і
    # ця сама перевірка при SQL-автентифікації впала б без нього.
    if ($SqlPassword) { $env:SQLCMDPASSWORD = ConvertFrom-SecureStringPlain $SqlPassword }

    # Перевірка з'єднання — читає, нічого не змінює, але й вона під ShouldProcess:
    # контракт -WhatIf каже прямо «жодного sqlcmd», без винятків для читання.
    Invoke-DeploySql -TargetDb 'master' -Query 'SELECT 1;'

    # ── Редакція і версія SQL Server (D-206: система підлаштовується під
    # Standard або Enterprise, визначається під час інсталяції). До будь-якої
    # зміни: непридатний сервер зупиняє розгортання тут, а не на 01-filegroups.
    $editionRows = Invoke-DeployQuery -TargetDb 'master' -Query (
        "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('EngineEdition') AS int), " +
        "CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), " +
        "CAST(SERVERPROPERTY('ProductMajorVersion') AS nvarchar(16)), " +
        "CAST(SERVERPROPERTY('Edition') AS nvarchar(128));")
    if ($null -eq $editionRows) {
        Write-Host "  Редакцію SQL Server буде визначено запитом SERVERPROPERTY (-WhatIf: не виконується)." -ForegroundColor DarkGray
    }
    else {
        $fields = ([string] $editionRows[0]).Split('|')
        if ($fields.Count -lt 4) { throw "Неочікувана відповідь на запит редакції: $($editionRows -join ' / ')" }
        $detectedEdition = Resolve-SqlEdition -EngineEdition ([int] $fields[0].Trim()) `
            -ProductVersion $fields[1].Trim() -Edition $fields[3].Trim() -AllowExpress:$AllowExpress

        Write-Host ("  SQL Server: $($detectedEdition.Name), версія $($fields[1].Trim()) " +
            "(ProductMajorVersion $($fields[2].Trim()), EngineEdition $($fields[0].Trim()), «$($fields[3].Trim())»).")
        if ($detectedEdition.Note) { Write-Host "  $($detectedEdition.Note)" -ForegroundColor Yellow }
        if ($detectedEdition.Stop) { throw $detectedEdition.Stop }
    }

    if ($CreateDatabaseIfMissing) {
        # ⛔ Q-232 (директива людини, 2026-09-11): раніше відсутня база
        # ЗАВЖДИ зупиняла скрипт — адміністратор БД мав створити її
        # заздалегідь (`docs/build/11-install-guide.md` §0). Людина, що
        # запускає інсталятор, прямо дозволила автоматичне створення для
        # випадку, коли вона сама має право CREATE DATABASE. `sp_executesql`
        # — CREATE DATABASE не можна умовно виконати в тому самому пакеті,
        # що й IF (обмеження SQL Server), тому він — окремий динамічний
        # пакет усередині IF. Collation фіксовано тут же (02a §1.0) — не
        # лишається на волю дефолту інстансу; 01-filegroups.sql далі лише
        # ПЕРЕВІРЯЄ його, не задає.
        Invoke-DeploySql -TargetDb 'master' -Query @"
IF DB_ID('$Database') IS NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(N'$Database') + N' COLLATE Latin1_General_100_CI_AS_SC;';
    EXEC sp_executesql @sql;
END
"@
    }
    else {
        Invoke-DeploySql -TargetDb 'master' -Query "IF DB_ID('$Database') IS NULL RAISERROR('database missing', 16, 1);"
    }

    # ---------------------------------------------------------------------
    if ($SkipSchema) {
        Write-Step "Крок 2/7: схема — ПРОПУЩЕНО (-SkipSchema)"
    }
    else {
        Write-Step "Крок 2/7: схема ($Database на $SqlInstance)"

        if ($isPackagedSchema) {
            Write-Host "  migration.sql уже в пакеті — dotnet ef не викликається (немає SDK на чистому сервері)." -ForegroundColor DarkGray
        }
        elseif ($PSCmdlet.ShouldProcess($migration, 'dotnet ef migrations script --idempotent')) {
            New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
            Invoke-NativeStep "dotnet ef migrations script" {
                dotnet ef migrations script --idempotent `
                    --project (Join-Path $root 'src\Ecr.Infrastructure') `
                    --startup-project (Join-Path $root 'src\Ecr.Infrastructure') `
                    --output $migration
            }
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
            '04-partition-maintenance.sql', '05-rpt-views.sql', '15-cell-tvp.sql',
            '10-triggers.sql', '06-rcsi.sql'
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

# ── Воркер перерахунку (I2-2): рішення ДО msiexec — від нього залежать і
# WORKER_ENABLED, і режим Api на кроці 5.
$workerDecision = Resolve-WorkerDeployment -EnableWorker:$EnableWorker -DisableWorker:$DisableWorker `
    -EditionName $(if ($detectedEdition) { $detectedEdition.Name } else { $null })
$workerEnabled = [bool] $workerDecision.Enabled
Write-Host ("Воркер перерахунку (EcrWorker): $(if ($workerEnabled) { 'так' } else { 'ні' }) — $($workerDecision.Reason).")

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

# ⚠ WORKER_ENABLED передається ЗАВЖДИ, і 0 теж: MSI не пам'ятає властивість між
# установками (Worker.wxs), тож стан командного рядка = бажаний стан. Вимкнений
# воркер при наявній службі — MSI її прибирає; кажемо вголос.
$workerFlag = if ($workerEnabled) { '1' } else { '0' }
$msiArgs      += "WORKER_ENABLED=$workerFlag"
$msiArgsShown += "WORKER_ENABLED=$workerFlag"
if (-not $workerEnabled -and (Get-Service -Name EcrWorker -ErrorAction SilentlyContinue)) {
    Write-Warning ("Служба EcrWorker зараз встановлена, а воркер вимкнено ($($workerDecision.Reason)): MSI її ПРИБЕРЕ, " +
        "а EcrApi перейде на Jobs:Recalculation:Executor = InProcess (крок 5).")
}
if ($ServicePassword) {
    $msiArgs      += "SERVICE_PASSWORD=$(ConvertFrom-SecureStringPlain $ServicePassword)"
    $msiArgsShown += 'SERVICE_PASSWORD=***'   # ніколи не в плані/логу, лише в реальному виклику
}

if ($PSCmdlet.ShouldProcess($MsiPath, "msiexec $($msiArgsShown -join ' ')")) {
    $proc = Start-Process msiexec -ArgumentList $msiArgs -Wait -PassThru
    if ($proc.ExitCode -notin 0, 3010) { throw "msiexec повернув $($proc.ExitCode) — див. ecr-install.log" }

    # ⛔ I2-2: режим Api (крок 5) пишеться за ФАКТОМ служби, а не за наміром.
    # Служби немає після WORKER_ENABLED=1 — зупинка тут, до запису Executor = Worker.
    if ($workerEnabled -and -not (Get-Service -Name EcrWorker -ErrorAction SilentlyContinue)) {
        throw ("Служби EcrWorker немає після msiexec з WORKER_ENABLED=1 — див. ecr-install.log. " +
            "Режим перерахунку EcrApi не змінено. Без воркера — повтори з -DisableWorker.")
    }
}

# ---------------------------------------------------------------------
Write-Step "Крок 4/7: секрети служби (реєстр EcrApi\Environment)"

if (-not $ConnectionString) {
    Write-Host ("ECR_ConnectionStrings__Ecr не записано (-ConnectionString не задано) — " +
        "служба впаде при старті, поки значення не буде додано вручну.") -ForegroundColor Yellow
}
else {
    if ($PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment',
            'записати ECR_ConnectionStrings__Ecr')) {
        Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name 'ECR_ConnectionStrings__Ecr' `
            -Value (ConvertFrom-SecureStringPlain $ConnectionString)
        Write-Host "Рядок підключення записано." -ForegroundColor Green
    }

    # Воркер ходить у ту саму базу тим самим рядком (Worker.wxs: той самий
    # обліковий запис) — той самий канал, окремий ключ служби.
    if ($workerEnabled -and $PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrWorker\Environment',
            'записати ECR_ConnectionStrings__Ecr')) {
        Set-ServiceEnvironmentVariable -ServiceName 'EcrWorker' -Name 'ECR_ConnectionStrings__Ecr' `
            -Value (ConvertFrom-SecureStringPlain $ConnectionString)
        Write-Host "Рядок підключення записано й для EcrWorker." -ForegroundColor Green
    }
}

# ⛔ Q-221: без цього Kestrel слухає лише вбудований дефолт ASP.NET Core —
# http://localhost:$AppPort — тобто ЛИШЕ loopback. Інсталятор відкриває
# правило брандмауера на $AppPort (Package.wxs, APP_PORT), а
# 11-install-guide.md §3 каже відкрити http://<сервер>:$AppPort/ З ІНШОЇ
# машини — без цього запису порт відкритий, а слухати його нікому. Той
# самий канал, що ECR_ConnectionStrings__Ecr (реєстр служби), і той самий
# принцип: ASPNETCORE_URLS — не секрет, ASP.NET Core читає його як
# стандартну змінну оточення без жодного коду в Program.cs.
#
# ⛔ D14-08/R-01: адреси, режим HTTPS і Auth:RequireHttps пише Resolve-TransportConfig
# (рішення прийнято на кроці 1). Відбиток сертифіката HTTPS — не секрет, але й не
# друкується: лише імена змінних.
foreach ($name in $transport.Set.Keys) {
    if ($PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment', "записати $name")) {
        Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name $name -Value $transport.Set[$name]
    }
}
foreach ($name in $transport.Remove) {
    if ($PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment', "прибрати $name")) {
        Remove-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name $name
    }
}
Write-Host ("Транспорт записано ($($transport.Mode)): ASPNETCORE_URLS = $($transport.Set['ASPNETCORE_URLS']), " +
    "ECR_Auth__RequireHttps = $($transport.Set['ECR_Auth__RequireHttps']) — служба слухає всі інтерфейси, не лише localhost.") -ForegroundColor Green

# ⛔ S11: відбиток сертифіката Data Protection — тим самим каналом (реєстр
# служби), що й ASPNETCORE_URLS. Не секрет (відбиток — це хеш публічного
# сертифіката), але без нього служба в Production не стартує.
if ($PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment',
        'записати ECR_Auth__DataProtection__CertificateThumbprint')) {
    Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name 'ECR_Auth__DataProtection__CertificateThumbprint' `
        -Value $DataProtectionThumbprint
    Write-Host "ECR_Auth__DataProtection__CertificateThumbprint записано ($DataProtectionThumbprint)." -ForegroundColor Green
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

# ── Режим редакції (Database:EditionMode). ПІСЛЯ запису файлу вище: значення
# з -ConfigValues теж явне, і його має бути видно рішенню нижче.
# ⚠ -WhatIf: файл не записано — рішення показує стан ДО розгортання.
$editionDecision = Resolve-EditionModeWrite -Explicit $EditionMode `
    -Detected $(if ($detectedEdition) { $detectedEdition.Mode } else { $null }) `
    -FileValue (Get-ConfiguredEditionMode -Path $configPath) `
    -EnvironmentValue (Get-ServiceEnvironmentValue -ServiceName 'EcrApi' -Name 'ECR_Database__EditionMode')

if (-not $editionDecision.Write) {
    Write-Host "Database:EditionMode: $($editionDecision.Reason)." -ForegroundColor DarkGray
}
else {
    $editionServices = @('EcrApi') + $(if ($workerEnabled) { @('EcrWorker') } else { @() })
    foreach ($serviceName in $editionServices) {
        if ($PSCmdlet.ShouldProcess("HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName\Environment",
                "записати ECR_Database__EditionMode=$($editionDecision.Value)")) {
            Set-ServiceEnvironmentVariable -ServiceName $serviceName -Name 'ECR_Database__EditionMode' `
                -Value $editionDecision.Value
        }
    }
    Write-Host "Database:EditionMode = $($editionDecision.Value) ($($editionDecision.Reason))." -ForegroundColor Green
}

# ── Режим перерахунку EcrApi (I2-2) — за фактом служби EcrWorker (крок 3 уже
# перевірив, що вона є, коли $workerEnabled). ПІСЛЯ запису файлу — з тієї ж
# причини, що й EditionMode: значення з -ConfigValues теж явне.
$jobDecision = Resolve-JobExecutionConfig -WorkerEnabled $workerEnabled `
    -FileMode (Get-ConfiguredValue -Path $configPath -Keys 'Jobs', 'Queue', 'Mode') `
    -FileExecutor (Get-ConfiguredValue -Path $configPath -Keys 'Jobs', 'Recalculation', 'Executor')

foreach ($warning in $jobDecision.Warnings) { Write-Warning $warning }
foreach ($name in $jobDecision.Set.Keys) {
    if ($PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment',
            "записати $name=$($jobDecision.Set[$name])")) {
        Set-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name $name -Value $jobDecision.Set[$name]
    }
}
foreach ($name in $jobDecision.Remove) {
    if ($PSCmdlet.ShouldProcess('HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment', "прибрати $name")) {
        Remove-ServiceEnvironmentVariable -ServiceName 'EcrApi' -Name $name
    }
}
Write-Host ("Перерахунок: " + $(if ($workerEnabled) { 'служба EcrWorker (Jobs:Queue:Mode = Database, Executor = Worker).' }
        else { 'у процесі EcrApi (Executor = InProcess).' })) -ForegroundColor Green

# ---------------------------------------------------------------------
Write-Step "Крок 6/7: старт служби"

if (-not $ServiceAccount) {
    Write-Host "SERVICE_ACCOUNT не задано — служба зареєстрована, але не стартує (навмисно, docs/build/10-installer.md §1.4)." -ForegroundColor Yellow
    if ($workerEnabled) {
        Write-Host ("  ⚠ EcrWorker теж не стартує, а EcrApi вже налаштовано на Executor = Worker: запускай ОБИДВІ служби, " +
            "інакше перерахунок стоятиме в черзі (перевірка worker на /health/ready — Degraded).") -ForegroundColor Yellow
    }
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

    # Воркер — з тієї ж причини безумовний перезапуск: MSI міг підняти його
    # до того, як крок 4 записав рядок підключення.
    if ($workerEnabled -and $PSCmdlet.ShouldProcess('EcrWorker', 'Restart-Service')) {
        $worker = Get-Service -Name EcrWorker -ErrorAction SilentlyContinue
        if (-not $worker) { throw 'Служби EcrWorker немає після msiexec з WORKER_ENABLED=1 — див. ecr-install.log.' }
        Restart-Service -Name EcrWorker -Force

        # Наглядач не має HTTP — «здоров'я» тут лише те, що процес не впав
        # одразу: недійсна конфігурація Jobs:Workers:* дає код 3 за секунди.
        Start-Sleep -Seconds 5
        $worker.Refresh()
        if ($worker.Status -ne 'Running') {
            throw ("EcrWorker не працює (стан $($worker.Status)) через 5 с після старту. " +
                "Причина — журнал подій Application (Get-WinEvent) або ручний запуск " +
                "`"Ecr.Worker.exe --supervisor`" з теки застосунку; вимкнути воркер — docs/admin/operations-runbook.md §10.")
        }
        Write-Host "EcrWorker працює." -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------
Write-Step "Крок 7/7: перевірка здоров'я"

# ⛔ D14-08/R-01: у режимі Https зонд іде по TLS із пришпиленням сертифіката
# (Invoke-PinnedHttpsProbe): відповідь доводить і що служба жива, і що Kestrel віддає
# САМЕ сертифікат, заданий -HttpsThumbprint.
$probeHttps = ($transport.Mode -eq 'Https')
$healthUrl = "$($transport.ProbeScheme)://localhost:$AppPort/health/live"
if ($PSCmdlet.ShouldProcess($healthUrl, 'GET /health/live')) {
    $ok = $false
    for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
        try {
            if ($probeHttps) {
                $probe = Invoke-PinnedHttpsProbe -Port $AppPort -Path '/health/live' -Thumbprint $HttpsThumbprint -TimeoutSeconds 3
                if ($probe.CertificateMatches -eq $false) {
                    throw ("Kestrel віддає ІНШИЙ сертифікат, ніж заданий -HttpsThumbprint: служба працює, але не з тим сертифікатом. " +
                        "Перевір ECR_Transport__Https__CertificateThumbprint в Environment служби EcrApi і журнал старту.")
                }
                $ok = $probe.StatusCode -eq 200
                if (-not $ok) { Start-Sleep -Seconds 3 }
            }
            else {
                $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3
                $ok = $resp.StatusCode -eq 200
            }
        }
        catch {
            if ($_.Exception.Message -like 'Kestrel віддає ІНШИЙ*') { throw }
            Start-Sleep -Seconds 3
        }
    }
    if (-not $ok) { throw "Служба не відповіла на $healthUrl за відведений час. Перевір Event Log (джерело ECR) і %ProgramData%\ECR\logs." }
    Write-Host "Служба відповідає на $healthUrl." -ForegroundColor Green
}

# ⛔ U21: `live` доводить лише, що процес відповідає. Стенд без RCSI, без
# файлових груп чи з мертвим планувальником проходив би далі як «Готово» —
# саме тому після `live` чекаємо `ready` і друкуємо перевірки, що не Healthy.
$readyUrl = "$($transport.ProbeScheme)://localhost:$AppPort/health/ready"
if ($PSCmdlet.ShouldProcess($readyUrl, 'GET /health/ready')) {
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    do {
        $response = if ($probeHttps) {
            Invoke-PinnedHttpsProbe -Port $AppPort -Path '/health/ready' -Thumbprint $HttpsThumbprint
        }
        else { Invoke-ReadyProbe -Url $readyUrl }
        $verdict  = Get-ReadinessVerdict -StatusCode $response.StatusCode -Body $response.Body
        if ($verdict.Outcome -ne 'Wait') { break }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)

    foreach ($line in $verdict.Details) { Write-Host "  $line" -ForegroundColor Yellow }

    switch ($verdict.Outcome) {
        'Ready'   { Write-Host "Служба готова (${readyUrl}: $($verdict.Status))." -ForegroundColor Green }
        'Warning' {
            Write-Warning ("Служба готова до роботи, але $readyUrl — $($verdict.Status) лише через зовнішні " +
                "джерела даних (sources): ручне введення працює, збір — ні. Стан джерел — /admin/sources.")
        }
        default {
            throw ("Служба не стала готовою за $ReadyTimeoutSeconds с (${readyUrl}: $($verdict.Status)). " +
                "Перевірки вище; подробиці БД — /health/db після входу, причини — Event Log (джерело ECR) " +
                "і %ProgramData%\ECR\logs. Типові збої — docs/admin/operations-runbook.md §5.")
        }
    }
}

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
