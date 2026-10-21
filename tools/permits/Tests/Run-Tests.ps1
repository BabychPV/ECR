# tools/permits/Tests/Run-Tests.ps1
# Stand-less tests of Permits.Excel.Lib.ps1 (parser + mapping). Plain asserts, no Pester (not guaranteed on the machine).
# Exit code = number of failed assertions (0 = green). Run: powershell -NoProfile -File tools\permits\Tests\Run-Tests.ps1
# The fixture workbooks are generated into a temp folder (System.IO.Compression), so no real permit data is needed.

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here '..\Permits.Excel.Lib.ps1')
Set-StrictMode -Off

$script:Fail = 0
$script:Pass = 0
function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ("$Expected" -ceq "$Actual") { $script:Pass++ }
    else { $script:Fail++; Write-Host "FAIL: $Name (expected '$Expected', actual '$Actual')" -ForegroundColor Red }
}
function Assert-True {
    param([bool]$Condition, [string]$Name)
    if ($Condition) { $script:Pass++ } else { $script:Fail++; Write-Host "FAIL: $Name" -ForegroundColor Red }
}
function Assert-Throws {
    param([scriptblock]$Code, [string]$Name)
    $threw = $false
    try { & $Code } catch { $threw = $true }
    Assert-True $threw $Name
}

# ---------------------------------------------------------------- minimal xlsx writer (fixtures)

