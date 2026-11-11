# tools/hse400/Compare-Hse400Year.ps1
# HSE400 крок 4 - приймання проти звіту прода HSE400.Year (план HSE400 (d)):
#   1. вивантажує аркуш «GasComposition <рік>» КОПІЇ звіту (hse400_year_dump.py, Python + openpyxl) у out/;
#   2. зіставляє рядки звіту з моделлю шаблону (Hse400Year.Lib.ps1; будь-яка проблема зіставлення - відмова);
#   3. -Fill: пише вхідні рядки звіту в робочий документ HSE400_<рік> (PATCH /documents/{id}/cells);
#   4. -Recalc: перерахунок документа (POST /documents/{id}/recalculate) і очікування задачі;
#   5. читає розрахункові рядки документа й звіряє кожну комірку зі звітом: відносна похибка ≤ 1e-9, біля нуля
#      абсолютна ≤ 1e-12. Розбіжності - у out/hse400-year-diffs.<база>.csv (таблиця, рядок, рядок звіту, місяць,
#      звіт, документ, відносна похибка); у вивід - лише кількості й адреси розбіжних комірок.
# ⛔ Значення звіту - дані замовника: лише в out/ (у .gitignore). Оригінал звіту не відкривається - лише копія.
# Стенд - як у Build-Hse400Template.ps1: лише локальний, -ConfirmStand <база>, -CredentialFile поза git; документ і
# екземпляри таблиць - з файлу стану будівника. Облікових записів, ролей і баз не створює.
# Код виходу: 0 - усе збіглося; 1 - є розбіжності; 2 - зіставлення не вдалося.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workbook,
    [int]$Year = 2026,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [Parameter(Mandatory)][string]$CredentialFile,
    [switch]$Fill,
    [switch]$Recalc,
    [int]$Months = 12,
    [string]$Python = 'py'
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Hse400Stand.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Model.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Methodology.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Year.Lib.ps1')

$wb = [IO.Path]::GetFullPath($Workbook)
if (-not (Test-Path -LiteralPath $wb -PathType Leaf)) { throw "Немає файлу звіту: $wb" }
if (Test-Hse400InsideGit $wb) { throw 'Копія звіту лежить усередині git-worktree: дані замовника не мають бути поруч із репозиторієм. Відмова.' }

Set-Hse400Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:(-not ($Fill -or $Recalc))
$out = Get-Hse400OutDir
$db = $Database.Trim().ToLowerInvariant()

# 1-2. Звіт -> зіставлені рядки.
$dump = Join-Path $out ("hse400-year-{0}.{1}.json" -f $Year, $db)
& $Python -E (Join-Path $PSScriptRoot 'hse400_year_dump.py') $wb ([string]$Year) $dump
if ($LASTEXITCODE -ne 0) { throw "hse400_year_dump.py: код $LASTEXITCODE" }
$sheet = [IO.File]::ReadAllText($dump, [Text.Encoding]::UTF8) | ConvertFrom-Json
$model = Read-Hse400Model
$join = Join-Hse400YearRows -Model $model -Blocks (Split-Hse400YearBlocks $sheet.rows)
if (@($join.Problems).Count) {
    foreach ($p in $join.Problems) { Write-Warning $p }
    Write-Hse400Log "Зіставлення не вдалося: $(@($join.Problems).Count) проблем."
    exit 2
}
$items = @($join.Items)
Write-Hse400Log ("Зіставлено рядків: {0} (ввід {1}, розрахункових {2}); місяців у звіті: {3}." -f $items.Count,
    @($items | Where-Object Kind -eq 'Input').Count, @($items | Where-Object Kind -eq 'Calc').Count, @($sheet.months | Where-Object { $_ }).Count)

# 3-5. Документ.
Connect-Hse400
$state = Read-Hse400State
foreach ($k in 'documentId', 'periodKey', 'tableInstances') { if (-not $state.ContainsKey($k)) { throw "У файлі стану немає '$k': спершу Build-Hse400Template.ps1 -Stage Project." } }
$doc = [long]$state['documentId']; $pk = [int]$state['periodKey']; $inst = $state['tableInstances']

function Get-Slice([string]$table) {
    return (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents/{0}/tables/{1}?periodKey={2}" -f $doc, $inst[$table], $pk)).Json
}

if ($Fill) {
    $writes = Get-Hse400YearInputRows -Items $items -Months $Months
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
    $job = [string]$r.Json.jobId
    $st = $null
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
$cmp = Compare-Hse400YearResults -Items $items -Actual $actual -Months $Months
$csv = Join-Path $out ("hse400-year-diffs.{0}.csv" -f $db)
$cmp.Diffs | Select-Object Table, RowKey, SourceRow, Month, Expected, Actual, RelErr | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
Write-Hse400Log ("Звірено комірок: {0}; збіглося {1}; розбіжних {2} (деталі: {3})." -f $cmp.Compared, $cmp.Matched, @($cmp.Diffs).Count, $csv)
foreach ($g in @($cmp.Diffs | Group-Object Table, RowKey)) {
    $months = @($g.Group | ForEach-Object { $_.Month }) -join ','
    Write-Host ("  {0}: {1}" -f $g.Name, $months)
}
if (@($cmp.Diffs).Count) { exit 1 }
exit 0
