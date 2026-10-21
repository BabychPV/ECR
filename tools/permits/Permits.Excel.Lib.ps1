# tools/permits/Permits.Excel.Lib.ps1
# Minimal read-only .xlsx reader (System.IO.Compression + XML; no external modules, no Python) and the pure
# mapping helpers of Load-Permits.ps1. Dot-source: . "$PSScriptRoot\Permits.Excel.Lib.ps1".
# Windows PowerShell 5.1 and PowerShell 7. The file is ASCII only on purpose (PS 5.1 reads BOM-less files as ANSI).
# The functions never print cell values: callers log counters only.

Set-StrictMode -Version 2.0

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:SsNs = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
$script:RelNs = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
$script:PkgRelNs = 'http://schemas.openxmlformats.org/package/2006/relationships'

function ConvertTo-ColumnIndex {
    # 'A' -> 1, 'AA' -> 27
    param([Parameter(Mandatory)][string]$Letters)
    $n = 0
    foreach ($ch in $Letters.ToUpperInvariant().ToCharArray()) { $n = $n * 26 + ([int]$ch - [int][char]'A' + 1) }
    return $n
}

function Split-CellRef {
    # 'AB12' -> @{ Col = 28; Row = 12 }
    param([Parameter(Mandatory)][string]$Ref)
    if ($Ref -notmatch '^([A-Za-z]+)(\d+)$') { throw "Bad cell reference '$Ref'." }
    return @{ Col = (ConvertTo-ColumnIndex $Matches[1]); Row = [int]$Matches[2] }
}

function Read-ZipEntryXml {
    param($Zip, [string]$Name)
    $entry = $Zip.GetEntry($Name)
    if ($null -eq $entry) { return $null }
    $stream = $entry.Open()
    try {
        $xml = New-Object System.Xml.XmlDocument
        $xml.XmlResolver = $null   # no DTD / external entity resolution
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $xml.LoadXml($reader.ReadToEnd())
        return $xml
    }
    finally { $stream.Dispose() }
}

function Open-XlsxPackage {
    <#
    .SYNOPSIS
        Opens the package read-only (shared read, so a book open in Excel still loads). Returns the ZipArchive.
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Workbook not found: $Path" }
    $fs = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    return New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Read, $false)
}

function Read-SharedStrings {
    param($Zip)
    $list = New-Object System.Collections.Generic.List[string]
    $doc = Read-ZipEntryXml $Zip 'xl/sharedStrings.xml'
    if ($null -eq $doc) { return ,$list }
    $nsm = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $nsm.AddNamespace('m', $script:SsNs)
    foreach ($si in $doc.SelectNodes('/m:sst/m:si', $nsm)) {
        # plain <t> or rich-text runs <r><t>; phonetic <rPh> runs are NOT part of the value
        $sb = New-Object System.Text.StringBuilder
        foreach ($t in $si.SelectNodes('./m:t | ./m:r/m:t', $nsm)) { [void]$sb.Append($t.InnerText) }
        $list.Add($sb.ToString())
    }
    return ,$list
}

function Get-XlsxSheetList {
    <#
    .SYNOPSIS
        Sheet names in workbook order with their part paths: @{ Name; Part }.
    #>
    param($Zip)
    $wb = Read-ZipEntryXml $Zip 'xl/workbook.xml'
    $rels = Read-ZipEntryXml $Zip 'xl/_rels/workbook.xml.rels'
    if ($null -eq $wb -or $null -eq $rels) { throw 'Not a valid workbook (no xl/workbook.xml).' }
    $nsm = New-Object System.Xml.XmlNamespaceManager($wb.NameTable)
    $nsm.AddNamespace('m', $script:SsNs)
    $nsm.AddNamespace('r', $script:RelNs)
    $rnsm = New-Object System.Xml.XmlNamespaceManager($rels.NameTable)
    $rnsm.AddNamespace('p', $script:PkgRelNs)
    $target = @{}
    foreach ($rel in $rels.SelectNodes('/p:Relationships/p:Relationship', $rnsm)) { $target[$rel.GetAttribute('Id')] = $rel.GetAttribute('Target') }
    $result = New-Object System.Collections.ArrayList
    foreach ($s in $wb.SelectNodes('/m:workbook/m:sheets/m:sheet', $nsm)) {
        $rid = $s.GetAttribute('id', $script:RelNs)
        $part = [string]$target[$rid]
        if ($part.StartsWith('/')) { $part = $part.TrimStart('/') } else { $part = 'xl/' + $part }
        [void]$result.Add(@{ Name = $s.GetAttribute('name'); Part = $part })
    }
    return ,$result
}

function Read-XlsxSheet {
    <#
    .SYNOPSIS
        Reads one sheet into a sparse grid. Returns @{ Name; Cells = hashtable "row,col" -> string; MaxRow; MaxCol }.
        Numeric cells keep the raw invariant text of <v> (no culture, no rounding). Merged regions are filled down/right
        with the top-left value when -FillMerged is set (the Air book keeps substance data in merged cells).
    #>
    param(
        [Parameter(Mandatory)]$Zip,
        [Parameter(Mandatory)][string]$Part,
        [Parameter(Mandatory)][string]$Name,
        $SharedStrings,
        [switch]$FillMerged
    )
    $doc = Read-ZipEntryXml $Zip $Part
    if ($null -eq $doc) { throw "Sheet part '$Part' not found." }
    $nsm = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $nsm.AddNamespace('m', $script:SsNs)
    $cells = @{}
    $maxRow = 0
    $maxCol = 0
    foreach ($c in $doc.SelectNodes('//m:sheetData/m:row/m:c', $nsm)) {
        $ref = Split-CellRef $c.GetAttribute('r')
        $type = $c.GetAttribute('t')
        $text = $null
        switch ($type) {
            's' {
                $v = $c.SelectSingleNode('./m:v', $nsm)
                if ($null -ne $v) { $text = $SharedStrings[[int]$v.InnerText] }
            }
            'inlineStr' {
                $sb = New-Object System.Text.StringBuilder
                foreach ($t in $c.SelectNodes('./m:is/m:t | ./m:is/m:r/m:t', $nsm)) { [void]$sb.Append($t.InnerText) }
                $text = $sb.ToString()
            }
            default {
                $v = $c.SelectSingleNode('./m:v', $nsm)
                if ($null -ne $v) { $text = $v.InnerText }
            }
        }
        if ($null -eq $text -or $text -eq '') { continue }
        $cells["$($ref.Row),$($ref.Col)"] = $text
        if ($ref.Row -gt $maxRow) { $maxRow = $ref.Row }
        if ($ref.Col -gt $maxCol) { $maxCol = $ref.Col }
    }
    if ($FillMerged) {
        foreach ($m in $doc.SelectNodes('//m:mergeCells/m:mergeCell', $nsm)) {
            $parts = $m.GetAttribute('ref') -split ':'
            if ($parts.Count -ne 2) { continue }
            $a = Split-CellRef $parts[0]
            $b = Split-CellRef $parts[1]
            $key = "$($a.Row),$($a.Col)"
            if (-not $cells.ContainsKey($key)) { continue }
            $val = $cells[$key]
            for ($r = $a.Row; $r -le $b.Row; $r++) {
                for ($cc = $a.Col; $cc -le $b.Col; $cc++) {
                    $k = "$r,$cc"
                    if (-not $cells.ContainsKey($k)) { $cells[$k] = $val }
                    if ($r -gt $maxRow) { $maxRow = $r }
                    if ($cc -gt $maxCol) { $maxCol = $cc }
                }
            }
        }
    }
    return @{ Name = $Name; Cells = $cells; MaxRow = $maxRow; MaxCol = $maxCol }
}

