<#
.SYNOPSIS
    Library of the FULL-set Land parity harness (dot-sourced by Compare-LandParityFull.ps1). Functions only, no side effects.
.DESCRIPTION
    No production values are written anywhere by this library: cells carry identifiers, classes and relative errors.
    The compare core (Invoke-LandParityCompare) works on in-memory structures, so it is tested on synthetic data.

    Differences from the sample harness (Compare-LandParity.ps1):
      * several periods per document and several legacy EFIDs: rows are aligned BY POSITION, rowKey p<doc>-<seq:0000>
        (the same JSON is the loader input), not by an input signature;
      * methodology -> table: cfg.CalculationBinding (+ -MethodologyTable overrides + built-in fallback);
      * ROW_NOT_LOADED_CAPPED / ROW_NOT_LOADED_NO_CELLS: the legacy row exists, ECR has no row (template row limit /
        row without cells); the same tolerances and the same class list as the sample harness otherwise;
      * AggregateBy NaePhase: an ambiguous key (one NAE+phase for several Land_No) is compared as a SUM over the group.

    Naming rule (LandScriptVariableCollisionTests): no local variable may have the name of a parameter of any
    function/scriptblock in this file, so parameters are named differently from locals and the mutated objects are
    aliased to short locals ($st, $ac).
#>

function Get-LandParityV {
    # Dictionary-agnostic read (Hashtable and Dictionary[string,object]): null when the key is absent.
    param($Source, [string]$PropName)
    if ($null -eq $Source) { return $null }
    if ($Source -is [System.Collections.IDictionary]) {
        # Hashtable / Dictionary[string,object] have ContainsKey; Dictionary.Contains(x) is a different (KeyValuePair) overload.
        if ($null -ne $Source.PSObject.Methods['ContainsKey']) {
            if ($Source.ContainsKey($PropName)) { return $Source[$PropName] }
        }
        elseif ($Source.Contains($PropName)) { return $Source[$PropName] }
    }
    return $null
}

function ConvertFrom-LandParityJson {
    # Windows PowerShell 5.1: JavaScriptSerializer (fast on big files). pwsh 6+/Linux: ConvertFrom-Json -AsHashtable.
    param([Parameter(Mandatory)][string]$JsonText)
    if ($PSVersionTable.PSVersion.Major -ge 6) { return ($JsonText | ConvertFrom-Json -AsHashtable) }
    Add-Type -AssemblyName System.Web.Extensions
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = [int]::MaxValue
    $ser.RecursionLimit = 200
    return $ser.DeserializeObject($JsonText)
}

function Get-LandParityTableCode {
    # Physical legacy input table -> ECR table code (T3_00, T5_08, T11a_01 ...); $null when not an Air Land table.
    param([string]$PhysicalTable)
    switch -regex ($PhysicalTable) {
        '^AF_Air_Land_Stationary_Equipment$' { return 'T3_00' }
        '^AF_Air_Land_Mobile_Equipment$' { return 'T4_01' }
        '^AF_Air_Land_Other_Equipment_(5_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Dust_Producing_Activities_(6_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Utility_(9_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Off_Utility_DI_(10_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Off_Utility_AI_(10a_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Off_Process_DI_(11_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_11([abcd])_Off_Process' { return 'T11' + $Matches[1] + '_01' }
        default { return $null }
    }
}

function Get-LandParityMismatchClass {
    # Direction/ratio only: the class never carries the values themselves.
    param([double]$OldValue, [double]$EcrValue)
    if ($EcrValue -eq 0) { return 'MISMATCH_ECR_ZERO' }
    if ($OldValue -eq 0) { return 'MISMATCH_LEGACY_ZERO' }
    $q = $EcrValue / $OldValue
    foreach ($f in 1000.0, 3.6, 3600.0, 100.0, 10.0, 1e6) {
        if ([math]::Abs($q / $f - 1) -lt 1e-4) { return "MISMATCH_RATIO_x$f" }
        if ([math]::Abs($q * $f - 1) -lt 1e-4) { return "MISMATCH_RATIO_div$f" }
    }
    return 'MISMATCH'
}

