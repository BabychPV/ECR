# tools/land/LandLookupCompare.Lib.ps1
# LAND-5094 DIAG-A (2026-10-14): порівняння аргументу-Lookup з ТЕКСТОМ довідника.
# Чиста логіка без звернень до API; значень прода тут немає.
#
# Дефект. Комірка-Lookup і колонка-міст Formula над Lookup (`Land_Measure_ReleaseColdVent` у T10_01/T11_01) віддають
# у формулу НОМЕР запису довідника (ValueNumeric/EntryRef = 189/190), а не його текст. Вираз
# `@Land_Measure_ReleaseColdVent = 'No - Нет'` порівнює Number(190) з текстом і ніколи не дорівнює: гілка `0` недосяжна
# (C12_03 V1/V2: 1 248 клітинок MISMATCH_LEGACY_ZERO). Правка - у виразі: `REGFIELD(@X, 'NAME') = 'No - Нет'`
# (так C12_03 уже читає `@Land_TypeFuel`). REGFIELD приймає Number як id запису.
#
# Що робить модуль:
#   Get-LandLookupCompareArguments  - з моделі: аргументи (колонки), що віддають id запису довідника, з іменами записів;
#   Convert-LandLookupCompareExpression - `@X = 'лiт'` / `'лiт' = @X` / `@X <> 'лiт'` / `in(@X, 'лiт', ...)` ->
#                                         через REGFIELD(@X, 'NAME'); лише коли літерал - ім'я запису довідника ЦЬОГО
#                                         аргументу; повтор - 0 змін; маркер `?` зберігається;
#   Set-LandLookupCompareTestArguments - PARITY_1: аргументу потрібне числове value (id запису), інакше REGFIELD
#                                         дає #VALUE; id беруться з entryId або з довідника за valueString.

function Get-LandLookupCompareArguments {
    <#
    .SYNOPSIS
        Словник «код аргументу -> {Registries, Names}» для колонок, що віддають id запису довідника.
    .DESCRIPTION
        Колонка dataType=Lookup з registry; колонка dataType=Formula, чий вираз посилається на Lookup-колонки тієї ж
        таблиці і НЕ є `REGFIELD(...)` (REGFIELD уже повертає текст). Імена записів - поле name з model.registries.
        Колонки, що збігаються за кодом у різних таблицях, об'єднуються.
    #>
    param([Parameter(Mandatory)]$Model)

    $regNames = @{}
    foreach ($r in @($Model.registries)) {
        $names = New-Object System.Collections.Generic.List[string]
        foreach ($e in @($r.entries)) { if ($e.name) { $names.Add([string]$e.name) } }
        $regNames[[string]$r.code] = $names.ToArray()
    }

    $lcPlan = [ordered]@{}
    function Add-Arg($lcPlan, [string]$code, [string]$reg, $regNames) {
        if (-not $lcPlan.Contains($code)) { $lcPlan[$code] = [ordered]@{ Registries = (New-Object System.Collections.Generic.List[string]); Names = (New-Object System.Collections.Generic.List[string]) } }
        if (-not $lcPlan[$code].Registries.Contains($reg)) { $lcPlan[$code].Registries.Add($reg) }
        foreach ($n in @($regNames[$reg])) { if (-not $lcPlan[$code].Names.Contains([string]$n)) { $lcPlan[$code].Names.Add([string]$n) } }
    }

    foreach ($sheet in @($Model.sheets)) {
        foreach ($t in @($sheet.tables)) {
            $byCode = @{}
            foreach ($c in @($t.columns)) { $byCode[[string]$c.code] = $c }
            foreach ($c in @($t.columns)) {
                $dt = [string]$c.dataType
                if ($dt -eq 'Lookup' -and $c.registry) {
                    Add-Arg $lcPlan ([string]$c.code) ([string]$c.registry) $regNames
                    continue
                }
                if ($dt -ne 'Formula' -or -not $c.expression) { continue }
                $expr = [string]$c.expression
                if ($expr -match '^\s*REGFIELD\s*\(') { continue }
                foreach ($lcRefMatch in [regex]::Matches($expr, '\[([A-Za-z0-9_]+)\]')) {
                    $ref = [string]$lcRefMatch.Groups[1].Value
                    if ($ref -eq 'Period' -or -not $byCode.ContainsKey($ref)) { continue }
                    $src = $byCode[$ref]
                    if ([string]$src.dataType -eq 'Lookup' -and $src.registry) {
                        Add-Arg $lcPlan ([string]$c.code) ([string]$src.registry) $regNames
                    }
                }
            }
        }
    }

    $out = [ordered]@{}
    foreach ($k in $lcPlan.Keys) {
        $out[$k] = [pscustomobject]@{ Registries = $lcPlan[$k].Registries.ToArray(); Names = $lcPlan[$k].Names.ToArray() }
    }
    return $out
}

