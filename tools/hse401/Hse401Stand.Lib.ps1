# tools/hse401/Hse401Stand.Lib.ps1
# Спільне для скриптів tools/hse401: ціль (Scratch | Live), охорона стенда, обліковки, виклик API з повтором на 429/503,
# режим -PreviewOnly (лише читання), допоміжні функції. Залежить від tools/land/EcrApi.ps1.
#
# Ціль Scratch (за замовчуванням, поведінка НЕ змінена): ЛИШЕ база EcrHse401Scratch на http://localhost:5180; паролі scratch-стенда
# лежать у artifacts/ (не в git).
# Ціль Live: стенд задається явно (-BaseUrl, -Database), підтверджується -ConfirmStand <база>, вхід - з файлу -CredentialFile
# (TSV "користувач<TAB>пароль"; файл лише читається, пароль не пишеться в лог; шлях усередині git-worktree заборонено).
# У Live немає Create/Start/Stop/DROP, bootstrap, створення ролей і користувачів.

Set-StrictMode -Version 2.0

$script:Hse401Here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$script:Hse401Root = Split-Path -Parent (Split-Path -Parent $script:Hse401Here)
. (Join-Path $script:Hse401Root 'tools\land\EcrApi.ps1')
. (Join-Path $script:Hse401Here 'Hse401Json.Lib.ps1')

$script:Hse401Db = 'EcrHse401Scratch'
$script:Hse401Port = 5180
$script:Hse401Accounts = Join-Path $script:Hse401Root 'artifacts\hse401-accounts.tsv'
$script:Hse401OutDir = Join-Path $script:Hse401Here 'out'

# Поточна ціль. Задається один раз на скрипт через Set-Hse401Target; до виклику - Scratch.
$script:Hse401Target = [pscustomobject]@{
    Kind = 'Scratch'; BaseUrl = "http://localhost:$($script:Hse401Port)"; Database = $script:Hse401Db
    CredentialFile = $null; PreviewOnly = $false; AcceptNaePreview = $false
}

function Get-Hse401OutDir {
    if (-not (Test-Path $script:Hse401OutDir)) { New-Item -ItemType Directory -Force $script:Hse401OutDir | Out-Null }
    return $script:Hse401OutDir
}

function Write-HseLog { param([string]$Message) Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message) }

function Get-Hse401BaseUrl { return ([string]$script:Hse401Target.BaseUrl).TrimEnd('/') }

function Test-Hse401Live { return ($script:Hse401Target.Kind -eq 'Live') }
function Test-Hse401Preview { return [bool]$script:Hse401Target.PreviewOnly }

function Get-Hse401StandName {
    <# "хост:порт/база" - форма, яку приймає Set-EcrStandConfirmation. #>
    $u = [System.Uri](Get-Hse401BaseUrl)
    return "$($u.Authority)/$($script:Hse401Target.Database)"
}

function Get-Hse401StandKey { return ('{0}|{1}' -f (Get-Hse401BaseUrl), $script:Hse401Target.Database).ToLowerInvariant() }

# Файли стану окремі для Live: стан scratch-прогону не повинен підмінити id живого стенда.
function Get-Hse401StateFile { return (Join-Path (Get-Hse401OutDir) $(if (Test-Hse401Live) { 'hse401-state.live.json' } else { 'hse401-state.json' })) }
function Get-Hse401NaeMapFile { return (Join-Path (Get-Hse401OutDir) $(if (Test-Hse401Live) { 'hse401-nae-map.live.json' } else { 'hse401-nae-map.json' })) }

function Assert-Hse401StateMatches {
    <# Стан (id шаблону/документа) прив'язаний до стенда, на якому його створено. #>
    param($State)
    $key = if ($State.ContainsKey('standKey')) { [string]$State['standKey'] } else { $null }
    if ($key -and $key -ne (Get-Hse401StandKey)) {
        throw "Файл стану належить іншому стенду ($key), а ціль - $(Get-Hse401StandKey). Відмова."
    }
}

