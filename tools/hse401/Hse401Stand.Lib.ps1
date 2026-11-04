# tools/hse401/Hse401Stand.Lib.ps1
# Спільне для скриптів tools/hse401: охорона стенда (ЛИШЕ scratch-база EcrHse401Scratch на порту 5180),
# обліковки, виклик API з повтором на 429/503, допоміжні функції. Залежить від tools/land/EcrApi.ps1.
# Жодних значень прода й секретів у файлі немає; паролі scratch-стенда лежать у artifacts/ (не в git).

Set-StrictMode -Version 2.0

$script:Hse401Here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$script:Hse401Root = Split-Path -Parent (Split-Path -Parent $script:Hse401Here)
. (Join-Path $script:Hse401Root 'tools\land\EcrApi.ps1')

$script:Hse401Db = 'EcrHse401Scratch'
$script:Hse401Port = 5180
$script:Hse401Accounts = Join-Path $script:Hse401Root 'artifacts\hse401-accounts.tsv'
$script:Hse401OutDir = Join-Path $script:Hse401Here 'out'

function Get-Hse401OutDir {
    if (-not (Test-Path $script:Hse401OutDir)) { New-Item -ItemType Directory -Force $script:Hse401OutDir | Out-Null }
    return $script:Hse401OutDir
}

function Write-HseLog { param([string]$Message) Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message) }

function Get-Hse401BaseUrl { return "http://localhost:$($script:Hse401Port)" }

function Assert-Hse401Stand {
    <# Охорона: тільки localhost:5180 (scratch). Порти 5094/6094 і будь-який інший - відмова. #>
    param([string]$BaseUrl = (Get-Hse401BaseUrl))
    $u = [System.Uri]$BaseUrl
    if ($u.Port -ne $script:Hse401Port -or -not (Test-LocalHostName $u.Host)) {
        throw "Дозволено лише http://localhost:$($script:Hse401Port) (scratch $($script:Hse401Db)). Отримано $BaseUrl. Відмова."
    }
}

function Invoke-Hse {
    <# Invoke-Ecr з повтором на 429/503. Повертає відповідь; рішення про помилку - за викликачем. #>
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body = $null, [hashtable]$Headers = @{}, [int]$MaxAttempts = 40)
    $r = $null
    for ($i = 0; $i -lt $MaxAttempts; $i++) {
        if ($null -eq $Body) { $r = Invoke-Ecr -Method $Method -Path $Path -Headers $Headers }
        else { $r = Invoke-Ecr -Method $Method -Path $Path -Body $Body -Headers $Headers }
        if ($r.Status -ne 429 -and $r.Status -ne 503) { return $r }
        $wait = 4
        foreach ($k in @($r.Headers.Keys)) { if ([string]$k -ieq 'Retry-After') { $n = 0; if ([int]::TryParse(([string]$r.Headers[$k]).Trim(), [ref]$n) -and $n -ge 1) { $wait = $n } } }
        Start-Sleep -Seconds $wait
    }
    return $r
}

function Invoke-HseOk {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body = $null, [hashtable]$Headers = @{}, [int[]]$Ok = @(200, 201, 202, 204))
    $r = Invoke-Hse -Method $Method -Path $Path -Body $Body -Headers $Headers
    if ($Ok -notcontains $r.Status) {
        $t = [string]$r.Text
        throw ("{0} {1} -> HTTP {2} {3} {4}" -f $Method, $Path, $r.Status, (Get-EcrErrorCode $r), $t.Substring(0, [Math]::Min(700, $t.Length)))
    }
    return $r
}

function New-HsePassword {
    param([string]$Prefix)
    $b = New-Object byte[] 9
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    return $Prefix + '-' + ([Convert]::ToBase64String($b) -replace '[+/=]', 'q') + '-7Zx'
}

function Get-HseAccounts {
    $acc = @{}
    if (Test-Path $script:Hse401Accounts) {
        foreach ($ln in Get-Content $script:Hse401Accounts) { $p = $ln -split "`t"; if ($p.Count -ge 2) { $acc[$p[0]] = $p[1] } }
    }
    return $acc
}

function Save-HseAccount {
    param([string]$User, [string]$Password)
    $dir = Split-Path -Parent $script:Hse401Accounts
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $lines = @()
    if (Test-Path $script:Hse401Accounts) { $lines = @(Get-Content $script:Hse401Accounts | Where-Object { ($_ -split "`t")[0] -ne $User }) }
    $lines += "$User`t$Password"
    Set-Content -Path $script:Hse401Accounts -Value $lines -Encoding ascii
}

