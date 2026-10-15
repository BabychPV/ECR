# tools/land/LandSubstanceApplicability.Lib.ps1
# Застосовність речовин на рядок (рішення людини 13.10 «2»; 02b «Застосовність речовини до рядка»).
# Чиста логіка без звернень до API: будує правило категорії з обгорткою SUBSTANCE_APPLICABLE і розпізнає його.
# Дані (довідник LAND_SUBSTANCE_APPLICABILITY і опис `substanceApplicability`) - у моделі Land
# (model-gen/substance_applicability.py); значень прода тут немає.

function New-LandSubstanceApplicabilityRule {
    <#
    .SYNOPSIS
        Обгортає наявне правило категорії: `if(SUBSTANCE_APPLICABLE('<довідник>','<методологія>','<вид>',<значення>),<правило>,'')`.
    .DESCRIPTION
        Row-фаза: функція без речовини дає TRUE - ключ правила лишається як був (порожній ключ відмовляє рядку, як і
        раніше). Цикл речовин: немає запису застосовності - порожній ключ, і рушій пропускає речовину.
    .OUTPUTS
        string - текст правила.
    #>
    param(
        [Parameter(Mandatory)][string]$Inner,
        [Parameter(Mandatory)][string]$Registry,
        [Parameter(Mandatory)][string]$Methodology,
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$ValueExpression
    )

    $innerText = $Inner.Trim()
    if ($innerText.Length -eq 0) { throw 'Порожнє правило категорії: нічого обгортати.' }
    return "if(SUBSTANCE_APPLICABLE('$Registry','$Methodology','$Kind',$ValueExpression),$innerText,'')"
}

function Get-LandSubstanceApplicabilityInnerRule {
    <#
    .SYNOPSIS
        Наявне правило без обгортки, якщо воно побудоване New-LandSubstanceApplicabilityRule; інакше $null.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Rule)

    $head = 'if(SUBSTANCE_APPLICABLE('
    $tail = ",'')"
    if (-not $Rule.StartsWith($head, [StringComparison]::Ordinal) -or -not $Rule.EndsWith($tail, [StringComparison]::Ordinal)) { return $null }

    # Кінець виклику функції - парна дужка з урахуванням рядкових літералів ('...', подвоєна ' усередині).
    $depth = 1
    $inString = $false
    $i = $head.Length
    while ($i -lt $Rule.Length -and $depth -gt 0) {
        $c = $Rule[$i]
        if ($inString) {
            if ($c -eq "'") {
                if ($i + 1 -lt $Rule.Length -and $Rule[$i + 1] -eq "'") { $i++ } else { $inString = $false }
            }
        }
        elseif ($c -eq "'") { $inString = $true }
        elseif ($c -eq '(') { $depth++ }
        elseif ($c -eq ')') { $depth-- }
        $i++
    }
    if ($depth -ne 0 -or $i -ge $Rule.Length -or $Rule[$i] -ne ',') { return $null }
    $innerStart = $i + 1
    $innerLength = $Rule.Length - $tail.Length - $innerStart
    if ($innerLength -le 0) { return $null }
    return $Rule.Substring($innerStart, $innerLength)
}

function Test-LandSubstanceApplicabilityRule {
    <# TRUE, якщо правило вже містить SUBSTANCE_APPLICABLE (повторний запуск етапу - 0 змін). #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Rule)

    return $Rule.Contains('SUBSTANCE_APPLICABLE')
}

function Test-LandRuleMatchesBaseRule {
    <#
    .SYNOPSIS
        Чи збігається наявне правило з базовим із af-land-category-rules.json - прямо або з обгорткою застосовності.
    .DESCRIPTION
        Етап LandParity не має скидати обгортку, яку поставив етап SubstanceApplicability.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Existing,
        [Parameter(Mandatory)][string]$Base
    )

    if ($Existing -ceq $Base) { return $true }
    $unwrapped = Get-LandSubstanceApplicabilityInnerRule -Rule $Existing
    return ($null -ne $unwrapped) -and ($unwrapped -ceq $Base)
}