function Get-LandParityValueClass {
    # Same tolerances as Compare-LandParity.ps1. -StorageTol may be scaled by the caller for sums.
    param([double]$OldValue, [double]$EcrValue, [double]$StorageTol = 5e-13, [double]$RelTol = 1e-12, [double]$AbsTol = 1e-9)
    $diff = [math]::Abs($OldValue - $EcrValue)
    $den = [math]::Max([math]::Abs($OldValue), [math]::Abs($EcrValue))
    $relative = if ($den -eq 0) { 0.0 } else { $diff / $den }
    if ($diff -le $StorageTol -or $relative -le $RelTol) {
        $c = 'MATCH'
        if ($relative -gt 1e-9) { $c = 'MATCH_ROUNDING_LOSS' }
    }
    elseif ($diff -le $AbsTol) { $c = 'NEAR' }
    else { $c = Get-LandParityMismatchClass $OldValue $EcrValue }
    return @{ Class = $c; Abs = $diff; Rel = $relative }
}

function Get-LandParityMissingClass {
    param([hashtable]$StateObject, [string]$MethodologyCode)
    $ms = $StateObject.MethState
    if (-not $ms.ContainsKey($MethodologyCode)) { return 'MISSING_NOT_IN_ECR' }
    if ($ms[$MethodologyCode][0] -eq 0) { return 'MISSING_NOT_PUBLISHED' }
    if ($ms[$MethodologyCode][1] -eq 0) { return 'MISSING_NO_BINDING' }
    return 'MISSING_BOUND_NO_RESULT'
}