function Read-XlsxWorkbook {
    <#
    .SYNOPSIS
        Reads every sheet (or those named in -SheetFilter, a scriptblock taking the name) into a list of sheet grids.
    #>
    param([Parameter(Mandatory)][string]$Path, [scriptblock]$SheetFilter, [switch]$FillMerged)
    $zip = Open-XlsxPackage $Path
    try {
        $ss = Read-SharedStrings $zip
        $out = New-Object System.Collections.ArrayList
        foreach ($s in (Get-XlsxSheetList $zip)) {
            if ($null -ne $SheetFilter -and -not (& $SheetFilter $s.Name)) { continue }
            [void]$out.Add((Read-XlsxSheet -Zip $zip -Part $s.Part -Name $s.Name -SharedStrings $ss -FillMerged:$FillMerged))
        }
        return ,$out
    }
    finally { $zip.Dispose() }
}

function Get-Cell {
    # string value of (row, col), '' when empty
    param([Parameter(Mandatory)]$Sheet, [int]$Row, [int]$Col)
    $k = "$Row,$Col"
    if ($Sheet.Cells.ContainsKey($k)) { return [string]$Sheet.Cells[$k] }
    return ''
}

function Find-CellText {
    <#
    .SYNOPSIS
        First (row, col) whose trimmed text equals -Text (ordinal ignore case) within -MaxRow rows; $null if none.
    #>
    param([Parameter(Mandatory)]$Sheet, [Parameter(Mandatory)][string]$Text, [int]$MaxRow = 30)
    for ($r = 1; $r -le [Math]::Min($Sheet.MaxRow, $MaxRow); $r++) {
        for ($c = 1; $c -le $Sheet.MaxCol; $c++) {
            if ((Get-Cell $Sheet $r $c).Trim() -ieq $Text) { return @{ Row = $r; Col = $c } }
        }
    }
    return $null
}

# ---------------------------------------------------------------- mapping helpers (pure, unit-tested)

