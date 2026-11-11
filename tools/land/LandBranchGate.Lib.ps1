# tools/land/LandBranchGate.Lib.ps1
# AN-171/AN-172: one branch per (row, substance, metric), as in the legacy code; substance lists for methodologies whose
# legacy class keeps a flat list. Pure logic, no API calls, no production values.
#
# Legacy writes ONE branch (`_type1`, `_type2`, ...) per row, substance and metric (tons / gsec), chosen by NAE, stream or
# substance. In ECR every branch is an output, so every branch that computes is written: double counting where the wrong
# branch computes (live doc 96, LTD-A17: tons_type1 + tons_type3), failed steps where it does not (LTD-A40). Same fix as
# D-423 / D-435 (LandNaeType.Lib.ps1): the branch expression is wrapped
#   `if(<condition of the branch>, <expression>, NULL)`
# so a branch not selected for the row gives no number (an output without a number is not written,
# GenericCalculationModule.Emit). Where the legacy code compares exact values the condition is `in()` over them; where it
# uses `string.Contains` (ECW_C11_01) the condition is built with `CONTAINS(text, fragment)` (AN-165: ordinal,
# case-sensitive, NULL/empty text -> FALSE). Contract and migrations do not change.
#
# Selectors (sources: docs/source/.../R_EmissionCalculationWork/ECW_C11_OPF_Plant_Operational/*.cs,
# .../ECW_C12_OFFSHORE_OPERATIONAL/ECW_C12_01.cs; the same lists as the parity harness, LandParity.Lib.ps1
# Get-LandParitySelectedBranch):
#   nae        legacy `Land_NAE.Trim().Replace(" ", "")`, exact List.Contains. In `in()` each value is listed as written and,
#              for a value with a `(...)` tail, also with a space before `(` (the data has both spellings). A list value that
#              itself contains a space never matches in the legacy code, so it is dropped.
#   substance  `CURRENT_SUBSTANCE()` (legacy dictionaries by pollutant code, ECW_C11_11_04.cs:82-91).
#   stream     `@Land_Streams` exact value (ECW_C12_01.cs:121-128 typeOfStreamsInRDS, :151 type2 only for FuelGas).
#   streamContains  ECW_C11_01 (AN-165), ECW_C11_01.cs:105-117, :157-160, :176-208:
#              - `Land_NAE` (Trim, spaces removed) Contains one of 6646..6657 -> type2;
#              - otherwise by `streamfuel`, set from `Land_TypeFuelOrStream` by a chain of `Contains` checks where the LAST
#                match wins (gas|Gas -> FG, LPG, sludge, caustic, water -> Wwater, 401, 14, 452, Diesel); no match ->
#                streamfuel = the argument itself; FG/LPG -> type1_1, Diesel/452 -> type1_2, everything else -> type1_3.
#              "Last match is in set A" = OR over markers k of A: CONTAINS(k) and not (any LATER marker outside A), plus
#              `in(arg, code)` for a code of A that holds no marker fragment (only 'FG': the argument itself).
#              The gate reads the raw `@Land_NAE`: unlike the legacy code it does not remove spaces, so it differs only for
#              a NAE with a space INSIDE the four digits. The harness fallback to Land_TypeFuel is not legacy and is not here.
# An empty value list means the legacy code never writes that branch: the expression becomes `NULL`.

