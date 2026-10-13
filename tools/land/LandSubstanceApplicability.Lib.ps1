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
