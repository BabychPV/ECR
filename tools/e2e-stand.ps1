<#
.SYNOPSIS
    Піднімає стенд із живою базою і виконує прогони Playwright.

.DESCRIPTION
    ЕТАП 7.5 пункти 9 і 10 неможливі без ВХОДУ: прохід оператора клавіатурою і
    знімки маршрутів під двома ролями починаються зі сторінки, за якою стоїть
    сесія. `cellStates.spec.ts` обходився `/_kitchen-sink`, який працює до
    входу; решта — ні.

    ⛔ Стенд робить рівно те, що `smoke.ps1`, і зупиняється там, де той
    починає перевіряти: чиста база, розгортання, застосунок, bootstrap →
    роль → іменований користувач → грант. Далі керування бере Playwright.

    ⛔ Кожен стенд має СВІЙ dev-сервер Vite (`-WebPort`, типово `-Port`+1000),
    який проксіює `/api` саме на свій `-Port` (`ECR_API_URL`). Спільний
    фіксований 4173 з `reuseExistingServer` означав, що другий паралельний
    стенд підхоплював Vite першого, а з ним — його API, базу й документ.

    ⚠ Паролі тут ТЕСТОВІ й живуть лише в цьому скрипті та в тимчасовій базі,
    яку він же видаляє. Це не послаблення `ФВ-6.11`: у продуктивній системі
    жодного з цих записів не існує, а `ECR_Bootstrap__Password` і далі
    передається лише через оточення процесу.

.PARAMETER Server
    Екземпляр SQL Server.

.PARAMETER Database
    Тимчасова база; створюється і видаляється цим скриптом.

.PARAMETER Port
    Порт застосунку; стає ціллю проксі Vite цього стенда.

.PARAMETER WebPort
    Порт dev-сервера Vite для Playwright; 0 (типово) — `-Port` + 1000.
    Зайнятий чужим процесом — стенд падає одразу, а не підхоплює чужий сервер.

.PARAMETER Grep
    Фільтр назв прогонів Playwright; порожній — усі.

.PARAMETER DataPath
    Каталог файлів бази; передається в `setup-dev-db.ps1`. Не задано — діє
    його умовчання (`H:\EcrData`, якщо є `H:`); `''` — типовий каталог
    інстансу. Потрібен, коли на `H:` бракує ~16 ГБ, а на іншому диску є.

.PARAMETER RequireFreeGb
    Скільки вільного місця вимагати на диску даних; передається в
    `setup-dev-db.ps1`. Не задано — обчислюється за профілем; `0` — без перевірки.

.PARAMETER Login
    SQL-логін замість інтегрованої автентифікації; передається й у
    `setup-dev-db.ps1`. Пароль — `-SqlPassword` або змінна `ECR_SQL_PASSWORD`,
    до `sqlcmd` іде через `SQLCMDPASSWORD`. Потрібен для SQL Server у
    Linux-контейнері CI (`.github/workflows/e2e-stand.yml`), так само як у
    `smoke.ps1`. Не задано — `-E` і `Trusted_Connection=True`, як і раніше.

.PARAMETER SmallFiles
    Передається в `setup-dev-db.ps1`: файли бази 64 МБ замість 14 ГБ. Для CI-раннера.

.PARAMETER StartupTimeoutSec
    Скільки чекати старту застосунку (тут і в `setup-dev-db.ps1`). Умовчання 60 с.

.PARAMETER Reporter
    Значення `--reporter` для Playwright (напр. `list,html`); порожнє —
    репортер із `playwright.config.ts`, як і раніше.

.EXAMPLE
    powershell -File tools/e2e-stand.ps1
    pwsh -File tools/e2e-stand.ps1 -Server 'localhost,1433' -Login sa -SmallFiles -RequireFreeGb 0
    powershell -File tools/e2e-stand.ps1 -Server localhost -DataPath F:\EcrData
