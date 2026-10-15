<#
.SYNOPSIS
  API-частина пост-перевірки: лише GET (+ вхід), скан журналу API (A-14), COPY_ONLY бекап (A-15, лише з -DoBackup).
.DESCRIPTION
  Виводить лише статуси, час, лічильники. Пароль не друкується й не передається нікуди, крім входу в API;
  облікові дані читаються лише з -AccountsFile (рядки `<користувач><TAB><пароль>`). Нічого не пише в стенд
  (бекап - це читання БД у файл поза стендом). Адресу API перевіряє Connect-Ecr (лише локальна машина).
  Машинний підсумок: рядки "PCAPI|<ключ>|<значення>" у stdout і JSON-файл postcheck-api-<ts>.json.
  Код виходу: 0 - немає FAIL; 1 - є FAIL.
  Шляхи й акаунти не зашиті: -AccountsFile, -LogDir, -BackupDir, -OutDir (або змінні середовища
  ECR_POSTCHECK_ACCOUNTS / _USER / _BASEURL / _LOGDIR / _BACKUPDIR / _OUTDIR / _SERVER).
.PARAMETER ExpectedTemplateVersion
  Очікувана остання Published версія шаблону. Без параметра літерал версії не потрібен: перевіряється,
  що Published-версія існує (і, якщо DTO проєкту повертає templateVersionId, що проєкт на ній).
.PARAMETER BackupName
  Ім'я файлу бекапу (лише ім'я, .bak). За замовчуванням ecr-land-ui-post-<WindowTag>-<yyyy-MM-dd>.bak;
  файл, що вже існує, ніколи не перезаписується - додається суфікс часу.
.EXAMPLE
  powershell -File ApiSmoke.ps1 -Database EcrLandAccP -BaseUrl http://localhost:5101 -AccountsFile <файл> -User <користувач>