function ConvertTo-LandSubstanceApplicabilityPlan {
    <#
    .SYNOPSIS
        Довідник LAND_SUBSTANCE_APPLICABILITY з моделі -> план записів (перевірка складу й унікальності).
    .PARAMETER Registry
        Об'єкт довідника з моделі (code, entries[].code/name/fields).
    .OUTPUTS
        pscustomobject: Items (EntryCode, Name, Values), Markers (кількість маркерів), Skipped.
    #>
    param([Parameter(Mandatory)]$Registry)

    $items = New-Object System.Collections.Generic.List[object]
    # Регістр значущий (первинний ключ довідника - ignoreCase = false): `Makat Gas` і `Makat gas` - різні значення.
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $markers = 0
    $skipped = 0
    foreach ($e in @($Registry.entries)) {
        $f = $e.fields
        $values = [ordered]@{
            METHODOLOGY    = ([string]$f.METHODOLOGY).Trim()
            SELECTOR_KIND  = ([string]$f.SELECTOR_KIND).Trim()
            SELECTOR_VALUE = ([string]$f.SELECTOR_VALUE).Trim()
            SUBSTANCE_CODE = ([string]$f.SUBSTANCE_CODE).Trim()
        }
        $key = ($values.Values -join '|')
        $isMarker = ($values.SELECTOR_KIND -ceq '*') -and ($values.SELECTOR_VALUE -ceq '*') -and ($values.SUBSTANCE_CODE -ceq '*')
        $valid = ($values.METHODOLOGY -match '^ECW_[A-Za-z0-9_]+$') -and
        ($isMarker -or (($values.SELECTOR_KIND -match '^(NAE|InvNum|TypeFuel|Stream)$') -and $values.SELECTOR_VALUE.Length -gt 0 -and
                $values.SUBSTANCE_CODE -match '^[1-9][0-9]{2,3}$'))
        if (-not $valid -or -not $seen.Add($key)) { $skipped++; continue }
        if ($isMarker) { $markers++ }
        $items.Add([pscustomobject]@{ EntryCode = [string]$e.code; Name = $key; Values = $values })
    }
    return [pscustomobject]@{ Items = $items.ToArray(); Markers = $markers; Skipped = $skipped }
}

function Get-LandSubstanceApplicabilitySampleArguments {
    <#
    .SYNOPSIS
        Аргументи тест-кейсу PARITY_1, яких потребує ОБГОРТКА застосовності: SUBSTANCE_APPLICABLE(..., <valueExpression>)
        читає аргумент рядка (@Land_TypeFuelOrStream ...), а правило без обгортки його може не читати - тоді simulate
        дає 422 ECR-CALC-0422 (#ARG) і публікація блокується (D-7).
    .DESCRIPTION
        Значення береться з довідника застосовності (КЛЮЧ селектора, не значення прода): перший не-маркерний запис
        методології з видом `Kind`. Для valueExpression `in(@A,'x',...)` (вид contains) - перший літерал; для
        REGFIELD(@A,'F') значення - код запису довідника, а не селектор, тому аргумент не задається.
    .PARAMETER Entries
        Записи довідника з моделі (code, fields.METHODOLOGY/SELECTOR_KIND/SELECTOR_VALUE).
    .OUTPUTS
        hashtable: код аргументу -> текст (порожній, якщо підібрати нічого).
    #>
    param(
        [Parameter(Mandatory)][string]$Methodology,
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$ValueExpression,
        [Parameter(Mandatory)][AllowEmptyCollection()][AllowNull()][object[]]$Entries
    )

    $picked = @{}
    $expr = $ValueExpression.Trim()
    if ($expr -cmatch '^@([A-Za-z_][A-Za-z0-9_]*)$') {
        $arg = $Matches[1]
        foreach ($e in @($Entries)) {
            if ($null -eq $e) { continue }
            $f = $e.fields
            if (([string]$f.METHODOLOGY) -ceq $Methodology -and ([string]$f.SELECTOR_KIND) -ceq $Kind -and ([string]$f.SELECTOR_VALUE) -cne '*') {
                $picked[$arg] = [string]$f.SELECTOR_VALUE
                break
            }
        }
        return $picked
    }
    $m = [regex]::Match($expr, "in\(\s*@([A-Za-z_][A-Za-z0-9_]*)\s*,\s*'([^']+)'")
    if ($m.Success) { $picked[$m.Groups[1].Value] = $m.Groups[2].Value }
    return $picked
}

function Get-LandApplicabilityCodeUnion {
    <#
    .SYNOPSIS
        Склад речовин методології = об'єднання кодів усіх ключів `codes` з land-substance-applicability.json
        (для правил із `substancesFromApplicability: true` у af-land-category-rules.json).
    .DESCRIPTION
        Константи чернетки можуть не покривати всі коди (напр. 1707 лише у FG_Internal), а старий C# рахує їх за
        словником застосовності: склад версії має бути його об'єднанням, інакше речовина випаде з розрахунку.
    .PARAMETER Codes
        Об'єкт `methodologies.<код>.codes` (ключ рядка -> масив кодів).
    .OUTPUTS
        string[] - числові коди без провідних нулів, за зростанням (порожній, якщо `Codes` немає).
    #>
    param([AllowNull()]$Codes)

    $union = @{}
    if ($null -ne $Codes) {
        foreach ($key in $Codes.PSObject.Properties) {
            foreach ($c in @($key.Value)) {
                $s = ([string]$c).Trim()
                if ($s -match '^[0-9]{3,4}$') { $union[[string]([int]$s)] = $true }
            }
        }
    }
    return , @($union.Keys | Sort-Object { [int]$_ })
}

function Test-LandMixedFuelStreamCategories {
    <#
    .SYNOPSIS
        TRUE, якщо категорії констант змішані: є суфікси речовин `_(\d{3,4})$` І є категорії потоку FG/LPG без такого суфікса.
    .DESCRIPTION
        Ознака форми A у «змішаній» методології (C11_01: `6646_FG`, `6646_LPG`, `6647_FG` ... поряд із `..._1052`, `..._1715`).
        Категорія «Common», NAE без потоку тощо ознакою не є: склад методологій із чистими суфіксами не змінюється.
    .OUTPUTS
        bool
    #>
    param([AllowNull()][AllowEmptyCollection()][AllowEmptyString()][string[]]$Categories)

    $hasSuffix = $false
    $hasStream = $false
    foreach ($c in @($Categories)) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        $t = $c.Trim()
        if ($t -match '_(\d{3,4})$') { $hasSuffix = $true; continue }
        if ($t -match '(?i)(^|_)(FG|LPG)(_|$)') { $hasStream = $true }
    }
    return ($hasSuffix -and $hasStream)
}

