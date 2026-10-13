# tools/land/LandParityBranch.Lib.ps1
# Вибір гілки type1/type2 у методології C12_02 (рішення координатора 13.10 «ТАК, за вашою рекомендацією»).
# Чиста логіка без звернень до API; значень прода тут немає.
#
# Старий код ECW_C12_02: для кожного компонента (301/304/330/337) рахується ЛИШЕ type1, якщо Land_Measure_Component_<код>
# має значення, інакше ЛИШЕ type2; код 415 - завжди type2. В ECR обидві гілки - виходи (ECW_EC_*_<код>_type1/_type2), тож
# при наявному значенні компонента вийшли б ОБИДВІ. Тому формула type2 обгортається так, щоб за наявного значення вона
# давала порожнє (null): `if(@Land_Measure_Component_<код> = NULL, <вираз type2>, NULL)`. Порівняння з NULL на рівність
# дозволене (02b §6.2: null = null -> TRUE, null = 1 -> FALSE); аргумент, зібраний з порожньої клітинки, - легітимний
# null, а не #ARG. Вихід без числа не пишеться (GenericCalculationModule.Emit): гілка просто не дає результату.
# Рушій, контракт, міграція не змінюються; обгортка не чіпає type1 і код 415 (type1_415 - окремо, у кінці файлу).

# Методологія -> коди компонентів, для яких type2 обгортається. 415 тут немає навмисно: type1 для 415 у старому коді
# не існує (завжди type2), Land_Measure_Component_415 не передається.
$script:LandParityBranchMethodologies = [ordered]@{
    'ECW_C12_02' = @('301', '304', '330', '337')
}

function Get-LandParityBranchMethodologies {
    <# Словник «методологія -> коди компонентів» (копія, щоб виклик не міняв стан модуля). #>
    $copy = [ordered]@{}
    foreach ($k in $script:LandParityBranchMethodologies.Keys) { $copy[$k] = @($script:LandParityBranchMethodologies[$k]) }
    return $copy
}

function Get-LandParityBranchComponent {
    <#
    .SYNOPSIS
        Код компонента, якщо формула - type2 виходу (tons або gsec) цього компонента; інакше $null.
    .DESCRIPTION
        Формула визначається за кодом `ECW_EC_(tons|gsec)_<код>_type2` (так імпортована методологія називає виходи AF).
        type1 і код, якого немає в списку методології (415), повертають $null.
    #>
    param(
        [Parameter(Mandatory)][string]$FormulaCode,
        [Parameter(Mandatory)][string[]]$Components
    )

    $m = [regex]::Match($FormulaCode, '^ECW_EC_(?:tons|gsec)_([0-9]+)_type2$')
    if (-not $m.Success) { return $null }
    $code = $m.Groups[1].Value
    if ($Components -notcontains $code) { return $null }
    return $code
}

function Get-LandParityBranchHead {
    param([Parameter(Mandatory)][string]$Component)
    return "if(@Land_Measure_Component_$Component = NULL, "
}

function Test-LandParityBranchWrapped {
    <# TRUE, якщо вираз уже має обгортку цього компонента (повторний запуск етапу - 0 змін). #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        [Parameter(Mandatory)][string]$Component
    )

    $text = $Expression.Trim()
    $head = Get-LandParityBranchHead -Component $Component
    return $text.StartsWith($head, [StringComparison]::Ordinal) -and $text.EndsWith(', NULL)', [StringComparison]::Ordinal)
}

