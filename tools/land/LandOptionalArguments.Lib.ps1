# tools/land/LandOptionalArguments.Lib.ps1
# Необов'язкові аргументи методологій Land (D-397, lane integ-optional-arg): маркер `?` у кінці елемента `;`-списку
# аргументів формули (`@Land_Measure_OperatingTime?`). Чиста логіка без звернень до API; значень прода тут немає.
#
# Навіщо. У старому коді ECW_C11_10 / C11_11_03 / C11_16 деякі аргументи НЕ передаються для частини таблиць (клас O у
# LAND-D8-ARGS: атрибута AF-таблиці немає, `requiredFields` його не містить) - ключа в словнику немає, формула лишається
# нерозв'язаною, результат не пишеться. ECR-CALC-0438 вимагає колонку для кожного @-аргументу в кожній прив'язаній
# таблиці, тому без маркера чернетка не публікується. «NULL-міст» (колонка-Formula з виразом NULL) тут НЕ потрібен і не
# будується: за LAND-D8-Q1-NULL-BRIDGE він давав би #ARG, а не null. Маркер `?` знімає вимогу колонки (0438) і в
# рантаймі робить відсутню комірку null (як порожню). Множина необов'язкових - per-версія (D-397).
#
# Перелік - з моделі (argumentBridges.optionalArguments), його складає model-gen (af_attr_bridges.py, OPTIONAL_ARGUMENTS).

function ConvertTo-LandOptionalArgumentName {
    <# Ключ порівняння імені аргументу: як ArgumentDeclarationChecker.NormalizeName (без `@`, без кінцевого `?`, `.`->`_`). #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Token)

    $x = $Token.Trim()
    if ($x.StartsWith('@')) { $x = $x.Substring(1) }
    if ($x.EndsWith('?')) { $x = $x.Substring(0, $x.Length - 1).TrimEnd() }
    return $x.Replace('.', '_')
}

function Get-LandOptionalArguments {
    <#
    .SYNOPSIS
        Словник «методологія -> імена необов'язкових аргументів» з моделі (argumentBridges.optionalArguments.methodologies).
    .PARAMETER Model
        Розібрана land-template.model.json (ConvertFrom-Json).
    #>
    param([Parameter(Mandatory)]$Model)

    $result = [ordered]@{}
    $section = $Model.argumentBridges.optionalArguments
    if ($null -eq $section) { return $result }
    foreach ($p in $section.methodologies.PSObject.Properties) {
        $result[[string]$p.Name] = @($p.Value.arguments | ForEach-Object { [string]$_ })
    }
    return $result
}

function Convert-LandOptionalArgumentsCsv {
    <#
    .SYNOPSIS
        Ставить `?` у кінець елементів списку аргументів, чиє ім'я є в -Arguments; решту елементів не чіпає.
    .DESCRIPTION
        $null лишається $null (формула без списку: нічого позначати). Порожні елементи не змінюються. Уже позначений
        елемент не змінюється; повторний виклик - Changed = $false. Імена порівнюються як у рушії: без `@`, без
        регістру, `.` = `_`. Ім'я з -Arguments, якого в списку немає, повертається в Missing (формула його не оголошує).
    .OUTPUTS
        pscustomobject: Changed, ArgumentsCsv, Marked (позначені імена), Missing (немає в списку).
    #>
    param(
        # Без типу [string]: $null мусить лишитися $null (порожній рядок «оголошує нуль аргументів», ECR-CALC-0432).
        [AllowNull()][AllowEmptyString()]$ArgumentsCsv = $null,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    if ($null -eq $ArgumentsCsv) {
        return [pscustomobject]@{ Changed = $false; ArgumentsCsv = $null; Marked = @(); Missing = @($Arguments) }
    }

    $wanted = @{}
    foreach ($a in $Arguments) { $wanted[(ConvertTo-LandOptionalArgumentName -Token $a).ToLowerInvariant()] = $a }

    $tokens = New-Object System.Collections.Generic.List[string]
    $marked = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    $changed = $false
    foreach ($t in ([string]$ArgumentsCsv -split ';')) {
        $x = $t.Trim()
        if ($x.Length -eq 0) { continue }
        $key = (ConvertTo-LandOptionalArgumentName -Token $x).ToLowerInvariant()
        if ($wanted.ContainsKey($key)) {
            $seen[$key] = $true
            if (-not $x.EndsWith('?')) {
                $x = $x + '?'
                $changed = $true
                $marked.Add($wanted[$key])
            }
        }
        $tokens.Add($x)
    }

    $missing = @($wanted.Keys | Where-Object { -not $seen.ContainsKey($_) } | ForEach-Object { $wanted[$_] })
    $newCsv = if ($changed) { (($tokens -join ';') + ';') } else { $ArgumentsCsv }
    return [pscustomobject]@{ Changed = $changed; ArgumentsCsv = $newCsv; Marked = @($marked); Missing = $missing }
}
