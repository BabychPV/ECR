# tools/land/LandParityRuleArgs.Lib.ps1
# Чисті функції для етапу LandParity в Build-LandTemplate.ps1 (без мережі й без БД) — щоб їх перевіряв тест на
# синтетичних даних. Значень констант тут немає: працюємо лише з КЛЮЧАМИ (категоріями) і з виразом правила.
#
# Навіщо: правило категорії (PUT .../category-rule) читає аргументи рядка (@Land_NAE, @Land_TypeFuelOrStream,
# @Land_Streams, @Land_InvNumEqp ...). Тест-кейс чернетки (PARITY_1) їх не несе, тож simulate/publish падає
# `ECR-CALC-0422 categoryRuleFailed` (#ARG), що схоже на «зламане правило». Функції нижче дають мінімальний
# набір аргументів, потрібних правилу, і склад речовин версії з суфіксів категорій констант.

function Get-LandRuleArgumentNames {
    <#
    .SYNOPSIS
        Імена аргументів рядка, які читає вираз правила (`@Name`), без `@`, у порядку першої появи.
    #>
    param([Parameter(Mandatory)][string]$Expression)
    $seen = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($Expression, '@([A-Za-z_][A-Za-z0-9_]*)')) {
        $n = $m.Groups[1].Value
        if (-not $seen.Contains($n)) { $seen.Add($n) }
    }
    return , @($seen)
}

function Get-LandRuleArgumentLiterals {
    <#
    .SYNOPSIS
        Літерали, з якими вираз порівнює аргумент через `in(@Name, 'a', 'b', ...)`, у порядку появи (без повторів).
        Порожньо — вираз передає аргумент як є (`@Land_NAE`) або порівнює інакше.
    #>
    param([Parameter(Mandatory)][string]$Expression, [Parameter(Mandatory)][string]$Name)
    $out = New-Object System.Collections.Generic.List[string]
    $pattern = 'in\(\s*@' + [regex]::Escape($Name) + '\s*((?:,\s*''[^'']*''\s*)+)\)'
    foreach ($m in [regex]::Matches($Expression, $pattern)) {
        foreach ($l in [regex]::Matches($m.Groups[1].Value, '''([^'']*)''')) {
            $v = $l.Groups[1].Value
            if ($v.Length -gt 0 -and -not $out.Contains($v)) { $out.Add($v) }
        }
    }
    return , @($out)
}

function Get-LandSubstanceCodes {
    <#
    .SYNOPSIS
        Склад речовин версії з категорій її констант: суфікс `_(\d{3,4})$` (форма B: {ключ}_{код речовини}).
        Повертає числові коди без провідних нулів, відсортовані за зростанням.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowNull()][AllowEmptyString()][string[]]$Categories)
    $codes = @{}
    foreach ($c in @($Categories)) {
        if ($null -eq $c) { continue }
        $m = [regex]::Match($c.Trim(), '_(\d{3,4})$')
        if ($m.Success) { $codes[[string]([int]$m.Groups[1].Value)] = $true }
    }
    return , @($codes.Keys | Sort-Object { [int]$_ })
}

function Get-LandNaeCandidates {
    <#
    .SYNOPSIS
        Кандидати NAE для тест-кейсу: частина категорії до першого `_`, якщо вона починається з цифри
        (`6783(002)_sludge_333` -> `6783(002)`; `6585 (001)` лишається як є). Без повторів, у порядку категорій.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowNull()][AllowEmptyString()][string[]]$Categories, [int]$Max = 8)
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($c in @($Categories)) {
        if ($null -eq $c) { continue }
        $t = $c.Trim()
        if ($t -notmatch '^[0-9]') { continue }
        $nae = ($t -split '_')[0]
        if ($nae.Length -gt 0 -and -not $out.Contains($nae)) { $out.Add($nae) }
        if ($out.Count -ge $Max) { break }
    }
    return , @($out)
}

function Merge-LandParityArguments {
    <#
    .SYNOPSIS
        Додає до аргументів тест-кейсу ті, яких бракує (за кодом); наявні НЕ перезаписуються.
    .PARAMETER Existing  Масив {argumentCode, value, valueString, unitId[, entryId]} з inputJson.arguments (може бути порожнім).
    .PARAMETER Needed    Хеш-таблиця: код аргументу -> текстове значення.
    .OUTPUTS
        pscustomobject: Arguments (злитий масив), Added (коди, що додано).
    #>
    param(
        [AllowEmptyCollection()][AllowNull()][object[]]$Existing,
        [Parameter(Mandatory)][hashtable]$Needed
    )
    $have = @{}
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($a in @($Existing)) {
        if ($null -eq $a) { continue }
        $have[[string]$a.argumentCode] = $true
        $list.Add($a)
    }
    $added = New-Object System.Collections.Generic.List[string]
    foreach ($k in ($Needed.Keys | Sort-Object)) {
        if ($have.ContainsKey([string]$k)) { continue }
        $list.Add([ordered]@{ argumentCode = [string]$k; value = $null; valueString = [string]$Needed[$k]; unitId = $null })
        $added.Add([string]$k)
    }
    $result = New-Object psobject
    $result | Add-Member -NotePropertyName Arguments -NotePropertyValue $list.ToArray()
    $result | Add-Member -NotePropertyName Added -NotePropertyValue $added.ToArray()
    return $result
}

function Get-LandRuleSampleArguments {
    <#
    .SYNOPSIS
        Мінімальний набір {код аргументу -> текст}: перший літерал `in(...)` для кожного аргументу, що порівнюється
        літералами; NAE без літералів (передається як є або конкатенується) — кандидат; інший аргумент без літералів
        пропускається (його значення правилу невідоме).
    #>
    param(
        [Parameter(Mandatory)][string]$Expression,
        [string]$NaeCandidate,
        [string]$NaeArgument = 'Land_NAE'
    )
    $picked = @{}
    foreach ($argCode in (Get-LandRuleArgumentNames -Expression $Expression)) {
        $lits = Get-LandRuleArgumentLiterals -Expression $Expression -Name $argCode
        if ($lits.Count -gt 0) { $picked[$argCode] = $lits[0]; continue }
        if ($argCode -eq $NaeArgument -and $NaeCandidate) { $picked[$argCode] = $NaeCandidate }
    }
    return $picked
}