$script:LandBranchGate = [ordered]@{
    # ECW_C11_01.cs:109-117 (marker -> streamfuel, in the legacy order), :183-208 (branches). Tons and gsec alike.
    'ECW_C11_01'    = [ordered]@{
        Selector       = 'streamContains'
        NaeArgument    = 'Land_NAE'
        NaeContains    = @('6646', '6647', '6648', '6649', '6650', '6651', '6652', '6653', '6654', '6655', '6656', '6657')
        NaeBranch      = 'type2'
        StreamArgument = 'Land_TypeFuelOrStream'
        StreamMarkers  = @(
            @('gas', 'FG'), @('Gas', 'FG'), @('LPG', 'LPG'), @('sludge', 'sludge'), @('caustic', 'caustic'), @('water', 'Wwater'),
            @('401', '401'), @('14', '14'), @('452', '452'), @('Diesel', 'Diesel'))
        RestBranch     = 'type1_3'
        # Branch -> streamfuel codes (type2 and type1_3 are derived: NAE and "none of the others").
        Tons           = [ordered]@{
            'type2'   = @()
            'type1_1' = @('FG', 'LPG')
            'type1_2' = @('Diesel', '452')
            'type1_3' = @()
        }
    }
    # ECW_C11_02.cs:143-146 typeCalculationGroup (same groups for tons and gsec). '6583 (002)' has a space: never matches.
    'ECW_C11_02'    = [ordered]@{
        Selector = 'nae'
        Tons     = [ordered]@{
            'type1' = @('6640', '6641', '6642', '6643', '6644', '6645', '6582(002)', '6583 (002)', '6585(001)', '6585(003)',
                '6585(004)', '6586(002)', '6586(003)', '6586(004)', '6587(001)', '6587(003)', '6587(004)', '6592(002)', '6592(003)', '6592(004)')
            'type2' = @('6783(001)', '6786(001)', '6787(001)', '6789(001)')
        }
    }
    # ECW_C11_11_02.cs:109-133,221-239: NAE from groupNAE; 6975 group and 0883 -> type2, the rest of groupNAE -> type1.
    'ECW_C11_11_02' = [ordered]@{
        Selector = 'nae'
        Tons     = [ordered]@{
            'type1' = @('0788', '0789', '0790', '0791', '0788(001)', '0789(001)', '0790(001)', '0791(001)',
                '0560', '0562', '0563', '0561', '0564', '0565', '0903', '0905', '6904', '0793', '0524')
            'type2' = @('6975', '6981', '6982', '6983', '6984', '6985', '6986', '6976', '0883')
        }
    }
    # ECW_C11_11_03.cs:81-90: separate groups for tons (type1/2/3) and gsec (type1/2; 0804/6801-6803 are type2).
    'ECW_C11_11_03' = [ordered]@{
        Selector = 'nae'
        Tons     = [ordered]@{
            'type1' = @('0800(001)', '0800(002)', '0800(003)', '0800(004)', '0800(005)', '0800(006)', '0800(007)', '6800')
            'type2' = @('0782(001)', '0782(002)', '0782(003)', '0782(004)', '0782(005)', '0782(006)', '0782(007)', '0782(008)', '6784')
            'type3' = @('0804', '6801', '6802', '6803')
        }
        Gsec     = [ordered]@{
            'type1' = @('0800(001)', '0800(002)', '0800(003)', '0800(004)', '0800(005)', '0800(006)', '0800(007)', '6800')
            'type2' = @('0782(001)', '0782(002)', '0782(003)', '0782(004)', '0782(005)', '0782(006)', '0782(007)', '0782(008)', '6784',
                '0804', '6801', '6802', '6803')
            'type3' = @()
        }
    }
    # ECW_C11_11_04.cs:82-91: branch by pollutant code (tons and gsec alike).
    'ECW_C11_11_04' = [ordered]@{
        Selector = 'substance'
        Tons     = [ordered]@{
            'type1' = @('415', '416', '602', '616', '621')
            'type2' = @('333')
            'type3' = @('1716')
        }
    }
    # ECW_C11_16.cs:74-77: branch by NAE.
    'ECW_C11_16'    = [ordered]@{
        Selector = 'nae'
        Tons     = [ordered]@{
            'type1' = @('6491', '6482(004)', '6482(005)')
            'type2' = @('6493')
        }
    }
    # ECW_C12_01.cs:151-195: type2 only for Land_Streams = 'Fuel gas' (typeOfStream FuelGas), type1 for every other stream.
    'ECW_C12_01'    = [ordered]@{
        Selector = 'stream'
        Tons     = [ordered]@{
            'type2' = @('Fuel gas')
            'type1' = @('!Fuel gas')
        }
    }
}

