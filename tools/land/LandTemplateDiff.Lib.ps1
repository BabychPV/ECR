<#
.SYNOPSIS
    Чисті функції порівняння моделі Land зі структурою версії (без мережі й SQL): спільні для
    Build-LandTemplate.ps1 (клон-чернетка) і Verify-LandTemplate.ps1.

.DESCRIPTION
    D-YE-1: формули колонок. Build раніше лише ВСТАВЛЯВ відсутні формули; вираз наявної колонки клону
    лишався старим (T5_08/T5_09 Winter `< 4 OR > 9` замість `< 3 OR > 8`). Тепер план порівнює вираз
    моделі з поточним і повертає окремо вставки та оновлення.
    LP-28: таблиці версії, яких немає в моделі (застарілі T9a_03/06/07 на клоні).
#>

# Ключ порівняння виразу: сервер зберігає вираз дослівно (FormulaDef.SetExpression), тож нормалізуємо лише
# крайні пробіли й послідовності пробільних символів (у т.ч. переноси рядків).
function ConvertTo-LandFormulaKey([string]$Expression) {
    if ($null -eq $Expression) { return '' }
    return ([regex]::Replace($Expression, '\s+', ' ')).Trim()
}

# Wanted: масив { Key; Expression } (Key = "tableId/columnId"); Existing: hashtable Key -> { Id; Expression; Dialect }.
# Повертає @{ Insert = @(Wanted...); Update = @({ Id; Key; Expression }...); Same = n }.
# Діалект формули колонки шаблону - Template (0): інший діалект теж виправляється оновленням.
function Get-LandFormulaPlan($Wanted, [hashtable]$Existing) {
    $ins = @(); $upd = @(); $same = 0
    foreach ($w in @($Wanted)) {
        if ($null -eq $w) { continue }
        if (-not $Existing.ContainsKey([string]$w.Key)) { $ins += $w; continue }
        $e = $Existing[[string]$w.Key]
        $dialectOk = ([int]$e.Dialect -eq 0)
        if ($dialectOk -and (ConvertTo-LandFormulaKey ([string]$e.Expression)) -ceq (ConvertTo-LandFormulaKey ([string]$w.Expression))) { $same++; continue }
        $upd += [pscustomobject]@{ Id = [int]$e.Id; Key = [string]$w.Key; Expression = [string]$w.Expression }
    }
    return @{ Insert = $ins; Update = $upd; Same = $same }
}

# Кодові назви "SHEET/TABLE" таблиць структури, яких немає в моделі (за винятком -Known).
# Структура - відповідь GET template-versions/{id}/structure (sheets[].tables[]), Mdl - land-template.model.json.
function Get-LandStaleTables($Structure, $Mdl, [string[]]$Known = @()) {
    $want = @{}
    foreach ($sh in @($Mdl.sheets)) { foreach ($tb in @($sh.tables)) { $want["$($sh.code)/$($tb.code)"] = $true } }
    $stale = @()
    foreach ($s in @($Structure.sheets)) {
        foreach ($t in @($s.tables)) {
            $k = "$($s.code)/$($t.code)"
            if ($want.ContainsKey($k)) { continue }
            if (@($Known) -contains $k -or @($Known) -contains [string]$t.code) { continue }
            $stale += [pscustomobject]@{ Key = $k; Sheet = [string]$s.code; Table = [string]$t.code }
        }
    }
    return @($stale)
}

