<#
.SYNOPSIS
    Навантажувальна перевірка гейта `BR-07` однією командою: наповнення
    `doc.CellValue` до заданого обсягу і шість замірів фізичної моделі.

.DESCRIPTION
    Гейт `BR-07` (`tz/08-nfr.md` §8.10, `B02` §7) — це рішення про фізичну
    модель зберігання комірок: чи витримує нормалізована `doc.CellValue`
    бюджети на річному обсязі, чи окремі таблиці доведеться переводити на
    `StorageMode = Hybrid` (`D-21`) ціною втрати складеного FK.

    ⛔ Наповнюється саме `doc.CellValue`, а не `calc.CalculationResult`.
    Різниця не косметична: результатів розрахунку ~4 млн на рік проти ~108 млн
    комірок, тобто в двадцять сім разів менше. Перевірка, яка навантажує
    таблицю результатів, дала б зелений бюджет і не означала б нічого.

    ⛔ Скрипт створює ВЛАСНУ тимчасову базу і прибирає лише її. Чужих баз він
    не бачить: перед видаленням звіряється розширена властивість
    `Ecr_Br07_Temp`, яку ставить сам при створенні. Бази `EcrTest*`, `EcrDev`
    і будь-які інші лишаються недоторканими навіть при збігу імен.

    ⚠ Позначка `Ecr_SmallFiles` ставиться ОДРАЗУ після `CREATE DATABASE`.
    Без неї `01-filegroups.sql` створює файли продуктивного розміру
    (4096+4096+4096+2048 МБ = 14 ГБ на порожню базу) — так уже було з'їдено
    152 ГБ на сімнадцяти тестових базах. Файли натомість вирощуються під
    ПОРАХОВАНИЙ обсяг (`-Cells`) одним `MODIFY FILE`: 64-мегабайтний приріст
    на десятку гігабайтів дав би близько 160 подій росту файла, і замір
    показав би швидкість автоприросту, а не швидкість моделі.

.PARAMETER Server
    Екземпляр SQL Server. Має бути НЕ Express: `EngineEdition = 3`
    (Developer/Enterprise). На Express 1410 МБ буферного пулу означають, що
    замір №6 вимірює диск, а не `doc.CellValue` (`Q-063`).

.PARAMETER Database
    Ім'я тимчасової бази. Створюється наново; наявна база з таким іменем — це
    зупинка з помилкою, а не мовчазне перезаписування.

.PARAMETER Cells
    Цільова кількість рядків `doc.CellValue`. Типово 108 000 000 — обсяг із
    `02a-db-schema.md` («~108 млн рядків на рік»).

.PARAMETER Fill
    Заповненість комірок у відсотках: 35 | 60 | 90. Сценарій гейта — 90.

.PARAMETER LoadSeconds
    Тривалість заміру №6. Типово 900 (15 хв повного гейта). Менше значення
    прогін не забороняє, але результат помічається як неповний гейт.

.PARAMETER Keep
    Не видаляти базу після заміру (щоб перезняти числа без наповнення).

.PARAMETER SkipGenerate
    Не створювати і не наповнювати базу — лише заміряти наявну.

.PARAMETER BytesPerCell
    Оцінка місця на диску на одну комірку разом із рядками, індексами і
    журналом. Типове значення — ВИМІРЯНЕ, а не вигадане; звідки воно, сказано
    в коді біля константи.

.EXAMPLE
    powershell -File tools/br07-load-test.ps1
    powershell -File tools/br07-load-test.ps1 -Cells 5000000 -LoadSeconds 120
    powershell -File tools/br07-load-test.ps1 -SkipGenerate -Database EcrBr07

.OUTPUTS
    Код виходу 0 — бюджет витриманий; 2 — гейт НЕ пройдено; 1 — прогін не
    відбувся (немає місця, не та редакція, база вже існує).
