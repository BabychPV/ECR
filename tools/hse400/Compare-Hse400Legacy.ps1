# tools/hse400/Compare-Hse400Legacy.ps1
# HSE400 етап 8: реальні входи старої системи -> документ HSE400_<рік> нової системи -> звірка з CalcResult_AirGasCompositions_HSE400.
#   вибірка - файл Export-Hse400LegacySample.ps1 (дані замовника, поза git);
#   -Fill   : пише ввід вибірки в документ (PATCH /documents/{id}/cells; нульові місяці теж - це реальний ввід старої системи);
#   -Recalc : перерахунок документа й очікування задачі (рушій - який зараз активний у версії методології);
#   далі кожна розрахункова комірка (рядок F_<X> із парою в старій системі) звіряється: відносна <= 1e-9 або абсолютна <= 1e-12.
# Розбіжності й підсумки - у -OutDir (поза git): hse400-legacy-diffs.<мітка>.csv і hse400-legacy-summary.<мітка>.json;
# у вивід - лише кількості й адреси комірок (рядок, місяць), не значення.
# Стенд - як у Build-Hse400Template.ps1 (лише локальний, -ConfirmStand = -Database, -CredentialFile поза git); стан - з out/.
# Код виходу: 0 - збіг усіх порівнюваних комірок; 1 - є розбіжності; 2 - зіставлення з моделлю не вдалося.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SampleFile,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [Parameter(Mandatory)][string]$CredentialFile,
    [string]$Label = 'run',
    [switch]$Fill,
    [switch]$Recalc,
    [string]$SqlConnection = 'Server=localhost;Database=EcrLegacyProd;Integrated Security=true;TrustServerCertificate=true;ApplicationIntent=ReadOnly'
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Hse400Stand.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Model.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Methodology.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Year.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Legacy.Lib.ps1')

$sample = [IO.Path]::GetFullPath($SampleFile)
$outDir = [IO.Path]::GetFullPath($OutDir)
if (Test-Hse400InsideGit $sample) { throw 'Вибірка лежить усередині git-worktree: дані замовника. Відмова.' }
if (Test-Hse400InsideGit $outDir) { throw 'OutDir лежить усередині git-worktree: дані замовника. Відмова.' }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
if ($SqlConnection -notmatch 'ApplicationIntent=ReadOnly') { $SqlConnection += ';ApplicationIntent=ReadOnly' }

Set-Hse400Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:(-not ($Fill -or $Recalc))
$s = [IO.File]::ReadAllText($sample, [Text.Encoding]::UTF8) | ConvertFrom-Json
$year = [int]$s.year

# Вибірка -> місяць -> {inputs, results}: числа decimal -> double (так само читає їх документ нової системи).
$inv = [Globalization.CultureInfo]::InvariantCulture
$months = @{}
foreach ($p in $s.months.PSObject.Properties) {
    $m = [int]$p.Name
    $e = @{ inputs = @{}; results = @{} }
    foreach ($q in $p.Value.inputs.PSObject.Properties) { $e.inputs[$q.Name] = [double]::Parse([string]$q.Value, $inv) }
    foreach ($q in $p.Value.results.PSObject.Properties) { $e.results[$q.Name] = [double]::Parse([string]$q.Value, $inv) }
    $months[$m] = $e
}
$maxMonth = ($months.Keys | Measure-Object -Maximum).Maximum

# Імена колонок - із самих таблиць (а не зі значень вибірки), щоб зіставлення було за повним набором.
function Get-ColumnNames([string]$table) {
    $c = New-Object System.Data.SqlClient.SqlConnection $SqlConnection; $c.Open()
    try {
        $cmd = $c.CreateCommand(); $cmd.CommandText = 'SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t)'; [void]$cmd.Parameters.AddWithValue('@t', $table)
        $r = $cmd.ExecuteReader(); $l = New-Object System.Collections.Generic.List[string]; while ($r.Read()) { $l.Add([string]$r[0]) }; $r.Close(); return $l.ToArray()
    } finally { $c.Close() }
}
$allIn = @(Get-ColumnNames 'dbo.AF_AirGasCompositions_HSE400' | Where-Object { @('EFID', 'ModifiedTime', 'Time') -notcontains $_ })
$allOut = @(Get-ColumnNames 'dbo.CalcResult_AirGasCompositions_HSE400' | Where-Object { @('ID', 'ModifiedTime', 'EFID', 'DateTime') -notcontains $_ })