# Розбіжності формул колонок і readOnly між моделлю та структурою версії. Повертає рядки виду
# "SHEET/TABLE: формула відрізняється: C1,C2" (значень виразів не друкує).
function Get-LandFormulaMismatches($Structure, $Mdl) {
    $byTable = @{}
    foreach ($s in @($Structure.sheets)) { foreach ($t in @($s.tables)) { $byTable["$($s.code)/$($t.code)"] = $t } }
    $out = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $etb = $byTable["$($sh.code)/$($tb.code)"]
            if ($null -eq $etb) { continue }
            $diff = @(); $noFormula = @(); $dialect = @(); $ro = @()
            foreach ($c in @($tb.columns)) {
                $ecol = @($etb.columns) | Where-Object { [string]$_.code -eq [string]$c.code } | Select-Object -First 1
                if ($null -eq $ecol) { continue }
                if ($c.expression) {
                    if ([string]::IsNullOrEmpty([string]$ecol.formulaExpression)) { $noFormula += [string]$c.code }
                    elseif ((ConvertTo-LandFormulaKey ([string]$ecol.formulaExpression)) -cne (ConvertTo-LandFormulaKey ([string]$c.expression))) { $diff += [string]$c.code }
                    elseif ($null -ne $ecol.formulaDialect -and -not ([string]$ecol.formulaDialect -in 'Template', '0')) { $dialect += [string]$c.code }
                }
                $wantRo = [bool]($c.readOnly -or [string]$c.dataType -in 'Formula', 'Calculated')
                if ($wantRo -and -not [bool]$ecol.isReadOnly) { $ro += [string]$c.code }
            }
            $k = "$($sh.code)/$($tb.code)"
            if ($diff.Count) { $out += "${k}: формула відрізняється: $($diff -join ',')" }
            if ($noFormula.Count) { $out += "${k}: формули немає: $($noFormula -join ',')" }
            if ($dialect.Count) { $out += "${k}: діалект формули не Template: $($dialect -join ',')" }
            if ($ro.Count) { $out += "${k}: колонка має бути лише для читання: $($ro -join ',')" }
        }
    }
    return @($out)
}

# D-UPG-1: стеля динамічних рядків (TableDef.MaxDynamicRows) НАЯВНОЇ таблиці клону. Build раніше лише ВСТАВЛЯВ нові
# таблиці зі стелею моделі, а в наявних лишав стару (T3_00=498, T9_04=184, T9_07=100) - сервер відхиляв рядки понад
# стелю (ECR-ROW-0409). Правило - лише РОЗШИРЕННЯ: стеля піднімається, коли в моделі вона БІЛЬША; нижча чи рівна, а також
# «без стелі» (NULL) у клоні не чіпаються (зменшення стелі могло б урізати дані документів).
# Wanted: масив { Key; MaxRows } (Key = "SHEET/TABLE"; MaxRows - maxRows моделі, 0/null = без вимоги);
# Existing: hashtable Key -> { Id; MaxRows } (MaxRows = $null, якщо стелі немає).
# Повертає @{ Raise = @({ Id; Key; From; To }...); Same = n }.
function Get-LandMaxRowsPlan($Wanted, [hashtable]$Existing) {
    $raise = @(); $same = 0
    foreach ($w in @($Wanted)) {
        if ($null -eq $w -or -not $w.MaxRows) { continue }
        if (-not $Existing.ContainsKey([string]$w.Key)) { continue }
        $e = $Existing[[string]$w.Key]
        if ($null -eq $e.MaxRows) { $same++; continue }
        if ([int]$e.MaxRows -lt [int]$w.MaxRows) {
            $raise += [pscustomobject]@{ Id = [int]$e.Id; Key = [string]$w.Key; From = [int]$e.MaxRows; To = [int]$w.MaxRows }
        }
        else { $same++ }
    }
    return @{ Raise = $raise; Same = $same }
}

# Розбіжності стелі динамічних рядків між структурою версії (tables[].maxDynamicRows) і моделлю (tables[].maxRows):
# рядки виду "SHEET/TABLE: стеля динамічних рядків було -> має бути". Розбіжність - лише стеля НИЖЧА за модель
# (стеля вища за модель чи відсутня - розширення, Build її не зменшує). Значень документів тут немає.
function Get-LandMaxRowsMismatches($Structure, $Mdl) {
    $byTable = @{}
    foreach ($s in @($Structure.sheets)) { foreach ($t in @($s.tables)) { $byTable["$($s.code)/$($t.code)"] = $t } }
    $out = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            if (-not $tb.maxRows) { continue }
            $etb = $byTable["$($sh.code)/$($tb.code)"]
            if ($null -eq $etb -or $null -eq $etb.maxDynamicRows) { continue }
            if ([int]$etb.maxDynamicRows -lt [int]$tb.maxRows) {
                $out += ("{0}/{1}: стеля динамічних рядків {2} -> {3}" -f $sh.code, $tb.code, [int]$etb.maxDynamicRows, [int]$tb.maxRows)
            }
        }
    }
    return @($out)
}