function ConvertTo-LandParityMethodologyTableMap {
    # -MethodologyTable entries: '<regex>=<TableCode>[+<TableCode>...]', e.g. '^ECW_C12_01$=T11_02+T11a_01'.
    param([string[]]$Entry)
    $items = New-Object System.Collections.ArrayList
    foreach ($raw in @($Entry)) {
        foreach ($e in ([string]$raw -split ';')) {
            if ([string]::IsNullOrWhiteSpace($e)) { continue }
            $i = $e.LastIndexOf('=')
            if ($i -lt 1 -or $i -eq $e.Length - 1) { throw "MethodologyTable entry '$e' must be <regex>=<TableCode>[+<TableCode>]." }
            $rx = $e.Substring(0, $i).Trim()
            $codes = @($e.Substring($i + 1).Split('+') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            foreach ($t in $codes) {
                if ($t -notmatch '^T\d+[a-d]?_\d+$') { throw "MethodologyTable entry '$e': '$t' is not a table code (T3_00, T5_08, T11a_01 ...)." }
            }
            try { [void][regex]::new($rx) } catch { throw "MethodologyTable entry '$e': bad regex ($($_.Exception.Message))." }
            [void]$items.Add(@{ Regex = $rx; Tables = $codes })
        }
    }
    return , @($items.ToArray())
}

function ConvertTo-LandParityGroupByMap {
    # -GroupBy entries: '<TableCode>=<Column>[+<Column>...]' or '<Column>[+<Column>]' (all tables), e.g. 'T11_02=Land_TypeFuel'.
    # Result: hashtable TableCode|'*' -> string[] columns. The FIRST column is also the legacy Component_Phase filter.
    param([string[]]$Entry)
    $map = @{}
    foreach ($raw in @($Entry)) {
        foreach ($e in ([string]$raw -split ';')) {
            if ([string]::IsNullOrWhiteSpace($e)) { continue }
            $tbl = '*'; $colsPart = $e.Trim()
            $i = $e.IndexOf('=')
            if ($i -ge 0) { $tbl = $e.Substring(0, $i).Trim(); $colsPart = $e.Substring($i + 1).Trim() }
            if ($tbl -ne '*' -and $tbl -notmatch '^T\d+[a-d]?_\d+$') { throw "GroupBy entry '$e': '$tbl' is not a table code." }
            $cols = @($colsPart.Split('+') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            if ($cols.Count -eq 0) { throw "GroupBy entry '$e' has no columns." }
            foreach ($c in $cols) { if ($c -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "GroupBy entry '$e': bad column name '$c'." } }
            $map[$tbl] = $cols
        }
    }
    return $map
}

# Built-in fallback methodology -> table (the 6 patterns of the sample harness, as table codes).
$script:LandParityDefaultMethodologyTable = @(
    @{ Regex = '^ECW_C05_'; Tables = @('T3_00') }
    @{ Regex = '^ECW_C06_01'; Tables = @('T4_01') }
    @{ Regex = '^ECW_C07_07'; Tables = @('T5_08') }
    @{ Regex = '^ECW_C07_16'; Tables = @('T5_22') }
    @{ Regex = '^ECW_C07_08'; Tables = @('T5_09') }
    @{ Regex = '^ECW_C07_21'; Tables = @('T5_27') }
)

function New-LandParityState {
    return @{
        Ecr = @{}            # "doc|period|table|rowKey" -> @{ "meth|output" = double }
        EcrMeth = @{}        # "doc|period|table|meth" -> 1
        RowAny = @{}         # "doc|period|table|rowKey" -> 1
        Loaded = New-Object 'System.Collections.Generic.HashSet[string]'   # "doc|period|table|rowKey"
        LoadedCnt = @{}      # "doc|period|table" -> rows
        MaxRows = @{}        # table -> MaxDynamicRows
        MethState = @{}      # methodology code -> @(published, bound)
        MethTables = @{}     # methodology code -> string[] table codes (active bindings)
        MethodologyTable = @()   # overrides (ConvertTo-LandParityMethodologyTableMap)
        MultiTable = @{}     # methodology -> results attributed to several candidate tables
        ResultCount = 0
    }
}

function Resolve-LandParityResultTables {
    param([hashtable]$StateObject, [string]$DocumentName, [int]$PeriodKey, [string]$MethodologyCode, [string]$RowKey)
    $cands = @()
    if ($StateObject.MethTables.ContainsKey($MethodologyCode)) { $cands = @($StateObject.MethTables[$MethodologyCode]) }
    if ($cands.Count -eq 0) {
        foreach ($m in @($StateObject.MethodologyTable) + @($script:LandParityDefaultMethodologyTable)) {
            if ($MethodologyCode -match $m.Regex) { $cands = @($m.Tables); break }
        }
    }
    if ($cands.Count -eq 0) { return @('(unmapped)') }
    if ($cands.Count -eq 1) { return $cands }
    # The result row key carries no table: keep the candidates whose document row exists.
    $hit = @($cands | Where-Object { $StateObject.Loaded.Contains("$DocumentName|$PeriodKey|$_|$RowKey") })
    if ($hit.Count -eq 0) { $hit = $cands }
    if ($hit.Count -gt 1) {
        $mt = $StateObject.MultiTable
        $mcode = $MethodologyCode
        if (-not $mt.ContainsKey($mcode)) { $mt[$mcode] = 0 }
        $mt[$mcode]++
    }
    return $hit
}

function Add-LandParityEcrResult {
    param([hashtable]$StateObject, [string]$DocumentName, [int]$PeriodKey, [string]$MethodologyCode, [string]$RowKey, [string]$OutputCode, [double]$Value)
    $st = $StateObject
    foreach ($tc in (Resolve-LandParityResultTables $st $DocumentName $PeriodKey $MethodologyCode $RowKey)) {
        $mkey = "$DocumentName|$PeriodKey|$tc|$MethodologyCode"
        $st.EcrMeth[$mkey] = 1
        $k = "$DocumentName|$PeriodKey|$tc|$RowKey"
        $outKey = "$MethodologyCode|$OutputCode"
        if (-not $st.Ecr.ContainsKey($k)) { $st.Ecr[$k] = @{} }
        $st.Ecr[$k][$outKey] = $Value
        $st.RowAny[$k] = 1
    }
    $st.ResultCount++
}

function Import-LandParityStateFile {
    # Offline state (tests, archived runs). JSON:
    #  methodologies: [{code, published, bound, tables: ["T3_00"]}], tables: [{document, period, table, rowKeys: [..], maxRows}],
    #  results: [{document, period, methodology, rowKey, outputCode, value}]
    param([Parameter(Mandatory)][string]$Path, [hashtable]$TargetState)
    $st = $TargetState
    $j = ConvertFrom-LandParityJson ([System.IO.File]::ReadAllText($Path))
    $inv = [Globalization.CultureInfo]::InvariantCulture
    foreach ($m in @(Get-LandParityV $j 'methodologies')) {
        $code = [string](Get-LandParityV $m 'code')
        $st.MethState[$code] = @([int](Get-LandParityV $m 'published'), [int](Get-LandParityV $m 'bound'))
        $names = @(Get-LandParityV $m 'tables' | ForEach-Object { [string]$_ })
        if ($names.Count -gt 0) { $st.MethTables[$code] = $names }
    }
    foreach ($t in @(Get-LandParityV $j 'tables')) {
        $dn = [string](Get-LandParityV $t 'document'); $pk = [int](Get-LandParityV $t 'period'); $tc = [string](Get-LandParityV $t 'table')
        $cnt = 0
        foreach ($rk in @(Get-LandParityV $t 'rowKeys')) { [void]$st.Loaded.Add("$dn|$pk|$tc|$rk"); $cnt++ }
        $st.LoadedCnt["$dn|$pk|$tc"] = $cnt
        $mx = Get-LandParityV $t 'maxRows'
        if ($null -ne $mx) { $st.MaxRows[$tc] = [int]$mx }
    }
    foreach ($res in @(Get-LandParityV $j 'results')) {
        Add-LandParityEcrResult $st ([string](Get-LandParityV $res 'document')) ([int](Get-LandParityV $res 'period')) `
            ([string](Get-LandParityV $res 'methodology')) ([string](Get-LandParityV $res 'rowKey')) ([string](Get-LandParityV $res 'outputCode')) `
            ([double]::Parse([string](Get-LandParityV $res 'value'), $inv))
    }
}

function Import-LandParityStateSql {
    # Read-only SELECTs under READ UNCOMMITTED against a local EcrLand* stand database.
    param([Parameter(Mandatory)][string]$ConnectionString, [hashtable]$TargetState)
    $st = $TargetState
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $rd = {
        param([string]$SqlText, [scriptblock]$PerRow)
        $cn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString); $cn.Open()
        try {
            $cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 600
            $cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; $SqlText"
            $reader = $cmd.ExecuteReader()
            try { while ($reader.Read()) { & $PerRow $reader } } finally { $reader.Close() }
        }
        finally { $cn.Close() }
    }
    & $rd "SELECT m.Code, CASE WHEN EXISTS (SELECT 1 FROM calc.MethodologyVersion v WHERE v.MethodologyId = m.Id AND v.Status = 1) THEN 1 ELSE 0 END, CASE WHEN EXISTS (SELECT 1 FROM cfg.CalculationBinding b WHERE b.MethodologyId = m.Id AND b.IsActive = 1) THEN 1 ELSE 0 END FROM calc.Methodology m" {
        param($rec) $code = [string]$rec[0]; $st.MethState[$code] = @([int]$rec[1], [int]$rec[2])
    }
    $byCode = @{}
    & $rd "SELECT DISTINCT m.Code, td.Code FROM cfg.CalculationBinding b JOIN calc.Methodology m ON m.Id = b.MethodologyId JOIN cfg.TableDef td ON td.Id = b.TableDefId WHERE b.IsActive = 1" {
        param($rec) $k = [string]$rec[0]; if (-not $byCode.ContainsKey($k)) { $byCode[$k] = New-Object System.Collections.ArrayList }; [void]$byCode[$k].Add([string]$rec[1])
    }
    foreach ($k in $byCode.Keys) { $st.MethTables[$k] = @($byCode[$k].ToArray()) }
    & $rd "SELECT JSON_VALUE(d.NameL10n, '$.en'), tr.PeriodKey, td.Code, tr.RowKey, td.MaxDynamicRows FROM doc.TableRow tr JOIN doc.TableInstance ti ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey JOIN doc.Document d ON d.Id = ti.DocumentId JOIN cfg.TableDef td ON td.Id = ti.TableDefId WHERE tr.IsDeleted = 0" {
        param($rec)
        $tk = "$([string]$rec[0])|$([int]$rec[1])|$([string]$rec[2])"
        [void]$st.Loaded.Add("$tk|$([string]$rec[3])")
        $st.LoadedCnt[$tk] = 1 + [int]$st.LoadedCnt[$tk]
        $tcode = [string]$rec[2]
        if (-not $rec.IsDBNull(4)) { $st.MaxRows[$tcode] = [int]$rec[4] }
    }
    & $rd "SELECT JSON_VALUE(d.NameL10n, '$.en'), cr.PeriodKey, m.Code, cr.SourceRowKey, cr.OutputCode, cr.Value FROM calc.CalculationResult cr JOIN calc.CalculationRun r ON r.Id = cr.CalculationRunId AND r.Status = 'Current' JOIN calc.MethodologyVersion mv ON mv.Id = cr.MethodologyVersionId JOIN calc.Methodology m ON m.Id = mv.MethodologyId JOIN doc.Document d ON d.Id = cr.DocumentId" {
        param($rec)
        Add-LandParityEcrResult $st ([string]$rec[0]) ([int]$rec[1]) ([string]$rec[2]) ([string]$rec[3]) ([string]$rec[4]) ([double]::Parse([string]$rec[5], $inv))
    }
}

function Import-LandParityOracleTsv {
    # Columns: efid month landTable nae component phase tons gsec methodology modified [source]. LOCAL file with values.
    param([Parameter(Mandatory)][string]$Path)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $orc = @{}
    foreach ($ln in [System.IO.File]::ReadLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($ln)) { continue }
        $f = $ln.Split("`t")
        if ($f.Count -lt 10) { continue }
        $k = "$($f[0])|$([int]$f[1])|$($f[2])|$($f[3].Trim())"
        if (-not $orc.ContainsKey($k)) { $orc[$k] = New-Object System.Collections.ArrayList }
        $tons = $null; $gsec = $null
        if ($f[6] -ne '') { $tons = [double]::Parse($f[6], $inv) }
        if ($f[7] -ne '') { $gsec = [double]::Parse($f[7], $inv) }
        [void]$orc[$k].Add(@{ component = $f[4]; phase = $f[5]; tons = $tons; gsec = $gsec; methodology = $f[8]; modified = $f[9] })
    }
    return $orc
}