# Flat legacy pollutant lists (polCodeList) of methodologies whose published versions have no substance list (AN-171).
$script:LandBranchGateSubstances = [ordered]@{
    'ECW_C11_11_04' = @('333', '415', '416', '602', '616', '621', '1716')   # ECW_C11_11_04.cs:71-80
    'ECW_C11_12_03' = @('333')                                              # ECW_C11_12_03.cs:72-74
    'ECW_C11_16'    = @('331')                                              # ECW_C11_16.cs:70-72
    'ECW_C11_17'    = @('331')                                              # ECW_C11_17.cs:68-70
    'ECW_C11_18'    = @('331')                                              # ECW_C11_18.cs:70-72
}

function Get-LandBranchGateMethodologies { return @($script:LandBranchGate.Keys) }

function Get-LandBranchGateTestOverrides {
    <#
    .SYNOPSIS  Golden-test argument values to replace when the test input no longer resolves to a category of the version.
    .DESCRIPTION
        ECW_C11_02 PARITY_1: NAE 6640 with the stream '1-204 sludge' gives the category key '6640_sludge', which the version
        does not have (publication fails, scratch EcrScratch_An171 11.11). With the fuel-gas stream the rule gives '6640'.
        The Cyrillic part is built from code points: the lib is dot-sourced by Windows PowerShell 5 without a BOM.
    #>
    $ru = -join ([char[]]@(0x0422, 0x043E, 0x043F, 0x043B, 0x0438, 0x0432, 0x043D, 0x044B, 0x0439, 0x20, 0x0433, 0x0430, 0x0437, 0x20, 0x28,
            0x0412, 0x043D, 0x0443, 0x0442, 0x0440, 0x0435, 0x043D, 0x043D, 0x0438, 0x0439, 0x29))
    return @{ 'ECW_C11_02' = @{ 'Land_TypeFuelOrStream' = "Fuel Gas (Internal) - $ru" } }
}

function Convert-LandBranchGateTestInput {
    <#
    .SYNOPSIS  Golden-test input moved to an existing document/period (the old one may be deleted), with argument overrides.
    .OUTPUTS   pscustomobject: Changed, InputJson.
    #>
    param(
        [Parameter(Mandatory)][string]$InputJson,
        [Parameter(Mandatory)][long]$DocumentId,
        [Parameter(Mandatory)][int]$PeriodKey,
        [hashtable]$Overrides = @{}
    )
    $o = $InputJson | ConvertFrom-Json
    $changed = $false
    if ([long]$o.documentId -ne $DocumentId) { $o.documentId = $DocumentId; $changed = $true }
    $pk = $o.periodKey
    $cur = if ($null -eq $pk) { 0 } elseif ($pk.PSObject.Properties['value']) { [int]$pk.value } else { [int]$pk }
    if ($cur -ne $PeriodKey) { $o.periodKey = [pscustomobject]@{ value = $PeriodKey }; $changed = $true }
    foreach ($a in @($o.arguments)) {
        $code = [string]$a.argumentCode
        if ($Overrides.ContainsKey($code) -and [string]$a.valueString -cne [string]$Overrides[$code]) {
            $a.valueString = [string]$Overrides[$code]; $a.value = $null; $changed = $true
        }
    }
    return [pscustomobject]@{ Changed = $changed; InputJson = ($o | ConvertTo-Json -Depth 8 -Compress) }
}

function Get-LandBranchGateSubstances {
    <# Copy of «methodology -> pollutant codes» (legacy flat lists). #>
    $copy = [ordered]@{}
    foreach ($k in $script:LandBranchGateSubstances.Keys) { $copy[$k] = @($script:LandBranchGateSubstances[$k]) }
    return $copy
}

function Get-LandBranchOfFormula {
    <# @{ Metric = 'tons'|'gsec'; Branch = 'type1'|'type1_2'|... } for `ECW_EC_(tons|gsec)_<XXX|code>_typeN[_M]`; otherwise $null. #>
    param([Parameter(Mandatory)][string]$FormulaCode)
    $m = [regex]::Match($FormulaCode, '^ECW_EC_(?<metric>tons|gsec)_[A-Za-z0-9]+_(?<branch>type[0-9]+(_[0-9]+)?)$')
    if (-not $m.Success) { return $null }
    return @{ Metric = $m.Groups['metric'].Value; Branch = $m.Groups['branch'].Value }
}