#>
[CmdletBinding()]
param(
    [string] $Server = 'localhost\SQLEXPRESS',
    [string] $Database = 'EcrBr07',
    [long]   $Cells = 108000000,
    [int]    $Fill = 90,
    [int]    $LoadSeconds = 900,
    [switch] $Keep,
    [switch] $SkipGenerate,

    # ⚠ 45 байтів на комірку — це ВИМІРЯНЕ значення (прогін 2026-09-06):
    # 2.056 млн комірок зайняли 81.8 МБ усіма таблицями і індексами разом
    # (`doc.CellValue` — 64.1 МБ, тобто 32.7 Б на рядок після PAGE-стиснення;
    # решта — `doc.TableRow` з двома індексами). 41.7 Б на комірку + запас на
    # журнал. Оцінка бере базу ЦІЛКОМ, а не саму `doc.CellValue`: місце на
    # диску з'їдає база, а не одна таблиця.
    [int]    $BytesPerCell = 45
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sql = Join-Path $root 'src/Ecr.Infrastructure/Persistence/Sql'
$artifacts = Join-Path $root 'artifacts'
$migration = Join-Path $artifacts 'migration.sql'
$datagen = Join-Path $root 'tools/Ecr.DataGen'
$connection = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"

if ($null -eq (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Write-Error 'sqlcmd не знайдено. Саме ним виконується розгортання.'
}

# ⛔ Release, а не Debug. У Debug вимкнена оптимізація JIT, і генератор
# навантаження заміру №6 сам стає вузьким місцем: 125 RPS не досягаються, і
# гейт червоніє через конфігурацію збірки, а не через модель даних.
Write-Host 'Складання (Release)…'
& dotnet build $datagen -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet build повернув $LASTEXITCODE" }

function Invoke-Sql {
    param([string] $Db, [string] $Query, [string] $File)

    $arguments = @('-S', $Server, '-E', '-C', '-b', '-I', '-d', $Db)
    if ($File) { $arguments += @('-i', $File) } else { $arguments += @('-Q', $Query) }

    & sqlcmd @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd повернув $LASTEXITCODE на $(if ($File) { $File } else { $Query })"
    }
}

function Get-SqlScalar {
    param([string] $Db, [string] $Query)

    $value = & sqlcmd -S $Server -E -C -b -I -h -1 -W -d $Db -Q "SET NOCOUNT ON; $Query"
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd повернув $LASTEXITCODE на $Query" }

    return ($value | Where-Object { $_ -ne '' } | Select-Object -First 1)
}

# ── Придатність інстансу ────────────────────────────────────────────────────
# ⚠ Перевіряється РЕДАКЦІЯ, а не ім'я екземпляра. На цій машині інстанс
# називається `SQLEXPRESS`, а видання — Developer: ім'я казало «Express»,
# видання — ні, і саме на цьому одного разу помилилися обидва рази поспіль
# (`P-04`). Джерело істини — `EngineEdition`.
$edition = Get-SqlScalar -Db 'master' -Query "SELECT CONVERT(nvarchar(200), SERVERPROPERTY('Edition'))"
$engineEdition = [int](Get-SqlScalar -Db 'master' -Query "SELECT CONVERT(int, SERVERPROPERTY('EngineEdition'))")
$version = Get-SqlScalar -Db 'master' -Query "SELECT CONVERT(nvarchar(50), SERVERPROPERTY('ProductVersion'))"

Write-Host "SQL Server: $edition ($version), EngineEdition = $engineEdition"

if ($engineEdition -eq 4) {
    Write-Error @"
Це SQL Server Express (EngineEdition = 4). Замір на ньому не має сенсу:
1410 МБ буферного пулу проти робочого набору в кілька ГБ означають, що
замір №6 вимірює швидкість диска, а не нормалізовану модель (Q-063).
"@
}

# ── Місце на диску ──────────────────────────────────────────────────────────
# ⛔ Перевірка ПЕРЕД наповненням, а не після. Прогін на 108 млн комірок, який
# упреться в «operating system error 112» на дев'яностій хвилині, коштує двох
# годин і лишає по собі напівнаповнену базу.
$dataPath = Get-SqlScalar -Db 'master' -Query "SELECT CONVERT(nvarchar(400), SERVERPROPERTY('InstanceDefaultDataPath'))"
$driveLetter = (Split-Path -Qualifier $dataPath).TrimEnd(':')
$drive = Get-PSDrive $driveLetter

$neededGb = [math]::Round(($Cells * $BytesPerCell) / 1GB * 1.3, 2)
$neededMb = [math]::Round(($Cells * $BytesPerCell) / 1MB * 1.3, 0)
$freeGb = [math]::Round($drive.Free / 1GB, 1)

Write-Host "Каталог даних: $dataPath (диск $driveLetter)"
Write-Host "Потрібно ≈ $neededGb ГБ ($neededMb МБ, із запасом 30 % на журнал і tempdb), вільно $freeGb ГБ"

if (-not $SkipGenerate -and $neededGb -gt $freeGb) {
    $fits = [math]::Floor($drive.Free / 1.3 / $BytesPerCell / 1e6)
    Write-Error @"
Не влазить: потрібно $neededGb ГБ, вільно $freeGb ГБ.
На цьому диску поміститься близько $fits млн комірок — запустіть із
`-Cells $($fits)000000` і читайте результат як замір на МЕНШОМУ обсязі.
Екстраполяція на 108 млн — окреме припущення, а не виміряне число.
"@
}

# ── Створення бази ──────────────────────────────────────────────────────────
if (-not $SkipGenerate) {
    $exists = Get-SqlScalar -Db 'master' -Query "SELECT CASE WHEN DB_ID('$Database') IS NULL THEN 0 ELSE 1 END"
    if ($exists -eq '1') {
        Write-Error @"
База $Database вже існує. Скрипт її НЕ перезаписує: на цій машині живуть бази
замовника (EcrDev, EcrTest*), і мовчазне `DROP DATABASE` за збігом імені —
рівно та помилка, яку не можна зробити один раз. Приберіть базу руками або
візьміть інше ім'я через -Database.
"@
    }

    Write-Host "Створюю базу $Database…"

    # ⚠ Позначка і рекавері-модель — ОДРАЗУ після CREATE DATABASE, до
    # `01-filegroups.sql`. SIMPLE тут тому, що наповнення на 108 млн рядків у
    # FULL роздуло б журнал до розміру даних; у проді модель інша, і це
    # означає, що виміряний РОЗМІР бази занижений на розмір журналу.
    Invoke-Sql -Db 'master' -Query @"
CREATE DATABASE [$Database];
ALTER DATABASE [$Database] SET RECOVERY SIMPLE;
"@

    Invoke-Sql -Db $Database -Query @"
EXEC sys.sp_addextendedproperty @name = N'Ecr_SmallFiles', @value = 1;
EXEC sys.sp_addextendedproperty @name = N'Ecr_Br07_Temp', @value = 1;
"@

    Write-Host 'Генерую migration.sql…'
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    & dotnet ef migrations script --idempotent `
        --project (Join-Path $root 'src/Ecr.Infrastructure') `
        --startup-project (Join-Path $root 'src/Ecr.Infrastructure') `
        --output $migration | Out-Null

    if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations script повернув $LASTEXITCODE" }

    # ⛔ Перелік і порядок — з `09-commands.md` §3, той самий, що в
    # `setup-dev-db.ps1`. `07` переносить таблиці на схеми партиціонування і
    # тому йде ПІСЛЯ міграцій; `11` — ПЕРЕД `07`; `06` (RCSI) — після того, як
    # таблиці на місці. Без `06` замір №6 міряв би базу без версійності
    # рядків, тобто іншу базу.
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
        '09-seed.sql'
        '06-rcsi.sql'
    )

    foreach ($name in $scripts) {
        if ($name -eq '<migration>') {
            Write-Host '  migration.sql'
            Invoke-Sql -Db $Database -File $migration
        }
        else {
            Write-Host "  $name"
            Invoke-Sql -Db $Database -File (Join-Path $sql $name)
        }
    }

    # ── Файли під порахований обсяг ─────────────────────────────────────────
    # ⚠ Один MODIFY FILE замість сотні автоприростів по 64 МБ. Ріст файла —
    # синхронна операція: без миттєвої ініціалізації файлів кожні 64 МБ
    # зануляються, і наповнення міряло б диск, а не завантажувач.
    $hotMb = [math]::Max(64, [math]::Ceiling($Cells * $BytesPerCell / 1MB * 0.8))
    $logMb = [math]::Max(256, [math]::Ceiling($hotMb * 0.15))

    Write-Host "Вирощую DATA_HOT до $hotMb МБ і журнал до $logMb МБ…"

    # ⛔ Розмір задається ТІЛЬКИ вгору і ТІЛЬКИ через динамічний SQL:
    # `MODIFY FILE (SIZE = …)` менший або рівний поточному — це помилка 5039, а
    # не «нічого не робити», і вона зупиняла б увесь прогін на малих обсягах.
    # Ім'я файла журналу читається з `sys.database_files`, а не збирається як
    # `<база>_log`: SQL Server дає логічне ім'я за шаблоном, який залежить від
    # версії, і вгадування ламається мовчки.
    Invoke-Sql -Db $Database -Query @"
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd = @cmd + N'ALTER DATABASE ' + QUOTENAME(DB_NAME())
     + N' MODIFY FILE (NAME = ' + QUOTENAME(f.name, '''')
     + N', SIZE = ' + CAST(t.TargetMb AS nvarchar(20)) + N'MB'
     + N', FILEGROWTH = ' + CAST(t.GrowthMb AS nvarchar(20)) + N'MB);'
FROM sys.database_files AS f
CROSS APPLY (SELECT TargetMb = CASE WHEN f.type = 1 THEN $logMb ELSE $hotMb END,
                    GrowthMb = CASE WHEN f.type = 1 THEN 256 ELSE 512 END) AS t
WHERE (f.type = 1 OR f.name = N'Ecr_hot')
  AND f.size * 8 / 1024 < t.TargetMb;
IF @cmd <> N'' EXEC sys.sp_executesql @cmd;
"@

    # ── Наповнення ──────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host "Наповнюю doc.CellValue до $Cells комірок (заповненість $Fill %)…"
    $started = Get-Date

    & dotnet run --project $datagen --no-build -c Release -- `
        --cells $Cells --fill $Fill --year 2026 --connection $connection

    if ($LASTEXITCODE -ne 0) { throw "Ecr.DataGen повернув $LASTEXITCODE" }

    Write-Host "Наповнення зайняло $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) хв."

    # ⛔ Статистика ПІСЛЯ наповнення і ДО замірів. Без неї оптимізатор бачить
    # порожню таблицю (статистику зібрано на нулі рядків) і будує план під
    # неї — замір показав би не модель, а застарілу статистику.
    Write-Host 'Оновлюю статистику…'
    Invoke-Sql -Db $Database -Query 'EXEC sys.sp_updatestats;'
}

# ── Заміри ──────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host "Заміри гейта BR-07 (замір №6 — $LoadSeconds с)…"
Write-Host '⚠ На інстансі не має працювати нічого іншого: лічильник ескалацій'
Write-Host '  блокувань SQL Server дає на весь інстанс, а не на базу.'

& dotnet run --project $datagen --no-build -c Release -- `
    --gate --load-seconds $LoadSeconds --connection $connection

$gate = $LASTEXITCODE

$sizeMb = Get-SqlScalar -Db $Database -Query @"
SELECT CONVERT(int, SUM(size) * 8 / 1024) FROM sys.database_files
"@

Write-Host ''
Write-Host "Файли бази $Database : $sizeMb МБ"

# ── Прибирання ──────────────────────────────────────────────────────────────
# ⛔ Видаляється ЛИШЕ база з власною позначкою `Ecr_Br07_Temp`. Перевірка не
# зайва: ім'я бази приходить параметром, і `-Database EcrDev` без цієї
# перевірки знищив би базу розробника з даними, які ніхто не відновить.
if (-not $Keep -and -not $SkipGenerate) {
    $mine = Get-SqlScalar -Db $Database -Query @"
SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = N'Ecr_Br07_Temp'
"@

    if ($mine -eq '1') {
        Write-Host "Прибираю тимчасову базу $Database…"
        Invoke-Sql -Db 'master' -Query @"
ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$Database];
"@
    }
    else {
        Write-Warning "База $Database не має позначки Ecr_Br07_Temp — НЕ чіпаю її."
    }
}
else {
    Write-Host "База $Database лишається (використайте -SkipGenerate, щоб перезняти числа)."
}

if ($gate -ne 0) {
    Write-Host ''
    Write-Host 'Гейт BR-07 НЕ пройдено.' -ForegroundColor Red
    exit 2
}

Write-Host ''
Write-Host 'Гейт BR-07 пройдено.' -ForegroundColor Green
exit 0