#>
[CmdletBinding()]
param(
    [string] $Server = 'localhost\SQLEXPRESS',
    [string] $Database = 'EcrE2E',
    [int] $Port = 5080,
    [int] $WebPort = 0,
    [string] $Grep = '',

    # ⚠ Без умовчання навмисно: передається далі ЛИШЕ якщо задано явно, тож
    # запуск без параметра поводиться рівно як раніше (умовчання вирішує
    # `setup-dev-db.ps1`, а не дублюється тут і не розходиться з ним).
    [string] $DataPath,
    [double] $RequireFreeGb = -1,

    # Див. `.PARAMETER Login`/`SmallFiles`/`StartupTimeoutSec`/`Reporter`. Без
    # них поведінка рівно та сама, що й до їх появи.
    [string] $Login,
    [string] $SqlPassword = $env:ECR_SQL_PASSWORD,
    [switch] $SmallFiles,
    [int] $StartupTimeoutSec = 60,
    [string] $Reporter = ''
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
if ($SmallFiles) { $setupExtra += '-SmallFiles' }
if ($PSBoundParameters.ContainsKey('StartupTimeoutSec')) {
    $setupExtra += @('-StartupTimeoutSec', [string] $StartupTimeoutSec)
}

# ⚠ Автентифікація SQL — рівно як у `smoke.ps1`: інтегрована або SQL-логін.
# Пароль лише оточенням (`SQLCMDPASSWORD` для `sqlcmd`, `ECR_SQL_PASSWORD` для
# дочірнього `setup-dev-db.ps1`): аргументи процесу видно всім на агенті.
if ($Login) {
    if (-not $SqlPassword) { throw '-Login задано, а пароля немає: передай -SqlPassword або змінну ECR_SQL_PASSWORD.' }
    $env:SQLCMDPASSWORD = $SqlPassword
    $env:ECR_SQL_PASSWORD = $SqlPassword
    $sqlAuth = @('-U', $Login)
    $setupExtra += @('-Login', $Login)
}
else {
    $sqlAuth = @('-E')
}

# ⚠ Поза Windows: дочірній скрипт — `pwsh`, `-ExecutionPolicy` лише у Windows,
# `Start-Process -WindowStyle`, `cmd.exe`, `taskkill`, `npx.cmd` і
# `Get-NetTCPConnection` там не існують (той самий вибір, що й у `smoke.ps1`).
$onWindows = $IsWindows -or $PSVersionTable.PSEdition -eq 'Desktop'
$psExe = if ($onWindows) { 'powershell' } else { 'pwsh' }
$psPrefix = if ($onWindows) { @('-ExecutionPolicy', 'Bypass') } else { @() }
$npxExe = if ($onWindows) { 'npx.cmd' } else { 'npx' }

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

$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'src/Ecr.Web'
$base = "http://localhost:$Port"
$bootstrapPassword = 'E2E-Bootstrap-2026!'
$step = 0

function Step {
    param([string] $Name)

    $script:step++
    Write-Host ("  {0,2}. {1}" -f $script:step, $Name)
}

function Fail {
    param([string] $Message)

    throw "e2e-stand: крок $script:step — $Message"
}

# ⚠ Спільна сесія з cookie: застосунок працює на cookie-автентифікації, і без
# контейнера кожен виклик був би анонімним.
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Call {
    param([string] $Method, [string] $Path, $Body, [hashtable] $Headers)

    $arguments = @{
        Uri             = "$base$Path"
        Method          = $Method
        WebSession      = $session
        UseBasicParsing = $true
        TimeoutSec      = 60
    }

    if ($null -ne $Headers) { $arguments.Headers = $Headers }

    if ($null -ne $Body) {
        $arguments.Body = ($Body | ConvertTo-Json -Depth 8 -Compress)
        $arguments.ContentType = 'application/json'
    }

    $response = Invoke-WebRequest @arguments
    if ($response.StatusCode -ge 400) { Fail "$Method $Path — $($response.StatusCode)" }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }

    return $null
}

# Версія набору грантів ролі (`ETag` GET) — для `If-Match` на PUT.
function GrantsVersion {
    param([int] $RoleId)

    $etag = (Invoke-WebRequest -Uri "$base/api/v1/roles/$RoleId/grants" -WebSession $session -UseBasicParsing -TimeoutSec 60).Headers['ETag']
    if (-not $etag) { Fail "GET /api/v1/roles/$RoleId/grants не віддав ETag" }

    return $etag
}

if ($WebPort -eq 0) { $WebPort = $Port + 1000 }