function Test-Hse401InsideGit {
    <# $true, якщо шлях лежить усередині git-worktree (є .git у самому каталозі чи в предку) або під коренем цього репозиторію. #>
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($script:Hse401Root).TrimEnd('\') + '\'
    if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $dir = Split-Path -Parent $full
    while ($dir) {
        if (Test-Path -LiteralPath (Join-Path $dir '.git')) { return $true }
        $parent = Split-Path -Parent $dir
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    return $false
}

function Set-Hse401Target {
    <# Один раз на скрипт. Scratch - як було (охорона 5180 / EcrHse401Scratch). Live - явний стенд + -ConfirmStand = база + файл облікових даних. #>
    param(
        [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch',
        [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
        [switch]$PreviewOnly, [switch]$AcceptNaePreview
    )
    if ($Target -eq 'Scratch') {
        if ($ConfirmStand -or $CredentialFile) { throw '-ConfirmStand і -CredentialFile потрібні лише для -Target Live.' }
        if ($Database -and $Database -ne $script:Hse401Db) { throw "Для Scratch дозволена лише база $($script:Hse401Db). Отримано $Database. Відмова." }
        $url = if ($BaseUrl) { $BaseUrl } else { "http://localhost:$($script:Hse401Port)" }
        $script:Hse401Target = [pscustomobject]@{
            Kind = 'Scratch'; BaseUrl = $url; Database = $script:Hse401Db
            CredentialFile = $null; PreviewOnly = [bool]$PreviewOnly; AcceptNaePreview = [bool]$AcceptNaePreview
        }
        Assert-Hse401Stand $url
        return
    }
    # ---- Live ----
    if ([string]::IsNullOrWhiteSpace($BaseUrl)) { throw 'Live: потрібен -BaseUrl (наприклад http://localhost:5094).' }
    if ([string]::IsNullOrWhiteSpace($Database)) { throw 'Live: потрібен -Database.' }
    if ([string]::IsNullOrWhiteSpace($ConfirmStand) -or $ConfirmStand.Trim() -ine $Database.Trim()) {
        throw "Live: -ConfirmStand має дорівнювати -Database ('$Database'). Без цього скрипт нічого не робить."
    }
    if ([string]::IsNullOrWhiteSpace($CredentialFile)) { throw 'Live: потрібен -CredentialFile (TSV "користувач<TAB>пароль", поза git-worktree).' }
    Assert-LocalBaseUrl $BaseUrl
    $uri = [System.Uri]$BaseUrl
    if ($uri.Scheme -notin 'http', 'https') { throw "Live: схема $($uri.Scheme) не підтримується." }
    $cf = [System.IO.Path]::GetFullPath($CredentialFile)
    if (Test-Hse401InsideGit $cf) { throw 'Live: файл облікових даних лежить усередині git-worktree (або під коренем репозиторію). Покладіть його поза репозиторієм. Відмова.' }
    if (-not (Test-Path -LiteralPath $cf -PathType Leaf)) { throw "Live: файл облікових даних не знайдено: $cf" }
    $script:Hse401Target = [pscustomobject]@{
        Kind = 'Live'; BaseUrl = $BaseUrl.TrimEnd('/'); Database = $Database.Trim()
        CredentialFile = $cf; PreviewOnly = [bool]$PreviewOnly; AcceptNaePreview = [bool]$AcceptNaePreview
    }
}

function Assert-Hse401Stand {
    <# Scratch: тільки localhost:5180. Порти 5094/6094 і будь-який інший - відмова. Live: адреса мусить збігатися з обраною ціллю й бути локальною. #>
    param([string]$BaseUrl = (Get-Hse401BaseUrl))
    $u = [System.Uri]$BaseUrl
    if (Test-Hse401Live) {
        $t = [System.Uri](Get-Hse401BaseUrl)
        if ($u.Authority -ne $t.Authority -or -not (Test-LocalHostName $u.Host)) { throw "Live: адреса $BaseUrl не збігається з ціллю $(Get-Hse401BaseUrl). Відмова." }
        return
    }
    if ($u.Port -ne $script:Hse401Port -or -not (Test-LocalHostName $u.Host)) {
        throw "Дозволено лише http://localhost:$($script:Hse401Port) (scratch $($script:Hse401Db)). Отримано $BaseUrl. Відмова."
    }
}

function Assert-Hse401MethodAllowed {
    <# Центральна охорона -PreviewOnly: лише GET/HEAD та аналіз+перегляд NAE-імпорту (apply - ні). #>
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path)
    if (-not (Test-Hse401Preview)) { return }
    $m = $Method.ToUpperInvariant()
    if ($m -eq 'GET' -or $m -eq 'HEAD') { return }
    if ($m -eq 'POST' -and $Path -match '^/api/v1/nae-tree/import/(analyse|preview)(\?.*)?$') { return }
    throw "PreviewOnly: $Method $Path заборонено (дозволені лише GET та NAE analyse/preview)."
}

function Invoke-Hse {
    <# Invoke-Ecr з повтором на 429/503. Повертає відповідь; рішення про помилку - за викликачем. #>
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body = $null, [hashtable]$Headers = @{}, [int]$MaxAttempts = 40)
    Assert-Hse401MethodAllowed -Method $Method -Path $Path
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
    if (Test-Hse401Live) { throw 'Live: scratch-файл облікових даних не пишеться.' }
    $dir = Split-Path -Parent $script:Hse401Accounts
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $lines = @()
    if (Test-Path $script:Hse401Accounts) { $lines = @(Get-Content $script:Hse401Accounts | Where-Object { ($_ -split "`t")[0] -ne $User }) }
    $lines += "$User`t$Password"
    Set-Content -Path $script:Hse401Accounts -Value $lines -Encoding ascii
}

function Read-Hse401Credential {
    <# Live: перший непорожній рядок "користувач<TAB>пароль". Файл лише читається; пароль у повідомленнях не фігурує. #>
    $f = $script:Hse401Target.CredentialFile
    $line = $null
    foreach ($ln in [System.IO.File]::ReadAllLines($f, [System.Text.Encoding]::UTF8)) { if ($ln.Trim().Length -gt 0) { $line = $ln; break } }
    if ($null -eq $line) { throw 'Файл облікових даних порожній.' }
    $idx = $line.IndexOf("`t")
    if ($idx -lt 1 -or $idx -ge $line.Length - 1) { throw 'Файл облікових даних: очікується один рядок "користувач<TAB>пароль".' }
    $user = $line.Substring(0, $idx).Trim()
    $pw = $line.Substring($idx + 1).TrimEnd("`r", "`n")
    return [pscustomobject]@{ User = $user; Password = (ConvertTo-SecureString $pw -AsPlainText -Force) }
}

function Connect-Hse {
    <# Scratch: вхід під обліковкою зі scratch-файлу (спершу Initialize-Hse401Accounts). Live: вхід за -CredentialFile.
       Мутації дозволяються лише після Set-EcrStandConfirmation; під -PreviewOnly підтвердження НЕ ставиться (другий рубіж охорони). #>
    param([string]$User = 'hse401-owner')
    $base = Get-Hse401BaseUrl
    Assert-Hse401Stand $base
    if (Test-Hse401Live) {
        $c = Read-Hse401Credential
        Connect-Ecr -BaseUrl $base -User $c.User -Password $c.Password
        if (-not (Test-Hse401Preview)) { Set-EcrStandConfirmation -Stand (Get-Hse401StandName) -Url $base -Database $script:Hse401Target.Database }
        return
    }
    $acc = Get-HseAccounts
    if (-not $acc.ContainsKey($User)) { throw "Немає пароля $User у $($script:Hse401Accounts): спершу Initialize-Hse401Accounts." }
    Connect-Ecr -BaseUrl $base -User $User -Password (ConvertTo-SecureString $acc[$User] -AsPlainText -Force)
    if (-not (Test-Hse401Preview)) { Set-EcrStandConfirmation -Stand "localhost:$($script:Hse401Port)/$($script:Hse401Db)" -Url $base -Database $script:Hse401Db }
}

function Initialize-Hse401Accounts {
    <# Лише Scratch: bootstrap змінює пароль; створюється роль Hse401Owner (усі дозволи) і користувач hse401-owner. Повтор - без змін. #>
    param([Parameter(Mandatory)][string]$BootstrapPassword)
    if (Test-Hse401Live) { throw 'Live: Initialize-Hse401Accounts заборонено (без bootstrap, без створення ролей і користувачів).' }
    if (Test-Hse401Preview) { throw 'PreviewOnly: Initialize-Hse401Accounts заборонено.' }
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

# ---------------- план -PreviewOnly: out/hse401-preview.json ----------------

function Get-Hse401PreviewFile { return (Join-Path (Get-Hse401OutDir) 'hse401-preview.json') }

function Reset-Hse401Plan {
    $f = Get-Hse401PreviewFile
    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
}

function Read-Hse401Plan {
    $f = Get-Hse401PreviewFile
    $plan = [ordered]@{}
    if (Test-Path -LiteralPath $f) { $old = Read-HseJson $f; foreach ($k in $old.Keys) { $plan[$k] = $old[$k] } }
    return $plan
}

function Save-Hse401PlanSection {
    <# Дописує розділ у спільний файл плану (окремі процеси-скрипти зливають свої розділи). Лише локальний файл. #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Value)
    $plan = Read-Hse401Plan
    $plan['target'] = [ordered]@{ kind = $script:Hse401Target.Kind; baseUrl = (Get-Hse401BaseUrl); database = $script:Hse401Target.Database; generatedUtc = (Get-Date).ToUniversalTime().ToString('s') + 'Z' }
    $plan[$Name] = $Value
    [IO.File]::WriteAllText((Get-Hse401PreviewFile), (ConvertTo-HseJson -Value $plan).ToString(), (New-Object Text.UTF8Encoding($false)))
}

function Write-Hse401PlanSummary {
    <# Підсумок плану в консоль (і шлях до JSON). Усі заплановані заповнення COMPONENT/PHASE - поіменно. #>
    $plan = Read-Hse401Plan
    Write-Host ''
    Write-HseLog "=== ПЛАН (PreviewOnly) цілі $($script:Hse401Target.Kind): $(Get-Hse401BaseUrl) / $($script:Hse401Target.Database) ==="
    if ($plan.Contains('registries')) {
        $r = $plan['registries']
        Write-HseLog ("довідники: створити {0}: {1}" -f @($r['registriesToCreate']).Count, (@($r['registriesToCreate']) -join ', '))
        foreach ($k in $r['fieldsToAdd'].Keys) { Write-HseLog ("  {0}: додати поля {1}" -f $k, (@($r['fieldsToAdd'][$k]) -join ', ')) }
        foreach ($k in $r['entriesToCreate'].Keys) { Write-HseLog ("  {0}: створити записів {1}" -f $k, @($r['entriesToCreate'][$k]).Count) }
        Write-HseLog ("заповнення COMPONENT/PHASE: {0} (вже заповнено: {1})" -f @($r['fills']).Count, $r['alreadySet'])
        foreach ($f in @($r['fills'])) { Write-HseLog ("  {0} {1} {2} <- {3}" -f $f['registry'], $f['code'], $f['field'], $f['value']) }
    }
    if ($plan.Contains('nae')) {
        $n = $plan['nae']
        Write-HseLog ("NAE: рядків {0}, canApply={1}, помилок {2}, попереджень {3}" -f $n['rowsRead'], $n['canApply'], $n['errors'].Count, $n['warnings'])
        foreach ($l in @($n['levels'])) { Write-HseLog ("  рівень {0}: створити {1}, існує {2}" -f $l['levelCode'], $l['create'], $l['existing']) }
        foreach ($e in @($n['errors'])) { Write-HseLog ("  ПОМИЛКА рядок {0} {1} {2} {3}" -f $e['row'], $e['code'], $e['naeCode'], $e['detail']) }
        Write-HseLog ("  вилучені вузли у файлі збігаються з: {0}; прихованих (неактивних/закінчених) збігів SRC: {1}" -f @($n['retiredMatches']).Count, $n['hiddenExistingSrc'])
        foreach ($m in @($n['retiredMatches'])) { Write-HseLog ("  ВИЛУЧЕНИЙ {0} {1} {2} validTo={3}" -f $m['levelCode'], $m['code'], $m['display'], $m['validTo']) }
    }
    if ($plan.Contains('template')) { $t = $plan['template']; Write-HseLog ("шаблон {0}: існує={1}, опублікована версія={2}, чернетка={3}" -f $t['templateCode'], $t['templateExists'], $t['publishedVersion'], $t['draftVersion']) }
    if ($plan.Contains('project')) {
        $p = $plan['project']
        Write-HseLog ("політика HSE401_OPEN існує={0}; проєкт HSE401_2026 існує={1}; документ існує={2}" -f $p['policyExists'], $p['projectExists'], $p['documentExists'])
    }
    if ($plan.Contains('load')) {
        $l = $plan['load']
        Write-HseLog ("завантаження: рядків {0}, вхідних комірок до PATCH {1}, пачок {2}; рядків у документі вже: {3}" -f $l['rows'], $l['inputCells'], $l['batches'], $l['existingRowsInDocument'])
    }
    Write-HseLog "план: $(Get-Hse401PreviewFile)"
}

function Find-Hse401Document {
    <# Проєкт HSE401_2026 і його документ (лише GET). $null - проєкту немає; documentId $null - документа немає. #>
    param([string]$ProjectCode = 'HSE401_2026')
    $prj = Get-HseItems (Invoke-HseOk GET '/api/v1/projects').Json | Where-Object { $_.code -eq $ProjectCode } | Select-Object -First 1
    if ($null -eq $prj) { return $null }
    $res = [ordered]@{ projectId = [int]$prj.id; documentId = $null; periodKey = $null; tableInstanceId = $null }
    $docs = Get-HseItems (Invoke-HseOk GET "/api/v1/documents?limit=200&projectId=$($prj.id)").Json
    $doc = $docs | Where-Object { $_.projectId -eq $prj.id } | Select-Object -First 1
    if ($null -eq $doc) { return $res }
    $res.documentId = [int]$doc.id
    $per = (Invoke-HseOk GET "/api/v1/projects/$($prj.id)/periods").Json
    $res.periodKey = [int](@($per.periods) | Select-Object -First 1).periodKey
    $tabs = (Invoke-HseOk GET "/api/v1/documents/$($doc.id)/tables?periodKey=$($res.periodKey)").Json
    $ti = @($tabs) | Where-Object { $_.tableCode -eq $script:HseTableCode } | Select-Object -First 1
    if ($null -ne $ti) { $res.tableInstanceId = [int]$ti.tableInstanceId }
    return $res
}
