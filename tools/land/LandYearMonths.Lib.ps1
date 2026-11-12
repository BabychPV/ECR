# tools/land/LandYearMonths.Lib.ps1
# LAND-YEAR, зріз L4 (рішення людини 12.11, D-474 варіант A): перебудова моделі шаблону Land під річний аркуш
# (версія 1.0.17.0, розкладка YearWithMonthColumns). Чиста логіка без мережі й SQL; етап -Stage YearMonths у
# Build-LandTemplate.ps1 лише викликає Convert-LandYearMonthsModel.
#
# Що робить з моделлю (land-template.model.json), лише в таблицях MonthsInColumns:
#   * Calculated немісячна колонка C (вихід методології) -> C_M01..C_M12 (Calculated, місяць m), прив'язка виходу C до
#     кожної з 12 колонок (рішення 3: T7b_00.ECW_EC_tons теж);
#   * Formula немісячна колонка F, що (транзитивно, в межах таблиці) читає [Period].<не Year> або [Period:…] ->
#     F_M01..F_M12, вираз переписано на місяць m: [Period].Sequence -> m, [Period].Days -> дні місяця m, [S] селектора ->
#     його джерело місяця m, [V] іншої змінної формули -> [V_Mmm];
#   * селектор «поточного періоду» ([Period].Sequence = 1 ? [X1] : … : [X12]) прибирається (рішення 4); його ім'я
#     лишається аргументом методологій через псевдонім місяця (cfg.MonthInputAlias, зріз L0);
#   * порядок нових колонок — по виходу (рішення 1): C_M01..C_M12 на місці C, далі наступний; група заголовків на
#     кожен вихід (MB_O_<C>, згортається), заголовок колонки — назва місяця;
#   * річні підсумки (рішення 2) — лише там, де вони є в xlsm замовника (land-year-totals.json): той самий вираз і місце.
# Повтор на результаті — 0 змін. Сумісна з Windows PowerShell 5.1 і PowerShell 7.

$script:LandYearMonthsTargetVersion = '1.0.17.0'
$script:LandYearMonthsGroupPrefix = 'MB_O_'
$script:LandYearMonthsSelectorHead = '^\s*\[Period\]\.Sequence\s*=\s*1\s*\?'

function Get-LandYearMonthCode {
    param([Parameter(Mandatory)][string]$BaseCode, [Parameter(Mandatory)][int]$Month)
    return ('{0}_M{1:00}' -f $BaseCode, $Month)
}

function Get-LandYearMonthDaysExpression {
    <# Дні місяця m літералом: [Period:#m].Days діалект не розбирає (Parser.cs, expr.calendarContextOffset), а [Period].Days
       у місячній колонці залежав би від того, в якому періоді рахують. Лютий — за тим самим правилом, що й DaysInCalcYear моделі. #>
    param([Parameter(Mandatory)][int]$Month)
    if ($Month -eq 2) { return '(([Period].Year % 4 = 0) ? 29 : 28)' }
    if (@(4, 6, 9, 11) -contains $Month) { return '30' }
    return '31'
}

function Test-LandYearMonthSelector {
    param([string]$Expression)
    return ([string]$Expression) -match $script:LandYearMonthsSelectorHead
}

function Get-LandSelectorSources {
    <#
    .SYNOPSIS
        Джерела селектора «[Period].Sequence = 1 ? [A] : … = 11 ? [K] : [L]» -> @{ 1 = 'A'; …; 12 = 'L' }.
        Інша форма (не 11 гілок по порядку + else) — виняток: назва джерела не виводиться з коду селектора (12.11, §0 плану).
    #>
    param([Parameter(Mandatory)][string]$Expression)
    $rx = '^\s*' + ((1..11 | ForEach-Object { '\[Period\]\.Sequence\s*=\s*' + $_ + '\s*\?\s*\[([A-Za-z][A-Za-z0-9_]*)\]\s*:\s*' }) -join '') + '\[([A-Za-z][A-Za-z0-9_]*)\]\s*$'
    $m = [regex]::Match($Expression, $rx)
    if (-not $m.Success) { throw "Селектор не має форми «[Period].Sequence = 1 ? [X] : … : [X12]»: $Expression" }
    $map = @{}
    for ($i = 1; $i -le 12; $i++) { $map[$i] = $m.Groups[$i].Value }
    return $map
}