function New-TestWorkbook {
    # $Sheets: array of @{ Name; Cells = ordered "A1" -> string | double }
    param([string]$Path, $Sheets)
    $shared = New-Object System.Collections.Generic.List[string]
    function Get-SsIndex($s) { $i = $shared.IndexOf($s); if ($i -lt 0) { $shared.Add($s); $i = $shared.Count - 1 }; return $i }
    $sheetXml = @()
    foreach ($sh in $Sheets) {
        $byRow = @{}
        foreach ($ref in $sh.Cells.Keys) {
            $m = [regex]::Match($ref, '^([A-Z]+)(\d+)$')
            $r = [int]$m.Groups[2].Value
            if (-not $byRow.ContainsKey($r)) { $byRow[$r] = New-Object System.Collections.ArrayList }
            [void]$byRow[$r].Add(@{ Ref = $ref; Val = $sh.Cells[$ref]; Col = (ConvertTo-ColumnIndex $m.Groups[1].Value) })
        }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>')
        foreach ($r in ($byRow.Keys | Sort-Object)) {
            [void]$sb.Append("<row r=`"$r`">")
            foreach ($c in ($byRow[$r] | Sort-Object { $_.Col })) {
                $v = $c.Val
                if ($v -is [string]) { [void]$sb.Append("<c r=`"$($c.Ref)`" t=`"s`"><v>$(Get-SsIndex $v)</v></c>") }
                else { [void]$sb.Append("<c r=`"$($c.Ref)`"><v>$([double]$v)</v></c>") }
            }
            [void]$sb.Append('</row>')
        }
        [void]$sb.Append('</sheetData></worksheet>')
        $sheetXml += $sb.ToString()
    }
    if (Test-Path $Path) { Remove-Item $Path -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($Path, 'Create')
    function Add-Entry($name, $text) {
        $e = $zip.CreateEntry($name)
        $w = New-Object System.IO.StreamWriter($e.Open(), (New-Object System.Text.UTF8Encoding($false)))
        $w.Write($text); $w.Dispose()
    }
    $wbSheets = ''; $rels = ''
    for ($i = 0; $i -lt $Sheets.Count; $i++) {
        $n = $i + 1
        $nm = [System.Security.SecurityElement]::Escape($Sheets[$i].Name)
        $wbSheets += "<sheet name=`"$nm`" sheetId=`"$n`" r:id=`"rId$n`"/>"
        $rels += "<Relationship Id=`"rId$n`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet`" Target=`"worksheets/sheet$n.xml`"/>"
        Add-Entry "xl/worksheets/sheet$n.xml" $sheetXml[$i]
    }
    Add-Entry 'xl/workbook.xml' ('<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>' + $wbSheets + '</sheets></workbook>')
    Add-Entry 'xl/_rels/workbook.xml.rels' ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + $rels + '</Relationships>')
    $ss = '<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
    foreach ($s in $shared) { $ss += '<si><t>' + [System.Security.SecurityElement]::Escape($s) + '</t></si>' }
    Add-Entry 'xl/sharedStrings.xml' ($ss + '</sst>')
    $zip.Dispose()
}

function New-AirSheet {
    # 2026-04-29 05:00 = serial 46141.2083333333 (as in the real books)
    param([string]$Name, [string]$Number, $Rows, [double]$Start = 46141.2083333333, [double]$End = 46387.2083333333)
    $c = [ordered]@{
        A3 = 'Permit Number'; B3 = $Number
        A5 = 'Industrial Facility Name En'; B5 = 'Industrial Facility Name Ru'; C5 = 'Start Date'; E5 = 'End Date'; F5 = 'Actual End Date'; H5 = 'Issue Date'
        A6 = 'Facility X'; B6 = 'Facility X ru'; C6 = $Start; E6 = $End; F6 = $End; H6 = $Start
        A9 = 'Substance (En)'; B9 = 'Substance (Ru)'; C9 = 'Substance Code'; E9 = 'Site (En)'; G9 = 'Site (Ru)'
        I9 = 'Emission (t/year)'; J9 = 'Emission (mg/nm3)'; K9 = 'Emission (g/sec)'
    }
    $r = 10
    foreach ($row in $Rows) {
        $c["A$r"] = $row.SubEn; $c["B$r"] = $row.SubRu; $c["C$r"] = $row.Code
        if ($row.SiteEn) { $c["E$r"] = $row.SiteEn }
        if ($row.SiteRu) { $c["G$r"] = $row.SiteRu }
        $c["I$r"] = $row.T; $c["J$r"] = $row.Mg; $c["K$r"] = $row.G
        $r++
    }
    return @{ Name = $Name; Cells = $c }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('permits-tests-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $tmp)
try {
    # ------------------------------------------------------------ scalar helpers
    Assert-Equal '2026-04-29' (ConvertTo-IsoDate (ConvertFrom-ExcelSerialDate '46141.2083333333')) 'serial date drops the 05:00 time'
    Assert-Equal '2026-12-31' (ConvertTo-IsoDate (ConvertFrom-ExcelSerialDate '46387.2083333333')) 'serial date end'
    Assert-True ($null -eq (ConvertFrom-ExcelSerialDate '')) 'empty serial -> null'
    Assert-Equal '2026-04-29' (ConvertTo-IsoDate (ConvertFrom-JsonDate ([datetime]'2026-04-29'))) 'JSON date as datetime (PS 5.1) -> ISO'
    Assert-Equal '2026-04-29' (ConvertTo-IsoDate (ConvertFrom-JsonDate '2026-04-29')) 'JSON date as text (PS 7) -> ISO'
    Assert-True ($null -eq (ConvertFrom-JsonDate $null)) 'JSON null date -> null'
    Assert-True ($null -eq (ConvertFrom-ExcelSerialDate 'abc')) 'text serial -> null'

    # End Date is INCLUSIVE, ValidTo is the first non-valid day
    Assert-Equal '2027-01-01' (ConvertTo-IsoDate (Get-ValidTo ([datetime]'2026-12-31'))) 'ValidTo = End + 1 day (year end)'
    Assert-Equal '2026-04-10' (ConvertTo-IsoDate (Get-ValidTo ([datetime]'2026-04-09'))) 'ValidTo = End + 1 day (mid-year end)'
    Assert-Equal '2026-03-01' (ConvertTo-IsoDate (Get-ValidTo ([datetime]'2026-02-28'))) 'ValidTo crosses month end'
    Assert-Equal '2026-12-31' (ConvertTo-IsoDate (Get-ValidTo ([datetime]'2026-12-31') -AddDays 0)) 'ValidTo offset is a parameter'

    Assert-Equal '16.333' (ConvertTo-DecimalText '16.3329999999999984') 'excel double noise -> 15 significant digits'
    Assert-Equal '1.5' (ConvertTo-DecimalText '1,5') 'comma decimal'
    Assert-Equal '0.0000176' (ConvertTo-DecimalText '1.76E-05') 'exponent'
    Assert-Equal '0' (ConvertTo-DecimalText '0') 'zero is a value'
    Assert-True ($null -eq (ConvertTo-DecimalText '-')) 'dash -> null'
    Assert-True ($null -eq (ConvertTo-DecimalText '')) 'empty -> null'
    Assert-Throws { ConvertTo-DecimalText 'n/a' } 'unparsable decimal throws'

    Assert-Equal 'KZ06TEST0006_2023-2030' (ConvertTo-PermitNumber 'KZ06TEST0006_2023-2030') 'number kept as in the book'
    Assert-Equal 'KZ06TEST0006_2023_2030' (ConvertTo-PermitNumber 'KZ06TEST0006_2023_2030') 'underscore kept by default'
    Assert-Equal 'KZ06TEST0006_2023-2030' (ConvertTo-PermitNumber 'KZ06TEST0006_2023_2030' -DashUnderscoreEquivalence) 'dash/underscore option'
    Assert-Equal 'KZ02TEST0002_2026' (ConvertTo-PermitNumber 'KZ02TEST0002_2026' -DashUnderscoreEquivalence) 'single year suffix untouched'
    Assert-True ($null -eq (ConvertTo-PermitNumber 'AF_1234' -StripPrefixes @('^AF_'))) 'exclusion list: AF_ prefix'
    Assert-True ($null -eq (ConvertTo-PermitNumber 'permit_901' -StripPrefixes @('^permit_\d{3}$'))) 'exclusion list: permit_00N'
    Assert-Equal 'N/A' (ConvertTo-PermitNumber 'N/A') 'service number kept when not excluded'

    Assert-True (Test-ExcludedTestValue 'QWWE' @('qwwe', 'Test_Water')) 'marker match is case-insensitive'
    Assert-True (-not (Test-ExcludedTestValue 'qwwe2' @('qwwe'))) 'marker is an exact match'
    Assert-Equal 'a|permit|2026-01-01' (Get-IdempotencyKey 'A' 'PERMIT' '2026-01-01') 'idempotency key (type, number, ValidFrom) lower-cased'
    Assert-Equal 'Water_body' (ConvertTo-ColumnCode 'Water body') 'column code from header'
    Assert-Equal 'C1x' (ConvertTo-ColumnCode '1x') 'column code starting with a digit'

    $chunks = Split-IntoChunks -Items (1..1201) -Size 500
    Assert-Equal 3 $chunks.Count 'chunks of 1201 rows by 500'
    Assert-Equal '500,500,201' (($chunks | ForEach-Object { $_.Count }) -join ',') 'chunk sizes'
    Assert-Equal 0 (Split-IntoChunks -Items @() -Size 500).Count 'no rows -> no chunks'

    # ------------------------------------------------------------ Air workbook: dates, test rows, duplicates, fallbacks
    $rowsOk = @(
        @{ SubEn = 'NO2'; SubRu = 'x'; Code = '301'; SiteEn = 'Site A'; SiteRu = 'sa'; T = 0.6669955; Mg = 0; G = 0.4566289 },
        @{ SubEn = 'NO2'; SubRu = 'x'; Code = '301'; SiteEn = 'Site B'; SiteRu = 'sb'; T = 0.0004104; Mg = 75435.958; G = 0.00684 },
        @{ SubEn = 'qwwe row'; SubRu = 'x'; Code = 'qwwe'; SiteEn = 'Site A'; SiteRu = 'sa'; T = 1; Mg = 1; G = 1 },
        @{ SubEn = 'Soot'; SubRu = 'y'; Code = '328'; SiteEn = $null; SiteRu = 'only ru'; T = 3.75E-05; Mg = 0; G = 0 }
    )
    $air = Join-Path $tmp 'PermitsInfo.Year.xlsx'
    New-TestWorkbook -Path $air -Sheets @(
        @{ Name = 'Document map'; Cells = [ordered]@{ A1 = 'map' } },
        (New-AirSheet 'P1_Air' 'KZ01TEST0001' $rowsOk),
        (New-AirSheet 'NA_Air' 'N/A' @()),
        (New-AirSheet 'DUP_Air' 'KZ02TEST0002' @(
                @{ SubEn = 'a'; SubRu = 'a'; Code = '10'; SiteEn = 'S'; SiteRu = 's'; T = 1; Mg = 0; G = 0 },
                @{ SubEn = 'b'; SubRu = 'b'; Code = '10'; SiteEn = ' s '; SiteRu = 's'; T = 2; Mg = 0; G = 0 }))
    )
    $opts = Get-DefaultLoadOptions
    $plan = Build-LoadPlan -SourceDir $tmp -Books @($air) -Only @('AIR') -Options $opts
    $p1 = $plan.Plans | Where-Object { $_.Number -eq 'KZ01TEST0001' }
    Assert-Equal 'Ready' $p1.Status 'air permit is ready'
    Assert-Equal '2026-04-29' (ConvertTo-IsoDate $p1.ValidFrom) 'air ValidFrom'
    Assert-Equal '2027-01-01' (ConvertTo-IsoDate $p1.ValidTo) 'air ValidTo = End (2026-12-31) + 1 day'
    Assert-Equal 'Facility X' $p1.Location 'air location = facility name (En)'
    Assert-Equal 3 $p1.Rows.Count 'air rows without the test row'
    Assert-Equal 1 $p1.TestExcluded 'test row counted'
    Assert-True ($null -eq $p1.ActualEnd) 'actual end equal to end -> not stored'
    Assert-Equal 'only ru' $p1.Rows[2].Values['Site_En'] 'empty Site (En) falls back to Site (Ru)'
    Assert-Equal '0' $p1.Rows[0].Values['Emission_mg_nm3'] 'zero stays a value'
    Assert-Equal '0.0000375' $p1.Rows[2].Values['Emission_t_year'] 'exponent decimal mapped'
    Assert-True (-not $p1.Rows[0].Values.Contains('PERMIT')) 'PERMIT is added by the batch builder only'

    $na = $plan.Plans | Where-Object { $_.Number -eq 'N/A' }
    Assert-Equal 'Ready' $na.Status 'service sheet N/A is loaded (no rows)'
    Assert-Equal 0 $na.Rows.Count 'service sheet has no rows'
    $dup = $plan.Plans | Where-Object { $_.Number -eq 'KZ02TEST0002' }
    Assert-Equal 'Invalid' $dup.Status 'duplicate key (NFC/case/space-insensitive) invalidates the permit'
    Assert-Equal 1 $dup.Rejected['duplicateKey'] 'duplicate counted'

    $optsKeep = Get-DefaultLoadOptions
    $optsKeep.ExcludeTestRows = $false
    $planKeep = Build-LoadPlan -SourceDir $tmp -Books @($air) -Only @('AIR') -Options $optsKeep
    Assert-Equal 4 (($planKeep.Plans | Where-Object { $_.Number -eq 'KZ01TEST0001' }).Rows.Count) 'ExcludeTestRows off keeps the test row'

    $optsEx = Get-DefaultLoadOptions
    $optsEx.ExcludeNumberPatterns = @('^N/A$')
    $planEx = Build-LoadPlan -SourceDir $tmp -Books @($air) -Only @('AIR') -Options $optsEx
    Assert-Equal 'Excluded' (($planEx.Plans | Where-Object { $_.SheetName -eq 'NA_Air' }).Status) 'number exclusion list works'

    # ------------------------------------------------------------ batch payload
    $items = ConvertTo-BatchItems -Rows $p1.Rows -PermitId 77
    Assert-Equal 3 $items.Count 'batch items'
    Assert-True ($null -eq $items[0].code) 'batch code is null (CodeMode Auto)'
    Assert-Equal 'upsert' $items[0].op 'batch op'
    Assert-Equal '77' $items[0].values['PERMIT'] 'PERMIT = header id'

    # ------------------------------------------------------------ type content from config
    $cfg = (Get-PermitTypeConfigs)['WATER_DISCHARGE']
    $content = ConvertTo-TypeContent $cfg
    $cols = $content.tables[0].columns
    $keyCols = @($cols | Where-Object { $_['key'] -eq $true })
    $monthCols = @($cols | Where-Object { $_['group'] -eq 'Months' })
    Assert-Equal 1 $keyCols.Count 'discharge type has one key column (Event_Frame)'
    Assert-Equal 12 $monthCols.Count 'twelve month columns in group Months'
    Assert-True ($null -eq $cols[0].unitId) 'units are not set'
    Assert-True ($null -eq $cols[0]['group']) 'a column without a group sends group = null, not an empty string (API: groupCodeInvalid)'
    Assert-True ((Get-PermitTypeConfigs).Count -eq 5) 'five permit types'

    # ------------------------------------------------------------ Waste: dates only in the R1 text
    $waste = Join-Path $tmp 'PermitsInfoWaste.Year.xlsx'
    New-TestWorkbook -Path $waste -Sheets @(
        @{ Name = 'Document map'; Cells = [ordered]@{ A1 = 'map' } },
        @{ Name = 'KZ03WASTE'; Cells = [ordered]@{
                A1 = 'Permit / label: KZ03WASTE   |   KZ03WASTE   |   29.04.2026 - 31.12.2026   |   total: 4.0 t/year'
                A2 = "Platform`nP"; B2 = "Place`nP"; C2 = "Waste code`nC"; D2 = "Waste type (EN)`nX"; E2 = "Waste type (RU)`nX"; F2 = "Category`nK"; G2 = "Permitted, t/year`nR"
                A3 = 'Plat'; B3 = 'Storage'; C3 = '20 03 01'; D3 = 'Domestic'; E3 = 'ru'; G3 = 0.2271
                A4 = 'Plat'; B4 = 'Storage'; C4 = '08 01 11*'; D4 = 'Paint'; E4 = 'ru'; G4 = 3.811 } },
        @{ Name = 'Sheet2'; Cells = [ordered]@{ A1 = 'Platform' } }
    )
    $wplan = Build-LoadPlan -SourceDir $tmp -Books @($waste) -Only @('WASTE') -Options (Get-DefaultLoadOptions)
    Assert-Equal 1 @($wplan.Plans).Count 'waste: one permit (Sheet2 and the map are not permits)'
    $w = $wplan.Plans[0]
    Assert-Equal 'KZ03WASTE' $w.Number 'waste number from the R1 text'
    Assert-Equal '2026-04-29' (ConvertTo-IsoDate $w.ValidFrom) 'waste ValidFrom from the text'
    Assert-Equal '2027-01-01' (ConvertTo-IsoDate $w.ValidTo) 'waste ValidTo = text end + 1 day'
    Assert-Equal 2 $w.Rows.Count 'waste rows'

    # ------------------------------------------------------------ header Permit remap: matching + lookupRemaps JSON
    $srcE = @(
        [pscustomobject]@{ Id = 11; Code = 'KZ01TEST0001'; Docs = 3 },
        [pscustomobject]@{ Id = 12; Code = 'KZ06TEST0006_2023_2030'; Docs = 5 },
        [pscustomobject]@{ Id = 13; Code = 'permit_901'; Docs = 0 },
        [pscustomobject]@{ Id = 14; Code = 'KZ09TEST0009'; Docs = 2 })
    $tgtE = @(
        [pscustomobject]@{ Id = 101; Num = 'KZ01TEST0001'; IsActive = $true },
        [pscustomobject]@{ Id = 102; Num = 'KZ06TEST0006_2023-2030'; IsActive = $true },
        [pscustomobject]@{ Id = 103; Num = 'permit_901'; IsActive = $true })
    $hp = Resolve-HeaderPermitMap -Source $srcE -Target $tgtE -Normalize $true
    Assert-Equal 3 $hp.Counters.matched 'remap: three entries matched'
    Assert-Equal 1 $hp.Counters.noMatch 'remap: one entry without a target'
    Assert-Equal 1 $hp.Counters.normalized 'remap: only the dash/underscore number counts as normalized'
    Assert-Equal 2 $hp.Counters.docsBlocked 'remap: documents of the unmatched entry are blocked'
    Assert-Equal 102 ($hp.Map | Where-Object { $_.oldId -eq 12 }).newId 'remap: dash/underscore entry maps to its target id'
    $hpStrict = Resolve-HeaderPermitMap -Source $srcE -Target $tgtE -Normalize $false
    Assert-Equal 2 $hpStrict.Counters.matched 'remap: without normalization the underscore number does not match'
    $doc = ConvertTo-LookupRemapsDocument -Map $hp.Map -Database 'EcrTestDb' -GeneratedAt ([datetime]'2026-10-21T12:00:00Z')
    $json = $doc | ConvertTo-Json -Depth 8
    $obj = $json | ConvertFrom-Json
    Assert-Equal 1 @($obj.lookupRemaps).Count 'lookupRemaps: one target'
    Assert-Equal 'header' $obj.lookupRemaps[0].target.kind 'lookupRemaps: target.kind'
    Assert-Equal 'Permit' $obj.lookupRemaps[0].target.code 'lookupRemaps: target.code'
    Assert-Equal 2 @($obj.lookupRemaps[0].mappings).Count 'lookupRemaps: only USED matched entries are mapped (docs > 0)'
    Assert-Equal '11:101,12:102' ((@($obj.lookupRemaps[0].mappings) | ForEach-Object { "$($_.from):$($_.to)" }) -join ',') 'lookupRemaps: from/to pairs'
    Assert-Equal 'EcrTestDb' $obj.meta.database 'lookupRemaps: meta.database'
    Assert-Equal 1 $obj.meta.normalized 'lookupRemaps: meta.normalized'
    Assert-Equal 3 $obj.meta.docsPerFromId.'11' 'lookupRemaps: meta.docsPerFromId'
    Assert-True ($null -ne $obj.meta.generatedAt) 'lookupRemaps: meta.generatedAt'
    Assert-True ($null -eq $obj.meta.PSObject.Properties['codesPerFromId']) 'lookupRemaps: codes are written only with -IncludeCodes'
    Assert-True ($json -notmatch 'KZ0') 'lookupRemaps: no permit number in the default file'
    $withCodes = ConvertTo-LookupRemapsDocument -Map $hp.Map -Database 'EcrTestDb' -IncludeCodes
    Assert-True ($null -ne $withCodes.meta['codesPerFromId']) 'lookupRemaps: -IncludeCodes adds codesPerFromId'
    # ------------------------------------------------------------ service entries: dates are never invented
    $svcFile = Join-Path $tmp 'svc.csv'
    [IO.File]::WriteAllText($svcFile, "# comment line: dates pending confirmation`nnumber;location;validFrom;validTo`npermit_901;;;`nAF_x;loc;2025-01-01;2026-01-01`n", (New-Object System.Text.UTF8Encoding($false)))
    $svc = Read-ServiceEntries -Path $svcFile
    Assert-Equal 2 @($svc).Count 'comment line of the service file is skipped'
    $sp = Build-LoadPlan -SourceDir $tmp -Books @($air) -Only @('AIR') -Options (Get-DefaultLoadOptions) -ServiceEntries $svc
    $s1 = $sp.Plans | Where-Object { $_.Number -eq 'permit_901' }
    $s2 = $sp.Plans | Where-Object { $_.Number -eq 'AF_x' }
    Assert-Equal 'Skipped' $s1.Status 'service entry without dates is skipped, not invented'
    Assert-Equal 'no-dates' $s1.Reason 'service entry reason'
    Assert-Equal 'Ready' $s2.Status 'service entry with dates is ready'
    Assert-Equal 'service: Land dictionary only' $s1.Location 'service entry without a location gets the default label'
    Assert-Equal 'loc' $s2.Location 'service entry keeps its own location'
    Assert-Equal '2026-01-01' (ConvertTo-IsoDate $s2.ValidTo) 'service entry dates are taken as given (exclusive end)'
    Assert-Equal 0 $s2.Rows.Count 'service entry has no rows'
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ("Permits tests: passed={0} failed={1}" -f $script:Pass, $script:Fail)
exit $script:Fail