#>
[CmdletBinding()]
param(
    [string]$BaseUrl      = $env:ECR_POSTCHECK_BASEURL,
    [string]$ToolsDir,                                                 # де лежить EcrApi.ps1 (за замовчуванням tools\land поруч)
    [string]$AccountsFile = $env:ECR_POSTCHECK_ACCOUNTS,
    [string]$User         = $env:ECR_POSTCHECK_USER,
    [string]$TemplateCode = 'LAND_UI',
    [string]$ProjectCode  = 'LANDUI2026',
    [string]$ExpectedTemplateVersion,
    [long]  $DocumentId   = 0,                                         # 0 = перший документ зі списку періоду
    [int]   $PeriodKey    = 202606,
    [string]$TableCode    = 'T10_01',
    [string]$TableColumnCode,                                          # необов'язково: колонка, наявність якої перевірити у зрізі
    [string[]]$MethodologyCodes = @(),                                 # S-09: остання Published версія кожної (список через кому також дозволено)
    [string]$RegistryCode,                                             # S-10b: рядки реєстру (необов'язково)
    [string]$LogPath,                                                  # за замовчуванням - найновіший api*.log у -LogDir
    [string]$LogDir       = $env:ECR_POSTCHECK_LOGDIR,
    [int]   $StartLine    = 0,                                         # рядок журналу, з якого рахувати; 0 = від останнього маркера старту
    [string]$StartMarker  = 'Application started\. Press Ctrl\+C',
    [bool]  $MeasureLp45  = $true,                                     # LP-45: холодний/теплий замір /documents
    [switch]$SkipLp45,
    [string[]]$Lp45Periods = @('202601', '202604', '202606'),     # PeriodKey через кому також дозволено
    [int]   $Lp45Warm     = 3,
    [int]   $Lp45SawSeconds = 60,
    [switch]$DoBackup,                                                 # A-15: COPY_ONLY бекап (за замовчуванням ВИМКНЕНО)
    [string]$Server       = $(if ($env:ECR_POSTCHECK_SERVER) { $env:ECR_POSTCHECK_SERVER } else { 'localhost' }),
    [Parameter(Mandatory = $true)][ValidatePattern('^EcrLand[A-Za-z0-9_]+$')][string]$Database,
    [string]$BackupDir    = $env:ECR_POSTCHECK_BACKUPDIR,
    [string]$BackupName,
    [ValidatePattern('^[A-Za-z0-9_]+$')][string]$WindowTag = 'check',
    [string]$OutDir       = $env:ECR_POSTCHECK_OUTDIR
)
$ErrorActionPreference = 'Stop'
$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$MethodologyCodes = @($MethodologyCodes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Lp45Periods = @($Lp45Periods | ForEach-Object { $_ -split ',' } | Where-Object { $_ -match '^\d{6}$' })
if (-not $ToolsDir) { $ToolsDir = Split-Path -Parent $PSScriptRoot }
if (-not $OutDir) { $OutDir = Join-Path ([IO.Path]::GetTempPath()) 'ecr-postcheck' }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
if ([string]::IsNullOrWhiteSpace($BaseUrl)) { throw 'Не задано -BaseUrl (або ECR_POSTCHECK_BASEURL).' }
if ([string]::IsNullOrWhiteSpace($AccountsFile)) { throw 'Не задано -AccountsFile (або ECR_POSTCHECK_ACCOUNTS).' }
if ([string]::IsNullOrWhiteSpace($User)) { throw 'Не задано -User (або ECR_POSTCHECK_USER).' }
if ($DoBackup -and [string]::IsNullOrWhiteSpace($BackupDir)) { throw '-DoBackup потребує -BackupDir (або ECR_POSTCHECK_BACKUPDIR).' }
if ($BackupName -and ($BackupName -notmatch '^[A-Za-z0-9._-]+\.bak$')) { throw '-BackupName: лише ім''я файлу з розширенням .bak (без шляху).' }

$rows = New-Object System.Collections.ArrayList
$kv   = [ordered]@{}
function Add-Row($id, $what, $status, $sec, $max, $verdict, $note = '') {
    [void]$rows.Add([pscustomobject]@{ id = $id; what = $what; status = $status; sec = $sec; max = $max; verdict = $verdict; note = $note })
}
function Prop($o, $name) {
    if ($null -eq $o) { return $null }
    $p = $o.PSObject.Properties[$name]
    if ($null -eq $p) { return $null }
    return $p.Value
}
function Items($r) {
    if ($null -ne $r.Json -and $r.Json.PSObject.Properties.Match('items').Count) { return @($r.Json.items) }
    return @($r.Json)
}

# ---------------- вхід ----------------
. (Join-Path $ToolsDir 'EcrApi.ps1')
# EcrApi.ps1 вмикає StrictMode; JSON-елементи мають різні набори властивостей (tableCode/code) - читаємо через Prop.
Set-StrictMode -Off
$acc = @{}
Get-Content -LiteralPath $AccountsFile -Encoding UTF8 | ForEach-Object { $p = $_ -split "`t"; if ($p.Count -ge 2) { $acc[$p[0]] = $p[1] } }
if (-not $acc.ContainsKey($User)) { throw "обліковий запис відсутній у -AccountsFile" }
Connect-Ecr -BaseUrl $BaseUrl -User $User -Password (ConvertTo-SecureString $acc[$User] -AsPlainText -Force) | Out-Null
$acc = $null

function Chk($id, $path, $maxSec, $okStatus = 200) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = $null; $st = -1
    try { $r = Invoke-Ecr -Method GET -Path $path; $st = [int]$r.Status } catch { $st = -1 }
    $sw.Stop()
    $sec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $ok = ($st -eq $okStatus) -and ($sec -le $maxSec)
    $note = ''
    if ($st -ge 500) { $note = '5xx' } elseif ($st -eq $okStatus -and $sec -gt $maxSec) { $note = 'повільно' }
    Add-Row $id $path $st $sec $maxSec $(if ($ok) { 'PASS' } else { 'FAIL' }) $note
    return $r
}

