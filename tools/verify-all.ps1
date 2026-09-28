<#
.SYNOPSIS
    Повна перевірка проєкту одним викликом: складання, тести, розгортання,
    клієнт.

.DESCRIPTION
    Скрипт існує через `P-17`. Його висновок був не про один зламаний файл, а
    про те, що **тести не перевіряють шлях розгортання**: вони виконують ті
    самі `.sql` іншим клієнтом, ніж DBA, і тому були зеленими, поки реальне
    розгортання падало. Те саме стосується клієнта: `dotnet test` нічого не
    знає ні про `tsc`, ні про `vitest`.

    Тут зібрано всі чотири перевірки в одному місці, щоб конвеєр складання
    викликав ОДНУ команду і не міг випадково пропустити одну з них. Який саме
    конвеєр — GitHub Actions, Azure DevOps чи локальний хук — скрипт не знає
    навмисно: платформу обирає замовник, а перелік перевірок від неї не
    залежить.

    ⚠ Порядок значущий і йде від дешевого до дорогого: складання падає за
    секунди, розгортання під `sqlcmd` — за хвилину. Зворотний порядок змусив
    би чекати найдовшу перевірку заради помилки, яку видно одразу.

.PARAMETER SkipDeployment
    Пропустити перевірку розгортання (потрібен `sqlcmd` і SQL Server).

.PARAMETER SkipClient
    Пропустити перевірку клієнта (потрібні Node.js і встановлені пакети).

.PARAMETER TestSql
    Рядок з'єднання для інтеграційних тестів. За замовчуванням береться
    `ECR_TEST_SQL` з оточення; без нього тести шукатимуть Docker.

.PARAMETER Only
    Виконати лише названі кроки. Існує заради конвеєра: гейти йдуть різними
    завданнями паралельно, інакше доступність (понад двадцять хвилин) тримала
    б усе решту.

    ⛔ Пропуск кроку через `-Only` НЕ означає, що крок можна не виконати.
    `CiPipelineTests` звіряє об'єднання `-Only` всіх завдань конвеєра з цим
    переліком: крок, якого не бере жодне завдання, робить тест червоним.
    Без цього «розділити на завдання» і «тихо викинути гейт» виглядали б
    однаково.

.PARAMETER SqlServer
    Екземпляр SQL Server для гейта розгортання. За замовчуванням локальний.

.PARAMETER SqlLogin
    Логін SQL-автентифікації для гейта розгортання. Без нього — інтегрована.

.PARAMETER SqlPassword
    Пароль до `SqlLogin`. За замовчуванням — `ECR_SQL_PASSWORD` з оточення.

    ⛔ Оточення, а не аргумент: аргументи процесу видно всім у переліку
    процесів. На агенті конвеєра це означало б пароль на видноті.

    ⛔ Ці три існують, щоб конвеєр кликав гейт розгортання ЧЕРЕЗ цей скрипт, а
    не повз нього. Обхід виглядав би однаково зеленим — і мовчки перестав би
    отримувати кожну наступну зміну в тому, ЯК гейт запускається.

.PARAMETER ApiParallel
    Скільки паралельних процесів `dotnet test` запускає КОЖЕН із кроків
    «Тести API (частина 1/2)». Типово — `ECR_API_PARALLEL` з оточення, без
    нього 1.

    1 — рівно один процес на крок, як і раніше (так іде конвеєр, доки його
    не змінять). K > 1 — шарди частини (`tools/api-test-shards.psd1`, по три
    на частину) розподіляються між min(K, 3) процесами на ту саму зібрану
    DLL; кожен процес отримує `ECR_TEST_SHARD=<N>` і тому свою базу
    `EcrTest_Api_<мітка>_s<N>`, свій фільтр і свій журнал у
    `tests/Ecr.Api.Tests/TestResults/shards/`. Код виходу спільний: крок
    червоний, якщо впав хоч один процес, і журнал упалого друкується цілком.

    ⛔ Чому процеси, а не паралельні колекції xUnit: `EcrApiFactory` передає
    рядок з'єднання через змінну оточення (одна на процес), а реєстри Quartz
    статичні. Два шарди в одному процесі ділили б одну базу.

    ⚠ Кожен процес — важкий слот машини (власна база, власний хост). Замір
    2026-09-28, обидві частини поспіль, машина під чужим навантаженням
    (CPU 78–83 % у середньому): K=1 — 580 с, K=2 — 570 с, K=3 — 269 с;
    деталі в `api-test-shards.psd1`. Природне значення — 3 (по процесу на
    шард). K=2 кладе два шарди частини в один процес, і на завантаженій
    машині виграшу майже немає.

    ⚠ `ECR_TEST_DB` при K > 1 заборонено: він перекриває ім'я бази цілком, і
    всі процеси скидали б одну базу одне в одного.

