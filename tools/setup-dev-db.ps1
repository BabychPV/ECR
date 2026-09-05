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
    [string] $BootstrapPassword = 'Dev-Bootstrap-2026!'
)

$ErrorActionPreference = 'Stop'

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

    & sqlcmd @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd повернув $LASTEXITCODE на $(if ($File) { $File } else { $Query })"
    }
}

function Invoke-Script {
    param([string] $Name)

    $path = Join-Path $sql $Name
    if (-not (Test-Path $path)) { throw "Немає ${path}." }

    Write-Host "  $Name"
    Invoke-Sql -Db $Database -File $path
}

Write-Host 'Генерую migration.sql…'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
& dotnet ef migrations script --idempotent `
    --project (Join-Path $root 'src/Ecr.Infrastructure') `
    --startup-project (Join-Path $root 'src/Ecr.Infrastructure') `
    --output $migration | Out-Null

if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations script повернув $LASTEXITCODE" }

Write-Host "Створюю базу $Database…"
Invoke-Sql -Db 'master' -Query @"
IF DB_ID('$Database') IS NOT NULL
BEGIN
    ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$Database];
END;
CREATE DATABASE [$Database];
"@

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
$onDisk = Get-ChildItem -Path $sql -Filter '*.sql' | Select-Object -ExpandProperty Name
$missed = $onDisk | Where-Object { $_ -notin $scripts -and $_ -ne '09-seed.sql' }

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
    & dotnet run --project (Join-Path $root 'tools/Ecr.DataGen') --no-build -- `
        --documents $Documents --fill 90 --year 2026 --connection $connection | Out-Null

    if ($LASTEXITCODE -ne 0) { throw "Ecr.DataGen повернув $LASTEXITCODE" }
}

Write-Host ''
Write-Host "База $Database готова." -ForegroundColor Green
Write-Host ''
Write-Host 'Змінні оточення для запуску:'
Write-Host "  ECR_ConnectionStrings__Ecr = $connection"
Write-Host "  ECR_Bootstrap__Password    = $BootstrapPassword"
Write-Host ''
Write-Host "Вхід: bootstrap / $BootstrapPassword (пароль треба змінити при першому вході)."
