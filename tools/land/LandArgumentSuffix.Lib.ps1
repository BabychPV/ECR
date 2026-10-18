# tools/land/LandArgumentSuffix.Lib.ps1
# K1 (D-8, клас K - конфлікт джерел аргументів): методологія читає аргумент під ІМЕНЕМ ІЗ СУФІКСОМ методології
# (`Land_Measure_OperatingTime` -> `Land_Measure_OperatingTime__ECW_C11_01`). Чиста логіка без звернень до API;
# значень прода тут немає.
#
# Навіщо. На T9a_17/T9a_27/T9_07 методології однієї таблиці беруть один аргумент з РІЗНИХ атрибутів AF (старий код
# перейменовує ключ у своєму словнику): колонка з кодом аргументу не може бути двома джерелами. Model-gen
# (af_attr_bridges.py, ARGUMENT_SUFFIXES) будує для методології власну колонку-міст з суфіксом, а цей модуль підміняє
# токен у ЧЕРНЕТЦІ: у виразі `@Name` (не префікс довшого імені) і в елементі списку аргументів (з `@` чи без, зберігаючи
# кінцевий `?`). Решту елементів списку не чіпає. Повтор - 0 змін: суфіксоване ім'я продовжується `_`, а межа імені
# `(?![A-Za-z0-9_])` його не збігає.
#
# Перелік - з моделі (argumentBridges.argumentSuffixes), його складає model-gen.

function ConvertTo-LandArgumentSuffixKey {
    <# Ключ порівняння імені аргументу: як ArgumentDeclarationChecker.NormalizeName (без `@`, без кінцевого `?`, `.`->`_`). #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Token)

    $x = $Token.Trim()
    if ($x.StartsWith('@')) { $x = $x.Substring(1) }
    if ($x.EndsWith('?')) { $x = $x.Substring(0, $x.Length - 1).TrimEnd() }
    return $x.Replace('.', '_').ToLowerInvariant()
}

function Get-LandArgumentSuffixes {
    <#
    .SYNOPSIS
        Словник «методологія -> масив {Argument, Column}» з моделі (argumentBridges.argumentSuffixes.methodologies).
    .PARAMETER Model
        Розібрана land-template.model.json (ConvertFrom-Json).
    #>
    param([Parameter(Mandatory)]$Model)

    $result = [ordered]@{}
    $section = $Model.argumentBridges.argumentSuffixes
    if ($null -eq $section) { return $result }
    foreach ($m in $section.methodologies.PSObject.Properties) {
        $items = New-Object System.Collections.Generic.List[object]
        foreach ($a in $m.Value.PSObject.Properties) {
            $items.Add([pscustomobject]@{ Argument = [string]$a.Name; Column = [string]$a.Value.column })
        }
        $result[[string]$m.Name] = $items.ToArray()
    }
    return $result
}