function New-LandParityAccumulator {
    return @{
        Agg = @{}                                   # "meth|table|class" -> counters
        Rels = @{}                                  # same key -> List[double]
        Detail = New-Object 'System.Collections.Generic.List[string]'
        Docs = @{}; Periods = @{}
        AggregateGroups = 0
    }
}

function Add-LandParityCell {
    param([hashtable]$Accumulator, [string]$DocumentName, [int]$PeriodKey, [string]$TableCode, [string]$RowKey, [int]$RowCount, [string]$EfidValue,
        [string]$MethodologyCode, [string]$OutputLabel, [string]$ClassName, [double]$RelErr = 0.0)
    $ac = $Accumulator
    $k = "$MethodologyCode|$TableCode|$ClassName"
    if (-not $ac.Agg.ContainsKey($k)) {
        $ac.Agg[$k] = @{ Methodology = $MethodologyCode; Table = $TableCode; Class = $ClassName; Cells = 0; Rows = 0; Docs = @{}; Periods = @{}; Sample = $null }
    }
    $a = $ac.Agg[$k]
    $dn = $DocumentName; $pks = [string]$PeriodKey
    $a.Cells++; $a.Rows += $RowCount; $a.Docs[$dn] = 1; $a.Periods[$pks] = 1
    $ac.Docs[$dn] = 1; $ac.Periods[$pks] = 1
    $isMatch = ($ClassName -eq 'MATCH' -or $ClassName -eq 'AGG_MATCH')
    if ($null -eq $a.Sample -and $ClassName -notmatch '^(AGG_)?MATCH') { $a.Sample = "$DocumentName/$PeriodKey/$TableCode/$RowKey" }
    if ($ClassName -match '^(AGG_)?(MATCH|MISMATCH|NEAR)') {
        if (-not $ac.Rels.ContainsKey($k)) { $ac.Rels[$k] = New-Object 'System.Collections.Generic.List[double]' }
        $ac.Rels[$k].Add($RelErr)
    }
    if (-not $isMatch) {
        $ac.Detail.Add(($DocumentName, $PeriodKey, $TableCode, $RowKey, $RowCount, $EfidValue, $MethodologyCode, $OutputLabel, $ClassName, $RelErr.ToString('R', [Globalization.CultureInfo]::InvariantCulture)) -join "`t")
    }
}

