<#
.SYNOPSIS
    Наскрізний сценарій на ЖИВОМУ процесі: від чистої бази до відкритої книги.

.DESCRIPTION
    Скрипт існує через `A7-25`…`A7-30`. Цей шлях ламався в СЕМИ місцях і не
    падав у жодному: проєкт не можна було активувати, тому періоди не
    відкривалися; грант не міг створити ніхто, тому переліки були порожні;
    документ із API не мав таблиць; перевірка перед поданням нічого не
    перевіряла; експорт падав завжди. При 616 зелених тестах.

    ⛔ Кожен крок падає одразу, щойно отримав не той код або порожню
    відповідь. Найважливіші дві перевірки — не коди, а ЗМІСТ:
      * валідація мусить повернути `periodKey`, який просили, а не нуль
        (`A7-28`: сервер читав період не звідти, і перевірка йшла по
        неіснуючому періоду, відповідаючи «помилок немає»);
      * вивантажена книга мусить МІСТИТИ введене значення (`A7-29`: експорт
        падав на реальному документі, а порожня книга виглядала б як успіх).

.PARAMETER Server
    Екземпляр SQL Server.

.PARAMETER Database
    Тимчасова база; створюється і видаляється цим скриптом.

.PARAMETER Port
    Порт, на якому підняти застосунок.

.PARAMETER DataPath
    Каталог файлів бази; передається в `setup-dev-db.ps1`. Не задано — діє
    його умовчання (`H:\EcrData`, якщо є `H:`); `''` — типовий каталог
    інстансу. Потрібен, коли на `H:` бракує ~16 ГБ, а на іншому диску є.

.PARAMETER RequireFreeGb
    Скільки вільного місця вимагати на диску даних; передається в
    `setup-dev-db.ps1`. Не задано — обчислюється за профілем; `0` — без перевірки.

.EXAMPLE
    powershell -File tools/smoke.ps1
    powershell -File tools/smoke.ps1 -Server localhost -DataPath F:\EcrData
#>
[CmdletBinding()]
param(
    [string] $Server = 'localhost\SQLEXPRESS',
    [string] $Database = 'EcrSmoke',
    [int] $Port = 5099,

    # ⚠ Без умовчання навмисно: передається далі ЛИШЕ якщо задано явно, тож
    # запуск без параметра поводиться рівно як раніше (умовчання вирішує
    # `setup-dev-db.ps1`, а не дублюється тут і не розходиться з ним).
    [string] $DataPath,
    [double] $RequireFreeGb = -1
)

# ⚠ Масив аргументів, а не сплат: `setup-dev-db.ps1` викликається окремим
# процесом. Порожній рядок PowerShell 5.1 нативній команді не передає, тож
# `-DataPath ''` («типовий каталог інстансу») їде як `""`.
$setupExtra = @()
if ($PSBoundParameters.ContainsKey('DataPath')) {
    $setupExtra += @('-DataPath', $(if ($DataPath) { $DataPath } else { '""' }))
}
if ($PSBoundParameters.ContainsKey('RequireFreeGb')) {
    $setupExtra += @('-RequireFreeGb', [string] $RequireFreeGb)
}

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$base = "http://localhost:$Port"
$password = 'Smoke-Bootstrap-2026!'
$step = 0

function Step {
    param([string] $Name)

    $script:step++
    Write-Host ("  {0,2}. {1}" -f $script:step, $Name)
}

function Fail {
    param([string] $Message)

    throw "smoke: крок $script:step — $Message"
}

# ⚠ Власна сесія з cookie: застосунок працює на cookie-автентифікації, і
# без спільного контейнера кожен виклик був би анонімним.
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Call {
    param(
        [string] $Method,
        [string] $Path,
        $Body,
        [int[]] $Expect = @(200, 201, 204)
    )

    $arguments = @{
        Uri             = "$base$Path"
        Method          = $Method
        WebSession      = $session
        UseBasicParsing = $true
        TimeoutSec      = 120
    }

    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json; charset=utf-8'
        $arguments.Body = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8 -Compress))
    }

    try {
        $response = Invoke-WebRequest @arguments
    }
    catch {
        $code = $_.Exception.Response.StatusCode.value__
        Fail "$Method $Path повернув $code, очікувалося $($Expect -join '/')"
    }

    if ($response.StatusCode -notin $Expect) {
        Fail "$Method $Path повернув $($response.StatusCode), очікувалося $($Expect -join '/')"
    }

    if ($response.Content) { return $response.Content | ConvertFrom-Json }

    return $null
}