function Convert-LandArgumentSuffixFormula {
    <#
    .SYNOPSIS
        Підміняє аргументи з -Suffixes на суфіксовані колонки у виразі й у списку аргументів однієї формули.
    .DESCRIPTION
        Вираз: `@<Argument>` з межею імені -> `@<Column>`. Список: елемент, чиє ім'я (без `@`, без `?`, без регістру,
        `.` = `_`) збігається з Argument, отримує ім'я Column із збереженням `@` і кінцевого `?`; чужі елементи й порожні
        лишаються. $null у списку лишається $null (формула без списку); порожній рядок не вводиться (ECR-CALC-0432).
        Дубль, що виник би після підміни (суфіксоване ім'я вже є в списку), не додається. Повтор - Changed = $false.
    .PARAMETER Suffixes
        Масив {Argument, Column} (Get-LandArgumentSuffixes).
    .OUTPUTS
        pscustomobject: Changed, Expression, ArgumentsCsv, Replaced (імена Argument, що підмінено).
    #>
    param(
        [AllowNull()][AllowEmptyString()]$Expression = $null,
        [AllowNull()][AllowEmptyString()]$ArgumentsCsv = $null,
        [Parameter(Mandatory)][object[]]$Suffixes
    )

    $newExpr = $Expression
    $replaced = New-Object System.Collections.Generic.HashSet[string]
    if ($null -ne $Expression) {
        foreach ($s in $Suffixes) {
            $rx = '@' + [regex]::Escape([string]$s.Argument) + '(?![A-Za-z0-9_])'
            if ([regex]::IsMatch([string]$newExpr, $rx, 'IgnoreCase')) {
                $newExpr = [regex]::Replace([string]$newExpr, $rx, ('@' + [string]$s.Column).Replace('$', '$$'), 'IgnoreCase')
                [void]$replaced.Add([string]$s.Argument)
            }
        }
    }

    $newCsv = $ArgumentsCsv
    if ($null -ne $ArgumentsCsv) {
        $map = @{}
        foreach ($s in $Suffixes) { $map[(ConvertTo-LandArgumentSuffixKey -Token ([string]$s.Argument))] = $s }
        $tokens = New-Object System.Collections.Generic.List[string]
        $seen = @{}
        $listChanged = $false
        foreach ($t in ([string]$ArgumentsCsv -split ';')) {
            $x = $t.Trim()
            if ($x.Length -eq 0) { continue }
            $key = ConvertTo-LandArgumentSuffixKey -Token $x
            if ($map.ContainsKey($key)) {
                $s = $map[$key]
                $x = $(if ($x.StartsWith('@')) { '@' } else { '' }) + [string]$s.Column + $(if ($x.EndsWith('?')) { '?' } else { '' })
                $key = ConvertTo-LandArgumentSuffixKey -Token $x
                $listChanged = $true
                [void]$replaced.Add([string]$s.Argument)
            }
            if ($seen.ContainsKey($key)) { $listChanged = $true; continue }
            $seen[$key] = $true
            $tokens.Add($x)
        }
        if ($listChanged) { $newCsv = (($tokens -join ';') + ';') }
    }

    $changed = ($newExpr -cne $Expression) -or ($newCsv -cne $ArgumentsCsv)
    return [pscustomobject]@{
        Changed      = $changed
        Expression   = $newExpr
        ArgumentsCsv = $newCsv
        Replaced     = @($replaced)
    }
}

function Add-LandArgumentSuffixTestArguments {
    <#
    .SYNOPSIS
        Додає до аргументів тест-кейсу суфіксований аргумент з ТИМ САМИМ значенням, що й оригінал (PARITY_1).
    .DESCRIPTION
        Без цього simulate дає #ARG (аргумент формули не передано). Оригінал лишається (зайві аргументи тест-кейса рушій
        приймає). Суфіксований, що вже є, не перезаписується. Оригіналу в тест-кейсі немає - нічого не вигадуємо
        (Missing). Копіюються всі поля запису (value, valueString, unitId, entryId), міняється лише argumentCode.
    .PARAMETER Existing  Масив {argumentCode, value, valueString, unitId[, entryId]} з inputJson.arguments.
    .PARAMETER Suffixes  Масив {Argument, Column}.
    .OUTPUTS
        pscustomobject: Arguments (злитий масив), Added (коди колонок), Missing (аргументи без оригіналу в тест-кейсі).
    #>
    param(
        [AllowEmptyCollection()][AllowNull()][object[]]$Existing,
        [Parameter(Mandatory)][object[]]$Suffixes
    )

    $byCode = @{}
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($a in @($Existing)) {
        if ($null -eq $a) { continue }
        $byCode[(ConvertTo-LandArgumentSuffixKey -Token ([string]$a.argumentCode))] = $a
        $list.Add($a)
    }
    $added = New-Object System.Collections.Generic.List[string]
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($s in $Suffixes) {
        $colKey = ConvertTo-LandArgumentSuffixKey -Token ([string]$s.Column)
        if ($byCode.ContainsKey($colKey)) { continue }
        $orig = $byCode[(ConvertTo-LandArgumentSuffixKey -Token ([string]$s.Argument))]
        if ($null -eq $orig) { $missing.Add([string]$s.Argument); continue }
        $copy = [ordered]@{}
        if ($orig -is [System.Collections.IDictionary]) { foreach ($k in $orig.Keys) { $copy[[string]$k] = $orig[$k] } }
        else { foreach ($p in $orig.PSObject.Properties) { $copy[[string]$p.Name] = $p.Value } }
        $copy['argumentCode'] = [string]$s.Column
        $list.Add($copy)
        $byCode[$colKey] = $copy
        $added.Add([string]$s.Column)
    }
    return [pscustomobject]@{ Arguments = $list.ToArray(); Added = $added.ToArray(); Missing = $missing.ToArray() }
}

