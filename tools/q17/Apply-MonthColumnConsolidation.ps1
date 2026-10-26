<#
.SYNOPSIS
    Q-17 (D-457): консолідація місячних стовпців — Preview / Apply / PurgeCopies / Reanchor.

.DESCRIPTION
    Обгортка над Preview-MonthColumnConsolidation.sql (один T-SQL для всіх режимів). Без режиму — PREVIEW:
    лише читання, звіт (лічильники + CSV конфліктів / noPeriodRow / lockedTargets).

    Режими запису (Apply, PurgeCopies, Reanchor) ЗАПУСКАЮТЬСЯ ЛИШЕ після всіх запобіжників:
      * -Database вказано явно (за замовчуванням немає);
      * база EcrLandUi — відмова без -AllowLive (це дія людини окремим словом; агенти його не вмикають);
      * -JobsPaused — оператор підтверджує, що фонові джоби (ConsistencyCheck, Recalc, SourceEventSync,
        збір, імпорт) зупинено, а користувачі не редагують;
      * -ConfirmBackup <шлях .bak>: файл існує, RESTORE VERIFYONLY = valid, у заголовку бекапу ЦЯ база
        (DatabaseName = -Database) і він не старший за -BackupMaxAgeHours (умовчання 48 год);
      * -PurgeCopies додатково вимагає -ConfirmPurge.

    Apply: ДО -> запис -> ПІСЛЯ. «До» і «Після» — це Preview того самого обсягу; «Після» має показати
    copyPlanned = 0 і rowsToCreate = 0 (ідемпотентність). Транзакція — на документ; помилка відкочує поточний
    документ і зупиняє прогін (вже завершені документи лишаються — прогін можна повторити).

    -Reanchor: те саме, але лише НЕ-місячні введені стовпці (запускати після того, як календар проєкту
    доповнено періодом раніше за попередній якір; спершу Preview).

    -PurgeCopies: окрема необов'язкова фаза — видаляє копії (p <> власник) введених комірок, значення яких
    дорівнює непорожньому значенню власника; копії в заблокованих періодах і з ІНШИМ значенням лишаються.

    Перерахунок після Apply (РОБИТЬ ОПЕРАТОР, скрипт його не запускає): POST /api/v1/documents/{id}/recalculate
    для кожного зміненого документа (або /api/v1/projects/{id}/recalculate) — див. runbook
    `docs/admin/admin-guide.md`, «Q-17: консолідація місячних стовпців».

.PARAMETER Server
    Екземпляр SQL Server (інтегрована автентифікація).
.PARAMETER Database
    Ім'я бази. Обов'язково, умовчання немає.
.PARAMETER DocumentId
    Один документ (умовчання — усі документи Monthly-проєктів).
.PARAMETER Year
    Один рік (умовчання — усі).
.PARAMETER Apply
    Запис власників (комірки, екземпляри, дзеркальні рядки, аудит Origin='Migration').
.PARAMETER PurgeCopies
    Окрема фаза видалення копій (вимагає -ConfirmPurge).
.PARAMETER Reanchor
    Повторний прогін Apply для НЕ-місячних стовпців з новим якорем.
.PARAMETER ConfirmBackup
    Шлях до .bak (бачить служба SQL Server), зробленого перед запуском.
.PARAMETER JobsPaused
    Підтвердження, що фонові джоби зупинено.
.PARAMETER AllowLive
    Дозвіл на EcrLandUi. Лише за окремим словом людини.
.PARAMETER IncludeLockedTargets
    Писати й у заблокованих власників (адмінська дія; лічильник writtenToLocked). Без — лише звіт.
.PARAMETER UserId
    sec.User.Id для aud.CellChange.ChangedByUserId (умовчання — найменший Id).
.PARAMETER OutDir
    Каталог CSV і підсумку (умовчання — .\q17-out).
.PARAMETER SummaryJson
    Шлях JSON з підсумком (before / run / after) — для тестів і автоматизації.
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [Parameter(Mandatory = $true)][string]$Database,
    [long]$DocumentId = 0,
    [int]$Year = 0,
    [switch]$Apply,
    [switch]$PurgeCopies,
    [switch]$Reanchor,
    [string]$ConfirmBackup,
    [switch]$JobsPaused,
    [switch]$AllowLive,
    [switch]$ConfirmPurge,
    [switch]$IncludeLockedTargets,
    [int]$BackupMaxAgeHours = 48,
    [int]$UserId = 0,
    [string]$OutDir = '.\q17-out',
    [string]$SummaryJson
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

function Fail([string]$message) { throw "Q17: $message" }

$sqlPath = Join-Path $PSScriptRoot 'Preview-MonthColumnConsolidation.sql'
if (-not (Test-Path -LiteralPath $sqlPath)) { Fail "не знайдено $sqlPath" }