# ---------------- LP-45: холодний/теплий замір /documents (лише GET) ----------------
# Холодний = перший виклик періоду; теплі = наступні $Lp45Warm виклики з паузою 1 с;
# пилка = $Lp45SawSeconds ітерацій по 1 с: спайк >5 с кожні ~15-16 викликів = TTL 15 с карти.
function Lp45-Call($pk) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $st = -1; $cnt = -1
    try {
        $r = Invoke-Ecr -Method GET -Path "/api/v1/documents?limit=50&periodKey=$pk"
        $st = [int]$r.Status
        $cnt = @(Items $r).Count
    } catch {
        $m = [regex]::Match("$($_.Exception.Message)", '\b(4\d\d|5\d\d)\b')
        if ($m.Success) { $st = [int]$m.Value }
    }
    $sw.Stop()
    [pscustomobject]@{ status = $st; ms = [int][math]::Round($sw.Elapsed.TotalMilliseconds); items = $cnt }
}
if ($MeasureLp45 -and -not $SkipLp45) {
    foreach ($pk in $Lp45Periods) {
        $cold = Lp45-Call $pk
        $warm = @()
        for ($i = 1; $i -le $Lp45Warm; $i++) { Start-Sleep -Seconds 1; $warm += Lp45-Call $pk }
        $saw = @()
        for ($i = 1; $i -le $Lp45SawSeconds; $i++) { Start-Sleep -Seconds 1; $saw += Lp45-Call $pk }
        $warmMax = if ($warm.Count) { ($warm | Measure-Object ms -Maximum).Maximum } else { 0 }
        $non200 = @($warm + $saw + $cold | Where-Object { $_.status -ne 200 }).Count
        $has5xx = [bool](@($warm + $saw + $cold | Where-Object { $_.status -ge 500 -or $_.status -eq -1 }).Count)
        $spikes = @($saw | Where-Object { $_.ms -gt 5000 }).Count
        $sawMax = if ($saw.Count) { ($saw | Measure-Object ms -Maximum).Maximum } else { 0 }
        $kv["lp45_${pk}_cold_status"] = $cold.status
        $kv["lp45_${pk}_cold_ms"]     = $cold.ms
        $kv["lp45_${pk}_cold_items"]  = $cold.items
        $kv["lp45_${pk}_warm_ms"]     = ($warm | ForEach-Object { $_.ms }) -join '/'
        $kv["lp45_${pk}_warm_max_ms"] = $warmMax
        $kv["lp45_${pk}_non200"]      = $non200
        $kv["lp45_${pk}_saw_spikes_gt5s"] = $spikes
        $kv["lp45_${pk}_saw_max_ms"]  = $sawMax
        $verdict = if ($has5xx -or $warmMax -gt 5000) { 'FAIL' } elseif ($cold.ms -gt 10000) { 'INFO' } elseif ($spikes -gt 0) { 'INFO' } else { 'PASS' }
        Add-Row "LP45/$pk" "/documents?limit=50&periodKey=$pk холодний/теплий" "$($cold.status)" ([math]::Round($cold.ms / 1000, 1)) 10 $verdict ("cold=$($cold.ms)мс items=$($cold.items); warm=$(($warm | ForEach-Object { $_.ms }) -join '/')мс (<=5000); non200=$non200; пилка ${Lp45SawSeconds}x1с: спайків>5с=$spikes max=${sawMax}мс")
    }
    $kv['lp45_periods'] = ($Lp45Periods -join ',')
} else {
    Add-Row 'LP45' 'LP-45 замір не робився' '-' 0 0 'INFO' ''
}

