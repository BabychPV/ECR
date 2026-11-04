<#
.SYNOPSIS
    Крок 4 HSE401: дерево NAE зі стовпців книги (Region / On-Offshore / Area / Location / MPE) НАШИМ Excel-імпортом
    (POST /api/v1/nae-tree/import/analyse -> preview -> apply). Будує вхідний .xlsx у формі шаблону
    (Region | Area | Site | Equipment | NAE code | Name_En | ...): Area = Onshore/Offshore, Site = район (F), Equipment = об'єкт (G), NAE code = MPE (L).

.DESCRIPTION
    Лише scratch EcrHse401Scratch (localhost:5180). Група = англійська частина двомовного рядка словника.
    Джерело NAE = унікальна трійка (регіон, суша/море, MPE); перший рядок книги з цією трійкою задає шлях Site/Equipment
    (якщо в інших рядках шлях інший - рахується в звіті як "кілька шляхів", NAE стоїть на першому).
    Результат: out/hse401-nae-map.json (ключ "регіон|суша/море|MPE" -> id запису SRC) і звіт у консолі.
#>
[CmdletBinding()]
param([string]$ExtractFile, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Xlsx.Lib.ps1')
. (Join-Path $here 'Hse401XlsxWriter.Lib.ps1')
$out = Get-Hse401OutDir
if (-not $ExtractFile) { $ExtractFile = Join-Path $out 'hse401-extract.json' }

function ConvertTo-NaeCode([string]$raw) {
    $t = ([string]$raw -replace '\s+', ' ').Trim()
    if ($t -match '^[0-9]+$' -and $t.Length -lt 4) { return $t.PadLeft(4, '0') }
    return $t
}
function En([string]$s) { $b = Split-HseBilingual $s; if ($b.En) { return $b.En } else { return $s.Trim() } }

$data = Read-HseJson $ExtractFile
$sources = [ordered]@{}     # ключ -> @{ region; onoff; area; facility; mpe; name; paths; firstRow }
foreach ($row in $data['rows']) {
    $v = $row['v']
    if (-not ($v['C'] -and $v['E'] -and $v['L'])) { continue }
    $mpe = ConvertTo-NaeCode $v['L']
    $key = "$(En $v['C'])|$(En $v['E'])|$mpe"
    $path = "$(En $(if ($v['F']) { $v['F'] } else { '' }))|$(En $(if ($v['G']) { $v['G'] } else { '' }))"
    if (-not $sources.Contains($key)) {
        $sources[$key] = @{ region = (En $v['C']); onoff = (En $v['E']); area = $(if ($v['F']) { En $v['F'] } else { $null }); facility = $(if ($v['G']) { En $v['G'] } else { $null }); mpe = $mpe; name = $(if ($v['D']) { ($v['D'] -replace '\s+', ' ').Trim() } else { $null }); paths = @{}; firstRow = $row['r'] }
    }
    $s = $sources[$key]; $s.paths[$path] = 1
    if (-not $s.name -and $v['D']) { $s.name = ($v['D'] -replace '\s+', ' ').Trim() }
}
$multi = @($sources.Values | Where-Object { $_.paths.Count -gt 1 })
Write-HseLog ("Джерел NAE (регіон|суша/море|MPE): {0}; із кількома шляхами F|G: {1}" -f $sources.Count, $multi.Count)

$xrows = New-Object System.Collections.Generic.List[object]
$xrows.Add(@('Region', 'Area', 'Site', 'Equipment', 'NAE code', 'Name_En', 'Name_Ru', 'Name_Kz', 'On_Offshore'))
$rowToKey = @{}
$i = 1
foreach ($k in $sources.Keys) {
    $s = $sources[$k]; $i++
    $xrows.Add(@($s.region, $s.onoff, $s.area, $s.facility, $s.mpe, $s.name, $null, $null, $null))
    $rowToKey[$i] = $k
}
$xlsx = Join-Path $out 'hse401-nae-tree.xlsx'
New-Hse401Xlsx -Path $xlsx -SheetName 'NAE' -Rows $xrows
Write-HseLog "xlsx: $xlsx ($($xrows.Count - 1) рядків)"

Connect-Hse
Add-Type -AssemblyName System.Net.Http

function Post-NaeFile([string]$what, [string]$optionsJson) {
    $h = New-Object System.Net.Http.HttpClientHandler
    $h.CookieContainer = $script:EcrSession.Cookies
    $cl = New-Object System.Net.Http.HttpClient($h)
    $cl.Timeout = [TimeSpan]::FromMinutes(20)
    $mp = New-Object System.Net.Http.MultipartFormDataContent
    $bytes = [IO.File]::ReadAllBytes($xlsx)
    $fc = New-Object System.Net.Http.ByteArrayContent(, $bytes)
    $fc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')
    $mp.Add($fc, 'file', 'hse401-nae-tree.xlsx')
    if ($optionsJson) { $mp.Add((New-Object System.Net.Http.StringContent($optionsJson, [Text.Encoding]::UTF8)), 'request') }
    $resp = $cl.PostAsync((Get-Hse401BaseUrl) + "/api/v1/nae-tree/import/$what", $mp).GetAwaiter().GetResult()
    $txt = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $res = [pscustomobject]@{ Status = [int]$resp.StatusCode; Text = $txt; Json = $null }
    try { $res.Json = $txt | ConvertFrom-Json } catch { }
    $cl.Dispose()
    return $res
}

$an = Post-NaeFile 'analyse' $null
if ($an.Status -ne 200) { throw "analyse -> $($an.Status) $($an.Text)" }
$pv = Post-NaeFile 'preview' '{}'
if ($pv.Status -ne 200) { throw "preview -> $($pv.Status) $($pv.Text)" }
Write-HseLog ("preview: рядків {0}, canApply={1}, fingerprint {2}" -f $pv.Json.rowsRead, $pv.Json.canApply, $pv.Json.fingerprint)
foreach ($l in $pv.Json.levels) { Write-HseLog ("  рівень {0}: створити {1}, існує {2}" -f $l.levelCode, $l.create, $l.existing) }
$errors = @($pv.Json.issues | Where-Object { $_.severity -eq 'error' })
$warns = @($pv.Json.issues | Where-Object { $_.severity -ne 'error' })
Write-HseLog ("зауваження: помилок {0}, попереджень {1}, відкинуто понад ліміт {2}" -f $errors.Count, $warns.Count, $pv.Json.droppedIssues)
$unplaced = @()
foreach ($e in $errors) { $unplaced += [pscustomobject]@{ row = $e.row; code = $e.code; detail = $e.detail; source = $rowToKey[[int]$e.row] } }
if ($DryRun) { Write-HseLog 'DryRun: застосування пропущено.'; $unplaced | Select-Object -First 20 | Format-Table -AutoSize | Out-String | Write-Host; return }

if ($errors.Count -gt 0) {
    Write-HseLog 'Є помилки - apply відмовить (naeImportHasErrors). Виключаю проблемні рядки з файлу і повторюю.'
    $bad = @{}; foreach ($e in $errors) { $bad[[int]$e.row] = $true }
    $xrows2 = New-Object System.Collections.Generic.List[object]
    for ($r = 0; $r -lt $xrows.Count; $r++) { if (-not $bad.ContainsKey($r + 1)) { $xrows2.Add($xrows[$r]) } }
    New-Hse401Xlsx -Path $xlsx -SheetName 'NAE' -Rows $xrows2
    $pv = Post-NaeFile 'preview' '{}'
    if ($pv.Status -ne 200 -or -not $pv.Json.canApply) { throw "preview після виключення -> $($pv.Status) $($pv.Text)" }
}
$ap = Post-NaeFile 'apply' (@{ expectedFingerprint = $pv.Json.fingerprint } | ConvertTo-Json -Compress)
if ($ap.Status -ne 200) { throw "apply -> $($ap.Status) $($ap.Text)" }
Write-HseLog ("apply: applied={0}, provisioned={1}" -f $ap.Json.applied, $ap.Json.registryProvisioned)
foreach ($l in $ap.Json.levels) { Write-HseLog ("  рівень {0}: створено {1}, існувало {2}" -f $l.levelCode, $l.create, $l.existing) }

# карта SRC: ключ "регіон|суша/море|MPE" -> id запису (шукаємо за значеннями NaeCode/RegionCode/AreaCode)
$list = (Invoke-HseOk GET "/api/v1/registries/NAE_TREE/entries?level=SRC&asOf=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))").Json
$entries = @($list)
if ($entries.Count -eq 1 -and $null -ne $entries[0].PSObject.Properties['items']) { $entries = @($entries[0].items) }
Write-HseLog "SRC-записів у реєстрі NAE_TREE: $($entries.Count)"
$map = @{}
foreach ($e in $entries) { $map[([string]$e.code).ToUpperInvariant()] = [long]$e.id }
$mapOut = [ordered]@{}
$notFound = @()
foreach ($k in $sources.Keys) {
    $s = $sources[$k]
    $mk = ("N_{0}_{1}_{2}" -f $s.region.ToUpperInvariant(), $s.onoff.ToUpperInvariant(), $s.mpe.ToUpperInvariant())
    if ($map.ContainsKey($mk)) { $mapOut[$k] = $map[$mk] } else { $notFound += $k }
}
$mapJson = [ordered]@{ map = $mapOut; unplaced = @($notFound); errors = @($unplaced | ForEach-Object { [ordered]@{ row = $_.row; code = $_.code; detail = $_.detail; source = $_.source } }); multiPath = @($multi | ForEach-Object { "$($_.region)|$($_.onoff)|$($_.mpe)" }) }
[IO.File]::WriteAllText((Join-Path $out 'hse401-nae-map.json'), (ConvertTo-HseJson -Value $mapJson).ToString(), (New-Object Text.UTF8Encoding($false)))
Write-HseLog ("SRC-джерел створено/знайдено в карті: {0} з {1}; не розміщено: {2}" -f $mapOut.Count, $sources.Count, ($sources.Count - $mapOut.Count))
foreach ($u in ($unplaced | Select-Object -First 15)) { Write-HseLog ("  не розміщено: рядок {0} {1} {2}" -f $u.row, $u.code, $u.source) }
