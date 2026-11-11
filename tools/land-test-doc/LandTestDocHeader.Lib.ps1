<#
    Pure helpers of the contract-header section of Fill-LandTestDocument.ps1 (no network; tested by
    tools/tests/Land-TestDocHeader.Tests.ps1).

    Header model (template Land, tab "2. Contract"): document header fields (cfg.HeaderFieldDef), NOT table cells.
    Read  GET   /api/v1/documents/{id}/header -> { fields: [{ headerFieldDefId, code, label, dataType, isRequired, value,
                                                              lookupRegistryDefId }], version }
    Write PATCH /api/v1/documents/{id}/header  { fields: [{ code, value, isEmpty }], baseVersion = version of the GET }
    A Lookup field takes the registry entry id (number); a String field takes text.
#>

$script:LandTestHeaderKinds = @('lookup', 'text', 'number', 'date')
# kind of the data file -> dataType of the header field (GET .../header, enum names).
$script:LandTestHeaderDataTypes = @{ lookup = @('Lookup'); text = @('String'); number = @('Int', 'Decimal'); date = @('Date') }

function Read-LandTestHeader {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Header file not found: $Path" }
    $items = @(Import-Csv -LiteralPath $Path -Encoding UTF8)
    if ($items.Count -eq 0) { throw "Header file is empty: $Path" }
    foreach ($h in 'code', 'kind', 'value', 'source') {
        if ($items[0].PSObject.Properties.Match($h).Count -eq 0) { throw "Header file has no column '$h'." }
    }
    return $items
}

function Test-LandTestHeaderPlaceholder {
    <# A value written as <...> (e.g. <PICK:LAND_AREA>, <PERMIT>) is a placeholder, i.e. not supplied yet. #>
    param([string]$Value)
    return (-not [string]::IsNullOrWhiteSpace($Value)) -and $Value.Trim() -match '^<[^<>]*>$'
}

function Set-LandTestHeaderPermit {
    <#
        Replaces the value <PERMIT> with -PermitNumber (the real number stays out of git, as in Build-LandTemplate.ps1).
        Returns copies; the input objects are not changed.
    #>
    param([Parameter(Mandatory)]$Items, [string]$PermitNumber)
    $out = New-Object System.Collections.ArrayList
    foreach ($i in @($Items)) {
        $c = [pscustomobject]@{ code = [string]$i.code; kind = [string]$i.kind; value = [string]$i.value; source = [string]$i.source }
        if (-not [string]::IsNullOrWhiteSpace($PermitNumber) -and $c.value.Trim() -eq '<PERMIT>') { $c.value = $PermitNumber.Trim() }
        [void]$out.Add($c)
    }
    return , @($out)
}