function Get-LandBranchGateValues {
    <#
    .SYNOPSIS  Values that select the branch of this formula; $null = the formula is not gated by this library.
    .OUTPUTS   @{ Values = string[] } (empty Values = the legacy code never writes this branch) or $null.
               Selector 'streamContains': Values = the branch name (the condition is built from the plan of the methodology).
    #>
    param([Parameter(Mandatory)][string]$Methodology, [Parameter(Mandatory)][string]$FormulaCode)
    if (-not $script:LandBranchGate.Contains($Methodology)) { return $null }
    $b = Get-LandBranchOfFormula -FormulaCode $FormulaCode
    if ($null -eq $b) { return $null }
    $plan = $script:LandBranchGate[$Methodology]
    $groups = $plan.Tons
    if ($b.Metric -eq 'gsec' -and $plan.Contains('Gsec')) { $groups = $plan.Gsec }
    # A two-level branch (`typeN_M`) or any branch of a 'streamContains' plan is gated only when the plan names it: codes
    # of that form in the other methodologies stay untouched, as before AN-165.
    if ($plan.Selector -eq 'streamContains') {
        if (-not $groups.Contains($b.Branch)) { return $null }
        return @{ Values = [string[]]@($b.Branch) }
    }
    if ($b.Branch.Contains('_') -and -not $groups.Contains($b.Branch)) { return $null }
    $gateList = [string[]]@()
    if ($groups.Contains($b.Branch)) { $gateList = [string[]]@($groups[$b.Branch]) }
    return @{ Values = $gateList }
}

function Get-LandBranchNaeSpellings {
    <# Legacy-equivalent spellings for `in(@Land_NAE, ...)`: drop values with a space, add `X (tail)` for `X(tail)`. #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Naes)
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($n in $Naes) {
        if ($n.Contains(' ')) { continue }
        $out.Add($n)
        $ix = $n.IndexOf('(')
        if ($ix -gt 0) { $out.Add($n.Substring(0, $ix) + ' ' + $n.Substring($ix)) }
    }
    return $out.ToArray()
}

# --- 'streamContains' (AN-165): one condition tree, one renderer (dialect text) and one evaluator (tests) -----------------
# Node: @{ Op = 'contains'|'in'|'or'|'and'|'not'; Arg; Text; Items }.

function Get-LandBranchLastMatchNode {
    <# "The streamfuel the legacy chain ends with is one of $FuelCodes" (see the header: the last matching marker wins). #>
    param([Parameter(Mandatory)]$GatePlan, [Parameter(Mandatory)][string[]]$FuelCodes)
    $markers = @($GatePlan.StreamMarkers)
    $arg = [string]$GatePlan.StreamArgument
    $groups = [ordered]@{}
    for ($k = 0; $k -lt $markers.Count; $k++) {
        if (-not ($FuelCodes -ccontains [string]$markers[$k][1])) { continue }
        $later = New-Object System.Collections.Generic.List[object]
        for ($j = $k + 1; $j -lt $markers.Count; $j++) {
            if (-not ($FuelCodes -ccontains [string]$markers[$j][1])) { $later.Add(@{ Op = 'contains'; Arg = $arg; Text = [string]$markers[$j][0] }) }
        }
        $key = 'k:' + (($later | ForEach-Object { $_.Text }) -join '|')
        if (-not $groups.Contains($key)) { $groups[$key] = @{ Members = (New-Object System.Collections.Generic.List[object]); Later = $later } }
        $groups[$key].Members.Add(@{ Op = 'contains'; Arg = $arg; Text = [string]$markers[$k][0] })
    }
    $alts = New-Object System.Collections.Generic.List[object]
    foreach ($key in $groups.Keys) {
        $g = $groups[$key]
        $members = @{ Op = 'or'; Items = $g.Members.ToArray() }
        if ($g.Later.Count -eq 0) { $alts.Add($members) }
        else { $alts.Add(@{ Op = 'and'; Items = @($members, @{ Op = 'not'; Items = @(@{ Op = 'or'; Items = $g.Later.ToArray() }) }) }) }
    }
    # No marker matched: streamfuel = the argument itself. Only a code that holds no marker fragment can get here.
    foreach ($code in $FuelCodes) {
        $holdsMarker = $false
        foreach ($mk in $markers) { if ($code.Contains([string]$mk[0])) { $holdsMarker = $true } }
        if (-not $holdsMarker) { $alts.Add(@{ Op = 'in'; Arg = $arg; Text = $code }) }
    }
    return @{ Op = 'or'; Items = $alts.ToArray() }
}