$writeMode = $Apply.IsPresent -or $PurgeCopies.IsPresent -or $Reanchor.IsPresent
if ($PurgeCopies -and ($Apply -or $Reanchor)) { Fail 'PurgeCopies — окрема фаза; не поєднуйте з -Apply/-Reanchor.' }

$connectionString = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Application Name=Q17-Consolidation"

function Invoke-Q17Sql([string]$text, [int]$timeoutSeconds = 0) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $text
        $cmd.CommandTimeout = $timeoutSeconds
        $ds = New-Object System.Data.DataSet
        $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
        [void]$adapter.Fill($ds)
        return $ds
    }
    finally { $cn.Dispose() }
}

function Invoke-Q17Scalar([string]$text) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $text
        $cmd.CommandTimeout = 120
        return $cmd.ExecuteScalar()
    }
    finally { $cn.Dispose() }
}

# ---------------------------------------------------------------------------------------------------
# запобіжники
# ---------------------------------------------------------------------------------------------------
if ($writeMode) {
    if ($Database -ieq 'EcrLandUi' -and -not $AllowLive) {
        Fail 'база EcrLandUi — живий стенд; запис без -AllowLive заборонено (рішення людини окремим словом, з бекапом).'
    }
    if (-not $JobsPaused) {
        Fail 'потрібен -JobsPaused: зупиніть фонові джоби (ConsistencyCheck, Recalc, SourceEventSync, збір, імпорт) і підтвердіть.'
    }
    if ([string]::IsNullOrWhiteSpace($ConfirmBackup)) {
        Fail 'потрібен -ConfirmBackup <шлях .bak>: зробіть бекап бази ДО запису й вкажіть файл.'
    }
    if ($PurgeCopies -and -not $ConfirmPurge) { Fail '-PurgeCopies вимагає -ConfirmPurge (окрема фаза, видаляє дані копій).' }
    if (-not (Test-Path -LiteralPath $ConfirmBackup)) { Fail "файл бекапу не знайдено: $ConfirmBackup" }

    $bak = $ConfirmBackup.Replace("'", "''")
    try { [void](Invoke-Q17Sql "RESTORE VERIFYONLY FROM DISK = N'$bak'" 600) }
    catch { Fail "RESTORE VERIFYONLY не пройшов для $ConfirmBackup : $($_.Exception.Message)" }

    $hdr = (Invoke-Q17Sql "RESTORE HEADERONLY FROM DISK = N'$bak'" 120).Tables[0].Rows[0]
    if ([string]$hdr['DatabaseName'] -ine $Database) {
        Fail "бекап належить базі '$($hdr['DatabaseName'])', а не '$Database'."
    }
    $age = (Get-Date) - [datetime]$hdr['BackupFinishDate']
    if ($age.TotalHours -gt $BackupMaxAgeHours) {
        Fail ("бекап застарілий: {0:N1} год (ліміт {1}). Зробіть свіжий." -f $age.TotalHours, $BackupMaxAgeHours)
    }
    Write-Host ("Бекап перевірено: {0}, закінчено {1:yyyy-MM-dd HH:mm} (локальний час), VERIFYONLY valid." -f $ConfirmBackup, [datetime]$hdr['BackupFinishDate'])
}

$hasTable = Invoke-Q17Scalar "SELECT CASE WHEN OBJECT_ID(N'doc.TableInstance', N'U') IS NULL THEN 0 ELSE 1 END"
if ($hasTable -ne 1) { Fail "у базі '$Database' немає doc.TableInstance — це не база ECR." }

if ($UserId -le 0) {
    $uid = Invoke-Q17Scalar 'SELECT MIN(Id) FROM sec.[User]'
    if ($uid -is [System.DBNull] -or $null -eq $uid) { Fail 'у sec.User немає користувачів: вкажіть -UserId.' }
    $UserId = [int]$uid
}

# ---------------------------------------------------------------------------------------------------
# складання параметрів і запуск
# ---------------------------------------------------------------------------------------------------
$sqlText = [IO.File]::ReadAllText($sqlPath, [Text.Encoding]::UTF8)
$begin = $sqlText.IndexOf('-- BEGIN PARAMS')
$end = $sqlText.IndexOf('-- END PARAMS')
if ($begin -lt 0 -or $end -lt $begin) { Fail 'у SQL немає блоку параметрів BEGIN/END PARAMS.' }
$end += '-- END PARAMS'.Length

