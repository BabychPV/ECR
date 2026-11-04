<#
.SYNOPSIS
    Крок 8 HSE401: сценарій користувача "змінив формулу в шаблоні на реальному файлі":
    клон версії -> PUT формули колонки -> publish -> migrate-version документа -> очікування перерахунку -> перевірка значень.
    Міряє: клон, зміна формули, publish, migrate (запит + задача), перерахунок до тиші, зріз таблиці (час/розмір).

.PARAMETER Expression  нова формула H_RATE (за замовчуванням: ×1.1).
.PARAMETER NewVersion  номер нової версії (Major.Minor.Patch.Build).
.PARAMETER ColumnCode  колонка, формулу якої міняємо.
#>
[CmdletBinding()]
param(
    [string]$ColumnCode = 'H_RATE',
    [string]$Expression = '[H_LIMIT_T] * 1.1 / IF([H_BASIS] = NULL, [Period].Days, [H_BASIS])',
    [double]$Factor = 1.1,
    [string]$NewVersion = '1.0.0.2'
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Xlsx.Lib.ps1')
. (Join-Path $here 'Hse401Model.Lib.ps1')
$out = Get-Hse401OutDir
$state = Read-HseJson (Join-Path $out 'hse401-state.json')
Connect-Hse
$inv = [Globalization.CultureInfo]::InvariantCulture
$docId = [int]$state['documentId']; $pk = [int]$state['periodKey']
$res = [ordered]@{}
function Lap($sw) { $x = [math]::Round($sw.Elapsed.TotalSeconds, 2); $sw.Restart(); return $x }

# 0. зріз ДО (для порівняння ×Factor)
$sw = [Diagnostics.Stopwatch]::StartNew()
$before = Invoke-Hse GET "/api/v1/documents/$docId/tables/$($state['tableInstanceId'])?periodKey=$pk"
$res['sliceBeforeSec'] = Lap $sw; $res['sliceKB'] = [int]($before.Text.Length / 1KB)
$bRows = @{}; foreach ($r in $before.Json.rows) { $bRows[[string]$r.rowKey] = $r.cells }
Write-HseLog ("зріз до: {0} c, {1} КБ" -f $res['sliceBeforeSec'], $res['sliceKB'])

# 1. клон
$since = (Get-Date).ToUniversalTime().AddSeconds(-1)
$wall = [Diagnostics.Stopwatch]::StartNew()
$cl = Invoke-HseOk POST "/api/v1/template-versions/$($state['versionId'])/clone" @{ newVersion = $NewVersion }
$newVid = [int]$cl.Json.versionId
$res['cloneSec'] = Lap $sw
Write-HseLog "клон: версія $NewVersion id $newVid ($($res['cloneSec']) c)"

# 2. PUT формули
$st = (Invoke-HseOk GET "/api/v1/template-versions/$newVid/structure").Json
$sheet = @($st.sheets) | Where-Object { $_.code -eq $script:HseSheetCode } | Select-Object -First 1
$table = @($sheet.tables) | Where-Object { $_.code -eq $script:HseTableCode } | Select-Object -First 1
$col = @($table.columns) | Where-Object { $_.code -eq $ColumnCode } | Select-Object -First 1
$res['structureReadSec'] = Lap $sw
$null = Invoke-HseOk PUT "/api/v1/template-versions/$newVid/tables/$($table.id)/formulas/column/$($col.id)" @{ dialect = 'Template'; expression = $Expression }
$res['putFormulaSec'] = Lap $sw
Write-HseLog "PUT формули $ColumnCode : $Expression ($($res['putFormulaSec']) c)"

# 3. publish
$p = Invoke-Hse POST "/api/v1/template-versions/$newVid/publish" @{ reason = "HSE401 scratch: $ColumnCode ×$Factor" }
if ($p.Status -notin 200, 201, 202, 204) { throw "publish -> $($p.Status) $(Get-EcrErrorCode $p) $($p.Text)" }
$res['publishSec'] = Lap $sw
Write-HseLog "publish ($($res['publishSec']) c)"

# 4. migrate-version (dry-run, потім застосування у фон)
$dry = Invoke-HseOk POST "/api/v1/documents/$docId/migrate-version" @{ targetVersionId = $newVid; mode = 'Safe'; dryRun = $true }
$res['migrateDryRunSec'] = Lap $sw
Write-HseLog ("migrate dry-run: canApply={0} ({1} c)" -f $dry.Json.canApply, $res['migrateDryRunSec'])
$mig = Invoke-Hse POST "/api/v1/documents/$docId/migrate-version" @{ targetVersionId = $newVid; mode = 'Safe'; dryRun = $false; async = $true }
if ($mig.Status -notin 200, 202) { throw "migrate -> $($mig.Status) $(Get-EcrErrorCode $mig) $($mig.Text)" }
$res['migrateRequestSec'] = Lap $sw
$migStart = [Diagnostics.Stopwatch]::StartNew()
if ($mig.Status -eq 202) { $job = Wait-HseJob -JobId ([string]$mig.Json.jobId) -TimeoutMinutes 60 -PollSeconds 2; $res['migrateJobSec'] = [math]::Round($migStart.Elapsed.TotalSeconds, 1); Write-HseLog "migrate-задача: $($job.state) $($job.message) ($($res['migrateJobSec']) c)" }
$w = Wait-HseDocJobs -DocumentId $docId -Since $since -PollSeconds 2
$res['migratePlusRecalcSec'] = [math]::Round($res['migrateRequestSec'] + $migStart.Elapsed.TotalSeconds, 1)
$res['allJobsCount'] = $w.Jobs; $res['allJobsFailed'] = $w.Failed
Write-HseLog ("migrate + перерахунок до тиші: {0} c; задач {1}, невдалих {2}; {3}" -f $res['migratePlusRecalcSec'], $w.Jobs, $w.Failed, ($w.Messages -join ' | '))
$res['wallFromCloneSec'] = [math]::Round($wall.Elapsed.TotalSeconds, 1)

# 5. зріз ПІСЛЯ + перевірка значень
$state2 = (Invoke-HseOk GET "/api/v1/documents/$docId/tables?periodKey=$pk").Json
$ti = @($state2) | Where-Object { $_.tableCode -eq $script:HseTableCode } | Select-Object -First 1
$sw.Restart()
$after = Invoke-HseOk GET "/api/v1/documents/$docId/tables/$($ti.tableInstanceId)?periodKey=$pk"
$res['sliceAfterSec'] = Lap $sw
Write-HseLog ("зріз після: {0} c, {1} КБ" -f $res['sliceAfterSec'], [int]($after.Text.Length / 1KB))
$monthCols = 1..12 | ForEach-Object { 'H_T{0:00}' -f $_ }
$qCols = 1..4 | ForEach-Object { "H_Q${_}_T" }
$chk = 0; $ok = 0; $bad = New-Object System.Collections.Generic.List[string]; $unchangedRq = 0; $rqChk = 0
foreach ($r in $after.Json.rows) {
    $b = $bRows[[string]$r.rowKey]
    if ($null -eq $b) { continue }
    foreach ($cc in ($monthCols + $qCols)) {
        $bv = 0.0; $av = 0.0
        if ($b.PSObject.Properties[$cc]) { $bv = [double]::Parse([string]$b.$cc, $inv) }
        if ($r.cells.PSObject.Properties[$cc]) { $av = [double]::Parse([string]$r.cells.$cc, $inv) }
        $chk++
        $exp = $bv * $Factor
        if ([Math]::Abs($av - $exp) -le 1e-9 * [Math]::Max([Math]::Abs($exp), [Math]::Abs($av)) + 1e-13) { $ok++ } elseif ($bad.Count -lt 10) { $bad.Add("$($r.rowKey) $cc before=$bv after=$av expected=$exp") }
    }
    foreach ($cc in 'H_RQ1_T', 'H_RQ2_T') {
        $bv = if ($b.PSObject.Properties[$cc]) { [string]$b.$cc } else { '' }; $av = if ($r.cells.PSObject.Properties[$cc]) { [string]$r.cells.$cc } else { '' }
        $rqChk++; if ($bv -eq $av) { $unchangedRq++ }
    }
}
$res['verifyCells'] = $chk; $res['verifyOk'] = $ok; $res['rawUnchangedOk'] = "$unchangedRq/$rqChk"
Write-HseLog ("перевірка ×{0}: {1} з {2} комірок (місяці й квартали тонн) відповідають; RQ без змін: {3}/{4}" -f $Factor, $ok, $chk, $unchangedRq, $rqChk)
foreach ($x in $bad) { Write-HseLog "  розбіжність: $x" }
$ex = @($after.Json.rows)[8]; $ebf = $bRows[[string]$ex.rowKey]
Write-HseLog ("приклад {0}: H_T01 було {1} стало {2}; H_Q1_T було {3} стало {4}" -f $ex.rowKey, $ebf.H_T01, $ex.cells.H_T01, $ebf.H_Q1_T, $ex.cells.H_Q1_T)

$res['newVersionId'] = $newVid; $res['newVersion'] = $NewVersion; $res['expression'] = $Expression
$state['formulaEdit'] = $res
[IO.File]::WriteAllText((Join-Path $out 'hse401-state.json'), (ConvertTo-HseJson -Value $state).ToString(), (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $out 'hse401-formula-edit.json'), (ConvertTo-HseJson -Value $res).ToString(), (New-Object Text.UTF8Encoding($false)))
$res.GetEnumerator() | ForEach-Object { Write-Host ("  {0} = {1}" -f $_.Key, $_.Value) }
