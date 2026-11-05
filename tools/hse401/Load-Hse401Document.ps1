<#
.SYNOPSIS
    Крок 6 HSE401: завантаження ОДНОГО документа 2026 (лише вхідні комірки) пачками PATCH /documents/{id}/cells
    (~1000 рядків, RowKey r0001..r6428, baseVersion null = створення рядка), округлення шуму float до шкали колонки (лог),
    очікування перерахунку.

.PARAMETER Limit   скільки рядків завантажити (0 = усі) - для проби.
.PARAMETER BatchSize  рядків у пачці (за замовчуванням 1000).
#>
[CmdletBinding()]
param([int]$Limit = 0, [int]$BatchSize = 1000, [int]$StartAt = 1, [switch]$DryRun,
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Xlsx.Lib.ps1')
. (Join-Path $here 'Hse401Model.Lib.ps1')
. (Join-Path $here 'Hse401Rows.Lib.ps1')
Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
$preview = Test-Hse401Preview
$out = Get-Hse401OutDir
$stateFile = Get-Hse401StateFile
Connect-Hse
Add-Type -AssemblyName System.Net.Http

$data = Read-HseJson (Join-Path $out 'hse401-extract.json')

if ($preview) {
    # Лише підрахунок: скільки вхідних комірок пішло б у PATCH (усі підстановки довідників вважаються знайденими). Жодного запису.
    $pm = @{}
    foreach ($l in @(@('C', 'region', 'REG'), @('E', 'onoff', 'OON'), @('F', 'area', 'ARE'), @('G', 'facility', 'FAC'), @('H', 'activity', 'ACT'), @('I', 'permit', 'PRM'), @('J', 'equip', 'EQT'), @('K', 'srctype', 'SRT'))) {
        $m = @{}
        foreach ($it in (Get-HseListPlan -Extract $data -Key $l[1] -Pfx $l[2] -Xl $l[0])) { $m[$it.Norm] = 0L }
        $pm[$l[0]] = $m
    }
    $pol = @{}
    foreach ($p in $data['dictionaries']['pollutant']) { $pol[('E' + ([string]$p['code']).Trim()).ToUpperInvariant()] = 0L }
    $pm['O'] = $pol
    $nae = @{}
    foreach ($row in $data['rows']) {
        $v = $row['v']
        if ($v.ContainsKey('C') -and $v.ContainsKey('E') -and $v.ContainsKey('L')) { $nae["$(ConvertTo-HseEn $v['C'])|$(ConvertTo-HseEn $v['E'])|$(ConvertTo-HseNaeCode $v['L'])"] = 0L }
    }
    $pm['NAE'] = $nae
    $plog = [pscustomobject]@{ unmapped = (New-Object System.Collections.Generic.List[object]); round = (New-Object System.Collections.Generic.List[object]) }
    $cnt = 0
    foreach ($row in $data['rows']) { $cnt += (Get-HseRowCells -Row $row -Maps $pm -Log $plog).Count }
    $nrows = $data['rows'].Count
    $existingRows = $null
    $found = Find-Hse401Document
    if ($null -ne $found -and $null -ne $found['tableInstanceId']) {
        $sl = Invoke-HseOk GET "/api/v1/documents/$($found['documentId'])/tables/$($found['tableInstanceId'])?periodKey=$($found['periodKey'])"
        $existingRows = @($sl.Json.rows).Count
    }
    Save-Hse401PlanSection 'load' ([ordered]@{
            rows = $nrows; inputCells = $cnt; batchSize = $BatchSize; batches = [int][Math]::Ceiling($nrows / [double]$BatchSize)
            documentExists = ($null -ne $found -and $null -ne $found['documentId']); existingRowsInDocument = $existingRows
            roundingsExpected = $plog.round.Count
            note = 'PATCH створює рядки з baseVersion=null: у документі з уже наявними рядками пачка дасть конфлікт 409 - спершу переконайтеся, що existingRowsInDocument = 0 або null.'
        })
    Write-HseLog ("[preview] завантаження: рядків {0}, вхідних комірок {1}, пачок {2}, рядків у документі вже: {3}" -f $nrows, $cnt, [int][Math]::Ceiling($nrows / [double]$BatchSize), $existingRows)
    return
}

$state = Read-HseJson $stateFile
Assert-Hse401StateMatches $state
$maps = Get-HseLookupMaps -Extract $data -NaeMapFile (Get-Hse401NaeMapFile)
$log = [pscustomobject]@{ unmapped = (New-Object System.Collections.Generic.List[object]); round = (New-Object System.Collections.Generic.List[object]) }

$rows = $data['rows']
$total = $rows.Count
$from = $StartAt
$to = if ($Limit -gt 0) { [Math]::Min($total, $StartAt + $Limit - 1) } else { $total }
$docId = [int]$state['documentId']; $ti = [int]$state['tableInstanceId']; $pk = [int]$state['periodKey']
Write-HseLog ("Документ {0}, tableInstance {1}, period {2}; рядки {3}..{4} з {5}" -f $docId, $ti, $pk, $from, $to, $total)

# D3 (репетиція 05.11): PATCH створює рядки з baseVersion=null, тож у документі з рядками кожна пачка дає 409 rowKeysExist.
# Повний прогін по вже завантаженому документу - no-op; дозавантаження частини (-StartAt/-Limit) - свідомий вибір викликача.
if (-not $DryRun -and $StartAt -le 1 -and $Limit -le 0) {
    $sl = Invoke-HseOk GET "/api/v1/documents/$docId/tables/${ti}?periodKey=$pk"
    $existingRows = @($sl.Json.rows).Count
    if ($existingRows -gt 0) {
        Write-HseLog ("Документ уже має {0} рядків - завантаження пропущено (no-op). Дозавантаження - -StartAt/-Limit." -f $existingRows)
        return
    }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$cellsTotal = 0; $rowsDone = 0; $failedBatches = 0
$batchLog = New-Object System.Collections.Generic.List[object]
for ($b = $from; $b -le $to; $b += $BatchSize) {
    $e = [Math]::Min($b + $BatchSize - 1, $to)
    $keys = New-Object System.Collections.Generic.List[string]
    $rc = New-Object System.Collections.Generic.List[object]
    $tb = [Diagnostics.Stopwatch]::StartNew()
    for ($i = $b; $i -le $e; $i++) {
        $row = $rows[$i - 1]
        if ([int]$row['r'] -ne ($i + 8)) { throw "Рядок #$i має Excel-номер $($row['r']), очікувався $($i + 8): порядок порушено." }
        $cells = Get-HseRowCells -Row $row -Maps $maps -Log $log
        $keys.Add(('r{0:0000}' -f $i)); $rc.Add($cells); $cellsTotal += $cells.Count
    }
    $prep = $tb.Elapsed.TotalSeconds
    $json = ConvertTo-HsePatchJson -PeriodKey $pk -TableInstanceId $ti -RowKeys $keys -RowCells $rc
    if ($DryRun) { Write-HseLog ("[dry] пачка {0}..{1}: комірок {2}, JSON {3} КБ" -f $b, $e, ($rc | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum, [int]($json.Length / 1KB)); continue }
    $tb.Restart()
    $r = Invoke-HseRawJson -Method PATCH -Path "/api/v1/documents/$docId/cells" -Json $json
    $sec = $tb.Elapsed.TotalSeconds
    if ($r.Status -ne 200) {
        $failedBatches++
        Write-HseLog ("ПАЧКА {0}..{1}: HTTP {2} {3}" -f $b, $e, $r.Status, $r.Text.Substring(0, [Math]::Min(600, $r.Text.Length)))
        continue
    }
    $rowsDone += ($e - $b + 1)
    $batchLog.Add([ordered]@{ from = $b; to = $e; rows = ($e - $b + 1); cells = ($rc | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum; prepSec = [math]::Round($prep, 1); patchSec = [math]::Round($sec, 1); responseKB = [int]($r.Text.Length / 1KB) })
    Write-HseLog ("пачка {0}..{1}: {2} c (підготовка {3} c), відповідь {4} КБ" -f $b, $e, [math]::Round($sec, 1), [math]::Round($prep, 1), [int]($r.Text.Length / 1KB))
    if ($b -eq $from) {
        $sample = $r.Text.Substring(0, [Math]::Min(1500, $r.Text.Length))
        [IO.File]::WriteAllText((Join-Path $out 'hse401-patch-response-sample.txt'), $sample, (New-Object Text.UTF8Encoding($false)))
    }
}
$loadSec = $sw.Elapsed.TotalSeconds
Write-HseLog ("Завантажено рядків {0}, вхідних комірок {1}, пачок з помилкою {2}; всього {3} c" -f $rowsDone, $cellsTotal, $failedBatches, [math]::Round($loadSec, 1))
Write-HseLog ("Округлень шуму: {0}; незіставлених значень: {1}" -f $log.round.Count, $log.unmapped.Count)
if ($log.round.Count -gt 0) {
    $log.round | Group-Object col | ForEach-Object { Write-HseLog ("  округлено {0}: {1}" -f $_.Name, $_.Count) }
    $csv = Join-Path $out 'hse401-roundings.csv'
    $log.round | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
    Write-HseLog "  повний лог: $csv"
}
if ($log.unmapped.Count -gt 0) {
    $log.unmapped | Group-Object col, text | Sort-Object Count -Descending | Select-Object -First 10 | ForEach-Object { Write-HseLog ("  незіставлено {0} x{1}" -f $_.Name, $_.Count) }
    $log.unmapped | Export-Csv -LiteralPath (Join-Path $out 'hse401-unmapped.csv') -NoTypeInformation -Encoding UTF8
}
$state['loadSeconds'] = [math]::Round($loadSec, 1); $state['loadRows'] = $rowsDone; $state['loadCells'] = $cellsTotal
$state['batches'] = $batchLog.ToArray()
if (-not $DryRun) { $state['standKey'] = Get-Hse401StandKey; [IO.File]::WriteAllText($stateFile, (ConvertTo-HseJson -Value $state).ToString(), (New-Object Text.UTF8Encoding($false))) }
if ($failedBatches -gt 0) { throw ("Завантаження: пачок з помилкою {0} - див. лог вище; прогін НЕ успішний." -f $failedBatches) }