function ConvertFrom-ExcelSerialDate {
    <#
    .SYNOPSIS
        Excel serial -> date-only [datetime] (integer part; the books carry a ":05:00" time = UTC+5, which is dropped).
        Returns $null for empty / non-numeric / <= 0.
    #>
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $d = 0.0
    if (-not [double]::TryParse($Text.Trim(), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $null }
    if ($d -le 0) { return $null }
    return ([datetime]'1899-12-30').AddDays([Math]::Floor($d)).Date
}

function ConvertFrom-JsonDate {
    # a JSON date value after ConvertFrom-Json: [datetime] (PS 5.1) or ISO text (PS 7); $null when empty
    param($Value)
    if ($null -eq $Value -or "$Value" -eq '') { return $null }
    if ($Value -is [datetime]) { return $Value.Date }
    $dt = [datetime]::MinValue
    if ([datetime]::TryParseExact(([string]$Value).Substring(0, [Math]::Min(10, ([string]$Value).Length)), 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$dt)) { return $dt }
    return $null
}

function ConvertTo-IsoDate {
    param($Date)
    if ($null -eq $Date) { return $null }
    return ([datetime]$Date).ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-ValidTo {
    <#
    .SYNOPSIS
        The workbook "End Date" is INCLUSIVE; the Permits ValidTo is the first NON-valid day: End + AddDays (default 1).
    #>
    param([Parameter(Mandatory)][datetime]$EndInclusive, [int]$AddDays = 1)
    return $EndInclusive.Date.AddDays($AddDays)
}

function ConvertTo-DecimalText {
    <#
    .SYNOPSIS
        Cell text -> invariant decimal text for the API ('1,5' and '1.5' both accepted; '' / '-' / '_' -> $null).
        Throws on unparsable text (the caller counts it as a rejected row, never prints the value).
    #>
    param([string]$Text)
    if ($null -eq $Text) { return $null }
    $t = $Text.Trim()
    if ($t -eq '' -or $t -eq '-' -or $t -eq '_') { return $null }
    $t = $t.Replace(([string][char]0xA0), '').Replace(' ', '')
    if ($t.Contains(',') -and -not $t.Contains('.')) { $t = $t.Replace(',', '.') }
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    $styles = [System.Globalization.NumberStyles]::Float
    # Excel stores doubles (16.3329999999999984 for the 16.333 the user typed): 15 significant digits is what Excel shows.
    $dbl = 0.0
    if ([double]::TryParse($t, $styles, $inv, [ref]$dbl) -and -not [double]::IsInfinity($dbl) -and -not [double]::IsNaN($dbl)) { $t = $dbl.ToString('G15', $inv) }
    $d = [decimal]0
    if (-not [decimal]::TryParse($t, $styles, $inv, [ref]$d)) { throw 'not a number' }
    return $d.ToString($inv)
}

function ConvertTo-CleanText {
    # trim, NFC, collapse whitespace runs; '' -> $null
    param([string]$Text)
    if ($null -eq $Text) { return $null }
    $t = $Text.Normalize([System.Text.NormalizationForm]::FormC)
    $t = ([regex]::Replace($t, '\s+', ' ')).Trim()
    if ($t -eq '') { return $null }
    return $t
}

function ConvertTo-PermitNumber {
    <#
    .SYNOPSIS
        Permit number from a sheet name / header cell. Default: AS IS (only trim + NFC).
        -DashUnderscoreEquivalence: '_' -> '-' ONLY inside a trailing year range '_YYYY_YYYY' (KZ00TEST0000_2023_2030 -> _2023-2030).
        -StripPrefixes: exclusion list (regex fragments, e.g. '^AF_','^permit_\d{3}$'): a number matching any is "excluded"
        (returned as $null), never rewritten.
    #>
    param(
        [string]$Text,
        [switch]$DashUnderscoreEquivalence,
        [string[]]$StripPrefixes = @()
    )
    $n = ConvertTo-CleanText $Text
    if ($null -eq $n) { return $null }
    foreach ($p in @($StripPrefixes)) {
        if (-not [string]::IsNullOrWhiteSpace($p) -and $n -match $p) { return $null }
    }
    if ($DashUnderscoreEquivalence) { $n = [regex]::Replace($n, '_(\d{4})_(\d{4})$', '_$1-$2') }
    return $n
}

function Test-ExcludedTestValue {
    <#
    .SYNOPSIS
        $true if the text equals one of the test markers (ordinal ignore case, trimmed). Markers are a parameter.
    #>
    param([string]$Text, [string[]]$Markers = @())
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    $t = $Text.Trim()
    foreach ($m in @($Markers)) { if ($t -ieq $m) { return $true } }
    return $false
}

function Get-IdempotencyKey {
    param([string]$TypeCode, [string]$Number, [string]$ValidFromIso)
    return ($TypeCode + '|' + $Number + '|' + $ValidFromIso).ToLowerInvariant()
}

function ConvertTo-ColumnCode {
    <#
    .SYNOPSIS
        Free text header -> EcrCode ([A-Za-z][A-Za-z0-9_]{0,63}): non-alnum -> '_', collapse, trim, prefix 'C' if it starts
        with a digit. Used only to derive stable codes from the per-type config, never from data values.
    #>
    param([Parameter(Mandatory)][string]$Header)
    $c = [regex]::Replace($Header.Trim(), '[^A-Za-z0-9]+', '_').Trim('_')
    if ($c -eq '') { throw 'Empty column code.' }
    if ($c -notmatch '^[A-Za-z]') { $c = 'C' + $c }
    if ($c.Length -gt 64) { $c = $c.Substring(0, 64) }
    return $c
}

function Split-IntoChunks {
    # list -> array of arrays, each of at most -Size items (order kept)
    param([Parameter(Mandatory)][AllowEmptyCollection()]$Items, [int]$Size = 500)
    if ($Size -lt 1) { throw 'Chunk size must be positive.' }
    $all = @($Items)
    $chunks = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $all.Count; $i += $Size) {
        $end = [Math]::Min($i + $Size, $all.Count) - 1
        $slice = @($all[$i..$end])
        [void]$chunks.Add($slice)
    }
    return ,$chunks
}

# ---------------------------------------------------------------- per-type configuration (all choices are data, not code)

function New-ColSpec {
    # Header = text of the workbook header cell (first line, case-insensitive); Code = EcrCode of the registry field.
    param(
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$Header,
        [ValidateSet('String', 'Decimal', 'Date')][string]$Type = 'String',
        [switch]$Key,
        [switch]$Required,
        [string]$Group = $null,
        [string]$FallbackCode = $null
    )
    return @{ Code = $Code; Header = $Header; Type = $Type; Key = [bool]$Key; Required = ([bool]$Required -or [bool]$Key); Group = $Group; FallbackCode = $FallbackCode }
}

function Get-MonthColumns {
    $out = @()
    foreach ($m in 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec') {
        $out += , (New-ColSpec -Code $m -Header $m -Type Decimal -Group 'Months')
    }
    return $out
}

function Get-WaterCommonColumns {
    param([switch]$WithManagementSite)
    $cols = @(
        (New-ColSpec 'Event_Frame' 'Event frame' -Key),
        (New-ColSpec 'Water_Body' 'Water body'),
        (New-ColSpec 'Source_Code' 'Source code'),
        (New-ColSpec 'Sea_River_Code' 'Sea/river code'),
        (New-ColSpec 'Quality_Code' 'Quality code'),
        (New-ColSpec 'Distance_Km' 'Distance, km' -Type Decimal),
        (New-ColSpec 'Transmitting_Org' 'Transmitting org')
    )
    if ($WithManagementSite) { $cols += , (New-ColSpec 'Water_Mgmt_Site' 'Water mgmt site') }
    foreach ($i in 1..5) { $cols += , (New-ColSpec "Tributary_$i" "Tributary $i") }
    $cols += , (New-ColSpec 'Issue_Date' 'Issue Date' -Type Date)
    $cols += , (New-ColSpec 'Start_Date' 'Start Date' -Type Date)
    $cols += , (New-ColSpec 'End_Date' 'End Date' -Type Date)
    $cols += , (New-ColSpec 'Actual_End_Date' 'Actual End Date' -Type Date)
    return $cols
}

function Get-PermitTypeConfigs {
    <#
    .SYNOPSIS
        The five permit types (customer defaults of the load plan, section 5). Every key / unit / column name lives here.
        Book: which workbook family feeds the type (Air | Water | Waste); SheetPrefix / SheetExclude pick the sheets.
        HeaderRow / DataRow: row numbers of the row table inside a sheet. Units are not set (unitId = null; the unit is in
        the header text, as in AF).
    #>
    $configs = [ordered]@{}

    $configs['AIR'] = @{
        Code = 'AIR'; Category = 'Air'; NameEn = 'Air emission permit'; Book = 'Air'
        SheetExclude = '^(Document map)$'; TableCode = 'Emission_limits'; TableNameEn = 'Emission limits'
        HeaderRow = 9; DataRow = 10
        Columns = @(
            (New-ColSpec 'Substance_Code' 'Substance Code' -Key),
            (New-ColSpec 'Site_En' 'Site (En)' -Key -FallbackCode 'Site_Ru'),
            (New-ColSpec 'Substance_En' 'Substance (En)'),
            (New-ColSpec 'Substance_Ru' 'Substance (Ru)'),
            (New-ColSpec 'Site_Ru' 'Site (Ru)'),
            (New-ColSpec 'Emission_t_year' 'Emission (t/year)' -Type Decimal -Required),
            (New-ColSpec 'Emission_mg_nm3' 'Emission (mg/nm3)' -Type Decimal),
            (New-ColSpec 'Emission_g_sec' 'Emission (g/sec)' -Type Decimal)
        )
    }

    $configs['WATER_DISCHARGE'] = @{
        Code = 'WATER_DISCHARGE'; Category = 'Water'; NameEn = 'Water discharge permit'; Book = 'Water'
        SheetPrefix = '^Discharge_'; TableCode = 'Discharge_values'; TableNameEn = 'Discharge values'
        HeaderRow = 9; DataRow = 10
        Columns = @(
            (Get-WaterCommonColumns -WithManagementSite) +
            @(
                (New-ColSpec 'Norm_Clean' 'Norm. clean' -Type Decimal),
                (New-ColSpec 'Norm_Treated' 'Norm. treated' -Type Decimal),
                (New-ColSpec 'Insuff_Treated' 'Insuff. treated' -Type Decimal),
                (New-ColSpec 'Without_Treatment' 'Without treatment' -Type Decimal)
            ) + (Get-MonthColumns) +
            @(
                (New-ColSpec 'Sum_Of_Months' 'Sum of months' -Type Decimal),
                (New-ColSpec 'Annual_Stated' 'Annual stated' -Type Decimal -Required)
            )
        )
    }

    $configs['WATER_INTAKE'] = @{
        Code = 'WATER_INTAKE'; Category = 'Water'; NameEn = 'Water intake permit'; Book = 'Water'
        SheetPrefix = '^Intake_'; TableCode = 'Intake_values'; TableNameEn = 'Intake values'
        HeaderRow = 9; DataRow = 10
        Columns = @(
            (Get-WaterCommonColumns) +
            @(
                (New-ColSpec 'Reliability_50' 'Reliability 50%' -Type Decimal),
                (New-ColSpec 'Reliability_75' 'Reliability 75%' -Type Decimal),
                (New-ColSpec 'Reliability_95' 'Reliability 95%' -Type Decimal),
                (New-ColSpec 'Use_Type_Code' 'Use type code'),
                (New-ColSpec 'Use_Type_Volume' 'Use type volume' -Type Decimal)
            ) + (Get-MonthColumns) +
            @(
                (New-ColSpec 'Sum_Of_Months' 'Sum of months' -Type Decimal),
                (New-ColSpec 'Annual_Stated' 'Annual stated' -Type Decimal -Required)
            )
        )
    }

    $configs['WATER_GENERAL'] = @{
        Code = 'WATER_GENERAL'; Category = 'Water'; NameEn = 'General water permit'; Book = 'Water'
        SheetPrefix = '^General_'; TableCode = 'Emission_limits_water'; TableNameEn = 'Emission limits per substance'
        HeaderRow = 9; DataRow = 10
        Columns = @(
            (New-ColSpec 'Event_Frame' 'Event frame' -Key),
            (New-ColSpec 'Substance_Code' 'Substance Code'),
            (New-ColSpec 'Substance_En' 'Substance (En)'),
            (New-ColSpec 'Substance_Ru' 'Substance (Ru)'),
            (New-ColSpec 'Formula' 'Formula'),
            (New-ColSpec 'Permissible_mg_dm3' 'Permissible, mg/dm3' -Type Decimal),
            (New-ColSpec 'Discharge_g_h' 'Discharge (g/h)' -Type Decimal),
            (New-ColSpec 'Discharge_t_year' 'Discharge (t/year)' -Type Decimal -Required),
            (New-ColSpec 'Flow_m3_h' 'Flow (m3/h)' -Type Decimal),
            (New-ColSpec 'Flow_m3_year' 'Flow (m3/year)' -Type Decimal)
        )
    }

    $configs['WASTE'] = @{
        Code = 'WASTE'; Category = 'Waste'; NameEn = 'Waste placement permit'; Book = 'Waste'
        SheetExclude = '^(Document map|Sheet\d*)$'; TableCode = 'Waste_limits'; TableNameEn = 'Waste limits'
        HeaderRow = 2; DataRow = 3
        Columns = @(
            (New-ColSpec 'Platform' 'Platform'),
            (New-ColSpec 'Place' 'Place' -Key),
            (New-ColSpec 'Waste_Code' 'Waste code' -Key),
            (New-ColSpec 'Waste_Type_En' 'Waste type (EN)'),
            (New-ColSpec 'Waste_Type_Ru' 'Waste type (RU)'),
            (New-ColSpec 'Category' 'Category'),
            (New-ColSpec 'Permitted_t_year' 'Permitted, t/year' -Type Decimal -Required)
        )
    }

    return $configs
}

function Get-DefaultLoadOptions {
    return @{
        DashUnderscoreEquivalence = $false
        # human decision 21.10: service sheets (N/A, No Permit - ...) ARE loaded as permits without rows; the list is an exclusion hook only
        ExcludeNumberPatterns     = @()
        ExcludeTestRows           = $true
        TestMarkers               = @('qwwe', 'Test_Water')
        DashAsEmpty               = $true
        # key part empty in a row -> placeholder (code -> text); {} disables and the row is rejected as 'required:<code>'
        EmptyKeyDefaults          = @{ Site_En = '-' }
        ValidToAddDays            = 1
        GeneralDatesFrom          = 'Air'      # Air | None
        ActualEndMode             = 'IfDiffers' # IfDiffers | None
        ServiceLocation           = 'service: Land dictionary only'
        ServiceValidFrom          = $null      # no default: dates of service entries are never invented
        ServiceValidTo            = $null
    }
}

# ---------------------------------------------------------------- sheet parsing

function Get-HeaderMap {
    <#
    .SYNOPSIS
        header text (first line, lower-case, collapsed spaces) -> first column that carries it, for one row.
    #>
    param([Parameter(Mandatory)]$Sheet, [Parameter(Mandatory)][int]$Row)
    $map = @{}
    for ($c = 1; $c -le $Sheet.MaxCol; $c++) {
        $t = Get-Cell $Sheet $Row $c
        if ($t -eq '') { continue }
        $first = ($t -split "[\r\n]+")[0]
        $key = ([regex]::Replace($first, '\s+', ' ')).Trim().ToLowerInvariant()
        if ($key -ne '' -and -not $map.ContainsKey($key)) { $map[$key] = $c }
    }
    return $map
}

function Get-ColumnForHeader {
    param($HeaderMap, [string]$Header)
    $k = ([regex]::Replace($Header, '\s+', ' ')).Trim().ToLowerInvariant()
    if ($HeaderMap.ContainsKey($k)) { return [int]$HeaderMap[$k] }
    return 0
}

function New-RejectCounter { return @{} }

function Add-Reject {
    param($Counter, [string]$Reason)
    if ($Counter.ContainsKey($Reason)) { $Counter[$Reason] = [int]$Counter[$Reason] + 1 } else { $Counter[$Reason] = 1 }
}

function ConvertTo-CanonicalKey {
    # same idea as the server RegistryKeyNormalizer for text parts: NFC, collapsed spaces, case-insensitive
    param([string[]]$Parts)
    $norm = foreach ($p in $Parts) {
        $x = $p
        if ($null -eq $x) { $x = '' }
        ([regex]::Replace($x.Normalize([System.Text.NormalizationForm]::FormC), '\s+', ' ')).Trim().ToLowerInvariant()
    }
    return ($norm -join [string][char]0x1F)
}

function ConvertTo-PermitRows {
    <#
    .SYNOPSIS
        Rows of one sheet -> API value maps for the table of -Config. Returns
        @{ Rows = list of @{ SrcRow; Values (ordered code -> string) }; TestExcluded; Rejected (reason -> count); MaxLen (code -> int);
           Blank }.
        Never returns / logs cell values in a counter or reason. Rejected rows are NOT in Rows.
    #>
    param(
        [Parameter(Mandatory)]$Sheet,
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)]$Options
    )
    $hm = Get-HeaderMap $Sheet $Config.HeaderRow
    $colOf = @{}
    foreach ($spec in $Config.Columns) {
        $c = Get-ColumnForHeader $hm $spec.Header
        if ($c -le 0) { throw "Sheet layout changed: column '$($spec.Code)' not found in the header row of a $($Config.Code) sheet." }
        $colOf[$spec.Code] = $c
    }
    $rows = New-Object System.Collections.ArrayList
    $rejected = New-RejectCounter
    $maxLen = @{}
    $seen = @{}
    $testExcluded = 0
    $blank = 0
    $fallbacks = @{}
    $keySpecs = @($Config.Columns | Where-Object { $_.Key })
    for ($r = $Config.DataRow; $r -le $Sheet.MaxRow; $r++) {
        $raw = [ordered]@{}
        $any = $false
        foreach ($spec in $Config.Columns) {
            $t = Get-Cell $Sheet $r $colOf[$spec.Code]
            $raw[$spec.Code] = $t
            if ($t.Trim() -ne '') { $any = $true }
        }
        if (-not $any) { $blank++; continue }

        if ($Options.ExcludeTestRows) {
            $isTest = $false
            foreach ($spec in $Config.Columns) {
                if (Test-ExcludedTestValue $raw[$spec.Code] $Options.TestMarkers) { $isTest = $true; break }
            }
            if (-not $isTest) {
                foreach ($ks in $keySpecs) {
                    foreach ($m in @($Options.TestMarkers)) {
                        if (-not [string]::IsNullOrWhiteSpace($m) -and $raw[$ks.Code].IndexOf($m, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $isTest = $true }
                    }
                }
            }
            if ($isTest) { $testExcluded++; continue }
        }

        $values = [ordered]@{}
        $bad = $false
        foreach ($spec in $Config.Columns) {
            $t = $raw[$spec.Code]
            $v = $null
            switch ($spec.Type) {
                'Decimal' {
                    try { $v = ConvertTo-DecimalText $t } catch { Add-Reject $rejected ("number:" + $spec.Code); $bad = $true }
                }
                'Date' {
                    if ($t.Trim() -ne '' -and $t.Trim() -ne '-') {
                        $dt = ConvertFrom-ExcelSerialDate $t
                        if ($null -eq $dt) { Add-Reject $rejected ("date:" + $spec.Code); $bad = $true } else { $v = ConvertTo-IsoDate $dt }
                    }
                }
                default {
                    $v = ConvertTo-CleanText $t
                    if ($null -eq $v -and $spec.FallbackCode) {
                        # key part empty in the book: take the sibling column (counted, never printed)
                        $v = ConvertTo-CleanText $raw[$spec.FallbackCode]
                        if ($null -ne $v) { if ($fallbacks.ContainsKey($spec.Code)) { $fallbacks[$spec.Code]++ } else { $fallbacks[$spec.Code] = 1 } }
                    }
                    if ($null -eq $v -and $spec.Key -and $Options.EmptyKeyDefaults.ContainsKey($spec.Code)) {
                        # a key part missing in the whole permit (one Air permit has no site): configured placeholder, counted
                        $v = [string]$Options.EmptyKeyDefaults[$spec.Code]
                        if ($fallbacks.ContainsKey('default:' + $spec.Code)) { $fallbacks['default:' + $spec.Code]++ } else { $fallbacks['default:' + $spec.Code] = 1 }
                    }
                    if ($null -ne $v -and $Options.DashAsEmpty -and -not $spec.Key -and ($v -eq '-' -or $v -eq '_')) { $v = $null }
                }
            }
            if ($spec.Required -and $null -eq $v -and -not $bad) { Add-Reject $rejected ("required:" + $spec.Code); $bad = $true }
            if ($null -ne $v) {
                $values[$spec.Code] = $v
                $len = ([string]$v).Length
                if (-not $maxLen.ContainsKey($spec.Code) -or $len -gt $maxLen[$spec.Code]) { $maxLen[$spec.Code] = $len }
            }
        }
        if (-not $bad) {
            $ck = ConvertTo-CanonicalKey @($keySpecs | ForEach-Object { [string]$values[$_.Code] })
            if ($seen.ContainsKey($ck)) { Add-Reject $rejected 'duplicateKey'; $bad = $true } else { $seen[$ck] = $true }
        }
        if ($bad) { continue }
        [void]$rows.Add(@{ SrcRow = $r; Values = $values })
    }
    return @{ Rows = $rows; TestExcluded = $testExcluded; Rejected = $rejected; MaxLen = $maxLen; Blank = $blank; Fallbacks = $fallbacks }
}