.PARAMETER ListSteps
    Надрукувати імена кроків і вийти. Це вхід для сторожа: перелік кроків
    здобувається із самого скрипта, а не переписується в тест.

.EXAMPLE
    powershell -File tools/verify-all.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipDeployment,
    [switch] $SkipClient,
    [string] $TestSql = $env:ECR_TEST_SQL,
    [string[]] $Only,
    [switch] $ListSteps,
    [string] $SqlServer,
    [string] $SqlLogin,
    [string] $SqlPassword = $env:ECR_SQL_PASSWORD,
    [int] $ApiParallel = $(if ($env:ECR_API_PARALLEL) { [int] $env:ECR_API_PARALLEL } else { 1 })
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'src/Ecr.Web'
$failures = @()
$declared = @()

# ⛔ Сервісні контейнери GitHub Actions працюють ЛИШЕ на Linux-раннерах, отже
# конвеєр, якому потрібен SQL Server, не має вибору платформи. До цієї правки
# скрипт був Windows-only в двох місцях — `npm.cmd` і `& powershell`, — і
# кожне з них падало б на агенті так, що причина виглядала б як зламаний код.
#
# ⚠ `$IsWindows` немає у Windows PowerShell 5.1: там змінна не визначена й
# читається як `$false`. Тому питаємо ще й редакцію — інакше на машині
# розробника скрипт шукав би `npm` без розширення і не знаходив.
# ⛔ `-Only 'a','b'` через `powershell -File` приходить ОДНИМ рядком
# `'a,b'`: кому розбирає мова, а не парсер параметрів, і при запуску файла
# мови тут немає. Наслідок був би найгіршим із можливих — жодне ім'я не
# збіглося б, ЖОДЕН крок не виконався, а конвеєр вийшов би нулем і показав
# зелене. Знайдено прогоном на машині розробника, до першого запуску конвеєра.
#
# ⚠ Розбір безпечний: у назвах кроків ком немає і бути не може — вони імена,
# а не переліки.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$onWindows = $IsWindows -or $PSVersionTable.PSEdition -eq 'Desktop'
$npm = if ($onWindows) { 'npm.cmd' } else { 'npm' }
$psExe = if ($onWindows) { 'powershell' } else { 'pwsh' }

# ⚠ `-ExecutionPolicy` існує лише у Windows: у PowerShell на Linux політик
# виконання немає, і параметр там зайвий.
$psPrefix = if ($onWindows) { @('-ExecutionPolicy', 'Bypass') } else { @() }

<#
.SYNOPSIS
    Запускає дочірній скрипт окремим процесом PowerShell.
.DESCRIPTION
    ⛔ Існує через падіння на агенті: `& $psExe @psArgs (Join-Path …) @sqlArgs`
    давало `The argument 'F' is not recognized as the name of a script file`.
    Змішувати розкладання масиву (`@psArgs`) з позиційним аргументом у виклику
    зовнішньої програми не можна — порядок, у якому PowerShell збирає рядок
    запуску, при цьому не той, який видно в коді.

    ⚠ Тут список збирається ОДИН і розкладається один раз. Помилка була
    видима лише на Linux, бо на Windows префікс має три елементи і `-File`
    опинявся останнім — тобто на машині розробника все працювало.
#>
function Invoke-Child {
    param([string] $Script, [string[]] $Arguments = @())

    $all = @()
    $all += $psPrefix
    $all += @('-File', (Join-Path $PSScriptRoot $Script))
    $all += $Arguments

    & $psExe @all
}

