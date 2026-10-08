# tools/land/EcrApi.ps1
# Спільний клієнт REST API ECR для скриптів tools/land (dot-source: . "$PSScriptRoot\EcrApi.ps1").
# Сумісний з Windows PowerShell 5.1 і PowerShell 7. Секретів у файлі немає: логін і пароль
# беруться з параметрів скрипта або зі змінних середовища ECR_USER / ECR_PASSWORD.

Set-StrictMode -Version 2.0

$script:EcrBase = $null
$script:EcrSession = $null
$script:EcrClient = $null

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