# ---------------- S-01..S-10 ----------------
$null = Chk 'S-01' '/health/ready' 2
$null = Chk 'S-02' '/api/v1/me' 1
$t   = Chk 'S-03a' "/api/v1/templates?limit=50&q=$TemplateCode" 2
$tpl = Items $t | Where-Object { (Prop $_ 'code') -eq $TemplateCode } | Select-Object -First 1
$latestPub = $null
if ($tpl) {
    $v = Chk 'S-03b' "/api/v1/templates/$($tpl.id)/versions?limit=100" 2
    $vers = Items $v
    $latestPub = $vers | Where-Object { "$(Prop $_ 'status')" -match '^(1|Published)$' } | Sort-Object { [int](Prop $_ 'id') } | Select-Object -Last 1
    $drafts = @($vers | Where-Object { "$(Prop $_ 'status')" -match '^(0|Draft)$' }).Count
    $kv['template_latest_published'] = "$(Prop $latestPub 'version')"
    $kv['template_drafts'] = $drafts
    if ($ExpectedTemplateVersion) {
        $okv = ("$(Prop $latestPub 'version')" -eq $ExpectedTemplateVersion)
        Add-Row 'S-03c' "остання Published шаблону = очікувана (-ExpectedTemplateVersion)" '-' 0 0 $(if ($okv) { 'PASS' } else { 'FAIL' }) "latest=$(Prop $latestPub 'version') expected=$ExpectedTemplateVersion drafts=$drafts"
    } else {
        Add-Row 'S-03c' 'шаблон має Published версію (очікувана версія не задана; відповідність проєкту - у SQL-перевірці A-16)' '-' 0 0 $(if ($latestPub) { 'PASS' } else { 'FAIL' }) "latest=$(Prop $latestPub 'version') drafts=$drafts"
    }
} else { Add-Row 'S-03b' 'шаблон не знайдено' '-' 0 0 'FAIL' "code=$TemplateCode" }
$pr   = Chk 'S-04a' '/api/v1/projects?limit=10' 2
$proj = Items $pr | Where-Object { (Prop $_ 'code') -eq $ProjectCode } | Select-Object -First 1
if ($proj) {
    $null = Chk 'S-04b' "/api/v1/projects/$($proj.id)/periods" 2
    $ptv = Prop $proj 'templateVersionId'
    if ($null -ne $ptv -and $latestPub) {
        Add-Row 'S-04c' 'проєкт на найновішій Published версії шаблону' '-' 0 0 $(if ("$ptv" -eq "$(Prop $latestPub 'id')") { 'PASS' } else { 'FAIL' }) "project_tv=$ptv latest_id=$(Prop $latestPub 'id')"
    }
} else { Add-Row 'S-04b' 'проєкт не знайдено' '-' 0 0 'FAIL' "code=$ProjectCode" }
$firstPk = if ($Lp45Periods.Count) { $Lp45Periods[0] } else { $PeriodKey }
$null = Chk 'S-05' "/api/v1/documents/summary?periodKey=$firstPk" 10
foreach ($pk in $Lp45Periods) {
    $dl = Chk "S-06/$pk" "/api/v1/documents?limit=50&periodKey=$pk" 5
    $kv["documents_list_${pk}_items"] = @(Items $dl).Count
    if ($DocumentId -le 0 -and $pk -eq $PeriodKey) {
        $d0 = Items $dl | Select-Object -First 1
        if ($d0) { $DocumentId = [long](Prop $d0 'id') }
    }
}
if ($DocumentId -le 0) {
    Add-Row 'S-07' 'документ для перевірки не визначено (-DocumentId)' '-' 0 0 'FAIL' "period=$PeriodKey"
    $cr = $null
} else {
    $kv['smoke_document_id'] = $DocumentId
    $null = Chk 'S-07' "/api/v1/documents/$DocumentId`?periodKey=$PeriodKey" 3
    $tb = Chk 'S-08a' "/api/v1/documents/$DocumentId/tables?periodKey=$PeriodKey" 3
    $ti = Items $tb | Where-Object { (Prop $_ 'tableCode') -eq $TableCode -or (Prop $_ 'code') -eq $TableCode } | Select-Object -First 1
    if ($ti) {
        $tiId = if ($ti.PSObject.Properties.Match('tableInstanceId').Count) { $ti.tableInstanceId } else { $ti.id }
        $sl = Chk 'S-08b' "/api/v1/documents/$DocumentId/tables/$tiId`?periodKey=$PeriodKey" 3
        $kv['s08b_rows'] = @($sl.Json.rows).Count
        if ($TableColumnCode) {
            $kv['s08b_has_column'] = [bool](@($sl.Json.columns) | Where-Object { (Prop $_ 'code') -eq $TableColumnCode })
        }
    } else { Add-Row 'S-08b' "таблицю $TableCode у /tables не знайдено" '-' 0 0 'FAIL' }
    $cr = Chk 'S-10c' "/api/v1/documents/$DocumentId/calculation-results?periodKey=$PeriodKey" 5
}
$ml = Chk 'S-09a' '/api/v1/methodologies' 3
$mm = Items $ml
$kv['methodologies_c11_c12'] = @($mm | Where-Object { "$(Prop $_ 'code')" -like 'ECW_C1[12]*' }).Count
foreach ($code in $MethodologyCodes) {
    $m = $mm | Where-Object { (Prop $_ 'code') -eq $code } | Select-Object -First 1
    if ($m) {
        $mv = Chk "S-09/$code" "/api/v1/methodologies/$($m.id)/versions" 3
        $lp = Items $mv | Where-Object { "$(Prop $_ 'status')" -match '^(1|Published)$' } | Sort-Object { [int](Prop $_ 'id') } | Select-Object -Last 1
        $kv["latest_published_$code"] = "$(Prop $lp 'version')"
    } else { Add-Row "S-09/$code" 'методологію не знайдено' '-' 0 0 'FAIL' '' }
}
$null = Chk 'S-10a' '/api/v1/registries' 3
if ($RegistryCode) { $null = Chk 'S-10b' "/api/v1/registries/$RegistryCode/rows?limit=1" 3 }