function Connect-Hse {
    <# Вхід під обліковкою зі scratch-файлу (спершу Initialize-Hse401Accounts). Дозволяє мутації (охоронене ім'ям бази). #>
    param([string]$User = 'hse401-owner')
    $base = Get-Hse401BaseUrl
    Assert-Hse401Stand $base
    $acc = Get-HseAccounts
    if (-not $acc.ContainsKey($User)) { throw "Немає пароля $User у $($script:Hse401Accounts): спершу Initialize-Hse401Accounts." }
    Connect-Ecr -BaseUrl $base -User $User -Password (ConvertTo-SecureString $acc[$User] -AsPlainText -Force)
    Set-EcrStandConfirmation -Stand "localhost:$($script:Hse401Port)/$($script:Hse401Db)" -Url $base -Database $script:Hse401Db
}

function Initialize-Hse401Accounts {
    <# bootstrap змінює пароль; створюється роль Hse401Owner (усі дозволи) і користувач hse401-owner. Повтор - без змін. #>
    param([Parameter(Mandatory)][string]$BootstrapPassword)
    $base = Get-Hse401BaseUrl
    Assert-Hse401Stand $base
    Set-EcrStandConfirmation -Stand "localhost:$($script:Hse401Port)/$($script:Hse401Db)" -Url $base -Database $script:Hse401Db
    $acc = Get-HseAccounts
    if (-not $acc.ContainsKey('bootstrap')) {
        Connect-Ecr -BaseUrl $base -User 'bootstrap' -Password (ConvertTo-SecureString $BootstrapPassword -AsPlainText -Force)
        $new = New-HsePassword 'Adm'
        $c = Invoke-Ecr -Method POST -Path '/api/v1/auth/change-password' -Body @{ currentPassword = $BootstrapPassword; newPassword = $new }
        if ($c.Status -in 200, 204) { Save-HseAccount 'bootstrap' $new } else { Save-HseAccount 'bootstrap' $BootstrapPassword }
    }
    Connect-Hse -User 'bootstrap'
    $perms = @((Invoke-HseOk GET '/api/v1/permissions').Json | ForEach-Object { [string]$_.code })
    $roles = @{}
    foreach ($r in @((Invoke-HseOk GET '/api/v1/roles').Json)) { $roles[[string]$r.code] = $r }
    if (-not $roles.ContainsKey('Hse401Owner')) {
        $null = Invoke-HseOk POST '/api/v1/roles' @{ code = 'Hse401Owner'; nameL10n = @{ en = 'HSE401 owner (scratch)' }; permissionCodes = @($perms) }
        Write-HseLog 'роль Hse401Owner створено'
    }
    if (-not (Get-HseAccounts).ContainsKey('hse401-owner')) {
        $pw = New-HsePassword 'Own'
        $r = Invoke-Hse POST '/api/v1/users' @{ userName = 'hse401-owner'; provider = 'Local'; sid = $null; displayName = 'hse401-owner'; initialPassword = $pw; roleCodes = @('Hse401Owner') }
        if ($r.Status -notin 200, 201) { throw "POST users -> $($r.Status) $(Get-EcrErrorCode $r) $($r.Text)" }
        Connect-Ecr -BaseUrl $base -User 'hse401-owner' -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
        $final = New-HsePassword 'Own'
        $c = Invoke-Ecr -Method POST -Path '/api/v1/auth/change-password' -Body @{ currentPassword = $pw; newPassword = $final }
        if ($c.Status -in 200, 204) { Save-HseAccount 'hse401-owner' $final } else { Save-HseAccount 'hse401-owner' $pw }
        Write-HseLog 'користувач hse401-owner створено'
    }
    Connect-Hse -User 'hse401-owner'
}

function Wait-HseJob {
    <# Опитування задачі з розумним інтервалом (не tight loop). Повертає останній стан (state/percent). #>
    param([Parameter(Mandatory)][string]$JobId, [int]$TimeoutMinutes = 60, [int]$PollSeconds = 5)
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ($true) {
        $r = Invoke-Hse GET ('/api/v1/jobs/' + [uri]::EscapeDataString($JobId))
        if ($r.Status -ne 200) { throw "GET jobs/$JobId -> $($r.Status)" }
        $s = $r.Json
        if ($s.state -in @('Failed', 'Cancelled', 'Dead', 'Unavailable')) { throw "Задача ${JobId}: $($s.state) $($s.errorCode) $($s.error)" }
        if ($s.state -in @('Succeeded', 'SucceededWithErrors')) { return $s }
        if ((Get-Date) -gt $deadline) { throw "Задача ${JobId}: таймаут $TimeoutMinutes хв (стан $($s.state), $($s.percent)%)." }
        Start-Sleep -Seconds $PollSeconds
    }
}

