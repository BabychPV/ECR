# tools/land/EcrApi.ps1
# Спільний клієнт REST API ECR для скриптів tools/land (dot-source: . "$PSScriptRoot\EcrApi.ps1").
# Сумісний з Windows PowerShell 5.1 і PowerShell 7. Секретів у файлі немає: логін і пароль
# беруться з параметрів скрипта або зі змінних середовища ECR_USER / ECR_PASSWORD.

Set-StrictMode -Version 2.0

$script:EcrBase = $null
$script:EcrSession = $null
$script:EcrClient = $null
$script:EcrStandConfirmed = $false

function ConvertTo-PlainText {
    param([Parameter(Mandatory)][securestring]$Secure)
    $cred = New-Object System.Net.NetworkCredential('', $Secure)
    return $cred.Password
}

function Get-LoopbackFlag {
    <#
    .SYNOPSIS
        $true, якщо адреса веде на цю ж машину (localhost, 127.0.0.1, ::1).
    #>
    param([Parameter(Mandatory)][string]$Url)
    $u = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$u)) { return $false }
    return [bool]$u.IsLoopback
}

function Test-LocalHostName {
    <#
    .SYNOPSIS
        $true лише для localhost, 127.0.0.1 і ::1. Жодних інших імен чи адрес (у т.ч. *.localhost:
        довільне ім'я під .localhost залежить від резолвера, тут воно не довіряється).
    #>
    param([Parameter(Mandatory)][string]$Candidate)
    $h = $Candidate.Trim().Trim('[', ']').ToLowerInvariant()
    # Uri.Host у .NET Framework віддає IPv6 у розгорнутому вигляді (0000:…:0001): порівнюємо як адресу.
    $ip = $null
    if ([System.Net.IPAddress]::TryParse($h, [ref]$ip)) {
        return ($ip.Equals([System.Net.IPAddress]::Loopback) -or $ip.Equals([System.Net.IPAddress]::IPv6Loopback))
    }
    return ($h -eq 'localhost' -or $h -eq '127.0.0.1' -or $h -eq '::1')
}

function Assert-LocalBaseUrl {
    <#
    .SYNOPSIS
        Відмова, якщо адреса API веде не на цю машину. Скрипти tools/land будують стенд приймальної й
        не мають права торкатися чужого сервера (ні http, ні https).
    #>
    param([Parameter(Mandatory)][string]$Url)
    $u = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$u) -or -not (Test-LocalHostName $u.Host)) {
        throw "Адреса API '$Url' не локальна. Скрипти tools/land працюють лише зі стендом на цій машині (localhost, 127.0.0.1, [::1]). Відмова."
    }
}

function Assert-LocalSqlConnection {
    <#
    .SYNOPSIS
        Відмова, якщо рядок підключення SQL Server (а) веде не на локальний сервер (localhost, ., (local),
        127.0.0.1, ::1, ім'я цієї машини, (localdb); екземпляр і порт дозволені) або (б) вказує базу, чиє
        ім'я не починається з EcrLand (прод-інстанс старої системи на цій же машині не проходить).
        Розбір — SqlConnectionStringBuilder, тобто за тими самими правилами, що й SqlClient (при дублі
        ключів перемагає ОСТАННЄ значення); регулярка тут була б вразливою до `Server=localhost;Data Source=prod`.
        Повертає ім'я бази.
    #>
    param([Parameter(Mandatory)][string]$ConnectionString)
    $sb = $null
    try { $sb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString) }
    catch { throw "Рядок підключення SQL не розібрано: $($_.Exception.Message)" }
    $ds = [string]$sb.DataSource
    $db = [string]$sb.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($ds)) { throw 'У рядку підключення SQL немає Server/Data Source: відмова.' }
    if (-not [string]::IsNullOrWhiteSpace([string]$sb.AttachDBFilename)) {
        throw 'У рядку підключення SQL є AttachDBFilename: база задана файлом, а не іменем. Відмова.'
    }
    $s = $ds.Trim()
    $pipe = ($s -match '^(np:)?\\\\\.\\pipe\\')
    if (-not $pipe) {
        $s = $s -replace '^(tcp|np|lpc|admin):', ''
        $s = ($s -split ',')[0]
        $s = ($s -split '\\')[0]
        $s = $s.Trim().Trim('[', ']')
        $local = ($s -eq '.' -or $s -eq '(local)' -or $s -eq '(localdb)' -or (Test-LocalHostName $s) -or
            (-not [string]::IsNullOrEmpty($env:COMPUTERNAME) -and $s -ieq $env:COMPUTERNAME))
        if (-not $local) {
            throw "SQL-сервер '$ds' не локальний. Скрипти tools/land пишуть у базу лише на цій машині (localhost, ., (local), 127.0.0.1, ::1, ім'я машини). Відмова."
        }
    }
    if ([string]::IsNullOrWhiteSpace($db) -or $db -notmatch '^(?i)EcrLand') {
        throw "База '$db' не належить стенду приймальної: дозволено лише бази з іменем EcrLand* (наприклад EcrLandUi). Відмова."
    }
    return $db
}