function Test-LandTestHeader {
    <# Problems of the header data (missing/placeholder value, missing source, bad kind, duplicate, bad number/date). Empty = complete. #>
    param([Parameter(Mandatory)]$Items)
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($c in @($Items)) {
        $code = [string]$c.code
        if ([string]::IsNullOrWhiteSpace($code)) { $problems.Add('HEADER EMPTY CODE'); continue }
        if ($seen.ContainsKey($code)) { $problems.Add("HEADER DUPLICATE $code") }
        $seen[$code] = 1
        if ($script:LandTestHeaderKinds -notcontains [string]$c.kind) { $problems.Add("HEADER BAD KIND ${code}: '$($c.kind)'") }
        if ([string]::IsNullOrWhiteSpace($c.value)) { $problems.Add("HEADER MISSING VALUE $code [$($c.kind)]") }
        elseif (Test-LandTestHeaderPlaceholder $c.value) { $problems.Add("HEADER MISSING VALUE $code [$($c.kind)] placeholder $($c.value.Trim()) ($($c.source))") }
        elseif ([string]::IsNullOrWhiteSpace($c.source)) { $problems.Add("HEADER MISSING SOURCE $code = $($c.value)") }
        if ([string]::IsNullOrWhiteSpace($c.value) -or (Test-LandTestHeaderPlaceholder $c.value)) { continue }
        if ($c.kind -eq 'number') {
            $d = 0.0
            if (-not [double]::TryParse($c.value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $problems.Add("HEADER NOT A NUMBER ${code}: '$($c.value)'") }
        }
        if ($c.kind -eq 'date') {
            $dt = [DateTime]::MinValue
            if (-not [DateTime]::TryParseExact($c.value.Trim(), 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$dt)) { $problems.Add("HEADER NOT A DATE ${code}: '$($c.value)' (yyyy-MM-dd)") }
        }
    }
    return , $problems
}

function ConvertTo-LandTestHeaderComparable {
    <# Current or desired header value as text: id/number invariant, date yyyy-MM-dd, text as is, null = ''. #>
    param($Value, [string]$Kind)
    if ($null -eq $Value) { return '' }
    if ($Kind -eq 'date') {
        if ($Value -is [DateTime]) { return $Value.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) }
        $s = ([string]$Value).Trim()
        if ($s.Length -ge 10 -and $s.Substring(0, 10) -match '^\d{4}-\d{2}-\d{2}$') { return $s.Substring(0, 10) }
        return $s
    }
    if ($Kind -in 'lookup', 'number') {
        if ($Value -is [double] -or $Value -is [decimal] -or $Value -is [int] -or $Value -is [long]) {
            return ([decimal]$Value).ToString('G29', [Globalization.CultureInfo]::InvariantCulture)
        }
        $d = [decimal]0
        if ([decimal]::TryParse([string]$Value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d.ToString('G29', [Globalization.CultureInfo]::InvariantCulture) }
    }
    return [string]$Value
}

function Get-LandTestHeaderPlan {
    <#
        Diff of the header data against GET /documents/{id}/header.
        -ResolveEntry: { param($RegistryDefId, $Value) -> entry id } (network in the script, a stub in tests).
        Returns @{ Changes = @(@{ Code; Kind; Value; Current; Registry }); Unchanged = n }. Throws when a field is
        absent from the template version, read-only kind mismatch, or a lookup field without a registry.
        Items with an empty/placeholder value are skipped (plan with incomplete data).
    #>
    param([Parameter(Mandatory)]$Items, [Parameter(Mandatory)]$Header, [Parameter(Mandatory)][scriptblock]$ResolveEntry)
    $byCode = @{}
    foreach ($f in @($Header.fields)) { if ($null -ne $f) { $byCode[[string]$f.code] = $f } }
    $changes = New-Object System.Collections.ArrayList
    $unchanged = 0
    foreach ($c in @($Items)) {
        $code = [string]$c.code
        if (-not $byCode.ContainsKey($code)) { throw "Header field '$code' is not in the template version of the document (fields: $((@($byCode.Keys) | Sort-Object) -join ', '))." }
        $f = $byCode[$code]
        $dt = [string]$f.dataType
        $allowed = $script:LandTestHeaderDataTypes[[string]$c.kind]
        if ($null -ne $allowed -and $dt -notmatch '^\d+$' -and $allowed -notcontains $dt) { throw "Header field '$code' is $dt in the template; the data file says kind '$($c.kind)'." }
        if ([string]::IsNullOrWhiteSpace($c.value) -or (Test-LandTestHeaderPlaceholder $c.value)) { continue }
        $desired = $null
        $registry = $null
        if ($c.kind -eq 'lookup') {
            $registry = $f.lookupRegistryDefId
            if ($null -eq $registry) { throw "Header field '$code' has no lookup registry." }
            $desired = [long](& $ResolveEntry $registry ([string]$c.value).Trim())
        }
        elseif ($c.kind -eq 'number') { $desired = [decimal]::Parse($c.value, [Globalization.CultureInfo]::InvariantCulture) }
        elseif ($c.kind -eq 'date') { $desired = ([string]$c.value).Trim() }
        else { $desired = [string]$c.value }
        $current = ConvertTo-LandTestHeaderComparable $f.value $c.kind
        if ($current -ceq (ConvertTo-LandTestHeaderComparable $desired $c.kind)) { $unchanged++; continue }
        $wire = $desired
        if ($c.kind -eq 'number') { $wire = $desired.ToString([Globalization.CultureInfo]::InvariantCulture) }
        [void]$changes.Add([pscustomobject]@{ Code = $code; Kind = [string]$c.kind; Value = $wire; Current = $current; Registry = $registry })
    }
    return [pscustomobject]@{ Changes = @($changes); Unchanged = $unchanged }
}

function New-LandTestHeaderPatchBody {
    <# Body of PATCH /documents/{id}/header: only the changed fields, baseVersion = version of the GET. #>
    param([Parameter(Mandatory)]$Changes, [Parameter(Mandatory)][string]$BaseVersion)
    $fields = @(@($Changes) | Where-Object { $null -ne $_ } | ForEach-Object { @{ code = [string]$_.Code; value = $_.Value; isEmpty = $false } })
    return @{ fields = $fields; baseVersion = $BaseVersion }
}

function Select-LandTestHeaderEntry {
    <#
        Entry id for a header lookup value among the entries of the document's window
        (GET /registries/{code}/entries?documentId=). Match: code (exact), else display (exact, trimmed) - permit
        type headers (PRM_*) carry the permit number as display, their code is generated. Ambiguous display = error.
    #>
    param([Parameter(Mandatory)]$Entries, [Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$RegistryCode)
    $list = @(@($Entries) | Where-Object { $null -ne $_ })
    $byCode = @($list | Where-Object { [string]$_.code -ceq $Value })
    if ($byCode.Count -eq 1) { return [long]$byCode[0].id }
    $byDisplay = @($list | Where-Object { ([string]$_.display).Trim() -ceq $Value })
    if ($byDisplay.Count -eq 1) { return [long]$byDisplay[0].id }
    if ($byDisplay.Count -gt 1) { throw "Registry $RegistryCode has $($byDisplay.Count) usable entries displayed '$Value' (ids $(($byDisplay | ForEach-Object { $_.id }) -join ', ')); put the entry code in the header file." }
    throw "Registry $RegistryCode has no entry '$Value' usable in the document's project window (active, valid at least one day of it)."
}
