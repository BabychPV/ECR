<#
.SYNOPSIS
    Готує базу розробника: схема, скрипти, seed і (за бажанням) дані.

.DESCRIPTION
    Скрипт закриває розрив, на який наштовхується кожен, хто вперше тисне F5:
    рядок підключення береться **зі змінної оточення** і в `appsettings.json`
    його немає навмисно (`D-11`, ФВ-6.11). Застосунок при цьому не «мовчки не
    працює», а зупиняється з поясненням — але бази від цього не з'являється.

    Порядок дослівно повторює `09-commands.md` §3 і той самий, яким ставиться
    прод: `01` → `02` → міграції → `11` → `07` → решта. Через `sqlcmd`, тобто
    тим самим клієнтом, що й DBA (`P-17`).

    ⚠ Скрипт ВИДАЛЯЄ базу з указаним іменем і створює її наново. Ім'я за
    замовчуванням — `EcrDev`, а не `Ecr`: сплутати базу розробника з робочою
    не має бути можливості через одну неуважність.

.PARAMETER Server
    Екземпляр SQL Server.

.PARAMETER Database
    Ім'я бази розробника.

.PARAMETER Documents
    Скільки синтетичних документів згенерувати (0 — не генерувати).
    Один документ — це ~90 таблиць × 12 періодів, тобто ~1.7 млн комірок.

.EXAMPLE
    powershell -File tools/setup-dev-db.ps1
    powershell -File tools/setup-dev-db.ps1 -Documents 0