Write-Host ''
Write-Host "Наскрізний сценарій на базі $Database" -ForegroundColor Cyan

# ── Розгортання ──────────────────────────────────────────────────────────
Step 'чиста база і розгортання через sqlcmd'
# ⚠ `-Documents 1` не для обсягу, а заради СТРУКТУРИ ШАБЛОНУ: створити
# аркуші, таблиці й колонки через API неможливо — структура приходить із
# `tools/Ecr.Bootstrap.Excel`, який поки заглушка. Це відома межа сценарію, а
# не спрощення: усе, що після структури, іде саме по HTTP.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'setup-dev-db.ps1') `
        -Server $Server -Database $Database -Documents 1 -BootstrapPassword $password @setupExtra | Out-Null
}
finally {
    $ErrorActionPreference = $previousEap
}
if ($LASTEXITCODE -ne 0) { Fail 'розгортання не пройшло' }

# ⛔ Q-222 (аудит): без цієї позначки прибирання нижче видаляло б БУДЬ-ЯКУ
# базу, названу в `-Database`, — включно з чиєюсь справжньою dev-базою
# (`setup-dev-db.ps1` так само обслуговує персистентні бази розробників,
# не лише одноразові). Той самий сторож, що вже в `e2e-stand.ps1`/
# `br07-load-test.ps1`.
$previousEapTag = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & sqlcmd -S $Server -E -C -b -d $Database `
        -Q "EXEC sys.sp_addextendedproperty @name = N'Ecr_Smoke_Temp', @value = 1;" | Out-Null
}
finally {
    $ErrorActionPreference = $previousEapTag
}
if ($LASTEXITCODE -ne 0) { Fail 'не вдалося позначити тимчасову базу' }

$connection = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
$log = Join-Path $root 'artifacts/smoke.api.log'

$env:ECR_ConnectionStrings__Ecr = $connection
$env:ECR_Bootstrap__Password = $password
$env:ASPNETCORE_URLS = $base

# ⛔ Сценарій іде по HTTP, а cookie автентифікації типово позначена `Secure` і
# по HTTP не надсилається — вхід проходив би, а наступний виклик отримував 401.
# Це НЕ послаблення проду: змінна діє лише на цей тимчасовий процес.
$env:ECR_Auth__RequireHttps = 'false'

Step 'старт застосунку'
$api = Start-Process -PassThru -WindowStyle Hidden dotnet `
    -ArgumentList "run --project `"$(Join-Path $root 'src/Ecr.Api')`" --no-build --no-launch-profile" `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err"