function Set-EcrStandConfirmation {
    <#
    .SYNOPSIS
        Вмикає мутуючі виклики лише за явним підтвердженням стенда: значення має збігатися з адресою
        API (повною, host:port або host). Без нього Invoke-Ecr відмовляє на будь-який не-GET.
        Якщо передано -Database (скрипт пише ще й в SQL), підтвердження ОБОВ'ЯЗКОВО несе імʼя бази:
        `<адреса>/<база>` (наприклад localhost:5094/EcrLandUi): URL API базу не називає, і підтвердження
        лише адреси не відрізнило б стенд від локального прод-інстансу.
    #>
    param([string]$Stand, [Parameter(Mandatory)][string]$Url, [string]$Database)
    Assert-LocalBaseUrl $Url
    $u = [System.Uri]$Url
    $forms = @($Url.TrimEnd('/'), $u.Authority, $u.Host)
    if (-not [string]::IsNullOrWhiteSpace($Database)) { $forms = @($forms | ForEach-Object { "$_/$Database" }) }
    if ([string]::IsNullOrWhiteSpace($Stand)) {
        throw "Мутуючі виклики потребують явного -ConfirmStand <імʼя або URL стенду> (наприклад '$($forms[0])'). Без нього скрипт нічого не змінює; -DryRun лише читає."
    }
    $wanted = @($forms | ForEach-Object { $_.ToLowerInvariant() })
    if ($wanted -notcontains $Stand.Trim().TrimEnd('/').ToLowerInvariant()) {
        throw "-ConfirmStand '$Stand' не збігається зі стендом (очікується '$($forms[1])' або '$($forms[0])'$(if ($Database) { ': імʼя бази обовʼязкове' })). Відмова."
    }
    $script:EcrStandConfirmed = $true
}

function Assert-EcrMutationAllowed {
    param([string]$What = 'мутуючий виклик')
    if (-not $script:EcrStandConfirmed) {
        throw "Відмова: $What без -ConfirmStand. Скрипти tools/land змінюють стенд лише після явного підтвердження його імені/адреси."
    }
}

function Enable-LoopbackHttpCookies {
    <#
    .SYNOPSIS
        Сесійний cookie API має прапор Secure, а Invoke-WebRequest (особливо Windows PowerShell 5.1)
        не надсилає Secure-cookie по http://, тож усі виклики після входу дають 401. Для ЛОКАЛЬНОЇ
        адреси копіюємо cookie сесії без Secure. Для не-loopback http нічого не робимо (див. Connect-Ecr).
    #>
    $u = [System.Uri]$script:EcrBase
    if ($u.Scheme -ne 'http' -or -not $u.IsLoopback) { return }
    # GetCookies за https-адресою того ж хоста повертає і Secure-cookie (порт у домені не враховується).
    $hostName = $u.Host.Trim('[', ']')
    if ($u.HostNameType -eq [System.UriHostNameType]::IPv6) { $hostName = '[' + $hostName + ']' }
    $secureUri = [System.Uri]('https://' + $hostName + '/')
    foreach ($c in @($script:EcrSession.Cookies.GetCookies($secureUri))) {
        if (-not $c.Secure) { continue }
        $copy = New-Object System.Net.Cookie($c.Name, $c.Value, $c.Path, $c.Domain)
        $copy.HttpOnly = $c.HttpOnly
        $copy.Secure = $false
        if ($c.Expires -ne [datetime]::MinValue) { $copy.Expires = $c.Expires }
        $script:EcrSession.Cookies.Add($copy)
    }
}