$model = Read-Hse400Model
$join = Join-Hse400LegacyItems -Model $model -Months $months -InputColumns $allIn -ResultColumns $allOut
$items = @($join.Items)
Write-Hse400Log ("Зіставлено: ввід {0}, розрахункових {1}; без пари в старій системі {2}; колонок AF без рядка шаблону {3}, колонок CalcResult без рядка шаблону {4}." -f
    @($items | Where-Object Kind -eq 'Input').Count, @($items | Where-Object Kind -eq 'Calc').Count, @($join.NoPair).Count,
    @($join.UnusedInputColumns).Count, @($join.UnusedResultColumns).Count)
# Колонки CalcResult, які не є ехом вводу й не мають рядка шаблону, - помилка зіставлення (мовчки не губимо).
$orphan = @($join.UnusedResultColumns | Where-Object { $allIn -notcontains $_ })
if (@($orphan).Count) { Write-Warning ("Колонки CalcResult без рядка шаблону (не ехо вводу): {0}" -f (@($orphan) -join ', ')); exit 2 }

Connect-Hse400
$state = Read-Hse400State
foreach ($k in 'documentId', 'periodKey', 'tableInstances') { if (-not $state.ContainsKey($k)) { throw "У файлі стану немає '$k': спершу Build-Hse400Template.ps1 -Stage Project." } }
$doc = [long]$state['documentId']; $pk = [int]$state['periodKey']; $inst = $state['tableInstances']
if ([int]([string]$pk).Substring(0, 4) -ne $year) { throw "Період документа $pk не з року вибірки $year." }

