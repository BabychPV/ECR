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

    ⛔ Порт застосунку — 5080, бо саме туди проксіює dev-сервер Vite
    (`vite.config.ts`). Інший порт означав би, що браузер стукає в порожнечу,
    а падіння виглядало б як помилка тесту.

    ⚠ Паролі тут ТЕСТОВІ й живуть лише в цьому скрипті та в тимчасовій базі,
    яку він же видаляє. Це не послаблення `ФВ-6.11`: у продуктивній системі
    жодного з цих записів не існує, а `ECR_Bootstrap__Password` і далі
    передається лише через оточення процесу.

.PARAMETER Server
    Екземпляр SQL Server.

.PARAMETER Database
    Тимчасова база; створюється і видаляється цим скриптом.

.PARAMETER Port
    Порт застосунку; має збігатися з ціллю проксі Vite.

.PARAMETER Grep
    Фільтр назв прогонів Playwright; порожній — усі.

.EXAMPLE
    powershell -File tools/e2e-stand.ps1
#>
[CmdletBinding()]
param(
    [string] $Server = 'localhost\SQLEXPRESS',
    [string] $Database = 'EcrE2E',
    [int] $Port = 5080,
    [string] $Grep = ''
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
    param([string] $Method, [string] $Path, $Body)

    $arguments = @{
        Uri             = "$base$Path"
        Method          = $Method
        WebSession      = $session
        UseBasicParsing = $true
        TimeoutSec      = 60
    }

    if ($null -ne $Body) {
        $arguments.Body = ($Body | ConvertTo-Json -Depth 8 -Compress)
        $arguments.ContentType = 'application/json'
    }

    $response = Invoke-WebRequest @arguments
    if ($response.StatusCode -ge 400) { Fail "$Method $Path — $($response.StatusCode)" }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }

    return $null
}

Write-Host ''
Write-Host "Стенд Playwright на базі $Database" -ForegroundColor Cyan

Step 'чиста база і розгортання через sqlcmd'
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'setup-dev-db.ps1') `
        -Server $Server -Database $Database -Documents 1 -BootstrapPassword $bootstrapPassword | Out-Null
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
    & sqlcmd -S $Server -E -C -b -d $Database -Q "EXEC sys.sp_addextendedproperty @name = N'Ecr_E2E_Temp', @value = 1;" | Out-Null
}
finally {
    $ErrorActionPreference = $previousEap
}
if ($LASTEXITCODE -ne 0) { Fail 'не вдалося позначити тимчасову базу' }

$connection = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
$log = Join-Path $root 'artifacts/e2e.api.log'

$env:ECR_ConnectionStrings__Ecr = $connection
$env:ECR_Bootstrap__Password = $bootstrapPassword
$env:ASPNETCORE_URLS = $base

# ⛔ Cookie автентифікації типово `Secure` і по HTTP не надсилається — вхід
# проходив би, а наступний виклик отримував 401. Змінна діє лише на цей
# тимчасовий процес.
$env:ECR_Auth__RequireHttps = 'false'

Step 'старт застосунку'
$api = Start-Process -PassThru -WindowStyle Hidden dotnet `
    -ArgumentList "run --project `"$(Join-Path $root 'src/Ecr.Api')`" --no-build --no-launch-profile" `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err"

try {
    $ready = $false
    foreach ($i in 1..120) {
        Start-Sleep -Milliseconds 500
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
            'Report.ViewRegulatory', 'Report.BuildSnapshot',
            'Integration.Manage', 'System.ViewHealth', 'System.ManageLocalization')
    } | Out-Null

    $roles = Call GET '/api/v1/roles'
    $operatorRole = ($roles | Where-Object { $_.code -eq 'E2EOperator' }).id
    $adminRole = ($roles | Where-Object { $_.code -eq 'E2EAdmin' }).id
    if (-not $operatorRole -or -not $adminRole) { Fail 'ролі не створилися' }

    Step 'два іменовані користувачі'
    Call POST '/api/v1/users' @{
        userName = 'e2e-operator'; provider = 'Local'; sid = $null
        displayName = 'E2E operator'; initialPassword = 'E2E-Operator-2026!'
        roleCodes = @('E2EOperator')
    } | Out-Null

    Call POST '/api/v1/users' @{
        userName = 'e2e-admin'; provider = 'Local'; sid = $null
        displayName = 'E2E administrator'; initialPassword = 'E2E-Admin-2026!'
        roleCodes = @('E2EAdmin')
    } | Out-Null

    # ⚠ Разовий пароль міняється ЗАРАЗ, а не в тесті: інакше кожен прогін
    # починався б із примусової зміни пароля, і перевірявся б саме цей екран,
    # а не той, заради якого прогін написаний (`ФВ-6.18`).
    Step 'зміна разових паролів обох'
    foreach ($account in @(
            @{ user = 'e2e-operator'; issued = 'E2E-Operator-2026!'; work = 'E2E-Operator-Work-2026!' },
            @{ user = 'e2e-admin'; issued = 'E2E-Admin-2026!'; work = 'E2E-Admin-Work-2026!' })) {

        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        Call POST '/api/v1/login/local' @{ userName = $account.user; password = $account.issued } | Out-Null
        Call POST '/api/v1/auth/change-password' `
            @{ currentPassword = $account.issued; newPassword = $account.work } | Out-Null
    }

    # ⛔ Без гранта перелік порожній для всіх, включно з носієм усіх прав
    # (`A7-22`). Грант видає адміністратор — оператор такого права не має.
    Step 'ресурсні гранти на проєкт'
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'e2e-admin'; password = 'E2E-Admin-Work-2026!' } | Out-Null

    Call PUT "/api/v1/roles/$adminRole/grants" @{
        grants = @(@{ resourceKind = 'Project'; resourceId = 1; level = 'Manage'; isDeny = $false })
    } | Out-Null

    Call PUT "/api/v1/roles/$operatorRole/grants" @{
        grants = @(@{ resourceKind = 'Project'; resourceId = 1; level = 'Write'; isDeny = $false })
    } | Out-Null

    Step 'перевірка стенда: проєкт, період, документ'
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Call POST '/api/v1/login/local' @{ userName = 'e2e-admin'; password = 'E2E-Admin-Work-2026!' } | Out-Null

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

    Step 'прогони Playwright'
    Push-Location $client
    try {
        # ⛔ Q-222 (аудит): той самий Q-217 клас — без тимчасового послаблення
        # звичайний вивід Playwright у stderr зупинив би скрипт ДО перевірки
        # $LASTEXITCODE нижче.
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            if ($Grep) { & npx.cmd playwright test --grep $Grep }
            else { & npx.cmd playwright test }
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
    if ($api -and -not $api.HasExited) { $api.Kill(); $api.WaitForExit() }

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
        $exists = (& sqlcmd -S $Server -E -C -b -h -1 -W -d master `
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
            $mine = (& sqlcmd -S $Server -E -C -b -h -1 -W -d $Database `
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
                & sqlcmd -S $Server -E -C -b -Q "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database];" | Out-Null
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
