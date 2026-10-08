# tools/land/EcrApi.ps1
# Спільний клієнт REST API ECR для скриптів tools/land (dot-source: . "$PSScriptRoot\EcrApi.ps1").
# Сумісний з Windows PowerShell 5.1 і PowerShell 7. Секретів у файлі немає: логін і пароль
# беруться з параметрів скрипта або зі змінних середовища ECR_USER / ECR_PASSWORD.

Set-StrictMode -Version 2.0

$script:EcrBase = $null
$script:EcrSession = $null

function ConvertTo-PlainText {
    param([Parameter(Mandatory)][securestring]$Secure)
    $cred = New-Object System.Net.NetworkCredential('', $Secure)
    return $cred.Password
}

function Connect-Ecr {
    <#
    .SYNOPSIS
        Локальний вхід (POST /api/v1/login/local); сесія тримається в cookie-jar.
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

    $script:EcrBase = $BaseUrl.TrimEnd('/')
    $script:EcrSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

    $r = Invoke-Ecr -Method POST -Path '/api/v1/login/local' -Body @{ userName = $User; password = $plain }
    if ($r.Status -ne 200) {
        # Тіло відповіді не друкуємо повністю: у ньому немає пароля, але й корисного — лише код помилки.
        throw "Вхід не вдався: HTTP $($r.Status) $(Get-EcrErrorCode $r)."
    }
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

    $p = @{
        Uri             = $script:EcrBase + $Path
        Method          = $Method
        WebSession      = $script:EcrSession
        UseBasicParsing = $true
        Headers         = $Headers
        ErrorAction     = 'Stop'
    }
    if ($null -ne $Body) {
        # Тіло — байти UTF-8: рядок у PS 5.1 без charset кодується як ISO-8859-1 і ламає кирилицю.
        $p.Body = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress))
        $p.ContentType = 'application/json; charset=utf-8'
    }

    $status = 0
    $text = ''
    $hdrs = $null
    try {
        $resp = Invoke-WebRequest @p
        $status = [int]$resp.StatusCode
        $text = [string]$resp.Content
        $hdrs = $resp.Headers
    }
    catch {
        $errResp = $null
        if ($_.Exception.PSObject.Properties.Match('Response').Count -gt 0) { $errResp = $_.Exception.Response }
        if ($null -eq $errResp) { throw }
        $status = [int]$errResp.StatusCode
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $text = $_.ErrorDetails.Message }
        elseif ($errResp.PSObject.Methods.Match('GetResponseStream').Count -gt 0) {
            $reader = New-Object System.IO.StreamReader($errResp.GetResponseStream(), [System.Text.Encoding]::UTF8)
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        $hdrs = $errResp.Headers
    }

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

function Read-Utf8Csv {
    param([Parameter(Mandatory)][string]$Path)
    # Файли в репозиторії — UTF-8 без BOM; -Encoding UTF8 читає обидва варіанти.
    return @(Import-Csv -LiteralPath $Path -Encoding UTF8)
}
