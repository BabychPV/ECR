<#
.SYNOPSIS
  Один запуск пост-перевірки стенда Land: черга -> SQL (PostCheck.sql) -> API (ApiSmoke.ps1) -> [ізоляція вікна] -> світлофор PASS/FAIL/INFO.
.DESCRIPTION
  Пише лише у свій -OutDir: postcheck-<ts>.out.txt, postcheck-api-<ts>.json, postcheck-summary-<ts>.txt.
  SQL - лише SELECT NOLOCK (1 процес sqlcmd, -l 30 -t 120). API - лише GET + вхід. Бекап - лише з -DoBackup.
  Значень прода пакет не містить: усі пороги - параметри (-Baseline*, -Expected*); не задано - рядок INFO зі значенням.
  Шляхів і акаунтів у пакеті немає: -AccountsFile/-User/-BackupDir/-LogDir/-OutDir (або змінні середовища
  ECR_POSTCHECK_ACCOUNTS / _USER / _BASEURL / _BACKUPDIR / _LOGDIR / _OUTDIR / _SERVER). Пароль не друкується.
  Код виходу: 0 - немає FAIL; 1 - є FAIL; 2 - черга не порожня (нічого не запущено); 3 - sqlcmd не відповів.
.PARAMETER Since
  UTC час старту вікна (recalc/міграція), напр. 2026-10-14T05:00:00.
.PARAMETER Database
  ОБОВ'ЯЗКОВИЙ, лише EcrLand*: пакет не має бази за замовчуванням, щоб не цілити в живий стенд мовчки.
.PARAMETER Docs
  Doc-scope для перевірки "Current-прогони періоду завершились після since": BusinessKey або DocumentId
  документів, що перераховувались. Без параметра - усі документи (поведінка за замовчуванням).
.PARAMETER ExpectedTemplateVersion
  Очікувана версія шаблону. Без параметра: проєкт має стояти на найновішій Published, а всі документи - на ній
  (літералу версії немає).
.PARAMETER WindowTag
  Тег імені post-бекапу: ecr-land-ui-post-<WindowTag>-<yyyy-MM-dd>.bak (існуючий файл не перезаписується).
.PARAMETER Window7Isolation
  Необов'язковий розділ: ізоляція колонок одного документа/таблиці (WindowExtra.sql; потрібні -IsolationDocument).