function Get-LandBranchContainsTree {
    <# Condition tree of a branch of a 'streamContains' methodology. #>
    param([Parameter(Mandatory)][string]$Methodology, [Parameter(Mandatory)][string]$Branch)
    $plan = $script:LandBranchGate[$Methodology]
    if ($null -eq $plan -or $plan.Selector -ne 'streamContains') { throw "Branch gate: $Methodology is not a 'streamContains' methodology" }
    $naeItems = @($plan.NaeContains | ForEach-Object { @{ Op = 'contains'; Arg = [string]$plan.NaeArgument; Text = [string]$_ } })
    $nae = @{ Op = 'or'; Items = $naeItems }
    if ($Branch -ceq $plan.NaeBranch) { return $nae }
    $notNae = @{ Op = 'not'; Items = @($nae) }
    $inner = [ordered]@{}
    foreach ($name in $plan.Tons.Keys) {
        $codes = [string[]]@($plan.Tons[$name])
        if ($codes.Count -gt 0) { $inner[$name] = Get-LandBranchLastMatchNode -GatePlan $plan -FuelCodes $codes }
    }
    if ($inner.Contains($Branch)) { return @{ Op = 'and'; Items = @($notNae, $inner[$Branch]) } }
    if ($Branch -ceq $plan.RestBranch) {
        $items = New-Object System.Collections.Generic.List[object]
        $items.Add($notNae)
        foreach ($name in $inner.Keys) { $items.Add(@{ Op = 'not'; Items = @($inner[$name]) }) }
        return @{ Op = 'and'; Items = $items.ToArray() }
    }
    throw "Branch gate of ${Methodology}: unknown branch '$Branch'"
}

function ConvertTo-LandBranchConditionText {
    <# Dialect text of a condition tree. `not` is always written `not (...)`; a nested and/or is parenthesized. #>
    param([Parameter(Mandatory)]$Node)
    switch ($Node.Op) {
        'contains' { return 'CONTAINS(@' + $Node.Arg + ", '" + ([string]$Node.Text).Replace("'", "''") + "')" }
        'in' { return 'in(@' + $Node.Arg + ", '" + ([string]$Node.Text).Replace("'", "''") + "')" }
        'not' { return 'not (' + (ConvertTo-LandBranchConditionText -Node $Node.Items[0]) + ')' }
        default {
            $items = @($Node.Items)
            if ($items.Count -eq 1) { return (ConvertTo-LandBranchConditionText -Node $items[0]) }
            $parts = foreach ($i in $items) {
                # A one-item and/or is its item: unwrap before deciding on parentheses.
                $inner = $i
                while (($inner.Op -eq 'and' -or $inner.Op -eq 'or') -and @($inner.Items).Count -eq 1) { $inner = @($inner.Items)[0] }
                $t = ConvertTo-LandBranchConditionText -Node $inner
                if ($inner.Op -eq 'and' -or $inner.Op -eq 'or') { "($t)" } else { $t }
            }
            return ($parts -join " $($Node.Op) ")
        }
    }
}