# ---------------- TableInstanceId у DTO результатів, лише читання ----------------
$resItems = @()
if ($cr -and $cr.Json) {
    if ($cr.Json.PSObject.Properties.Match('items').Count) { $resItems = @($cr.Json.items) }
    elseif ($cr.Json.PSObject.Properties.Match('results').Count) { $resItems = @($cr.Json.results) }
    elseif ($cr.Json -is [array]) { $resItems = @($cr.Json) }
}
$withTbl = @($resItems | Where-Object { $_.PSObject.Properties.Match('tableInstanceId').Count -and $null -ne $_.tableInstanceId })
$hasProp = ($resItems.Count -gt 0) -and [bool]($resItems[0].PSObject.Properties.Match('tableInstanceId').Count)
$kv['results_in_dto'] = $resItems.Count
$kv['results_with_tableInstanceId'] = $withTbl.Count
$kv['dto_has_tableInstanceId'] = $hasProp
if (-not $hasProp) {
    Add-Row 'TII' 'calculation-results: поле tableInstanceId у DTO' '-' 0 0 'INFO' 'поле відсутнє в DTO (збірка без нього) або список порожній'
} else {
    Add-Row 'TII' 'calculation-results: рядків із tableInstanceId' '-' 0 0 $(if ($withTbl.Count -gt 0) { 'PASS' } else { 'FAIL' }) "withTable=$($withTbl.Count) of $($resItems.Count)"
    $tid = ($withTbl | Select-Object -First 1).tableInstanceId
    if ($tid) {
        $sl2 = Chk 'TII-SLICE' "/api/v1/documents/$DocumentId/tables/$tid`?periodKey=$PeriodKey" 3
        $kv['slice_rows'] = @($sl2.Json.rows).Count
    }
}

# ---------------- A-14: журнал API ----------------
if (-not $LogPath -and $LogDir) {
    $f = Get-ChildItem -LiteralPath $LogDir -Filter 'api*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($f) { $LogPath = $f.FullName }
}
if ($LogPath -and (Test-Path -LiteralPath $LogPath)) {
    $fs = New-Object IO.FileStream($LogPath, 'Open', 'Read', 'ReadWrite')   # журнал відкритий API - читаємо зі спільним доступом
    $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
    $lines = New-Object System.Collections.Generic.List[string]
    while (-not $sr.EndOfStream) { [void]$lines.Add($sr.ReadLine()) }
    $sr.Dispose(); $fs.Dispose()
    $from = 0
    if ($StartLine -gt 0) { $from = [math]::Min($StartLine - 1, $lines.Count) }
    else {
        for ($i = $lines.Count - 1; $i -ge 0; $i--) { if ($lines[$i] -match $StartMarker) { $from = $i; break } }
    }
    $seg = $lines.GetRange($from, $lines.Count - $from)
    $cErr  = @($seg | Where-Object { $_ -match '^fail:|^\s*ERROR\b|\[ERR\]|\bERROR\b' }).Count
    $cSys  = @($seg | Where-Object { $_ -match 'ECR-SYS-0500' }).Count
    $cUnh  = @($seg | Where-Object { $_ -match 'Unhandled|unhandled exception' }).Count
    $c5xx  = @($seg | Where-Object { $_ -match 'responded 5\d\d|Status(Code)?[:= ]+5\d\d|HTTP/[\d.]+"? 5\d\d' }).Count
    $kv['log_file'] = (Split-Path $LogPath -Leaf); $kv['log_from_line'] = $from + 1; $kv['log_lines_scanned'] = $seg.Count
    $kv['log_error'] = $cErr; $kv['log_ecr_sys_0500'] = $cSys; $kv['log_unhandled'] = $cUnh; $kv['log_5xx'] = $c5xx
    $bad = $cSys + $cUnh + $c5xx
    Add-Row 'A-14' "журнал $(Split-Path $LogPath -Leaf) з рядка $($from + 1): ERROR=$cErr SYS0500=$cSys Unhandled=$cUnh 5xx=$c5xx" '-' 0 0 $(if ($bad -eq 0 -and $cErr -eq 0) { 'PASS' } elseif ($bad -eq 0) { 'INFO' } else { 'FAIL' }) 'PASS: усі 0; INFO: лише fail:-рядки; FAIL: SYS0500/Unhandled/5xx > 0'
} else {
    Add-Row 'A-14' 'журнал API не знайдено (-LogPath / -LogDir)' '-' 0 0 'INFO' ''
}

