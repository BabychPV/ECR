# tools/hse30x/SgFlaringParity.Lib.ps1
# SGF-flaring AC-1: pure comparison logic of Compare-SgFlaringParity.ps1 (no database), shared with SgFlaringParity.Tests.ps1.
# Rows are objects with Kind, Key, Column, Value (and Month for legacy rows).

Set-StrictMode -Version 2.0

function Get-SgPeriodWindow {
    <# ECR period keys inside [From, To): monthly YYYYMM with From <= key < To; yearly YYYY00 only when the whole year is inside. #>
    param([datetime]$From, [datetime]$To)
    $fromKey = [int]$From.ToString('yyyyMM')
    $toKey = [int]$To.ToString('yyyyMM')
    return [pscustomobject]@{ FromKey = $fromKey; ToKey = $toKey
        Sql = "((r.PeriodKey % 100 <> 0 AND r.PeriodKey >= $fromKey AND r.PeriodKey < $toKey) OR (r.PeriodKey % 100 = 0 AND r.PeriodKey + 1 >= $fromKey AND r.PeriodKey + 12 < $toKey))" }
}

function ConvertTo-SgEcrRows {
    <#
    ECR result rows (Kind, RowKey, EventId, Column, Value, SubstanceEntryId) -> comparison rows.
    INT key = EFID; CONT key = EFID|FCID where FCID = the row's SGF_FCID output (LEGACY_COMPOSITION_ID of the resolved case),
    the same as legacy CalcResult_FlaringsContinuous_SG EFID|FCID; TOX key = the TOX row key. A substance-specific output keeps
    its substance in the column name, so values never collapse.
    #>
    param([object[]]$Rows)
    $fcid = @{}
    foreach ($r in $Rows) {
        if ($r.Column -eq 'SGF_FCID') { $fcid["$($r.Kind)|$($r.RowKey)"] = [string][int64][double]$r.Value }
    }

    foreach ($r in $Rows) {
        if ($r.Column -eq 'SGF_FCID') { continue }
        $eventId = if ($r.EventId) { $r.EventId } else { $r.RowKey }
        $key = switch ($r.Kind) {
            'CONT' { $f = $fcid["$($r.Kind)|$($r.RowKey)"]; if ($f) { "$eventId|$f" } else { "$eventId|?" } }
            'TOX' { $r.RowKey }
            default { $eventId }
        }
        $column = if ($r.SubstanceEntryId) { "$($r.Column)#$($r.SubstanceEntryId)" } else { $r.Column }
        [pscustomobject]@{ Kind = $r.Kind; Key = $key; Column = $column; Value = $r.Value }
    }
}

function Find-SgDuplicates {
    <# Keys (Kind|Key|Column) that occur more than once - a parity run with duplicates must fail, not overwrite. #>
    param([object[]]$Rows, [string]$Side)
    @($Rows | Group-Object { "$($_.Kind)|$($_.Key)|$($_.Column)" } | Where-Object Count -gt 1 |
        ForEach-Object { "$Side duplicate: $($_.Name) x$($_.Count)" })
}

function Compare-SgParity {
    <# Compares legacy and ECR rows; returns rows with Status equal|different|missingInEcr|missingInLegacy and Known class. #>
    # D-580 (human 26.11, review M3): a case without temperature is missing data in ECR (row rejected); legacy computed it at 0 C.
    # Legacy INT/CONT rows whose FCID is such a case are a KNOWN difference "M3-noTemperature", not a defect.
    param([object[]]$Legacy, [object[]]$Ecr, [double]$RelTolerance = 1e-6, [string[]]$NoTemperatureFcids = @())
    $noTemperature = @{}
    foreach ($id in $NoTemperatureFcids) { $noTemperature[[string]$id] = $true }
    $byKey = @{}
    foreach ($r in $Ecr) { $byKey["$($r.Kind)|$($r.Key)|$($r.Column)"] = $r }
    $seen = @{}
    foreach ($l in $Legacy) {
        $k = "$($l.Kind)|$($l.Key)|$($l.Column)"
        $seen[$k] = $true
        $e = $byKey[$k]
        $status = if ($null -eq $e) { 'missingInEcr' } else {
            $a = [double]$l.Value; $b = [double]$e.Value
            $den = [Math]::Max([Math]::Abs($a), [Math]::Abs($b))
            if ($den -eq 0 -or [Math]::Abs($a - $b) / $den -le $RelTolerance) { 'equal' } else { 'different' }
        }
        $known = ''
        if ($status -ne 'equal' -and $l.Kind -eq 'TOX' -and $l.Key -match '\|Train 1\|' -and $l.Key -match '\|481') { $known = 'L6' }
        if ($status -ne 'equal' -and $l.Kind -eq 'TOX' -and $l.Key -match '\|EOR\|') { $known = 'H3/L1' }
        $fcid = if ($l.PSObject.Properties['Fcid']) { [string]$l.Fcid } else { $null }
        if ($status -ne 'equal' -and $l.Kind -in @('INT', 'CONT') -and $fcid -and $noTemperature.ContainsKey($fcid)) { $known = 'M3-noTemperature' }
        [pscustomobject]@{ Kind = $l.Kind; Key = $l.Key; Column = $l.Column; Legacy = $l.Value
            Ecr = $(if ($e) { $e.Value } else { $null }); Status = $status; Known = $known }
    }
    foreach ($r in $Ecr) {
        $k = "$($r.Kind)|$($r.Key)|$($r.Column)"
        if (-not $seen.ContainsKey($k)) {
            [pscustomobject]@{ Kind = $r.Kind; Key = $r.Key; Column = $r.Column; Legacy = $null; Ecr = $r.Value
                Status = 'missingInLegacy'; Known = $(if ($r.Kind -eq 'TOX') { 'L7?' } else { '' }) }
        }
    }
}