function Get-Slice([string]$table) { return (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents/{0}/tables/{1}?periodKey={2}" -f $doc, $inst[$table], $pk)).Json }

if ($Fill) {
    $writes = Get-Hse400YearInputRows -Items $items -Months 12
    $cells = 0
    foreach ($table in @($writes.Keys)) {
        $slice = Get-Slice $table
        $rv = @{}; foreach ($x in @($slice.rows)) { if ($null -ne $x) { $rv[[string]$x.rowKey] = [string]$x.rowVersion } }
        $body = New-Hse400CellsPatchBody -PeriodKey $pk -TableInstanceId $inst[$table] -Rows $writes[$table] -RowVersions $rv
        Invoke-Hse400Ok -Method PATCH -Path ("/api/v1/documents/{0}/cells" -f $doc) -Body $body | Out-Null
        foreach ($r in @($writes[$table])) { $cells += @($r.cells).Count }
    }
    Write-Hse400Log "Ввід записано: комірок $cells у $(@($writes.Keys).Count) таблиць документа $doc."
}

if ($Recalc) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-Hse400Ok -Method POST -Path "/api/v1/documents/$doc/recalculate" -Body @{ periodKey = $pk }
    $job = [string]$r.Json.jobId; $st = $null
    while ($sw.Elapsed.TotalMinutes -lt 30) {
        Start-Sleep -Milliseconds 500
        $js = (Invoke-Hse400Ok -Method GET -Path ("/api/v1/jobs/{0}" -f [Uri]::EscapeDataString($job))).Json
        $st = if ($js.PSObject.Properties['effectiveState'] -and $js.effectiveState) { [string]$js.effectiveState } else { [string]$js.state }
        if ($st -in 'Succeeded', 'Failed', 'Cancelled', 'Canceled') { break }
    }
    Write-Hse400Log "Перерахунок: $st за $([int]$sw.Elapsed.TotalSeconds) с."
    if ($st -ne 'Succeeded') { throw "Перерахунок не вдався: $st" }
}

$actual = @{}
foreach ($t in @($model.tables)) { $actual[[string]$t.code] = ConvertFrom-Hse400YearSlice (Get-Slice ([string]$t.code)) }
$cmp = Compare-Hse400YearResults -Items $items -Actual $actual -Months $maxMonth
$classes = @{}
$rows = @(foreach ($d in $cmp.Diffs) {
        $cls = Get-Hse400LegacyDiffClass -Expected $d.Expected -Actual $d.Actual
        if ($classes.ContainsKey($cls)) { $classes[$cls] = $classes[$cls] + 1 } else { $classes[$cls] = 1 }
        [pscustomobject]@{ Table = $d.Table; RowKey = $d.RowKey; LegacyColumn = $d.SourceRow; Month = $d.Month; Class = $cls; Legacy = $d.Expected; New = $d.Actual; RelErr = $d.RelErr }
    })
# Тісність збігу (лише кількості): точна рівність, відносна похибка серед збіглих, ненульові комірки.
$exact = 0; $nonZero = 0; $maxRel = 0.0
foreach ($it in @($items | Where-Object Kind -eq 'Calc')) {
    for ($mn = 1; $mn -le $maxMonth; $mn++) {
        $col = 'M{0:D2}' -f $mn
        $e = $it.Values[$mn - 1]
        $a = $null
        if ($actual[$it.Table].ContainsKey($it.RowKey) -and $actual[$it.Table][$it.RowKey].ContainsKey($col)) { $a = $actual[$it.Table][$it.RowKey][$col] }
        if ($null -eq $e -or $null -eq $a) { continue }
        if ($e -ne 0) { $nonZero++ }
        if ($e -eq $a) { $exact++ }
        elseif ($e -ne 0) { $r = [Math]::Abs($e - $a) / [Math]::Max([Math]::Abs($e), [Math]::Abs($a)); if ($r -le 1e-9 -and $r -gt $maxRel) { $maxRel = $r } }
    }
}
$csv = Join-Path $outDir ("hse400-legacy-diffs.{0}.csv" -f $Label)
$rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
$byRow = @($rows | Group-Object Table, RowKey | ForEach-Object { [pscustomobject]@{ row = $_.Name; months = @($_.Group | ForEach-Object { $_.Month }) -join ','; classes = @($_.Group | ForEach-Object { $_.Class } | Sort-Object -Unique) -join ',' } })
$summary = [ordered]@{
    label = $Label; year = $year; monthsInSample = @($months.Keys | Sort-Object); monthsCompared = $maxMonth
    inputRows = @($items | Where-Object Kind -eq 'Input').Count; calcRowsPaired = @($items | Where-Object Kind -eq 'Calc').Count
    calcRowsNoPair = @($join.NoPair); compared = $cmp.Compared; matched = $cmp.Matched; differ = $rows.Count
    classes = $classes; differingRows = $byRow.Count
    exactEqual = $exact; nonZeroCompared = $nonZero; maxRelErrAmongNotExact = $maxRel
}
[IO.File]::WriteAllText((Join-Path $outDir ("hse400-legacy-summary.{0}.json" -f $Label)), ($summary | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
Write-Hse400Log ("Звірено комірок: {0}; збіглося {1}; розбіжних {2} у {3} рядках шаблону (деталі: {4})." -f $cmp.Compared, $cmp.Matched, $rows.Count, $byRow.Count, $csv)
Write-Hse400Log ("Точна рівність: {0} з {1}; ненульових порівняних {2}; найбільша відносна похибка серед неточно рівних: {3:E2}." -f $exact, $cmp.Compared, $nonZero, $maxRel)
foreach ($g in $byRow) { Write-Host ("  {0}: місяці {1} [{2}]" -f $g.row, $g.months, $g.classes) }
if ($rows.Count) { exit 1 }
exit 0