function Get-LandParityPhaseColumns {
    param([string[]]$Columns, [string]$TableCode, [hashtable]$GroupByMap)
    if ($GroupByMap.ContainsKey($TableCode)) { return @($GroupByMap[$TableCode]) }
    if ($GroupByMap.ContainsKey('*')) { return @($GroupByMap['*']) }
    $rx = '^Land_Type(Fuel|FuelOrStream|StorMater|StorMaterial|StoredMaterial|BurnFuel|PumpedLiquid)$'
    $c = @($Columns | Where-Object { $_ -match $rx } | Select-Object -First 1)
    return $c
}

function Invoke-LandParityCompare {
    <#
    .PARAMETER DocumentFile  full-set sample files: {doc, periods: {"2026-01": {<AF table>: {columns, rows:[{_efid, Land_NAE, ...}]}}}}
    .PARAMETER Option        Months (int[]), StorageAbs, TolRel, TolAbs, AggregateBy ('None'|'NaePhase'), GroupBy (hashtable)
    #>
    param([Parameter(Mandatory)][string[]]$DocumentFile, [Parameter(Mandatory)][hashtable]$StateObject, [Parameter(Mandatory)][hashtable]$Oracle,
        [Parameter(Mandatory)][hashtable]$Accumulator, [hashtable]$Option = @{}, [scriptblock]$Progress)
    $st = $StateObject; $ac = $Accumulator
    $months = @(); if ($Option.ContainsKey('Months')) { $months = @($Option.Months) }
    $stAbs = 5e-13; if ($Option.ContainsKey('StorageAbs')) { $stAbs = [double]$Option.StorageAbs }
    $tolRel = 1e-12; if ($Option.ContainsKey('TolRel')) { $tolRel = [double]$Option.TolRel }
    $tolAbs = 1e-9; if ($Option.ContainsKey('TolAbs')) { $tolAbs = [double]$Option.TolAbs }
    $aggMode = 'None'; if ($Option.ContainsKey('AggregateBy')) { $aggMode = [string]$Option.AggregateBy }
    $groupByOpt = @{}; if ($Option.ContainsKey('GroupBy')) { $groupByOpt = $Option.GroupBy }
    $fi = 0

    foreach ($file in $DocumentFile) {
        $fi++
        $d = ConvertFrom-LandParityJson ([System.IO.File]::ReadAllText($file))
        $docName = [string](Get-LandParityV $d 'doc')
        $n = [int]($docName -replace '^LAND-PROD-', '')
        $periods = Get-LandParityV $d 'periods'
        if ($null -eq $periods) { continue }
        foreach ($ym in @($periods.Keys)) {
            $mo = [int]([string]$ym).Substring(5, 2)
            if ($months.Count -gt 0 -and $months -notcontains $mo) { continue }
            $pk = [int](([string]$ym).Replace('-', ''))
            $tables = $periods[$ym]
            foreach ($pt in @($tables.Keys)) {
                if ($pt -notlike 'AF_Air_Land*') { continue }
                $tbl = Get-LandParityTableCode $pt
                if (-not $tbl) { continue }
                $tdat = $tables[$pt]
                $cols = @(Get-LandParityV $tdat 'columns' | ForEach-Object { [string]$_ })
                $srcRows = @(Get-LandParityV $tdat 'rows')
                $phaseCols = @(Get-LandParityPhaseColumns $cols $tbl $groupByOpt)
                $hasNae = $cols -contains 'Land_NAE'; $hasNo = $cols -contains 'Land_No'
                $tk = "$docName|$pk|$tbl"

                # ambiguity per (efid, NAE, phase): several DIFFERENT Land_No share the key
                $members = @{}; $i = 0
                $infos = New-Object System.Collections.ArrayList
                foreach ($r in $srcRows) {
                    $i++
                    $efid = [string](Get-LandParityV $r '_efid')
                    $nae = ''; $ph = ''; $gk = ''
                    if ($hasNae) {
                        $nae = [string](Get-LandParityV $r 'Land_NAE')
                        if ($phaseCols.Count -gt 0) {
                            $ph = [string](Get-LandParityV $r $phaseCols[0])
                            $gk = (@($phaseCols | ForEach-Object { [string](Get-LandParityV $r $_) })) -join '|'
                        }
                    }
                    $gkey = "$efid|$nae|$gk"
                    $ln = "#$i"; if ($hasNo) { $ln = [string](Get-LandParityV $r 'Land_No') }
                    if (-not $members.ContainsKey($gkey)) { $members[$gkey] = @{ Nos = @{}; Rows = New-Object System.Collections.ArrayList } }
                    $members[$gkey].Nos[$ln] = 1
                    $rk = 'p{0}-{1:0000}' -f $n, $i
                    [void]$members[$gkey].Rows.Add($rk)
                    [void]$infos.Add(@{ Row = $r; Efid = $efid; Nae = $nae; Ph = $ph; Key = $gkey; RowKey = $rk })
                }
                $groupDone = @{}

                foreach ($info in $infos) {
                    $nae = $info.Nae; $efid = $info.Efid; $rk = $info.RowKey; $ph = $info.Ph
                    if ($nae -eq '') { continue }
                    $mem = $members[$info.Key]
                    $amb = ($mem.Nos.Count -gt 1)
                    $ok = "$efid|$mo|$pt|$($nae.Trim())"
                    if (-not $Oracle.ContainsKey($ok)) { continue }      # no stored legacy result: nothing to compare
                    if ($amb -and $aggMode -eq 'NaePhase') {
                        if ($groupDone.ContainsKey($info.Key)) { continue }
                        $groupDone[$info.Key] = 1
                    }
                    $rs = @($Oracle[$ok])
                    if ($ph -ne '') {
                        $nr = @($rs | Where-Object { $_.phase.Trim() -eq $ph.Trim() })
                        if ($nr.Count -gt 0) { $rs = $nr }
                    }
                    $legacy = @{}
                    foreach ($lr in $rs) {
                        $lk = "$($lr.methodology)|$($lr.component)"
                        if (-not $legacy.ContainsKey($lk)) { $legacy[$lk] = New-Object System.Collections.ArrayList }
                        [void]$legacy[$lk].Add($lr)
                    }
                    $grouped = ($amb -and $aggMode -eq 'NaePhase')
                    if ($grouped) { $ac.AggregateGroups++ }
                    $rowKeys = @($rk)
                    if ($grouped) { $rowKeys = @($mem.Rows.ToArray()) }
                    $nRows = $rowKeys.Count
                    $pre = ''; if ($grouped) { $pre = 'AGG_' }
                    $stV = Get-LandParityV $info.Row 'Land_Status'

                    # loaded / ECR state of the members
                    $ecrRows = @(); $notLoaded = 0; $anyRow = $false
                    foreach ($mk in $rowKeys) {
                        if (-not $st.Loaded.Contains("$tk|$mk")) { $notLoaded++ }
                        $er = $null; if ($st.Ecr.ContainsKey("$tk|$mk")) { $er = $st.Ecr["$tk|$mk"] }
                        if ($null -ne $er) { $ecrRows += , $er; $anyRow = $true }
                    }
                    $notLoadedClass = 'ROW_NOT_LOADED_NO_CELLS'
                    $mxr = $null; if ($st.MaxRows.ContainsKey($tbl)) { $mxr = $st.MaxRows[$tbl] }
                    $cnt = 0; if ($st.LoadedCnt.ContainsKey($tk)) { $cnt = [int]$st.LoadedCnt[$tk] }
                    if ($mxr -and $cnt -ge $mxr) { $notLoadedClass = 'ROW_NOT_LOADED_CAPPED' }

                    $seen = @{}
                    foreach ($lk in $legacy.Keys) {
                        $meth, $comp = $lk -split '\|', 2
                        $copies = @($legacy[$lk] | Sort-Object { $_.modified })
                        $latest = $copies[-1]
                        foreach ($metric in 'tons', 'gsec') {
                            $outCode = "ECW_EC_${metric}_$comp"; $lab = "${metric}_$comp"
                            $oldV = $latest[$metric]
                            if ($amb -and -not $grouped) { Add-LandParityCell $ac $docName $pk $tbl $rk 1 $efid $meth $lab 'AMBIGUOUS_KEY'; continue }
                            if ($copies.Count -gt 1) {
                                $vals = @($copies | ForEach-Object { $_[$metric] } | Where-Object { $null -ne $_ } | Select-Object -Unique)
                                if ($vals.Count -gt 1) { Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + 'LEGACY_DUP_CONFLICT'); continue }
                            }
                            if ($null -eq $oldV) { continue }
                            $ek = "$meth|$outCode"
                            # ECR side: the row value, or the SUM over the group
                            $sum = 0.0; $have = 0; $diffCount = 0
                            foreach ($er in $ecrRows) {
                                if ($er.ContainsKey($ek)) { $sum += [double]$er[$ek]; $have++; $seen[$ek] = 1 }
                                else {
                                    foreach ($ck in $er.Keys) {
                                        if ($ck.EndsWith("|$outCode", [System.StringComparison]::Ordinal)) { $diffCount++; $seen[$ck] = 1; break }
                                    }
                                }
                            }
                            if ($have -eq 0) {
                                if ($diffCount -gt 0) { Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + 'METHODOLOGY_DIFF'); continue }
                                if ($notLoaded -gt 0 -and -not $anyRow) { $cls = $pre + $notLoadedClass }
                                else {
                                    $tm = $st.EcrMeth.ContainsKey("$tk|$meth")
                                    if (-not $tm) { $cls = Get-LandParityMissingClass $st $meth }
                                    elseif (-not $anyRow) { $cls = 'MISSING_ROW' }
                                    else { $cls = 'MISSING_OUTPUT' }
                                    if ($cls -in 'MISSING_ROW', 'MISSING_OUTPUT' -and $null -ne $stV -and [string]$stV -ne '' -and [string]$stV -ne 'Running') { $cls = 'LEGACY_STATUS_SKIP' }
                                    $cls = $pre + $cls
                                }
                                if ([double]$oldV -eq 0) { $cls = $cls + '_ZERO' }
                                Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab $cls
                                continue
                            }
                            $scaled = $stAbs * [math]::Max(1, $have)
                            $vc = Get-LandParityValueClass ([double]$oldV) $sum $scaled $tolRel $tolAbs
                            $cls = $vc.Class
                            if ($grouped -and $cls -like 'MISMATCH*' -and $notLoaded -gt 0) { $cls = 'INCOMPLETE_' + $notLoadedClass }
                            Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + $cls) $vc.Rel
                        }
                    }
                    # ECR outputs the legacy store does not have (aggregated over the group)
                    $onlySum = @{}
                    foreach ($er in $ecrRows) {
                        foreach ($ck in $er.Keys) {
                            if ($seen.ContainsKey($ck)) { continue }
                            if (-not $onlySum.ContainsKey($ck)) { $onlySum[$ck] = 0.0 }
                            $onlySum[$ck] += [double]$er[$ck]
                        }
                    }
                    foreach ($ck in $onlySum.Keys) {
                        $meth, $outCode = $ck -split '\|', 2
                        $lab = $outCode -replace '^ECW_EC_', ''
                        if ($lab -match '_NOx$') { $cls = 'ECR_ONLY_INTERMEDIATE' } elseif ($onlySum[$ck] -eq 0) { $cls = 'ECR_ONLY_ZERO' } else { $cls = 'ECR_ONLY' }
                        if ($amb -and -not $grouped) { $cls = 'AMBIGUOUS_KEY' }
                        Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + $cls)
                    }
                }
            }
        }
        if ($Progress) { & $Progress $fi $DocumentFile.Count }
    }
}

