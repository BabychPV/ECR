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