# ⛔ Playwright у стенді запускає власний Vite з `reuseExistingServer: false`
# (`playwright.config.ts`, коли задано `E2E_WEB_PORT`), тож чужий сервер на
# цьому порту він і так не підхопить — але впаде аж на кроці прогонів, після
# хвилин розгортання, і з повідомленням Playwright. Тут — одразу й з PID.
# ⚠ Поза Windows `Get-NetTCPConnection` немає: там «зайнято» — це вдале
# з'єднання на localhost (IPv4 або IPv6, Vite слухає те, у що резолвиться
# `localhost`), без PID власника.
function Test-WebPortListening {
    if ($onWindows) {
        return [bool] (Get-NetTCPConnection -State Listen -LocalPort $WebPort -ErrorAction SilentlyContinue)
    }
    foreach ($address in @('127.0.0.1', '::1')) {
        $tcp = $null
        try {
            # ⚠ Конструктор теж усередині try: без IPv6 на агенті він кидає.
            $tcp = New-Object System.Net.Sockets.TcpClient([System.Net.IPAddress]::Parse($address).AddressFamily)
            if ($tcp.ConnectAsync($address, $WebPort).Wait(1000) -and $tcp.Connected) { return $true }
        }
        catch { }
        finally { if ($tcp) { $tcp.Dispose() } }
    }
    return $false
}

function Assert-WebPortFree {
    if (-not $onWindows) {
        if (Test-WebPortListening) { Fail "порт Vite $WebPort уже зайнятий. Це чужий сервер — не підхоплюю його; задай інший -WebPort." }
        return
    }
    $owners = @(Get-NetTCPConnection -State Listen -LocalPort $WebPort -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owners.Count -gt 0) {
        $who = ($owners | ForEach-Object {
            $p = Get-Process -Id $_ -ErrorAction SilentlyContinue
            if ($p) { "$($p.ProcessName) ($_)" } else { "PID $_" }
        }) -join ', '
        Fail "порт Vite $WebPort уже зайнятий: $who. Це чужий сервер — не підхоплюю його; задай інший -WebPort."
    }
}

Assert-WebPortFree
$env:E2E_WEB_PORT = [string] $WebPort

