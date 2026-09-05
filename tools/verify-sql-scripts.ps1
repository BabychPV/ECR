<#
.SYNOPSIS
    Проганяє повне розгортання через `sqlcmd` — тим самим клієнтом, що й DBA.

.DESCRIPTION
    Скрипт існує через `P-17`. Інтеграційні тести виконують ті самі `.sql`
    через `Microsoft.Data.SqlClient`, у якого `QUOTED_IDENTIFIER ON`, а `sqlcmd`
    за замовчуванням має `OFF`. Через це `07-partition-tables.sql` проходив у
    тестах і падав у розгортанні: зелений прогін нічого не казав про той шлях,
    яким система насправді ставиться.

    Послідовність дослівно повторює `09-commands.md` §3, разом із міграціями:
    `11-audit-tables.sql` посилається на `sec.User`, якої скрипти не створюють.
    Тому перевіряється не «чи парситься файл», а чи ставиться система цілком.

    Перевірка йде на ОКРЕМІЙ базі, яку сама створює і видаляє: ганяти DDL по
    базі з даними означало б перетворити перевірку на ризик.

.PARAMETER Server
    Екземпляр SQL Server. За замовчуванням локальний SQLEXPRESS.

.PARAMETER Database
    Ім'я тимчасової бази. Створюється і видаляється цим самим скриптом.

.EXAMPLE
    powershell -File tools/verify-sql-scripts.ps1
#>
[CmdletBinding()]
param(
    [string] $Server = 'localhost\SQLEXPRESS',
    [string] $Database = 'EcrSqlScriptCheck'
)

$ErrorActionPreference = 'Stop'

if ($null -eq (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Write-Error 'sqlcmd не знайдено. Саме ним DBA виконує розгортання: без нього перевірка беззмістовна.'
}

$root = Split-Path -Parent $PSScriptRoot
$sql = Join-Path $root 'src/Ecr.Infrastructure/Persistence/Sql'
$artifacts = Join-Path $root 'artifacts'
$migration = Join-Path $artifacts 'migration.sql'

function Invoke-Sql {
    param([string] $Db, [string] $Query, [string] $File)

    $arguments = @('-S', $Server, '-E', '-C', '-b', '-I', '-d', $Db)
    if ($File) { $arguments += @('-i', $File) } else { $arguments += @('-Q', $Query) }

    & sqlcmd @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $what = if ($File) { $File } else { $Query }
        throw "sqlcmd повернув $LASTEXITCODE на $what"
    }
}

function Invoke-Script {
    param([string] $Name)

    $path = Join-Path $sql $Name
    if (-not (Test-Path $path)) {
        throw "Немає ${path}: перелік у цьому скрипті розійшовся з деревом."
    }

    Write-Host "  $Name"
    Invoke-Sql -Db $Database -File $path
}

Write-Host 'Генерую migration.sql…'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
& dotnet ef migrations script --idempotent `
    --project (Join-Path $root 'src/Ecr.Infrastructure') `
    --startup-project (Join-Path $root 'src/Ecr.Infrastructure') `
    --output $migration | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "dotnet ef migrations script повернув $LASTEXITCODE"
}

Write-Host "Створюю тимчасову базу $Database…"
Invoke-Sql -Db 'master' -Query "IF DB_ID('$Database') IS NOT NULL DROP DATABASE [$Database]; CREATE DATABASE [$Database];"

try {
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

    Write-Host 'Розгортання пройшло під sqlcmd повністю.' -ForegroundColor Green
}
finally {
    Write-Host "Прибираю $Database…"
    Invoke-Sql -Db 'master' -Query "IF DB_ID('$Database') IS NOT NULL ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"
    Invoke-Sql -Db 'master' -Query "IF DB_ID('$Database') IS NOT NULL DROP DATABASE [$Database];"
}
