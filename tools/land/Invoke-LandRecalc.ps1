<#
.SYNOPSIS
    Перерахунок документів стенда (за -DocMap ім'я -> id) за періодами (POST /api/v1/documents/{id}/recalculate) з очікуванням задач.

.DESCRIPTION
    Після точкового завантаження вхідних комірок (Load-LandInputCells.ps1) і публікації версій методологій результати
    треба перерахувати. Інструмент іде послідовно: документ за документом, період за періодом; 429 (квота) чекає
    Retry-After; кожна задача відстежується до кінця (GET /api/v1/jobs/{id}). Значень у вивід не йде: лише ім'я
    документа, період, стан, секунди.

    За замовчуванням (і з -DryRun) лише читає: входить, зіставляє імена документів з id і друкує план. Запуск
    перерахунків - тільки з -Execute, явним -SqlConnection (локальний сервер, база EcrLand*) і -ConfirmStand
    `<адреса>/<база>`. Документ береться з -DocMap (ім'я -> id), не за назвою на стенді; недоступний документ/період
    (NO_DOC) - зупинка ДО першого перерахунку й код виходу 1 (також у плані). Лише локальний API.

    Код виходу 1, якщо хоч одна задача не завершилась успішно.

.PARAMETER DocMap          JSON {"<ім'я документа>": <id>, ...} (рядок або шлях до файла): які документи й під якими id.
.PARAMETER PeriodKey       Періоди Рік*100+Місяць (202601,202602 або 202601;202602).
.PARAMETER BaseUrl         Адреса API стенда (обов'язково, без дефолту).
.PARAMETER User            Обліковий запис (або $env:ECR_USER).
.PARAMETER Password        Пароль SecureString (або $env:ECR_PASSWORD).
.PARAMETER ConfirmStand    Підтвердження стенда (обов'язково, без дефолту): адреса API, host:port або host; для запису -
                           `<адреса>/<база>` (наприклад localhost:5094/EcrLandUi).
.PARAMETER SqlConnection   Явний рядок підключення стенда (для назви бази в підтвердженні; дефолту немає).
.PARAMETER AllowNameMismatch  Не зупиняти на NAME_MISMATCH (ім'я документа на стенді інше, ніж ключ у -DocMap).
.PARAMETER JobTimeoutMinutes  Скільки чекати одну задачу (за замовчуванням 30).
.PARAMETER Out             Файл звіту JSON (за бажання).
.PARAMETER Execute         Запустити перерахунки (без нього - лише план).
.PARAMETER DryRun          Явно лише план (перемагає -Execute).

.EXAMPLE
    $pw = Read-Host 'Пароль ECR' -AsSecureString
    .\Invoke-LandRecalc.ps1 -DocMap '{"<імʼя1>":<id1>}' -PeriodKey 202601,202602 -BaseUrl http://localhost:5094 -ConfirmStand localhost:5094 -User land-owner -Password $pw
    .\Invoke-LandRecalc.ps1 -DocMap '{"<імʼя1>":<id1>}' -PeriodKey 202601,202602 -BaseUrl http://localhost:5094 -User land-owner -Password $pw `
        -SqlConnection 'Server=localhost;Database=EcrLandUi;Integrated Security=true' -ConfirmStand localhost:5094/EcrLandUi -Execute
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DocMap,
    [Parameter(Mandatory)]$PeriodKey,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [string]$User,
    [securestring]$Password,
    [string]$SqlConnection,
    [int]$JobTimeoutMinutes = 30,
    [switch]$AllowNameMismatch,
    [string]$Out,
    [switch]$Execute,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandInputCells.Lib.ps1')
. (Join-Path $here 'LandStandSession.Lib.ps1')
Set-StrictMode -Off

$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)
# Охорона стенда - ДО входу й будь-якого виклику.
$null = Initialize-LandStandSession -BaseUrl $BaseUrl -ConfirmStand $ConfirmStand -SqlConnection $SqlConnection -ForWrite:$exec

$idMap = ConvertFrom-LandDocMap -Json $DocMap
$docNames = @($idMap.Keys)
$periods = @(ConvertTo-DocumentIdList $PeriodKey)
foreach ($p in $periods) {
    if ($p -lt 200001 -or $p -gt 209912 -or ($p % 100) -lt 1 -or ($p % 100) -gt 12) { throw "Некоректний період $p (очікується Рік*100+Місяць)." }
}
if ($periods.Count -eq 0) { throw 'Задайте -PeriodKey (202601 або 202601,202602).' }

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
# Передперегляд (читання): кожен документ/період має відкриватися на стенді - інакше NO_DOC і зупинка.
$noDoc = New-Object System.Collections.Generic.List[string]
$badName = New-Object System.Collections.Generic.List[string]
foreach ($n in $docNames) {
    $nameVerdict = Get-LandDocumentNameState -DocumentId ([int]$idMap[$n]) -ExpectedName $n
    if ($nameVerdict -eq 'NAME_MISMATCH' -and -not $AllowNameMismatch) { $badName.Add($n) }
    foreach ($p in $periods) {
        $probe = Invoke-LandEcrRetry -HttpMethod GET -ApiPath "/api/v1/documents/$([int]$idMap[$n])/tables?periodKey=$p"
        if ($probe.Status -ne 200) { $noDoc.Add("$n/$p (HTTP $($probe.Status))") }
    }
}
if ($noDoc.Count -gt 0 -or $badName.Count -gt 0) {
    if ($noDoc.Count -gt 0) { Write-Host "NO_DOC: $($noDoc -join ', '). Перерахунків не запущено." }
    if ($badName.Count -gt 0) { Write-Host "NAME_MISMATCH: $($badName -join ', ') (ім'я документа на стенді не збігається з ключем -DocMap). Перерахунків не запущено." }
    exit 1
}

Write-Host "ПЛАН: документів $($docNames.Count), періодів $($periods.Count), задач перерахунку $($docNames.Count * $periods.Count)."
if (-not $exec) {
    foreach ($n in $docNames) { Write-Host "  $n -> період(и) $($periods -join ',')" }
    Write-Host 'ПЛАН (лише читання). Запуск: -Execute -SqlConnection <локальний, EcrLand*> -ConfirmStand <адреса>/<база>.'
    return
}

$utcStart = (Get-Date).ToUniversalTime()
$results = New-Object System.Collections.ArrayList
foreach ($n in $docNames) {
    foreach ($p in $periods) {
        $docId = [int]$idMap[$n]
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $state = '-'
        $message = ''
        $post = Invoke-LandEcrRetry -HttpMethod POST -ApiPath "/api/v1/documents/$docId/recalculate" -RequestBody @{ periodKey = $p }
        if ($post.Status -in 200, 202) {
            $jobId = [string]$post.Json.jobId
            $deadline = (Get-Date).AddMinutes($JobTimeoutMinutes)
            $state = 'Timeout'
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 1500
                $job = Invoke-Ecr -Method GET -Path ('/api/v1/jobs/' + [uri]::EscapeDataString($jobId))
                if ($job.Status -eq 429) { Start-Sleep -Seconds (Get-LandRetryAfterSeconds -Response $job -DefaultSeconds 2); continue }
                if ($job.Status -ne 200) { $state = "HTTP$($job.Status)"; break }
                $current = [string]$job.Json.state
                if ($current -match 'Succeeded|Failed|Cancel|Dead|Unavailable|Error') { $state = $current; $message = [string]$job.Json.message; break }
            }
        }
        else { $state = "HTTP$($post.Status)"; $message = Get-EcrErrorCode $post }
        $ok = ($state -eq 'Succeeded' -or $state -eq 'SucceededWithErrors')
        [void]$results.Add([pscustomobject]@{ document = $n; period = $p; http = $post.Status; state = $state; seconds = [int]$watch.Elapsed.TotalSeconds; ok = $ok; message = $message })
        Write-Host ('doc={0} period={1} http={2} state={3} sec={4} {5}' -f $n, $p, $post.Status, $state, [int]$watch.Elapsed.TotalSeconds, $message)
    }
}
$failed = @($results | Where-Object { -not $_.ok }).Count
Write-Host "RECALC_START_UTC=$($utcStart.ToString('o')) RECALC_END_UTC=$((Get-Date).ToUniversalTime().ToString('o')) задач $($results.Count), невдалих $failed."
if ($Out) { $results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Out -Encoding UTF8 }
if ($failed -gt 0) { exit 1 }