# ⛔ Прогрів Vite ДО Playwright. Без нього перший тест прогону
# (`cellStates.spec.ts`, тема light) упирався в 30 с на `page.goto`: свіжий
# чекаут не має кешу оптимізатора залежностей (`node_modules/.vite/deps`), і
# перше відкриття сторінки чекало, доки esbuild збере 51 залежність.
# Заміряно 2026-09-29: базова лінія — тест 1 `x … (30.4s)`, решта 28 зелені.
#
# ⚠ Прогрівати ЖИВИЙ сервер Playwright звідси неможливо: у стенді він
# піднімає власний Vite з `reuseExistingServer: false`
# (`playwright.config.ts`). Тому тут — ОДНОРАЗОВИЙ Vite тією самою командою
# і на тому самому порту, і прогрівається те, що переживає його зупинку:
# кеш залежностей на диску. Трансформи власних модулів у пам'яті не
# переживають, але вони дешеві: на свіжому Vite з теплим кешем
# `goto /_kitchen-sink` — 0.9 с проти 13.0 с із холодним (окремий замір).
#
# ⚠ Чому саме `/src/main.tsx`, а не лише `/` і `/_kitchen-sink`: SPA на обидві
# адреси віддає той самий `index.html`, і оптимізатор від цього не
# зрушує. Запит модуля входу запускає обхід статичних імпортів
# (`preTransformRequests`), після якого оптимізатор комітить кеш — рівно
# так само, як це робить браузер (перевірено: ті самі 51 файл у `deps`).
function Invoke-ViteWarmup {
    param([int] $TimeoutSec = 180)

    $viteCache = Join-Path $client 'node_modules/.vite'
    $metadata = Join-Path $viteCache 'deps/_metadata.json'
    $warmLog = Join-Path $root 'artifacts/e2e.vite-warmup.log'
    New-Item -ItemType Directory -Force (Split-Path $warmLog) | Out-Null

    $viteArgs = @{
        PassThru               = $true
        WorkingDirectory       = $client
        RedirectStandardOutput = $warmLog
        RedirectStandardError  = "$warmLog.err"
    }
    if ($onWindows) {
        $viteArgs.WindowStyle = 'Hidden'
        $viteArgs.FilePath = 'cmd.exe'
        $viteArgs.ArgumentList = "/c npm run dev -- --port $WebPort --strictPort"
    }
    else {
        $viteArgs.FilePath = 'npm'
        $viteArgs.ArgumentList = "run dev -- --port $WebPort --strictPort"
    }
    $vite = Start-Process @viteArgs

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    try {
        foreach ($path in @('/', '/_kitchen-sink', '/@vite/client', '/src/main.tsx')) {
            $url = "http://localhost:$WebPort$path"
            $last = 'немає відповіді'
            while ($true) {
                try {
                    $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60
                    $last = [string] $r.StatusCode
                    if ($r.StatusCode -eq 200) { break }
                }
                catch {
                    $resp = $_.Exception.Response
                    $last = if ($resp) { [string] [int] $resp.StatusCode } else { $_.Exception.Message }
                }
                if ($vite.HasExited) { Fail "прогрів Vite: сервер завершився (код $($vite.ExitCode)) на $url, останній статус: $last; лог: $warmLog" }
                if ((Get-Date) -gt $deadline) { Fail "прогрів Vite: $url не віддав 200 за $TimeoutSec с, останній статус: $last; лог: $warmLog" }
                Start-Sleep -Seconds 2
            }
        }

        # ⚠ Кеш закомічено, коли є `_metadata.json` і немає `deps_temp_*` (туди
        # оптимізатор пише до перейменування) — двічі поспіль, бо застарілий
        # кеш (новий lock-файл) лежить на місці, поки поруч збирається новий.
        $stable = 0
        while ($stable -lt 2) {
            $busy = @(Get-ChildItem $viteCache -Directory -Filter 'deps_temp_*' -ErrorAction SilentlyContinue).Count -gt 0
            if ((Test-Path $metadata) -and -not $busy) { $stable++ } else { $stable = 0 }
            if ($stable -ge 2) { break }
            if ((Get-Date) -gt $deadline) { Fail "прогрів Vite: кеш залежностей ($metadata) не закомічено за $TimeoutSec с; лог: $warmLog" }
            Start-Sleep -Seconds 2
        }
    }
    finally {
        # Дерево процесів: cmd → npm → node (vite). Лише своє, за PID.
        # ⚠ Q-217: stderr taskkill під 'Stop' став би винятком.
        if (-not $vite.HasExited) {
            if ($onWindows) {
                $previousEapKill = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try { & taskkill.exe /T /F /PID $vite.Id | Out-Null }
                finally { $ErrorActionPreference = $previousEapKill }
            }
            else {
                # npm → sh → node (vite): .NET вбиває дерево за PID.
                $vite.Kill($true)
                $vite.WaitForExit()
            }
        }
    }

    # Порт має звільнитися до Playwright: `--strictPort` інакше впаде.
    foreach ($i in 1..15) {
        if (-not (Test-WebPortListening)) { break }
        Start-Sleep -Seconds 1
    }
}

Write-Host ''
Write-Host "Стенд Playwright на базі $Database (API $Port, Vite $WebPort)" -ForegroundColor Cyan

Step 'чиста база і розгортання через sqlcmd'

# ⛔ L10-13: маркер `Ecr_E2E_Temp` нижче захищає лише фінальне прибирання, а
# `setup-dev-db.ps1` безумовно ЗНИЩУЄ базу з цим іменем ще до маркера. Існуюча
# база без нашого маркера - відмова ДО розгортання.
$previousEapGuard = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $foreign = (& sqlcmd -S $Server @sqlAuth -C -b -h -1 -W -d master `
        -Q "SET NOCOUNT ON; DECLARE @r int = 0; IF DB_ID(N'$Database') IS NOT NULL EXEC sp_executesql N'SELECT @r = CASE WHEN EXISTS (SELECT 1 FROM [$Database].sys.extended_properties WHERE class = 0 AND name = N''Ecr_E2E_Temp'') THEN 0 ELSE 1 END', N'@r int OUTPUT', @r OUTPUT; SELECT @r;") `
        | Select-Object -Last 1
}
finally {
    $ErrorActionPreference = $previousEapGuard
}
if ($LASTEXITCODE -ne 0) { Fail "не вдалося перевірити, чи є база $Database на $Server" }
if ("$foreign".Trim() -eq '1') {
    Fail "база $Database на $Server вже існує і не має позначки Ecr_E2E_Temp (не наша тимчасова) - розгортання знищило б її. Задай інше -Database або прибери базу вручну."
}