function ConvertFrom-DottedDate {
    # 'dd.MM.yyyy' -> date or $null
    param([string]$Text)
    $dt = [datetime]::MinValue
    if ([datetime]::TryParseExact($Text.Trim(), 'dd.MM.yyyy', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$dt)) { return $dt.Date }
    return $null
}

function Add-FallbackNotes {
    param($Plan, $Res)
    foreach ($k in $Res.Fallbacks.Keys) { [void]$Plan.Notes.Add(('fallback:{0}={1}' -f $k, $Res.Fallbacks[$k])) }
}

function New-PermitPlan {
    param([string]$TypeCode, [string]$SheetName, [string]$Number)
    return @{
        TypeCode = $TypeCode; SheetName = $SheetName; Number = $Number; Location = $null
        IssueDate = $null; ValidFrom = $null; ValidTo = $null; ActualEnd = $null; EndInclusive = $null
        Rows = $null; TestExcluded = 0; Rejected = @{}; MaxLen = @{}
        Status = 'Ready'; Reason = $null; Notes = New-Object System.Collections.ArrayList
    }
}

function Complete-PermitDates {
    <#
    .SYNOPSIS
        Fills ValidFrom / ValidTo (= End + ValidToAddDays) / ActualEnd on a plan from inclusive Start / End dates.
        Actual End is stored only when it differs from End (-ActualEndMode IfDiffers); like ValidTo it is the first
        non-valid day (End API contract), so inclusive + the same offset.
    #>
    param($Plan, $Start, $EndInclusive, $ActualInclusive, $Options)
    if ($null -eq $Start -or $null -eq $EndInclusive) { return $false }
    if ($EndInclusive -lt $Start) { return $false }
    $Plan.ValidFrom = ([datetime]$Start).Date
    $Plan.EndInclusive = ([datetime]$EndInclusive).Date
    $Plan.ValidTo = Get-ValidTo $EndInclusive $Options.ValidToAddDays
    if ($Options.ActualEndMode -eq 'IfDiffers' -and $null -ne $ActualInclusive -and ([datetime]$ActualInclusive).Date -ne ([datetime]$EndInclusive).Date) {
        $Plan.ActualEnd = Get-ValidTo $ActualInclusive $Options.ValidToAddDays
    }
    return $true
}