function Get-LandPeriodProperties {
    <# Властивості [Period].X виразу (без [Period:…]); 'Year' — період-незалежна в межах року. #>
    param([string]$Expression)
    return @([regex]::Matches([string]$Expression, '\[Period\]\.([A-Za-z]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

function Get-LandYearMonthsTableClasses {
    <#
    .SYNOPSIS
        Клас кожної немісячної колонки-формули/результату таблиці: Selector | Varying | Calculated. Решта (місячні,
        введені, період-незалежні формули) у словник не потрапляє. Varying — транзитивно: формула, що читає Selector
        або Varying тієї ж таблиці.
    #>
    param([Parameter(Mandatory)]$Table)
    $cls = [ordered]@{}
    $formulas = @{}
    foreach ($c in @($Table.columns)) {
        if ($c.month) { continue }
        $code = [string]$c.code
        if ([string]$c.dataType -eq 'Calculated') { $cls[$code] = 'Calculated'; continue }
        if ([string]$c.dataType -ne 'Formula') { continue }
        $e = [string]$c.expression
        if (Test-LandYearMonthSelector $e) { $cls[$code] = 'Selector'; continue }
        $props = @(Get-LandPeriodProperties $e | Where-Object { $_ -ne 'Year' })
        if ($props.Count -gt 0 -or $e -match '\[Period:') { $cls[$code] = 'Varying'; continue }
        $formulas[$code] = $e
    }
    do {
        $grew = $false
        foreach ($code in @($formulas.Keys)) {
            foreach ($r in [regex]::Matches($formulas[$code], '\[([A-Za-z][A-Za-z0-9_]*)\]')) {
                $k = $r.Groups[1].Value
                if ($cls.Contains($k) -and $cls[$k] -in 'Selector', 'Varying') { $cls[$code] = 'Varying'; $formulas.Remove($code); $grew = $true; break }
            }
        }
    } while ($grew)
    return $cls
}

function Convert-LandYearMonthFormula {
    <#
    .SYNOPSIS
        Вираз змінної формули для місяця m. Selectors: код селектора -> @{ m -> джерело }; Varying: коди змінних формул.
        Невідома властивість [Period] (крім Year/Sequence/Days) або [Period:…] — виняток: тихо переписати не можна.
    #>
    param([Parameter(Mandatory)][string]$Expression, [Parameter(Mandatory)][int]$Month, [hashtable]$Selectors = @{}, [string[]]$Varying = @())
    $bad = @(Get-LandPeriodProperties $Expression | Where-Object { $_ -notin 'Year', 'Sequence', 'Days' })
    if ($bad.Count -gt 0) { throw "Властивість [Period].$($bad -join ', [Period].') у місячну колонку не переписується: $Expression" }
    if ($Expression -match '\[Period:') { throw "Посилання [Period:…] у місячну колонку не переписується: $Expression" }
    # один прохід токенами (MatchEvaluator зі scriptblock у 5.1 — ~6 мс на вираз)
    $days = Get-LandYearMonthDaysExpression $Month
    $sb = New-Object Text.StringBuilder
    $pos = 0
    foreach ($x in [regex]::Matches($Expression, '\[Period\]\.(Sequence|Days)\b|\[([A-Za-z][A-Za-z0-9_]*)\]')) {
        $null = $sb.Append($Expression, $pos, $x.Index - $pos)
        $pos = $x.Index + $x.Length
        if ($x.Groups[1].Success) { $null = $sb.Append($(if ($x.Groups[1].Value -eq 'Sequence') { [string]$Month } else { $days })); continue }
        $k = $x.Groups[2].Value
        if ($Selectors.ContainsKey($k)) { $null = $sb.Append('[' + $Selectors[$k][$Month] + ']') }
        elseif ($Varying -contains $k) { $null = $sb.Append('[' + $k + ('_M{0:00}' -f $Month) + ']') }
        else { $null = $sb.Append($x.Value) }
    }
    $null = $sb.Append($Expression, $pos, $Expression.Length - $pos)
    return $sb.ToString()
}

function Copy-LandYearMonthColumn {
    param($Source, [string]$NewCode, [int]$Month, $MonthName, [string]$Expression)
    $h = [ordered]@{}
    foreach ($p in $Source.PSObject.Properties) {
        if ($p.Name -in 'excel', 'span', 'dv', 'filledRows', 'zone') { continue }
        $h[$p.Name] = $p.Value
    }
    $h.code = $NewCode
    $h.month = $Month
    $h.headerEn = [string]$MonthName.en
    $h.headerRu = $(if ($MonthName.ru) { [string]$MonthName.ru } else { '' })
    $h.excel = $null
    $h.readOnly = $true
    $h.yearMonthsOf = [string]$Source.code
    $h.ecrAdded = 'year months (LAND-YEAR L4): ' + [string]$Source.code
    if ($null -ne $Expression) { $h.expression = $Expression }
    return [pscustomobject]$h
}

function Get-LandTotalsPlan {
    <#
    .SYNOPSIS
        Річні підсумки за land-year-totals.json: наявна колонка з тим самим виразом — нічого; іншим — помилка; відсутня —
        вставити Formula-колонку відразу після «after». Повертає @{ Insert = @(@{ Index; Column }); Errors = @() }.
    #>
    param([Parameter(Mandatory)]$Table, [object[]]$Totals, [string]$SheetCode)
    $res = @{ Insert = @(); Errors = @() }
    $cols = @($Table.columns)
    foreach ($t in @($Totals | Where-Object { $_ -and [string]$_.table -eq [string]$Table.code -and (-not $_.sheet -or [string]$_.sheet -eq $SheetCode) })) {
        $have = @($cols | Where-Object { [string]$_.code -eq [string]$t.code })
        if ($have.Count -gt 0) {
            if ([string]$have[0].dataType -ne 'Formula' -or ([string]$have[0].expression).Trim() -ne ([string]$t.expression).Trim()) {
                $res.Errors += "$($Table.code).$($t.code): підсумок xlsm ($($t.expression)) не збігається з колонкою моделі ($($have[0].dataType): $($have[0].expression))"
            }
            continue
        }
        $idx = -1
        for ($i = 0; $i -lt $cols.Count; $i++) { if ([string]$cols[$i].code -eq [string]$t.after) { $idx = $i } }
        if ($idx -lt 0) { $res.Errors += "$($Table.code).$($t.code): немає колонки $($t.after), після якої стоїть підсумок у xlsm"; continue }
        $col = [pscustomobject][ordered]@{ code = [string]$t.code; dataType = 'Formula'; headerEn = [string]$t.headerEn; headerRu = ''
            month = $null; hidden = $false; readOnly = $true; excel = $null; expression = [string]$t.expression; precision = 34; scale = 16
            ecrAdded = "year total (xlsm $($t.xlsm.sheet)!$($t.xlsm.cell) $($t.xlsm.formula))" }
        $res.Insert += [pscustomobject]@{ Index = $idx + 1; Column = $col; After = [string]$t.after }
    }
    return [pscustomobject]$res
}

function Convert-LandYearMonthsModel {
    <#
    .SYNOPSIS
        Чиста перебудова моделі під рік (див. заголовок файлу). Вхідну модель не змінює (глибока копія).
    .PARAMETER MonthNames
        12 записів { month; en; ru } (land-month-bands.json, months) — заголовки місячних колонок результатів.
    .PARAMETER Totals
        Записи land-year-totals.json (table, code, after, expression, headerEn, xlsm).
    .OUTPUTS
        Model, Changes (0 — повтор), Errors, Tables (по таблиці: Added, Removed, Selectors, Varying, Calculated, Totals),
        Bindings (Table, Column, Output, Month), Aliases (Table, Alias, Month, Column), Groups (Table, Code, En, Ru, Columns).
    #>
    param([Parameter(Mandatory)]$Model, [Parameter(Mandatory)][object[]]$MonthNames, [object[]]$Totals = @())
    $names = @{}
    foreach ($n in @($MonthNames)) { $names[[int]$n.month] = $n }
    if ($names.Count -ne 12) { throw 'Потрібно рівно 12 назв місяців.' }
    # Копія без мутації входу: модель, аркуші й таблиці — поверхнево (колонки замінюються новим масивом, наявні об'єкти
    # колонок не змінюються); yearMonths попереднього прогону — глибоко. Повна глибока копія 8 МБ моделі в 5.1 — хвилини.
    $m = $Model.PSObject.Copy()
    $m.sheets = @(foreach ($sh0 in @($Model.sheets)) {
            $shc = $sh0.PSObject.Copy()
            $shc.tables = @(foreach ($tb0 in @($sh0.tables)) {
                    $tbc = $tb0.PSObject.Copy()
                    if ($tbc.PSObject.Properties['yearMonths']) {
                        # елементи списків не змінюються (лише дописуються нові), тож досить нових масивів
                        $y0 = $tb0.yearMonths
                        $tbc.yearMonths = [pscustomobject][ordered]@{ removed = @($y0.removed); bindings = @($y0.bindings); aliases = @($y0.aliases)
                            groups = @($y0.groups); totals = @($y0.totals) }
                    }
                    $tbc })
            $shc })
    $r = [ordered]@{ Model = $m; Changes = 0; Errors = @(); Tables = @() }
    $outB = New-Object 'System.Collections.Generic.List[object]'; $outA = New-Object 'System.Collections.Generic.List[object]'; $outG = New-Object 'System.Collections.Generic.List[object]'

    foreach ($sh in @($m.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $tcode = [string]$tb.code
            $cls = Get-LandYearMonthsTableClasses $tb
            if ([string]$tb.layout -ne 'MonthsInColumns') {
                if ($cls.Count -gt 0) { $r.Errors += "$tcode ($($tb.layout)): немісячні обчислювані колонки ($(@($cls.Keys) -join ', ')), а таблиця не MonthsInColumns" }
                continue
            }
            $byCode = @{}
            foreach ($c in @($tb.columns)) { $byCode[[string]$c.code] = $c }
            $sel = @{}; $var = @()
            foreach ($k in @($cls.Keys)) {
                if ($cls[$k] -eq 'Selector') {
                    try { $sel[$k] = Get-LandSelectorSources ([string]$byCode[$k].expression) } catch { $r.Errors += "${tcode}.${k}: $($_.Exception.Message)"; continue }
                    for ($mo = 1; $mo -le 12; $mo++) {
                        $src = $byCode[$sel[$k][$mo]]
                        if ($null -eq $src -or [int]$src.month -ne $mo) { $r.Errors += "${tcode}.${k}: джерело місяця $mo ($($sel[$k][$mo])) не є місячною колонкою $mo цієї таблиці" }
                    }
                }
                elseif ($cls[$k] -eq 'Varying') { $var += $k }
            }
            $prior = $tb.PSObject.Properties['yearMonths']
            $ym = if ($prior) { $prior.Value } else { [pscustomobject][ordered]@{ removed = @(); bindings = @(); aliases = @(); groups = @(); totals = @() } }
            $removedSet = @{}; foreach ($x in @($ym.removed)) { $removedSet[[string]$x] = $true }

            # перевірки споживачів кодів, що зникають: правила, формули комірок, умовне форматування
            $gone = @($cls.Keys)
            if ($gone.Count -gt 0) {
                $goneSet = @{}; foreach ($g in $gone) { $goneSet[$g] = $true }
                $goneRx = New-Object Text.RegularExpressions.Regex ('\[(' + (($gone | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\]')
                foreach ($ru in @($tb.rules)) {
                    if (-not $ru) { continue }
                    $hit = $goneRx.Match([string]$ru.expression)
                    if ($goneSet.ContainsKey([string]$ru.column) -or $hit.Success) { $r.Errors += "${tcode}: правило $($ru.code) читає $(if ($hit.Success) { $hit.Groups[1].Value } else { $ru.column })" }
                }
                foreach ($cf in @($tb.cellFormulas)) {
                    if ($cf -and $goneRx.IsMatch([string]$cf.expression)) { $r.Errors += "${tcode}: формула комірки $($cf.row)/$($cf.column) читає $($goneRx.Match([string]$cf.expression).Groups[1].Value)" }
                }
                foreach ($cf in @($m.conditionalFormats)) {
                    if ($cf -and $goneSet.ContainsKey([string]$cf.columnCode)) { $r.Errors += "${tcode}: умовне форматування на $($cf.columnCode)" }
                }
            }

            $new = New-Object 'System.Collections.Generic.List[object]'
            $st = [ordered]@{ Table = $tcode; Added = 0; Removed = 0; Selectors = 0; Varying = 0; Calculated = 0; Totals = 0 }
            foreach ($c in @($tb.columns)) {
                $code = [string]$c.code
                if (-not $cls.Contains($code)) { $new.Add($c); continue }
                $kind = $cls[$code]
                $removedSet[$code] = $true; $st.Removed++
                if ($kind -eq 'Selector') {
                    $st.Selectors++
                    if (-not $sel.ContainsKey($code)) { continue }
                    for ($mo = 1; $mo -le 12; $mo++) { $ym.aliases += [pscustomobject][ordered]@{ alias = $code; month = $mo; column = $sel[$code][$mo] } }
                    continue
                }
                $cols = @()
                for ($mo = 1; $mo -le 12; $mo++) {
                    $mc = Get-LandYearMonthCode $code $mo
                    if ($byCode.ContainsKey($mc) -and -not $byCode[$mc].PSObject.Properties['yearMonthsOf']) {
                        $r.Errors += "${tcode}.${mc}: код уже зайнятий колонкою $($byCode[$mc].dataType) (R-3: нові коди не повторюють наявні)"
                    }
                    $expr = $null
                    if ($kind -eq 'Varying') {
                        try { $expr = Convert-LandYearMonthFormula -Expression ([string]$c.expression) -Month $mo -Selectors $sel -Varying $var }
                        catch { $r.Errors += "${tcode}.${code}: $($_.Exception.Message)"; $expr = [string]$c.expression }
                    }
                    $new.Add((Copy-LandYearMonthColumn -Source $c -NewCode $mc -Month $mo -MonthName $names[$mo] -Expression $expr))
                    $cols += $mc; $st.Added++
                    if ($kind -eq 'Varying') { $ym.aliases += [pscustomobject][ordered]@{ alias = $code; month = $mo; column = $mc } }
                    else { $ym.bindings += [pscustomobject][ordered]@{ output = $code; column = $mc; month = $mo } }
                }
                if ($kind -eq 'Varying') { $st.Varying++ } else { $st.Calculated++ }
                $gc = $script:LandYearMonthsGroupPrefix + $code
                if ($gc.Length -gt 64) { $r.Errors += "${tcode}.${code}: код групи $gc довший за 64" }
                $hRu = if ($c.headerRu) { [string]$c.headerRu } else { $null }
                $ym.groups += [pscustomobject][ordered]@{ code = $gc; en = [string]$c.headerEn; ru = $hRu; columns = $cols }
            }
            $tb.columns = $new.ToArray()

            $tp = Get-LandTotalsPlan -Table $tb -Totals $Totals -SheetCode ([string]$sh.code)
            $r.Errors += @($tp.Errors)
            if (@($tp.Insert).Count -gt 0) {
                $list = New-Object 'System.Collections.Generic.List[object]'
                foreach ($c in @($tb.columns)) { $list.Add($c) }
                foreach ($ins in @($tp.Insert | Sort-Object Index -Descending)) { $list.Insert([int]$ins.Index, $ins.Column); $ym.totals += [string]$ins.Column.code; $st.Totals++ }
                $tb.columns = $list.ToArray()
            }

            $ym.removed = @($removedSet.Keys | Sort-Object)
            $touched = ($st.Added + $st.Removed + $st.Totals) -gt 0
            if ($touched -and -not $prior) { $tb | Add-Member -NotePropertyName yearMonths -NotePropertyValue $ym }
            if ($touched -or $prior) {
                foreach ($b in @($ym.bindings)) { $outB.Add([pscustomobject]@{ Table = $tcode; Column = [string]$b.column; Output = [string]$b.output; Month = [int]$b.month }) }
                foreach ($a in @($ym.aliases)) { $outA.Add([pscustomobject]@{ Table = $tcode; Alias = [string]$a.alias; Month = [int]$a.month; Column = [string]$a.column }) }
                foreach ($g in @($ym.groups)) { $outG.Add([pscustomobject]@{ Table = $tcode; Code = [string]$g.code; En = [string]$g.en; Ru = $g.ru; Columns = @($g.columns) }) }
            }
            if ($touched) { $r.Tables += [pscustomobject]$st }
            $r.Changes += $st.Added + $st.Removed + $st.Totals
        }
    }
    if ([string]$m.PSObject.Properties['yearMonthsTargetVersion'].Value -ne $script:LandYearMonthsTargetVersion) {
        $m | Add-Member -NotePropertyName yearMonthsTargetVersion -NotePropertyValue $script:LandYearMonthsTargetVersion -Force
        $r.Changes++
    }
    $r.Bindings = $outB.ToArray(); $r.Aliases = $outA.ToArray(); $r.Groups = $outG.ToArray()
    return [pscustomobject]$r
}

function Get-LandTypeFamily {
    <# Родина типу за R-3 (ECR-TMPL-0422 columnTypeFamilyChanged): числові Int/Decimal/Formula/Calculated — одна. #>
    param([string]$DataType)
    if ($DataType -in 'Int', 'Decimal', 'Formula', 'Calculated') { return 'Numeric' }
    return $DataType
}

function Test-LandYearMonthsCodes {
    <#
    .SYNOPSIS
        R-3 на результаті: кожен новий код (yearMonthsOf) — тієї ж родини, що й джерело; не дорівнює коду, прибраному в
        тій самій таблиці; не збігається з кодом опублікованої версії іншої родини (Published: 'T|code' -> DataType).
        Повертає перелік порушень (порожній — безпечно).
    #>
    param([Parameter(Mandatory)]$Model, [hashtable]$Published = @{}, [hashtable]$SourceTypes = @{})
    $bad = @()
    foreach ($sh in @($Model.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $ym = $tb.PSObject.Properties['yearMonths']
            $removed = if ($ym) { @($ym.Value.removed) } else { @() }
            $seen = @{}
            foreach ($c in @($tb.columns)) {
                $code = [string]$c.code
                if ($seen.ContainsKey($code)) { $bad += "$($tb.code).${code}: код двічі" }
                $seen[$code] = $true
                if (-not $c.PSObject.Properties['yearMonthsOf']) { continue }
                if ($removed -contains $code) { $bad += "$($tb.code).${code}: новий код збігається з прибраним" }
                $srcType = $SourceTypes["$($tb.code)|$($c.yearMonthsOf)"]
                if ($srcType -and (Get-LandTypeFamily $srcType) -ne (Get-LandTypeFamily ([string]$c.dataType))) { $bad += "$($tb.code).${code}: родина $($c.dataType) <> джерела $srcType" }
                $pub = $Published["$($tb.code)|$code"]
                if ($pub -and (Get-LandTypeFamily $pub) -ne (Get-LandTypeFamily ([string]$c.dataType))) { $bad += "$($tb.code).${code}: опублікований код іншої родини ($pub -> $($c.dataType))" }
            }
        }
    }
    return , $bad
}

function Get-LandYearMonthsOutputGroups {
    <# Групи «вихід -> 12 місяців» моделі для LandMonthBands: таблиця -> @(@{ Code; En; Ru; Columns }). #>
    param([Parameter(Mandatory)]$Model)
    $map = @{}
    foreach ($sh in @($Model.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $ym = $tb.PSObject.Properties['yearMonths']
            if (-not $ym) { continue }
            $map[[string]$tb.code] = @($ym.Value.groups | ForEach-Object { [pscustomobject]@{ Code = [string]$_.code; En = [string]$_.en; Ru = $_.ru; Columns = @($_.columns) } })
        }
    }
    return $map
}

function Get-LandResultColumnCodes {
    <# Колонки, до яких прив'язується вихід методології: після YearMonths — 12 місячних, інакше колонка з кодом виходу. #>
    param($Table, [Parameter(Mandatory)][string]$Output)
    if ($Table) {
        $ym = $Table.PSObject.Properties['yearMonths']
        if ($ym) {
            $cols = @($ym.Value.bindings | Where-Object { [string]$_.output -eq $Output } | Sort-Object { [int]$_.month } | ForEach-Object { [string]$_.column })
            if ($cols.Count -gt 0) { return , $cols }
        }
    }
    return , @($Output)
}