# LP-21: формат відображення Decimal-колонки (cfg.ColumnDef.DisplayFormat).
# Правило (рішення людини 12.10: «Decimal 16 зберігаємо до 16 знаків, показуємо за DisplayFormat колонки»):
#   1) displayFormat з моделі (числовий формат Excel-шаблону) - беремо як є;
#   2) інакше для Decimal: '0.' + '0' x min(Scale, 6) (Scale 0 -> '0'). Це той самий формат, що Excel-шаблон Land має
#      у 458 із 595 колонок із форматом (0.000000), і єдиний, що виводиться без вигадування: з Scale 16 показ обмежено 6 знаками;
#   3) не Decimal або Scale не задано - формат не виводиться ($null).
# Одна функція на Build (запис) і Verify (звірка), тож модель-генератор не мусить нести формат кожної колонки.
function Resolve-LandDisplayFormat($Column) {
    if ($null -eq $Column) { return $null }
    $explicit = [string]$Column.displayFormat
    if ($explicit) { return $explicit }
    if ([string]$Column.dataType -ne 'Decimal') { return $null }
    if ($null -eq $Column.scale -or [string]$Column.scale -eq '') { return $null }
    $digits = [Math]::Min([Math]::Max([int]$Column.scale, 0), 6)
    if ($digits -eq 0) { return '0' }
    return '0.' + ('0' * $digits)
}

# D-423 (Stage Template): коди cfg.ColumnDef.DataType (Ecr.Domain CellDataType).
$script:LandDataTypeCode = @{ String = 0; Int = 1; Decimal = 2; Bool = 3; Date = 4; Lookup = 5; Formula = 6; Unit = 7; Calculated = 8 }

# Коди колонок, на які посилається вираз у квадратних дужках: [Code]. Регістр значущий (колонки ECR - case-sensitive).
function Get-LandExpressionColumnRefs([string]$Expression) {
    if ([string]::IsNullOrEmpty($Expression)) { return @() }
    $refs = @{}
    foreach ($m in [regex]::Matches($Expression, '\[([A-Za-z_][A-Za-z0-9_]*)\]')) { $refs[$m.Groups[1].Value] = $true }
    return @($refs.Keys)
}