function Split-HseBilingual {
    <# "English - Русский" -> @{ En; Ru }: розділювач " - " останній, ліворуч від якого немає кирилиці. #>
    param([string]$Text)
    $t = ([string]$Text).Trim()
    if ($t.Length -eq 0) { return @{ En = ''; Ru = '' } }
    $idx = -1
    $pos = 0
    while (($i = $t.IndexOf(' - ', $pos, [StringComparison]::Ordinal)) -ge 0) {
        $left = $t.Substring(0, $i)
        if ($left -notmatch '[А-Яа-яЁёІіЇїЄєҚқҒғҢңӘәӨөҰұҮүҺһ]') { $idx = $i }
        $pos = $i + 1
    }
    if ($idx -lt 0) {
        if ($t -match '[А-Яа-яЁё]') { return @{ En = ''; Ru = $t } }
        return @{ En = $t; Ru = '' }
    }
    $right = $t.Substring($idx + 3).Trim()
    if ($right -notmatch '[А-Яа-яЁё]') { return @{ En = $t; Ru = '' } }
    return @{ En = $t.Substring(0, $idx).Trim(); Ru = $right }
}

function ConvertTo-HseCode {
    <# Код запису довідника: літера першою, далі A-Z0-9_ (EcrCode). #>
    param([string]$Prefix, [string]$Text)
    $s = ([string]$Text).ToUpperInvariant() -replace '[^A-Z0-9]+', '_'
    $s = $s.Trim('_')
    if ($s.Length -gt 60) { $s = $s.Substring(0, 60).TrimEnd('_') }
    return ($Prefix + '_' + $s)
}

function Get-HseItems {
    param($Json)
    if ($null -eq $Json) { return @() }
    if ($Json -is [array]) { return @($Json) }
    if ($Json.PSObject.Properties['items']) { return @($Json.items) }
    return @($Json)
}

function Wait-HseDocJobs {
    <# Чекає, поки не лишиться активних задач перерахунку документа (інтервал 3 c, не tight loop).
       Повертає @{ Jobs; FirstCreated; LastUpdated; Seconds; Failed }; Since - UTC-час, раніше за який задачі ігноруються. #>
    param([Parameter(Mandatory)][int]$DocumentId, [datetime]$Since, [int]$TimeoutMinutes = 60, [int]$PollSeconds = 3)
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $quiet = 0
    while ($true) {
        $r = Invoke-Hse GET '/api/v1/jobs?limit=50'
        if ($r.Status -ne 200) { throw "GET jobs -> $($r.Status)" }
        $mine = @(@($r.Json) | Where-Object { $_.documentId -eq $DocumentId -and ([datetime]$_.createdAt).ToUniversalTime() -ge $Since.ToUniversalTime() })
        $active = @($mine | Where-Object { $_.state -notin 'Succeeded', 'SucceededWithErrors', 'Failed', 'Cancelled', 'Dead' })
        if ($active.Count -eq 0) { $quiet++ } else { $quiet = 0 }
        if ($quiet -ge 2) {
            $failed = @($mine | Where-Object { $_.state -in 'Failed', 'Cancelled', 'Dead' })
            $first = $null; $last = $null
            if ($mine.Count -gt 0) {
                $first = ($mine | ForEach-Object { ([datetime]$_.createdAt).ToUniversalTime() } | Measure-Object -Minimum).Minimum
                $last = ($mine | ForEach-Object { ([datetime]$_.updatedAt).ToUniversalTime() } | Measure-Object -Maximum).Maximum
            }
            return [pscustomobject]@{ Jobs = $mine.Count; FirstCreated = $first; LastUpdated = $last; Failed = $failed.Count; Messages = @($mine | Select-Object -First 3 | ForEach-Object { $_.message }) }
        }
        if ((Get-Date) -gt $deadline) { throw "Задачі документа $DocumentId не завершились за $TimeoutMinutes хв" }
        Start-Sleep -Seconds $PollSeconds
    }
}