function Connect-Ecr {
    <#
    .SYNOPSIS
        Локальний вхід (POST /api/v1/login/local); сесія тримається в cookie-jar.
        По http:// дозволено лише loopback-адреси (localhost, 127.0.0.1, ::1).
    #>
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [string]$User,
        [securestring]$Password
    )

    if ([string]::IsNullOrWhiteSpace($User)) { $User = $env:ECR_USER }
    $plain = $null
    if ($null -ne $Password) { $plain = ConvertTo-PlainText $Password }
    elseif (-not [string]::IsNullOrEmpty($env:ECR_PASSWORD)) { $plain = $env:ECR_PASSWORD }

    if ([string]::IsNullOrWhiteSpace($User) -or [string]::IsNullOrEmpty($plain)) {
        throw 'Не задано обліковий запис: передайте -User/-Password або змінні середовища ECR_USER/ECR_PASSWORD.'
    }

    $baseUri = $null
    if (-not [System.Uri]::TryCreate($BaseUrl, [System.UriKind]::Absolute, [ref]$baseUri)) {
        throw "Некоректна адреса API: $BaseUrl"
    }
    if ($baseUri.Scheme -eq 'http' -and -not (Get-LoopbackFlag $BaseUrl)) {
        # Пароль і cookie сесії по відкритому http не передаємо.
        throw "Адреса $BaseUrl — http не на локальній машині: використовуйте https (http дозволено лише для localhost, 127.0.0.1, ::1)."
    }

    # Лише локальний стенд: і http, і https не на цій машині — відмова (ні логін, ні cookie не йдуть назовні).
    Assert-LocalBaseUrl $BaseUrl

    $script:EcrBase = $BaseUrl.TrimEnd('/')
    $script:EcrSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $script:EcrClient = $null

    $r = Invoke-Ecr -Method POST -Path '/api/v1/login/local' -Body @{ userName = $User; password = $plain }
    if ($r.Status -ne 200) {
        # Тіло відповіді не друкуємо повністю: у ньому немає пароля, але й корисного — лише код помилки.
        throw "Вхід не вдався: HTTP $($r.Status) $(Get-EcrErrorCode $r)."
    }
    Enable-LoopbackHttpCookies
}

function Invoke-Ecr {
    <#
    .SYNOPSIS
        Виклик API. Повертає @{ Status; Json; Text; Headers } і НЕ кидає виняток на 4xx/5xx
        (рішення про помилку приймає викликач).
    #>
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        $Body = $null,
        [hashtable]$Headers = @{}
    )

    # Не-GET (крім самого входу) — лише після -ConfirmStand.
    if ($Method.ToUpperInvariant() -notin @('GET', 'HEAD') -and -not ($Method -ieq 'POST' -and $Path -eq '/api/v1/login/local')) {
        Assert-EcrMutationAllowed "$Method $Path"
    }

    # HttpClient, а не Invoke-WebRequest: у Windows PowerShell 5.1 (HttpWebRequest) відповідь 422 problem+json
    # від API (publish шаблону) дає «The connection was closed unexpectedly» без коду і тіла, тож
    # діагностику ECR-TMPL-4228 було б не прочитати. HttpClient однаково працює в 5.1 і 7.
    if ($null -eq $script:EcrClient) {
        Add-Type -AssemblyName System.Net.Http
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.CookieContainer = $script:EcrSession.Cookies
        $script:EcrClient = New-Object System.Net.Http.HttpClient($handler)
        $script:EcrClient.Timeout = [TimeSpan]::FromMinutes(30)
    }

    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), ($script:EcrBase + $Path))
    foreach ($k in $Headers.Keys) { [void]$req.Headers.TryAddWithoutValidation([string]$k, [string]$Headers[$k]) }
    if ($null -ne $Body) {
        # Тіло — UTF-8 явно: кирилиця в підписах полів.
        $json0 = $Body | ConvertTo-Json -Depth 12 -Compress
        $req.Content = New-Object System.Net.Http.StringContent($json0, [System.Text.Encoding]::UTF8, 'application/json')
    }

    $status = 0
    $text = ''
    $hdrs = @{}
    $resp = $script:EcrClient.SendAsync($req).GetAwaiter().GetResult()
    try {
        $status = [int]$resp.StatusCode
        $text = [string]$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        foreach ($h in $resp.Headers) { $hdrs[$h.Key] = ($h.Value -join ', ') }
        foreach ($h in $resp.Content.Headers) { $hdrs[$h.Key] = ($h.Value -join ', ') }
    }
    finally { $resp.Dispose(); $req.Dispose() }

    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try { $json = $text | ConvertFrom-Json } catch { $json = $null }
    }
    return [pscustomobject]@{ Status = $status; Json = $json; Text = $text; Headers = $hdrs }
}

function Get-EcrErrorCode {
    param($Response)
    if ($null -ne $Response.Json -and $Response.Json.PSObject.Properties.Match('errorCode').Count -gt 0) {
        return [string]$Response.Json.errorCode
    }
    return ''
}

function Assert-EcrOk {
    param(
        [Parameter(Mandatory)]$Response,
        [Parameter(Mandatory)][string]$What,
        [int[]]$Allowed = @(200, 201, 204)
    )
    if ($Allowed -notcontains $Response.Status) {
        throw "${What}: HTTP $($Response.Status) $(Get-EcrErrorCode $Response)"
    }
}