function Step {
    param([string] $Name, [scriptblock] $Body)

    # ⚠ Ім'я записується ДО будь-якого пропуску: перелік кроків має бути
    # повним незалежно від того, які з них виконуються цього разу. Інакше
    # `-ListSteps` віддавав би різне при різних прапорцях, і сторож звіряв би
    # конвеєр із випадковою підмножиною.
    $script:declared += $Name

    if ($ListSteps) { return }
    if ($Only -and $Name -notin $Only) { return }

    Write-Host ''
    Write-Host "── $Name" -ForegroundColor Cyan

    try {
        & $Body
        if ($LASTEXITCODE -ne 0) {
            throw "код виходу $LASTEXITCODE"
        }
    }
    catch {
        # ⚠ Перевірки НЕ зупиняють одна одну: інакше зламаний клієнт ховав би
        # стан розгортання, і кожен прогін показував би рівно одну проблему.
        $script:failures += "$Name — $($_.Exception.Message)"
        Write-Host "   ✗ $Name" -ForegroundColor Red
        return
    }

    Write-Host "   ✓ $Name" -ForegroundColor Green
}

# ⛔ Запущений застосунок тримає `Ecr.Infrastructure.dll`, і складання падає з
# MSB3027 — «файл використовується іншим процесом». Помилка виглядає як
# зламаний код, а насправді це забутий `dotnet run` у сусідньому вікні.
#
# ⚠ Знайдено аудитом: прогін показав ✗ на кроці складання при цілком
# справному дереві, і на з'ясування причини пішло більше часу, ніж на цей
# рядок.
#
# ⛔ 2026-09-27: тут стояло `Get-Process -Name 'Ecr.Api' | Stop-Process -Force`
# — вбивало БУДЬ-ЯКИЙ `Ecr.Api` на машині, зокрема чужий стенд іншої сесії з
# іншого worktree, що прямо суперечить правилу «чужий піднятий Ecr.Api не
# вбивай» (CLAUDE.md). Сам цей скрипт `Ecr.Api` не запускає (стенди
# `smoke.ps1`/`e2e-stand.ps1` піднімають і гасять свій), тож «свого» PID, який
# можна було б зберегти й загасити, тут немає. Тому нічого не вбиваємо:
# складанню заважає лише процес, запущений із ЦЬОГО чекауту (тримає саме
# нашу DLL), — його називаємо і зупиняємося з поясненням замість MSB3027.
if (-not $ListSteps) {
    $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $ours = @(Get-Process -Name 'Ecr.Api' -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and $_.Path.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($ours.Count -gt 0) {
        Write-Host "Ecr.Api запущено з цього чекауту — складання впаде з MSB3027 (файл зайнятий):" -ForegroundColor Red
        $ours | ForEach-Object { Write-Host "  PID $($_.Id)  $($_.Path)" -ForegroundColor Red }
        Write-Host 'Зупини його сам, якщо він твій (Stop-Process -Id <PID>), або запускай перевірку з окремого worktree.' -ForegroundColor Red
        exit 3
    }
}

# ⚠ При переліку прапорці пропуску знімаються: питання «які кроки взагалі
# існують» не залежить від того, що вміє ця машина.
if ($ListSteps) {
    $SkipDeployment = $false
    $SkipClient = $false
}

Step 'Складання' {
    & dotnet build (Join-Path $root 'Ecr.sln') -v q --nologo
}

# ⛔ Гейт ліцензій (D-12, D-82): граф NuGet береться з obj/project.assets.json,
# тож крок іде ПІСЛЯ складання (restore). Політика — contracts/license-policy.json.
Step 'Ліцензії NuGet' { & node (Join-Path $root 'tools/license-check.mjs') nuget }

Step 'Тести .NET' {
    if ($TestSql) {
        # ⚠ Через оточення, а не аргументом: фікстура читає саме `ECR_TEST_SQL`
        # (`P-04`), і передати рядок з'єднання інакше нема куди.
        $env:ECR_TEST_SQL = $TestSql
    }

    # ⛔ Рішення (Q-207): фільтра `Category` тут НЕМАЄ навмисно. Інтеграційні
    # тести (`[Trait("Category","Integration")]`, кількасот у
    # `Ecr.Api.Tests`/`Ecr.Application.Tests`/`Ecr.Infrastructure.Tests` тощо)
    # виконуються в цьому самому кроці — саме тому CI піднімає реальний SQL
    # Server у контейнері (`.github/workflows/ci.yml`, job `server`) і
    # прокидає `ECR_TEST_SQL` сюди. Раніше `04-environment.md`/`09-commands.md`
    # стверджували протилежне — що інтеграційні тести виключені тут за
    # замовчуванням; це виправлено в документації, а не тут: додавання
    # фільтра зняло б кількасот тестів із CI без жодної заміни ЖОДНИМ іншим
    # кроком конвеєра, тобто тихо зменшило б покриття.
    #
    # ⚠ Локально без Docker і без `ECR_TEST_SQL` цей крок впаде на
    # інтеграційних тестах (`SqlServerFixture` не підніме контейнер) — тоді
    # свідомий вибір розробника — виклик `dotnet test` напряму з
    # `--filter "Category!=Integration"` (`09-commands.md` §2), а не зміна
    # цього скрипта.

    # ⛔ `--logger console;verbosity=normal` — не оздоба. З самим `-v q`
    # багатослівність журналу успадковує консольний логер, і в конвеєрі
    # лишається `[FAIL]` з іменем тесту БЕЗ повідомлення. Так сталося з
    # `OpenApiSnapshotTests`: тест уміє назвати перший розбіжний рядок, а
    # прочитати його було ніде — довелося відтворювати локально.
    #
    # ⚠ На зелених прогонах це нічого не додає: логер друкує подробиці лише
    # для падінь.
    #
    # ⛔ `Ecr.Scenarios.Tests` виключений ЯВНО. Директива №09 §6.7 навмисно
    # каже: «сценарії пишуться всі 28 одразу, і майже всі падають — це не
    # марна робота, це прилад». Гейт, який червоніє від очікуваного, — гейт,
    # який вимкнуть за тиждень. Прогрес сценаріїв — окремий, довідковий крок
    # нижче; те, що блокує збірку, лишається тут.
    #
    # ⛔ `Ecr.Api.Tests` теж виключений тут — і НЕ вимкнений: він іде двома
    # кроками нижче («Тести API (частина 1/2)»). Причина — замір конвеєра
    # 2026-09-27: у `Ecr.Api.Tests` усі тести в одній колекції `SqlServer`,
    # тобто ПОСЛІДОВНО, ~1 с на тест, 479 тестів — 9.2 хв; решта проєктів
    # закінчує за ~2 хв. Одне завдання тримало весь вердикт на цьому хвості.
    & dotnet test (Join-Path $root 'Ecr.sln') --no-build -v q --nologo `
        --logger 'console;verbosity=normal' `
        --filter 'FullyQualifiedName!~Ecr.Scenarios.Tests&FullyQualifiedName!~Ecr.Api.Tests.'
}

# ⛔ `Ecr.Api.Tests` — двома кроками, щоб конвеєр гнав їх ПАРАЛЕЛЬНО на двох
# агентах (кожен зі своїм SQL Server). Частина 2 — ТОЧНЕ доповнення частини
# 1: той самий перелік префіксів, заперечений. Тож тест не може випасти з
# обох частин і не може потрапити в обидві, хоч би які класи додавалися;
# новий клас лише зсуне баланс, а не покриття.
#
# ⚠ Склад частин і шардів — у `tools/api-test-shards.psd1` (замір
# 2026-09-28). До того тут стояла межа A–M / N–Z за першою літерою; тепер
# частина — це три шарди, і при `-ApiParallel 1` вона йде одним процесом із
# об'єднаним фільтром, як і раніше.
$apiShards = @((Import-PowerShellDataFile (Join-Path $PSScriptRoot 'api-test-shards.psd1')).Shards)

function Get-ApiClause {
    param([string] $Prefix)
    "FullyQualifiedName~Ecr.Api.Tests.$Prefix"
}

# ⛔ Перевірка даних шардів — ДО будь-якого прогону. Помилка в `.psd1`
# (два шарди-залишки, префікс, що є початком іншого) дала б тест у двох
# шардах або в жодному, і прогін при цьому лишився б зеленим.
$restShards = @($apiShards | Where-Object { $_.Rest })
if ($restShards.Count -ne 1 -or $restShards[0].Part -ne 2) {
    throw 'api-test-shards.psd1: шард-залишок (Rest) має бути рівно один і в частині 2.'
}
$allPrefixes = @($apiShards | Where-Object { -not $_.Rest } | ForEach-Object { $_.Prefixes })
foreach ($a in $allPrefixes) {
    foreach ($b in $allPrefixes) {
        if ($a -ne $b -and $b.StartsWith($a, [System.StringComparison]::Ordinal)) {
            throw "api-test-shards.psd1: префікс '$a' є початком '$b' — клас потрапив би у два шарди."
        }
    }
    if (@($allPrefixes | Where-Object { $_ -eq $a }).Count -gt 1) {
        throw "api-test-shards.psd1: префікс '$a' повторюється."
    }
}

$apiPart1 = @($apiShards | Where-Object { $_.Part -eq 1 } | ForEach-Object { $_.Prefixes } | ForEach-Object { Get-ApiClause $_ })

<#
.SYNOPSIS
    Фільтр для групи шардів, що йде одним процесом.
.DESCRIPTION
    Група без шарда-залишку — об'єднання її префіксів. Група із залишком —
    ЗАПЕРЕЧЕННЯ префіксів усіх шардів поза групою (з обох частин): так
    залишок разом із рештою групи береться одним фільтром, а повнота
    тримається тим самим прийомом, що й для частин.
#>
function Get-ApiGroupFilter {
    param([object[]] $Group)

    $ids = @($Group | ForEach-Object { $_.Id })
    if (@($Group | Where-Object { $_.Rest }).Count -gt 0) {
        $others = @($apiShards | Where-Object { $_.Id -notin $ids -and -not $_.Rest } |
            ForEach-Object { $_.Prefixes } | ForEach-Object { (Get-ApiClause $_) -replace '~', '!~' })
        return ($others -join '&')
    }

    return (@($Group | ForEach-Object { $_.Prefixes } | ForEach-Object { Get-ApiClause $_ }) -join '|')
}

<#
.SYNOPSIS
    Запускає частину `Ecr.Api.Tests` — одним процесом або K шардами.
#>
function Invoke-ApiPart {
    param([int] $Part)

    if ($TestSql) { $env:ECR_TEST_SQL = $TestSql }

    $filter = if ($Part -eq 1) { $apiPart1 -join '|' } else { ($apiPart1 -replace '~', '!~') -join '&' }

    if ($ApiParallel -le 1) {
        & dotnet test (Join-Path $root 'tests/Ecr.Api.Tests') --no-build -v q --nologo `
            --logger 'console;verbosity=normal' `
            --filter $filter
        return
    }

    if ($env:ECR_TEST_DB) {
        throw 'ECR_TEST_DB задано — при -ApiParallel > 1 усі процеси скидали б одну базу. Зніми змінну або став -ApiParallel 1.'
    }

    # ⚠ Саме зібрана DLL, а не каталог проєкту: `dotnet test <проєкт>` кличе
    # MSBuild, і кілька одночасних оцінок одного проєкту — зайвий ризик
    # блокувань `obj/`. DLL лише читається.
    $dll = Get-ChildItem (Join-Path $root 'tests/Ecr.Api.Tests/bin') -Recurse -Filter 'Ecr.Api.Tests.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -like 'net*' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $dll) { throw 'Ecr.Api.Tests.dll не знайдено — спершу крок «Складання».' }

    # Розподіл шардів частини між процесами — жадібно за заміряним часом
    # (найдовший шард — у найменш завантажений процес).
    $partShards = @($apiShards | Where-Object { $_.Part -eq $Part } | Sort-Object { [double] $_.Seconds } -Descending)
    $n = [Math]::Min($ApiParallel, $partShards.Count)
    $groups = @(1..$n | ForEach-Object { , @() })
    $load = @(1..$n | ForEach-Object { 0.0 })
    foreach ($s in $partShards) {
        $i = [Array]::IndexOf($load, ($load | Measure-Object -Minimum).Minimum)
        $groups[$i] += $s
        $load[$i] += [double] $s.Seconds
    }

    $logDir = Join-Path $root 'tests/Ecr.Api.Tests/TestResults/shards'
    New-Item -ItemType Directory -Force $logDir | Out-Null

    $previousShard = $env:ECR_TEST_SHARD
    $runs = @()
    try {
        foreach ($g in $groups) {
            $shard = ($g | ForEach-Object { $_.Id } | Measure-Object -Minimum).Minimum
            $log = Join-Path $logDir "shard-$shard.log"
            $err = Join-Path $logDir "shard-$shard.err"

            # ⚠ Змінна ставиться в ЦЬОМУ процесі перед запуском: дочірній
            # успадковує оточення в момент старту. Потім відновлюється.
            $env:ECR_TEST_SHARD = "$shard"
            $argLine = "test `"$($dll.FullName)`" --nologo --logger `"console;verbosity=normal`" --filter `"$(Get-ApiGroupFilter $g)`""
            $p = Start-Process -FilePath 'dotnet' -ArgumentList $argLine -NoNewWindow -PassThru `
                -RedirectStandardOutput $log -RedirectStandardError $err

            # ⛔ Без звернення до Handle одразу після старту Windows PowerShell
            # 5.1 віддає порожній ExitCode — і впалий шард читався б як зелений.
            $null = $p.Handle
            $runs += [pscustomobject]@{ Shard = $shard; Ids = ($g | ForEach-Object { $_.Id }) -join ','; Process = $p; Log = $log; Err = $err }
            Write-Host "   шард $shard (шарди $($runs[-1].Ids)) — PID $($p.Id), журнал $log"
        }
    }
    finally {
        $env:ECR_TEST_SHARD = $previousShard
    }

    $failed = 0
    $total = 0
    foreach ($r in $runs) {
        $r.Process.WaitForExit()
        $code = $r.Process.ExitCode
        # ⚠ З `console;verbosity=normal` підсумок має вигляд `Total tests: N`,
        # а не `Passed! … Total: N` (той — лише в тихому режимі). Беремо обидва.
        $summary = Select-String -Path $r.Log -Pattern 'Total(?: tests)?:\s+(\d+)' | Select-Object -Last 1
        if ($summary) { $total += [int] $summary.Matches[0].Groups[1].Value }
        $line = if ($summary) { $summary.Line.Trim() } else { '(підсумку в журналі немає)' }

        if ($code -ne 0 -or -not $summary) {
            $failed++
            Write-Host "   ✗ шард $($r.Shard) (код $code): $line" -ForegroundColor Red
            Get-Content $r.Log | ForEach-Object { Write-Host $_ }
            Get-Content $r.Err -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
        }
        else {
            Write-Host "   ✓ шард $($r.Shard): $line"
        }
    }

    Write-Host "   тестів у шардах частини $($Part): $total"
    $global:LASTEXITCODE = if ($failed -gt 0) { 1 } else { 0 }
}

Step 'Тести API (частина 1)' { Invoke-ApiPart -Part 1 }

Step 'Тести API (частина 2)' { Invoke-ApiPart -Part 2 }

Step 'Сценарії директиви №09 (довідково)' {
    if ($TestSql) {
        $env:ECR_TEST_SQL = $TestSql
    }

    # ⛔ Гейт (директива №10 `DIR-006`), НАЗВА лишена як була. Доти крок був
    # довідковим навмисно: директива №09 §2 веде число «S: N із 28» як
    # окрему одиницю обліку прогресу, і змішати його з бінарним
    # «пройшло / не пройшло» означало б вимкнути крок, щойно перших
    # дванадцять сценаріїв стали червоними по суті (авторства структури
    # шаблону не було в API, `S-04`/`S-05`). Тепер усі 32 зелені — і саме
    # тому негейт більше нічого не захищає, а лише ховає регресію: жодна
    # перевірка в CI не впала б, якби зламався сценарій, доки цей крок ловив
    # помилку в `try/catch` і йшов далі.
    #
    # ⚠ «(довідково)» в імені кроку тепер бреше — крок гейт, а не довідка.
    # Перейменувати чесно не можу сам: `.github/workflows/ci.yml` бере цей
    # крок за точним рядком у `-Only`, а `.github/**` — не моя зона.
    # Записано `ASK` у `PK2-LOG.md`.
    #
    # ⚠ `try/catch` лишається — НЕ про акуратність. `$ErrorActionPreference =
    # 'Stop'` разом із `$PSNativeCommandUseErrorActionPreference` (pwsh
    # 7.3+) робить ненульовий код виходу нативної команди ТЕРМІНУЮЧИМ
    # винятком: без catch він вилітає зі скрипт-блока так різко, що звичний
    # для решти кроків шлях `if ($LASTEXITCODE -ne 0) { throw ... }` у
    # функції `Step` навіть не встигає спрацювати. Зловлений виняток тепер
    # НЕ проковтується — перекидається далі (`throw`), і саме це робить
    # крок кроком-гейтом: зовнішній `catch` у `Step` (рядок ~158) реєструє
    # його як провал так само, як і будь-який інший.
    #
    # ⛔ NITPICK з `VERDICT-002`: без бази `SqlServerFixture` падає в
    # ІНІЦІАЛІЗАЦІЇ, і крок кричить «код виходу N» так само, як на реальному
    # провалі сценарію — розробник без Docker і без `ECR_TEST_SQL` бачить
    # червоний гейт без натяку, що причина не в сценаріях. Перевірка НАПЕРЕД,
    # а не розбір виводу заднім числом: розбір тексту тесту крихкий (формат
    # логера — не контракт), а «є SQL чи нема» — питання, на яке можна
    # відповісти до запуску. Пропуску кроку тут НЕМАЄ (`ANSWER-003`): якщо
    # бази нема, крок падає з поясненням, а не мовчить зеленим.
    if (-not $env:ECR_TEST_SQL) {
        $dockerOk = $false
        if (Get-Command docker -ErrorAction SilentlyContinue) {
            # ⚠ `docker info` теж нативна команда: під тим самим
            # `$PSNativeCommandUseErrorActionPreference` її ненульовий вихід
            # (Docker не запущений) сам стає термінуючим винятком РАНІШЕ, ніж
            # `$LASTEXITCODE` встигає прочитатися. Тому свій try/catch —
            # відсутність Docker тут це `$false`, а не привід впасти самою
            # перевіркою замість зрозумілим повідомленням нижче.
            try {
                & docker info *> $null
                $dockerOk = $LASTEXITCODE -eq 0
            }
            catch {
                $dockerOk = $false
            }
        }

        if (-not $dockerOk) {
            throw 'сценарії директиви №09: немає ні $env:ECR_TEST_SQL, ні робочого Docker ' +
                  '(Testcontainers підніме контейнер лише з Docker). Задайте ECR_TEST_SQL ' +
                  '(рядок підключення до локального SQL Server) або пропустіть цей крок: ' +
                  "-Only без 'Сценарії директиви №09 (довідково)'."
        }
    }

    try {
        & dotnet test (Join-Path $root 'tests/Ecr.Scenarios.Tests') --no-build -v q --nologo `
            --logger 'console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) {
            throw "сценарії директиви №09: код виходу $LASTEXITCODE"
        }
    }
    catch {
        throw
    }
}

if (-not $SkipDeployment) {
    Step 'Розгортання під sqlcmd' {
        $sqlArgs = @()
        if ($SqlServer) { $sqlArgs += @('-Server', $SqlServer) }
        if ($SqlLogin) { $sqlArgs += @('-Login', $SqlLogin, '-Password', $SqlPassword) }

        Invoke-Child -Script 'verify-sql-scripts.ps1' -Arguments $sqlArgs
    }
}

if (-not $SkipDeployment) {
    # ⛔ Останнім кроком і навмисно: це єдина перевірка, яка запускає ЖИВИЙ
    # процес і проходить шлях користувача цілком. Саме він ламався в семи
    # місцях і не падав у жодному (`A7-25`…`A7-30`) при 616 зелених тестах.
    Step 'Наскрізний сценарій' {
        Invoke-Child -Script 'smoke.ps1'
    }
}

if (-not $SkipClient) {
    Push-Location $client
    try {
        # ⚠ Саме `npm run`, а не `npx`: перевірки мусять запускати ТІ САМІ
        # команди, що й розробник (`package.json` §scripts). Крім того, `npx`
        # із PowerShell не знаходить локально встановлених пакетів і мовчки
        # падає з «could not determine executable to run».
        # ⛔ Гейт безпеки (D-143). Перевіряються ЛИШЕ ті залежності, що
        # потрапляють до користувача: `--omit=dev`. Вразливість у
        # `react-router-dom` 7.1.1 (XSS через відкриті редіректи в `<Link>`)
        # знайшлася вручну — далі це робить машина.
        #
        # ⚠ Для dev-залежностей гейт НЕ ставиться: він блокував би роботу
        # через чужі релізи, і його вимкнули б разом із робочим. Їхній стан
        # виводиться довідково нижче.
        Step 'Безпека залежностей' { & $npm audit --omit=dev --audit-level=high }

        # ⛔ Гейт ліцензій (D-12, D-82): усі пакети lock-файла, разом із dev.
        Step 'Ліцензії npm' { & node (Join-Path $root 'tools/license-check.mjs') npm }

        Step 'Аудит інструментів (довідково)' {
            & $npm audit
            # ⚠ Ненульовий код тут НЕ є помилкою: це довідка, а не гейт.
            $global:LASTEXITCODE = 0
        }

        # ⛔ Типи клієнта РЕГЕНЕРУЮТЬСЯ зі знімка контракту і мають збігтися з
        # тим, що в репозиторії (D-137). Розбіжність означає, що знімок
        # оновили, а типи — ні: рівно той стан, у якому клієнт описує форму
        # відповіді сам і розходиться з сервером мовчки (`A7-36`).
        Step 'Типи клієнта зі знімка' {
            & $npm run api:types
            if ($LASTEXITCODE -ne 0) { return }

            & git -C $root diff --exit-code -- 'src/Ecr.Web/src/api/schema.d.ts'
            if ($LASTEXITCODE -ne 0) {
                throw 'schema.d.ts розійшовся зі знімком OpenAPI — перегенеруй і закоміть.'
            }
        }

        Step 'Типи клієнта' { & $npm run typecheck }
        Step 'Стиль клієнта' { & $npm run lint }
        Step 'Тести клієнта' { & $npm run test }

        # ⛔ Доступність — окремим кроком, бо повільна: замір 2026-09-07 дає
        # **975 с на повний обхід** без конкуренції і 1372 с під навантаженням,
        # а найважчий маршрут (`/admin/expressions`) — до 266 с сам по собі.
        #
        # ⚠ Тут стояла оцінка «близько 35 секунд на сторінку». Вона була
        # застаріла, і саме під неї поставили межу 120 с, від якої гейт потім
        # червонів не з тієї причини, яку стереже (`Q-143`). Число замінене на
        # ЗАМІР із датою навмисно: оцінка без дати не старіє на вигляд. Усередині звичайного `npm test` це
        # додавало б хвилини до кожного прогону під час роботи, і перевірку
        # зрештою вимкнули б. Поріг блокуючий: нуль `critical` і `serious`
        # на КОЖНОМУ маршруті (ФВ-14.16, D-127).
        Step 'Доступність клієнта' { & $npm run test:a11y }
        Step 'Збірка клієнта' { & $npm run build }

        # ⛔ Гейт бюджету (`D-132`). До нього бюджет був записаний у двох
        # документах і не перевірявся ніде: `07-checkpoints.md` стверджував,
        # що його стереже `npm run build`, а той друкує розміри і виходить із
        # нулем незалежно від них. Обґрунтування самого рішення — «бюджет,
        # який не перевіряють, не існує» — описувало власний стан.
        #
        # ⚠ Обов'язково ПІСЛЯ збірки: гейт зважує `dist/`, а не вихідний код.
        Step 'Бюджет клієнта' { & $npm run budget }
    }
    finally {
        Pop-Location
    }
}

if (-not $SkipClient -and -not $SkipDeployment) {
    # ⛔ Прогони у СПРАВЖНЬОМУ браузері — останніми і найдорожчими
    # (близько чотирьох хвилин). Три речі з ЕТАПУ 7.5 неможливі в jsdom, бо
    # в ньому немає ані розкладки, ані пікселів:
    #   — структурна різниця станів комірки після знеколірення (`D-140`);
    #   — прохід оператора без миші з виміром кільця фокуса (`D-141`);
    #   — знімки маршрутів під двома ролями × темами × щільностями (`D-142`).
    #
    # ⚠ Стенд піднімає власну базу і власний застосунок і видаляє їх за
    # собою. Крок і далі кличе САМЕ стенд, а не `npm run test:e2e`: без стенда
    # перевіряти нічого, і команда це тепер каже вголос.
    #
    # ⛔ Тут стояло «без стенда прогони мовчки пропускаються (`test.skip`)» —
    # і так воно й було: `npm run test:e2e` виходив нулем, виконавши 3 прогони
    # з 20. Тепер набір без стенда ПАДАЄ (`src/Ecr.Web/e2e/globalSetup.ts`),
    # тобто правильна поведінка більше не тримається на одному цьому рядку.
    Step 'Прогони в браузері' {
        Invoke-Child -Script 'e2e-stand.ps1'
    }
}

if ($ListSteps) {
    $declared | ForEach-Object { Write-Output $_ }
    exit 0
}

Write-Host ''

# ⛔ Названий крок, якого не існує, — це друкарська помилка в конвеєрі, і без
# цієї перевірки вона виглядала б як зелений прогін: `-Only 'Тести .NET '`
# із зайвим пробілом просто не виконав би нічого.
$unknown = $Only | Where-Object { $_ -notin $declared }
if ($unknown) {
    Write-Host "Немає таких кроків: $($unknown -join ', ')" -ForegroundColor Red
    exit 2
}

if ($failures.Count -gt 0) {
    Write-Host "Невдалих перевірок: $($failures.Count)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  • $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Усі перевірки пройдено.' -ForegroundColor Green