# ---------------- A-15: COPY_ONLY бекап (лише -DoBackup) ----------------
if ($DoBackup) {
    if (-not (Test-Path -LiteralPath $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
    $baseName = if ($BackupName) { $BackupName } else { "ecr-land-ui-post-$WindowTag-$(Get-Date -Format 'yyyy-MM-dd').bak" }
    $bak = Join-Path $BackupDir $baseName
    if (Test-Path -LiteralPath $bak) {
        # існуючий файл не перезаписуємо: суфікс часу (і лічильник, якщо й такий уже є)
        $stem = [IO.Path]::GetFileNameWithoutExtension($baseName)
        $bak = Join-Path $BackupDir ("{0}-{1}.bak" -f $stem, (Get-Date -Format 'HHmmss'))
        $k = 1
        while (Test-Path -LiteralPath $bak) { $bak = Join-Path $BackupDir ("{0}-{1}-{2}.bak" -f $stem, (Get-Date -Format 'HHmmss'), $k); $k++ }
    }
    $bakSql = $bak.Replace("'", "''")
    $q = "BACKUP DATABASE [$Database] TO DISK = N'$bakSql' WITH COPY_ONLY, NOINIT, CHECKSUM, COMPRESSION, STATS = 25;"
    & sqlcmd -S $Server -E -b -l 30 -t 1800 -Q $q | Out-Null
    $okB = ($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $bak)
    $okV = $false; $sha = ''
    if ($okB) {
        & sqlcmd -S $Server -E -b -l 30 -t 1800 -Q "RESTORE VERIFYONLY FROM DISK = N'$bakSql' WITH CHECKSUM;" | Out-Null
        $okV = ($LASTEXITCODE -eq 0)
        $sha = (Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash
        Set-Content -LiteralPath ($bak + '.sha256') -Value "$sha  $(Split-Path $bak -Leaf)" -Encoding ascii
    }
    $kv['backup_file'] = $bak; $kv['backup_verifyonly'] = $okV; $kv['backup_sha256'] = $sha
    Add-Row 'A-15' "COPY_ONLY бекап $(Split-Path $bak -Leaf), VERIFYONLY=$okV" '-' 0 0 $(if ($okB -and $okV) { 'PASS' } else { 'FAIL' }) "sha256=$sha"
} else {
    Add-Row 'A-15' 'бекап не робився (немає -DoBackup)' '-' 0 0 'INFO' ''
}

# ---------------- підсумок ----------------
$rows | Format-Table id, what, status, sec, max, verdict, note -AutoSize -Wrap | Out-String -Width 250 | Write-Host
$nPass = @($rows | Where-Object verdict -eq 'PASS').Count
$nFail = @($rows | Where-Object verdict -eq 'FAIL').Count
$nInfo = @($rows | Where-Object verdict -eq 'INFO').Count
Write-Host "Підсумок API: PASS=$nPass FAIL=$nFail INFO=$nInfo"
Write-Host "PCAPI|pass|$nPass"
Write-Host "PCAPI|fail|$nFail"
Write-Host "PCAPI|info|$nInfo"
foreach ($k in $kv.Keys) { Write-Host "PCAPI|$k|$($kv[$k])" }
foreach ($r in $rows) { if ($r.id -in 'A-14', 'A-15') { Write-Host "PCAPI|verdict_$($r.id)|$($r.verdict)" } }
$json = Join-Path $OutDir "postcheck-api-$ts.json"
[pscustomobject]@{ at = (Get-Date).ToString('o'); baseUrl = $BaseUrl; rows = $rows; values = $kv } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $json -Encoding UTF8
Write-Host "JSON: $json"
if ($nFail -gt 0) { exit 1 } else { exit 0 }