function ConvertTo-DocumentIdList {
    <#
    .SYNOPSIS
        Розбирає список id документів: масив (3,10) або рядок «3,10» / «3;10» / «3 10».
        `powershell -File x.ps1 -P 3,10` віддає параметру ОДИН рядок «3,10»; [int[]] прочитав би його як 310.
    #>
    param($Value)
    $ids = New-Object System.Collections.Generic.List[int]
    foreach ($item in @($Value)) {
        if ($null -eq $item) { continue }
        foreach ($tok in ([string]$item -split '[,; ]+')) {
            if ([string]::IsNullOrWhiteSpace($tok)) { continue }
            $n = 0
            if (-not [int]::TryParse($tok.Trim(), [ref]$n) -or $n -le 0) { throw "Некоректний id документа: «$tok»." }
            $ids.Add($n)
        }
    }
    return $ids.ToArray()
}

function Test-EcrMigrateSameVersion {
    <#
    .SYNOPSIS
        $true, якщо відповідь migrate-version — відмова «документ уже на цільовій версії»
        (422 ECR-TMPL-0422, messageKey err.ECR-TMPL-0422.migrateSameVersion).
    #>
    param([Parameter(Mandatory)]$Response)
    if ($Response.Status -ne 422) { return $false }
    return ([string]$Response.Text).Contains('migrateSameVersion')
}

function Test-EcrTemplateUnsuitable {
    <#
    .SYNOPSIS
        $true, якщо відповідь — відмова ECR-TMPL-4228 (фіксована таблиця без рядків): шаблон не Land.
    #>
    param([Parameter(Mandatory)]$Response)
    if ($Response.Status -lt 400 -or $Response.Status -ge 500) { return $false }
    if ((Get-EcrErrorCode $Response) -eq 'ECR-TMPL-4228') { return $true }
    return ([string]$Response.Text).Contains('ECR-TMPL-4228')
}

function Stop-EcrTemplateUnsuitable {
    <#
    .SYNOPSIS
        Зрозуміла діагностика замість голого «HTTP 422 ECR-TMPL-4228»: що не так, які шаблони є.
    #>
    param(
        [Parameter(Mandatory)][string]$TemplateCode,
        [string]$Detail = ''
    )
    $codes = ''
    $t = Invoke-Ecr -Method GET -Path '/api/v1/templates?limit=50'
    if ($t.Status -eq 200 -and $null -ne $t.Json) {
        $codes = (@($t.Json.items) | ForEach-Object { [string]$_.code }) -join ', '
    }
    if ([string]::IsNullOrEmpty($codes)) { $codes = '(не вдалося отримати GET /templates)' }
    throw ("Шаблон '$TemplateCode' не підходить: фіксовані таблиці без рядків (ECR-TMPL-4228). $Detail" +
        "Запускайте на шаблоні Land (імпорт Land: tools/Ecr.MethodologyImport, див. tools/land/README.md). " +
        "Доступні шаблони: $codes.")
}

function Wait-EcrJob {
    <#
    .SYNOPSIS
        Опитує GET /api/v1/jobs/{id} до завершення. Повертає JobStatus; кидає виняток при Failed/таймауті.
    #>
    param(
        [Parameter(Mandatory)][string]$JobId,
        [int]$TimeoutMinutes = 60,
        [int]$PollSeconds = 3
    )
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $lastLine = ''
    while ($true) {
        $r = Invoke-Ecr -Method GET -Path ('/api/v1/jobs/' + [uri]::EscapeDataString($JobId))
        Assert-EcrOk $r "GET jobs/$JobId"
        $s = $r.Json
        $line = "  задача ${JobId}: $($s.state) $($s.percent)% $($s.message)"
        if ($line -ne $lastLine) { Write-Host $line; $lastLine = $line }
        if ($s.state -in @('Failed', 'Cancelled', 'Dead', 'Unavailable')) {
            throw "Задача ${JobId} завершилась зі станом $($s.state): $($s.errorCode) $($s.error)"
        }
        if ($s.state -eq 'Succeeded' -or $s.state -eq 'SucceededWithErrors') { return $s }
        if ((Get-Date) -gt $deadline) { throw "Задача ${JobId}: таймаут $TimeoutMinutes хв (стан $($s.state), $($s.percent)%)." }
        Start-Sleep -Seconds $PollSeconds
    }
}

function Read-Utf8Csv {
    param([Parameter(Mandatory)][string]$Path)
    # Файли в репозиторії — UTF-8 без BOM; -Encoding UTF8 читає обидва варіанти.
    return @(Import-Csv -LiteralPath $Path -Encoding UTF8)
}