function Read-AirBook {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Config, [Parameter(Mandatory)]$Options)
    $plans = New-Object System.Collections.ArrayList
    foreach ($sheet in (Read-XlsxWorkbook -Path $Path)) {
        if ($sheet.Name -match $Config.SheetExclude) { continue }
        $numCell = Find-CellText $sheet 'Permit Number' 6
        $numRaw = ''
        if ($null -ne $numCell) { $numRaw = Get-Cell $sheet $numCell.Row ($numCell.Col + 1) }
        $number = ConvertTo-PermitNumber -Text $numRaw -DashUnderscoreEquivalence:$Options.DashUnderscoreEquivalence -StripPrefixes $Options.ExcludeNumberPatterns
        $plan = New-PermitPlan $Config.Code $sheet.Name $number
        if ($null -eq $number) { $plan.Status = 'Excluded'; $plan.Reason = 'number-excluded'; [void]$plans.Add($plan); continue }

        $h5 = Get-HeaderMap $sheet 5
        $col = { param($name) $k = Get-ColumnForHeader $h5 $name; if ($k -le 0) { throw "Sheet layout changed: '$name' not found in the Air facility header." }; $k }
        $plan.Location = ConvertTo-CleanText (Get-Cell $sheet 6 (& $col 'Industrial Facility Name En'))
        $plan.IssueDate = ConvertFrom-ExcelSerialDate (Get-Cell $sheet 6 (& $col 'Issue Date'))
        $start = ConvertFrom-ExcelSerialDate (Get-Cell $sheet 6 (& $col 'Start Date'))
        $end = ConvertFrom-ExcelSerialDate (Get-Cell $sheet 6 (& $col 'End Date'))
        $act = ConvertFrom-ExcelSerialDate (Get-Cell $sheet 6 (& $col 'Actual End Date'))
        if (-not (Complete-PermitDates $plan $start $end $act $Options)) { $plan.Status = 'Invalid'; $plan.Reason = 'dates-missing' }

        $res = ConvertTo-PermitRows -Sheet $sheet -Config $Config -Options $Options
        $plan.Rows = $res.Rows; $plan.TestExcluded = $res.TestExcluded; $plan.Rejected = $res.Rejected; $plan.MaxLen = $res.MaxLen; Add-FallbackNotes $plan $res
        [void]$plans.Add($plan)
    }
    return ,$plans
}