function Resolve-LandMethodologySubstanceCodes {
    <#
    .SYNOPSIS
        Склад речовин версії методології для етапу LandParity: суфікси категорій констант (форма B) або словник
        застосовності (форма A: константи за категорією без `_{код}`).
    .DESCRIPTION
        Правило вибору (✎ 2026-10-13, C11_10 / C11_11_05):
          1. `-FromApplicability` (правило з `substancesFromApplicability: true`) - завжди об'єднання кодів словника;
          2а. (✎ 2026-10-14) категорії змішані (суфікси речовин + потоки FG/LPG без суфікса, `-MixedFuelStreamCategories`) -
              суфікси ∪ словник застосовності: речовини FG/LPG-набору не відкидаються;
          2. інакше, якщо в категоріях констант є суфікси `_(\d{3,4})$` (`-SuffixCodes` не порожній), - вони, незмінно;
          3. інакше (форма A: суфіксів немає) - об'єднання кодів словника застосовності цієї методології, якщо воно є;
          4. інакше порожньо (склад не задається).
        Пункти 1-3 нічого не вигадують: у форми A старий код ітерує коди речовин зі словника (`pollutionCodeByFuelType`
        у ECW_C11_10, `codeGroup` у ECW_C11_11_05), а константи шукає за категорією-ключем без коду; рушій
        (`ConstantResolver`) спершу пробує `{ключ}_{код}`, далі `{ключ}`, тож константи без суфікса знаходяться.
    .PARAMETER SuffixCodes
        Результат `Get-LandSubstanceCodes` по категоріях констант чернетки.
    .PARAMETER ApplicabilityCodes
        Об'єкт `methodologies.<код>.codes` з land-substance-applicability.json або `$null`.
    .PARAMETER FromApplicability
        Правило явно просить склад зі словника.
    .OUTPUTS
        pscustomobject: Codes (string[], за зростанням), Source = 'Applicability' | 'Suffix' | 'SuffixUnionApplicability' |
        'ApplicabilityFallback' | 'None'.
    .PARAMETER MixedFuelStreamCategories
        Результат Test-LandMixedFuelStreamCategories: суфікси речовин і потоки FG/LPG без суфікса разом (правило 2а, див. вище;
        викликач передає TRUE лише коли для методології є обгортка SUBSTANCE_APPLICABLE).
    #>
    param(
        [AllowNull()][AllowEmptyCollection()][string[]]$SuffixCodes,
        [AllowNull()]$ApplicabilityCodes,
        [bool]$FromApplicability = $false,
        [bool]$MixedFuelStreamCategories = $false
    )

    # Get-LandApplicabilityCodeUnion повертає `,@(...)`: прямий вираз-присвоєння знімає обгортку, `@(f)` лишив би вкладений масив.
    $union = [string[]](Get-LandApplicabilityCodeUnion -Codes $ApplicabilityCodes)
    if ($null -eq $union) { $union = [string[]]@() }
    $suffix = @($SuffixCodes | Where-Object { $_ })
    if ($FromApplicability) {
        return [pscustomobject]@{ Codes = [string[]]$union; Source = 'Applicability' }
    }
    if ($suffix.Count -gt 0 -and $MixedFuelStreamCategories -and $union.Count -gt 0) {
        # Правило 2а (✎ 2026-10-14, LAND-5094 K-п'ятірка): категорії змішані - форма B із суфіксами речовин і форма A
        # потоків FG/LPG без суфікса (C11_01: `..._Wwater_1052` поряд із `6646_FG`). Старий код ітерує коди словника
        # застосовності, тож склад = суфікси ∪ словник; зайві речовини рядка відсікає SUBSTANCE_APPLICABLE
        # (етап SubstanceApplicability у тій самій чернетці).
        $all = @{}
        foreach ($c in @($suffix) + @($union)) { $all[[string]([int]$c)] = $true }
        return [pscustomobject]@{ Codes = [string[]]@($all.Keys | Sort-Object { [int]$_ }); Source = 'SuffixUnionApplicability' }
    }
    if ($suffix.Count -gt 0) {
        return [pscustomobject]@{ Codes = [string[]]$suffix; Source = 'Suffix' }
    }
    if ($union.Count -gt 0) {
        return [pscustomobject]@{ Codes = [string[]]$union; Source = 'ApplicabilityFallback' }
    }
    return [pscustomobject]@{ Codes = [string[]]@(); Source = 'None' }
}

