<#
.SYNOPSIS
    Крок 4 HSE401: дерево NAE зі стовпців книги (Region / On-Offshore / Area / Location / MPE) НАШИМ Excel-імпортом
    (POST /api/v1/nae-tree/import/analyse -> preview -> apply). Будує вхідний .xlsx у формі шаблону
    (Region | Area | Site | Equipment | NAE code | Name_En | ...): Area = Onshore/Offshore, Site = район (F), Equipment = об'єкт (G), NAE code = MPE (L).

.DESCRIPTION
    Ціль: Scratch (EcrHse401Scratch, localhost:5180) або Live (-Target Live, див. README). Група = англійська частина двомовного рядка словника.
    Джерело NAE = унікальна трійка (регіон, суша/море, MPE); перший рядок книги з цією трійкою задає шлях Site/Equipment
    (якщо в інших рядках шлях інший - рахується в звіті як "кілька шляхів", NAE стоїть на першому).
    Результат: out/hse401-nae-map.json (Live: hse401-nae-map.live.json; ключ "регіон|суша/море|MPE" -> id запису SRC) і звіт у консолі.

    Вилучені вузли (validTo <= сьогодні або IsActive=false) НЕ чіпаються ніколи (ні відновлення, ні перенесення, ні правка полів):
    сервер зіставляє існуючі вузли за кодом разом із вилученими, тож скрипт перед apply звіряє файл із вилученими вузлами NAE_TREE
    і при збігу ЗУПИНЯЄТЬСЯ у будь-якому режимі (-AcceptNaePreview цього НЕ скасовує).
    У Live будь-яка помилка/колізія в preview (codeTaken, existingElsewhere, ...) зупиняє роботу до apply, якщо немає -AcceptNaePreview.

    -PreviewOnly: лише analyse + preview (без apply), план -> out/hse401-preview.json, розділ "nae".