# D-423: чернетка-клон, колонка якої в моделі String, а в клоні ще Decimal (міграція типу НЕ робиться через API:
# ECR-TMPL-0422 columnDataTypeImmutable). Правило вузьке:
#   * лише Decimal -> String МІСЯЧНИХ колонок моделі (month; у моделі це M01..M12 T9_04/T9_07): вибір вузький свідомо -
#     немісячна String над Decimal-клоном може бути чужою розбіжністю, її Build не виправляє, а лише WARN-ить;
#   * інші розбіжності типів не чіпаються (WARN);
#   * колонку, на яку посилається ЧИСЛОВА формула моделі (Formula/Calculated із precision) або формула комірки (cellFormulas),
#     не чіпаємо - WARN. ⚠ Межа: CalculationBinding/агрегати/правила у скрипті невидимі - їх перевіряє publish (ECR-TMPL-4227/4226);
#   * текстові мости (Formula/Calculated, precision у моделі null, вираз посилається на МІСЯЧНУ String-колонку моделі;
#     інші мости над String - Land_NAE тощо - не чіпаються):
#     Precision/Scale клону скидаються в NULL (інакше Verify/migrate бачать зайве Widened/Guarded).
# Columns - tb.columns моделі; Existing - hashtable "код колонки" -> @{ id; type; precision; scale } клону.
# Повертає @{ Retype = @({ Id; Code; FromPrecision; FromScale }); Bridges = @({ Id; Code; FromPrecision; FromScale });
#             Warn = @(рядки); Same = n }. Лише обчислення: SQL і запис - у виклику (Fill-StructureSql).
function Get-LandTextRetypePlan($Columns, [hashtable]$Existing, $CellFormulas = @()) {
    $retype = @(); $bridges = @(); $warn = @(); $same = 0
    $dt = $script:LandDataTypeCode
    $stringCodes = @{}
    foreach ($c in @($Columns)) { if ([string]$c.dataType -eq 'String' -and $c.month) { $stringCodes[[string]$c.code] = $true } }   # лише місячні String-колонки
    $numericReaders = @{}   # код колонки -> коди числових формул моделі, що її читають
    foreach ($c in @($Columns)) {
        if ([string]$c.dataType -notin 'Formula', 'Calculated' -or -not $c.precision) { continue }
        foreach ($r in (Get-LandExpressionColumnRefs ([string]$c.expression))) {
            if (-not $numericReaders.ContainsKey($r)) { $numericReaders[$r] = @() }
            $numericReaders[$r] += [string]$c.code
        }
    }
    # формули комірок таблиці (tb.cellFormulas: { column; expression }): колонка-ціль і будь-яке посилання [Code] у виразі
    foreach ($cf in @($CellFormulas)) {
        if ($null -eq $cf) { continue }
        $refs = @(Get-LandExpressionColumnRefs ([string]$cf.expression)) + @([string]$cf.column)
        foreach ($r in $refs) {
            if (-not $r) { continue }
            if (-not $numericReaders.ContainsKey($r)) { $numericReaders[$r] = @() }
            $numericReaders[$r] += 'cellFormula'
        }
    }
    $retypedOrText = @{}
    foreach ($c in @($Columns)) {
        if ([string]$c.dataType -ne 'String') { continue }
        $code = [string]$c.code
        if (-not $Existing.ContainsKey($code)) { continue }
        $e = $Existing[$code]
        if ([int]$e.type -eq $dt.String) { $retypedOrText[$code] = $true; continue }
        if ([int]$e.type -ne $dt.Decimal) { $warn += "${code}: тип клону $($e.type), у моделі String - лише Decimal->String, не чіпаю"; continue }
        if (-not $c.month) { $warn += "${code}: у клоні Decimal, у моделі String, але колонка не місячна - не чіпаю (ретайп лише місячних)"; continue }
        if ($numericReaders.ContainsKey($code)) { $warn += "${code}: на колонку посилається числова формула ($($numericReaders[$code] -join ',')) - тип не змінюю"; continue }
        $retype += [pscustomobject]@{ Id = [int]$e.id; Code = $code; FromPrecision = $e.precision; FromScale = $e.scale }
        $retypedOrText[$code] = $true
    }
    foreach ($c in @($Columns)) {
        if ([string]$c.dataType -notin 'Formula', 'Calculated' -or $c.precision) { continue }
        $code = [string]$c.code
        if (-not $Existing.ContainsKey($code)) { continue }
        $src = @(Get-LandExpressionColumnRefs ([string]$c.expression) | Where-Object { $stringCodes.ContainsKey($_) })
        if ($src.Count -eq 0) { continue }
        if (@($src | Where-Object { -not $retypedOrText.ContainsKey($_) }).Count -gt 0) { continue }   # джерело лишилось Decimal
        $e = $Existing[$code]
        if ($null -eq $e.precision -and $null -eq $e.scale) { $same++; continue }
        $bridges += [pscustomobject]@{ Id = [int]$e.id; Code = $code; FromPrecision = $e.precision; FromScale = $e.scale }
    }
    return @{ Retype = $retype; Bridges = $bridges; Warn = $warn; Same = $same }
}

# Кандидати Decimal->String, відомі лише з моделі (для DryRun, де клону немає): String-колонки, які читає текстовий міст
# (Formula/Calculated без precision) і є місячною (month). Повертає рядки "SHEET/TABLE/COLUMN".
function Get-LandTextBridgeSources($Mdl) {
    $out = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $str = @{}
            foreach ($c in @($tb.columns)) { if ([string]$c.dataType -eq 'String' -and $c.month) { $str[[string]$c.code] = $true } }
            $seen = @{}
            foreach ($c in @($tb.columns)) {
                if ([string]$c.dataType -notin 'Formula', 'Calculated' -or $c.precision) { continue }
                foreach ($r in (Get-LandExpressionColumnRefs ([string]$c.expression))) {
                    if ($str.ContainsKey($r) -and -not $seen.ContainsKey($r)) { $seen[$r] = $true; $out += "$($sh.code)/$($tb.code)/$r" }
                }
            }
        }
    }
    return @($out)
}

