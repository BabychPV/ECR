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

function Get-LandGoldenRowColumn {
    <#
    .SYNOPSIS
        Колонка рядка старої системи для аргументу золотого входу. Порядок: сам аргумент; псевдонім шаблону (`IDCodeNAEProject` -> `Land_NAE`);
        суфіксний (`<аргумент>__ECW_Cnn_nn`, рішення K1) -> базовий `<аргумент>`; колонка шаблону-формула, що є лише посиланням `[X]`
        (`Land_TypeFuelOrStream` = `[TypeOfFuel]`), -> `X`. Немає нічого з цього - `$null`.
    .PARAMETER Columns
        Колонки таблиці шаблону (`$tb.columns` моделі): потрібні лише для правила `[X]`; без них воно не діє.
    #>
    param([Parameter(Mandatory)][string]$Argument, [Parameter(Mandatory)][hashtable]$Row, [object[]]$Columns = @())

    if ($Row.ContainsKey($Argument)) { return $Argument }
    if ($Argument -eq 'IDCodeNAEProject' -and $Row.ContainsKey('Land_NAE')) { return 'Land_NAE' }
    $m = [regex]::Match($Argument, '^(?<base>.+?)__ECW_[A-Za-z0-9_]+$')
    if ($m.Success -and $Row.ContainsKey($m.Groups['base'].Value)) { return $m.Groups['base'].Value }
    $col = @($Columns | Where-Object { [string]$_.code -eq $Argument }) | Select-Object -First 1
    if ($null -ne $col -and [string]$col.expression -match '^\s*\[(?<src>[A-Za-z0-9_]+)\]\s*$' -and $Row.ContainsKey($Matches['src'])) { return $Matches['src'] }
    return $null
}

function Test-LandGoldenBridgeArgument {
    <#
    .SYNOPSIS
        Аргумент - мостова колонка шаблону, що читає склад газу HSE400 (`Density20`, `TotalSulphurContent` ...): у таблиці старої системи її немає
        за побудовою, тож відсутність у рядку не робить таблицю «неповною».
    #>
    param([Parameter(Mandatory)][string]$Argument, [object[]]$Columns = @())

    $col = @($Columns | Where-Object { [string]$_.code -eq $Argument }) | Select-Object -First 1
    return ($null -ne $col -and [string]$col.expression -match 'LAND_HSE400_COMPOSITION')
}

function Get-LandGoldenMissingArguments {
    <#
    .SYNOPSIS
        Аргументи, яких рядок старої системи не дає: немає колонки (з псевдонімами Get-LandGoldenRowColumn) і це не мостова колонка HSE400.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments, [Parameter(Mandatory)][hashtable]$Row, [object[]]$Columns = @())

    return @($Arguments | Where-Object {
            $null -eq (Get-LandGoldenRowColumn -Argument $_ -Row $Row -Columns $Columns) -and -not (Test-LandGoldenBridgeArgument -Argument $_ -Columns $Columns)
        })
}

function Get-LandGoldenLookupRegistry {
    <#
    .SYNOPSIS
        Код довідника колонки золотого входу: для Lookup - її довідник; для колонки-формули вибору місяця (`... [X_M01] : ... [X_M02] ...`)
        - довідник першої Lookup-колонки `X_Mnn`, яку вона читає. Інакше `$null`.
    #>
    param($Column, [object[]]$Columns = @())

    if ($null -eq $Column) { return $null }
    if ([string]$Column.dataType -eq 'Lookup' -and $Column.registry) { return [string]$Column.registry }
    $m = [regex]::Match([string]$Column.expression, '\[(?<c>[A-Za-z0-9_]+_M\d\d)\]')
    if (-not $m.Success) { return $null }
    $src = @($Columns | Where-Object { [string]$_.code -eq $m.Groups['c'].Value }) | Select-Object -First 1
    if ($null -ne $src -and [string]$src.dataType -eq 'Lookup' -and $src.registry) { return [string]$src.registry }
    return $null
}