function Test-LandBranchCondition {
    <# The same tree evaluated in PowerShell with the semantics of CONTAINS / in(): ordinal, empty text -> false. #>
    param([Parameter(Mandatory)]$Node, [Parameter(Mandatory)][hashtable]$ArgRow)
    switch ($Node.Op) {
        'contains' { $v = [string]$ArgRow[$Node.Arg]; return ($v.Length -gt 0 -and $v.Contains([string]$Node.Text)) }
        'in' { return ([string]$ArgRow[$Node.Arg] -ceq [string]$Node.Text) }
        'not' { return (-not (Test-LandBranchCondition -Node $Node.Items[0] -ArgRow $ArgRow)) }
        'or' { foreach ($i in @($Node.Items)) { if (Test-LandBranchCondition -Node $i -ArgRow $ArgRow) { return $true } }; return $false }
        'and' { foreach ($i in @($Node.Items)) { if (-not (Test-LandBranchCondition -Node $i -ArgRow $ArgRow)) { return $false } }; return $true }
    }
    throw "Branch gate: unknown node '$($Node.Op)'"
}

function Get-LandBranchGateArguments {
    <# Arguments the condition of the selector reads (to be declared in the formula's argument list). #>
    param([Parameter(Mandatory)][string]$Selector, [string]$Methodology = '')
    switch ($Selector) {
        'nae' { return @('Land_NAE') }
        'stream' { return @('Land_Streams') }
        'streamContains' {
            $plan = $script:LandBranchGate[$Methodology]
            return @([string]$plan.NaeArgument, [string]$plan.StreamArgument)
        }
        default { return @() }
    }
}