# Розбіжності ТИПУ колонок між моделлю та структурою версії ("SHEET/TABLE: тип відрізняється: COL model!=ecr").
# DTO структури несе dataType рядком (або числом) - порівнюємо за назвою.
function Get-LandColumnTypeMismatches($Structure, $Mdl, [string[]]$Known = @()) {
    $names = @('String', 'Int', 'Decimal', 'Bool', 'Date', 'Lookup', 'Formula', 'Unit', 'Calculated')
    $byTable = @{}
    foreach ($s in @($Structure.sheets)) { foreach ($t in @($s.tables)) { $byTable["$($s.code)/$($t.code)"] = $t } }
    $out = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $etb = $byTable["$($sh.code)/$($tb.code)"]
            if ($null -eq $etb) { continue }
            $diff = @()
            foreach ($c in @($tb.columns)) {
                $ecol = @($etb.columns) | Where-Object { [string]$_.code -eq [string]$c.code } | Select-Object -First 1
                if ($null -eq $ecol -or $null -eq $ecol.dataType) { continue }
                $got = [string]$ecol.dataType
                $n = 0; if ([int]::TryParse($got, [ref]$n) -and $n -ge 0 -and $n -lt $names.Count) { $got = $names[$n] }
                if ($got -cne [string]$c.dataType) {
                    # -KnownTypeMismatches: відомі законні розбіжності "TABLE.COL" (Build їх свідомо не чіпає) - не червонять
                    if (@($Known) -contains "$($tb.code).$($c.code)") { continue }
                    $diff += "$($c.code) $($c.dataType)!=$got"
                }
            }
            if ($diff.Count) { $out += "$($sh.code)/$($tb.code): тип відрізняється: $($diff -join ', ')" }
        }
    }
    return @($out)
}

# Текстові мости моделі: Formula/Calculated без precision, вираз яких читає МІСЯЧНУ String-колонку тієї ж таблиці.
# Повертає @{ Sheet; Table; Code } - Verify по них звіряє Precision/Scale (мають бути NULL).
function Get-LandTextBridgeColumns($Mdl) {
    $out = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $str = @{}
            foreach ($c in @($tb.columns)) { if ([string]$c.dataType -eq 'String' -and $c.month) { $str[[string]$c.code] = $true } }
            foreach ($c in @($tb.columns)) {
                if ([string]$c.dataType -notin 'Formula', 'Calculated' -or $c.precision) { continue }
                if (@(Get-LandExpressionColumnRefs ([string]$c.expression) | Where-Object { $str.ContainsKey($_) }).Count -gt 0) {
                    $out += [pscustomobject]@{ Sheet = [string]$sh.code; Table = [string]$tb.code; Code = [string]$c.code }
                }
            }
        }
    }
    return @($out)
}

# Коди методологій, на які посилається модель (tb.calc.methodologies таблиць і sh.calc.methodologies аркушів).
function Get-LandModelMethodologyCodes($Model) {
    $codes = @{}
    foreach ($sh in @($Model.sheets)) {
        if ($sh.calc) { foreach ($n in @($sh.calc.methodologies)) { if ($n) { $codes[[string]$n] = $true } } }
        foreach ($tb in @($sh.tables)) {
            if ($tb.calc) { foreach ($n in @($tb.calc.methodologies)) { if ($n) { $codes[[string]$n] = $true } } }
        }
    }
    return @($codes.Keys | Sort-Object)
}

# P3: методологія з опублікованою ОСТАННЬОЮ версією мусить мати хоч одну активну прив'язку (cfg.CalculationBinding.IsActive=1).
# Build-LandTemplate (Bind-ResultColumns) ставить IsActive за статусом останньої версії НА МОМЕНТ прив'язки: публікація
# після прив'язки лишає прив'язки вимкненими, і результат ніколи не лягає в таблицю (клас MISSING_BINDING_INACTIVE у паритеті).
# $Info: елементи @{ Code; Published (bool, остання версія Published); Bindings (масив bool IsActive) }.
# Повертає елементи @{ Code; Total; Reason = 'ALL_INACTIVE' | 'NO_BINDINGS' }; значень немає.
function Get-LandInactiveBindingMethodologies($Info) {
    $res = @()
    foreach ($i in @($Info)) {
        if (-not [bool]$i.Published) { continue }
        $b = @($i.Bindings)
        $active = @($b | Where-Object { [bool]$_ }).Count
        if ($active -gt 0) { continue }
        $reason = 'ALL_INACTIVE'; if ($b.Count -eq 0) { $reason = 'NO_BINDINGS' }
        $res += [pscustomobject]@{ Code = [string]$i.Code; Total = $b.Count; Reason = $reason }
    }
    return @($res | Sort-Object Code)
}