function Get-LandParityPercentile {
    param($Values, [double]$Fraction)
    if ($Values.Count -eq 0) { return $null }
    $s = $Values.ToArray(); [Array]::Sort($s)
    return $s[[int][math]::Min($s.Length - 1, [math]::Floor($Fraction * ($s.Length - 1)))]
}

function Get-LandParitySummary {
    # Identifiers, counters, classes and relative errors only.
    param([Parameter(Mandatory)][hashtable]$Accumulator, [hashtable]$StateObject)
    $ac = $Accumulator
    $groups = @(foreach ($k in $ac.Agg.Keys) {
            $a = $ac.Agg[$k]; $l = $null
            if ($ac.Rels.ContainsKey($k)) { $l = $ac.Rels[$k] }
            [ordered]@{
                methodology = $a.Methodology; table = $a.Table; class = $a.Class; cells = [int]$a.Cells; rows = [int]$a.Rows
                docs = [int]$a.Docs.Count; periods = (@($a.Periods.Keys | Sort-Object) -join ','); sample = $a.Sample
                relMedian = $(if ($l) { Get-LandParityPercentile $l 0.5 } else { $null })
                relP95 = $(if ($l) { Get-LandParityPercentile $l 0.95 } else { $null })
                relMax = $(if ($l) { Get-LandParityPercentile $l 1.0 } else { $null })
            }
        })
    $groups = @($groups | Sort-Object { $_.class }, { $_.methodology }, { $_.table })
    $byClass = @{}; $byMeth = @{}
    foreach ($g in $groups) {
        if (-not $byClass.ContainsKey($g.class)) { $byClass[$g.class] = 0 }
        $byClass[$g.class] += $g.cells
        if (-not $byMeth.ContainsKey($g.methodology)) { $byMeth[$g.methodology] = @{} }
        if (-not $byMeth[$g.methodology].ContainsKey($g.class)) { $byMeth[$g.methodology][$g.class] = 0 }
        $byMeth[$g.methodology][$g.class] += $g.cells
    }
    $classes = @($byClass.Keys | Sort-Object { - $byClass[$_] } | ForEach-Object { [ordered]@{ class = $_; cells = [int]$byClass[$_] } })
    $byMethodology = @($byMeth.Keys | Sort-Object | ForEach-Object {
            $mc = $_
            $cl = @($byMeth[$mc].Keys | Sort-Object { - $byMeth[$mc][$_] } | ForEach-Object { [ordered]@{ class = $_; cells = [int]$byMeth[$mc][$_] } })
            [ordered]@{ methodology = $mc; cells = [int](($cl | ForEach-Object { $_.cells } | Measure-Object -Sum).Sum); classes = $cl }
        })
    $multi = @()
    if ($null -ne $StateObject) { $multi = @($StateObject.MultiTable.Keys | Sort-Object | ForEach-Object { [ordered]@{ methodology = $_; results = [int]$StateObject.MultiTable[$_] } }) }
    return [ordered]@{
        totalCells = [int](($classes | ForEach-Object { $_.cells } | Measure-Object -Sum).Sum)
        classes = $classes
        byMethodology = $byMethodology
        multiTableResults = $multi
        groups = $groups
    }
}