function Get-LandBranchGateCondition {
    <# The condition text for the branch (without `if(`); $null for an empty list (expression NULL).
       'streamContains': -Methodology is required, Values = the branch name. #>
    param(
        [Parameter(Mandatory)][ValidateSet('nae', 'substance', 'stream', 'streamContains')][string]$Selector,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Values,
        [string]$Methodology = ''
    )
    if (@($Values).Count -eq 0) { return $null }
    switch ($Selector) {
        'streamContains' {
            if ($Values.Count -ne 1) { throw "Branch gate: 'streamContains' takes one branch name, got $($Values.Count)" }
            return (ConvertTo-LandBranchConditionText -Node (Get-LandBranchContainsTree -Methodology $Methodology -Branch $Values[0]))
        }
        'nae' {
            $items = @(Get-LandBranchNaeSpellings -Naes $Values)
            if ($items.Count -eq 0) { return $null }
            return 'in(@Land_NAE, ' + (($items | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', ') + ')'
        }
        'substance' {
            return 'in(CURRENT_SUBSTANCE(), ' + (($Values | ForEach-Object { "'" + $_ + "'" }) -join ', ') + ')'
        }
        'stream' {
            # '!x' = every stream except x (ECW_C12_01: type1 for all streams but Fuel gas).
            if ($Values.Count -eq 1 -and $Values[0].StartsWith('!')) { return "@Land_Streams <> '" + $Values[0].Substring(1).Replace("'", "''") + "'" }
            return 'in(@Land_Streams, ' + (($Values | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', ') + ')'
        }
    }
}

function Convert-LandBranchGateFormula {
    <#
    .SYNOPSIS
        Wraps the branch expression: `if(<condition>, <expression>, NULL)`; an empty list -> `NULL`. The argument the condition
        reads (`@Land_NAE` / `@Land_Streams`; both `@Land_NAE` and `@Land_TypeFuelOrStream` for 'streamContains') is added to
        the formula's argument list. Repeating gives Changed = $false. 'streamContains' needs -Methodology.
    .OUTPUTS  pscustomobject: Changed, Expression, ArgumentsCsv.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        [AllowNull()][AllowEmptyString()]$ArgumentsCsv = $null,
        [Parameter(Mandatory)][ValidateSet('nae', 'substance', 'stream', 'streamContains')][string]$Selector,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Values,
        [string]$Methodology = ''
    )
    $text = $Expression.Trim()
    if ($text.Length -eq 0) { return [pscustomobject]@{ Changed = $false; Expression = $Expression; ArgumentsCsv = $ArgumentsCsv } }
    $condition = Get-LandBranchGateCondition -Selector $Selector -Values $Values -Methodology $Methodology
    if ($null -eq $condition) {
        $same = ($text -ceq 'NULL')
        return [pscustomobject]@{ Changed = (-not $same); Expression = $(if ($same) { $Expression } else { 'NULL' }); ArgumentsCsv = $ArgumentsCsv }
    }
    $head = "if($condition, "
    $wrapped = $text.StartsWith($head, [StringComparison]::Ordinal) -and $text.EndsWith(', NULL)', [StringComparison]::Ordinal)
    $newExpression = if ($wrapped) { $Expression } else { $head + $text + ', NULL)' }

    $arguments = @(Get-LandBranchGateArguments -Selector $Selector -Methodology $Methodology)
    $newArguments = $ArgumentsCsv
    $argsChanged = $false
    if ($arguments.Count -gt 0 -and $null -ne $ArgumentsCsv) {
        $tokens = New-Object System.Collections.Generic.List[string]
        foreach ($t in (([string]$ArgumentsCsv) -split ';')) {
            $x = $t.Trim()
            if ($x.Length -eq 0) { continue }
            $tokens.Add($x)
        }
        foreach ($argument in $arguments) {
            $present = $false
            foreach ($x in $tokens) { if (($x.TrimStart('@').TrimEnd('?').TrimEnd()) -ieq $argument) { $present = $true } }
            if (-not $present) { $tokens.Add("@$argument"); $argsChanged = $true }
        }
        if ($argsChanged) { $newArguments = (($tokens -join ';') + ';') }
    }
    return [pscustomobject]@{ Changed = ((-not $wrapped) -or $argsChanged); Expression = $newExpression; ArgumentsCsv = $newArguments }
}

function Get-LandBranchGateSelector {
    param([Parameter(Mandatory)][string]$Methodology)
    if (-not $script:LandBranchGate.Contains($Methodology)) { return $null }
    return [string]$script:LandBranchGate[$Methodology].Selector
}

function Get-LandSelectedBranchByGate {
    <#
    .SYNOPSIS  Branch the gate lets through for a row value (NAE, substance code or stream), per metric; $null = none.
               Used by the tests to compare with the parity harness (Get-LandParitySelectedBranch).
               'streamContains': -Value is the NAE, -Stream the stream text (Land_TypeFuelOrStream).
    #>
    param([Parameter(Mandatory)][string]$Methodology, [Parameter(Mandatory)][ValidateSet('tons', 'gsec')][string]$Metric, [AllowEmptyString()][string]$Value,
        [AllowEmptyString()][string]$Stream = '')
    $plan = $script:LandBranchGate[$Methodology]
    $groups = $plan.Tons
    if ($Metric -eq 'gsec' -and $plan.Contains('Gsec')) { $groups = $plan.Gsec }
    $hits = @()
    foreach ($b in $groups.Keys) {
        $vals = @($groups[$b])
        switch ($plan.Selector) {
            'streamContains' {
                $row = @{}
                $row[[string]$plan.NaeArgument] = $Value
                $row[[string]$plan.StreamArgument] = $Stream
                if (Test-LandBranchCondition -Node (Get-LandBranchContainsTree -Methodology $Methodology -Branch $b) -ArgRow $row) { $hits += $b }
            }
            'nae' { if (@(Get-LandBranchNaeSpellings -Naes $vals) -ccontains $Value) { $hits += $b } }
            'substance' { if ($vals -ccontains $Value) { $hits += $b } }
            'stream' {
                if ($vals.Count -eq 1 -and $vals[0].StartsWith('!')) { if ($Value -cne $vals[0].Substring(1)) { $hits += $b } }
                elseif ($vals -ccontains $Value) { $hits += $b }
            }
        }
    }
    if ($hits.Count -gt 1) { throw "Branch gate of $Methodology/$Metric selects several branches for '$Value': $($hits -join ', ')" }
    if ($hits.Count -eq 0) { return $null }
    return $hits[0]
}