#>
[CmdletBinding()]
param([string]$ExtractFile, [switch]$DryRun,
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Xlsx.Lib.ps1')
. (Join-Path $here 'Hse401XlsxWriter.Lib.ps1')
Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
$preview = Test-Hse401Preview
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
$xlsx = Join-Path $out $(if (Test-Hse401Live) { 'hse401-nae-tree.live.xlsx' } else { 'hse401-nae-tree.xlsx' })
New-Hse401Xlsx -Path $xlsx -SheetName 'NAE' -Rows $xrows
Write-HseLog "xlsx: $xlsx ($($xrows.Count - 1) рядків)"

Connect-Hse
Add-Type -AssemblyName System.Net.Http

function Post-NaeFile([string]$what, [string]$optionsJson) {
    Assert-Hse401MethodAllowed -Method 'POST' -Path "/api/v1/nae-tree/import/$what"
    if ($what -eq 'apply' -and -not $script:EcrStandConfirmed) { throw 'Відмова: NAE apply без підтвердження стенда.' }
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

# ---------- вилучені вузли NAE_TREE (лише GET) ----------
function Get-NaeEntriesAsOf([string]$asOf) {
    $r = Invoke-Hse GET "/api/v1/registries/NAE_TREE/entries?asOf=$asOf"
    if ($r.Status -eq 404) { return $null }   # дерева ще немає
    if ($r.Status -ne 200) { throw "GET NAE_TREE/entries?asOf=$asOf -> $($r.Status) $(Get-EcrErrorCode $r)" }
    # ⛔ Кома обов'язкова: порожній масив без неї PowerShell розгортає в $null, і «усі вузли вилучені» читалося як «дерева
    # немає» - перевірка вилучених мовчки не виконувалась (репетиція 05.11, D1).
    return , @(Get-HseItems $r.Json)
}

function Find-NaeRetired {
    <# Вилучені вузли: GET entries віддає лише активні й чинні на asOf, тому вилучені (validTo <= сьогодні) ловимо зрізами на минулі дати
       (1900-01-01 і перші числа кварталів від 2010). IsActive=false у переліку не видно зовсім - їх ловить порівняння кількості
       знайдених SRC із preview.existing (див. нижче). Повертає @{ Active (id->запис, чинні сьогодні); Retired (id->запис) }. #>
    $todayStr = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $active = Get-NaeEntriesAsOf $todayStr
    $res = @{ Exists = ($null -ne $active); Active = @{}; Retired = @{}; Scans = 0 }
    if ($null -eq $active) { return $res }
    foreach ($e in $active) { $res.Active[[long]$e.id] = $e }
    $dates = New-Object System.Collections.Generic.List[string]
    $dates.Add('1900-01-01')
    $d = [datetime]'2010-01-01'
    $limit = (Get-Date).ToUniversalTime().Date
    while ($d -lt $limit) { $dates.Add($d.ToString('yyyy-MM-dd')); $d = $d.AddMonths(3) }
    foreach ($ds in $dates) {
        $res.Scans++
        $list = Get-NaeEntriesAsOf $ds
        if ($null -eq $list) { continue }
        foreach ($e in $list) {
            $id = [long]$e.id
            if (-not $res.Active.ContainsKey($id) -and -not $res.Retired.ContainsKey($id)) { $res.Retired[$id] = $e }
        }
    }
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

# ---- збіг файлу з вилученими вузлами (блокує завжди, -AcceptNaePreview не діє) ----
$fileSrcCodes = @{}      # N_<REGION>_<ONOFF>_<MPE> -> ключ джерела
$namesByLevel = @{ REGION = @{}; AREA = @{}; SITE = @{}; EQUIP = @{} }
foreach ($k in $sources.Keys) {
    $s = $sources[$k]
    $fileSrcCodes[("N_{0}_{1}_{2}" -f $s.region.ToUpperInvariant(), $s.onoff.ToUpperInvariant(), $s.mpe.ToUpperInvariant())] = $k
    $namesByLevel.REGION[$s.region.Trim().ToLowerInvariant()] = 1
    $namesByLevel.AREA[$s.onoff.Trim().ToLowerInvariant()] = 1
    if ($s.area) { $namesByLevel.SITE[$s.area.Trim().ToLowerInvariant()] = 1 }
    if ($s.facility) { $namesByLevel.EQUIP[$s.facility.Trim().ToLowerInvariant()] = 1 }
}
Write-HseLog 'NAE_TREE: пошук вилучених вузлів (лише GET, зрізи на минулі дати)...'
$ret = Find-NaeRetired
$retiredMatches = New-Object System.Collections.Generic.List[object]
$activeSrcMatches = 0
if ($ret.Exists) {
    foreach ($e in $ret.Active.Values) { if ($fileSrcCodes.ContainsKey(([string]$e.code).ToUpperInvariant())) { $activeSrcMatches++ } }
    foreach ($e in $ret.Retired.Values) {
        $lc = if ($null -ne $e.PSObject.Properties['levelCode']) { [string]$e.levelCode } else { '' }
        $hit = $false
        if ($lc -eq 'SRC' -or $lc -eq '') { if ($fileSrcCodes.ContainsKey(([string]$e.code).ToUpperInvariant())) { $hit = $true } }
        if (-not $hit -and $namesByLevel.ContainsKey($lc) -and $namesByLevel[$lc].ContainsKey(([string]$e.display).Trim().ToLowerInvariant())) { $hit = $true }
        if ($hit) {
            $vt = if ($null -ne $e.PSObject.Properties['validTo']) { [string]$e.validTo } else { '' }
            $retiredMatches.Add([ordered]@{ levelCode = $lc; code = [string]$e.code; display = [string]$e.display; validTo = $vt; id = [long]$e.id })
        }
    }
}
$srcExisting = 0
foreach ($l in $pv.Json.levels) { if ([string]$l.levelCode -eq 'SRC') { $srcExisting = [int]$l.existing } }
$hiddenSrc = [Math]::Max(0, $srcExisting - $activeSrcMatches)
Write-HseLog ("NAE_TREE: чинних вузлів {0}, вилучених знайдено {1} (зрізів {2}); збігів файлу з вилученими: {3}; SRC preview.existing={4}, серед чинних знайдено {5}, прихованих {6}" -f $ret.Active.Count, $ret.Retired.Count, $ret.Scans, $retiredMatches.Count, $srcExisting, $activeSrcMatches, $hiddenSrc)
# Другий запобіжник (D1): сервер зіставляє вузли за кодом і серед вилучених, тож «існує» на рівні більше, ніж там чинних
# вузлів, означає перевикористання вилученого - незалежно від того, чи спрацювали зрізи вище.
$activeByLevel = @{}
foreach ($e in $ret.Active.Values) {
    $lcA = if ($null -ne $e.PSObject.Properties['levelCode']) { [string]$e.levelCode } else { '' }
    if (-not $activeByLevel.ContainsKey($lcA)) { $activeByLevel[$lcA] = 0 }
    $activeByLevel[$lcA]++
}
$levelOverflow = New-Object System.Collections.Generic.List[object]
foreach ($l in $pv.Json.levels) {
    $lcP = [string]$l.levelCode
    $act = if ($activeByLevel.ContainsKey($lcP)) { [int]$activeByLevel[$lcP] } else { 0 }
    if ([int]$l.existing -gt $act) { $levelOverflow.Add([ordered]@{ levelCode = $lcP; existing = [int]$l.existing; active = $act }) }
}
foreach ($o in $levelOverflow) { Write-HseLog ("УВАГА: рівень {0}: preview.existing={1}, а чинних вузлів рівня {2} - збіг із вилученим вузлом." -f $o.levelCode, $o.existing, $o.active) }
$retiredBlocked = ($retiredMatches.Count -gt 0 -or $hiddenSrc -gt 0 -or $levelOverflow.Count -gt 0)
if ($retiredBlocked) {
    Write-HseLog 'УВАГА: файл збігається з вилученими вузлами NAE_TREE (вони не чіпаються ні в якому режимі):'
    foreach ($m in $retiredMatches) { Write-HseLog ("  {0} {1} '{2}' validTo={3}" -f $m.levelCode, $m.code, $m.display, $m.validTo) }
    if ($hiddenSrc -gt 0) { Write-HseLog ("  + {0} SRC-вузлів, що збігаються з файлом, але не є чинними й активними (неактивні/закінчені поза зрізами)." -f $hiddenSrc) }
}

if ($preview) {
    Save-Hse401PlanSection 'nae' ([ordered]@{
            rowsRead = [int]$pv.Json.rowsRead; canApply = [bool]$pv.Json.canApply; fingerprint = [string]$pv.Json.fingerprint
            levels = @($pv.Json.levels | ForEach-Object { [ordered]@{ levelCode = [string]$_.levelCode; create = [int]$_.create; existing = [int]$_.existing } })
            errors = @($errors | ForEach-Object { [ordered]@{ row = [int]$_.row; code = [string]$_.code; naeCode = [string]$_.naeCode; detail = [string]$_.detail; source = [string]$rowToKey[[int]$_.row] } })
            warnings = $warns.Count; droppedIssues = [int]$pv.Json.droppedIssues
            retiredMatches = $retiredMatches.ToArray(); hiddenExistingSrc = $hiddenSrc; levelOverflow = $levelOverflow.ToArray(); retiredBlocked = $retiredBlocked
            retiredScan = [ordered]@{ activeNodes = $ret.Active.Count; retiredFound = $ret.Retired.Count; scans = $ret.Scans }
        })
    Write-HseLog 'NAE: PreviewOnly - apply пропущено, план записано.'
    return
}
if ($DryRun) { Write-HseLog 'DryRun: застосування пропущено.'; $unplaced | Select-Object -First 20 | Format-Table -AutoSize | Out-String | Write-Host; return }

if ($retiredBlocked) { throw 'NAE: файл збігається з вилученими вузлами NAE_TREE - apply не виконується (-AcceptNaePreview цього не обходить). Розберіться з вузлами вручну.' }
if ((Test-Hse401Live) -and $errors.Count -gt 0 -and -not $script:Hse401Target.AcceptNaePreview) {
    $errors | Select-Object -First 20 | ForEach-Object { Write-HseLog ("  ПОМИЛКА рядок {0} {1} {2} {3}" -f $_.row, $_.code, $_.naeCode, $_.detail) }
    throw "NAE (Live): preview містить помилок/колізій кодів: $($errors.Count). apply зупинено; перегляньте список вище (або -AcceptNaePreview, щоб прийняти виключення проблемних рядків)."
}

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
$mapJson = [ordered]@{ standKey = (Get-Hse401StandKey); map = $mapOut; unplaced = @($notFound); errors = @($unplaced | ForEach-Object { [ordered]@{ row = $_.row; code = $_.code; detail = $_.detail; source = $_.source } }); multiPath = @($multi | ForEach-Object { "$($_.region)|$($_.onoff)|$($_.mpe)" }) }
[IO.File]::WriteAllText((Get-Hse401NaeMapFile), (ConvertTo-HseJson -Value $mapJson).ToString(), (New-Object Text.UTF8Encoding($false)))
Write-HseLog ("SRC-джерел створено/знайдено в карті: {0} з {1}; не розміщено: {2}" -f $mapOut.Count, $sources.Count, ($sources.Count - $mapOut.Count))
foreach ($u in ($unplaced | Select-Object -First 15)) { Write-HseLog ("  не розміщено: рядок {0} {1} {2}" -f $u.row, $u.code, $u.source) }