function Test-LandLegacyPrefixAccepts {
    <#
    .SYNOPSIS
        Префікс, який старий C# (PrefixProvider) зіставляє з текстом потоку/палива: перший ключ, що міститься в тексті
        або містить його, БЕЗ регістру (`ToLower().Contains` в обидва боки). `$null`, якщо жоден не підійшов.
    .PARAMETER ProviderEntries
        Пари [ключ, префікс] у порядку словника (`providerEntries` з land-substance-applicability.json).
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ProviderEntries
    )

    $t = $Text.ToLowerInvariant()
    foreach ($pair in @($ProviderEntries)) {
        $k = ([string]$pair[0]).ToLowerInvariant()
        if ($t.Contains($k) -or $k.Contains($t)) { return [string]$pair[1] }
    }
    return $null
}

function Get-LandSelectorCoverageGaps {
    <#
    .SYNOPSIS
        Написання потоку/палива з даних, які легасі приймає (ToLower().Contains), але для яких у довіднику застосовності
        методології немає ключа з ТИМИ САМИМИ ординальними символами (регістр значущий: ignoreCase=false) - рядок ECR
        мовчки втратить речовини (SUBSTANCE_APPLICABLE -> FALSE).
    .DESCRIPTION
        Лише порівняння; нічого не читає з БД і не пише. Методології без providerEntries (NAE/InvNum/map/contains) і
        ланцюг `prefix-chain` (C11_01: легасі приймає будь-що через streamfuel) тут не перевіряються.
    .PARAMETER Observed
        Елементи з полями Value (текст селектора) і Rows (кількість рядків даних).
    .PARAMETER Methodologies
        Об'єкт `methodologies` з land-substance-applicability.json (kind, selector, providerEntries, codes).
    .PARAMETER Entries
        Записи довідника з моделі (fields.METHODOLOGY/SELECTOR_KIND/SELECTOR_VALUE/SUBSTANCE_CODE).
    .OUTPUTS
        pscustomobject: Methodology, Kind, Value, Rows, Prefix, MissingCodes (string[]).
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Observed,
        [Parameter(Mandatory)]$Methodologies,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Entries
    )

    # (методологія|вид|значення) -> HashSet кодів; ординальний словник
    $have = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.HashSet[string]]' ([StringComparer]::Ordinal)
    foreach ($e in @($Entries)) {
        $f = $e.fields
        if (([string]$f.SELECTOR_KIND) -ceq '*') { continue }
        $k = ([string]$f.METHODOLOGY) + '|' + ([string]$f.SELECTOR_KIND) + '|' + ([string]$f.SELECTOR_VALUE)
        if (-not $have.ContainsKey($k)) { $have[$k] = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal) }
        [void]$have[$k].Add([string]$f.SUBSTANCE_CODE)
    }

    $gaps = New-Object System.Collections.Generic.List[object]
    foreach ($m in $Methodologies.PSObject.Properties) {
        $spec = $m.Value
        if ($spec.selector -ne 'prefix-required') { continue }
        $provider = @($spec.providerEntries)
        foreach ($o in @($Observed)) {
            $prefix = Test-LandLegacyPrefixAccepts -Text ([string]$o.Value) -ProviderEntries $provider
            if ($null -eq $prefix) { continue }
            $want = @($spec.codes.PSObject.Properties | Where-Object { $_.Name -ceq $prefix } | ForEach-Object { $_.Value } | ForEach-Object { [string]$_ })
            if ($want.Count -eq 0) { continue }   # префікс без словника кодів: легасі KeyNotFound/пропуск
            $k = $m.Name + '|' + [string]$spec.kind + '|' + [string]$o.Value
            $missing = @($want | Where-Object { -not ($have.ContainsKey($k) -and $have[$k].Contains($_)) })
            if ($missing.Count -gt 0) {
                $gaps.Add([pscustomobject]@{
                        Methodology = $m.Name; Kind = [string]$spec.kind; Value = [string]$o.Value
                        Rows = [int]$o.Rows; Prefix = $prefix; MissingCodes = [string[]]$missing
                    })
            }
        }
    }
    # Елементи повертаються як звичайний потік (БЕЗ `return ,` - інакше `@(Get-...)` у виклику дає масив із одного масиву,
    # Count=1 за 0 прогалин). Виклик завжди через `@(...)`: 0 -> порожній масив, 1 -> масив із 1, N -> масив із N (PS 5.1).
    return $gaps.ToArray()
}