function New-Header([string]$mode, [string]$scope, [string]$runId) {
    $doc = if ($DocumentId -gt 0) { [string]$DocumentId } else { 'NULL' }
    $yr = if ($Year -gt 0) { [string]$Year } else { 'NULL' }
    $wl = if ($IncludeLockedTargets) { 1 } else { 0 }
    $live = if ($AllowLive) { 1 } else { 0 }
    $tok = if ($mode -eq 'Preview') { 'NULL' } else { "'Q17-APPLY-v1'" }
    $uidText = if ($mode -eq 'Preview') { 'NULL' } else { [string]$UserId }
    $rid = if ($mode -eq 'Preview') { 'NULL' } else { "N'$runId'" }
    return @"
DECLARE @Mode        varchar(10)  = '$mode';
DECLARE @DocumentId  bigint       = $doc;
DECLARE @Year        int          = $yr;
DECLARE @ColumnScope varchar(10)  = '$scope';
DECLARE @RunId       nvarchar(64) = $rid;
DECLARE @UserId      int          = $uidText;
DECLARE @WriteLocked bit          = $wl;
DECLARE @AllowLive   bit          = $live;
DECLARE @ApplyToken  varchar(40)  = $tok;
"@
}

function Invoke-Q17([string]$mode, [string]$scope, [string]$runId) {
    $text = $sqlText.Substring(0, $begin) + (New-Header $mode $scope $runId) + $sqlText.Substring($end)
    return Invoke-Q17Sql $text 0
}

function ConvertTo-Summary($ds) {
    $row = $ds.Tables[0].Rows[0]
    $o = [ordered]@{}
    foreach ($c in $ds.Tables[0].Columns) { $v = $row[$c.ColumnName]; if ($v -is [System.DBNull]) { $v = $null }; $o[$c.ColumnName] = $v }
    return [pscustomobject]$o
}

$scope = if ($Reanchor) { 'NonMonth' } else { 'All' }
$runId = 'q17-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Save-Details($ds, [string]$tag) {
    $names = @('perDocument', 'conflicts', 'noPeriodRow', 'lockedTargets')
    for ($i = 1; $i -le 4; $i++) {
        $path = Join-Path $OutDir ("{0}-{1}-{2}.csv" -f $runId, $tag, $names[$i - 1])
        $rows = @($ds.Tables[$i].Rows | ForEach-Object {
            $r = $_; $o = [ordered]@{}
            foreach ($c in $ds.Tables[$i].Columns) { $o[$c.ColumnName] = $r[$c.ColumnName] }
            [pscustomobject]$o
        })
        if ($rows.Count -gt 0) { $rows | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8 }
    }
}

Write-Host "Q17 run $runId  база=$Database  режим=$(if ($PurgeCopies) {'PurgeCopies'} elseif ($Reanchor) {'Reanchor'} elseif ($Apply) {'Apply'} else {'Preview'})"

$beforeDs = Invoke-Q17 'Preview' $scope $runId
$before = ConvertTo-Summary $beforeDs
Save-Details $beforeDs 'before'
Write-Host '--- ДО (Preview) ---'
$before | Format-List | Out-String | Write-Host

$run = $null; $after = $null
if ($writeMode) {
    $mode = if ($PurgeCopies) { 'Purge' } else { 'Apply' }
    $runDs = Invoke-Q17 $mode $scope $runId
    $run = ConvertTo-Summary $runDs
    Save-Details $runDs 'run'
    Write-Host "--- ЗАПИС ($mode) ---"
    $run | Format-List | Out-String | Write-Host

    $afterDs = Invoke-Q17 'Preview' $scope $runId
    $after = ConvertTo-Summary $afterDs
    Save-Details $afterDs 'after'
    Write-Host '--- ПІСЛЯ (Preview) ---'
    $after | Format-List | Out-String | Write-Host

    if ($mode -eq 'Apply') {
        $left = [int]$after.copyPlanned + [int]$after.mirrorRowsToCreate + [int]$after.anchorRowsToCreate + [int]$after.ownerInstancesToCreate
        if ($left -eq 0) { Write-Host 'Ідемпотентність: повторний прогін планує 0 змін.' }
        else { Write-Warning "Лишилось запланованих змін: $left (заблоковані цілі / видалені рядки / noPeriodRow — див. CSV)." }
        Write-Host 'ДАЛІ: перерахунок змінених документів (POST /api/v1/documents/{id}/recalculate), потім ввімкнення Collection:MonthColumnRouting.'
    }
}

if ($SummaryJson) {
    [pscustomobject]@{ runId = $runId; database = $Database; mode = $(if ($writeMode) { if ($PurgeCopies) { 'Purge' } else { 'Apply' } } else { 'Preview' }); before = $before; run = $run; after = $after } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $SummaryJson -Encoding UTF8
}
Write-Host "CSV і підсумок: $((Resolve-Path -LiteralPath $OutDir).Path)"
