# tools/land/LandLegacyPublish.Lib.ps1
# Чисті функції етапу LegacyPublish (Build-LandTemplate.ps1) - без мережі й без БД, щоб їх можна було перевірити
# тестом на синтетичних відповідях. Прод-даних тут немає і бути не може.
#
# Золотий знімок виходів: публікація з «значенням-вартовим» відхиляється `goldenSetDiverged`, і відповідь називає
# ФАКТИЧНІ числа. Вихід по речовинах дає по одному числу на (вихід x речовина), тож очікування записується ключем
# `код@речовина` (GoldenSet.SubstanceSeparator); одне число на код виходу годилося лише для виходів без речовини.
# Вихід, якого не було взагалі (гілка методології не спрацювала: type1/type2), у знімок не потрапляє - очікувати
# відсутнє не можна, а його відсутність у знімку не ховає нічого, чого рушій не видав.

function Get-LandGoldenActuals {
    <#
    .SYNOPSIS
        Фактичні значення з відповіді відмови публікації `goldenSetDiverged`: (вихід, речовина, число).
    .DESCRIPTION
        Джерело 1 - структурована відповідь: `goldenSet[].mismatches[]` (outputCode, substanceEntryId, actual).
        Джерело 2 (запасне) - текст повідомлення: `<вихід> (речовина <id>|без речовини): очікували ..., отримали <число>`.
        Розбіжності без числа (`actual` порожнє: «виходу не було») пропускаються. Дубль (вихід, речовина) - перший.
    .PARAMETER Json
        Тіло відповіді як об'єкт (ConvertFrom-Json); може бути $null.
    .PARAMETER Text
        Тіло відповіді як текст; може бути порожнім.
    .OUTPUTS
        Масив pscustomobject { Output; Substance ([int] або $null); Actual ([double]) }.
    #>
    param($Json = $null, [string]$Text = '')

    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    $seen = @{}
    $items = New-Object System.Collections.ArrayList
    $add = {
        param([string]$Output, $Substance, $Actual)
        if ([string]::IsNullOrWhiteSpace($Output) -or $null -eq $Actual) { return }
        $sub = $null
        if ($null -ne $Substance -and "$Substance" -ne '') { $sub = [int]$Substance }
        $key = if ($null -eq $sub) { $Output } else { "$Output@$sub" }
        if ($seen.ContainsKey($key)) { return }
        $seen[$key] = $true
        [void]$items.Add([pscustomobject]@{ Output = $Output; Substance = $sub; Actual = [double]::Parse([string]::Format($inv, '{0}', $Actual), $inv) })
    }

    $set = $null
    if ($null -ne $Json -and $Json.PSObject.Properties.Match('goldenSet').Count -gt 0) { $set = @($Json.goldenSet) }
    if ($null -ne $set -and $set.Count -gt 0) {
        foreach ($verdict in $set) {
            foreach ($mm in @($verdict.mismatches)) {
                & $add ([string]$mm.outputCode) $mm.substanceEntryId $mm.actual
            }
        }
        return @($items.ToArray())
    }

    foreach ($m in [regex]::Matches($Text, '([A-Za-z][A-Za-z0-9_]*)\s*\((?:речовина\s+(\d+)|без речовини)\)[^\r\n]{0,120}?(?:отримали|got|actual)[^\d\-]{0,12}(-?\d+(?:[.,]\d+)?(?:[eE][+\-]?\d+)?)')) {
        & $add $m.Groups[1].Value $(if ($m.Groups[2].Success) { $m.Groups[2].Value } else { $null }) ($m.Groups[3].Value.Replace(',', '.'))
    }
    return @($items.ToArray())
}

function New-LandSnapshotExpected {
    <#
    .SYNOPSIS
        Очікуване золотого знімка: ключ `вихід` (без речовини) або `вихід@речовина` -> число. Лише оголошені виходи.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Actuals,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Outputs
    )

    $exp = [ordered]@{}
    foreach ($a in $Actuals) {
        if ($Outputs -notcontains [string]$a.Output) { continue }
        $key = if ($null -eq $a.Substance) { [string]$a.Output } else { '{0}@{1}' -f $a.Output, $a.Substance }
        if (-not $exp.Contains($key)) { $exp[$key] = [double]$a.Actual }
    }
    return $exp
}