function Get-LandSelectorCoverageClassified {
    <#
    .SYNOPSIS
        Прогалини Get-LandSelectorCoverageGaps з прив'язкою до таблиць і методологій: GAP лише там, де методологію справді
        обслуговує таблиця (активна прив'язка cfg.CalculationBinding) і значення лежить у колонці-аргументі методології.
    .DESCRIPTION
        Усе інше - INFO (довідково, не впливає на код виходу): таблиця без активної прив'язки цієї методології або колонка,
        яку методологія не читає (напр. Streams/TypeOfStreams, тоді як аргумент - Land_TypeFuel). З -IncludeUnbound
        (стара поведінка) кожна прогалина - GAP. Нічого не читає з БД.
    .PARAMETER Observed
        Елементи Table, Column, Value, Rows (одне написання в одній колонці таблиці).
    .PARAMETER Bindings
        Елементи Methodology, Table - активні прив'язки.
    .PARAMETER ExtraArguments
        hashtable: методологія -> додаткові коди колонок-аргументів (на додачу до `argument` з довідника).
    .OUTPUTS
        pscustomobject: Status (GAP|INFO), Methodology, Table, Column, Kind, Value, Rows, Prefix, MissingCodes, Reason.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Observed,
        [Parameter(Mandatory)]$Methodologies,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Entries,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Bindings,
        [hashtable]$ExtraArguments = @{},
        [switch]$IncludeUnbound
    )

    $bound = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($b in @($Bindings)) { if ($null -ne $b) { [void]$bound.Add(([string]$b.Methodology) + '|' + ([string]$b.Table)) } }

    $out = New-Object System.Collections.Generic.List[object]
    foreach ($m in $Methodologies.PSObject.Properties) {
        $argSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        if (-not [string]::IsNullOrEmpty([string]$m.Value.argument)) { [void]$argSet.Add([string]$m.Value.argument) }
        if ($ExtraArguments.ContainsKey($m.Name)) { foreach ($a in @($ExtraArguments[$m.Name])) { [void]$argSet.Add([string]$a) } }
        $one = [pscustomobject]@{}
        $one | Add-Member -NotePropertyName $m.Name -NotePropertyValue $m.Value
        foreach ($o in @($Observed)) {
            $gaps = @(Get-LandSelectorCoverageGaps -Observed @($o) -Methodologies $one -Entries $Entries | ForEach-Object { $_ })
            foreach ($g in $gaps) {
                $isArg = $argSet.Contains([string]$o.Column)
                $isBound = $bound.Contains($m.Name + '|' + [string]$o.Table)
                $reason = if ($isArg -and $isBound) { 'bound' } elseif (-not $isBound) { 'unbound-table' } else { 'other-argument' }
                $status = if ($IncludeUnbound -or ($isArg -and $isBound)) { 'GAP' } else { 'INFO' }
                $out.Add([pscustomobject]@{
                        Status = $status; Methodology = $g.Methodology; Table = [string]$o.Table; Column = [string]$o.Column
                        Kind = $g.Kind; Value = $g.Value; Rows = $g.Rows; Prefix = $g.Prefix; MissingCodes = $g.MissingCodes; Reason = $reason
                    })
            }
        }
    }
    return $out.ToArray()
}