.EXAMPLE
  powershell -File Run-PostCheck.ps1 -Database EcrLandAccP -Since 2026-10-14T05:00:00 -BaseUrl http://localhost:5101 -SkipApi -NoExpectNew
  powershell -File Run-PostCheck.ps1 -Database <база> -Since 2026-10-15T12:00:00 -Docs P1-V1-0010 -WindowTag w7 -DoBackup -BackupDir <каталог>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Since,
    [Parameter(Mandatory = $true)][ValidatePattern('^EcrLand[A-Za-z0-9_]+$')][string]$Database,
    [string]$Server       = $(if ($env:ECR_POSTCHECK_SERVER) { $env:ECR_POSTCHECK_SERVER } else { 'localhost' }),
    [string]$BaseUrl      = $env:ECR_POSTCHECK_BASEURL,
    [string]$ToolsDir,
    [string]$AccountsFile = $env:ECR_POSTCHECK_ACCOUNTS,
    [string]$User         = $env:ECR_POSTCHECK_USER,
    [string]$BackupDir    = $env:ECR_POSTCHECK_BACKUPDIR,
    [string]$BackupName,
    [ValidatePattern('^[A-Za-z0-9_]+$')][string]$WindowTag = 'check',
    [string]$LogDir       = $env:ECR_POSTCHECK_LOGDIR,
    [string]$LogPath,
    [int]   $StartLine    = 0,
    [string]$OutDir       = $env:ECR_POSTCHECK_OUTDIR,
    [string[]]$Docs       = @(),
    [ValidatePattern('^[A-Za-z0-9_]+$')][string]$ProjectCode = 'LANDUI2026',
    [string]$TemplateCode = 'LAND_UI',
    [int]   $Period       = 202606,
    [string]$ExpectedTemplateVersion,
    [double]$ExpectedDocuments = -1,
    [double]$ExpectedApprovals = -1,
    [double]$BaselineResults = -1,            # C11/C12 Current: >= PASS, 95-100% INFO, <95% FAIL
    [double]$BaselineMethodologies = -1,
    [double]$BaselineResultsPeriod = -1,
    [double]$BaselineEntered = -1,
    [double]$BaselineErrorCells = -1,         # комірок-помилок не більше
    [double]$BaselineLp40Columns = -1,
    [double]$BaselineLp40NonEmpty = -1,
    [string[]]$ExpectedMethodologyVersions = @(),    # CODE=V3 (кілька - через кому): остання Published версія
    [string]$ExpectedRowFilter,
    [double]$ExpectedRowFilterCount = -1,
    [string]$ExpectedRowFilterNullCodes,      # коди через кому, що мають лишитись без фільтра
    [string]$SmokeDocumentId,
    [string]$TableCode    = 'T10_01',
    [string]$TableColumnCode,
    [string]$RegistryCode,
    [string]$Lp45Periods  = '202601,202604,202606',
    [switch]$DoBackup,
    [switch]$SkipApi,
    [switch]$SkipLp45,
    [switch]$AllowQueue,
    [switch]$NoExpectNew,                     # база без нових колонок/версій: відсутність = INFO, не FAIL
    [switch]$Window7Isolation,
    [string]$IsolationDocument,               # BusinessKey документа для -Window7Isolation
    [string]$IsolationTable = 'T9_04',
    [int]   $IsolationPeriodFrom = 202601,
    [int]   $IsolationPeriodTo   = 202607,
    [double]$IsolationBaselineRows = -1,
    [string[]]$WindowMethodologies = @()      # CODE або CODE=поріг_Current
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ToolsDir) { $ToolsDir = Split-Path -Parent $here }
if (-not $OutDir) { $OutDir = Join-Path ([IO.Path]::GetTempPath()) 'ecr-postcheck' }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$Docs = @($Docs | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($d in $Docs) { if ($d -notmatch '^[A-Za-z0-9_.-]{1,60}$') { throw "-Docs: недопустиме значення '$d' (літери, цифри, _ . -)." } }
$emv = @{}
foreach ($e in @($ExpectedMethodologyVersions | ForEach-Object { $_ -split '[,;]' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    if ($e -notmatch '^([A-Za-z0-9_]+)=([A-Za-z0-9_.-]+)$') { throw "-ExpectedMethodologyVersions: очікується КОД=версія, отримано '$e'." }
    $emv[$matches[1]] = $matches[2]
}
$WindowMethodologies = @($WindowMethodologies | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($w in $WindowMethodologies) { if ($w -notmatch '^[A-Za-z0-9_]+(=\d+)?$') { throw "-WindowMethodologies: недопустиме значення '$w'." } }
if ($ExpectedRowFilter -and $ExpectedRowFilter -notmatch '^[A-Za-z0-9_:.-]+$') { throw '-ExpectedRowFilter: недопустимі символи.' }
if ($Window7Isolation -and -not $IsolationDocument) { throw '-Window7Isolation потребує -IsolationDocument (BusinessKey).' }
if ($IsolationDocument -and $IsolationDocument -notmatch '^[A-Za-z0-9_.-]{1,60}$') { throw '-IsolationDocument: недопустиме значення.' }
if ($IsolationTable -notmatch '^[A-Za-z0-9_]+$') { throw '-IsolationTable: недопустиме значення.' }
if (-not $SkipApi -and [string]::IsNullOrWhiteSpace($BaseUrl)) { throw 'API-етап потребує -BaseUrl (або ECR_POSTCHECK_BASEURL); без API - -SkipApi.' }
$sinceDt = [datetime]::Parse($Since, [Globalization.CultureInfo]::InvariantCulture)
$sinceCompact = $sinceDt.ToString('yyyyMMddHHmmss')       # sqlcmd -v: без двокрапок і пробілів
function Say($m) { "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m | Write-Host }
function Given($x) { return ($x -ge 0) }
# Підсумковий файл пишеться завжди, навіть при ранній зупинці.
function Write-EarlySummary($code, $msg) {
    $p = Join-Path $OutDir "postcheck-summary-$ts.txt"
    Set-Content -LiteralPath $p -Value @("post-check $ts; база=$Database; ЗУПИНКА (код виходу $code): $msg", "STOP         FAIL  $msg") -Encoding UTF8
    Say "підсумок: $p"
}
# ---- 1. черга порожня? ----
$q = "SET NOCOUNT ON; SELECT COUNT(*) FROM itg.JobProgress WITH (NOLOCK) WHERE State IN ('Queued','Running','Retrying');"
$qo = @(& sqlcmd -S $Server -E -d $Database -l 30 -t 60 -h -1 -W -b -Q $q)
$qrc = $LASTEXITCODE
$open = ($qo | Where-Object { "$_".Trim() -match '^\d+$' } | Select-Object -First 1)
if ($qrc -ne 0 -or $null -eq $open) { Say "sqlcmd не відповів (код $qrc) - зупинка"; Write-EarlySummary 3 "sqlcmd не відповів (код $qrc)"; exit 3 }
if ([int]$open -gt 0 -and -not $AllowQueue) { Say "ЗУПИНКА: у черзі $open задач (Queued/Running/Retrying). Дочекайтесь кінця recalc."; Write-EarlySummary 2 "у черзі $open задач (Queued/Running/Retrying)"; exit 2 }
Say "черга: $open - ок"

function Read-Kv($path, $prefix) {
    $h = @{}
    foreach ($l in (Get-Content -LiteralPath $path -Encoding UTF8)) {
        if ($l -match ('^' + $prefix + '\|([^|]+)\|(.*)$')) { $h[$matches[1]] = $matches[2].Trim() }
    }
    return $h
}

# ---- 2. SQL ----
$outSql = Join-Path $OutDir "postcheck-$ts.out.txt"
$docsVar = if ($Docs.Count) { $Docs -join ';' } else { 'ALL' }
$methCodes = @($emv.Keys | ForEach-Object { "$_" } | Where-Object { $_ -match '^[A-Za-z0-9_]+$' } | Sort-Object)
$methVar = if ($methCodes.Count) { $methCodes -join ';' } else { 'NONE' }
$rfVar = if ($ExpectedRowFilter) { $ExpectedRowFilter.Replace(':', '~') } else { 'NONE' }   # sqlcmd -v не приймає ':' у значенні
$sqlArgs = @('-S', $Server, '-E', '-d', $Database, '-f', '65001', '-W', '-s', '|', '-b', '-l', '30', '-t', '120',
       '-v', "since=$sinceCompact", "projectcode=$ProjectCode", "period=$Period", "docs=$docsVar", "methcodes=$methVar", "rowfilter=$rfVar",
       '-i', (Join-Path $here 'PostCheck.sql'), '-o', $outSql)
Say "SQL: $Database since=$($sinceDt.ToString('s'))Z docs=$docsVar ..."
$sw = [Diagnostics.Stopwatch]::StartNew()
& sqlcmd @sqlArgs
$sqlRc = $LASTEXITCODE
Say ("SQL завершено за {0:N0} с, код {1}; вивід: {2}" -f $sw.Elapsed.TotalSeconds, $sqlRc, $outSql)
$KV = Read-Kv $outSql 'PC'

# ---- 3. API ----
$AV = @{}; $apiRc = $null; $apiCrash = $false; $apiCrashText = ''
if (-not $SkipApi) {
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'ApiSmoke.ps1'),
               '-BaseUrl', $BaseUrl, '-ToolsDir', $ToolsDir, '-OutDir', $OutDir, '-Database', $Database, '-Server', $Server,
               '-TemplateCode', $TemplateCode, '-ProjectCode', $ProjectCode, '-PeriodKey', "$Period", '-TableCode', $TableCode,
               '-Lp45Periods', $Lp45Periods, '-WindowTag', $WindowTag)
    if ($AccountsFile)  { $args2 += @('-AccountsFile', $AccountsFile) }
    if ($User)          { $args2 += @('-User', $User) }
    if ($LogPath)       { $args2 += @('-LogPath', $LogPath) }
    if ($LogDir)        { $args2 += @('-LogDir', $LogDir) }
    if ($StartLine)     { $args2 += @('-StartLine', "$StartLine") }
    if ($SmokeDocumentId) { $args2 += @('-DocumentId', $SmokeDocumentId) }
    if ($TableColumnCode) { $args2 += @('-TableColumnCode', $TableColumnCode) }
    if ($RegistryCode)  { $args2 += @('-RegistryCode', $RegistryCode) }
    if ($ExpectedTemplateVersion) { $args2 += @('-ExpectedTemplateVersion', $ExpectedTemplateVersion) }
    if ($methCodes.Count) { $args2 += @('-MethodologyCodes', ($methCodes -join ',')) }
    if ($BackupDir)     { $args2 += @('-BackupDir', $BackupDir) }
    if ($BackupName)    { $args2 += @('-BackupName', $BackupName) }
    if ($DoBackup)      { $args2 += '-DoBackup' }
    if ($SkipLp45)      { $args2 += '-SkipLp45' }
    Say 'API smoke (лише GET) ...'
    $apiOut = @()
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $apiOut = @(& powershell @args2 2>&1 | ForEach-Object { "$_" })
        $apiRc = $LASTEXITCODE
    } catch {
        $apiRc = 99
        $apiOut = @("API-етап впав у run-скрипті: $($_.Exception.Message)")
    } finally { $ErrorActionPreference = $prevEap }
    $apiOut | ForEach-Object { if ($_ -match '^PCAPI\|([^|]+)\|(.*)$') { $AV[$matches[1]] = $matches[2].Trim() } }
    $apiOut | Where-Object { $_ -notmatch '^PCAPI\|' } | ForEach-Object { Write-Host $_ }
    # крах: немає підсумкових рядків PCAPI|pass (скрипт упав до підсумку) - не плутати зі «FAIL = N»
    $apiCrash = (-not $AV.ContainsKey('pass'))
    if ($apiCrash) {
        $apiCrashText = (@($apiOut | Where-Object { "$_".Trim() } | Select-Object -Last 6) -join ' // ')
        if ($apiCrashText.Length -gt 600) { $apiCrashText = $apiCrashText.Substring(0, 600) }
    }
}

# ---- 4. ізоляція вікна (необов'язково) ----
$WX = @{}; $wxRc = $null
if ($Window7Isolation) {
    $outWx = Join-Path $OutDir "postcheck-window-$ts.out.txt"
    $wmVar = if ($WindowMethodologies.Count) { ($WindowMethodologies -join ';').Replace('=', '@') } else { 'NONE' }
    $wxArgs = @('-S', $Server, '-E', '-d', $Database, '-f', '65001', '-W', '-s', '|', '-b', '-l', '30', '-t', '120',
           '-v', "since=$sinceCompact", "projectcode=$ProjectCode", "isodoc=$IsolationDocument", "istable=$IsolationTable",
           "pfrom=$IsolationPeriodFrom", "pto=$IsolationPeriodTo", "wmeth=$wmVar",
           '-i', (Join-Path $here 'WindowExtra.sql'), '-o', $outWx)
    Say "WindowExtra: $Database doc=$IsolationDocument table=$IsolationTable ..."
    & sqlcmd @wxArgs
    $wxRc = $LASTEXITCODE
    $WX = Read-Kv $outWx 'WX'
}

# ---- 5. світлофор ----
$res = New-Object System.Collections.ArrayList
function V($id, $what, $verdict, $detail) { [void]$res.Add([pscustomobject]@{ id = $id; verdict = $verdict; what = $what; detail = $detail }) }
function N($key) { if ($KV.ContainsKey($key)) { $d = 0.0; if ([double]::TryParse($KV[$key], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d } } return $null }
function NW($key) { if ($WX.ContainsKey($key)) { $d = 0.0; if ([double]::TryParse($WX[$key], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d } } return $null }
function Ge($a, $b) { return ($null -ne $a -and $a -ge $b) }
function Eq($a, $b) { return ($null -ne $a -and $a -eq $b) }
function Pv($ok) { if ($ok) { 'PASS' } else { 'FAIL' } }
$newFail = if ($NoExpectNew) { 'INFO' } else { 'FAIL' }

if ($sqlRc -ne 0 -or $KV.Count -eq 0) { V 'SQL' 'PostCheck.sql виконано' 'FAIL' "sqlcmd код $sqlRc, PC-рядків: $($KV.Count)" }

# doc-scope
$sReq = N 'scope_docs_requested'; $sRes = N 'scope_docs_resolved'
if ($Docs.Count) {
    V 'SCOPE' "doc-scope: усі $($Docs.Count) документів із -Docs знайдено в базі" $(Pv ((Eq $sReq $Docs.Count) -and (Eq $sRes $Docs.Count))) "requested=$sReq resolved=$sRes"
}
# A-10
$r = N 'a10_current_results'; $nul = N 'a10_null_values'; $mm = N 'a10_methodologies'
if (Given $BaselineResults) {
    $v = if ($null -eq $r) { 'FAIL' } elseif ($r -ge $BaselineResults) { 'PASS' } elseif ($r -ge 0.95 * $BaselineResults) { 'INFO' } else { 'FAIL' }
    V 'A-10a' "результати C11/C12 Current >= $BaselineResults (PASS), 95-100% (INFO), <95% (FAIL)" $v "results=$r"
} else { V 'A-10a' 'результати C11/C12 Current (поріг -BaselineResults не задано)' 'INFO' "results=$r" }
V 'A-10b' 'null_values = 0' $(Pv (Eq $nul 0)) "null_values=$nul"
if (Given $BaselineMethodologies) { V 'A-10c' "методологій із результатами >= $BaselineMethodologies" $(Pv (Ge $mm $BaselineMethodologies)) "methodologies=$mm" }
else { V 'A-10c' 'методологій із результатами (поріг не задано)' 'INFO' "methodologies=$mm" }
# період
$p = N 'period_results'; $pr = N 'period_current_runs'; $pb = N 'period_current_before_since'
if (Given $BaselineResultsPeriod) {
    $v = if ($null -eq $p) { 'FAIL' } elseif ($p -ge $BaselineResultsPeriod) { 'PASS' } elseif ($p -ge 0.95 * $BaselineResultsPeriod) { 'INFO' } else { 'FAIL' }
    V "${Period}a" "результати $Period >= $BaselineResultsPeriod (95-100% INFO)" $v "results=$p"
} else { V "${Period}a" "результати $Period (поріг -BaselineResultsPeriod не задано)" 'INFO' "results=$p" }
$scopeText = if ($Docs.Count) { "у doc-scope ($($Docs.Count) док.)" } else { 'для всіх документів' }
V "${Period}b" "Current-прогони $Period $scopeText завершились після старту вікна" $(if ($pr -gt 0 -and $pb -eq 0) { 'PASS' } else { 'FAIL' }) "current_runs=$pr before_since=$pb all_docs_before_since=$($KV['period_current_before_since_all_docs'])"
# LP-40
$lc = N 'lp40_columns_with_values'; $ln = N 'lp40_non_empty'; $le = N 'lp40_errs'
$lpOk = (Eq $le 0)
if (Given $BaselineLp40Columns) { $lpOk = $lpOk -and (Ge $lc $BaselineLp40Columns) }
if (Given $BaselineLp40NonEmpty) { $lpOk = $lpOk -and (Ge $ln $BaselineLp40NonEmpty) }
V 'LP-40' 'колонок із значеннями / непорожніх не менше порогів (якщо задані), помилок = 0' $(Pv $lpOk) "columns=$lc non_empty=$ln errs=$le"
# A-13
$ce = N 'cells_entered'; $cx = N 'cells_error'
$a13 = $true
if (Given $BaselineEntered) { $a13 = $a13 -and (Ge $ce $BaselineEntered) }
if (Given $BaselineErrorCells) { $a13 = $a13 -and ($null -ne $cx -and $cx -le $BaselineErrorCells) }
V 'A-13' 'введених не менше порога, комірок-помилок (#...) не більше порога (якщо задані)' $(if ($a13 -and ((Given $BaselineEntered) -or (Given $BaselineErrorCells))) { 'PASS' } elseif ($a13) { 'INFO' } else { 'FAIL' }) "entered=$ce error_cells=$cx"
# A-14 / A-15 / API
if (-not $SkipApi) {
    $v14 = $AV['verdict_A-14']; if (-not $v14) { $v14 = 'INFO' }
    V 'A-14' 'журнал API: SYS0500/Unhandled/5xx = 0, ERROR = 0' $v14 "ERROR=$($AV['log_error']) SYS0500=$($AV['log_ecr_sys_0500']) Unhandled=$($AV['log_unhandled']) 5xx=$($AV['log_5xx']) from=$($AV['log_from_line']) file=$($AV['log_file'])"
    $v15 = if ($DoBackup) { $AV['verdict_A-15'] } else { 'INFO' }
    V 'A-15' 'COPY_ONLY бекап + VERIFYONLY + SHA256' $(if ($v15) { $v15 } else { 'INFO' }) $(if ($DoBackup) { "file=$($AV['backup_file']) sha256=$($AV['backup_sha256'])" } else { 'не робився (-DoBackup)' })
    if ($apiCrash) {
        V 'API-крах' 'API-етап не дійшов до підсумку (скрипт упав: вхід/мережа)' 'FAIL' "код=$apiRc; хвіст виводу: $apiCrashText"
    } else {
        V 'API' 'S-01..S-10 (+періоди, слайс): FAIL = 0' $(Pv ($apiRc -eq 0)) "pass=$($AV['pass']) fail=$($AV['fail']) info=$($AV['info'])"
    }
    if ($SkipLp45) {
        V 'LP45cold' 'LP-45 холодний замір' 'INFO' 'пропущено (-SkipLp45)'
        V 'LP45warm' 'LP-45 теплий замір' 'INFO' 'пропущено (-SkipLp45)'
    } elseif ($apiCrash) {
        V 'LP45cold' 'LP-45 холодний <= 10 с' 'FAIL' 'API-крах - замір не виконано'
        V 'LP45warm' 'LP-45 теплий <= 5 с, без 5xx' 'FAIL' 'API-крах - замір не виконано'
    } else {
        $lpP = @(); if ($AV['lp45_periods']) { $lpP = @($AV['lp45_periods'] -split ',' | Where-Object { $_ }) }
        $coldBad = @(); $coldSlow = @(); $warmBad = @(); $det = @(); $spk = 0
        foreach ($pk in $lpP) {
            $cs = $AV["lp45_${pk}_cold_status"]; $cm = $AV["lp45_${pk}_cold_ms"]; $wm = $AV["lp45_${pk}_warm_max_ms"]; $nn = $AV["lp45_${pk}_non200"]
            $det += "$pk cold=${cm}мс($cs) items=$($AV["lp45_${pk}_cold_items"]) warm=$($AV["lp45_${pk}_warm_ms"]) spikes=$($AV["lp45_${pk}_saw_spikes_gt5s"])"
            if ($cs -ne '200') { $coldBad += $pk } elseif ([double]$cm -gt 10000) { $coldSlow += $pk }
            if (([double]$wm -gt 5000) -or ([int]$nn -gt 0)) { $warmBad += $pk }
            $spk += [int]$AV["lp45_${pk}_saw_spikes_gt5s"]
        }
        $vc = if ($lpP.Count -eq 0) { 'FAIL' } elseif ($coldBad.Count) { 'FAIL' } elseif ($coldSlow.Count) { 'INFO' } else { 'PASS' }
        $vw = if ($lpP.Count -eq 0) { 'FAIL' } elseif ($warmBad.Count) { 'FAIL' } else { 'PASS' }
        V 'LP45cold' 'LP-45 холодний /documents <= 10 с (PASS), > 10 с (INFO), не 200 (FAIL)' $vc ($det -join '; ')
        V 'LP45warm' 'LP-45 теплий /documents <= 5 с, без 5xx (інакше FAIL); пилка >5 с - у деталях' $vw ("bad=$($warmBad -join ',') пилка_спайків>5с=$spk")
    }
} else {
    V 'A-14' 'журнал API' 'INFO' 'пропущено (-SkipApi)'
    V 'A-15' 'бекап' 'INFO' 'пропущено (-SkipApi)'
    V 'API' 'API smoke' 'INFO' 'пропущено (-SkipApi)'
}
# A-16: шаблон / документи / затвердження (без літералу версії)
$latest = $KV['template_latest_published']; $projTpl = $KV['project_template']
$dTot = N 'documents_total'; $dProj = N 'documents_on_project_template'; $dOth = N 'documents_on_other_template'; $appr = N 'approval_rows'
$tplOk = ($latest -ne 'none') -and ($projTpl -ne 'none') -and ($projTpl -eq $latest)
if ($ExpectedTemplateVersion) { $tplOk = $tplOk -and ($latest -eq $ExpectedTemplateVersion) }
$docsOk = (Eq $dOth 0) -and ($null -ne $dTot) -and (Eq $dProj $dTot)
if (Given $ExpectedDocuments) { $docsOk = $docsOk -and (Eq $dTot $ExpectedDocuments) }
$apprOk = $true
if (Given $ExpectedApprovals) { $apprOk = (Eq $appr $ExpectedApprovals) }
$expText = if ($ExpectedTemplateVersion) { "очікувана $ExpectedTemplateVersion" } else { 'проєкт на найновішій Published' }
V 'A-16' "шаблон: $expText; усі документи лише на версії проєкту; ApprovalState за -ExpectedApprovals (якщо задано)" $(if ($tplOk -and $docsOk -and $apprOk) { 'PASS' } else { $newFail }) "latest=$latest project=$projTpl docs=$dTot on_project_tpl=$dProj on_other=$dOth approval=$appr"
# дублі, збої
V 'DUP' 'дублів Current (проєкт x документ x період) = 0' $(Pv (Eq (N 'dup_current_groups') 0)) "groups=$($KV['dup_current_groups'])"
V 'FAILED' 'Failed після since: calc.CalculationRun = 0 і itg.JobProgress = 0' $(Pv ((Eq (N 'runs_failed_since') 0) -and (Eq (N 'jobs_failed_since') 0))) "runs=$($KV['runs_failed_since']) jobs=$($KV['jobs_failed_since'])"
V 'CANCELLED' 'Cancelled після since (інформативно: витіснення EnqueueExclusive легальне)' $(if ((Eq (N 'runs_cancelled_since') 0) -and (Eq (N 'jobs_cancelled_since') 0)) { 'PASS' } else { 'INFO' }) "runs=$($KV['runs_cancelled_since']) jobs=$($KV['jobs_cancelled_since'])"
V 'QUEUE' 'черга Queued/Running/Retrying = 0 після прогону' $(Pv (Eq (N 'queue_open') 0)) "open=$($KV['queue_open'])"
# TableInstanceId
if ($KV['tableinstanceid_column'] -eq 'absent') {
    V 'TII' 'calc.CalculationResult.TableInstanceId заповнений у нових результатах' $newFail 'колонка відсутня (міграція не застосована)'
} else {
    $tn = N 'tii_new_results'; $tnull = N 'tii_new_null'
    $v = if ($tn -le 0) { 'FAIL' } elseif ($tnull -eq 0) { 'PASS' } elseif ($tnull -lt $tn) { 'INFO' } else { 'FAIL' }
    V 'TII' 'нові результати: NULL = 0 (PASS), частково (INFO), усі NULL або немає нових (FAIL)' $v "new=$tn null=$tnull not_null=$($tn - $tnull)"
}
V 'BRANCH_BOTH' 'BRANCH_BOTH у TraceJson/ModulesProfileJson/JobProgress після since = 0' $(Pv (Eq (N 'branch_both_hits') 0)) "hits=$($KV['branch_both_hits'])"
# RowActivityFilter
if ($KV['rowactivityfilter_column'] -eq 'absent') {
    V 'ROWFILTER' 'calc.Methodology.RowActivityFilter' $newFail 'колонка відсутня (міграція не застосована)'
} else {
    $wf = N 'rowactivityfilter_methodologies'
    if (Given $ExpectedRowFilterCount) { V 'ROWFILTERa' "методологій із RowActivityFilter = $ExpectedRowFilterCount" $(if ($wf -eq $ExpectedRowFilterCount) { 'PASS' } elseif ($wf -gt 0) { 'INFO' } else { 'FAIL' }) "with_filter=$wf" }
    else { V 'ROWFILTERa' 'методологій із RowActivityFilter (очікувана кількість не задана)' 'INFO' "with_filter=$wf" }
    if ($ExpectedRowFilter) {
        $vo = N 'rowfilter_value_ok'; $oth = N 'rowfilter_other'; $nc = "$($KV['rowfilter_null_codes'])"
        $okAll = ($vo -gt 0) -and ($oth -eq 0)
        if (Given $ExpectedRowFilterCount) { $okAll = $okAll -and ($vo -eq $ExpectedRowFilterCount) }
        if ($ExpectedRowFilterNullCodes) {
            $want = (($ExpectedRowFilterNullCodes -split ',' | Where-Object { $_ } | Sort-Object) -join ',')
            $got = (($nc -split ',' | Where-Object { $_ } | Sort-Object) -join ',')
            $okAll = $okAll -and ($want -eq $got)
        }
        V 'ROWFILTERv' "RowActivityFilter = очікуваному значенню; NULL лише в -ExpectedRowFilterNullCodes (якщо задано); інших значень 0" $(if ($okAll) { 'PASS' } else { $newFail }) "value_ok=$vo null_codes=$nc other=$oth other_codes=$($KV['rowfilter_other_codes'])"
    }
}
V 'RULES' 'jobs.recalcCategoryRuleRejected після since (інформативно; порівняти з попереднім recalc)' 'INFO' "jobs=$($KV['jobs_category_rule_rejected'])"
# версії методологій
if ($methCodes.Count) {
    $latestMap = @{}
    foreach ($pair in ("$($KV['methodology_latest_published'])" -split ';')) { if ($pair -match '^([^=]+)=(.*)$') { $latestMap[$matches[1]] = $matches[2] } }
    foreach ($c in $methCodes) {
        $got = $latestMap[$c]
        V "VER-$c" "${c}: остання Published = $($emv[$c])" $(if ($got -eq "$($emv[$c])") { 'PASS' } else { $newFail }) "latest=$got"
    }
}

# ---- розділ ізоляції вікна ----
if ($Window7Isolation) {
    if ($wxRc -ne 0 -or $WX.Count -eq 0 -or -not $WX.ContainsKey('done')) { V 'WX-SQL' 'WindowExtra.sql виконано' 'FAIL' "sqlcmd код $wxRc, WX-рядків: $($WX.Count)" }
    elseif ($WX['iso_doc'] -match '^\D' -or $WX['iso_tabledef'] -match '^\D') { V 'WX-ISO' 'документ/таблиця ізоляції знайдені' 'FAIL' "doc=$($WX['iso_doc']) tabledef=$($WX['iso_tabledef'])" }
    else {
        $rows = NW 'iso_rows'
        if (Given $IsolationBaselineRows) {
            V 'WX-rows' "рядків таблиці ізоляції >= $IsolationBaselineRows (більше - нові rowKey)" $(if (Ge $rows $IsolationBaselineRows) { 'PASS' } else { $newFail }) "rows=$rows legacy_key=$($WX['iso_rows_legacy_key'])"
            V 'WX-KEY4' "комірок на KEY4-колонку >= $IsolationBaselineRows" $(if (Ge (NW 'iso_key4_min_cells') $IsolationBaselineRows) { 'PASS' } else { $newFail }) "min_cells=$($WX['iso_key4_min_cells'])"
        } else {
            V 'WX-rows' 'рядків таблиці ізоляції (поріг -IsolationBaselineRows не задано)' 'INFO' "rows=$rows legacy_key=$($WX['iso_rows_legacy_key']) key4_min_cells=$($WX['iso_key4_min_cells'])"
        }
        V 'WX-G20' 'комірки G20 (SubstanceConsumption20Dgr1atm): наявні й введені (інформативно)' $(if ((NW 'iso_G20_entered') -gt 0) { 'PASS' } else { 'INFO' }) "cells=$($WX['iso_G20_cells']) entered=$($WX['iso_G20_entered']) calculated=$($WX['iso_G20_calculated']) empty=$($WX['iso_G20_empty'])"
        V 'WX-KgH' 'SubstanceConsumption_M* (KgH): лічильники' 'INFO' "cells=$($WX['iso_KgH_cells']) entered=$($WX['iso_KgH_entered']) calculated=$($WX['iso_KgH_calculated']) empty=$($WX['iso_KgH_empty'])"
        V 'WX-Kg' 'SubstanceConsumption_M*_<літера> (Kg): лічильники' 'INFO' "cells=$($WX['iso_Kg_cells']) entered=$($WX['iso_Kg_entered']) calculated=$($WX['iso_Kg_calculated']) empty=$($WX['iso_Kg_empty'])"
        V 'ISO-rows' 'правки G20 і НЕ-перерахунком KgH/Kg у тих самих рядках після since = 0' $(Pv (Eq (NW 'ch_rows_g20_and_kgh_kg_nonrecalc') 0)) "rows=$($WX['ch_rows_g20_and_kgh_kg_nonrecalc'])"
        $kgNon = (NW 'ch_KgH_total') - (NW 'ch_KgH_recalc') + (NW 'ch_Kg_total') - (NW 'ch_Kg_recalc')
        V 'ISO-KgKgH' 'правки KgH/Kg (не Recalculation) після since = 0' $(Pv (Eq $kgNon 0)) "nonrecalc=$kgNon KgH=$($WX['ch_KgH_total']) Kg=$($WX['ch_Kg_total'])"
        V 'ISO-G20' 'правки G20 після since (інформативно)' 'INFO' "G20=$($WX['ch_G20_total']) (recalc=$($WX['ch_G20_recalc'])) OT=$($WX['ch_OT_total']) KEY4=$($WX['ch_KEY4_total']) OTHER=$($WX['ch_OTHER_total'])"
        foreach ($w in $WindowMethodologies) {
            $code = ($w -split '=')[0]
            $cur = NW "m_${code}_current"; $nv = NW "m_${code}_null_values"; $bs = $WX["m_${code}_base"]
            if ($bs -and $bs -ne 'none') { V "WX-$code" "$code Current >= $bs, null_values = 0" $(Pv ((Ge $cur ([double]$bs)) -and (Eq $nv 0))) "current=$cur null=$nv versions_used=$($WX["m_${code}_versions_used"])" }
            else { V "WX-$code" "$code Current (поріг не задано), null_values = 0" $(if (Eq $nv 0) { 'INFO' } else { 'FAIL' }) "current=$cur null=$nv versions_used=$($WX["m_${code}_versions_used"])" }
        }
        V 'WX-DUPRES' 'дублі Current-результатів у різних прогонах (методології вікна) = 0' $(Pv (Eq (NW 'dup_current_result_runs') 0)) "groups=$($WX['dup_current_result_runs'])"
        foreach ($k in 'step', 'input') {
            $tl = NW "tii_${k}_null"
            V "WX-TII-$k" "Calculation$([char]::ToUpper($k[0]))$($k.Substring(1)).TableInstanceId NULL = 0" $(if ($null -eq $tl) { 'INFO' } else { Pv ($tl -eq 0) }) "new=$($WX["tii_${k}_new"]) null=$($WX["tii_${k}_null"])"
        }
        V 'WX-RULES' 'recalcCategoryRuleRejected / recalcNoMatchingRule після since (інформативно)' 'INFO' "rejected=$($WX['jobs_category_rule_rejected']) noMatching=$($WX['jobs_no_matching_rule'])"
        V 'WX-BINDINGS' "прив'язки активні/неактивні (інформативно)" 'INFO' "active=$($WX['bindings_active']) inactive=$($WX['bindings_inactive'])"
    }
}

$res | Format-Table id, verdict, what, detail -AutoSize -Wrap | Out-String -Width 260 | Write-Host
$nF = @($res | Where-Object { $_.verdict -eq 'FAIL' }).Count
$nP = @($res | Where-Object { $_.verdict -eq 'PASS' }).Count
$nI = @($res | Where-Object { $_.verdict -eq 'INFO' }).Count
$sum = Join-Path $OutDir "postcheck-summary-$ts.txt"
$lines = @("post-check $ts; база=$Database; since=$($sinceDt.ToString('s'))Z; docs=$docsVar; PASS=$nP FAIL=$nF INFO=$nI") + ($res | ForEach-Object { "{0,-12} {1,-5} {2} | {3}" -f $_.id, $_.verdict, $_.what, $_.detail })
Set-Content -LiteralPath $sum -Value $lines -Encoding UTF8
Say "ПІДСУМОК: PASS=$nP FAIL=$nF INFO=$nI; файл: $sum"
if ($nF -gt 0) { exit 1 } else { exit 0 }