$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & $psExe @psPrefix -File (Join-Path $PSScriptRoot 'setup-dev-db.ps1') `
        -Server $Server -Database $Database -Documents 1 -BootstrapPassword $bootstrapPassword @setupExtra | Out-Null
}
finally {
    $ErrorActionPreference = $previousEap
}
if ($LASTEXITCODE -ne 0) { Fail 'розгортання не пройшло' }

# ⛔ Позначка ставиться ОДРАЗУ після створення і потрібна лише для одного:
# щоб `finally` нижче мав що перевірити перед `DROP DATABASE`. Ім'я бази
# приходить параметром, отже `-Database EcrDev` без цієї перевірки знищив би
# базу розробника мовчки і безповоротно.
#
# ⚠ Урок не новий: рівно такий сторож стоїть у `br07-load-test.ps1:209`
# із тим самим поясненням. Сюди він не доїхав — і це знайшов аудит, а не
# випадок, якому пощастило статися на чужій базі.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & sqlcmd -S $Server @sqlAuth -C -b -d $Database -Q "EXEC sys.sp_addextendedproperty @name = N'Ecr_E2E_Temp', @value = 1;" | Out-Null
}
finally {
    $ErrorActionPreference = $previousEap
}
if ($LASTEXITCODE -ne 0) { Fail 'не вдалося позначити тимчасову базу' }

$connection = if ($Login) {
    "Server=$Server;Database=$Database;User Id=$Login;Password=$SqlPassword;TrustServerCertificate=True"
}
else {
    "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
}
$log = Join-Path $root 'artifacts/e2e.api.log'

$env:ECR_ConnectionStrings__Ecr = $connection
$env:ECR_Bootstrap__Password = $bootstrapPassword
$env:ASPNETCORE_URLS = $base

# ⛔ Q-259 (аудит): без цього рядка `-Port` вимикав лише бекенд — Vite
# (`vite.config.ts`) далі проксіював `/api` на зашитий `http://localhost:5080`
# незалежно від `$base`. Ізольований лабораторний прогін (свій `-Database` й
# свій `-Port`, щоб не заважати паралельним лініям) із портом, відмінним від
# 5080, падав на самому першому екрані — `ECONNREFUSED` у проксі, а не
# дефект застосунку. `npm run dev`, який запускає Playwright у
# `webServer.command` (`playwright.config.ts`), успадковує середовище цього
# процесу, тому досить виставити змінну тут, ДО кроку 10.
$env:ECR_API_URL = $base

# ⛔ Cookie автентифікації типово `Secure` і по HTTP не надсилається — вхід
# проходив би, а наступний виклик отримував 401. Змінна діє лише на цей
# тимчасовий процес.
$env:ECR_Auth__RequireHttps = 'false'

# ⛔ S11: процес іде без launch-профілю, тобто в Production, а там без
# сертифіката Data Protection застосунок не стартує. Одноразовий стенд — явна
# згода на незахищене кільце (тоді старт пише Critical, а db — Degraded).
$env:ECR_Auth__DataProtection__AllowUnprotectedKeys = 'true'