function Read-WaterBook {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Config, [Parameter(Mandatory)]$Options)
    $plans = New-Object System.Collections.ArrayList
    $isGeneral = ($Config.Code -eq 'WATER_GENERAL')
    foreach ($sheet in (Read-XlsxWorkbook -Path $Path)) {
        if ($sheet.Name -notmatch $Config.SheetPrefix) { continue }
        $numCell = Find-CellText $sheet 'Permit Number' 8
        $numRaw = ''
        if ($null -ne $numCell) { $numRaw = Get-Cell $sheet $numCell.Row ($numCell.Col + 1) }
        $number = ConvertTo-PermitNumber -Text $numRaw -DashUnderscoreEquivalence:$Options.DashUnderscoreEquivalence -StripPrefixes $Options.ExcludeNumberPatterns
        $plan = New-PermitPlan $Config.Code $sheet.Name $number
        if ($null -eq $number) { $plan.Status = 'Excluded'; $plan.Reason = 'number-excluded'; [void]$plans.Add($plan); continue }

        $res = ConvertTo-PermitRows -Sheet $sheet -Config $Config -Options $Options
        $plan.Rows = $res.Rows; $plan.TestExcluded = $res.TestExcluded; $plan.Rejected = $res.Rejected; $plan.MaxLen = $res.MaxLen; Add-FallbackNotes $plan $res
        if (-not $isGeneral) {
            # the permit window is the row-level Start / End / Issue / Actual End (min start, max end); inconsistent rows are noted
            $starts = @($plan.Rows | ForEach-Object { $_.Values['Start_Date'] } | Where-Object { $_ })
            $ends = @($plan.Rows | ForEach-Object { $_.Values['End_Date'] } | Where-Object { $_ })
            $issues = @($plan.Rows | ForEach-Object { $_.Values['Issue_Date'] } | Where-Object { $_ })
            $acts = @($plan.Rows | ForEach-Object { $_.Values['Actual_End_Date'] } | Where-Object { $_ })
            if ($starts.Count -gt 0 -and $ends.Count -gt 0) {
                $s = ([datetime]($starts | Sort-Object | Select-Object -First 1))
                $e = ([datetime]($ends | Sort-Object | Select-Object -Last 1))
                $a = $null
                if ($acts.Count -gt 0) { $a = [datetime]($acts | Sort-Object | Select-Object -Last 1) }
                if (-not (Complete-PermitDates $plan $s $e $a $Options)) { $plan.Status = 'Invalid'; $plan.Reason = 'dates-missing' }
                if (@($starts | Select-Object -Unique).Count -gt 1 -or @($ends | Select-Object -Unique).Count -gt 1) { [void]$plan.Notes.Add('rows-dates-differ') }
                if ($issues.Count -gt 0) { $plan.IssueDate = [datetime]($issues | Sort-Object | Select-Object -First 1) }
            }
            elseif ($plan.Rows.Count -gt 0) { $plan.Status = 'Invalid'; $plan.Reason = 'dates-missing' }
        }
        [void]$plans.Add($plan)
    }
    return ,$plans
}

function Read-WasteBook {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Config, [Parameter(Mandatory)]$Options)
    $plans = New-Object System.Collections.ArrayList
    foreach ($sheet in (Read-XlsxWorkbook -Path $Path)) {
        if ($sheet.Name -match $Config.SheetExclude) { continue }
        # R1: "Permit / <label>: <number> | <number> | dd.MM.yyyy - dd.MM.yyyy | total ..." (the dates exist only in this text)
        $r1 = Get-Cell $sheet 1 1
        $m = [regex]::Match($r1, ':\s*(?<n>[^\s|]+)\s*\|[^|]*\|\s*(?<a>\d{2}\.\d{2}\.\d{4})\s*-\s*(?<b>\d{2}\.\d{2}\.\d{4})')
        if (-not $m.Success) { continue }
        $number = ConvertTo-PermitNumber -Text $m.Groups['n'].Value -DashUnderscoreEquivalence:$Options.DashUnderscoreEquivalence -StripPrefixes $Options.ExcludeNumberPatterns
        $plan = New-PermitPlan $Config.Code $sheet.Name $number
        if ($null -eq $number) { $plan.Status = 'Excluded'; $plan.Reason = 'number-excluded'; [void]$plans.Add($plan); continue }
        $start = ConvertFrom-DottedDate $m.Groups['a'].Value
        $end = ConvertFrom-DottedDate $m.Groups['b'].Value
        if (-not (Complete-PermitDates $plan $start $end $null $Options)) { $plan.Status = 'Invalid'; $plan.Reason = 'dates-missing' }
        $res = ConvertTo-PermitRows -Sheet $sheet -Config $Config -Options $Options
        $plan.Rows = $res.Rows; $plan.TestExcluded = $res.TestExcluded; $plan.Rejected = $res.Rejected; $plan.MaxLen = $res.MaxLen; Add-FallbackNotes $plan $res
        [void]$plans.Add($plan)
    }
    return ,$plans
}

function Resolve-GeneralPermitDates {
    <#
    .SYNOPSIS
        General water permits carry no dates: take them from the Air permit with the same number (plan section 5, Q5).
        A General permit with no such Air permit gets Status = Invalid-free "Skipped" / Reason 'no-dates'.
    #>
    param($GeneralPlans, $AirPlans, $Options)
    foreach ($g in $GeneralPlans) {
        if ($g.Status -ne 'Ready' -or $null -ne $g.ValidFrom) { continue }
        $air = $null
        if ($Options.GeneralDatesFrom -eq 'Air') {
            $air = @($AirPlans | Where-Object { $_.Status -eq 'Ready' -and $_.Number -ieq $g.Number }) | Select-Object -First 1
        }
        if ($null -eq $air) { $g.Status = 'Skipped'; $g.Reason = 'no-dates'; continue }
        $g.ValidFrom = $air.ValidFrom; $g.ValidTo = $air.ValidTo; $g.EndInclusive = $air.EndInclusive; $g.ActualEnd = $air.ActualEnd
        $g.IssueDate = $air.IssueDate
        [void]$g.Notes.Add('dates-from-air')
    }
}