#>
[CmdletBinding()]
param(
    [string] $Server = 'localhost\SQLEXPRESS',
    [string] $Database = 'EcrDev',
    [int] $Documents = 1,

    # ⚠ Пароль bootstrap задається ПАРАМЕТРОМ, бо він діє лише на першому
    # старті (`D-115`): запис уже існує → змінна ігнорується. Скрипт, що
    # викликає цей, мусить знати той самий пароль, інакше не увійде.
    [string] $BootstrapPassword = 'Dev-Bootstrap-2026!',

    # ⚠ Куди класти файли бази. Порожній рядок — типовий каталог інстансу
    # (поведінка до 2026-09-18). Умовчання нижче обирається за наявністю
    # диска: на машині розробки це `H:`, бо типовий каталог інстансу тут
    # лежить на носії, який стенди вибирають до нуля — кожна база важить
    # 14 ГБ (`01-filegroups.sql`, коментар про Developer Edition).
    #
    # ⛔ Це рішення РОЗГОРТАННЯ, а не схеми: у замовника каталог свій, і
    # жодного шляху в `Sql/*.sql` не зашито (`Q-029`).
    [string] $DataPath = $(if (Test-Path 'H:\') { 'H:\EcrData' } else { '' }),

    # ⚠ Скільки вільного місця вимагати на диску даних. `-1` — обчислити за
    # профілем файлових груп (див. `Get-RequiredFreeGb` нижче); `0` — не
    # перевіряти взагалі. Параметр існує не заради тесту самої перевірки, а
    # тому, що профіль задають ПІСЛЯ створення бази (позначка
    # `Ecr_SmallFiles`), і той, хто знає свій профіль, знає число краще.
    [double] $RequireFreeGb = -1
)

$ErrorActionPreference = 'Stop'

# ⛔ Q-217: PowerShell перетворює запис нативної команди в stderr на
# помилку, і $ErrorActionPreference = 'Stop' зупиняє скрипт на ньому
# незалежно від коду виходу (не про $PSNativeCommandUseErrorActionPreference
# — та за замовчуванням і так $false). Єдине надійне джерело істини —
# фактичний код виходу.
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

if ($null -eq (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Write-Error 'sqlcmd не знайдено. Саме ним виконується розгортання.'
}

$root = Split-Path -Parent $PSScriptRoot
$sql = Join-Path $root 'src/Ecr.Infrastructure/Persistence/Sql'
$artifacts = Join-Path $root 'artifacts'
$migration = Join-Path $artifacts 'migration.sql'
$connection = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"

function Invoke-Sql {
    param([string] $Db, [string] $Query, [string] $File)

    $arguments = @('-S', $Server, '-E', '-C', '-b', '-I', '-d', $Db)
    if ($File) { $arguments += @('-i', $File) } else { $arguments += @('-Q', $Query) }

    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # ⛔ Вивід ЗБИРАЄТЬСЯ, а не глушиться в `Out-Null`. Доти відмова
        # виглядала як «sqlcmd повернув 1 на 01-filegroups.sql» — код без
        # причини. 2026-09-18 за цим рядком ховалося
        # `Msg 5149 ... operating system error 112 (There is not enough space
        # on the disk.)`, і три прогони стенда поспіль прочиталися як дефект
        # розгортання, хоча бракувало місця на диску даних SQL Server.
        #
        # ⚠ На успіху вивід так само не показується (його багато й він ні про
        # що), тож звичний прогін не змінюється — змінюється лише те, що видно
        # при падінні.
        $output = & sqlcmd @arguments 2>&1
    }
    finally {
        $ErrorActionPreference = $previousEap
    }
    if ($LASTEXITCODE -ne 0) {
        # ⚠ Останні рядки, а не весь вивід: `sqlcmd` друкує попередження про
        # зіставлення на кожен файл, і справжня помилка йде в кінці.
        $tail = ($output | Select-Object -Last 12) -join [Environment]::NewLine

        throw "sqlcmd повернув $LASTEXITCODE на $(if ($File) { $File } else { $Query })" `
            + [Environment]::NewLine + $tail
    }
}

function Invoke-Script {
    param([string] $Name)

    $path = Join-Path $sql $Name
    if (-not (Test-Path $path)) { throw "Немає ${path}." }

    Write-Host "  $Name"
    Invoke-Sql -Db $Database -File $path
}

# ⛔ Вільне місце перевіряється ПЕРШИМ — раніше за `dotnet ef`, раніше за
# `CREATE DATABASE`. Причина не в акуратності, а в тому, що брак місця тут
# **не схожий на брак місця**. 2026-09-18 стенди вибрали диск даних до
# 0.15 ГБ із 293, і та сама причина дала три різні «дефекти продукту»:
#
#   1. `sqlcmd` віддав `Msg 5149 … operating system error 112 (There is not
#      enough space on the disk.)`, а назовні поїхало «крок 1 — розгортання
#      не пройшло»;
#   2. заміри гейта спотворилися втричі (читання p95 642 мс проти 240);
#   3. `keyboardPath.spec.ts` тричі поспіль дав «кільце фокуса невидиме —
#      різниця 0.00 %», і це читалося як детермінована зв'язаність наборів,
#      хоча зонд усередині виміру показував, що кільце намальоване.
#
# На третій із них пішли години, і жоден із трьох не вказував на диск. Один
# рядок на старті коштує дешевше за будь-яку з тих гіпотез.
#
# ⚠ Міряється диск КЛІЄНТА. Для віддаленого інстансу це була б неправда, тож
# там перевірка мовчки пропускається — краще не перевірити, ніж збрехати.
function Get-RequiredFreeGb {
    # Повний профіль `01-filegroups.sql`: 4096 + 4096 + 4096 + 2048 МБ файлів
    # груп = 14 ГБ, плюс `.mdf`/`.ldf` і запас на приріст під час прогону.
    # Зменшений профіль (Express або позначка `Ecr_SmallFiles`) — 4 × 64 МБ.
    #
    # ⚠ Позначку `Ecr_SmallFiles` тут знати НЕМОЖЛИВО: вона ставиться на вже
    # створену базу. Тому за нею не вгадуємо — беремо повний профіль і
    # називаємо це в повідомленні, щоб число можна було перевірити.
    $edition = (& sqlcmd -S $Server -E -C -b -h -1 -W `
            -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);" 2>&1 |
        Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1)

    if ($LASTEXITCODE -eq 0 -and "$edition".Trim() -eq '4') { return 1.0 }   # 4 = Express

    return 15.5
}

$serverHost = ($Server -split '\\')[0]
$isLocalServer = $serverHost -in @('localhost', '.', '(local)', '127.0.0.1', $env:COMPUTERNAME)

if ($isLocalServer) {
    if ($RequireFreeGb -ge 0) { $needGb = [double] $RequireFreeGb } else { $needGb = Get-RequiredFreeGb }

    if ($needGb -gt 0) {
        # Куди насправді ляжуть файли: наш `-DataPath`, інакше типовий каталог
        # інстансу — його знає лише сервер, тому питаємо сервер, а не вгадуємо.
        $target = $DataPath
        if (-not $target) {
            $target = (& sqlcmd -S $Server -E -C -b -h -1 -W `
                    -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260));" 2>&1 |
                Where-Object { $_ -match ':' } | Select-Object -First 1)
        }

        $drive = $null
        if ($target) { $drive = (Split-Path -Qualifier "$target".Trim()).TrimEnd(':') }

        if ($drive) {
            $free = (Get-PSDrive -Name $drive -ErrorAction SilentlyContinue).Free
            if ($null -ne $free) {
                $freeGb = [math]::Round($free / 1GB, 1)
                Write-Host ("Диск даних {0}: вільно {1} ГБ, потрібно {2} ГБ" -f $drive, $freeGb, $needGb)

                if ($freeGb -lt $needGb) {
                    throw @"
Замало місця на диску даних SQL Server ${drive}: вільно $freeGb ГБ, потрібно $needGb ГБ.

Це НЕ дефект розгортання і не дефект продукту. Без цієї перевірки далі було б
'sqlcmd повернув 1 на 01-filegroups.sql' (насправді Msg 5149, operating system
error 112), а прогони на такому стенді ще й дали б спотворені заміри.

Що робити:
  * прибрати бази тестів (їх відтворює SqlServerFixture, нічого цінного немає):
    sqlcmd -S $Server -E -Q "SELECT name FROM sys.databases WHERE name LIKE 'EcrTest[_]%';"
  * прибрати стенди, що лишилися від обірваних прогонів (кожен ~14 ГБ);
  * або вказати інший диск: -DataPath <шлях>;
  * або, якщо профіль буде зменшений, зняти перевірку: -RequireFreeGb 0.
"@
                }
            }
        }
    }
}

Write-Host 'Генерую migration.sql…'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Invoke-NativeStep "dotnet ef migrations script" {
    dotnet ef migrations script --idempotent `
        --project (Join-Path $root 'src/Ecr.Infrastructure') `
        --startup-project (Join-Path $root 'src/Ecr.Infrastructure') `
        --output $migration | Out-Null
}

Write-Host "Створюю базу $Database…"
Invoke-Sql -Db 'master' -Query @"
IF DB_ID('$Database') IS NOT NULL
BEGIN
    ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$Database];
END;
CREATE DATABASE [$Database]$(if ($DataPath) { @"

ON PRIMARY (NAME = N'$Database', FILENAME = N'$DataPath\$Database.mdf')
LOG ON      (NAME = N'${Database}_log', FILENAME = N'$DataPath\${Database}_log.ldf')
"@ });
"@

# ⚠ Каталог файлів бази. Типовий каталог інстансу — це диск, який обирали не
# під ECR: 2026-09-18 стенди вибрали його до 0.15 ГБ із 293, бо кожна база тут
# важить 14 ГБ (Developer Edition звітує як Enterprise — див. коментар у
# `01-filegroups.sql`). Наслідок був не лише «розгортання не пройшло»:
# вичерпаний диск СПОТВОРИВ заміри гейта втричі.
#
# ⚠ Властивість ставиться ДО `01-filegroups.sql`, бо саме він створює файли.
if ($DataPath) {
    if (-not (Test-Path $DataPath)) { New-Item -ItemType Directory -Force -Path $DataPath | Out-Null }

    Invoke-Sql -Db $Database -Query @"
EXEC sys.sp_addextendedproperty @name = N'Ecr_DataPath', @value = N'$DataPath';
"@
}

# ⛔ Перелік і порядок — з `09-commands.md` §3. `07` переносить таблиці на
# схеми партиціонування і тому йде ПІСЛЯ міграцій; `11` — ПЕРЕД `07`,
# інакше `aud.*` лишиться на PRIMARY; `06` — після того, як таблиці на
# місці, бо він бере базу в ексклюзивне користування.
$scripts = @(
'01-filegroups.sql'
'02-partitions.sql'
'<migration>'
'11-audit-tables.sql'
'07-partition-tables.sql'
'08-system-tables.sql'
'12-archive-tables.sql'
'13-cache-table.sql'
'03-archive-proc.sql'
'04-partition-maintenance.sql'
'05-rpt-views.sql'
'15-cell-tvp.sql'
'10-triggers.sql'
'06-rcsi.sql'
)

# ⛔ Сторож проти того, що вже сталося одного разу: `06-rcsi.sql` існував,
# був у документованому порядку — і не виконувався ЖОДНИМ розгортанням,
# бо перелік тут писався руками. База при цьому піднімалася без RCSI, і
# єдиним слідом був рядок `RCSI False` у логу старту.
#
# ⚠ `09-seed.sql` виконує сам застосунок (це DML, `02-contracts.md` §14),
# тому він єдиний легальний виняток.
    # ⚠ Два винятки, і обидва названі. `09-seed.sql` виконує сам застосунок
# (це DML, `02-contracts.md` §14). `14-agent-jobs.sql` чіпає `msdb`, а не
# базу застосунку: він створює завдання обслуговування під окремим
# principal (`D-66`) і виконується DBA один раз, а не при кожній перевірці
# розгортання.
$onDisk = Get-ChildItem -Path $sql -Filter '*.sql' | Select-Object -ExpandProperty Name
$missed = $onDisk | Where-Object {
    $_ -notin $scripts -and $_ -ne '09-seed.sql' -and $_ -ne '14-agent-jobs.sql'
}

if ($missed) {
throw "Скрипти є в дереві, але не виконуються: $($missed -join ', ')"
}

foreach ($name in $scripts) {
    if ($name -eq '<migration>') {
        Write-Host '  migration.sql'
        Invoke-Sql -Db $Database -File $migration
    }
    else {
        Invoke-Script $name
    }
}

# ⚠ Seed виконує САМ застосунок при старті (`02-contracts.md` §14: seed — це
# DML, і він належить застосунку). Тому дані генеруються вже після першого
# запуску — генератор спирається на довідник політик періодів із seed.
if ($Documents -gt 0) {
    # ⛔ Складання ЯВНО і один раз. Нижче обидва `dotnet run` ідуть із
    # `--no-build` — і без цього кроку вони виконують ПОПЕРЕДНЮ збірку, а не
    # дерево. Скрипт, чия мета «чисте середовище з поточного дерева», мовчки
    # піднімав середовище з коду, якого в дереві вже немає.
    #
    # ⚠ Коштувало це двох марних прогонів стенда 2026-09-07: правку в
    # `Ecr.DataGen` було внесено, помилка лишалася та сама, і причина не була
    # видна нізвідки — вивід показував стан минулої збірки і виглядав як стан
    # дерева. Той самий клас пастки, що й інкрементне складання, яке ховає
    # попередження.
    Write-Host 'Складаю (щоб --no-build нижче взяв поточне дерево)…'
    Invoke-NativeStep "dotnet build" {
        dotnet build (Join-Path $root 'Ecr.sln') -v q --nologo | Out-Null
    }

    Write-Host 'Перший старт: seed і bootstrap-адміністратор…'

    $env:ECR_ConnectionStrings__Ecr = $connection
    $env:ECR_Bootstrap__Password = $BootstrapPassword
    $env:ASPNETCORE_URLS = 'http://localhost:5099'

    # ⚠ Шлях береться В ЛАПКИ: у ньому є пробіл («ECR Web»), а Start-Process
    # ділить -ArgumentList по пробілах і без лапок передає два аргументи.
    # ⛔ `--no-launch-profile` обов'язковий: інакше `dotnet run` бере
    # `Properties/launchSettings.json` розробника і слухає ЙОГО порти, а не
    # той, що чекає цей скрипт. Скрипт підготовки не має залежати від того,
    # що кожен налаштував собі локально.
    # ⚠ Вивід іде у файл: інакше падіння старту виглядає як «не піднявся» без
    # жодної причини — а причина там же, у першому рядку логу.
    $log = Join-Path $artifacts 'setup-dev-db.api.log'
    $project = '"' + (Join-Path $root 'src/Ecr.Api') + '"'

    $api = Start-Process -PassThru -WindowStyle Hidden dotnet `
        -ArgumentList "run --project $project --no-build --no-launch-profile" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"

    try {
        $ready = $false
        foreach ($i in 1..120) {
            Start-Sleep -Milliseconds 500

            if ($api.HasExited) {
                throw "Застосунок завершився з кодом $($api.ExitCode). Лог: $log"
            }

            try {
                $code = (Invoke-WebRequest -Uri 'http://localhost:5099/health/live' `
                            -UseBasicParsing -TimeoutSec 2).StatusCode
                if ($code -eq 200) { $ready = $true; break }
            }
            catch { }
        }

        if (-not $ready) { throw "Застосунок не піднявся за 60 с. Лог: $log" }
        Write-Host '  seed виконано, bootstrap створено'
    }
    finally {
        if (-not $api.HasExited) { $api.Kill(); $api.WaitForExit() }
    }

    Write-Host "Генерую $Documents документ(ів)…"
    Invoke-NativeStep "Ecr.DataGen" {
        dotnet run --project (Join-Path $root 'tools/Ecr.DataGen') --no-build -- `
            --documents $Documents --fill 90 --year 2026 --connection $connection | Out-Null
    }
}

Write-Host ''
Write-Host "База $Database готова." -ForegroundColor Green
Write-Host ''
Write-Host 'Змінні оточення для запуску:'
Write-Host "  ECR_ConnectionStrings__Ecr = $connection"
Write-Host "  ECR_Bootstrap__Password    = $BootstrapPassword"
Write-Host ''
Write-Host "Вхід: bootstrap / $BootstrapPassword (пароль треба змінити при першому вході)."