Step 'старт застосунку'
$startArgs = @{
    PassThru               = $true
    FilePath               = 'dotnet'
    ArgumentList           = "run --project `"$(Join-Path $root 'src/Ecr.Api')`" --no-build --no-launch-profile"
    RedirectStandardOutput = $log
    RedirectStandardError  = "$log.err"
}
if ($onWindows) { $startArgs.WindowStyle = 'Hidden' }
$api = Start-Process @startArgs

try {
    $ready = $false
    foreach ($i in 1..($StartupTimeoutSec * 2)) {
        Start-Sleep -Milliseconds 500
        if ($api.HasExited) { Fail "застосунок завершився з кодом $($api.ExitCode); лог: $log" }
        try {
            $probe = Invoke-WebRequest -Uri "$base/health/live" -UseBasicParsing -TimeoutSec 5
            if ($probe.StatusCode -eq 200) { $ready = $true; break }
        }
        catch { }
    }

    if (-not $ready) { Fail "застосунок не піднявся; лог: $log" }

    Step 'bootstrap і зміна разового пароля'
    Call POST '/api/v1/login/local' @{ userName = 'bootstrap'; password = $bootstrapPassword } | Out-Null
    Call POST '/api/v1/auth/change-password' `
        @{ currentPassword = $bootstrapPassword; newPassword = 'E2E-Real-2026!' } | Out-Null

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'bootstrap'; password = 'E2E-Real-2026!' } | Out-Null

    # ⛔ ДВІ ролі, а не одна. Знімки під однією роллю доводять лише, що екран
    # рендериться; сенс перевірки в тому, що оператор НЕ БАЧИТЬ того, чого не
    # має бачити, а це видно тільки в порівнянні (`D-142`).
    Step 'роль оператора'
    Call POST '/api/v1/roles' @{
        code            = 'E2EOperator'
        nameL10n        = @{ en = 'E2E operator' }
        permissionCodes = @('Document.View', 'Document.Create', 'Document.Export', 'Document.Import')
    } | Out-Null

    Step 'роль адміністратора'
    Call POST '/api/v1/roles' @{
        code            = 'E2EAdmin'
        nameL10n        = @{ en = 'E2E administrator' }
        permissionCodes = @(
            'Template.View', 'Template.Edit', 'Template.Publish',
            'Registry.View', 'Registry.EditData',
            'Document.View', 'Document.Create', 'Document.Export', 'Document.Import',
            'Document.Reopen',
            'Project.Manage', 'Period.Configure', 'Period.Reopen',
            'Calculation.View', 'Calculation.Publish', 'Calculation.Recalculate',
            'Security.ManageUsers', 'Security.ManageRoles', 'Security.ViewAudit',
            'Security.Simulate',
            'Report.ViewRegulatory', 'Report.BuildSnapshot', 'Report.ViewCampaign',
            'Integration.View', 'Integration.Manage', 'System.ViewHealth', 'System.ManageLocalization',
            'System.ManageNotifications')
    } | Out-Null

    $roles = Call GET '/api/v1/roles'
    $operatorRole = ($roles | Where-Object { $_.code -eq 'E2EOperator' }).id
    $adminRole = ($roles | Where-Object { $_.code -eq 'E2EAdmin' }).id
    if (-not $operatorRole -or -not $adminRole) { Fail 'ролі не створилися' }

    Step 'три іменовані користувачі'
    Call POST '/api/v1/users' @{
        userName = 'e2e-operator'; provider = 'Local'; sid = $null
        displayName = 'E2E operator'; initialPassword = 'E2E-Oper8tor-2026!'
        roleCodes = @('E2EOperator')
    } | Out-Null

    Call POST '/api/v1/users' @{
        userName = 'e2e-admin'; provider = 'Local'; sid = $null
        displayName = 'E2E administrator'; initialPassword = 'E2E-Adm1n-2026!'
        roleCodes = @('E2EAdmin')
    } | Out-Null

    # ⛔ F-25 (пряме рішення людини, `ApproveSheetHandler.cs`): та сама
    # людина не може подати аркуш (`Submit`) і сама ж його погодити
    # (`Approve`) — правило чотирьох очей. `keyboardPath.spec.ts` подає
    # аркуш від імені `e2e-admin`, тож затверджувати ним ЦЕЙ САМИЙ аркуш
    # сервер відмовляє (`403 ECR-ACCS-0403`, `err.ECR-ACCS-0403.approveOwnSubmission`)
    # — другий обліковий запис із тим самим грантом (роль `E2EAdmin`) існує
    # рівно для цього кроку.
    #
    # ⛔ Ім'я НЕ `e2e-approver` — живцем зловлено на стенді. Кнопка
    # затвердження шукається `getByRole('button', { name: /Approve|Затвердити/i })`,
    # а Playwright звіряє `name`-регексп ПІДРЯДКОМ: `e2e-approver` містить
    # `approve`, тож той самий локатор (з `.first()`) резолвився в кнопку
    # МЕНЮ КОРИСТУВАЧА (її доступне ім'я — юзернейм) замість кнопки
    # робочого процесу. Симптом був загадковий: фокус і клік проходили без
    # жодної помилки, а замість діалогу підтвердження відкривалося меню
    # «Тема/Пароль/Вийти» — і це коштувало кількох прогонів, доки
    # відеокадр трасування не показав меню замість діалогу.
    Call POST '/api/v1/users' @{
        userName = 'e2e-reviewer'; provider = 'Local'; sid = $null
        displayName = 'E2E reviewer'; initialPassword = 'E2E-Rev1ewer-2026!'
        roleCodes = @('E2EAdmin')
    } | Out-Null

    # ⚠ Разовий пароль міняється ЗАРАЗ, а не в тесті: інакше кожен прогін
    # починався б із примусової зміни пароля, і перевірявся б саме цей екран,
    # а не той, заради якого прогін написаний (`ФВ-6.18`).
    Step 'зміна разових паролів усіх трьох'
    foreach ($account in @(
            @{ user = 'e2e-operator'; issued = 'E2E-Oper8tor-2026!'; work = 'E2E-Oper8tor-Work-2026!' },
            @{ user = 'e2e-admin'; issued = 'E2E-Adm1n-2026!'; work = 'E2E-Adm1n-Work-2026!' },
            @{ user = 'e2e-reviewer'; issued = 'E2E-Rev1ewer-2026!'; work = 'E2E-Rev1ewer-Work-2026!' })) {

        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        Call POST '/api/v1/login/local' @{ userName = $account.user; password = $account.issued } | Out-Null
        Call POST '/api/v1/auth/change-password' `
            @{ currentPassword = $account.issued; newPassword = $account.work } | Out-Null
    }

    # ⛔ Без гранта перелік порожній для всіх, включно з носієм усіх прав
    # (`A7-22`). Грант видає адміністратор — оператор такого права не має.
    Step 'ресурсні гранти на проєкт'
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'e2e-admin'; password = 'E2E-Adm1n-Work-2026!' } | Out-Null

    # ⛔ `If-Match` обов'язковий (без нього — 422): версія набору — `ETag`
    # відповіді GET, як це робить екран грантів.
    Call PUT "/api/v1/roles/$adminRole/grants" @{
        grants = @(@{ resourceKind = 'Project'; resourceId = 1; level = 'Manage'; isDeny = $false })
    } -Headers @{ 'If-Match' = (GrantsVersion $adminRole) } | Out-Null

    # ⚠ Без перелогіну між двома PUT — навмисно. PUT грантів крутить
    # SecurityStamp усіх членів ролі (`RotateStampsForRoleAsync`), зокрема й
    # самого e2e-admin, але сервер тепер перевидає виконавцю cookie в тій самій
    # відповіді (`3ec89e19`), а `-WebSession $session` у `Call` її підхоплює.
    # Колишній обхід (повторний вхід тут) маскував би саме цю ваду: без
    # перевидачі другий PUT отримав би 401 ECR-AUTH-0401, щойно мине кеш штампа.

    Call PUT "/api/v1/roles/$operatorRole/grants" @{
        grants = @(@{ resourceKind = 'Project'; resourceId = 1; level = 'Write'; isDeny = $false })
    } -Headers @{ 'If-Match' = (GrantsVersion $operatorRole) } | Out-Null

    Step 'перевірка стенда: проєкт, період, документ'
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'e2e-admin'; password = 'E2E-Adm1n-Work-2026!' } | Out-Null

    $projects = Call GET '/api/v1/projects'
    if ($projects.items.Count -eq 0) { Fail 'перелік проєктів порожній: грант не діє' }

    $calendar = Call GET "/api/v1/projects/$($projects.items[0].id)/periods"
    $open = $calendar.periods | Where-Object { $_.state -eq 'Open' } | Select-Object -First 1
    if (-not $open) { Fail 'жодного відкритого періоду' }

    $documents = Call GET "/api/v1/documents?periodKey=$($open.periodKey)"
    if ($documents.items.Count -eq 0) { Fail 'документів немає' }

    # ⚠ Прогони отримують ключ періоду і документ ЧЕРЕЗ ОТОЧЕННЯ, а не
    # вигадують їх: період, зашитий числом, ламався б щороку в січні.
    $env:ECR_E2E_PERIOD = $open.periodKey
    $env:ECR_E2E_DOCUMENT = $documents.items[0].id

    Write-Host ''
    Write-Host "  стенд готовий: період $($open.periodKey), документ $($env:ECR_E2E_DOCUMENT)" -ForegroundColor Green
    Write-Host ''

    Step 'прогрів Vite (кеш залежностей)'
    Assert-WebPortFree
    $warmStart = Get-Date
    Invoke-ViteWarmup
    Write-Host ("      прогріто за {0:N1} с" -f ((Get-Date) - $warmStart).TotalSeconds)

    Step 'прогони Playwright'
    Assert-WebPortFree
    Push-Location $client
    try {
        # ⛔ Q-222 (аудит): той самий Q-217 клас — без тимчасового послаблення
        # звичайний вивід Playwright у stderr зупинив би скрипт ДО перевірки
        # $LASTEXITCODE нижче.
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $pwArgs = @('playwright', 'test')
            if ($Grep) { $pwArgs += @('--grep', $Grep) }
            if ($Reporter) { $pwArgs += "--reporter=$Reporter" }
            & $npxExe @pwArgs
        }
        finally {
            $ErrorActionPreference = $previousEap
        }

        if ($LASTEXITCODE -ne 0) { Fail 'прогони не пройшли' }
    }
    finally {
        Pop-Location
    }

    Write-Host ''
    Write-Host 'Прогони в браузері пройдено.' -ForegroundColor Green
}
finally {
    # ⚠ Поза Windows `dotnet run` лишив би дочірній Ecr.Api живим — дерево.
    if ($api -and -not $api.HasExited) {
        if ($onWindows) { $api.Kill() } else { $api.Kill($true) }
        $api.WaitForExit()
    }

    # ⛔ Видаляється ЛИШЕ база з власною позначкою. Без цієї умови скрипт
    # знищував би будь-що, назване в `-Database`, — включно з базою, у якій
    # лежить чиясь робота. Рівно такий сторож стоїть у `br07-load-test.ps1:209`;
    # сюди він не доїхав, і це знайшов аудит, а не випадок.
    #
    # ⚠ Два простих запити замість одного складеного: питання «чи існує»
    # адресується `master`, питання «чи моя» — самій базі. Вкладений динамічний
    # SQL з підстановкою імені бази — саме те місце, де сторож стає діркою.
    #
    # ⛔ Q-222 (аудит): `2>$null` тут НЕ рятує від Q-217-класу — перевірено
    # реальним відтворенням (`cmd /c "echo w 1>&2 & exit 0"` під
    # `$ErrorActionPreference = 'Stop'` кидає виняток навіть із `2>$null`,
    # бо PowerShell перетворює запис у stderr на помилку СВОГО потоку до
    # того, як спрацьовує перенаправлення). Обидва виклики — під тим самим
    # тимчасовим послабленням, що DROP DATABASE нижче.
    $previousEapExists = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $exists = (& sqlcmd -S $Server @sqlAuth -C -b -h -1 -W -d master `
            -Q "SET NOCOUNT ON; SELECT CASE WHEN DB_ID('$Database') IS NULL THEN 0 ELSE 1 END;" 2>$null) `
            | Select-Object -Last 1
    }
    finally {
        $ErrorActionPreference = $previousEapExists
    }

    if ($exists -eq '1') {
        $previousEapMine = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $mine = (& sqlcmd -S $Server @sqlAuth -C -b -h -1 -W -d $Database `
                -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = N'Ecr_E2E_Temp';" 2>$null) `
                | Select-Object -Last 1
        }
        finally {
            $ErrorActionPreference = $previousEapMine
        }

        if ($mine -eq '1') {
            $previousEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & sqlcmd -S $Server @sqlAuth -C -b -Q "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database];" | Out-Null
            }
            finally {
                $ErrorActionPreference = $previousEap
            }
        }
        else {
            Write-Warning "База $Database не має позначки Ecr_E2E_Temp - НЕ чіпаю її."
        }
    }
}