function Get-LandStringBridgeSuffixMethodologies {
    <#
    .SYNOPSIS
        Коди методологій із суфіксним мостом (argumentSuffixes), вираз якого читає String-колонку тієї ж таблиці.
    .DESCRIPTION
        Приклад: C11_01/C11_02 - `Land_Measure_OperatingTime__ECW_C11_xx` над `OperatingTimeReleaseOnColdVentYesNo_Mnn` (String:
        години або `No - Нет`). У Strict Text x Number = #VALUE, у Legacy числовий рядок ToValue перетворює на число.
        Тому такі методології мають лишатися Legacy (діагноз LAND-POST-K3, 2026-10-15).
    #>
    param([Parameter(Mandatory)]$Model)

    $hit = New-Object System.Collections.Generic.HashSet[string]
    $sfx = Get-LandArgumentSuffixes -Model $Model
    foreach ($sheet in @($Model.sheets)) {
        foreach ($t in @($sheet.tables)) {
            $strCols = @(@($t.columns) | Where-Object { [string]$_.dataType -eq 'String' } | ForEach-Object { [string]$_.code })
            if ($strCols.Count -eq 0) { continue }
            foreach ($m in $sfx.Keys) {
                foreach ($s in $sfx[$m]) {
                    $b = @($t.columns) | Where-Object { [string]$_.code -eq [string]$s.Column } | Select-Object -First 1
                    if ($null -eq $b -or [string]::IsNullOrEmpty([string]$b.expression)) { continue }
                    foreach ($c in $strCols) {
                        if ([regex]::IsMatch([string]$b.expression, '(?<![A-Za-z0-9_])' + [regex]::Escape($c) + '(?![A-Za-z0-9_])')) { [void]$hit.Add([string]$m); break }
                    }
                }
            }
        }
    }
    return @($hit | Sort-Object)
}

function Get-LandStringBridgeStrictMethodologies {
    <#
    .SYNOPSIS
        Із -Methodologies (Get-LandStringBridgeSuffixMethodologies) ті, чия остання версія має NumericMode=Strict.
    .PARAMETER Versions
        Масив {Code, NumericMode} - остання версія кожної методології на стенді.
    #>
    param([string[]]$Methodologies = @(), [object[]]$Versions = @())

    return @(@($Versions) | Where-Object { $null -ne $_ -and @($Methodologies) -contains [string]$_.Code -and [string]$_.NumericMode -eq 'Strict' } |
        ForEach-Object { [string]$_.Code } | Sort-Object)
}

function Get-LandTextNumericSourceMethodologies {
    <#
    .SYNOPSIS
        Методології, джерела яких - числа, збережені як String (клас 'число як String', D-437; C12_01 Legacy V2).
    .DESCRIPTION
        Перелік тримаємо тут, не в land-template.model.json (модель не регенеруємо). Список закритий: додавати
        лише за рішенням і діагнозом (LAND-POST-K4-DIAG-C12-01-PRODUCT-2026-10-15.md, K1-WINDOW6-C1201-2026-10-15.md).
    #>
    return @('ECW_C12_01')
}

function Test-LandCompositionMassKeepsNumericMode {
    <#
    .SYNOPSIS
        Чи лишає Stage-CompositionMass режим версії як є (true) замість примусового Strict.
    .DESCRIPTION
        true - методологія має суфіксний міст над String-колонкою (Get-LandStringBridgeSuffixMethodologies): у Strict
        Text x Number = #VALUE (tons C11_01/C11_02), тому режим версії (Legacy у V3) не чіпаємо. D-426 дозволяє
        REG*/CURRENT_SUBSTANCE/SUBSTANCE_APPLICABLE у Legacy, тож 0433 тут більше не причина Strict. Інші методології
        (C11_10, C12_03) - false: Strict лишається.
        Друга гілка - явний перелік 'text-numeric sources' (Get-LandTextNumericSourceMethodologies): C12_01 (V1 id 40,
        джерела No_* типу String, клас 'число як String', D-437) після V2 Legacy не повертаємо у Strict.
    #>
    param([Parameter(Mandatory)][string]$Methodology, [string[]]$StringBridgeMethodologies = @())

    return ((@($StringBridgeMethodologies) -contains $Methodology) -or (@(Get-LandTextNumericSourceMethodologies) -contains $Methodology))
}