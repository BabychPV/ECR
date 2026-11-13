# tools/hse400/Hse400Stand.Lib.ps1
# Ціль і охорона стенда для скриптів tools/hse400. Залежить від tools/land/EcrApi.ps1 і tools/hse401/Hse401Json.Lib.ps1.
#
# Стенд завжди задається явно: -BaseUrl (лише локальна адреса), -Database, -ConfirmStand <база>, -CredentialFile
# (TSV "користувач<TAB>пароль" поза git-worktree; файл лише читається, пароль у лог не потрапляє).
# Скрипти НЕ створюють облікових записів, ролей і баз. -PreviewOnly - лише GET.

Set-StrictMode -Version 2.0

$script:Hse400Here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$script:Hse400Root = Split-Path -Parent (Split-Path -Parent $script:Hse400Here)
. (Join-Path $script:Hse400Root 'tools\land\EcrApi.ps1')
. (Join-Path $script:Hse400Root 'tools\hse401\Hse401Json.Lib.ps1')

$script:Hse400Target = $null

function Write-Hse400Log { param([string]$Message) Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message) }

function Get-Hse400OutDir {
    $d = Join-Path $script:Hse400Here 'out'
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
    return $d
}

function Get-Hse400Target { if ($null -eq $script:Hse400Target) { throw 'Ціль не задано: спершу Set-Hse400Target.' }; return $script:Hse400Target }
function Get-Hse400BaseUrl { return ([string](Get-Hse400Target).BaseUrl).TrimEnd('/') }
function Test-Hse400Preview { return [bool](Get-Hse400Target).PreviewOnly }
function Get-Hse400StandName { $u = [System.Uri](Get-Hse400BaseUrl); return "$($u.Authority)/$((Get-Hse400Target).Database)" }
function Get-Hse400StandKey { return ('{0}|{1}' -f (Get-Hse400BaseUrl), (Get-Hse400Target).Database).ToLowerInvariant() }
function Get-Hse400StateFile { return (Join-Path (Get-Hse400OutDir) ('hse400-state.{0}.json' -f (Get-Hse400Target).Database.ToLowerInvariant())) }

function Test-Hse400InsideGit {
    <# $true, якщо шлях лежить під коренем цього репозиторію або всередині будь-якого git-worktree. #>
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($script:Hse400Root).TrimEnd('\') + '\'
    if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $dir = Split-Path -Parent $full
    while ($dir) {
        if (Test-Path -LiteralPath (Join-Path $dir '.git')) { return $true }
        $parent = Split-Path -Parent $dir
        if (-not $parent -or $parent -eq $dir) { break }
        $dir = $parent
    }
    return $false
}

function Set-Hse400Target {
    param([string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile, [switch]$PreviewOnly)
    if ([string]::IsNullOrWhiteSpace($BaseUrl)) { throw 'Потрібен -BaseUrl (наприклад http://localhost:5094).' }
    if ([string]::IsNullOrWhiteSpace($Database) -or $Database.Trim() -notmatch '^[A-Za-z0-9_]+$') { throw 'Потрібен -Database (ім''я бази стенда).' }
    if ([string]::IsNullOrWhiteSpace($ConfirmStand) -or $ConfirmStand.Trim() -ine $Database.Trim()) {
        throw "-ConfirmStand має дорівнювати -Database ('$Database'). Без цього скрипт нічого не робить."
    }
    if ([string]::IsNullOrWhiteSpace($CredentialFile)) { throw 'Потрібен -CredentialFile (TSV "користувач<TAB>пароль", поза git-worktree).' }
    Assert-LocalBaseUrl $BaseUrl
    $uri = [System.Uri]$BaseUrl
    if ($uri.Scheme -notin 'http', 'https') { throw "Схема $($uri.Scheme) не підтримується." }
    $cf = [System.IO.Path]::GetFullPath($CredentialFile)
    if (Test-Hse400InsideGit $cf) { throw 'Файл облікових даних лежить усередині git-worktree. Покладіть його поза репозиторієм. Відмова.' }
    if (-not (Test-Path -LiteralPath $cf -PathType Leaf)) { throw "Файл облікових даних не знайдено: $cf" }
    $script:Hse400Target = [pscustomobject]@{
        BaseUrl = $BaseUrl.TrimEnd('/'); Database = $Database.Trim(); CredentialFile = $cf; PreviewOnly = [bool]$PreviewOnly
    }
}

function Read-Hse400Credential {
    <# Перший непорожній рядок "користувач<TAB>пароль". Пароль у повідомленнях не фігурує. #>
    $line = $null
    foreach ($ln in [System.IO.File]::ReadAllLines((Get-Hse400Target).CredentialFile, [System.Text.Encoding]::UTF8)) { if ($ln.Trim().Length -gt 0) { $line = $ln; break } }
    if ($null -eq $line) { throw 'Файл облікових даних порожній.' }
    $idx = $line.IndexOf("`t")
    if ($idx -lt 1 -or $idx -ge $line.Length - 1) { throw 'Файл облікових даних: очікується один рядок "користувач<TAB>пароль".' }
    return [pscustomobject]@{ User = $line.Substring(0, $idx).Trim(); Password = (ConvertTo-SecureString ($line.Substring($idx + 1).TrimEnd("`r", "`n")) -AsPlainText -Force) }
}

function Connect-Hse400 {
    <# Вхід за -CredentialFile. Мутації можливі лише після Set-EcrStandConfirmation; під -PreviewOnly його немає. #>
    $base = Get-Hse400BaseUrl
    $c = Read-Hse400Credential
    Connect-Ecr -BaseUrl $base -User $c.User -Password $c.Password
    if (-not (Test-Hse400Preview)) { Set-EcrStandConfirmation -Stand (Get-Hse400StandName) -Url $base -Database (Get-Hse400Target).Database }
}

function Invoke-Hse400 {
    <# Invoke-Ecr з повтором на 429/503. Під -PreviewOnly - лише GET/HEAD. #>
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body = $null, [int]$MaxAttempts = 40)
    if ((Test-Hse400Preview) -and $Method.ToUpperInvariant() -notin 'GET', 'HEAD') { throw "PreviewOnly: $Method $Path заборонено." }
    $r = $null
    for ($i = 0; $i -lt $MaxAttempts; $i++) {
        if ($null -eq $Body) { $r = Invoke-Ecr -Method $Method -Path $Path } else { $r = Invoke-Ecr -Method $Method -Path $Path -Body $Body }
        if ($r.Status -ne 429 -and $r.Status -ne 503) { return $r }
        $wait = 4
        foreach ($k in @($r.Headers.Keys)) { if ([string]$k -ieq 'Retry-After') { $n = 0; if ([int]::TryParse(([string]$r.Headers[$k]).Trim(), [ref]$n) -and $n -ge 1) { $wait = $n } } }
        Start-Sleep -Seconds $wait
    }
    return $r
}

function Invoke-Hse400Ok {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body = $null, [int[]]$Ok = @(200, 201, 202, 204))
    $r = Invoke-Hse400 -Method $Method -Path $Path -Body $Body
    if ($Ok -notcontains $r.Status) {
        $t = [string]$r.Text
        throw ("{0} {1} -> HTTP {2} {3} {4}" -f $Method, $Path, $r.Status, (Get-EcrErrorCode $r), $t.Substring(0, [Math]::Min(700, $t.Length)))
    }
    return $r
}