try {
    $ready = $false
    foreach ($i in 1..120) {
        Start-Sleep -Milliseconds 500
        if ($api.HasExited) { Fail "застосунок завершився з кодом $($api.ExitCode); лог: $log" }

        try {
            if ((Invoke-WebRequest -Uri "$base/health/live" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch { }
    }

    if (-not $ready) { Fail "застосунок не піднявся; лог: $log" }

    # ── Первинне налаштування ────────────────────────────────────────────
    Step 'вхід bootstrap разовим паролем'
    Call POST '/api/v1/login/local' @{ userName = 'bootstrap'; password = $password } | Out-Null

    Step 'зміна разового пароля'
    Call POST '/api/v1/auth/change-password' `
        @{ currentPassword = $password; newPassword = 'Smoke-Real-2026!' } | Out-Null

    # ⛔ Вхід ОДРАЗУ після зміни: `A7-21` — кеш штампа тримав старе значення,
    # і застосунок виходив із сеансу, який щойно створив.
    Step 'вхід новим паролем одразу після зміни'
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'bootstrap'; password = 'Smoke-Real-2026!' } | Out-Null
    Call GET '/api/v1/me' | Out-Null

    Step 'роль із небезпечними правами'
    Call POST '/api/v1/roles' @{
        code            = 'SmokeOperator'
        nameL10n        = @{ en = 'Smoke operator' }
        # ⚠ Три права `Report.*` — заради кроку «зріз звітності» наприкінці.
        # Вивантаження зрізу вимагає саме `Report.Export`, окремо від
        # `Report.ViewRegulatory`: книга ВИХОДИТЬ ІЗ СИСТЕМИ (`R7`), і сценарій
        # мусить іти тим самим шляхом, що й оператор, а не в обхід права.
        permissionCodes = @(
            'Template.View', 'Template.Edit', 'Template.Publish',
            'Document.View', 'Document.Create', 'Document.Export',
            'Project.Manage', 'Period.Configure',
            'Calculation.View', 'Calculation.Publish', 'Calculation.Recalculate',
            'Report.ViewRegulatory', 'Report.BuildSnapshot', 'Report.Export',
            'Security.ManageUsers', 'Security.ManageRoles', 'System.ViewHealth')
    } | Out-Null

    $roles = Call GET '/api/v1/roles'
    $roleId = ($roles | Where-Object { $_.code -eq 'SmokeOperator' }).id
    if (-not $roleId) { Fail 'роль не створилася' }

    Step 'іменований користувач'
    Call POST '/api/v1/users' @{
        userName        = 'smoke'
        provider        = 'Local'
        sid             = $null
        displayName     = 'Smoke operator'
        initialPassword = 'Smoke-Operator-2026!'
        roleCodes       = @('SmokeOperator')
    } | Out-Null

    # ⚠ Далі все робить ОПЕРАТОР. Bootstrap має рівно два права (`D-121`): він
    # передає систему людям і більше нічого не вміє — спроба створити ним
    # проєкт дає 403, і це правильно.
    Step 'вхід оператором і зміна разового пароля'
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'smoke'; password = 'Smoke-Operator-2026!' } | Out-Null
    Call POST '/api/v1/auth/change-password' `
        @{ currentPassword = 'Smoke-Operator-2026!'; newPassword = 'Smoke-Work-2026!' } | Out-Null

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'smoke'; password = 'Smoke-Work-2026!' } | Out-Null

    # ⛔ Без гранта перелік порожній для всіх, включно з власником усіх прав
    # (`A7-22`). Оператор має `Security.ManageRoles` і видає грант своїй ролі.
    Step 'ресурсний грант на проєкт'
    Call PUT "/api/v1/roles/$roleId/grants" @{
        grants = @(@{ resourceKind = 'Project'; resourceId = 1; level = 'Manage'; isDeny = $false })
    } | Out-Null

    # ⚠ Грант прокручує штамп безпеки носіям ролі (`A7-23`) — сеанс треба
    # перевидати, як це зробить браузер, отримавши 401.
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'smoke'; password = 'Smoke-Work-2026!' } | Out-Null

    Step 'проєкт видно у переліку'
    $projects = Call GET '/api/v1/projects'
    if ($projects.items.Count -eq 0) { Fail 'перелік проєктів порожній: грант не діє' }

    $projectId = $projects.items[0].id

    Step 'календар періодів'
    $calendar = Call GET "/api/v1/projects/$projectId/periods"
    if ($calendar.periods.Count -eq 0) { Fail 'календар порожній' }

    # ⚠ Відкритий період шукається СЕРЕД ПОВЕРНУТИХ, а не задається числом:
    # інакше сценарій ламався б щороку в січні.
    $open = $calendar.periods | Where-Object { $_.state -eq 'Open' } | Select-Object -First 1
    if (-not $open) { Fail 'жодного відкритого періоду: стани не вирівняні (A7-24, A7-25)' }

    $periodKey = $open.periodKey

    Step "документ за відкритий період $periodKey"
    $documents = Call GET "/api/v1/documents?periodKey=$periodKey"
    if ($documents.items.Count -eq 0) { Fail 'документів немає' }

    $documentId = $documents.items[0].id

    # ⛔ Без екземплярів таблиць документ порожній назавжди (`A7-30`).
    Step 'таблиці документа'
    $tables = Call GET "/api/v1/documents/$documentId/tables?periodKey=$periodKey"
    if ($tables.Count -eq 0) { Fail 'у документа немає жодної таблиці' }

    $instance = $tables[10].tableInstanceId

    Step 'зріз таблиці'
    $slice = Call GET "/api/v1/documents/$documentId/tables/$instance`?periodKey=$periodKey"
    if ($slice.rows.Count -eq 0) { Fail 'у зрізі немає рядків' }

    $row = $slice.rows[0]

    Step 'запис комірок через HTTP'
    Call PATCH "/api/v1/documents/$documentId/cells" @{
        tableInstanceId = $instance
        periodKey       = $periodKey
        origin          = 'UserEdit'
        rows            = @(@{
            rowKey      = $row.rowKey
            baseVersion = $row.rowVersion
            cells       = @(
                @{ columnCode = 'C2'; value = 4242.42 },
                @{ columnCode = 'C1'; value = 'наскрізна перевірка' })
        })
    } | Out-Null

    # ⛔ Читання назад: число мусить лишитися ЧИСЛОМ (`A7-01`), а лягти саме в
    # ту таблицю, куди писали (`A7-27`).
    #
    # ⚠ На дроті воно тепер РЯДКОМ (`D-30`): JSON-число на клієнті проходить
    # через `JSON.parse` і втрачає знаки за межею IEEE-754. Тому звіряється
    # ЗНАЧЕННЯ, а не текст — «4242.4200000000» несе масштаб колонки
    # `decimal(28,10)` і рівне тому, що записали.
    Step 'читання назад'
    $after = Call GET "/api/v1/documents/$documentId/tables/$instance`?periodKey=$periodKey"
    $written = ($after.rows | Where-Object { $_.rowKey -eq $row.rowKey }).cells.C2

    if ([decimal] $written -ne [decimal] 4242.42) { Fail "прочитано '$written' замість 4242.42" }

    # ⛔ Перерахунок і ЧЕКАННЯ КІНЦЕВОГО СТАНУ, а не самого лише `202`. Задача
    # ставилася в чергу з payload, що губив `ProjectId`, і `SaveChangesAsync`
    # на створенні `CalculationRun` падав до `try`: `catch`, який мав
    # позначити `Failed`, не спрацьовував, і задача лишалася `Running`
    # назавжди без жодного видимого сліду (директива №09 §1.3, `S-25`; `W3`).
    Step 'перерахунок і очікування кінцевого стану'
    $recalc = Call POST "/api/v1/documents/$documentId/recalculate" @{ periodKey = $periodKey } -Expect @(202)

    $recalcState = $null
    foreach ($i in 1..120) {
        Start-Sleep -Milliseconds 500
        $recalcStatus = Call GET "/api/v1/jobs/$($recalc.jobId)"
        $recalcState = $recalcStatus.state
        if ($recalcState -in @('Succeeded', 'Failed')) { break }
    }

    if ($recalcState -ne 'Succeeded') { Fail "перерахунок завершився станом '$recalcState': $($recalcStatus.error)" }

    # ⛔ Валідація мусить повернути ТОЙ САМИЙ період. `A7-28`: сервер читав його
    # не звідти, отримував нуль і відповідав «помилок немає», нічого не
    # перевіривши.
    Step 'валідація за той самий період'
    $validation = Call POST "/api/v1/documents/$documentId/validate" @{ periodKey = $periodKey }
    if ($validation.periodKey -ne $periodKey) {
        Fail "валідація повернула період $($validation.periodKey) замість $periodKey"
    }

    Step 'подання аркуша'
    Call POST "/api/v1/documents/$documentId/submit" `
        @{ sheetDefId = $tables[0].sheetDefId; periodKey = $periodKey } | Out-Null

    Step 'експорт у чергу'
    $job = Call POST "/api/v1/documents/$documentId/export" @{
        includeFormulas = $true
        includeStyles   = $true
        language        = 'en'
        periodKey       = $periodKey
    } -Expect @(202)

    $state = $null
    foreach ($i in 1..120) {
        Start-Sleep -Milliseconds 500
        $status = Call GET "/api/v1/jobs/$($job.jobId)"
        $state = $status.state
        if ($state -in @('Succeeded', 'Failed')) { break }
    }

    if ($state -ne 'Succeeded') { Fail "експорт завершився станом '$state': $($status.error)" }

    Step 'вивантаження книги'
    $book = Join-Path $root 'artifacts/smoke-book.xlsx'
    Invoke-WebRequest -Uri "$base/api/v1/documents/$documentId/export/$($status.message)" `
        -WebSession $session -UseBasicParsing -OutFile $book | Out-Null

    # ⛔ Книгу ВІДКРИВАЄМО. Порожня книга виглядає як успіх — саме так `A7-29`
    # дожив до реального обсягу, а до нього експорт віддавав аркуш без жодної
    # комірки.
    # ⛔ Книгу РОЗБИРАЄМО. Порожня книга виглядає як успіх — саме так `A7-29`
    # дожив до реального обсягу: експорт віддавав аркуш без жодної комірки, а
    # задача звітувала «Succeeded».
    #
    # ⚠ Через zip, а не ClosedXML: цей скрипт виконує Windows PowerShell 5.1 на
    # .NET Framework, і збірку під .NET 10 він не завантажить. Формат `.xlsx`
    # і є zip — розбирати його тут чесніше, ніж тягнути другий рантайм.
    Step 'книга розбирається і містить введене значення'
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($book)
    try {
        function Read-Entry {
            param([string] $Name)

            $entry = $archive.Entries | Where-Object { $_.FullName -eq $Name }
            if (-not $entry) { return '' }

            $stream = $entry.Open()
            try {
                $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            finally { $stream.Dispose() }
        }

        $sheets = @($archive.Entries | Where-Object { $_.FullName -like 'xl/worksheets/*.xml' })
        if ($sheets.Count -eq 0) { Fail 'у книзі немає жодного аркуша' }

        # ⚠ Елементи з префіксом простору імен (`<x:c `), а не `<c `: ClosedXML
        # пише саме так, і наївний пошук дав би нуль на цілком коректній книзі.
        $cells = 0
        foreach ($sheet in $sheets) {
            $cells += ([regex]::Matches((Read-Entry $sheet.FullName), '<x:c ')).Count
        }

        if ($cells -lt 2) { Fail "у книзі $cells комірок" }

        $strings = Read-Entry 'xl/sharedStrings.xml'
        if ($strings -notmatch 'наскрізна перевірка') {
            Fail 'у книзі немає введеного значення'
        }

        Write-Host "      комірок у книзі: $cells"
    }
    finally {
        $archive.Dispose()
    }

    # ── Зріз звітності ───────────────────────────────────────────────────
    # ⚠ Другий шлях вивантаження, і він ІНШИЙ: книга зрізу приходить
    # відповіддю на `GET`, без `202` і фонової задачі (`R7`). Усе, що
    # перевірено вище, про нього не говорить нічого — там черга, тут потік у
    # відповіді й окреме право `Report.Export`.
    Step 'побудова зрізу звіту'
    $snapshotJob = Call POST '/api/v1/reports/IEC/build' @{
        projectId = $projectId
        periodKey = $periodKey
    } -Expect @(202)

    # ⚠ Стеля очікування — 300 с, і це не «про всяк випадок». Ретрай задачі
    # спить 30 с, потім 60 с (`QuartzJobAdapter.MaxRetryAttempts` = 3), тобто
    # задача, що падає з першого разу, доходить до `Failed` аж на ~95-й
    # секунді. Стеля в 60 с обривала прогін РАНІШЕ, ніж стан ставав кінцевим,
    # і причина провалу не потрапляла в повідомлення взагалі — перевірка
    # казала «завершилася станом Running», тобто рівно те, чого бути не може.
    $snapshotState = $null
    $snapshotWait = [System.Diagnostics.Stopwatch]::StartNew()
    while ($snapshotWait.Elapsed.TotalSeconds -lt 300) {
        Start-Sleep -Milliseconds 500
        $snapshotStatus = Call GET "/api/v1/jobs/$($snapshotJob.jobId)"
        $snapshotState = $snapshotStatus.state
        if ($snapshotState -in @('Succeeded', 'Failed', 'Cancelled')) { break }
    }

    $snapshotWait.Stop()

    if ($snapshotState -ne 'Succeeded') {
        Fail ("побудова зрізу завершилася станом '$snapshotState' за " +
            "$([math]::Round($snapshotWait.Elapsed.TotalSeconds)) с: $($snapshotStatus.error)")
    }

    # Час побудови друкуємо завжди: «зелено, але 4 хвилини» — теж знахідка.
    Write-Host "      зріз побудовано за $([math]::Round($snapshotWait.Elapsed.TotalSeconds, 1)) с"

    $snapshots = @(Call GET "/api/v1/reports/snapshots?projectId=$projectId&periodKey=$periodKey")
    if ($snapshots.Count -eq 0) { Fail 'зрізів немає, хоча побудова відзвітувала успіх' }

    # Перелік іде найновішими вперед — щойно побудований зріз перший.
    $snapshot = $snapshots[0]
    $page = Call GET "/api/v1/reports/snapshots/$($snapshot.id)/rows?limit=100"
    $columns = @($page.columns | ForEach-Object { $_.code })
    if ($columns.Count -eq 0) { Fail 'зріз не називає жодної колонки' }

    # ⛔ Очікуване беремо з `GET …/rows`, а не з голови: книга мусить містити
    # ТЕ САМЕ, що застосунок віддає рядками. Літерал тут довів би лише те, що
    # хтось колись його сюди вписав.
    $expected = @($columns)
    if (@($page.rows).Count -gt 0) {
        $first = @($page.rows)[0]
        foreach ($column in @($page.columns | Where-Object { $_.kind -eq 'text' })) {
            $value = $first.cells.$($column.code)
            if ($value -is [string] -and $value.Trim()) { $expected += $value }
        }
    }

    Step 'книга зрізу розбирається і містить значення зрізу'
    $snapshotBook = Join-Path $root 'artifacts/smoke-snapshot.xlsx'
    Invoke-WebRequest -Uri "$base/api/v1/reports/snapshots/$($snapshot.id)/export.xlsx" `
        -WebSession $session -UseBasicParsing -OutFile $snapshotBook | Out-Null

    $snapshotArchive = [System.IO.Compression.ZipFile]::OpenRead($snapshotBook)
    try {
        # ⚠ Власна читалка, а не `Read-Entry` вище: та тримає ПОПЕРЕДНІЙ архів
        # змінною, і після його закриття мовчки читала б закритий потік.
        function Read-SnapshotEntry {
            param($Archive, [string] $Name)

            $entry = $Archive.Entries | Where-Object { $_.FullName -eq $Name }
            if (-not $entry) { return '' }

            $stream = $entry.Open()
            try {
                $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            finally { $stream.Dispose() }
        }

        $snapshotSheets = @($snapshotArchive.Entries | Where-Object { $_.FullName -like 'xl/worksheets/*.xml' })
        if ($snapshotSheets.Count -ne 1) { Fail "у книзі зрізу $($snapshotSheets.Count) аркушів замість одного" }

        $sheetXml = Read-SnapshotEntry $snapshotArchive $snapshotSheets[0].FullName
        $snapshotStrings = Read-SnapshotEntry $snapshotArchive 'xl/sharedStrings.xml'

        # ⛔ Рядків рівно стільки, скільки в зрізі, плюс заголовок. «Не порожня»
        # книга виглядала б так само і з половиною рядків.
        $bookRows = ([regex]::Matches($sheetXml, '<x:row ')).Count
        if ($bookRows -ne ($snapshot.rowCount + 1)) {
            Fail "у книзі зрізу $bookRows рядків, а зріз має $($snapshot.rowCount) плюс заголовок"
        }

        foreach ($value in $expected) {
            $escaped = [System.Security.SecurityElement]::Escape($value)
            if ($snapshotStrings -notmatch [regex]::Escape($escaped)) {
                Fail "у книзі зрізу немає значення «$value»"
            }
        }

        Write-Host "      рядків у книзі зрізу: $bookRows; звірено значень: $($expected.Count)"
    }
    finally {
        $snapshotArchive.Dispose()
    }

    Write-Host ''
    Write-Host 'Наскрізний сценарій пройдено.' -ForegroundColor Green
}
finally {
    if ($api -and -not $api.HasExited) { $api.Kill(); $api.WaitForExit() }

    # ⛔ Q-222 (аудит): видаляється ЛИШЕ база з власною позначкою
    # (Ecr_Smoke_Temp, вище) — без цієї умови скрипт знищував би будь-що,
    # назване в `-Database`. Два простих запити замість одного складеного:
    # питання «чи існує» — до `master`, «чи моя» — до самої бази.
    $previousEapExists = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $exists = (& sqlcmd -S $Server -E -C -b -h -1 -W -d master `
            -Q "SET NOCOUNT ON; SELECT CASE WHEN DB_ID('$Database') IS NULL THEN 0 ELSE 1 END;") `
            | Select-Object -Last 1
    }
    finally {
        $ErrorActionPreference = $previousEapExists
    }

    if ($exists -eq '1') {
        $previousEapMine = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $mine = (& sqlcmd -S $Server -E -C -b -h -1 -W -d $Database `
                -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = N'Ecr_Smoke_Temp';") `
                | Select-Object -Last 1
        }
        finally {
            $ErrorActionPreference = $previousEapMine
        }

        if ($mine -eq '1') {
            $previousEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & sqlcmd -S $Server -E -C -b -Q "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database];" | Out-Null
            }
            finally {
                $ErrorActionPreference = $previousEap
            }
        }
    }
}