function Convert-LandLookupCompareExpression {
    <#
    .SYNOPSIS
        Переписує порівняння аргументу з текстом запису довідника через REGFIELD(@X, 'NAME').
    .PARAMETER Plan
        Результат Get-LandLookupCompareArguments.
    .OUTPUTS
        pscustomobject: Changed, Expression, Replaced (коди аргументів).
    #>
    param(
        [AllowNull()][AllowEmptyString()]$Expression = $null,
        [Parameter(Mandatory)]$Plan
    )

    $expr = $Expression
    $replaced = New-Object System.Collections.Generic.HashSet[string]
    if ([string]::IsNullOrEmpty([string]$Expression)) {
        return [pscustomobject]@{ Changed = $false; Expression = $Expression; Replaced = @() }
    }

    foreach ($code in @($Plan.Keys)) {
        $names = @($Plan[$code].Names)
        if ($names.Count -eq 0) { continue }
        $arg = '@' + [regex]::Escape([string]$code) + '(?![A-Za-z0-9_])(?<q>\?)?'
        $op = '(?<op>==|=|<>|!=)'

        # @X = 'літерал'
        $rx1 = "(?<![A-Za-z0-9_])$arg(?<s1>\s*)$op(?<s2>\s*)'(?<lit>[^']*)'"
        $expr = [regex]::Replace([string]$expr, $rx1, {
                param($lcMatch)
                if ($names -cnotcontains $lcMatch.Groups['lit'].Value) { return $lcMatch.Value }
                [void]$replaced.Add([string]$code)
                $q = $lcMatch.Groups['q'].Value
                return "REGFIELD(@$code$q, 'NAME')" + $lcMatch.Groups['s1'].Value + $lcMatch.Groups['op'].Value + $lcMatch.Groups['s2'].Value + "'" + $lcMatch.Groups['lit'].Value + "'"
            }, 'IgnoreCase')

        # 'літерал' = @X
        $rx2 = "'(?<lit>[^']*)'(?<s1>\s*)$op(?<s2>\s*)$arg"
        $expr = [regex]::Replace([string]$expr, $rx2, {
                param($lcMatch)
                if ($names -cnotcontains $lcMatch.Groups['lit'].Value) { return $lcMatch.Value }
                [void]$replaced.Add([string]$code)
                $q = $lcMatch.Groups['q'].Value
                return "'" + $lcMatch.Groups['lit'].Value + "'" + $lcMatch.Groups['s1'].Value + $lcMatch.Groups['op'].Value + $lcMatch.Groups['s2'].Value + "REGFIELD(@$code$q, 'NAME')"
            }, 'IgnoreCase')

        # in(@X, 'a', 'b', ...) - лише якщо в переліку є ім'я запису цього довідника
        $rx3 = "(?<![A-Za-z0-9_])(?<fn>in)(?<s0>\s*\(\s*)$arg(?<tail>\s*,[^)]*\))"
        $expr = [regex]::Replace([string]$expr, $rx3, {
                param($lcMatch)
                $known = $false
                foreach ($l in [regex]::Matches($lcMatch.Groups['tail'].Value, "'([^']*)'")) {
                    if ($names -ccontains $l.Groups[1].Value) { $known = $true }
                }
                if (-not $known) { return $lcMatch.Value }
                [void]$replaced.Add([string]$code)
                $q = $lcMatch.Groups['q'].Value
                return $lcMatch.Groups['fn'].Value + $lcMatch.Groups['s0'].Value + "REGFIELD(@$code$q, 'NAME')" + $lcMatch.Groups['tail'].Value
            }, 'IgnoreCase')
    }

    return [pscustomobject]@{
        Changed    = ($expr -cne $Expression)
        Expression = $expr
        Replaced   = @($replaced)
    }
}