function Complete-PlanStatus {
    <#
    .SYNOPSIS
        Final status of a plan: Invalid when any row was rejected or dates are missing, Skipped when nothing is left to load.
    #>
    param($Plan, [bool]$SkipEmptyPermits = $true)
    if ($Plan.Status -ne 'Ready') { return }
    $rejected = 0
    foreach ($k in $Plan.Rejected.Keys) { $rejected += [int]$Plan.Rejected[$k] }
    if ($rejected -gt 0) { $Plan.Status = 'Invalid'; $Plan.Reason = 'rows-rejected'; return }
    if ($null -eq $Plan.ValidFrom) { $Plan.Status = 'Invalid'; $Plan.Reason = 'dates-missing'; return }
    if ($null -eq $Plan.Rows -or $Plan.Rows.Count -eq 0) {
        if ($SkipEmptyPermits) { $Plan.Status = 'Skipped'; $Plan.Reason = 'no-rows'; return }
        [void]$Plan.Notes.Add('no-rows')
    }
    if ($Plan.Number.Length -gt 200) { $Plan.Status = 'Invalid'; $Plan.Reason = 'number-too-long'; return }
    if ($null -ne $Plan.Location -and $Plan.Location.Length -gt 500) { $Plan.Location = $Plan.Location.Substring(0, 500); [void]$Plan.Notes.Add('location-truncated') }
}

function Read-ServiceEntries {
    <#
    .SYNOPSIS
        Service entries that exist only in the Land dictionary (permit_001.., AF_<guid>): CSV/semicolon file with the header
        number;location;validFrom;validTo (dates yyyy-MM-dd, optional). Returns objects; values are never logged.
    #>
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    if (-not (Test-Path -LiteralPath $Path)) { throw "Service entries file not found." }
    # lines starting with '#' are comments (the shipped file carries the 'dates pending confirmation' note)
    $text = (([IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) -split "\r?\n") | Where-Object { -not $_.StartsWith('#') }) -join "`n"
    $delim = ';'
    if (($text -split "`n")[0].Contains(',') -and -not ($text -split "`n")[0].Contains(';')) { $delim = ',' }
    return @($text | ConvertFrom-Csv -Delimiter $delim)
}

function ConvertTo-ServicePlans {
    <#
    .SYNOPSIS
        Entries -> AIR permit plans WITHOUT rows. Dates come from the entry, else Options.ServiceValidFrom / ServiceValidTo
        (ISO dates, exclusive end). No dates anywhere -> Skipped 'no-dates' (dates are NOT invented).
    #>
    param($Entries, $Options)
    $out = New-Object System.Collections.ArrayList
    foreach ($e in @($Entries)) {
        $num = ConvertTo-CleanText ([string](Get-Prop2 $e 'number'))
        if ($null -eq $num) { continue }
        $plan = New-PermitPlan 'AIR' 'service-entry' $num
        $loc = ConvertTo-CleanText ([string](Get-Prop2 $e 'location'))
        if ($null -eq $loc) { $loc = [string]$Options.ServiceLocation }
        $plan.Location = $loc
        $plan.Rows = New-Object System.Collections.ArrayList
        [void]$plan.Notes.Add('service-entry')
        $from = [string](Get-Prop2 $e 'validFrom'); if ([string]::IsNullOrWhiteSpace($from)) { $from = [string]$Options.ServiceValidFrom }
        $to = [string](Get-Prop2 $e 'validTo'); if ([string]::IsNullOrWhiteSpace($to)) { $to = [string]$Options.ServiceValidTo }
        $f = [datetime]::MinValue; $tt = [datetime]::MinValue
        $ci = [System.Globalization.CultureInfo]::InvariantCulture
        if ([datetime]::TryParseExact($from, 'yyyy-MM-dd', $ci, 'None', [ref]$f) -and [datetime]::TryParseExact($to, 'yyyy-MM-dd', $ci, 'None', [ref]$tt) -and $tt -gt $f) {
            $plan.ValidFrom = $f; $plan.ValidTo = $tt; $plan.EndInclusive = $tt.AddDays(-1)
        }
        else { $plan.Status = 'Skipped'; $plan.Reason = 'no-dates' }
        [void]$out.Add($plan)
    }
    return $out
}

function Get-Prop2 {
    param($Object, [string]$Name)
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Build-LoadPlan {
    <#
    .SYNOPSIS
        Reads the books and returns @{ Plans = all permit plans (type order AIR, WATER_*, WASTE); Types = configs used }.
        -Only limits the type codes. Pure file work: no network.
    #>
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [string[]]$Books = @('PermitsInfo.Year.xlsx', 'PermitsInfoWater.Year.xlsx', 'PermitsInfoWaste.Year.xlsx'),
        [string[]]$Only = @(),
        $Options = (Get-DefaultLoadOptions),
        [bool]$SkipEmptyPermits = $false,
        $ServiceEntries = @()
    )
    $configs = Get-PermitTypeConfigs
    $byBook = @{}
    foreach ($b in $Books) {
        $leaf = Split-Path -Leaf $b
        $full = $b
        if (-not [IO.Path]::IsPathRooted($b)) { $full = Join-Path $SourceDir $b }
        if ($leaf -match 'Water') { $byBook['Water'] = $full } elseif ($leaf -match 'Waste') { $byBook['Waste'] = $full } else { $byBook['Air'] = $full }
    }
    $wanted = @($configs.Keys)
    if (@($Only).Count -gt 0) { $wanted = @($wanted | Where-Object { $Only -contains $_ }) }
    $all = New-Object System.Collections.ArrayList
    $airPlans = @()
    # Air plans are also needed for General dates even when AIR itself is not loaded
    $needAirForDates = ($wanted -contains 'WATER_GENERAL') -and $Options.GeneralDatesFrom -eq 'Air' -and $byBook.ContainsKey('Air')
    $airRead = $null
    if (($wanted -contains 'AIR' -or $needAirForDates) -and $byBook.ContainsKey('Air')) {
        $airRead = Read-AirBook -Path $byBook['Air'] -Config $configs['AIR'] -Options $Options
        $airPlans = @($airRead)
    }
    foreach ($code in $wanted) {
        $cfg = $configs[$code]
        $plans = $null
        switch ($cfg.Book) {
            'Air' { $plans = $airRead }
            'Water' { if ($byBook.ContainsKey('Water')) { $plans = Read-WaterBook -Path $byBook['Water'] -Config $cfg -Options $Options } }
            'Waste' { if ($byBook.ContainsKey('Waste')) { $plans = Read-WasteBook -Path $byBook['Waste'] -Config $cfg -Options $Options } }
        }
        if ($null -eq $plans) { continue }
        if ($code -eq 'WATER_GENERAL') { Resolve-GeneralPermitDates -GeneralPlans $plans -AirPlans $airPlans -Options $Options }
        foreach ($p in $plans) { Complete-PlanStatus $p $SkipEmptyPermits; [void]$all.Add($p) }
    }
    if ($wanted -contains 'AIR') {
        foreach ($sp in @(ConvertTo-ServicePlans -Entries $ServiceEntries -Options $Options)) { [void]$all.Add($sp) }
    }
    return @{ Plans = $all; Types = $configs; Wanted = $wanted }
}

# ---------------------------------------------------------------- API payload builders (pure)