function Get-Hse400Items {
    param($Json)
    if ($null -eq $Json) { return @() }
    if ($Json -is [array]) { return @($Json) }
    if ($Json.PSObject.Properties['items']) { return @($Json.items) }
    return @($Json)
}

function Get-Hse400AllItems {
    <# Усі сторінки PagedResult {items, nextCursor} (CursorPagination: limit 1..500, параметр cursor). #>
    param([Parameter(Mandatory)][string]$Path, [int]$Limit = 200, [int]$MaxPages = 50)
    $all = New-Object System.Collections.Generic.List[object]
    $sep = if ($Path.Contains('?')) { '&' } else { '?' }
    $cursor = $null
    for ($i = 0; $i -lt $MaxPages; $i++) {
        $p = '{0}{1}limit={2}' -f $Path, $sep, $Limit
        if ($cursor) { $p += '&cursor=' + [Uri]::EscapeDataString([string]$cursor) }
        $j = (Invoke-Hse400Ok -Method GET -Path $p).Json
        foreach ($x in Get-Hse400Items $j) { $all.Add($x) }
        $cursor = $null
        if ($null -ne $j -and -not ($j -is [array]) -and $j.PSObject.Properties['nextCursor']) { $cursor = $j.nextCursor }
        if (-not $cursor) { break }
    }
    return $all.ToArray()
}

function Read-Hse400State {
    <# Стан (id шаблону/проєкту/документа) прив'язаний до стенда, на якому його створено. #>
    $f = Get-Hse400StateFile
    if (-not (Test-Path -LiteralPath $f)) { return @{} }
    $s = Read-HseJson $f
    $key = if ($s.ContainsKey('standKey')) { [string]$s['standKey'] } else { $null }
    if ($key -and $key -ne (Get-Hse400StandKey)) { throw "Файл стану належить іншому стенду ($key), а ціль - $(Get-Hse400StandKey). Відмова." }
    return $s
}

function Save-Hse400State {
    param([Parameter(Mandatory)]$State)
    $State['standKey'] = Get-Hse400StandKey
    [IO.File]::WriteAllText((Get-Hse400StateFile), (ConvertTo-HseJson -Value $State).ToString(), (New-Object Text.UTF8Encoding($false)))
}