function Set-LandLookupCompareTestArguments {
    <#
    .SYNOPSIS
        PARITY_1: аргументам, що тепер читає REGFIELD, дає числове value (id запису довідника).
    .DESCRIPTION
        value вже є - нічого; є entryId - value = entryId; є лише valueString (ім'я запису) і -Resolve повернув id -
        value = id; інакше аргумент потрапляє в Missing (значення не вигадуються). Повтор - 0 змін.
    .PARAMETER Existing  Масив {argumentCode, value, valueString, unitId[, entryId]}.
    .PARAMETER Codes     Коди аргументів, що переписано.
    .PARAMETER Plan      Результат Get-LandLookupCompareArguments (реєстри).
    .PARAMETER Resolve   Необов'язковий scriptblock { param($registry, $name) -> id | $null }.
    #>
    param(
        [AllowEmptyCollection()][AllowNull()][object[]]$Existing,
        [Parameter(Mandatory)][string[]]$Codes,
        [Parameter(Mandatory)]$Plan,
        [scriptblock]$Resolve = $null
    )

    $list = New-Object System.Collections.Generic.List[object]
    $updated = New-Object System.Collections.Generic.List[string]
    $missing = New-Object System.Collections.Generic.List[string]
    $wanted = @{}
    foreach ($c in $Codes) { $wanted[$c.ToLowerInvariant()] = $c }
    $seen = @{}
    foreach ($a in @($Existing)) {
        if ($null -eq $a) { continue }
        $key = ([string]$a.argumentCode).ToLowerInvariant()
        if (-not $wanted.ContainsKey($key)) { $list.Add($a); continue }
        $seen[$key] = $true
        $hasValue = $null -ne $a.value
        if ($hasValue) { $list.Add($a); continue }
        $id = $null
        $entry = $null
        if ($a.PSObject.Properties['entryId']) { $entry = $a.entryId }
        if ($null -ne $entry) { $id = $entry }
        elseif ($a.valueString -and $Resolve) {
            foreach ($reg in @($Plan[$wanted[$key]].Registries)) {
                try { $id = & $Resolve $reg ([string]$a.valueString) } catch { $id = $null }
                if ($null -ne $id) { break }
            }
        }
        if ($null -eq $id) { $missing.Add($wanted[$key]); $list.Add($a); continue }
        $copy = [ordered]@{}
        if ($a -is [System.Collections.IDictionary]) { foreach ($k in $a.Keys) { $copy[[string]$k] = $a[$k] } }
        else { foreach ($p in $a.PSObject.Properties) { $copy[[string]$p.Name] = $p.Value } }
        $copy['value'] = [double]$id
        $list.Add($copy)
        $updated.Add($wanted[$key])
    }
    foreach ($c in $Codes) { if (-not $seen.ContainsKey($c.ToLowerInvariant())) { $missing.Add($c) } }
    return [pscustomobject]@{ Arguments = $list.ToArray(); Updated = $updated.ToArray(); Missing = @($missing | Select-Object -Unique) }
}