function ConvertTo-TypeColumnDto {
    param($Spec, [int]$Order)
    return [ordered]@{
        code = $Spec.Code; nameL10n = [ordered]@{ en = (($Spec.Header -split "[\r\n]+")[0]) }; dataType = $Spec.Type; unitId = $null
        required = [bool]$Spec.Required; key = [bool]$Spec.Key; group = $(if ([string]::IsNullOrEmpty($Spec.Group)) { $null } else { $Spec.Group }); precision = $null; scale = $null
        listScope = $null; listRegistryCode = $null; siblingTableCode = $null; choices = $null; order = $Order
    }
}

function ConvertTo-TypeContent {
    <#
    .SYNOPSIS
        Type config -> the ReplacePermitTypeRequest pieces (nameL10n, category, tables, rules). No validation rules by default.
    #>
    param([Parameter(Mandatory)]$Config)
    $cols = New-Object System.Collections.ArrayList
    $i = 0
    foreach ($spec in $Config.Columns) { $i++; [void]$cols.Add((ConvertTo-TypeColumnDto $spec $i)) }
    $table = [ordered]@{
        code = $Config.TableCode; nameL10n = [ordered]@{ en = $Config.TableNameEn }; order = 1
        columns = @($cols); rules = @()
    }
    return [ordered]@{ nameL10n = [ordered]@{ en = $Config.NameEn }; category = $Config.Category; tables = @($table); rules = @() }
}

function ConvertTo-BatchItems {
    <#
    .SYNOPSIS
        Rows of a plan -> entries/batch items (code = null, op upsert, PERMIT = header id).
    #>
    param([Parameter(Mandatory)]$Rows, [Parameter(Mandatory)][long]$PermitId, [string]$IdPrefix = 'r')
    $items = New-Object System.Collections.ArrayList
    $n = 0
    foreach ($row in $Rows) {
        $n++
        $values = [ordered]@{ PERMIT = $PermitId.ToString([System.Globalization.CultureInfo]::InvariantCulture) }
        foreach ($k in $row.Values.Keys) { $values[$k] = $row.Values[$k] }
        [void]$items.Add([ordered]@{ clientRowId = ('{0}{1}' -f $IdPrefix, $row.SrcRow); op = 'upsert'; id = $null; code = $null; baseVersion = $null; values = $values })
    }
    return ,$items
}

# ---------------------------------------------------------------- header Permit remap (pure; used by Remap-HeaderPermit.ps1)

function Get-NormalizedNumberKey {
    # matching key: trimmed, lower-case, optionally without '-' / '_' (dash/underscore equivalence is for MATCHING only)
    param([string]$Text, [bool]$Normalize = $true)
    $k = $Text.Trim().ToLowerInvariant()
    if ($Normalize) { $k = $k -replace '[-_]', '' }
    return $k
}

function Resolve-HeaderPermitMap {
    <#
    .SYNOPSIS
        Source entries (Id, Code, Docs) -> target entries (Id, Num, IsActive) 1:1 by number.
        Returns @{ Map = list of ordered @{ oldId; oldCode; docs; newId; status; normalized };
                   Counters = @{ entries; withDocs; matched; noMatch; ambiguous; normalized; inactiveTarget; docsMapped; docsBlocked } }.
        status: matched | matched-inactive | no-match | ambiguous. normalized = matched only thanks to the '-'/'_' equivalence
        (or letter case), i.e. the numbers are not byte-equal.
    #>
    param($Source, $Target, [bool]$Normalize = $true)
    $byKey = @{}
    foreach ($t in @($Target)) {
        $k = Get-NormalizedNumberKey ([string]$t.Num) $Normalize
        if (-not $byKey.ContainsKey($k)) { $byKey[$k] = New-Object System.Collections.ArrayList }
        [void]$byKey[$k].Add($t)
    }
    $map = New-Object System.Collections.ArrayList
    $c = [ordered]@{ entries = 0; withDocs = 0; matched = 0; noMatch = 0; ambiguous = 0; normalized = 0; inactiveTarget = 0; docsMapped = 0; docsBlocked = 0 }
    foreach ($s in @($Source)) {
        $c.entries++
        $docs = [int]$s.Docs
        if ($docs -gt 0) { $c.withDocs++ }
        $hits = @()
        $k = Get-NormalizedNumberKey ([string]$s.Code) $Normalize
        if ($byKey.ContainsKey($k)) { $hits = @($byKey[$k]) }
        $status = 'no-match'; $newId = $null; $norm = $false
        if ($hits.Count -eq 1) {
            $status = 'matched'; $newId = [int]$hits[0].Id
            if (-not [bool]$hits[0].IsActive) { $status = 'matched-inactive'; $c.inactiveTarget++ }
            if (([string]$s.Code).Trim() -cne ([string]$hits[0].Num).Trim()) { $norm = $true; $c.normalized++ }
        }
        elseif ($hits.Count -gt 1) { $status = 'ambiguous' }
        if ($status -like 'matched*') { $c.matched++; $c.docsMapped += $docs }
        elseif ($status -eq 'ambiguous') { $c.ambiguous++; $c.docsBlocked += $docs }
        else { $c.noMatch++; $c.docsBlocked += $docs }
        [void]$map.Add([ordered]@{ oldId = [int]$s.Id; oldCode = [string]$s.Code; docs = $docs; newId = $newId; status = $status; normalized = $norm })
    }
    return @{ Map = $map; Counters = $c }
}

function ConvertTo-LookupRemapsDocument {
    <#
    .SYNOPSIS
        The body part of POST /documents/{id}/migrate-version for the Analiz plan MIGRATE-LOOKUP-REMAP:
        { lookupRemaps:[{ target:{kind:'header',code}, mappings:[{from,to}] }], meta:{ generatedAt, database, docsPerFromId, normalized } }.
        Only entries that are USED (docs > 0) and matched are mapped. Codes are written to meta.codesPerFromId only with -IncludeCodes.
    #>
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Database,
        [string]$HeaderFieldCode = 'Permit',
        [switch]$IncludeCodes,
        [datetime]$GeneratedAt = (Get-Date)
    )
    $pairs = @()
    $docsPer = [ordered]@{}
    $codes = [ordered]@{}
    $normalized = 0
    foreach ($m in @($Map | Where-Object { $_.docs -gt 0 -and $null -ne $_.newId })) {
        $pairs += , ([ordered]@{ from = [long]$m.oldId; to = [long]$m.newId })
        $docsPer[[string]$m.oldId] = [int]$m.docs
        $codes[[string]$m.oldId] = $m.oldCode
        if ($m.normalized) { $normalized++ }
    }
    $meta = [ordered]@{ generatedAt = $GeneratedAt.ToUniversalTime().ToString('o'); database = $Database; docsPerFromId = $docsPer; normalized = $normalized }
    if ($IncludeCodes) { $meta['codesPerFromId'] = $codes }
    return [ordered]@{
        lookupRemaps = @([ordered]@{ target = [ordered]@{ kind = 'header'; code = $HeaderFieldCode }; mappings = @($pairs) })
        meta         = $meta
    }
}