function Convert-LandParityBranchFormula {
    <#
    .SYNOPSIS
        Обгортає вираз type2: порожній результат, якщо @Land_Measure_Component_<код> має значення; компонент-аргументи
        формули отримують маркер необов'язковості `?` (D-397).
    .DESCRIPTION
        Список аргументів (`;`) отримує `@Land_Measure_Component_<код>?`, якщо його ще немає (тільки коли список заданий:
        $null лишається $null - формула без списку не перевіряється, а порожній рядок «оголошує нуль аргументів»).
        Порожній вираз не обгортається. Повторний виклик на вже обгорнуту й позначену формулу - без змін
        (Changed = $false).

        Навіщо `?`. Міст Land_Measure_Component_<код> (GTU, MAX по порожній множині) НЕ зберігає комірку, коли рядка-джерела
        з компонентом немає: без маркера аргумент не збирається (#ARG), type2 для такого рядка не рахується, хоча старий код
        його рахує (компонента немає -> type2). Маркер `?` робить відсутню комірку null (GenericCalculationModule,
        OptionalArguments.Fill), а `null = NULL` - TRUE (Evaluator, 02b §6.2). Позначаються ВСІ елементи списку виду
        `Land_Measure_Component_*` (не лише компонент обгортки); чужі елементи списку не чіпаються. Формула вже обгорнута,
        але без `?` (чернетка до цього правила) - обгортка не повторюється, ставиться лише маркер.
    .OUTPUTS
        pscustomobject: Changed, Expression, ArgumentsCsv.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        [AllowNull()][AllowEmptyString()]$ArgumentsCsv = $null,
        [Parameter(Mandatory)][string]$Component
    )

    $text = $Expression.Trim()
    if ($text.Length -eq 0) {
        return [pscustomobject]@{ Changed = $false; Expression = $Expression; ArgumentsCsv = $ArgumentsCsv }
    }

    $wrappedAlready = Test-LandParityBranchWrapped -Expression $text -Component $Component
    $newExpression = if ($wrappedAlready) { $Expression } else { (Get-LandParityBranchHead -Component $Component) + $text + ', NULL)' }

    $newArguments = $ArgumentsCsv
    $argsChanged = $false
    if ($null -ne $ArgumentsCsv) {
        $name = 'Land_Measure_Component_' + $Component
        $tokens = New-Object System.Collections.Generic.List[string]
        $present = $false
        foreach ($t in (([string]$ArgumentsCsv) -split ';')) {
            $x = $t.Trim()
            if ($x.Length -eq 0) { continue }
            $core = $x.TrimStart('@').TrimEnd('?').TrimEnd()
            if ($core -ieq $name) { $present = $true }
            # Компонент-міст може не мати комірки (порожня множина GTU): маркер `?`, інакше #ARG.
            if ($core -like 'Land_Measure_Component_*' -and -not $x.EndsWith('?')) { $x = $x + '?'; $argsChanged = $true }
            $tokens.Add($x)
        }
        if (-not $present) { $tokens.Add('@' + $name + '?'); $argsChanged = $true }
        if ($argsChanged) { $newArguments = (($tokens -join ';') + ';') }
    }

    $changed = (-not $wrappedAlready) -or $argsChanged
    return [pscustomobject]@{ Changed = $changed; Expression = $newExpression; ArgumentsCsv = $newArguments }
}

# --- Код 415: type1 у старому коді не існує ---
# У старому коді Land_Measure_Component_415 не читається й не передається (цикл читання пропускає 415), тому для 415
# завжди type2, а вихід type1_415 ніколи не пишеться. В AF-пакеті ECW_EC_(tons|gsec)_415_type1 існує з виразом, ідентичним
# type2 і без компонента, тож у ECR вийшли б ОБИДВА з однаковим числом. Вираз type1_415 ставиться в `NULL` (вихід без числа
# не пишеться, GenericCalculationModule.Emit); прив'язка виходу, список аргументів і type2 не змінюються. Повтор - 0 змін.
$script:LandParityBranchNullType1 = [ordered]@{
    'ECW_C12_02' = @('415')
}

function Get-LandParityBranchNullType1Methodologies {
    <# Словник «методологія -> коди, чий type1 має бути без числа» (копія). #>
    $copy = [ordered]@{}
    foreach ($k in $script:LandParityBranchNullType1.Keys) { $copy[$k] = @($script:LandParityBranchNullType1[$k]) }
    return $copy
}

function Get-LandParityBranchNullType1Component {
    <#
    .SYNOPSIS
        Код, якщо формула - type1 виходу (tons або gsec) коду зі списку Components; інакше $null (type2 і інші коди).
    #>
    param(
        [Parameter(Mandatory)][string]$FormulaCode,
        [Parameter(Mandatory)][string[]]$Components
    )

    $m = [regex]::Match($FormulaCode, '^ECW_EC_(?:tons|gsec)_([0-9]+)_type1$')
    if (-not $m.Success) { return $null }
    $code = $m.Groups[1].Value
    if ($Components -notcontains $code) { return $null }
    return $code
}

function Convert-LandParityBranchNullFormula {
    <#
    .SYNOPSIS
        Вираз type1 без числа: `NULL`. Уже `NULL` (або порожній вираз) - без змін (Changed = $false).
    .OUTPUTS
        pscustomobject: Changed, Expression.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression
    )

    $text = $Expression.Trim()
    if ($text.Length -eq 0 -or $text -ieq 'NULL') {
        return [pscustomobject]@{ Changed = $false; Expression = $Expression }
    }
    return [pscustomobject]@{ Changed = $true; Expression = 'NULL' }
}
